using System.Reflection;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Pricing;
using Ncw.Agent.Llm.Routing;

namespace Ncw.Agent.Catalog;

public sealed class ModelCatalogOptions
{
    /// <summary>Agent 启动后第一次拉取的等待(02 A8:30 秒)。</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>两次成功检查之间的间隔(02 A8:6 小时,设置里可以改——每次现读)。</summary>
    public Func<TimeSpan> Interval { get; init; } = () => TimeSpan.FromHours(6);

    /// <summary>沿用 2.x <c>models-dev.ts</c>:30 秒超时。</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>沿用 2.x:实测约 5.6MB,32MB 防的是被劫持成无底洞,不是正常增长。</summary>
    public long MaxBytes { get; init; } = 32L * 1024 * 1024;

    /// <summary>失败后的重试间隔从 15 分钟开始指数退避,最长 6 小时。</summary>
    public TimeSpan RetryBase { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan RetryMax { get; init; } = TimeSpan.FromHours(6);

    /// <summary>新数据的模型数 ≤ 上一份的这个比例就视为异常数据(「少一半以上」)。</summary>
    public double ShrinkRatio { get; init; } = 0.5;

    /// <summary>只写产品名和主版本,不带任何用户或设备标识(02 A8)。</summary>
    public string UserAgent { get; init; } = "NextCoWork/3.x";

    public string Url { get; init; } = ModelsDevOptions.Url;

    public ModelsDevOptions ModelsDev { get; init; } = ModelsDevOptions.Default;
}

/// <summary>
/// 模型目录(02 A8、决策 D19):定时从 models.dev 拉取、校验、合并,控制端只通过协议读取。
/// <list type="bullet">
/// <item>数据:内置种子 ← models.dev 最新数据(采用后整份替换种子)← 用户覆盖(改名、自定义窗口 / 价格、删除)。供应商自己的 <c>/models</c> 列表只决定「这家有哪些模型」,元数据仍取目录,见 <see cref="ProviderService"/>。</item>
/// <item>省流量:<c>If-None-Match</c> / <c>If-Modified-Since</c>,304 只更新「上次检查时间」。</item>
/// <item>防护:30 秒超时、32MB 上限、不跟随重定向(3xx 当失败);新数据模型数比上一份少一半以上视为异常,丢弃并保留上一份。</item>
/// <item>失败:保留上一份,15 分钟起指数退避,最长 6 小时;最近一次错误在 <see cref="Status"/> 里。</item>
/// <item>价格自动采用,只用于费用估算;已落库的用量费用不回算(冻结规则在 Llm 层)。</item>
/// </list>
/// HTTP、时钟、定时器(<see cref="IDelayer"/>)全部注入,没有任何真实网络或睡眠也能完整测试。
/// </summary>
public sealed class ModelCatalogService : IDisposable
{
    private readonly IUpstreamHttp _http;
    private readonly ICatalogStore _store;
    private readonly ICatalogOverrideStore _overrideStore;
    private readonly TimeProvider _clock;
    private readonly IDelayer _delayer;
    private readonly ModelCatalogOptions _options;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _gate = new();

    private IReadOnlyList<CatalogProvider> _rawProviders = [];
    private IReadOnlyList<ModelOverride> _overrides = [];
    private CatalogCacheMeta? _meta;
    private CatalogSource _source;
    private DateTimeOffset? _fetchedAt;
    private CatalogError? _lastError;
    private DateTimeOffset? _lastChecked;
    private DateTimeOffset? _nextCheck;
    private long _version;
    private CatalogSnapshot _current = CatalogSnapshot.Empty;
    private CancellationTokenSource? _loop;
    private Task? _loopTask;

