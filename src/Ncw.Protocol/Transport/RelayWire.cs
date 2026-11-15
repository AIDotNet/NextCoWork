using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ncw.Protocol.Security;

namespace Ncw.Protocol.Transport;

/// <summary>
/// 中继承载(docs/design/09 §9)的线上约定:设备和控制端各自用 WebSocket 连到中继,中继只在三种连接上说话——
/// 设备的<b>注册连接</b>(<c>/device</c>,文本消息)、控制端的<b>接入连接</b>(<c>/connect</c>,首条文本消息出示令牌)、
/// 设备为一个会话开的<b>数据连接</b>(<c>/device/data</c>)。接入连接和数据连接在「就绪」之后只有二进制消息,由中继原样拼接,
/// 里面是端到端的 TLS 1.3(和局域网直连完全相同)。这一层是 L0 承载,<b>不属于</b> L1 以上的协议;所以它不进帧表,也不升协议版本,
/// 但格式同样由冻结向量保护(<c>GoldenRelayWire</c>)。
/// </summary>
public static class RelayProtocol
{
    /// <summary>承载约定的版本;不兼容的改动才升,新增可选字段不升。</summary>
    public const int Version = 1;

    /// <summary>设备注册连接的路径(查询串 <c>d=</c> 是设备 id,方便负载均衡器按 deviceId 粘性路由)。</summary>
    public const string DevicePath = "/device";

    /// <summary>设备数据连接的路径(<c>d=</c> 设备 id,<c>s=</c> 会话 id)。</summary>
    public const string DataPath = "/device/data";

    /// <summary>控制端接入连接的路径(<c>d=</c> 设备 id)。</summary>
    public const string ConnectPath = "/connect";

    /// <summary>注册连接上一条文本消息的上限。</summary>
    public const int MaxControlMessageBytes = 128 * 1024;

    /// <summary>拼接阶段一条二进制消息的上限(TLS 记录最大约 16 KiB;留足余量)。</summary>
    public const int MaxPipeMessageBytes = 64 * 1024;

    /// <summary>发送方把写入切成不超过这个大小的消息。</summary>
    public const int SendChunkBytes = 16 * 1024;

    /// <summary>一台设备同时登记的路由令牌哈希上限。</summary>
    public const int MaxTokensPerDevice = 1024;

    /// <summary>一台设备并发的控制端会话上限(09 §9)。</summary>
    public const int MaxSessionsPerDevice = 16;

    /// <summary>注册签名的域分隔前缀(同一把设备私钥不会被拿去给别的东西签名)。</summary>
    public const string RegisterContext = "ncw-relay-register/1";

    public const int NonceBytes = 32;

