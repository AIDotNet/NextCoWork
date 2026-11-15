using PolyType;

namespace Ncw.Protocol.Methods;

/// <summary>
/// 对话链路的方法表和主题(docs/design/09 §8.4,功能盘点 10 的 <c>sessions</c> / <c>runs</c> / <c>interactions</c> / <c>queue</c> / <c>tools</c> / <c>context</c>)。
/// 每个方法声明:种类(query / command,command 必带幂等键)、所需角色、所需能力(<see cref="Capabilities.Conversation"/>)、允许的承载。
/// 角色:viewer 只读;member 可对话、审批、改会话;owner 全部(删除会话、使用 <c>full</c> 权限档位、运行中改档位)。
/// </summary>
public static class ConversationMethods
{
    // ── sessions ──

    public static readonly MethodDef<SessionsListParams, SessionsListResult> SessionsList = Query<SessionsListParams, SessionsListResult>("sessions.list", Role.Viewer);

    /// <summary>整段会话详情(全部消息)。长会话请用 <see cref="SessionsGetPage"/>。</summary>
    public static readonly MethodDef<SessionRefParams, SessionPageDto> SessionsGet = Query<SessionRefParams, SessionPageDto>("sessions.get", Role.Viewer);

    public static readonly MethodDef<SessionsGetPageParams, SessionPageDto> SessionsGetPage = Query<SessionsGetPageParams, SessionPageDto>("sessions.getPage", Role.Viewer);

    public static readonly MethodDef<SessionsCreateParams, SessionDto> SessionsCreate = Command<SessionsCreateParams, SessionDto>("sessions.create", Role.Member);

    public static readonly MethodDef<SessionsRenameParams, SessionDto> SessionsRename = Command<SessionsRenameParams, SessionDto>("sessions.rename", Role.Member);

    public static readonly MethodDef<SessionsSetModeParams, SessionDto> SessionsSetMode = Command<SessionsSetModeParams, SessionDto>("sessions.setMode", Role.Member);

    public static readonly MethodDef<SessionsSetModelParams, SessionDto> SessionsSetModel = Command<SessionsSetModelParams, SessionDto>("sessions.setModel", Role.Member);

    public static readonly MethodDef<SessionsArchiveParams, SessionDto> SessionsArchive = Command<SessionsArchiveParams, SessionDto>("sessions.archive", Role.Member);

    public static readonly MethodDef<SessionsFavoriteParams, SessionDto> SessionsFavorite = Command<SessionsFavoriteParams, SessionDto>("sessions.favorite", Role.Member);

    /// <summary>删除会话(连同它的子代理转录、排队消息)。会话有 Run 在跑时先中止并等它收尾。</summary>
    public static readonly MethodDef<SessionRefParams, Unit> SessionsDelete = Command<SessionRefParams, Unit>("sessions.delete", Role.Owner);

    public static readonly MethodDef<SessionsDuplicateParams, SessionDto> SessionsDuplicate = Command<SessionsDuplicateParams, SessionDto>("sessions.duplicate", Role.Member);

    public static readonly MethodDef<SessionsBranchParams, SessionDto> SessionsBranch = Command<SessionsBranchParams, SessionDto>("sessions.branch", Role.Member);

    public static readonly MethodDef<SessionsEditMessageParams, Unit> SessionsEditMessage = Command<SessionsEditMessageParams, Unit>("sessions.editMessage", Role.Member);

    public static readonly MethodDef<SessionsDeleteTurnParams, Unit> SessionsDeleteTurn = Command<SessionsDeleteTurnParams, Unit>("sessions.deleteTurn", Role.Member);

    public static readonly MethodDef<SessionsDeleteReplyParams, Unit> SessionsDeleteReply = Command<SessionsDeleteReplyParams, Unit>("sessions.deleteReply", Role.Member);

    // ── runs(attach = 订阅 run/<id> 主题,带 sinceSeq 游标) ──

