using Ncw.Client.Accounts;

namespace Ncw.Desktop;

/// <summary>
/// 账户刷新令牌在桌面端的保存位置(docs/design/07 §2 第 6 步):系统钥匙串(macOS Keychain、Windows DPAPI)。
/// Linux 的 libsecret 未接入:<see cref="Save"/> 抛 <see cref="PlatformNotSupportedException"/>,账户服务据此拒绝保存并提示用户,不降级为明文。
/// 只放刷新令牌(访问令牌只在内存里)。
/// </summary>
public sealed class AccountVault(string clientDirectory, string? service = null) : IRefreshTokenVault
{
    internal const string DefaultService = "NextCoWork Account";
    private const string Account = "refresh-token-v1";
    private const string WindowsFile = "account-refresh.dpapi";

    private readonly PlatformSecretStore _secrets = new(clientDirectory);
    private readonly string _service = service ?? DefaultService;

    public string? Load() => _secrets.Read(_service, Account, WindowsFile);

    public void Save(string refreshToken) => _secrets.Write(_service, Account, WindowsFile, refreshToken);

    public void Delete() => _secrets.Delete(_service, Account, WindowsFile);
}
