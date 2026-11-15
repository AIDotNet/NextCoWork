using System.Text;
using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Environments.Ssh;

namespace Ncw.Agent.Environments.Tests;

public class HostKeyPinningTests
{
    private static HostKeyInfo Key(string alg, string fp) => new("[h]:22", alg, fp);

    [Fact]
    public void Evaluate_follows_strict_known_hosts_semantics()
    {
        Assert.Equal(HostKeyVerdict.Unknown, HostKeyPinning.Evaluate([], Key("ssh-ed25519", "AAA")));
        PinnedHostKey[] pinned = [new("ssh-ed25519", "AAA")];
        Assert.Equal(HostKeyVerdict.Trusted, HostKeyPinning.Evaluate(pinned, Key("ssh-ed25519", "AAA")));
        Assert.Equal(HostKeyVerdict.Changed, HostKeyPinning.Evaluate(pinned, Key("ssh-ed25519", "BBB")));
        // 钉扎的是别的算法:当作新密钥让用户确认,而不是「指纹变了」
        Assert.Equal(HostKeyVerdict.Unknown, HostKeyPinning.Evaluate(pinned, Key("ecdsa-sha2-nistp256", "CCC")));
        // 同一算法钉扎了多把时,任意一把一致即可
        Assert.Equal(HostKeyVerdict.Trusted, HostKeyPinning.Evaluate([new("ssh-ed25519", "AAA"), new("ssh-ed25519", "BBB")], Key("ssh-ed25519", "BBB")));
    }

    [Fact]
    public void Pinned_algorithms_are_negotiated_first()
    {
        var order = HostKeyPinning.PreferPinned(["ssh-ed25519", "ecdsa-sha2-nistp256", "rsa-sha2-512"], [new("rsa-sha2-512", "x"), new("ecdsa-sha2-nistp256", "y")]);
        Assert.Equal(["ecdsa-sha2-nistp256", "rsa-sha2-512", "ssh-ed25519"], order);
        Assert.Equal(["a", "b"], HostKeyPinning.PreferPinned(["a", "b"], []));
    }

    [Theory]
    [InlineData("SHA256:abc=", "abc")]
    [InlineData("sha256:abc", "abc")]
    [InlineData("abc==", "abc")]
    [InlineData("  abc  ", "abc")]
    public void Fingerprints_are_normalized(string input, string expected) => Assert.Equal(expected, HostKeyPinning.NormalizeFingerprint(input));

    [Fact]
    public async Task In_memory_store_replaces_per_algorithm_and_removes_per_host()
    {
        var store = new InMemoryHostKeyStore();
        await store.PinAsync("h", new PinnedHostKey("ssh-ed25519", "1"), default);
        await store.PinAsync("h", new PinnedHostKey("ecdsa", "2"), default);
        await store.PinAsync("h", new PinnedHostKey("ssh-ed25519", "3"), default);
        var keys = await store.GetAsync("h", default);
        Assert.Equal(2, keys.Count);
        Assert.Contains(keys, k => k is { Algorithm: "ssh-ed25519", FingerprintSha256: "3" });
        Assert.Empty(await store.GetAsync("other", default));
        await store.RemoveAsync("h", default);
        Assert.Empty(await store.GetAsync("h", default));
    }

    [Fact]
    public async Task The_default_callback_rejects_everything()
    {
        Assert.False(await RejectingHostKeyCallback.Instance.ConfirmNewHostAsync(Key("a", "b"), default));
        Assert.Equal("SHA256:b", Key("a", "b").Display);
    }
}

public class ReconnectPolicyTests
{
    [Fact]
    public void Delays_grow_exponentially_up_to_the_cap()
    {
        var policy = new ReconnectPolicy { InitialDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(10), Factor = 2, Jitter = 0 };
        Assert.Equal([1, 2, 4, 8, 10, 10], Enumerable.Range(1, 6).Select(i => policy.DelayBefore(i, 0.5).TotalSeconds));
    }

