using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Media;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Llm.Thinking;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Catalog;

/// <summary>选中的模型落在哪一条绑定上,没落上时是为什么(路由发请求时会给权威错误,这里只回答「界面该怎么画」)。</summary>
public enum ModelResolutionIssue
{
    None,
    UnknownAlias,
    PinnedProviderMissing,
    PinnedProviderDisabled,
    PinnedModelMissing,
    NoEnabledProvider,
}

/// <summary>
/// 一次解析的结果。<see cref="Ref"/> 是交给 <c>LlmRouter</c> 的 <see cref="ModelRef"/>;<see cref="Binding"/> / <see cref="Provider"/> 是路由会落到的那一条
/// (与路由同一段规则算出来的:界面说 A、请求发给 B 且不报错,是这个模块存在要消灭的那个 bug)。<see cref="Info"/> 是目录里的有效元数据(窗口、能力、价格)。
/// </summary>
public sealed record ModelResolution(
    ModelRef Ref,
    ModelBinding? Binding,
    ProviderConfig? Provider,
    ModelInfo? Info,
    ModelResolutionIssue Issue)
{
    public bool Found => Binding is not null;

    public WireProtocol? Protocol => Binding is null || Provider is null ? null : Binding.ProtocolOverride ?? Provider.Protocol;
}

/// <summary>图片 / 视频 / 音频模型的路由解析结果。</summary>
public sealed record MediaModelResolution(ProviderRecord Provider, ModelAliasRecord Alias, ProviderProtocol Protocol);

/// <summary>
/// 把「会话选的模型 / 供应商」解析成 Llm 层 <c>LlmRouter</c> 所需的路由配置。Host 把它注入给会话层(解析上下文窗口、校验图片 / 思考)和路由(<see cref="IProviderConfigSource"/>)。
/// </summary>
public interface IModelResolver
{
    ModelResolution Resolve(ModelSelection selection);

    /// <summary>路由的配置来源(供应商 + 绑定)。设置页改完供应商立刻生效,不必重建路由。</summary>
    IProviderConfigSource ConfigSource { get; }

    /// <summary>选择器的全部候选(用户配过的全部模型,含停用的、冷却中的:它们应该显示出来并标成不可用,否则用户会以为自己的配置丢了)。</summary>
    IReadOnlyList<ModelOption> Options();
}

/// <summary>选择器里的一行。</summary>
public sealed record ModelOption(
    string ProviderId,
    string ProviderName,
    string Alias,
    string DisplayName,
    string UpstreamModel,
    int ContextWindow,
    int MaxOutputTokens,
    bool Enabled,
    bool ProviderEnabled,
    bool SupportsImages,
    bool IsChatModel,
    ThinkingMode? ThinkingMode,
    IReadOnlyList<ReasoningEffort>? ReasoningEfforts,
    ProviderProtocol Protocol,
    ProviderKind Kind = ProviderKind.Text);

/// <summary>
/// <see cref="IModelResolver"/> 的实现,同时是 <see cref="IProviderConfigSource"/>。绑定表按 (供应商快照版本, 目录版本) 缓存:
/// 路由每次请求都会枚举绑定,而目录有五千多个模型的索引,不能每次现算。
/// </summary>
public sealed class ProviderConfigSource : IModelResolver, IProviderConfigSource
{
    private readonly ProviderService _providers;
    private readonly ModelCatalogService _catalog;
    private readonly Func<bool> _failover;
    private readonly object _gate = new();
    private (long ProviderVersion, long CatalogVersion, IReadOnlyList<ProviderConfig> Providers, IReadOnlyList<ModelBinding> Bindings, IReadOnlyList<ModelInfo?> Infos)? _cache;

    public ProviderConfigSource(ProviderService providers, ModelCatalogService catalog, Func<bool>? failoverEnabled = null)
    {
        _providers = providers;
        _catalog = catalog;
        _failover = failoverEnabled ?? (() => true);
    }

    public IProviderConfigSource ConfigSource => this;

