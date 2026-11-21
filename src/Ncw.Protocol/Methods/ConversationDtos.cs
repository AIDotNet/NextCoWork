using PolyType;

namespace Ncw.Protocol.Methods;

// 对话链路的线上类型(docs/design/09 §8.4、§8.5):会话、Run、排队消息、交互、上下文。
// 这里只放「线上长什么样」:Agent 的领域类型(Message / AgentEvent / QueueItem / InteractionRequest)不进协议,Host 负责来回映射。
// 全部是不可变记录,MessagePack 字符串键,未知字段忽略;可选字段缺省为 null / 空数组,新增字段不改协议版本。
// 枚举一律用小写字符串(见各 *Names 常量类),控制端不认识的值按「未知」处理,不报错。
// 消息内容块和工具输入 / 详情是 JSON 文本:块的形状只有一份定义(Agent 会话层的 MessageCodec 的存储形状,docs/design/08.04),协议不再镜像一份类型层次。

// ───────────────────────── 通用 ─────────────────────────

/// <summary>一条结构化错误(Run 的终态错误、压缩失败等)。本层不含界面文字:控制端按 <see cref="MessageKey"/> 翻译,<see cref="Message"/> 是英文诊断。</summary>
public sealed record ErrorDto(
    string Code,
    string Message,
    bool Retryable,
    int? Status = null,
    int? RetryAfterMs = null,
    string? MessageKey = null,
    Dictionary<string, string>? MessageParams = null);

/// <summary>
/// 执行环境标识(设备 + 环境)。审批和工具卡片必须带上它(AGENTS.md §5)。审批(<see cref="ToolPermissionInteractionDto"/>、SSH 交互)里四个字段都是全的;
/// <c>tool_start</c> 里只有 <see cref="DeviceId"/> + <see cref="Environment"/>(内核的环境标签只有这两个 id),<see cref="Name"/> / <see cref="Type"/> 为空串——
/// 控制端按 <c>environments</c> 镜像里的 (设备, 环境 id) 取显示名和种类(一个事实只有一个真源)。
/// </summary>
/// <param name="Type"><c>local</c> / <c>ssh</c>。</param>
public sealed record EnvRefDto(string DeviceId, string Environment, string Name, string Type);

/// <summary>一次请求的用量。</summary>
public sealed record UsageDto(
    long InputTokens,
    long OutputTokens,
    long? CacheCreationInputTokens = null,
    long? CacheReadInputTokens = null,
    long? CacheCreation1hInputTokens = null,
    long? ReasoningTokens = null);

/// <summary>费用三态:<c>notMetered</c> / <c>unknown</c> / <c>known</c>(金额是整数微单位)。</summary>
public sealed record CostDto(string State, long Micros = 0, string Currency = "usd");

/// <summary>
/// 一条消息(用户 / 助手)。<see cref="PartsJson"/> 是内容块数组的 JSON 文本(2.x 的 <c>parts</c> 列形状,块类型见 docs/design/08.04);
/// <see cref="MetaJson"/> 是 3.0 新增的来源和停止原因(可空,2.x 写的旧消息没有)。
/// </summary>
public sealed record MessageDto(string Id, string Role, long CreatedAt, bool Internal, string PartsJson, string? MetaJson = null);

/// <summary>用户输入里的一个块:文本、图片或文件引用(排队消息和 <c>runs.start</c> 共用)。</summary>
/// <param name="Kind"><see cref="InputPartKinds"/> 之一。</param>
public sealed record InputPartDto(string Kind, string? Text = null, string? Mime = null, string? DataRef = null, string? Path = null, string? Name = null);

public static class InputPartKinds
{
    public const string Text = "text";
    public const string Image = "image";
    public const string File = "file";
}

/// <summary>发送档位(逐条冻结,排队 5 分钟里改两次模型,三条消息就是三份档位)。</summary>
/// <param name="Model">模型别名;空 = 用会话上记住的那个。</param>
/// <param name="ModelProviderId">非空 = 用户固定了供应商(硬约束)。</param>
/// <param name="Thinking"><c>auto</c> / <c>off</c> / <c>minimal</c> / <c>low</c> / <c>medium</c> / <c>high</c> / <c>xhigh</c> / <c>max</c>。</param>
/// <param name="Mode">会话模式(例如 <c>code</c>);空 = 会话当前的。</param>
/// <param name="PermissionMode"><c>ask</c> / <c>auto</c> / <c>full</c>;空 = <c>ask</c>。<c>full</c> 只有 owner 能用。</param>
public sealed record SendOptionsDto(string Model, string? ModelProviderId = null, string Thinking = "auto", bool MaxContext = false, string Mode = "", string PermissionMode = "");

