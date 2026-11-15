using Ncw.Client.Tests.Updates;
using Ncw.Client.Updates;
using Ncw.Desktop.Updates;

namespace Ncw.Desktop.Tests.Updates;

/// <summary>内置公钥的来源只有构建(程序集元数据),feed 位置可配置,平台标识。</summary>
public class UpdateTrustTests
{
    [Fact]
    public void The_build_time_metadata_becomes_the_key_ring_including_rotation()
    {
        var a = new TestSigningKey("k1");
        var b = new TestSigningKey("k2");
        var ring = UpdateTrust.FromMetadata($"k1:{Convert.ToBase64String(a.PublicKey)},k2:{Convert.ToBase64String(b.PublicKey)}");
        Assert.Equal(["k1", "k2"], ring.KeyIds.Order());
        Assert.True(ring.TryGet("k2", out var key));
        Assert.Equal(b.PublicKey, key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("k1:AAAA")]
    public void No_or_broken_metadata_means_an_empty_ring_so_nothing_can_be_verified(string? metadata) =>
        Assert.Equal(0, UpdateTrust.FromMetadata(metadata).Count);

    [Fact]
    public void A_dev_build_of_the_desktop_app_has_no_embedded_key()
    {
        // 测试里引用的是开发构建(没有传 NcwReleasePublicKeys):信任根为空 → 自更新拒绝一切。
        Assert.Equal(0, UpdateTrust.BuiltIn().Count);
    }

    [Fact]
    public void Nothing_in_the_environment_can_add_a_trusted_key()
    {
        var a = new TestSigningKey("env");
        var env = new Dictionary<string, string> { ["NCW_RELEASE_PUBLIC_KEYS"] = $"env:{Convert.ToBase64String(a.PublicKey)}", ["NCW_UPDATE_PUBLIC_KEYS"] = "x:y" };
        var config = UpdateConfiguration.FromEnvironment(n => env.GetValueOrDefault(n));
        Assert.Equal(0, config.Keys.Count); // 开发构建没有内置公钥;环境变量改不了信任根
    }

    [Fact]
    public void The_feed_defaults_to_the_backend_in_the_design_and_can_be_pointed_at_a_local_dir_or_server()
    {
        Assert.Equal(FeedLocation.DefaultTemplate, UpdateConfiguration.FromEnvironment(_ => null).FeedTemplate);
        Assert.Equal("/tmp/feed", UpdateConfiguration.FromEnvironment(n => n == "NCW_UPDATE_FEED" ? "/tmp/feed" : null).FeedTemplate);
        Assert.Equal(FeedLocation.DefaultTemplate, UpdateConfiguration.FromEnvironment(n => n == "NCW_UPDATE_FEED" ? "" : null).FeedTemplate);
    }

    [Fact]
    public void The_platform_id_matches_the_build_tool_rids()
    {
        Assert.Matches("^(osx|win|linux)-(x64|arm64)$", PlatformId.Current);
        Assert.Equal(PlatformId.Current, UpdateConfiguration.FromEnvironment(_ => null).Rid);
    }
}
