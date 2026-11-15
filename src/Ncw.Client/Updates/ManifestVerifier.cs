using System.Text.Json;
using Ncw.Core.Domain;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Ncw.Client.Updates;

/// <summary>
/// 发布公钥的信任列表(<c>kid → 32 字节 Ed25519 公钥</c>)。内置在构建里,可轮换:新旧 kid 并存一段时间,旧 kid 从下一个构建里删掉即吊销。
/// 文本形式 <c>kid:base64,kid:base64</c>(和编排密钥同一种写法)。公钥是公开信息,但来源必须是构建(不能来自 feed 本身)。
/// </summary>
public sealed class ReleaseKeyRing
{
    public static readonly ReleaseKeyRing Empty = new(new Dictionary<string, byte[]>());

    private readonly Dictionary<string, byte[]> _keys;

    public ReleaseKeyRing(IReadOnlyDictionary<string, byte[]> keys)
    {
        _keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (kid, key) in keys)
        {
            if (kid.Length == 0 || key.Length != 32)
            {
                throw new ArgumentException("A release key needs a key id and a 32-byte Ed25519 public key.", nameof(keys));
            }

            _keys[kid] = [.. key];
        }
    }

    public int Count => _keys.Count;

    public IEnumerable<string> KeyIds => _keys.Keys;

    public bool TryGet(string keyId, out byte[] key) => _keys.TryGetValue(keyId, out key!);

    internal IEnumerable<byte[]> All => _keys.Values;

    /// <summary>解析 <c>kid:base64,kid:base64</c>。任何一项不合法返回 false(整份作废,不部分采用);空串返回 false。</summary>
    public static bool TryParse(string? text, out ReleaseKeyRing ring)
    {
        ring = Empty;
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

        ring = new ReleaseKeyRing(map);
        return true;
    }
}

/// <summary>验证的结果:通过时带清单,否则带失败原因和一句给日志的诊断(不进界面)。</summary>
public sealed record ManifestVerification(ReleaseManifest? Manifest, UpdateFailureKind? Failure, string? Detail)
{
    public bool IsValid => Manifest is not null;

    public static ManifestVerification Ok(ReleaseManifest manifest) => new(manifest, null, null);

    public static ManifestVerification Fail(UpdateFailureKind kind, string detail) => new(null, kind, detail);
}

/// <summary>
/// 清单的验证(docs/design/04 §6「所有平台:发布清单另外用 Ed25519 签名」)。<b>先验签、再信任内容</b>:
/// <list type="number">
/// <item>信任列表为空 → 拒绝(<see cref="UpdateFailureKind.NoTrustedKeys"/>),不是跳过验证。</item>
/// <item>签名文件缺失 / 不是 64 字节的 base64 → 拒绝。</item>
/// <item>密钥选择:清单里声明了 <c>signing.keyId</c> 就只用那一把(不在列表里 → <see cref="UpdateFailureKind.UnknownSigningKey"/>);没声明就逐把试。
/// 读 keyId 发生在验签之前,但它只决定「用哪把公钥验」,攻击者改它只会让验证失败。</item>
/// <item>验签通过之后才解析并检查内容:<c>signing.status</c> 必须是 <c>signed</c>、schema、产品名、通道必须和请求的一致(防止把合法签名的 beta 清单放到 stable 的 feed 里)、
/// 版本号能读懂、强制更新策略自洽(<see cref="UpdatePolicy.IsCoherent"/>)。</item>
/// </list>
/// Ed25519 用 BouncyCastle(.NET 没有内置;仓库本来就固定了它),不自研。永远不抛异常。
/// </summary>
public static class ManifestVerifier
{
    public static ManifestVerification Verify(ReadOnlySpan<byte> manifestBytes, string? signatureText, ReleaseKeyRing keys, UpdateChannel expectedChannel)
    {
        if (keys.Count == 0)
        {
            return ManifestVerification.Fail(UpdateFailureKind.NoTrustedKeys, "no release public key is built into this app");
        }

        if (string.IsNullOrWhiteSpace(signatureText))
        {
            return ManifestVerification.Fail(UpdateFailureKind.SignatureMissing, "no detached signature next to the manifest");
        }

        var signature = new byte[64];
        if (!Convert.TryFromBase64String(signatureText.Trim(), signature, out var written) || written != 64)
        {
            return ManifestVerification.Fail(UpdateFailureKind.SignatureInvalid, "the signature is not a base64 Ed25519 signature");
        }

        IEnumerable<byte[]> candidates;
        var declared = PeekKeyId(manifestBytes);
        if (declared is not null)
        {
            if (!keys.TryGet(declared, out var key))
            {
                return ManifestVerification.Fail(UpdateFailureKind.UnknownSigningKey, $"the manifest is signed with key '{declared}', which this app does not trust");
            }

            candidates = [key];
        }
        else
        {
            candidates = keys.All;
        }

        var matched = false;
        foreach (var key in candidates)
        {
            if (VerifySignature(manifestBytes, signature, key))
            {
                matched = true;
                break;
            }
        }

        if (!matched)
        {
            return ManifestVerification.Fail(UpdateFailureKind.SignatureInvalid, "the manifest does not match its Ed25519 signature");
        }

        // 到这里内容是可信的:检查它是不是我们要的那一份
        ReleaseManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(manifestBytes, UpdateJson.Default.ReleaseManifest);
        }
        catch (JsonException)
        {
            manifest = null;
        }

