using PolyType;

namespace Ncw.Protocol.Methods;

/// <summary>
/// 子代理 / 计划 / 目标的方法表和主题(docs/design/09 §8.4,功能盘点 10 的 <c>agents</c> 与 <c>goal</c> 模块;<c>runs.background</c> 在 <see cref="ConversationMethods"/>)。
/// 每个方法声明种类(command 必带幂等键)、所需角色、所需能力、允许的承载。
/// <list type="bullet">
/// <item><b>子代理</b>(能力 <see cref="Capabilities.Subagents"/>):定义的列表 / 诊断读取 viewer 可;启停是设备配置(owner);手动汇报是对话动作(member)。
/// 子代理的运行事件(<c>subagent_start / update / end</c>)是父 Run 事件日志里的普通事件,走 <c>run/&lt;id&gt;</c> 主题(能力 <c>conversation</c>),本类不重复。</item>
/// <item><b>目标 / 计划</b>(能力 <see cref="Capabilities.Planning"/>):<c>goal.get</c> viewer;<c>goal.set / clear</c>、<c>runs.executePlan</c> member;
/// <c>goal</c> 主题的快照与增量是同一个全量类型(见 <see cref="GoalsTopicState"/> / <see cref="GoalChangeDto"/>)。</item>
/// </list>
/// </summary>
public static class OrchestrationMethods
{
    // ── subagents ──

    /// <summary>这个工作区磁盘上有哪些子代理定义(内建 + 全局 + 项目,含被关掉的:<see cref="AgentInfoDto.Enabled"/> 标出来)。只读,不碰运行中的注册表。</summary>
    public static readonly MethodDef<SubagentsListParams, SubagentsListResult> SubagentsList =
        Query<SubagentsListParams, SubagentsListResult>("subagents.list", Role.Viewer, Capabilities.Subagents);

    /// <summary>加载诊断(哪个文件为什么被跳过)。带稳定 key,控制端翻译。</summary>
    public static readonly MethodDef<SubagentsListParams, SubagentsDiagnosticsResult> SubagentsDiagnostics =
        Query<SubagentsListParams, SubagentsDiagnosticsResult>("subagents.diagnostics", Role.Viewer, Capabilities.Subagents);

    /// <summary>启用 / 关闭一个子代理(设备级偏好,存 <c>agents.disabled</c>)。关掉的既不进 <c>Task</c> 的清单也不能被派遣。</summary>
    public static readonly MethodDef<SubagentsSetEnabledParams, Unit> SubagentsSetEnabled =
        Command<SubagentsSetEnabledParams, Unit>("subagents.setEnabled", Role.Owner, Capabilities.Subagents);

    /// <summary>用户在卡片上点「处理」:把后台子代理的结果汇报给主代理(重启后、或自动那一趟被挡住之后)。结果是汇报进度,不是错误。</summary>
    public static readonly MethodDef<SubagentsReportParams, SubagentsReportResult> SubagentsReport =
        Command<SubagentsReportParams, SubagentsReportResult>("subagents.report", Role.Member, Capabilities.Subagents);

    // ── goal ──

    /// <summary>某个会话当前的目标(先从转录恢复)。没有目标 <see cref="GoalStateDto.Goal"/> 为 null。</summary>
    public static readonly MethodDef<SessionRefParams, GoalStateDto> GoalGet =
        Query<SessionRefParams, GoalStateDto>("goal.get", Role.Viewer, Capabilities.Planning);

    /// <summary>用户设定目标:校验 → 生效 → kickoff 经内部输入交给会话(Run 在跑就进它的引导队列,空闲就起一个内部 Run)。校验失败不是错误,是 <see cref="GoalSetResultDto.Outcome"/>。</summary>
    public static readonly MethodDef<GoalSetParams, GoalSetResultDto> GoalSet =
        Command<GoalSetParams, GoalSetResultDto>("goal.set", Role.Member, Capabilities.Planning);

    /// <summary>用户清除目标(幂等:没有目标也不报错)。</summary>
    public static readonly MethodDef<SessionRefParams, Unit> GoalClear =
        Command<SessionRefParams, Unit>("goal.clear", Role.Member, Capabilities.Planning);

    // ── plan ──

