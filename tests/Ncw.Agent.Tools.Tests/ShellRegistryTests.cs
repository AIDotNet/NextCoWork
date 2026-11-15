using System.Text.RegularExpressions;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Tools;

namespace Ncw.Agent.Tools.Tests;

/// <summary>
/// 后台 shell 注册表本身的测试(移植自 2.x <c>agent-shells.test.ts</c>),进程是假的(但输出走真实管道)。
/// 这里钉的全是「坏了也不报错」的那几条:读过即清空(否则模型每轮重读同一段日志并以为服务又崩了一次)、丢的是最早的一段且要说出来、
/// 杀过之后状态不被退出回调改回 exited、还在跑的永远不被淘汰、退出时一个不剩。
/// </summary>
public sealed class ShellRegistryTests : IDisposable
{
    private readonly Fixture _fx = new(shells: true);

    public void Dispose() => _fx.Dispose();

    private BackgroundShells Shells => _fx.Shells!;

    private (BackgroundShellInfo Info, FakeProcess Process) Adopt(string command = "npm run dev", string runId = "run-1", string callId = "call-1")
    {
        var process = new FakeProcess();
        var capture = new ShellCapture(process);
        capture.Start();
        var info = Shells.Adopt(new DetachedShell(_fx.Env, process, capture), new ShellStartRequest(command, _fx.Runtime.Root!.Value, null, new RunId(runId), callId));
        return (info, process);
    }

    private async Task ReadyAsync(string id, string contains) =>
        await Wait.UntilAsync(() => Shells.Peek(id)!.Value.Output.Contains(contains, StringComparison.Ordinal), what: $"'{contains}' to arrive");

    // ── 前台停止句柄 ──

    [Fact]
    public void Stop_targets_exactly_the_call_by_run_and_call_id()
    {
        var first = 0;
        var second = 0;
        Shells.Hold(new ForegroundCall(new RunId("run-1"), "call-1", "a"), () => first++, null);
        Shells.Hold(new ForegroundCall(new RunId("run-1"), "call-2", "b"), () => second++, null);
        Assert.True(Shells.StopCall(new RunId("run-1"), "call-1"));
        Assert.Equal((1, 0), (first, second));
        Assert.False(Shells.StopCall(new RunId("run-2"), "call-1")); // 键必须带 runId
    }

    [Fact]
    public void After_release_a_stop_is_false_not_a_stop_of_some_other_command()
    {
        var stops = 0;
        var release = Shells.Hold(new ForegroundCall(new RunId("run-1"), "call-1", "a"), () => stops++, null);
        release.Dispose();
        release.Dispose(); // 幂等
        Assert.False(Shells.StopCall(new RunId("run-1"), "call-1"));
        Assert.Equal(0, stops);
    }

    [Fact]
    public void Only_calls_registered_with_a_detach_handle_can_be_moved_to_the_background()
    {
        var detached = 0;
        Shells.Hold(new ForegroundCall(new RunId("run-1"), "call-1", "a"), () => { }, () => detached++);
        Shells.Hold(new ForegroundCall(new RunId("run-1"), "call-2", "b"), () => { }, null);
        Assert.False(Shells.DetachCall(new RunId("run-1"), "call-2"));
        Assert.True(Shells.DetachCall(new RunId("run-1"), "call-1"));
        Assert.Equal(1, detached);
    }

    /// <summary>名额满了当场拒绝,不先转走再收编失败:拒绝时前台照旧在等,什么都没坏。</summary>
    [Fact]
    public void A_full_table_refuses_the_move_up_front()
    {
        for (var i = 0; i < ShellLimits.MaxRunning; i++)
        {
            Adopt(callId: $"bg-{i}");
        }

        var detached = 0;
        Shells.Hold(new ForegroundCall(new RunId("run-1"), "call-1", "a"), () => { }, () => detached++);
        Assert.False(Shells.DetachCall(new RunId("run-1"), "call-1"));
        Assert.Equal(0, detached);
        var e = Assert.Throws<ShellRegistryException>(() => Adopt(callId: "one-too-many"));
        Assert.Contains("Too many background shells", e.Message);
    }

    // ── 后台读取 ──

