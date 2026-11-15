using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Runs.Tests.Support;

namespace Ncw.Agent.Runs.Tests;

/// <summary>
/// 被看门狗 / 强制收口放弃的 Run,驱动醒来后不许再写库(05 §12,2.x #187)。真的 <c>AgentSession</c> + 真 SQLite;
/// 驱动卡在一个不认取消令牌的工具里,手动时钟推过宽限期让看门狗放弃它,再放行工具,断言库里没有放弃之后的任何写入。
/// </summary>
public sealed class AbandonedWriteTests
{
    private static async Task StuckThenAbandonedAsync(Action<Rig, RunHandle> abandon)
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        using var rig = new Rig(o => o.ManualClock = true);
        rig.NewSessionRow();
        rig.Tools.Add(new FakeTool("stuck", async (_, _, _) =>
        {
            entered.SetResult();
            await release.Task; // 故意不认取消令牌
            return ToolResult.Text("late result");
        }));
        rig.Llm.Turn(Up.Call("c1", "stuck"));
        var listener = new CollectingListener();
        var sub = rig.Registry.Subscribe(listener, new SubscriptionOptions { Coalesce = TimeSpan.Zero });
        var run = rig.Start("s1", "go", runId: "r1", subscriptions: [sub]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var before = rig.Repo.History("s1").Select(r => r.Id).ToList();
        Assert.Equal(2, before.Count); // 用户输入 + 带工具调用的助手消息:放弃之前已经落库

        abandon(rig, run);

        Assert.True(run.Abandoned);
        Assert.Equal(RunState.Aborted, run.State);
        Assert.True(run.IsSettled);
        var endedAt = rig.Repo.GetRunRecord("r1")!.EndedAt;
        var eventsAtAbandon = listener.Events().Count;
        Assert.Single(listener.Events().OfType<RunSettledEvent>());

        // 放行驱动:它醒来、拿到工具结果、循环发现已取消,照常走「补孤儿 / 提交中止收尾」——这些写入全部要被跳过。
        release.SetResult();
        var session = rig.Sessions.Get("s1", Rig.Workspace);
        await Wait.UntilAsync(() => !session.IsBusy, what: "the abandoned driver to unwind");

        Assert.Equal(before, rig.Repo.History("s1").Select(r => r.Id)); // 数据库里没有放弃之后的写入
        Assert.True(rig.Log.Any(SessionDiagnosticKeys.WriteSkippedAbandoned)); // 跳过留下了结构化诊断
        Assert.Equal("aborted", rig.Repo.GetRunRecord("r1")!.Status);
        Assert.Equal(endedAt, rig.Repo.GetRunRecord("r1")!.EndedAt);
        // 事件一律丢弃、run_settled 仍然只有一个
        Assert.Equal(eventsAtAbandon, listener.Events().Count);
        Assert.Single(listener.Events().OfType<RunSettledEvent>());

        // 会话没有被它弄脏:下一个 Run 正常落库(顺序是 message_end 提交语义),孤儿调用在它开始时修复。
        rig.Llm.Turn(Up.Text("fine"));
        await rig.RunAsync("s1", "again", runId: "r2");
        var stored = rig.Stored("s1");
        Assert.Equal("go", stored[0].Text);
        Assert.Equal("again", stored[^2].Text);
        Assert.Equal("fine", stored[^1].Text);
        Assert.Equal("done", rig.Repo.GetRunRecord("r2")!.Status);
    }

    [Fact]
    public Task The_watchdog_abandons_a_driver_stuck_in_a_tool_and_when_it_wakes_up_it_writes_nothing() =>
        StuckThenAbandonedAsync((rig, run) =>
        {
            rig.Registry.Abort(run.RunId);
            rig.Manual!.Advance(TimeSpan.FromMilliseconds(4900));
            Assert.Equal(RunState.Running, run.State); // 宽限期内不动手
            rig.Manual.Advance(TimeSpan.FromMilliseconds(200));
        });

    [Fact]
    public Task A_forced_settle_before_deleting_the_session_also_leaves_the_late_driver_without_write_access() =>
        StuckThenAbandonedAsync((rig, _) => Assert.True(rig.Registry.ForceSettleSessions(["s1"]) >= 1));

    [Fact]
    public async Task A_run_that_stops_in_time_is_never_flagged_and_keeps_saving_normally()
    {
        using var rig = new Rig(o => o.ManualClock = true);
        rig.NewSessionRow();
        rig.Llm.Turn(Up.Text("hello"));

        var run = await rig.RunAsync("s1", "hi", runId: "r1");

        Assert.False(run.Abandoned);
        Assert.Equal(["hi", "hello"], rig.Stored("s1").Select(m => m.Text));
        Assert.False(rig.Log.Any(SessionDiagnosticKeys.WriteSkippedAbandoned));
    }
}
