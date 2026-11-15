using PolyType;

namespace Ncw.Protocol.Methods;

// 设置(docs/design/07 §3 三种作用域、09 §8.4;功能盘点 10 的 `settings` / `proxy` / `configSync` 落到设备的部分)。
// 作用域:设备 / 项目 → Agent(本文件);本应用(外观、快捷键、通知、账户)→ 客户端本地,不进协议。
// 真源在 Agent 的 `settings` 表(2.x 第 1 行 JSON,接管后原样可用;3.0 不认识的键写回时原样保留)和工作区 JSON 的 `settings` 段。
// 读取 member 起(不含任何凭据:代理密码经 NCK1 另存,只回「有没有」),写入 owner(改设备设置与权限模式不是 member 的事,09 §4)。
// `settings` 主题是全量权威(快照与 `settings.changed` 同一个类型,带单调的 `Rev`)。全是不可变记录,字符串键,未知字段忽略;枚举一律小写字符串。

/// <summary>「模型别名 + 供应商」成对(同一个别名可以挂在多家,光凭别名定不下发给谁)。<see cref="Model"/> 为空 = 没选 / 跟随默认;此时 <see cref="ProviderId"/> 无意义。</summary>
public sealed record ModelPickDto(string Model, string? ProviderId = null)
{
    public static readonly ModelPickDto None = new("");
}

/// <param name="Thinking"><c>inherit</c>(跟随本轮对话)或思考档位(<c>off</c> / <c>minimal</c> / <c>low</c> / <c>medium</c> / <c>high</c> / <c>xhigh</c> / <c>max</c> / <c>auto</c>)。</param>
public sealed record SubagentSettingsDto(ModelPickDto Model, string Thinking, int PerSessionLimit, int GlobalLimit);

/// <param name="Mode"><c>system</c> / <c>manual</c>。</param>
/// <param name="Scheme"><c>http</c> / <c>https</c> / <c>socks5</c> / <c>socks4</c>。</param>
/// <param name="Bypass">用户填的直连白名单,一行一条(内置那张表不存在这里)。</param>
/// <param name="HasPassword">代理密码在 NCK1 里存没存;密码本身永不出现在任何 DTO 里。</param>
public sealed record ProxySettingsDto(bool Enabled, string Mode, string Scheme, string Host, int Port, bool AuthEnabled, string AuthUser, string Bypass, bool HasPassword);

/// <summary>开放网关的开关(<c>gateway.*</c> 的密钥与地址是另一组方法)。默认关闭;<see cref="AllowRemote"/> 关 = 只绑回环地址。</summary>
public sealed record GatewaySettingsDto(bool Enabled, bool AllowRemote, int PreferredPort);

/// <param name="BackupFrequency"><c>manual</c> / <c>daily</c> / <c>weekly</c>。</param>
public sealed record DataSettingsDto(string? BackupDirectory, string BackupFrequency);

/// <summary>个性化三栏:唯一会被原样拼进系统提示词的设置(落库前按上限截断:名字 64 / 背景 2000 / 指令 8000)。</summary>
public sealed record PersonalizationSettingsDto(string Name, string Background, string Instructions);

/// <summary>
/// 设备作用域的全部设置(全量)。<b>Run 开始时现读</b>:改完立即对下一个 Run 生效(AGENTS.md §3);其中几项(空闲超时、账号轮换、故障转移、网关开关)每次请求 / 每次选择现读。
/// </summary>
/// <param name="DefaultPermissionMode">新会话的默认权限档位:<c>ask</c> / <c>auto</c> / <c>full</c> 之一(<c>full</c> 只有 owner 能设)。</param>
/// <param name="ModelProposedGoals"><c>auto</c> / <c>alwaysAsk</c> / <c>disabled</c>。</param>
/// <param name="Shell"><c>system</c> / <c>cmd</c> / <c>powershell</c> / <c>pwsh</c> / <c>zsh</c> / <c>bash</c> / <c>fish</c> / <c>sh</c>。</param>
/// <param name="GoalCheckinMinutes">后台子代理推迟期间目标检查的基数间隔(分钟,1–1440;出厂 30,第 n 次 ×2^min(n,2))。</param>
/// <param name="UpstreamIdleTimeoutSeconds">上游空闲超时(秒,60–3600;出厂 600)。</param>
/// <param name="AccountRotation">同一家供应商的多个登录账号之间自动轮换(出厂开)。</param>
/// <param name="Failover">这家挂了换另一家供应商(出厂关,和账号轮换是两件事)。</param>
public sealed record DeviceSettingsDto(
    ModelPickDto DefaultModel,
    ModelPickDto PermissionReviewer,
    ModelPickDto GoalEvaluator,
    ModelPickDto Compact,
    string CompactThinking,
    ModelPickDto Completion,
    ModelPickDto InlineCompletionModel,
    ModelPickDto Image,
    string DefaultPermissionMode,
    string ModelProposedGoals,
    bool AutoCompact,
    int MaxOutputTokens,
    bool PromptSuggestions,
    bool LocalCompletion,
    bool InlineCompletion,
    bool ImageGeneration,
    string Shell,
    int GoalCheckinMinutes,
    int UpstreamIdleTimeoutSeconds,
    bool AccountRotation,
    bool Failover,
    string BuiltinSearxngUrl,
    SubagentSettingsDto Subagent,
    ProxySettingsDto Proxy,
    GatewaySettingsDto Gateway,
    DataSettingsDto Data,
    PersonalizationSettingsDto Personalization);