// ───────────────────────── 会话 ─────────────────────────

public sealed record SessionDto(
    string Id,
    string WorkspaceId,
    string? ParentSessionId,
    string Origin,
    string Title,
    string? TitleSource,
    string Model,
    string? ModelProviderId,
    string Mode,
    string Thinking,
    bool Archived,
    bool Favorited,
    long CreatedAt,
    long UpdatedAt,
    string PermissionMode = "ask");

/// <summary>侧边栏的一行。运行状态不在这里:「这条会话在不在跑」只由 <c>device</c> 主题的 activeRuns 回答(一个事实一个真源)。<see cref="IsEmpty"/> 表示尚未发出任何消息、仍为默认标题且未归档的空白新会话。</summary>
public sealed record SessionListItemDto(string Id, string Title, long UpdatedAt, bool Archived, bool Favorited, bool SkillExtraction, bool IsEmpty = false);

public sealed record SessionsListParams(string WorkspaceId, bool? Archived = null);

public sealed record SessionsListResult(SessionListItemDto[] Sessions);

/// <summary>只带会话 id 的参数(<c>sessions.get</c>、<c>sessions.delete</c>、<c>queue.list</c>)。</summary>
public sealed record SessionRefParams(string SessionId);

public sealed record SessionsGetPageParams(string SessionId, int Limit, string? BeforeMessageId = null);

/// <summary>一次累计用量(从 <c>usage_records</c> 聚合)。<see cref="Cost"/> 为 null = 这一轮算不齐,界面不画金额(绝不是 0)。</summary>
public sealed record RunUsageDto(
    long InputTokens,
    long OutputTokens,
    long CacheReadInputTokens,
    long CacheCreationInputTokens,
    long CacheCreation1hInputTokens,
    long? ReasoningTokens,
    long? UpstreamMs,
    CostDto? Cost);

/// <summary>整页会话详情(<c>sessions.get</c> 是全部,<c>sessions.getPage</c> 是一页)。</summary>
/// <param name="MessageRuns">消息 id → 产生它的 Run id。</param>
/// <param name="RunUsage">Run id → 用量。</param>
/// <param name="RunModel">Run id → 用的模型别名。</param>
public sealed record SessionPageDto(
    SessionDto Session,
    MessageDto[] Messages,
    bool HasMore,
    Dictionary<string, string> MessageRuns,
    Dictionary<string, RunUsageDto> RunUsage,
    Dictionary<string, string> RunModel);

public sealed record SessionsCreateParams(
    string WorkspaceId,
    string? Id = null,
    string? Title = null,
    string? Model = null,
    string? ModelProviderId = null,
    string? Mode = null,
    string? Thinking = null,
    string? PermissionMode = null);

public sealed record SessionsRenameParams(string SessionId, string Title);

public sealed record SessionsSetModeParams(string SessionId, string Mode);

public sealed record SessionsSetModelParams(string SessionId, string Model, string? ModelProviderId = null);

public sealed record SessionsSetPermissionModeParams(string SessionId, string PermissionMode);

public sealed record SessionsArchiveParams(string SessionId, bool Archived);

public sealed record SessionsFavoriteParams(string SessionId, bool Favorited);

public sealed record SessionsDuplicateParams(string SessionId, string? Title = null);

/// <summary>分支:复制到 <see cref="UptoMessageId"/>(含)为止的前缀到一个新会话。</summary>
public sealed record SessionsBranchParams(string SessionId, string UptoMessageId, string? Title = null);

/// <summary>改一条用户消息的文字;<see cref="Truncate"/> 为真时同时丢掉它之后的全部(「编辑并重新生成」)。</summary>
public sealed record SessionsEditMessageParams(string SessionId, string MessageId, string Text, bool Truncate);

public sealed record SessionsDeleteTurnParams(string SessionId, string UserMessageId);

/// <summary>删除 <see cref="FromId"/> 到 <see cref="ToId"/> 之间的一段回复(两端含)。</summary>
public sealed record SessionsDeleteReplyParams(string SessionId, string FromId, string ToId);

// ───────────────────────── 排队消息 ─────────────────────────

