using PolyType;

namespace Ncw.Protocol.Methods;

// 模型目录的线上类型与方法表(docs/design/09 §8.4,02 A8;功能盘点 10 的 `model` / `modelCatalog` 并入 `catalog.*`)。
// 真源在 Agent 的 ModelCatalogService:控制端只通过协议读取,不自己去拉 models.dev。读取 viewer 可;写入(刷新、覆盖、删除 / 恢复、设置)默认 owner。
// 全部是不可变记录,MessagePack 字符串键,未知字段忽略;枚举一律用小写字符串。时间是 Unix 毫秒。价格是每百万 token 的绝对价格(double,只用于展示和用户覆盖输入)。

// ───────────────────────── 目录里的模型 ─────────────────────────

public sealed record CatalogCapabilitiesDto(
    bool ToolCall,
    bool Reasoning,
    bool Attachment,
    bool StructuredOutput,
    string[] InputModalities,
    string[] OutputModalities);

/// <summary>一套费率(每百万 token 的绝对价格)。缺省的缓存价 = 这一类 token 没有价(费用算不齐,不当 0)。</summary>
public sealed record PriceRatesDto(double Input, double Output, double? CacheRead = null, double? CacheWrite = null, double? CacheWrite1h = null, double? PerCall = null);

/// <summary>一档价格。<see cref="UpToInputTokens"/> 是该档输入 token 上界(含);最后一档为 null = 无上界。</summary>
public sealed record PriceTierDto(long? UpToInputTokens, PriceRatesDto Rates);

/// <param name="Currency"><c>usd</c> / <c>cny</c>。</param>
public sealed record ModelPriceDto(string Currency, PriceTierDto[] Tiers, string Source);

/// <summary>目录里的一个模型(已套用用户覆盖)。<see cref="ProviderId"/> 是 models.dev 的供应商 id,不是用户配置的供应商 id。</summary>
/// <param name="ReasoningEfforts">接受的推理强度档位(<c>none</c> / <c>minimal</c> / <c>low</c> / <c>medium</c> / <c>high</c> / <c>xhigh</c> / <c>max</c>);null = 目录没有声明。</param>
public sealed record CatalogModelDto(
    string ProviderId,
    string Id,
    string Name,
    string? Family,
    int ContextWindow,
    int MaxOutputTokens,
    CatalogCapabilitiesDto Capabilities,
    string[]? ReasoningEfforts,
    ModelPriceDto? Price,
    string? Status,
    string? ReleaseDate,
    string? LastUpdated,
    bool Overridden);

/// <summary>目录里的一个供应商(不含模型,模型用 <c>catalog.query</c> 按 <c>providerId</c> 取)。</summary>
public sealed record CatalogProviderDto(string Id, string Name, string? Api, string? Doc, string[] Env, int ModelCount);

/// <summary>结构化的错误(没有界面文字):<see cref="Key"/> 由控制端翻译,<see cref="Detail"/> 是英文诊断。</summary>
public sealed record CatalogErrorDto(string Key, string Detail, long At);

/// <summary>最近一次拉取的状态(设置页「上次更新于……」和最近一次错误)。</summary>
/// <param name="Source"><c>seed</c> / <c>cache</c> / <c>remote</c>。</param>
public sealed record CatalogStatusDto(
    long Version,
    string Source,
    long? FetchedAt,
    long? LastCheckedAt,
    long? NextCheckAt,
    int ModelCount,
    int ProviderCount,
    CatalogErrorDto? LastError);

public sealed record PriceOverrideDto(string Currency, double Input, double Output, double? CacheRead = null, double? CacheWrite = null, double? CacheWrite1h = null);

