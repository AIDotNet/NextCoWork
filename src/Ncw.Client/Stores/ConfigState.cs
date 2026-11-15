using System.Collections.Immutable;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Stores;

/// <summary>
/// 一台设备上模型目录的镜像(<c>catalog</c> 主题 + 按需拉的目录供应商列表)。真源在设备的 <c>ModelCatalogService</c>(AGENTS.md §3 规则 1,key 带 deviceId)。
/// 目录本身(几千个模型)不进 Store:界面按 <see cref="Status"/>.Version 变化用 <c>CatalogActions.QueryAsync</c> 按需拉。
/// </summary>
/// <param name="Known">false = 设备断开或还没拿到快照,状态是未知(规则 7),界面不把旧值当现状。</param>
/// <param name="ProvidersVersion"><see cref="Providers"/> 是哪一版目录的(和 <see cref="Status"/>.Version 对不上就该重拉)。</param>
public sealed record DeviceCatalog(
    bool Known,
    CatalogStatusDto Status,
    ImmutableArray<ModelOverrideDto> Overrides,
    ImmutableArray<CatalogProviderDto> Providers,
    long ProvidersVersion)
{
    public static readonly DeviceCatalog Unknown = new(false, new CatalogStatusDto(0, "seed", null, null, null, 0, 0, null), [], [], 0);
}

public sealed record CatalogMirror(ImmutableDictionary<string, DeviceCatalog> Devices)
{
    public static readonly CatalogMirror Empty = new(ImmutableDictionary<string, DeviceCatalog>.Empty);

    public DeviceCatalog Of(string deviceId) => Devices.GetValueOrDefault(deviceId) ?? DeviceCatalog.Unknown;
}

/// <summary>
/// 一台设备上供应商配置的镜像(<c>providers</c> 主题 + <c>providers.oauth</c> 主题 + 模型选择器候选)。真源在设备的 <c>ProviderService</c>。
/// 这里<b>没有任何凭据</b>:只有「有没有 key」和登录态摘要;明文只经 <c>ProvidersActions.RevealCredentialAsync</c> 单次取回,不落进 Store。
/// </summary>
/// <param name="Version">供应商快照版本,单调,只收不更旧的。</param>
/// <param name="Accounts">providerId → 这家的账号(只含走账号登录的供应商)。</param>
/// <param name="Options">模型选择器的候选(<c>providers.models.list</c>)。设备端算(别名 + 目录 + 绑定规则只有一份),随供应商 / 目录版本变化重拉。</param>
/// <param name="OptionsFor">候选是哪一对(供应商版本, 目录版本)算出来的。</param>
/// <param name="OAuth">正在进行的登录的最新进度(授权页由控制端打开);断开后清掉(未知)。</param>
public sealed record DeviceProviders(
    bool Known,
    long Version,
    ImmutableArray<ProviderDto> Providers,
    ImmutableArray<ModelAliasDto> Aliases,
    ImmutableArray<ProviderProblemDto> Problems,
    ImmutableDictionary<string, ImmutableArray<ProviderAccountDto>> Accounts,
    ImmutableArray<ModelOptionDto> Options,
    (long Providers, long Catalog) OptionsFor,
    OAuthProgressDto? OAuth)
{
    public static readonly DeviceProviders Unknown = new(
        false, 0, [], [], [], ImmutableDictionary<string, ImmutableArray<ProviderAccountDto>>.Empty, [], (0, 0), null);

    public ImmutableArray<ProviderAccountDto> AccountsOf(string providerId) => Accounts.GetValueOrDefault(providerId, []);
}

public sealed record ProvidersMirror(ImmutableDictionary<string, DeviceProviders> Devices)
{
    public static readonly ProvidersMirror Empty = new(ImmutableDictionary<string, DeviceProviders>.Empty);

    public DeviceProviders Of(string deviceId) => Devices.GetValueOrDefault(deviceId) ?? DeviceProviders.Unknown;
}

/// <summary>
/// 目录 / 供应商镜像的折叠规则(纯函数)。全量状态,收到不更旧的就整体替换;重放的旧事件不能把状态改回旧值(规则 6);
/// 设备断开标为未知(规则 7)。
/// </summary>
public static class ConfigReducer
{
    // ───────────────────────── 目录 ─────────────────────────

