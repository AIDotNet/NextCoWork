using System.Text.Json;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Runs.Tests.Support;

namespace Ncw.Agent.Runs.Tests;

/// <summary>
/// 事件日志与游标(05 §2、02 §3)。移植自 2.x <c>run-registry.test.ts</c> 的「RunHandle · seq 与重放 / 日志裁剪 / 多消费者与生命周期 / 日志预算」几组,
/// 并补上 3.0 的游标状态(Fresh / Continued / Lost)和终态单调。
/// </summary>
public sealed class EventLogTests
{
    internal sealed class StubHost : IRunHandleHost
    {
        public TimeProvider Clock { get; } = new FakeClock();

        public CollectingLog Warnings { get; } = new();

        public ILoopDiagnostics Log => Warnings;

        public List<(AgentEvent Event, long Seq)> Events { get; } = [];

        public void OnEvent(RunHandle run, AgentEvent ev, long seq) => Events.Add((ev, seq));

        public void OnAbortRequested(RunHandle run)
        {
        }
    }

    internal static RunHandle NewHandle(StubHost? host = null, int maxEntries = 2000, long maxBytes = 16L * 1024 * 1024, string run = "r1") =>
        new(new RunLaunch("s1", "ws", new RunRequest(new RunId(run), [], ModelRef.Unpinned("m"))), host ?? new StubHost(), maxEntries, maxBytes);

    internal static MessageDeltaEvent Delta(string id, string text) => new(new MessageId(id), 0, MessageDeltaKind.Text, text);

    internal static MessageStartEvent Start(string id) => new(new MessageId(id), Role.Assistant);

    internal static MessageEndEvent End(string id, string text = "done") =>
        new(new Message(new MessageId(id), Role.Assistant, [new TextPart(text)], DateTimeOffset.UnixEpoch));

    internal static RunStartEvent RunStart(RunHandle h) => new(h.RunId, "s1", ModelRef.Unpinned("m"));

    [Fact]
    public void Seq_starts_at_one_and_increments_per_event()
    {
        var h = NewHandle();
        Assert.Equal(1, h.Emit(RunStart(h)));
        Assert.Equal(2, h.Emit(new TurnStartEvent(0)));
        Assert.Equal(2, h.Seq);
    }

    [Fact]
    public void Since_zero_gives_everything_and_since_n_gives_what_follows()
    {
        var h = NewHandle();
        h.Emit(RunStart(h));
        h.Emit(new TurnStartEvent(0));
        h.Emit(new TurnEndEvent(0));
        Assert.Equal(3, h.Since(0).Count);
        Assert.Equal([2L, 3L], h.Since(1).Select(e => e.Seq));
        Assert.Empty(h.Since(3));
    }

    [Fact]
    public void A_committed_message_does_not_disturb_numbering()
    {
        var h = NewHandle();
        h.Emit(Start("m1"));
        h.Emit(Delta("m1", "a"));
        h.Emit(End("m1"));
        Assert.Equal(4, h.Emit(new TurnEndEvent(0)));
    }

    [Fact]
    public void Message_end_drops_that_messages_deltas_but_keeps_start_and_the_commit()
    {
        var h = NewHandle();
        h.Emit(Start("m1"));
        h.Emit(Delta("m1", "a"));
        h.Emit(Delta("m1", "b"));
        h.Emit(End("m1"));
        var kinds = h.Since(0).Select(e => e.Event.GetType()).ToList();
        Assert.Equal([typeof(MessageStartEvent), typeof(MessageEndEvent)], kinds);
        Assert.Equal([1L, 4L], h.Since(0).Select(e => e.Seq));
    }

    [Fact]
    public void Trimming_is_lossless_for_a_rebuild_deltas_after_the_commit_stay()
    {
        var h = NewHandle();
        h.Emit(Start("m1"));
        h.Emit(Delta("m1", "a"));
        h.Emit(End("m1"));
        h.Emit(Start("m2"));
        h.Emit(Delta("m2", "b"));
        Assert.Equal(typeof(MessageDeltaEvent), h.Since(0)[^1].Event.GetType());
        Assert.Equal(4, h.Since(0).Count);
    }

    [Fact]
    public void Trimming_only_reaches_back_to_the_previous_message_other_messages_are_untouched()
    {
        var h = NewHandle();
        h.Emit(Start("m1"));
        h.Emit(Delta("m1", "a"));
        h.Emit(Start("m2"));
        h.Emit(Delta("m2", "b"));
        h.Emit(End("m2"));
        var remaining = h.Since(0).Select(e => e.Event).OfType<MessageDeltaEvent>().ToList();
        Assert.Single(remaining);
        Assert.Equal("m1", remaining[0].MessageId.Value);
    }

