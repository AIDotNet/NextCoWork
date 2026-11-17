using System.Diagnostics;
using System.Text;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Environments.Tests;

/// <summary>进程契约:本机和 SSH 必须表现一致(输出、退出码、环境变量、stdin、超时、取消、进程树终止),同一份测试跑两遍。</summary>
public abstract class ProcessContractTests : IAsyncLifetime
{
    protected IExecutionEnvironment Env { get; private set; } = null!;

    protected abstract Task<IExecutionEnvironment> CreateAsync();

    public async ValueTask InitializeAsync()
    {
        Env = await CreateAsync();
        await Env.EnsureReadyAsync();
    }

    public async ValueTask DisposeAsync() => await Env.DisposeAsync();

    private static void RequirePosix() => Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX shell semantics");

    private static CommandSpec Sh(string script) => CommandSpec.Exec("/bin/sh", "-c", script);

    [Fact]
    public async Task Exec_runs_a_program_with_arguments_and_returns_stdout()
    {
        RequirePosix();
        var result = await Env.RunAsync(CommandSpec.Exec("/bin/echo", "hello", "wor ld"));
        Assert.True(result.Succeeded);
        Assert.Equal("hello wor ld\n", result.Stdout.ToText());
        Assert.Equal("", result.Stderr.ToText());
    }

    [Fact]
    public async Task Shell_form_uses_the_environments_shell()
    {
        RequirePosix();
        var result = await Env.RunAsync(CommandSpec.Shell("echo $((1+2)); echo err >&2"));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("3\n", result.Stdout.ToText());
        Assert.Equal("err\n", result.Stderr.ToText());
    }

