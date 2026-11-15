using System.Diagnostics;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Tools;

namespace Ncw.Agent.Tools.Tests;

/// <summary>
/// <c>Bash</c> 的真进程测试(移植自 2.x <c>bash.test.ts</c> / <c>bash-background.test.ts</c>;2.x 里测试打桩 SpawnFn,3.0 里经 <c>LocalEnvironment</c> 真跑)。
/// 这些测试用的是 POSIX shell 语法,Windows 上跳过(那里的 shell 方言由环境层的单测覆盖)。
/// </summary>
public sealed class BashToolTests : IDisposable
{
    private readonly Fixture _fx = new(shells: true);
    private readonly BashTool _bash = new();
    private readonly BashOutputTool _output = new();
    private readonly KillShellTool _kill = new();

    public void Dispose() => _fx.Dispose();

    private static bool Skip => OperatingSystem.IsWindows();

    private Task<ToolResult> BashAsync(string command, int? timeout = null, bool? background = null, string description = "test command", ToolRuntime? runtime = null)
    {
        var args = new List<(string, object?)> { ("command", command), ("description", description) };
        if (timeout is { } t)
        {
            args.Add(("timeout", t));
        }

        if (background is { } b)
        {
            args.Add(("run_in_background", b));
        }

        return _fx.Run(_bash, Fixture.Args([.. args]), runtime: runtime);
    }

    private ToolRuntime NoShells() => new() { Environment = _fx.Env, Root = _fx.Runtime.Root };

    // ── 前置条件 ──

    [Fact]
    public async Task Without_a_workspace_it_refuses_and_starts_no_process()
    {
        if (Skip) return;
        using var fx = new Fixture(withRoot: false);
        var r = await fx.Run(_bash, ("command", "touch started"), ("description", "x"));
        Assert.True(r.IsError);
        Assert.Contains("workspace", r.Text());
        Assert.False(File.Exists("started"));
    }

    [Fact]
    public async Task An_empty_command_and_an_over_long_timeout_are_rejected_by_the_schema()
    {
        var empty = await _fx.Run(_bash, ("command", ""), ("description", "x"));
        Assert.True(empty.IsError);
        Assert.Contains("Invalid arguments", empty.Text());
        var timeout = await BashAsync("echo hi", timeout: ShellLimits.MaxTimeoutMs + 1);
        Assert.True(timeout.IsError);
        Assert.Contains("Invalid arguments", timeout.Text());
    }

    /// <summary>description 在描述里写成必填,但缺席不能让命令失败:用户看到的会是「我的命令怎么跑不了了」,而他什么都没做错。</summary>
    [Fact]
    public async Task A_missing_description_defaults_to_empty_instead_of_rejecting_the_command()
    {
        if (Skip) return;
        var r = await _fx.Run(_bash, ("command", "echo hi"));
        Assert.False(r.IsError, r.Text());
        Assert.Contains("hi", r.Text());
    }

    // ── 真的跑一条命令 ──

    [Fact]
    public async Task Gets_stdout_in_a_tagged_section()
    {
        if (Skip) return;
        var r = await BashAsync("echo hello-from-bash");
        Assert.False(r.IsError);
        Assert.Contains("hello-from-bash", r.Text());
        Assert.Contains("<stdout>", r.Text());
    }

    [Fact]
    public async Task The_working_directory_is_the_workspace_root()
    {
        if (Skip) return;
        var r = await BashAsync("pwd");
        Assert.Contains(_fx.Root, r.Text());
    }

    [Fact]
    public async Task Can_write_files_proving_it_really_runs()
    {
        if (Skip) return;
        var r = await BashAsync("printf abc > written.txt");
        Assert.False(r.IsError, r.Text());
        Assert.Equal("abc", _fx.Text("written.txt"));
    }

