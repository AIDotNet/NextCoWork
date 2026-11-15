namespace Ncw.Client.Accounts;

/// <summary>没有安全存储可用的宿主(预览、还没接钥匙串的平台):一律拒绝保存 / 读取,账户服务据此给出提示,不降级为明文。</summary>
public sealed class UnsupportedRefreshTokenVault : IRefreshTokenVault
{
    public static readonly UnsupportedRefreshTokenVault Instance = new();

    public string? Load() => throw new PlatformNotSupportedException("No secure key store on this platform");

    public void Save(string refreshToken) => throw new PlatformNotSupportedException("No secure key store on this platform");

    public void Delete()
    {
        // 本来就没存过
    }
}
