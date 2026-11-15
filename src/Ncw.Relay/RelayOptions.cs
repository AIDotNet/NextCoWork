using Microsoft.Extensions.Logging;
using Ncw.Protocol.Transport;

namespace Ncw.Relay;

/// <summary>某个方向的字节被中继转发(只给测试:断言中继看到的全是密文)。生产不设。</summary>
public delegate void RelayTap(string deviceId, bool towardDevice, ReadOnlyMemory<byte> bytes);

/// <summary>一个速率限制:<see cref="Limit"/> 次 / <see cref="Window"/>(滑动窗口,近似)。</summary>
public readonly record struct RateRule(int Limit, TimeSpan Window);

/// <summary>
/// 中继的全部可调项(默认值就是 09 §9 的限制:每台设备 16 个并发控制端、带宽与空闲超时上限、连接频率限制)。
/// 生产部署通过环境变量 / 配置覆盖(见 <see cref="RelayConfig"/>);测试直接构造。
/// </summary>
public sealed class RelayOptions
{
    /// <summary>监听地址。生产在负载均衡 / 反向代理后面用 http,TLS 由部署层终止,或者配置证书(<see cref="CertificatePath"/>)。</summary>
    public string[] Urls { get; init; } = ["http://0.0.0.0:8080"];

    /// <summary>PEM 证书(链)路径;配了就让 Kestrel 自己终止 TLS(<c>https://</c> 的 <see cref="Urls"/>)。</summary>
    public string? CertificatePath { get; init; }

    public string? CertificateKeyPath { get; init; }

    /// <summary>一条拼接中的二进制消息的上限;更大的消息会让这条会话以 <c>too_large</c> 关闭。</summary>
    public int MaxMessageBytes { get; init; } = RelayProtocol.MaxPipeMessageBytes;

    /// <summary>设备注册的挑战 / 控制端出示令牌的首条消息的时限。</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>通知设备后,等它接上数据连接的时限;超时控制端得到 <c>timeout</c>。</summary>
    public TimeSpan DeviceAcceptTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>一条会话双向都没有数据的最长时间(L3 每 20 秒心跳,所以正常连接不会触发)。</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>一台设备并发的控制端会话(含等待设备接入的)上限。</summary>
    public int MaxSessionsPerDevice { get; init; } = RelayProtocol.MaxSessionsPerDevice;

    /// <summary>同时登记的设备上限(内存保护)。</summary>
    public int MaxDevices { get; init; } = 200_000;

    /// <summary>每个来源 IP 同时打开的 WebSocket 连接上限(控制端接入、设备注册、设备数据连接合计)。</summary>
    public int MaxConnectionsPerIp { get; init; } = 128;

    /// <summary>每个来源 IP 的新连接速率(所有路径合计)。</summary>
    public RateRule ConnectsPerIp { get; init; } = new(240, TimeSpan.FromMinutes(1));

    /// <summary>每个来源 IP 的设备注册速率。</summary>
    public RateRule RegistersPerIp { get; init; } = new(60, TimeSpan.FromMinutes(1));

    /// <summary>每个来源 IP 的失败次数(令牌不对 / 签名不对 / 数据连接的会话 id 不对)。超过后这个 IP 在窗口内的所有尝试都被拒绝,<b>包括令牌正确的</b>——先于比较,这样才能限制暴力尝试。</summary>
    public RateRule FailuresPerIp { get; init; } = new(20, TimeSpan.FromMinutes(1));

    /// <summary>每台设备被尝试的失败次数(分布式暴力尝试的兜底)。令牌是 256 位随机数,暴力在数学上无望;这条只是保护设备不被刷。</summary>
    public RateRule FailuresPerDevice { get; init; } = new(300, TimeSpan.FromMinutes(1));

    /// <summary>每台设备接受的控制端接入速率(合法的也算)。</summary>
    public RateRule ConnectsPerDevice { get; init; } = new(600, TimeSpan.FromMinutes(1));

    /// <summary>每台设备在注册连接上发控制消息(更新令牌)的速率;设备只在配对 / 吊销时才发,超过说明出了问题,中继关掉这条注册连接。</summary>
    public RateRule ControlMessagesPerDevice { get; init; } = new(60, TimeSpan.FromMinutes(1));

    /// <summary>每条会话的带宽上限(字节 / 秒,两个方向合计);0 = 不限。</summary>
    public long MaxBytesPerSecondPerSession { get; init; } = 16L * 1024 * 1024;

    /// <summary>在可信反向代理后面时,用 <c>X-Forwarded-For</c> 的最后一项(代理追加的那一项)作为来源 IP。直接暴露在公网时必须保持 false。</summary>
    public bool TrustForwardedFor { get; init; }

    /// <summary><c>/metrics</c> 默认只接受本机 / 内网来源;设为 true 才对所有来源开放(不建议)。</summary>
    public bool MetricsPublic { get; init; }

    /// <summary>按 deviceId 粘性路由的位置判断(默认单实例,全部在本机)。</summary>
    public IRelayPlacement Placement { get; init; } = SingleInstancePlacement.Instance;

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>只给测试:记录转发的所有字节。</summary>
    public RelayTap? Tap { get; init; }

    /// <summary>测试和部署层用它接日志(默认控制台,单行结构化)。</summary>
    public Action<ILoggingBuilder>? ConfigureLogging { get; init; }
}