    /// <summary>发起一个 Run;会话忙时这句话进队列(结果里 <c>outcome = queued</c>)。<c>full</c> 档位只有 owner 能用。</summary>
    public static readonly MethodDef<RunsStartParams, RunsStartResult> RunsStart = Command<RunsStartParams, RunsStartResult>("runs.start", Role.Member);

    public static readonly MethodDef<RunsAbortParams, Unit> RunsAbort = Command<RunsAbortParams, Unit>("runs.abort", Role.Member);

    public static readonly MethodDef<RunsSteerParams, QueueResultDto> RunsSteer = Command<RunsSteerParams, QueueResultDto>("runs.steer", Role.Member);

    /// <summary>把前台子代理挪到后台(子代理层落地前,没有可挪的 Run,返回 <c>invalid_request</c>)。</summary>
    public static readonly MethodDef<RunsBackgroundParams, Unit> RunsBackground = Command<RunsBackgroundParams, Unit>("runs.background", Role.Member);

    public static readonly MethodDef<RunsSetPermissionModeParams, Unit> RunsSetPermissionMode = Command<RunsSetPermissionModeParams, Unit>("runs.setPermissionMode", Role.Owner);

    // ── interactions ──

    public static readonly MethodDef<Unit, InteractionsDto> InteractionsList = Query<Unit, InteractionsDto>("interactions.list", Role.Viewer);

    /// <summary>回答一次交互(先到先得)。已被处理或版本对不上返回 <c>conflict</c>,<c>details</c> 是 <see cref="InteractionConflictDto"/>;校验失败 <c>invalid_request</c>(不消耗这次交互)。</summary>
    public static readonly MethodDef<InteractionsRespondParams, InteractionsRespondResult> InteractionsRespond =
        Command<InteractionsRespondParams, InteractionsRespondResult>("interactions.respond", Role.Member);

    // ── queue ──

    public static readonly MethodDef<SessionRefParams, QueueDto> QueueList = Query<SessionRefParams, QueueDto>("queue.list", Role.Viewer);

    public static readonly MethodDef<QueueEnqueueParams, QueueResultDto> QueueEnqueue = Command<QueueEnqueueParams, QueueResultDto>("queue.enqueue", Role.Member);

    public static readonly MethodDef<QueueItemParams, QueueResultDto> QueuePromote = Command<QueueItemParams, QueueResultDto>("queue.promote", Role.Member);

    public static readonly MethodDef<QueueItemParams, QueueResultDto> QueueRemove = Command<QueueItemParams, QueueResultDto>("queue.remove", Role.Member);

    // ── tools / context ──

    public static readonly MethodDef<ToolsListParams, ToolsListResult> ToolsList = Query<ToolsListParams, ToolsListResult>("tools.list", Role.Viewer);

    public static readonly MethodDef<ContextCompactParams, ContextCompactResult> ContextCompact = Command<ContextCompactParams, ContextCompactResult>("context.compact", Role.Member);

    public static readonly MethodDef<ContextPreviewParams, ContextPreviewResult> ContextPreview = Query<ContextPreviewParams, ContextPreviewResult>("context.preview", Role.Viewer);

    // ── 主题 ──

    /// <summary>
    /// 本设备所有挂起的交互。快照和增量(<see cref="InteractionEvents.Changed"/>)是同一个全量类型 <see cref="InteractionsDto"/>。
    /// </summary>
    public static readonly TopicDef<InteractionsDto> InteractionsTopic = new("interactions", PayloadShapes.GetShape<InteractionsDto>());

    /// <summary><c>run/&lt;id&gt;</c> 的前缀。</summary>
    public const string RunTopicPrefix = "run/";

    /// <summary><c>session/&lt;id&gt;</c> 的前缀。</summary>
    public const string SessionTopicPrefix = "session/";

    /// <summary>某个 Run 的主题(attach):<c>run/&lt;id&gt;</c>。订阅时带上次收到的最后一个 <c>seq</c> 作为游标即可续传。</summary>
    public static TopicDef<RunSnapshotDto> RunTopic(string runId) => new(RunTopicPrefix + runId, PayloadShapes.GetShape<RunSnapshotDto>());

