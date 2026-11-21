using Ncw.Core.Domain;
using PolyType;

namespace Ncw.Protocol.Methods;

// 供应商的线上类型与方法表(docs/design/09 §8.4,功能盘点 10 的 `provider` / `providers`;搜索提供方 `websearch` 以后并入同一能力)。
// 真源在 Agent 的 ProviderService。读取供应商配置、模型列表、账号列表 member 可;写入、凭据、OAuth 默认 owner;
// `providers.credential.reveal` 仅 owner 且仅本机通道。★ 凭据 / 令牌永远不出现在任何 DTO 或事件里:
// 列表只带「有没有 key」,凭据摘要只带后四位和过期信息;明文只经 reveal 单次返回(以及 `providers.credential.set` / `providers.oauth.submitCode` 的请求参数,方向是控制端 → Agent)。
// 业务失败的结构化原因在 `error.details`(<see cref="ProviderErrorDto"/>),控制端按 key 翻译。

// ───────────────────────── 供应商与别名 ─────────────────────────

/// <summary>一个供应商(不含凭据)。</summary>
/// <param name="Protocol">文本:<c>anthropic</c> / <c>openai-chat</c> / <c>openai-responses</c>;图片:<c>openai-images</c> / <c>volcengine-images</c>;视频:<c>openai-videos</c> / <c>volcengine-videos</c>;音频:<c>openai-audio</c>。</param>
/// <param name="CacheTtl"><c>5m</c> / <c>1h</c>(Anthropic 提示词缓存的生存期)。</param>
/// <param name="ProtocolOptionsJson">协议选项(JSON 对象文本);没有为 null。</param>
/// <param name="HasCredential">有没有配过 key / 登录过(只是个布尔,绝不带任何凭据内容)。</param>
/// <param name="OAuthIssuer">走账号登录的供应商给出 issuer;API Key 供应商为 null。</param>
/// <param name="Kind">供应商类型:<c>text</c> / <c>image</c> / <c>video</c> / <c>audio</c>(缺省 <c>text</c>)。</param>
public sealed record ProviderDto(
    string Id,
    string Name,
    string Protocol,
    string BaseUrl,
    int Priority,
    bool Enabled,
    string CacheTtl,
    string? ProtocolOptionsJson,
    bool HasCredential,
    string? OAuthIssuer,
    string Kind = ProviderKinds.Text);

/// <summary>供应商下的一个模型别名绑定(主键 <c>(ProviderId, Alias)</c>)。</summary>
/// <param name="CatalogOverrides">归供应商管的字段名(列在里面的字段不再跟随目录,其余跟随)。</param>
/// <param name="ThinkingConfigJson">存着的思考配置(JSON 文本);没有为 null。</param>
/// <param name="ReasoningEffortsJson">存着的推理强度档位(JSON 数组文本);没有为 null。</param>
public sealed record ModelAliasDto(
    string Alias,
    string ProviderId,
    string UpstreamModel,
    int ContextWindow,
    int MaxOutputTokens,
    string? DisplayName,
    string? ProtocolOverride,
    int? Priority,
    string? Modality,
    bool Enabled,
    bool SupportsImages,
    string[] CatalogOverrides,
    string? ThinkingConfigJson,
    string? ReasoningEffortsJson);

/// <summary>读 <c>providers.json</c> 时被跳过的坏条目(设置页的诊断)。<see cref="Key"/> 由控制端翻译。</summary>
public sealed record ProviderProblemDto(string? ProviderId, string Key, string Detail);

/// <summary>登录态里可以给控制端看的那部分(一个 token 字符都没有)。<see cref="ExpiresAt"/> 为 null = 过期时间未知,不是永不过期。</summary>
public sealed record CredentialAuthDto(string Issuer, string AccountId, string? Email, string? PlanType, long? ExpiresAt, bool Expired, bool NeedsReauth);

/// <summary>凭据摘要(设置页列表只拿这个)。<see cref="Last4"/> 只有 API Key 有;<see cref="SignatureScheme"/> 是 <c>tencentTc3</c> / <c>awsSigV4</c>。</summary>
public sealed record CredentialInfoDto(bool HasKey, string? Last4, CredentialAuthDto? Auth, string? SignatureScheme, string? AccessKeyId);

