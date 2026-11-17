using Microsoft.Data.Sqlite;
using Ncw.Core.Domain;

namespace Ncw.Agent.Storage.Tests;

/// <summary>
/// 用量查询与按日汇总。移植自 2.x 的 <c>db/__tests__/usage.test.ts</c>(明细落库、会话逐次账目、汇总与供应商 / 模型统计、工具计数回写)
/// 和 <c>usage-rollup.test.ts</c>(汇总 = 直查原表、幂等、乱序到达、分桶、NULL 与 0 分得开、币种不相加、回写之后仍一致、日桶 / 活跃度)。
/// 差异:3.0 不做逐条的请求日志(只做用量统计),2.x 里针对日志查询的分页 / 搜索 / 成败过滤用例不移植,落库与窗口的断言改走会话逐次账目和汇总。
/// 时区由测试注入(<see cref="TimeZoneInfo"/>),不依赖机器本地时区,所以这些用例在任何机器上结果一样。
/// </summary>
public sealed class UsageTests : IDisposable
{
    private const long Day = 24 * 60 * 60 * 1000;

    private static readonly TimeZoneInfo Shanghai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
    private static readonly long Now = new DateTimeOffset(2026, 11, 14, 4, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(); // 上海 12:00

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncwuse-{Guid.NewGuid():N}");
    private readonly AgentDatabase _db;
    private readonly UsageRepository _usage;
    private readonly UsageRollup _rollup;

    public UsageTests()
    {
        _db = AgentDatabase.Open(_root);
        _usage = new UsageRepository(_db);
        _rollup = new UsageRollup(_db, Shanghai);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>固定在上海当天正午的前 n 天(避免用例在接近零点时跨到另一天)。</summary>
    private static long NoonAgo(int days) => Now - days * Day;

    private static UsageRow Attempt(string id, long at, Func<UsageRow, UsageRow>? change = null)
    {
        var row = new UsageRow(
            id, at, $"run-{id}", "workspace-1", "session-1", 1, "provider-1", "Provider One", "anthropic", "https://provider.example/v1/messages", "assistant", "model-a",
            "model-a-20260901", 100, 20, 300, 50, 10, 5, 1_200, 250, true, 200, null, null, "end_turn", 2, 1_500_000, "USD", 0, null);
        return change is null ? row : change(row);
    }

    private void Put(string id, long at, Func<UsageRow, UsageRow>? change = null) => _usage.Insert(Attempt(id, at, change));

    // ───────────────────────── usage.test.ts ─────────────────────────

    [Fact]
    public void Stores_detailed_attempt_metadata_including_failures()
    {
        Put("a", 1_000);
        Put("b", 2_000, r => r with
        {
            ProviderId = "provider-2", ProviderName = "Fallback Cloud", UpstreamModel = "model-b", Ok = false, HttpStatus = 429, ErrorKind = "rate_limit",
            ErrorMessage = "too many requests", StopReason = null, InputTokens = 0, OutputTokens = 0, CacheReadTokens = 0, CacheWriteTokens = 0, ThinkingTokens = null,
            TimeToFirstTokenMs = null, CostMicros = null, Currency = null,
        });

        var item = _usage.SessionAttempts("session-1").Single(r => !r.Ok);
        Assert.Equal(("b", "Fallback Cloud", 429, "rate_limit", "too many requests"), (item.Id, item.ProviderName, item.HttpStatus, item.ErrorKind, item.ErrorMessage));
    }

    [Fact]
    public void Returns_every_attempt_for_only_the_requested_session_including_fallback_models_and_failed_retries()
    {
        Put("a", 1_000);
        Put("b", 2_000, r => r with { RunId = "run-a", UpstreamModel = "model-b", Ok = false, CostMicros = null, Currency = null });
        Put("foreign", 3_000, r => r with { SessionId = "session-2" });

        Assert.Equal([("a", "model-a", (long?)1_500_000), ("b", "model-b", null)], _usage.SessionAttempts("session-1").Select(r => (r.Id, r.UpstreamModel, r.CostMicros)));
        Assert.Equal(["foreign"], _usage.SessionAttempts("session-2").Select(r => r.Id));
    }

    [Fact]
    public void Aggregates_token_cache_cost_latency_model_provider_and_tool_totals()
    {
        Put("a", 1_000);
        Put("b", 2_000, r => r with
        {
            InputTokens = 50, OutputTokens = 10, CacheReadTokens = 0, CacheWriteTokens = 0, CacheWrite1hTokens = 0, ThinkingTokens = 3, ThinkingTokensEstimated = true,
            LatencyMs = 800, TimeToFirstTokenMs = 150, CostMicros = 500_000, ToolCalls = 1, ToolErrors = 1,
        });
        Put("outside", 10_000, r => r with { CostMicros = 99_000_000 });

        var window = new UsageWindow(0, 3_000);
        var summary = _usage.Summary(window);
        Assert.Equal((2L, 2L, 150L, 30L, 300L, 50L, 10L, 8L, 1L), (summary.RequestCount, summary.SuccessCount, summary.InputTokens, summary.OutputTokens, summary.CacheReadTokens,
            summary.CacheWriteTokens, summary.CacheWrite1hTokens, summary.ThinkingTokens, summary.EstimatedThinkingRequestCount));
        Assert.Equal((530L, 1_000d, 200d, 3L, 1L), (summary.TotalTokens, summary.AverageLatencyMs, summary.AverageTimeToFirstTokenMs, summary.ToolCalls, summary.ToolErrors));
        Assert.Equal(300d / 450, summary.CacheHitRate!.Value, 9);
        Assert.Equal([new UsageCostTotal("USD", 2_000_000)], summary.Costs);

        var provider = _usage.ProviderStats(window)[0];
        Assert.Equal(("provider-1", "Provider One", 2L), (provider.Id, provider.Label, provider.RequestCount));
        Assert.Equal([new UsageCostTotal("USD", 2_000_000)], provider.Costs);
        var model = _usage.ModelStats(window)[0];
        Assert.Equal(("model-a", 2L, 8L), (model.Id, model.RequestCount, model.ThinkingTokens));
    }

    [Fact]
    public void Finalizes_tool_outcomes_on_the_latest_successful_tool_use_attempt_only()
    {
        Put("retry-failed", 1_000, r => r with { RunId = "shared-run", Ok = false, StopReason = null, ToolCalls = 0 });
        Put("tool-turn", 2_000, r => r with { RunId = "shared-run", StopReason = "tool_use", ToolCalls = 2, ToolErrors = 0 });
        Put("final-turn", 3_000, r => r with { RunId = "shared-run", StopReason = "end_turn", ToolCalls = 0, ToolErrors = 0 });

        Assert.True(_usage.UpdateToolsForRun("shared-run", 2, 1));
        Assert.False(_usage.UpdateToolsForRun("missing-run", 1, 1));

        var items = _usage.SessionAttempts("session-1");
        Assert.Equal((2, 1), (items.Single(r => r.Id == "tool-turn").ToolCalls, items.Single(r => r.Id == "tool-turn").ToolErrors));
        Assert.Equal((0, 0), (items.Single(r => r.Id == "final-turn").ToolCalls, items.Single(r => r.Id == "final-turn").ToolErrors));
    }

    [Fact]
    public void The_window_includes_its_start_and_excludes_its_end()
    {
        Put("before", 999);
        Put("in", 1_000);
        Put("end", 2_000);

        Assert.Equal(1, _usage.Summary(new UsageWindow(1_000, 2_000)).RequestCount);
        Assert.Equal(2, _usage.Summary(new UsageWindow(null, 2_000)).RequestCount);
    }

    [Fact]
    public void Cost_is_null_when_unpriced_and_never_zero_and_currencies_are_never_added()
    {
        Put("usd", 1_000, r => r with { CostMicros = 1_000_000, Currency = "USD" });
        Put("cny", 1_001, r => r with { CostMicros = 7_000_000, Currency = "CNY" });
        Put("unknown", 1_002, r => r with { CostMicros = null, Currency = null });

        var summary = _usage.Summary(new UsageWindow(null, 2_000));
        Assert.Equal([new UsageCostTotal("CNY", 7_000_000), new UsageCostTotal("USD", 1_000_000)], summary.Costs);
        Assert.Null(_usage.SessionAttempts("session-1").Single(r => r.Id == "unknown").CostMicros);
    }

    // ───────────────────────── usage-rollup.test.ts ─────────────────────────

    private List<(string Provider, string Model, string Currency, long Requests, long Success, long In, long Out, long CacheRead, long? Cost, long Priced)> Direct(string day)
    {
        var (from, to) = UsageRollup.DayBounds(day, Shanghai);
        return Query($"""
            SELECT provider_id, upstream_model, COALESCE(currency, '') AS currency, COUNT(*), SUM(ok), SUM(input_tokens), SUM(output_tokens), SUM(cache_read_tokens), SUM(cost_micros), COUNT(cost_micros)
            FROM usage_records WHERE at >= {from} AND at < {to} GROUP BY provider_id, upstream_model, COALESCE(currency, '') ORDER BY upstream_model, currency
            """);
    }

    private List<(string Provider, string Model, string Currency, long Requests, long Success, long In, long Out, long CacheRead, long? Cost, long Priced)> Buckets(string day) => Query($"""
        SELECT provider_id, upstream_model, currency, request_count, success_count, input_tokens, output_tokens, cache_read_tokens, cost_micros, priced_count
        FROM usage_daily WHERE day = '{day}' ORDER BY upstream_model, currency
        """);

    private List<(string, string, string, long, long, long, long, long, long?, long)> Query(string sql)
    {
        using var command = _db.Connection.CreateCommand();
        command.CommandText = sql;
        using var r = command.ExecuteReader();
        var list = new List<(string, string, string, long, long, long, long, long, long?, long)>();
        while (r.Read())
        {
            list.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.IsDBNull(8) ? null : r.GetInt64(8), r.GetInt64(9)));
        }

        return list;
    }