    public ModelCatalogService(
        IUpstreamHttp http,
        ICatalogStore store,
        ICatalogOverrideStore overrides,
        TimeProvider? clock = null,
        IDelayer? delayer = null,
        ModelCatalogOptions? options = null)
    {
        _http = http;
        _store = store;
        _overrideStore = overrides;
        _clock = clock ?? TimeProvider.System;
        _delayer = delayer ?? new TimeProviderDelayer(_clock);
        _options = options ?? new ModelCatalogOptions();
        _overrides = overrides.Load();
        PriceBook = new CatalogPriceBook(this);
        LoadInitial();
    }

    /// <summary>目录有可见变化时触发(全量快照,带版本号)。处理器抛出的异常被吞掉:订阅者不能拖垮同步。</summary>
    public event Action<CatalogSnapshot>? Changed;

    /// <summary>
    /// 每次检查(成功、304、失败、被拒)结束后触发,参数是检查后的状态:定时器自己发起的检查控制端看不见,靠这个把「上次检查时间 / 最近一次错误」推出去。
    /// 目录有可见变化时 <see cref="Changed"/> 先触发。处理器抛出的异常被吞掉。
    /// </summary>
    public event Action<CatalogStatus>? StatusChanged;

    public CatalogSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Llm 层的价目表(自动采用 models.dev 的价格 + 用户价格覆盖)。</summary>
    public IPriceBook PriceBook { get; }

    public IReadOnlyList<ModelOverride> Overrides
    {
        get
        {
            lock (_gate)
            {
                return _overrides;
            }
        }
    }

