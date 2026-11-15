using System.Text;
using Ncw.Build.Core;
using Ncw.Build.Release;
using Ncw.Client.Updates;
using Ncw.Core.Domain;

namespace Ncw.Build.Tests;

/// <summary>
/// <c>tools/Ncw.Build</c> 生成并签名的发布清单,必须能被桌面端运行时的验证代码(<c>Ncw.Client.Updates</c>)原样接受 —— 两边是各自独立的类型,
/// 字段名、签名格式(对原始字节的分离签名 + base64)、强制更新策略的语义靠这组测试锁住(docs/design/04 §7)。
/// </summary>
public class UpdateContractTests
{
    private static ReleaseKeyRing Ring(string keyId, string publicKeyBase64) =>
        ReleaseKeyRing.TryParse($"{keyId}:{publicKeyBase64}", out var ring) ? ring : throw new InvalidOperationException("bad test key");

    private static async Task<(string ManifestPath, string PublicKey, string Dir)> Generate(TempDir dir, string[] extraArgs, string keyId = "dev-1")
    {
        var (priv, pub) = Ed25519.Generate();
        var ctx = TestKit.Context(dir, new FakeRunner(), new MemoryBuildLog(), TestKit.MacArm,
            new Dictionary<string, string> { [Ed25519.KeyEnv] = priv, [Ed25519.KeyIdEnv] = keyId });
        dir.Write("feed/NextCoWork-3.0.1-stable-full.nupkg", "full package");
        dir.Write("feed/NextCoWork-3.0.1-stable-delta.nupkg", "delta package");
        dir.Write("feed/NextCoWork-stable-Setup.pkg", "installer");
        dir.Write("feed/releases.stable.json", """{"Assets":[]}""");
        var code = await Program.RunAsync(["manifest", "--scan", dir.Sub("feed"), "--rid", "osx-arm64", "--version", "3.0.1", "--channel", "stable", "--out", dir.Sub("feed"), .. extraArgs], ctx, default);
        Assert.Equal(0, code);
        return (dir.Sub("feed", "release-manifest.json"), pub, dir.Sub("feed"));
    }

