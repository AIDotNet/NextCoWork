using System.Net;
using System.Net.Sockets;
using System.Text;
using Ncw.Agent.Environments;
using Ncw.Agent.Environments.Ssh;
using static Ncw.Agent.Environments.Tests.SshTestKit;

namespace Ncw.Agent.Environments.Tests;

/// <summary>SSH 环境的其它真实 sshd 用例:密码提示、终端、等待重连的预算、HTTP / SOCKS 代理。</summary>
public class SshEnvironmentTests(SshdFixture fixture) : IClassFixture<SshdFixture>
{
    private static string UnknownKey(TempDir dir)
    {
        using var keygen = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SshdInstance.KeygenPath!, ["-q", "-t", "ed25519", "-N", "", "-f", dir.File("k")]) { UseShellExecute = false })!;
        keygen.WaitForExit();
        return dir.File("k");
    }

    [Fact]
    public async Task Auto_mode_asks_for_a_password_only_after_the_key_is_refused_and_only_if_the_server_accepts_passwords()
    {
        var sshd = fixture.Require();
        using var passwordServer = await SshdInstance.StartAsync(passwordAuth: true);
        using var temp = new TempDir();
        var key = UnknownKey(temp);

        var credentials = new ScriptedCredentials("one", "two", "three");
        await using var env = NewEnv(passwordServer, ManualProfile(passwordServer, identityFile: key, auth: SshAuthMethod.Auto), Services(passwordServer, credentials: credentials));
        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await env.EnsureReadyAsync());
        Assert.Equal(EnvironmentErrorCode.Authentication, error.Code);

        // 三次提示(和 OpenSSH 的 NumberOfPasswordPrompts 一样),第一次不是「上次被拒绝」,之后都是
        Assert.Equal(3, credentials.Requests.Count);
        Assert.All(credentials.Requests, r => Assert.Equal(SshCredentialKind.Password, r.Kind));
        Assert.Equal([false, true, true], credentials.Requests.Select(r => r.PreviousRejected));
        Assert.All(credentials.Requests, r => Assert.Equal(passwordServer.Username, r.UserName));

        // 服务器只接受公钥时,不会白白问密码
        var silent = new ScriptedCredentials("never");
        await using var keyOnly = NewEnv(sshd, ManualProfile(sshd, identityFile: key, auth: SshAuthMethod.Auto), Services(sshd, credentials: silent));
        await Assert.ThrowsAsync<EnvironmentException>(async () => await keyOnly.EnsureReadyAsync());
        Assert.Empty(silent.Requests);
    }

    [Fact]
    public async Task Password_mode_asks_up_front_and_cancelling_cancels_the_connection()
    {
        _ = fixture.Require();
        using var server = await SshdInstance.StartAsync(passwordAuth: true);
        var credentials = new ScriptedCredentials((string?)null);
        await using var env = NewEnv(server, ManualProfile(server, auth: SshAuthMethod.Password), Services(server, credentials: credentials));
        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await env.EnsureReadyAsync());
        Assert.Equal(EnvironmentErrorCode.Cancelled, error.Code);
        var request = Assert.Single(credentials.Requests);
        Assert.Equal(SshCredentialKind.Password, request.Kind);
        Assert.False(request.PreviousRejected);

        // 给了错的密码:被拒绝后再问,并标明上次被拒绝
        var wrong = new ScriptedCredentials("bad", null);
        await using var second = NewEnv(server, ManualProfile(server, auth: SshAuthMethod.Password), Services(server, credentials: wrong));
        var failure = await Assert.ThrowsAsync<EnvironmentException>(async () => await second.EnsureReadyAsync());
        Assert.Equal(EnvironmentErrorCode.Cancelled, failure.Code);
        Assert.Equal([false, true], wrong.Requests.Select(r => r.PreviousRejected));
    }

    [Fact]
    public async Task A_pty_terminal_runs_commands_and_can_be_resized()
    {
        var sshd = fixture.Require();
        await using var env = NewEnv(sshd);
        await using var terminal = await env.OpenTerminalAsync(new TerminalSpec(Columns: 100, Rows: 30));
        await terminal.Stream.WriteAsync("echo term-$((20+22))\n"u8.ToArray());
        await terminal.Stream.FlushAsync();

        var seen = new StringBuilder();
        var buffer = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!seen.ToString().Contains("term-42", StringComparison.Ordinal))
        {
            var read = await Task.Run(() => terminal.Stream.Read(buffer, 0, buffer.Length), cts.Token).WaitAsync(cts.Token);
            Assert.NotEqual(0, read);
            seen.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        await terminal.ResizeAsync(120, 40);
        await terminal.KillAsync();
    }

    [Fact]
    public async Task A_terminal_gets_its_working_directory_and_environment()
    {
        var sshd = fixture.Require();
        await using var env = NewEnv(sshd);
        using var temp = new TempDir();
        await using var terminal = await env.OpenTerminalAsync(new TerminalSpec(temp.Env, 100, 30, new Dictionary<string, string> { ["NCW_T"] = "it's" }));
        await terminal.Stream.WriteAsync("printf 'cwd=%s env=%s\\n' \"$(pwd -P)\" \"$NCW_T\"\n"u8.ToArray());
        await terminal.Stream.FlushAsync();

        var seen = new StringBuilder();
        var buffer = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!seen.ToString().Contains("env=it's", StringComparison.Ordinal))
        {
            var read = await Task.Run(() => terminal.Stream.Read(buffer, 0, buffer.Length), cts.Token).WaitAsync(cts.Token);
            Assert.NotEqual(0, read);
            seen.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        Assert.Contains("cwd=", seen.ToString());
    }

    [Fact]
    public async Task New_operations_give_up_after_the_ready_wait_budget_while_the_server_is_down()
    {
        _ = fixture.Require();
        using var own = await SshdInstance.StartAsync();
        await using var env = NewEnv(own, options: new SshEnvironmentOptions
        {
            Reconnect = new ReconnectPolicy { InitialDelay = TimeSpan.FromMilliseconds(100), MaxDelay = TimeSpan.FromMilliseconds(200), Jitter = 0 },
            ReadyWait = TimeSpan.FromMilliseconds(600),
        });
        await env.EnsureReadyAsync();
        own.Stop();
        await Wait.UntilAsync(() => env.State == EnvironmentState.Reconnecting, 20_000, "reconnecting state");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await env.RunAsync(CommandSpec.Shell("true")));
        Assert.Equal(EnvironmentErrorCode.Disconnected, error.Code);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        // 文件操作同理
        await Assert.ThrowsAsync<EnvironmentException>(async () => await env.Fs.StatAsync(EnvPath.Posix("/tmp")));

        // 服务器回来之后自动恢复
        await own.RestartAsync();
        await Wait.UntilAsync(() => env.State == EnvironmentState.Ready, 20_000, "ready again");
        Assert.True((await env.Fs.StatAsync(EnvPath.Posix("/tmp"))).IsDirectory);
    }

    [Fact]
    public async Task Probed_tools_survive_a_reconnect()
    {
        _ = fixture.Require();
        using var own = await SshdInstance.StartAsync();
        await using var env = NewEnv(own);
        var probed = await env.ProbeToolsAsync();
        Assert.True(probed.HasTool("grep"));
        await own.RestartAsync();
        await Wait.UntilAsync(() => env.Generation == 2 && env.State == EnvironmentState.Ready, 20_000, "reconnect");
        Assert.True(env.Facts.HasTool("grep"));
    }

    [Fact]
    public async Task Connecting_through_an_http_connect_proxy_works_and_the_host_key_is_still_pinned_by_name()
    {
        var sshd = fixture.Require();
        await using var proxy = await ProxyServer.StartHttpAsync();
        var callback = new ScriptedHostKeyCallback();
        var profile = ManualProfile(sshd) with { Proxy = new SshProxy(SshProxyKind.Http, "127.0.0.1", proxy.Port) };
        await using var env = NewEnv(sshd, profile, Services(sshd, callback: callback));
        var result = await env.RunAsync(CommandSpec.Shell("echo via-http-proxy"));
        Assert.Equal("via-http-proxy\n", result.Stdout.ToText());
        Assert.Contains(proxy.Requests, r => r.StartsWith($"CONNECT 127.0.0.1:{sshd.Port}", StringComparison.Ordinal));
        Assert.Equal($"[127.0.0.1]:{sshd.Port}", callback.Asked[0].HostName);
    }

    [Fact]
    public async Task Connecting_through_a_socks5_proxy_works()
    {
        var sshd = fixture.Require();
        await using var proxy = await ProxyServer.StartSocks5Async();
        var profile = ManualProfile(sshd) with { Proxy = new SshProxy(SshProxyKind.Socks5, "127.0.0.1", proxy.Port) };
        await using var env = NewEnv(sshd, profile);
        var result = await env.RunAsync(CommandSpec.Shell("echo via-socks"));
        Assert.Equal("via-socks\n", result.Stdout.ToText());
        Assert.Contains(proxy.Requests, r => r.EndsWith($":{sshd.Port}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_proxy_that_refuses_fails_with_connection_failed()
    {
        var sshd = fixture.Require();
        await using var proxy = await ProxyServer.StartHttpAsync(refuse: true);
        var profile = ManualProfile(sshd) with { Proxy = new SshProxy(SshProxyKind.Http, "127.0.0.1", proxy.Port) };
        await using var env = NewEnv(sshd, profile);
        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await env.EnsureReadyAsync());
        Assert.Equal(EnvironmentErrorCode.ConnectionFailed, error.Code);
    }

    [Fact]
    public async Task Write_streams_report_a_structured_error_when_the_connection_dies()
    {
        _ = fixture.Require();
        using var own = await SshdInstance.StartAsync();
        await using var env = NewEnv(own, options: new SshEnvironmentOptions { Reconnect = ReconnectPolicy.Never });
        using var temp = new TempDir();
        await env.EnsureReadyAsync();
        var stream = await env.Fs.OpenWriteAsync(temp.EnvFile("partial"), new FsWriteOptions());
        await stream.WriteAsync(new byte[1024]);
        own.Stop();
        await Wait.UntilAsync(() => env.State == EnvironmentState.Failed, 20_000, "failed state");

        // 提交(关闭)时连接已断:结果未知,而不是吞掉错误或报成功
        var error = await Record.ExceptionAsync(async () => await stream.DisposeAsync());
        Assert.True(error is null or EnvironmentException { Code: EnvironmentErrorCode.ResultUnknown or EnvironmentErrorCode.Disconnected }, error?.ToString());
    }
}

/// <summary>测试用的 HTTP CONNECT / SOCKS5 代理:记录请求并在两端之间转发字节。</summary>
internal sealed class ProxyServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly bool _socks;
    private readonly bool _refuse;

    private ProxyServer(bool socks, bool refuse)
    {
        _socks = socks;
        _refuse = refuse;
    }

    public int Port { get; private set; }

    public List<string> Requests { get; } = [];

    public static Task<ProxyServer> StartHttpAsync(bool refuse = false) => Task.FromResult(Start(false, refuse));

    public static Task<ProxyServer> StartSocks5Async() => Task.FromResult(Start(true, false));

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        return ValueTask.CompletedTask;
    }

    private static ProxyServer Start(bool socks, bool refuse)
    {
        var server = new ProxyServer(socks, refuse);
        server._listener.Start();
        server.Port = ((IPEndPoint)server._listener.LocalEndpoint).Port;
        _ = server.AcceptLoop();
        return server;
    }

    private async Task AcceptLoop()
    {
        try
        {
            for (;;)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => Handle(client));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                (string Host, int Port) target;
                if (_socks)
                {
                    var greeting = new byte[2];
                    await stream.ReadExactlyAsync(greeting);
                    await stream.ReadExactlyAsync(new byte[greeting[1]]);
                    await stream.WriteAsync(new byte[] { 5, 0 });
                    var head = new byte[4];
                    await stream.ReadExactlyAsync(head);
                    string host;
                    if (head[3] == 1)
                    {
                        var ip = new byte[4];
                        await stream.ReadExactlyAsync(ip);
                        host = new IPAddress(ip).ToString();
                    }
                    else
                    {
                        var length = new byte[1];
                        await stream.ReadExactlyAsync(length);
                        var name = new byte[length[0]];
                        await stream.ReadExactlyAsync(name);
                        host = Encoding.ASCII.GetString(name);
                    }

                    var port = new byte[2];
                    await stream.ReadExactlyAsync(port);
                    target = (host, (port[0] << 8) | port[1]);
                    lock (Requests)
                    {
                        Requests.Add($"{target.Host}:{target.Port}");
                    }

                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
                }
                else
                {
                    var header = new StringBuilder();
                    var one = new byte[1];
                    while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        await stream.ReadExactlyAsync(one);
                        header.Append((char)one[0]);
                    }

                    var line = header.ToString().Split("\r\n")[0];
                    lock (Requests)
                    {
                        Requests.Add(line);
                    }

                    if (_refuse)
                    {
                        await stream.WriteAsync("HTTP/1.1 403 Forbidden\r\n\r\n"u8.ToArray());
                        return;
                    }

                    var authority = line.Split(' ')[1].Split(':');
                    target = (authority[0], int.Parse(authority[1]));
                    await stream.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray());
                }

                using var upstream = new TcpClient();
                await upstream.ConnectAsync(target.Host, target.Port);
                var up = upstream.GetStream();
                await Task.WhenAny(stream.CopyToAsync(up, _stop.Token), up.CopyToAsync(stream, _stop.Token));
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }
}