/// <summary><c>settings</c> 主题的状态,同时是 <c>settings.changed</c> 的数据(首帧与事件同一个类型,AGENTS.md §3 规则 5)。<see cref="Rev"/> 单调,控制端只收不更旧的。</summary>
public sealed record DeviceSettingsState(long Rev, DeviceSettingsDto Settings);

public sealed record SubagentSettingsPatchDto(ModelPickDto? Model = null, string? Thinking = null, int? PerSessionLimit = null, int? GlobalLimit = null);

public sealed record ProxySettingsPatchDto(bool? Enabled = null, string? Mode = null, string? Scheme = null, string? Host = null, int? Port = null, bool? AuthEnabled = null, string? AuthUser = null, string? Bypass = null);

public sealed record GatewaySettingsPatchDto(bool? Enabled = null, bool? AllowRemote = null, int? PreferredPort = null);

/// <summary><see cref="BackupDirectory"/> 为空串 = 清掉(没选);null = 不改。</summary>
public sealed record DataSettingsPatchDto(string? BackupDirectory = null, string? BackupFrequency = null);

public sealed record PersonalizationPatchDto(string? Name = null, string? Background = null, string? Instructions = null);

/// <summary>
/// 设置补丁:字段缺省(null)= 不改;嵌套块可以只给要改的那个属性(深合并:连点两个同块的开关,第二次写不会把第一次写的值覆盖回旧值)。
/// 「模型 + 供应商」成对写:给了 <c>ModelPickDto</c> 就整对替换,所以供应商不会留下「新别名 + 旧供应商」的脏配对。
/// 一项不合法整个补丁都不生效(<c>invalid_request</c>,<c>details</c> 带 <c>settings.*</c> key 和字段名)。
/// </summary>
public sealed record DeviceSettingsPatchDto(
    ModelPickDto? DefaultModel = null,
    ModelPickDto? PermissionReviewer = null,
    ModelPickDto? GoalEvaluator = null,
    ModelPickDto? Compact = null,
    string? CompactThinking = null,
    ModelPickDto? Completion = null,
    ModelPickDto? InlineCompletionModel = null,
    ModelPickDto? Image = null,
    string? DefaultPermissionMode = null,
    string? ModelProposedGoals = null,
    bool? AutoCompact = null,
    int? MaxOutputTokens = null,
    bool? PromptSuggestions = null,
    bool? LocalCompletion = null,
    bool? InlineCompletion = null,
    bool? ImageGeneration = null,
    string? Shell = null,
    int? GoalCheckinMinutes = null,
    int? UpstreamIdleTimeoutSeconds = null,
    bool? AccountRotation = null,
    bool? Failover = null,
    string? BuiltinSearxngUrl = null,
    SubagentSettingsPatchDto? Subagent = null,
    ProxySettingsPatchDto? Proxy = null,
    GatewaySettingsPatchDto? Gateway = null,
    DataSettingsPatchDto? Data = null,
    PersonalizationPatchDto? Personalization = null);

public sealed record SettingsUpdateParams(DeviceSettingsPatchDto Patch);

/// <summary>项目作用域的设置(工作区 JSON 的 <c>settings</c> 段里归 3.0 设置页管的那几项;技能选装等别的段原样保留)。</summary>
/// <param name="PermissionMode">该项目新会话的默认权限档位。</param>
/// <param name="DefaultMode">会话模式(<c>code</c> / <c>plan</c> 等)。</param>
/// <param name="DefaultThinking">默认思考档位(<c>auto</c> / <c>off</c> / …)。</param>
/// <param name="CompactModel">null = 跟随全局设置;<c>ModelPickDto.None</c> 以外 = 用这个别名。</param>
/// <param name="CompactThinking">null = 跟随全局设置;<c>inherit</c> = 跟随会话本轮档位;其余 = 显式档位。</param>
public sealed record ProjectSettingsDto(
    string WorkspaceId,
    string PermissionMode,
    ModelPickDto DefaultModel,
    string DefaultMode,
    string DefaultThinking,
    bool WebSearch,
    bool MaxContext,
    ModelPickDto? CompactModel,
    string? CompactThinking);

