using PolyType;

namespace Ncw.Protocol.Methods;

public static partial class Capabilities
{
    /// <summary>插件:<c>plugins.*</c> 和 <c>plugins</c> 主题(安装 / 启停 / 卸载 / 授权 / 配置 / 市场 / 更新 owner;列表 / 活动 / 配置读取 / 市场浏览 member;运行命令 / 卡片动作 / 回答插件的提问 member)。</summary>
    public const string Plugins = "plugins";
}

/// <summary>
/// 插件的方法表与主题(docs/design/09 §8.4「插件」,功能盘点 10 的 <c>plugins</c> 模块:安装、启停、权限、市场、配置、更新 → Agent(Jaunt 宿主,D14);
/// <c>runCommand</c> / <c>cardAction</c> / <c>interactionReply</c> 按协议转发;React 视图相关的 <c>activateEditor</c> / <c>openWebApp</c> / <c>setEditorDirty</c> / <c>confirmClose</c> 不做(Q2),<c>pickPackage</c> 是客户端文件选择框)。
/// 每个方法声明种类(command 必带幂等键)、所需角色、所需能力、允许的承载。
/// <list type="bullet">
/// <item><b>读</b>(<c>list</c> / <c>activity</c> / <c>getConfiguration</c> / <c>marketList</c> / <c>marketCategories</c> / <c>marketDetail</c> / <c>checkUpdates</c>)member 起:清单里有命令白名单 / 域名 / 诊断,活动日志里有插件做过什么;viewer 不给。市场浏览和更新检查让设备代为联网,所以也是 member(同 Skills)。</item>
/// <item><b>写</b>(<c>installPackage</c> / <c>installMarket</c> / <c>updateAll</c> / <c>setEnabled</c> / <c>uninstall</c> / <c>grantPermissions</c> / <c>revokePermissions</c> / <c>setConfiguration</c>)owner:
/// 在设备上装并运行第三方代码、决定它能碰哪些文件 / 命令 / 域名,与终端 / Hooks / MCP 同一等级的授权。所有变更方法都<b>返回变更之后的完整状态</b>(控制端镜像整体替换,不拼增量)。</item>
/// <item><b>用户的回应</b>(<c>runCommand</c> / <c>cardAction</c> / <c>interactionReply</c>)member:命令是插件自己在清单里声明的入口,效果受它被授予的能力约束;卡片按钮是对一次 member 发起的 Run 里的工具调用的回应;
/// 插件的提问(确认、输入、执行命令的审批)和工具权限审批一样由发起 Run 的 member 回答(<c>interactions.respond</c> 也是 member)。能力没批的调用在宿主里就被拒,不靠角色。</item>
/// <item><c>plugins.installPackage</c> 的 zip 字节走一个请求帧:单帧上限 4MB,所以上限 <see cref="PluginsLimits.MaxZipBytes"/>(3MB);更大的包走市场安装(设备自己下载,≤ 20MB)。</item>
/// <item>业务失败的结构化原因在 <c>error.details</c>(<see cref="ProviderErrorDto"/>,key 是稳定的 <c>plugins.*</c>),控制端按 key 翻译;市场安装需要账户令牌而设备没有 → <c>plugins.authRequired</c>(浏览照常)。<b>错误里不含任何凭据。</b></item>
/// <item><b>状态是全量快照</b>:<c>plugins</c> 主题的首帧、<see cref="PluginsEvents.Changed"/> 与 <c>plugins.list</c> 是同一个类型 <see cref="PluginsTopicState"/>,带单调 <c>Rev</c>(AGENTS.md §3 规则 5);
/// 设备断开时控制端把插件状态标为 <c>unknown</c>,不判成结束。</item>
/// </list>
/// </summary>
public static class PluginsMethods
{
    /// <summary>全量状态(与主题首帧同一个类型)。</summary>
    public static readonly MethodDef<Unit, PluginsTopicState> List = MethodTable.Query<Unit, PluginsTopicState>("plugins.list", Role.Member, Capabilities.Plugins);

