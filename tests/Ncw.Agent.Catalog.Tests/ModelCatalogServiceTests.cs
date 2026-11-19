using System.Net;
using System.Text;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Pricing;

namespace Ncw.Agent.Catalog.Tests;

/// <summary>02 A8 与路线图「模型目录」:304、超时、超大响应、数据缩水一半以上、断网首次启动,加条件请求、缓存重启、退避、覆盖、价格。</summary>
public class ModelCatalogServiceTests : IDisposable
{
    private readonly FakeHttp _http = new();
    private readonly MemoryCatalogStore _store = new();
    private readonly MemoryOverrideStore _overrides = new();
    private readonly ManualClock _clock = new();
    private readonly ManualDelayer _delayer = new();
    private readonly List<ModelCatalogService> _services = [];

    private ModelCatalogService Create(ModelCatalogOptions? options = null, ICatalogStore? store = null)
    {
        var service = new ModelCatalogService(_http, store ?? _store, _overrides, _clock, _delayer, options);
        _services.Add(service);
        return service;
    }

    public void Dispose() => _services.ForEach(s => s.Dispose());

    // ── 断网首次启动 ──

    [Fact]
    public async Task First_start_offline_uses_the_embedded_seed_and_records_the_error()
    {
        _http.Throw(new HttpRequestException("no route to host"));
        var service = Create();

        Assert.Equal(CatalogSource.Seed, service.Current.Source);
        Assert.True(service.Current.ModelCount >= 10);
        Assert.NotNull(service.Get("anthropic", "claude-sonnet-4-5"));

        var result = await service.RefreshAsync();
        Assert.Equal(CatalogRefreshOutcome.Failed, result.Outcome);
        Assert.Equal(CatalogErrorKeys.Network, result.Error!.Key);
        Assert.Equal(CatalogSource.Seed, service.Current.Source); // 失败保留上一份(这里是种子)
        Assert.Equal(CatalogErrorKeys.Network, service.Status.LastError!.Key);
        Assert.NotNull(service.Status.LastCheckedAt);
        Assert.Null(_store.Value); // 没有拉到任何东西,磁盘上不留半成品
    }

    [Fact]
    public async Task The_seed_provides_prices_so_costs_are_known_even_before_the_first_sync()
    {
        var service = Create();
        var cost = PricingCalculator.CostOf(service.PriceBook, "anthropic", "claude-sonnet-4-5", new TokenUsage(1_000_000, 0), DateTimeOffset.UtcNow);
        Assert.Equal(CostState.Known, cost.State);
        Assert.Equal(3_000_000, cost.Micros);
        await Task.CompletedTask;
    }

    // ── 采用新数据 ──

    [Fact]
    public async Task A_fresh_download_is_adopted_persisted_and_announced_once()
    {
        _http.Ok(Samples.ModelsDev(12, 3), etag: "\"v1\"");
        var service = Create();
        var seen = new List<CatalogSnapshot>();
        service.Changed += seen.Add;
        var before = service.Current.Version;

        var result = await service.RefreshAsync();

        Assert.Equal(CatalogRefreshOutcome.Updated, result.Outcome);
        Assert.Equal(15, result.ModelCount);
        Assert.Equal(CatalogSource.Remote, service.Current.Source);
        Assert.Equal(15, service.Current.ModelCount);
        Assert.Single(seen);
        Assert.True(seen[0].Version > before);
        Assert.Same(service.Current, seen[0]); // 通知的就是全量快照
        Assert.Equal("\"v1\"", _store.Value!.Value.Meta.ETag);
        Assert.Equal(FileCatalogStore.Hash(_store.Value.Value.Body), service.Status.Sha256);
        Assert.Null(service.Status.LastError);
        Assert.Null(service.Get("anthropic", "claude-sonnet-4-5")); // 采用后整份替换种子
    }