public sealed record ProjectSettingsRefParams(string WorkspaceId);

/// <summary>项目设置补丁:字段缺省 = 不改。<see cref="ClearCompactModel"/> / <see cref="ClearCompactThinking"/> 为真 = 改回跟随全局(写 null)。</summary>
public sealed record ProjectSettingsPatchDto(
    string WorkspaceId,
    string? PermissionMode = null,
    ModelPickDto? DefaultModel = null,
    string? DefaultMode = null,
    string? DefaultThinking = null,
    bool? WebSearch = null,
    bool? MaxContext = null,
    ModelPickDto? CompactModel = null,
    string? CompactThinking = null,
    bool ClearCompactModel = false,
    bool ClearCompactThinking = false);

/// <summary><c>settings.proxy.setPassword</c>:空串 = 清掉。密码经 NCK1 存进凭据表,不进 settings JSON、不进任何 DTO。</summary>
public sealed record ProxyPasswordParams(string Password);

public static class SettingsEvents
{
    /// <summary>设置变了(数据是新的 <see cref="DeviceSettingsState"/>,全量)。</summary>
    public const string Changed = "settings.changed";
}

/// <summary>设置方法表(能力 <see cref="Capabilities.Settings"/>,承载 本机 + 远程):读取 member 起,写入 owner。</summary>
public static class SettingsMethods
{
    public static readonly MethodDef<Unit, DeviceSettingsState> Get =
        MethodTable.Query<Unit, DeviceSettingsState>("settings.get", Role.Member, Capabilities.Settings);

    /// <summary>深合并补丁;返回新的全量状态。</summary>
    public static readonly MethodDef<SettingsUpdateParams, DeviceSettingsState> Update =
        MethodTable.Command<SettingsUpdateParams, DeviceSettingsState>("settings.update", Role.Owner, Capabilities.Settings);

    public static readonly MethodDef<ProjectSettingsRefParams, ProjectSettingsDto> ProjectGet =
        MethodTable.Query<ProjectSettingsRefParams, ProjectSettingsDto>("settings.project.get", Role.Member, Capabilities.Settings);

    public static readonly MethodDef<ProjectSettingsPatchDto, ProjectSettingsDto> ProjectUpdate =
        MethodTable.Command<ProjectSettingsPatchDto, ProjectSettingsDto>("settings.project.update", Role.Owner, Capabilities.Settings);

    /// <summary>设置 / 清掉代理密码(NCK1)。只给本机通道:明文密码不经远程连接改。</summary>
    public static readonly MethodDef<ProxyPasswordParams, DeviceSettingsState> ProxySetPassword =
        MethodTable.Command<ProxyPasswordParams, DeviceSettingsState>("settings.proxy.setPassword", Role.Owner, Capabilities.Settings, Transports.Local);

    /// <summary>设备设置(快照和 <see cref="SettingsEvents.Changed"/> 同一个类型)。member 起(不含任何凭据)。</summary>
    public static readonly TopicDef<DeviceSettingsState> Topic = new("settings", PayloadShapes.GetShape<DeviceSettingsState>(), Role.Member);

    public static readonly IReadOnlyList<MethodDef> All = [Get, Update, ProjectGet, ProjectUpdate, ProxySetPassword];
}

[GenerateShapeFor<ModelPickDto>]
[GenerateShapeFor<SubagentSettingsDto>]
[GenerateShapeFor<ProxySettingsDto>]
[GenerateShapeFor<GatewaySettingsDto>]
[GenerateShapeFor<DataSettingsDto>]
[GenerateShapeFor<PersonalizationSettingsDto>]
[GenerateShapeFor<DeviceSettingsDto>]
[GenerateShapeFor<DeviceSettingsState>]
[GenerateShapeFor<DeviceSettingsPatchDto>]
[GenerateShapeFor<SettingsUpdateParams>]
[GenerateShapeFor<ProjectSettingsDto>]
[GenerateShapeFor<ProjectSettingsRefParams>]
[GenerateShapeFor<ProjectSettingsPatchDto>]
[GenerateShapeFor<ProxyPasswordParams>]
public sealed partial class PayloadShapes;
