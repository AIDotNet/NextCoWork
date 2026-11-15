using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Llm;
using Ncw.Agent.Runs.Tests.Support;
using static Ncw.Agent.Runs.Tests.EventLogTests;

namespace Ncw.Agent.Runs.Tests;

/// <summary>
/// 订阅、事件泵、attach、积压溢出(05 §2、09 §8.2)。2.x 的 <c>agent-pump.test.ts</c> 里属于「运行侧」的那部分(合批窗口、结构性事件立即送、
/// 无人订阅时 Run 照跑、attach 的顺序、子 Run 继承订阅)在这里;窗口授权、structuredClone 属于 Host。
/// </summary>
public sealed class SubscriptionTests
{
    private static RunSubscription Sub(CollectingListener listener, ManualClock? clock = null, TimeSpan? coalesce = null, int maxBatch = 64) =>
        new(listener, new SubscriptionOptions { Coalesce = coalesce ?? TimeSpan.Zero, MaxBatch = maxBatch }, clock ?? new ManualClock(), new CollectingLog());

    private static RunHandle Handle(string id = "r1") => NewHandle(run: id);

    [Fact]
    public void Two_subscribers_both_receive_every_event()
    {
        var h = Handle();
        var a = new CollectingListener();
        var b = new CollectingListener();
        h.AddSubscriber(Sub(a));
        h.AddSubscriber(Sub(b));
        h.Emit(RunStart(h));
        h.Emit(new TurnStartEvent(0));
        Assert.Equal(2, a.Events().Count);
        Assert.Equal(2, b.Events().Count);
    }

    [Fact]
    public void A_disposed_subscription_receives_nothing_more()
    {
        var h = Handle();
        var a = new CollectingListener();
        var sub = Sub(a);
        h.AddSubscriber(sub);
        h.Emit(RunStart(h));
        sub.Dispose();
        h.Emit(new TurnStartEvent(0));
        Assert.Single(a.Events());
        Assert.Equal(0, h.SubscriberCount);
    }

    [Fact]
    public void Text_deltas_are_held_for_the_window_and_sent_as_one_batch()
    {
        var clock = new ManualClock();
        var h = Handle();
        var a = new CollectingListener();
        h.AddSubscriber(Sub(a, clock, TimeSpan.FromMilliseconds(16)));
        h.Emit(Start("m1"));
        a.Batches.ToList().Clear();
        var before = a.Deliveries;
        for (var i = 0; i < 10; i++)
        {
            h.Emit(Delta("m1", "x"));
        }

        Assert.Equal(before, a.Deliveries);
        clock.Advance(TimeSpan.FromMilliseconds(16));
        Assert.Equal(before + 1, a.Deliveries);
        Assert.Equal(11, a.Events().Count);
    }

    [Fact]
    public void A_structural_event_is_sent_at_once_together_with_the_deltas_before_it_in_order()
    {
        var clock = new ManualClock();
        var h = Handle();
        var a = new CollectingListener();
        h.AddSubscriber(Sub(a, clock, TimeSpan.FromMilliseconds(16)));
        h.Emit(Start("m1"));
        h.Emit(Delta("m1", "a"));
        h.Emit(Delta("m1", "b"));
        Assert.Single(a.Events()); // 只有 start 立即送出
        h.Emit(End("m1"));
        Assert.Equal([1L, 2L, 3L, 4L], a.Seqs());
        Assert.IsType<MessageEndEvent>(a.Events()[^1]);
    }

    [Fact]
    public void A_burst_larger_than_the_batch_cap_is_flushed_without_waiting_for_the_window()
    {
        var clock = new ManualClock();
        var h = Handle();
        var a = new CollectingListener();
        h.AddSubscriber(Sub(a, clock, TimeSpan.FromSeconds(10), maxBatch: 8));
        for (var i = 0; i < 8; i++)
        {
            h.Emit(Delta("m1", "x"));
        }

        Assert.Equal(8, a.Events().Count);
    }

