using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Ncw.Agent.Catalog;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Credentials;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;
using CatalogModelResolver = Ncw.Agent.Catalog.IModelResolver;
using HostModelResolver = Ncw.Agent.Host.Conversation.IModelResolver;

namespace Ncw.Agent.Host.Providers;

/// <summary>
/// 模型目录与供应商在 Host 里的装配(设备数据就绪后创建一份):
/// <list type="bullet">
/// <item><see cref="ModelCatalogService"/>:定时更新随 Host 启停(<see cref="Start"/> 在数据就绪后调,<see cref="DisposeAsync"/> 停);HTTP / 时钟 / 定时器都可注入,默认 HTTP 是不跟随重定向、自动解压的 <see cref="HttpClient"/>(AOT 友好)。</item>
/// <item><see cref="ProviderService"/>、账号池、<see cref="CredentialResolver"/>(<c>onChanged</c> 接 <see cref="ProviderService.OnCredentialChanged"/>)、<see cref="LlmRouter"/>(价目表 = 目录、用量落 <c>usage_records</c>)。</item>
/// <item>本类同时是对话链路的 <see cref="HostModelResolver"/>:别名 → 绑定 与路由用同一段规则(<see cref="ProviderConfigSource"/>),界面说 A、请求发给 B 且不报错的分家 bug 不会出现。</item>
/// <item>事件 → 主题:<c>catalog.changed</c>、<c>providers.changed</c>、<c>providers.accounts.changed</c>、<c>providers.oauth.progress</c>。全部是全量状态,凭据永远不进事件。</item>
/// </list>
/// </summary>
internal sealed class ProviderRuntime : HostModelResolver, IAsyncDisposable
{
    private const string SettingsKey = "v3.catalog.settings";

    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan MaxInterval = TimeSpan.FromDays(7);

    private readonly TopicHub _topics;
    private readonly ILoopDiagnostics _log;
    private readonly KvStore _kv;
    private readonly HttpClient? _ownedHttp;
    private readonly object _oauthGate = new();
    private OAuthProgressDto? _oauthCurrent;
    private CatalogSettingsFile? _settings;
    private int _disposed;

    public ProviderRuntime(
        AgentDatabase database, string dataRoot, TimeProvider clock, AgentServices services, TopicHub topics, ILoopDiagnostics log,
        Ncw.Agent.Services.Settings.SettingsService? deviceSettings = null, Ncw.Agent.Host.Services.SettingsProxy? proxy = null)
    {
        _topics = topics;
        _log = log;
        _kv = new KvStore(database);
        _settings = LoadSettings();

        IUpstreamHttp http;
        if (services.Http is { } injected)
        {
            http = injected;
        }
        else
        {
            _ownedHttp = DefaultHttp.Create(proxy);
            http = new HttpClientUpstream(_ownedHttp);
        }

        var options = services.Catalog ?? new ModelCatalogOptions();
        var effective = new ModelCatalogOptions
        {
            InitialDelay = options.InitialDelay,
            Interval = () => _settings is { } s ? ClampInterval(s.IntervalMinutes) : options.Interval(),
            RequestTimeout = options.RequestTimeout,
            MaxBytes = options.MaxBytes,
            RetryBase = options.RetryBase,
            RetryMax = options.RetryMax,
            ShrinkRatio = options.ShrinkRatio,
            UserAgent = options.UserAgent,
            Url = options.Url,
            ModelsDev = options.ModelsDev,
        };

        File = new ProviderFileStore(Path.Combine(ProfileRootOf(dataRoot), ProviderFileStore.FileName));
        var vault = new ProviderCredentialVault(new CredentialStore(database, dataRoot, NoLegacySecrets.Instance), File);
        var accounts = new ProviderAccountRepository(database, vault, clock);
        Catalog = new ModelCatalogService(http, services.CatalogStore ?? new FileCatalogStore(dataRoot), new KvOverrideStore(_kv), clock, services.CatalogDelayer, effective);
        // 设置页的几项每次现读:账号轮换(同一家的多个登录账号)、故障转移(这家挂了换另一家)、上游空闲超时。注入了自己的参数(测试)就以注入的为准。
        var providerOptions = services.ProviderOptions ?? (deviceSettings is null ? null : new ProviderServiceOptions { RotationEnabled = () => deviceSettings.Current().AccountRotation });
        var routerOptions = services.Router ?? (deviceSettings is null ? null : new LlmRouterOptions { IdleTimeout = LlmRouterOptions.IdleTimeoutFromSettings(() => deviceSettings.Current().UpstreamIdleTimeoutSeconds) });
        Providers = new ProviderService(File, vault, accounts, http, Catalog, clock, services.Presets, services.Transport, providerOptions)
        {
            OAuthLogins = services.OAuthLogins ?? (_ => null),
        };
        Config = new ProviderConfigSource(Providers, Catalog, deviceSettings is null ? null : () => deviceSettings.Current().Failover);
        Discovery = new Ncw.Agent.Catalog.Discovery.ProviderDiscovery(Providers, services.DiscoveryHost ?? Ncw.Agent.Catalog.Discovery.LocalDiscoveryHost.Instance);
        Presets = services.Presets ?? BuiltinProviderPresets.Instance;
        Router = new LlmRouter(
            Config, http, new CredentialResolver(vault, services.OAuth ?? new OAuthRegistry([]), http, clock, Providers.OnCredentialChanged), clock,
            services.Ids, services.Delayer, routerOptions, Catalog.PriceBook, new UsageRecordSink(new UsageRepository(database)), Providers.AccountPool,
            transport: services.Transport);

        Catalog.Changed += _ => PublishCatalog();
        Catalog.StatusChanged += _ => PublishCatalog();
        Providers.Changed += _ => PublishProviders();
        Providers.AccountsChanged += PublishAccounts;
        Providers.OAuthProgressChanged += OnOAuthProgress;
    }

