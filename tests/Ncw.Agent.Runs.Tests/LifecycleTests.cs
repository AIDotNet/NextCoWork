using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Runs.Tests.Support;

namespace Ncw.Agent.Runs.Tests;

/// <summary>
/// Run 生命周期(05 §1):创建、运行、终态、<c>run_settled</c>、会话互斥、中止、看门狗、回收。用真的 <c>AgentSession</c> + 真 SQLite + 假上游 + 假工具;
/// 看门狗这类要「卡住」的场景换成脚本化的假驱动。移植自 2.x <c>run-registry.test.ts</c>(嵌套与中断 / 回收 / 删除前活体检查)、<c>agent-run.test.ts</c>、<c>agent-pump.test.ts</c> 的「run_end 到驱动收尾之间仍然挡住下一轮」。
/// </summary>
public sealed class LifecycleTests
{
    private static Ext BlockOnRunEnd(TaskCompletionSource entered, Task release) => new("block", 0, r =>
        r.On(LoopExtensionPoints.RunEnd, async (_, _, _) =>
        {
            entered.TrySetResult();
            await release;
        }));

    [Fact]
    public async Task A_run_goes_through_the_real_session_with_a_well_formed_event_stream_and_is_recorded()
    {
        using var rig = new Rig();
        rig.NewSessionRow();
        rig.Llm.Turn(Up.Text("hello"));
        var a = new CollectingListener();
        var sub = rig.Registry.Subscribe(a, new SubscriptionOptions { Coalesce = TimeSpan.Zero });
        var run = rig.Start("s1", "hi", runId: "r1", subscriptions: [sub]);
        await Rig.Settled(run);

        var events = a.Events();
        Assert.IsType<RunStartEvent>(events[0]);
        Assert.IsType<RunEndEvent>(events[^2]);
        Assert.IsType<RunSettledEvent>(events[^1]);
        Assert.Single(events.OfType<RunSettledEvent>()); // 会话层自己的 run_settled 被拦下,只有编排层发的那一个
        Assert.Equal(RunState.Done, run.State);
        Assert.Equal(a.Seqs(), a.Seqs().OrderBy(s => s));

        var stored = rig.Stored("s1");
        Assert.Equal(["hi", "hello"], stored.Select(m => m.Text));
        var record = rig.Repo.GetRunRecord("r1")!;
        Assert.Equal("done", record.Status);
        Assert.NotNull(record.EndedAt);
        Assert.True(record.EndedAt >= record.StartedAt);
    }

    [Fact]
    public async Task Run_settled_is_sent_only_after_everything_is_saved_and_the_session_mutex_is_free()
    {
        using var rig = new Rig();
        rig.NewSessionRow();
        (bool Busy, int Messages, string? Status, bool RunRecorded)? atSettled = null;
        (bool Busy, int Messages)? atEnd = null;
        var probe = new ProbeListener(ev =>
        {
            switch (ev)
            {
                case RunEndEvent:
                    atEnd = (rig.Registry.IsSessionBusy("s1"), rig.Repo.History("s1").Count);
                    break;
                case RunSettledEvent:
                    atSettled = (rig.Registry.IsSessionBusy("s1"), rig.Repo.History("s1").Count, rig.Repo.GetRunRecord("r1")?.Status, rig.Repo.GetRunRecord("r1")?.EndedAt is not null);
                    break;
            }
        });
        var sub = rig.Registry.Subscribe(probe, new SubscriptionOptions { Coalesce = TimeSpan.Zero });
        var run = rig.Start("s1", "hi", runId: "r1", subscriptions: [sub]);
        await Rig.Settled(run);

        Assert.True(atEnd!.Value.Busy); // run_end 的那一刻互斥还攥着(R8)
        Assert.False(atSettled!.Value.Busy);
        Assert.Equal(2, atSettled.Value.Messages);
        Assert.Equal("done", atSettled.Value.Status);
        Assert.True(atSettled.Value.RunRecorded);
        // 收到 run_settled 之后立刻就能开下一个 Run。
        Assert.IsType<LaunchResult.Started>(rig.Registry.Launch(rig.Make("s1", "again")));
    }