    [Fact]
    public async Task Requests_carry_only_the_product_user_agent_and_ask_for_gzip()
    {
        _http.Ok(Samples.ModelsDev(12));
        var service = Create();
        await service.RefreshAsync();

        var seen = _http.Requests.Single();
        Assert.Equal("https://models.dev/api.json", seen.Url);
        Assert.Equal("NextCoWork/3.x", seen.Header("user-agent"));
        Assert.Equal("gzip", seen.Header("accept-encoding"));
        Assert.Null(seen.Header("if-none-match")); // 种子不带条件头
        Assert.DoesNotContain(seen.Headers.Keys, k => k is "cookie" or "authorization" or "x-device-id");
    }

    // ── 304 ──

    [Fact]
    public async Task A_conditional_request_that_gets_304_only_updates_the_last_checked_time()
    {
        _http.Ok(Samples.ModelsDev(12), etag: "\"v1\"").Status(HttpStatusCode.NotModified);
        var service = Create();
        await service.RefreshAsync();
        var seen = new List<CatalogSnapshot>();
        service.Changed += seen.Add;
        var version = service.Current.Version;
        var fetchedAt = service.Status.FetchedAt;

        _clock.Advance(TimeSpan.FromHours(6));
        var result = await service.RefreshAsync();

        Assert.Equal(CatalogRefreshOutcome.NotModified, result.Outcome);
        Assert.Equal("\"v1\"", _http.Requests[1].Header("if-none-match"));
        Assert.Equal(version, service.Current.Version); // 版本不变,没有变更通知
        Assert.Empty(seen);
        Assert.Equal(fetchedAt, service.Status.FetchedAt);
        Assert.Equal(_clock.GetUtcNow(), service.Status.LastCheckedAt);
        Assert.Equal(_clock.GetUtcNow(), _store.Value!.Value.Meta.CheckedAt); // 元数据落盘,重启后「上次检查」也对
    }

    [Fact]
    public async Task A_200_with_identical_content_is_treated_like_304()
    {
        // 上游不支持条件请求时的常态。
        var body = Samples.ModelsDev(12);
        _http.Ok(body).Ok(body);
        var service = Create();
        await service.RefreshAsync();
        var seen = new List<CatalogSnapshot>();
        service.Changed += seen.Add;

        var result = await service.RefreshAsync();

        Assert.Equal(CatalogRefreshOutcome.NotModified, result.Outcome);
        Assert.Empty(seen);
    }

    // ── 超时 ──

    [Fact]
    public async Task A_server_that_never_answers_times_out_and_the_previous_catalog_is_kept()
    {
        _http.Ok(Samples.ModelsDev(12)).Hang();
        var service = Create(new ModelCatalogOptions { RequestTimeout = TimeSpan.FromMilliseconds(80) }, store: _store);
        await service.RefreshAsync();
        var version = service.Current.Version;

        var result = await service.RefreshAsync();

        Assert.Equal(CatalogRefreshOutcome.Failed, result.Outcome);
        Assert.Equal(CatalogErrorKeys.Timeout, result.Error!.Key);
        Assert.Equal(version, service.Current.Version);
        Assert.Equal(12, service.Current.ModelCount);
    }

