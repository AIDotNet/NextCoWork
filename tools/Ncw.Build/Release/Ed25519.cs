using Ncw.Build.Core;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Ncw.Build.Release;

/// <summary>
/// Ed25519 签名与验证(BouncyCastle:.NET 没有内置 Ed25519,仓库里本来就固定了 BouncyCastle,见 Directory.Packages.props)。
/// 私钥是 32 字节种子的 base64;公钥是 32 字节的 base64。密钥只从环境变量 / 文件读,不进仓库。
/// </summary>
internal static class Ed25519
{
    public const string KeyEnv = "NCW_RELEASE_SIGNING_KEY";

    public const string KeyFileEnv = "NCW_RELEASE_SIGNING_KEY_FILE";

    public const string KeyIdEnv = "NCW_RELEASE_SIGNING_KEY_ID";

    public static (string PrivateKey, string PublicKey) Generate()
    {
        var generator = new Org.BouncyCastle.Crypto.Generators.Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        var priv = (Ed25519PrivateKeyParameters)pair.Private;
        var pub = (Ed25519PublicKeyParameters)pair.Public;
        return (Convert.ToBase64String(priv.GetEncoded()), Convert.ToBase64String(pub.GetEncoded()));
    }

    public static string PublicKeyOf(string privateKeyBase64) =>
        Convert.ToBase64String(ParsePrivate(privateKeyBase64).GeneratePublicKey().GetEncoded());

    public static byte[] Sign(byte[] data, string privateKeyBase64)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, ParsePrivate(privateKeyBase64));
        signer.BlockUpdate(data, 0, data.Length);
        return signer.GenerateSignature();
    }

    public static bool Verify(byte[] data, byte[] signature, string publicKeyBase64)
    {
        var signer = new Ed25519Signer();
        signer.Init(false, new Ed25519PublicKeyParameters(DecodeKey(publicKeyBase64, "public key"), 0));
        signer.BlockUpdate(data, 0, data.Length);
        return signer.VerifySignature(signature);
    }

    /// <summary>读私钥:先环境变量,再文件;都没有返回 null(调用方据此跳过签名并说明)。</summary>
    public static string? LoadPrivateKey(Func<string, string?> env)
    {
        if (env(KeyEnv) is { Length: > 0 } inline)
        {
            return inline.Trim();
        }

        if (env(KeyFileEnv) is { Length: > 0 } file)
        {
            if (!File.Exists(file))
            {
                throw new BuildException($"{KeyFileEnv} points to '{file}', which does not exist.");
            }

            return File.ReadAllText(file).Trim();
        }

        return null;
    }

    private static Ed25519PrivateKeyParameters ParsePrivate(string base64) => new(DecodeKey(base64, "private key"), 0);

    private static byte[] DecodeKey(string base64, string what)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64.Trim());
        }
        catch (FormatException e)
        {
            throw new BuildException($"The Ed25519 {what} is not valid base64.", e);
        }

        return bytes.Length == 32 ? bytes : throw new BuildException($"The Ed25519 {what} must be 32 bytes (got {bytes.Length}).");
    }
}