/// <param name="Source"><c>http429</c> / <c>quotaExhausted</c> / <c>manual</c>。</param>
public sealed record AccountLimitDto(long Until, long Since, string Source, string Reason);

public sealed record QuotaWindowDto(double UsedPercent, int WindowMinutes, long ResetsAt);

public sealed record QuotaDto(QuotaWindowDto? Primary, QuotaWindowDto? Secondary, long CapturedAt);

/// <summary>账号池里的一个登录身份(行 + 凭据摘要)。<see cref="Auth"/> 读密文失败时缺席,其余字段照常下发。</summary>
public sealed record ProviderAccountDto(
    string Id,
    string ProviderId,
    string Issuer,
    string? Label,
    int Order,
    bool Enabled,
    bool Current,
    bool NeedsReauth,
    AccountLimitDto? Limit,
    QuotaDto? Quota,
    long CreatedAt,
    long UpdatedAt,
    CredentialAuthDto? Auth);

public sealed record ProviderAccountsDto(string ProviderId, ProviderAccountDto[] Accounts);

/// <summary>
/// <c>providers</c> 主题的状态,同时是 <c>providers.changed</c> 事件的数据(全量,首帧与后续广播同一个类型,AGENTS.md §3 规则 5)。
/// <see cref="Version"/> 单调递增,控制端只收更新的那份。<see cref="Accounts"/> 只含走账号登录的供应商。
/// </summary>
public sealed record ProvidersTopicState(
    long Version,
    ProviderDto[] Providers,
    ModelAliasDto[] Aliases,
    ProviderProblemDto[] Problems,
    ProviderAccountsDto[] Accounts);

public static class ProvidersEvents
{
    /// <summary>供应商或别名变了(自己的写入,或 providers.json 被外部修改并热加载)。数据是新的 <see cref="ProvidersTopicState"/>(全量)。</summary>
    public const string Changed = "providers.changed";

    /// <summary>一家供应商的账号变了(限流 / 额度 / 登录态 / 增删)。数据是 <see cref="ProviderAccountsDto"/>(这一家的全量,整体替换)。</summary>
    public const string AccountsChanged = "providers.accounts.changed";

    /// <summary><c>providers.oauth</c> 主题上的登录进度。数据是 <see cref="OAuthProgressDto"/>。</summary>
    public const string OAuthProgress = "providers.oauth.progress";
}

// ───────────────────────── OAuth ─────────────────────────