    public IReadOnlyList<ProviderConfig> Providers => Build().Providers;

    public IReadOnlyList<ModelBinding> Bindings => Build().Bindings;

    /// <summary>旁路开关(2.x 沿用):关掉时路由只取优先级最高的一家。每次现读。</summary>
    public bool FailoverEnabled => _failover();

    private (long ProviderVersion, long CatalogVersion, IReadOnlyList<ProviderConfig> Providers, IReadOnlyList<ModelBinding> Bindings, IReadOnlyList<ModelInfo?> Infos) Build()
    {
        var catalog = _catalog.Current.Version;
        lock (_gate)
        {
            if (_cache is { } hit && hit.ProviderVersion == _providers.Version && hit.CatalogVersion == catalog)
            {
                return hit;
            }

            var snapshot = _providers.Snapshot;

            // LlmRouter 只路由文本对话模型;图片 / 视频 / 音频供应商不进入文本路由表。
            var textProviders = snapshot.Providers.Where(p => p.Kind == ProviderKind.Text && p.TextProtocol is not null).ToList();
            var byId = textProviders.ToDictionary(p => p.Id, StringComparer.Ordinal);
            var configs = textProviders
                .Select(p => new ProviderConfig(p.Id, p.Name, p.BaseUrl, p.TextProtocol!.Value, p.Priority, p.Enabled, p.CredentialRef, p.CacheTtl))
                .ToList();
            var bindings = new List<ModelBinding>();
            var infos = new List<ModelInfo?>();
            foreach (var alias in snapshot.Aliases)
            {
                if (!byId.TryGetValue(alias.ProviderId, out var provider))
                {
                    continue;
                }

                var info = _catalog.Describe(provider.Id, alias.UpstreamModel);
                bindings.Add(ModelBindingResolver.Resolve(alias, provider, info));
                infos.Add(info);
            }

            return (_cache = (snapshot.Version, catalog, configs, bindings, infos)).Value;
        }
    }

    /// <summary>
    /// 与路由 <c>BindingsFor</c> 逐字一致的规则:按别名筛 → 钉住供应商时<b>不做任何回退</b>(那家没了就是空,而不是「换一家给你」)→ 供应商和绑定都得启用 →
    /// 只按供应商 priority 升序,同分时稳定排序保留输入顺序。任何一边改了排序,两边就又分家了。
    /// </summary>
    public ModelResolution Resolve(ModelSelection selection)
    {
        var cache = Build();
        var providers = cache.Providers.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var all = new List<(ModelBinding Binding, ModelInfo? Info)>();
        for (var i = 0; i < cache.Bindings.Count; i++)
        {
            if (cache.Bindings[i].Alias == selection.Alias)
            {
                all.Add((cache.Bindings[i], cache.Infos[i]));
            }
        }

        var pinned = selection.ProviderId;
        var candidates = all
            .Where(c => pinned is null || c.Binding.ProviderId == pinned)
            .Where(c => providers.TryGetValue(c.Binding.ProviderId, out var p) && p.Enabled && c.Binding.Enabled)
            .OrderBy(c => providers[c.Binding.ProviderId].Priority)
            .ToList();
        if (candidates.Count > 0)
        {
            var (binding, info) = candidates[0];
            return new ModelResolution(selection.ToRef(), binding, providers[binding.ProviderId], info, ModelResolutionIssue.None);
        }

        return new ModelResolution(selection.ToRef(), null, null, null, IssueFor(selection, all.Select(a => a.Binding).ToList(), providers));
    }

