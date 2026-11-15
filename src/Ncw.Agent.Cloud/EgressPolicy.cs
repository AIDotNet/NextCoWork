namespace Ncw.Agent.Cloud;

/// <summary>出网的用途(出网策略按它区分「哪些出口受白名单约束」)。</summary>
public enum EgressKind
{
    /// <summary>模型 API(供应商 / nextco.work 模型通道)。</summary>
    Model,

    /// <summary>WebFetch / web_search 工具。</summary>
    WebFetch,

    /// <summary>浏览器工具打开的页面。</summary>
    Browser,

    /// <summary>MCP 服务器(HTTP 传输)。</summary>
    Mcp,

    /// <summary>SSH 环境的目标主机。</summary>
    SshTarget,

    /// <summary>Git 远程(克隆 / 拉取 / 推送)。</summary>
    GitRemote,

    /// <summary>插件的 <c>net.fetch</c>。</summary>
    Plugin,
}

public sealed record EgressRequest(EgressKind Kind, string Host, int? Port = null);

public sealed record EgressDecision(bool Allowed, string? Reason = null)
{
    public static readonly EgressDecision Allow = new(true);
}

/// <summary>
/// 出网策略钩子(02 A6「隔离与安全:出网走白名单加审计」、05 B12):Agent 在发起出网之前问一句「这个目标可以吗」。
/// <para>
/// ★ <b>真正的出网白名单与固定出口 IP 是基础设施</b>(沙箱的网络策略、出口网关),这里不重复实现它;这个钩子只给 Agent 一个<b>提前、结构化地拒绝</b>的机会
/// (用户看到的是 <c>cloud.egress.denied</c> 加目标主机,而不是一次莫名其妙的连接超时),并给审计留一个口子(<see cref="Audit"/>)。
/// 本机模式用 <see cref="AllowAllEgress"/>,行为不变。
/// </para>
/// 接线状态(见 docs/design/02 A6):<b>Git 克隆</b>已接;WebFetch / 浏览器 / MCP / SSH 目标 / 插件 <c>net.fetch</c> 这些出口的接线点是各自的 HTTP 出口
/// (<c>IWebHttp</c>、<c>IBrowserLauncher</c>、<c>IMcpHttpFactory</c>、SSH 连接、<c>IPluginHttp</c>),后续在云端装配时把本接口包进去,不用改它们的业务代码。
/// </summary>
public interface IEgressPolicy
{
    EgressDecision Evaluate(EgressRequest request);
}

public sealed class AllowAllEgress : IEgressPolicy
{
    public static readonly AllowAllEgress Instance = new();

    public EgressDecision Evaluate(EgressRequest request) => EgressDecision.Allow;
}

/// <summary>
/// 主机白名单:<c>allowedHosts</c> 里的精确主机名(<c>github.com</c>)或通配(<c>*.github.com</c>,不含 <c>github.com</c> 自己),
/// 只约束 <c>enforceFor</c> 里列出的用途(空 = 全部用途)。不在白名单里的目标被拒绝。每次决定都经 <paramref name="audit"/>(可空)记录:只有用途、主机、结论,没有路径 / 查询 / 头。
/// </summary>
public sealed class HostAllowlistEgress(IReadOnlyCollection<string> allowedHosts, IReadOnlyCollection<EgressKind>? enforceFor = null, Action<EgressRequest, EgressDecision>? audit = null) : IEgressPolicy
{
    private readonly string[] _allowed = [.. allowedHosts.Select(static h => h.Trim().ToLowerInvariant())];

    public EgressDecision Evaluate(EgressRequest request)
    {
        EgressDecision decision;
        if (enforceFor is { Count: > 0 } kinds && !kinds.Contains(request.Kind))
        {
            decision = EgressDecision.Allow;
        }
        else
        {
            var host = request.Host.Trim().TrimEnd('.').ToLowerInvariant();
            decision = _allowed.Any(pattern => Matches(pattern, host)) ? EgressDecision.Allow : new EgressDecision(false, "host-not-allowed");
        }

        audit?.Invoke(request, decision);
        return decision;
    }

    private static bool Matches(string pattern, string host) =>
        pattern.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(pattern[1..], StringComparison.Ordinal) && host.Length > pattern.Length - 1
            : host == pattern;

    /// <summary>从启动配置的 <c>egress</c> 段建;没配 <c>allowedHosts</c> = 不约束。不认识的用途名返回 null(配置错误)。</summary>
    public static IEgressPolicy? FromConfig(CloudEgressFile? file, Action<EgressRequest, EgressDecision>? audit = null)
    {
        if (file?.AllowedHosts is not { Length: > 0 } hosts)
        {
            return AllowAllEgress.Instance;
        }

        var kinds = new List<EgressKind>();
        foreach (var name in file.EnforceFor ?? [])
        {
            if (!Enum.TryParse<EgressKind>(name, ignoreCase: true, out var kind))
            {
                return null;
            }

            kinds.Add(kind);
        }

        return new HostAllowlistEgress(hosts, kinds, audit);
    }
}
