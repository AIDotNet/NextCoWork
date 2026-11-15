using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Runs.Tests.Support;
using Ncw.Core.Domain;

namespace Ncw.Agent.Runs.Tests;

/// <summary>
/// <c>activeRuns</c> 全量权威广播(05 §2,状态统一管理规则 5)。移植自 2.x <c>active-run-broadcast.test.ts</c>(没订阅过的窗口也收得到起止、子代理在自己那半边、不让监听器钉住 Run)
/// 和渲染层 <c>active-run-sync.test.ts</c> 的收敛场景(丢了结束事件,靠这条广播收口)。
/// </summary>
public sealed class BroadcastTests
{
    private static ScriptedDriver Waiting(TaskCompletionSource release) => new(async (handle, request, sink, ct) =>
    {
        await sink.EmitAsync(new RunStartEvent(handle.RunId, handle.SessionId, request.Model), CancellationToken.None);
        await release.Task;
        await sink.EmitAsync(new RunEndEvent(RunStatus.Done), CancellationToken.None);
        return ScriptedDriver.Done(handle);
    });

    private static RunLaunch Child(Rig rig, string session, string run, string parentRun, string parentSession) =>
        rig.Make(session, "task", runId: run, origin: RunOrigin.Subagent) with { ParentRunId = new RunId(parentRun), ParentSessionId = parentSession };

    [Fact]
    public async Task Start_and_end_are_broadcast_to_a_listener_that_never_subscribed_to_the_run()
    {
        var release = new TaskCompletionSource();
        using var rig = new Rig(o => o.Driver = _ => Waiting(release));
        rig.NewSessionRow();
        var run = rig.Start("s1", "x", runId: "r1");
        Assert.Equal(["r1"], rig.Broadcasts[^1].Runs.Select(r => r.RunId));
        Assert.Equal("s1", rig.Broadcasts[^1].Runs[0].SessionId);
        Assert.Equal(Rig.Workspace, rig.Broadcasts[^1].Runs[0].WorkspaceId);
        Assert.Equal("dev-1", rig.Broadcasts[^1].DeviceId);

        release.SetResult();
        await Rig.Settled(run);
        Assert.Empty(rig.Broadcasts[^1].Runs);
    }

    [Fact]
    public async Task The_broadcast_is_sent_at_run_end_not_only_at_settle_and_every_broadcast_is_the_full_set()
    {
        var release = new TaskCompletionSource();
        using var rig = new Rig(o => o.Driver = _ => Waiting(release));
        rig.NewSessionRow("a");
        rig.NewSessionRow("b");
        var a = rig.Start("a", "x", runId: "ra");
        var b = rig.Start("b", "x", runId: "rb");
        Assert.Equal(["ra", "rb"], rig.Broadcasts[^1].Runs.Select(r => r.RunId).Order());
        release.SetResult();
        await Rig.Settled(a);
        await Rig.Settled(b);
        Assert.All(rig.Broadcasts, index => Assert.Equal("dev-1", index.DeviceId));
        Assert.Empty(rig.Broadcasts[^1].Runs);
    }

    [Fact]
    public async Task Subagents_are_broadcast_in_their_own_half_under_the_parent_session_and_never_in_the_top_level_set()
    {
        var release = new TaskCompletionSource();
        using var rig = new Rig(o => o.Driver = _ => Waiting(release));
        rig.NewSessionRow("p");
        rig.NewSessionRow("c");
        var parent = rig.Start("p", "x", runId: "p");
        var child = Assert.IsType<LaunchResult.Started>(rig.Registry.Launch(Child(rig, "c", "p:sub:1", "p", "p"))).Handle;

        var last = rig.Broadcasts[^1];
        Assert.Equal(["p"], last.Runs.Select(r => r.RunId));
        var sub = Assert.Single(last.Subagents);
        Assert.Equal("p:sub:1", sub.RunId);
        Assert.Equal("p", sub.ParentRunId);
        Assert.Equal("p", sub.SessionId); // 父会话,不是子会话 "c"

        release.SetResult();
        await Rig.Settled(parent);
        await Rig.Settled(child);
        Assert.Empty(rig.Broadcasts[^1].Subagents);
    }