public static class OAuthPhases
{
    /// <summary>授权页已交给控制端打开 / 配对码已展示,正在等用户。</summary>
    public const string Waiting = "waiting";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// 登录进度。授权页由<b>控制端</b>打开(<see cref="AuthorizeUrl"/> 带着 state,经协议下发);设备码流程带 <see cref="UserCode"/> / <see cref="VerificationUrl"/>。
/// <see cref="NeedsPastedCode"/> 由授权方式现算:控制端据它决定 Waiting 阶段画输入框还是 spinner。终态(succeeded / failed / cancelled)后这一轮登录结束。
/// </summary>
public sealed record OAuthProgressDto(
    string ProviderId,
    string Phase,
    string? AuthorizeUrl = null,
    string? UserCode = null,
    string? VerificationUrl = null,
    int? ExpiresInSeconds = null,
    bool NeedsPastedCode = false,
    string? ErrorKey = null,
    string? Detail = null);

/// <summary><c>providers.oauth</c> 主题的状态:当前正在进行的那一轮登录的最新进度;没有登录在进行为 null。只给 owner(授权 URL 带 state)。</summary>
public sealed record OAuthTopicState(OAuthProgressDto? Current);

// ───────────────────────── 参数与结果 ─────────────────────────

/// <summary>业务失败的结构化原因(放在 <c>error.details</c>):<see cref="Key"/> 是 <c>providers.*</c> 的稳定 key,参数供翻译用,不含任何凭据。</summary>
public sealed record ProviderErrorDto(string Key, Dictionary<string, string> Params);

public sealed record ProviderRefParams(string ProviderId);

/// <summary><c>providers.upsert</c>:新建或更新。<see cref="Priority"/> / <see cref="Enabled"/> 缺省 = 50 / 启用(更新时也按缺省值写,不是「不改」)。<see cref="Kind"/> 缺省时由 <see cref="Protocol"/> 推断。凭据 ref 由 id 派生,不接受调用方给的值。</summary>
public sealed record ProvidersUpsertParams(
    string Id,
    string Name,
    string Protocol,
    string BaseUrl,
    int? Priority = null,
    bool? Enabled = null,
    string? ProtocolOptionsJson = null,
    string? Kind = null);

/// <summary><c>providers.credential.set</c>:存一把 API Key。方向是控制端 → Agent;Agent 只回摘要。</summary>
public sealed record CredentialSetParams(string ProviderId, string ApiKey);

/// <summary><c>providers.credential.reveal</c> 的结果:明文只在这一个方法里单次返回(仅 owner、仅本机通道)。<see cref="Kind"/> 是 <c>apiKey</c> / <c>oauth</c> / <c>signature</c>,只填对应的字段。</summary>
public sealed record RevealedCredentialDto(
    string Kind,
    string? ApiKey = null,
    string? AccessToken = null,
    string? RefreshToken = null,
    string? Scheme = null,
    string? AccessKeyId = null,
    string? SecretKey = null,
    string? Region = null,
    string? SessionToken = null);

/// <summary>上游报上来的一个模型。这不是配置,是上游报上来的事实(模型列表端点不提供窗口、能力)。</summary>
public sealed record FetchedModelDto(string Id, string? DisplayName);

public sealed record FetchedModelsResult(FetchedModelDto[] Models);

public sealed record SetAliasesParams(string ProviderId, string[] UpstreamModels);

public sealed record ModelAliasesResult(ModelAliasDto[] Aliases);

/// <summary>逐条编辑别名:只改给了值的字段;<see cref="ClearProtocolOverride"/> 显式清掉协议覆盖(区别于「没给」)。JSON 字段是 JSON 文本(写入后归供应商,不再跟随目录)。</summary>
public sealed record ModelUpdateParams(
    string ProviderId,
    string Alias,
    string? DisplayName = null,
    int? ContextWindow = null,
    int? MaxOutputTokens = null,
    bool? Enabled = null,
    int? Priority = null,
    string? ProtocolOverride = null,
    bool ClearProtocolOverride = false,
    bool? Vision = null,
    string? ThinkingConfigJson = null,
    string? ReasoningEffortsJson = null,
    string? RequestAdapterJson = null);

public sealed record ModelRenameParams(string ProviderId, string Alias, string NextAlias);

public sealed record ModelRefParams(string ProviderId, string Alias);

public sealed record ModelsListParams(bool ChatOnly = false);

/// <summary>模型选择器里的一行(用户配过的全部模型,含停用的:界面应当显示并标成不可用,否则用户会以为自己的配置丢了)。</summary>
/// <param name="ThinkingMode">思考模式(<c>effort</c> / <c>budget</c> / <c>toggle</c> 等,控制端不认识的值按未知处理);null = 不支持。</param>
/// <param name="Kind">所属供应商类型:<c>text</c> / <c>image</c> / <c>video</c> / <c>audio</c>(缺省 <c>text</c>)。</param>
public sealed record ModelOptionDto(
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
    string? ThinkingMode,
    string[]? ReasoningEfforts,
    string Protocol,
    string Kind = ProviderKinds.Text);

public sealed record ModelOptionsResult(ModelOptionDto[] Options);

/// <summary>连通性测试结果。<see cref="Ok"/> = 上游认了这把凭据;<see cref="Status"/> 为空 = 根本没连上。<see cref="Detail"/> 已脱敏。</summary>
public sealed record ProviderTestResultDto(bool Ok, int? LatencyMs, int? Status, string? ErrorKey, string? Detail);

public sealed record AccountRefParams(string ProviderId, string AccountId);

public sealed record AccountsResult(ProviderAccountDto[] Accounts);

public sealed record AccountSetEnabledParams(string ProviderId, string AccountId, bool Enabled);

public sealed record AccountSetLabelParams(string ProviderId, string AccountId, string Label);

public sealed record AccountsReorderParams(string ProviderId, string[] AccountIds);

/// <summary>手动停用到某个时刻(落 <c>manual</c> 闸门,只有用户能解除)。</summary>
public sealed record AccountPauseParams(string ProviderId, string AccountId, long UntilMs);

/// <summary><c>providers.oauth.submitCode</c>:把授权页给的回调码交回 Agent(方向是控制端 → Agent)。</summary>
public sealed record OAuthSubmitCodeParams(string ProviderId, string Code);

/// <summary>解析一个「模型 + 可选供应商」会落在哪条绑定上(和路由同一段规则)。</summary>
public sealed record ProvidersResolveParams(string Alias, string? ProviderId = null);

public static class ModelResolutionIssues
{
    public const string None = "none";
    public const string UnknownAlias = "unknownAlias";
    public const string PinnedProviderMissing = "pinnedProviderMissing";
    public const string PinnedProviderDisabled = "pinnedProviderDisabled";
    public const string PinnedModelMissing = "pinnedModelMissing";
    public const string NoEnabledProvider = "noEnabledProvider";
}

/// <param name="Issue"><see cref="ModelResolutionIssues"/> 之一;<c>none</c> 时 <see cref="Found"/> 为真,下面的字段有值。</param>
/// <param name="Price">目录里这个模型的有效价格(含用户覆盖);查不到为 null(费用是「未知」,不是 0)。</param>
public sealed record ModelResolutionDto(
    bool Found,
    string Issue,
    string? ProviderId,
    string? Alias,
    string? UpstreamModel,
    int ContextWindow,
    int MaxOutputTokens,
    bool SupportsImages,
    string? Protocol,
    string? DisplayName,
    ModelPriceDto? Price);

/// <summary>供应商方法表(能力 <see cref="Capabilities.Providers"/>,承载 本机 + 远程;<c>credential.reveal</c> 仅本机)。</summary>
public static class ProvidersMethods
{
    // ── 读(member) ──

