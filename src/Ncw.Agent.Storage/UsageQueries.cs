using Microsoft.Data.Sqlite;
using Ncw.Core.Domain;

namespace Ncw.Agent.Storage;

/// <summary>查询的时间窗:含 <see cref="From"/>、不含 <see cref="To"/>(Unix 毫秒);<see cref="From"/> 为 null = 无下界。</summary>
public sealed record UsageWindow(long? From, long To);

/// <summary>一种币种的费用合计;只含已计价的请求(<c>cost_micros</c> 为 NULL 的行不参与,绝不当 0)。</summary>
public sealed record UsageCostTotal(string Currency, long Micros);

public sealed record UsageSummary(
    long RequestCount,
    long SuccessCount,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long CacheWrite1hTokens,
    long ThinkingTokens,
    long EstimatedThinkingRequestCount,
    double? AverageLatencyMs,
    double? AverageTimeToFirstTokenMs,
    long ToolCalls,
    long ToolErrors,
    IReadOnlyList<UsageCostTotal> Costs)
{
    public long FailedCount => RequestCount - SuccessCount;

    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;

    /// <summary>缓存命中率 = 缓存读 / (输入 + 缓存读);分母为 0 时为 null(不是 0%)。</summary>
    public double? CacheHitRate => InputTokens + CacheReadTokens == 0 ? null : (double)CacheReadTokens / (InputTokens + CacheReadTokens);
}

/// <summary>按供应商 / 模型的一行统计。</summary>
public sealed record UsageDimensionStat(
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
    IReadOnlyList<UsageCostTotal> Costs);

