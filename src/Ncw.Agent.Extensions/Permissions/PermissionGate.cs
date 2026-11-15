namespace Ncw.Agent.Extensions.Permissions;

public enum GateKind
{
    Allow,
    Ask,
    Deny,
}

/// <summary>
/// 档位闸门的结论。刻意和用户的回答(<see cref="Interactions.PermissionChoice"/>)分成两个类型:
/// <c>allow_always</c> / <c>allow_edited</c> 是<b>用户的回答</b>,闸门永远不可能产出它们——混成一个类型,「谁有资格说 always」在类型上就看不出来了。
/// </summary>
public readonly record struct GateOutcome(GateKind Kind, string? Reason = null)
{
    public static GateOutcome Allow { get; } = new(GateKind.Allow);

    public static GateOutcome Ask { get; } = new(GateKind.Ask);

    public static GateOutcome Deny(string reason) => new(GateKind.Deny, reason);
}

/// <summary>
/// <paramref name="NeedsNetwork"/> 是「受联网开关管的工具」(<see cref="PermissionGate.NetworkSwitchTools"/>),<b>不是</b> <c>ToolTraits.NeedsNetwork</c>:
/// 字段名沿用旧名,语义已随开关收窄(2.x 2026-09-27 的决定)。
/// </param>
public readonly record struct PermissionQuery(PermissionMode Mode, bool ReadOnly, bool Destructive, bool WebSearch, bool NeedsNetwork = false);

/// <summary>权限闸门的<b>策略</b>:只有那张表,没有任何机制(2.x <c>kernel/permission-gate.ts</c>)。策略判断不持有待决状态,也不把询问自动降级为拒绝。</summary>
public static class PermissionGate
{
    /// <summary>
    /// 联网被关掉时给模型的说法。必须说清「这是用户关的开关」,否则模型会去试 Bash 里的 curl——那是同一件事换个入口,而且绕过了这道闸。
    /// </summary>
    public const string NetworkOff =
        "This tool needs web access, but the \"web search\" switch is turned off for this workspace, so the call was denied. "
        + "This is the user's setting, not a mistake in how you called it. Do NOT reach for curl or wget through Bash to do the same thing — "
        + "that would route around a switch the user turned off deliberately. Tell the user this step needs web access and that they can turn on the "
        + "\"web search\" switch in the composer.";

    /// <summary>
    /// 受「联网搜索」开关管的工具的 <c>InternalId</c>——<b>精确名单</b>,不是下限表(2.x 用户决定,2026-09-27):开关只管网页搜索与网页抓取,
    /// 生图有自己的开关,其余工具不受它控制。两处消费者必须读同一张表:工具快照(不下发)和权限扩展(调用时拒)。
    /// </summary>
    public static IReadOnlySet<string> NetworkSwitchTools { get; } = new HashSet<string>(StringComparer.Ordinal) { "WebFetch", "web_search" };

    /// <summary>
    /// 那张表。<b>顺序即语义</b>:
    /// <list type="number">
    /// <item><c>NeedsNetwork &amp;&amp; !WebSearch</c> → 拒绝。必须排在只读之前:只读的联网工具(WebFetch)在开关关掉时仍然要拒;<c>full</c> 档也放宽不了它。</item>
    /// <item>只读 → 放行。三档都放行读:每读一个文件弹一次窗,用户 30 秒内就学会无脑点「允许」,那比不弹更危险。</item>
    /// <item><c>ask</c> → 问。写与执行才问。</item>
    /// <item><c>auto</c> 且破坏性 → 问。</item>
    /// <item>其余(<c>auto</c> 非破坏 / <c>full</c>)→ 放行。</item>
    /// </list>
    /// </summary>
    public static GateOutcome Evaluate(PermissionQuery q)
    {
        if (q.NeedsNetwork && !q.WebSearch)
        {
            return GateOutcome.Deny(NetworkOff);
        }

        if (q.ReadOnly)
        {
            return GateOutcome.Allow;
        }

        if (q.Mode == PermissionMode.Ask)
        {
            return GateOutcome.Ask;
        }

        if (q.Mode == PermissionMode.Auto && q.Destructive)
        {
            return GateOutcome.Ask;
        }

        return GateOutcome.Allow;
    }

    /// <summary>
    /// 系统提示词里「权限事实」那几行(2.x <c>permissionFacts</c>)。放在这里、跟着上面那张表走:两处必须给出<b>同一个答案</b>——
    /// 提示词说「写盘会被拒」而闸门其实放行(或者反过来)是最坏的一种漂移:模型会据此提前放弃一件它本来做得成的事,而这中间不会有任何报错。
    /// </summary>
    public static string Facts(PermissionMode mode, bool webSearch)
    {
        string Say(bool readOnly, bool destructive) => Evaluate(new PermissionQuery(mode, readOnly, destructive, webSearch)).Kind switch
        {
            GateKind.Allow => "run without asking",
            GateKind.Ask => "wait for user approval before execution",
            _ => "are denied",
        };

        // 只说「搜索与抓取」这两个:开关现在只管它们。
        var net = webSearch
            ? "available"
            : "denied — the network switch is off for this workspace, and Bash cannot be used to route around it";
        return $"Permission mode: {mode.ToWire()}\n"
            + $"- Reading and searching: {Say(true, false)}\n"
            + $"- Writing files and running commands: {Say(false, true)}\n"
            + "- File tools are not fenced to the workspace: an absolute path anywhere on this machine resolves, and the rules above are what governs it\n"
            + $"- Web search and web fetch ({string.Join(", ", NetworkSwitchTools.Order(StringComparer.Ordinal))}): {net}";
    }
}
