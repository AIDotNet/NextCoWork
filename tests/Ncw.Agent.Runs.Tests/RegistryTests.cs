using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Llm;
using Ncw.Agent.Runs.Tests.Support;

namespace Ncw.Agent.Runs.Tests;

/// <summary>
/// 注册表的查询、会话互斥与操作、回收、删除前活体检查、退出。移植自 2.x <c>run-registry.test.ts</c> 的
/// 「嵌套与中断 / 回收已结束的 run / 删除会话前的活体检查」。
/// </summary>
public sealed class RegistryTests
{
    private static Ext BlockOnRunEnd(TaskCompletionSource entered, Task release) => new("block", 0, r =>
        r.On(LoopExtensionPoints.RunEnd, async (_, _, _) =>
        {
            entered.TrySetResult();
            await release;
        }));

    private static ScriptedDriver WaitingDriver(TaskCompletionSource release, Func<RunHandle, bool>? waits = null) => new(async (handle, request, sink, ct) =>
    {
        await sink.EmitAsync(new RunStartEvent(handle.RunId, handle.SessionId, request.Model), CancellationToken.None);
        if (waits is null || waits(handle))
        {
            await release.Task;
        }

        await sink.EmitAsync(new RunEndEvent(RunStatus.Done), CancellationToken.None);
        return ScriptedDriver.Done(handle);
    });

    private static RunLaunch Child(Rig rig, string session, string run, string parentRun, string parentSession) =>
        rig.Make(session, "task", runId: run, origin: RunOrigin.Subagent) with { ParentRunId = new RunId(parentRun), ParentSessionId = parentSession };

    // ── 查询 ──

    [Fact]
    public async Task Running_children_are_counted_not_the_cumulative_set_the_concurrency_gate_must_not_run_dry()
    {
        var release = new TaskCompletionSource();
        using var rig = new Rig(o => o.Driver = _ => WaitingDriver(release, h => h.ParentRunId is null || h.RunId.Value == "c5"));
        foreach (var s in new[] { "p", "c1", "c2", "c3", "c4", "c5" })
        {
            rig.NewSessionRow(s);
        }

        var parent = rig.Start("p", "x", runId: "p");
        for (var i = 1; i <= 4; i++)
        {
            var child = Assert.IsType<LaunchResult.Started>(rig.Registry.Launch(Child(rig, $"c{i}", $"c{i}", "p", "p"))).Handle;
            await Rig.Settled(child);
        }

        Assert.Equal(4, parent.Children.Count); // 累计:只增不减
        Assert.Equal(0, rig.Registry.ActiveChildCount(new RunId("p"))); // 还在跑的:0
        Assert.Equal(0, rig.Registry.ActiveSubagentCount);
        var fifth = Assert.IsType<LaunchResult.Started>(rig.Registry.Launch(Child(rig, "c5", "c5", "p", "p"))).Handle;
        await Wait.UntilAsync(() => fifth.HasStarted);
        Assert.Equal(1, rig.Registry.ActiveChildCount(new RunId("p")));
        Assert.Equal(["c5"], rig.Registry.ActiveSubagentRunIds().Select(r => r.Value));
        release.SetResult();
        await Rig.Settled(parent);
    }

    [Fact]
    public async Task Running_in_a_workspace_and_top_level_lookup_only_see_what_is_still_running()
    {
        var release = new TaskCompletionSource();
        using var rig = new Rig(o => o.Driver = _ => WaitingDriver(release));
        rig.NewSessionRow("s1");
        rig.NewSessionRow("s2");
        var a = rig.Start("s1", "x", runId: "a");
        var b = Assert.IsType<LaunchResult.Started>(rig.Registry.Launch(Child(rig, "s2", "b", "a", "s1"))).Handle;
        Assert.Equal(["a", "b"], rig.Registry.RunningIn(Rig.Workspace).Select(h => h.RunId.Value).Order());
        Assert.Empty(rig.Registry.RunningIn("other"));
        Assert.Equal(a, rig.Registry.ActiveTopLevelRun("s1"));
        Assert.Null(rig.Registry.ActiveTopLevelRun("s2")); // 子 Run 不算这条会话的顶层 Run
        Assert.Equal(["b"], rig.Registry.DescendantRunIds(new RunId("a")).Select(r => r.Value));
        release.SetResult();
        await Rig.Settled(a);
        await Rig.Settled(b);
        Assert.Empty(rig.Registry.RunningIn(Rig.Workspace));
        Assert.Null(rig.Registry.ActiveTopLevelRun("s1"));
    }