    [Fact]
    public async Task Exit_codes_are_reported()
    {
        RequirePosix();
        var result = await Env.RunAsync(Sh("exit 7"));
        Assert.Equal(ProcessOutcome.Exited, result.Outcome);
        Assert.Equal(7, result.ExitCode);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Working_directory_and_environment_are_applied()
    {
        RequirePosix();
        using var temp = new TempDir();
        var env = Env;
        var spec = Sh("pwd; printf %s \"$FOO\"") with
        {
            WorkingDirectory = temp.Env,
            Environment = new Dictionary<string, string> { ["FOO"] = "a b'c\"d" },
        };
        var result = await env.RunAsync(spec);
        var lines = result.Stdout.ToText().Split('\n');
        Assert.Equal((await env.Fs.RealPathAsync(temp.Env)).Value, (await env.Fs.RealPathAsync(LocalEnvironment.PathOf(lines[0]))).Value);
        Assert.Equal("a b'c\"d", lines[1]);
    }

    [Fact]
    public async Task Stdin_is_closed_by_default_so_waiting_commands_do_not_hang()
    {
        RequirePosix();
        var result = await Env.RunAsync(CommandSpec.Exec("/bin/cat"), new RunOptions { Timeout = TimeSpan.FromSeconds(10) });
        Assert.Equal(ProcessOutcome.Exited, result.Outcome);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Input_is_written_to_stdin_and_closed()
    {
        RequirePosix();
        var result = await Env.RunAsync(CommandSpec.Exec("/bin/cat"), new RunOptions { Input = Encoding.UTF8.GetBytes("ping\n你好") });
        Assert.Equal("ping\n你好", result.Stdout.ToText());
    }

    [Fact]
    public async Task Pipe_mode_gives_the_caller_a_live_stdin()
    {
        RequirePosix();
        await using var process = await Env.OpenProcessAsync(CommandSpec.Exec("/bin/cat") with { Stdin = StdinMode.Pipe });
        Assert.NotNull(process.Stdin);
        await process.Stdin!.WriteAsync("line\n"u8.ToArray());
        await process.Stdin.FlushAsync();
        var buffer = new byte[16];
        var read = await process.Stdout.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("line\n", Encoding.UTF8.GetString(buffer, 0, read));
        process.Stdin.Close();
        var exit = await process.Exited.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, exit.ExitCode);
        Assert.Equal(ProcessExitReason.Exited, exit.Reason);
    }

    [Fact]
    public async Task Output_is_streamed_while_the_process_runs()
    {
        RequirePosix();
        var seen = new List<string>();
        var firstChunk = new TaskCompletionSource();
        var run = Env.RunAsync(Sh("echo first; sleep 1; echo second"), new RunOptions
        {
            OnOutput = (kind, data) =>
            {
                lock (seen)
                {
                    seen.Add($"{kind}:{Encoding.UTF8.GetString(data)}");
                }

                firstChunk.TrySetResult();
            },
        });
        await firstChunk.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // 第一块输出到达时,命令还没结束
        Assert.False(run.IsCompleted);
        var result = await run;
        Assert.Equal("first\nsecond\n", result.Stdout.ToText());
        Assert.Contains(seen, s => s.StartsWith("Stdout:first", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Timeout_kills_the_command_and_keeps_the_output_so_far()
    {
        RequirePosix();
        var watch = Stopwatch.StartNew();
        var result = await Env.RunAsync(Sh("echo started; sleep 300"), new RunOptions { Timeout = TimeSpan.FromMilliseconds(600) });
        Assert.Equal(ProcessOutcome.TimedOut, result.Outcome);
        Assert.Contains("started", result.Stdout.ToText());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Cancellation_kills_the_command_and_returns_instead_of_throwing()
    {
        RequirePosix();
        using var cts = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        var run = Env.RunAsync(Sh("echo started; sleep 300"), ct: cts.Token);
        await Task.Delay(400);
        cts.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ProcessOutcome.Cancelled, result.Outcome);
        Assert.Contains("started", result.Stdout.ToText());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task A_token_cancelled_before_start_throws_without_starting_anything()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Env.RunAsync(CommandSpec.Shell("echo"), ct: cts.Token));
    }

    [Fact]
    public async Task Large_output_is_consumed_fully_and_truncated_by_policy()
    {
        RequirePosix();
        // 20 MB 输出:管道必须一直被读,否则子进程卡在 write 上
        var result = await Env.RunAsync(
            Sh("head -c 20000000 /dev/zero | tr '\\0' 'x'; echo; echo END"),
            new RunOptions { OutputPolicy = OutputPolicies.Tail(1000), Timeout = TimeSpan.FromSeconds(60) });
        Assert.Equal(ProcessOutcome.Exited, result.Outcome);
        Assert.Equal(20_000_005, result.Stdout.TotalBytes);
        Assert.Equal(1000, result.Stdout.Tail.Length);
        Assert.True(result.Stdout.Truncated);
        Assert.EndsWith("xxxx\nEND\n", result.Stdout.ToText());
    }

    [Fact]
    public async Task Streaming_callback_sees_every_byte_even_when_the_buffer_truncates()
    {
        RequirePosix();
        long streamed = 0;
        var result = await Env.RunAsync(
            Sh("head -c 3000000 /dev/zero"),
            new RunOptions { OutputPolicy = OutputPolicies.Head(10), OnOutput = (_, data) => Interlocked.Add(ref streamed, data.Length) });
        Assert.Equal(3_000_000, Interlocked.Read(ref streamed));
        Assert.Equal(10, result.Stdout.Head.Length);
        Assert.Equal(3_000_000, result.Stdout.TotalBytes);
    }

    [Fact]
    public async Task Killing_a_process_terminates_its_whole_tree()
    {
        RequirePosix();
        using var temp = new TempDir();
        var script = $"sh -c 'sleep 300 & echo $! > \"{temp.File("inner")}\"; wait' & echo $! > \"{temp.File("middle")}\"; sleep 300 & echo $! > \"{temp.File("leaf")}\"; wait";
        var run = Env.RunAsync(Sh(script), new RunOptions { Timeout = TimeSpan.FromSeconds(2) });
        var result = await run;
        Assert.Equal(ProcessOutcome.TimedOut, result.Outcome);

        var pids = new[] { "inner", "middle", "leaf" }.Select(name => int.Parse(File.ReadAllText(temp.File(name)).Trim())).ToArray();
        await Wait.UntilAsync(() => pids.All(pid => !Wait.ProcessAlive(pid)), 5000, "the whole process tree to die");
    }

    [Fact]
    public async Task Processes_that_ignore_sigterm_are_killed_after_the_grace_period()
    {
        RequirePosix();
        using var temp = new TempDir();
        // trap '' TERM 被子进程继承:整棵树都无视 SIGTERM,只能靠 SIGKILL 升级
        var script = $"trap '' TERM; sleep 300 & echo $! > \"{temp.File("pid")}\"; wait";
        var watch = Stopwatch.StartNew();
        var result = await Env
            .RunAsync(Sh(script), new RunOptions { Timeout = TimeSpan.FromMilliseconds(800) });
        Assert.Equal(ProcessOutcome.TimedOut, result.Outcome);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        var pid = int.Parse(File.ReadAllText(temp.File("pid")).Trim());
        await Wait.UntilAsync(() => !Wait.ProcessAlive(pid), 5000, "the SIGTERM-ignoring child to die");
    }

    [Fact]
    public async Task Background_children_holding_the_pipe_do_not_hang_the_caller()
    {
        RequirePosix();
        using var temp = new TempDir();
        var watch = Stopwatch.StartNew();
        var result = await Env.RunAsync(
            Sh($"sleep 30 & echo $! > \"{temp.File("pid")}\"; echo done"),
            new RunOptions { DrainGrace = TimeSpan.FromMilliseconds(300), Timeout = TimeSpan.FromSeconds(20) });
        try
        {
            Assert.Equal(ProcessOutcome.Exited, result.Outcome);
            Assert.Equal("done\n", result.Stdout.ToText());
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        }
        finally
        {
            // 命令正常退出时不杀后台子进程(那是用户命令自己的行为),测试自己收拾
            if (int.TryParse(File.ReadAllText(temp.File("pid")).Trim(), out var pid) && Wait.ProcessAlive(pid))
            {
                Process.GetProcessById(pid).Kill();
            }
        }
    }

    [Fact]
    public async Task Disposing_a_running_process_kills_it()
    {
        RequirePosix();
        using var temp = new TempDir();
        var process = await Env.OpenProcessAsync(Sh($"echo $$ > \"{temp.File("pid")}\"; sleep 300"));
        await Wait.UntilAsync(() => File.Exists(temp.File("pid")) && File.ReadAllText(temp.File("pid")).EndsWith('\n'), 5000, "pid file");
        var pid = int.Parse(File.ReadAllText(temp.File("pid")).Trim());
        await process.DisposeAsync();
        await Wait.UntilAsync(() => !Wait.ProcessAlive(pid), 5000, "process to die");
    }

    [Fact]
    public async Task Killed_processes_report_reason_and_kill_is_idempotent()
    {
        RequirePosix();
        await using var process = await Env.OpenProcessAsync(Sh("sleep 300"));
        await process.KillAsync();
        var exit = await process.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ProcessExitReason.Killed, exit.Reason);
        await process.KillAsync();
    }

    [Fact]
    public async Task Environment_values_survive_awkward_bytes()
    {
        RequirePosix();
        // 换行、结尾换行、引号、百分号、反斜杠、美元符、中文:都不能被任何一层引号吃掉
        var value = "line1\nline2\n  'q' \"d\" %s %% \\ $HOME `x` 你好\n";
        var spec = Sh("printf %s \"$NCW_AWKWARD\"") with { Environment = new Dictionary<string, string> { ["NCW_AWKWARD"] = value } };
        var result = await Env.RunAsync(spec);
        Assert.Equal(value, result.Stdout.ToText());
    }

    [Fact]
    public async Task Environment_values_stay_off_the_command_line()
    {
        RequirePosix();
        // 进程自己的命令行里看不到值(SSH 靠 stdin 传,本机靠进程环境)
        var spec = CommandSpec.Exec("/bin/sh", "-c", "ps -o command= -p $$ | grep -c ncw-secret-v''alue; true") with
        {
            Environment = new Dictionary<string, string> { ["NCW_SECRET"] = "ncw-secret-value" },
        };
        var result = await Env.RunAsync(spec);
        Assert.Equal("0", result.Stdout.ToText().Trim());
    }

    [Fact]
    public async Task Stdin_still_works_when_environment_values_are_also_sent()
    {
        RequirePosix();
        var spec = CommandSpec.Exec("/bin/sh", "-c", "printf %s \"$NCW_X|\"; cat") with { Environment = new Dictionary<string, string> { ["NCW_X"] = "v" } };
        var result = await Env.RunAsync(spec, new RunOptions { Input = "from-stdin"u8.ToArray() });
        Assert.Equal("v|from-stdin", result.Stdout.ToText());
    }

    [Fact]
    public async Task Facts_describe_the_machine_and_probe_finds_tools()
    {
        var facts = Env.Facts;
        Assert.Equal(OperatingSystem.IsWindows() ? EnvironmentOs.Windows : OperatingSystem.IsMacOS() ? EnvironmentOs.MacOS : EnvironmentOs.Linux, facts.Os);
        Assert.False(string.IsNullOrEmpty(facts.Home));
        Assert.False(string.IsNullOrEmpty(facts.Shell));
        Assert.Empty(facts.Tools);

        var probed = await Env.ProbeToolsAsync();
        Assert.Same(Env.Facts, probed);
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(probed.HasTool("grep"));
            Assert.True(probed.HasTool("find"));
        }
    }

    [Fact]
    public async Task Tcp_streams_and_local_forwards_reach_a_listener()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var echo = Task.Run(async () =>
            {
                for (var i = 0; i < 2; i++)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    var stream = client.GetStream();
                    var buffer = new byte[16];
                    var read = await stream.ReadAsync(buffer);
                    await stream.WriteAsync(buffer.AsMemory(0, read));
                }
            });