    /// <summary>路由令牌的哈希:SHA-256(ASCII 令牌)的小写十六进制。令牌是 256 位随机数,不可能由哈希反推或猜出。</summary>
    public static string HashToken(string routeToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(routeToken)));

    /// <summary>设备注册时签名的内容:域前缀 ‖ 0 ‖ nonce ‖ deviceId。</summary>
    public static byte[] RegisterPayload(byte[] nonce, string deviceId)
    {
        var context = Encoding.ASCII.GetBytes(RegisterContext);
        var id = Encoding.ASCII.GetBytes(deviceId);
        var data = new byte[context.Length + 1 + nonce.Length + id.Length];
        context.CopyTo(data, 0);
        nonce.CopyTo(data, context.Length + 1);
        id.CopyTo(data, context.Length + 1 + nonce.Length);
        return data;
    }

    /// <summary>设备用身份私钥对中继的挑战签名(ECDSA P-256 / SHA-256,IEEE P1363 格式)。</summary>
    public static byte[] Sign(PeerIdentity identity, byte[] nonce)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(identity.PrivateKey, out _);
        return key.SignData(RegisterPayload(nonce, identity.DeviceId), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>设备的公钥信息(SubjectPublicKeyInfo DER),注册时随签名一起发给中继。</summary>
    public static byte[] PublicKeyInfo(PeerIdentity identity)
    {
        using var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(identity.Certificate);
        return cert.PublicKey.ExportSubjectPublicKeyInfo();
    }

    /// <summary>
    /// 中继验证注册:<c>deviceId</c> 必须是公钥的指纹派生值,签名必须是这把公钥对 <c>nonce</c> 的签名。
    /// 任何畸形输入都返回 false,不抛异常。
    /// </summary>
    public static bool VerifyRegistration(byte[] publicKeyInfo, byte[] signature, byte[] nonce, string deviceId)
    {
        try
        {
            if (PeerIdentity.DeviceIdOf(SHA256.HashData(publicKeyInfo)) != deviceId)
            {
                return false;
            }

            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKeyInfo, out _);
            if (key.KeySize != 256)
            {
                return false;
            }

            return key.VerifyData(RegisterPayload(nonce, deviceId), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException or FormatException)
        {
            return false;
        }
    }

    /// <summary>设备 id 的格式(<c>dev_</c> + 26 位小写 base32);中继用它在做任何事之前拒掉垃圾输入。</summary>
    public static bool IsValidDeviceId(string? deviceId)
    {
        if (deviceId is not { Length: 30 } || !deviceId.StartsWith("dev_", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in deviceId.AsSpan(4))
        {
            if (!(c is >= '0' and <= '9' || c is >= 'a' and <= 'z'))
            {
                return false;
            }
        }

        return true;
    }

    public static string Serialize(RelayMessage message) => JsonSerializer.Serialize(message, RelayJson.Default.RelayMessage);

    /// <summary>解码一条文本消息;不是合法的消息返回 null。</summary>
    public static RelayMessage? Deserialize(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var message = JsonSerializer.Deserialize(utf8, RelayJson.Default.RelayMessage);
            return message?.Type is null ? null : message; // 没有 type 的不是消息
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 中继 WebSocket 的关闭状态码(4000 段,应用自定):文字原因就是 <see cref="RelayErrors"/> 里的错误码。
    /// 对端读到文本 <c>error</c> 消息优先;状态码是消息送不出去时的兜底。
    /// </summary>
    public static System.Net.WebSockets.WebSocketCloseStatus CloseStatusOf(string errorCode) => errorCode switch
    {
        "closed" => System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
        RelayErrors.TooLarge => System.Net.WebSockets.WebSocketCloseStatus.MessageTooBig,
        RelayErrors.BadRequest => System.Net.WebSockets.WebSocketCloseStatus.ProtocolError,
        RelayErrors.Unavailable => System.Net.WebSockets.WebSocketCloseStatus.EndpointUnavailable,
        _ => (System.Net.WebSockets.WebSocketCloseStatus)4000,
    };
}

/// <summary>中继错误码(控制端 / 设备收到的 <c>error.code</c>)。</summary>
public static class RelayErrors
{
    /// <summary>请求格式不对(缺字段、不是合法 JSON、设备 id 非法、出现了不该出现的文本消息)。</summary>
    public const string BadRequest = "bad_request";

    /// <summary>令牌不对 / 已被吊销,或设备的签名验不过。</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>设备没有在这台中继上注册(不在线)。</summary>
    public const string DeviceOffline = "device_offline";

    /// <summary>这台设备的并发会话已满(16)。</summary>
    public const string Busy = "busy";

    /// <summary>连接频率 / 失败次数超过限制。</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>这台设备归另一个中继实例(<c>message</c> 里是那个实例的地址)——负载均衡器没有按 deviceId 粘性路由。</summary>
    public const string Misdirected = "misdirected";

    /// <summary>设备在时限内没有接上会话。</summary>
    public const string Timeout = "timeout";

    /// <summary>中继正在关闭,或内部出错。</summary>
    public const string Unavailable = "unavailable";

    /// <summary>同一台设备在别处重新注册了,这条注册连接作废。</summary>
    public const string Replaced = "replaced";

    /// <summary>一条消息超过大小上限。</summary>
    public const string TooLarge = "too_large";

    /// <summary>空闲超时。</summary>
    public const string Idle = "idle";

    /// <summary>令牌被吊销,这条会话被中继掐断。</summary>
    public const string Revoked = "revoked";
}

/// <summary>
/// 控制消息(JSON 文本)。一个平铺的类型,<see cref="Type"/> 决定用哪些字段;没用到的字段不出现在线上(省略 null)。
/// <list type="bullet">
/// <item><c>challenge</c>(中继 → 设备):<c>v</c>、<c>nonce</c>(base64)。</item>
/// <item><c>register</c>(设备 → 中继):<c>v</c>、<c>publicKey</c>(base64 SPKI)、<c>signature</c>(base64)、<c>tokens</c>(路由令牌哈希,十六进制)。</item>
/// <item><c>registered</c>(中继 → 设备):<c>deviceId</c>。</item>
/// <item><c>tokens</c>(设备 → 中继):<c>set</c>(整体替换)或 <c>add</c> / <c>remove</c>(增量);被移除的哈希对应的会话立即被掐断。</item>
/// <item><c>session</c>(中继 → 设备):<c>session</c>(一次性会话 id)、<c>tokenHash</c>(控制端出示的令牌的哈希,设备据此核对「这个令牌属于 TLS 里的那个控制端」)。</item>
/// <item><c>connect</c>(控制端 → 中继):<c>v</c>、<c>token</c>。</item>
/// <item><c>ready</c>(中继 → 控制端):数据连接已接上,之后只有二进制消息。</item>
/// <item><c>error</c>(中继 → 任一方):<c>code</c>(<see cref="RelayErrors"/>)、<c>message</c>(给人看的,可能没有)。</item>
/// </list>
/// </summary>
public sealed record RelayMessage(string Type)
{
    public int? V { get; init; }

    public string? Nonce { get; init; }

    public string? PublicKey { get; init; }

    public string? Signature { get; init; }

    public string[]? Tokens { get; init; }

    public string[]? Set { get; init; }

    public string[]? Add { get; init; }

    public string[]? Remove { get; init; }

    public string? DeviceId { get; init; }

    public string? Session { get; init; }

    public string? TokenHash { get; init; }

    public string? Token { get; init; }

    public string? Code { get; init; }

    public string? Message { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RelayMessage))]
internal sealed partial class RelayJson : JsonSerializerContext;

/// <summary>经中继连接失败。<see cref="Code"/> 是 <see cref="RelayErrors"/> 之一(连不上中继本身用 <see cref="RelayErrors.Unavailable"/>)。</summary>
public sealed class RelayException(string code, string message, Exception? inner = null) : IOException(message, inner)
{
    public string Code { get; } = code;
}