public sealed record QueueItemDto(
    string Id,
    InputPartDto[] Parts,
    string State,
    long PromotedOrder,
    long EnqueuedAt,
    long? PromotedAt,
    SendOptionsDto Options);

public static class QueueStates
{
    public const string Pending = "pending";
    public const string Promoted = "promoted";
}

/// <summary>
/// 一个会话排队消息的权威全量快照(<c>queue.updated</c> 的数据,也是 <c>session/&lt;id&gt;</c> 主题的队列半边)。
/// <see cref="Rev"/> <b>全局</b>单调递增:控制端只收比手里更新的那份。
/// </summary>
public sealed record QueueDto(string SessionId, long Rev, QueueItemDto[] Items);

/// <summary>队列操作的回执。<see cref="Accepted"/> = false:被拒绝(超上限、找不到、同 id 已存在),队列没变,控制端据此保留草稿。</summary>
public sealed record QueueResultDto(bool Accepted, QueueDto Queue);

public sealed record QueueEnqueueParams(string SessionId, string? MessageId, InputPartDto[] Parts, SendOptionsDto Options);

/// <summary><c>queue.promote</c> 是开关:没引入的引入(插话),已引入的退回排队。</summary>
public sealed record QueueItemParams(string SessionId, string ItemId);

// ───────────────────────── Run ─────────────────────────

/// <summary>
/// <c>runs.start</c>。<see cref="RunId"/> 和 <see cref="MessageId"/> 由控制端生成(送达以同 id 的 <c>message_end</c> 为准;重发同一个幂等键拿到同一个结果)。
/// 会话已有运行中的顶层 Run 时,这句话进队列(<see cref="RunsStartResult.Outcome"/> = <c>queued</c>),不是失败。
/// </summary>
public sealed record RunsStartParams(string RunId, string SessionId, string MessageId, InputPartDto[] Input, SendOptionsDto Options);

public static class StartOutcomes
{
    public const string Started = "started";
    public const string Queued = "queued";
}

/// <summary><see cref="Queue"/> 只在 <c>queued</c> 时有值(回执里的新队列)。</summary>
public sealed record RunsStartResult(string Outcome, string RunId, QueueDto? Queue = null);

public sealed record RunsAbortParams(string RunId, bool Cascade = true);

/// <summary>插话:把这句话立刻引入正在跑的 Run(回合边界送达)。等于入队再引入;会话空闲时就是普通发送。</summary>
public sealed record RunsSteerParams(string SessionId, string MessageId, InputPartDto[] Parts, SendOptionsDto Options);

public sealed record RunsBackgroundParams(string RunId);

/// <summary>运行中改权限档位(<c>ask</c> / <c>auto</c> / <c>full</c>)。只对还没做出的判定生效。只有 owner。</summary>
public sealed record RunsSetPermissionModeParams(string RunId, string Mode);

// ───────────────────────── Run 事件(run/<id> 主题) ─────────────────────────

/// <summary>
/// 一个 Run 的事件。线上形状是 Agent 事件(docs/design/08.02 §3)的一一映射:<c>seq</c> 在外层(<see cref="SeqEventDto"/> / 事件帧),这里是事件本身。
/// 只推增量(<c>message_delta</c>),<c>message_end</c> 带完整消息,控制端用它替换累积的增量。
/// </summary>
[DerivedTypeShape(typeof(RunStartEventDto), Name = "run_start")]
[DerivedTypeShape(typeof(TurnStartEventDto), Name = "turn_start")]
[DerivedTypeShape(typeof(MessageStartEventDto), Name = "message_start")]
[DerivedTypeShape(typeof(MessageDeltaEventDto), Name = "message_delta")]
[DerivedTypeShape(typeof(MessageEndEventDto), Name = "message_end")]
[DerivedTypeShape(typeof(MessageDiscardEventDto), Name = "message_discard")]
[DerivedTypeShape(typeof(ProviderRetryEventDto), Name = "provider_retry")]
[DerivedTypeShape(typeof(ProviderSwitchEventDto), Name = "provider_switch")]
[DerivedTypeShape(typeof(ToolStartEventDto), Name = "tool_start")]
[DerivedTypeShape(typeof(ToolProgressEventDto), Name = "tool_progress")]
[DerivedTypeShape(typeof(ToolEndEventDto), Name = "tool_end")]
[DerivedTypeShape(typeof(NotificationEventDto), Name = "notification")]
[DerivedTypeShape(typeof(TurnEndEventDto), Name = "turn_end")]
[DerivedTypeShape(typeof(RunEndEventDto), Name = "run_end")]
[DerivedTypeShape(typeof(ContextUsageEventDto), Name = "context_usage")]
[DerivedTypeShape(typeof(ContextStatusEventDto), Name = "context_status")]
[DerivedTypeShape(typeof(InteractionRequestEventDto), Name = "interaction_request")]
[DerivedTypeShape(typeof(InteractionResolvedEventDto), Name = "interaction_resolved")]
[DerivedTypeShape(typeof(RunSettledEventDto), Name = "run_settled")]
[DerivedTypeShape(typeof(SubagentStartEventDto), Name = "subagent_start")]
[DerivedTypeShape(typeof(SubagentUpdateEventDto), Name = "subagent_update")]
[DerivedTypeShape(typeof(SubagentEndEventDto), Name = "subagent_end")]
public abstract record RunEventDto;