/// <summary>
/// 用户对一个模型的本地覆盖。<see cref="ProviderId"/> 为空 = 对所有供应商上这个模型 id 生效;非空 = 同时匹配 models.dev 的供应商 id 和用户配置的供应商 id。
/// 字段为空 = 不覆盖(跟随同步数据);<see cref="Deleted"/> = 从目录里隐藏这个模型。
/// </summary>
public sealed record ModelOverrideDto(
    string? ProviderId,
    string ModelId,
    string? DisplayName = null,
    int? ContextWindow = null,
    int? MaxOutputTokens = null,
    bool? ToolCall = null,
    bool? Reasoning = null,
    bool? Vision = null,
    PriceOverrideDto? Price = null,
    bool Deleted = false);

/// <summary>
/// <c>catalog</c> 主题的状态,同时是 <c>catalog.changed</c> 事件的数据(首帧与后续广播同一个类型,AGENTS.md §3 规则 5)。
/// 目录本身很大(几千个模型),所以主题只带版本、状态和用户覆盖;<see cref="CatalogStatusDto.Version"/> 变了,控制端按需用 <c>catalog.query</c> 重新拉。
/// </summary>
public sealed record CatalogTopicState(CatalogStatusDto Status, ModelOverrideDto[] Overrides);

public static class CatalogEvents
{
    /// <summary>目录、状态或用户覆盖变了(数据是新的 <see cref="CatalogTopicState"/>,全量)。</summary>
    public const string Changed = "catalog.changed";
}

public static class CatalogRefreshOutcomes
{
    public const string Updated = "updated";
    public const string NotModified = "notModified";
    public const string Rejected = "rejected";
    public const string Failed = "failed";
}

// ───────────────────────── 参数与结果 ─────────────────────────

/// <summary>目录查询:字段都是可选的合取条件。<see cref="Limit"/> 缺省 500(上限 5000):目录有几千个模型,整份进一帧会超过帧上限。</summary>
public sealed record CatalogQueryParams(
    string? ProviderId = null,
    string? ModelId = null,
    string? Text = null,
    bool? ToolCall = null,
    bool? Reasoning = null,
    bool? Vision = null,
    bool? ChatOnly = null,
    int? MinContextWindow = null,
    int Offset = 0,
    int Limit = 0);

/// <param name="Total">匹配的总数(不受 <c>Offset</c> / <c>Limit</c> 影响)。</param>
public sealed record CatalogQueryResult(long Version, int Total, CatalogModelDto[] Models);

public sealed record CatalogProvidersResult(long Version, CatalogProviderDto[] Providers);

public sealed record CatalogGetParams(string ProviderId, string ModelId);

public sealed record CatalogGetResult(long Version, CatalogModelDto? Model);

/// <summary>指向一个覆盖 / 一个模型(<c>catalog.override.remove</c>、<c>catalog.model.delete</c>、<c>catalog.model.restore</c>)。</summary>
public sealed record CatalogModelRefParams(string? ProviderId, string ModelId);

/// <param name="Outcome"><see cref="CatalogRefreshOutcomes"/> 之一。</param>
public sealed record CatalogRefreshResultDto(string Outcome, int ModelCount, CatalogErrorDto? Error);

public sealed record CatalogOverridesResult(ModelOverrideDto[] Overrides);

/// <summary>目录同步的设置(设置页「立即更新」旁边的那几项)。<see cref="IntervalMinutes"/> 夹在 15 分钟到 7 天之间。</summary>
public sealed record CatalogSettingsDto(bool AutoUpdate, int IntervalMinutes);

/// <summary>目录方法表(能力 <see cref="Capabilities.Catalog"/>,承载 本机 + 远程):读取 viewer 可,写入 owner。</summary>
public static class CatalogMethods
{
    public static readonly MethodDef<CatalogQueryParams, CatalogQueryResult> Query = Q<CatalogQueryParams, CatalogQueryResult>("catalog.query", Role.Viewer);

    public static readonly MethodDef<Unit, CatalogProvidersResult> Providers = Q<Unit, CatalogProvidersResult>("catalog.providers", Role.Viewer);

    public static readonly MethodDef<CatalogGetParams, CatalogGetResult> Get = Q<CatalogGetParams, CatalogGetResult>("catalog.get", Role.Viewer);

    public static readonly MethodDef<Unit, CatalogStatusDto> Status = Q<Unit, CatalogStatusDto>("catalog.status", Role.Viewer);