    public static readonly MethodDef<Unit, ProvidersTopicState> List = Q<Unit, ProvidersTopicState>("providers.list", Role.Member);

    public static readonly MethodDef<ModelsListParams, ModelOptionsResult> ModelsList = Q<ModelsListParams, ModelOptionsResult>("providers.models.list", Role.Member);

    public static readonly MethodDef<ProvidersResolveParams, ModelResolutionDto> Resolve = Q<ProvidersResolveParams, ModelResolutionDto>("providers.resolve", Role.Member);

    public static readonly MethodDef<ProviderRefParams, AccountsResult> AccountsList = Q<ProviderRefParams, AccountsResult>("providers.accounts.list", Role.Member);

    // ── 供应商(owner) ──

    public static readonly MethodDef<ProvidersUpsertParams, ProviderDto> Upsert = C<ProvidersUpsertParams, ProviderDto>("providers.upsert", Role.Owner);

    public static readonly MethodDef<ProviderRefParams, Unit> Remove = C<ProviderRefParams, Unit>("providers.remove", Role.Owner);

    public static readonly MethodDef<ProviderRefParams, ProviderTestResultDto> Test = Q<ProviderRefParams, ProviderTestResultDto>("providers.test", Role.Owner);

    // ── 凭据(owner) ──

    public static readonly MethodDef<CredentialSetParams, CredentialInfoDto> CredentialSet = C<CredentialSetParams, CredentialInfoDto>("providers.credential.set", Role.Owner);

    public static readonly MethodDef<ProviderRefParams, CredentialInfoDto> CredentialInfo = Q<ProviderRefParams, CredentialInfoDto>("providers.credential.info", Role.Owner);

    /// <summary>显式查看凭据明文(单次返回,不进任何列表 / 广播)。只允许本机通道的 owner。</summary>
    public static readonly MethodDef<ProviderRefParams, RevealedCredentialDto> CredentialReveal =
        new("providers.credential.reveal", MethodKind.Query, Role.Owner, Transports.Local, PayloadShapes.GetShape<ProviderRefParams>(), PayloadShapes.GetShape<RevealedCredentialDto>())
        {
            Capability = Capabilities.Providers,
        };

    public static readonly MethodDef<ProviderRefParams, Unit> CredentialRemove = C<ProviderRefParams, Unit>("providers.credential.remove", Role.Owner);