public sealed record RunStartEventDto(string RunId, string SessionId, string Model, string? ProviderId = null) : RunEventDto;

public sealed record TurnStartEventDto(int TurnIndex) : RunEventDto;

public sealed record MessageStartEventDto(string MessageId, string Role, string? Model = null, string? ProviderId = null) : RunEventDto;

/// <param name="Kind"><c>text</c> / <c>thinking</c> / <c>toolArgs</c>。toolArgs 的第一条带 <see cref="ToolName"/>(增量为空串)。</param>
public sealed record MessageDeltaEventDto(string MessageId, int ContentIndex, string Kind, string Delta, string? CallId = null, string? ToolName = null) : RunEventDto;

/// <param name="Stop"><c>endTurn</c> / <c>toolUse</c> / <c>maxTokens</c> / <c>stopSequence</c> / <c>refusal</c> / <c>aborted</c>。</param>
public sealed record MessageEndEventDto(MessageDto Message, UsageDto? Usage = null, CostDto? Cost = null, string? Stop = null) : RunEventDto;

public sealed record MessageDiscardEventDto(string MessageId) : RunEventDto;

public sealed record ProviderRetryEventDto(int Attempt, int DelayMs, string Reason) : RunEventDto;

public sealed record ProviderSwitchEventDto(string From, string To, string Reason) : RunEventDto;

/// <summary>工具开始。<see cref="Environment"/> 说清在哪台设备的哪个环境里执行(AGENTS.md §5)。</summary>
public sealed record ToolStartEventDto(string CallId, string ToolName, string InputJson, EnvRefDto? Environment = null) : RunEventDto;

public sealed record ToolProgressEventDto(string CallId, string? Message = null, double? Fraction = null, string? DetailsJson = null) : RunEventDto;

public sealed record ToolImageDto(string Mime, string DataRef);

/// <summary>工具结束。失败也必须有这一条(每个 <c>tool_start</c> 恰好对应一个)。</summary>
public sealed record ToolEndEventDto(
    string CallId,
    string Content,
    ToolImageDto[]? Images,
    bool Truncated,
    long? OriginalBytes,
    string? DetailsJson,
    bool IsError,
    long DurationMs) : RunEventDto;

public sealed record NotificationEventDto(string Key, string? Detail = null) : RunEventDto;

public sealed record TurnEndEventDto(int TurnIndex) : RunEventDto;

/// <summary>终态。此时可能还没落库完,<c>run_settled</c> 才是真的结束。</summary>
public sealed record RunEndEventDto(string Status, ErrorDto? Error = null) : RunEventDto;

public sealed record ContextSegmentDto(string Kind, int Tokens);

public sealed record ContextUsageEventDto(int Used, int Window, bool ShouldCompact, ContextSegmentDto[] Segments) : RunEventDto;

/// <param name="Phase"><c>compacting</c> / <c>compacted</c> / <c>failed</c> / <c>disabled</c>。</param>
public sealed record ContextStatusEventDto(string Phase, string Trigger) : RunEventDto;

public sealed record InteractionRequestEventDto(InteractionDto Interaction) : RunEventDto;

public sealed record InteractionResolvedEventDto(ResolutionDto Resolution, InteractionResponseDto? Response = null) : RunEventDto;

/// <summary>落库、释放会话互斥之后才发。下一个 Run 只等它。</summary>
public sealed record RunSettledEventDto(string Status) : RunEventDto;

public static class RunStatusNames
{
    public const string Running = "running";
    public const string Done = "done";
    public const string Error = "error";
    public const string Aborted = "aborted";
}

