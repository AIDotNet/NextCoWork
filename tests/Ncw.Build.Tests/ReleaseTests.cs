using System.Text.Json;
using Ncw.Build.Core;
using Ncw.Build.Release;

namespace Ncw.Build.Tests;

public class ArtifactInputTests
{
    [Fact]
    public void Parses_rid_kind_path_including_windows_drive_letters()
    {
        var a = ArtifactInput.Parse("win-x64:velopack-setup:C:\\out\\Setup.exe");
        Assert.Equal("win-x64", a.Rid.Value);
        Assert.Equal("velopack-setup", a.Kind);
        Assert.Equal("C:\\out\\Setup.exe", a.Path);
        Assert.Null(ArtifactInput.Parse("osx-arm64::/x/y.zip").Kind);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("osx-arm64:only-kind")]
    [InlineData("bogus:k:/x")]
    public void Rejects_malformed(string text) => Assert.Throws<BuildException>(() => ArtifactInput.Parse(text));

    [Theory]
    [InlineData("NextCoWork-3.0.0-stable-full.nupkg", "velopack-full")]
    [InlineData("NextCoWork-3.0.1-stable-delta.nupkg", "velopack-delta")]
    [InlineData("NextCoWork-stable-Setup.exe", "velopack-setup")]
    [InlineData("NextCoWork-stable-Setup.pkg", "installer-pkg")]
    [InlineData("NextCoWork-stable-Portable.zip", "portable")]
    [InlineData("NextCoWork-stable.AppImage", "appimage")]
    [InlineData("releases.stable.json", "feed")]
    [InlineData("whatever.bin", "file")]
    public void Infers_kind_from_velopack_names(string name, string kind) => Assert.Equal(kind, ManifestBuilder.InferKind(name));
}

public class ManifestTests
{
    private static async Task<ReleaseManifest> Build(TempDir dir, params string[] files)
    {
        var inputs = files.Select(f => new ArtifactInput(TestKit.MacArm, null, dir.Write(f, "content of " + f)));
        return await ManifestBuilder.BuildAsync("3.0.0", "stable", TestKit.Now, inputs, new ReleasePolicy(true, "2.9.0", null), "notes", "key-1");
    }

    [Fact]
    public async Task Records_sha256_size_and_leaves_url_and_signature_pending()
    {
        using var dir = new TempDir();
        var m = await Build(dir, "NextCoWork-3.0.0-stable-full.nupkg");
        var a = Assert.Single(m.Artifacts);
        Assert.Equal(Hashing.Sha256(dir.Sub("NextCoWork-3.0.0-stable-full.nupkg")), a.Sha256);
        Assert.Equal(new FileInfo(dir.Sub("NextCoWork-3.0.0-stable-full.nupkg")).Length, a.Size);
        Assert.Null(a.Url); // 上传之后才有地址
        Assert.Equal("pending", m.Signing.Status);
        Assert.Equal("ed25519", m.Signing.Algorithm);
        Assert.True(m.Policy.Mandatory);
        Assert.Equal("2.9.0", m.Policy.MinimumSupportedVersion);
    }

    [Fact]
    public async Task Same_inputs_produce_the_same_bytes_regardless_of_input_order()
    {
        using var dir = new TempDir();
        var a = await Build(dir, "b.nupkg", "a-Setup.exe");
        var b = await Build(dir, "a-Setup.exe", "b.nupkg");
        Assert.Equal(
            JsonOut.Serialize(a, BuildJson.Default.ReleaseManifest),
            JsonOut.Serialize(b, BuildJson.Default.ReleaseManifest));
    }

    [Fact]
    public async Task Rejects_empty_and_missing_artifacts()
    {
        await Assert.ThrowsAsync<BuildException>(() => ManifestBuilder.BuildAsync("1", "stable", TestKit.Now, [], new ReleasePolicy(false, null, null), null, null));
        await Assert.ThrowsAsync<BuildException>(() => ManifestBuilder.BuildAsync("1", "stable", TestKit.Now, [new ArtifactInput(TestKit.MacArm, null, "/nope/x.zip")], new ReleasePolicy(false, null, null), null, null));
    }