    /// <summary>
    /// 启用 / 禁用。启用会先核对必选能力是否都批过(没批 = 状态 <c>pending-approval</c>,不激活);装载失败(<c>error</c>)的不能启用(<c>plugins.notRunnable</c>)。
    /// 禁用 = 先注销贡献(工具 / 命令 / Skill)再销毁运行时。
    /// </summary>
    public static readonly MethodDef<PluginsSetEnabledParams, PluginsTopicState> SetEnabled =
        MethodTable.Command<PluginsSetEnabledParams, PluginsTopicState>("plugins.setEnabled", Role.Owner, Capabilities.Plugins);

    /// <summary>卸载:先下线,再删包、状态、配置、私有存储、密钥、活动日志。</summary>
    public static readonly MethodDef<PluginsUninstallParams, PluginsTopicState> Uninstall =
        MethodTable.Command<PluginsUninstallParams, PluginsTopicState>("plugins.uninstall", Role.Owner, Capabilities.Plugins);

    /// <summary>从 zip 字节安装。新装的插件是禁用的、没有任何授权(用户逐条勾选再启用);覆盖安装 = 升级,用户的决定保留,必选能力变大则待批准。</summary>
    public static readonly MethodDef<PluginsInstallPackageParams, PluginsInstallResult> InstallPackage =
        MethodTable.Command<PluginsInstallPackageParams, PluginsInstallResult>("plugins.installPackage", Role.Owner, Capabilities.Plugins);

    /// <summary>授予。越过清单上界(必选 ∪ 可选)的静默丢弃。</summary>
    public static readonly MethodDef<PluginsPermissionsParams, PluginsTopicState> GrantPermissions =
        MethodTable.Command<PluginsPermissionsParams, PluginsTopicState>("plugins.grantPermissions", Role.Owner, Capabilities.Plugins);

    /// <summary>撤销。撤掉必选能力 = 插件停下来(状态变已禁用);授权是实时读的,对正在跑的插件立刻生效。</summary>
    public static readonly MethodDef<PluginsPermissionsParams, PluginsTopicState> RevokePermissions =
        MethodTable.Command<PluginsPermissionsParams, PluginsTopicState>("plugins.revokePermissions", Role.Owner, Capabilities.Plugins);

    /// <summary>最近的宿主调用(最近的在前,≤ 500 条;不含参数原文)。<c>PluginId</c> 为 null = 所有插件。</summary>
    public static readonly MethodDef<PluginsActivityParams, PluginsActivityResult> Activity =
        MethodTable.Query<PluginsActivityParams, PluginsActivityResult>("plugins.activity", Role.Member, Capabilities.Plugins);

    /// <summary>执行插件贡献的命令(菜单 / 命令面板 / 斜杠命令按命令 id 分发)。必须是清单声明过且已注册的;失败的原因在 <c>error.details</c>(插件自己 throw = <c>plugins.commandFailed</c>,超预算 = <c>plugins.faulted</c>)。</summary>
    public static readonly MethodDef<PluginsRunCommandParams, Unit> RunCommand =
        MethodTable.Command<PluginsRunCommandParams, Unit>("plugins.runCommand", Role.Member, Capabilities.Plugins);

    /// <summary>用户点了实时工具卡片上的按钮。<b>即发即忘</b>:送不到(工具已结束 / callId 不属于该插件)是 <see cref="PluginsCardActionResult.Delivered"/> = false,不是错误。</summary>
    public static readonly MethodDef<PluginsCardActionParams, PluginsCardActionResult> CardAction =
        MethodTable.Command<PluginsCardActionParams, PluginsCardActionResult>("plugins.cardAction", Role.Member, Capabilities.Plugins);

    /// <summary>回答插件的一个提问(确认 / 输入 / 选择 / 运行时权限申请 / 执行命令的审批)。取消 = 全部字段缺省。请求已经不存在(超时 / 被撤销 / 别的控制端先回答)是 <c>plugins.interactionNotFound</c>。</summary>
    public static readonly MethodDef<PluginsInteractionReplyParams, Unit> InteractionReply =
        MethodTable.Command<PluginsInteractionReplyParams, Unit>("plugins.interactionReply", Role.Member, Capabilities.Plugins);

