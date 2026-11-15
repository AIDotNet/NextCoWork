using System.Text;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Terminal.Tests;

/// <summary>
/// 终端会话表的逻辑测试,移植自 2.x 的 <c>agent/server/__tests__/terminals.test.ts</c>(8 条)并补上 3.0 新增的:游标续传、状态单调、同 id 重连、字节而不是字符串。
/// 终端是假的(只验证管理器的逻辑),时钟是手动的(合批、保留期确定性)。真 PTY 见 <see cref="PosixPtyTests"/>。
/// </summary>
public class TerminalManagerTests
{
    private static string Text(ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span);

    [Fact]
    public async Task Output_is_batched_into_sequenced_events_and_a_late_attacher_reads_the_buffer_then_continues_contiguously()
    {
        await using var f = new Fixture();
        var info = await f.CreateAsync();
        var terminal = f.Env.Terminals[0];
        await terminal.PushAsync("he");
        await terminal.PushAsync("llo");
        var early = new List<TerminalEvent>();
        using var a = f.Manager.Attach(info.Id, early.Add);
        // attach 先冲掉合批里的输出:快照完整,订阅之后才有增量
        Assert.Equal("hello", Text(a.Snapshot));
        Assert.Equal(1, a.Seq);
        Assert.False(a.Truncated);
        Assert.False(a.Resumed);

        await terminal.PushAsync("!");
        Assert.Empty(early); // 还在合批里
        f.Time.Advance(TimeSpan.FromMilliseconds(20));
        var evt = Assert.IsType<TerminalOutput>(Assert.Single(early));
        Assert.Equal(2, evt.Seq);
        Assert.Equal("!", Text(evt.Data));

        using var late = f.Manager.Attach(info.Id, _ => { });
        Assert.Equal("hello!", Text(late.Snapshot));
        Assert.Equal(2, late.Seq);
    }

    [Fact]
    public async Task Only_the_last_256_KiB_are_kept_and_oversized_batches_flush_immediately()
    {
        await using var f = new Fixture();
        var info = await f.CreateAsync();
        var terminal = f.Env.Terminals[0];
        var events = new List<TerminalEvent>();
        using var _ = f.Manager.Attach(info.Id, events.Add);
        await terminal.PushAsync(new string('x', 70 * 1024)); // 超过单批上限:不等定时器
        Assert.Single(events);
        Assert.Equal(64 * 1024, Assert.IsType<TerminalOutput>(events[0]).Data.Length);
        for (var i = 0; i < 5; i++)
        {
            await terminal.PushAsync(new string('y', 70 * 1024));
        }

        var buffer = f.Manager.GetBuffer(info.Id);
        Assert.Equal(256 * 1024, buffer.Data.Length);
        Assert.True(buffer.Truncated);
        // 事件序号连续
        Assert.Equal(Enumerable.Range(1, events.Count).Select(static i => (long)i), events.Select(static e => e.Seq));
    }

    [Fact]
    public async Task Exit_is_the_next_sequence_number_and_input_is_refused_afterwards()
    {
        await using var f = new Fixture();
        var info = await f.CreateAsync();
        var terminal = f.Env.Terminals[0];
        var events = new List<TerminalEvent>();
        using var _ = f.Manager.Attach(info.Id, events.Add);
        await terminal.PushAsync("bye");
        terminal.Finish(3);
        await f.WaitExitedAsync(info.Id);

        Assert.Equal(2, events.Count);
        Assert.Equal("bye", Text(Assert.IsType<TerminalOutput>(events[0]).Data));
        var exit = Assert.IsType<TerminalExit>(events[1]);
        Assert.Equal((2L, 3), (exit.Seq, exit.ExitCode));
        var listed = Assert.Single(f.Manager.List("ws"));
        Assert.Equal(TerminalStatus.Exited, listed.Status);
        Assert.Equal(3, listed.ExitCode);
        Assert.Equal(ProcessExitReason.Exited, listed.ExitReason);
        await Fixture.ExpectAsync(TerminalErrorCode.Conflict, () => f.Manager.WriteAsync(info.Id, "x"));
        await Fixture.ExpectAsync(TerminalErrorCode.Conflict, () => f.Manager.ResizeAsync(info.Id, 10, 10));
        await Fixture.ExpectAsync(TerminalErrorCode.Conflict, () => f.Manager.KillAsync(info.Id));
    }

