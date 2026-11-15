using System.Globalization;
using Ncw.Build.Cli;
using Ncw.Build.Core;

namespace Ncw.Build.Release;

/// <summary>manifest:生成发布清单 JSON(各平台产物、sha256、大小),有 Ed25519 密钥时签名,没有就跳过并明说。</summary>
internal sealed class ManifestCommand : IBuildCommand
{
    public CommandSpec Spec { get; } = new("manifest",
        "Write release-manifest.json (artifacts, sha256, size) and sign it with Ed25519 when a key is configured.",
        ["version", "channel", "artifact", "scan", "rid", "out", "artifacts", "notes", "minimum-supported-version", "grace-until"],
        ["mandatory"],
        [
            "manifest --channel stable --artifact osx-arm64:velopack-setup:artifacts/velopack/osx-arm64/stable/NextCoWork-stable.pkg",
            "manifest --scan artifacts/velopack/osx-arm64/stable --rid osx-arm64",
        ]);

    public async Task<int> RunAsync(BuildContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var version = CommonOptions.Version(args, ctx);
        var channel = args.Get("channel") ?? "stable";
        var inputs = args.GetAll("artifact").Select(ArtifactInput.Parse).ToList();
        if (args.Get("scan") is { } dir)
        {
            var rid = args.Get("rid") is { } text ? Rid.Parse(text) : throw new BuildException("--scan needs --rid (the platform of the scanned folder).");
            inputs.AddRange(Scan(dir, rid));
        }

        DateTimeOffset? grace = null;
        if (args.Get("grace-until") is { } graceText)
        {
            grace = DateTimeOffset.Parse(graceText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        }

        var notes = args.Get("notes");
        var policy = new ReleasePolicy(args.Has("mandatory"), args.Get("minimum-supported-version"), grace);
        var keyId = ctx.EnvNonEmpty(Ed25519.KeyIdEnv);
        var manifest = await ManifestBuilder.BuildAsync(version, channel, ctx.Clock.GetUtcNow(), inputs, policy, notes, keyId, ct).ConfigureAwait(false);

        var outDir = args.Get("out") ?? ctx.Repo.ReleaseDir(version);
        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, "release-manifest.json");
        var privateKey = Ed25519.LoadPrivateKey(ctx.Env);

        ctx.Log.Step($"release manifest {version} ({channel}), {manifest.Artifacts.Length} artifact(s)");
        var signed = await ManifestBuilder.WriteAsync(manifest, path, privateKey, keyId).ConfigureAwait(false);
        foreach (var a in manifest.Artifacts)
        {
            ctx.Log.Info($"{a.Platform,-12} {a.Kind,-15} {a.FileName}  {a.Size / 1024.0 / 1024.0:F1} MiB  sha256 {a.Sha256[..16]}...");
        }

        ctx.Log.Info($"manifest: {path}");
        if (signed)
        {
            ctx.Log.Info($"signed with Ed25519 (key id {keyId ?? "-"}) -> {path}.sig; public key {Ed25519.PublicKeyOf(privateKey!)}");
        }
        else
        {
            ctx.Log.Warn($"NOT SIGNED: neither {Ed25519.KeyEnv} nor {Ed25519.KeyFileEnv} is set. The manifest says signing.status = \"pending\"; the release job that holds the key must run this step again before upload (05 B2).");
        }

        return 0;
    }

    private static IEnumerable<ArtifactInput> Scan(string dir, Rid rid)
    {
        if (!Directory.Exists(dir))
        {
            throw new BuildException($"--scan directory not found: {dir}");
        }

        // Velopack 输出目录:安装器、nupkg(全量 / 增量)、portable;feed 的 json 也列入(后端要原样托管)。
        foreach (var file in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Setup", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Portable", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase)
                || (name.StartsWith("releases.", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                || name.Equals("RELEASES", StringComparison.Ordinal))
            {
                yield return new ArtifactInput(rid, null, file);
            }
        }
    }
}

/// <summary>manifest-verify:按公钥验证清单和分离签名,并核对每个产物的 sha256(文件在清单旁边或 --dir 下)。</summary>
internal sealed class ManifestVerifyCommand : IBuildCommand
{
    public CommandSpec Spec { get; } = new("manifest-verify",
        "Verify release-manifest.json against its detached Ed25519 signature and the artifact hashes.",
        ["manifest", "public-key", "dir", "artifacts"],
        ["skip-artifacts"],
        ["manifest-verify --manifest artifacts/release/3.0.0/release-manifest.json --public-key <base64>"]);

    public Task<int> RunAsync(BuildContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var path = args.Require("manifest");
        var key = args.Get("public-key") ?? ctx.EnvNonEmpty("NCW_RELEASE_PUBLIC_KEY") ?? throw new BuildException("Pass --public-key <base64> or set NCW_RELEASE_PUBLIC_KEY.");
        var bytes = File.ReadAllBytes(path);
        var sigPath = path + ".sig";
        if (!File.Exists(sigPath))
        {
            throw new BuildException($"No detached signature: {sigPath}");
        }

        var signature = Convert.FromBase64String(File.ReadAllText(sigPath).Trim());
        if (!Ed25519.Verify(bytes, signature, key))
        {
            throw new BuildException("Signature check FAILED: the manifest does not match its Ed25519 signature for this public key.");
        }

        ctx.Log.Info("signature OK");
        if (!args.Has("skip-artifacts"))
        {
            var manifest = JsonOut.Deserialize(System.Text.Encoding.UTF8.GetString(bytes), BuildJson.Default.ReleaseManifest, path);
            // 产物可能分在几个目录(每个平台一个 Velopack 输出目录):--dir 可重复,清单所在目录也找。
            var dirs = args.GetAll("dir").Append(Path.GetDirectoryName(Path.GetFullPath(path))!).ToArray();
            foreach (var artifact in manifest.Artifacts)
            {
                var file = dirs.Select(d => Path.Combine(d, artifact.FileName)).FirstOrDefault(File.Exists)
                    ?? throw new BuildException($"Artifact missing: {artifact.FileName} (searched {string.Join(", ", dirs)})");

                if (Hashing.Sha256(file) != artifact.Sha256)
                {
                    throw new BuildException($"Artifact hash mismatch: {artifact.FileName}");
                }

                ctx.Log.Info($"sha256 OK  {artifact.FileName}");
            }
        }

        return Task.FromResult(0);
    }
}

/// <summary>keygen:生成一对 Ed25519 密钥(私钥写文件,0600;公钥打印)。给开发和 CI 初始化用,生产密钥由发布负责人保管。</summary>
internal sealed class KeygenCommand : IBuildCommand
{
    public CommandSpec Spec { get; } = new("keygen",
        "Generate an Ed25519 release-signing key pair (private key to a 0600 file, public key printed).",
        ["out", "artifacts"], [], ["keygen --out ~/secrets/ncw-release.key"]);

    public Task<int> RunAsync(BuildContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var path = Path.GetFullPath(args.Require("out"));
        if (File.Exists(path))
        {
            throw new BuildException($"{path} already exists; refusing to overwrite a key.");
        }

        var repoRoot = ctx.Repo.Root + Path.DirectorySeparatorChar;
        if (path.StartsWith(repoRoot, StringComparison.Ordinal))
        {
            throw new BuildException("Refusing to write a private key inside the repository.");
        }

        var (priv, pub) = Ed25519.Generate();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, priv + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        ctx.Log.Info($"private key: {path} (keep secret; use via {Ed25519.KeyFileEnv})");
        ctx.Log.Info($"public key:  {pub}");
        return Task.FromResult(0);
    }
}
