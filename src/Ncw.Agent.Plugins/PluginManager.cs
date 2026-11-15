using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Session;

namespace Ncw.Agent.Plugins;

public sealed class PluginManagerOptions
{
    /// <summary>设备本机环境:插件包在设备上(<c>&lt;数据根&gt;/plugins</c>),插件的 JS 源码经它读。</summary>
    public required IExecutionEnvironment Environment { get; init; }

    /// <summary>插件根目录(本机环境里的绝对路径,<c>&lt;数据根&gt;/plugins</c>,与 2.x 同)。</summary>
    public required EnvPath Root { get; init; }

    public required IPluginKv Kv { get; init; }

    /// <summary>统一工具注册表:插件的工具按来源 <c>plugin:&lt;id&gt;</c> 登记 / 注销。</summary>
    public required ToolRegistry Tools { get; init; }

    public IPluginSecrets? Secrets { get; init; }

    public IPluginHttp? Http { get; init; }

    public IPluginWorkspaces? Workspaces { get; init; }

    /// <summary>市场目录与下载。null = 没有市场(浏览 / 安装 / 更新检查返回 <c>plugins.marketFailed</c>)。</summary>
    public IPluginMarketClient? Market { get; init; }

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public ILoopDiagnostics Log { get; init; } = NullLoopDiagnostics.Instance;

    public PluginRuntimeLimits Limits { get; init; } = new();

    public PluginAppInfo AppInfo { get; init; } = new("NextCoWork", "3.0.0", "en-US");

    /// <summary>这个宿主实现的插件 API 版本(<c>engines.nextcowork</c> 比的是它)。可注入,测试能在不改常量的前提下钉住 ok / deprecated / incompatible 三种结局。</summary>
    public string ApiVersion { get; init; } = PluginApiVersion.Current;

    /// <summary>会话 id → 它所在的项目 id(工具调用的作用域要用:<c>storage.workspace</c>、<c>workspace.folders</c>)。</summary>
    public Func<string, string?>? WorkspaceOfSession { get; init; }
}

/// <summary>
/// 插件系统(2.x <c>main/plugin/manager.ts</c> 去掉 Electron 之后):安装、启停、卸载、权限、配置、市场、更新,以及每个插件一个 Jaunt 运行时的生命周期。
/// <list type="bullet">
/// <item><b>状态</b>:清单每次从盘上重读,用户的决定(启用 / 授权 / 批准过的必选能力 / 来自哪个市场条目)存在 kv <c>plugins.state</c>(2.x 的键和形状,2.x 数据被接管之后原样可用)。
/// 全量快照(<see cref="Snapshot"/>)带单调 <c>Rev</c>,<see cref="Changed"/> 在每次状态变化之后触发(首帧与增量同一个类型,同一个生成函数)。</item>
/// <item><b>激活</b>:启用的插件在启动 / 启用 / 安装后立即激活(3.0 偏离 2.x 的激活事件 + 休眠:没有按需唤醒的壳,而工具的描述 / schema 只有跑过 <c>activate</c> 才知道)。</item>
/// <item><b>隔离</b>:一个插件的任何失败(插件 throw、超预算、引擎故障)只影响它自己:状态变 <c>faulted</c> / <c>error</c>、贡献撤下、诊断上报,Agent 不受影响。</item>
/// <item><b>贡献</b>:Agent 工具 → <see cref="ToolRegistry"/>;Skills → <see cref="SkillSource"/>(<c>ISkillPluginSource</c>);拦截器 → <see cref="Interceptor"/>(<c>IToolInterceptor</c>,只能收紧);上下文 → <see cref="CreateExtension"/>。</item>
/// </list>
/// </summary>
public sealed partial class PluginManager : IAsyncDisposable
{
    public const string StateKey = "plugins.state";

    private static string ConfigKey(string id) => $"plugins.config.{id}";

    private static string KvKey(string id) => $"plugins.kv.{id}";

    private static readonly TimeSpan DeactivateGrace = TimeSpan.FromSeconds(2);

    private readonly PluginManagerOptions _options;
    private readonly IExecutionEnvironment _env;
    private readonly EnvPath _root;
    private readonly PluginServices _services;
    private readonly PluginActivityLog _activity = new();
    private readonly Lock _gate = new();
    private readonly Lock _publishGate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Dictionary<string, PluginRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingInteraction> _interactions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginKvBucket> _buckets = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _disposed = new();
    private long _rev;
    private int _disposeFlag;