/// <summary>带 <c>seq</c> 的事件(快照里的重放日志)。<c>seq</c> 每个 Run 一套、严格递增;裁剪后可能有空洞,控制端不做「seq == last + 1」的检查。</summary>
public sealed record SeqEventDto(long Seq, RunEventDto Event);

/// <summary>
/// <c>run/&lt;id&gt;</c> 主题的快照(docs/design/09 §8.2):Run 状态、当前 <c>seq</c>、日志里的事件、挂起的交互。
/// 主题的 <c>seq</c> 就是 Run 事件日志的序号:控制端断线重连时带上收到的最后一个 <c>seq</c> 作为游标,设备只补发之后的事件;
/// 游标早已被日志上限挤出去(<see cref="Cursor"/> = <c>lost</c>)时给现存的全部事件,控制端以此重建。
/// </summary>
/// <param name="State"><see cref="RunStatusNames"/> 之一。</param>
/// <param name="Cursor"><c>fresh</c>(没给游标)/ <c>lost</c>(游标已失效)。游标续得上时不发快照,直接补发事件帧。</param>
/// <param name="Pending">这个 Run 挂起的交互(权威状态在交互闸门,这里是快照时刻的副本)。</param>
public sealed record RunSnapshotDto(
    string RunId,
    string SessionId,
    string WorkspaceId,
    string? ParentRunId,
    int Depth,
    string State,
    ErrorDto? Error,
    long StartedAt,
    long? EndedAt,
    long Seq,
    string Cursor,
    SeqEventDto[] Events,
    InteractionDto[] Pending,
    string[] Children,
    bool LogTrimmed,
    bool Settled);

// ───────────────────────── 交互 ─────────────────────────

/// <summary>
/// 一个挂起的交互。<see cref="Version"/> 是登记那一刻设备上交互闸门的修订号(全设备唯一、只增):回答时带着它(<c>expectedVersion</c>),
/// 设备据此识别「答的是不是我看到的那一份」。不含界面文字:控制端按类型翻译。
/// </summary>
[DerivedTypeShape(typeof(ToolPermissionInteractionDto), Name = "tool_permission")]
[DerivedTypeShape(typeof(AskUserInteractionDto), Name = "ask_user")]
[DerivedTypeShape(typeof(PlanApprovalInteractionDto), Name = "plan_approval")]
[DerivedTypeShape(typeof(GoalProposalInteractionDto), Name = "goal_proposal")]
[DerivedTypeShape(typeof(WorkflowApprovalInteractionDto), Name = "workflow_approval")]
[DerivedTypeShape(typeof(WidgetSubmitInteractionDto), Name = "widget_submit")]
[DerivedTypeShape(typeof(SshHostKeyInteractionDto), Name = "ssh_host_key")]
[DerivedTypeShape(typeof(SshCredentialInteractionDto), Name = "ssh_credential")]
public abstract record InteractionDto
{
    public required string Id { get; init; }

    public required string RunId { get; init; }

    public long CreatedAt { get; init; }

    public long Version { get; init; }
}

/// <summary>工具审批。<see cref="Env"/> 必有:审批卡片必须说清「在哪台设备的哪个环境里执行」。</summary>
public sealed record ToolPermissionInteractionDto : InteractionDto
{
    public required string CallId { get; init; }

    /// <summary>模型看到的名字。</summary>
    public required string ToolName { get; init; }

    /// <summary>稳定 id,规则按它匹配。</summary>
    public required string InternalId { get; init; }

    public required string InputJson { get; init; }

    public bool ReadOnly { get; init; }

    public bool Destructive { get; init; }

    public bool NeedsNetwork { get; init; }

    public required EnvRefDto Env { get; init; }

    /// <summary>设备算好的建议规则(「记住」时写的就是它,不由控制端拼)。</summary>
    public string? SuggestedRule { get; init; }

    /// <summary>这次可以「记住」的作用域:<c>session</c> / <c>project</c> / <c>global</c>。</summary>
    public required string[] AllowedScopes { get; init; }

    /// <summary>仅 Bash:命令风险分级 <c>unknown</c> / <c>readOnly</c> / <c>dangerous</c>。</summary>
    public string? Risk { get; init; }

    public bool OutsideWorkspace { get; init; }
}

public sealed record AskUserOptionDto(string Label, string? Description = null);

public sealed record AskUserQuestionDto(string Header, string Question, AskUserOptionDto[] Options, bool MultiSelect, bool AllowFreeform);