    [Fact]
    public void Seq_inside_and_across_batches_is_strictly_increasing()
    {
        var clock = new ManualClock();
        var h = Handle();
        var a = new CollectingListener();
        h.AddSubscriber(Sub(a, clock, TimeSpan.FromMilliseconds(16)));
        for (var i = 0; i < 100; i++)
        {
            h.Emit(i % 7 == 0 ? new TurnStartEvent(i) : Delta("m1", "x"));
            if (i % 13 == 0)
            {
                clock.Advance(TimeSpan.FromMilliseconds(20));
            }
        }

        clock.Advance(TimeSpan.FromSeconds(1));
        var seqs = a.Seqs();
        Assert.Equal(100, seqs.Count);
        Assert.Equal(seqs.OrderBy(s => s), seqs);
        Assert.Equal(seqs.Distinct().Count(), seqs.Count);
    }

    [Fact]
    public void Nobody_listening_costs_nothing_and_the_log_is_still_the_truth()
    {
        var h = Handle();
        h.Emit(RunStart(h));
        h.Emit(Start("m1"));
        h.Emit(Delta("m1", "a"));
        var a = new CollectingListener();
        var snapshot = h.Attach(Sub(a), null, 0);
        Assert.Equal(3, snapshot.Events.Count);
        Assert.Empty(a.Events());
    }

    // ── 积压溢出 ──

    [Fact]
    public void When_the_listener_cannot_take_a_batch_of_pure_deltas_the_whole_batch_is_dropped_and_the_run_carries_on()
    {
        var h = Handle();
        var a = new CollectingListener { Accept = false };
        var sub = Sub(a);
        h.AddSubscriber(sub);
        h.Emit(Delta("m1", "lost"));
        Assert.Equal(1, sub.DroppedBatches);
        Assert.Equal(0, a.Resyncs);
        Assert.False(sub.IsClosed);
        a.Accept = true;
        h.Emit(Delta("m1", "kept"));
        Assert.Single(a.Events());
    }

    [Fact]
    public void A_structural_event_that_cannot_be_delivered_is_never_dropped_silently_the_client_is_told_to_resync()
    {
        var h = Handle();
        var a = new CollectingListener { Accept = false };
        var sub = Sub(a);
        h.AddSubscriber(sub);
        h.Emit(RunStart(h));
        Assert.Equal(1, a.Resyncs);
        Assert.True(sub.IsClosed);
        Assert.Equal(0, h.SubscriberCount);
        a.Accept = true;
        h.Emit(new TurnStartEvent(0));
        Assert.Empty(a.Events());
    }

    [Fact]
    public void A_listener_that_throws_does_not_affect_the_run_or_other_subscribers()
    {
        var h = Handle();
        var bad = new CollectingListener { ThrowOnDeliver = new InvalidOperationException("boom") };
        var good = new CollectingListener();
        h.AddSubscriber(Sub(bad));
        h.AddSubscriber(Sub(good));
        Assert.Equal(1, h.Emit(RunStart(h)));
        Assert.Equal(2, h.Emit(new TurnStartEvent(0)));
        Assert.Equal(2, good.Events().Count);
    }

    [Fact]
    public void After_run_settled_the_final_buffer_is_flushed_and_subscribers_are_released()
    {
        var clock = new ManualClock();
        var h = Handle();
        var a = new CollectingListener();
        h.AddSubscriber(Sub(a, clock, TimeSpan.FromSeconds(1)));
        h.Emit(RunStart(h));
        h.Emit(new RunEndEvent(RunStatus.Done));
        h.Emit(new RunSettledEvent(h.RunId, RunStatus.Done));
        Assert.IsType<RunSettledEvent>(a.Events()[^1]);
        Assert.Equal(0, h.SubscriberCount);
    }

    // ── attach 的原子性(★ 订阅顺序) ──