/// <summary>一天里某个「供应商 × 模型 × 币种」的汇总(<c>usage_daily</c> 的一行)。<see cref="CostMicros"/> 为 null = 这一桶一条也算不出钱;<see cref="PricedCount"/> &lt; <see cref="RequestCount"/> = 只有一部分算得出。</summary>
public sealed record UsageDailyBucket(
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

public sealed record UsageActivityStats(
    IReadOnlyList<string> ActiveDays,
    int CurrentStreak,
    int LongestStreak,
    long PeakDayTokens,
    string? PeakDay,
    long LongestChatMs,
    int SyncedDevices);

public sealed partial class UsageRepository
{
    private const string Columns = """
        id, at, run_id, workspace_id, session_id, attempt, provider_id, provider_name, protocol, endpoint, alias, upstream_model, response_model,
        input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cache_write_1h_tokens, thinking_tokens, thinking_tokens_estimated,
        latency_ms, time_to_first_token_ms, ok, http_status, error_kind, error_message, stop_reason, cost_micros, currency, pricing_tier, pricing_window,
        tool_calls, tool_errors
        """;

    private static UsageRow MapRow(SqliteDataReader r) => new(
        r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), (int)r.GetInt64(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9),
        r.GetString(10), r.GetString(11), r.Text(12),
        r.Int(13), r.Int(14), r.Int(15), r.Int(16), r.Int(17), r.IsDBNull(18) ? null : r.GetInt64(18),
        (int)r.Int(20), r.IsDBNull(21) ? null : (int)r.GetInt64(21), r.GetInt64(22) != 0, r.IsDBNull(23) ? null : (int)r.GetInt64(23), r.Text(24), r.Text(25), r.Text(26),
        (int)r.Int(31), r.IsDBNull(27) ? null : r.GetInt64(27), r.Text(28), r.IsDBNull(29) ? null : (int)r.GetInt64(29), r.Text(30),
        r.Int(19) != 0, (int)r.Int(32));

    private static (string Sql, (string, object?)[] Args) WindowWhere(UsageWindow window) =>
        window.From is { } from
            ? ("at >= $from AND at < $to", [("$from", from), ("$to", window.To)])
            : ("at < $to", [("$to", window.To)]);

    /// <summary>时间窗内的汇总(请求数、token、缓存、延迟、工具、按币种的费用)。</summary>
    public UsageSummary Summary(UsageWindow window)
    {
        var (where, args) = WindowWhere(window);
        var row = database.Row($"""
            SELECT COUNT(*), COALESCE(SUM(ok), 0), COALESCE(SUM(input_tokens), 0), COALESCE(SUM(output_tokens), 0), COALESCE(SUM(cache_read_tokens), 0),
                   COALESCE(SUM(cache_write_tokens), 0), COALESCE(SUM(cache_write_1h_tokens), 0), COALESCE(SUM(thinking_tokens), 0),
                   COALESCE(SUM(CASE WHEN thinking_tokens_estimated <> 0 THEN 1 ELSE 0 END), 0), AVG(latency_ms), AVG(time_to_first_token_ms),
                   COALESCE(SUM(tool_calls), 0), COALESCE(SUM(tool_errors), 0)
            FROM usage_records WHERE {where}
            """, r => new UsageSummary(
                r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8),
                r.IsDBNull(9) ? null : r.GetDouble(9), r.IsDBNull(10) ? null : r.GetDouble(10), r.GetInt64(11), r.GetInt64(12), []), args)!;
        return row with { Costs = Costs(where, args) };
    }

    private IReadOnlyList<UsageCostTotal> Costs(string where, (string, object?)[] args) => database.Rows($"""
        SELECT currency, SUM(cost_micros) FROM usage_records WHERE {where} AND cost_micros IS NOT NULL AND currency IS NOT NULL GROUP BY currency ORDER BY currency
        """, r => new UsageCostTotal(r.GetString(0), r.GetInt64(1)), args);

    /// <summary>一条会话的逐次账目(不分页:费用浮层逐模型核算整条会话,分页会漏算旧请求)。</summary>
    public IReadOnlyList<UsageRow> SessionAttempts(string sessionId) =>
        database.Rows($"SELECT {Columns} FROM usage_records WHERE session_id = $s ORDER BY at, id", MapRow, ("$s", sessionId));

    public IReadOnlyList<UsageDimensionStat> ProviderStats(UsageWindow window) => DimensionStats(window, "provider_id", "COALESCE(NULLIF(MAX(provider_name), ''), provider_id)");

    public IReadOnlyList<UsageDimensionStat> ModelStats(UsageWindow window) => DimensionStats(window, "upstream_model", "upstream_model");

    private IReadOnlyList<UsageDimensionStat> DimensionStats(UsageWindow window, string idColumn, string labelExpression)
    {
        var (where, args) = WindowWhere(window);
        var stats = database.Rows($"""
            SELECT {idColumn}, {labelExpression} AS label, COUNT(*) AS n, COALESCE(SUM(ok), 0), COALESCE(SUM(input_tokens), 0), COALESCE(SUM(output_tokens), 0),
                   COALESCE(SUM(cache_read_tokens), 0), COALESCE(SUM(cache_write_tokens), 0), COALESCE(SUM(thinking_tokens), 0), AVG(latency_ms), AVG(time_to_first_token_ms),
                   COALESCE(SUM(tool_calls), 0), COALESCE(SUM(tool_errors), 0)
            FROM usage_records WHERE {where} GROUP BY {idColumn} ORDER BY n DESC, label LIMIT 200
            """, r => new UsageDimensionStat(
                r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8),
                r.IsDBNull(9) ? null : r.GetDouble(9), r.IsDBNull(10) ? null : r.GetDouble(10), r.GetInt64(11), r.GetInt64(12), []), args);
        var costs = database.Rows($"""
            SELECT {idColumn}, currency, SUM(cost_micros) FROM usage_records
            WHERE {where} AND cost_micros IS NOT NULL AND currency IS NOT NULL GROUP BY {idColumn}, currency ORDER BY {idColumn}, currency
            """, r => (Id: r.GetString(0), Cost: new UsageCostTotal(r.GetString(1), r.GetInt64(2))), args)
            .ToLookup(c => c.Id, c => c.Cost);
        return [.. stats.Select(s => s with { Costs = [.. costs[s.Id]] })];
    }

    /// <summary>
    /// 把一次尝试结束之后才知道的工具调用数回写到「产生这些工具调用的那次尝试」上(2.x 的 <c>updateUsageToolsForRun</c>):
    /// 重试共用 Run id,但只有成功的 tool_use 尝试才会走到工具执行;按 stop_reason 限定,后面的最终文本回合不会拿到前一回合的计数。
    /// 返回是否更新了一行。
    /// </summary>
    public bool UpdateToolsForRun(string runId, int toolCalls, int toolErrors) => database.Run("""
        UPDATE usage_records SET tool_calls = $calls, tool_errors = $errors
        WHERE id = (SELECT id FROM usage_records WHERE run_id = $run AND ok = 1 AND stop_reason = 'tool_use' ORDER BY at DESC, id DESC LIMIT 1)
        """, ("$calls", toolCalls), ("$errors", toolErrors), ("$run", runId)) > 0;
}

