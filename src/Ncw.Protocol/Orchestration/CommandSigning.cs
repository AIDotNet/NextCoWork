using System.Security.Cryptography;
using System.Text;
using Ncw.Protocol.Security;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Ncw.Protocol.Orchestration;

/// <summary>
/// 编排密钥集(设备信任的验证公钥,<c>kid → Ed25519 公钥</c>;编排服务轮换密钥时新旧 kid 并存一段时间)。公钥是 32 字节原始公钥的 base64
/// (和 <c>tools/Ncw.Build</c> 的发布清单签名同一种形式)。公钥是公开信息,但来源必须可信:只能来自编排服务注入的启动配置,不能来自控制通道本身。
/// </summary>
public sealed class OrchestratorKeys
{
    private readonly Dictionary<string, byte[]> _keys;

    public OrchestratorKeys(IReadOnlyDictionary<string, byte[]> keys)
    {
        _keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (kid, key) in keys)
        {
            if (kid.Length == 0 || key.Length != 32)
            {
                throw new ArgumentException("An orchestrator key needs a key id and a 32-byte Ed25519 public key.", nameof(keys));
            }

            _keys[kid] = [.. key];
        }
    }

    public int Count => _keys.Count;

    public IEnumerable<string> KeyIds => _keys.Keys;

    public bool TryGet(string kid, out byte[] key) => _keys.TryGetValue(kid, out key!);

    /// <summary>解析 <c>kid:base64,kid:base64</c>;任何一项不合法返回 false(整个配置作废,不部分采用)。</summary>
    public static bool TryParse(string? text, out OrchestratorKeys keys)
    {
        keys = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = item.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || colon == item.Length - 1)
            {
                return false;
            }

            var bytes = new byte[32];
            if (!Convert.TryFromBase64String(item[(colon + 1)..], bytes, out var written) || written != 32)
            {
                return false;
            }

            map[item[..colon]] = bytes;
        }

        if (map.Count == 0)
        {
            return false;
        }

        keys = new OrchestratorKeys(map);
        return true;
    }
}

/// <summary>签名与验证(Ed25519,BouncyCastle:.NET 没有内置 Ed25519,仓库里本来就固定了 BouncyCastle)。永远不抛异常给调用方验证路径。</summary>
public static class EnvelopeSigning
{
    /// <summary>被签名的字节:域前缀 ‖ 0x00 ‖ payload。</summary>
    public static byte[] SignedBytes(string context, ReadOnlySpan<byte> payload)
    {
        var prefix = Encoding.ASCII.GetBytes(context);
        var data = new byte[prefix.Length + 1 + payload.Length];
        prefix.CopyTo(data, 0);
        payload.CopyTo(data.AsSpan(prefix.Length + 1));
        return data;
    }

    public static SignedEnvelope Sign(string context, string kid, byte[] payload, byte[] privateSeed)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(privateSeed, 0));
        var data = SignedBytes(context, payload);
        signer.BlockUpdate(data, 0, data.Length);
        return new SignedEnvelope(OrchestrationProtocol.Version, kid, Convert.ToBase64String(payload), Convert.ToBase64String(signer.GenerateSignature()));
    }

    /// <summary>验签并返回载荷字节。失败返回错误码(<see cref="RejectCodes"/>),载荷为 null。</summary>
    public static string? Open(SignedEnvelope? envelope, string context, OrchestratorKeys keys, out byte[]? payload)
    {
        payload = null;
        if (envelope is null || envelope.V != OrchestrationProtocol.Version || string.IsNullOrEmpty(envelope.Kid)
            || string.IsNullOrEmpty(envelope.Payload) || string.IsNullOrEmpty(envelope.Sig)
            || envelope.Payload.Length > OrchestrationProtocol.MaxEnvelopeBytes)
        {
            return RejectCodes.BadFormat;
        }

        byte[] body, signature;
        try
        {
            body = Convert.FromBase64String(envelope.Payload);
            signature = Convert.FromBase64String(envelope.Sig);
        }
        catch (FormatException)
        {
            return RejectCodes.BadFormat;
        }

        if (!keys.TryGet(envelope.Kid, out var key))
        {
            return RejectCodes.UnknownKey;
        }

        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(key, 0));
            var data = SignedBytes(context, body);
            verifier.BlockUpdate(data, 0, data.Length);
            if (signature.Length != 64 || !verifier.VerifySignature(signature))
            {
                return RejectCodes.BadSignature;
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Org.BouncyCastle.Crypto.CryptoException)
        {
            return RejectCodes.BadSignature;
        }

        payload = body;
        return null;
    }

    public static (string PrivateSeed, string PublicKey) GenerateKeyPair()
    {
        var generator = new Org.BouncyCastle.Crypto.Generators.Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new Org.BouncyCastle.Security.SecureRandom()));
        var pair = generator.GenerateKeyPair();
        return (Convert.ToBase64String(((Ed25519PrivateKeyParameters)pair.Private).GetEncoded()), Convert.ToBase64String(((Ed25519PublicKeyParameters)pair.Public).GetEncoded()));
    }
}