            await using (var stream = await Env.OpenTcpAsync("127.0.0.1", port))
            {
                await stream.WriteAsync("abc"u8.ToArray());
                var response = new byte[3];
                await stream.ReadExactlyAsync(response).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("abc", Encoding.ASCII.GetString(response));
            }

            await using var forward = await Env.OpenLocalForwardAsync("127.0.0.1", port);
            using var client2 = new System.Net.Sockets.TcpClient();
            await client2.ConnectAsync(forward.LocalHost, forward.LocalPort);
            var stream2 = client2.GetStream();
            await stream2.WriteAsync("xyz"u8.ToArray());
            var response2 = new byte[3];
            await stream2.ReadExactlyAsync(response2).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("xyz", Encoding.ASCII.GetString(response2));
            await echo;
        }
        finally
        {
            listener.Stop();
        }
    }
}

public class LocalProcessTests : ProcessContractTests
{
    protected override Task<IExecutionEnvironment> CreateAsync() =>
        Task.FromResult<IExecutionEnvironment>(new LocalEnvironment("dev", new LocalEnvironmentOptions { KillGrace = TimeSpan.FromMilliseconds(500) }));
}

public class SshProcessTests(SshdFixture fixture) : ProcessContractTests, IClassFixture<SshdFixture>
{
    protected override Task<IExecutionEnvironment> CreateAsync() => Task.FromResult<IExecutionEnvironment>(SshTestKit.NewEnv(fixture.Require()));
}