/// <summary>
/// 按天 × 供应商 × 模型 × 币种的用量汇总(<c>usage_daily</c>,迁移 20;2.x 的 <c>usage-rollup.ts</c>)。设置页概览(指标卡、热力图、趋势、模型环形图、费用表)全读这张表,
/// 原始的 <c>usage_records</c> 只服务汇总、供应商 / 模型统计和会话的逐次账目(没有逐条的请求日志)。
/// <para>
/// <b>按日整桶重算,不做行级增量累加</b>:<c>usage_records</c> 只增不删(清理与清空历史都不碰它),所以任何一天的桶一旦算出来就是终局;重算幂等,就没有理由维护会漂移的「上次加到哪了」。
/// </para>
/// <para>
/// <b>水位与日期是两件事</b>:<c>usage_records.id</c> 是写入时刻(ULID,单调),<c>at</c> 是请求开始时刻。一个跑了三分钟的请求 <c>at</c> 可能早于水位却晚于水位才落盘。
/// 所以「哪些日子有新数据」用 id(写入序),「属于哪一天」用 at(业务时刻);再并入最近两天兜底(跨重启时钟回拨时 ULID 可能倒退)。
/// </para>
/// <para>
/// <b>时区</b>:<c>day</c> 是本地日期,依赖写库时的时区;记下算表时的时区名,发现不一致就整表重建(不悄悄按新时区往下加:同一张图里前半段东八区、后半段西五区,图上看不出异常)。
/// 时区由调用方注入(<see cref="TimeZoneInfo"/>),不碰 SQLite 的 <c>'localtime'</c>:测试可以钉死时区。
/// </para>
/// </summary>
public sealed class UsageRollup(AgentDatabase database, TimeZoneInfo? zone = null)
{
    public const string StateKey = "usage.rollup.state";

    /// <summary>每次刷新无条件重算的尾部天数。</summary>
    public const int TailDays = 2;

    /// <summary>日历能安全表示的最晚时刻(公元 9999 年 1 月 1 日,留出任何 UTC 偏移的余量)。</summary>
    private static readonly long MaxCalendarMs = new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    /// <summary>相邻消息间隔超过它就算两场聊天(30 分钟:短到能切开「上午聊完下午再聊」,长到不会把一次思考停顿切成两段)。</summary>
    public const long ChatGapMs = 30 * 60 * 1000;

    private readonly TimeZoneInfo _zone = zone ?? TimeZoneInfo.Local;

    /// <summary>某个本地日期的 <c>[起, 止)</c> 毫秒区间(夏令时那两天也一致;午夜落在夏令时空档时顺延一小时)。</summary>
    public static (long From, long To) DayBounds(string day, TimeZoneInfo zone)
    {
        var index = UsageActivity.DayIndex(day) ?? throw new ArgumentException($"Not a day: {day}", nameof(day));
        return (MidnightUtc(index, zone), MidnightUtc(index + 1, zone));
    }

