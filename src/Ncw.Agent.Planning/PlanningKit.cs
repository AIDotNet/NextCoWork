using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Extensions.Planning.Goals;
using Ncw.Agent.Extensions.Planning.Plans;
using Ncw.Agent.Extensions.Planning.TodoLists;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Storage.Sessions;

namespace Ncw.Agent.Extensions.Planning;

/// <summary>装配参数。每个功能可以单独关闭(06 §3「能否关闭」:计划工作流开关 / 待办 / 目标),关掉的不注册,等于不存在。</summary>
public sealed class PlanningKitOptions
{
    public required ILlmClient Llm { get; init; }

    public required SessionRepository Sessions { get; init; }

    public InteractionGate? Gate { get; init; }

    public PermissionRuns? PermissionRuns { get; init; }

    public IIdGenerator Ids { get; init; } = new UlidIdGenerator();

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public Func<ModelRef, ModelBinding?> ResolveModel { get; init; } = static _ => null;

    /// <summary>会话累计用掉的 token(用量账本)。</summary>
    public Func<string, long> SessionTokens { get; init; } = static _ => 0;

    /// <summary>这个 Run 开着计划工作流吗(装配方按它知道的模式回答;缺省 false = 计划工具对所有模式隐藏)。</summary>
    public Func<ExtensionContext, bool> PlanWorkflow { get; init; } = static _ => false;

    public Func<ModelRef?> EvaluatorModel { get; init; } = static () => null;

    public Func<GoalProposalPolicy> ProposalPolicy { get; init; } = static () => GoalProposalPolicy.Auto;

    public Func<string, bool> Interactive { get; init; } = static _ => true;

    public TimeSpan? CheckinInterval { get; init; }

    /// <summary>检查间隔基数的动态来源(设置页,每次现读);优先于 <see cref="CheckinInterval"/>。</summary>
    public Func<TimeSpan?>? CheckinIntervalSource { get; init; }

    public TimeSpan EvaluationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public ILoopDiagnostics Log { get; init; } = NullLoopDiagnostics.Instance;

    public Action<string, IReadOnlyDictionary<string, string>>? Telemetry { get; init; }

    public bool EnablePlanMode { get; init; } = true;

    public bool EnableTodos { get; init; } = true;

    public bool EnableGoals { get; init; } = true;
}

/// <summary>
/// 计划模式 + 待办 + 目标三个一方扩展的装配(Host 的一站式入口)。三者共享的事实只有一份:谁在收尾上让路(目标在续跑 / 计划活着 / 后台子代理在跑时,待办不补报)、
/// 计划里不许提目标、目标和计划各自的 Run 扩展必须排在 Host 造 <c>ToolRuntime</c> 的 Run 扩展<b>之后</b>(<see cref="RunExtensions"/>)。
/// 编排层 <see cref="RunRegistry"/> 要拿 <see cref="RunExtensions"/> 才能造出来,而这里又要靠它投递目标检查,所以先造 Kit、再造注册表、最后 <see cref="Bind"/>。
/// </summary>
public sealed class PlanningKit
{
    private RunRegistry? _registry;

    private PlanningKit(PlanService plans, GoalService goals, IReadOnlyList<IAgentExtension> extensions, IReadOnlyList<IRunExtension> runExtensions)
    {
        Plans = plans;
        Goals = goals;
        Extensions = extensions;
        RunExtensions = runExtensions;
    }

    public PlanService Plans { get; }

    public GoalService Goals { get; }

    /// <summary>放进 <c>AgentSessionDeps.Extensions</c>(和权限扩展一起;顺序由各自的 <c>Order</c> 决定)。</summary>
    public IReadOnlyList<IAgentExtension> Extensions { get; }

    /// <summary>放进 <c>RunRegistryDeps.Extensions</c>,排在造 <c>ToolRuntime</c> / <c>PermissionRunState</c> 的 Host 扩展之后。</summary>
    public IReadOnlyList<IRunExtension> RunExtensions { get; }

    /// <summary>注册表造好之后接上:目标检查的投递、后台工作、会话是否空闲都从它读。</summary>
    public void Bind(RunRegistry registry) => _registry = registry;

    public static PlanningKit Create(PlanningKitOptions o)
    {
        PlanningKit? kit = null;
        var transcript = new SessionTranscriptReader(o.Sessions);
        var plans = new PlanService(new PlanOptions { PlanWorkflow = o.PlanWorkflow, PermissionRuns = o.PermissionRuns, Ids = o.Ids });

        IReadOnlyList<BackgroundTaskLine> Background(string sessionId) =>
            kit?._registry is { } registry
                ? [.. registry.ActiveBackgroundChildrenOfSession(sessionId).Where(h => h.Background is not null).Select(h => new BackgroundTaskLine(h.RunId.Value, h.Background!.Type, h.Background.Description))]
                : [];

        var goals = new GoalService(new GoalOptions
        {
            Llm = o.Llm,
            Transcript = new SessionGoalTranscript(o.Sessions),
            Ids = o.Ids,
            Clock = o.Clock,
            ResolveModel = o.ResolveModel,
            SessionTokens = o.SessionTokens,
            BackgroundWork = Background,
            IsIdle = sessionId => kit?._registry is not { } registry || (registry.ActiveTopLevelRun(sessionId) is null && !registry.IsSessionBusy(sessionId)),
            Deliver = (sessionId, input) => kit?._registry?.DeliverInternal(sessionId, input) ?? InternalDelivery.Failed,
            SessionExists = o.Sessions.Exists,
            EvaluatorModel = o.EvaluatorModel,
            ProposalPolicy = o.ProposalPolicy,
            Planning = (runId, sessionId) => plans.InWorkflow(runId) || plans.ActiveFor(sessionId) is not null,
            Interactive = o.Interactive,
            CheckinInterval = o.CheckinInterval,
            CheckinIntervalSource = o.CheckinIntervalSource,
            EvaluationTimeout = o.EvaluationTimeout,
            Gate = o.Gate,
            PermissionRuns = o.PermissionRuns,
            Log = o.Log,
            Telemetry = o.Telemetry,
        });

        var extensions = new List<IAgentExtension>();
        var runExtensions = new List<IRunExtension>();
        if (o.EnablePlanMode)
        {
            extensions.Add(new PlanModeExtension(plans));
            runExtensions.Add(new PlanRunExtension(plans));
        }

        if (o.EnableGoals)
        {
            extensions.Add(new GoalExtension(goals));
            runExtensions.Add(new GoalRunExtension(goals));
        }

        if (o.EnableTodos)
        {
            extensions.Add(new TodoExtension(new TodoOptions
            {
                Transcript = transcript,
                Ids = o.Ids,
                Clock = o.Clock,
                // 目标在续跑 / 计划活着 / 后台子代理在跑:补报只是进度核对,不能覆盖它们的裁决,也不能催计划模式写平行清单
                YieldTo = sessionId => goals.Store.Get(sessionId) is not null || plans.ActiveFor(sessionId) is not null || Background(sessionId).Count > 0,
            }));
        }

        kit = new PlanningKit(plans, goals, extensions, runExtensions);
        return kit;
    }
}
