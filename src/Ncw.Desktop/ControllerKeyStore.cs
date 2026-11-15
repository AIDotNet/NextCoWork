using System.Text.Json;
using System.Text.Json.Serialization;
using Ncw.Client.Services;
using Ncw.Protocol.Security;

namespace Ncw.Desktop;

/// <summary>
/// 桌面端控制端身份的保存位置(docs/design/09 §4):私钥放系统钥匙串,不落明文。
/// macOS:钥匙串里的通用口令(服务「NextCoWork Controller」);Windows:DPAPI(当前用户)加密的文件;
/// Linux 的 libsecret 还没接上,先拒绝(配对时提示「暂时无法安全保存配对密钥」),绝不降级为明文。具体的平台实现在 <see cref="PlatformSecretStore"/>(账户刷新令牌共用)。
/// </summary>
public sealed partial class ControllerKeyStore(string clientDirectory) : IControllerIdentityStore
{
    private const string Service = "NextCoWork Controller";
    private const string Account = "identity-v1";
    private readonly object _gate = new();
    private PeerIdentity? _cached;

    public PeerIdentity LoadOrCreate()
    {
        lock (_gate)
        {
            return _cached ??= LoadOrCreateCore();
        }
    }

    private PeerIdentity LoadOrCreateCore()
    {
        if (Read() is { } existing)
        {
            try
            {
                var file = JsonSerializer.Deserialize(existing, KeyJson.Default.StoredIdentity)!;
                return PeerIdentity.Load(Convert.FromBase64String(file.Certificate), Convert.FromBase64String(file.PrivateKey));
            }
            catch (Exception e) when (e is JsonException or FormatException or System.Security.Cryptography.CryptographicException or NullReferenceException)
            {
                // 存坏了:生成新的身份(之前配对过的设备需要重新配对)。
            }
        }

        var identity = PeerIdentity.Create($"NextCoWork {Environment.MachineName}");
        var json = JsonSerializer.Serialize(new StoredIdentity(Convert.ToBase64String(identity.Certificate), Convert.ToBase64String(identity.PrivateKey)),
            KeyJson.Default.StoredIdentity);
        Write(json);
        return identity;
    }

    private const string WindowsFile = "controller-identity.dpapi";

    private readonly PlatformSecretStore _secrets = new(clientDirectory);

    private string? Read() => _secrets.Read(Service, Account, WindowsFile);

    private void Write(string value) => _secrets.Write(Service, Account, WindowsFile, value);

    public sealed record StoredIdentity(string Certificate, string PrivateKey);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(StoredIdentity))]
    internal sealed partial class KeyJson : JsonSerializerContext;
}