    [Fact]
    public void Jitter_stays_within_its_bounds()
    {
        var policy = new ReconnectPolicy { InitialDelay = TimeSpan.FromSeconds(10), MaxDelay = TimeSpan.FromSeconds(100), Jitter = 0.2 };
        Assert.Equal(8, policy.DelayBefore(1, 0).TotalSeconds, 6);
        Assert.Equal(12, policy.DelayBefore(1, 1).TotalSeconds, 6);
        Assert.Equal(10, policy.DelayBefore(1, 0.5).TotalSeconds, 6);
    }

    [Fact]
    public void Retrying_stops_for_errors_that_retrying_cannot_fix()
    {
        var policy = ReconnectPolicy.Default;
        Assert.True(policy.ShouldRetry(0, null));
        Assert.True(policy.ShouldRetry(50, EnvironmentErrorCode.ConnectionFailed));
        Assert.True(policy.ShouldRetry(1, EnvironmentErrorCode.Timeout));
        foreach (var fatal in new[]
        {
            EnvironmentErrorCode.Authentication, EnvironmentErrorCode.HostKeyChanged, EnvironmentErrorCode.HostKeyRejected,
            EnvironmentErrorCode.InvalidProfile, EnvironmentErrorCode.Cancelled, EnvironmentErrorCode.UnsupportedPlatform,
        })
        {
            Assert.False(policy.ShouldRetry(1, fatal), fatal.ToString());
        }
    }

    [Fact]
    public void Max_attempts_is_respected()
    {
        var limited = new ReconnectPolicy { MaxAttempts = 3 };
        Assert.True(limited.ShouldRetry(2, null));
        Assert.False(limited.ShouldRetry(3, null));
        Assert.False(ReconnectPolicy.Never.ShouldRetry(0, null));
    }
}

public class SshAuthPlanTests
{
    [Fact]
    public void Each_method_enables_the_right_mechanisms()
    {
        Assert.True(SshAuthPlan.UsesKeys(SshAuthMethod.Auto));
        Assert.True(SshAuthPlan.UsesKeys(SshAuthMethod.Key));
        Assert.False(SshAuthPlan.UsesKeys(SshAuthMethod.Password));
        Assert.False(SshAuthPlan.UsesKeys(SshAuthMethod.Interactive));

        Assert.True(SshAuthPlan.UsesKeyboardInteractive(SshAuthMethod.Auto));
        Assert.True(SshAuthPlan.UsesKeyboardInteractive(SshAuthMethod.Interactive));
        Assert.False(SshAuthPlan.UsesKeyboardInteractive(SshAuthMethod.Key));

        Assert.True(SshAuthPlan.UsesPassword(SshAuthMethod.Auto));
        Assert.True(SshAuthPlan.UsesPassword(SshAuthMethod.Password));
        Assert.True(SshAuthPlan.UsesPassword(SshAuthMethod.Ask));
        Assert.False(SshAuthPlan.UsesPassword(SshAuthMethod.Key));

        // Auto 不会一上来就问密码(私钥被拒绝之后才问);用户明确选了密码才先问
        Assert.False(SshAuthPlan.AsksPasswordUpFront(SshAuthMethod.Auto));
        Assert.True(SshAuthPlan.AsksPasswordUpFront(SshAuthMethod.Password));
        Assert.True(SshAuthPlan.AsksPasswordUpFront(SshAuthMethod.Ask));
    }

    [Theory]
    [InlineData("No suitable authentication method found to complete authentication (publickey,password).", true)]
    [InlineData("No suitable authentication method found to complete authentication (publickey, keyboard-interactive, password).", true)]
    [InlineData("No suitable authentication method found to complete authentication (publickey).", false)]
    [InlineData("No suitable authentication method found to complete authentication (publickey,keyboard-interactive).", false)]
    [InlineData("something unexpected", true)]
    [InlineData(null, true)]
    public void The_servers_accepted_methods_are_read_from_the_failure_message(string? message, bool expected) =>
        Assert.Equal(expected, SshAuth.MessageMentionsPassword(message));