    [Fact]
    public async Task Background_children_are_found_by_parent_session_and_stop_counting_once_aborted()
    {
        var release = new TaskCompletionSource();
        using var rig = new Rig(o => o.Driver = _ => WaitingDriver(release));
        rig.NewSessionRow("p");
        rig.NewSessionRow("c");
        var parent = rig.Start("p", "x", runId: "p");
        var child = Assert.IsType<LaunchResult.Started>(rig.Registry.Launch(Child(rig, "c", "c", "p", "p"))).Handle;
        Assert.Empty(rig.Registry.ActiveBackgroundChildrenOfSession("p"));
        child.Background = new RunBackground("explorer", "look around");
        Assert.Equal([child], rig.Registry.ActiveBackgroundChildrenOfSession("p"));
        child.Abort();
        Assert.Empty(rig.Registry.ActiveBackgroundChildrenOfSession("p"));
        release.SetResult();
        await Rig.Settled(parent);
        await Rig.Settled(child);
    }

    // ── 会话互斥与操作 ──

    [Fact]
    public async Task An_operation_holds_the_session_exclusively_and_a_stale_release_never_clears_a_newer_lock()
    {
        using var rig = new Rig();
        rig.NewSessionRow();
        var lease = rig.Registry.AcquireOperation("s1");
        Assert.Equal(RunRefusal.SessionBusy, Assert.IsType<LaunchResult.Refused>(rig.Registry.Launch(rig.Make("s1", "x"))).Reason);
        Assert.Throws<SessionBusyException>(() => rig.Registry.AcquireOperation("s1"));
        Assert.True(rig.Registry.IsSessionBusy("s1"));

        // 强制放掉之后别人重新拿到:旧的释放函数不会清掉新的锁。
        rig.Registry.ForceSettleSessions(["s1"]);
        var second = rig.Registry.AcquireOperation("s1");
        lease.Dispose();
        Assert.True(rig.Registry.IsSessionBusy("s1"));
        second.Dispose();
        second.Dispose();
        Assert.False(rig.Registry.IsSessionBusy("s1"));
        await rig.RunAsync("s1", "now fine");
    }

    [Fact]
    public async Task Activity_classifies_live_stopping_finalizing_and_operations_by_what_really_holds_the_session()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var stuck = new TaskCompletionSource();
        using var rig = new Rig(o =>
        {
            o.SessionExtensions = [BlockOnRunEnd(entered, release.Task)];
            o.Driver = r => new ScriptedDriver(async (handle, request, sink, ct) =>
            {
                if (handle.SessionId == "stopping")
                {
                    await sink.EmitAsync(new RunStartEvent(handle.RunId, handle.SessionId, request.Model), CancellationToken.None);
                    await stuck.Task; // 不认取消令牌
                    return ScriptedDriver.Done(handle);
                }

                return await new SessionRunDriver(r.Sessions).RunAsync(handle, request, sink, ct);
            });
        });
        foreach (var s in new[] { "live", "stopping", "finalizing", "op", "idle" })
        {
            rig.NewSessionRow(s);
        }

        rig.Llm.Route = ctx => ctx.SessionId == "live" ? Up.Hang() : null; // 谁先到不可靠,按会话分派
        var live = rig.Start("live", "x", runId: "live");
        var stopping = rig.Start("stopping", "x", runId: "stopping");
        await Wait.UntilAsync(() => stopping.HasStarted);
        rig.Registry.Abort(new RunId("stopping"));
        using var op = rig.Registry.AcquireOperation("op");