    /// <summary>
    /// 批准计划之后以 <c>code</c> 模式发起执行 Run:和 <c>runs.start</c> 同一套参数,外加那份批准的计划引用(设备在 Run 开始前登记「这个 Run 是执行计划」)。
    /// 会话忙时是 <c>busy</c>(可重试),不进队列:计划只属于这个 RunId。
    /// </summary>
    public static readonly MethodDef<RunsExecutePlanParams, RunsStartResult> RunsExecutePlan =
        Command<RunsExecutePlanParams, RunsStartResult>("runs.executePlan", Role.Member, Capabilities.Planning);

    // ── 主题 ──

    /// <summary>
    /// 本设备上所有会话的目标。<b>快照和增量(<see cref="GoalEvents.Changed"/>)里每个会话的目标都是全量</b>,带单调的 <c>Rev</c>(设备上每次目标变化加一):
    /// 控制端按 <c>(deviceId, sessionId)</c> 镜像,只收 <c>Rev</c> 不更旧的;清除也带 <c>Rev</c>(墓碑),所以乱序到达的旧事件不能让清掉的目标复活。
    /// 目标是进程内状态:快照只含内存里已有的(会话被打开 / 恢复时才进来),要取某个会话的用 <see cref="GoalGet"/>(它会先恢复,恢复本身也会广播)。
    /// </summary>
    public static readonly TopicDef<GoalsTopicState> GoalTopic = new("goal", PayloadShapes.GetShape<GoalsTopicState>());

    public static readonly IReadOnlyList<MethodDef> All =
    [
        SubagentsList, SubagentsDiagnostics, SubagentsSetEnabled, SubagentsReport,
        GoalGet, GoalSet, GoalClear,
        RunsExecutePlan,
    ];

    private static MethodDef<TP, TR> Query<TP, TR>(string name, Role role, string capability) =>
        new(name, MethodKind.Query, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = capability };

    private static MethodDef<TP, TR> Command<TP, TR>(string name, Role role, string capability) =>
        new(name, MethodKind.Command, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = capability };
}

// ───────────────────────── 子代理:定义 ─────────────────────────

public sealed record SubagentsListParams(string WorkspaceId);

/// <summary>扩展面板里一行子代理定义。</summary>
/// <param name="Scope"><c>builtin</c> / <c>global</c> / <c>project</c>。</param>
/// <param name="Source">定义文件的路径;内建为空串。</param>
/// <param name="Tools">允许用的工具(归一化后的内部 id);null = 继承主 Run 的全部。</param>
/// <param name="PermissionMode">定义里声明的权限档位(<c>ask</c> / <c>auto</c> / <c>full</c>);null = 取父 Run 的。</param>
public sealed record AgentInfoDto(
    string Name,
    string Description,
    string Scope,
    string Source,
    string[]? Tools,
    string? Model,
    string? PermissionMode,
    string? Color,
    bool Enabled);

public sealed record SubagentsListResult(AgentInfoDto[] Agents);

/// <summary>一条加载诊断。<see cref="Key"/> 是稳定的 <c>agents.*</c> key(控制端翻译),<see cref="Message"/> 是英文,给日志和排错。</summary>
public sealed record AgentDiagnosticDto(string Path, string Key, string Message, Dictionary<string, string>? Params = null);

public sealed record SubagentsDiagnosticsResult(AgentDiagnosticDto[] Diagnostics);

public sealed record SubagentsSetEnabledParams(string Name, bool Enabled);

public sealed record SubagentsReportParams(string SessionId, string CallId);

/// <param name="Status"><c>none</c>(没有这张回执)/ <c>pending</c> / <c>injecting</c> / <c>reported</c> / <c>blocked</c>。</param>
public sealed record SubagentsReportResult(string Status);

// ───────────────────────── 子代理:父 Run 事件日志里的三个事件 ─────────────────────────

/// <summary>
/// 子 Run 拿到名额、开始运行(父 Run 的 <c>run/&lt;id&gt;</c> 主题)。<b>子会话 id 由设备带过来</b>(<see cref="ChildSessionId"/>),控制端不许从 id 里拼:
/// 子 Run id 是递归拼出来的(<c>&lt;父 RunId&gt;:sub:N</c>),权威在库里的 <c>parent_session_id</c>。排队中的子代理<b>还没有</b>这个事件:排位走 <c>tool_progress</c>,
/// 它的 <c>DetailsJson</c> 是 <c>{"kind":"queued","ahead":N}</c>(<see cref="ToolQueueDetails"/>)。
/// </summary>
public sealed record SubagentStartEventDto(
    string CallId,
    string ChildRunId,
    string ChildSessionId,
    string AgentType,
    string Description,
    string? Color,
    string Model,
    bool Background,
    long At) : RunEventDto;

