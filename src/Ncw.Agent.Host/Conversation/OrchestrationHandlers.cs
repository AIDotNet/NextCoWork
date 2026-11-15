using Ncw.Agent.Extensions.Planning.Goals;
using Ncw.Agent.Extensions.Planning.Plans;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Kernel.Subagents;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Conversation;

/// <summary>
/// 子代理 / 目标 / 计划的方法实现(方法表在 <see cref="OrchestrationMethods"/>)。角色、承载、幂等键由路由统一检查,这里只做业务:校验参数 → 调领域层 → 映射成 DTO。
/// 设备数据没就绪时 <paramref name="service"/> 抛可重试的 <c>unavailable</c>。
/// </summary>
internal static class OrchestrationHandlers
{
    public static void Register(MethodRouter router, Func<ConversationService> service)
    {
        // subagents
        router.Handle(OrchestrationMethods.SubagentsList, async (p, _, ct) => await ListAsync(service(), p, ct).ConfigureAwait(false));
        router.Handle(OrchestrationMethods.SubagentsDiagnostics, async (p, _, ct) => await DiagnosticsAsync(service(), p, ct).ConfigureAwait(false));
        router.Handle(OrchestrationMethods.SubagentsSetEnabled, (p, _, _) => ValueTask.FromResult(SetEnabled(service(), p)));
        router.Handle(OrchestrationMethods.SubagentsReport, (p, _, _) => ValueTask.FromResult(Report(service(), p)));

        // goal
        router.Handle(OrchestrationMethods.GoalGet, (p, _, _) => ValueTask.FromResult(GoalGet(service(), p)));
        router.Handle(OrchestrationMethods.GoalSet, async (p, ctx, ct) => await GoalSetAsync(service(), p, ctx, ct).ConfigureAwait(false));
        router.Handle(OrchestrationMethods.GoalClear, (p, _, _) => ValueTask.FromResult(GoalClear(service(), p)));

        // plan
        router.Handle(OrchestrationMethods.RunsExecutePlan, async (p, ctx, ct) => await ExecutePlanAsync(service(), p, ctx, ct).ConfigureAwait(false));
    }

    // ───────────────────────── subagents ─────────────────────────

    private static async ValueTask<SubagentsListResult> ListAsync(ConversationService s, SubagentsListParams p, CancellationToken ct)
    {
        _ = s.RequireProject(p.WorkspaceId);
        return new SubagentsListResult([.. (await s.Agents.ListAsync(p.WorkspaceId, ct).ConfigureAwait(false)).Select(static a => OrchestrationMapping.ToDto(a))]);
    }

    private static async ValueTask<SubagentsDiagnosticsResult> DiagnosticsAsync(ConversationService s, SubagentsListParams p, CancellationToken ct)
    {
        _ = s.RequireProject(p.WorkspaceId);
        return new SubagentsDiagnosticsResult([.. (await s.Agents.DiagnosticsAsync(p.WorkspaceId, ct).ConfigureAwait(false)).Select(static d => OrchestrationMapping.ToDto(d))]);
    }

    private static Unit SetEnabled(ConversationService s, SubagentsSetEnabledParams p)
    {
        // 名字会原样进 kv:和定义文件同一条规则(小写字母数字和连字符),免得一个调用方往设备配置里塞任意文本
        if (!AgentLimits.IsValidName(p.Name))
        {
            throw new ProtocolException(ErrorCodes.InvalidRequest, $"'{p.Name}' is not a valid subagent name.");
        }

        try
        {
            s.Agents.SetEnabled(p.Name, p.Enabled);
        }
        catch (Microsoft.Data.Sqlite.SqliteException e)
        {
            throw new ProtocolException(ErrorCodes.Unavailable, e.Message, retryable: true);
        }

        return Unit.Value;
    }