    public ModelCatalogService Catalog { get; }

    public ProviderService Providers { get; }

    /// <summary>供应商自动发现与导入(在这台设备上读其他客户端的配置)。</summary>
    public Ncw.Agent.Catalog.Discovery.ProviderDiscovery Discovery { get; }

    public ProviderConfigSource Config { get; }

    public IProviderPresets Presets { get; }

    public LlmRouter Router { get; }

    public ProviderFileStore File { get; }

    // ───────────────────────── 对话链路的模型解析 ─────────────────────────

    public ILlmClient Client => Router;

    public ModelBinding? Resolve(ModelRef model) => Config.Resolve(new ModelSelection(model.Alias, model.ProviderId)).Binding;

    // ───────────────────────── 生命周期 ─────────────────────────

    /// <summary>数据就绪之后启动:监听 <c>providers.json</c>、迁移存量账号、目录定时更新(用户没关自动更新的话)。</summary>
    public void Start()
    {
        File.StartWatching();
        try
        {
            Providers.EnsureAccountsSeededAsync().GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is CryptographicException or IOException or Microsoft.Data.Sqlite.SqliteException or StorageException)
        {
            _log.Warn("Seeding provider accounts failed.", e);
        }

        if (CatalogSettings().AutoUpdate)
        {
            Catalog.Start();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        File.StopWatching();
        await Catalog.StopAsync().ConfigureAwait(false);
        Catalog.Dispose();
        _ownedHttp?.Dispose();
    }

    // ───────────────────────── 目录同步设置 ─────────────────────────

    public CatalogSettingsDto CatalogSettings() =>
        _settings is { } s ? new CatalogSettingsDto(s.AutoUpdate, (int)ClampInterval(s.IntervalMinutes).TotalMinutes) : new CatalogSettingsDto(true, 6 * 60);

    public async Task<CatalogSettingsDto> SetCatalogSettingsAsync(CatalogSettingsDto next)
    {
        var minutes = (int)Math.Clamp(next.IntervalMinutes, MinInterval.TotalMinutes, MaxInterval.TotalMinutes);
        var file = new CatalogSettingsFile(next.AutoUpdate, minutes);
        _kv.Set(SettingsKey, JsonSerializer.Serialize(file, HostStoresJsonContext.Default.CatalogSettingsFile));
        _settings = file;
        if (next.AutoUpdate)
        {
            Catalog.Start();
        }
        else
        {
            await Catalog.StopAsync().ConfigureAwait(false);
        }

        PublishCatalog();
        return CatalogSettings();
    }

    private CatalogSettingsFile? LoadSettings()
    {
        try
        {
            return _kv.Get(SettingsKey) is { } json ? JsonSerializer.Deserialize(json, HostStoresJsonContext.Default.CatalogSettingsFile) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TimeSpan ClampInterval(int minutes) => TimeSpan.FromMinutes(Math.Clamp(minutes, MinInterval.TotalMinutes, MaxInterval.TotalMinutes));

    /// <summary>providers.json 在 profile 根下(2.x 是 <c>~/.next-cowork/providers.json</c>,数据根是它下面的 <c>data/</c>);数据根不叫 <c>data</c> 时(开发、测试、云端)就放在数据根里。</summary>
    internal static string ProfileRootOf(string dataRoot)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        return Path.GetFileName(full) == Ncw.Agent.Storage.Takeover.LegacyLayout.DataSubdirectory && Path.GetDirectoryName(full) is { } parent ? parent : full;
    }

    // ───────────────────────── 主题的状态生成(首帧与事件同一个函数) ─────────────────────────

    public CatalogTopicState CatalogState() =>
        new(ProviderMapping.ToDto(Catalog.Status), [.. Catalog.Overrides.Select(ProviderMapping.ToDto)]);

    public ProvidersTopicState ProvidersState() => ProvidersState(Providers.Snapshot);

    private ProvidersTopicState ProvidersState(ProviderSnapshot snapshot)
    {
        var accounts = new List<ProviderAccountsDto>();
        foreach (var provider in snapshot.Providers)
        {
            if (Presets.OAuthIssuerOf(provider.Id) is not null)
            {
                accounts.Add(AccountsOf(provider.Id));
            }
        }

        return new ProvidersTopicState(
            snapshot.Version,
            [.. snapshot.Providers.Select(ProviderOf)],
            [.. snapshot.Aliases.Select(ProviderMapping.ToDto)],
            [.. Providers.FileProblems.Select(ProviderMapping.ToDto)],
            [.. accounts]);
    }

    public ProviderDto ProviderOf(ProviderRecord provider) => ProviderMapping.ToDto(provider, HasCredential(provider.Id), Presets.OAuthIssuerOf(provider.Id));

    private bool HasCredential(string providerId)
    {
        try
        {
            return Providers.GetCredentialInfoAsync(providerId).GetAwaiter().GetResult().HasKey;
        }
        catch (Exception e) when (e is CryptographicException or ProviderException or IOException)
        {
            return false;
        }
    }

    private ProviderAccountsDto AccountsOf(string providerId)
    {
        try
        {
            return new ProviderAccountsDto(providerId, [.. Providers.ListAccountsAsync(providerId).GetAwaiter().GetResult().Select(ProviderMapping.ToDto)]);
        }
        catch (Exception e) when (e is CryptographicException or ProviderException or IOException)
        {
            return new ProviderAccountsDto(providerId, []);
        }
    }

    // ───────────────────────── 往主题发布 ─────────────────────────

    public void PublishCatalog() =>
        Safely(() => _topics.Publish(CatalogMethods.Topic.Name, CatalogEvents.Changed, CatalogState(), PayloadShapes.GetShape<CatalogTopicState>()));

    /// <summary>供应商、别名、有没有凭据变了(凭据写入 / 删除 / 登录不改变快照版本,同一个版本再发一份,控制端按「版本不倒退」收)。</summary>
    public void PublishProviders() =>
        Safely(() => _topics.Publish(ProvidersMethods.Topic.Name, ProvidersEvents.Changed, ProvidersState(), PayloadShapes.GetShape<ProvidersTopicState>()));

    public void PublishAccounts(string providerId) =>
        Safely(() => _topics.Publish(ProvidersMethods.Topic.Name, ProvidersEvents.AccountsChanged, AccountsOf(providerId), PayloadShapes.GetShape<ProviderAccountsDto>()));

    public OAuthTopicState OAuthState()
    {
        lock (_oauthGate)
        {
            return new OAuthTopicState(_oauthCurrent);
        }
    }

    /// <summary>登录成功:Catalog 层只在失败 / 取消时推终态进度(成功由调用本身的返回给),这里补一条 succeeded,控制端据它收尾并清掉「还在等」。</summary>
    public void OAuthSucceeded(string providerId) => OnOAuthProgress(new OAuthProgress(providerId, OAuthPhase.Succeeded));

    private void OnOAuthProgress(OAuthProgress progress)
    {
        var dto = ProviderMapping.ToDto(progress);
        lock (_oauthGate)
        {
            _oauthCurrent = dto.Phase == OAuthPhases.Waiting ? dto : null;
        }

        Safely(() => _topics.Publish(ProvidersMethods.OAuthTopic.Name, ProvidersEvents.OAuthProgress, dto, PayloadShapes.GetShape<OAuthProgressDto>()));
    }

    private void Safely(Action publish)
    {
        try
        {
            publish();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 广播失败不能影响正在做的事(一次保存、一次请求、一次同步)。控制端下次订阅 / 查询会拿到权威状态。
            _log.Warn("Publishing a catalog / providers event failed.", e);
        }
    }
}

/// <summary>默认的上游 HTTP:不跟随重定向(目录同步的硬要求;供应商接口也不该被重定向带去别处)、自动解压、不设整体超时(流式响应可能很长,超时由调用方按请求给)。</summary>
internal static class DefaultHttp
{
    /// <param name="proxy">设置页的代理(每次请求现读);null = 不走设置(默认的 <c>HttpClient.DefaultProxy</c> 行为)。</param>
    public static HttpClient Create(IWebProxy? proxy = null) => new(new SocketsHttpHandler
    {
        Proxy = proxy,
        UseProxy = proxy is not null,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
}