    private static long MidnightUtc(long dayIndex, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(DateTime.UnixEpoch.AddDays(dayIndex), DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// 把 <c>usage_records</c> 的新增部分汇总进 <c>usage_daily</c>。幂等:重复调用不改变结果。
    /// </summary>
    public (int Days, int Rows) Refresh(long nowMs)
    {
        var kv = new KvStore(database);
        var zoneId = _zone.Id;
        var since = "";
        var storedZone = zoneId;
        if (kv.Get(StateKey) is { } stateJson)
        {
            (since, storedZone) = ReadState(stateJson, zoneId);
        }

        return database.InTransaction(() =>
        {
            if (storedZone != zoneId)
            {
                // 时区变了:所有 day 的边界都可能移位,没有「只修一部分」的说法。
                database.Run("DELETE FROM usage_daily");
                since = "";
            }

            // 先按 id 收窄(主键索引),再在少数几个 15 分钟桶上算本地日期(任何 UTC 偏移都是 15 分钟的整数倍,桶不会跨本地午夜)。
            var days = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var bucket in database.Rows("SELECT DISTINCT at / 900000 FROM usage_records WHERE id > $since", r => r.GetInt64(0), ("$since", since)))
            {
                days.Add(UsageActivity.LocalDayOf(bucket * 900000, _zone));
            }

            for (var i = 0; i < TailDays; i++)
            {
                days.Add(UsageActivity.LocalDayOf(nowMs - i * 24L * 60 * 60 * 1000, _zone));
            }

            var rows = 0;
            foreach (var day in days)
            {
                var (from, to) = DayBounds(day, _zone);
                database.Run("DELETE FROM usage_daily WHERE day = $d", ("$d", day));
                rows += database.Run("""
                    INSERT INTO usage_daily (
                      day, provider_id, provider_name, upstream_model, alias, currency, request_count, success_count, input_tokens, output_tokens,
                      cache_read_tokens, cache_write_tokens, cache_write_1h_tokens, thinking_tokens, cost_micros, priced_count, latency_sum, ttft_sum, ttft_count)
                    SELECT $d, provider_id, COALESCE(NULLIF(MAX(provider_name), ''), provider_id), upstream_model, COALESCE(MAX(alias), ''), COALESCE(currency, ''),
                           COUNT(*), SUM(ok), SUM(input_tokens), SUM(output_tokens), SUM(cache_read_tokens), SUM(cache_write_tokens), SUM(cache_write_1h_tokens),
                           SUM(COALESCE(thinking_tokens, 0)),
                           SUM(cost_micros), COUNT(cost_micros),
                           SUM(latency_ms), SUM(COALESCE(time_to_first_token_ms, 0)), COUNT(time_to_first_token_ms)
                    FROM usage_records WHERE at >= $from AND at < $to
                    GROUP BY provider_id, upstream_model, COALESCE(currency, '')
                    """, ("$d", day), ("$from", from), ("$to", to));
            }

            // ★ SUM 跳过 NULL、全是 NULL 时返回 NULL:正好就是「这一桶一条也算不出钱」,千万别 COALESCE 成 0(那是把「不知道多少钱」说成「免费」)。
            var head = database.Value("SELECT MAX(id) FROM usage_records") as string;
            kv.Set(StateKey, WriteState(string.IsNullOrEmpty(head) ? since : head, zoneId));
            return (days.Count, rows);
        });
    }

    /// <summary>时间窗内的日桶,按日期升序。</summary>
    public IReadOnlyList<UsageDailyBucket> Series(UsageWindow window)
    {
        // 控制端给的窗口端点不可信:越界的毫秒数夹进日历能表示的范围(「到现在为止的全部」常被写成 long.MaxValue 这类值),不能让查询炸成内部错误。
        var toDay = UsageActivity.LocalDayOf(Math.Clamp(window.To - 1, 0, MaxCalendarMs), _zone);
        var fromDay = window.From is { } from && from > 0 ? UsageActivity.LocalDayOf(Math.Min(from, MaxCalendarMs), _zone) : ""; // 无下界:任何 YYYY-MM-DD 都 >= ''
        return database.Rows("""
            SELECT day, provider_id, provider_name, upstream_model, alias, currency, request_count, success_count, input_tokens, output_tokens,
                   cache_read_tokens, cache_write_tokens, cache_write_1h_tokens, thinking_tokens, cost_micros, priced_count, latency_sum, ttft_sum, ttft_count
            FROM usage_daily WHERE day >= $from AND day <= $to ORDER BY day ASC, provider_id, upstream_model, currency
            """, r => new UsageDailyBucket(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                r.Int(6), r.Int(7), r.Int(8), r.Int(9), r.Int(10), r.Int(11), r.Int(12), r.Int(13),
                r.IsDBNull(14) ? null : r.GetInt64(14), r.Int(15), r.Int(16), r.Int(17), r.Int(18)),
            ("$from", fromDay), ("$to", toDay));
    }

    /// <summary>
    /// 全历史活跃度。不带时间窗(「最长连续天数」按定义就是问全部历史)。活跃日取自汇总表而不是会话 / 消息:后者会被「按时间清理」和「清空对话历史」删掉,
    /// 清一次历史连续天数就凭空断档;usage 表不被清理,并且和同一页其它图表同源。
    /// </summary>
    public UsageActivityStats Activity(long nowMs)
    {
        var activeDays = database.Rows("SELECT DISTINCT day FROM usage_daily ORDER BY day ASC", r => r.GetString(0));
        var peak = database.Rows("""
            SELECT day, SUM(input_tokens + output_tokens + cache_read_tokens + cache_write_tokens) AS total
            FROM usage_daily GROUP BY day ORDER BY total DESC, day DESC LIMIT 1
            """, r => (Day: r.GetString(0), Total: r.GetInt64(1))).Cast<(string Day, long Total)?>().FirstOrDefault();
        var streaks = UsageActivity.ComputeStreaks(activeDays, UsageActivity.LocalDayOf(nowMs, _zone));
        return new UsageActivityStats(activeDays, streaks.Current, streaks.Longest, peak?.Total ?? 0, peak?.Day, LongestChatMs(), 0);
    }

    /// <summary>
    /// 最长的一场连续聊天(毫秒):gaps-and-islands,先求相邻消息间隔,间隔超阈值处 +1 形成段号,再取各段跨度的最大值。
    /// ★ 只看 <c>internal = 0</c>(内部协调消息会把两场相隔很久的聊天连成一场);用 messages 而不是 runs / sessions.updated_at(前者是单次 run 的时长,后者会被重命名、归档推进而虚高)。
    /// 代价是 messages 会被清理删掉,清理之后这个数字会变小。
    /// </summary>
    public long LongestChatMs() => database.Value("""
        WITH ordered AS (
          SELECT session_id, created_at, created_at - LAG(created_at) OVER (PARTITION BY session_id ORDER BY created_at) AS gap
          FROM messages WHERE internal = 0
        ), marked AS (
          SELECT session_id, created_at,
                 SUM(CASE WHEN gap IS NULL OR gap > $gap THEN 1 ELSE 0 END) OVER (PARTITION BY session_id ORDER BY created_at ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS segment
          FROM ordered
        )
        SELECT MAX(span) FROM (SELECT MAX(created_at) - MIN(created_at) AS span FROM marked GROUP BY session_id, segment)
        """, ("$gap", ChatGapMs)) is { } v ? Convert.ToInt64(v) : 0;

    // 状态是 2.x 的 JSON { "lastId": "...", "timeZone": "..." }:手写读写,不引入反射序列化。
    private static (string LastId, string TimeZone) ReadState(string json, string fallbackZone)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            var id = root.TryGetProperty("lastId", out var l) && l.ValueKind == System.Text.Json.JsonValueKind.String ? l.GetString() ?? "" : "";
            var zone = root.TryGetProperty("timeZone", out var z) && z.ValueKind == System.Text.Json.JsonValueKind.String ? z.GetString() ?? fallbackZone : fallbackZone;
            return (id, zone);
        }
        catch (System.Text.Json.JsonException)
        {
            return ("", fallbackZone);
        }
    }

    private static string WriteState(string lastId, string zone)
    {
        var o = new System.Text.Json.Nodes.JsonObject { ["lastId"] = lastId, ["timeZone"] = zone };
        return o.ToJsonString();
    }
}
