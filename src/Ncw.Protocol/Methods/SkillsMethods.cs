using PolyType;

namespace Ncw.Protocol.Methods;

public static partial class Capabilities
{
    /// <summary>Skills:<c>skills.*</c>(清单 / 诊断 / 全局启停 / 工作区选装 / zip 与市场安装 / 卸载 / 市场浏览)。读取 viewer 起,选装 member,写设备文件系统的 owner。</summary>
    public const string Skills = "skills";
}

/// <summary>
/// Skills 的方法表(docs/design/09 §8.4,功能盘点 10 的 <c>skills</c> 模块:除 <c>pickZip</c>(客户端文件选择框,客户端本地)外都在 Agent)。每个方法声明种类(command 必带幂等键)、所需角色、所需能力、允许的承载。
/// <list type="bullet">
/// <item><b>读</b>:<c>skills.list</c> / <c>skills.diagnostics</c> viewer。清单每次现扫(用户刚拖进目录的 Skill 马上看得见)。</item>
/// <item><b>选装</b>(<c>skills.setWorkspaceActive</c>)member:对话相关的项目偏好,不碰文件系统;<b>全局启停 / 安装 / 卸载</b> owner:改设备上所有项目共享的东西、写设备文件系统。</item>
/// <item><b>市场</b>(<c>marketList / marketCategories / marketDetail</c>)member:设备代为联网;<c>installMarket</c> owner。</item>
/// <item>所有变更方法都<b>返回变更之后的完整清单</b>(控制端镜像整体替换,不拼增量);别的控制端经 <c>extensions</c> 修订主题知道要重拉。</item>
/// <item>业务失败的结构化原因在 <c>error.details</c>(<see cref="ProviderErrorDto"/>,key 是稳定的 <c>skills.*</c>),控制端按 key 翻译;<b>错误里不含任何凭据</b>。</item>
/// <item><c>skills.installZip</c> 的 zip 字节走一个请求帧:单帧上限 4MB,所以上限 <see cref="SkillsLimits.MaxZipBytes"/>(3MB);更大的包走市场安装(设备自己下载,≤ 20MB)。安装没有进度主题:结果带终态与 key。</item>
/// </list>
/// </summary>
public static class SkillsMethods
{
    public static readonly MethodDef<SkillsScopeParams, SkillsListResult> List =
        Query<SkillsScopeParams, SkillsListResult>("skills.list", Role.Viewer);

    public static readonly MethodDef<SkillsScopeParams, SkillsDiagnosticsResult> Diagnostics =
        Query<SkillsScopeParams, SkillsDiagnosticsResult>("skills.diagnostics", Role.Viewer);

    /// <summary>全局启停一条(设备级偏好,存「被关掉的那些」)。<see cref="SkillsSetGlobalEnabledParams.WorkspaceId"/> 只用来算返回的清单里「在这个工作区是否激活」。</summary>
    public static readonly MethodDef<SkillsSetGlobalEnabledParams, SkillsListResult> SetGlobalEnabled =
        Command<SkillsSetGlobalEnabledParams, SkillsListResult>("skills.setGlobalEnabled", Role.Owner);

    /// <summary>
    /// 在一个工作区里选装 / 取消选装一条。设备上的权威闸门:依赖设备本机资源、在 SSH 工作区里跑不了的 Skill 不许启用(<c>invalid_request</c> + <c>skills.clientAssetsUnavailable</c>),
    /// 关掉始终可行。「空清单 = 全都要」在首次手动选装时物化成显式清单。
    /// </summary>
    public static readonly MethodDef<SkillsSetWorkspaceActiveParams, SkillsListResult> SetWorkspaceActive =
        Command<SkillsSetWorkspaceActiveParams, SkillsListResult>("skills.setWorkspaceActive", Role.Member);

    public static readonly MethodDef<SkillsInstallZipParams, SkillsInstallResult> InstallZip =
        Command<SkillsInstallZipParams, SkillsInstallResult>("skills.installZip", Role.Owner);

    public static readonly MethodDef<SkillsInstallMarketParams, SkillsInstallResult> InstallMarket =
        Command<SkillsInstallMarketParams, SkillsInstallResult>("skills.installMarket", Role.Owner);

    public static readonly MethodDef<SkillsUninstallParams, SkillsListResult> Uninstall =
        Command<SkillsUninstallParams, SkillsListResult>("skills.uninstall", Role.Owner);

    public static readonly MethodDef<SkillsMarketListParams, SkillsMarketListResult> MarketList =
        Query<SkillsMarketListParams, SkillsMarketListResult>("skills.marketList", Role.Member);

    public static readonly MethodDef<Unit, SkillsMarketCategoriesResult> MarketCategories =
        Query<Unit, SkillsMarketCategoriesResult>("skills.marketCategories", Role.Member);

    public static readonly MethodDef<SkillsMarketDetailParams, SkillMarketDetailDto> MarketDetail =
        Query<SkillsMarketDetailParams, SkillMarketDetailDto>("skills.marketDetail", Role.Member);

    public static readonly IReadOnlyList<MethodDef> All =
    [
        List, Diagnostics, SetGlobalEnabled, SetWorkspaceActive, InstallZip, InstallMarket, Uninstall, MarketList, MarketCategories, MarketDetail,
    ];

    private static MethodDef<TP, TR> Query<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Query, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = Capabilities.Skills };

    private static MethodDef<TP, TR> Command<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Command, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = Capabilities.Skills };
}

public static class SkillsLimits
{
    /// <summary><c>skills.installZip</c> 请求里 zip 字节的上限:协议单帧 4MB,留出余量。</summary>
    public const int MaxZipBytes = 3 * 1024 * 1024;
}

