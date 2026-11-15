using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
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
            var master = CredentialStore.MasterKeyForDataRoot(dataRoot);
            try
            {
                var pkcs8 = Convert.FromBase64String(CredentialCrypto.Decrypt(master, Convert.FromBase64String(sealedKey)));
                return new DeviceIdentity(file.Name, PeerIdentity.Load(Convert.FromBase64String(cert), pkcs8));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(master);
            }
        }

        name ??= Environment.MachineName;
        var keys = PeerIdentity.Create(name, clock);
        var key = CredentialStore.MasterKeyForDataRoot(dataRoot);
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