public sealed record AskUserInteractionDto : InteractionDto
{
    public required AskUserQuestionDto[] Questions { get; init; }
}

public sealed record PlanApprovalInteractionDto : InteractionDto
{
    public required string Plan { get; init; }

    public required string PlanId { get; init; }

    public required string Path { get; init; }
}

public sealed record GoalProposalInteractionDto : InteractionDto
{
    public required string SessionId { get; init; }

    public required string Condition { get; init; }
}

public sealed record WorkflowApprovalInteractionDto : InteractionDto
{
    public required string CallId { get; init; }

    public required string WorkflowJson { get; init; }
}

public sealed record WidgetSubmitInteractionDto : InteractionDto
{
    public required string CallId { get; init; }

    public required string Title { get; init; }
}

/// <summary>
/// SSH 主机指纹确认(首次连接这台主机):让用户核对指纹后信任并钉扎。设备级交互,<see cref="InteractionDto.RunId"/> 是 <c>env:&lt;环境 id&gt;:…</c> 这样的作用域(不属于任何会话)。
/// <b>指纹和钉扎的不一致不走这里</b>:连接直接失败,详情在环境状态里(<see cref="EnvironmentErrorDto.HostKey"/>)。<see cref="Env"/> 必有(设备 + 环境)。
/// </summary>
public sealed record SshHostKeyInteractionDto : InteractionDto
{
    public required EnvRefDto Env { get; init; }

    /// <summary>钉扎用的主机名(默认端口是裸主机名,否则 <c>[host]:port</c>;多跳时是这一跳的)。</summary>
    public required string HostName { get; init; }

    public required string Algorithm { get; init; }

    /// <summary>SHA256 指纹(<c>ssh-keygen -l</c> 同款,Base64 不带 <c>=</c> 和 <c>SHA256:</c> 前缀)。</summary>
    public required string Fingerprint { get; init; }
}

/// <summary>键盘交互式的一个问题。</summary>
public sealed record SshPromptDto(string Text, bool Echo);

/// <summary>
/// SSH 凭据提示。请求里只有「问什么」,没有任何凭据。<see cref="PromptKind"/>:<c>password</c> / <c>keyPassphrase</c> / <c>keyboardInteractive</c>。
/// 设备级交互(同 <see cref="SshHostKeyInteractionDto"/>),<see cref="Env"/> 必有。回答里有秘密,<c>interaction_resolved</c> 事件不带它。
/// </summary>
public sealed record SshCredentialInteractionDto : InteractionDto
{
    public required EnvRefDto Env { get; init; }

    public required string PromptKind { get; init; }

    /// <summary>在向哪一跳要凭据(钉扎用的主机名)。</summary>
    public required string HostName { get; init; }

    public required string UserName { get; init; }

    /// <summary>私钥文件名(只有文件名,不含目录);只在 <c>keyPassphrase</c> 时有值。</summary>
    public string? KeyName { get; init; }

    /// <summary>键盘交互式的服务器说明文字。</summary>
    public string? Instruction { get; init; }

    public SshPromptDto[] Prompts { get; init; } = [];

    /// <summary>这次是因为上一次给的(含已保存的)被拒绝才再问的:界面要把这件事说出来。</summary>
    public bool PreviousRejected { get; init; }

    /// <summary>回答能不能「记住」(已保存的环境才能;试连接的草稿不能)。</summary>
    public bool CanRemember { get; init; }

    /// <summary>设备上已经存了这一项(被拒绝过,所以才再问)。</summary>
    public bool HasSaved { get; init; }
}

/// <summary>全量:挂起的交互 + 闸门修订号。<b><c>interactions</c> 主题的首帧、后续变更、<c>interactions.list</c> 都是这一个类型</b>(没有增量,收到就对账)。</summary>
public sealed record InteractionsDto(long Revision, InteractionDto[] Pending);

/// <summary>一次交互是怎么结束的:谁(<see cref="ResolvedBy"/> = 回答的控制端的 clientId;中止时为 null)、什么时候(<see cref="At"/>,Unix 毫秒)。</summary>
/// <param name="Status"><c>answered</c> / <c>aborted</c>。</param>
public sealed record ResolutionDto(string Id, string RunId, string Kind, long Version, string Status, string? ResolvedBy, long At, long Revision);