    public static readonly MethodDef<PluginsMarketListParams, PluginsMarketListResult> MarketList =
        MethodTable.Query<PluginsMarketListParams, PluginsMarketListResult>("plugins.marketList", Role.Member, Capabilities.Plugins);

    public static readonly MethodDef<Unit, PluginsMarketCategoriesResult> MarketCategories =
        MethodTable.Query<Unit, PluginsMarketCategoriesResult>("plugins.marketCategories", Role.Member, Capabilities.Plugins);

    public static readonly MethodDef<PluginsMarketDetailParams, PluginMarketDetailDto> MarketDetail =
        MethodTable.Query<PluginsMarketDetailParams, PluginMarketDetailDto>("plugins.marketDetail", Role.Member, Capabilities.Plugins);

    /// <summary>从市场安装:授权(需要账户令牌)→ 下载(≤ 20MB)→ 校验权威摘要 → 安装。没有令牌 = <c>plugins.authRequired</c>。同一个 slug 的两次安装合流成一次。</summary>
    public static readonly MethodDef<PluginsInstallMarketParams, PluginsInstallResult> InstallMarket =
        MethodTable.Command<PluginsInstallMarketParams, PluginsInstallResult>("plugins.installMarket", Role.Owner, Capabilities.Plugins);

    /// <summary>检查已装插件有没有新版(把市场整张表翻完,缓存 10 分钟)。<see cref="PluginsCheckUpdatesParams.Force"/> 跳过缓存。</summary>
    public static readonly MethodDef<PluginsCheckUpdatesParams, PluginsUpdatesResult> CheckUpdates =
        MethodTable.Query<PluginsCheckUpdatesParams, PluginsUpdatesResult>("plugins.checkUpdates", Role.Member, Capabilities.Plugins);

    /// <summary>更新所有<b>市场来源</b>的插件(本地装的不动)。串行,一个失败不中断后面的。</summary>
    public static readonly MethodDef<Unit, PluginsUpdateAllResult> UpdateAll =
        MethodTable.Command<Unit, PluginsUpdateAllResult>("plugins.updateAll", Role.Owner, Capabilities.Plugins);

    public static readonly MethodDef<PluginsConfigurationParams, PluginsConfigurationResult> GetConfiguration =
        MethodTable.Query<PluginsConfigurationParams, PluginsConfigurationResult>("plugins.getConfiguration", Role.Member, Capabilities.Plugins);

    /// <summary>改一项配置(<see cref="PluginsSetConfigurationParams.Value"/> 为 null / 全空 = 恢复默认)。类型对不上、键不在清单里被拒(<c>plugins.configurationInvalid</c>)。</summary>
    public static readonly MethodDef<PluginsSetConfigurationParams, PluginsConfigurationResult> SetConfiguration =
        MethodTable.Command<PluginsSetConfigurationParams, PluginsConfigurationResult>("plugins.setConfiguration", Role.Owner, Capabilities.Plugins);

    /// <summary>全部插件的全量状态 + 挂起的交互 + 单调版本号。member 起可订阅(含清单里的命令白名单 / 域名和诊断;交互里有待批准的命令行)。</summary>
    public static readonly TopicDef<PluginsTopicState> Topic = new("plugins", PayloadShapes.GetShape<PluginsTopicState>(), Role.Member);

    public static readonly IReadOnlyList<MethodDef> All =
    [
        List, SetEnabled, Uninstall, InstallPackage, GrantPermissions, RevokePermissions, Activity, RunCommand, CardAction, InteractionReply,
        MarketList, MarketCategories, MarketDetail, InstallMarket, CheckUpdates, UpdateAll, GetConfiguration, SetConfiguration,
    ];
}

public static class PluginsEvents
{
    /// <summary>状态变了(数据是新的 <see cref="PluginsTopicState"/>,全量,带单调 <c>Rev</c>;控制端只收不更旧的)。</summary>
    public const string Changed = "plugins.changed";
}

public static class PluginsLimits
{
    /// <summary><c>plugins.installPackage</c> 请求里 zip 字节的上限:协议单帧 4MB,留出余量。</summary>
    public const int MaxZipBytes = 3 * 1024 * 1024;
}

