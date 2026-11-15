using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Catalog;

/// <summary>供应商配置的全量快照(<c>providers.changed</c> 事件的负载:首帧和后续广播同一个类型)。<c>Version</c> 单调递增。</summary>
public sealed record ProviderSnapshot(long Version, IReadOnlyList<ProviderRecord> Providers, IReadOnlyList<ModelAliasRecord> Aliases);

/// <summary>连通性测试结果。<c>Ok</c> = 上游认了这把凭据(模型列表端点 2xx);<c>Status</c> 为空 = 根本没连上。</summary>
public sealed record ProviderTestResult(bool Ok, int? LatencyMs, int? Status, string? ErrorKey, string? Detail);

public sealed class ProviderServiceOptions
{
    /// <summary>拉模型列表的超时。没有的话一个不回包的地址会让导入弹窗一直转下去;20 秒足够慢网络拉一页,又短到不至于让人以为卡死(2.x)。</summary>
    public TimeSpan FetchTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public string UserAgent { get; init; } = "NextCoWork/3.x";

    /// <summary>账号轮换开关(设置 › 模型)。每次现读,改完立刻对下一条请求生效。</summary>
    public Func<bool> RotationEnabled { get; init; } = () => true;
}

/// <summary>
/// 供应商服务(2.x <c>ipc/provider.ts</c>、<c>provider-accounts.ts</c>、<c>provider-auth.ts</c> 的 Agent 侧):
/// <list type="bullet">
/// <item>配置:<c>providers.json</c>(<see cref="ProviderFileStore"/>)里的供应商与别名;凭据 ref 一律由 id 派生(<c>provider:&lt;id&gt;</c>,不采信调用方给的值)。</item>
/// <item>凭据:读写走 NCK1(<see cref="ICredentialVault"/>);列表只回摘要,明文只经 <see cref="RevealCredentialAsync"/> 单次返回。</item>
/// <item>账号池:<c>provider_accounts</c> 表 + Llm 层的 <see cref="AccountPool"/>(「现在该用哪个」只由那段纯函数回答)。</item>
/// <item>模型:从上游拉列表(<see cref="FetchModelsAsync"/>)、整表替换别名、改名、逐条编辑;元数据跟随 <see cref="ModelCatalogService"/>。</item>
/// <item>OAuth:只对接 Llm 层的 <see cref="IOAuthLogin"/> 抽象(授权页由控制端打开,回调码经协议回到 <see cref="SubmitOAuthCode"/>),见 ProviderOAuth.cs。</item>
/// </list>
/// 不做的事:托管供应商 <c>nextcowork</c>(登录流程自己管,不进 providers.json);设置里的默认模型 / 子代理模型指向的修复由持有设置的一层做,
/// 这里只给纯函数 <see cref="ModelSelectionRepair"/>。
/// </summary>
public sealed partial class ProviderService
{
    private readonly ProviderFileStore _file;
    private readonly ICredentialVault _vault;
    private readonly ProviderAccountRepository _accounts;
    private readonly IUpstreamHttp _http;
    private readonly ModelCatalogService _catalog;
    private readonly TimeProvider _clock;
    private readonly IProviderPresets _presets;
    private readonly ITransportDecorator _transport;
    private readonly ProviderServiceOptions _options;
    private readonly object _gate = new();
    private long _version;

    public ProviderService(
        ProviderFileStore file,
        ICredentialVault vault,
        ProviderAccountRepository accounts,
        IUpstreamHttp http,
        ModelCatalogService catalog,
        TimeProvider? clock = null,
        IProviderPresets? presets = null,
        ITransportDecorator? transport = null,
        ProviderServiceOptions? options = null)
    {
        _file = file;
        _vault = vault;
        _accounts = accounts;
        _http = http;
        _catalog = catalog;
        _clock = clock ?? TimeProvider.System;
        _presets = presets ?? BuiltinProviderPresets.Instance;
        _transport = transport ?? NoTransportDecoration.Instance;
        _options = options ?? new ProviderServiceOptions();
        AccountPortImpl = new AccountPoolPort(accounts, _clock, _options.RotationEnabled);
        AccountPool = new AccountPool(AccountPortImpl);
        AccountPortImpl.Changed += providerId => AccountsChanged?.Invoke(providerId);
        _version = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        _file.Load();
        _file.ExternalChange += _ => Notify();
    }

