using System.Security.Cryptography;
using System.Text;
using Ncw.Client.Tests.Updates;
using Ncw.Client.Updates;
using Ncw.Desktop.Updates;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Ncw.Desktop.Tests.Updates;

/// <summary>验签的 Velopack 更新源包装:没验过签名的东西 Velopack 看不到、也下载不了。</summary>
public class SignedFeedSourceTests
{
    private const string Rid = "osx-arm64";

    private static readonly TestSigningKey Key = new("test-1");

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static VelopackAsset Asset(string version, VelopackAssetType type = VelopackAssetType.Full, string? sha = null, string? fileName = null) => new()
    {
        PackageId = "NextCoWork",
        Version = Velopack.SemanticVersion.Parse(version),
        Type = type,
        FileName = fileName ?? $"NextCoWork-{version}-stable-{(type == VelopackAssetType.Full ? "full" : "delta")}.nupkg",
        SHA256 = sha ?? "",
        Size = 10,
    };

    private sealed class FakeInner(params VelopackAsset[] assets) : IUpdateSource
    {
        public int FeedCalls { get; private set; }

        public List<string> Downloads { get; } = [];

        public byte[] Content { get; set; } = "package bytes"u8.ToArray();

        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            FeedCalls++;
            return Task.FromResult(new VelopackAssetFeed { Assets = assets });
        }