    [Fact]
    public void A_discarded_message_loses_its_deltas_too_but_keeps_start_and_the_discard()
    {
        var h = NewHandle();
        h.Emit(Start("m1"));
        h.Emit(Delta("m1", "a"));
        h.Emit(new MessageDiscardEvent(new MessageId("m1")));
        Assert.Equal([typeof(MessageStartEvent), typeof(MessageDiscardEvent)], h.Since(0).Select(e => e.Event.GetType()));
    }

    [Fact]
    public void Structural_events_are_never_trimmed_so_a_replay_can_rebuild_tool_cards()
    {
        var h = NewHandle();
        var input = JsonDocument.Parse("{}").RootElement.Clone();
        h.Emit(new TurnStartEvent(0));
        h.Emit(new ToolStartEvent("c1", "Read", input, null));
        h.Emit(Start("m1"));
        h.Emit(End("m1"));
        h.Emit(new ToolEndEvent("c1", new ToolOutput("x"), false, 1));
        h.Emit(new TurnEndEvent(0));
        Assert.Equal(6, h.Since(0).Count);
    }

    [Fact]
    public void Since_filters_by_seq_not_by_index_after_trimming()
    {
        var h = NewHandle();
        h.Emit(Start("m1"));
        h.Emit(Delta("m1", "a"));
        h.Emit(Delta("m1", "b"));
        h.Emit(End("m1"));
        h.Emit(new TurnEndEvent(0));
        // 日志里剩 seq 1、4、5;下标 1 对应 seq 4,但「seq 1 之后」必须是 4、5,「seq 2 之后」同样是 4、5。
        Assert.Equal([4L, 5L], h.Since(1).Select(e => e.Seq));
        Assert.Equal([4L, 5L], h.Since(2).Select(e => e.Seq));
        Assert.Equal([5L], h.Since(4).Select(e => e.Seq));
    }

    [Fact]
    public void Tool_end_clears_progress_of_that_call_only()
    {
        var h = NewHandle();
        var input = JsonDocument.Parse("{}").RootElement.Clone();
        h.Emit(new ToolStartEvent("c1", "Bash", input, null));
        h.Emit(new ToolStartEvent("c2", "Bash", input, null));
        h.Emit(new ToolProgressEvent("c1", new ToolProgress("a")));
        h.Emit(new ToolProgressEvent("c2", new ToolProgress("b")));
        h.Emit(new ToolEndEvent("c1", new ToolOutput("x"), false, 1));
        var progress = h.Since(0).Select(e => e.Event).OfType<ToolProgressEvent>().ToList();
        Assert.Equal(["c2"], progress.Select(p => p.CallId));
    }

    /// <summary>
    /// 回归(Integration 的「排位」用例整程序集并发时失败的根因):后台 Task 调用立刻 <c>tool_end</c>,但派发还在排队,客户端把排位留到 <c>subagent_start</c>。
    /// 日志不能把排位当成普通进度在 <c>tool_end</c> 时裁掉,否则任何还没读到它的订阅者 / 续传 / 快照都丢了它。
    /// </summary>
    [Fact]
    public void Tool_end_keeps_the_queue_position_of_a_dispatch_but_still_clears_other_progress()
    {
        var h = NewHandle();
        var input = JsonDocument.Parse("{}").RootElement.Clone();
        var queued = JsonDocument.Parse("""{"kind":"queued","ahead":2}""").RootElement.Clone();
        h.Emit(new ToolStartEvent("c1", "Task", input, null));
        h.Emit(new ToolProgressEvent("c1", new ToolProgress("Queued, 2 ahead.", Details: queued)));
        h.Emit(new ToolProgressEvent("c1", new ToolProgress("working", Details: JsonDocument.Parse("""{"kind":"other"}""").RootElement.Clone())));
        h.Emit(new ToolProgressEvent("c1", new ToolProgress("plain")));
        h.Emit(new ToolEndEvent("c1", new ToolOutput("queued"), false, 1));

        var progress = h.Since(0).Select(e => e.Event).OfType<ToolProgressEvent>().ToList();
        var kept = Assert.Single(progress);
        Assert.Equal("Queued, 2 ahead.", kept.Progress.Message);
        // 续传游标之后、快照里都读得到(订阅者晚一步才来取,也不会丢)
        Assert.Contains(h.Snapshot().Events, e => e.Event is ToolProgressEvent { Progress.Message: "Queued, 2 ahead." });
    }