/// <summary>插件状态的取值(<see cref="PluginInfoDto.Status"/>)。设备断开时控制端自己标 unknown,不是这里的取值。</summary>
public static class PluginStatuses
{
    public const string Idle = "idle";
    public const string Activating = "activating";
    public const string Active = "active";
    public const string Disabled = "disabled";
    public const string PendingApproval = "pending-approval";
    public const string Error = "error";
    public const string Faulted = "faulted";
}

/// <summary>插件提问的种类(<see cref="PluginInteractionDto.Kind"/>)。</summary>
public static class PluginInteractionKindNames
{
    public const string Confirm = "confirm";
    public const string Input = "input";
    public const string Pick = "pick";
    public const string Permissions = "permissions";
    public const string Exec = "exec";
}

public sealed record PluginCommandDto(string Command, string Title, string? Icon);

public sealed record PluginMenuItemDto(string MenuId, string Command, string? Group, string? When);

public sealed record PluginToolDto(string Name, string Title, string? Shape, string? CardTitle, string? CardSummary, string InternalId);

public sealed record PluginSlashCommandDto(string Name, string Command, string Title, string? Description);

public sealed record PluginKeybindingDto(string Command, string Key, string? When);

/// <param name="Type"><c>boolean</c> / <c>string</c> / <c>number</c> / <c>enum</c>。</param>
public sealed record PluginConfigPropertyDto(
    string Key, string Type, string Title, string? Description, string[] Enum, string? DefaultText, double? DefaultNumber, bool? DefaultFlag);

public sealed record PluginConfigurationDto(string Title, PluginConfigPropertyDto[] Properties);

/// <summary>一条诊断。<see cref="Key"/> 是稳定的 <c>plugins.diag.*</c> key(控制端翻译),<see cref="Message"/> 是英文,给日志和排错;<see cref="Level"/>:<c>info</c> / <c>warn</c> / <c>error</c>。</summary>
public sealed record PluginDiagnosticDto(string Path, string Key, string Message, string Level, Dictionary<string, string>? Params);

/// <summary>某种语言的词条(<c>plugin.&lt;id&gt;.&lt;key&gt;</c> → 文案)。菜单项在插件被激活之前就要画出来,所以随状态一起下发。</summary>
public sealed record PluginMessagesDto(string Locale, Dictionary<string, string> Entries);

/// <summary>
/// 一个已安装插件(2.x 的 <c>InstalledPlugin</c> 去掉视图 / 状态栏 / 图标 data URL 之后)。<see cref="Path"/> 是包目录(设备上的路径)。
/// <see cref="Required"/> / <see cref="Optional"/> 来自清单,<see cref="Granted"/> 来自用户(三者分开:升级时才回答得了「这次新增了哪几条」);
/// <see cref="PendingPermissions"/> = 必选而还没批的(授权界面逐条列出)。<see cref="RegisteredTools"/> / <see cref="RegisteredCommands"/> 是插件激活后真正注册的(在线的贡献)。
/// </summary>
public sealed record PluginInfoDto(
    string Id,
    string Name,
    string Publisher,
    string Kind,
    string DisplayName,
    string Description,
    string Version,
    string Engines,
    string[] Categories,
    string Status,
    bool Enabled,
    string Path,
    string[] Required,
    string[] Optional,
    string[] Granted,
    string[] PendingPermissions,
    string[] HostPermissions,
    string[] AllowedCommands,
    PluginCommandDto[] Commands,
    PluginMenuItemDto[] Menus,
    PluginToolDto[] Tools,
    string[] Skills,
    PluginSlashCommandDto[] SlashCommands,
    PluginKeybindingDto[] Keybindings,
    PluginConfigurationDto? Configuration,
    string[] Unsupported,
    PluginDiagnosticDto[] Diagnostics,
    string[] RegisteredTools,
    string[] RegisteredCommands,
    PluginMessagesDto[] Messages,
    long InstalledAt,
    long UpdatedAt,
    string? Slug);

public sealed record PluginPickItemDto(string Id, string LabelKey);