    [Fact]
    public async Task A_child_ending_alone_is_broadcast_so_its_card_cannot_stay_running_forever()
    {
        var releaseChild = new TaskCompletionSource();
        var releaseParent = new TaskCompletionSource();
        using var rig = new Rig(o => o.Driver = _ => new ScriptedDriver(async (handle, request, sink, ct) =>
        {
            await sink.EmitAsync(new RunStartEvent(handle.RunId, handle.SessionId, request.Model), CancellationToken.None);
            await (handle.ParentRunId is null ? releaseParent.Task : releaseChild.Task);
            await sink.EmitAsync(new RunEndEvent(RunStatus.Done), CancellationToken.None);
            return ScriptedDriver.Done(handle);
        }));
        rig.NewSessionRow("p");
        rig.NewSessionRow("c");
        var parent = rig.Start("p", "x", runId: "p");
        var child = Assert.IsType<LaunchResult.Started>(rig.Registry.Launch(Child(rig, "c", "c1", "p", "p"))).Handle;
        releaseChild.SetResult();
        await Rig.Settled(child);
        Assert.Empty(rig.Broadcasts[^1].Subagents);
        Assert.Equal(["p"], rig.Broadcasts[^1].Runs.Select(r => r.RunId));
        releaseParent.SetResult();
        await Rig.Settled(parent);
    }

    [Fact]
    public async Task Pending_interactions_of_all_descendants_count_towards_the_top_level_run_and_changes_are_re_broadcast()
    {
        var release = new TaskCompletionSource();
        using var rig = new Rig(o => o.Driver = _ => Waiting(release));
        rig.NewSessionRow("p");
        rig.NewSessionRow("c");
        rig.NewSessionRow("g");
        var parent = rig.Start("p", "x", runId: "p");
        var child = Assert.IsType<LaunchResult.Started>(rig.Registry.Launch(Child(rig, "c", "c1", "p", "p"))).Handle;
        var grand = Assert.IsType<LaunchResult.Started>(rig.Registry.Launch(Child(rig, "g", "g1", "c1", "c"))).Handle;
        Assert.Equal(0, rig.Broadcasts[^1].Runs[0].PendingInteractions);

        var before = rig.Broadcasts.Count;
        rig.Interactions.Set(new RunId("p"), 1);
        rig.Interactions.Set(new RunId("c1"), 2);
        rig.Interactions.Set(new RunId("g1"), 1);
        rig.Registry.NotifyInteractionsChanged();
        Assert.Equal(before + 1, rig.Broadcasts.Count);
        Assert.Equal(4, rig.Broadcasts[^1].Runs[0].PendingInteractions);

        rig.Interactions.Set(new RunId("g1"), 0);
        rig.Registry.NotifyInteractionsChanged();
        Assert.Equal(3, rig.Broadcasts[^1].Runs[0].PendingInteractions);

        release.SetResult();
        await Rig.Settled(parent);
        await Rig.Settled(child);
        await Rig.Settled(grand);
    }

    [Fact]
    public async Task The_first_frame_and_the_latest_broadcast_come_from_the_same_function_and_have_the_same_shape()
    {
        var release = new TaskCompletionSource();
        using var rig = new Rig(o => o.Driver = _ => Waiting(release));
        rig.NewSessionRow("p");
        rig.NewSessionRow("c");
        Assert.Empty(rig.Registry.ActiveRuns().Runs);
        var parent = rig.Start("p", "x", runId: "p");
        var child = Assert.IsType<LaunchResult.Started>(rig.Registry.Launch(Child(rig, "c", "c1", "p", "p"))).Handle;

        var first = rig.Registry.ActiveRuns();
        var latest = rig.Broadcasts[^1];
        Assert.Equal(latest.Rev, first.Rev);
        Assert.Equal(latest.Runs.Select(r => r.RunId), first.Runs.Select(r => r.RunId));
        Assert.Equal(latest.Subagents.Select(s => s.RunId), first.Subagents.Select(s => s.RunId));
        Assert.IsType<ActiveRunIndex>(first);

        release.SetResult();
        await Rig.Settled(parent);
        await Rig.Settled(child);
    }

    [Fact]
    public async Task Revisions_are_strictly_increasing_even_when_many_runs_start_and_end_concurrently()
    {
        using var rig = new Rig(o => o.Driver = _ => ScriptedDriver.Simple(async _ => await Task.Yield()));
        const int n = 24;
        for (var i = 0; i < n; i++)
        {
            rig.NewSessionRow($"s{i}");
        }

        await Task.WhenAll(Enumerable.Range(0, n).Select(i => Task.Run(async () => await rig.RunAsync($"s{i}", "x", runId: $"r{i}"))));
        var revs = rig.Broadcasts.Select(b => b.Rev).ToList();
        Assert.Equal(revs.OrderBy(r => r), revs);
        Assert.Equal(revs.Distinct().Count(), revs.Count);
        Assert.True(revs.Count >= 2 * n);
        Assert.Empty(rig.Broadcasts[^1].Runs);
        Assert.Empty(rig.Registry.ActiveRuns().Runs);
    }

