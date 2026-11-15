using System.Text.RegularExpressions;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Skills;

/// <summary>怎么装进来的。<c>Plugin</c> 的生命周期跟随插件的启用状态,用户不能单独卸载它。</summary>
public enum SkillSourceKind
{
    Builtin,
    Folder,
    Zip,
    Git,
    Plugin,
}

/// <summary>装在哪一层。同名时 <c>Project</c> &gt; <c>Global</c> &gt; <c>Plugin</c>(插件提供合理默认,用户自己写的那条永远说了算)。</summary>
public enum SkillScope
{
    Global,
    Project,
    Plugin,
}

public sealed record SkillSource(SkillSourceKind Kind, string Path, string? Version = null, string? Sha256 = null, string? PluginId = null);

/// <summary>
/// 一条已加载的 Skill。<see cref="Body"/> 是<b>不可信输入</b>(从 zip / 市场装的):已消毒 + 封顶,进模型上下文时必须带边界声明。
/// <see cref="ClientAssetsOnly"/>:SSH 会话里依赖设备本地附带文件的非项目 Skill——不可用(模型会照着正文去跑一个只存在于设备上的脚本)。
/// </summary>
public sealed record Skill(
    string Name,
    string Description,
    string Category,
    SkillSource Source,
    SkillScope Scope,
    string Body,
    string? Model = null,
    IReadOnlyList<string>? AllowedTools = null,
    bool ClientAssetsOnly = false)
{
    /// <summary>id 就是 name:同名时项目覆盖全局,两条永远不会共存;再造一个 <c>&lt;scope&gt;:&lt;name&gt;</c> 会让工作区的启用清单在「项目里新增了一条同名 Skill」之后失配。</summary>
    public string Id => Name;

    /// <summary>这条 Skill 的目录(<c>SKILL.md</c> 的父目录)。</summary>
    public string Directory { get; init; } = "";
}

public static partial class SkillLimits
{
    public const int DescriptionMax = 1024;

    /// <summary>正文封顶(字符)。</summary>
    public const int BodyMax = 64 * 1024;

    /// <summary>SKILL.md 读进内存的字节上限(远大于正文上限,只为判断 frontmatter)。</summary>
    public const int FileMaxBytes = 256 * 1024;

    /// <summary>一次扫描最多认多少条。</summary>
    public const int MaxSkills = 200;

    /// <summary>zip 安装:压缩包字节上限(市场下载)。</summary>
    public const int MaxZipBytes = 20 * 1024 * 1024;

    /// <summary>
    /// 协议请求里直接带 zip 字节时的上限:单帧上限是 4 MB(<c>ProtocolInfo.MaxFrameBytes</c>),留出余量。更大的包走市场安装(设备自己下载)。
    /// </summary>
    public const int MaxWireZipBytes = 3 * 1024 * 1024;

    public const long MaxExpandedBytes = 50L * 1024 * 1024;

    public const int MaxEntries = 1000;

    public const int MaxDepth = 12;

    /// <summary>Skill 目录名 / name:<c>^[a-z0-9][a-z0-9-]{0,63}$</c>。名字会进系统提示词、会被模型原样当 <c>Skill</c> 工具的入参。</summary>
    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}

/// <summary>诊断的稳定 key(控制端按它翻译成界面文字;<see cref="SkillDiagnostic.Message"/> 是英文,给日志和排错)。</summary>
public static class SkillDiagnosticKeys
{
    public const string DirectoryUnreadable = "skills.directoryUnreadable";
    public const string FileUnreadable = "skills.fileUnreadable";
    public const string NameInvalid = "skills.nameInvalid";
    public const string DirectoryNameInvalid = "skills.directoryNameInvalid";
    public const string NameMismatch = "skills.nameMismatch";
    public const string OutsideRoot = "skills.outsideRoot";
    public const string SkillMdMissing = "skills.skillMdMissing";
    public const string DescriptionMissing = "skills.descriptionMissing";
    public const string BodyEmpty = "skills.bodyEmpty";
    public const string Frontmatter = "skills.frontmatter";
    public const string TooMany = "skills.tooMany";
    public const string PluginSkillOutside = "skills.pluginSkillOutside";
    public const string PluginNameClash = "skills.pluginNameClash";
    public const string BundledInvalid = "skills.bundledInvalid";
    public const string BundledFailed = "skills.bundledFailed";
}

/// <summary>一条加载诊断。<see cref="Params"/> 是结构化参数(名字、数量……);<b>不回显软链目标</b>(那是逃逸想探的东西)。</summary>
public sealed record SkillDiagnostic(string Path, string Key, string Message, IReadOnlyDictionary<string, string>? Params = null);

public sealed record SkillScanResult(IReadOnlyList<Skill> Skills, IReadOnlyList<SkillDiagnostic> Diagnostics);

/// <summary>
/// 一个已启用插件贡献的<b>一条</b> Skill(<c>&lt;插件包&gt;/skills/&lt;name&gt;</c>)。<see cref="Directory"/> 直接是那一条 Skill 的目录,不是含若干条的根:
/// 贡献点必须是清单里写了的那些(包里多放一个目录不能自动生效)。插件包永远在设备本机。
/// </summary>
public sealed record PluginSkillRoot(string PluginId, IExecutionEnvironment Environment, EnvPath Directory);

/// <summary>
/// 「现在有哪些插件 Skill 目录」的提供者(插件宿主以后实现;现在没有——<b>接口位置</b>)。
/// ★ 每次扫描都重新问,不缓存:用户禁用了插件,下一轮模型就不该再看见它带来的 Skill。提供者抛异常 = 当作没有(插件管理器炸了不该让所有 Skill 都扫不出来)。
/// </summary>
public interface ISkillPluginSource
{
    IReadOnlyList<PluginSkillRoot> Current();
}

public static class SkillPluginSources
{
    /// <summary>
    /// 每次扫描都重新问;提供者抛异常 = 当作没有。插件管理器坏了,用户自己装的 Skill 不该跟着一起消失——那个症状会把排查引向完全错误的方向。
    /// 没有提供者(插件宿主还没接)也是「没有」,不是错误。
    /// </summary>
    public static IReadOnlyList<PluginSkillRoot> CurrentOrEmpty(this ISkillPluginSource? source)
    {
        try
        {
            return source?.Current() ?? [];
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _ = e;
            return [];
        }
    }
}

/// <summary>
/// 扫描的输入。全局目录和项目目录各有自己的执行环境:全局在<b>设备本机</b>,项目在项目的执行环境里(可能是 SSH)。根为 null = 跳过那一层。
/// 只经 <see cref="IExecutionEnvironment"/> 读文件(AGENTS.md §5)。
/// </summary>
public sealed record SkillScanInput(
    IExecutionEnvironment? GlobalEnvironment,
    EnvPath? GlobalRoot,
    IExecutionEnvironment? ProjectEnvironment,
    EnvPath? ProjectRoot,
    IReadOnlyList<PluginSkillRoot>? PluginRoots = null)
{
    public const string SkillsDirectory = "skills";

    public static EnvPath ProjectSkillsRoot(EnvPath projectRoot) => projectRoot.Join(Ncw.Agent.Extensions.Permissions.ProjectRuleFile.DirectoryName, SkillsDirectory);

    public static EnvPath GlobalSkillsRoot(EnvPath dataRoot) => dataRoot.Join(SkillsDirectory);

    /// <summary>项目环境是远端(SSH):设备本地的非项目 Skill 若带附带文件,在这里不可用。</summary>
    public bool ProjectIsRemote => ProjectEnvironment is { Kind: EnvironmentKind.Ssh };
}