    /// <summary>某个会话的主题:<c>session/&lt;id&gt;</c>。</summary>
    public static TopicDef<SessionTopicState> SessionTopic(string sessionId) => new(SessionTopicPrefix + sessionId, PayloadShapes.GetShape<SessionTopicState>());

    public static string RunTopicName(string runId) => RunTopicPrefix + runId;

    public static string SessionTopicName(string sessionId) => SessionTopicPrefix + sessionId;

    public static readonly IReadOnlyList<MethodDef> All =
    [
        SessionsList, SessionsGet, SessionsGetPage, SessionsCreate, SessionsRename, SessionsSetMode, SessionsSetModel, SessionsArchive,
        SessionsFavorite, SessionsDelete, SessionsDuplicate, SessionsBranch, SessionsEditMessage, SessionsDeleteTurn, SessionsDeleteReply,
        RunsStart, RunsAbort, RunsSteer, RunsBackground, RunsSetPermissionMode,
        InteractionsList, InteractionsRespond,
        QueueList, QueueEnqueue, QueuePromote, QueueRemove,
        ToolsList, ContextCompact, ContextPreview,
    ];

    private static MethodDef<TP, TR> Query<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Query, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = Capabilities.Conversation };

    private static MethodDef<TP, TR> Command<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Command, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = Capabilities.Conversation };
}

/// <summary>对话链路的载荷形状(源生成,与 <see cref="DeviceMethods"/> 里的同属一个 <c>PayloadShapes</c>)。新增 DTO 时在这里加一行。</summary>
[GenerateShapeFor<SessionsListParams>]
[GenerateShapeFor<SessionsListResult>]
[GenerateShapeFor<SessionRefParams>]
[GenerateShapeFor<SessionsGetPageParams>]
[GenerateShapeFor<SessionPageDto>]
[GenerateShapeFor<SessionDto>]
[GenerateShapeFor<SessionsCreateParams>]
[GenerateShapeFor<SessionsRenameParams>]
[GenerateShapeFor<SessionsSetModeParams>]
[GenerateShapeFor<SessionsSetModelParams>]
[GenerateShapeFor<SessionsArchiveParams>]
[GenerateShapeFor<SessionsFavoriteParams>]
[GenerateShapeFor<SessionsDuplicateParams>]
[GenerateShapeFor<SessionsBranchParams>]
[GenerateShapeFor<SessionsEditMessageParams>]
[GenerateShapeFor<SessionsDeleteTurnParams>]
[GenerateShapeFor<SessionsDeleteReplyParams>]
[GenerateShapeFor<RunsStartParams>]
[GenerateShapeFor<RunsStartResult>]
[GenerateShapeFor<RunsAbortParams>]
[GenerateShapeFor<RunsSteerParams>]
[GenerateShapeFor<RunsBackgroundParams>]
[GenerateShapeFor<RunsSetPermissionModeParams>]
[GenerateShapeFor<InteractionsDto>]
[GenerateShapeFor<InteractionsRespondParams>]
[GenerateShapeFor<InteractionsRespondResult>]
[GenerateShapeFor<InteractionConflictDto>]
[GenerateShapeFor<QueueDto>]
[GenerateShapeFor<QueueResultDto>]
[GenerateShapeFor<QueueEnqueueParams>]
[GenerateShapeFor<QueueItemParams>]
[GenerateShapeFor<ToolsListParams>]
[GenerateShapeFor<ToolsListResult>]
[GenerateShapeFor<ContextCompactParams>]
[GenerateShapeFor<ContextCompactResult>]
[GenerateShapeFor<ContextPreviewParams>]
[GenerateShapeFor<ContextPreviewResult>]
[GenerateShapeFor<RunSnapshotDto>]
[GenerateShapeFor<RunEventDto>]
[GenerateShapeFor<SessionTopicState>]
[GenerateShapeFor<InteractionDto>]
[GenerateShapeFor<InteractionResponseDto>]
public sealed partial class PayloadShapes;