/// <summary>对一次交互的回答(类型必须和交互的种类一致,否则 <c>invalid_request</c>)。</summary>
[DerivedTypeShape(typeof(ToolPermissionResponseDto), Name = "tool_permission")]
[DerivedTypeShape(typeof(AskUserResponseDto), Name = "ask_user")]
[DerivedTypeShape(typeof(PlanApprovalResponseDto), Name = "plan_approval")]
[DerivedTypeShape(typeof(GoalProposalResponseDto), Name = "goal_proposal")]
[DerivedTypeShape(typeof(WorkflowApprovalResponseDto), Name = "workflow_approval")]
[DerivedTypeShape(typeof(WidgetSubmitResponseDto), Name = "widget_submit")]
[DerivedTypeShape(typeof(SshHostKeyResponseDto), Name = "ssh_host_key")]
[DerivedTypeShape(typeof(SshCredentialResponseDto), Name = "ssh_credential")]
public abstract record InteractionResponseDto;

/// <param name="Choice"><c>allowOnce</c> / <c>allowAlways</c> / <c>allowEdited</c> / <c>deny</c>。</param>
/// <param name="Scope"><c>allowAlways</c> 时记住的作用域(<c>session</c> / <c>project</c> / <c>global</c>);规则文本由设备用请求里的建议规则自己写。</param>
/// <param name="EditedInputJson"><c>allowEdited</c> 时用户改写后的参数(JSON 对象);改写后的参数会重新走一遍规则检查。</param>
/// <param name="Reason">拒绝时的理由(给模型看)。</param>
public sealed record ToolPermissionResponseDto(string Choice, string? Scope = null, string? EditedInputJson = null, string? Reason = null) : InteractionResponseDto;

public static class PermissionChoices
{
    public const string AllowOnce = "allowOnce";
    public const string AllowAlways = "allowAlways";
    public const string AllowEdited = "allowEdited";
    public const string Deny = "deny";
}

/// <summary>一组回答对应一道题;<see cref="Answers"/> 为 null = 用户关掉了框(dismiss),不是回答。</summary>
public sealed record AskUserAnswerDto(string[] Values);

public sealed record AskUserResponseDto(AskUserAnswerDto[]? Answers) : InteractionResponseDto;

/// <param name="Action"><c>approveCurrent</c> / <c>approveNewSession</c> / <c>requestRevision</c> / <c>reject</c>。</param>
public sealed record PlanApprovalResponseDto(string Action, string? Feedback = null) : InteractionResponseDto;

public sealed record GoalProposalResponseDto(bool Approved) : InteractionResponseDto;

/// <param name="Action"><c>run</c> / <c>revise</c> / <c>cancel</c>。</param>
public sealed record WorkflowApprovalResponseDto(string Action, string? Feedback = null) : InteractionResponseDto;

/// <param name="Value">null = 用户关掉了,没有提交;否则是 widget <c>submit()</c> 的值的 JSON 文本。</param>
public sealed record WidgetSubmitResponseDto(string? Value) : InteractionResponseDto;

/// <summary><c>interactions.respond</c>:<see cref="ExpectedVersion"/> 是控制端看到的那次交互的版本。</summary>
public sealed record InteractionsRespondParams(string Id, long ExpectedVersion, InteractionResponseDto Response);

/// <summary>回答被接受:交互已经结束,<see cref="Resolution"/> 记下是谁、什么时候。</summary>
public sealed record InteractionsRespondResult(ResolutionDto Resolution);

/// <summary>
/// <c>interactions.respond</c> 的 <c>conflict</c> 错误的 <c>details</c>(<see cref="ProtocolError.Details"/> 里编码的就是它)。
/// <c>alreadyResolved</c>:已被别人处理(或已被中止),带 <see cref="Resolution"/>(谁、什么时候、<c>answered</c> / <c>aborted</c>)——界面显示「已在另一台设备上处理」;
/// <c>staleVersion</c>:仍在挂起但版本对不上,带 <see cref="CurrentVersion"/>。
/// </summary>
public sealed record InteractionConflictDto(string Reason, ResolutionDto? Resolution = null, long? CurrentVersion = null);

public static class ConflictReasons
{
    public const string AlreadyResolved = "alreadyResolved";
    public const string StaleVersion = "staleVersion";
}

// ───────────────────────── 工具与上下文 ─────────────────────────

public sealed record ToolInfoDto(string Name, string Description, bool ReadOnly, bool Destructive, bool NeedsNetwork, string InputSchemaJson);

