namespace Ncw.Agent.Plugins;

/// <summary>
/// 一个已安装插件此刻的状态。
/// <list type="bullet">
/// <item><c>Idle</c>:装上了、启用着,没有运行时(零代码插件,或还没轮到激活)。</item>
/// <item><c>Active</c>:JS 已激活,贡献的命令 / 工具 / 拦截器在线。</item>
/// <item><c>Disabled</c>:用户关了。</item>
/// <item><c>PendingApproval</c>:升级后必选能力变大,用户不批不激活(堵住「自动更新悄悄扩权」)。</item>
/// <item><c>Error</c>:装载失败(清单读不懂 / engines 不匹配 / 入口缺失 / 激活失败)。</item>
/// <item><c>Faulted</c>:运行期崩溃或超出预算(死循环、内存、指令数):引擎已丢弃,<b>不拖垮 Agent</b>;用户重新启用才会再试一次。</item>
/// </list>
/// </summary>
public enum PluginStatus
{
    Idle,
    Activating,
    Active,
    Disabled,
    PendingApproval,
    Error,
    Faulted,
}

public static class PluginStatusNames
{
    public static string Of(PluginStatus status) => status switch
    {
        PluginStatus.Idle => "idle",
        PluginStatus.Activating => "activating",
        PluginStatus.Active => "active",
        PluginStatus.Disabled => "disabled",
        PluginStatus.PendingApproval => "pending-approval",
        PluginStatus.Error => "error",
        _ => "faulted",
    };
}

/// <summary>一条插件诊断。<see cref="Level"/>:<c>info</c> / <c>warn</c> / <c>error</c>。<see cref="Path"/> 是出问题的地方(字段路径、包内文件、宿主方法名)。</summary>
public sealed record PluginDiagnostic(string Path, string Key, string Message, string Level, IReadOnlyDictionary<string, string>? Params = null);

/// <summary>
/// 活动日志的一条(每一次宿主 API 调用记一条)。<see cref="Summary"/> 是人话摘要,<b>不含参数原文</b>(参数里可能有文件内容、token)。
/// <see cref="Verdict"/>:<c>ok</c> / <c>denied</c> / <c>error</c> / <c>timeout</c>。
/// </summary>
public sealed record PluginActivity(long Ts, string PluginId, string Method, string Summary, string Verdict, long DurationMs);

/// <summary>界面上的一个已安装插件(只读投影,没有任何句柄)。</summary>
public sealed record InstalledPlugin(
    PluginManifest Manifest,
    PluginStatus Status,
    bool Enabled,
    string Path,
    PluginPermissionState Permissions,
    IReadOnlyList<PluginDiagnostic> Diagnostics,
    long InstalledAt,
    long UpdatedAt,
    IReadOnlyList<string> PendingPermissions,
    string? Slug,
    IReadOnlyList<string> RegisteredTools,
    IReadOnlyList<string> RegisteredCommands,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Messages)
{
    public string Id => Manifest.Id;
}

/// <summary>
/// 插件在等用户回答的一个请求(<c>window.showConfirm</c> / <c>showInputBox</c> / <c>showQuickPick</c>、运行期 <c>permissions.request</c>、<c>process.exec</c> 的执行审批)。
/// 经 <c>plugins</c> 主题广播,用 <c>plugins.interactionReply</c> 回答。<b>涉及执行的审批带执行环境标识(设备 + 环境,AGENTS.md §5)</b>。
/// </summary>
public sealed record PluginInteraction(
    string RequestId,
    string PluginId,
    string Kind,
    string? TitleKey,
    string? DetailKey,
    string? PlaceholderKey,
    IReadOnlyList<PluginPickItem> Items,
    bool Password,
    bool Danger,
    string? Initial,
    IReadOnlyList<string> Permissions,
    string? Command,
    PluginEnvironmentRef? Environment,
    long CreatedAt);

public sealed record PluginPickItem(string Id, string LabelKey);

/// <summary>审批所指的执行环境(设备 + 环境 + 名字 + 类型)。</summary>
public sealed record PluginEnvironmentRef(string DeviceId, string Environment, string Name, string Type);

public static class PluginInteractionKinds
{
    public const string Confirm = "confirm";
    public const string Input = "input";
    public const string Pick = "pick";
    public const string Permissions = "permissions";
    public const string Exec = "exec";
}

/// <summary>用户对一个 <see cref="PluginInteraction"/> 的回答。取消 = 全部为 null / false(不是错误:没理会一个弹窗不是故障)。</summary>
public sealed record PluginInteractionReply(bool? Confirmed = null, string? Text = null, string? PickedId = null);

/// <summary><c>plugins</c> 主题的全量状态(首帧、<c>plugins.changed</c> 与 <c>plugins.list</c> 是同一个类型):所有插件(按 id 排序) + 挂起的交互,<see cref="Rev"/> 单调。</summary>
public sealed record PluginsSnapshot(long Rev, IReadOnlyList<InstalledPlugin> Plugins, IReadOnlyList<PluginInteraction> Interactions)
{
    public static PluginsSnapshot Empty { get; } = new(0, [], []);
}

/// <summary>「这个已装的插件有新版」(2.x <c>PluginUpdate</c>)。<see cref="EscalatedPermissions"/> 用 <c>approvedRequired</c> 算(不是 granted:后者是超集)。</summary>
public sealed record PluginUpdate(string PluginId, string Slug, string DisplayName, string CurrentVersion, string LatestVersion, IReadOnlyList<string> EscalatedPermissions, bool FromMarket);

public sealed record PluginUpdateFailure(string PluginId, string ErrorKey);

public sealed record PluginUpdateResult(IReadOnlyList<string> Updated, IReadOnlyList<PluginUpdateFailure> Failed);

/// <summary>配置项的一个值(2.x:<c>boolean | string | number</c>)。</summary>
public readonly record struct PluginConfigValue(string? Text, double? Number, bool? Flag)
{
    public static PluginConfigValue Of(string value) => new(value, null, null);

    public static PluginConfigValue Of(double value) => new(null, value, null);

    public static PluginConfigValue Of(bool value) => new(null, null, value);

    public bool IsEmpty => Text is null && Number is null && Flag is null;
}

/// <summary>安装进度(市场安装 / zip 安装共用):<c>preparing</c> → <c>downloading</c> → <c>installing</c> → <c>done</c> / <c>failed</c>。</summary>
public sealed record PluginInstallProgress(string Phase, long? Received = null, long? Total = null, string? ErrorKey = null);
