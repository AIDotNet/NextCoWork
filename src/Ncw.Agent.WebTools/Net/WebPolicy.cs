namespace Ncw.Agent.WebTools.Net;

/// <summary>
/// 联网出口的策略。<see cref="AllowPrivateAddresses"/> 只给测试和「明确要碰本机服务」的受控装配用(例如端到端测试里的本机假上游),<b>不是</b>设置项、
/// 不经协议暴露:默认永远是 <c>false</c>,模型发起的请求碰不到环回 / 内网 / 链路本地 / 元数据地址。
/// </summary>
public sealed record WebPolicy
{
    public static WebPolicy Default { get; } = new();

    /// <summary>放行环回 / 内网地址(URL 字面量筛查与连接时的 IP 校验一并放行)。生产恒为 false。</summary>
    public bool AllowPrivateAddresses { get; init; }

    /// <summary>名字解析的超时。</summary>
    public TimeSpan ResolveTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>单个连接的建立超时。</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
