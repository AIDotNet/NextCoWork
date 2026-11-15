namespace Ncw.Agent.Plugins;

/// <summary>
/// 插件能力全表(2.x <c>shared/plugin/permission.ts</c> 原样)。每一项对应宿主 API 里一条<b>参数门</b>(<see cref="HostGuards"/>),写类(<see cref="IsMutating"/>)还要过审批。
/// 值就是线上和清单里的字符串。
/// </summary>
public static class PluginPermissions
{
    public const string WorkspaceRead = "workspace.read";
    public const string WorkspaceWrite = "workspace.write";
    public const string Process = "process";
    public const string Net = "net";
    public const string Secrets = "secrets";
    public const string Storage = "storage";
    public const string ScmRead = "scm.read";
    public const string ScmWrite = "scm.write";
    public const string AgentIntercept = "agent.intercept";
    public const string AgentContext = "agent.context";
    public const string Clipboard = "clipboard";
    public const string WindowNotify = "window.notify";
    public const string TabsBrowser = "tabs.browser";
    public const string Plugins = "plugins";

    /// <summary>枚举里的书写顺序:同时也是授权界面里各行的顺序(Set 的迭代顺序取决于插入顺序,界面上条目会换位置,用户没法靠肌肉记忆确认批的是同一份东西)。</summary>
    public static readonly IReadOnlyList<string> All =
    [
        WorkspaceRead, WorkspaceWrite, Process, Net, Secrets, Storage, ScmRead, ScmWrite, AgentIntercept, AgentContext, Clipboard, WindowNotify, TabsBrowser, Plugins,
    ];

    private static readonly HashSet<string> Known = new(All, StringComparer.Ordinal);

    public static bool IsValid(string? value) => value is not null && Known.Contains(value);

    /// <summary>「写类」:会改文件 / 起进程 / 改仓库,必须过审批,不能被「只读」声明洗白。</summary>
    public static bool IsMutating(string permission) => permission is WorkspaceWrite or Process or ScmWrite;

    /// <summary>按枚举里的书写顺序排,去重。</summary>
    public static IReadOnlyList<string> Sort(IEnumerable<string> permissions)
    {
        var set = new HashSet<string>(permissions, StringComparer.Ordinal);
        return [.. All.Where(set.Contains)];
    }
}

/// <summary>
/// 一个插件此刻的授权状态。<see cref="Required"/> / <see cref="Optional"/> 来自<b>清单</b>(装的时候定死,升级时重新比对),<see cref="Granted"/> 来自<b>用户</b>。
/// 三者分开存:「清单要什么」和「用户给了什么」必须能各自回答——合成一个数组之后升级时就没法回答「这次新增了哪几条」。
/// <para>
/// ★ 铁律:<b>运行期能要到的东西 = 清单里 required ∪ optional 之内的</b>(<see cref="CanRequest"/>)。越界时不弹窗,否则「市场审核看到的上界」就不等于「运行期可获得的上界」,
/// 一次静默的自动更新就能把只读插件变成能跑命令的插件。
/// </para>
/// </summary>
public sealed record PluginPermissionState(IReadOnlyList<string> Required, IReadOnlyList<string> Optional, IReadOnlyList<string> Granted)
{
    public static PluginPermissionState Empty { get; } = new([], [], []);

    public IReadOnlySet<string> Ceiling => new HashSet<string>(Required.Concat(Optional), StringComparer.Ordinal);

    public bool CanRequest(IEnumerable<string> permissions)
    {
        var ceiling = Ceiling;
        return permissions.All(ceiling.Contains);
    }

    /// <summary>已经批了吗。「全都批了」才算(一次要多条时缺一条就是没有)。</summary>
    public bool Has(string permission) => Granted.Contains(permission, StringComparer.Ordinal);

    public bool HasAll(IEnumerable<string> permissions) => permissions.All(Has);

    /// <summary>授予。越过上界的那些静默丢弃(调用方已经先过 <see cref="CanRequest"/> 了)。</summary>
    public PluginPermissionState Grant(IEnumerable<string> permissions)
    {
        var ceiling = Ceiling;
        var granted = new HashSet<string>(Granted, StringComparer.Ordinal);
        foreach (var permission in permissions)
        {
            if (ceiling.Contains(permission))
            {
                granted.Add(permission);
            }
        }

        return this with { Granted = PluginPermissions.Sort(granted) };
    }

    public PluginPermissionState Revoke(IEnumerable<string> permissions)
    {
        var drop = new HashSet<string>(permissions, StringComparer.Ordinal);
        return this with { Granted = [.. Granted.Where(p => !drop.Contains(p))] };
    }
}

public static class PluginPermissionRules
{
    /// <summary>
    /// 升级时的<b>扩权判定</b>:新版本的必选能力里有旧版本没有的。只看 <c>required</c>(<c>optional</c> 变大不构成扩权:它仍然要用户在运行期点头,而必选是「不批就不激活」)。
    /// 判定为真时插件进入 <c>pending-approval</c>:用户不批不激活——堵的是「装完之后自动更新悄悄扩权」那条路。
    /// </summary>
    public static bool Escalated(IEnumerable<string> previous, IEnumerable<string> next)
    {
        var had = new HashSet<string>(previous, StringComparer.Ordinal);
        return next.Any(p => !had.Contains(p));
    }

    /// <summary>新增了哪几条(授权界面要逐条列出来,而不是只说「权限有变化」)。</summary>
    public static IReadOnlyList<string> Added(IEnumerable<string> previous, IEnumerable<string> next)
    {
        var had = new HashSet<string>(previous, StringComparer.Ordinal);
        return [.. next.Where(p => !had.Contains(p))];
    }
}