    [Fact]
    public async Task Reading_clears_so_the_second_read_only_gets_what_came_after()
    {
        var (info, process) = Adopt();
        process.Emit(false, "first\n");
        await ReadyAsync(info.Id, "first");
        Assert.Equal("first\n", Shells.Read(info.Id, null).Stdout);
        Assert.Equal("", Shells.Read(info.Id, null).Stdout);
        process.Emit(false, "second\n");
        await ReadyAsync(info.Id, "second");
        Assert.Equal("second\n", Shells.Read(info.Id, null).Stdout);
    }

    [Fact]
    public async Task Streams_are_separate_and_the_status_follows_the_process()
    {
        var (info, process) = Adopt();
        process.Emit(false, "out");
        process.Emit(true, "err");
        await Wait.UntilAsync(() => Shells.Peek(info.Id)!.Value.Output is var o && o.Contains("out", StringComparison.Ordinal) && o.Contains("err", StringComparison.Ordinal));
        var first = Shells.Read(info.Id, null);
        Assert.Equal(("out", "err"), (first.Stdout, first.Stderr));
        Assert.Equal(BackgroundShellStatus.Running, first.Info.Status);

        process.Finish(3);
        await Wait.UntilAsync(() => Shells.List().Single().Status == BackgroundShellStatus.Exited);
        var second = Shells.Read(info.Id, null);
        Assert.Equal(3, second.Info.ExitCode);
    }

    [Fact]
    public async Task The_filter_selects_lines_and_discards_the_rest_instead_of_keeping_them_for_next_time()
    {
        var (info, process) = Adopt();
        process.Emit(false, "keep me\ndrop me\n");
        await ReadyAsync(info.Id, "drop me");
        Assert.Equal("keep me", Shells.Read(info.Id, new Regex("keep")).Stdout);
        Assert.Equal("", Shells.Read(info.Id, null).Stdout);
    }

    [Fact]
    public async Task Overflow_drops_the_oldest_part_and_reports_it_once()
    {
        var (info, process) = Adopt();
        process.Emit(false, new string('A', ShellLimits.MaxBufferChars));
        process.Emit(false, "TAIL");
        await ReadyAsync(info.Id, "TAIL");
        var first = Shells.Read(info.Id, null);
        Assert.True(first.Dropped);
        Assert.EndsWith("TAIL", first.Stdout);
        Assert.Equal(ShellLimits.MaxBufferChars, first.Stdout.Length);
        Assert.False(Shells.Read(info.Id, null).Dropped);
    }

    [Fact]
    public async Task ANSI_is_stripped_on_the_way_in_so_line_filters_work()
    {
        var (info, process) = Adopt();
        process.Emit(false, "\u001B[31merror: boom\u001B[0m\nfine\n");
        await ReadyAsync(info.Id, "fine");
        Assert.Equal("error: boom", Shells.Read(info.Id, new Regex("^error")).Stdout);
    }

    [Fact]
    public async Task A_multi_byte_character_split_across_two_writes_is_decoded_correctly()
    {
        var (info, process) = Adopt();
        var bytes = System.Text.Encoding.UTF8.GetBytes("日本\n");
        // 把「日」的三个字节拆成两次写
        process.EmitRaw(false, bytes[..2]);
        await Task.Delay(50);
        process.EmitRaw(false, bytes[2..]);
        await ReadyAsync(info.Id, "本");
        Assert.Equal("日本\n", Shells.Read(info.Id, null).Stdout);
    }

    [Fact]
    public void An_unknown_id_error_lists_what_exists_so_the_model_does_not_retry_blindly()
    {
        var (info, _) = Adopt();
        var e = Assert.Throws<ShellRegistryException>(() => Shells.Read("bash_404", null));
        Assert.Contains(info.Id, e.Message);
        Assert.Contains("Do not retry", e.Message);
    }

    // ── 收工 ──

    [Fact]
    public async Task Kill_marks_it_killed_and_a_later_exit_does_not_flip_it_back_to_exited()
    {
        var (info, process) = Adopt();
        var killed = await Shells.KillAsync(info.Id, CancellationToken.None);
        Assert.Equal(BackgroundShellStatus.Killed, killed.Status);
        Assert.Equal(1, process.Kills);
        process.Finish(143);
        await Wait.UntilAsync(() => process.Disposed, what: "the watcher to finish");
        Assert.Equal(BackgroundShellStatus.Killed, Shells.Read(info.Id, null).Info.Status);
    }