    [Fact]
    public async Task Between_run_end_and_run_settled_the_session_stays_blocked_and_the_broadcast_already_says_the_run_is_over()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        using var rig = new Rig(o => o.SessionExtensions = [BlockOnRunEnd(entered, release.Task)]);
        rig.NewSessionRow();
        var run = rig.Start("s1", "hi", runId: "r1");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(RunState.Done, run.State);
        Assert.False(run.IsSettled);
        Assert.True(rig.Registry.IsSessionBusy("s1"));
        var refused = Assert.IsType<LaunchResult.Refused>(rig.Registry.Launch(rig.Make("s1", "too early")));
        Assert.Equal(RunRefusal.SessionBusy, refused.Reason);
        Assert.Empty(rig.Registry.ActiveRuns().Runs); // 「谁在跑」已经不含它
        Assert.IsType<SendResult.Refused>(await rig.Registry.SendAsync(new UserSend(new RunId("r2"), "s1", Rig.Workspace, new MessageId("u9"), [new TextPart("x")], Opts.Default)));

        release.SetResult();
        await Rig.Settled(run);
        Assert.False(rig.Registry.IsSessionBusy("s1"));
    }

    [Fact]
    public async Task A_duplicate_run_id_is_refused_silently_reusing_it_would_make_two_runs_share_a_transcript()
    {
        using var rig = new Rig();
        rig.NewSessionRow("s1");
        rig.NewSessionRow("s2");
        await rig.RunAsync("s1", "a", runId: "same");
        var result = Assert.IsType<LaunchResult.Refused>(rig.Registry.Launch(rig.Make("s2", "b", runId: "same")));
        Assert.Equal(RunRefusal.DuplicateRunId, result.Reason);
    }

    [Fact]
    public async Task One_session_cannot_run_two_runs_but_other_sessions_are_unaffected()
    {
        var gate = new TaskCompletionSource();
        using var rig = new Rig();
        rig.NewSessionRow("s1");
        rig.NewSessionRow("s2");
        rig.Llm.Route = ctx => ctx.SessionId == "s1" ? Up.Gate(gate.Task) : null;
        var first = rig.Start("s1", "a", runId: "r1");
        var second = Assert.IsType<LaunchResult.Refused>(rig.Registry.Launch(rig.Make("s1", "b", runId: "r2")));
        Assert.Equal(RunRefusal.SessionBusy, second.Reason);
        var other = rig.Start("s2", "c", runId: "r3");
        gate.SetResult();
        await Rig.Settled(first);
        await Rig.Settled(other);
        Assert.Null(rig.Registry.Get(new RunId("r2")));
    }

    [Fact]
    public async Task Concurrent_runs_in_different_sessions_do_not_cross_streams()
    {
        var gate = new TaskCompletionSource();
        using var rig = new Rig();
        for (var i = 0; i < 4; i++)
        {
            rig.NewSessionRow($"s{i}");
            rig.Llm.Turn(Up.Gate(gate.Task, "reply"));
        }

        var listeners = new List<CollectingListener>();
        var runs = new List<RunHandle>();
        for (var i = 0; i < 4; i++)
        {
            var listener = new CollectingListener();
            listeners.Add(listener);
            var sub = rig.Registry.Subscribe(listener, new SubscriptionOptions { Coalesce = TimeSpan.Zero });
            runs.Add(rig.Start($"s{i}", $"q{i}", runId: $"r{i}", subscriptions: [sub]));
        }

        await Wait.UntilAsync(() => rig.Registry.ActiveRuns().Runs.Length == 4, what: "four runs active");
        gate.SetResult();
        foreach (var run in runs)
        {
            await Rig.Settled(run);
        }

        for (var i = 0; i < 4; i++)
        {
            Assert.All(listeners[i].Batches, b => Assert.Equal($"r{i}", b.RunId.Value));
            Assert.Equal(listeners[i].Seqs(), listeners[i].Seqs().OrderBy(s => s));
            Assert.Equal([$"q{i}", "reply"], rig.Stored($"s{i}").Select(m => m.Text));
        }
    }

    [Fact]
    public async Task Aborting_while_a_tool_runs_ends_aborted_pairs_the_call_and_frees_the_session()
    {
        var started = new TaskCompletionSource();
        using var rig = new Rig();
        rig.NewSessionRow();
        rig.Tools.Add(new FakeTool("slow", async (_, _, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return ToolResult.Text("never");
        }));
        rig.Llm.Turn(Up.Call("c1", "slow"));
        var a = new CollectingListener();
        var sub = rig.Registry.Subscribe(a, new SubscriptionOptions { Coalesce = TimeSpan.Zero });
        var run = rig.Start("s1", "go", runId: "r1", subscriptions: [sub]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        rig.Registry.Abort(new RunId("r1"));
        await Rig.Settled(run);

        Assert.Equal(RunState.Aborted, run.State);
        Assert.Equal(RunStatus.Aborted, a.Events().OfType<RunEndEvent>().Single().Status);
        Assert.IsType<RunSettledEvent>(a.Events()[^1]);
        Assert.Equal("aborted", rig.Repo.GetRunRecord("r1")!.Status);
        // 每个 tool_call 都配得上一个 tool_result(否则下一次请求就是 400)。
        var parts = rig.Repo.History("s1").Select(r => r.PartsJson).ToList();
        Assert.Contains(parts, p => p.Contains("\"tool_call\"", StringComparison.Ordinal) || p.Contains("tool_use", StringComparison.Ordinal) || p.Contains("\"callId\"", StringComparison.Ordinal));
        Assert.Contains(parts, p => p.Contains("tool_result", StringComparison.Ordinal));
        // 会话可以继续。
        rig.Llm.Turn(Up.Text("fine"));
        await rig.RunAsync("s1", "again");
        Assert.Equal("fine", rig.Stored("s1")[^1].Text);
    }

    [Fact]
    public async Task Abort_cascades_to_the_whole_subtree_unless_told_not_to()
    {
        using var rig = new Rig(o => o.Driver = _ => new ScriptedDriver(async (handle, request, sink, ct) =>
        {
            await sink.EmitAsync(new RunStartEvent(handle.RunId, handle.SessionId, request.Model), CancellationToken.None);
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }

            await sink.EmitAsync(new RunEndEvent(RunStatus.Aborted), CancellationToken.None);
            return ScriptedDriver.Done(handle, RunStatus.Aborted);
        }));
        foreach (var s in new[] { "p", "c", "g", "p2", "c2" })
        {
            rig.NewSessionRow(s);
        }

        var parent = rig.Start("p", "x", runId: "p");
        var child = Assert.IsType<LaunchResult.Started>(rig.Registry.Launch(rig.Make("c", "x", runId: "c", origin: RunOrigin.Subagent) with { ParentRunId = new RunId("p"), ParentSessionId = "p" })).Handle;
        var grand = Assert.IsType<LaunchResult.Started>(rig.Registry.Launch(rig.Make("g", "x", runId: "g", origin: RunOrigin.Subagent) with { ParentRunId = new RunId("c"), ParentSessionId = "c" })).Handle;
        rig.Registry.Abort(new RunId("p"), cascade: true);
        await Rig.Settled(parent);
        await Rig.Settled(child);
        await Rig.Settled(grand);
        Assert.All(new[] { parent, child, grand }, h => Assert.Equal(RunState.Aborted, h.State));

        var p2 = rig.Start("p2", "x", runId: "p2");
        var c2 = Assert.IsType<LaunchResult.Started>(rig.Registry.Launch(rig.Make("c2", "x", runId: "c2", origin: RunOrigin.Subagent) with { ParentRunId = new RunId("p2"), ParentSessionId = "p2" })).Handle;
        rig.Registry.Abort(new RunId("p2"), cascade: false);
        await Rig.Settled(p2);
        Assert.Equal(RunState.Running, c2.State);
        rig.Registry.Abort(new RunId("c2"));
        await Rig.Settled(c2);
    }

    [Fact]
    public void Aborting_a_run_that_does_not_exist_does_not_throw()
    {
        using var rig = new Rig();
        rig.Registry.Abort(new RunId("ghost"));
        rig.Registry.AbortAll();
    }

    // ── 看门狗(#187) ──

    [Fact]
    public async Task A_driver_that_ignores_cancellation_is_ended_by_the_watchdog_after_five_seconds()
    {
        var wake = new TaskCompletionSource();
        var hold2 = new TaskCompletionSource();
        var lateDone = new TaskCompletionSource();
        var calls = new RecordingExtension();
        using var rig = new Rig(o =>
        {
            o.ManualClock = true;
            o.RunExtensions = [calls];
            o.Driver = _ => new ScriptedDriver(async (handle, request, sink, ct) =>
            {
                await sink.EmitAsync(new RunStartEvent(handle.RunId, handle.SessionId, request.Model), CancellationToken.None);
                if (handle.RunId.Value == "r1")
                {
                    await wake.Task; // 不认取消令牌
                    // 醒过来之后继续「写」:这些必须被丢弃。
                    await sink.EmitAsync(new TurnStartEvent(99), CancellationToken.None);
                    await sink.EmitAsync(new RunEndEvent(RunStatus.Done), CancellationToken.None);
                    lateDone.SetResult();
                    return ScriptedDriver.Done(handle);
                }

                await hold2.Task;
                await sink.EmitAsync(new RunEndEvent(RunStatus.Done), CancellationToken.None);
                return ScriptedDriver.Done(handle);
            });
        });
        rig.NewSessionRow();
        var a = new CollectingListener();
        var sub = rig.Registry.Subscribe(a, new SubscriptionOptions { Coalesce = TimeSpan.Zero });
        var run = rig.Start("s1", "hi", runId: "r1", subscriptions: [sub]);
        await Wait.UntilAsync(() => run.HasStarted);
        rig.Registry.Abort(new RunId("r1"));

        rig.Manual!.Advance(TimeSpan.FromMilliseconds(4900));
        Assert.Equal(RunState.Running, run.State);
        Assert.True(rig.Registry.IsSessionBusy("s1"));

        rig.Manual.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(RunState.Aborted, run.State);
        Assert.True(run.Abandoned);
        Assert.True(run.IsSettled);
        Assert.False(rig.Registry.IsSessionBusy("s1"));
        Assert.Equal("run.abandoned", run.Error!.MessageKey);
        Assert.Equal("aborted", rig.Repo.GetRunRecord("r1")!.Status);
        Assert.IsType<RunSettledEvent>(a.Events()[^1]);

        // 下一个 Run 已经可以开始;驱动哪天醒过来:它的事件被丢弃,不会再放一次别人的锁、不会再 settle 一次。
        var next = Assert.IsType<LaunchResult.Started>(rig.Registry.Launch(rig.Make("s1", "next", runId: "r2"))).Handle;
        await Wait.UntilAsync(() => next.HasStarted);
        wake.SetResult();
        await lateDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await run.Completion!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(a.Events().OfType<TurnStartEvent>());
        Assert.Single(a.Events().OfType<RunEndEvent>());
        Assert.Single(a.Events().OfType<RunSettledEvent>());
        Assert.Single(calls.Calls, c => c == "settled:r1");
        Assert.True(rig.Registry.IsSessionBusy("s1")); // 属于 r2 的互斥没有被 r1 的迟到收尾放掉
        hold2.SetResult();
        await Rig.Settled(next);
    }

    [Fact]
    public async Task A_run_that_stops_in_time_is_not_touched_by_the_watchdog()
    {
        using var rig = new Rig(o =>
        {
            o.ManualClock = true;
            o.Driver = _ => new ScriptedDriver(async (handle, request, sink, ct) =>
            {
                await sink.EmitAsync(new RunStartEvent(handle.RunId, handle.SessionId, request.Model), CancellationToken.None);
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                }

                await sink.EmitAsync(new RunEndEvent(RunStatus.Aborted), CancellationToken.None);
                return ScriptedDriver.Done(handle, RunStatus.Aborted);
            });
        });
        rig.NewSessionRow();
        var run = rig.Start("s1", "hi", runId: "r1");
        await Wait.UntilAsync(() => run.HasStarted);
        rig.Registry.Abort(new RunId("r1"));
        await Rig.Settled(run);
        Assert.False(run.Abandoned);
        Assert.Equal(2, rig.Manual!.PendingTimers); // 只剩回收和重播两个周期定时器,看门狗已摘
        rig.Manual.Advance(TimeSpan.FromSeconds(10));
        Assert.False(rig.Log.Any("forcing it to end"));
    }

    [Fact]
    public async Task A_run_whose_run_end_is_already_in_the_log_is_not_force_settled_its_driver_is_still_saving()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        using var rig = new Rig(o =>
        {
            o.ManualClock = true;
            o.SessionExtensions = [BlockOnRunEnd(entered, release.Task)];
        });
        rig.NewSessionRow();
        var run = rig.Start("s1", "hi", runId: "r1");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Registry.Abort(new RunId("r1")); // 终态之后按停止:无效
        rig.Manual!.Advance(TimeSpan.FromSeconds(30));
        Assert.False(run.Abandoned);
        Assert.False(run.IsSettled);
        release.SetResult();
        await Rig.Settled(run);
    }

    // ── 驱动异常与没开始的 Run ──

    [Fact]
    public async Task A_driver_that_throws_becomes_an_error_run_and_still_settles()
    {
        using var rig = new Rig(o => o.Driver = _ => new ScriptedDriver((_, _, _, _) => throw new InvalidOperationException("boom")));
        rig.NewSessionRow();
        var run = await rig.RunAsync("s1", "hi", runId: "r1");
        Assert.Equal(RunState.Error, run.State);
        Assert.Equal("run.internal", run.Error!.MessageKey);
        Assert.True(run.IsSettled);
        Assert.False(rig.Registry.IsSessionBusy("s1"));
        Assert.Equal([typeof(RunStartEvent), typeof(RunEndEvent), typeof(RunSettledEvent)], run.Since(0).Select(e => e.Event.GetType()));
    }

    [Fact]
    public async Task A_driver_that_returns_without_a_run_end_is_closed_with_its_outcome()
    {
        using var rig = new Rig(o => o.Driver = _ => new ScriptedDriver((h, _, _, _) => Task.FromResult(ScriptedDriver.Done(h, RunStatus.Aborted))));
        rig.NewSessionRow();
        var run = await rig.RunAsync("s1", "hi", runId: "r1");
        Assert.Equal(RunState.Aborted, run.State);
    }

    [Fact]
    public async Task A_run_for_a_session_that_does_not_exist_ends_in_error_with_a_self_consistent_stream()
    {
        using var rig = new Rig();
        var run = await rig.RunAsync("ghost", "hi", runId: "r1");
        Assert.Equal(RunState.Error, run.State);
        Assert.Equal("session.notFound", run.Error!.MessageKey);
        Assert.Equal([typeof(RunStartEvent), typeof(RunEndEvent), typeof(RunSettledEvent)], run.Since(0).Select(e => e.Event.GetType()));
        Assert.False(rig.Registry.IsSessionBusy("ghost"));
    }

    [Fact]
    public async Task A_run_the_session_layer_refuses_as_busy_ends_in_error_and_never_blocks_the_registry()
    {
        // 会话层自己也有一把锁(手动压缩等操作直接拿它)。注册表的互斥之外这把锁还在 → session.busy。
        var gate = new TaskCompletionSource();
        using var rig = new Rig();
        rig.NewSessionRow();
        rig.Llm.Turn(Up.Gate(gate.Task));
        var session = rig.Sessions.Get("s1", Rig.Workspace);
        var direct = session.RunAsync(new RunRequest(new RunId("direct"), [Msg.User("ud", "x")], rig.Model), new ListRunSink());
        await Wait.UntilAsync(() => session.IsBusy);

        var run = await rig.RunAsync("s1", "hi", runId: "r1");
        Assert.Equal(RunState.Error, run.State);
        Assert.Equal("session.busy", run.Error!.MessageKey);
        Assert.False(rig.Registry.IsSessionBusy("s1"));
        gate.SetResult();
        await direct;
    }

    // ── 扩展点 ──

    [Fact]
    public async Task Extensions_see_the_lifecycle_in_order_and_one_that_throws_cannot_break_it()
    {
        var calls = new RecordingExtension();
        using var rig = new Rig(o => o.RunExtensions = [new ThrowingExtension(), calls]);
        rig.NewSessionRow();
        var run = await rig.RunAsync("s1", "hi", runId: "r1");
        Assert.Equal(RunState.Done, run.State);
        Assert.Equal(["created:r1", "ended:r1:Done", "settled:r1"], calls.Calls.Where(c => !c.StartsWith("enrich", StringComparison.Ordinal)));
        Assert.True(rig.Log.Any("A run extension threw"));
    }

    [Fact]
    public async Task The_tool_context_is_enriched_by_extensions_on_top_of_the_session_layer()
    {
        ToolContext? seen = null;
        using var rig = new Rig(o => o.RunExtensions = [new MarkingExtension()]);
        rig.NewSessionRow();
        rig.Tools.Add(new FakeTool("peek", (_, ctx, _) =>
        {
            seen = ctx;
            return Task.FromResult(ToolResult.Text("ok"));
        }));
        rig.Llm.Turn(Up.Call("c1", "peek"), Up.Text("done"));
        await rig.RunAsync("s1", "hi", runId: "r1");
        Assert.IsType<MarkedContext>(seen);
        Assert.Equal("r1", ((MarkedContext)seen).Run);
        Assert.IsAssignableFrom<SessionToolContext>(((MarkedContext)seen).Inner);
    }

    private sealed class ThrowingExtension : IRunExtension
    {
        public void OnCreated(RunHandle run) => throw new InvalidOperationException("x");

        public ToolContext EnrichToolContext(RunHandle run, ToolContext context) => throw new InvalidOperationException("x");

        public void OnEnded(RunHandle run, RunStatus status, AgentError? error) => throw new InvalidOperationException("x");

        public void OnSettled(RunHandle run) => throw new InvalidOperationException("x");
    }

    internal sealed record MarkedContext : ToolContext
    {
        public MarkedContext(ToolContext inner, string run)
            : base(inner)
        {
            Inner = inner;
            Run = run;
        }

        public ToolContext Inner { get; }

        public string Run { get; }
    }

    private sealed class MarkingExtension : IRunExtension
    {
        public ToolContext EnrichToolContext(RunHandle run, ToolContext context) => new MarkedContext(context, run.RunId.Value);
    }
}
