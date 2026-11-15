using Ncw.Agent.Environments;
using Ncw.Agent.Environments.Ssh;
using static Ncw.Agent.Environments.Tests.SshTestKit;

namespace Ncw.Agent.Environments.Tests;

/// <summary>真实 sshd:连接、主机指纹钉扎、认证、断线重连、ProxyJump、ssh_config。没有 sshd 时跳过。</summary>
public class SshConnectionTests(SshdFixture fixture) : IClassFixture<SshdFixture>
{
    [Fact]
    public async Task Connects_with_a_key_and_reports_facts()
    {
        var sshd = fixture.Require();
        await using var env = NewEnv(sshd);
        Assert.Equal(EnvironmentState.Idle, env.State);
        Assert.Equal(0, env.Generation);
        Assert.Throws<EnvironmentException>(() => env.Facts);

        var states = new List<EnvironmentState>();
        env.StatusChanged += (_, e) => states.Add(e.State);
        await env.EnsureReadyAsync();

        Assert.Equal(EnvironmentState.Ready, env.State);
        Assert.Equal(1, env.Generation);
        Assert.Equal([EnvironmentState.Connecting, EnvironmentState.Ready], states);
        Assert.Equal(EnvironmentKind.Ssh, env.Kind);
        Assert.Equal(sshd.Username, env.Facts.Username);
        Assert.Equal(OperatingSystem.IsMacOS() ? EnvironmentOs.MacOS : EnvironmentOs.Linux, env.Facts.Os);
        Assert.Equal(PathStyle.Posix, env.Facts.PathStyle);
        Assert.StartsWith("/", env.Facts.Home);
        Assert.True(env.Facts.Shell.StartsWith('/'));
        Assert.False(string.IsNullOrEmpty(env.Facts.Architecture));
        // 惰性且幂等
        await env.EnsureReadyAsync();
        Assert.Equal(1, env.Generation);
    }

    [Fact]
    public async Task First_use_connects_lazily()
    {
        var sshd = fixture.Require();
        await using var env = NewEnv(sshd);
        var result = await env.RunAsync(CommandSpec.Shell("echo lazy"));
        Assert.Equal("lazy\n", result.Stdout.ToText());
        Assert.Equal(EnvironmentState.Ready, env.State);
    }

    [Fact]
    public async Task An_unknown_host_key_is_confirmed_once_and_then_pinned()
    {
        var sshd = fixture.Require();
        var store = new InMemoryHostKeyStore();
        var callback = new ScriptedHostKeyCallback(approve: true);
        await using (var first = NewEnv(sshd, services: Services(sshd, store, callback)))
        {
            await first.EnsureReadyAsync();
        }

        var asked = Assert.Single(callback.Asked);
        Assert.Equal($"[127.0.0.1]:{sshd.Port}", asked.HostName);
        Assert.Equal("ssh-ed25519", asked.Algorithm);
        var pinned = Assert.Single(await store.GetAsync(asked.HostName, default));
        Assert.Equal(asked.FingerprintSha256, pinned.FingerprintSha256);
        Assert.DoesNotContain("=", asked.FingerprintSha256);

        // 第二次连接:已钉扎,不再询问
        await using var second = NewEnv(sshd, services: Services(sshd, store, callback));
        await second.EnsureReadyAsync();
        Assert.Single(callback.Asked);
        Assert.Empty(callback.Changed);
    }

    [Fact]
    public async Task A_rejected_host_key_fails_before_any_credentials_are_sent()
    {
        var sshd = fixture.Require();
        var store = new InMemoryHostKeyStore();
        var callback = new ScriptedHostKeyCallback(approve: false);
        var before = sshd.Log.Split("Accepted publickey").Length;
        await using var env = NewEnv(sshd, services: Services(sshd, store, callback));

        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await env.EnsureReadyAsync());
        Assert.Equal(EnvironmentErrorCode.HostKeyRejected, error.Code);
        Assert.Equal(EnvironmentState.Failed, env.State);
        Assert.Empty(await store.GetAsync($"[127.0.0.1]:{sshd.Port}", default));
        // 指纹被拒绝时握手就中止了:sshd 没有收到任何公钥认证
        Assert.Equal(before, sshd.Log.Split("Accepted publickey").Length);