    [Fact]
    public async Task Terminal_state_is_monotonic_late_output_and_a_second_exit_are_dropped()
    {
        await using var f = new Fixture();
        var info = await f.CreateAsync();
        var terminal = f.Env.Terminals[0];
        var events = new List<TerminalEvent>();
        using var _ = f.Manager.Attach(info.Id, events.Add);
        terminal.Finish(1);
        await f.WaitExitedAsync(info.Id);
        terminal.Finish(9, ProcessExitReason.Killed); // 第二个退出:丢弃
        await Task.Delay(50);
        Assert.Single(events);
        Assert.Equal(1, f.Manager.Get(info.Id).ExitCode);
        Assert.Equal(1, f.Manager.GetBuffer(info.Id).Seq);
    }

    [Fact]
    public async Task Input_resize_and_kill_are_forwarded_and_input_is_bounded()
    {
        await using var f = new Fixture();
        var info = await f.CreateAsync();
        var terminal = f.Env.Terminals[0];
        await f.Manager.WriteAsync(info.Id, "ls\n");
        await f.Manager.ResizeAsync(info.Id, 120, 40);
        Assert.Equal("ls\n", terminal.WrittenText);
        Assert.Equal((120, 40), terminal.Size);
        var listed = Assert.Single(f.Manager.List("ws"));
        Assert.Equal((120, 40), (listed.Columns, listed.Rows));

        // 越界的大小保持原样(同 2.x 的 safeSize)
        await f.Manager.ResizeAsync(info.Id, 0, -5);
        Assert.Equal((120, 40), terminal.Size);

        await Fixture.ExpectAsync(TerminalErrorCode.InvalidArgument, () => f.Manager.WriteAsync(info.Id, ReadOnlyMemory<byte>.Empty));
        await Fixture.ExpectAsync(TerminalErrorCode.TooLarge, () => f.Manager.WriteAsync(info.Id, new byte[64 * 1024 + 1]));
        await f.Manager.WriteAsync(info.Id, new byte[64 * 1024]); // 恰好上限:允许
        await Fixture.ExpectAsync(TerminalErrorCode.NotFound, () => f.Manager.WriteAsync("missing", "x"));
        await f.Manager.KillAsync(info.Id);
        Assert.Equal(1, terminal.Kills);
        Assert.False(f.Manager.Get(info.Id).Alive);
        Assert.Equal(ProcessExitReason.Killed, f.Manager.Get(info.Id).ExitReason);
    }

    [Fact]
    public async Task Limits_are_enforced_per_scope_and_in_total()
    {
        await using var f = new Fixture(o => new TerminalManagerOptions { Time = o.Time, NewId = o.NewId, MaxPerScope = 2, MaxTotal = 3 });
        await f.CreateAsync("a");
        await f.CreateAsync("a");
        await Fixture.ExpectAsync(TerminalErrorCode.ResourceLimit, () => f.CreateAsync("a"));
        await f.CreateAsync("b");
        await Fixture.ExpectAsync(TerminalErrorCode.ResourceLimit, () => f.CreateAsync("c"));
    }

