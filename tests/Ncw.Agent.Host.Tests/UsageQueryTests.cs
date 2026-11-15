using Ncw.Agent.Catalog;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Tests.Support;
using Ncw.Agent.Kernel.Loop;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// 用量查询经协议的端到端(docs/design/10 的 <c>usage</c> 模块 → <c>usage.*</c>):真实 Agent + 真实路由 + 假上游。
/// 一个 Run 经真实路由完成后,<c>usage.*</c> 查到的数字<b>对得上 Run 的记账</b>:token、冻结的费用(按目录价格)、逐次尝试、按供应商 / 模型的统计、日桶和活跃度;
/// 不同的窗口、过滤与分页;只读且 viewer 起。
/// </summary>
public sealed class UsageQueryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Origin = new(2026, 11, 14, 4, 0, 0, TimeSpan.Zero);

    private readonly FakeUpstream _up = new();
    private readonly ManualClock _clock = new(Origin);
    private readonly ConversationRig _rig;
    private DeviceClient _owner = null!;
    private ProjectDto _project = null!;

    public UsageQueryTests()
    {
        _rig = new ConversationRig(_ => new AgentServices
        {
            Http = _up,
            CatalogDelayer = new NeverDelay(),
            CatalogStore = new MemoryCatalogStore(),
            Delayer = new NoDelay(),
            Loop = new AgentLoopOptions { ResumeDelays = [] },
        }, _clock);
    }

    public async ValueTask InitializeAsync()
    {
        await _rig.InitializeAsync();
        _owner = await _rig.ConnectAsync("owner");
        _project = await _rig.AddProjectAsync(_owner);

        // 目录(价格:输入 3 / 输出 15 每百万)+ 一家供应商,凭据在保险库里
        _up.On("models.dev/api.json", () => Replies.Json(Replies.ModelsDev(12), etag: "\"v1\""));
        var refreshed = await _owner.CallAsync(CatalogMethods.Refresh, Unit.Value);
        Assert.Equal(CatalogRefreshOutcomes.Updated, refreshed.Outcome);
        await _owner.CallAsync(ProvidersMethods.Upsert, new ProvidersUpsertParams("gw", "Gateway", "openai-chat", "https://gw.example.com/v1"));
        await _owner.CallAsync(ProvidersMethods.CredentialSet, new CredentialSetParams("gw", "sk-usage-test-1234"));
        await _owner.CallAsync(ProvidersMethods.ModelsSetAliases, new SetAliasesParams("gw", ["claude-test-0", "claude-test-1"]));
    }

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private async Task<string> RunAsync(string model, int prompt, int completion, string text = "hello")
    {
        _up.On("gw.example.com/v1/chat/completions", () => Replies.ChatStream(model, "ok", prompt, completion));
        var session = await _rig.CreateSessionAsync(_owner, _project, model);
        var start = await _owner.CallAsync(ConversationMethods.RunsStart,
            new RunsStartParams(Guid.NewGuid().ToString("N"), session.Id, Guid.NewGuid().ToString("N"), [new InputPartDto(InputPartKinds.Text, text)], new SendOptionsDto(model, PermissionMode: "ask")));
        await (await RunWatch.OpenAsync(_owner, start.RunId)).SettledAsync();
        return session.Id;
    }

    private static UsageWindowDto Everything => new(null, long.MaxValue / 2);

    [Fact]
    public async Task The_numbers_usage_returns_match_what_the_run_booked()
    {
        var sessionId = await RunAsync("claude-test-0", 1000, 500);

        // Run 自己的记账(会话详情里按 Run 聚合,同一张表)
        var page = await _owner.CallAsync(ConversationMethods.SessionsGet, new SessionRefParams(sessionId));
        var booked = page.RunUsage.Values.Single();
        Assert.Equal((1000L, 500L), (booked.InputTokens, booked.OutputTokens));
        Assert.Equal(new CostDto("known", 10_500, "usd"), booked.Cost); // 1000 × 3 + 500 × 15 微美元

        var summary = await _owner.CallAsync(UsageMethods.GetSummary, Everything);
        Assert.Equal((1L, 1L, 0L, 1000L, 500L, 1500L), (summary.RequestCount, summary.SuccessCount, summary.FailedCount, summary.InputTokens, summary.OutputTokens, summary.TotalTokens));
        Assert.Equal([new UsageCostTotalDto("USD", 10_500)], summary.Costs);

        var attempts = (await _owner.CallAsync(UsageMethods.GetSessionAttempts, new UsageSessionRefParams(sessionId))).Attempts;
        var attempt = Assert.Single(attempts);
        Assert.Equal((sessionId, "gw", "claude-test-0", true, 10_500L, "USD"), (attempt.SessionId, attempt.ProviderId, attempt.Alias, attempt.Ok, attempt.CostMicros, attempt.Currency));
        Assert.Equal(_project.WorkspaceId, attempt.WorkspaceId);
        Assert.Equal(Origin.ToUnixTimeMilliseconds(), attempt.At);

        var providers = (await _owner.CallAsync(UsageMethods.GetProviderStats, Everything)).Stats;
        Assert.Equal(("gw", "Gateway", 1L), (providers[0].Id, providers[0].Label, providers[0].RequestCount));
        Assert.Equal([new UsageCostTotalDto("USD", 10_500)], providers[0].Costs);
        var models = (await _owner.CallAsync(UsageMethods.GetModelStats, Everything)).Stats;
        Assert.Equal("claude-test-0", Assert.Single(models).Id);
    }

    [Fact]
    public async Task Logs_filter_by_text_and_status_and_the_window_is_half_open()
    {
        await RunAsync("claude-test-0", 100, 10);
        await RunAsync("claude-test-1", 200, 20);

        var all = await _owner.CallAsync(UsageMethods.GetRequestLogs, new UsageLogsQuery(null, long.MaxValue / 2, Limit: 10));
        Assert.Equal(2, all.Total);

        var one = await _owner.CallAsync(UsageMethods.GetRequestLogs, new UsageLogsQuery(null, long.MaxValue / 2, Query: "claude-test-1"));
        Assert.Equal("claude-test-1", Assert.Single(one.Items).UpstreamModel);
        Assert.Equal(0, (await _owner.CallAsync(UsageMethods.GetRequestLogs, new UsageLogsQuery(null, long.MaxValue / 2, Status: "failed"))).Total);

        var at = Origin.ToUnixTimeMilliseconds();
        Assert.Equal(2, (await _owner.CallAsync(UsageMethods.GetSummary, new UsageWindowDto(at, at + 1))).RequestCount);  // 含起点
        Assert.Equal(0, (await _owner.CallAsync(UsageMethods.GetSummary, new UsageWindowDto(at - 5, at))).RequestCount);  // 不含终点
        Assert.Equal(ErrorCodes.InvalidRequest, (await Assert.ThrowsAsync<ProtocolException>(() => _owner.CallAsync(UsageMethods.GetSummary, new UsageWindowDto(10, 5)))).Code);
        Assert.Equal(ErrorCodes.InvalidRequest, (await Assert.ThrowsAsync<ProtocolException>(() => _owner.CallAsync(UsageMethods.GetRequestLogs, new UsageLogsQuery(null, 10, Status: "nope")))).Code);
    }

    [Fact]
    public async Task The_daily_series_and_activity_come_from_the_rollup_and_a_fresh_run_shows_up_immediately()
    {
        await RunAsync("claude-test-0", 1000, 500);

        // 刚聊完就查:查询前先刷一次汇总,不依赖任何后台定时器
        var series = (await _owner.CallAsync(UsageMethods.GetDailySeries, Everything)).Buckets;
        var bucket = Assert.Single(series);
        Assert.Equal(("gw", "claude-test-0", "USD", 1L, 1000L, 500L, 10_500L, 1L), (bucket.ProviderId, bucket.UpstreamModel, bucket.Currency, bucket.RequestCount, bucket.InputTokens, bucket.OutputTokens, bucket.CostMicros, bucket.PricedCount));

        await RunAsync("claude-test-0", 10, 5);
        var again = Assert.Single((await _owner.CallAsync(UsageMethods.GetDailySeries, Everything)).Buckets);
        Assert.Equal((2L, 1010L, 10_500L + 10 * 3 + 5 * 15), (again.RequestCount, again.InputTokens, again.CostMicros));

        var activity = await _owner.CallAsync(UsageMethods.GetActivityStats, Unit.Value);
        Assert.Single(activity.ActiveDays);
        Assert.Equal((1, 1, 0), (activity.CurrentStreak, activity.LongestStreak, activity.SyncedDevices));
        Assert.Equal(1000L + 500 + 10 + 5, activity.PeakDayTokens);
        Assert.NotNull(activity.PeakDay);
    }

    [Fact]
    public async Task A_failed_attempt_is_counted_with_its_error_and_an_unpriced_model_has_unknown_cost_not_zero()
    {
        // 上游 500:请求失败(不重试的话只有一次尝试);失败的请求没有费用
        _up.On("gw.example.com/v1/chat/completions", () => Replies.Json("{\"error\":\"boom\"}", status: System.Net.HttpStatusCode.BadRequest));
        var session = await _rig.CreateSessionAsync(_owner, _project, "claude-test-0");
        var start = await _owner.CallAsync(ConversationMethods.RunsStart,
            new RunsStartParams(Guid.NewGuid().ToString("N"), session.Id, Guid.NewGuid().ToString("N"), [new InputPartDto(InputPartKinds.Text, "x")], new SendOptionsDto("claude-test-0", PermissionMode: "ask")));
        await (await RunWatch.OpenAsync(_owner, start.RunId)).SettledAsync();

        var summary = await _owner.CallAsync(UsageMethods.GetSummary, Everything);
        Assert.Equal((1L, 0L, 1L), (summary.RequestCount, summary.SuccessCount, summary.FailedCount));
        var attempt = Assert.Single((await _owner.CallAsync(UsageMethods.GetSessionAttempts, new UsageSessionRefParams(session.Id))).Attempts);
        Assert.False(attempt.Ok);
        Assert.Equal(400, attempt.HttpStatus);
        Assert.NotNull(attempt.ErrorKind);
        Assert.Null(attempt.CostMicros); // 未知,绝不是 0
        Assert.Empty(summary.Costs);
    }
}