    /// <summary>
    /// 回归(Integration 的「排位」用例整程序集并发时失败的根因):工具线程经 <c>Post</c> 直接投的进度可能比循环事件通道里的 <c>tool_start</c> 先到,
    /// 控制端对「还没有卡片的进度」只能丢掉。句柄必须让这类事件永远排在对应的 <c>tool_start</c> 之后——这里手动安排「进度先到」,不靠时序。
    /// </summary>
    [Fact]
    public void A_tool_scoped_event_that_arrives_before_its_tool_start_is_held_and_lands_right_after_it()
    {
        var h = NewHandle();
        var input = JsonDocument.Parse("{}").RootElement.Clone();
        var queued = JsonDocument.Parse("""{"kind":"queued","ahead":0}""").RootElement.Clone();
        h.Emit(RunStart(h));

        var early = h.Emit(new ToolProgressEvent("c2", new ToolProgress("Queued", Details: queued)));
        Assert.True(early > 0); // 接受了(不是被丢弃),只是还没有落进日志
        Assert.DoesNotContain(h.Since(0), e => e.Event is ToolProgressEvent);

        h.Emit(new ToolStartEvent("c1", "Task", input, null));
        Assert.DoesNotContain(h.Since(0), e => e.Event is ToolProgressEvent); // 别的调用的 tool_start 不放行它
        h.Emit(new ToolStartEvent("c2", "Task", input, null));
        h.Emit(new ToolEndEvent("c1", new ToolOutput("x"), false, 1));
        h.Emit(new ToolEndEvent("c2", new ToolOutput("queued"), false, 1));

        var events = h.Since(0).ToList();
        var start = events.Single(e => e.Event is ToolStartEvent { CallId: "c2" });
        var progress = events.Single(e => e.Event is ToolProgressEvent);
        Assert.Equal(start.Seq + 1, progress.Seq); // 紧跟在 tool_start 之后,seq 连续、严格递增
        Assert.Equal(events.Select(e => e.Seq).Order().ToList(), events.Select(e => e.Seq).ToList());
        Assert.Equal(events.Count, events.Select(e => e.Seq).Distinct().Count());
    }

    [Fact]
    public void Events_held_for_a_tool_start_that_never_comes_are_dropped_when_the_run_ends_and_do_not_reach_subscribers_early()
    {
        var host = new StubHost();
        var h = NewHandle(host);
        h.Emit(RunStart(h));
        h.Emit(new ToolProgressEvent("ghost", new ToolProgress("p")));
        Assert.DoesNotContain(host.Events, e => e.Event is ToolProgressEvent); // 暂存期间没有推给任何人(也没有 OnEvent)
        h.Emit(new RunEndEvent(RunStatus.Done));
        Assert.DoesNotContain(h.Since(0), e => e.Event is ToolProgressEvent);
        Assert.True(host.Warnings.Any("waiting for a tool_start"));
    }

    [Fact]
    public void Past_the_byte_budget_the_head_is_cut_the_snapshot_says_so_and_seq_keeps_counting()
    {
        var h = NewHandle(maxBytes: 4000);
        for (var i = 0; i < 50; i++)
        {
            h.Emit(new NotificationEvent("k", new string('x', 500)));
        }

        var snapshot = h.Snapshot();
        Assert.True(snapshot.LogTrimmed);
        Assert.Equal(50, snapshot.Seq);
        Assert.Equal(50, snapshot.Events[^1].Seq);
        Assert.True(snapshot.Events.Count < 50);
        Assert.True(h.RetainedLogBytes <= 4000 + 600);
        Assert.Equal(51, h.Emit(new NotificationEvent("k")));
    }

    [Fact]
    public void The_newest_event_is_always_kept_even_when_it_alone_exceeds_the_budget()
    {
        var h = NewHandle(maxBytes: 100);
        h.Emit(new NotificationEvent("k", new string('x', 5000)));
        Assert.Single(h.Since(0));
    }

    [Fact]
    public void The_entry_cap_is_a_hard_ceiling()
    {
        var h = NewHandle(maxEntries: 10);
        for (var i = 0; i < 25; i++)
        {
            h.Emit(new NotificationEvent("k"));
        }

        Assert.Equal(10, h.Since(0).Count);
        Assert.Equal(16, h.Since(0)[0].Seq);
        Assert.True(h.Snapshot().LogTrimmed);
    }

    [Fact]
    public void The_size_estimate_covers_strings_nested_in_messages_and_tool_output()
    {
        var small = EventSizes.Estimate(End("m", "x"));
        var big = EventSizes.Estimate(End("m", new string('x', 10_000)));
        Assert.True(big - small >= 9_000);
        Assert.True(EventSizes.Estimate(new ToolEndEvent("c", new ToolOutput(new string('y', 4000)), false, 1)) >= 4000);
    }

