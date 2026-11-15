using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ncw.Agent.Cloud;
using Ncw.Agent.Storage.Credentials;
using Ncw.Protocol.Security;

namespace Ncw.Agent.Host;

/// <summary>
/// 设备身份文件 <c>device.json</c>(v2,docs/design/09 §4):证书 + 用 NCK1 加密的私钥。deviceId 由公钥派生。
/// v1(M2 早期的随机 id,没有密钥)不再使用,读到时重新生成身份。
/// </summary>
public sealed record DeviceIdentityFile(int V, string DeviceId, string Name, long CreatedAt, string? Certificate, string? PrivateKey);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeviceIdentityFile))]
internal sealed partial class AgentJsonContext : JsonSerializerContext;

/// <summary>设备身份:名字 + 密钥对。</summary>
public sealed record DeviceIdentity(string Name, PeerIdentity Keys)
{
    public string DeviceId => Keys.DeviceId;

    public const string FileName = "device.json";

    /// <summary>读数据根里的身份;没有(或是旧格式)就生成一个。私钥用数据根的主密钥(NCK1)加密。</summary>
    public static DeviceIdentity LoadOrCreate(string dataRoot, TimeProvider clock, string? name = null)
    {
        var path = Path.Combine(dataRoot, FileName);
        DeviceIdentityFile? file;
        try
        {
            file = AtomicFile.Read(path, AgentJsonContext.Default.DeviceIdentityFile);
        }
        catch (JsonException)
        {
            file = null;
        }

        if (file is { V: 2, Certificate: { } cert, PrivateKey: { } sealedKey })
        {
            // 私钥已经用主密钥加密过了:主密钥只能读已有的,绝不生成新的(新密钥解不开它,还会留下一把永远对不上的 credential.key)。
            var master = OpenMasterKey(dataRoot, allowCreate: false);
            try
            {
                string pkcs8Text;
                try
                {
                    pkcs8Text = CredentialCrypto.Decrypt(master, Convert.FromBase64String(sealedKey));
                }
                catch (CryptographicException e)
                {
                    // 密钥文件读得出来,却解不开这份数据的私钥:不是这份数据的密钥(或 device.json 被改过)
                    throw MasterKeyFailure(LocalStartupCodes.MasterKeyUnreadable, e);
                }

                var pkcs8 = Convert.FromBase64String(pkcs8Text);
                return new DeviceIdentity(file.Name, PeerIdentity.Load(Convert.FromBase64String(cert), pkcs8));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(master);
            }
        }

        name ??= Environment.MachineName;
        var keys = PeerIdentity.Create(name, clock);
        var key = OpenMasterKey(dataRoot, allowCreate: true);
        try
        {
            var sealedPrivate = Convert.ToBase64String(CredentialCrypto.Encrypt(key, Convert.ToBase64String(keys.PrivateKey)));
            var created = new DeviceIdentityFile(2, keys.DeviceId, name, clock.GetUtcNow().ToUnixTimeMilliseconds(),
                Convert.ToBase64String(keys.Certificate), sealedPrivate);
            AtomicFile.Write(path, s => JsonSerializer.Serialize(s, created, AgentJsonContext.Default.DeviceIdentityFile));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return new DeviceIdentity(name, keys);
    }

    /// <summary>
    /// 打开数据根的主密钥(<c>credential.key</c>)。<paramref name="allowCreate"/> 时沿用 <see cref="CredentialStore.MasterKeyForDataRoot"/> 的保护:
    /// 全新的数据根生成密钥,库里已经有 NCK1 密文却没有密钥就拒绝。失败一律是结构化的启动失败(<see cref="CloudStartupException"/>:稳定 key + 字段名,
    /// 退出码 <see cref="CloudExitCodes.Identity"/>,没有任何值、没有堆栈),只映射「主密钥缺失 / 读不了」这两种,别的异常保持原样。
    /// </summary>
    private static byte[] OpenMasterKey(string dataRoot, bool allowCreate)
    {
        try
        {
            return CredentialCrypto.LoadMasterKey(dataRoot);
        }
        catch (FileNotFoundException e) when (IsMasterKeyFile(e))
        {
            if (!allowCreate)
            {
                throw MasterKeyFailure(LocalStartupCodes.MasterKeyMissing, e);
            }

            try
            {
                // 全新的数据根:生成;库里已经有 NCK1 密文:它拒绝(再抛一次 FileNotFoundException)。生成过程自己的失败(磁盘满……)不在映射范围内,保持原样。
                return CredentialStore.MasterKeyForDataRoot(dataRoot);
            }
            catch (FileNotFoundException again) when (IsMasterKeyFile(again))
            {
                throw MasterKeyFailure(LocalStartupCodes.MasterKeyMissing, again);
            }
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw MasterKeyFailure(LocalStartupCodes.MasterKeyUnreadable, e);
        }
    }

    private static bool IsMasterKeyFile(FileNotFoundException e) => e.FileName is { } name && Path.GetFileName(name) == CredentialCrypto.MasterKeyFileName;

    private static CloudStartupException MasterKeyFailure(string code, Exception inner) => new(
        code,
        CredentialCrypto.MasterKeyFileName,
        CloudExitCodes.Identity,
        code == LocalStartupCodes.MasterKeyMissing
            ? "The credential master key is missing; refusing to create a new one for data that is already encrypted."
            : "The credential master key cannot be used for this data.",
        inner);
}

internal static class AtomicFile
{
    /// <summary>先写临时文件再改名,写到一半崩溃也不会留下坏文件。</summary>
    public static void Write(string path, Action<Stream> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
        {
            write(stream);
        }

        File.Move(temp, path, overwrite: true);
    }

    public static T? Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, type);
    }
}