    public PluginManager(PluginManagerOptions options)
    {
        _options = options;
        _env = options.Environment;
        _root = options.Root;
        _services = new PluginServices
        {
            Http = options.Http ?? NoHttp.Instance,
            Secrets = options.Secrets ?? NoSecrets.Instance,
            Workspaces = options.Workspaces ?? NoWorkspaces.Instance,
            Clock = options.Clock,
            Log = options.Log,
            Limits = options.Limits,
            AppInfo = options.AppInfo,
            Kv = BucketOf,
            Configuration = GetConfiguration,
            Ask = AskAsync,
            Grant = (id, permissions) => Detach(GrantAsync(id, permissions)),
            Revoke = (id, permissions) => Detach(RevokeAsync(id, permissions)),
        };
        SkillSource = new PluginSkillSource(this);
        Interceptor = new PluginToolInterceptor(this);
    }

    /// <summary>插件在自己的调用里申请 / 放弃能力:不等结果(调用方持有引擎锁,等就是死锁),失败(插件已被卸载之类)只记日志,不留未观察的任务异常。</summary>
    private void Detach(Task<InstalledPlugin> work) =>
        _ = work.ContinueWith(t => _options.Log.Warn("[plugin] a permission change requested by a plugin failed", t.Exception!.GetBaseException()), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    /// <summary>状态变了(全量快照,带单调 <c>Rev</c>;在发布锁里按 <c>Rev</c> 序触发,所以推送序 = 版本序)。</summary>
    public event Action<PluginsSnapshot>? Changed;

    public string ApiVersion => _options.ApiVersion;

    // ───────────────────────── 快照 ─────────────────────────

    /// <summary>
    /// 全量状态。★ 不取 <c>_publishGate</c>:主题表在持有它自己的锁时调快照函数,而发布路径是先 <c>_publishGate</c> 后主题表的锁——两把锁反着取就是死锁。
    /// </summary>
    public PluginsSnapshot Snapshot()
    {
        lock (_gate)
        {
            return BuildSnapshot(Volatile.Read(ref _rev));
        }
    }

    private PluginsSnapshot BuildSnapshot(long rev) => new(
        rev,
        [.. _records.Values.OrderBy(r => r.Manifest.DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Manifest.Id, StringComparer.Ordinal).Select(Project)],
        [.. _interactions.Values.OrderBy(i => i.Request.CreatedAt).ThenBy(i => i.Request.RequestId, StringComparer.Ordinal).Select(i => i.Request)]);

    private void Publish()
    {
        if (Volatile.Read(ref _disposeFlag) != 0)
        {
            return;
        }

        lock (_publishGate)
        {
            PluginsSnapshot snapshot;
            lock (_gate)
            {
                snapshot = BuildSnapshot(++_rev);
            }

            try
            {
                Changed?.Invoke(snapshot);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _options.Log.Warn("[plugin] a Changed handler threw", e);
            }
        }
    }

    private InstalledPlugin Project(PluginRecord r)
    {
        var diagnostics = new List<PluginDiagnostic>(r.LoadDiagnostics);
        if (r.RuntimeDiagnostics is not null)
        {
            diagnostics.AddRange(r.RuntimeDiagnostics.Snapshot());
        }

        return new InstalledPlugin(
            r.Manifest, r.Status, r.Enabled, r.Directory.Value,
            new PluginPermissionState(r.Manifest.Permissions, r.Manifest.OptionalPermissions, PluginPermissions.Sort(r.Granted)),
            diagnostics, r.InstalledAt, r.UpdatedAt,
            [.. r.Manifest.Permissions.Where(p => !r.ApprovedRequired.Contains(p))],
            r.Slug,
            [.. (r.Host?.Tools.Keys ?? (ICollection<string>)[]).Order(StringComparer.Ordinal)],
            [.. (r.Host?.Commands.Keys ?? (ICollection<string>)[]).Order(StringComparer.Ordinal)],
            r.Messages);
    }

    // ───────────────────────── 记录 ─────────────────────────

    private sealed class PluginRecord(PluginManifest manifest, EnvPath directory)
    {
        public PluginManifest Manifest { get; set; } = manifest;

        public EnvPath Directory { get; } = directory;

        public bool Enabled { get; set; }

        public PluginStatus Status { get; set; }

        public List<string> Granted { get; set; } = [];

        public List<string> ApprovedRequired { get; set; } = [];

        public List<PluginDiagnostic> LoadDiagnostics { get; } = [];

        public PluginDiagnostics? RuntimeDiagnostics { get; set; }

        public long InstalledAt { get; set; }

        public long UpdatedAt { get; set; }

        public string? Slug { get; set; }

        public PluginHost? Host { get; set; }

        public PluginRuntime? Runtime { get; set; }

        /// <summary>包里 l10n 的词条:语言 → <c>plugin.&lt;id&gt;.&lt;key&gt;</c> → 文案(菜单项、命令名在插件被激活之前就要画出来,所以装载时读、随快照一起下发)。</summary>
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Messages { get; set; } = new Dictionary<string, IReadOnlyDictionary<string, string>>();

        public PluginPermissionState Permissions => new(Manifest.Permissions, Manifest.OptionalPermissions, Granted);

        /// <summary>必选能力都被批过(2.x:没批过的必选能力 = 不批不激活)。</summary>
        public bool RequiredApproved => Manifest.Permissions.All(ApprovedRequired.Contains);
    }

    private sealed record PendingInteraction(PluginInteraction Request, TaskCompletionSource<PluginInteractionReply?> Completion);

    private PluginRecord Require(string pluginId)
    {
        lock (_gate)
        {
            return _records.TryGetValue(pluginId, out var record) ? record : throw new PluginException(PluginErrorKeys.NotFound, $"There is no plugin \"{pluginId}\".", new Dictionary<string, string> { ["plugin"] = pluginId });
        }
    }

    private PluginKvBucket BucketOf(string pluginId)
    {
        lock (_gate)
        {
            if (!_buckets.TryGetValue(pluginId, out var bucket))
            {
                _buckets[pluginId] = bucket = new PluginKvBucket(_options.Kv, KvKey(pluginId), _options.Limits.StorageQuotaBytes);
            }

            return bucket;
        }
    }

    // ───────────────────────── 持久化的用户决定 ─────────────────────────

    private sealed record PersistedPlugin(bool Enabled, List<string> Granted, List<string> ApprovedRequired, long InstalledAt, long UpdatedAt, string? Slug);

    private Dictionary<string, PersistedPlugin> LoadState()
    {
        var state = new Dictionary<string, PersistedPlugin>(StringComparer.Ordinal);
        try
        {
            if (_options.Kv.Get(StateKey) is { } json && JsonNode.Parse(json) is JsonObject root)
            {
                foreach (var (id, node) in root)
                {
                    if (node is not JsonObject o)
                    {
                        continue;
                    }

                    state[id] = new PersistedPlugin(
                        o["enabled"] is JsonValue e && e.TryGetValue<bool>(out var enabled) && enabled,
                        Permissions(o["granted"]), Permissions(o["approvedRequired"]), Long(o["installedAt"]), Long(o["updatedAt"]),
                        o["slug"] is JsonValue s && s.TryGetValue<string>(out var slug) ? slug : null);
                }
            }
        }
        catch (JsonException)
        {
            // kv 是用户能手改的:一个坏值不该让插件系统起不来
        }

        return state;

        static List<string> Permissions(JsonNode? node)
        {
            var list = new List<string>();
            if (node is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is JsonValue v && v.TryGetValue<string>(out var p) && PluginPermissions.IsValid(p))
                    {
                        list.Add(p);
                    }
                }
            }

            return list;
        }