    public CatalogStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new CatalogStatus(
                    _current.Version, _source, _fetchedAt, _lastChecked, _nextCheck,
                    _current.ModelCount, _current.Providers.Count, _meta?.ETag, _meta?.Sha256, _lastError);
            }
        }
    }

    // ── 查询 ──

    public IReadOnlyList<CatalogModel> Query(CatalogQuery query) => Current.Query(query);

    public CatalogModel? Get(string providerId, string modelId) => Current.Get(providerId, modelId);

    /// <summary>(用户配置的供应商, 模型 id)的有效元数据;见 <see cref="CatalogOverrides.Describe"/>。</summary>
    public ModelInfo? Describe(string? providerId, string modelId) => CatalogOverrides.Describe(Current, Overrides, providerId, modelId);

    // ── 用户覆盖 ──

    /// <summary>写入(或替换)一条覆盖。内容为空等于删掉这条覆盖。</summary>
    public CatalogSnapshot SetOverride(ModelOverride item)
    {
        if (string.IsNullOrWhiteSpace(item.ModelId))
        {
            throw new ArgumentException("A model id is required.", nameof(item));
        }

        return MutateOverrides(list =>
        {
            list.RemoveAll(o => o.Key == item.Key);
            if (!item.IsEmpty)
            {
                list.Add(item with { ModelId = item.ModelId.Trim() });
            }
        });
    }

    /// <summary>撤销覆盖(恢复同步数据)。</summary>
    public CatalogSnapshot RemoveOverride(string? providerId, string modelId) =>
        MutateOverrides(list => list.RemoveAll(o => o.ProviderId == providerId && string.Equals(o.ModelId, modelId, StringComparison.OrdinalIgnoreCase)));

    /// <summary>从目录里隐藏一个模型(保留它已有的其他覆盖,恢复时一起回来)。</summary>
    public CatalogSnapshot DeleteModel(string? providerId, string modelId) => MutateOverrides(list =>
    {
        var existing = list.FirstOrDefault(o => o.ProviderId == providerId && string.Equals(o.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
        list.RemoveAll(o => ReferenceEquals(o, existing));
        list.Add((existing ?? new ModelOverride(providerId, modelId.Trim())) with { Deleted = true });
    });

    public CatalogSnapshot RestoreModel(string? providerId, string modelId) => MutateOverrides(list =>
    {
        var existing = list.FirstOrDefault(o => o.ProviderId == providerId && string.Equals(o.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return;
        }

        list.Remove(existing);
        var restored = existing with { Deleted = false };
        if (!restored.IsEmpty)
        {
            list.Add(restored);
        }
    });

    private CatalogSnapshot MutateOverrides(Action<List<ModelOverride>> mutate)
    {
        CatalogSnapshot snapshot;
        lock (_gate)
        {
            var next = _overrides.ToList();
            mutate(next);
            _overrideStore.Save(next);
            _overrides = next;
            snapshot = Rebuild();
        }

        Notify(snapshot);
        return snapshot;
    }

    // ── 启动加载 ──

    private void LoadInitial()
    {
        lock (_gate)
        {
            var cached = _store.Load();
            if (cached is { } c && TryParse(c.Body, c.Meta.FetchedAt) is { ModelCount: > 0 } parsed)
            {
                _rawProviders = parsed.Providers;
                _meta = c.Meta;
                _source = CatalogSource.Cache;
                _fetchedAt = c.Meta.FetchedAt;
                _lastChecked = c.Meta.CheckedAt;
            }
            else
            {
                // 没有缓存(断网的首次启动)或缓存坏了:用内置种子,不留下任何元数据,下一次拉取不带条件头。
                _rawProviders = ModelsDevParser.Parse(SeedCatalog.Bytes(), "seed", _options.ModelsDev).Providers;
                _meta = null;
                _source = CatalogSource.Seed;
                _fetchedAt = null;
            }

            _version = _clock.GetUtcNow().ToUnixTimeMilliseconds();
            Rebuild(bump: false);
        }
    }

    private ParsedCatalog? TryParse(byte[] body, DateTimeOffset fetchedAt)
    {
        try
        {
            return ModelsDevParser.Parse(body, fetchedAt.ToString("yyyy-MM-dd"), _options.ModelsDev);
        }
        catch (CatalogParseException)
        {
            return null;
        }
    }

    /// <summary>在持有锁时调用:按当前原始数据和覆盖重建对外快照。</summary>
    private CatalogSnapshot Rebuild(bool bump = true)
    {
        if (bump)
        {
            _version = Math.Max(_version + 1, _clock.GetUtcNow().ToUnixTimeMilliseconds());
        }

        _current = new CatalogSnapshot(_version, _source, _fetchedAt, CatalogOverrides.Apply(_rawProviders, _overrides));
        return _current;
    }

    private void Notify(CatalogSnapshot snapshot)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<CatalogSnapshot>>())
        {
            try
            {
                handler(snapshot);
            }
            catch (Exception)
            {
                // 一个订阅者出错不能挡住其余订阅者,更不能让同步失败。
            }
        }
    }

    // ── 拉取 ──

    /// <summary>
    /// 立即检查一次(设置页的「立即更新」,也是定时器调用的那个)。永不抛(调用方取消除外):所有失败变成 <see cref="CatalogRefreshResult"/> 并记进 <see cref="Status"/>。
    /// </summary>
    public async Task<CatalogRefreshResult> RefreshAsync(CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        CatalogRefreshResult result;
        try
        {
            result = await RefreshCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }

        try
        {
            StatusChanged?.Invoke(Status);
        }
        catch (Exception)
        {
            // 订阅者不能拖垮同步。
        }

        return result;
    }

    private async Task<CatalogRefreshResult> RefreshCoreAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        CatalogCacheMeta? meta;
        lock (_gate)
        {
            meta = _meta;
        }

        using var timeout = new CancellationTokenSource(_options.RequestTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.Url);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
            request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
            if (meta?.ETag is { } etag)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }

            if (meta?.LastModified is { } modified)
            {
                request.Headers.TryAddWithoutValidation("If-Modified-Since", modified);
            }

            using var response = await _http.SendAsync(request, linked.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status == 304 && meta is not null)
            {
                return NotModified(meta, now);
            }

            if (status is >= 300 and < 400)
            {
                return Fail(CatalogErrorKeys.Redirect, $"models.dev answered with a redirect (HTTP {status}); redirects are not followed.", now);
            }

            if (status != 200)
            {
                return Fail(CatalogErrorKeys.HttpStatus, $"models.dev returned HTTP {status}.", now);
            }

            if (response.Content.Headers.ContentLength is { } declared && declared > _options.MaxBytes)
            {
                return Fail(CatalogErrorKeys.TooLarge, $"The response is larger than the {_options.MaxBytes} byte limit.", now);
            }

            var body = await ReadCappedAsync(response.Content, _options.MaxBytes, linked.Token).ConfigureAwait(false);
            if (body is null)
            {
                return Fail(CatalogErrorKeys.TooLarge, $"The response is larger than the {_options.MaxBytes} byte limit.", now);
            }

            return Adopt(body, response, now);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Fail(CatalogErrorKeys.Timeout, $"models.dev did not answer within {_options.RequestTimeout.TotalSeconds:0} seconds.", now);
        }
        catch (HttpRequestException e)
        {
            return Fail(CatalogErrorKeys.Network, e.Message, now);
        }
    }

    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, long max, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        // 声明了长度就按它一次分配好(几 MB 的正文边读边翻倍,会在大对象堆上留下一串用过即弃的缓冲);长度对得上时直接交出底层数组,不再拷一份
        using var buffer = new MemoryStream(content.Headers.ContentLength is { } declared and > 0 && declared <= max ? (int)declared : 0);
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.Length == buffer.Capacity ? buffer.GetBuffer() : buffer.ToArray();
            }

            if (buffer.Length + read > max)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private CatalogRefreshResult NotModified(CatalogCacheMeta meta, DateTimeOffset now)
    {
        var updated = meta with { CheckedAt = now };
        _store.SaveMeta(updated);
        lock (_gate)
        {
            _meta = updated;
            _lastChecked = now;
            _lastError = null;
        }

        return new CatalogRefreshResult(CatalogRefreshOutcome.NotModified, ModelCount: Current.ModelCount);
    }

    private CatalogRefreshResult Adopt(byte[] body, HttpResponseMessage response, DateTimeOffset now)
    {
        ParsedCatalog parsed;
        try
        {
            parsed = ModelsDevParser.Parse(body, now.ToString("yyyy-MM-dd"), _options.ModelsDev);
        }
        catch (CatalogParseException e)
        {
            return Reject(CatalogErrorKeys.InvalidJson, e.Message, now);
        }

        var hash = FileCatalogStore.Hash(body);
        CatalogSnapshot? snapshot = null;
        CatalogRefreshResult result;
        lock (_gate)
        {
            var previous = _rawProviders.Sum(p => p.Models.Count);
            // 「少一半以上」:新数据不到上一份的一半(含恰好一半)。空数据无论如何都拒绝。
            if (parsed.ModelCount == 0 || (previous > 0 && parsed.ModelCount <= previous * _options.ShrinkRatio))
            {
                return RejectLocked(CatalogErrorKeys.Shrunk, $"The new catalog has {parsed.ModelCount} models but the current one has {previous}; keeping the current one.", now);
            }

            var meta = new CatalogCacheMeta(
                response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified is { } modified ? modified.ToString("R") : null,
                now,
                now,
                hash,
                parsed.ModelCount);
            _lastChecked = now;
            _lastError = null;
            if (_meta is { } known && string.Equals(known.Sha256, hash, StringComparison.Ordinal))
            {
                // 内容与已采用的这一份一字不差(上游不支持条件请求时的常态):等同 304,不发变更通知。
                _meta = known with { CheckedAt = now, ETag = meta.ETag ?? known.ETag, LastModified = meta.LastModified ?? known.LastModified };
                _store.SaveMeta(_meta);
                return new CatalogRefreshResult(CatalogRefreshOutcome.NotModified, ModelCount: parsed.ModelCount);
            }

            _store.Save(body, meta);
            _meta = meta;
            _rawProviders = parsed.Providers;
            _source = CatalogSource.Remote;
            _fetchedAt = now;
            snapshot = Rebuild();
            result = new CatalogRefreshResult(CatalogRefreshOutcome.Updated, ModelCount: parsed.ModelCount);
        }

        Notify(snapshot);
        return result;
    }

    private CatalogRefreshResult Fail(string key, string detail, DateTimeOffset now)
    {
        lock (_gate)
        {
            _lastChecked = now;
            _lastError = new CatalogError(key, detail, now);
        }

        return new CatalogRefreshResult(CatalogRefreshOutcome.Failed, _lastError);
    }

    private CatalogRefreshResult Reject(string key, string detail, DateTimeOffset now)
    {
        lock (_gate)
        {
            return RejectLocked(key, detail, now);
        }
    }

    private CatalogRefreshResult RejectLocked(string key, string detail, DateTimeOffset now)
    {
        _lastChecked = now;
        _lastError = new CatalogError(key, detail, now);
        return new CatalogRefreshResult(CatalogRefreshOutcome.Rejected, _lastError);
    }

    // ── 定时器 ──

    /// <summary>
    /// 启动定时同步:先等 <see cref="ModelCatalogOptions.InitialDelay"/>,之后成功则每 <see cref="ModelCatalogOptions.Interval"/> 一次;
    /// 失败(含被拒)按 15 分钟起指数退避、最长 6 小时,成功一次就恢复。重复调用无效。
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null)
            {
                return;
            }

            _loop = new CancellationTokenSource();
            // 先取令牌再起任务:任务体里读 _loop 会和紧接着的 StopAsync(把 _loop 置空)赛跑,启动后立刻停机(例如云端启动失败)时抛 NullReferenceException
            var token = _loop.Token;
            _loopTask = Task.Run(() => RunLoopAsync(token));
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var delay = _options.InitialDelay;
        var failures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                lock (_gate)
                {
                    _nextCheck = _clock.GetUtcNow() + delay;
                }

                await _delayer.DelayAsync(delay, ct).ConfigureAwait(false);
                var result = await RefreshAsync(ct).ConfigureAwait(false);
                if (result.Outcome is CatalogRefreshOutcome.Failed or CatalogRefreshOutcome.Rejected)
                {
                    failures++;
                    delay = Backoff(failures);
                }
                else
                {
                    failures = 0;
                    delay = _options.Interval();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 停止
        }
    }

    /// <summary>第 n 次连续失败后的等待:15 分钟 × 2^(n-1),封顶 6 小时。</summary>
    public TimeSpan Backoff(int failures)
    {
        var factor = Math.Pow(2, Math.Max(0, failures - 1));
        var ticks = Math.Min(_options.RetryMax.Ticks, _options.RetryBase.Ticks * factor);
        return TimeSpan.FromTicks((long)ticks);
    }

    public async Task StopAsync()
    {
        Task? task;
        lock (_gate)
        {
            _loop?.Cancel();
            task = _loopTask;
            _loop = null;
            _loopTask = null;
            _nextCheck = null;
        }

        if (task is not null)
        {
            await task.ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _loop?.Cancel();
            _loop = null;
        }

        _refreshGate.Dispose();
    }
}

/// <summary>内置种子(发版时的 models.dev 精简快照,嵌在程序集里,和线上数据是同一种格式)。</summary>
public static class SeedCatalog
{
    public static byte[] Bytes()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Ncw.Catalog/models-dev.seed.json")
            ?? throw new InvalidOperationException("The embedded catalog seed is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}

/// <summary>
/// <see cref="IPriceBook"/>:价格自动采用,优先级 ① 用户价格覆盖(指定供应商 → 所有供应商)② 目录里同供应商 id 的那一条 ③ 同模型 id 的一手厂商。
/// 查不到 / 非 USD 一手厂商且没有覆盖 → null,调用方记 Unknown,不编 0。<paramref name="at"/> 不参与:目录价格没有生效区间。
/// </summary>
internal sealed class CatalogPriceBook(ModelCatalogService service) : IPriceBook
{
    public ModelPricing? Find(string? providerId, string modelId, DateTimeOffset at) => service.Describe(providerId, modelId)?.Price;
}