    [Fact]
    public void Default_identity_files_live_under_the_given_home()
    {
        var files = SshAuthPlan.DefaultIdentityFiles("/h");
        Assert.Equal(3, files.Count);
        Assert.All(files, f => Assert.StartsWith(Path.Combine("/h", ".ssh"), f));
        Assert.EndsWith("id_ed25519", files[0]);
    }
}

public class RemoteCommandTests
{
    private static EnvironmentFacts Linux(string shell = "/bin/bash") => new()
    {
        Os = EnvironmentOs.Linux, OsVersion = "6", Shell = shell, Home = "/home/u", Hostname = "h", Username = "u",
    };

    private static EnvironmentFacts Windows() => new()
    {
        Os = EnvironmentOs.Windows, OsVersion = "10", Shell = "powershell.exe", Home = @"C:\Users\u", Hostname = "h", Username = "u",
    };

    [Fact]
    public void Posix_exec_form_passes_every_argument_as_a_quoted_positional_parameter()
    {
        var launch = RemoteCommands.Build(Linux(), CommandSpec.Exec("node", "server.js", "it's", "a b") with { WorkingDirectory = EnvPath.Posix("/work dir") });
        Assert.StartsWith("exec /bin/sh -c '", launch.CommandText);
        Assert.EndsWith(" ncw '/work dir' 'node' 'server.js' 'it'\\''s' 'a b'", launch.CommandText);
        Assert.Contains("NCW-PID:", launch.CommandText);
        Assert.Contains("</dev/null", launch.CommandText);
        Assert.Null(launch.StdinPrefix);
    }

    [Fact]
    public void Posix_shell_form_runs_the_command_with_the_environments_shell()
    {
        var launch = RemoteCommands.Build(Linux("/bin/zsh"), CommandSpec.Shell("echo $HOME && ls 'x y'"));
        Assert.EndsWith(" ncw '' '/bin/zsh' 'echo $HOME && ls '\\''x y'\\'''", launch.CommandText);
    }

    [Fact]
    public void Stdin_stays_open_in_pipe_mode_and_closes_via_redirection_otherwise()
    {
        Assert.DoesNotContain("</dev/null", RemoteCommands.Build(Linux(), CommandSpec.Shell("cat") with { Stdin = StdinMode.Pipe }).CommandText);
        Assert.Contains("</dev/null", RemoteCommands.Build(Linux(), CommandSpec.Shell("cat")).CommandText);
    }

    [Fact]
    public void Environment_values_never_appear_in_the_command_text()
    {
        var spec = CommandSpec.Shell("env") with { Environment = new Dictionary<string, string> { ["API_KEY"] = "super-secret\nvalue", ["B"] = "ünï" } };
        var launch = RemoteCommands.Build(Linux(), spec);
        Assert.DoesNotContain("super-secret", launch.CommandText);
        Assert.DoesNotContain("API_KEY", launch.CommandText);
        Assert.Contains("read -r ncw_env", launch.CommandText);
        // 值只在 stdin 的第一行里,并且是八进制转义,原文也看不出来
        Assert.NotNull(launch.StdinPrefix);
        Assert.EndsWith("\n", launch.StdinPrefix);
        Assert.DoesNotContain("super-secret", launch.StdinPrefix);
        Assert.Contains("API_KEY=", launch.StdinPrefix);
    }