    // ── 模型别名(owner) ──

    /// <summary>从上游拉「这家有哪些模型」(用这家的凭据发一次请求,不改任何状态)。</summary>
    public static readonly MethodDef<ProviderRefParams, FetchedModelsResult> ModelsFetch = Q<ProviderRefParams, FetchedModelsResult>("providers.models.fetch", Role.Owner);

    /// <summary>整表替换这家的模型别名:调用完之后这家的别名 = 给的那些,一个不多一个不少;已有的整行留着。</summary>
    public static readonly MethodDef<SetAliasesParams, ModelAliasesResult> ModelsSetAliases = C<SetAliasesParams, ModelAliasesResult>("providers.models.setAliases", Role.Owner);

    public static readonly MethodDef<ModelUpdateParams, ModelAliasDto> ModelsUpdate = C<ModelUpdateParams, ModelAliasDto>("providers.models.update", Role.Owner);

    public static readonly MethodDef<ModelRenameParams, ModelAliasDto> ModelsRename = C<ModelRenameParams, ModelAliasDto>("providers.models.rename", Role.Owner);

    public static readonly MethodDef<ModelRefParams, Unit> ModelsRemove = C<ModelRefParams, Unit>("providers.models.remove", Role.Owner);

    // ── 账号池(owner) ──

    public static readonly MethodDef<AccountRefParams, AccountsResult> AccountsRemove = C<AccountRefParams, AccountsResult>("providers.accounts.remove", Role.Owner);

    public static readonly MethodDef<AccountSetEnabledParams, AccountsResult> AccountsSetEnabled = C<AccountSetEnabledParams, AccountsResult>("providers.accounts.setEnabled", Role.Owner);

    public static readonly MethodDef<AccountSetLabelParams, AccountsResult> AccountsSetLabel = C<AccountSetLabelParams, AccountsResult>("providers.accounts.setLabel", Role.Owner);

    public static readonly MethodDef<AccountRefParams, AccountsResult> AccountsSetCurrent = C<AccountRefParams, AccountsResult>("providers.accounts.setCurrent", Role.Owner);

    public static readonly MethodDef<AccountsReorderParams, AccountsResult> AccountsReorder = C<AccountsReorderParams, AccountsResult>("providers.accounts.reorder", Role.Owner);

    /// <summary>「立即解除限流」:只有用户知道是刚升级了套餐还是我们判早了,所以不做确认请求。</summary>
    public static readonly MethodDef<AccountRefParams, AccountsResult> AccountsClearLimit = C<AccountRefParams, AccountsResult>("providers.accounts.clearLimit", Role.Owner);

    public static readonly MethodDef<AccountPauseParams, AccountsResult> AccountsPause = C<AccountPauseParams, AccountsResult>("providers.accounts.pause", Role.Owner);

    // ── OAuth(owner)。start / addAccount / reauth 在登录结束(成功、失败、取消)时才返回;进度走 providers.oauth 主题。 ──

    /// <summary>把这家登录成(老路径语义:建账号行并成为当前账号)。</summary>
    public static readonly MethodDef<ProviderRefParams, CredentialInfoDto> OAuthStart = C<ProviderRefParams, CredentialInfoDto>("providers.oauth.start", Role.Owner);

    public static readonly MethodDef<ProviderRefParams, AccountsResult> OAuthAddAccount = C<ProviderRefParams, AccountsResult>("providers.oauth.addAccount", Role.Owner);

    public static readonly MethodDef<AccountRefParams, AccountsResult> OAuthReauth = C<AccountRefParams, AccountsResult>("providers.oauth.reauth", Role.Owner);

    /// <summary>退出登录:退出的是「当前账号」,不是这家的全部账号。</summary>
    public static readonly MethodDef<ProviderRefParams, CredentialInfoDto> OAuthSignOut = C<ProviderRefParams, CredentialInfoDto>("providers.oauth.signOut", Role.Owner);

    public static readonly MethodDef<ProviderRefParams, Unit> OAuthCancel = C<ProviderRefParams, Unit>("providers.oauth.cancel", Role.Owner);

    public static readonly MethodDef<OAuthSubmitCodeParams, Unit> OAuthSubmitCode = C<OAuthSubmitCodeParams, Unit>("providers.oauth.submitCode", Role.Owner);