    private static string DayOf(long at) => UsageActivity.LocalDayOf(at, Shanghai);

    [Fact]
    public void The_rollup_equals_a_direct_query_of_the_raw_table()
    {
        var today = DayOf(NoonAgo(0));
        Put("a1", NoonAgo(0));
        Put("a2", NoonAgo(0), r => r with { UpstreamModel = "model-b", OutputTokens = 99 });
        Put("a3", NoonAgo(0), r => r with { Ok = false, CostMicros = null, Currency = null });

        _rollup.Refresh(Now);

        Assert.Equal(Direct(today), Buckets(today));
    }

    [Fact]
    public void Refreshing_repeatedly_does_not_change_the_result()
    {
        var today = DayOf(NoonAgo(0));
        Put("a1", NoonAgo(0));
        Put("a2", NoonAgo(0));

        _rollup.Refresh(Now);
        var first = Buckets(today);
        _rollup.Refresh(Now);
        _rollup.Refresh(Now);

        Assert.Equal(first, Buckets(today));
        Assert.Equal(2, first[0].Requests);
    }

    /// <summary>
    /// 整张表的设计前提:<c>usage_records.id</c> 是写入时刻,<c>at</c> 是请求开始时刻。一个慢请求的 id 大于水位、at 却落在好几天前;
    /// 按 <c>at &gt; 水位</c> 取增量的实现会把这条账永久漏掉,而且只表现为「那天的钱少一点」。
    /// </summary>
    [Fact]
    public void An_out_of_order_old_record_is_recomputed_into_the_day_it_belongs_to()
    {
        var oldDay = DayOf(NoonAgo(5));
        Put("a1", NoonAgo(5));
        _rollup.Refresh(Now);
        Assert.Equal(1, Buckets(oldDay)[0].Requests);

        Put("z9", NoonAgo(5) + 60_000, r => r with { OutputTokens = 7 }); // id 更大(写入更晚),at 仍在 5 天前
        _rollup.Refresh(Now);

        Assert.Equal(Direct(oldDay), Buckets(oldDay));
        Assert.Equal(2, Buckets(oldDay)[0].Requests);
    }