    [Fact]
    public async Task A_subscriber_that_throws_cannot_break_the_registry_or_the_other_subscribers()
    {
        using var rig = new Rig(o => o.Driver = _ => ScriptedDriver.Simple());
        rig.NewSessionRow();
        var seen = 0;
        rig.Registry.ActiveRunsChanged += _ => throw new InvalidOperationException("boom");
        rig.Registry.ActiveRunsChanged += _ => seen++;
        var run = await rig.RunAsync("s1", "x");
        Assert.Equal(RunState.Done, run.State);
        Assert.True(seen >= 2);
        Assert.True(rig.Log.Any("subscriber threw"));
    }

    [Fact]
    public async Task A_listener_does_not_pin_a_finished_run_the_broadcast_hook_must_not_keep_it_alive()
    {
        using var rig = new Rig(o =>
        {
            o.ManualClock = true;
            o.Driver = _ => ScriptedDriver.Simple();
        });
        rig.NewSessionRow();
        var run = await rig.RunAsync("s1", "x", runId: "r1");
        Assert.Equal(0, run.SubscriberCount);
        Assert.Equal(1, rig.Registry.ReapFinished(run.EndedAt!.Value.AddMinutes(5)));
    }

    // ── 收敛:丢了结束事件,靠全量广播收口(规则 5、7) ──

    [Fact]
    public async Task A_client_that_lost_run_end_is_converged_by_the_next_broadcast()
    {
        var release = new TaskCompletionSource();
        using var rig = new Rig(o => o.Driver = _ => Waiting(release));
        rig.NewSessionRow();
        // 控制端对这个 Run 的镜像:只靠事件推导,而「网络」丢掉了 run_end / run_settled。
        var mirror = RunPhase.Unknown;
        var lossy = new ProbeListener(ev =>
        {
            if (ev is RunStartEvent)
            {
                mirror = RunLifecycle.Merge(mirror, RunPhase.Running);
            }
        });
        var sub = rig.Registry.Subscribe(lossy, new SubscriptionOptions { Coalesce = TimeSpan.Zero });
        var project = new ProjectRef("dev-1", Rig.Workspace);
        var run = rig.Start("s1", "x", runId: "r1", subscriptions: [sub]);
        await Wait.UntilAsync(() => mirror == RunPhase.Running);
        Assert.True(ProjectActivityView.Of(project, rig.Broadcasts[^1], DeviceConnection.Connected).IsRunning);

        release.SetResult();
        await Rig.Settled(run);

        // 事件流里的镜像停在「运行中」(丢了结束事件);权威广播收口。
        Assert.Equal(RunPhase.Running, mirror);
        var activity = ProjectActivityView.Of(project, rig.Broadcasts[^1], DeviceConnection.Connected);
        Assert.False(activity.IsRunning);
        Assert.False(activity.Unknown);
    }

    [Fact]
    public async Task A_disconnected_device_is_unknown_not_finished_and_a_late_broadcast_cannot_revive_a_terminal_run()
    {
        var release = new TaskCompletionSource();
        using var rig = new Rig(o => o.Driver = _ => Waiting(release));
        rig.NewSessionRow();
        var run = rig.Start("s1", "x", runId: "r1");
        var last = rig.Broadcasts[^1];
        var project = new ProjectRef("dev-1", Rig.Workspace);
        var offline = ProjectActivityView.Of(project, last, DeviceConnection.Offline);
        Assert.True(offline.Unknown);
        Assert.False(offline.IsRunning);

        release.SetResult();
        await Rig.Settled(run);
        // 旧的(运行中)广播乱序晚到,Rev 比当前小:只收更新的那份。
        var newest = rig.Broadcasts[^1];
        Assert.True(newest.Rev > last.Rev);
        Assert.Equal(RunPhase.Aborted, RunLifecycle.Merge(RunPhase.Aborted, RunPhase.Running));
    }

    [Fact]
    public async Task While_something_runs_the_full_set_is_re_broadcast_periodically_and_never_when_idle()
    {
        var release = new TaskCompletionSource();
        using var rig = new Rig(o =>
        {
            o.ManualClock = true;
            o.Driver = _ => Waiting(release);
        });
        rig.NewSessionRow();
        rig.Manual!.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty(rig.Broadcasts); // 空闲:没有什么可收口的

        var run = rig.Start("s1", "x", runId: "r1");
        var before = rig.Broadcasts.Count;
        rig.Manual.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(before + 1, rig.Broadcasts.Count);
        Assert.Equal(["r1"], rig.Broadcasts[^1].Runs.Select(r => r.RunId));
        Assert.True(rig.Broadcasts[^1].Rev > rig.Broadcasts[^2].Rev);

        release.SetResult();
        await Rig.Settled(run);
        var after = rig.Broadcasts.Count;
        rig.Manual.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(after, rig.Broadcasts.Count);
    }
}
