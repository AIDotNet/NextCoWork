using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Runs.Tests.Support;

namespace Ncw.Agent.Runs.Tests;

/// <summary>扩展点的剩余几条、终态单调在注册表层的表现、并发下的不死锁 / 不串台。</summary>
public sealed class RobustnessTests
{
    [Fact]
    public async Task Extensions_are_told_when_an_abort_is_requested_so_a_goal_layer_can_pause_the_goal()
    {
        var calls = new RecordingExtension();
        using var rig = new Rig(o =>
        {
            o.RunExtensions = [calls];
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
        var run = rig.Start("s1", "x", runId: "r1");
        await Wait.UntilAsync(() => run.HasStarted);
        rig.Registry.Abort(run.RunId);
        rig.Registry.Abort(run.RunId); // 幂等:只通知一次
        await Rig.Settled(run);
        Assert.Single(calls.Calls, c => c == "abort:r1");
    }

    [Fact]
    public async Task A_replayed_run_start_posted_after_the_terminal_state_changes_nothing_in_the_registry()
    {
        using var rig = new Rig(o => o.Driver = _ => ScriptedDriver.Simple());
        rig.NewSessionRow();
        var run = await rig.RunAsync("s1", "x", runId: "r1");
        var seq = run.Seq;
        Assert.Equal(0, rig.Registry.Post(run.RunId, new RunStartEvent(run.RunId, "s1", ModelRef())));
        Assert.Equal(seq, run.Seq);
        Assert.Equal(RunState.Done, run.State);
        Assert.Empty(rig.Registry.ActiveRuns().Runs);
    }

    private static Ncw.Agent.Llm.ModelRef ModelRef() => Ncw.Agent.Llm.ModelRef.Unpinned("m");

    [Fact]
    public async Task Attach_and_snapshot_carry_the_pending_interaction_count_taken_outside_the_registry_lock()
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
        var run = rig.Start("s1", "x", runId: "r1");
        rig.Interactions.Set(new RunId("r1"), 3);
        var sub = rig.Registry.Subscribe(new CollectingListener());
        Assert.Equal(3, rig.Registry.Attach(run.RunId, null, sub)!.Root.PendingInteractions);
        Assert.Equal(3, rig.Registry.SnapshotOf(run.RunId)!.PendingInteractions);
        release.SetResult();
        await Rig.Settled(run);
    }

    [Fact]
    public async Task Many_concurrent_runs_subscriptions_aborts_and_attaches_neither_deadlock_nor_cross_streams()
    {
        const int n = 40;
        using var rig = new Rig(o => o.Driver = _ => new ScriptedDriver(async (handle, request, sink, ct) =>
        {
            await sink.EmitAsync(new RunStartEvent(handle.RunId, handle.SessionId, request.Model), CancellationToken.None);
            for (var i = 0; i < 20; i++)
            {
                await sink.EmitAsync(new NotificationEvent("tick", handle.RunId.Value), CancellationToken.None);
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                await Task.Yield();
            }

            await sink.EmitAsync(new RunEndEvent(ct.IsCancellationRequested ? RunStatus.Aborted : RunStatus.Done), CancellationToken.None);
            return ScriptedDriver.Done(handle);
        }));
        for (var i = 0; i < n; i++)
        {
            rig.NewSessionRow($"s{i}");
        }

        var listeners = new List<CollectingListener>();
        var tasks = Enumerable.Range(0, n).Select(i => Task.Run(async () =>
        {
            var listener = new CollectingListener();
            lock (listeners)
            {
                listeners.Add(listener);
            }

            var sub = rig.Registry.Subscribe(listener, new SubscriptionOptions { Coalesce = TimeSpan.FromMilliseconds(1) });
            var run = rig.Start($"s{i}", "x", runId: $"r{i}", subscriptions: [sub]);
            if (i % 3 == 0)
            {
                rig.Registry.Abort(run.RunId);
            }

            var late = rig.Registry.Subscribe(new CollectingListener());
            _ = rig.Registry.Attach(run.RunId, null, late);
            await Rig.Settled(run);
            return run;
        })).ToList();
        var runs = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.All(runs, r => Assert.True(r.IsSettled));
        foreach (var listener in listeners)
        {
            Assert.Single(listener.Batches.Select(b => b.RunId).Distinct());
            Assert.Equal(listener.Seqs(), listener.Seqs().OrderBy(s => s));
            Assert.IsType<RunSettledEvent>(listener.Events()[^1]);
        }

        Assert.Empty(rig.Registry.ActiveRuns().Runs);
        Assert.All(Enumerable.Range(0, n), i => Assert.False(rig.Registry.IsSessionBusy($"s{i}")));
    }

    [Fact]
    public async Task The_open_gateway_gets_a_refusal_when_the_session_is_busy_it_is_never_queued()
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
        var run = rig.Start("s1", "x", runId: "r1");
        var gateway = await rig.Registry.SendAsync(new UserSend(new RunId("g1"), "s1", Rig.Workspace, new Ncw.Agent.Llm.MessageId("gm"), [new Ncw.Agent.Llm.TextPart("hi")], Opts.Default) { Origin = RunOrigin.Gateway });
        Assert.Equal(RunRefusal.SessionBusy, Assert.IsType<SendResult.Refused>(gateway).Reason);
        Assert.Empty(rig.Registry.QueueOf("s1").Items);
        var user = await rig.Registry.SendAsync(new UserSend(new RunId("u1"), "s1", Rig.Workspace, new Ncw.Agent.Llm.MessageId("um"), [new Ncw.Agent.Llm.TextPart("hi")], Opts.Default));
        Assert.IsType<SendResult.Queued>(user); // 用户发送:同样的情形进队列
        release.SetResult();
        await Rig.Settled(run);
    }
}
