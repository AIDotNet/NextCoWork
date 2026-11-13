using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ncw.Protocol.Security;

/// <summary>
/// 设备或控制端的身份(docs/design/09 §4):ECDSA P-256 自签名证书 + 私钥。
/// 指纹 = 证书里公钥(SubjectPublicKeyInfo)的 SHA-256,按公钥钉扎,证书换发不影响信任。
/// 设备的 deviceId 由指纹派生(中继以后据此验证设备签名,不需要账户)。
/// </summary>
public sealed class PeerIdentity
{
    private PeerIdentity(byte[] certificate, byte[] privateKey)
    {
        Certificate = certificate;
        PrivateKey = privateKey;
        using var cert = X509CertificateLoader.LoadCertificate(certificate);
        Fingerprint = FingerprintOf(cert);
    }

    /// <summary>证书 DER。</summary>
    public byte[] Certificate { get; }

    /// <summary>私钥 PKCS#8 DER。调用方负责加密保存(设备用 NCK1,控制端用系统钥匙串)。</summary>
    public byte[] PrivateKey { get; }

    /// <summary>公钥指纹(32 字节)。</summary>
    public byte[] Fingerprint { get; }

    public string FingerprintHex => Convert.ToHexStringLower(Fingerprint);

    /// <summary>设备 id:<c>dev_</c> + 指纹前 16 字节的 Crockford base32(小写,26 位)。</summary>
    public string DeviceId => DeviceIdOf(Fingerprint);

    public static PeerIdentity Create(string commonName, TimeProvider? clock = null)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(new X500DistinguishedName($"CN={Sanitize(commonName)}"), key, HashAlgorithmName.SHA256);
        // 有效期只是形式:校验按公钥指纹钉扎,不看有效期和 CA。
        using var cert = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(100));
        return new PeerIdentity(cert.RawData, key.ExportPkcs8PrivateKey());
    }

    public static PeerIdentity Load(byte[] certificate, byte[] privateKey)
    {
        var identity = new PeerIdentity(certificate, privateKey);
        // 确认私钥和证书是一对。
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(privateKey, out _);
        using var cert = X509CertificateLoader.LoadCertificate(certificate);
        using var pub = cert.GetECDsaPublicKey() ?? throw new CryptographicException("The certificate has no ECDSA public key.");
        if (!pub.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo()))
        {
            throw new CryptographicException("The private key does not match the certificate.");
        }

        return identity;
    }

    public static byte[] FingerprintOf(X509Certificate2 certificate) =>
        SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo());

    public static byte[] FingerprintOf(byte[] certificateDer)
    {
        using var cert = X509CertificateLoader.LoadCertificate(certificateDer);
        return FingerprintOf(cert);
    }

    public static string DeviceIdOf(ReadOnlySpan<byte> fingerprint)
    {
        const string alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
        Span<char> chars = stackalloc char[26];
        var bits = 0;
        var buffer = 0;
        var n = 0;
        foreach (var b in fingerprint[..16])
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                chars[n++] = alphabet[(buffer >> (bits - 5)) & 31];
                bits -= 5;
            }
        }

        chars[n++] = alphabet[(buffer << (5 - bits)) & 31];
        return "dev_" + new string(chars[..n]);
    }

    private static string Sanitize(string name)
    {
        var cleaned = new string(name.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').ToArray()).Trim();
        return cleaned.Length == 0 ? "ncw" : cleaned[..Math.Min(cleaned.Length, 60)];
    }
}