        public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
        {
            Downloads.Add(releaseEntry.FileName);
            await File.WriteAllBytesAsync(localFile, Content, cancelToken);
            progress(100);
        }
    }

    private static (SignedFeedSource Source, FakeInner Inner, MemoryFeed Feed) Make(ReleaseManifest manifest, FakeInner inner, TestSigningKey? signer = null, ReleaseKeyRing? ring = null)
    {
        var feed = new MemoryFeed();
        feed.Publish(signer ?? Key, manifest);
        return (new SignedFeedSource(inner, feed, ring ?? Key.Ring, UpdateChannel.Stable, Rid), inner, feed);
    }

    private static Task<VelopackAssetFeed> Read(SignedFeedSource source) => source.GetReleaseFeed(NullVelopackLogger.Instance, "NextCoWork", "stable");

    [Fact]
    public async Task Only_packages_listed_in_the_signed_manifest_pass_and_get_the_signed_hash()
    {
        var full = Asset("3.0.1", sha: "aa");
        var delta = Asset("3.0.1", VelopackAssetType.Delta, "bb");
        var unlisted = Asset("3.0.2", sha: "cc"); // feed 里多出来的、没被发布流程签进清单的
        var manifest = UpdateKit.Manifest("3.0.1", artifacts:
        [
            UpdateKit.Artifact(full.FileName, "velopack-full", "AA"),
            UpdateKit.Artifact(delta.FileName, "velopack-delta", "BB"),
            UpdateKit.Artifact("NextCoWork-stable-Setup.pkg", "installer-pkg", "DD"),
        ]);
        var (source, _, _) = Make(manifest, new FakeInner(full, delta, unlisted));

        var feed = await Read(source);

        Assert.Equal([full.FileName, delta.FileName], feed.Assets.Select(a => a.FileName));
        Assert.All(feed.Assets, a => Assert.Contains(a.SHA256, new[] { "AA", "BB" }));
        Assert.Equal("3.0.1", source.LastManifest!.Version);
    }

    [Fact]
    public async Task The_lowercase_hex_the_build_tool_writes_is_handed_to_velopack_as_the_uppercase_it_compares_against()
    {
        // 真实演练抓到的缺陷:Velopack 的校验区分大小写,清单(Ncw.Build)是小写,不统一的话每次下载都报「校验和不一致」
        var full = Asset("3.0.1", sha: "ABCDEF");
        var (source, _, _) = Make(UpdateKit.Manifest("3.0.1", artifacts: UpdateKit.Artifact(full.FileName, "velopack-full", "abcdef")), new FakeInner(full));
        Assert.Equal("ABCDEF", Assert.Single((await Read(source)).Assets).SHA256);
    }

    [Fact]
    public async Task A_package_with_no_hash_in_the_feed_gets_the_hash_from_the_signed_manifest()
    {
        var full = Asset("3.0.1", sha: "");
        var (source, _, _) = Make(UpdateKit.Manifest("3.0.1", artifacts: UpdateKit.Artifact(full.FileName, "velopack-full", "ABCD")), new FakeInner(full));
        Assert.Equal("ABCD", Assert.Single((await Read(source)).Assets).SHA256);
    }

    [Fact]
    public async Task A_tampered_manifest_is_refused_before_the_inner_source_is_even_asked()
    {
        var full = Asset("3.0.1");
        var inner = new FakeInner(full);
        var feed = new MemoryFeed();
        feed.Publish(Key, UpdateKit.Manifest("3.0.1", artifacts: UpdateKit.Artifact(full.FileName)));
        feed.Files[FeedLocation.ManifestName] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(feed.Files[FeedLocation.ManifestName]).Replace("3.0.1", "9.9.9", StringComparison.Ordinal));
        var source = new SignedFeedSource(inner, feed, Key.Ring, UpdateChannel.Stable, Rid);

        var failure = await Assert.ThrowsAsync<UpdateException>(() => Read(source));

        Assert.Equal(UpdateFailureKind.SignatureInvalid, failure.Kind);
        Assert.Equal(0, inner.FeedCalls);
        Assert.Null(source.LastManifest);
    }

    [Fact]
    public async Task A_manifest_signed_by_a_key_we_do_not_trust_is_refused()
    {
        var full = Asset("3.0.1");
        var attacker = new TestSigningKey("test-1");
        var (source, inner, _) = Make(UpdateKit.Manifest("3.0.1", artifacts: UpdateKit.Artifact(full.FileName)), new FakeInner(full), signer: attacker);
        Assert.Equal(UpdateFailureKind.SignatureInvalid, (await Assert.ThrowsAsync<UpdateException>(() => Read(source))).Kind);
        Assert.Equal(0, inner.FeedCalls);
    }

    [Fact]
    public async Task A_feed_whose_hash_disagrees_with_the_manifest_is_refused()
    {
        var full = Asset("3.0.1", sha: "FEED");
        var (source, _, _) = Make(UpdateKit.Manifest("3.0.1", artifacts: UpdateKit.Artifact(full.FileName, "velopack-full", "SIGNED")), new FakeInner(full));
        Assert.Equal(UpdateFailureKind.ChecksumMismatch, (await Assert.ThrowsAsync<UpdateException>(() => Read(source))).Kind);
    }

    [Fact]
    public async Task A_feed_without_the_version_the_manifest_announces_is_refused()
    {
        var old = Asset("3.0.0");
        var (source, _, _) = Make(UpdateKit.Manifest("3.0.1", artifacts: UpdateKit.Artifact(old.FileName)), new FakeInner(old));
        Assert.Equal(UpdateFailureKind.FeedInvalid, (await Assert.ThrowsAsync<UpdateException>(() => Read(source))).Kind);
    }

    [Fact]
    public async Task Artifacts_for_another_platform_do_not_count()
    {
        var full = Asset("3.0.1");
        var (source, _, _) = Make(UpdateKit.Manifest("3.0.1", artifacts: UpdateKit.Artifact(full.FileName, platform: "win-x64")), new FakeInner(full));
        Assert.Equal(UpdateFailureKind.FeedInvalid, (await Assert.ThrowsAsync<UpdateException>(() => Read(source))).Kind);
    }

    [Fact]
    public async Task A_beta_manifest_in_a_stable_feed_is_refused()
    {
        var full = Asset("3.0.1");
        var (source, _, _) = Make(UpdateKit.Manifest("3.0.1", channel: "beta", artifacts: UpdateKit.Artifact(full.FileName)), new FakeInner(full));
        Assert.Equal(UpdateFailureKind.ManifestRejected, (await Assert.ThrowsAsync<UpdateException>(() => Read(source))).Kind);
    }

    [Fact]
    public async Task A_build_without_trusted_keys_refuses_everything()
    {
        var full = Asset("3.0.1");
        var (source, _, _) = Make(UpdateKit.Manifest("3.0.1", artifacts: UpdateKit.Artifact(full.FileName)), new FakeInner(full), ring: ReleaseKeyRing.Empty);
        Assert.Equal(UpdateFailureKind.NoTrustedKeys, (await Assert.ThrowsAsync<UpdateException>(() => Read(source))).Kind);
    }

    [Fact]
    public async Task Download_keeps_a_file_whose_hash_matches_and_deletes_one_that_does_not()
    {
        var content = "package bytes"u8.ToArray();
        var full = Asset("3.0.1");
        var (source, inner, _) = Make(UpdateKit.Manifest("3.0.1", artifacts: UpdateKit.Artifact(full.FileName, "velopack-full", Sha(content))), new FakeInner(full) { Content = content });
        var asset = Assert.Single((await Read(source)).Assets);
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var target = Path.Combine(dir.Path, "ok.nupkg");

        var last = -1;
        await source.DownloadReleaseEntry(NullVelopackLogger.Instance, asset, target, p => last = p);
        Assert.True(File.Exists(target));
        Assert.Equal(100, last);

        inner.Content = "TAMPERED bytes"u8.ToArray(); // 服务器上的包被换了
        var bad = Path.Combine(dir.Path, "bad.nupkg");
        var failure = await Assert.ThrowsAsync<UpdateException>(() => source.DownloadReleaseEntry(NullVelopackLogger.Instance, asset, bad, _ => { }));
        Assert.Equal(UpdateFailureKind.ChecksumMismatch, failure.Kind);
        Assert.False(File.Exists(bad));
    }

    [Fact]
    public async Task Nothing_outside_the_manifest_can_be_downloaded_even_if_asked_directly()
    {
        var full = Asset("3.0.1");
        var (source, inner, _) = Make(UpdateKit.Manifest("3.0.1", artifacts: UpdateKit.Artifact(full.FileName, "velopack-full", "AA")), new FakeInner(full));
        await Read(source);
        var failure = await Assert.ThrowsAsync<UpdateException>(() => source.DownloadReleaseEntry(NullVelopackLogger.Instance, Asset("3.0.9"), "/tmp/never", _ => { }));
        Assert.Equal(UpdateFailureKind.FeedInvalid, failure.Kind);
        Assert.Empty(inner.Downloads);

        // 还没读过 feed(没验过任何清单)的源:什么都不能下载
        var fresh = new SignedFeedSource(new FakeInner(full), new MemoryFeed(), Key.Ring, UpdateChannel.Stable, Rid);
        await Assert.ThrowsAsync<UpdateException>(() => fresh.DownloadReleaseEntry(NullVelopackLogger.Instance, full, "/tmp/never", _ => { }));
    }
}