        static long Long(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
    }

    private void Persist()
    {
        var root = new JsonObject();
        lock (_gate)
        {
            foreach (var (id, r) in _records.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var o = new JsonObject
                {
                    ["enabled"] = r.Enabled,
                    ["granted"] = new JsonArray([.. PluginPermissions.Sort(r.Granted).Select(p => (JsonNode?)JsonValue.Create(p))]),
                    ["approvedRequired"] = new JsonArray([.. PluginPermissions.Sort(r.ApprovedRequired).Select(p => (JsonNode?)JsonValue.Create(p))]),
                    ["installedAt"] = r.InstalledAt,
                    ["updatedAt"] = r.UpdatedAt,
                };
                if (r.Slug is not null)
                {
                    o["slug"] = r.Slug;
                }

                root[id] = o;
            }
        }

        _options.Kv.Set(StateKey, root.ToJsonString());
    }

    // ───────────────────────── 装载 ─────────────────────────

    /// <summary>
    /// 扫描插件目录,读清单,恢复用户的决定,然后并行激活启用的插件。
    /// ★ 一个坏插件(清单读不懂、目录名对不上)不进记录、不拖垮别的:只记一条日志(一个没有 id 的东西没法被启用 / 禁用 / 卸载,放进表里只会变成一行点什么都没反应的条目)。
    /// 安装中断留下的 staging / backup 残片在这里清掉。
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _env.Fs.MkdirAsync(_root, recursive: true, ct).ConfigureAwait(false);
            var persisted = LoadState();
            foreach (var entry in await _env.Fs.ListAsync(_root, ct).ConfigureAwait(false))
            {
                if (!entry.Info.IsDirectory)
                {
                    continue;
                }

                if (entry.Name.Contains(".installing-", StringComparison.Ordinal) || entry.Name.Contains(".backup-", StringComparison.Ordinal))
                {
                    try
                    {
                        await _env.Fs.DeleteDirectoryAsync(_root.Join(entry.Name), recursive: true, ct).ConfigureAwait(false);
                    }
                    catch (EnvironmentException)
                    {
                    }

                    continue;
                }

                try
                {
                    var record = await LoadRecordAsync(_root.Join(entry.Name), persisted.GetValueOrDefault(entry.Name), ct).ConfigureAwait(false);
                    if (record.Manifest.Id != entry.Name)
                    {
                        _options.Log.Warn($"[plugin] {entry.Name}: the directory name does not match the manifest id {record.Manifest.Id}; skipped");
                        continue;
                    }

                    lock (_gate)
                    {
                        _records[record.Manifest.Id] = record;
                    }
                }
                catch (PluginException e)
                {
                    _options.Log.Warn($"[plugin] {entry.Name}: {e.Message}");
                }
            }