    [Fact]
    public async Task Attach_is_atomic_with_emission_nothing_is_lost_nothing_is_repeated()
    {
        for (var round = 0; round < 20; round++)
        {
            var h = Handle();
            var a = new CollectingListener();
            var sub = Sub(a);
            using var go = new ManualResetEventSlim();
            const int total = 600;
            var emitter = Task.Run(() =>
            {
                go.Wait();
                for (var i = 0; i < total; i++)
                {
                    h.Emit(new NotificationEvent("k", i.ToString()));
                }
            });
            go.Set();
            Thread.Sleep(round % 3);
            var snapshot = h.Attach(sub, null, 0);
            await emitter;

            var snapshotSeqs = snapshot.Events.Select(e => e.Seq).ToList();
            var pushed = a.Seqs();
            Assert.Empty(snapshotSeqs.Intersect(pushed));
            Assert.Equal(Enumerable.Range(1, total).Select(i => (long)i), snapshotSeqs.Concat(pushed).OrderBy(s => s));
            Assert.All(pushed, s => Assert.True(s > snapshot.Seq));
        }
    }

    [Fact]
    public void Attach_after_settle_returns_the_final_snapshot_and_follows_nothing()
    {
        var h = Handle();
        h.Emit(RunStart(h));
        h.Emit(new RunEndEvent(RunStatus.Done));
        h.Emit(new RunSettledEvent(h.RunId, RunStatus.Done));
        var a = new CollectingListener();
        var sub = Sub(a);
        var snapshot = h.Attach(sub, null, 0);
        Assert.True(snapshot.Settled);
        Assert.Equal(RunState.Done, snapshot.State);
        Assert.Equal(0, h.SubscriberCount);
    }

    // ── 注册表层:先订阅再启动、子 Run 继承、attach 含后代 ──

    [Fact]
    public async Task A_subscription_bound_at_launch_sees_the_run_from_its_first_event_to_run_settled()
    {
        using var rig = new Rig(o => o.Driver = _ => ScriptedDriver.Simple());
        rig.NewSessionRow();
        var a = new CollectingListener();
        var sub = rig.Registry.Subscribe(a, new SubscriptionOptions { Coalesce = TimeSpan.Zero });
        var run = rig.Start("s1", "hi", subscriptions: [sub]);
        await Rig.Settled(run);
        var types = a.Events().Select(e => e.GetType()).ToList();
        Assert.Equal([typeof(RunStartEvent), typeof(RunEndEvent), typeof(RunSettledEvent)], types);
    }

    [Fact]
    public async Task A_child_run_inherits_the_parents_subscriptions_before_it_starts_so_its_first_event_is_not_lost()
    {
        var release = new TaskCompletionSource();
        using var rig = new Rig(o => o.Driver = _ => new ScriptedDriver(async (handle, request, sink, ct) =>
        {
            await sink.EmitAsync(new RunStartEvent(handle.RunId, handle.SessionId, request.Model), CancellationToken.None);
            if (handle.ParentRunId is null)
            {
                await release.Task;
            }

            await sink.EmitAsync(new RunEndEvent(RunStatus.Done), CancellationToken.None);
            return ScriptedDriver.Done(handle);
        }));
        rig.NewSessionRow("parent");
        rig.NewSessionRow("child");
        var a = new CollectingListener();
        var sub = rig.Registry.Subscribe(a, new SubscriptionOptions { Coalesce = TimeSpan.Zero });
        var parent = rig.Start("parent", "go", runId: "p", subscriptions: [sub]);

        var launch = rig.Make("child", "sub-task", runId: "p:sub:1", origin: RunOrigin.Subagent) with { ParentRunId = new RunId("p"), ParentSessionId = "parent" };
        var child = Assert.IsType<LaunchResult.Started>(rig.Registry.Launch(launch)).Handle;
        await Rig.Settled(child);

        Assert.Equal([typeof(RunStartEvent), typeof(RunEndEvent), typeof(RunSettledEvent)], a.Events(new RunId("p:sub:1")).Select(e => e.GetType()));
        Assert.Equal(["p:sub:1"], rig.Registry.Get(new RunId("p"))!.Children.Select(c => c.Value));
        release.SetResult();
        await Rig.Settled(parent);
    }

