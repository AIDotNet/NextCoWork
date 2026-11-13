using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement.Srp;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using BigInteger = Org.BouncyCastle.Math.BigInteger;

namespace Ncw.Protocol.Security;

/// <summary>
/// 配对时要绑定的 TLS 会话信息(docs/design/09 §3 第 4 步):exporter 值 + 双方公钥指纹,都是「本端看到的」。
/// 经过中间人时两端看到的不一样,证明对不上。
/// </summary>
public sealed record PairingTranscript(byte[] Exporter, byte[] ClientFingerprint, byte[] DeviceFingerprint)
{
    internal byte[] Proof(byte[] sessionKey, string side)
    {
        var label = Encoding.ASCII.GetBytes($"ncw-pair/1 {side}\0");
        var data = new byte[label.Length + Exporter.Length + ClientFingerprint.Length + DeviceFingerprint.Length];
        var offset = 0;
        foreach (var part in new[] { label, Exporter, ClientFingerprint, DeviceFingerprint })
        {
            part.CopyTo(data, offset);
            offset += part.Length;
        }

        return HMACSHA256.HashData(sessionKey, data);
    }
}

/// <summary>SRP-6a 参数:RFC 5054 的 2048 位组、SHA-256;用户名 = deviceId,口令 = 配对码。</summary>
internal static class SrpParameters
{
    public static Srp6GroupParameters Group => Srp6StandardGroups.rfc5054_2048;

    public static IDigest Digest() => new Sha256Digest();

    public static byte[] Identity(string deviceId) => Encoding.UTF8.GetBytes(deviceId);

    /// <summary>配对码只认数字(用户可能输入空格或横线)。</summary>
    public static byte[] Password(string code) => Encoding.ASCII.GetBytes(new string(code.Where(char.IsAsciiDigit).ToArray()));

    public static byte[] ToBytes(BigInteger value) => value.ToByteArrayUnsigned();

    /// <summary>会话密钥 K = H(S)(SRP-6a 的定义)。在交换 M2 之前就要用它算绑定证明。</summary>
    public static byte[] SessionKey(BigInteger secret) => ToBytes(Srp6Utilities.CalculateKey(Digest(), Group.N, secret));

    public static BigInteger FromBytes(byte[] bytes) => new(1, bytes);
}

/// <summary>设备端的一次配对尝试(每条连接一个,用一次)。</summary>
public sealed class PairingServer
{
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(16);
    private readonly Srp6Server _srp = new();
    private bool _finished;

    public PairingServer(string deviceId, string code)
    {
        var generator = new Srp6VerifierGenerator();
        generator.Init(SrpParameters.Group, SrpParameters.Digest());
        var verifier = generator.GenerateVerifier(_salt, SrpParameters.Identity(deviceId), SrpParameters.Password(code));
        _srp.Init(SrpParameters.Group, verifier, SrpParameters.Digest(), new SecureRandom());
        B = SrpParameters.ToBytes(_srp.GenerateServerCredentials());
    }

    public byte[] Salt => _salt;

    public byte[] B { get; }

    /// <summary>校验控制端的 SRP 证据和会话绑定证明。通过返回 (M2, 设备证明);配对码不对或绑定不符返回 null。只能调用一次。</summary>
    public (byte[] M2, byte[] Proof)? Finish(byte[] a, byte[] m1, byte[] clientProof, PairingTranscript transcript)
    {
        if (_finished)
        {
            return null;
        }

        _finished = true;
        try
        {
            var secret = _srp.CalculateSecret(SrpParameters.FromBytes(a));
            if (!_srp.VerifyClientEvidenceMessage(SrpParameters.FromBytes(m1)))
            {
                return null;
            }

            var key = SrpParameters.SessionKey(secret);
            if (!CryptographicOperations.FixedTimeEquals(transcript.Proof(key, "client"), clientProof))
            {
                return null;
            }

            return (SrpParameters.ToBytes(_srp.CalculateServerEvidenceMessage()), transcript.Proof(key, "device"));
        }
        catch (CryptoException)
        {
            return null; // 非法的 A(例如 A mod N == 0)
        }
    }
}

/// <summary>控制端的一次配对。</summary>
public sealed class PairingClient(string deviceId, string code)
{
    private readonly Srp6Client _srp = new();
    private byte[]? _key;

    /// <summary>用设备给的 salt 和 B 算出 (A, M1, 控制端证明)。</summary>
    public (byte[] A, byte[] M1, byte[] Proof) Respond(byte[] salt, byte[] b, PairingTranscript transcript)
    {
        _srp.Init(SrpParameters.Group, SrpParameters.Digest(), new SecureRandom());
        var a = _srp.GenerateClientCredentials(salt, SrpParameters.Identity(deviceId), SrpParameters.Password(code));
        BigInteger secret;
        try
        {
            secret = _srp.CalculateSecret(SrpParameters.FromBytes(b));
        }
        catch (CryptoException e)
        {
            throw new SecureChannelException("The device sent invalid pairing parameters", e);
        }

        var m1 = _srp.CalculateClientEvidenceMessage();
        _key = SrpParameters.SessionKey(secret);
        return (SrpParameters.ToBytes(a), SrpParameters.ToBytes(m1), transcript.Proof(_key, "client"));
    }

    /// <summary>校验设备的 SRP 证据和绑定证明:通过才说明对面就是知道配对码、而且没被中间人转接的那台设备。</summary>
    public bool Verify(byte[] m2, byte[] deviceProof, PairingTranscript transcript)
    {
        if (_key is null)
        {
            return false;
        }

        try
        {
            return _srp.VerifyServerEvidenceMessage(SrpParameters.FromBytes(m2))
                   && CryptographicOperations.FixedTimeEquals(transcript.Proof(_key, "device"), deviceProof);
        }
        catch (CryptoException)
        {
            return false;
        }
    }
}