/// <summary>审批所指的执行环境(设备 + 环境 + 名字 + 类型,AGENTS.md §5)。</summary>
public sealed record PluginEnvironmentDto(string DeviceId, string Environment, string Name, string Type);

/// <summary>
/// 插件在等用户回答的一个请求。界面文字一律是 l10n key(<c>*Key</c>),不是句子。<see cref="Kind"/> 见 <see cref="PluginInteractionKindNames"/>:
/// <c>confirm</c>(<see cref="TitleKey"/> / <see cref="DetailKey"/> / <see cref="Danger"/>)、<c>input</c>(<see cref="TitleKey"/> / <see cref="PlaceholderKey"/> / <see cref="Initial"/> / <see cref="Password"/>)、<c>pick</c>(<see cref="Items"/>)、
/// <c>permissions</c>(<see cref="Permissions"/> + <see cref="TitleKey"/> = 理由的 key)、<c>exec</c>(<see cref="Command"/> = 要跑的命令行,<see cref="Environment"/> = 在哪个设备的哪个环境里跑)。
/// </summary>
public sealed record PluginInteractionDto(
    string RequestId,
    string PluginId,
    string Kind,
    string? TitleKey,
    string? DetailKey,
    string? PlaceholderKey,
    PluginPickItemDto[] Items,
    bool Password,
    bool Danger,
    string? Initial,
    string[] Permissions,
    string? Command,
    PluginEnvironmentDto? Environment,
    long CreatedAt);

/// <summary><c>plugins</c> 主题的全量状态(快照 = <see cref="PluginsEvents.Changed"/> = <c>plugins.list</c>):插件按显示名排序,<see cref="Rev"/> 单调;<see cref="ApiVersion"/> 是设备实现的插件 API 版本(<c>engines.nextcowork</c> 比的是它)。</summary>
public sealed record PluginsTopicState(long Rev, string ApiVersion, PluginInfoDto[] Plugins, PluginInteractionDto[] Interactions);

public sealed record PluginsSetEnabledParams(string PluginId, bool Enabled);

public sealed record PluginsUninstallParams(string PluginId);

/// <param name="Zip">zip 字节(≤ <see cref="PluginsLimits.MaxZipBytes"/>)。</param>
/// <param name="FileName">下载 / 选择的文件名,只用来给人看;安装目录名永远取自包里校验过的清单。</param>
public sealed record PluginsInstallPackageParams(byte[] Zip, string? FileName);

public sealed record PluginsInstallMarketParams(string Slug, string? Version);

/// <summary>安装的结果:装好的那个 + 变更之后的完整状态。</summary>
public sealed record PluginsInstallResult(PluginInfoDto Installed, PluginsTopicState State);

public sealed record PluginsPermissionsParams(string PluginId, string[] Permissions);

public sealed record PluginsActivityParams(string? PluginId);

public sealed record PluginActivityDto(long Ts, string PluginId, string Method, string Summary, string Verdict, long DurationMs);

public sealed record PluginsActivityResult(PluginActivityDto[] Entries);

/// <param name="ArgsJson">命令的附加参数(JSON 对象文本,≤ 16KB)。宿主固定带 <c>workspaceId</c>(<paramref name="WorkspaceId"/>),调用方不能改写它。</param>
/// <param name="WorkspaceId">菜单在哪个项目里点的;命令里的文件 / 命令类能力以它为根。</param>
public sealed record PluginsRunCommandParams(string PluginId, string CommandId, string? ArgsJson, string? WorkspaceId);

/// <param name="ValueJson">按钮携带的值(JSON 文本,≤ 16KB)。</param>
public sealed record PluginsCardActionParams(string PluginId, string CallId, string ActionId, string? ValueJson);

public sealed record PluginsCardActionResult(bool Delivered);

/// <summary>回答:取消 = 全部缺省。<c>confirm</c> / <c>permissions</c> / <c>exec</c> 看 <see cref="Confirmed"/>,<c>input</c> 看 <see cref="Text"/>,<c>pick</c> 看 <see cref="PickedId"/>。</summary>
public sealed record PluginsInteractionReplyParams(string RequestId, bool? Confirmed, string? Text, string? PickedId);