    /// <summary>按相同优先级 / 钉住供应商规则解析图片 / 视频 / 音频模型。</summary>
    public MediaModelResolution? ResolveMedia(ModelSelection selection, ProviderKind kind)
    {
        var snapshot = _providers.Snapshot;
        var providers = snapshot.Providers.Where(p => p.Kind == kind).ToDictionary(p => p.Id, StringComparer.Ordinal);
        var pinned = selection.ProviderId;
        var winner = snapshot.Aliases
            .Where(a => a.Alias == selection.Alias && (pinned is null || a.ProviderId == pinned))
            .Where(a => a.IsEnabled && providers.TryGetValue(a.ProviderId, out var p) && p.Enabled)
            .OrderBy(a => providers[a.ProviderId].Priority)
            .FirstOrDefault();
        if (winner is null)
        {
            return null;
        }

        var provider = providers[winner.ProviderId];
        var protocol = winner.ProtocolOverride is { } po && po.KindOf() == kind ? po : provider.Protocol;
        return new MediaModelResolution(provider, winner, protocol);
    }

    private static ModelResolutionIssue IssueFor(ModelSelection selection, List<ModelBinding> bindings, Dictionary<string, ProviderConfig> providers)
    {
        if (selection.ProviderId is { } pinned)
        {
            if (!providers.TryGetValue(pinned, out var provider))
            {
                return ModelResolutionIssue.PinnedProviderMissing;
            }

            return !provider.Enabled ? ModelResolutionIssue.PinnedProviderDisabled : ModelResolutionIssue.PinnedModelMissing;
        }

        return bindings.Count == 0 ? ModelResolutionIssue.UnknownAlias : ModelResolutionIssue.NoEnabledProvider;
    }

    public IReadOnlyList<ModelOption> Options()
    {
        var cache = Build();
        var snapshot = _providers.Snapshot;
        var allProviders = snapshot.Providers.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var rank = snapshot.Providers.Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        var textBindings = new Dictionary<(string ProviderId, string Alias), (ModelBinding Binding, ModelInfo? Info)>();
        for (var i = 0; i < cache.Bindings.Count; i++)
        {
            var binding = cache.Bindings[i];
            textBindings[(binding.ProviderId, binding.Alias)] = (binding, cache.Infos[i]);
        }

        var options = new List<(ModelOption Option, int Rank, int Priority)>();
        foreach (var record in snapshot.Aliases)
        {
            if (!allProviders.TryGetValue(record.ProviderId, out var provider))
            {
                continue;
            }

            if (provider.Kind == ProviderKind.Text && textBindings.TryGetValue((record.ProviderId, record.Alias), out var resolved))
            {
                var (binding, info) = resolved;
                var chat = (record.Modality is null or "text") && (info?.Capabilities.IsChatModel ?? true);
                var protocol = binding.ProtocolOverride?.ToProviderProtocol() ?? provider.Protocol;
                options.Add((new ModelOption(
                    binding.ProviderId, provider.Name, binding.Alias, record.DisplayName ?? info?.DisplayName ?? binding.Alias, binding.UpstreamModel,
                    binding.ContextWindow, binding.MaxOutputTokens, binding.Enabled, provider.Enabled, binding.SupportsImages, chat,
                    binding.Thinking?.Mode, binding.ReasoningEfforts, protocol, ProviderKind.Text),
                    rank[binding.ProviderId], record.Priority ?? int.MaxValue));
            }
            else
            {
                var info = _catalog.Describe(provider.Id, record.UpstreamModel);
                var protocol = record.ProtocolOverride ?? provider.Protocol;
                options.Add((new ModelOption(
                    record.ProviderId, provider.Name, record.Alias, record.DisplayName ?? info?.DisplayName ?? record.Alias, record.UpstreamModel,
                    record.ContextWindow, record.MaxOutputTokens, record.IsEnabled, provider.Enabled, false, false,
                    null, null, protocol, provider.Kind),
                    rank[record.ProviderId], record.Priority ?? int.MaxValue));
            }
        }

        // 「供应商顺序 → 别名优先级 → 别名」:下拉框的顺序是用户资产,不该按字典序重排成他不认识的样子。
        return options.OrderBy(o => o.Rank).ThenBy(o => o.Priority).ThenBy(o => o.Option.Alias, StringComparer.CurrentCulture).Select(o => o.Option).ToList();
    }
}
