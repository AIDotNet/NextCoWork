using System.Security.Cryptography;
using System.Text;
using Ncw.Client.Tests.Updates;
using Ncw.Client.Updates;
using Ncw.Desktop.Updates;
using Velopack.Locators;

namespace Ncw.Desktop.Tests.Updates;

/// <summary>
/// 真实的 Velopack <c>UpdateManager</c>(用 <see cref="TestVelopackLocator"/> 假装「装出来了」)+ 真实的本地目录 feed + 真实的验签:
/// 检查 / 下载走 Velopack 自己的代码,「应用并重启」要真实的安装(更新器),在本机演练里另测。
/// </summary>
public class VelopackEngineTests : IDisposable
{
    private static readonly TestSigningKey Key = new("test-1");

    private readonly TempDir _feed = new();
    private readonly TempDir _packages = new();

    public VelopackEngineTests()
    {
        Directory.CreateDirectory(_feed.Path);
        Directory.CreateDirectory(_packages.Path);
    }

    public void Dispose()
    {
        _feed.Dispose();
        _packages.Dispose();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>往 feed 里放一个版本:nupkg(假字节)+ 清单里的条目;写 releases.stable.json 并按给定密钥签清单。</summary>
    private (byte[] Content, ReleaseManifest Manifest) Publish(string version, TestSigningKey? signer = null, bool mandatory = false, string? minimum = null, string channel = "stable", params string[] olderVersions)
    {
        var contents = new Dictionary<string, byte[]>();
        var assets = new List<string>();
        var artifacts = new List<ManifestArtifact>();
        foreach (var v in olderVersions.Append(version))
        {
            var bytes = Encoding.UTF8.GetBytes("fake nupkg " + v);
            var name = $"NextCoWork-{v}-stable-full.nupkg";
            File.WriteAllBytes(Path.Combine(_feed.Path, name), bytes);
            contents[v] = bytes;
            assets.Add($$"""{"PackageId":"NextCoWork","Version":"{{v}}","Type":"Full","FileName":"{{name}}","SHA1":"{{Convert.ToHexString(SHA1.HashData(bytes))}}","SHA256":"{{Sha(bytes)}}","Size":{{bytes.Length}},"NotesMarkdown":"","NotesHTML":""}""");
            artifacts.Add(UpdateKit.Artifact(name, "velopack-full", Sha(bytes).ToLowerInvariant())); // 清单里是小写(Ncw.Build),Velopack 算出来的是大写
        }

        File.WriteAllText(Path.Combine(_feed.Path, "releases.stable.json"), $$"""{"Assets":[{{string.Join(',', assets)}}]}""");
        var manifest = UpdateKit.Manifest(version, channel, mandatory, minimum, notes: "n", artifacts: [.. artifacts]);
        var (manifestBytes, signature) = UpdateKit.Signed(signer ?? Key, manifest);
        File.WriteAllBytes(Path.Combine(_feed.Path, FeedLocation.ManifestName), manifestBytes);
        File.WriteAllText(Path.Combine(_feed.Path, FeedLocation.SignatureName), signature);
        return (contents[version], manifest);
    }

    private VelopackUpdateEngine Engine(string current, ReleaseKeyRing? keys = null, string? feed = null) =>
        new(new UpdateConfiguration(feed ?? _feed.Path, keys ?? Key.Ring, PlatformId.Current),
            new TestVelopackLocator("NextCoWork", current, _packages.Path, null));

    [Fact]
    public void An_app_that_velopack_did_not_install_cannot_update_itself()
    {
        using var engine = new VelopackUpdateEngine(new UpdateConfiguration(_feed.Path, Key.Ring, PlatformId.Current));
        Assert.False(engine.IsInstalled); // dotnet test 进程不在 .app 里
    }

    [Fact]
    public void An_installed_app_reports_its_version()
    {
        using var engine = Engine("3.0.0");
        Assert.True(engine.IsInstalled);
        Assert.Equal("3.0.0", engine.CurrentVersion);
    }

    [Fact]
    public async Task A_newer_signed_release_is_offered_with_the_manifest_policy()
    {
        var (_, published) = Publish("3.0.1", mandatory: true, minimum: "3.0.1");
        using var engine = Engine("3.0.0");

        var result = await engine.CheckAsync(UpdateChannel.Stable, default);

        Assert.Equal("3.0.1", result.Manifest.Version);
        Assert.True(result.Manifest.Policy.Mandatory);
        Assert.NotNull(result.Offer);
        Assert.Equal("3.0.1", result.Offer!.Version);
        Assert.Equal(published.Version, result.Offer.Manifest.Version);
    }

    [Fact]
    public async Task Same_version_offers_nothing()
    {
        Publish("3.0.0");
        using var engine = Engine("3.0.0");
        var result = await engine.CheckAsync(UpdateChannel.Stable, default);
        Assert.Null(result.Offer);
        Assert.Equal("3.0.0", result.Manifest.Version);
    }

    [Fact]
    public async Task A_feed_that_rolled_back_to_an_older_version_offers_nothing_and_never_downgrades()
    {
        Publish("3.0.0"); // 签名完全合法,只是版本比正在运行的低
        using var engine = Engine("3.0.1");
        var result = await engine.CheckAsync(UpdateChannel.Stable, default);
        Assert.Null(result.Offer);
    }

    [Fact]
    public async Task A_tampered_manifest_is_refused_with_a_signature_failure_and_nothing_is_offered()
    {
        Publish("3.0.1");
        var path = Path.Combine(_feed.Path, FeedLocation.ManifestName);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"mandatory\": false", "\"mandatory\": true", StringComparison.Ordinal));
        using var engine = Engine("3.0.0");