    [Fact]
    public void Posix_env_line_round_trips_through_a_real_shell()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX shell");
        var value = "a\nb  \"q\" 'q' %s \\ $X `y` 你好\n\n";
        var line = RemoteCommands.PosixEnvLine(new Dictionary<string, string> { ["NCW_V"] = value, ["NCW_EMPTY"] = "" });
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh", ["-c", line + " printf %s \"$NCW_V|$NCW_EMPTY|\""])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        Assert.Equal(value + "||", process.StandardOutput.ReadToEnd());
        process.WaitForExit();
    }

    [Fact]
    public void The_full_posix_wrapper_runs_for_real_and_reports_its_pid_on_stderr()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX shell");
        var spec = CommandSpec.Shell("printf '%s|%s' \"$FOO\" \"$(pwd)\"") with
        {
            Environment = new Dictionary<string, string> { ["FOO"] = "bar baz" },
            WorkingDirectory = EnvPath.Posix("/tmp"),
        };
        var launch = RemoteCommands.Build(Linux("/bin/sh"), spec);
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh", ["-c", launch.CommandText])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        process.StandardInput.Write(launch.StdinPrefix);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.StartsWith("bar baz|", stdout);
        Assert.EndsWith("tmp", stdout.Replace("/private", ""));
        Assert.Equal($"NCW-PID:{process.Id}\n", stderr);
    }

    [Fact]
    public void The_wrapper_fails_loudly_when_the_working_directory_is_missing()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX shell");
        var launch = RemoteCommands.Build(Linux("/bin/sh"), CommandSpec.Shell("echo should-not-run") with { WorkingDirectory = EnvPath.Posix("/definitely/not/here") });
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh", ["-c", launch.CommandText])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(126, process.ExitCode);
        Assert.Equal("", stdout);
    }

    [Fact]
    public void Windows_uses_an_encoded_powershell_script()
    {
        var spec = CommandSpec.Shell("Get-Location") with { WorkingDirectory = EnvPath.Windows(@"C:\work space") };
        var launch = RemoteCommands.Build(Windows(), spec);
        Assert.StartsWith("powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand ", launch.CommandText);
        var script = Decode(launch.CommandText);
        Assert.Contains("[Console]::Error.WriteLine('NCW-PID:' + $PID)", script);
        Assert.Contains("Set-Location -LiteralPath 'C:\\work space'", script);
        Assert.Contains("[ScriptBlock]::Create('Get-Location')", script);
        Assert.Null(launch.StdinPrefix);

        var exec = Decode(RemoteCommands.Build(Windows(), CommandSpec.Exec("node.exe", "a b", "it's")).CommandText);
        Assert.Contains("& 'node.exe' 'a b' 'it''s'", exec);
    }

    [Fact]
    public void Windows_environment_travels_as_base64_json_on_stdin()
    {
        var launch = RemoteCommands.Build(Windows(), CommandSpec.Shell("x") with { Environment = new Dictionary<string, string> { ["TOKEN"] = "s3cret" } });
        Assert.DoesNotContain("s3cret", launch.CommandText);
        Assert.DoesNotContain("s3cret", Decode(launch.CommandText));
        Assert.EndsWith("\n", launch.StdinPrefix);
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(launch.StdinPrefix!.Trim()));
        using var document = JsonDocument.Parse(json);
        Assert.Equal("s3cret", document.RootElement.GetProperty("TOKEN").GetString());
    }

    [Fact]
    public void Kill_commands_target_the_process_group_or_the_tree()
    {
        var term = RemoteCommands.Kill(EnvironmentOs.Linux, 4242, force: false);
        Assert.Contains("kill -s TERM -- \"-$1\"", term);
        Assert.EndsWith(" ncw 4242", term);
        Assert.Contains("kill -s KILL", RemoteCommands.Kill(EnvironmentOs.MacOS, 7, force: true));
        Assert.Equal("taskkill /PID 99 /T /F", RemoteCommands.Kill(EnvironmentOs.Windows, 99, force: false));
    }

    [Fact]
    public void The_posix_probe_is_parsed_into_facts()
    {
        var facts = RemoteCommands.ParsePosixProbe("Linux\0" + "6.8.0\0" + "devbox\0" + "alice\0" + "/home/alice\0" + "/bin/zsh\0" + "x86_64\0");
        Assert.NotNull(facts);
        Assert.Equal(EnvironmentOs.Linux, facts.Os);
        Assert.Equal("6.8.0", facts.OsVersion);
        Assert.Equal("devbox", facts.Hostname);
        Assert.Equal("alice", facts.Username);
        Assert.Equal("/home/alice", facts.Home);
        Assert.Equal("/bin/zsh", facts.Shell);
        Assert.Equal("x86_64", facts.Architecture);
        Assert.Equal(EnvironmentOs.MacOS, RemoteCommands.ParsePosixProbe("Darwin\0" + "24\0" + "h\0" + "u\0" + "/Users/u\0" + "/bin/bash\0")!.Os);
        // 不支持的系统、缺字段
        Assert.Null(RemoteCommands.ParsePosixProbe("FreeBSD\0" + "14\0" + "h\0" + "u\0" + "/home/u\0" + "/bin/sh\0"));
        Assert.Null(RemoteCommands.ParsePosixProbe("Linux\0" + "6\0" + "\0" + "u\0" + "/home/u\0" + "/bin/sh\0"));
        Assert.Null(RemoteCommands.ParsePosixProbe(""));
    }

    [Theory]
    [InlineData("/usr/bin/fish")]
    [InlineData("/bin/tcsh")]
    [InlineData("/usr/bin/nu")]
    [InlineData("/sbin/nologin")]
    public void Non_posix_login_shells_fall_back_to_bin_sh(string shell) => Assert.Equal("/bin/sh", RemoteCommands.NormalizeRemoteShell(shell));

    [Theory]
    [InlineData("/bin/bash")]
    [InlineData("/usr/local/bin/zsh")]
    [InlineData("/bin/dash")]
    public void Posix_login_shells_are_kept(string shell) => Assert.Equal(shell, RemoteCommands.NormalizeRemoteShell(shell));

    [Fact]
    public void The_windows_probe_is_parsed_into_facts()
    {
        var facts = RemoteCommands.ParseWindowsProbe("\uFEFF{\"os\":\"win32\",\"osVersion\":\"Microsoft Windows NT 10.0.22631.0\",\"hostname\":\"WIN\",\"username\":\"bob\",\"home\":\"C:\\\\Users\\\\bob\",\"shell\":\"C:\\\\Windows\\\\powershell.exe\",\"arch\":\"AMD64\"}\n");
        Assert.NotNull(facts);
        Assert.Equal(EnvironmentOs.Windows, facts.Os);
        Assert.Equal(@"C:\Users\bob", facts.Home);
        Assert.Equal(ShellDialect.PowerShell, facts.ShellDialect);
        Assert.Equal("AMD64", facts.Architecture);
        Assert.Null(RemoteCommands.ParseWindowsProbe("not json"));
        Assert.Null(RemoteCommands.ParseWindowsProbe("{\"os\":\"linux\"}"));
        Assert.Null(RemoteCommands.ParseWindowsProbe("{\"os\":\"win32\",\"osVersion\":\"1\"}"));
    }

    private static string Decode(string commandLine) => Encoding.Unicode.GetString(Convert.FromBase64String(commandLine[(commandLine.LastIndexOf(' ') + 1)..]));
}

