using System.Security.Cryptography;
using System.Text;

namespace Ncw.Agent.Storage.Credentials;

/// <summary>
/// <c>credentials</c> 表的程序自管加密(NCK1),与 2.x 的 <c>main/secrets/credential-crypto.ts</c> 逐字节兼容:
/// <c>"NCK1" + 版本(1) + nonce(12) + 密文 + GCM tag(16)</c>,AES-256-GCM,主密钥是数据根下 0600 权限的 <c>credential.key</c>。
/// <list type="bullet">
/// <item>判断格式看前缀和最小长度,不看「解不解得开」:旧 safeStorage 行是任意字节,试解分不清「旧格式」和「坏了」。</item>
/// <item>解不开就抛异常,不返回 null:那只可能是主密钥不对或密文被改过,静默当成「没配过」会掩盖问题。</item>
/// <item>主密钥已存在但长度不对时抛异常,绝不重新生成(重新生成等于让库里所有密文作废)。</item>
/// </list>
/// </summary>
public static class CredentialCrypto
{
    public const string MasterKeyFileName = "credential.key";

    private static readonly byte[] Magic = "NCK1"u8.ToArray();
    private const byte FormatVersion = 1;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;

    public static bool IsProgramEncrypted(ReadOnlySpan<byte> blob) =>
        blob.Length >= Magic.Length + 1 + NonceBytes + TagBytes && blob[..Magic.Length].SequenceEqual(Magic);

    public static byte[] Encrypt(ReadOnlySpan<byte> key, string plain)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var blob = new byte[Magic.Length + 1 + NonceBytes + plainBytes.Length + TagBytes];
        Magic.CopyTo(blob, 0);
        blob[Magic.Length] = FormatVersion;
        var nonce = blob.AsSpan(Magic.Length + 1, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plainBytes, blob.AsSpan(Magic.Length + 1 + NonceBytes, plainBytes.Length), blob.AsSpan(blob.Length - TagBytes));
        CryptographicOperations.ZeroMemory(plainBytes);
        return blob;
    }

    public static string Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> blob)
    {
        if (!IsProgramEncrypted(blob))
        {
            throw new CryptographicException("The credential blob is not in the NCK1 format.");
        }

        if (blob[Magic.Length] != FormatVersion)
        {
            throw new CryptographicException($"Unknown credential blob version: {blob[Magic.Length]}.");
        }

        var nonce = blob.Slice(Magic.Length + 1, NonceBytes);
        var cipher = blob[(Magic.Length + 1 + NonceBytes)..^TagBytes];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(nonce, cipher, blob[^TagBytes..], plain);
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>只读已有的主密钥。不存在抛 <see cref="FileNotFoundException"/>;不是普通文件或长度不对抛 <see cref="CryptographicException"/>。</summary>
    public static byte[] LoadMasterKey(string directory)
    {
        var file = Path.Combine(directory, MasterKeyFileName);
        var info = new FileInfo(file);
        if (info.LinkTarget is not null)
        {
            throw new CryptographicException("The master key path is not a regular file.");
        }

        if (!info.Exists)
        {
            throw new FileNotFoundException("The credential master key is missing.", file);
        }

        var key = File.ReadAllBytes(file);
        if (key.Length != KeyBytes)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new CryptographicException($"The credential master key is damaged (length {key.Length}, expected {KeyBytes}).");
        }

        Tighten(file);
        return key;
    }

    /// <summary>读主密钥;不存在时生成 32 字节随机密钥并以 0600 落盘(两个进程同时首次生成时,后到者读先到者的)。</summary>
    public static byte[] LoadOrCreateMasterKey(string directory)
    {
        try
        {
            return LoadMasterKey(directory);
        }
        catch (FileNotFoundException)
        {
        }

        var file = Path.Combine(directory, MasterKeyFileName);
        var key = RandomNumberGenerator.GetBytes(KeyBytes);
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(file, options))
            {
                stream.Write(key);
                stream.Flush(flushToDisk: true);
            }

            Tighten(file);
            return key;
        }
        catch (IOException) when (File.Exists(file))
        {
            CryptographicOperations.ZeroMemory(key);
            return LoadMasterKey(directory);
        }
    }

    private static void Tighten(string file)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