    [Fact]
    public async Task Terminals_still_being_opened_count_against_the_limit()
    {
        await using var f = new Fixture(o => new TerminalManagerOptions { Time = o.Time, NewId = o.NewId, MaxTotal = 2 });
        f.Env.OpenGate = new TaskCompletionSource();
        var first = f.CreateAsync("a");
        var second = f.CreateAsync("b");
        await Wait.UntilAsync(() => f.Env.OpenCount == 2, 5000, "two opens in flight");
        await Fixture.ExpectAsync(TerminalErrorCode.ResourceLimit, () => f.CreateAsync("c"));
        f.Env.OpenGate.SetResult();
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task A_failed_open_is_not_counted_and_leaks_no_session()
    {
        await using var f = new Fixture(o => new TerminalManagerOptions { Time = o.Time, NewId = o.NewId, MaxTotal = 1 });
        f.Env.OpenFailure = new EnvironmentException(EnvironmentErrorCode.ProcessStartFailed, "spawn failed");
        var error = await Assert.ThrowsAsync<EnvironmentException>(() => f.CreateAsync("a"));
        Assert.Equal(EnvironmentErrorCode.ProcessStartFailed, error.Code);
        f.Env.OpenFailure = null;
        await f.CreateAsync("a");
        Assert.Single(f.Manager.List("a"));
    }

    [Fact]
    public async Task Exited_terminals_are_reaped_after_their_retention_but_a_running_one_never_is()
    {
        await using var f = new Fixture(o => new TerminalManagerOptions { Time = o.Time, NewId = o.NewId, ExitedRetention = TimeSpan.FromSeconds(1) });
        var dead = await f.CreateAsync();
        var live = await f.CreateAsync();
        f.Env.Terminals[0].Finish(0);
        await f.WaitExitedAsync(dead.Id);
        f.Time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal([dead.Id, live.Id], f.Manager.List("ws").Select(static t => t.Id));
        f.Time.Advance(TimeSpan.FromMilliseconds(2));
        Assert.Equal([live.Id], f.Manager.List("ws").Select(static t => t.Id));
        f.Time.Advance(TimeSpan.FromDays(30));
        Assert.Equal([live.Id], f.Manager.List("ws").Select(static t => t.Id));
    }

    [Fact]
    public async Task A_throwing_subscriber_cannot_break_the_terminal_or_other_subscribers_and_shutdown_kills_everything()
    {
        await using var f = new Fixture();
        var info = await f.CreateAsync();
        var terminal = f.Env.Terminals[0];
        var seen = new List<TerminalEvent>();
        using var bad = f.Manager.Attach(info.Id, _ => throw new InvalidOperationException("boom"));
        using var good = f.Manager.Attach(info.Id, seen.Add);
        await terminal.PushAsync("ok");
        f.Time.Advance(TimeSpan.FromMilliseconds(20));
        Assert.Single(seen);
        await f.Manager.ShutdownAsync();
        Assert.True(terminal.Disposals >= 1);
        Assert.Empty(f.Manager.List("ws"));
    }

    [Fact]
    public async Task A_disposed_subscription_stops_receiving_events()
    {
        await using var f = new Fixture();
        var info = await f.CreateAsync();
        var seen = new List<TerminalEvent>();
        var subscription = f.Manager.Attach(info.Id, seen.Add);
        await f.Env.Terminals[0].PushAsync("a");
        f.Time.Advance(TimeSpan.FromMilliseconds(20));
        subscription.Dispose();
        await f.Env.Terminals[0].PushAsync("b");
        f.Time.Advance(TimeSpan.FromMilliseconds(20));
        Assert.Single(seen);
    }

    // ───────────────────────── 游标续传 ─────────────────────────

    [Fact]
    public async Task Attaching_with_a_cursor_in_the_log_replays_only_the_missed_events_including_the_exit()
    {
        await using var f = new Fixture();
        var info = await f.CreateAsync();
        var terminal = f.Env.Terminals[0];
        await terminal.PushAsync("one");
        f.Time.Advance(TimeSpan.FromMilliseconds(20)); // seq 1
        await terminal.PushAsync("two");
        f.Time.Advance(TimeSpan.FromMilliseconds(20)); // seq 2
        await terminal.PushAsync("three");
        f.Time.Advance(TimeSpan.FromMilliseconds(20)); // seq 3
        terminal.Finish(0);
        await f.WaitExitedAsync(info.Id); // exit seq 4

        using var resumed = f.Manager.Attach(info.Id, _ => { }, sinceSeq: 1);
        Assert.True(resumed.Resumed);
        Assert.True(resumed.Snapshot.IsEmpty);
        Assert.Equal(4, resumed.Seq);
        Assert.Equal([2L, 3L, 4L], resumed.Replay.Select(static e => e.Seq));
        Assert.Equal("two", Text(Assert.IsType<TerminalOutput>(resumed.Replay[0]).Data));
        Assert.IsType<TerminalExit>(resumed.Replay[2]);

        using var current = f.Manager.Attach(info.Id, _ => { }, sinceSeq: 4);
        Assert.True(current.Resumed);
        Assert.Empty(current.Replay);

        // 游标比日志还新(重启过、或者是别的终端的):整个快照
        using var future = f.Manager.Attach(info.Id, _ => { }, sinceSeq: 99);
        Assert.False(future.Resumed);
        Assert.Equal("onetwothree", Text(future.Snapshot));
    }

    [Fact]
    public async Task A_cursor_older_than_the_log_gets_a_full_snapshot()
    {
        await using var f = new Fixture(o => new TerminalManagerOptions { Time = o.Time, NewId = o.NewId, BufferBytes = 10 });
        var info = await f.CreateAsync();
        var terminal = f.Env.Terminals[0];
        for (var i = 0; i < 4; i++)
        {
            await terminal.PushAsync("abcdef");
            f.Time.Advance(TimeSpan.FromMilliseconds(20));
        }

        // 容量 10 字节:seq 1、2 的批已经被丢掉,seq 3 被裁成半截
        using var stale = f.Manager.Attach(info.Id, _ => { }, sinceSeq: 1);
        Assert.False(stale.Resumed);
        Assert.True(stale.Truncated);
        Assert.Equal("cdefabcdef", Text(stale.Snapshot));
        // 紧挨着最新的游标还能续上
        using var fresh = f.Manager.Attach(info.Id, _ => { }, sinceSeq: 3);
        Assert.True(fresh.Resumed);
        Assert.Equal([4L], fresh.Replay.Select(static e => e.Seq));
    }

    [Fact]
    public async Task A_truncated_buffer_starts_on_a_character_boundary()
    {
        await using var f = new Fixture(o => new TerminalManagerOptions { Time = o.Time, NewId = o.NewId, BufferBytes = 8 });
        var info = await f.CreateAsync();
        // 4 个三字节字符 = 12 字节,容量 8:从第一个字符的中间裁就会剩下半个字符,必须对齐到下一个字符的开头
        await f.Env.Terminals[0].PushAsync("你好世界");
        var buffer = f.Manager.GetBuffer(info.Id);
        Assert.True(buffer.Truncated);
        Assert.Equal("世界", Text(buffer.Data)); // 从第 4 个字节裁:落在「好」的中间,跳过它的两个续字节
        Assert.DoesNotContain('�', Text(buffer.Data));
    }

    [Fact]
    public async Task Output_is_bytes_a_character_split_across_batches_is_left_to_the_display_decoder()
    {
        await using var f = new Fixture();
        var info = await f.CreateAsync();
        var bytes = Encoding.UTF8.GetBytes("世");
        var events = new List<TerminalEvent>();
        using var _ = f.Manager.Attach(info.Id, events.Add);
        await f.Env.Terminals[0].PushAsync(bytes[..2]);
        f.Time.Advance(TimeSpan.FromMilliseconds(20));
        await f.Env.Terminals[0].PushAsync(bytes[2..]);
        f.Time.Advance(TimeSpan.FromMilliseconds(20));
        Assert.Equal(2, events.Count);
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[8];
        var count = events.Cast<TerminalOutput>().Sum(e => decoder.GetChars(e.Data.Span, chars.AsSpan(), flush: false));
        Assert.Equal(1, count);
        Assert.Equal(bytes, f.Manager.GetBuffer(info.Id).Data.ToArray());
    }

    // ───────────────────────── 同 id 重连、作用域、目录 ─────────────────────────

    [Fact]
    public async Task Creating_again_with_a_live_id_returns_the_same_terminal_and_opens_nothing_new()
    {
        await using var f = new Fixture();
        var first = await f.Manager.CreateAsync(f.Request(id: "tab-1"), Fixture.Alice);
        var second = await f.Manager.CreateAsync(f.Request(id: "tab-1"), Fixture.Bob); // 本机终端属于设备:别的控制端也能重连
        Assert.Equal(first, second);
        Assert.Equal(1, f.Env.OpenCount);
        await Fixture.ExpectAsync(TerminalErrorCode.Conflict, () => f.Manager.CreateAsync(f.Request(scope: "other", id: "tab-1"), Fixture.Alice));
    }

    [Fact]
    public async Task Creating_again_after_the_exit_continues_the_same_sequence_and_buffer()
    {
        await using var f = new Fixture();
        await f.Manager.CreateAsync(f.Request(id: "tab-1"), Fixture.Alice);
        var seen = new List<TerminalEvent>();
        using var subscription = f.Manager.Attach("tab-1", seen.Add);
        await f.Env.Terminals[0].PushAsync("before");
        f.Env.Terminals[0].Finish(0);
        await f.WaitExitedAsync("tab-1"); // seq 1 输出, seq 2 退出

        var again = await f.Manager.CreateAsync(f.Request(id: "tab-1"), Fixture.Alice);
        Assert.True(again.Alive);
        Assert.Equal(2, f.Env.OpenCount);
        await f.Env.Terminals[1].PushAsync("after");
        f.Time.Advance(TimeSpan.FromMilliseconds(20));
        Assert.Equal([1L, 2L, 3L], seen.Select(static e => e.Seq)); // 订阅者续订到新的一段
        var buffer = f.Manager.GetBuffer("tab-1");
        Assert.Equal("beforeafter", Text(buffer.Data));
        Assert.Equal(3, buffer.Seq);
    }

    [Fact]
    public async Task The_working_directory_must_be_a_directory_inside_the_root()
    {
        await using var f = new Fixture();
        Directory.CreateDirectory(Path.Combine(f.Dir.Path, "sub"));
        File.WriteAllText(Path.Combine(f.Dir.Path, "file.txt"), "x");

        var inside = await f.Manager.CreateAsync(f.Request(cwd: "sub"), Fixture.Alice);
        Assert.EndsWith("sub", inside.Cwd, StringComparison.Ordinal);
        Assert.Equal("sub", inside.Title);
        Assert.Equal(inside.Cwd, f.Env.Specs[0].WorkingDirectory!.Value.Value);

        var outside = await Assert.ThrowsAsync<EnvironmentException>(() => f.Manager.CreateAsync(f.Request(cwd: "../escape"), Fixture.Alice));
        Assert.Equal(EnvironmentErrorCode.PathEscape, outside.Code);
        var notDirectory = await Assert.ThrowsAsync<EnvironmentException>(() => f.Manager.CreateAsync(f.Request(cwd: "file.txt"), Fixture.Alice));
        Assert.Equal(EnvironmentErrorCode.NotDirectory, notDirectory.Code);
        var missing = await Assert.ThrowsAsync<EnvironmentException>(() => f.Manager.CreateAsync(f.Request(cwd: "nope"), Fixture.Alice));
        Assert.Equal(EnvironmentErrorCode.NotFound, missing.Code);
        Assert.Equal(1, f.Env.OpenCount);
    }

    [Fact]
    public async Task A_disconnected_environment_refuses_to_open_and_the_terminal_info_carries_the_environment_identity()
    {
        await using var f = new Fixture();
        var info = await f.CreateAsync();
        Assert.Equal(new EnvironmentId("dev", "local"), info.Environment.Id);
        Assert.Equal(EnvironmentKind.Local, info.Environment.Kind);
        Assert.Equal("/bin/sh", info.Shell);

        f.Env.State = EnvironmentState.Failed;
        var error = await Assert.ThrowsAsync<EnvironmentException>(() => f.CreateAsync());
        Assert.Equal(EnvironmentErrorCode.Disconnected, error.Code);
        Assert.Equal(1, f.Env.OpenCount);
    }

    [Fact]
    public async Task A_terminal_whose_environment_connection_was_replaced_refuses_input()
    {
        await using var f = new Fixture();
        var info = await f.CreateAsync();
        f.Env.Generation++; // 重连:旧代发起的终端不会悄悄「复活」到新连接上
        var error = await Assert.ThrowsAsync<EnvironmentException>(() => f.Manager.WriteAsync(info.Id, "x"));
        Assert.Equal(EnvironmentErrorCode.StaleGeneration, error.Code);
    }

    [Fact]
    public async Task A_connection_loss_marks_the_terminal_exited_with_an_unknown_exit_code()
    {
        await using var f = new Fixture(kind: EnvironmentKind.Ssh);
        var grant = await f.GrantAsync("tab-1");
        await f.Manager.CreateAsync(f.Request(id: "tab-1", approval: grant), Fixture.Alice);
        f.Env.Terminals[0].Finish(0, ProcessExitReason.ConnectionLost);
        await f.WaitExitedAsync("tab-1");
        var info = f.Manager.Get("tab-1");
        Assert.Equal(ProcessExitReason.ConnectionLost, info.ExitReason);
    }

    [Fact]
    public async Task Closing_a_terminal_kills_it_and_removes_it_and_closing_a_scope_closes_all_of_them()
    {
        await using var f = new Fixture();
        var a = await f.CreateAsync("ws");
        var b = await f.CreateAsync("ws");
        var other = await f.CreateAsync("other");
        await f.Manager.CloseAsync(a.Id);
        Assert.Equal(1, f.Env.Terminals[0].Kills);
        Assert.Equal([b.Id], f.Manager.List("ws").Select(static t => t.Id));
        await f.Manager.CloseAsync("missing"); // 不存在:什么都不做
        await f.Manager.CloseScopeAsync("ws");
        Assert.Empty(f.Manager.List("ws"));
        Assert.Equal([other.Id], f.Manager.List("other").Select(static t => t.Id));
    }

    [Fact]
    public async Task A_controller_leaving_does_not_kill_local_terminals_and_another_controller_can_reattach()
    {
        await using var f = new Fixture();
        var info = await f.CreateAsync(controller: Fixture.Alice);
        await f.Env.Terminals[0].PushAsync("before-disconnect");
        await f.Manager.ReleaseController(Fixture.Alice);
        Assert.True(f.Manager.Get(info.Id).Alive);
        Assert.Contains("before-disconnect", Text(f.Manager.GetBuffer(info.Id, Fixture.Bob).Data), StringComparison.Ordinal);
        Assert.Single(f.Manager.List("ws", Fixture.Bob));
    }
}