public class PidMarkerStreamTests
{
    /// <summary>每次只给 <paramref name="chunk"/> 个字节的流,模拟标记被 TCP 拆成好几段到达。</summary>
    private sealed class DribbleStream(byte[] data, int chunk) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = Math.Min(Math.Min(chunk, count), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<(string Text, int? Pid)> ReadAll(byte[] input, int chunk)
    {
        await using var stream = new PidMarkerStream(new DribbleStream(input, chunk));
        using var reader = new MemoryStream();
        await stream.CopyToAsync(reader);
        return (Encoding.UTF8.GetString(reader.ToArray()), await stream.Pid);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(1000)]
    public async Task The_marker_line_is_removed_however_it_arrives(int chunk)
    {
        var (text, pid) = await ReadAll("NCW-PID:12345\nreal stderr\nmore\n"u8.ToArray(), chunk);
        Assert.Equal(12345, pid);
        Assert.Equal("real stderr\nmore\n", text);
    }

    [Fact]
    public async Task Output_without_the_marker_passes_through_untouched()
    {
        var (text, pid) = await ReadAll("bash: line 1: exec: failed\nsecond line\n"u8.ToArray(), 4);
        Assert.Null(pid);
        Assert.Equal("bash: line 1: exec: failed\nsecond line\n", text);
    }

    [Fact]
    public async Task Marker_only_and_empty_streams_are_handled()
    {
        var (text, pid) = await ReadAll("NCW-PID:7\n"u8.ToArray(), 100);
        Assert.Equal(7, pid);
        Assert.Equal("", text);
        var (empty, noPid) = await ReadAll([], 10);
        Assert.Equal("", empty);
        Assert.Null(noPid);
    }

    [Fact]
    public async Task A_malformed_marker_is_treated_as_ordinary_output()
    {
        var (text, pid) = await ReadAll("NCW-PID:abc\nrest"u8.ToArray(), 5);
        Assert.Null(pid);
        Assert.Equal("NCW-PID:abc\nrest", text);
    }

    [Fact]
    public async Task A_long_first_line_without_newline_is_not_swallowed()
    {
        var long_ = new string('x', 500);
        var (text, pid) = await ReadAll(Encoding.UTF8.GetBytes(long_), 16);
        Assert.Null(pid);
        Assert.Equal(long_, text);
    }
}

public class EnvironmentRegistryTests
{
    private static SshEnvironmentProfile Profile(string name = "box") => new()
    {
        Name = name,
        Target = new SshTarget.Manual("127.0.0.1", 1, "u", "/nonexistent/key"),
        ConnectTimeout = TimeSpan.FromSeconds(2),
    };

