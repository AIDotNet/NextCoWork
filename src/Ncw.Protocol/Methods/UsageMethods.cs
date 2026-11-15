using PolyType;

namespace Ncw.Protocol.Methods;

// 用量统计(docs/design/10 的 `usage` 模块 → `usage.*`)。数据来自设备上的 `usage_records` 表(每次 HTTP 尝试一行,费用写入即冻结)和按日汇总表 `usage_daily`。
// 全部只读,viewer 起(不含提示词、响应、凭据、请求头——用量记录里只有计量和路由元数据)。时间窗是 `[From, To)`,Unix 毫秒;`From` 缺省 = 无下界。
// ★ 费用的 `CostMicros` 为 null = 查不到价格(「未知」),绝不是 0;不同币种分开,绝不相加(跨币种相加得到的是没有单位的数)。

/// <summary>查询的时间窗:含 <see cref="From"/>、不含 <see cref="To"/>(Unix 毫秒);<see cref="From"/> 为 null = 无下界。</summary>
public sealed record UsageWindowDto(long? From, long To);

/// <summary>一种币种的费用合计(<see cref="Currency"/> 是 <c>USD</c> / <c>CNY</c>)。只含已计价的请求。</summary>
public sealed record UsageCostTotalDto(string Currency, long Micros);

public sealed record UsageSummaryDto(
    long RequestCount,
    long SuccessCount,
    long FailedCount,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long CacheWrite1hTokens,
    long ThinkingTokens,
    long EstimatedThinkingRequestCount,
    long TotalTokens,
    double? CacheHitRate,
    double? AverageLatencyMs,
    double? AverageTimeToFirstTokenMs,
    long ToolCalls,
    long ToolErrors,
    UsageCostTotalDto[] Costs);

/// <summary>一次上游尝试的账目(日志的一行)。只含计量和路由元数据:提示词、响应、凭据、请求头从不进用量记录。</summary>
/// <param name="Protocol"><c>anthropic</c> / <c>openai-chat</c> / <c>openai-responses</c> / <c>unknown</c>。</param>
public sealed record UsageAttemptDto(
    string Id,
    long At,
    string RunId,
    string WorkspaceId,
    string SessionId,
    int Attempt,
    string ProviderId,
    string ProviderName,
    string Protocol,
    string Endpoint,
    string Alias,
    string UpstreamModel,
    string? ResponseModel,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long CacheWrite1hTokens,
    long? ThinkingTokens,
    bool ThinkingTokensEstimated,
    int LatencyMs,
    int? TimeToFirstTokenMs,
    bool Ok,
    int? HttpStatus,
    string? ErrorKind,
    string? ErrorMessage,
    string? StopReason,
    long? CostMicros,
    string? Currency,
    int? PricingTier,
    string? PricingWindow,
    int ToolCalls,
    int ToolErrors);

/// <param name="Query">在供应商名 / id、别名、上游模型、Run id 里做不区分大小写的子串匹配。</param>
/// <param name="Status"><c>all</c>(缺省)/ <c>success</c> / <c>failed</c>。</param>
/// <param name="Limit">1–200,缺省 50。</param>
public sealed record UsageLogsQuery(long? From, long To, string? Query = null, string? Status = null, int Offset = 0, int Limit = 50);

public sealed record UsageLogsPage(UsageAttemptDto[] Items, long Total, int Offset, int Limit);

public sealed record UsageSessionRefParams(string SessionId);

public sealed record UsageAttemptsResult(UsageAttemptDto[] Attempts);

/// <summary>按供应商 / 模型的一行统计(最多 200 行,按请求数降序)。</summary>
public sealed record UsageDimensionStatDto(
    string Id,
    string Label,
    long RequestCount,
    long SuccessCount,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long ThinkingTokens,
    double? AverageLatencyMs,
    double? AverageTimeToFirstTokenMs,
    long ToolCalls,
    long ToolErrors,
    UsageCostTotalDto[] Costs);

public sealed record UsageDimensionStatsResult(UsageDimensionStatDto[] Stats);

/// <summary>
/// 一天里某个「供应商 × 模型 × 币种」的汇总(趋势图、热力图、模型环形图和费用表都由它推出来,同一个事实源)。
/// <see cref="CostMicros"/> 为 null = 这一桶里没有一条请求能定价;<see cref="PricedCount"/> &lt; <see cref="RequestCount"/> = 只有一部分算得出钱,界面必须说出来。
/// 延迟存 sum + count,跨桶再聚合才不会变成「平均的平均」。
/// </summary>
/// <param name="Day">本地日期 <c>YYYY-MM-DD</c>(设备本地时区)。</param>
/// <param name="Currency">空串 = 这一桶未计价。</param>
public sealed record UsageDailyBucketDto(
    string Day,
    string ProviderId,
    string ProviderName,
    string UpstreamModel,
    string Alias,
    string Currency,
    long RequestCount,
    long SuccessCount,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long CacheWrite1hTokens,
    long ThinkingTokens,
    long? CostMicros,
    long PricedCount,
    long LatencySum,
    long TtftSum,
    long TtftCount);