public static class SkillSourceKinds
{
    public const string Builtin = "builtin";
    public const string Folder = "folder";
    public const string Zip = "zip";
    public const string Git = "git";
    public const string Plugin = "plugin";
}

public static class SkillScopes
{
    public const string Global = "global";
    public const string Project = "project";
    public const string Plugin = "plugin";
}

/// <summary><c>skills.list</c> 等:<see cref="WorkspaceId"/> 为 null = 只看全局层(全局设置页,不谈「在这个工作区激活」)。</summary>
public sealed record SkillsScopeParams(string? WorkspaceId = null);

/// <summary>清单里的一行(2.x 的 <c>SkillListItem</c>)。<see cref="SourcePath"/> 是 Skill 目录(设备 / 项目环境里的路径,和 <c>AgentInfoDto.Source</c> 一样直接给)。</summary>
/// <param name="SourceKind"><see cref="SkillSourceKinds"/>。</param>
/// <param name="Scope"><see cref="SkillScopes"/>:同名时 project &gt; global &gt; plugin。</param>
/// <param name="PluginId">只有 <c>SourceKind == plugin</c> 时有值(界面据此藏掉卸载按钮)。</param>
/// <param name="ActiveInWorkspace">在请求的工作区里是否激活(空清单 = 全都要,所以此时每一条都是 true;没指定工作区恒为 true)。</param>
/// <param name="ClientAssetsOnly">SSH 工作区里依赖设备本机文件:不可用,不能启用。</param>
/// <param name="LastUsedAt">Unix 毫秒;0 = 没用过。</param>
public sealed record SkillInfoDto(
    string Id,
    string Name,
    string Description,
    string Category,
    string SourceKind,
    string Scope,
    string? PluginId,
    bool GlobalEnabled,
    bool ActiveInWorkspace,
    bool ClientAssetsOnly,
    string? Version,
    string? Sha256,
    string SourcePath,
    int UsageCount,
    long LastUsedAt);

public sealed record SkillsListResult(SkillInfoDto[] Skills);

/// <summary>一条加载诊断。<see cref="Key"/> 是稳定的 <c>skills.*</c> key(控制端翻译),<see cref="Message"/> 是英文,给日志和排错。</summary>
public sealed record SkillDiagnosticDto(string Path, string Key, string Message, Dictionary<string, string>? Params = null);

public sealed record SkillsDiagnosticsResult(SkillDiagnosticDto[] Diagnostics);

public sealed record SkillsSetGlobalEnabledParams(string SkillId, bool Enabled, string? WorkspaceId = null);

public sealed record SkillsSetWorkspaceActiveParams(string SkillId, string WorkspaceId, bool Active);

/// <param name="Zip">zip 字节(≤ <see cref="SkillsLimits.MaxZipBytes"/>)。</param>
/// <param name="FileName">下载 / 选择的文件名,只用来在包里没写版本时从名字里取一个(用户能随手改文件名,所以优先级最低);安装目录名永远取自包里校验过的 name。</param>
/// <param name="Scope"><c>global</c> / <c>project</c>(项目作用域要 <paramref name="WorkspaceId"/>)。</param>
public sealed record SkillsInstallZipParams(byte[] Zip, string? FileName, string Scope, string? WorkspaceId = null);

public sealed record SkillsInstallMarketParams(string Slug, string? Version, string Scope, string? WorkspaceId = null);

/// <summary>安装的结果:装好的那一条 + 变更之后的完整清单(按请求里的工作区算)。</summary>
public sealed record SkillsInstallResult(SkillInfoDto Installed, SkillInfoDto[] Skills);

/// <param name="Scope">null = 按这条 Skill 实际所在的层。给了但对不上它实际所在的位置会被拒(<c>skills.invalidLocation</c>)。</param>
public sealed record SkillsUninstallParams(string SkillId, string? Scope = null, string? WorkspaceId = null);

public sealed record SkillsMarketListParams(string? Query = null, string? Category = null);

/// <summary>市场上的一条 Skill。<see cref="IconUrl"/> 已按市场 origin 解析成绝对地址(客户端是本地来源,不能自己解析)。</summary>
public sealed record SkillMarketItemDto(
    string Slug,
    string Name,
    string DisplayName,
    string Description,
    string Category,
    string? IconUrl,
    string? Author,
    string? Version,
    string? Sha256,
    long? FileSize,
    int? DownloadCount,
    int? TriggerCount);

public sealed record SkillsMarketListResult(SkillMarketItemDto[] Items);

public sealed record SkillsMarketCategoriesResult(string[] Categories);

public sealed record SkillsMarketDetailParams(string Slug);

public sealed record SkillMarketVersionDto(string Version, string? Changelog, string? Sha256, long? FileSize);

public sealed record SkillMarketDetailDto(SkillMarketItemDto Item, SkillMarketVersionDto[] Versions);

[GenerateShapeFor<SkillsScopeParams>]
[GenerateShapeFor<SkillsListResult>]
[GenerateShapeFor<SkillsDiagnosticsResult>]
[GenerateShapeFor<SkillsSetGlobalEnabledParams>]
[GenerateShapeFor<SkillsSetWorkspaceActiveParams>]
[GenerateShapeFor<SkillsInstallZipParams>]
[GenerateShapeFor<SkillsInstallMarketParams>]
[GenerateShapeFor<SkillsInstallResult>]
[GenerateShapeFor<SkillsUninstallParams>]
[GenerateShapeFor<SkillsMarketListParams>]
[GenerateShapeFor<SkillsMarketListResult>]
[GenerateShapeFor<SkillsMarketCategoriesResult>]
[GenerateShapeFor<SkillsMarketDetailParams>]
[GenerateShapeFor<SkillMarketDetailDto>]
public sealed partial class PayloadShapes;