    private static EnvironmentRegistry NewRegistry() => new("dev-1", new EnvironmentRegistryOptions
    {
        SshServices = new SshEnvironmentServices { HostKeys = new InMemoryHostKeyStore() },
        SshOptions = new SshEnvironmentOptions { Reconnect = ReconnectPolicy.Never },
    });

    [Fact]
    public async Task A_new_registry_has_the_local_environment_as_default()
    {
        await using var registry = NewRegistry();
        var list = registry.List();
        var local = Assert.Single(list);
        Assert.Equal("local", local.Id);
        Assert.Equal(EnvironmentKind.Local, local.Kind);
        Assert.Equal("local", registry.DefaultId);

        var environment = registry.Get();
        Assert.IsType<LocalEnvironment>(environment);
        Assert.Equal(new EnvironmentId("dev-1", "local"), environment.Id);
        Assert.Same(environment, registry.Get("local"));
    }

    [Fact]
    public async Task Disconnecting_drops_only_the_runtime_instance_and_the_next_get_creates_a_fresh_idle_one()
    {
        await using var registry = NewRegistry();
        registry.AddSsh("dev-box", "Dev box", Profile());
        Assert.Null(registry.TryGetInstance("dev-box")); // 还没被用过:没有实例(TryGet 不创建)
        Assert.False(await registry.DisconnectAsync("dev-box")); // 没有实例可断

        var first = registry.Get("dev-box");
        Assert.Same(first, registry.TryGetInstance("dev-box"));
        var changes = new List<EnvironmentRegistryChange>();
        registry.Changed += (_, e) => changes.Add(e.Change);

        Assert.True(await registry.DisconnectAsync("dev-box"));
        Assert.Null(registry.TryGetInstance("dev-box"));
        Assert.Equal(EnvironmentState.Closed, first.State);
        // 配置和默认环境不动,也不广播「配置变了」:空闲回收只是丢掉连接
        Assert.Equal(["local", "dev-box"], registry.List().Select(d => d.Id));
        Assert.Empty(changes);
        var second = registry.Get("dev-box");
        Assert.NotSame(first, second);
        Assert.Equal(EnvironmentState.Idle, second.State);

        // local 没有连接可断;不存在的 id 也不抛
        Assert.False(await registry.DisconnectAsync("local"));
        Assert.Same(registry.Get("local"), registry.Get("local"));
        Assert.False(await registry.DisconnectAsync("nope"));
    }