    [Fact]
    public async Task Killing_a_finished_shell_is_not_an_error_and_reports_its_old_status()
    {
        var (info, process) = Adopt();
        process.Finish(0);
        await Wait.UntilAsync(() => Shells.List().Single().Status == BackgroundShellStatus.Exited);
        var result = await Shells.KillAsync(info.Id, CancellationToken.None);
        Assert.Equal(BackgroundShellStatus.Exited, result.Status);
        Assert.Equal(0, process.Kills);
    }

    [Fact]
    public async Task RunningCount_only_counts_the_running_ones()
    {
        var first = Adopt();
        Adopt(callId: "c2");
        Assert.Equal(2, Shells.RunningCount);
        first.Process.Finish(0);
        await Wait.UntilAsync(() => Shells.RunningCount == 1);
    }

    /// <summary>淘汰只动已结束的,还在跑的一条都不能丢。</summary>
    [Fact]
    public async Task Eviction_only_touches_finished_shells()
    {
        var alive = Adopt();
        for (var i = 0; i < ShellLimits.MaxTracked + 4; i++)
        {
            var finished = Adopt(callId: $"f{i}");
            finished.Process.Finish(0);
            await Wait.UntilAsync(() => finished.Process.Disposed || Shells.List().All(s => s.Id != finished.Info.Id) || Shells.List().First(s => s.Id == finished.Info.Id).Status != BackgroundShellStatus.Running);
        }

        Assert.True(Shells.List().Count <= ShellLimits.MaxTracked);
        Assert.Contains(Shells.List(), s => s.Id == alive.Info.Id);
    }

    [Fact]
    public async Task Ids_are_never_reused()
    {
        var first = Adopt();
        first.Process.Finish(0);
        await Wait.UntilAsync(() => Shells.RunningCount == 0);
        Assert.NotEqual(first.Info.Id, Adopt().Info.Id);
    }

    /// <summary>shutdown 杀光所有后台进程:它们不杀的话会活过应用本身,重启后端口还占着,而界面上再也找不到是谁占的。</summary>
    [Fact]
    public async Task Shutdown_kills_every_running_shell_and_clears_the_table()
    {
        var first = Adopt();
        var second = Adopt(callId: "c2");
        await Shells.ShutdownAsync();
        Assert.Equal(1, first.Process.Kills);
        Assert.Equal(1, second.Process.Kills);
        Assert.Empty(Shells.List());
    }

    // ── 界面旁观 ──

    [Fact]
    public async Task Peek_does_not_consume_the_models_buffer()
    {
        var (info, process) = Adopt();
        process.Emit(false, "ready\n");
        await ReadyAsync(info.Id, "ready");
        Assert.Contains("ready", Shells.Peek(info.Id)!.Value.Output);
        Assert.Equal("ready\n", Shells.Read(info.Id, null).Stdout);
        Assert.Contains("ready", Shells.Peek(info.Id)!.Value.Output); // 模型读走之后,界面的尾巴不跟着清空
    }

    [Fact]
    public void Peek_of_an_unknown_id_is_null_not_an_exception()
    {
        Assert.Null(Shells.Peek("bash_404"));
    }

    [Fact]
    public async Task Changed_fires_for_open_kill_and_exit_but_not_for_output_growth()
    {
        var changes = 0;
        Shells.Changed += () => Interlocked.Increment(ref changes);
        var first = Adopt();
        Assert.Equal(1, Volatile.Read(ref changes));
        first.Process.Emit(false, "log line\n");
        await ReadyAsync(first.Info.Id, "log line");
        Assert.Equal(1, Volatile.Read(ref changes));
        await Shells.KillAsync(first.Info.Id, CancellationToken.None);
        Assert.Equal(2, Volatile.Read(ref changes));
        var second = Adopt(callId: "c2");
        second.Process.Finish(0);
        await Wait.UntilAsync(() => Volatile.Read(ref changes) >= 4, what: "open + exit notifications");
    }

    // ── 工具层:BashOutput / KillShell ──

    private readonly BashOutputTool _output = new();
    private readonly KillShellTool _kill = new();