    /// <summary>立即检查一次(设置页的「立即更新」)。永不报错:失败 / 被拒的结果在 <see cref="CatalogRefreshResultDto"/> 里。</summary>
    public static readonly MethodDef<Unit, CatalogRefreshResultDto> Refresh = C<Unit, CatalogRefreshResultDto>("catalog.refresh", Role.Owner);

    /// <summary>写入(或替换)一条覆盖;内容为空等于撤销。返回新的 <see cref="CatalogTopicState"/>。</summary>
    public static readonly MethodDef<ModelOverrideDto, CatalogTopicState> OverrideSet = C<ModelOverrideDto, CatalogTopicState>("catalog.override.set", Role.Owner);

    public static readonly MethodDef<CatalogModelRefParams, CatalogTopicState> OverrideRemove = C<CatalogModelRefParams, CatalogTopicState>("catalog.override.remove", Role.Owner);

    /// <summary>从目录里隐藏一个模型(同步数据再来也不会回来;保留它已有的其他覆盖,恢复时一起回来)。</summary>
    public static readonly MethodDef<CatalogModelRefParams, CatalogTopicState> ModelDelete = C<CatalogModelRefParams, CatalogTopicState>("catalog.model.delete", Role.Owner);

    public static readonly MethodDef<CatalogModelRefParams, CatalogTopicState> ModelRestore = C<CatalogModelRefParams, CatalogTopicState>("catalog.model.restore", Role.Owner);

    public static readonly MethodDef<Unit, CatalogOverridesResult> OverridesList = Q<Unit, CatalogOverridesResult>("catalog.overrides.list", Role.Viewer);

    public static readonly MethodDef<Unit, CatalogSettingsDto> SettingsGet = Q<Unit, CatalogSettingsDto>("catalog.settings.get", Role.Viewer);

    public static readonly MethodDef<CatalogSettingsDto, CatalogSettingsDto> SettingsSet = C<CatalogSettingsDto, CatalogSettingsDto>("catalog.settings.set", Role.Owner);

    /// <summary>目录状态与用户覆盖(快照和 <see cref="CatalogEvents.Changed"/> 同一个类型)。</summary>
    public static readonly TopicDef<CatalogTopicState> Topic = new("catalog", PayloadShapes.GetShape<CatalogTopicState>());

    public static readonly IReadOnlyList<MethodDef> All =
    [
        Query, Providers, Get, Status, Refresh, OverrideSet, OverrideRemove, ModelDelete, ModelRestore, OverridesList, SettingsGet, SettingsSet,
    ];

    private static MethodDef<TP, TR> Q<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Query, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = Capabilities.Catalog };

    private static MethodDef<TP, TR> C<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Command, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = Capabilities.Catalog };
}

[GenerateShapeFor<CatalogCapabilitiesDto>]
[GenerateShapeFor<PriceRatesDto>]
[GenerateShapeFor<PriceTierDto>]
[GenerateShapeFor<ModelPriceDto>]
[GenerateShapeFor<CatalogModelDto>]
[GenerateShapeFor<CatalogProviderDto>]
[GenerateShapeFor<CatalogErrorDto>]
[GenerateShapeFor<CatalogStatusDto>]
[GenerateShapeFor<PriceOverrideDto>]
[GenerateShapeFor<ModelOverrideDto>]
[GenerateShapeFor<CatalogTopicState>]
[GenerateShapeFor<CatalogQueryParams>]
[GenerateShapeFor<CatalogQueryResult>]
[GenerateShapeFor<CatalogProvidersResult>]
[GenerateShapeFor<CatalogGetParams>]
[GenerateShapeFor<CatalogGetResult>]
[GenerateShapeFor<CatalogModelRefParams>]
[GenerateShapeFor<CatalogRefreshResultDto>]
[GenerateShapeFor<CatalogOverridesResult>]
[GenerateShapeFor<CatalogSettingsDto>]
public sealed partial class PayloadShapes;
