namespace Ncw.Agent.Storage.Sessions;

/// <summary>
/// 一条会话(2.x 的 <c>Session</c>)。列(过滤 / 级联 / 排序读的那些)是真源;<c>origin</c>、<c>titleSource</c>、
/// <c>modelProviderId</c>、<c>skillSource</c>、<c>permissionMode</c> 只活在 json 列里,没有提列(2.x 的判据:不参与 WHERE / ORDER BY / 级联)。
/// 2.x 的 <c>status</c> 永远是 idle(运行中永不从磁盘恢复),这里不建模。
/// </summary>
public sealed record SessionRecord(
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
    string RootPathAtCreation,
    string? SkillSourceSessionId,
    bool Archived,
    bool Favorited,
    long CreatedAt,
    long UpdatedAt,
    string PermissionMode = "ask");

/// <summary>2.x 的 <c>SessionCreateInput</c>。</summary>
public sealed record SessionCreate(
    string WorkspaceId,
    string? Id = null,
    string? ParentSessionId = null,
    string? Origin = null,
    string? Title = null,
    string? Model = null,
    string? ModelProviderId = null,
    string? Mode = null,
    string? Thinking = null,
    string? RootPathAtCreation = null,
    long? CreatedAt = null,
    string? SkillSourceSessionId = null,
    string? PermissionMode = null);

/// <summary>侧边栏的一行。<c>Running</c> 永远是 false:运行状态不在库里,由 Agent 的运行注册表在上层合并。</summary>
public sealed record SessionListItem(string Id, string Title, long UpdatedAt, bool Archived, bool Favorited, bool Running, bool SkillExtraction, bool IsEmpty = false);

/// <summary>会话元数据 + 消息条数(不读任何消息正文)。</summary>
public sealed record SessionSummary(SessionRecord Session, long MessageCount);

/// <summary>
/// 一条消息。<see cref="PartsJson"/> 是 2.x 的 <c>parts</c> 列原文(内容块数组的 JSON),Storage 不解释它(只在判轮次边界、
/// 全文索引、编辑文字时读 <c>type</c> / <c>text</c>);和 Llm 的 <c>Message</c> / <c>Part</c> 互转属于会话层。
/// 读出时坏 JSON / 非数组按 2.x 当空数组。
/// <see cref="MetaJson"/> 是 3.0 新增的可空列(迁移 39):助手消息的来源(api / provider / model)和停止原因这类 2.x 没有的元数据,
/// 也是原文进出、Storage 不解释;2.x 写的旧消息为 NULL。
/// </summary>
public sealed record MessageRecord(
    string Id,
    string Role,
    string PartsJson,
    long CreatedAt,
    int SchemaVersion = 1,
    bool Internal = false,
    string? MetaJson = null);

/// <summary><see cref="SessionRepository.HistoryPage"/> 的结果。</summary>
public sealed record HistoryPageResult(IReadOnlyList<MessageRecord> Messages, bool HasMore);

/// <summary>一次 Run 的累计用量(从 <c>usage_records</c> 聚合,2.x 的 <c>RunUsage</c>)。</summary>
public sealed record RunUsageRecord(
    long InputTokens,
    long OutputTokens,
    long CacheReadInputTokens,
    long CacheCreationInputTokens,
    long CacheCreation1hInputTokens,
    long? ReasoningTokens,
    long? UpstreamMs,
    RunCost? Cost);

/// <summary>
/// 一轮的花费(微单位)。<see cref="RunUsageRecord.Cost"/> 为 null = 2.x 的 <c>cost: null</c>:这一轮算不齐
/// (有一次尝试查不到价、或混了两种币种),界面据此不画金额,会话总额随之作废;绝不是 0。
/// </summary>
public sealed record RunCost(long Micros, string Currency);

/// <summary>整页会话详情(2.x 的 <c>SessionDetail</c> / <c>SessionPage</c>)。</summary>
public sealed record SessionDetailRecord(
    SessionRecord Session,
    IReadOnlyList<MessageRecord> Messages,
    bool HasMore,
    IReadOnlyDictionary<string, string> MessageRuns,
    IReadOnlyDictionary<string, RunUsageRecord> RunUsage,
    IReadOnlyDictionary<string, string> RunModel);

/// <summary>全局搜索的命中项;<c>Snippet</c> 带 FTS5 的 <c>&lt;mark&gt;</c> 高亮标记。</summary>
public sealed record SearchHit(string SessionId, string WorkspaceId, string MessageId, string Title, string Snippet, long CreatedAt);

/// <summary>会话范围内的上下文检索命中。</summary>
public sealed record ContextSearchHit(string MessageId, string Role, long CreatedAt, string Snippet);

/// <summary><see cref="SessionRepository.RecentWorkspacePrompts"/> 的两道上限(2.x 默认值)。</summary>
public sealed record RecentPromptLimits(int Sessions = 200, int Scan = 2000, int Limit = 500);

/// <summary><see cref="SessionRepository.SweepOrphans"/> 的结果。</summary>
public sealed record OrphanSweepResult(long FtsRows, long InputRows);

/// <summary><c>runs</c> 表的一行。</summary>
public sealed record RunRecordRow(string Id, string SessionId, string Status, long StartedAt, long? EndedAt);