    [Fact]
    public async Task Cancelling_the_caller_is_not_a_timeout()
    {
        _http.Hang();
        var service = Create();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RefreshAsync(cts.Token));
        Assert.Null(service.Status.LastError);
    }

    // ── 超大响应 ──

    [Fact]
    public async Task A_declared_content_length_over_the_limit_is_refused_without_reading_the_body()
    {
        _http.Reply((_, _) =>
        {
            var response = FakeHttp.Json(HttpStatusCode.OK, "{}");
            response.Content.Headers.ContentLength = 33L * 1024 * 1024;
            return Task.FromResult(response);
        });
        var service = Create();

        var result = await service.RefreshAsync();

        Assert.Equal(CatalogRefreshOutcome.Failed, result.Outcome);
        Assert.Equal(CatalogErrorKeys.TooLarge, result.Error!.Key);
        Assert.Equal(CatalogSource.Seed, service.Current.Source);
    }

    [Fact]
    public async Task A_stream_that_exceeds_the_limit_without_declaring_it_is_cut_off()
    {
        // 没有 Content-Length(分块 / gzip 解压后)的无底洞。
        _http.Reply((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndlessStream()) }));
        var service = Create(new ModelCatalogOptions { MaxBytes = 100_000 });

        var result = await service.RefreshAsync();

        Assert.Equal(CatalogErrorKeys.TooLarge, result.Error!.Key);
    }

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)' ', offset, count);
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ── 数据缩水 ──

    [Fact]
    public async Task A_catalog_that_lost_half_or_more_of_its_models_is_refused()
    {
        _http.Ok(Samples.ModelsDev(100)).Ok(Samples.ModelsDev(50)).Ok(Samples.ModelsDev(51));
        var service = Create();
        await service.RefreshAsync();
        var version = service.Current.Version;

        var exactlyHalf = await service.RefreshAsync();
        Assert.Equal(CatalogRefreshOutcome.Rejected, exactlyHalf.Outcome);
        Assert.Equal(CatalogErrorKeys.Shrunk, exactlyHalf.Error!.Key);
        Assert.Equal(100, service.Current.ModelCount); // 保留上一份
        Assert.Equal(version, service.Current.Version);
        Assert.Equal(CatalogErrorKeys.Shrunk, service.Status.LastError!.Key);

        var slightlyLess = await service.RefreshAsync();
        Assert.Equal(CatalogRefreshOutcome.Updated, slightlyLess.Outcome); // 少 49% 是正常的下架
        Assert.Equal(51, service.Current.ModelCount);
    }

    [Fact]
    public async Task A_rejected_download_never_reaches_the_disk()
    {
        _http.Ok(Samples.ModelsDev(100), etag: "\"a\"").Ok(Samples.ModelsDev(10), etag: "\"b\"");
        var service = Create();
        await service.RefreshAsync();
        await service.RefreshAsync();

        Assert.Equal("\"a\"", _store.Value!.Value.Meta.ETag);
        Assert.Equal(100, ModelsDevParser.Parse(_store.Value.Value.Body, "x").ModelCount);
    }

    [Fact]
    public async Task An_empty_catalog_is_always_refused_and_so_is_invalid_json()
    {
        _http.Ok("{}").Ok("<html>captive portal</html>");
        var service = Create();

        var empty = await service.RefreshAsync();
        Assert.Equal(CatalogRefreshOutcome.Rejected, empty.Outcome);
        var html = await service.RefreshAsync();
        Assert.Equal(CatalogRefreshOutcome.Rejected, html.Outcome);
        Assert.Equal(CatalogErrorKeys.InvalidJson, html.Error!.Key);
        Assert.Equal(CatalogSource.Seed, service.Current.Source);
    }

    // ── 状态码、重定向 ──

    [Fact]
    public async Task Redirects_are_not_followed_and_error_statuses_are_failures()
    {
        _http.Status(HttpStatusCode.Redirect).Status(HttpStatusCode.InternalServerError);
        var service = Create();

        Assert.Equal(CatalogErrorKeys.Redirect, (await service.RefreshAsync()).Error!.Key);
        var server = await service.RefreshAsync();
        Assert.Equal(CatalogErrorKeys.HttpStatus, server.Error!.Key);
        Assert.Contains("500", server.Error.Detail);
        Assert.Equal(2, _http.Requests.Count); // 没有第二跳
    }

    [Fact]
    public async Task A_success_clears_the_last_error()
    {
        _http.Status(HttpStatusCode.BadGateway).Ok(Samples.ModelsDev(12));
        var service = Create();
        await service.RefreshAsync();
        Assert.NotNull(service.Status.LastError);
        await service.RefreshAsync();
        Assert.Null(service.Status.LastError);
    }

    // ── 重启后从磁盘缓存起步 ──

    [Fact]
    public async Task A_restart_starts_from_the_cached_snapshot_and_sends_conditional_headers()
    {
        _http.Ok(Samples.ModelsDev(12), etag: "\"v1\"");
        var first = Create();
        await first.RefreshAsync();

        _http.Status(HttpStatusCode.NotModified);
        var second = Create();
        Assert.Equal(CatalogSource.Cache, second.Current.Source);
        Assert.Equal(12, second.Current.ModelCount);
        Assert.NotNull(second.Status.FetchedAt);

        var result = await second.RefreshAsync();
        Assert.Equal(CatalogRefreshOutcome.NotModified, result.Outcome);
        Assert.Equal("\"v1\"", _http.Requests[^1].Header("if-none-match"));
    }

    [Fact]
    public void A_corrupted_cache_falls_back_to_the_seed()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ncwcat-{Guid.NewGuid():N}");
        try
        {
            var files = new FileCatalogStore(dir);
            var body = Encoding.UTF8.GetBytes(Samples.ModelsDev(12));
            files.Save(body, new CatalogCacheMeta("\"v1\"", null, _clock.GetUtcNow(), null, FileCatalogStore.Hash(body), 12));
            Assert.NotNull(files.Load((b, _) => b.Length));

            // 截断了正文:哈希对不上,不拿一份来历不明的数据当快照。
            File.WriteAllBytes(Path.Combine(dir, "catalog", "models-dev.json"), body[..100]);
            Assert.Null(files.Load((b, _) => b.Length));
            var service = Create(store: files);
            Assert.Equal(CatalogSource.Seed, service.Current.Source);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Downloads_go_through_a_draft_file_that_is_committed_or_discarded()
    {
        // 正文边下边写临时文件(不进 GC 堆);采用才替换,拒绝 / 内容没变都不留下临时文件,内容没变也不再解析
        var dir = Path.Combine(Path.GetTempPath(), $"ncwcat-{Guid.NewGuid():N}");
        try
        {
            var files = new FileCatalogStore(dir);
            var catalogDir = Path.Combine(dir, "catalog");
            var service = Create(store: files);
            var first = Samples.ModelsDev(12, 3);

            _http.Ok(first, etag: "\"v1\"");
            Assert.Equal(CatalogRefreshOutcome.Updated, (await service.RefreshAsync()).Outcome);
            Assert.Equal(first, File.ReadAllText(Path.Combine(catalogDir, "models-dev.json")));
            Assert.Equal(15, files.Load((b, _) => ModelsDevParser.Parse(b, "x").ModelCount)!.Value.Result);

            _http.Ok(first);   // 上游不支持条件请求:同样的内容再来一遍
            var same = await service.RefreshAsync();
            Assert.Equal((CatalogRefreshOutcome.NotModified, 15), (same.Outcome, same.ModelCount));

            _http.Ok("{ not json");
            Assert.Equal(CatalogRefreshOutcome.Rejected, (await service.RefreshAsync()).Outcome);
            Assert.Equal(first, File.ReadAllText(Path.Combine(catalogDir, "models-dev.json")));   // 旧的一份原样留着

            Assert.Equal(["models-dev.json", "models-dev.meta.json"], Directory.GetFiles(catalogDir).Select(f => Path.GetFileName(f)).Order().ToArray());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_file_store_round_trips_body_and_meta_atomically()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ncwcat-{Guid.NewGuid():N}");
        try
        {
            var files = new FileCatalogStore(dir);
            var body = Encoding.UTF8.GetBytes(Samples.ModelsDev(3));
            var meta = new CatalogCacheMeta("\"e\"", "Wed, 12 Nov 2026 00:00:00 GMT", _clock.GetUtcNow(), null, FileCatalogStore.Hash(body), 3);
            files.Save(body, meta);
            files.SaveMeta(meta with { CheckedAt = _clock.GetUtcNow() });

            var loaded = files.Load((b, _) => b.ToArray())!.Value;
            Assert.Equal(body, loaded.Result);
            Assert.Equal("\"e\"", loaded.Meta.ETag);
            Assert.Equal("Wed, 12 Nov 2026 00:00:00 GMT", loaded.Meta.LastModified);
            Assert.Equal(_clock.GetUtcNow(), loaded.Meta.CheckedAt);
            Assert.Empty(Directory.GetFiles(Path.Combine(dir, "catalog"), "*.tmp"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── 定时器 ──

    [Fact]
    public async Task The_timer_waits_30_seconds_then_6_hours_and_backs_off_exponentially_on_failure()
    {
        _http.Ok(Samples.ModelsDev(12)).Throw(new HttpRequestException("down")).Throw(new HttpRequestException("down")).Throw(new HttpRequestException("down"))
            .Ok(Samples.ModelsDev(13));
        var service = Create();
        service.Start();
        service.Start(); // 重复调用无效

        for (var i = 0; i < 5; i++)
        {
            await _delayer.ReleaseNextAsync();
            await WaitForRequestsAsync(i + 1);
        }

        await WaitForDelaysAsync(6);
        Assert.Equal(
            [
                TimeSpan.FromSeconds(30),     // 启动 30 秒后拉一次
                TimeSpan.FromHours(6),        // 成功:6 小时一次
                TimeSpan.FromMinutes(15),     // 失败:15 分钟起
                TimeSpan.FromMinutes(30),
                TimeSpan.FromMinutes(60),
                TimeSpan.FromHours(6),        // 再次成功:恢复
            ],
            _delayer.Delays.ToArray());
        await service.StopAsync();
    }

    [Fact]
    public void Backoff_doubles_from_15_minutes_and_stops_at_6_hours()
    {
        var service = Create();
        Assert.Equal(
            [15, 30, 60, 120, 240, 360, 360],
            Enumerable.Range(1, 7).Select(n => (int)service.Backoff(n).TotalMinutes).ToArray());
    }

    [Fact]
    public async Task The_interval_is_read_fresh_each_time_so_a_settings_change_applies_to_the_next_wait()
    {
        var hours = 6;
        _http.Ok(Samples.ModelsDev(12)).Ok(Samples.ModelsDev(12));
        var service = Create(new ModelCatalogOptions { Interval = () => TimeSpan.FromHours(hours) });
        service.Start();
        await _delayer.ReleaseNextAsync();
        await WaitForDelaysAsync(2);
        hours = 1;
        await _delayer.ReleaseNextAsync();
        await WaitForDelaysAsync(3);
        Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromHours(6), TimeSpan.FromHours(1)], _delayer.Delays.ToArray());
        await service.StopAsync();
    }

    private async Task WaitForRequestsAsync(int count)
    {
        for (var i = 0; i < 500 && _http.Requests.Count < count; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(_http.Requests.Count >= count);
    }

    private async Task WaitForDelaysAsync(int count)
    {
        for (var i = 0; i < 500 && _delayer.Delays.Count < count; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(_delayer.Delays.Count >= count);
    }

    // ── 用户覆盖优先于同步数据 ──

    [Fact]
    public async Task User_overrides_win_over_synced_data_and_survive_a_refresh()
    {
        _http.Ok(Samples.ModelsDev(12)).Ok(Samples.ModelsDev(12, priceScale: 2), etag: "\"v2\"");
        var service = Create();
        await service.RefreshAsync();
        var seen = new List<CatalogSnapshot>();
        service.Changed += seen.Add;

        service.SetOverride(new ModelOverride("anthropic", "claude-test-0", DisplayName: "  My Claude  ", ContextWindow: 123_456, Vision: false,
            Price: new PriceOverride(Currency.Cny, 21, 105)));
        var model = service.Get("anthropic", "claude-test-0")!;
        Assert.Equal("My Claude", model.Name);
        Assert.Equal(123_456, model.ContextWindow);
        Assert.Equal(64_000, model.MaxOutputTokens); // 没覆盖的字段跟随同步数据
        Assert.False(model.Capabilities.Vision);
        Assert.True(model.Overridden);
        Assert.Equal(Currency.Cny, model.Price!.Currency);
        Assert.Single(seen);

        // 同步来的新数据(价格翻倍)不能冲掉用户设的。
        await service.RefreshAsync();
        var after = service.Get("anthropic", "claude-test-0")!;
        Assert.Equal("My Claude", after.Name);
        Assert.Equal(Currency.Cny, after.Price!.Currency);
        Assert.Equal(6m, service.Get("anthropic", "claude-test-1")!.Price!.Tiers[0].Rate.Input); // 没覆盖的模型拿到新价
        Assert.Single(_overrides.Stored); // 落盘
    }

    [Fact]
    public async Task A_deleted_model_disappears_and_does_not_come_back_with_the_next_sync_until_restored()
    {
        _http.Ok(Samples.ModelsDev(12)).Ok(Samples.ModelsDev(12), etag: "\"x\"");
        var service = Create();
        await service.RefreshAsync();

        service.DeleteModel("anthropic", "claude-test-3");
        Assert.Null(service.Get("anthropic", "claude-test-3"));
        Assert.Equal(11, service.Current.ModelCount);
        Assert.Null(service.Describe("anthropic", "claude-test-3"));

        _http.Ok(Samples.ModelsDev(12) + " ");
        await service.RefreshAsync();
        Assert.Null(service.Get("anthropic", "claude-test-3"));

        service.RestoreModel("anthropic", "claude-test-3");
        Assert.NotNull(service.Get("anthropic", "claude-test-3"));
        Assert.Empty(_overrides.Stored);
    }

    [Fact]
    public async Task Removing_an_override_restores_the_synced_values_and_an_empty_override_is_dropped()
    {
        _http.Ok(Samples.ModelsDev(12));
        var service = Create();
        await service.RefreshAsync();
        service.SetOverride(new ModelOverride("anthropic", "claude-test-0", DisplayName: "Renamed"));
        service.RemoveOverride("anthropic", "claude-test-0");
        Assert.Equal("Claude Test 0", service.Get("anthropic", "claude-test-0")!.Name);

        service.SetOverride(new ModelOverride("anthropic", "claude-test-0"));
        Assert.Empty(service.Overrides);
    }

    [Fact]
    public void Overrides_are_loaded_from_the_store_on_start()
    {
        _overrides.Save([new ModelOverride(null, "claude-sonnet-4-5", DisplayName: "Sonnet (mine)")]);
        var service = Create();
        Assert.Equal("Sonnet (mine)", service.Get("anthropic", "claude-sonnet-4-5")!.Name);
    }

    [Fact]
    public void A_throwing_subscriber_does_not_break_the_service_or_the_other_subscribers()
    {
        var service = Create();
        var reached = false;
        service.Changed += _ => throw new InvalidOperationException("boom");
        service.Changed += _ => reached = true;
        service.SetOverride(new ModelOverride(null, "gpt-5", DisplayName: "x"));
        Assert.True(reached);
    }

    // ── 查询 ──

    [Fact]
    public void Queries_filter_by_provider_model_text_and_capabilities()
    {
        var service = Create();
        Assert.All(service.Query(new CatalogQuery(ProviderId: "anthropic")), m => Assert.Equal("anthropic", m.ProviderId));
        Assert.Single(service.Query(new CatalogQuery(ProviderId: "anthropic", ModelId: "claude-sonnet-4-5")));
        Assert.Contains(service.Query(new CatalogQuery(Text: "GPT")), m => m.Id == "gpt-5");
        Assert.All(service.Query(new CatalogQuery(Vision: false)), m => Assert.False(m.Capabilities.Vision));
        Assert.All(service.Query(new CatalogQuery(MinContextWindow: 1_000_000)), m => Assert.True(m.ContextWindow >= 1_000_000));
        Assert.Contains(service.Query(new CatalogQuery(Reasoning: true, ToolCall: true)), m => m.Id == "deepseek-reasoner");
        Assert.DoesNotContain(service.Query(new CatalogQuery(Reasoning: true)), m => m.Id == "deepseek-chat");
    }

    [Fact]
    public void Lookup_by_model_id_prefers_first_party_ignores_case_and_strips_gateway_prefixes()
    {
        var service = Create();
        var snapshot = service.Current;
        Assert.Equal("anthropic", snapshot.Find(null, "claude-sonnet-4-5")!.ProviderId);
        Assert.Equal("anthropic", snapshot.Find("my-gateway", "Claude-Sonnet-4-5")!.ProviderId);
        Assert.Equal("claude-sonnet-4-5", snapshot.Find(null, "openrouter/anthropic/claude-sonnet-4-5")!.Id);
        Assert.Null(snapshot.Find(null, "no-such-model"));
        Assert.Null(snapshot.Find(null, "vendor/"));
        // 提示的供应商正好是目录里的供应商 → 它那一条。
        Assert.Equal("deepseek", snapshot.Find("deepseek", "deepseek-chat")!.ProviderId);
    }

    // ── 价格 ──

    [Fact]
    public async Task Prices_are_adopted_automatically_and_new_prices_apply_to_later_requests()
    {
        _http.Ok(Samples.ModelsDev(12)).Ok(Samples.ModelsDev(12, priceScale: 2), etag: "\"v2\"");
        var service = Create();
        await service.RefreshAsync();
        var usage = new TokenUsage(1_000_000, 0);
        var at = DateTimeOffset.UtcNow;

        Assert.Equal(3_000_000, PricingCalculator.CostOf(service.PriceBook, "anthropic", "claude-test-0", usage, at).Micros);
        await service.RefreshAsync();
        Assert.Equal(6_000_000, PricingCalculator.CostOf(service.PriceBook, "anthropic", "claude-test-0", usage, at).Micros);
    }

    [Fact]
    public void A_model_without_a_trusted_price_is_unknown_not_zero()
    {
        var service = Create();
        var at = DateTimeOffset.UtcNow;
        var usage = new TokenUsage(1000, 1000);
        // deepseek 不是 USD 一手厂商:价格不采用,费用是「未知」而不是 0。
        Assert.Equal(CostState.Unknown, PricingCalculator.CostOf(service.PriceBook, "deepseek", "deepseek-chat", usage, at).State);
        Assert.Equal(CostState.Unknown, PricingCalculator.CostOf(service.PriceBook, null, "no-such-model", usage, at).State);
        Assert.Null(service.PriceBook.Find("x", "no-such-model", at));
    }

    [Fact]
    public void A_user_price_with_an_explicit_currency_fills_the_gap_and_a_gateway_provider_shares_the_model_price()
    {
        var service = Create();
        var at = DateTimeOffset.UtcNow;
        var usage = new TokenUsage(1_000_000, 0);

        service.SetOverride(new ModelOverride("my-gateway", "deepseek-chat", Price: new PriceOverride(Currency.Cny, 2, 8)));
        var cost = PricingCalculator.CostOf(service.PriceBook, "my-gateway", "deepseek-chat", usage, at);
        Assert.Equal(CostInfo.Of(2_000_000, Currency.Cny), cost);
        // 覆盖只对这个供应商生效。
        Assert.Equal(CostState.Unknown, PricingCalculator.CostOf(service.PriceBook, "deepseek", "deepseek-chat", usage, at).State);

        // 经中转站用的 Claude:供应商 id 不在目录里,按模型 id 取一手厂商的价。
        Assert.Equal(3_000_000, PricingCalculator.CostOf(service.PriceBook, "routin", "claude-sonnet-4-5", usage, at).Micros);
    }

    [Fact]
    public void A_user_override_without_a_catalog_entry_still_describes_the_model()
    {
        var service = Create();
        service.SetOverride(new ModelOverride("my-gateway", "private-model-1", ContextWindow: 32_000, MaxOutputTokens: 4096, ToolCall: true));
        var info = service.Describe("my-gateway", "private-model-1")!;
        Assert.Equal(32_000, info.ContextWindow);
        Assert.True(info.Overridden);
        Assert.Null(info.CatalogProviderId);
        Assert.Null(service.Describe("other", "private-model-1"));
    }
}
