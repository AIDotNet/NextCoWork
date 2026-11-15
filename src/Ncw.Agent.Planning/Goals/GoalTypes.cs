using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;
using Ncw.Core.Domain;

namespace Ncw.Agent.Extensions.Planning.Goals;

/// <summary>设置里的「模型自提目标」:<c>auto</c>(模型断言「用户就是这么说的」时直接设立)/ <c>alwaysAsk</c>(一律弹审批)/ <c>disabled</c>(不下发 <c>ProposeGoal</c>)。设置是可信的用户状态:仓库里的文件改不了同意策略。</summary>
public enum GoalProposalPolicy
{
    Auto,
    AlwaysAsk,
    Disabled,
}

/// <summary>
/// 目标层对一个 Run 需要知道的事实(Run 开始时冻结)。<see cref="EvaluatorModel"/> 与 <see cref="Model"/> 是<b>成对</b>冻结的(别名 + 供应商):
/// 中途改设置不该换掉正在跑的这一轮的判定器,下一轮才换。<see cref="Options"/> 是发送档位快照:空闲时起内部 Run(检查、kickoff)续用它。
/// </summary>
public sealed record GoalRunContext(RunId RunId, string WorkspaceId, ModelRef Model, ModelRef? EvaluatorModel, SendOptions? Options);

public sealed class GoalOptions
{
    public required ILlmClient Llm { get; init; }

    /// <summary>标记落在转录的哪里 / 从哪里恢复(生产是 <see cref="SessionGoalTranscript"/>)。</summary>
    public required IGoalTranscript Transcript { get; init; }

    public IIdGenerator Ids { get; init; } = new UlidIdGenerator();

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>别名 → 绑定(判定要它算窗口和思考档位)。查不到 = 这次判定 skipped(<c>no_model</c>)。</summary>
    public Func<ModelRef, ModelBinding?> ResolveModel { get; init; } = static _ => null;

    /// <summary>这个会话累计用掉的 token(从用量账本读);目标的 <c>Tokens</c> = 现在 − 设定那一刻。</summary>
    public Func<string, long> SessionTokens { get; init; } = static _ => 0;

    /// <summary>会话名下跑着的后台工作(后台子代理):有就推迟判定。装配方从 <c>RunRegistry.ActiveBackgroundChildrenOfSession</c> 映射。</summary>
    public Func<string, IReadOnlyList<BackgroundTaskLine>> BackgroundWork { get; init; } = static _ => [];

    /// <summary>会话此刻没有任何 Run 在占着(空闲唤醒的前提)。</summary>
    public Func<string, bool> IsIdle { get; init; } = static _ => true;

    /// <summary>交付内部输入(目标检查、kickoff):Run 在跑进它的引导队列,会话在收尾就排到之后,空闲就起一个新的内部 Run。生产是 <c>RunRegistry.DeliverInternal</c>。</summary>
    public Func<string, InternalInput, InternalDelivery> Deliver { get; init; } = static (_, _) => InternalDelivery.Failed;

    public Func<string, bool> SessionExists { get; init; } = static _ => true;

    /// <summary>判定模型(设置里的 <c>goalEvaluatorModel</c> + 供应商,成对)。<c>null</c> = 用 Run 的模型。每个 Run 开始时读一次并冻结。</summary>
    public Func<ModelRef?> EvaluatorModel { get; init; } = static () => null;

    public Func<GoalProposalPolicy> ProposalPolicy { get; init; } = static () => GoalProposalPolicy.Auto;

    /// <summary>这个 Run 在计划模式里吗(有活动计划,或计划工作流开着):是就不许提目标——让模型先把计划走完再提。</summary>
    public Func<RunId, string, bool> Planning { get; init; } = static (_, _) => false;

    /// <summary>这个会话是不是交互式的(定时任务起的会话不是)。</summary>
    public Func<string, bool> Interactive { get; init; } = static _ => true;