    public static CatalogMirror ApplyCatalog(CatalogMirror mirror, string deviceId, CatalogTopicState state)
    {
        var current = mirror.Of(deviceId);
        if (current.Known && state.Status.Version < current.Status.Version)
        {
            return mirror;
        }

        // 版本变了,旧版本的目录供应商列表作废(等重拉);版本没变(只是状态 / 覆盖更新)沿用。
        var keepProviders = current.ProvidersVersion == state.Status.Version;
        return mirror with
        {
            Devices = mirror.Devices.SetItem(deviceId, current with
            {
                Known = true,
                Status = state.Status,
                Overrides = [.. state.Overrides],
                Providers = keepProviders ? current.Providers : [],
                ProvidersVersion = keepProviders ? current.ProvidersVersion : 0,
            }),
        };
    }

    public static CatalogMirror ApplyCatalogProviders(CatalogMirror mirror, string deviceId, CatalogProvidersResult result)
    {
        var current = mirror.Of(deviceId);
        // 只收和当前目录版本一致(或更新)的:拉取期间目录又变了,这份已经过期,下一次重拉会补上。
        return result.Version < current.Status.Version || result.Version < current.ProvidersVersion
            ? mirror
            : mirror with { Devices = mirror.Devices.SetItem(deviceId, current with { Providers = [.. result.Providers], ProvidersVersion = result.Version }) };
    }

    public static CatalogMirror MarkCatalogUnknown(CatalogMirror mirror, string deviceId) =>
        mirror.Devices.TryGetValue(deviceId, out var current) && current.Known
            ? mirror with { Devices = mirror.Devices.SetItem(deviceId, current with { Known = false }) }
            : mirror;

    // ───────────────────────── 供应商 ─────────────────────────

    public static ProvidersMirror ApplyProviders(ProvidersMirror mirror, string deviceId, ProvidersTopicState state)
    {
        var current = mirror.Of(deviceId);
        if (current.Known && state.Version < current.Version)
        {
            return mirror;
        }

        return mirror with
        {
            Devices = mirror.Devices.SetItem(deviceId, current with
            {
                Known = true,
                Version = state.Version,
                Providers = [.. state.Providers],
                Aliases = [.. state.Aliases],
                Problems = [.. state.Problems],
                Accounts = state.Accounts.ToImmutableDictionary(a => a.ProviderId, a => ImmutableArray.CreateRange(a.Accounts), StringComparer.Ordinal),
            }),
        };
    }

    /// <summary>一家供应商的账号变了(这一家的全量,整体替换)。</summary>
    public static ProvidersMirror ApplyAccounts(ProvidersMirror mirror, string deviceId, ProviderAccountsDto accounts)
    {
        var current = mirror.Of(deviceId);
        return current.Known
            ? mirror with { Devices = mirror.Devices.SetItem(deviceId, current with { Accounts = current.Accounts.SetItem(accounts.ProviderId, [.. accounts.Accounts]) }) }
            : mirror;
    }

    public static ProvidersMirror ApplyOptions(ProvidersMirror mirror, string deviceId, ModelOptionsResult options, long providersVersion, long catalogVersion)
    {
        var current = mirror.Of(deviceId);
        return mirror with { Devices = mirror.Devices.SetItem(deviceId, current with { Options = [.. options.Options], OptionsFor = (providersVersion, catalogVersion) }) };
    }

    public static ProvidersMirror ApplyOAuth(ProvidersMirror mirror, string deviceId, OAuthProgressDto? progress)
    {
        var current = mirror.Of(deviceId);
        // 终态进度(成功 / 失败 / 取消)到达后这一轮登录结束:Store 里不留着它(界面据终态收尾,不据 Store 残留画「还在等」)。
        var next = progress is null || progress.Phase != OAuthPhases.Waiting ? null : progress;
        return Equals(current.OAuth, next) ? mirror : mirror with { Devices = mirror.Devices.SetItem(deviceId, current with { OAuth = next }) };
    }

    public static ProvidersMirror MarkProvidersUnknown(ProvidersMirror mirror, string deviceId) =>
        mirror.Devices.TryGetValue(deviceId, out var current) && (current.Known || current.OAuth is not null)
            ? mirror with { Devices = mirror.Devices.SetItem(deviceId, current with { Known = false, OAuth = null }) }
            : mirror;
}