public sealed record UsageDailySeriesResult(UsageDailyBucketDto[] Buckets);

/// <summary>全历史活跃度(不带时间窗:「最长连续天数」按定义就是问全部历史)。</summary>
/// <param name="ActiveDays">有过上游请求的本地日期,升序。</param>
/// <param name="LongestChatMs">最长的一场连续聊天(毫秒;相邻消息间隔超过 30 分钟就算两场)。依赖 messages,清理历史之后会变小。</param>
/// <param name="SyncedDevices">合并了几台其它设备同步来的数据;3.0 的 Agent 恒为 0(账户用量同步随账户配置同步一起做)。</param>
public sealed record UsageActivityStatsDto(
    string[] ActiveDays,
    int CurrentStreak,
    int LongestStreak,
    long PeakDayTokens,
    string? PeakDay,
    long LongestChatMs,
    int SyncedDevices);

/// <summary>用量方法表(能力 <see cref="Capabilities.Usage"/>,承载 本机 + 远程,全部只读 viewer 起)。</summary>
public static class UsageMethods
{
    public static readonly MethodDef<UsageWindowDto, UsageSummaryDto> GetSummary =
        MethodTable.Query<UsageWindowDto, UsageSummaryDto>("usage.getSummary", Role.Viewer, Capabilities.Usage);

    public static readonly MethodDef<UsageLogsQuery, UsageLogsPage> GetRequestLogs =
        MethodTable.Query<UsageLogsQuery, UsageLogsPage>("usage.getRequestLogs", Role.Viewer, Capabilities.Usage);

    /// <summary>一条会话的逐次账目(不分页:费用浮层逐模型核算整条会话,分页会漏算旧请求)。</summary>
    public static readonly MethodDef<UsageSessionRefParams, UsageAttemptsResult> GetSessionAttempts =
        MethodTable.Query<UsageSessionRefParams, UsageAttemptsResult>("usage.getSessionAttempts", Role.Viewer, Capabilities.Usage);

    public static readonly MethodDef<UsageWindowDto, UsageDimensionStatsResult> GetProviderStats =
        MethodTable.Query<UsageWindowDto, UsageDimensionStatsResult>("usage.getProviderStats", Role.Viewer, Capabilities.Usage);

    public static readonly MethodDef<UsageWindowDto, UsageDimensionStatsResult> GetModelStats =
        MethodTable.Query<UsageWindowDto, UsageDimensionStatsResult>("usage.getModelStats", Role.Viewer, Capabilities.Usage);

    /// <summary>概览的日桶。查询前先刷一次汇总(幂等):刚聊完就打开统计页也看得到新数字;比上面几条慢,别放进每秒轮询的路径。</summary>
    public static readonly MethodDef<UsageWindowDto, UsageDailySeriesResult> GetDailySeries =
        MethodTable.Query<UsageWindowDto, UsageDailySeriesResult>("usage.getDailySeries", Role.Viewer, Capabilities.Usage);

    public static readonly MethodDef<Unit, UsageActivityStatsDto> GetActivityStats =
        MethodTable.Query<Unit, UsageActivityStatsDto>("usage.getActivityStats", Role.Viewer, Capabilities.Usage);

    public static readonly IReadOnlyList<MethodDef> All =
        [GetSummary, GetRequestLogs, GetSessionAttempts, GetProviderStats, GetModelStats, GetDailySeries, GetActivityStats];
}

[GenerateShapeFor<UsageWindowDto>]
[GenerateShapeFor<UsageCostTotalDto>]
[GenerateShapeFor<UsageSummaryDto>]
[GenerateShapeFor<UsageAttemptDto>]
[GenerateShapeFor<UsageLogsQuery>]
[GenerateShapeFor<UsageLogsPage>]
[GenerateShapeFor<UsageSessionRefParams>]
[GenerateShapeFor<UsageAttemptsResult>]
[GenerateShapeFor<UsageDimensionStatDto>]
[GenerateShapeFor<UsageDimensionStatsResult>]
[GenerateShapeFor<UsageDailyBucketDto>]
[GenerateShapeFor<UsageDailySeriesResult>]
[GenerateShapeFor<UsageActivityStatsDto>]
public sealed partial class PayloadShapes;
