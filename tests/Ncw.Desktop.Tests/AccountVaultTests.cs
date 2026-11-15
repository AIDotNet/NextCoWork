using Ncw.Desktop;

namespace Ncw.Desktop.Tests;

/// <summary>
/// 账户刷新令牌的系统钥匙串实现(只在 macOS / Windows 有真实现;Linux 的 libsecret 未接入,必须拒绝保存而不是写明文)。
/// 用唯一的服务名,测完删掉,不碰用户真实的条目。
/// </summary>
public sealed class AccountVaultTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ncw-vault-{Guid.NewGuid():N}");
    private readonly string _service = $"NextCoWork Account Test {Guid.NewGuid():N}";

    public void Dispose()
    {
        try
        {
            new AccountVault(_dir, _service).Delete();
        }
        catch (PlatformNotSupportedException)
        {
            // Linux
        }

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void The_refresh_token_round_trips_through_the_system_store_is_replaced_in_place_and_deleted()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(), "The secure store exists on macOS and Windows only.");
        var vault = new AccountVault(_dir, _service);
        Assert.Null(vault.Load());

        vault.Save("RT-first-密钥");
        Assert.Equal("RT-first-密钥", vault.Load());

        vault.Save("RT-second"); // 刷新令牌会轮换:覆盖写,不报「已存在」
        Assert.Equal("RT-second", vault.Load());
        Assert.Equal("RT-second", new AccountVault(_dir, _service).Load()); // 换一个实例也读得到(真的在系统存储里)

        vault.Delete();
        Assert.Null(vault.Load());
        vault.Delete(); // 本来就没有:不报错
    }

    [Fact]
    public void A_token_is_never_written_to_a_plain_file()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(), "The secure store exists on macOS and Windows only.");
        new AccountVault(_dir, _service).Save("RT-PLAINTEXT-CHECK");
        if (Directory.Exists(_dir))
        {
            foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            {
                Assert.DoesNotContain("RT-PLAINTEXT-CHECK", File.ReadAllText(file)); // Windows 上是 DPAPI 密文
            }
        }
    }

    [Fact]
    public void Without_a_secure_store_saving_is_refused_instead_of_falling_back_to_plain_text()
    {
        Assert.SkipUnless(!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows(), "Linux only: libsecret is not wired in yet.");
        var vault = new AccountVault(_dir, _service);
        Assert.Throws<PlatformNotSupportedException>(() => vault.Save("RT"));
        Assert.Throws<PlatformNotSupportedException>(() => vault.Load());
        Assert.False(Directory.Exists(_dir) && Directory.EnumerateFileSystemEntries(_dir).Any()); // 没有留下任何文件
    }
}