    [Fact]
    public void Days_are_bucketed_separately()
    {
        Put("a1", NoonAgo(3));
        Put("a2", NoonAgo(1));
        Put("a3", NoonAgo(1));
        _rollup.Refresh(Now);

        Assert.Equal(1, Buckets(DayOf(NoonAgo(3)))[0].Requests);
        Assert.Equal(2, Buckets(DayOf(NoonAgo(1)))[0].Requests);
    }

    [Fact]
    public void A_wholly_unpriced_bucket_has_null_cost_not_zero()
    {
        var today = DayOf(NoonAgo(0));
        Put("a1", NoonAgo(0), r => r with { CostMicros = null, Currency = null });
        _rollup.Refresh(Now);

        var row = Buckets(today)[0];
        Assert.Null(row.Cost);
        Assert.Equal((0, ""), (row.Priced, row.Currency));
    }

    [Fact]
    public void Truly_free_zero_and_unknown_null_are_distinguishable()
    {
        var today = DayOf(NoonAgo(0));
        Put("a1", NoonAgo(0), r => r with { CostMicros = 0, Currency = "USD" });
        _rollup.Refresh(Now);

        var row = Buckets(today)[0];
        Assert.Equal((0L, 1L), (row.Cost, row.Priced));
    }

    [Fact]
    public void A_partially_priced_bucket_sums_only_the_priced_rows_and_priced_count_is_below_request_count()
    {
        var today = DayOf(NoonAgo(0));
        Put("a1", NoonAgo(0), r => r with { CostMicros = 2_000_000 });
        Put("a2", NoonAgo(0), r => r with { CostMicros = null, Currency = "USD" });
        _rollup.Refresh(Now);

        var row = Buckets(today)[0];
        Assert.Equal((2_000_000L, 1L, 2L), (row.Cost, row.Priced, row.Requests));
    }

