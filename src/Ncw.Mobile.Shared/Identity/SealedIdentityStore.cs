using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ncw.Client.Services;
using Ncw.Protocol.Security;

namespace Ncw.Mobile;

/// <summary>
/// 平台的「封存盒」:用一把<b>不可导出</b>的包裹密钥(Android Keystore 里的 AES-GCM 密钥、iOS Keychain)加解密一段字节。
/// 实现自己生成并带上随机 IV(随机化加密);<paramref name="associatedData"/> 参与认证,换了用途就解不开。
/// </summary>
public interface ISecretBox
{
    /// <summary>加密。平台不能安全保存(没有硬件 / 软件密钥库、锁屏被移除后密钥失效等)时抛 <see cref="PlatformNotSupportedException"/>,绝不降级为明文。</summary>
    byte[] Seal(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData);

    /// <summary>解密。密文被改动、密钥已失效或不存在时抛 <see cref="SecretBoxException"/>。</summary>
    byte[] Open(ReadOnlySpan<byte> sealedData, ReadOnlySpan<byte> associatedData);
}

/// <summary>封存的数据打不开(损坏、被篡改、包裹密钥丢失)。调用方把它当作「文件已经没用」处理,而不是崩溃。</summary>
public sealed class SecretBoxException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 控制端身份(远程连接的 TLS 客户端证书与私钥,docs/design/09 §4)在手机上的保存:私钥 + 证书序列化后用 <see cref="ISecretBox"/>(Keystore 包裹密钥)
/// 加密,密文放在应用私有目录的一个文件里(0600)。<b>私钥本身不以明文落盘</b>:磁盘上只有密文,解开它需要的密钥留在 Keystore 里、不可导出。
/// 文件打不开(密钥失效、损坏)就生成新身份——之前配对过的设备需要重新配对,同桌面端的 <c>ControllerKeyStore</c>。
/// </summary>
public sealed partial class SealedIdentityStore(string path, ISecretBox box, string name) : IControllerIdentityStore
{
    private static readonly byte[] Magic = "NCWI"u8.ToArray();
    private const byte FormatVersion = 1;
    private static readonly byte[] Aad = "ncw.controller-identity.v1"u8.ToArray();

    private readonly object _gate = new();
    private PeerIdentity? _cached;

    public PeerIdentity LoadOrCreate()
    {
        lock (_gate)
        {
            return _cached ??= LoadOrCreateCore();
        }
    }

    /// <summary>磁盘上的文件内容是否是密文(测试用:确认里面没有私钥明文)。</summary>
    public static bool LooksSealed(ReadOnlySpan<byte> fileBytes) => fileBytes.Length > Magic.Length + 1 && fileBytes[..Magic.Length].SequenceEqual(Magic);

    private PeerIdentity LoadOrCreateCore()
    {
        if (TryLoad() is { } existing)
        {
            return existing;
        }

        var identity = PeerIdentity.Create(name);
        var json = JsonSerializer.SerializeToUtf8Bytes(
            new StoredIdentity(Convert.ToBase64String(identity.Certificate), Convert.ToBase64String(identity.PrivateKey)), IdentityJson.Default.StoredIdentity);
        var sealedData = box.Seal(json, Aad);
        CryptographicOperations.ZeroMemory(json);
        Write(sealedData);
        return identity;
    }

    private PeerIdentity? TryLoad()
    {
        byte[] file;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            file = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (!LooksSealed(file) || file[Magic.Length] != FormatVersion)
        {
            return null;
        }

        byte[]? json = null;
        try
        {
            json = box.Open(file.AsSpan(Magic.Length + 1), Aad);
            var stored = JsonSerializer.Deserialize(json, IdentityJson.Default.StoredIdentity)!;
            return PeerIdentity.Load(Convert.FromBase64String(stored.Certificate), Convert.FromBase64String(stored.PrivateKey));
        }
        catch (Exception e) when (e is SecretBoxException or JsonException or FormatException or CryptographicException or NullReferenceException)
        {
            return null; // 存坏了或包裹密钥没了:当作没有,生成新身份
        }
        finally
        {
            if (json is not null)
            {
                CryptographicOperations.ZeroMemory(json);
            }
        }
    }

    private void Write(byte[] sealedData)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temp, options))
        {
            stream.Write(Magic);
            stream.WriteByte(FormatVersion);
            stream.Write(sealedData);
        }

        File.Move(temp, path, overwrite: true);
    }

    internal sealed record StoredIdentity(string Certificate, string PrivateKey);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(StoredIdentity))]
    internal sealed partial class IdentityJson : JsonSerializerContext;
}

/// <summary>
/// 软件实现的封存盒:密钥在内存里(随进程消失),只给测试和没有密钥库的开发场合用——<b>不能</b>当真实持久化的包裹密钥用
/// (密钥不落盘 = 重启后旧文件打不开 = 每次启动都是新身份)。
/// </summary>
public sealed class InMemorySecretBox : ISecretBox
{
    private readonly byte[] _key;

    public InMemorySecretBox(byte[]? key = null) => _key = key ?? RandomNumberGenerator.GetBytes(32);

    public byte[] Seal(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        var result = new byte[12 + plaintext.Length + 16];
        RandomNumberGenerator.Fill(result.AsSpan(0, 12));
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(result.AsSpan(0, 12), plaintext, result.AsSpan(12, plaintext.Length), result.AsSpan(12 + plaintext.Length, 16), associatedData);
        return result;
    }

    public byte[] Open(ReadOnlySpan<byte> sealedData, ReadOnlySpan<byte> associatedData)
    {
        if (sealedData.Length < 12 + 16)
        {
            throw new SecretBoxException("Sealed data is too short");
        }

        var plain = new byte[sealedData.Length - 12 - 16];
        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(sealedData[..12], sealedData.Slice(12, plain.Length), sealedData[^16..], plain, associatedData);
        }
        catch (CryptographicException e)
        {
            throw new SecretBoxException("Sealed data could not be opened", e);
        }

        return plain;
    }
}