    [Fact]
    public async Task Without_a_key_the_manifest_is_written_unsigned_and_a_stale_signature_is_removed()
    {
        using var dir = new TempDir();
        var m = await Build(dir, "x-Setup.exe");
        var path = dir.Sub("out", "release-manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".sig", "stale");
        Assert.False(await ManifestBuilder.WriteAsync(m, path, null, null));
        Assert.False(File.Exists(path + ".sig"));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("pending", doc.RootElement.GetProperty("signing").GetProperty("status").GetString());
    }

    [Fact]
    public async Task With_a_key_the_detached_signature_covers_the_exact_file_bytes()
    {
        using var dir = new TempDir();
        var (priv, pub) = Ed25519.Generate();
        var m = await Build(dir, "x-Setup.exe");
        var path = dir.Sub("out", "release-manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Assert.True(await ManifestBuilder.WriteAsync(m, path, priv, "key-1"));

        var bytes = File.ReadAllBytes(path);
        var sig = Convert.FromBase64String(File.ReadAllText(path + ".sig").Trim());
        Assert.True(Ed25519.Verify(bytes, sig, pub));
        using var doc = JsonDocument.Parse(bytes);
        Assert.Equal("signed", doc.RootElement.GetProperty("signing").GetProperty("status").GetString());
        Assert.Equal("key-1", doc.RootElement.GetProperty("signing").GetProperty("keyId").GetString());

        bytes[^3] ^= 1; // 改一个字节
        Assert.False(Ed25519.Verify(bytes, sig, pub));
        Assert.False(Ed25519.Verify(File.ReadAllBytes(path), sig, Ed25519.Generate().PublicKey));
    }

    [Fact]
    public void Ed25519_matches_the_rfc8032_test_vector()
    {
        // RFC 8032 §7.1 TEST 1:空消息。
        var seed = Convert.ToBase64String(Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60"));
        Assert.Equal("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", Convert.ToHexStringLower(Convert.FromBase64String(Ed25519.PublicKeyOf(seed))));
        var sig = Ed25519.Sign([], seed);
        Assert.Equal(
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b",
            Convert.ToHexStringLower(sig));
    }

    [Fact]
    public void Key_loading_prefers_the_environment_then_the_file_and_otherwise_returns_null()
    {
        using var dir = new TempDir();
        var file = dir.Write("k", "FILEKEY\n");
        Assert.Equal("ENVKEY", Ed25519.LoadPrivateKey(n => n == Ed25519.KeyEnv ? "ENVKEY" : n == Ed25519.KeyFileEnv ? file : null));
        Assert.Equal("FILEKEY", Ed25519.LoadPrivateKey(n => n == Ed25519.KeyFileEnv ? file : null));
        Assert.Null(Ed25519.LoadPrivateKey(_ => null));
        Assert.Null(Ed25519.LoadPrivateKey(_ => ""));
        Assert.Throws<BuildException>(() => Ed25519.LoadPrivateKey(n => n == Ed25519.KeyFileEnv ? "/nope" : null));
    }

    [Fact]
    public void Bad_keys_are_rejected_with_a_clear_message()
    {
        Assert.Throws<BuildException>(() => Ed25519.Sign([1], "not base64!!"));
        Assert.Throws<BuildException>(() => Ed25519.Sign([1], Convert.ToBase64String(new byte[16])));
    }

    [Fact]
    public async Task Manifest_command_skips_signing_and_says_so_without_a_key()
    {
        using var dir = new TempDir();
        var log = new MemoryBuildLog();
        var ctx = TestKit.Context(dir, new FakeRunner(), log, TestKit.MacArm);
        var artifact = dir.Write("art/NextCoWork-stable-Setup.pkg", "pkg");
        Assert.Equal(0, await Program.RunAsync(["manifest", "--artifact", $"osx-arm64::{artifact}", "--out", dir.Sub("rel")], ctx, default));
        Assert.Contains(log.Lines, l => l.Contains("NOT SIGNED"));
        Assert.False(File.Exists(dir.Sub("rel", "release-manifest.json.sig")));
    }

    [Fact]
    public async Task Manifest_command_signs_with_a_key_from_the_environment_and_verify_command_accepts_it()
    {
        using var dir = new TempDir();
        var (priv, pub) = Ed25519.Generate();
        var log = new MemoryBuildLog();
        var ctx = TestKit.Context(dir, new FakeRunner(), log, TestKit.MacArm, new Dictionary<string, string> { [Ed25519.KeyEnv] = priv, [Ed25519.KeyIdEnv] = "k1" });
        var artifact = dir.Write("art/NextCoWork-stable-Setup.pkg", "pkg");
        var manifest = dir.Sub("rel", "release-manifest.json");
        Assert.Equal(0, await Program.RunAsync(["manifest", "--artifact", $"osx-arm64::{artifact}", "--out", dir.Sub("rel")], ctx, default));
        Assert.DoesNotContain(log.Lines, l => l.Contains("NOT SIGNED"));

        Assert.Equal(0, await Program.RunAsync(["manifest-verify", "--manifest", manifest, "--public-key", pub, "--dir", dir.Sub("art")], ctx, default));
        File.WriteAllText(artifact, "different bytes");
        Assert.Equal(1, await Program.RunAsync(["manifest-verify", "--manifest", manifest, "--public-key", pub, "--dir", dir.Sub("art")], ctx, default));
    }

    [Fact]
    public async Task Verify_matches_same_named_files_of_different_platforms_by_their_own_platform_directory()
    {
        // 回归:三个平台各自产出同名的 releases.stable.json(内容不同)。验证以前只按文件名取第一个存在的文件,
        // 第二、第三个平台的记录就拿别的平台的文件比哈希,报 hash mismatch。
        using var dir = new TempDir();
        var (priv, pub) = Ed25519.Generate();
        var log = new MemoryBuildLog();
        var ctx = TestKit.Context(dir, new FakeRunner(), log, TestKit.MacArm, new Dictionary<string, string> { [Ed25519.KeyEnv] = priv, [Ed25519.KeyIdEnv] = "k1" });
        var linux = dir.Write("in/velopack-linux-x64/releases.stable.json", "{\"linux\":1}");
        var mac = dir.Write("in/velopack-osx-arm64/releases.stable.json", "{\"mac\":2}");
        var win = dir.Write("in/velopack-win-x64/releases.stable.json", "{\"win\":3}");
        var manifest = dir.Sub("rel", "release-manifest.json");
        Assert.Equal(0, await Program.RunAsync(
            ["manifest", "--artifact", $"linux-x64::{linux}", "--artifact", $"osx-arm64::{mac}", "--artifact", $"win-x64::{win}", "--out", dir.Sub("rel")], ctx, default));

        Assert.Equal(0, await Program.RunAsync(
            ["manifest-verify", "--manifest", manifest, "--public-key", pub,
                "--dir", dir.Sub("in", "velopack-linux-x64"), "--dir", dir.Sub("in", "velopack-osx-arm64"), "--dir", dir.Sub("in", "velopack-win-x64")], ctx, default));

        File.WriteAllText(mac, "tampered");
        Assert.Equal(1, await Program.RunAsync(
            ["manifest-verify", "--manifest", manifest, "--public-key", pub,
                "--dir", dir.Sub("in", "velopack-linux-x64"), "--dir", dir.Sub("in", "velopack-osx-arm64"), "--dir", dir.Sub("in", "velopack-win-x64")], ctx, default));
    }

    [Fact]
    public async Task Keygen_refuses_to_write_inside_the_repository_or_overwrite()
    {
        using var dir = new TempDir();
        var log = new MemoryBuildLog();
        var ctx = TestKit.Context(dir, new FakeRunner(), log, TestKit.MacArm);
        Assert.Equal(1, await Program.RunAsync(["keygen", "--out", Path.Combine(ctx.Repo.Root, "k.key")], ctx, default));
        var outside = Path.Combine(Path.GetTempPath(), "ncw-keygen-" + Guid.NewGuid().ToString("N")[..8] + ".key");
        try
        {
            Assert.Equal(0, await Program.RunAsync(["keygen", "--out", outside], ctx, default));
            Assert.Equal(1, await Program.RunAsync(["keygen", "--out", outside], ctx, default));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(outside));
            }
        }
        finally
        {
            File.Delete(outside);
        }
    }
}