/// <summary>「键存在但值为空 = 清除」的字符串补丁(<see cref="SubagentUpdateEventDto"/> 的字段语义)。字段本身为 null = 保持原值。</summary>
public sealed record StringPatchDto(string? Value);

public sealed record SubagentNoticeDto(string Kind, string Reason, int? Attempt = null, string? To = null);

public sealed record SubagentNoticePatchDto(SubagentNoticeDto? Value);

public sealed record SubagentContextUsageDto(int Used, int Window, bool ShouldCompact);

/// <summary>
/// 子 Run 的遥测镜像(工具开始 / 结束、上下文用量、消息结束)。字段语义:<b>为 null = 保持</b>;补丁类型(<see cref="StringPatchDto"/> / <see cref="SubagentNoticePatchDto"/>)
/// 的 <c>Value</c> 为 null = 清除。<see cref="Phase"/> 是 <c>starting</c> / <c>background</c> / <c>tool</c> / <c>thinking</c> / <c>finishing</c>。
/// </summary>
public sealed record SubagentUpdateEventDto(
    string CallId,
    string ChildRunId,
    string? Phase = null,
    StringPatchDto? CurrentTool = null,
    StringPatchDto? CurrentTarget = null,
    int? ToolCalls = null,
    int? ToolErrors = null,
    UsageDto? Usage = null,
    SubagentContextUsageDto? ContextUsage = null,
    SubagentNoticePatchDto? Notice = null,
    long? ChildSeq = null,
    long? At = null) : RunEventDto;

/// <summary>
/// 子 Run 终态(<c>done</c> / <c>error</c> / <c>aborted</c>)。<see cref="Summary"/> 是前 240 字的预览(全文在子会话的最后一条助手消息里)。
/// 终态之后的更新、重放的旧 <c>subagent_start</c> 控制端一律忽略(状态单调)。
/// </summary>
public sealed record SubagentEndEventDto(
    string CallId,
    string ChildRunId,
    string Status,
    string? Summary = null,
    ErrorDto? Error = null,
    long? ChildSeq = null,
    long? At = null) : RunEventDto;

/// <summary><c>tool_progress.DetailsJson</c> 里排队中的那一种(<c>Task</c> 工具在等子代理名额):<c>{"kind":"queued","ahead":N}</c>。</summary>
public static class ToolQueueDetails
{
    public const string Kind = "queued";
}

/// <summary><c>session/&lt;id&gt;</c> 主题里与子代理汇报有关的事件类型。</summary>
public static class SubagentEvents
{
    /// <summary>后台子代理的汇报进度变了(数据是 <see cref="SubagentReportDto"/>)。进度是运行期状态(<c>injecting</c> 不落盘),所以走事件而不是消息。</summary>
    public const string Report = "subagent.report";

    /// <summary>
    /// 一个子 Run 结束了(数据是 <see cref="SubagentStoppedDto"/>)。后台子代理常常比父 Run 活得久:父 Run 的日志那时已经收口,<c>subagent_end</c> 进不去,
    /// 所以父会话主题再单独发一条「它结束了」——控制端据此把卡片收口(状态单调:终态压过运行中,已经是终态的不改写)。
    /// </summary>
    public const string Stopped = "subagent.stopped";
}

public sealed record SubagentReportDto(string SessionId, string CallId, string Status);

/// <param name="SessionId">父会话。</param>
/// <param name="Status"><c>done</c> / <c>error</c> / <c>aborted</c>。</param>
public sealed record SubagentStoppedDto(string SessionId, string CallId, string ChildRunId, string ChildSessionId, string AgentType, string Status, bool Background);

// ───────────────────────── 目标 ─────────────────────────

/// <summary>一个会话当前的目标(全量)。时间是 Unix 毫秒;<see cref="Origin"/> 是 <c>user</c> / <c>proposalDirect</c> / <c>proposalApproved</c> / <c>restored</c>。</summary>
public sealed record GoalDto(
    string SessionId,
    string Id,
    string Condition,
    string Origin,
    long SetAt,
    long TokensAtStart,
    int Iterations,
    string? LastReason,
    long? DeferredSince,
    int CheckinCount,
    long? LastDeferralPassAt,
    int IdleCheckinCount,
    long Tokens);

