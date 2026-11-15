using System.Text.Json;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Tools.Schema;
using Ncw.Core.Domain;

namespace Ncw.Agent.Extensions.Planning.Goals;

public sealed partial class GoalService
{
    private bool HasPendingProposal(string sessionId) =>
        _options.Gate?.Snapshot().Pending.OfType<GoalProposal>().Any(p => p.SessionId == sessionId) == true;

    /// <summary>用户设定 / 清除时撤掉这个会话待决的目标提案(用户的新意图作废手里还拿着的批准条)。</summary>
    public int CancelProposals(string sessionId) =>
        _options.Gate?.AbortWhere(r => r is GoalProposal p && p.SessionId == sessionId) ?? 0;

    /// <summary>
    /// <c>ProposeGoal</c> 此刻能不能成立(五道门,和工具的 <c>IsEnabled</c> 是同一份判定):顶层 Run 且能问人、会话是交互式的、Run 还在运行没被要求中止、
    /// 不在计划模式里(让模型先把计划走完再提目标)、设置不是 disabled、这个会话没有待决的提案。
    /// </summary>
    public bool CanPropose(RunId runId, string sessionId, int depth)
    {
        if (depth != 0 || !_options.Interactive(sessionId) || _options.Gate is null)
        {
            return false;
        }

        if (_options.PermissionRuns?.Get(runId)?.Interactions is not { CanInteract: true })
        {
            return false;
        }

        return IsLive(runId)
            && !_options.Planning(runId, sessionId)
            && _options.ProposalPolicy() != GoalProposalPolicy.Disabled
            && !HasPendingProposal(sessionId);
    }

    /// <summary>
    /// 模型自提目标。<b>只等派发,不等人回答</b>:要问用户就把提案记下来、返回 <see cref="GoalProposalStatus.Pending"/>,审批弹窗还开着(<c>InteractionGate</c> 没有超时,
    /// 在这里等就会把整个 Run 卡在一个没人回答的任务上)。人的决定<b>从不</b>变成工具结果。
    /// 直接设立只在「模型断言用户的原话已经说了这个结果」<b>且</b>策略是 auto 时;<c>alwaysAsk</c> 下模型说「不用问」也强制走审批。
    /// 批准之后才装上目标、发 kickoff——但批准条会过期:Run 中止 / 进了计划模式 / 设置被关掉 / 用户手动设过或清掉目标(版本号前进)/ 会话没了,都不设。
    /// 提案不占住 Run:Run 正常结束之后仍然可以被批准。
    /// </summary>
    public GoalProposalStatus Propose(RunId runId, string sessionId, int depth, string condition, bool askUser)
    {
        if (!CanPropose(runId, sessionId, depth))
        {
            throw new InvalidOperationException("Goal proposals are unavailable in this session.");
        }

        var context = ContextOf(sessionId) ?? new GoalRunContext(runId, "", new ModelRef(null, ""), null, null);
        var ask = askUser || _options.ProposalPolicy() != GoalProposalPolicy.Auto;
        Log("goal_proposed", ("promptLength", condition.Length), ("askUser", ask));
        if (!ask)
        {
            SetProposed(sessionId, condition, GoalOrigin.ProposalDirect, context);
            return GoalProposalStatus.Set;
        }

        var revision = Store.Revision(sessionId);
        var interactions = _options.PermissionRuns!.Get(runId)!.Interactions;
        _ = AwaitDecisionAsync(interactions, runId, sessionId, condition, revision, context);
        return GoalProposalStatus.Pending;
    }

    private async Task AwaitDecisionAsync(RunInteractions interactions, RunId runId, string sessionId, string condition, long revision, GoalRunContext context)
    {
        InteractionResponse response;
        try
        {
            response = await interactions.RequestAsync(new GoalProposal(sessionId, condition)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 提案被撤掉(中止 / 用户手动设定 / 会话删除):不设目标
            Log("goal_proposal_decided", ("promptLength", condition.Length), ("askUser", true), ("decision", "cancelled"));
            return;
        }

        if (response is not GoalProposalResponse decision)
        {
            return;
        }

        Log("goal_proposal_decided", ("promptLength", condition.Length), ("askUser", true), ("decision", decision.Approved ? "approved" : "declined"));
        // 批准条已经过期的几种情形(见 Propose 的说明)
        if (!decision.Approved
            || WasAborted(runId)
            || _options.Planning(runId, sessionId)
            || _options.ProposalPolicy() == GoalProposalPolicy.Disabled
            || Store.Revision(sessionId) != revision
            || !_options.SessionExists(sessionId))
        {
            return;
        }

        SetProposed(sessionId, condition, GoalOrigin.ProposalApproved, context);
    }

    private void SetProposed(string sessionId, string condition, GoalOrigin origin, GoalRunContext context)
    {
        var kickoff = Activate(sessionId, condition, origin);
        var goal = Store.Get(sessionId);
        if (goal is null || kickoff is null)
        {
            return;
        }

        BindRun(sessionId, context, userMessage: false);
        Inject(sessionId, kickoff, goal.Id, context);
    }
}