    [Fact]
    public async Task Disconnecting_after_the_registry_is_disposed_does_nothing()
    {
        var registry = NewRegistry();
        registry.AddSsh("dev-box", "Dev box", Profile());
        _ = registry.Get("dev-box");
        await registry.DisposeAsync();
        Assert.False(await registry.DisconnectAsync("dev-box"));
    }

    [Fact]
    public async Task Ssh_environments_are_added_listed_and_created_lazily_once()
    {
        await using var registry = NewRegistry();
        var changes = new List<(EnvironmentRegistryChange, string)>();
        registry.Changed += (_, e) => changes.Add((e.Change, e.Id));

        var added = registry.AddSsh("dev-box", "Dev box", Profile());
        Assert.Equal(EnvironmentKind.Ssh, added.Kind);
        Assert.Equal(["local", "dev-box"], registry.List().Select(d => d.Id));
        Assert.Equal([(EnvironmentRegistryChange.Added, "dev-box")], changes);

        var first = registry.Get("dev-box");
        Assert.IsType<SshEnvironment>(first);
        Assert.Equal(new EnvironmentId("dev-1", "dev-box"), first.Id);
        Assert.Equal("Dev box", first.DisplayName);
        Assert.Equal(EnvironmentState.Idle, first.State);
        Assert.Same(first, registry.Get("dev-box"));
    }

    [Fact]
    public async Task Ids_are_validated_and_unique()
    {
        await using var registry = NewRegistry();
        registry.AddSsh("a", "A", Profile());
        Assert.Equal(EnvironmentErrorCode.Conflict, Assert.Throws<EnvironmentException>(() => registry.AddSsh("a", "again", Profile())).Code);
        foreach (var bad in new[] { "", "local", "has space", "slash/x", new string('x', 200) })
        {
            Assert.Equal(EnvironmentErrorCode.InvalidProfile, Assert.Throws<EnvironmentException>(() => registry.AddSsh(bad, "x", Profile())).Code);
        }
    }

    [Fact]
    public async Task Ssh_environments_need_ssh_services()
    {
        await using var registry = new EnvironmentRegistry("dev");
        Assert.Equal(EnvironmentErrorCode.Unsupported, Assert.Throws<EnvironmentException>(() => registry.AddSsh("a", "A", Profile())).Code);
    }

    [Fact]
    public async Task Unknown_environments_are_reported()
    {
        await using var registry = NewRegistry();
        Assert.Equal(EnvironmentErrorCode.EnvironmentNotFound, Assert.Throws<EnvironmentException>(() => registry.Get("nope")).Code);
        Assert.Equal(EnvironmentErrorCode.EnvironmentNotFound, Assert.Throws<EnvironmentException>(() => registry.SetDefault("nope")).Code);
        await Assert.ThrowsAsync<EnvironmentException>(async () => await registry.RemoveAsync("nope"));
        await Assert.ThrowsAsync<EnvironmentException>(async () => await registry.UpdateSshAsync("nope", "x", Profile()));
        Assert.Null(registry.Find("nope"));
    }

    [Fact]
    public async Task The_default_environment_can_be_changed_and_falls_back_when_removed()
    {
        await using var registry = NewRegistry();
        var changes = new List<EnvironmentRegistryChange>();
        registry.Changed += (_, e) => changes.Add(e.Change);
        registry.AddSsh("dev", "Dev", Profile());
        registry.SetDefault("dev");
        Assert.Equal("dev", registry.DefaultId);
        Assert.Equal(new EnvironmentId("dev-1", "dev"), registry.Get().Id);
        registry.SetDefault("dev");

        var instance = registry.Get("dev");
        await registry.RemoveAsync("dev");
        Assert.Equal("local", registry.DefaultId);
        Assert.Equal(EnvironmentState.Closed, instance.State);
        Assert.Equal([EnvironmentRegistryChange.Added, EnvironmentRegistryChange.DefaultChanged, EnvironmentRegistryChange.Removed, EnvironmentRegistryChange.DefaultChanged], changes);
        Assert.Equal(["local"], registry.List().Select(d => d.Id));
    }