/// <summary>只属于本机实现的行为(启动失败是异常、子进程环境注入、终端需要 PTY 提供者)。</summary>
public class LocalOnlyProcessTests
{
    private static LocalEnvironment NewEnv(LocalEnvironmentOptions? options = null) => new("dev", options ?? new LocalEnvironmentOptions());

    [Fact]
    public async Task A_missing_working_directory_is_an_error_not_a_start_failure_crash()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX path");
        var error = await Assert.ThrowsAsync<EnvironmentException>(async () =>
            await NewEnv().RunAsync(CommandSpec.Exec("/bin/sh", "-c", "true") with { WorkingDirectory = EnvPath.Posix("/definitely/not/here") }));
        Assert.Equal(EnvironmentErrorCode.NotFound, error.Code);
    }

    [Fact]
    public async Task Starting_a_missing_program_fails_with_a_structured_error()
    {
        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await NewEnv().RunAsync(CommandSpec.Exec("definitely-not-a-real-program-xyz")));
        Assert.Equal(EnvironmentErrorCode.ProcessStartFailed, error.Code);
    }

    [Fact]
    public async Task Injected_child_environment_never_overrides_what_the_user_already_set()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX shell");
        var env = NewEnv(new LocalEnvironmentOptions
        {
            ChildEnvironment = () => new Dictionary<string, string> { ["NCW_INJECTED"] = "from-hook", ["PATH"] = "/nonexistent", ["NCW_BOTH"] = "hook" },
        });
        var spec = CommandSpec.Exec("/bin/sh", "-c", "printf '%s|%s|%s' \"$NCW_INJECTED\" \"$NCW_BOTH\" \"$PATH\"") with
        {
            Environment = new Dictionary<string, string> { ["NCW_BOTH"] = "explicit" },
        };
        var parts = (await env.RunAsync(spec)).Stdout.ToText().Split('|');
        Assert.Equal("from-hook", parts[0]);
        Assert.Equal("explicit", parts[1]);
        Assert.NotEqual("/nonexistent", parts[2]);
    }

    [Fact]
    public async Task Opening_a_terminal_without_a_pty_provider_is_unsupported()
    {
        var error = await Assert.ThrowsAsync<EnvironmentException>(async () => await NewEnv().OpenTerminalAsync(new TerminalSpec()));
        Assert.Equal(EnvironmentErrorCode.Unsupported, error.Code);
    }

    [Fact]
    public async Task A_terminal_factory_can_be_injected()
    {
        var called = false;
        var env = NewEnv(new LocalEnvironmentOptions
        {
            TerminalFactory = (spec, facts, ct) =>
            {
                called = true;
                return Task.FromException<ITerminal>(new EnvironmentException(EnvironmentErrorCode.Unsupported, "stub"));
            },
        });
        await Assert.ThrowsAsync<EnvironmentException>(async () => await env.OpenTerminalAsync(new TerminalSpec()));
        Assert.True(called);
    }
}