/// <summary>
/// 编排服务一侧的签发工具(私钥只在编排服务;这里放一份是为了「协议定义只有一个来源」,也给测试里的假编排服务用)。
/// </summary>
public sealed class CommandSigner(string kid, string privateSeedBase64)
{
    private readonly byte[] _seed = Convert.FromBase64String(privateSeedBase64);

    public SignedEnvelope Sign(CommandBody body) => EnvelopeSigning.Sign(OrchestrationProtocol.CommandContext, kid, OrchestrationWire.Encode(body), _seed);

    public SignedEnvelope Sign(DeviceBinding binding) =>
        EnvelopeSigning.Sign(OrchestrationProtocol.BindingContext, kid, OrchestrationWire.Encode(binding, OrchestrationJson.Default.DeviceBinding), _seed);
}

/// <summary>一条指令的验证结果:要么是可以应用的指令体,要么是拒绝原因。</summary>
public readonly record struct CommandCheck(CommandBody? Body, string? RejectCode, long Seq)
{
    public bool Ok => Body is not null;
}

/// <summary>
/// 指令验证(设备一侧):验签 → 解析 → 类型已知 → 发给本设备 → 在有效期内。序号防重放由 <see cref="ReplayGuard"/> 单独负责(它需要持久化)。
/// </summary>
public sealed class CommandVerifier(OrchestratorKeys keys, string deviceId, TimeProvider clock)
{
    public CommandCheck Verify(SignedEnvelope? envelope)
    {
        if (EnvelopeSigning.Open(envelope, OrchestrationProtocol.CommandContext, keys, out var payload) is { } failure)
        {
            return new CommandCheck(null, failure, 0);
        }

        var body = OrchestrationWire.DecodeCommand(payload!);
        if (body is null || body.V != OrchestrationProtocol.Version || body.Seq <= 0)
        {
            return new CommandCheck(null, RejectCodes.BadFormat, 0);
        }

        if (!CommandTypes.IsKnown(body.Type))
        {
            return new CommandCheck(null, RejectCodes.UnknownType, body.Seq);
        }

        if (!string.Equals(body.DeviceId, deviceId, StringComparison.Ordinal))
        {
            return new CommandCheck(null, RejectCodes.WrongDevice, body.Seq);
        }

        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var skew = (long)OrchestrationProtocol.ClockSkew.TotalMilliseconds;
        if (body.ExpiresAt <= now - skew)
        {
            return new CommandCheck(null, RejectCodes.Expired, body.Seq);
        }

        if (body.IssuedAt > now + skew)
        {
            return new CommandCheck(null, RejectCodes.NotYetValid, body.Seq);
        }

        return new CommandCheck(body, null, body.Seq);
    }

    /// <summary>验证身份绑定;失败返回 null。</summary>
    public DeviceBinding? OpenBinding(SignedEnvelope? envelope)
    {
        if (EnvelopeSigning.Open(envelope, OrchestrationProtocol.BindingContext, keys, out var payload) is not null)
        {
            return null;
        }

        var binding = OrchestrationWire.Decode(payload!, OrchestrationJson.Default.DeviceBinding);
        return binding is not null && string.Equals(binding.DeviceId, deviceId, StringComparison.Ordinal) ? binding : null;
    }
}

/// <summary>
/// 序号防重放:接受严格大于「已应用的最大序号」的序号。<see cref="Last"/> 由调用方持久化(设备重启、休眠唤醒之后仍然有效);
/// <see cref="TryAdvance"/> 是原子的,并发的两条同序号指令只有一条通过。
/// </summary>
public sealed class ReplayGuard(long last)
{
    private long _last = last;

    public long Last => Interlocked.Read(ref _last);

    /// <summary>把已应用的最大序号退回(<c>busy</c> 的指令没有真正做成,序号不该被消耗)。只在指令串行处理时使用。</summary>
    public void Reset(long last) => Interlocked.Exchange(ref _last, last);

    public bool TryAdvance(long seq)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _last);
            if (seq <= current)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _last, seq, current) == current)
            {
                return true;
            }
        }
    }
}

/// <summary>设备向编排服务注册身份时的「持有私钥证明」。</summary>
public static class DeviceProof
{
    public static byte[] Payload(string deviceId, long issuedAt, string bootId)
    {
        var text = $"{OrchestrationProtocol.RegisterContext}\0{deviceId}\0{issuedAt.ToString(System.Globalization.CultureInfo.InvariantCulture)}\0{bootId}";
        return Encoding.UTF8.GetBytes(text);
    }

    public static byte[] Sign(PeerIdentity identity, long issuedAt, string bootId)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(identity.PrivateKey, out _);
        return key.SignData(Payload(identity.DeviceId, issuedAt, bootId), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>编排服务一侧验证:deviceId 必须是公钥的指纹派生值,签名必须是这把公钥的签名。畸形输入返回 false。</summary>
    public static bool Verify(AgentHello hello)
    {
        try
        {
            var spki = Convert.FromBase64String(hello.PublicKey);
            if (PeerIdentity.DeviceIdOf(SHA256.HashData(spki)) != hello.DeviceId)
            {
                return false;
            }

            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out _);
            return key.KeySize == 256
                && key.VerifyData(Payload(hello.DeviceId, hello.IssuedAt, hello.BootId), Convert.FromBase64String(hello.Signature), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException or FormatException)
        {
            return false;
        }
    }
}