    [Fact]
    public async Task A_non_zero_exit_is_an_error_with_both_streams()
    {
        if (Skip) return;
        var r = await BashAsync("echo out; echo err >&2; exit 3");
        Assert.True(r.IsError);
        Assert.Contains("exited with code 3", r.Text());
        // 失败时 stdout 和 stderr 都要给 —— 只给 stderr 的话模型看不到它做到哪一步了
        Assert.Contains("out", r.Text());
        Assert.Contains("err", r.Text());
        Assert.Equal(3, r.DetailsOrThrow().GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task Success_without_output_says_so_instead_of_returning_silence()
    {
        if (Skip) return;
        var r = await BashAsync("true");
        Assert.False(r.IsError);
        Assert.Contains("no output", r.Text());
        var failed = await BashAsync("exit 7");
        Assert.True(failed.IsError);
        Assert.Contains("exited with code 7", failed.Text());
        Assert.Contains("no output", failed.Text());
    }

    [Fact]
    public async Task Stdout_and_stderr_are_kept_apart()
    {
        if (Skip) return;
        var c = (await BashAsync("echo AAA; echo BBB >&2")).Text();
        Assert.True(c.IndexOf("<stdout>", StringComparison.Ordinal) < c.IndexOf("<stderr>", StringComparison.Ordinal));
        Assert.Contains("AAA", c[c.IndexOf("<stdout>", StringComparison.Ordinal)..c.IndexOf("</stdout>", StringComparison.Ordinal)]);
        Assert.Contains("BBB", c[c.IndexOf("<stderr>", StringComparison.Ordinal)..c.IndexOf("</stderr>", StringComparison.Ordinal)]);
    }

    [Fact]
    public async Task Every_call_gets_a_fresh_shell()
    {
        if (Skip) return;
        await BashAsync("export NCW_TEST_VAR=1; cd /");
        var r = await BashAsync("echo \"[$NCW_TEST_VAR]\"; pwd");
        Assert.Contains("[]", r.Text());
        Assert.Contains(_fx.Root, r.Text());
    }

    /// <summary>stdin 是关着的:等输入的命令得到 EOF 而不是挂到超时。</summary>
    [Fact]
    public async Task Stdin_is_closed_so_a_command_that_reads_input_returns_at_once()
    {
        if (Skip) return;
        var started = Stopwatch.StartNew();
        var r = await BashAsync("cat; echo done", timeout: 20_000);
        Assert.False(r.IsError, r.Text());
        Assert.Contains("done", r.Text());
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Strips_ANSI_escape_sequences()
    {
        if (Skip) return;
        var r = await BashAsync("printf '\\033[31mred\\033[0m \\033]0;title\\007ok'");
        Assert.Contains("red ok", r.Text());
        Assert.DoesNotContain('\u001B', r.Text());
    }

    // ── 超时 ──

    [Fact]
    public async Task A_command_that_exceeds_its_timeout_is_killed_and_reported_with_the_budget()
    {
        if (Skip) return;
        var started = Stopwatch.StartNew();
        var r = await BashAsync("echo before; sleep 30", timeout: 300);
        Assert.True(r.IsError);
        Assert.Contains("timed out after 300ms", r.Text());
        Assert.Contains("process tree", r.Text());
        Assert.Contains("before", r.Text()); // 超时前已经读到的输出照常给模型
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal("timedOut", r.DetailsOrThrow().GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_timeout_kills_the_whole_process_tree_not_just_the_shell()
    {
        if (Skip) return;
        var r = await BashAsync("(echo $$ > child.pid; exec sleep 60) & wait", timeout: 300);
        Assert.True(r.IsError);
        var pid = int.Parse(_fx.Text("child.pid").Trim());
        await Wait.UntilAsync(() => !Wait.ProcessAlive(pid), what: "the grandchild to die");
    }

    // ── 输出预算:保留尾部,完整输出存临时文件 ──

    private (ToolRuntime Runtime, string TempDir) WithTemp()
    {
        var temp = Path.Combine(_fx.Base, "tmp");
        Directory.CreateDirectory(temp);
        return (new ToolRuntime { Environment = _fx.Env, Root = _fx.Runtime.Root, TempDirectory = EnvPath.Parse(temp, Fixture.Style), Shells = _fx.Shells }, temp);
    }

    [Fact]
    public async Task Output_over_the_budget_keeps_the_tail_and_saves_the_full_output_to_a_file()
    {
        if (Skip) return;
        var (runtime, temp) = WithTemp();
        var r = await BashAsync("printf START; head -c 200000 /dev/zero | tr '\\0' x; printf END", runtime: runtime);
        var text = r.Text();
        Assert.False(r.IsError);
        Assert.True(r.Truncated);
        Assert.True(text.Length < ShellLimits.MaxOutputChars + 800, $"{text.Length} chars");
        Assert.EndsWith("END\n</stdout>", text);   // 尾部保留
        Assert.DoesNotContain("START", text);       // 开头被丢了
        Assert.Contains("showing the last", text);

        var file = r.DetailsOrThrow().GetProperty("outputFile").GetString()!;
        Assert.StartsWith(temp, file);
        var full = File.ReadAllText(file);
        Assert.Equal(200_000 + "START".Length + "END".Length, full.Length);
        Assert.StartsWith("START", full);
        Assert.Contains(file, text); // 路径在给模型的结果里
        Assert.Equal(200_000 + 8, r.OriginalBytes);
    }

    [Fact]
    public async Task Small_output_never_leaves_a_temporary_file_behind()
    {
        if (Skip) return;
        var (runtime, temp) = WithTemp();
        await BashAsync("echo small", runtime: runtime);
        await BashAsync("head -c 50000 /dev/zero | tr '\\0' y | tr 'y' 'z' | head -c 20000", runtime: runtime); // 超过落盘阈值但没有被截断
        Assert.Empty(Directory.GetFiles(temp));
    }

    [Fact]
    public async Task A_huge_stderr_is_truncated_independently_of_stdout()
    {
        if (Skip) return;
        var (runtime, _) = WithTemp();
        var r = await BashAsync("echo ok-out; head -c 100000 /dev/zero | tr '\\0' e >&2", runtime: runtime);
        var text = r.Text();
        Assert.Contains("ok-out", text);
        Assert.Contains("stderr truncated", text);
        Assert.DoesNotContain("stdout truncated", text);
    }

    [Fact]
    public async Task Truncated_output_still_fits_under_the_loop_s_64K_limit_even_for_both_streams()
    {
        if (Skip) return;
        var (runtime, _) = WithTemp();
        var r = await BashAsync("head -c 300000 /dev/zero | tr '\\0' o; head -c 300000 /dev/zero | tr '\\0' e >&2", runtime: runtime);
        Assert.True(r.Text().Length < 64 * 1024, $"{r.Text().Length}");
    }

    [Fact]
    public async Task Progress_shows_the_description_or_a_clipped_command_and_flags_detachable_commands()
    {
        if (Skip) return;
        await BashAsync("echo hi", description: "say hi");
        Assert.Equal("say hi", _fx.Progress[0].Message);
        Assert.True(_fx.Progress[0].Details!.Value.GetProperty("detachable").GetBoolean());
        _fx.Progress.Clear();
        await _fx.Run(_bash, ("command", "echo hi"), ("description", ""));
        Assert.Equal("echo hi", _fx.Progress[0].Message);
        _fx.Progress.Clear();
        await BashAsync("echo hi", runtime: NoShells());
        Assert.Null(_fx.Progress[0].Details); // 没有注册表:不告诉界面能转后台
    }

    [Fact]
    public async Task Details_carry_the_command_and_the_environment()
    {
        if (Skip) return;
        var d = (await BashAsync("echo hi", description: "say hi")).DetailsOrThrow();
        Assert.Equal("bash", d.GetProperty("kind").GetString());
        Assert.Equal("echo hi", d.GetProperty("command").GetString());
        Assert.Equal("say hi", d.GetProperty("description").GetString());
        Assert.Equal("dev-1", d.GetProperty("environment").GetProperty("deviceId").GetString());
        Assert.Equal("local", d.GetProperty("environment").GetProperty("environment").GetString());
    }

    // ── 中止 ──

    /// <summary>整轮中止必须原样抛出,不能变成一个普通的工具失败:否则模型会继续回合并换个写法重试,停止按钮就失效了。</summary>
    [Fact]
    public async Task A_run_abort_throws_and_kills_the_process_it_does_not_become_a_tool_failure()
    {
        if (Skip) return;
        using var cts = new CancellationTokenSource();
        var pending = _fx.Run(_bash, Fixture.Args(("command", "echo $$ > pid.txt; exec sleep 60"), ("description", "x")), ct: cts.Token);
        await Wait.UntilAsync(() => File.Exists(_fx.Abs("pid.txt")) && new FileInfo(_fx.Abs("pid.txt")).Length > 0);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var pid = int.Parse(_fx.Text("pid.txt").Trim());
        await Wait.UntilAsync(() => !Wait.ProcessAlive(pid), what: "the aborted process to die");
    }

    [Fact]
    public async Task A_token_that_is_already_cancelled_starts_nothing()
    {
        if (Skip) return;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _fx.Run(_bash, Fixture.Args(("command", "touch started"), ("description", "x")), ct: cts.Token));
        Assert.False(File.Exists(_fx.Abs("started")));
    }

    // ── 单条停止 ──

    /// <summary>用户停这一条 = 工具失败(带「不要重试」),不是抛出:抛出会把整轮回复也停掉。</summary>
    [Fact]
    public async Task Stopping_a_single_command_is_a_tool_failure_not_a_throw()
    {
        if (Skip) return;
        var pending = BashAsync("sleep 60");
        await Wait.UntilAsync(() => _fx.Shells!.StopCall(new RunId("run-1"), "call-1"), what: "the call to be registered");
        var r = await pending;
        Assert.True(r.IsError);
        Assert.Contains("user stopped", r.Text());
        Assert.Contains("do not run it again", r.Text());
        Assert.Equal("stopped", r.DetailsOrThrow().GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task The_stop_handle_is_released_when_the_command_finishes()
    {
        if (Skip) return;
        await BashAsync("true");
        Assert.False(_fx.Shells!.StopCall(new RunId("run-1"), "call-1"));
        Assert.False(_fx.Shells.DetachCall(new RunId("run-1"), "call-1"));
    }

    // ── 后台执行 ──

    [Fact]
    public async Task Background_without_a_registry_says_so_and_does_not_degrade_to_the_foreground()
    {
        if (Skip) return;
        var r = await BashAsync("touch started", background: true, runtime: NoShells());
        Assert.True(r.IsError);
        Assert.Contains("unavailable", r.Text());
        // 降级的话模型会以为服务已经起在后台,然后去调一个永远读不到东西的 BashOutput
        Assert.False(File.Exists(_fx.Abs("started")));
    }

    [Fact]
    public async Task Background_returns_a_shell_id_and_names_the_next_steps()
    {
        if (Skip) return;
        var r = await BashAsync("echo up; sleep 60", background: true, description: "start the thing");
        Assert.False(r.IsError, r.Text());
        Assert.Contains("bash_1", r.Text());
        Assert.Contains("BashOutput", r.Text());
        Assert.Contains("KillShell", r.Text());
        Assert.Equal("start the thing", _fx.Shells!.List().Single().Description);
        await _kill.ExecuteAsync(new ToolInvocation("c", "KillShell", _kill, Fixture.Args(("shell_id", "bash_1"))), Ctx(), CancellationToken.None);
    }

    private ExecutionToolContext Ctx() =>
        new(new ToolContext(new RunId("run-1"), "call-9", "s", 0, null, new Progress<ToolProgress>(), new FileMutationQueue()), _fx.Runtime);

    [Fact]
    public async Task Background_start_failures_are_forwarded_to_the_model()
    {
        if (Skip) return;
        for (var i = 0; i < ShellLimits.MaxRunning; i++)
        {
            Assert.False((await BashAsync("sleep 60", background: true)).IsError);
        }

        var r = await BashAsync("sleep 60", background: true);
        Assert.True(r.IsError);
        Assert.Contains("Too many background shells", r.Text());
    }

    [Fact]
    public async Task BashOutput_reads_only_what_is_new_and_KillShell_stops_the_whole_process_tree()
    {
        if (Skip) return;
        await BashAsync("echo first; sleep 1; echo second; (echo $$ > bg.pid; exec sleep 60) & wait", background: true);
        await Wait.UntilAsync(() => File.Exists(_fx.Abs("bg.pid")) && new FileInfo(_fx.Abs("bg.pid")).Length > 0);
        var first = await _fx.Run(_output, ("bash_id", "bash_1"));
        Assert.Contains("first", first.Text());
        Assert.Contains("is still running", first.Text());
        await Wait.UntilAsync(() => _fx.Shells!.Peek("bash_1")!.Value.Output.Contains("second", StringComparison.Ordinal));
        var second = await _fx.Run(_output, ("bash_id", "bash_1"));
        Assert.DoesNotContain("<stdout>\nfirst", second.Text());
        Assert.Contains("second", second.Text());

        var killed = await _fx.Run(_kill, ("shell_id", "bash_1"));
        Assert.Contains("Killed shell bash_1", killed.Text());
        var pid = int.Parse(_fx.Text("bg.pid").Trim());
        await Wait.UntilAsync(() => !Wait.ProcessAlive(pid), what: "the background grandchild to die");
        Assert.Contains("was killed", (await _fx.Run(_output, ("bash_id", "bash_1"))).Text());
    }

    [Fact]
    public async Task BashOutput_reports_the_exit_code_of_a_finished_shell()
    {
        if (Skip) return;
        await BashAsync("echo done; exit 4", background: true);
        await Wait.UntilAsync(() => _fx.Shells!.List().Single().Status != BackgroundShellStatus.Running);
        var r = await _fx.Run(_output, ("bash_id", "bash_1"));
        Assert.Contains("exited with code 4", r.Text());
        Assert.Contains("done", r.Text());
        Assert.Equal(4, r.DetailsOrThrow().GetProperty("exitCode").GetInt32());
    }

    // ── 前台转后台 ──

    [Fact]
    public async Task Moving_a_running_command_to_the_background_adopts_it_with_the_output_so_far()
    {
        if (Skip) return;
        // 42 / 55 只会出现在输出里(命令原文里写的是算式),所以能数「每一行恰好给过模型一次」
        var pending = BashAsync("echo $((40+2)); sleep 1; echo $((50+5)); sleep 60", description: "build");
        await Wait.UntilAsync(() => _fx.Shells!.DetachCall(new RunId("run-1"), "call-1"), what: "the call to be registered");
        var r = await pending;
        Assert.False(r.IsError, r.Text());
        Assert.Contains("bash_1", r.Text());
        Assert.Contains("still running", r.Text());
        Assert.Equal("detached", r.DetailsOrThrow().GetProperty("outcome").GetString());
        Assert.Equal("build", _fx.Shells!.List().Single().Description);

        // 之后的输出由 BashOutput 读,已经给过模型的那一段不会再来一遍
        await Wait.UntilAsync(() => _fx.Shells!.Peek("bash_1")!.Value.Output.Contains("55", StringComparison.Ordinal), what: "the command to print more");
        var next = (await _fx.Run(_output, ("bash_id", "bash_1"))).Text();
        var all = r.Text() + "\n" + next;
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(all, "42"));
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(all, "55"));
        await _fx.Shells!.KillAsync("bash_1", CancellationToken.None);
    }

    [Fact]
    public async Task A_full_background_table_refuses_the_move_and_the_foreground_command_keeps_running()
    {
        if (Skip) return;
        for (var i = 0; i < ShellLimits.MaxRunning; i++)
        {
            await BashAsync("sleep 60", background: true);
        }

        var pending = BashAsync("sleep 1; echo finished");
        await Wait.UntilAsync(() => _fx.Progress.Count > ShellLimits.MaxRunning);
        await Task.Delay(100);
        Assert.False(_fx.Shells!.DetachCall(new RunId("run-1"), "call-1"));
        var r = await pending;
        Assert.False(r.IsError, r.Text());
        Assert.Contains("finished", r.Text());
    }
}