    [Fact]
    public async Task A_manifest_made_by_the_build_tool_verifies_in_the_client_and_carries_every_field()
    {
        using var dir = new TempDir();
        var (path, pub, _) = await Generate(dir, ["--mandatory", "--minimum-supported-version", "3.0.0", "--grace-until", "2026-12-01T00:00:00Z", "--notes", "## What's new\n- x"]);

        var result = ManifestVerifier.Verify(File.ReadAllBytes(path), File.ReadAllText(path + ".sig"), Ring("dev-1", pub), UpdateChannel.Stable);

        Assert.True(result.IsValid, result.Detail);
        var m = result.Manifest!;
        Assert.Equal("3.0.1", m.Version);
        Assert.Equal("stable", m.Channel);
        Assert.Equal("NextCoWork", m.Product);
        Assert.Equal(new UpdateRelease("3.0.1", true, "3.0.0", new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero)), m.ToRelease());
        Assert.Equal("## What's new\n- x", m.Notes);
        Assert.Equal("signed", m.Signing.Status);
        Assert.Equal("dev-1", m.Signing.KeyId);
        Assert.Contains(m.Artifacts, a => a is { Platform: "osx-arm64", Kind: "velopack-full", FileName: "NextCoWork-3.0.1-stable-full.nupkg" } && a.Size > 0 && a.Sha256.Length == 64);
        Assert.Contains(m.Artifacts, a => a.Kind == "velopack-delta");
        Assert.Contains(m.Artifacts, a => a.Kind == "feed" && a.FileName == "releases.stable.json");
    }

    [Fact]
    public async Task The_hashes_in_the_manifest_are_the_ones_the_client_compares_against()
    {
        using var dir = new TempDir();
        var (path, pub, feed) = await Generate(dir, []);
        var m = ManifestVerifier.Verify(File.ReadAllBytes(path), File.ReadAllText(path + ".sig"), Ring("dev-1", pub), UpdateChannel.Stable).Manifest!;
        foreach (var artifact in m.Artifacts)
        {
            Assert.Equal(artifact.Sha256, Hashing.Sha256(Path.Combine(feed, artifact.FileName)), ignoreCase: true);
        }
    }

    [Fact]
    public async Task One_changed_byte_a_wrong_key_or_a_swapped_channel_fail_in_the_client()
    {
        using var dir = new TempDir();
        var (path, pub, _) = await Generate(dir, []);
        var bytes = File.ReadAllBytes(path);
        var sig = File.ReadAllText(path + ".sig");

        bytes[bytes.Length / 2] ^= 1;
        Assert.Equal(UpdateFailureKind.SignatureInvalid, ManifestVerifier.Verify(bytes, sig, Ring("dev-1", pub), UpdateChannel.Stable).Failure);

        var other = Ed25519.Generate().PublicKey;
        Assert.Equal(UpdateFailureKind.SignatureInvalid, ManifestVerifier.Verify(File.ReadAllBytes(path), sig, Ring("dev-1", other), UpdateChannel.Stable).Failure);
        Assert.Equal(UpdateFailureKind.UnknownSigningKey, ManifestVerifier.Verify(File.ReadAllBytes(path), sig, Ring("other-kid", pub), UpdateChannel.Stable).Failure);
        Assert.Equal(UpdateFailureKind.ManifestRejected, ManifestVerifier.Verify(File.ReadAllBytes(path), sig, Ring("dev-1", pub), UpdateChannel.Beta).Failure);
    }

    [Fact]
    public async Task A_manifest_written_without_a_key_is_pending_and_the_client_refuses_it_even_with_a_signature_from_someone_else()
    {
        using var dir = new TempDir();
        var ctx = TestKit.Context(dir, new FakeRunner(), new MemoryBuildLog(), TestKit.MacArm);
        dir.Write("feed/NextCoWork-3.0.1-stable-full.nupkg", "full package");
        Assert.Equal(0, await Program.RunAsync(["manifest", "--scan", dir.Sub("feed"), "--rid", "osx-arm64", "--version", "3.0.1", "--out", dir.Sub("feed")], ctx, default));
        var path = dir.Sub("feed", "release-manifest.json");
        Assert.False(File.Exists(path + ".sig"));

        var (priv, pub) = Ed25519.Generate();
        var bytes = File.ReadAllBytes(path);
        var forged = Convert.ToBase64String(Ed25519.Sign(bytes, priv));
        // 没有签名文件:缺失;有人补了一个自己密钥签的:密钥不在信任列表里;就算密钥在,清单自己说 pending:被拒
        Assert.Equal(UpdateFailureKind.SignatureMissing, ManifestVerifier.Verify(bytes, null, Ring("any", pub), UpdateChannel.Stable).Failure);
        Assert.Equal(UpdateFailureKind.ManifestUnsigned, ManifestVerifier.Verify(bytes, forged, Ring("any", pub), UpdateChannel.Stable).Failure);
    }

    [Fact]
    public async Task Prerelease_manifests_and_the_beta_channel_round_trip()
    {
        using var dir = new TempDir();
        var (priv, pub) = Ed25519.Generate();
        var ctx = TestKit.Context(dir, new FakeRunner(), new MemoryBuildLog(), TestKit.MacArm, new Dictionary<string, string> { [Ed25519.KeyEnv] = priv, [Ed25519.KeyIdEnv] = "k" });
        dir.Write("feed/NextCoWork-3.1.0-beta.2-beta-full.nupkg", "x");
        Assert.Equal(0, await Program.RunAsync(["manifest", "--scan", dir.Sub("feed"), "--rid", "osx-arm64", "--version", "3.1.0-beta.2", "--channel", "beta", "--out", dir.Sub("feed")], ctx, default));
        var path = dir.Sub("feed", "release-manifest.json");

        var result = ManifestVerifier.Verify(File.ReadAllBytes(path), File.ReadAllText(path + ".sig"), Ring("k", pub), UpdateChannel.Beta);

        Assert.True(result.IsValid, result.Detail);
        Assert.Equal("3.1.0-beta.2", result.Manifest!.Version);
        Assert.True(UpdatePolicy.IsNewer(result.Manifest.Version, "3.0.0"));
        Assert.True(UpdatePolicy.IsNewer("3.1.0", result.Manifest.Version));
    }
}