    /// <summary>供应商或别名变化(自己的写入,或 providers.json 被外部修改并热加载)。</summary>
    public event Action<ProviderSnapshot>? Changed;

    /// <summary>账号的限流 / 额度 / 登录态变化,参数是 providerId。</summary>
    public event Action<string>? AccountsChanged;

    /// <summary>交给 <c>LlmRouter</c> 的账号池。</summary>
    public AccountPool AccountPool { get; }

    internal AccountPoolPort AccountPortImpl { get; }

    public ModelCatalogService Catalog => _catalog;

    /// <summary>当前快照版本(不拷贝列表,给缓存做廉价的失效判断)。</summary>
    public long Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    public ProviderSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new ProviderSnapshot(_version, _file.ListProviders(), _file.ListAliases());
            }
        }
    }

    public IReadOnlyList<ProviderRecord> Providers => _file.ListProviders();

    public IReadOnlyList<ModelAliasRecord> Aliases(string? providerId = null) =>
        _file.ListAliases().Where(a => providerId is null || a.ProviderId == providerId).ToList();

    public ProviderRecord? Provider(string providerId) => _file.ListProviders().FirstOrDefault(p => p.Id == providerId);

    /// <summary>providers.json 读取时遇到的问题(被跳过的坏条目),设置页的诊断。</summary>
    public IReadOnlyList<ProviderFileProblem> FileProblems => _file.Problems;

    private long NowMs => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    private ProviderRecord Require(string providerId) =>
        Provider(providerId) ?? throw new ProviderException(
            ProviderErrorKeys.UnknownProvider, $"Unknown provider: {providerId}", new Dictionary<string, string> { ["provider"] = providerId });

    private void Notify()
    {
        ProviderSnapshot snapshot;
        lock (_gate)
        {
            _version = Math.Max(_version + 1, NowMs);
            snapshot = new ProviderSnapshot(_version, _file.ListProviders(), _file.ListAliases());
        }

        foreach (var handler in Changed?.GetInvocationList().Cast<Action<ProviderSnapshot>>() ?? [])
        {
            try
            {
                handler(snapshot);
            }
            catch (Exception)
            {
                // 订阅者出错不能让一次保存失败。
            }
        }
    }

    // ── 供应商 ──

    /// <summary>
    /// 新建或更新一个供应商。<c>credentialRef</c> 一律不采信调用方的值(由 id 派生);更新时保留已有的 <c>protocolOptions</c> / <c>videoGeneration</c>
    /// (视频生成属于后续版本,3.0 不接受调用方写它,只原样保留文件里已有的)。
    /// 地址只放行 http / https:baseUrl 会被拼进请求,<c>file:</c> / <c>data:</c> 必须挡在入口。
    /// </summary>
    public ProviderRecord Upsert(ProviderRecord input)
    {
        var id = input.Id.Trim();
        if (id.Length == 0 || !ProviderRefs.IsValidProviderId(id))
        {
            throw new ProviderException(ProviderErrorKeys.InvalidId, "The provider id must be non-empty and must not contain whitespace or \"/\".");
        }

        if (id == ProviderRefs.ManagedProviderId)
        {
            throw new ProviderException(ProviderErrorKeys.ManagedProvider, "The managed provider is controlled by the account login and cannot be edited here.");
        }

        var name = input.Name.Trim();
        if (name.Length == 0)
        {
            throw new ProviderException(ProviderErrorKeys.InvalidName, "The provider name must not be empty.");
        }

        var existing = Provider(id);
        ProviderRecord saved;
        lock (_gate)
        {
            saved = _file.PutProvider(new ProviderRecord(id, name, input.Protocol, NormalizeBaseUrl(input.BaseUrl), input.Priority, input.Enabled)
            {
                ProtocolOptions = MergeProtocolOptions(existing?.ProtocolOptions, input.ProtocolOptions),
                VideoGeneration = existing?.VideoGeneration,
            });
        }

        Notify();
        return saved;
    }

    /// <summary>
    /// 删掉一个供应商,连同它的别名、账号和<b>密钥</b>(留着的话下次有人建一个同 id 的供应商,会静默继承上一个的 key)。
    /// 走 <see cref="ICredentialVault.Remove"/> 而不是写空串:后者要读主密钥,主密钥损坏时删除会失败。
    /// </summary>
    public void Remove(string providerId)
    {
        if (providerId == ProviderRefs.ManagedProviderId)
        {
            throw new ProviderException(ProviderErrorKeys.ManagedProvider, "The built-in provider cannot be removed.");
        }

        if (Provider(providerId) is not { } target)
        {
            return;
        }

        lock (_gate)
        {
            _accounts.RemoveFor(providerId);
            _file.RemoveProvider(providerId);
            _vault.Remove(target.CredentialRef);
        }

        Notify();
    }

    private static string NormalizeBaseUrl(string raw)
    {
        var trimmed = raw.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ProviderException(ProviderErrorKeys.InvalidBaseUrl, $"The API address must be an absolute http or https URL: {trimmed}");
        }

        return trimmed;
    }

    /// <summary>
    /// 递归合并 JSON 形状的选项对象:协议选项自己是对象,将来的协议会加别的嵌套对象,浅合并会让一次局部更新抹掉旁边保存的字段。
    /// 数组和标量是替换值;缺省(null)的字段不能抹掉已保存的配置。Anthropic 缓存时长 <c>off</c> / 未知值落到强制的 5 分钟,非法值被拒。
    /// </summary>
    internal static JsonObject? MergeProtocolOptions(JsonObject? existing, JsonObject? incoming)
    {
        if (incoming is null)
        {
            return existing;
        }

        var merged = MergeObjects(existing ?? new JsonObject(), incoming);
        if (incoming["anthropic"] is { } rawAnthropic)
        {
            if (rawAnthropic is not JsonObject anthropicInput)
            {
                throw new ProviderException(ProviderErrorKeys.InvalidOptions, "protocolOptions.anthropic must be an object.");
            }

            if (anthropicInput["cacheTtl"] is JsonNode ttlNode)
            {
                var ttl = ttlNode is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                if (ttl is not ("off" or "5m" or "1h"))
                {
                    throw new ProviderException(ProviderErrorKeys.InvalidOptions, "The Anthropic cache TTL must be off, 5m or 1h.");
                }
            }

            var anthropic = merged["anthropic"] as JsonObject ?? new JsonObject();
            var current = anthropic["cacheTtl"] is JsonValue c && c.TryGetValue<string>(out var cs) ? cs : null;
            anthropic["cacheTtl"] = current == "1h" ? "1h" : "5m";
            merged["anthropic"] = anthropic;
        }

        return merged.Count == 0 ? null : merged;
    }

    private static JsonObject MergeObjects(JsonObject existing, JsonObject incoming)
    {
        var result = new JsonObject();
        foreach (var (key, value) in existing)
        {
            result[key] = value?.DeepClone();
        }

        foreach (var (key, value) in incoming)
        {
            if (value is null)
            {
                continue;
            }

            result[key] = result[key] is JsonObject old && value is JsonObject next ? MergeObjects(old, next) : value.DeepClone();
        }

        return result;
    }

    // ── 凭据 ──

    /// <summary>存一把 API Key(去空白;不能为空)。托管供应商的登录凭据不能手动改。</summary>
    public async Task<CredentialInfo> SetApiKeyAsync(string providerId, string apiKey, CancellationToken ct = default)
    {
        var provider = Require(providerId);
        if (providerId == ProviderRefs.ManagedProviderId)
        {
            throw new ProviderException(ProviderErrorKeys.ManagedProvider, "The built-in provider's login credential cannot be edited manually.");
        }

        var key = apiKey.Trim();
        if (key.Length == 0)
        {
            throw new ProviderException(ProviderErrorKeys.EmptyKey, "The key must not be empty.");
        }

        await _vault.SetAsync(provider.CredentialRef, key, ct).ConfigureAwait(false);
        return CredentialInfos.Of(key, NowMs);
    }

    public async Task<CredentialInfo> GetCredentialInfoAsync(string providerId, CancellationToken ct = default)
    {
        var provider = Require(providerId);
        return CredentialInfos.Of(await _vault.GetAsync(provider.CredentialRef, ct).ConfigureAwait(false), NowMs);
    }

    /// <summary>显式查看凭据。和 <see cref="GetCredentialInfoAsync"/> 分开是安全边界:列表刷新永远只拿摘要,明文只在用户点了查看的那一次请求中出现。</summary>
    public async Task<RevealedCredential> RevealCredentialAsync(string providerId, CancellationToken ct = default)
    {
        var provider = Require(providerId);
        return CredentialInfos.Reveal(await _vault.GetAsync(provider.CredentialRef, ct).ConfigureAwait(false))
            ?? throw new ProviderException(ProviderErrorKeys.NoCredential, "No key has been configured.");
    }

    /// <summary>清掉这家的凭据(不动账号)。</summary>
    public async Task RemoveCredentialAsync(string providerId, CancellationToken ct = default)
    {
        var provider = Require(providerId);
        if (providerId == ProviderRefs.ManagedProviderId)
        {
            throw new ProviderException(ProviderErrorKeys.ManagedProvider, "The built-in provider's login credential cannot be edited manually.");
        }

        await _vault.RemoveAsync(provider.CredentialRef, ct).ConfigureAwait(false);
    }

    // ── 模型列表与连通性 ──

    private (ModelList.ListRequest Request, ProviderConfig Config) ListRequestFor(ProviderRecord provider, ProviderCredential? credential)
    {
        var config = new ProviderConfig(provider.Id, provider.Name, provider.BaseUrl, provider.Protocol, provider.Priority, provider.Enabled, provider.CredentialRef, provider.CacheTtl);
        return (ModelList.RequestFor(config, provider.Protocol, credential, _transport, new RequestContext("model-list")), config);
    }

    private async Task<ProviderCredential?> ResolveCredentialAsync(ProviderRecord provider, CancellationToken ct)
    {
        // 有账号行时用当前账号的凭据(账号池是新的真源,旧槽只是镜像);否则用旧槽。
        var reference = _accounts.CurrentOf(provider.Id) is { } current ? ProviderRefs.ForAccount(provider.Id, current.Id) : provider.CredentialRef;
        return CredentialFormat.Parse(await _vault.GetAsync(reference, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// 从上游拉「这家有哪些模型」。不照预设表拒绝(用户可能已经把地址改成自己的中转):试了再说。
    /// 没有密钥时不是直接失败——免鉴权的那几家照样能拉。失败只给结构化的 <see cref="ProviderException"/>(原因里的 URL 与响应片段已脱敏)。
    /// </summary>
    public async Task<IReadOnlyList<FetchedModel>> FetchModelsAsync(string providerId, CancellationToken ct = default)
    {
        var provider = Require(providerId);
        var (request, _) = ListRequestFor(provider, await ResolveCredentialAsync(provider, ct).ConfigureAwait(false));
        using var response = await SendListAsync(request, provider, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var failure = ModelList.Classify((int)response.StatusCode, await ReadBodyAsync(response, ct).ConfigureAwait(false));
            throw FetchError(failure.Key, provider, request.Url, failure.Status, failure.Detail);
        }

        JsonElement payload;
        try
        {
            payload = JsonSerializer.Deserialize(await ReadBodyAsync(response, ct).ConfigureAwait(false), ModelsDevElementContext.Default.JsonElement);
        }
        catch (JsonException)
        {
            payload = default;
        }

        var models = ModelList.Parse(provider.Protocol, payload);
        if (models.Count == 0)
        {
            // 200 但一条都解不出来:报清楚这是「解析不出」而不是「这家没有模型」。
            throw FetchError(ProviderErrorKeys.FetchEmpty, provider, request.Url, 200, null);
        }

        return models;
    }

    /// <summary>连通性测试:用模型列表端点探一次(不烧 token)。2xx = 上游认了这把凭据;401 / 403 / 404 / 超时 / 连不上分别归类。永不抛(调用方取消除外)。</summary>
    public async Task<ProviderTestResult> TestAsync(string providerId, CancellationToken ct = default)
    {
        var provider = Require(providerId);
        var (request, _) = ListRequestFor(provider, await ResolveCredentialAsync(provider, ct).ConfigureAwait(false));
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var response = await SendListAsync(request, provider, ct).ConfigureAwait(false);
            var latency = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (response.IsSuccessStatusCode)
            {
                return new ProviderTestResult(true, latency, (int)response.StatusCode, null, null);
            }

            var failure = ModelList.Classify((int)response.StatusCode, await ReadBodyAsync(response, ct).ConfigureAwait(false));
            return new ProviderTestResult(false, latency, failure.Status, failure.Key, failure.Detail);
        }
        catch (ProviderException e)
        {
            return new ProviderTestResult(false, null, null, e.Key, e.Message);
        }
    }

    private async Task<HttpResponseMessage> SendListAsync(ModelList.ListRequest listRequest, ProviderRecord provider, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, listRequest.Url);
        foreach (var (name, value) in listRequest.Headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        request.Headers.TryAddWithoutValidation("user-agent", _options.UserAgent);
        using var timeout = new CancellationTokenSource(_options.FetchTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return await _http.SendAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw FetchError(ProviderErrorKeys.FetchTimeout, provider, listRequest.Url, null, $"No answer within {_options.FetchTimeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException e)
        {
            // redactSecrets 是兜底:2.x 的事故里一行 String(e) 把整段私钥送进了弹窗。
            throw FetchError(ProviderErrorKeys.FetchFailed, provider, listRequest.Url, null, ModelList.RedactSecrets(e.Message));
        }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return "";
        }
    }

    private static ProviderException FetchError(string key, ProviderRecord provider, string url, int? status, string? detail)
    {
        var parameters = new Dictionary<string, string> { ["provider"] = provider.Id, ["url"] = url };
        if (status is { } s)
        {
            parameters["status"] = s.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (detail is not null)
        {
            parameters["detail"] = detail;
        }

        return new ProviderException(key, $"Could not fetch the model list from {url}{(status is null ? "" : $" (HTTP {status})")}{(detail is null ? "" : $": {detail}")}", parameters);
    }

    // ── 别名 ──

    /// <summary>
    /// 整表替换这家的模型别名(导入弹窗点「更新列表」)。调用完之后这家的别名 = <paramref name="upstreamModels"/> 那些,一个不多一个不少。
    /// 已有的那条整行留着(用户可能改过别名、调过窗口),只按 upstreamModel 认;新建的别名 <c>catalogOverrides = []</c> = 全部字段跟随目录,
    /// 所以 models.dev 更新后窗口和能力自动跟上。别名默认等于上游模型名,不加任何前后缀:别名是用户在选择器里看见的字符串,加工过就对不上上游文档里的名字。
    /// </summary>
    public IReadOnlyList<ModelAliasRecord> SetAliases(string providerId, IReadOnlyList<string> upstreamModels)
    {
        var provider = Require(providerId);
        var wanted = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in upstreamModels)
        {
            var model = raw.Trim();
            if (model.Length > 0 && seen.Add(model))
            {
                wanted.Add(model);
            }
        }

        lock (_gate)
        {
            var before = _file.ListAliases().Where(a => a.ProviderId == providerId).ToList();
            var keep = new Dictionary<string, ModelAliasRecord>(StringComparer.Ordinal);
            foreach (var alias in before)
            {
                keep.TryAdd(alias.UpstreamModel, alias);
            }

            foreach (var alias in before)
            {
                if (!seen.Contains(alias.UpstreamModel) || !ReferenceEquals(keep[alias.UpstreamModel], alias))
                {
                    _file.RemoveAlias(providerId, alias.Alias);
                }
            }

            for (var priority = 0; priority < wanted.Count; priority++)
            {
                var model = wanted[priority];
                if (keep.TryGetValue(model, out var existing))
                {
                    _file.PutAlias(existing with { Priority = priority });
                    continue;
                }

                var info = _catalog.Describe(provider.Id, model);
                _file.PutAlias(new ModelAliasRecord(model, providerId, model)
                {
                    Priority = priority,
                    // 目录认识的生图 / 生视频模型带上模态:对话选择器据此把它们排除在聊天候选之外(选中它发消息是一次注定失败的对话)。
                    Modality = info?.Capabilities.OutputModalities is { } outputs
                        ? outputs.Contains("image") ? "image" : outputs.Contains("video") ? "video" : null
                        : null,
                    Extra = new JsonObject { ["catalogOverrides"] = new JsonArray() },
                });
            }
        }

        Notify();
        return Aliases(providerId);
    }

    public void RemoveAlias(string providerId, string alias)
    {
        Require(providerId);
        lock (_gate)
        {
            _file.RemoveAlias(providerId, alias);
        }

        Notify();
    }

    /// <summary>
    /// 改名:保持供应商绑定和全部配置不变。别名已存在或为空抛错。返回改名后的行。
    /// 设置里指向旧别名的默认模型 / 子代理模型等由持有设置的一层用 <see cref="ModelSelectionRepair"/> 修(锁定的正是这一家就无条件跟着改)。
    /// </summary>
    public ModelAliasRecord RenameAlias(string providerId, string alias, string nextAlias)
    {
        Require(providerId);
        var existing = Aliases(providerId).FirstOrDefault(a => a.Alias == alias)
            ?? throw new ProviderException(ProviderErrorKeys.UnknownAlias, $"Unknown model alias: {alias}");
        var next = nextAlias.Trim();
        if (next.Length == 0)
        {
            throw new ProviderException(ProviderErrorKeys.UnknownAlias, "The model alias must not be empty.");
        }

        if (next == alias)
        {
            return existing;
        }

        if (Aliases(providerId).Any(a => a.Alias == next))
        {
            throw new ProviderException(ProviderErrorKeys.AliasExists, $"The model alias already exists: {next}");
        }

        ModelAliasRecord renamed;
        lock (_gate)
        {
            _file.RemoveAlias(providerId, alias);
            renamed = _file.PutAlias(existing with { Alias = next });
        }

        Notify();
        return renamed;
    }

    /// <summary>逐条编辑别名(2.x <c>model:update</c>):只改给了值的字段,并把它们记进 <c>catalogOverrides</c>(归供应商,不再跟随目录)。</summary>
    public ModelAliasRecord UpdateAlias(AliasUpdate update)
    {
        Require(update.ProviderId);
        var existing = Aliases(update.ProviderId).FirstOrDefault(a => a.Alias == update.Alias)
            ?? throw new ProviderException(ProviderErrorKeys.UnknownAlias, $"Unknown model alias: {update.Alias}");
        var overrides = ModelBindingResolver.OverridesOf(existing) ?? ModelBindingResolver.InferOverrides(existing);
        var extra = (JsonObject)existing.Extra.DeepClone();
        var next = existing;
        if (update.DisplayName is { } displayName)
        {
            next = next with { DisplayName = displayName.Trim().Length == 0 ? null : displayName.Trim() };
            overrides.Add("displayName");
        }

        if (update.ContextWindow is { } context)
        {
            next = next with { ContextWindow = context };
            overrides.Add("contextWindow");
        }

        if (update.MaxOutputTokens is { } maxOutput)
        {
            next = next with { MaxOutputTokens = maxOutput };
            overrides.Add("maxOutputTokens");
        }

        if (update.Enabled is { } enabled)
        {
            next = next with { Enabled = enabled };
        }

        if (update.Priority is { } priority)
        {
            next = next with { Priority = priority };
        }

        if (update.ClearProtocolOverride)
        {
            next = next with { ProtocolOverride = null };
        }
        else if (update.ProtocolOverride is { } protocol)
        {
            next = next with { ProtocolOverride = protocol };
        }

        if (update.Vision is { } vision)
        {
            var caps = extra["capabilities"] as JsonObject ?? new JsonObject();
            caps["vision"] = vision;
            caps["visionInput"] = vision;
            extra["capabilities"] = caps;
            overrides.Add("capabilities.vision");
        }

        if (update.ThinkingConfig is { } thinking)
        {
            extra["thinkingConfig"] = thinking.DeepClone();
            overrides.Add("thinkingConfig");
            overrides.Add("reasoningEfforts");
        }

        if (update.ReasoningEfforts is { } efforts)
        {
            extra["reasoningEfforts"] = efforts.DeepClone();
            overrides.Add("reasoningEfforts");
        }

        if (update.RequestAdapter is { } adapter)
        {
            extra["requestAdapter"] = adapter.DeepClone();
        }

        extra["catalogOverrides"] = new JsonArray(overrides.Order(StringComparer.Ordinal).Select(o => (JsonNode?)JsonValue.Create(o)).ToArray());
        next = next with { Extra = extra };
        lock (_gate)
        {
            _file.PutAlias(next);
        }

        Notify();
        return next;
    }

    /// <summary>
    /// 迁移用:库里的账号行升级前已登录的那一个 OAuth 账号 → 账号 #1。幂等靠「这家已经有账号行就跳过」(不靠版本号:用户是升级之后才第一次登录的,
    /// 登录路径自己会建账号行)。先写密文再写行(反过来崩在中间会留下指向空密文的账号行:界面显示已登录,发请求却报没配密钥)。
    /// 旧槽 <c>provider:&lt;id&gt;</c> 保留不删——它从此是当前账号的镜像,回退旧版本时那个版本只认旧槽。返回新建的行数。
    /// </summary>
    public async Task<int> EnsureAccountsSeededAsync(CancellationToken ct = default)
    {
        var created = 0;
        foreach (var provider in Providers)
        {
            if (_presets.OAuthIssuerOf(provider.Id) is null || _accounts.List(provider.Id).Count > 0)
            {
                continue;
            }

            var raw = await _vault.GetAsync(provider.CredentialRef, ct).ConfigureAwait(false);
            if (CredentialFormat.Parse(raw) is not OAuthCredential oauth)
            {
                // 没登录过、或槽里装的是 API Key(GLM 那两家两种凭据都能用)——都不迁。
                continue;
            }

            var id = new UlidIdGenerator(_clock).NewId();
            var now = NowMs;
            await _vault.SetAsync(ProviderRefs.ForAccount(provider.Id, id), raw!, ct).ConfigureAwait(false);
            _accounts.Put(new ProviderAccountRow(id, provider.Id, oauth.Issuer, null, 0, true, true, oauth.NeedsReauth, null, null, now, now));
            created++;
        }

        return created;
    }
}

/// <summary>别名的逐条编辑。只改给了值的字段;<c>ClearProtocolOverride</c> 显式清掉协议覆盖(区别于「没给」)。</summary>
public sealed record AliasUpdate(
    string ProviderId,
    string Alias,
    string? DisplayName = null,
    int? ContextWindow = null,
    int? MaxOutputTokens = null,
    bool? Enabled = null,
    int? Priority = null,
    WireProtocol? ProtocolOverride = null,
    bool ClearProtocolOverride = false,
    bool? Vision = null,
    JsonObject? ThinkingConfig = null,
    JsonArray? ReasoningEfforts = null,
    JsonObject? RequestAdapter = null);