        var failure = await Assert.ThrowsAsync<UpdateException>(() => engine.CheckAsync(UpdateChannel.Stable, default));

        Assert.Equal(UpdateFailureKind.SignatureInvalid, failure.Kind);
    }

    [Fact]
    public async Task A_manifest_signed_by_another_key_is_refused()
    {
        Publish("3.0.1", signer: new TestSigningKey("test-1"));
        using var engine = Engine("3.0.0");
        Assert.Equal(UpdateFailureKind.SignatureInvalid, (await Assert.ThrowsAsync<UpdateException>(() => engine.CheckAsync(UpdateChannel.Stable, default))).Kind);
    }

    [Fact]
    public async Task A_missing_signature_file_is_refused()
    {
        Publish("3.0.1");
        File.Delete(Path.Combine(_feed.Path, FeedLocation.SignatureName));
        using var engine = Engine("3.0.0");
        Assert.Equal(UpdateFailureKind.SignatureMissing, (await Assert.ThrowsAsync<UpdateException>(() => engine.CheckAsync(UpdateChannel.Stable, default))).Kind);
    }

    [Fact]
    public async Task A_missing_manifest_is_a_feed_problem_not_a_silent_unsigned_update()
    {
        Publish("3.0.1");
        File.Delete(Path.Combine(_feed.Path, FeedLocation.ManifestName));
        using var engine = Engine("3.0.0");
        Assert.Equal(UpdateFailureKind.FeedInvalid, (await Assert.ThrowsAsync<UpdateException>(() => engine.CheckAsync(UpdateChannel.Stable, default))).Kind);
    }

    [Fact]
    public async Task A_dev_build_with_no_trusted_key_refuses_every_feed()
    {
        Publish("3.0.1");
        using var engine = Engine("3.0.0", keys: ReleaseKeyRing.Empty);
        Assert.Equal(UpdateFailureKind.NoTrustedKeys, (await Assert.ThrowsAsync<UpdateException>(() => engine.CheckAsync(UpdateChannel.Stable, default))).Kind);
    }

    [Fact]
    public async Task A_feed_directory_that_does_not_exist_is_a_feed_problem()
    {
        using var engine = Engine("3.0.0", feed: Path.Combine(_feed.Path, "nope"));
        var failure = await Assert.ThrowsAsync<UpdateException>(() => engine.CheckAsync(UpdateChannel.Stable, default));
        Assert.Contains(failure.Kind, new[] { UpdateFailureKind.FeedInvalid, UpdateFailureKind.Network });
    }

    [Fact]
    public async Task An_unusable_feed_address_is_refused()
    {
        using var engine = Engine("3.0.0", feed: "http://example.com/feed"); // 公网明文
        Assert.Equal(UpdateFailureKind.FeedInvalid, (await Assert.ThrowsAsync<UpdateException>(() => engine.CheckAsync(UpdateChannel.Stable, default))).Kind);
    }

    [Fact]
    public async Task Download_goes_through_velopack_and_verifies_the_signed_hash_before_it_is_ready()
    {
        var (content, _) = Publish("3.0.1");
        using var engine = Engine("3.0.0");
        var offer = (await engine.CheckAsync(UpdateChannel.Stable, default)).Offer!;

        var progress = new List<int>();
        await engine.DownloadAsync(offer, progress.Add, default);

        var downloaded = Path.Combine(_packages.Path, "NextCoWork-3.0.1-stable-full.nupkg");
        Assert.True(File.Exists(downloaded));
        Assert.Equal(content, await File.ReadAllBytesAsync(downloaded));
        Assert.NotEmpty(progress);
        Assert.Equal(100, progress[^1]);
    }

    [Fact]
    public async Task A_package_replaced_on_the_server_after_signing_is_discarded_with_a_checksum_failure()
    {
        Publish("3.0.1");
        using var engine = Engine("3.0.0");
        var offer = (await engine.CheckAsync(UpdateChannel.Stable, default)).Offer!;
        File.WriteAllText(Path.Combine(_feed.Path, "NextCoWork-3.0.1-stable-full.nupkg"), "malicious replacement");

        var failure = await Assert.ThrowsAsync<UpdateException>(() => engine.DownloadAsync(offer, _ => { }, default));

        Assert.Equal(UpdateFailureKind.ChecksumMismatch, failure.Kind);
        Assert.False(File.Exists(Path.Combine(_packages.Path, "NextCoWork-3.0.1-stable-full.nupkg")));
    }

    [Fact]
    public async Task A_beta_manifest_served_to_the_stable_channel_is_refused()
    {
        Publish("3.0.1", channel: "beta");
        using var engine = Engine("3.0.0");
        Assert.Equal(UpdateFailureKind.ManifestRejected, (await Assert.ThrowsAsync<UpdateException>(() => engine.CheckAsync(UpdateChannel.Stable, default))).Kind);
    }

    [Fact]
    public async Task The_channel_template_selects_the_feed_directory_and_the_releases_file()
    {
        // {channel}/{rid} 模板:stable 和 beta 各一个目录;beta 目录里的清单声明 beta
        var root = _feed.Path;
        foreach (var channel in new[] { "stable", "beta" })
        {
            var dir = Path.Combine(root, channel, PlatformId.Current);
            Directory.CreateDirectory(dir);
            var bytes = Encoding.UTF8.GetBytes("fake nupkg " + channel);
            var name = $"NextCoWork-3.0.{(channel == "beta" ? 2 : 1)}-{channel}-full.nupkg";
            File.WriteAllBytes(Path.Combine(dir, name), bytes);
            var version = channel == "beta" ? "3.0.2" : "3.0.1";
            File.WriteAllText(Path.Combine(dir, $"releases.{channel}.json"),
                $$"""{"Assets":[{"PackageId":"NextCoWork","Version":"{{version}}","Type":"Full","FileName":"{{name}}","SHA1":"{{Convert.ToHexString(SHA1.HashData(bytes))}}","SHA256":"{{Sha(bytes)}}","Size":{{bytes.Length}},"NotesMarkdown":"","NotesHTML":""}]}""");
            var (m, s) = UpdateKit.Signed(Key, UpdateKit.Manifest(version, channel, artifacts: UpdateKit.Artifact(name, "velopack-full", Sha(bytes), PlatformId.Current)));
            File.WriteAllBytes(Path.Combine(dir, FeedLocation.ManifestName), m);
            File.WriteAllText(Path.Combine(dir, FeedLocation.SignatureName), s);
        }

        using var engine = Engine("3.0.0", feed: Path.Combine(root, "{channel}", "{rid}"));
        Assert.Equal("3.0.1", (await engine.CheckAsync(UpdateChannel.Stable, default)).Offer!.Version);
        Assert.Equal("3.0.2", (await engine.CheckAsync(UpdateChannel.Beta, default)).Offer!.Version);
    }
}