    [Fact]
    public async Task Without_a_registry_BashOutput_and_KillShell_are_not_offered_and_say_so_when_called_anyway()
    {
        var bare = new ToolRuntime { Environment = _fx.Env, Root = _fx.Runtime.Root };
        var baseContext = new ToolContext(new RunId("r"), "c", "s", 0, null, new Progress<ToolProgress>(), new FileMutationQueue());
        var ctxWith = new ExecutionToolContext(baseContext, _fx.Runtime);
        var ctxWithout = new ExecutionToolContext(baseContext, bare);
        Assert.True(_output.IsEnabled(ctxWith));
        Assert.False(_output.IsEnabled(ctxWithout));
        Assert.False(_kill.IsEnabled(ctxWithout));
        Assert.False(_output.IsEnabled(baseContext));

        var r = await _fx.Run(_output, Fixture.Args(("bash_id", "bash_1")), runtime: bare);
        Assert.True(r.IsError);
        Assert.Contains("unavailable", r.Text());
        var k = await _fx.Run(_kill, Fixture.Args(("shell_id", "bash_1")), runtime: bare);
        Assert.True(k.IsError);
    }

    [Fact]
    public async Task BashOutput_describes_status_in_words_and_tags_the_two_streams()
    {
        var (info, process) = Adopt();
        process.Emit(false, "ready on :5173");
        process.Emit(true, "warn");
        await Wait.UntilAsync(() => Shells.Peek(info.Id)!.Value.Output.Length >= "ready on :5173warn".Length);
        var r = await _fx.Run(_output, ("bash_id", info.Id));
        Assert.Contains("is still running", r.Text());
        Assert.Contains("<stdout>", r.Text());
        Assert.Contains("<stderr>", r.Text());
    }

    [Fact]
    public async Task BashOutput_reports_the_exit_code_and_an_empty_read_explicitly()
    {
        var (info, process) = Adopt();
        process.Finish(1);
        await Wait.UntilAsync(() => Shells.List().Single().Status == BackgroundShellStatus.Exited);
        var r = await _fx.Run(_output, ("bash_id", info.Id));
        Assert.Contains("exited with code 1", r.Text());
        Assert.Contains("no new output", r.Text());
    }

    [Fact]
    public async Task BashOutput_says_when_output_was_dropped()
    {
        var (info, process) = Adopt();
        process.Emit(false, new string('x', ShellLimits.MaxBufferChars + 10));
        await Wait.UntilAsync(() => Shells.Peek(info.Id)!.Value.Output.Length > 0);
        await Task.Delay(200);
        var r = await _fx.Run(_output, ("bash_id", info.Id));
        Assert.Contains("OLDEST part was dropped", r.Text());
        Assert.True(r.Text().Length < ShellLimits.MaxReadChars + 600); // 回读有自己的上限
    }

    [Fact]
    public async Task BashOutput_rejects_a_dangerous_filter_without_consuming_the_buffer_and_rejects_an_invalid_one()
    {
        var (info, process) = Adopt();
        process.Emit(false, "line\n");
        await ReadyAsync(info.Id, "line");
        var danger = await _fx.Run(_output, ("bash_id", info.Id), ("filter", "(?=l)(l+)+$"));
        Assert.True(danger.IsError);
        Assert.Contains("backtracking", danger.Text());
        var invalid = await _fx.Run(_output, ("bash_id", info.Id), ("filter", "([x"));
        Assert.True(invalid.IsError);
        Assert.Contains("Invalid filter", invalid.Text());
        Assert.Equal("line\n", Shells.Read(info.Id, null).Stdout); // 都没消费
    }

    [Fact]
    public async Task BashOutput_with_an_unknown_id_forwards_the_registry_message()
    {
        Adopt();
        var r = await _fx.Run(_output, ("bash_id", "bash_9"));
        Assert.True(r.IsError);
        Assert.Contains("bash_1 (running)", r.Text());
    }

    [Fact]
    public async Task KillShell_names_what_it_killed_and_a_finished_shell_is_not_a_failure()
    {
        var (info, _) = Adopt();
        var r = await _fx.Run(_kill, ("shell_id", info.Id));
        Assert.False(r.IsError);
        Assert.Contains("Killed shell bash_1", r.Text());

        var (done, process) = Adopt(callId: "c2");
        process.Finish(0);
        await Wait.UntilAsync(() => Shells.List().Single(s => s.Id == done.Id).Status == BackgroundShellStatus.Exited);
        var again = await _fx.Run(_kill, ("shell_id", done.Id));
        Assert.False(again.IsError);
        Assert.Contains("already finished", again.Text());
    }
}