        var finalizing = rig.Start("finalizing", "x", runId: "finalizing");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RunState.Done, finalizing.State);

        var activity = rig.Registry.Activity(["live", "stopping", "finalizing", "op", "idle"]);
        Assert.Equal(["live"], activity.Live.Select(h => h.SessionId));
        Assert.Equal(["stopping"], activity.Stopping.Select(h => h.SessionId));
        Assert.Equal(["finalizing"], activity.Finalizing);
        Assert.Equal(["op"], activity.Operations);
        Assert.Empty(rig.Registry.Activity(["someone-else"]).Live); // 只看传进来的会话

        release.SetResult();
        rig.Registry.Abort(new RunId("live"));
        stuck.SetResult();
        await Rig.Settled(live);
        await Rig.Settled(finalizing);
        await Rig.Settled(stopping);
    }

    [Fact]
    public async Task Force_settle_ends_a_stuck_run_as_aborted_and_frees_the_session_so_delete_is_not_blocked_forever()
    {
        var stuck = new StuckDriver();
        using var rig = new Rig(o => o.Driver = _ => stuck);
        rig.NewSessionRow();
        var run = rig.Start("s1", "x", runId: "r1");
        await Wait.UntilAsync(() => run.HasStarted);
        rig.Registry.Abort(new RunId("r1"));
        Assert.True(rig.Registry.ForceSettleSessions(["s1"]) >= 1);
        Assert.Equal(RunState.Aborted, run.State);
        Assert.False(rig.Registry.IsSessionBusy("s1"));
        Assert.Empty(rig.Registry.Activity(["s1"]).Live);
        stuck.Release();
        await run.Completion!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RunState.Aborted, run.State);
    }

    // ── 回收 ──

    [Fact]
    public async Task A_finished_run_stays_for_the_ttl_and_is_reaped_after_it()
    {
        using var rig = new Rig(o =>
        {
            o.ManualClock = true;
            o.Driver = _ => ScriptedDriver.Simple();
        });
        rig.NewSessionRow();
        var reaped = new List<RunHandle>();
        rig.Registry.RunReaped += reaped.Add;
        var run = await rig.RunAsync("s1", "x", runId: "r1");
        var ended = run.EndedAt!.Value;

        Assert.Equal(0, rig.Registry.ReapFinished(ended + TimeSpan.FromSeconds(119)));
        Assert.NotNull(rig.Registry.Get(new RunId("r1")));
        Assert.Equal(1, rig.Registry.ReapFinished(ended + TimeSpan.FromSeconds(120)));
        Assert.Null(rig.Registry.Get(new RunId("r1")));
        Assert.Equal([run], reaped);
    }

    [Fact]
    public async Task The_periodic_timer_reaps_without_anyone_asking()
    {
        using var rig = new Rig(o =>
        {
            o.ManualClock = true;
            o.Driver = _ => ScriptedDriver.Simple();
        });
        rig.NewSessionRow();
        await rig.RunAsync("s1", "x", runId: "r1");
        rig.Manual!.Advance(TimeSpan.FromMinutes(3));
        Assert.Null(rig.Registry.Get(new RunId("r1")));
    }

    [Fact]
    public async Task Over_the_total_budget_the_oldest_finished_run_goes_first_without_waiting_for_the_ttl()
    {
        using var rig = new Rig(o =>
        {
            o.ManualClock = true;
            o.Driver = _ => ScriptedDriver.Simple();
            o.Registry = r => r with { FinishedCacheBytes = 250, ReapInterval = TimeSpan.FromDays(1) };
        });
        rig.NewSessionRow("s1");
        rig.NewSessionRow("s2");
        var old = await rig.RunAsync("s1", "x", runId: "old");
        Assert.NotNull(rig.Registry.Get(new RunId("old"))); // 一份日志在预算内:留着
        Assert.True(old.RetainedLogBytes is > 100 and <= 250);
        rig.Manual!.Advance(TimeSpan.FromSeconds(1));
        await rig.RunAsync("s2", "x", runId: "new");
        Assert.Null(rig.Registry.Get(new RunId("old"))); // 两份超了预算:从最早结束的收起,不等 2 分钟的 TTL
        Assert.NotNull(rig.Registry.Get(new RunId("new")));
    }

    [Fact]
    public async Task A_run_is_not_reaped_while_the_session_mutex_is_still_held_for_it()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        using var rig = new Rig(o =>
        {
            o.SessionExtensions = [BlockOnRunEnd(entered, release.Task)];
            o.Registry = r => r with { FinishedRunTtl = TimeSpan.Zero };
        });
        rig.NewSessionRow();
        var run = rig.Start("s1", "x", runId: "r1");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, rig.Registry.ReapFinished(DateTimeOffset.UtcNow.AddDays(1)));
        Assert.NotNull(rig.Registry.Get(new RunId("r1")));
        release.SetResult();
        await Rig.Settled(run);
        Assert.Null(rig.Registry.Get(new RunId("r1"))); // settle 之后(TTL 为 0)立刻收走
    }

    [Fact]
    public async Task A_run_with_a_running_descendant_anywhere_below_it_is_never_reaped_and_a_pending_interaction_pins_it()
    {
        var release = new TaskCompletionSource();
        using var rig = new Rig(o =>
        {
            o.ManualClock = true;
            o.Driver = _ => WaitingDriver(release, h => h.RunId.Value == "grand");
            o.Registry = r => r with { FinishedRunTtl = TimeSpan.Zero, ReapInterval = TimeSpan.FromDays(1) };
        });
        foreach (var s in new[] { "p", "c", "g" })
        {
            rig.NewSessionRow(s);
        }

        var parent = rig.Start("p", "x", runId: "p");
        var child = Assert.IsType<LaunchResult.Started>(rig.Registry.Launch(Child(rig, "c", "child", "p", "p"))).Handle;
        var grand = Assert.IsType<LaunchResult.Started>(rig.Registry.Launch(Child(rig, "g", "grand", "child", "c"))).Handle;
        await Rig.Settled(parent);
        await Rig.Settled(child);
        Assert.Equal(RunState.Running, grand.State);

        var later = rig.Manual!.GetUtcNow().AddDays(1);
        Assert.Equal(0, rig.Registry.ReapFinished(later)); // 要往下查到底:孙子还在跑
        Assert.NotNull(rig.Registry.Get(new RunId("p")));
        Assert.NotNull(rig.Registry.Get(new RunId("child")));

        rig.Interactions.Set(new RunId("p"), 1); // 还挂着一个等人点的交互
        release.SetResult();
        await Rig.Settled(grand);
        Assert.Null(rig.Registry.Get(new RunId("child"))); // 孙子结束之后,子孙都收走
        Assert.Null(rig.Registry.Get(new RunId("grand")));
        Assert.NotNull(rig.Registry.Get(new RunId("p"))); // p 被钉住
        rig.Interactions.Set(new RunId("p"), 0);
        Assert.Equal(1, rig.Registry.ReapFinished(later));
        Assert.Null(rig.Registry.Get(new RunId("p")));
    }

    // ── 退出与会话删除 ──

    [Fact]
    public async Task Shutdown_aborts_every_run_waits_for_them_and_refuses_new_ones()
    {
        using var rig = new Rig();
        rig.NewSessionRow("s1");
        rig.NewSessionRow("s2");
        rig.Llm.Turn(Up.Hang(), Up.Hang());
        var a = rig.Start("s1", "x", runId: "a");
        var b = rig.Start("s2", "x", runId: "b");
        await Wait.UntilAsync(() => a.HasStarted && b.HasStarted);

        await rig.Registry.ShutdownAsync();
        Assert.Equal(RunState.Aborted, a.State);
        Assert.Equal(RunState.Aborted, b.State);
        Assert.True(a.IsSettled && b.IsSettled);
        Assert.Equal(RunRefusal.ShuttingDown, Assert.IsType<LaunchResult.Refused>(rig.Registry.Launch(rig.Make("s1", "late"))).Reason);
    }
}
