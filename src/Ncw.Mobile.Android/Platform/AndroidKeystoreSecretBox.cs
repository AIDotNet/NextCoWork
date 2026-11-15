using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace Ncw.Mobile.Droid.Platform;

/// <summary>
/// <see cref="ISecretBox"/> 的 Android Keystore 实现(docs/design/11 §2):一把 AES-256-GCM 包裹密钥生成在 AndroidKeyStore 里,
/// <b>不可导出</b>(应用拿不到密钥字节,只能让 Keystore 用它加解密;有硬件的设备上密钥在 TEE / StrongBox 里)。
/// 随机 IV 由 Keystore 生成(<c>setRandomizedEncryptionRequired</c>),密文格式 <c>[ivLen:1][iv][ciphertext+tag]</c>。
/// 不要求「设备解锁」(<c>setUnlockedDeviceRequired</c>):后台重连不能被锁屏卡住。
/// </summary>
internal sealed class AndroidKeystoreSecretBox : ISecretBox
{
    private const string Provider = "AndroidKeyStore";
    private const string Alias = "work.nextco.ncw.controller-wrap.v1";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int TagBits = 128;

    public byte[] Seal(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        try
        {
            var key = GetOrCreateKey();
            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.EncryptMode, key);
            cipher.UpdateAAD(associatedData.ToArray());
            var iv = cipher.GetIV()!;
            var body = cipher.DoFinal(plaintext.ToArray())!;
            var result = new byte[1 + iv.Length + body.Length];
            result[0] = (byte)iv.Length;
            iv.CopyTo(result, 1);
            body.CopyTo(result, 1 + iv.Length);
            return result;
        }
        catch (Exception e) when (e is Java.Lang.Exception or GeneralSecurityException)
        {
            // 没有可用的密钥库 / 密钥生成失败:绝不降级为明文
            throw new PlatformNotSupportedException("Android Keystore is not available: " + e.Message, e);
        }
    }

    public byte[] Open(ReadOnlySpan<byte> sealedData, ReadOnlySpan<byte> associatedData)
    {
        if (sealedData.Length < 2 || sealedData[0] is 0 or > 32 || sealedData.Length < 1 + sealedData[0] + (TagBits / 8))
        {
            throw new SecretBoxException("Sealed data is malformed");
        }

        var ivLength = sealedData[0];
        try
        {
            var key = GetOrCreateKey(); // 密钥不存在(被清除 / 应用重装)时这里会新建一把,下面的解密必然失败 → SecretBoxException
            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.DecryptMode, key, new GCMParameterSpec(TagBits, sealedData.Slice(1, ivLength).ToArray()));
            cipher.UpdateAAD(associatedData.ToArray());
            return cipher.DoFinal(sealedData[(1 + ivLength)..].ToArray())!;
        }
        catch (Exception e) when (e is Java.Lang.Exception or GeneralSecurityException)
        {
            throw new SecretBoxException("Sealed data could not be opened", e);
        }
    }

    private static IKey GetOrCreateKey()
    {
        var store = KeyStore.GetInstance(Provider)!;
        store.Load(null);
        if (store.GetKey(Alias, null) is IKey existing)
        {
            return existing;
        }

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, Provider)!;
        generator.Init(new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
            .SetKeySize(256)!
            .SetRandomizedEncryptionRequired(true)!
            .Build());
        return generator.GenerateKey()!;
    }
}