    /// <summary>检查间隔的基数(2.x 的 <c>CLAUDE_CODE_GOAL_CHECKIN_MINUTES</c>,低于下限的抬到 1 分钟)。<c>null</c> = 30 分钟。</summary>
    public TimeSpan? CheckinInterval { get; init; }

    /// <summary>检查间隔基数的动态来源(设置页「目标检查间隔」,每次现读:改完立即对下一次空闲检查生效)。优先于 <see cref="CheckinInterval"/>;返回 null = 回退到它。</summary>
    public Func<TimeSpan?>? CheckinIntervalSource { get; init; }

    /// <summary>此刻的检查间隔基数(动态来源优先,其次固定值;都没有 = null = 30 分钟)。</summary>
    public TimeSpan? ResolveCheckinInterval() => CheckinIntervalSource?.Invoke() ?? CheckinInterval;

    /// <summary>一次判定的墙钟(2.x 的 prompt 钩子缺省 30 秒)。</summary>
    public TimeSpan EvaluationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>交互闸门:目标提案经它发出(<c>goal_proposal</c>)、用户设定 / 清除时按会话撤掉待决的提案。没有 = 不能提案。</summary>
    public InteractionGate? Gate { get; init; }

    /// <summary>Run → 权限状态(取交互句柄,子代理拿到的句柄不能提问)。</summary>
    public PermissionRuns? PermissionRuns { get; init; }

    /// <summary>此刻这个顶层 Run 还在运行、没被要求中止吗(提案的前提)。<c>null</c> = 用 <see cref="GoalRunExtension"/> 登记的 Run 句柄判断(没登记过 = 认为是)。</summary>
    public Func<RunId, bool>? RunIsLive { get; init; }

    /// <summary>这个 Run 被要求中止过吗(批准条的过期条件:Run 中止了,正常收尾不算)。<c>null</c> = 用 <see cref="GoalRunExtension"/> 登记的 Run 句柄判断。</summary>
    public Func<RunId, bool>? RunAborted { get; init; }

    public ILoopDiagnostics Log { get; init; } = NullLoopDiagnostics.Instance;

    /// <summary>遥测:<c>goal_set / goal_cleared / goal_evaluated / goal_checkin_* …</c> 加键值(2.x 的 <c>[goal] event {json}</c>)。缺省丢掉。</summary>
    public Action<string, IReadOnlyDictionary<string, string>>? Telemetry { get; init; }
}

/// <summary>回合末判定的结论(扩展把它映射成 <see cref="TurnEndDecision"/>)。</summary>
public abstract record GoalTurnDecision
{
    private GoalTurnDecision()
    {
    }

    /// <summary>不表态(没有目标 / 子 Run / 中断 / 这次判定没判成)。</summary>
    public sealed record None : GoalTurnDecision;

    /// <summary>未达成(或检查):再跑一个回合,先注入这段文字。<see cref="Status"/> 是要盖在转录上的标记。</summary>
    public sealed record Continue(string Inject, GoalStatusPart? Status) : GoalTurnDecision;

    /// <summary>
    /// 收尾:达成 / 判不可能(<see cref="Status"/> 是标记,目标已清)、后台推迟(<see cref="AcceptPendingInput"/>)、或空转刹车(<see cref="IdleStreak"/> 非空:目标留着,只给一条警告)。
    /// </summary>
    public sealed record Finish(GoalStatusPart? Status, string Note, bool AcceptPendingInput = false, int? IdleStreak = null) : GoalTurnDecision;
}

public enum GoalProposalStatus
{
    /// <summary>直接设立了(<c>ask_user=false</c> 且策略是 auto)。</summary>
    Set,

    /// <summary>派发给了用户,等审批;审批结果不再回到模型。</summary>
    Pending,
}

/// <summary>用户手动设目标的结果(<c>goal.set</c>)。</summary>
public abstract record GoalSetResult
{
    private GoalSetResult()
    {
    }

    public sealed record Ok(ActiveGoal Goal, InternalDelivery Delivery) : GoalSetResult;

    public sealed record Invalid(GoalInvalidReason Reason, int Length) : GoalSetResult;
}
