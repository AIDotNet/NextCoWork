using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using Ncw.Agent.Environments;
using Ncw.Agent.Environments.Ssh;
using static Ncw.Agent.Environments.Tests.SshTestKit;

namespace Ncw.Agent.Environments.Tests;

/// <summary>用户态 ssh-agent(<c>ssh-agent -D -a &lt;socket&gt;</c> + <c>ssh-add</c>)。没有 ssh-agent / ssh-add 时跳过。</summary>
internal sealed class SshAgentInstance : IDisposable
{
    private readonly Process _process;

    private SshAgentInstance(Process process, string directory, string socket)
    {
        _process = process;
        Directory = directory;
        Socket = socket;
    }

    public static bool Available => !OperatingSystem.IsWindows() && File.Exists("/usr/bin/ssh-agent") && File.Exists("/usr/bin/ssh-add");

    public string Directory { get; }

    public string Socket { get; }

    public static async Task<SshAgentInstance> StartAsync()
    {
        // Unix 套接字路径有长度上限(macOS 104 字节):目录名取短的
        var directory = Path.Combine(Path.GetTempPath(), "ncwa-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(directory);
        var socket = Path.Combine(directory, "a.sock");
        var process = Process.Start(new ProcessStartInfo("/usr/bin/ssh-agent", ["-D", "-a", socket])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var deadline = Environment.TickCount64 + 10_000;
        while (!File.Exists(socket))
        {
            if (Environment.TickCount64 > deadline || process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException("ssh-agent did not start");
            }

            await Task.Delay(20);
        }

        return new SshAgentInstance(process, directory, socket);
    }

    /// <summary>把私钥加进 agent。有口令的私钥用 SSH_ASKPASS 脚本回答口令。</summary>
    public async Task AddAsync(string keyFile, string? passphrase = null)
    {
        var start = new ProcessStartInfo("/usr/bin/ssh-add", [keyFile])
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.Environment["SSH_AUTH_SOCK"] = Socket;
        if (passphrase is not null)
        {
            var askpass = Path.Combine(Directory, "askpass.sh");
            await File.WriteAllTextAsync(askpass, $"#!/bin/sh\necho '{passphrase}'\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(askpass, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            start.Environment["SSH_ASKPASS"] = askpass;
            start.Environment["SSH_ASKPASS_REQUIRE"] = "force";
            start.Environment["DISPLAY"] = "none";
        }

        using var process = Process.Start(start)!;
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("ssh-add failed: " + stderr);
        }
    }

    public void Dispose()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(3000);
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary><c>.pub</c> 文件里的公钥 blob。</summary>
    public static byte[] BlobOf(string pubFile) => Convert.FromBase64String(File.ReadAllText(pubFile).Split(' ')[1]);
}

/// <summary>
/// ssh-agent 对接(06 §3「认证」):协议客户端对真实 ssh-agent 列身份、签名;经 SSH.NET 的公钥认证(自定义的签名转给 agent 的 <c>HostAlgorithm</c>)连上真实 sshd。
/// 私钥全程不进本进程:agent 里的私钥文件被删掉之后照样能认证。
/// </summary>
public class SshAgentTests(SshdFixture fixture) : IClassFixture<SshdFixture>
{
    private static void RequireAgent() => Assert.SkipUnless(SshAgentInstance.Available, "no ssh-agent / ssh-add on this machine");

    [Fact]
    public async Task Lists_the_identities_the_agent_holds()
    {
        RequireAgent();
        var sshd = fixture.Require();
        using var agent = await SshAgentInstance.StartAsync();
        var client = SshAgentClient.ForUnixSocket(agent.Socket);
        Assert.Empty(await client.ListIdentitiesAsync());

        await agent.AddAsync(sshd.ClientKey);
        var identity = Assert.Single(await client.ListIdentitiesAsync());
        Assert.Equal(SshAgentInstance.BlobOf(sshd.ClientKey + ".pub"), identity.PublicKeyBlob);
        Assert.Equal("ncw-test", identity.Comment);
        Assert.Equal("ssh-ed25519", AgentKeySource.KeyType(identity.PublicKeyBlob));
    }

    [Fact]
    public async Task Signs_with_a_key_that_only_the_agent_holds_and_a_server_accepts_it()
    {
        RequireAgent();
        var sshd = fixture.Require();
        using var agent = await SshAgentInstance.StartAsync();
        // 私钥文件拷一份给 agent 然后删掉:连接只可能靠 agent 里的身份
        using var temp = new TempDir();
        File.Copy(sshd.ClientKey, temp.File("k"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp.File("k"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        await agent.AddAsync(temp.File("k"));
        File.Delete(temp.File("k"));

        var options = new SshEnvironmentOptions { Agent = SshAgentClient.ForUnixSocket(agent.Socket), Reconnect = ReconnectPolicy.Never };
        await using var env = NewEnv(sshd, ManualProfile(sshd, identityFile: temp.File("gone")), options: options);
        await env.EnsureReadyAsync();
        Assert.Equal(EnvironmentState.Ready, env.State);
        Assert.Equal("agent\n", (await env.RunAsync(CommandSpec.Shell("echo agent"))).Stdout.ToText());
        Assert.Contains("Accepted publickey", sshd.Log);
    }

    [Fact]
    public async Task A_passphrase_key_the_agent_already_holds_is_not_asked_for_its_passphrase()
    {
        RequireAgent();
        var sshd = fixture.Require();
        using var agent = await SshAgentInstance.StartAsync();
        await agent.AddAsync(sshd.EncryptedKey, SshdInstance.Passphrase);

        var credentials = new ScriptedCredentials();
        var options = new SshEnvironmentOptions { Agent = SshAgentClient.ForUnixSocket(agent.Socket), Reconnect = ReconnectPolicy.Never };
        await using var env = NewEnv(sshd, ManualProfile(sshd, identityFile: sshd.EncryptedKey), Services(sshd, credentials: credentials), options);
        await env.EnsureReadyAsync();

        Assert.Equal(EnvironmentState.Ready, env.State);
        Assert.Empty(credentials.Requests);
    }

    [Fact]
    public async Task An_unreachable_agent_falls_back_to_the_key_file()
    {
        RequireAgent();
        var sshd = fixture.Require();
        var options = new SshEnvironmentOptions { Agent = SshAgentClient.ForUnixSocket(Path.Combine(sshd.Directory, "no-such.sock")), Reconnect = ReconnectPolicy.Never };
        await using var env = NewEnv(sshd, options: options);
        await env.EnsureReadyAsync();
        Assert.Equal(EnvironmentState.Ready, env.State);
    }

    [Fact]
    public async Task An_agent_without_the_right_key_does_not_authenticate()
    {
        RequireAgent();
        var sshd = fixture.Require();
        using var agent = await SshAgentInstance.StartAsync();
        using var other = new TempDir();
        using (var keygen = Process.Start(new ProcessStartInfo(SshdInstance.KeygenPath!, ["-q", "-t", "ed25519", "-N", "", "-f", other.File("k")]) { UseShellExecute = false })!)
        {
            await keygen.WaitForExitAsync();
        }

        await agent.AddAsync(other.File("k"));
        var options = new SshEnvironmentOptions { Agent = SshAgentClient.ForUnixSocket(agent.Socket), Reconnect = ReconnectPolicy.Never };
        await using var env = NewEnv(sshd, ManualProfile(sshd, identityFile: other.File("gone")), options: options);
        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await env.EnsureReadyAsync());
        Assert.Equal(EnvironmentErrorCode.Authentication, error.Code);
    }

    [Fact]
    public async Task An_rsa_key_in_the_agent_signs_with_sha2()
    {
        RequireAgent();
        var sshd = fixture.Require();
        using var agent = await SshAgentInstance.StartAsync();
        using var temp = new TempDir();
        using (var keygen = Process.Start(new ProcessStartInfo(SshdInstance.KeygenPath!, ["-q", "-t", "rsa", "-b", "2048", "-N", "", "-f", temp.File("rsa")]) { UseShellExecute = false })!)
        {
            await keygen.WaitForExitAsync();
        }

        var authorized = Path.Combine(sshd.Directory, "authorized_keys");
        var before = await File.ReadAllTextAsync(authorized);
        try
        {
            await File.AppendAllTextAsync(authorized, await File.ReadAllTextAsync(temp.File("rsa.pub")));
            await agent.AddAsync(temp.File("rsa"));
            File.Delete(temp.File("rsa"));

            var options = new SshEnvironmentOptions { Agent = SshAgentClient.ForUnixSocket(agent.Socket), Reconnect = ReconnectPolicy.Never };
            await using var env = NewEnv(sshd, ManualProfile(sshd, identityFile: temp.File("gone")), options: options);
            await env.EnsureReadyAsync();
            Assert.Equal(EnvironmentState.Ready, env.State);
        }
        finally
        {
            await File.WriteAllTextAsync(authorized, before);
        }
    }

    // ---- 协议层(不需要真 agent):假 agent 说畸形的话 ----

    private static SshAgentClient FakeAgent(Func<byte, byte[], byte[]?> reply, TimeSpan? timeout = null) => new(_ =>
    {
        var pair = new FakeAgentStream(reply);
        return Task.FromResult<Stream>(pair);
    }, timeout);

    [Fact]
    public async Task A_malformed_or_oversized_reply_is_an_error_not_an_allocation()
    {
        var oversized = FakeAgent((_, _) => [0xFF, 0xFF, 0xFF, 0xFF, 12]);
        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await oversized.ListIdentitiesAsync());
        Assert.Equal(EnvironmentErrorCode.Unsupported, error.Code);

        var truncated = FakeAgent((_, _) => Frame(12, [0, 0, 0, 5, 0, 0, 0, 9, 1]));
        error = await Assert.ThrowsAsync<EnvironmentException>(async () => await truncated.ListIdentitiesAsync());
        Assert.Equal(EnvironmentErrorCode.Unsupported, error.Code);

        var tooMany = FakeAgent((_, _) => Frame(12, [0, 0xFF, 0xFF, 0xFF]));
        error = await Assert.ThrowsAsync<EnvironmentException>(async () => await tooMany.ListIdentitiesAsync());
        Assert.Equal(EnvironmentErrorCode.Unsupported, error.Code);
    }

    [Fact]
    public async Task A_failure_reply_to_a_sign_request_is_an_authentication_error()
    {
        var agent = FakeAgent((_, _) => Frame(5, []));
        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await agent.SignAsync([1, 2, 3], [4], 0));
        Assert.Equal(EnvironmentErrorCode.Authentication, error.Code);
    }

    [Fact]
    public async Task The_request_is_framed_per_the_protocol()
    {
        byte? type = null;
        byte[]? body = null;
        var agent = FakeAgent((t, b) =>
        {
            type = t;
            body = b;
            return Frame(14, [0, 0, 0, 3, 7, 8, 9]);
        });

        var signature = await agent.SignAsync([1, 2], [3, 4, 5], SshAgentClient.RsaSha2_512);
        Assert.Equal([7, 8, 9], signature);
        Assert.Equal((byte)13, type);
        // string key_blob, string data, uint32 flags
        Assert.Equal([0, 0, 0, 2, 1, 2, 0, 0, 0, 3, 3, 4, 5, 0, 0, 0, 4], body);
    }

    [Fact]
    public async Task A_silent_agent_times_out()
    {
        var agent = new SshAgentClient(_ => Task.FromResult<Stream>(new SilentStream()), TimeSpan.FromMilliseconds(200));
        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await agent.ListIdentitiesAsync());
        Assert.Equal(EnvironmentErrorCode.Timeout, error.Code);
    }

    [Fact]
    public async Task Discover_finds_nothing_without_a_socket()
    {
        Assert.Null(SshAgentClient.Discover(Path.Combine(Path.GetTempPath(), "ncw-no-such-agent.sock")));
        await Task.CompletedTask;
    }

    private static byte[] Frame(byte type, byte[] body)
    {
        var frame = new byte[4 + 1 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)(1 + body.Length));
        frame[4] = type;
        body.CopyTo(frame, 5);
        return frame;
    }

    /// <summary>读一个请求帧、交给脚本、把脚本给的原始字节当作回答。</summary>
    private sealed class FakeAgentStream(Func<byte, byte[], byte[]?> reply) : Stream
    {
        private readonly MemoryStream _request = new();
        private MemoryStream? _reply;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_reply is null)
            {
                var all = _request.ToArray();
                var raw = reply(all[4], all[5..]);
                _reply = new MemoryStream(raw ?? []);
            }

            return _reply.Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count) => _request.Write(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class SilentStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