            Persist();
        }
        finally
        {
            _lifecycle.Release();
        }

        Publish();
        await ActivateAllAsync(ct).ConfigureAwait(false);
    }

    private async Task ActivateAllAsync(CancellationToken ct)
    {
        List<PluginRecord> candidates;
        lock (_gate)
        {
            candidates = [.. _records.Values.Where(NeedsActivation)];
        }

        await Task.WhenAll(candidates.Select(r => ActivateAsync(r, ct))).ConfigureAwait(false);
    }

    private static bool NeedsActivation(PluginRecord r) => r.Enabled && r.Status == PluginStatus.Idle && r.RequiredApproved && r.Manifest.Kind == PluginKind.Extension;

    private async Task<PluginRecord> LoadRecordAsync(EnvPath directory, PersistedPlugin? persisted, CancellationToken ct)
    {
        var manifest = await PluginPackageInstaller.ReadManifestAsync(_env, directory, ct).ConfigureAwait(false);
        var now = _options.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        var record = new PluginRecord(manifest, directory)
        {
            Enabled = persisted?.Enabled ?? false,
            Granted = [.. PluginPermissions.Sort((persisted?.Granted ?? []).Where(p => manifest.Permissions.Contains(p) || manifest.OptionalPermissions.Contains(p)))],
            ApprovedRequired = [.. persisted?.ApprovedRequired ?? []],
            InstalledAt = persisted?.InstalledAt is > 0 ? persisted.InstalledAt : now,
            UpdatedAt = now,
            Slug = persisted?.Slug,
        };
        record.RuntimeDiagnostics = new PluginDiagnostics(Publish);

        // 认得字段名、但 3.0 不实现的贡献点:按清单逐条给诊断,不静默不生效
        foreach (var key in manifest.Contributes.Unsupported)
        {
            record.LoadDiagnostics.Add(new PluginDiagnostic(
                $"contributes.{key}", PluginDiagnosticKeys.Unsupported, $"The contribution point \"{key}\" is not supported by this host; it is ignored.", "warn", new Dictionary<string, string> { ["contribution"] = key }));
        }

        var status = PluginStatus.Idle;
        switch (PluginApiVersion.Check(manifest.Engines, _options.ApiVersion))
        {
            case EngineCompatibility.Incompatible:
                record.LoadDiagnostics.Add(new PluginDiagnostic(
                    "engines.nextcowork", PluginDiagnosticKeys.EngineIncompatible, $"The plugin requires {manifest.Engines}, this host implements plugin API {_options.ApiVersion}.", "error",
                    new Dictionary<string, string> { ["required"] = manifest.Engines, ["api"] = _options.ApiVersion }));
                status = PluginStatus.Error;
                break;
            case EngineCompatibility.Deprecated:
                // 弃用但照常跑:这批插件在此之前根本装不上,兼容它们不会破坏任何既有行为
                record.LoadDiagnostics.Add(new PluginDiagnostic(
                    "engines.nextcowork", PluginDiagnosticKeys.EngineDeprecated, $"\"{manifest.Engines}\" targets a retired plugin API; update it to ^{_options.ApiVersion}.", "warn",
                    new Dictionary<string, string> { ["required"] = manifest.Engines, ["api"] = _options.ApiVersion }));
                break;
        }

        if (status != PluginStatus.Error && manifest.Kind == PluginKind.Extension && !await _env.Fs.ExistsAsync(directory.Join(PluginPackageInstaller.NormalizeRelative(manifest.Main).Split('/')), ct).ConfigureAwait(false))
        {
            record.LoadDiagnostics.Add(new PluginDiagnostic(
                manifest.Main, PluginDiagnosticKeys.MainMissing, $"The entry file \"{manifest.Main}\" is missing from the package.", "error", new Dictionary<string, string> { ["path"] = manifest.Main }));
            status = PluginStatus.Error;
        }

        // ★ 升级后必选能力变大 → pending-approval,不批不激活:堵住「装完之后自动更新悄悄扩权」那条路
        if (status != PluginStatus.Error && persisted is not null && PluginPermissionRules.Escalated(persisted.ApprovedRequired, manifest.Permissions))
        {
            var added = PluginPermissionRules.Added(persisted.ApprovedRequired, manifest.Permissions);
            record.LoadDiagnostics.Add(new PluginDiagnostic(
                "permissions", PluginDiagnosticKeys.PendingApproval, $"This version asks for new permissions: {string.Join(", ", added)}.", "warn",
                new Dictionary<string, string> { ["permissions"] = string.Join(',', added) }));
            status = PluginStatus.PendingApproval;
        }

        record.Status = status is PluginStatus.Error or PluginStatus.PendingApproval ? status : record.Enabled ? PluginStatus.Idle : PluginStatus.Disabled;
        record.Messages = await ReadMessagesAsync(manifest, directory, record.LoadDiagnostics, ct).ConfigureAwait(false);
        return record;
    }

    private const int MaxLocaleBytes = 128 * 1024;

    /// <summary>读包里 <c>l10n/</c> 的两种语言。★ 读不出来不是装载失败(文案是装饰):留一条 warn 诊断供作者排查。键由宿主加 <c>plugin.&lt;id&gt;.</c> 前缀。</summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> ReadMessagesAsync(PluginManifest manifest, EnvPath directory, List<PluginDiagnostic> diagnostics, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        if (manifest.L10n is not { } l10n)
        {
            return result;
        }

        foreach (var file in PluginLimits.LocaleFiles)
        {
            var path = $"{PluginPackageInstaller.NormalizeRelative(l10n)}/{file}";
            try
            {
                var text = await _env.Fs.ReadTextAsync(directory.Join(path.Split('/')), MaxLocaleBytes, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var entries = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var property in doc.RootElement.EnumerateObject().Take(1000))
                {
                    if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: <= 4000 } value)
                    {
                        entries[$"plugin.{manifest.Id}.{property.Name}"] = value;
                    }
                }

                result[file[..^".json".Length]] = entries;
            }
            catch (Exception e) when (e is EnvironmentException or JsonException)
            {
                diagnostics.Add(new PluginDiagnostic(path, PluginDiagnosticKeys.ManifestWarning, $"The l10n bundle could not be read ({e.GetType().Name}).", "warn"));
            }
        }

        return result;
    }

    // ───────────────────────── 安装 / 卸载 ─────────────────────────

    /// <summary>
    /// 从 zip 字节安装(文件选择框是客户端本地的,这里只收字节)。覆盖安装 = 升级:用户的决定(启用 / 授权)原样保留,必选能力变大则进入待批准。
    /// 新装的插件是<b>禁用</b>的,没有任何授权:用户在授权界面逐条勾选(<see cref="GrantAsync"/>)再启用(<see cref="SetEnabledAsync"/>)。
    /// </summary>
    public async Task<InstalledPlugin> InstallZipAsync(ReadOnlyMemory<byte> zip, string? slug = null, string? expectedSha256 = null, CancellationToken ct = default)
    {
        PluginRecord record;
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var installed = await PluginPackageInstaller.InstallZipAsync(zip, _env, _root, expectedSha256, ct).ConfigureAwait(false);
            record = await AfterInstallAsync(installed, slug, ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }

        return await ActivateAfterInstallAsync(record, ct).ConfigureAwait(false);
    }

    /// <summary>从一个目录安装(本地开发;源目录在 <paramref name="sourceEnvironment"/> 里)。复制一份进插件目录,不做软链。</summary>
    public async Task<InstalledPlugin> InstallDirectoryAsync(IExecutionEnvironment sourceEnvironment, EnvPath sourceDirectory, CancellationToken ct = default)
    {
        PluginRecord record;
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var installed = await PluginPackageInstaller.InstallDirectoryAsync(sourceEnvironment, sourceDirectory, _env, _root, ct).ConfigureAwait(false);
            record = await AfterInstallAsync(installed, null, ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }

        return await ActivateAfterInstallAsync(record, ct).ConfigureAwait(false);
    }

    private async Task<PluginRecord> AfterInstallAsync(InstalledPackage installed, string? slug, CancellationToken ct)
    {
        var id = installed.Manifest.Id;
        // ★ 旧决定必须在停掉旧版本之前读出来(停掉会落盘「已禁用」,之后再读到的旧配置里开关已经是关的了:更新完版本号对了,插件被静默关掉)
        var persisted = LoadState().GetValueOrDefault(id);
        PluginRecord? previous;
        lock (_gate)
        {
            _records.TryGetValue(id, out previous);
        }

        if (previous is not null)
        {
            await StopRuntimeAsync(previous).ConfigureAwait(false);
        }

        var record = await LoadRecordAsync(installed.Directory, persisted, ct).ConfigureAwait(false);
        // ★ slug 在装载之后写,不混进 persisted 里(「有没有 persisted」本身有含义:扩权闸门以它为前提)。
        // 本地覆盖安装时清掉旧 slug:用户手上这一份已经不是市场那一份了,不该再被「全部更新」悄悄换回去
        record.Slug = slug;
        lock (_gate)
        {
            _records[id] = record;
        }

        Persist();
        Publish();
        return record;
    }

    private async Task<InstalledPlugin> ActivateAfterInstallAsync(PluginRecord record, CancellationToken ct)
    {
        if (NeedsActivation(record))
        {
            await ActivateAsync(record, ct).ConfigureAwait(false);
        }

        lock (_gate)
        {
            return Project(record);
        }
    }

    /// <summary>
    /// 卸载:<b>先下线(注销贡献、销毁运行时),再删目录</b>(反过来的话,一次正在进行的调用会读到一个已经消失的包)。清掉状态、配置、私有存储、密钥、活动日志。
    /// 插件提供的 Skill 随之消失(下一次扫描时 <see cref="SkillSource"/> 不再返回它)。
    /// </summary>
    public async Task UninstallAsync(string pluginId, CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var record = Require(pluginId);
            await StopRuntimeAsync(record).ConfigureAwait(false);
            lock (_gate)
            {
                _records.Remove(pluginId);
            }

            _activity.Clear(pluginId);
            var bucket = BucketOf(pluginId);
            foreach (var key in bucket.TrackedSecrets())
            {
                try
                {
                    _services.Secrets.Remove(HostGuards.SecretReference(pluginId, key));
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    _options.Log.Warn($"[plugin] {pluginId}: removing a secret failed", e);
                }
            }

            bucket.Clear();
            _options.Kv.Set(ConfigKey(pluginId), "{}");
            lock (_gate)
            {
                _buckets.Remove(pluginId);
            }

            Persist();
            try
            {
                await _env.Fs.DeleteDirectoryAsync(record.Directory, recursive: true, ct).ConfigureAwait(false);
            }
            catch (EnvironmentException e)
            {
                _options.Log.Warn($"[plugin] {pluginId}: removing the package directory failed ({e.Code})", e);
            }

            Publish();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    // ───────────────────────── 启用 / 禁用 ─────────────────────────

    public async Task<InstalledPlugin> SetEnabledAsync(string pluginId, bool enabled, CancellationToken ct = default)
    {
        PluginRecord record;
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            record = Require(pluginId);
            if (!enabled)
            {
                lock (_gate)
                {
                    record.Enabled = false;
                    record.Status = PluginStatus.Disabled;
                }

                await StopRuntimeAsync(record).ConfigureAwait(false);
                Persist();
                Publish();
                return ProjectLocked(record);
            }

            if (record.Status == PluginStatus.Error)
            {
                throw new PluginException(PluginErrorKeys.NotRunnable, "The plugin failed to load and cannot be enabled.", new Dictionary<string, string> { ["plugin"] = pluginId });
            }

            // ★ 待批准的插件启用不了:不止界面上禁用,这里也要挡住——任何一条绕过界面的路径都不能把一个扩了权的版本悄悄跑起来
            if (!record.RequiredApproved)
            {
                lock (_gate)
                {
                    record.Status = PluginStatus.PendingApproval;
                }

                Publish();
                return ProjectLocked(record);
            }

            lock (_gate)
            {
                record.Enabled = true;
                record.Status = PluginStatus.Idle;
                record.RuntimeDiagnostics?.Clear();
            }

            Persist();
            Publish();
        }
        finally
        {
            _lifecycle.Release();
        }

        if (NeedsActivation(record))
        {
            await ActivateAsync(record, ct).ConfigureAwait(false);
        }

        return ProjectLocked(record);
    }

    private InstalledPlugin ProjectLocked(PluginRecord record)
    {
        lock (_gate)
        {
            return Project(record);
        }
    }

    // ───────────────────────── 权限 ─────────────────────────

    /// <summary>
    /// 授予。越过清单上界(required ∪ optional)的静默丢弃——<b>清单里的能力上界 = 运行期可获得的能力上界</b>(<see cref="PluginPermissionState.CanRequest"/>)。
    /// 必选能力一旦被批过就记进 <c>approvedRequired</c>(下次升级拿它比对扩权);必选全批了而插件待批准 → 解除待批准(启用着的随即激活)。
    /// </summary>
    public async Task<InstalledPlugin> GrantAsync(string pluginId, IEnumerable<string> permissions, CancellationToken ct = default)
    {
        var record = Require(pluginId);
        var requested = permissions.ToList();
        lock (_gate)
        {
            record.Granted = [.. record.Permissions.Grant(requested).Granted];
            foreach (var p in record.Manifest.Permissions)
            {
                if (record.Granted.Contains(p) && !record.ApprovedRequired.Contains(p))
                {
                    record.ApprovedRequired.Add(p);
                }
            }

            if (record.Status == PluginStatus.PendingApproval && record.RequiredApproved)
            {
                record.Status = record.Enabled ? PluginStatus.Idle : PluginStatus.Disabled;
            }
        }

        Persist();
        Publish();
        if (NeedsActivation(record))
        {
            await ActivateAsync(record, ct).ConfigureAwait(false);
        }

        return ProjectLocked(record);
    }

    /// <summary>
    /// 撤销。★ 撤掉必选能力 = 这个插件不能再跑:留着跑意味着它的每一次调用都会被能力门拒掉,而作者和用户都看不出为什么——不如直接停下来(状态变已禁用)。
    /// 授权状态是<b>实时</b>读的,所以撤销对正在跑的插件立刻生效(它的下一次宿主调用就被拒)。
    /// </summary>
    public async Task<InstalledPlugin> RevokeAsync(string pluginId, IEnumerable<string> permissions, CancellationToken ct = default)
    {
        var record = Require(pluginId);
        var stop = false;
        lock (_gate)
        {
            record.Granted = [.. record.Permissions.Revoke(permissions).Granted];
            record.ApprovedRequired = [.. record.ApprovedRequired.Where(record.Granted.Contains)];
            if (record.Manifest.Permissions.Any(p => !record.Granted.Contains(p)) && record.Status is not (PluginStatus.Error or PluginStatus.Disabled))
            {
                record.Enabled = false;
                record.Status = PluginStatus.Disabled;
                stop = true;
            }
        }

        if (stop)
        {
            await StopRuntimeAsync(record).ConfigureAwait(false);
        }

        Persist();
        Publish();
        return ProjectLocked(record);
    }

    // ───────────────────────── 配置 ─────────────────────────

    /// <summary>用户在详情页里拨的那些开关,合上清单里的默认值。★ 读的时候以清单为准:用户改过、后来插件升级把那一项删掉了——残留的值不该还出现在结果里。</summary>
    public IReadOnlyDictionary<string, PluginConfigValue> GetConfiguration(string pluginId)
    {
        PluginManifest manifest;
        lock (_gate)
        {
            if (!_records.TryGetValue(pluginId, out var record))
            {
                return new Dictionary<string, PluginConfigValue>();
            }

            manifest = record.Manifest;
        }

        var stored = ReadStoredConfiguration(pluginId);
        var result = new Dictionary<string, PluginConfigValue>(StringComparer.Ordinal);
        foreach (var (key, property) in manifest.Contributes.Configuration?.Properties ?? new Dictionary<string, PluginConfigurationProperty>())
        {
            result[key] = stored.TryGetValue(key, out var value) && IsAssignable(property, value) ? value : DefaultOf(property);
        }

        return result;
    }

    /// <summary>用户改了一项。<c>null</c> / 空值 = 恢复默认(删掉那一条,而不是写一个 null 进去:后者会让「用户没设过」和「用户设成了空」变成同一件事)。类型对不上的拒绝,不写进 kv。</summary>
    public IReadOnlyDictionary<string, PluginConfigValue> SetConfiguration(string pluginId, string key, PluginConfigValue? value)
    {
        var record = Require(pluginId);
        if (record.Manifest.Contributes.Configuration?.Properties.TryGetValue(key, out var property) != true)
        {
            throw new PluginException(PluginErrorKeys.ConfigurationInvalid, $"The plugin has no setting \"{key}\".", new Dictionary<string, string> { ["key"] = key });
        }

        var stored = ReadStoredConfiguration(pluginId);
        if (value is not { IsEmpty: false } v)
        {
            stored.Remove(key);
        }
        else if (IsAssignable(property!, v))
        {
            stored[key] = v;
        }
        else
        {
            throw new PluginException(PluginErrorKeys.ConfigurationInvalid, $"The value does not fit the type of \"{key}\".", new Dictionary<string, string> { ["key"] = key, ["type"] = property!.Type });
        }

        var node = new JsonObject();
        foreach (var (k, val) in stored)
        {
            node[k] = val.Flag is { } b ? b : val.Number is { } n ? n : val.Text;
        }

        _options.Kv.Set(ConfigKey(pluginId), node.ToJsonString());
        Publish();
        return GetConfiguration(pluginId);
    }

    private Dictionary<string, PluginConfigValue> ReadStoredConfiguration(string pluginId)
    {
        var stored = new Dictionary<string, PluginConfigValue>(StringComparer.Ordinal);
        try
        {
            if (_options.Kv.Get(ConfigKey(pluginId)) is { } json && JsonNode.Parse(json) is JsonObject root)
            {
                foreach (var (key, node) in root)
                {
                    if (node is JsonValue v)
                    {
                        if (v.TryGetValue<bool>(out var b))
                        {
                            stored[key] = PluginConfigValue.Of(b);
                        }
                        else if (v.TryGetValue<double>(out var n))
                        {
                            stored[key] = PluginConfigValue.Of(n);
                        }
                        else if (v.TryGetValue<string>(out var s))
                        {
                            stored[key] = PluginConfigValue.Of(s);
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return stored;
    }

    private static bool IsAssignable(PluginConfigurationProperty property, PluginConfigValue value) => property.Type switch
    {
        "boolean" => value.Flag is not null,
        "number" => value.Number is { } n && double.IsFinite(n),
        "enum" => value.Text is { } s && (property.Enum.Count == 0 || property.Enum.Contains(s, StringComparer.Ordinal)),
        _ => value.Text is not null,
    };

    private static PluginConfigValue DefaultOf(PluginConfigurationProperty property) => property.Type switch
    {
        "boolean" => PluginConfigValue.Of(property.DefaultBool ?? false),
        "number" => PluginConfigValue.Of(property.DefaultNumber ?? 0),
        _ => PluginConfigValue.Of(property.DefaultString ?? string.Empty),
    };

    // ───────────────────────── 活动 / 交互 ─────────────────────────

    /// <summary>最近的宿主调用(最近的在前;<c>null</c> = 所有插件)。每次调用记一条,不含参数原文。</summary>
    public IReadOnlyList<PluginActivity> Activity(string? pluginId = null) => _activity.Recent(pluginId);

    /// <summary>
    /// 回答一个插件交互。取消 = 回答里全是 null / false(不是错误)。请求不存在(已经超时 / 被撤销 / 别的控制端先回答了)抛 <see cref="PluginErrorKeys.InteractionNotFound"/>。
    /// </summary>
    public void ReplyInteraction(string requestId, PluginInteractionReply reply)
    {
        PendingInteraction? pending;
        lock (_gate)
        {
            pending = _interactions.GetValueOrDefault(requestId);
        }

        if (pending is null || !pending.Completion.TrySetResult(reply))
        {
            throw new PluginException(PluginErrorKeys.InteractionNotFound, "There is no pending plugin request with this id.", new Dictionary<string, string> { ["requestId"] = requestId });
        }
    }

    private async Task<PluginInteractionReply?> AskAsync(PluginInteraction template, CancellationToken ct)
    {
        var request = template with { RequestId = Guid.NewGuid().ToString("N") };
        var pending = new PendingInteraction(request, new TaskCompletionSource<PluginInteractionReply?>(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_gate)
        {
            _interactions[request.RequestId] = pending;
        }

        Publish();
        try
        {
            return await pending.Completion.Task.WaitAsync(_options.Limits.InteractionTimeout, _options.Clock, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null; // 没有人回答 = 取消(不是故障)
        }
        finally
        {
            lock (_gate)
            {
                _interactions.Remove(request.RequestId);
            }

            Publish();
        }
    }

    private void CancelInteractions(string pluginId)
    {
        List<PendingInteraction> mine;
        lock (_gate)
        {
            mine = [.. _interactions.Values.Where(i => i.Request.PluginId == pluginId)];
        }

        foreach (var pending in mine)
        {
            pending.Completion.TrySetResult(null);
        }
    }

    // ───────────────────────── 销毁 ─────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeFlag, 1) != 0)
        {
            return;
        }

        await _disposed.CancelAsync().ConfigureAwait(false);
        List<PluginRecord> all;
        lock (_gate)
        {
            all = [.. _records.Values];
        }

        foreach (var record in all)
        {
            await StopRuntimeAsync(record, deactivate: false).ConfigureAwait(false);
        }

        _disposed.Dispose();
    }

    private sealed class NoHttp : IPluginHttp
    {
        public static NoHttp Instance { get; } = new();

        public Task<PluginHttpResponse> SendAsync(PluginHttpRequest request, int maxBodyBytes, CancellationToken ct) =>
            throw new HostCallException("unavailable", "no network access is configured on this device");
    }

    private sealed class NoSecrets : IPluginSecrets
    {
        public static NoSecrets Instance { get; } = new();

        public string? Get(string reference) => null;

        public void Set(string reference, string value) => throw new HostCallException("unavailable", "no credential store is configured on this device");

        public bool Remove(string reference) => false;
    }

    private sealed class NoWorkspaces : IPluginWorkspaces
    {
        public static NoWorkspaces Instance { get; } = new();

        public PluginWorkspaceTarget? Resolve(string workspaceId) => null;
    }
}