/// <summary><c>goal.get</c> 的结果:<see cref="Rev"/> 是设备上目标变化的单调版本号。</summary>
public sealed record GoalStateDto(string SessionId, GoalDto? Goal, long Rev);

/// <summary><c>goal</c> 主题的快照:内存里所有会话的目标 + 当前版本号。</summary>
public sealed record GoalsTopicState(long Rev, GoalDto[] Goals);

/// <summary>
/// <c>goal</c> 主题的增量 <see cref="GoalEvents.Changed"/>:这个会话的目标(全量;null = 没有)。<see cref="Cleared"/> 是被清掉的原因
/// (<c>userClear</c> / <c>superseded</c> / <c>met</c> / <c>impossible</c> / <c>sessionEnded</c> / <c>runFailed</c>,只在清除时有)。
/// <see cref="Message"/> 是带外写了标记(<c>goal_status</c>)的那条助手消息(只给界面:按 id 替换会话镜像里的那条)。
/// </summary>
public sealed record GoalChangeDto(string SessionId, GoalDto? Goal, string? Cleared, MessageDto? Message, long Rev);

/// <summary>目标主题的事件类型。</summary>
public static class GoalEvents
{
    /// <summary>某个会话的目标变了(数据是 <see cref="GoalChangeDto"/>,全量)。</summary>
    public const string Changed = "goal.changed";
}

/// <param name="Options">
/// 发送档位:设备拿它给 kickoff 起内部 Run(会话从没发过消息时,设备没有别的档位可用)。null = 沿用设备记着的上一次档位;都没有时目标照样设上,只是 kickoff 没送出去
/// (<see cref="GoalSetResultDto.Delivery"/> = <c>failed</c>),下一条用户消息的回合末起开始判定。
/// </param>
public sealed record GoalSetParams(string SessionId, string Condition, SendOptionsDto? Options = null);

/// <summary>
/// <c>goal.set</c> 的结果。<see cref="Outcome"/> = <c>set</c>(带 <see cref="Goal"/> 和 <see cref="Delivery"/>)/ <c>empty</c>(去掉不可见字符后是空的)/ <c>tooLong</c>(超过上限,带 <see cref="Length"/>;不截断)。
/// <see cref="Delivery"/>:<c>interjected</c>(进了正在跑的 Run)/ <c>launched</c>(起了一个新的内部 Run)/ <c>deferred</c>(会话正在收尾,稍后送)/ <c>failed</c>(没送出去)。
/// </summary>
public sealed record GoalSetResultDto(string Outcome, GoalDto? Goal = null, string? Delivery = null, int Length = 0);

public static class GoalOutcomes
{
    public const string Set = "set";
    public const string Empty = "empty";
    public const string TooLong = "tooLong";
}

// ───────────────────────── 计划 ─────────────────────────

/// <summary>批准的计划文件的引用(取自 <c>plan_approval</c> 交互的 <c>PlanId</c> / <c>Path</c>)。</summary>
public sealed record PlanFileDto(string PlanId, string Path);

/// <summary><c>runs.executePlan</c>:一次普通的 <c>runs.start</c>,外加这个 Run 要执行的批准计划。</summary>
public sealed record RunsExecutePlanParams(RunsStartParams Start, PlanFileDto Plan);

/// <summary>子代理 / 目标 / 计划的载荷形状(与其它领域同属一个 <c>PayloadShapes</c>)。</summary>
[GenerateShapeFor<SubagentsListParams>]
[GenerateShapeFor<SubagentsListResult>]
[GenerateShapeFor<SubagentsDiagnosticsResult>]
[GenerateShapeFor<SubagentsSetEnabledParams>]
[GenerateShapeFor<SubagentsReportParams>]
[GenerateShapeFor<SubagentsReportResult>]
[GenerateShapeFor<SubagentReportDto>]
[GenerateShapeFor<SubagentStoppedDto>]
[GenerateShapeFor<GoalStateDto>]
[GenerateShapeFor<GoalsTopicState>]
[GenerateShapeFor<GoalChangeDto>]
[GenerateShapeFor<GoalSetParams>]
[GenerateShapeFor<GoalSetResultDto>]
[GenerateShapeFor<RunsExecutePlanParams>]
public sealed partial class PayloadShapes;
