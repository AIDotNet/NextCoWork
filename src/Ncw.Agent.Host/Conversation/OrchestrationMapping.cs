using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Subagents;
using Ncw.Agent.Extensions.Planning.Goals;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Agent.Host.Conversation;

/// <summary>
/// 子代理 / 目标的领域类型 ↔ 协议 DTO(只有 Host 知道协议)。纯函数,不走反射。
/// 状态 / 枚举的线上写法只有一份定义:Core 的 <see cref="SubagentView"/> / <see cref="GoalWire"/>,控制端读同一张表。
/// </summary>
internal static class OrchestrationMapping
{
    // ───────────────────────── 子代理事件 ─────────────────────────

    public static SubagentStartEventDto ToDto(SubagentStartEvent e) => new(
        e.CallId, e.ChildRunId.Value, e.ChildSessionId, e.AgentType, e.Description, e.Color, e.Model, e.Background, e.At.ToUnixTimeMilliseconds());

    public static SubagentUpdateEventDto ToDto(SubagentUpdateEvent e) => new(e.CallId, e.ChildRunId.Value)
    {
        Phase = e.Phase is { } phase ? PhaseName(phase) : null,
        CurrentTool = Patch(e.CurrentTool),
        CurrentTarget = Patch(e.CurrentTarget),
        ToolCalls = e.ToolCalls,
        ToolErrors = e.ToolErrors,
        Usage = e.Usage is { } usage ? Mapping.ToDto(usage) : null,
        ContextUsage = e.ContextUsage is { } context ? new SubagentContextUsageDto(context.Used, context.Window, context.ShouldCompact) : null,
        Notice = e.Notice.IsSet
            ? new SubagentNoticePatchDto(e.Notice.Value is { } notice ? new SubagentNoticeDto(notice.Kind, notice.Reason, notice.Attempt, notice.To) : null)
            : null,
        ChildSeq = e.ChildSeq,
        At = e.At?.ToUnixTimeMilliseconds(),
    };

    public static SubagentEndEventDto ToDto(SubagentEndEvent e) => new(
        e.CallId, e.ChildRunId.Value, Mapping.StatusName(e.Status), e.Summary, e.Error is { } error ? Mapping.ToDto(error) : null, e.ChildSeq, e.At?.ToUnixTimeMilliseconds());

    private static StringPatchDto? Patch(Patch<string> patch) => patch.IsSet ? new StringPatchDto(patch.Value) : null;

    private static string PhaseName(SubagentPhase phase) => phase switch
    {
        SubagentPhase.Starting => "starting",
        SubagentPhase.Background => "background",
        SubagentPhase.Tool => "tool",
        SubagentPhase.Thinking => "thinking",
        _ => "finishing",
    };

    // ───────────────────────── 子代理定义 ─────────────────────────

    public static AgentInfoDto ToDto(AgentListItem item) => new(
        item.Name,
        item.Description,
        item.Scope switch { AgentSourceKind.Builtin => "builtin", AgentSourceKind.Global => "global", _ => "project" },
        item.Source,
        item.Tools is null ? null : [.. item.Tools],
        item.Model,
        item.PermissionMode is { } mode ? Ncw.Agent.Extensions.Permissions.PermissionModes.ToWire(mode) : null,
        item.Color,
        item.Enabled);

    public static AgentDiagnosticDto ToDto(AgentDiagnostic d) => new(
        d.Path, d.Key, d.Message, d.Params is null ? null : new Dictionary<string, string>(d.Params, StringComparer.Ordinal));

    // ───────────────────────── 目标 ─────────────────────────

    public static GoalDto ToDto(string sessionId, ActiveGoal goal) => new(
        sessionId, goal.Id, goal.Condition, GoalWire.ToWire(goal.Origin), goal.SetAt.ToUnixTimeMilliseconds(), goal.TokensAtStart, goal.Iterations, goal.LastReason,
        goal.DeferredSince?.ToUnixTimeMilliseconds(), goal.CheckinCount, goal.LastDeferralPassAt?.ToUnixTimeMilliseconds(), goal.IdleCheckinCount, goal.Tokens);

    /// <summary>
    /// 一次变更 → 增量。★ 目标取<b>此刻</b>的值而不是事件里带的那份:监听器在锁外同步调用,两个线程的变更通知先后可能和状态变化的先后不一致,
    /// 而推送按「版本号 + 此刻的真源」发,最后一条一定是终局(控制端只收版本不更旧的)。
    /// </summary>
    public static GoalChangeDto ToChange(GoalChange change, ActiveGoal? current, long rev) => new(
        change.SessionId,
        current is null ? null : ToDto(change.SessionId, current),
        current is null && change.Cleared is { } reason ? GoalWire.ToWire(reason) : null,
        change.Message is { } message ? Mapping.ToDto(message) : null,
        rev);

    public static string DeliveryName(Ncw.Agent.Kernel.Runs.InternalDelivery delivery) => delivery switch
    {
        Ncw.Agent.Kernel.Runs.InternalDelivery.Interjected => "interjected",
        Ncw.Agent.Kernel.Runs.InternalDelivery.Launched => "launched",
        Ncw.Agent.Kernel.Runs.InternalDelivery.Deferred => "deferred",
        _ => "failed",
    };

    public static string ReportName(SubagentReportStatus status) => SubagentView.ToWire(status);
}