    // ── 终态与单调(★) ──

    [Fact]
    public void Events_after_the_terminal_state_are_dropped_and_do_not_advance_seq()
    {
        var host = new EventLogTests.StubHost();
        var h = NewHandle(host);
        h.Emit(RunStart(h));
        h.Emit(new RunEndEvent(RunStatus.Done));
        Assert.Equal(0, h.Emit(new TurnStartEvent(1)));
        Assert.Equal(2, h.Seq);
        Assert.True(host.Warnings.Any("dropped late event"));
    }

    [Fact]
    public void A_second_run_end_never_overwrites_the_first_state()
    {
        var h = NewHandle();
        h.Emit(RunStart(h));
        h.Emit(new RunEndEvent(RunStatus.Aborted));
        Assert.Equal(0, h.Emit(new RunEndEvent(RunStatus.Done)));
        Assert.Equal(RunState.Aborted, h.State);
        Assert.False(h.Finish(RunStatus.Error));
        Assert.Equal(RunState.Aborted, h.State);
    }

    [Fact]
    public void A_replayed_old_run_start_cannot_bring_a_finished_run_back_to_running()
    {
        var h = NewHandle();
        h.Emit(RunStart(h));
        h.Emit(new RunEndEvent(RunStatus.Error, AgentError.Create(AgentErrorCode.Network, "x")));
        Assert.Equal(0, h.Emit(RunStart(h)));
        Assert.Equal(RunState.Error, h.State);
        Assert.Equal(2, h.Snapshot().Events.Count);
    }

    [Fact]
    public void Run_settled_follows_the_terminal_state_exactly_once_and_never_while_running()
    {
        var h = NewHandle();
        h.Emit(RunStart(h));
        Assert.Equal(0, h.Emit(new RunSettledEvent(h.RunId, RunStatus.Done)));
        h.Emit(new RunEndEvent(RunStatus.Done));
        Assert.False(h.IsSettled);
        Assert.Equal(3, h.Emit(new RunSettledEvent(h.RunId, RunStatus.Done)));
        Assert.True(h.IsSettled);
        Assert.Equal(0, h.Emit(new RunSettledEvent(h.RunId, RunStatus.Done)));
    }

    [Fact]
    public void Finish_without_a_run_start_supplies_one_so_the_stream_is_well_formed()
    {
        var h = NewHandle();
        Assert.True(h.Finish(RunStatus.Error, AgentError.Create(AgentErrorCode.Unknown, "x")));
        Assert.Equal([typeof(RunStartEvent), typeof(RunEndEvent)], h.Since(0).Select(e => e.Event.GetType()));
    }

    // ── 快照与游标 ──

    [Fact]
    public void A_snapshot_carries_state_seq_and_children_and_is_a_copy()
    {
        var h = NewHandle();
        h.Emit(RunStart(h));
        h.AddChild(new RunId("c1"));
        var snapshot = h.Snapshot();
        h.Emit(new TurnStartEvent(0));
        Assert.Equal(RunState.Running, snapshot.State);
        Assert.Equal(1, snapshot.Seq);
        Assert.Equal(["c1"], snapshot.Children.Select(c => c.Value));
        Assert.Single(snapshot.Events);
        Assert.Equal(CursorStatus.Fresh, snapshot.Cursor);
    }

    [Fact]
    public void A_cursor_still_in_the_log_continues_and_one_that_was_cut_off_is_lost()
    {
        var h = NewHandle(maxEntries: 10);
        for (var i = 0; i < 25; i++)
        {
            h.Emit(new NotificationEvent("k"));
        }

        var continued = h.Snapshot(sinceSeq: 20);
        Assert.Equal(CursorStatus.Continued, continued.Cursor);
        Assert.Equal([21L, 22L, 23L, 24L, 25L], continued.Events.Select(e => e.Seq));

        // 15 是被上限挤掉的最大序号:游标 15 刚好还连得上(下一条 16 在日志里),游标 10 不行。
        Assert.Equal(CursorStatus.Continued, h.Snapshot(sinceSeq: 15).Cursor);
        var lost = h.Snapshot(sinceSeq: 10);
        Assert.Equal(CursorStatus.Lost, lost.Cursor);
        Assert.Equal(10, lost.Events.Count);
    }

    [Fact]
    public void Lossless_trimming_never_makes_a_cursor_lost()
    {
        var h = NewHandle();
        h.Emit(Start("m1"));
        h.Emit(Delta("m1", "a"));
        h.Emit(End("m1"));
        Assert.Equal(CursorStatus.Continued, h.Snapshot(sinceSeq: 1).Cursor);
    }
}