    [Fact]
    public void Different_currencies_are_separate_buckets_and_never_added()
    {
        var today = DayOf(NoonAgo(0));
        Put("a1", NoonAgo(0), r => r with { CostMicros = 1_000_000, Currency = "USD" });
        Put("a2", NoonAgo(0), r => r with { CostMicros = 7_000_000, Currency = "CNY" });
        _rollup.Refresh(Now);

        var rows = Buckets(today);
        Assert.Equal(2, rows.Count);
        Assert.Equal([("CNY", 7_000_000L), ("USD", 1_000_000L)], rows.Select(r => (r.Currency, r.Cost!.Value)).Order());
    }

    /// <summary>未计价行的 currency 在原表里是 NULL;汇总表用空串(SQLite 主键里的 NULL 不参与唯一性判定,照搬 NULL 会让同一天同一模型的未计价行反复插入)。</summary>
    [Fact]
    public void Unpriced_rows_merge_into_one_row_instead_of_one_more_per_refresh()
    {
        var today = DayOf(NoonAgo(0));
        Put("a1", NoonAgo(0), r => r with { CostMicros = null, Currency = null });
        Put("a2", NoonAgo(0), r => r with { CostMicros = null, Currency = null });
        _rollup.Refresh(Now);
        _rollup.Refresh(Now);

        var rows = Buckets(today);
        Assert.Single(rows);
        Assert.Equal(2, rows[0].Requests);
    }

