using System.Collections.Concurrent;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Extensions.Planning.Goals;

/// <summary>
/// 目标的一方扩展(06 §3):<c>session_start</c>(从转录恢复目标)、<c>before_run</c>(绑定这个 Run 的上下文、冻结判定模型)、<c>turn_end</c>(判定 / 续跑 / 空转刹车 / 后台推迟)、
/// <c>run_end</c>(正常收尾放掉定时器;中止 / 出错暂停目标)、工具 <c>ProposeGoal</c>。可关闭(不注册就等于不存在)。
/// <para>
/// 子 Run 没有「停止」这回事:它的结局是把结论交回父代理,没有人在等它满足一个会话级的条件——所以子 Run 和定时任务的 Run 都不参与(没有登记,回合末直接放行)。
/// </para>
/// </summary>
public sealed class GoalExtension(GoalService goals) : IAgentExtension
{
    private readonly ConcurrentDictionary<RunId, bool> _primary = new();

    public string Id => "goals";

    /// <summary>在 Hooks(100)之后、待办(600)之前:收尾裁决按 Order 取第一个不是 <c>Stop</c> 的,目标的续跑 / 强停优先于待办补报。</summary>
    public int Order => 500;

    internal int OpenRuns => _primary.Count;

    public void Register(IExtensionRegistry registry)
    {
        registry.RegisterTool(new ProposeGoalTool(goals));
        registry.On(SessionExtensionPoints.SessionStart, (e, _, _) =>
        {
            goals.Restore(e.SessionId);
            return ValueTask.CompletedTask;
        });
        registry.Mutate(SessionExtensionPoints.BeforeRun, OnBeforeRun);
        registry.Gate(LoopExtensionPoints.TurnEnd, OnTurnEnd);
        registry.On(LoopExtensionPoints.RunEnd, OnRunEnd);
    }

    private ValueTask OnBeforeRun(RunDraft draft, ExtensionContext ctx, CancellationToken ct)
    {
        var handle = goals.HandleOf(ctx.RunId);
        var primary = draft.Request.Depth == 0 && handle?.Origin != RunOrigin.Scheduled;
        _primary[ctx.RunId] = primary;
        if (!primary)
        {
            return ValueTask.CompletedTask;
        }

        // ★ 判定模型与供应商在 Run 开始时成对冻结:中途改设置不动这一轮,下一轮才换
        var context = new GoalRunContext(ctx.RunId, handle?.WorkspaceId ?? "", draft.Request.Model, goals.FreezeEvaluatorModel(), handle?.Options);
        goals.BindRun(ctx.SessionId, context, userMessage: draft.Request.Input.Any(m => !m.Internal && m.Parts.Count > 0));
        return ValueTask.CompletedTask;
    }

    private async ValueTask<TurnEndDecision> OnTurnEnd(TurnEnd end, ExtensionContext ctx, CancellationToken ct)
    {
        if (!_primary.TryGetValue(end.RunId, out var primary) || !primary || goals.ContextOf(end.SessionId) is not { } context)
        {
            return TurnEndDecision.Stop;
        }

        // 这一回合的助手消息已经落库:先把排着的标记(没有助手消息时设定的目标)挂上去
        goals.FlushMarkers(end.SessionId);
        var decision = await goals.HandleTurnEndAsync(end, context, isSubagent: false, ct).ConfigureAwait(false);
        switch (decision)
        {
            case GoalTurnDecision.Continue cont:
                Attach(end.SessionId, cont.Status);
                var message = new Message(new MessageId(goals.NewId()), Role.User, [new TextPart(cont.Inject)], goals.Clock.GetUtcNow(), Internal: true);
                return TurnEndDecision.Continue(message);
            case GoalTurnDecision.Finish finish:
                Attach(end.SessionId, finish.Status);
                if (finish.IdleStreak is { } streak)
                {
                    // 空转刹车:强制结束并警告(目标留着)。本层不产出界面文字:键由控制端翻译,细节是英文诊断
                    return new TurnEndDecision(
                        TurnEndAction.Finish, Notice: new LoopNotice("goal.warn.idleStreak", $"Stopped after {streak} consecutive tool-free turns."));
                }

                // 后台推迟的收尾:仍然接受此刻队列里的引导消息(等的就是后台的结果);达成 / 不可能的收尾不再看队列
                return finish.AcceptPendingInput ? TurnEndDecision.Stop : TurnEndDecision.Finish;
            default:
                return TurnEndDecision.Stop;
        }
    }

    private void Attach(string sessionId, GoalStatusPart? status)
    {
        if (status is not null)
        {
            goals.AttachStatus(sessionId, status);
        }
    }

    private ValueTask OnRunEnd(RunEndEvent end, ExtensionContext ctx, CancellationToken ct)
    {
        if (_primary.TryRemove(ctx.RunId, out var primary) && primary)
        {
            goals.FlushMarkers(ctx.SessionId);
            if (end.Status == RunStatus.Done)
            {
                goals.Release(ctx.SessionId);
            }
            else
            {
                // 手动中止 / 出错:暂停唤醒,但保留条件给用户的下一轮
                goals.Pause(ctx.SessionId);
            }
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// 目标的 Run 扩展(编排层的接缝):记下每个 Run 的句柄(档位快照、工作区、起点),用户中止顶层 Run 时立刻暂停目标(轮换目标 id:还在路上的判定和检查作废),
/// Run 收尾时登记「被中止过」。
/// </summary>
public sealed class GoalRunExtension(GoalService goals) : IRunExtension
{
    public void OnCreated(RunHandle run) => goals.NoteRunCreated(run);

    public void OnAbortRequested(RunHandle run)
    {
        if (run.IsTopLevel && run.Origin != RunOrigin.Scheduled)
        {
            goals.Pause(run.SessionId);
        }
    }

    public void OnSettled(RunHandle run) => goals.NoteRunSettled(run);
}