    // ── 主题 ──

    /// <summary>供应商、别名与账号(快照和 <see cref="ProvidersEvents.Changed"/> 同一个类型)。member 可订阅:不含任何凭据。</summary>
    public static readonly TopicDef<ProvidersTopicState> Topic = new("providers", PayloadShapes.GetShape<ProvidersTopicState>(), Role.Member);

    /// <summary>登录进度。只有 owner:授权 URL 带 state。</summary>
    public static readonly TopicDef<OAuthTopicState> OAuthTopic = new("providers.oauth", PayloadShapes.GetShape<OAuthTopicState>(), Role.Owner);

    public static readonly IReadOnlyList<MethodDef> All =
    [
        List, ModelsList, Resolve, AccountsList, Upsert, Remove, Test,
        CredentialSet, CredentialInfo, CredentialReveal, CredentialRemove,
        ModelsFetch, ModelsSetAliases, ModelsUpdate, ModelsRename, ModelsRemove,
        AccountsRemove, AccountsSetEnabled, AccountsSetLabel, AccountsSetCurrent, AccountsReorder, AccountsClearLimit, AccountsPause,
        OAuthStart, OAuthAddAccount, OAuthReauth, OAuthSignOut, OAuthCancel, OAuthSubmitCode,
    ];

    private static MethodDef<TP, TR> Q<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Query, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = Capabilities.Providers };

    private static MethodDef<TP, TR> C<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Command, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = Capabilities.Providers };
}

public static partial class Capabilities
{
    /// <summary>
    /// 供应商类型区分与媒体协议支持(图片 / 视频 / 音频供应商、按类型过滤上游模型、火山方舟图片与视频协议、四家内置托管供应商)。
    /// 相关 DTO 末尾追加的 <c>Kind</c> 均带默认值,旧版对端不带 <c>Kind</c> 的载荷照常解码。
    /// </summary>
    public const string ProviderMedia = "providers.media";
}

[GenerateShapeFor<ProviderDto>]
[GenerateShapeFor<ModelAliasDto>]
[GenerateShapeFor<ProviderProblemDto>]
[GenerateShapeFor<CredentialAuthDto>]
[GenerateShapeFor<CredentialInfoDto>]
[GenerateShapeFor<AccountLimitDto>]
[GenerateShapeFor<QuotaWindowDto>]
[GenerateShapeFor<QuotaDto>]
[GenerateShapeFor<ProviderAccountDto>]
[GenerateShapeFor<ProviderAccountsDto>]
[GenerateShapeFor<ProvidersTopicState>]
[GenerateShapeFor<OAuthProgressDto>]
[GenerateShapeFor<OAuthTopicState>]
[GenerateShapeFor<ProviderErrorDto>]
[GenerateShapeFor<ProviderRefParams>]
[GenerateShapeFor<ProvidersUpsertParams>]
[GenerateShapeFor<CredentialSetParams>]
[GenerateShapeFor<RevealedCredentialDto>]
[GenerateShapeFor<FetchedModelDto>]
[GenerateShapeFor<FetchedModelsResult>]
[GenerateShapeFor<SetAliasesParams>]
[GenerateShapeFor<ModelAliasesResult>]
[GenerateShapeFor<ModelUpdateParams>]
[GenerateShapeFor<ModelRenameParams>]
[GenerateShapeFor<ModelRefParams>]
[GenerateShapeFor<ModelsListParams>]
[GenerateShapeFor<ModelOptionDto>]
[GenerateShapeFor<ModelOptionsResult>]
[GenerateShapeFor<ProviderTestResultDto>]
[GenerateShapeFor<AccountRefParams>]
[GenerateShapeFor<AccountsResult>]
[GenerateShapeFor<AccountSetEnabledParams>]
[GenerateShapeFor<AccountSetLabelParams>]
[GenerateShapeFor<AccountsReorderParams>]
[GenerateShapeFor<AccountPauseParams>]
[GenerateShapeFor<OAuthSubmitCodeParams>]
[GenerateShapeFor<ProvidersResolveParams>]
[GenerateShapeFor<ModelResolutionDto>]
public sealed partial class PayloadShapes;