    [Fact]
    public async Task Attach_includes_all_descendants_and_a_child_created_afterwards_still_follows()
    {
        var release = new TaskCompletionSource();
        using var rig = new Rig(o => o.Driver = _ => new ScriptedDriver(async (handle, request, sink, ct) =>
        {
            await sink.EmitAsync(new RunStartEvent(handle.RunId, handle.SessionId, request.Model), CancellationToken.None);
            await release.Task;
            await sink.EmitAsync(new RunEndEvent(RunStatus.Done), CancellationToken.None);
            return ScriptedDriver.Done(handle);
        }));
        foreach (var s in new[] { "s0", "s1", "s2" })
        {
            rig.NewSessionRow(s);
        }

        var root = rig.Start("s0", "root", runId: "root");
        var first = Assert.IsType<LaunchResult.Started>(rig.Registry.Launch(rig.Make("s1", "c1", runId: "c1", origin: RunOrigin.Subagent) with { ParentRunId = new RunId("root"), ParentSessionId = "s0" })).Handle;
        await Wait.UntilAsync(() => first.HasStarted);

        var a = new CollectingListener();
        var sub = rig.Registry.Subscribe(a, new SubscriptionOptions { Coalesce = TimeSpan.Zero });
        var attached = rig.Registry.Attach(new RunId("root"), null, sub);
        Assert.NotNull(attached);
        Assert.Equal(["c1"], attached.Descendants.Select(d => d.RunId.Value));

        var second = Assert.IsType<LaunchResult.Started>(rig.Registry.Launch(rig.Make("s2", "c2", runId: "c2", origin: RunOrigin.Subagent) with { ParentRunId = new RunId("c1"), ParentSessionId = "s1" })).Handle;
        await Wait.UntilAsync(() => a.Events(new RunId("c2")).Count > 0);
        Assert.IsType<RunStartEvent>(a.Events(new RunId("c2"))[0]);

        release.SetResult();
        await Rig.Settled(root);
        await Rig.Settled(first);
        await Rig.Settled(second);
    }

    [Fact]
    public void Attaching_to_a_run_that_does_not_exist_gives_null_not_an_empty_snapshot()
    {
        using var rig = new Rig();
        var sub = rig.Registry.Subscribe(new CollectingListener());
        Assert.Null(rig.Registry.Attach(new RunId("nope"), null, sub));
        Assert.Null(rig.Registry.SnapshotOf(new RunId("nope")));
    }

    [Fact]
    public async Task Post_adds_events_from_outside_the_loop_to_the_run_and_stops_at_the_terminal_state()
    {
        var release = new TaskCompletionSource();
        using var rig = new Rig(o => o.Driver = _ => new ScriptedDriver(async (handle, request, sink, ct) =>
        {
            await sink.EmitAsync(new RunStartEvent(handle.RunId, handle.SessionId, request.Model), CancellationToken.None);
            await release.Task;
            await sink.EmitAsync(new RunEndEvent(RunStatus.Done), CancellationToken.None);
            return ScriptedDriver.Done(handle);
        }));
        rig.NewSessionRow();
        var run = rig.Start("s1", "go", runId: "r");
        await Wait.UntilAsync(() => run.HasStarted);
        Assert.True(rig.Registry.Post(new RunId("r"), new NotificationEvent("interaction")) > 0);
        release.SetResult();
        await Rig.Settled(run);
        Assert.Equal(0, rig.Registry.Post(new RunId("r"), new NotificationEvent("late")));
        Assert.Equal(0, rig.Registry.Post(new RunId("ghost"), new NotificationEvent("x")));
    }
}