public sealed record PluginsMarketListParams(string? Query, string? Category, int Page);

/// <summary>市场上的一条插件。<see cref="Permissions"/> 是这一版声明的<b>必选能力</b>(列表里就要显示);<see cref="Skills"/> 是它自带的 Skill 名;<see cref="IconUrl"/> 已按市场 origin 解析成绝对地址。</summary>
public sealed record PluginMarketItemDto(
    string PluginId,
    string Slug,
    string Publisher,
    string DisplayName,
    string Description,
    string Category,
    string? IconUrl,
    int DownloadCount,
    string? Version,
    string? Engines,
    string[] Permissions,
    string[] Skills,
    string Author);

public sealed record PluginsMarketListResult(PluginMarketItemDto[] Items, bool HasNext, int Page);

public sealed record PluginsMarketCategoriesResult(string[] Categories);

public sealed record PluginsMarketDetailParams(string Slug);

public sealed record PluginMarketVersionDto(string Version, string? Changelog, bool? PermissionEscalated, long? FileSize);

public sealed record PluginMarketDetailDto(PluginMarketItemDto Item, PluginMarketVersionDto[] Versions);

public sealed record PluginsCheckUpdatesParams(bool Force);

/// <summary>「这个已装的插件有新版」。<see cref="EscalatedPermissions"/> 逐条列出新增的必选能力;<see cref="FromMarket"/> = false 的(本地装的)不计入批量更新。</summary>
public sealed record PluginUpdateDto(string PluginId, string Slug, string DisplayName, string CurrentVersion, string LatestVersion, string[] EscalatedPermissions, bool FromMarket);

public sealed record PluginsUpdatesResult(PluginUpdateDto[] Updates);

public sealed record PluginUpdateFailureDto(string PluginId, string ErrorKey);

public sealed record PluginsUpdateAllResult(string[] Updated, PluginUpdateFailureDto[] Failed);

public sealed record PluginsConfigurationParams(string PluginId);

/// <summary>配置项的一个值(2.x:<c>boolean | string | number</c>;三个里恰好一个有值,全空 = 没有 / 恢复默认)。</summary>
public sealed record PluginConfigValueDto(string? Text, double? Number, bool? Flag);

public sealed record PluginsSetConfigurationParams(string PluginId, string Key, PluginConfigValueDto? Value);

public sealed record PluginsConfigurationResult(string PluginId, Dictionary<string, PluginConfigValueDto> Values);

[GenerateShapeFor<PluginsTopicState>]
[GenerateShapeFor<PluginInfoDto>]
[GenerateShapeFor<PluginInteractionDto>]
[GenerateShapeFor<PluginsSetEnabledParams>]
[GenerateShapeFor<PluginsUninstallParams>]
[GenerateShapeFor<PluginsInstallPackageParams>]
[GenerateShapeFor<PluginsInstallMarketParams>]
[GenerateShapeFor<PluginsInstallResult>]
[GenerateShapeFor<PluginsPermissionsParams>]
[GenerateShapeFor<PluginsActivityParams>]
[GenerateShapeFor<PluginsActivityResult>]
[GenerateShapeFor<PluginsRunCommandParams>]
[GenerateShapeFor<PluginsCardActionParams>]
[GenerateShapeFor<PluginsCardActionResult>]
[GenerateShapeFor<PluginsInteractionReplyParams>]
[GenerateShapeFor<PluginsMarketListParams>]
[GenerateShapeFor<PluginsMarketListResult>]
[GenerateShapeFor<PluginsMarketCategoriesResult>]
[GenerateShapeFor<PluginsMarketDetailParams>]
[GenerateShapeFor<PluginMarketDetailDto>]
[GenerateShapeFor<PluginsCheckUpdatesParams>]
[GenerateShapeFor<PluginsUpdatesResult>]
[GenerateShapeFor<PluginsUpdateAllResult>]
[GenerateShapeFor<PluginsConfigurationParams>]
[GenerateShapeFor<PluginsSetConfigurationParams>]
[GenerateShapeFor<PluginsConfigurationResult>]
public sealed partial class PayloadShapes;
