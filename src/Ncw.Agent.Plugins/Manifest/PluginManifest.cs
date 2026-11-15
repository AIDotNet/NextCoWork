namespace Ncw.Agent.Plugins;

/// <summary>这份清单描述的是哪一类插件。<c>webapp</c> 是零代码的那一类(只有 <c>contributes.webApps</c>);3.0 没有网页应用的承载界面,装得上但不起任何运行时。</summary>
public enum PluginKind
{
    Extension,
    WebApp,
}

public sealed record PluginCommandContribution(string Command, string Title, string? Icon = null, string? IconFile = null);

public sealed record PluginMenuContribution(string Command, string? Group = null, string? When = null);

public sealed record PluginToolContribution(string Name, string Title, string? Icon = null, string? Shape = null, string? CardTitle = null, string? CardSummary = null);

public sealed record PluginSlashCommandContribution(string Name, string Command, string Title, string? Description = null);

public sealed record PluginKeybindingContribution(string Command, string Key, string? When = null);

public sealed record PluginConfigurationProperty(string Type, string Title, string? Description, string? DefaultString, double? DefaultNumber, bool? DefaultBool, IReadOnlyList<string> Enum);

public sealed record PluginConfigurationContribution(string Title, IReadOnlyDictionary<string, PluginConfigurationProperty> Properties);

/// <summary>
/// 贡献点。3.0 实现的:命令、菜单(数据面)、Agent 工具、Skills、配置、斜杠命令 / 快捷键(数据面,转给界面)。
/// <see cref="Unsupported"/>:认得字段名、但 3.0 不实现的(React 视图、自定义编辑器、网页应用、卡片视图、主题、子代理 / 模式目录、文档引擎),装载时按它给诊断,不报错。
/// </summary>
public sealed record PluginContributes(
    IReadOnlyList<PluginCommandContribution> Commands,
    IReadOnlyDictionary<string, IReadOnlyList<PluginMenuContribution>> Menus,
    IReadOnlyList<PluginToolContribution> Tools,
    IReadOnlyList<string> Skills,
    PluginConfigurationContribution? Configuration,
    IReadOnlyList<PluginSlashCommandContribution> SlashCommands,
    IReadOnlyList<PluginKeybindingContribution> Keybindings,
    IReadOnlyList<string> Unsupported)
{
    public static PluginContributes Empty { get; } = new([], new Dictionary<string, IReadOnlyList<PluginMenuContribution>>(), [], [], null, [], [], []);
}

/// <summary>插件清单(<c>package.json</c>,字段名与 2.x / VS Code 同名)。解析与校验在 <see cref="PluginManifestParser"/>。</summary>
public sealed record PluginManifest(
    string Id,
    string Name,
    string Publisher,
    PluginKind Kind,
    string DisplayName,
    string Description,
    string Version,
    string? License,
    string? Icon,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Keywords,
    string Engines,
    string Main,
    string? L10n,
    IReadOnlyList<string> ActivationEvents,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> OptionalPermissions,
    IReadOnlyList<string> HostPermissions,
    IReadOnlyList<string> AllowedCommands,
    IReadOnlyDictionary<string, string> Dependencies,
    PluginContributes Contributes);

/// <summary>清单里一处问题:<see cref="Field"/> 是字段路径(给插件作者看的),<see cref="Message"/> 是英文说明。</summary>
public sealed record ManifestError(string Field, string Message);

public sealed record ManifestParseResult(PluginManifest? Manifest, IReadOnlyList<ManifestError> Errors, IReadOnlyList<ManifestError> Warnings)
{
    public bool Ok => Manifest is not null;
}