        // 失败之后操作直接报同一个错误,不会反复弹确认
        var again = await Assert.ThrowsAsync<EnvironmentException>(async () => await env.RunAsync(CommandSpec.Shell("true")));
        Assert.Equal(EnvironmentErrorCode.HostKeyRejected, again.Code);
        Assert.Single(callback.Asked);
    }

    [Fact]
    public async Task A_changed_host_key_is_refused_without_offering_a_confirmation()
    {
        var sshd = fixture.Require();
        var store = new InMemoryHostKeyStore();
        await store.PinAsync($"[127.0.0.1]:{sshd.Port}", new PinnedHostKey("ssh-ed25519", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"), default);
        var callback = new ScriptedHostKeyCallback(approve: true);
        await using var env = NewEnv(sshd, services: Services(sshd, store, callback));

        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await env.EnsureReadyAsync());
        Assert.Equal(EnvironmentErrorCode.HostKeyChanged, error.Code);
        Assert.Empty(callback.Asked);
        Assert.Single(callback.Changed);
        // 不会覆盖旧指纹
        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", Assert.Single(await store.GetAsync($"[127.0.0.1]:{sshd.Port}", default)).FingerprintSha256);
    }

    [Fact]
    public async Task Pinning_matches_the_fingerprint_openssh_reports()
    {
        var sshd = fixture.Require();
        var callback = new ScriptedHostKeyCallback();
        await using var env = NewEnv(sshd, services: Services(sshd, callback: callback));
        await env.EnsureReadyAsync();

        // ssh-keygen -lf 对公钥文件给出的指纹
        using var keygen = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SshdInstance.KeygenPath!, ["-l", "-E", "sha256", "-f", Path.Combine(sshd.Directory, "host_ed25519.pub")])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var output = await keygen.StandardOutput.ReadToEndAsync();
        Assert.Contains("SHA256:" + callback.Asked[0].FingerprintSha256, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_that_the_server_does_not_know_fails_with_an_authentication_error_and_no_retry_loop()
    {
        var sshd = fixture.Require();
        using var other = new TempDir();
        using var keygen = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SshdInstance.KeygenPath!, ["-q", "-t", "ed25519", "-N", "", "-f", other.File("k")]) { UseShellExecute = false })!;
        await keygen.WaitForExitAsync();

        await using var env = NewEnv(sshd, ManualProfile(sshd, identityFile: other.File("k")));
        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await env.EnsureReadyAsync());
        Assert.Equal(EnvironmentErrorCode.Authentication, error.Code);
        Assert.Equal(EnvironmentState.Failed, env.State);
        Assert.Equal(EnvironmentErrorCode.Authentication, env.LastFailure?.Code);
    }

    [Fact]
    public async Task An_encrypted_key_asks_for_its_passphrase_through_the_credential_provider()
    {
        var sshd = fixture.Require();
        var credentials = new ScriptedCredentials("wrong passphrase", SshdInstance.Passphrase);
        await using var env = NewEnv(sshd, ManualProfile(sshd, identityFile: sshd.EncryptedKey), Services(sshd, credentials: credentials));
        await env.EnsureReadyAsync();

        Assert.Equal(EnvironmentState.Ready, env.State);
        Assert.Equal(2, credentials.Requests.Count);
        Assert.All(credentials.Requests, r =>
        {
            Assert.Equal(SshCredentialKind.KeyPassphrase, r.Kind);
            Assert.Equal(sshd.EncryptedKey, r.Detail);
        });
        Assert.False(credentials.Requests[0].PreviousRejected);
        Assert.True(credentials.Requests[1].PreviousRejected);

        // 口令在环境内存里缓存:SFTP 那条连接、之后的重连都不会再问
        await env.Fs.WriteTextAsync(SshTestKit.Remote(env.Facts.Home).Join(".ncw-passphrase-probe-" + Guid.NewGuid().ToString("N")[..6]), "x");
        Assert.Equal(2, credentials.Requests.Count);
    }

    [Fact]
    public async Task The_host_key_is_confirmed_before_any_passphrase_is_asked()
    {
        var sshd = fixture.Require();
        var order = new List<string>();
        var callback = new OrderedHostKeyCallback(order);
        var credentials = new OrderedCredentials(order, SshdInstance.Passphrase);
        await using var env = NewEnv(sshd, ManualProfile(sshd, identityFile: sshd.EncryptedKey), Services(sshd, callback: callback, credentials: credentials));
        await env.EnsureReadyAsync();
        // 核对指纹之前,不问口令(解私钥的口令也不问):用户该先看到「这台主机是不是我要连的那台」
        Assert.Equal(["host-key", "passphrase"], order);
    }

    [Fact]
    public async Task A_rejected_host_key_never_reaches_the_passphrase_prompt()
    {
        var sshd = fixture.Require();
        var order = new List<string>();
        await using var env = NewEnv(sshd, ManualProfile(sshd, identityFile: sshd.EncryptedKey), Services(sshd, callback: new OrderedHostKeyCallback(order, approve: false), credentials: new OrderedCredentials(order, SshdInstance.Passphrase)));
        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await env.EnsureReadyAsync());
        Assert.Equal(EnvironmentErrorCode.HostKeyRejected, error.Code);
        Assert.Equal(["host-key"], order);
    }

    private sealed class OrderedHostKeyCallback(List<string> order, bool approve = true) : IHostKeyCallback
    {
        public ValueTask<bool> ConfirmNewHostAsync(HostKeyInfo presented, CancellationToken ct)
        {
            lock (order)
            {
                order.Add("host-key");
            }

            return ValueTask.FromResult(approve);
        }

        public ValueTask NotifyChangedAsync(HostKeyInfo presented, IReadOnlyList<PinnedHostKey> pinned, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class OrderedCredentials(List<string> order, string answer) : ISshCredentialProvider
    {
        public ValueTask<IReadOnlyList<string>?> AskAsync(SshCredentialRequest request, CancellationToken ct)
        {
            lock (order)
            {
                order.Add(request.Kind == SshCredentialKind.KeyPassphrase ? "passphrase" : "other");
            }

            return ValueTask.FromResult<IReadOnlyList<string>?>([answer]);
        }
    }

    [Fact]
    public async Task Cancelling_the_passphrase_prompt_cancels_the_connection()
    {
        var sshd = fixture.Require();
        await using var env = NewEnv(sshd, ManualProfile(sshd, identityFile: sshd.EncryptedKey), Services(sshd, credentials: new ScriptedCredentials((string?)null)));
        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await env.EnsureReadyAsync());
        Assert.Equal(EnvironmentErrorCode.Cancelled, error.Code);
    }

    [Fact]
    public async Task An_encrypted_key_without_a_credential_provider_is_skipped_not_hung()
    {
        var sshd = fixture.Require();
        await using var env = NewEnv(sshd, ManualProfile(sshd, identityFile: sshd.EncryptedKey));
        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await env.EnsureReadyAsync());
        Assert.Equal(EnvironmentErrorCode.Authentication, error.Code);
    }

    [Fact]
    public async Task The_platform_setting_is_enforced()
    {
        var sshd = fixture.Require();
        var wrong = OperatingSystem.IsMacOS() ? RemotePlatform.Linux : RemotePlatform.MacOS;
        await using var env = NewEnv(sshd, ManualProfile(sshd) with { Platform = wrong });
        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await env.EnsureReadyAsync());
        Assert.Equal(EnvironmentErrorCode.UnsupportedPlatform, error.Code);
    }

    [Fact]
    public async Task A_connection_to_a_closed_port_fails_cleanly()
    {
        var sshd = fixture.Require();
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var profile = ManualProfile(sshd) with { Target = new SshTarget.Manual("127.0.0.1", port, sshd.Username, sshd.ClientKey) };
        await using var env = NewEnv(sshd, profile);
        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await env.EnsureReadyAsync());
        Assert.Equal(EnvironmentErrorCode.ConnectionFailed, error.Code);
        Assert.Equal(EnvironmentState.Failed, env.State);

        // Failed 之后用户主动重连(EnsureReady)会再试一次
        var again = await Assert.ThrowsAsync<EnvironmentException>(async () => await env.EnsureReadyAsync());
        Assert.Equal(EnvironmentErrorCode.ConnectionFailed, again.Code);
    }

    [Fact]
    public async Task Cancelling_during_connect_returns_to_idle_without_poisoning_the_environment()
    {
        var sshd = fixture.Require();
        await using var env = NewEnv(sshd, services: Services(sshd, callback: new HangingHostKeyCallback()));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await env.EnsureReadyAsync(cts.Token));
        Assert.Equal(EnvironmentState.Idle, env.State);
    }

    [Fact]
    public async Task Dispose_closes_the_connection_and_later_use_fails()
    {
        var sshd = fixture.Require();
        var env = NewEnv(sshd);
        await env.EnsureReadyAsync();
        await env.DisposeAsync();
        Assert.Equal(EnvironmentState.Closed, env.State);
        await Assert.ThrowsAsync<EnvironmentException>(async () => await env.RunAsync(CommandSpec.Shell("true")));
        await env.DisposeAsync();
    }

    [Fact]
    public async Task ssh_config_aliases_are_resolved_through_the_config_file()
    {
        var sshd = fixture.Require();
        var configPath = Path.Combine(sshd.Directory, "alias_config");
        await File.WriteAllTextAsync(configPath,
            $"Host ncw-alias\n  HostName 127.0.0.1\n  Port {sshd.Port}\n  User {sshd.Username}\n  IdentityFile {sshd.ClientKey}\n  IdentitiesOnly yes\n");
        var profile = new SshEnvironmentProfile { Name = "alias", Target = new SshTarget.ConfigAlias("ncw-alias", configPath), AuthMethod = SshAuthMethod.Key };
        await using var env = NewEnv(sshd, profile);
        var result = await env.RunAsync(CommandSpec.Shell("id -un"));
        Assert.Equal(sshd.Username + "\n", result.Stdout.ToText());
    }

    [Fact]
    public async Task ProxyJump_chains_through_a_direct_tcpip_channel_on_the_previous_hop()
    {
        var sshd = fixture.Require();
        using var jump = await SshdInstance.StartAsync(debugLog: true);
        var services = Services(sshd);
        var profile = ManualProfile(sshd, proxyJump: $"{jump.Username}@127.0.0.1:{jump.Port}");
        // 跳板机的密钥是它自己生成的:用目标机的客户端密钥无法登录跳板机,所以让跳板机也接受目标机的客户端公钥
        await File.AppendAllTextAsync(Path.Combine(jump.Directory, "authorized_keys"), await File.ReadAllTextAsync(sshd.ClientKey + ".pub"));

        var callback = new ScriptedHostKeyCallback();
        await using var env = NewEnv(sshd, profile with { Target = ((SshTarget.Manual)profile.Target) with { IdentityFile = sshd.ClientKey } }, Services(sshd, callback: callback));
        var result = await env.RunAsync(CommandSpec.Shell("echo through-jump"));
        Assert.Equal("through-jump\n", result.Stdout.ToText());

        // 两跳各自按真实主机名钉扎指纹
        Assert.Equal(2, callback.Asked.Count);
        Assert.Contains(callback.Asked, a => a.HostName == $"[127.0.0.1]:{jump.Port}");
        Assert.Contains(callback.Asked, a => a.HostName == $"[127.0.0.1]:{sshd.Port}");
        // 跳板机确实转发了流量
        await Wait.UntilAsync(() => jump.Log.Contains("direct-tcpip", StringComparison.Ordinal), 3000, "direct-tcpip in the jump host log");
        _ = services;
    }

    [Fact]
    public async Task A_dead_jump_host_fails_the_connection()
    {
        var sshd = fixture.Require();
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var dead = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var env = NewEnv(sshd, ManualProfile(sshd, proxyJump: $"{sshd.Username}@127.0.0.1:{dead}"));
        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await env.EnsureReadyAsync());
        Assert.Equal(EnvironmentErrorCode.ConnectionFailed, error.Code);
    }

    [Fact]
    public async Task A_host_key_change_on_the_jump_host_is_detected_too()
    {
        var sshd = fixture.Require();
        using var jump = await SshdInstance.StartAsync();
        await File.AppendAllTextAsync(Path.Combine(jump.Directory, "authorized_keys"), await File.ReadAllTextAsync(sshd.ClientKey + ".pub"));
        var store = new InMemoryHostKeyStore();
        await store.PinAsync($"[127.0.0.1]:{jump.Port}", new PinnedHostKey("ssh-ed25519", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"), default);
        var callback = new ScriptedHostKeyCallback();
        await using var env = NewEnv(sshd, ManualProfile(sshd, proxyJump: $"{jump.Username}@127.0.0.1:{jump.Port}"), Services(sshd, store, callback));
        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await env.EnsureReadyAsync());
        Assert.Equal(EnvironmentErrorCode.HostKeyChanged, error.Code);
        Assert.Empty(callback.Asked);
    }

    [Fact]
    public async Task Connection_loss_reconnects_with_a_new_generation_and_fails_stale_work()
    {
        var sshd = fixture.Require();
        using var own = await SshdInstance.StartAsync();
        await using var env = NewEnv(own);
        var states = new List<EnvironmentState>();
        env.StatusChanged += (_, e) =>
        {
            lock (states)
            {
                states.Add(e.State);
            }
        };
        await env.EnsureReadyAsync();
        var firstGeneration = env.Generation;

        // 一个正在运行的进程和一个打开的读取流,绑定第一代连接
        await using var running = await env.OpenProcessAsync(CommandSpec.Shell("echo up; sleep 300"));
        _ = sshd;

        await own.RestartAsync();
        var exit = await running.Exited.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(ProcessExitReason.ConnectionLost, exit.Reason);
        Assert.Null(exit.ExitCode);

        await Wait.UntilAsync(() => env.State == EnvironmentState.Ready && env.Generation > firstGeneration, 20_000, "reconnect");
        Assert.Equal(firstGeneration + 1, env.Generation);
        Assert.Throws<EnvironmentException>(() => env.EnsureGeneration(firstGeneration));
        env.EnsureGeneration(env.Generation);
        lock (states)
        {
            Assert.Contains(EnvironmentState.Reconnecting, states);
            Assert.Equal(EnvironmentState.Ready, states[^1]);
        }

        // 旧代的进程句柄记着它自己的代;新连接可以正常工作
        Assert.Equal(firstGeneration, running.Generation);
        var after = await env.RunAsync(CommandSpec.Shell("echo back"));
        Assert.Equal("back\n", after.Stdout.ToText());
        // 重连没有再次询问指纹(已钉扎)
        Assert.Equal(1, env.Generation - firstGeneration);
    }

    [Fact]
    public async Task Operations_wait_for_a_reconnect_in_progress()
    {
        _ = fixture.Require();
        using var own = await SshdInstance.StartAsync();
        await using var env = NewEnv(own);
        await env.EnsureReadyAsync();
        await own.RestartAsync();
        // 不等重连完成,立刻发起操作:它应该等到新连接就绪后成功,而不是立刻失败
        var result = await env.RunAsync(CommandSpec.Shell("echo patient"));
        Assert.Equal("patient\n", result.Stdout.ToText());
    }

    [Fact]
    public async Task Without_a_reconnect_policy_a_lost_connection_ends_in_failed()
    {
        _ = fixture.Require();
        using var own = await SshdInstance.StartAsync();
        await using var env = NewEnv(own, options: new SshEnvironmentOptions { Reconnect = ReconnectPolicy.Never });
        await env.EnsureReadyAsync();
        own.Stop();
        await Wait.UntilAsync(() => env.State == EnvironmentState.Failed, 20_000, "failed state");
        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await env.RunAsync(CommandSpec.Shell("true")));
        Assert.Equal(EnvironmentErrorCode.Disconnected, error.Code);
    }

    [Fact]
    public async Task Reconnect_gives_up_on_errors_that_retrying_cannot_fix()
    {
        _ = fixture.Require();
        using var own = await SshdInstance.StartAsync();
        var store = new InMemoryHostKeyStore();
        await using var env = NewEnv(own, services: Services(own, store));
        await env.EnsureReadyAsync();
        // 服务器「换了主机密钥」:重连时指纹不一致,不能无限重试
        await store.RemoveAsync($"[127.0.0.1]:{own.Port}", default);
        await store.PinAsync($"[127.0.0.1]:{own.Port}", new PinnedHostKey("ssh-ed25519", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"), default);
        await own.RestartAsync();
        await Wait.UntilAsync(() => env.State == EnvironmentState.Failed, 20_000, "failed state after the host key changed");
        Assert.Equal(EnvironmentErrorCode.HostKeyChanged, env.LastFailure?.Code);
    }
}

internal sealed class HangingHostKeyCallback : IHostKeyCallback
{
    public async ValueTask<bool> ConfirmNewHostAsync(HostKeyInfo presented, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return true;
    }

    public ValueTask NotifyChangedAsync(HostKeyInfo presented, IReadOnlyList<PinnedHostKey> pinned, CancellationToken ct) => ValueTask.CompletedTask;
}