    /// <summary>工具计数在 Run 结束后回写历史行;汇总表刻意不存这两列(存了就必然在某些刷新时机下偏小)。</summary>
    [Fact]
    public void After_a_tool_count_write_back_the_rollup_still_equals_the_direct_query()
    {
        var today = DayOf(NoonAgo(0));
        Put("a1", NoonAgo(0), r => r with { StopReason = "tool_use" });
        _rollup.Refresh(Now);

        _usage.UpdateToolsForRun("run-a1", 11, 3);
        _rollup.Refresh(Now);

        Assert.Equal(Direct(today), Buckets(today));
    }

    [Fact]
    public void Refreshing_an_empty_database_does_not_fail()
    {
        _rollup.Refresh(Now);
        Assert.Empty(_rollup.Series(new UsageWindow(null, Now)));
    }

    [Fact]
    public void The_daily_series_is_ascending_and_bounded_by_the_window()
    {
        Put("a1", NoonAgo(10));
        Put("a2", NoonAgo(3));
        Put("a3", NoonAgo(0));
        _rollup.Refresh(Now);

        Assert.Equal([DayOf(NoonAgo(10)), DayOf(NoonAgo(3)), DayOf(NoonAgo(0))], _rollup.Series(new UsageWindow(null, Now + 1)).Select(b => b.Day));
        Assert.Equal([DayOf(NoonAgo(3)), DayOf(NoonAgo(0))], _rollup.Series(new UsageWindow(NoonAgo(4), Now + 1)).Select(b => b.Day));
    }

    [Fact]
    public void Series_buckets_carry_every_field()
    {
        Put("a1", NoonAgo(0));
        _rollup.Refresh(Now);

        var bucket = Assert.Single(_rollup.Series(new UsageWindow(null, Now + 1)));
        Assert.Equal(("provider-1", "Provider One", "model-a", "assistant", "USD", 1L, 1L), (bucket.ProviderId, bucket.ProviderName, bucket.UpstreamModel, bucket.Alias, bucket.Currency, bucket.RequestCount, bucket.SuccessCount));
        Assert.Equal((100L, 20L, 300L, 50L, 10L, 5L), (bucket.InputTokens, bucket.OutputTokens, bucket.CacheReadTokens, bucket.CacheWriteTokens, bucket.CacheWrite1hTokens, bucket.ThinkingTokens));
        Assert.Equal((1_500_000L, 1L, 1_200L, 250L, 1L), (bucket.CostMicros, bucket.PricedCount, bucket.LatencySum, bucket.TtftSum, bucket.TtftCount));
    }

    [Fact]
    public void A_missing_first_token_time_is_not_counted_so_cross_bucket_averages_are_not_diluted()
    {
        Put("a1", NoonAgo(0), r => r with { TimeToFirstTokenMs = 400 });
        Put("a2", NoonAgo(0), r => r with { TimeToFirstTokenMs = null });
        _rollup.Refresh(Now);

        var bucket = Assert.Single(_rollup.Series(new UsageWindow(null, Now + 1)));
        Assert.Equal((400L, 1L), (bucket.TtftSum, bucket.TtftCount));
    }

    [Fact]
    public void A_changed_time_zone_rebuilds_the_whole_table_instead_of_appending_in_the_new_zone()
    {
        Put("a1", NoonAgo(0));
        _rollup.Refresh(Now);
        var shanghaiDays = _rollup.Series(new UsageWindow(null, Now + 1)).Select(b => b.Day).ToList();

        // 同一条记录在「纽约」属于前一天;换了时区,历史桶的边界整体移位,必须整表重建
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var rollup = new UsageRollup(_db, newYork);
        rollup.Refresh(Now);

        var newYorkDays = rollup.Series(new UsageWindow(null, Now + 1)).Select(b => b.Day).ToList();
        Assert.Equal([UsageActivity.LocalDayOf(NoonAgo(0), newYork)], newYorkDays);
        Assert.Single(newYorkDays);
        Assert.Single(shanghaiDays);
    }