/// <summary><c>tools.list</c>:<see cref="SessionId"/> 为空 = 设备上通用的内置工具;给了会话就是该会话下一回合能用的(目前两者相同)。</summary>
public sealed record ToolsListParams(string? SessionId = null);

public sealed record ToolsListResult(ToolInfoDto[] Tools);

/// <summary>手动压缩。只能在会话空闲时做(占会话互斥);忙时返回 <c>busy</c>。</summary>
public sealed record ContextCompactParams(string SessionId, string? Instructions = null, SendOptionsDto? Options = null);

/// <summary>压缩的结果:成功时 <see cref="Boundary"/> 是已经落库的边界消息;失败时 <see cref="Error"/> 带 key(控制端翻译)。</summary>
public sealed record ContextCompactResult(MessageDto? Boundary, ErrorDto? Error);

public sealed record ContextPreviewParams(string SessionId, SendOptionsDto? Options = null);

/// <summary>上下文被谁占掉了:各档之和恒等于 <see cref="Used"/>。</summary>
public sealed record ContextPreviewResult(int Used, int Window, bool ShouldCompact, ContextSegmentDto[] Segments);

// ───────────────────────── 会话主题(session/<id>) ─────────────────────────

/// <summary>
/// <c>session/&lt;id&gt;</c> 主题的快照:会话元数据 + 最近一页消息、排队消息、当前运行中的顶层 Run(没有为 null)。
/// 增量事件(<see cref="SessionEvents"/>)的数据各是一个完整的类型,收到就整体替换对应的那一块。
/// </summary>
public sealed record SessionTopicState(SessionPageDto Page, QueueDto Queue, string? ActiveRunId);

public static class SessionEvents
{
    /// <summary>元数据变了(标题、模式、模型、归档、收藏)。数据是 <see cref="SessionDto"/>。</summary>
    public const string Updated = "session.updated";

    /// <summary>排队消息变了。数据是 <see cref="QueueDto"/>(全量,<c>Rev</c> 全局单调)。</summary>
    public const string QueueUpdated = "queue.updated";

    /// <summary>一条消息已落库(Run 里提交的每条:用户输入、助手回复、工具结果)。数据是 <see cref="MessageDto"/>,按 id 追加或替换。</summary>
    public const string MessageCommitted = "message.committed";

    /// <summary>历史被改写(编辑、删除回合 / 回复、压缩)。数据是 <see cref="SessionPageDto"/>(最近一页),控制端整体替换。</summary>
    public const string HistoryReset = "history.reset";

    /// <summary>会话被删除。数据是 <see cref="Unit"/>。</summary>
    public const string Deleted = "session.deleted";
}

/// <summary><c>interactions</c> 主题的事件类型。</summary>
public static class InteractionEvents
{
    /// <summary>挂起的交互变了(数据是新的 <see cref="InteractionsDto"/>,全量)。</summary>
    public const string Changed = "interactions.changed";
}

/// <summary><c>device</c> 主题里一条运行中的子代理 Run(<c>activeRuns</c> 的 <c>subagents</c> 半边)。<see cref="SessionId"/> 是父会话。</summary>
public sealed record ActiveSubagentDto(string RunId, string ParentRunId, string SessionId, string WorkspaceId, long StartedAt);

/// <param name="Trust"><c>true</c> = 信任并钉扎这个指纹,继续连接;<c>false</c> = 拒绝(不发任何凭据)。</param>
public sealed record SshHostKeyResponseDto(bool Trust) : InteractionResponseDto;

/// <summary>
/// SSH 凭据的回答。<b>只出现在请求里</b>(控制端 → 设备,单向):设备从不把它发回任何控制端(<c>interaction_resolved</c> 事件里没有)。
/// </summary>
/// <param name="Answers">null = 取消。密码 / 口令是一项,键盘交互式每个问题一项(与请求里 <see cref="SshCredentialInteractionDto.Prompts"/> 顺序一致)。</param>
/// <param name="Remember">用 NCK1 存在设备上(只对 <see cref="SshCredentialInteractionDto.CanRemember"/> 的请求有效)。</param>
public sealed record SshCredentialResponseDto(string[]? Answers, bool Remember = false) : InteractionResponseDto
{
    /// <summary>★ 自动生成的 <c>ToString()</c> 会打出每个属性:口令不能经日志、异常消息、断言输出带出去。</summary>
    public override string ToString() => "SshCredentialResponseDto { redacted }";
}