    /// <summary>
    /// 「处理」:把后台子代理的结果汇报给主代理。没有那张回执 → <c>none</c>(不是错误);档位取这个会话上一次的发送档位,
    /// 都没有时进 <c>blocked</c>(界面上那颗按钮不是静默的)。
    /// </summary>
    private static SubagentsReportResult Report(ConversationService s, SubagentsReportParams p)
    {
        var session = s.RequireSession(p.SessionId);
        // 这个进程没见过这条会话发消息时(重启之后),汇报靠会话自己记着的模型 / 模式才发得出去;会话也没有模型就没有档位(→ blocked)
        SendOptions? fallback = null;
        if (session.Model.Length > 0)
        {
            fallback = Mapping.ToOptions(new SendOptionsDto(""), session);
        }

        var status = s.Subagents.ReportManually(session.Id, p.CallId, fallback);
        return new SubagentsReportResult(OrchestrationMapping.ReportName(status));
    }

    // ───────────────────────── goal ─────────────────────────

    private static GoalStateDto GoalGet(ConversationService s, SessionRefParams p)
    {
        _ = s.RequireSession(p.SessionId);
        return s.GoalOf(p.SessionId);
    }

    private static async ValueTask<GoalSetResultDto> GoalSetAsync(ConversationService s, GoalSetParams p, CallContext context, CancellationToken ct)
    {
        var session = s.RequireSession(p.SessionId);
        _ = await s.EnvironmentOfAsync(s.RequireProject(session.WorkspaceId), ct).ConfigureAwait(false);
        if (p.Options is { } dto)
        {
            // 档位记在会话上:kickoff(以及之后的检查)在没有别的档位可用时续用它;full 档位和 runs.start 一样只有 owner
            var options = Mapping.ToOptions(dto, session);
            ConversationService.RequirePermissionModeAllowed(options.PermissionMode, context);
            s.Registry.RememberOptions(session.Id, options);
        }

        return s.Planning.Goals.SetByUser(session.Id, p.Condition) switch
        {
            GoalSetResult.Ok ok => new GoalSetResultDto(GoalOutcomes.Set, OrchestrationMapping.ToDto(session.Id, ok.Goal), OrchestrationMapping.DeliveryName(ok.Delivery)),
            GoalSetResult.Invalid { Reason: GoalInvalidReason.TooLong } invalid => new GoalSetResultDto(GoalOutcomes.TooLong, Length: invalid.Length),
            _ => new GoalSetResultDto(GoalOutcomes.Empty),
        };
    }

    private static Unit GoalClear(ConversationService s, SessionRefParams p)
    {
        var session = s.RequireSession(p.SessionId);
        s.Planning.Goals.Clear(session.Id);
        return Unit.Value;
    }

    // ───────────────────────── plan ─────────────────────────

    /// <summary>
    /// 批准计划之后以 <c>code</c> 模式发起执行 Run:发送之前先登记「这个 RunId 执行这份计划」(<c>before_run</c> 载入计划放进状态块)。
    /// 会话忙时是 <c>busy</c>:计划只属于这个 RunId,排进队列的那句话以后会用另一个 RunId 起来,带不上它。
    /// </summary>
    private static async ValueTask<RunsStartResult> ExecutePlanAsync(ConversationService s, RunsExecutePlanParams p, CallContext context, CancellationToken ct = default)
    {
        if (p.Plan.PlanId.Length == 0 || p.Plan.Path.Length == 0)
        {
            throw new ProtocolException(ErrorCodes.InvalidRequest, "A plan reference needs a plan id and a path.");
        }

        var session = s.RequireSession(p.Start.SessionId);
        if (s.Registry.IsSessionBusy(session.Id) || s.Registry.ActiveTopLevelRun(session.Id) is not null)
        {
            throw new ProtocolException(ErrorCodes.Busy, $"Session {session.Id} has a run in progress.", retryable: true);
        }

        var runId = new RunId(p.Start.RunId);
        RunsStartResult result;
        try
        {
            result = await ConversationHandlers.StartAsync(s, p.Start, context,
                id => s.Planning.Plans.ExpectExecution(id, new PlanFileRef(p.Plan.PlanId, p.Plan.Path)), ct).ConfigureAwait(false);
        }
        catch
        {
            s.Planning.Plans.CancelExpectation(runId);
            throw;
        }

        if (result.Outcome != StartOutcomes.Started)
        {
            s.Planning.Plans.CancelExpectation(runId); // 抢在这一瞬间排进队列的:它以后用另一个 RunId 起来,带不上这份计划
        }

        return result;
    }
}