    [Fact]
    public async Task The_local_environment_cannot_be_removed()
    {
        await using var registry = NewRegistry();
        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await registry.RemoveAsync("local"));
        Assert.Equal(EnvironmentErrorCode.Unsupported, error.Code);
    }

    [Fact]
    public async Task Updating_an_environment_replaces_its_instance_so_the_new_profile_takes_effect()
    {
        await using var registry = NewRegistry();
        registry.AddSsh("dev", "Old", Profile("Old"));
        var old = registry.Get("dev");
        var updated = await registry.UpdateSshAsync("dev", "New", Profile("New"));
        Assert.Equal("New", updated.Name);
        Assert.Equal(EnvironmentState.Closed, old.State);
        var fresh = registry.Get("dev");
        Assert.NotSame(old, fresh);
        Assert.Equal("New", fresh.DisplayName);
        // local 不是 SSH,不能当 SSH 更新
        await Assert.ThrowsAsync<EnvironmentException>(async () => await registry.UpdateSshAsync("local", "x", Profile()));
    }

    [Fact]
    public async Task Snapshot_and_load_round_trip_for_persistence()
    {
        await using var first = NewRegistry();
        first.AddSsh("a", "A", Profile("A"));
        first.AddSsh("b", "B", Profile("B"));
        first.SetDefault("b");
        var (environments, defaultId) = first.Snapshot();
        Assert.Equal(["a", "b"], environments.Select(e => e.Id));
        Assert.Equal("b", defaultId);

        await using var second = NewRegistry();
        second.Load([.. environments, environments[0], new EnvironmentDescriptor("bad id", "x", EnvironmentKind.Ssh, Profile()), new EnvironmentDescriptor("local", "x", EnvironmentKind.Local)], defaultId);
        Assert.Equal(["local", "a", "b"], second.List().Select(d => d.Id));
        Assert.Equal("b", second.DefaultId);

        await using var third = NewRegistry();
        third.Load(environments, "gone");
        Assert.Equal("local", third.DefaultId);
    }

    [Fact]
    public async Task Status_changes_of_instances_are_forwarded()
    {
        await using var registry = NewRegistry();
        registry.AddSsh("dead", "Dead", Profile());
        var states = new List<(string, EnvironmentState)>();
        registry.EnvironmentStatusChanged += (_, e) =>
        {
            lock (states)
            {
                states.Add((e.Id.Environment, e.State));
            }
        };
        var environment = registry.Get("dead");
        await Assert.ThrowsAsync<EnvironmentException>(async () => await environment.EnsureReadyAsync());
        lock (states)
        {
            Assert.Equal([("dead", EnvironmentState.Connecting), ("dead", EnvironmentState.Failed)], states);
        }
    }

    [Fact]
    public async Task Disposing_the_registry_closes_the_instances_and_refuses_further_use()
    {
        var registry = NewRegistry();
        registry.AddSsh("a", "A", Profile());
        var instance = registry.Get("a");
        await registry.DisposeAsync();
        Assert.Equal(EnvironmentState.Closed, instance.State);
        Assert.Throws<ObjectDisposedException>(() => registry.Get("a"));
        await registry.DisposeAsync();
    }

    [Fact]
    public async Task A_custom_factory_can_provide_the_instances()
    {
        var created = new List<string>();
        await using var registry = new EnvironmentRegistry("dev", new EnvironmentRegistryOptions
        {
            SshFactory = (id, descriptor) =>
            {
                created.Add(descriptor.Id);
                return new LocalEnvironment(id.DeviceId);
            },
        });
        registry.AddSsh("x", "X", Profile());
        registry.Get("x");
        registry.Get("x");
        Assert.Equal(["x"], created);
    }
}