    [Fact]
    public void Day_bounds_are_a_24_hour_interval_except_on_daylight_saving_switch_days()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var (from, to) = UsageRollup.DayBounds("2026-03-08", newYork); // 春季跳过一小时
        Assert.Equal(23 * 60 * 60 * 1000L, to - from);
        (from, to) = UsageRollup.DayBounds("2026-11-01", newYork); // 秋季多一小时
        Assert.Equal(25 * 60 * 60 * 1000L, to - from);
        (from, to) = UsageRollup.DayBounds("2026-06-01", newYork);
        Assert.Equal(24 * 60 * 60 * 1000L, to - from);
    }

    // ───────────────────────── 活跃度 ─────────────────────────

    [Fact]
    public void Activity_days_and_streaks_come_from_the_rollup()
    {
        Put("a1", NoonAgo(2));
        Put("a2", NoonAgo(1));
        Put("a3", NoonAgo(0));
        _rollup.Refresh(Now);

        var stats = _rollup.Activity(Now);
        Assert.Equal([DayOf(NoonAgo(2)), DayOf(NoonAgo(1)), DayOf(NoonAgo(0))], stats.ActiveDays);
        Assert.Equal((3, 3, 0), (stats.CurrentStreak, stats.LongestStreak, stats.SyncedDevices));
    }

    [Fact]
    public void The_peak_is_the_day_with_the_most_tokens()
    {
        Put("a1", NoonAgo(2), r => r with { InputTokens = 1, OutputTokens = 1, CacheReadTokens = 0, CacheWriteTokens = 0 });
        Put("a2", NoonAgo(1), r => r with { InputTokens = 500, OutputTokens = 500, CacheReadTokens = 0, CacheWriteTokens = 0 });
        _rollup.Refresh(Now);

        var stats = _rollup.Activity(Now);
        Assert.Equal((DayOf(NoonAgo(1)), 1000L), (stats.PeakDay, stats.PeakDayTokens));
    }

    [Fact]
    public void Activity_of_an_empty_database_is_zeros_not_an_error()
    {
        var stats = _rollup.Activity(Now);
        Assert.Empty(stats.ActiveDays);
        Assert.Null(stats.PeakDay);
        Assert.Equal((0, 0, 0L, 0L), (stats.CurrentStreak, stats.LongestStreak, stats.PeakDayTokens, stats.LongestChatMs));
    }

    [Fact]
    public void The_longest_chat_splits_on_gaps_over_thirty_minutes_and_ignores_internal_messages()
    {
        _db.Run0("INSERT INTO workspaces (id, last_opened_at, json, owner) VALUES ('ws', 0, '{\"id\":\"ws\"}', 'local')");
        _db.Run0("INSERT INTO sessions (id, workspace_id, title, model, mode, thinking, root_path_at_creation, created_at, updated_at, json) VALUES ('s', 'ws', 't', 'm', 'code', 'auto', '/', 0, 0, '{}')");
        var minute = 60_000L;
        long[] times = [0, 10 * minute, 25 * minute, 100 * minute, 110 * minute]; // 段一 25 分钟,段二 10 分钟
        for (var i = 0; i < times.Length; i++)
        {
            _db.Run0($"INSERT INTO messages (id, session_id, ordinal, role, parts, created_at, internal) VALUES ('m{i}', 's', {i}, 'user', '[]', {times[i]}, 0)");
        }

        // 一条内部协调消息落在两段中间:它不能把两场相隔很久的聊天连成一场
        _db.Run0($"INSERT INTO messages (id, session_id, ordinal, role, parts, created_at, internal) VALUES ('mi', 's', 9, 'user', '[]', {60 * minute}, 1)");

        Assert.Equal(25 * minute, _rollup.LongestChatMs());
    }
}

internal static class UsageTestExtensions
{
    public static void Run0(this AgentDatabase db, string sql)
    {
        using var command = db.Connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