        if (manifest is null || manifest.Policy is null || manifest.Signing is null || manifest.Artifacts is null
            || string.IsNullOrEmpty(manifest.Version) || string.IsNullOrEmpty(manifest.Channel) || string.IsNullOrEmpty(manifest.Product))
        {
            return ManifestVerification.Fail(UpdateFailureKind.ManifestRejected, "the signed manifest is incomplete");
        }

        if (!string.Equals(manifest.Signing.Status, "signed", StringComparison.Ordinal)
            || !string.Equals(manifest.Signing.Algorithm, "ed25519", StringComparison.OrdinalIgnoreCase))
        {
            return ManifestVerification.Fail(UpdateFailureKind.ManifestUnsigned, "the manifest does not declare itself signed with ed25519");
        }

        if (manifest.Schema != ReleaseManifest.SupportedSchema)
        {
            return ManifestVerification.Fail(UpdateFailureKind.ManifestRejected, $"unsupported manifest schema {manifest.Schema}");
        }

        if (!string.Equals(manifest.Product, ReleaseManifest.ProductName, StringComparison.Ordinal))
        {
            return ManifestVerification.Fail(UpdateFailureKind.ManifestRejected, $"the manifest is for product '{manifest.Product}'");
        }

        if (!string.Equals(manifest.Channel, UpdateChannels.Id(expectedChannel), StringComparison.Ordinal))
        {
            return ManifestVerification.Fail(UpdateFailureKind.ManifestRejected, $"the manifest is for channel '{manifest.Channel}', not '{UpdateChannels.Id(expectedChannel)}'");
        }

        if (!UpdatePolicy.IsCoherent(manifest.ToRelease()))
        {
            return ManifestVerification.Fail(UpdateFailureKind.ManifestRejected, "the manifest's version or minimumSupportedVersion is not coherent");
        }

        return ManifestVerification.Ok(manifest);
    }

    private static bool VerifySignature(ReadOnlySpan<byte> data, byte[] signature, byte[] publicKey)
    {
        try
        {
            var signer = new Ed25519Signer();
            signer.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
            signer.BlockUpdate(data.ToArray(), 0, data.Length);
            return signer.VerifySignature(signature);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Org.BouncyCastle.Crypto.CryptoException)
        {
            return false;
        }
    }

    /// <summary>读清单里声明的 keyId(未验证)。读不出来返回 null(然后逐把公钥试)。</summary>
    private static string? PeekKeyId(ReadOnlySpan<byte> manifestBytes)
    {
        try
        {
            var reader = new Utf8JsonReader(manifestBytes);
            using var doc = JsonDocument.ParseValue(ref reader);
            return doc.RootElement.TryGetProperty("signing", out var signing)
                && signing.ValueKind == JsonValueKind.Object
                && signing.TryGetProperty("keyId", out var id)
                && id.ValueKind == JsonValueKind.String
                    ? id.GetString() is { Length: > 0 } text ? text : null
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
