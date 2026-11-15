using Ncw.Build.Core;

namespace Ncw.Build.Release;

/// <summary>产物的描述:<c>rid:kind:path</c>(kind 可省略,由文件名推断)。</summary>
internal sealed record ArtifactInput(Rid Rid, string? Kind, string Path)
{
    public static ArtifactInput Parse(string text)
    {
        // Windows 路径里有 "C:\…",所以只在前两个冒号处切:rid : kind : path。
        var first = text.IndexOf(':');
        if (first <= 0)
        {
            throw new BuildException($"Bad --artifact '{text}'. Use <rid>:<kind>:<path> (kind may be empty).");
        }

        var second = text.IndexOf(':', first + 1);
        if (second < 0)
        {
            throw new BuildException($"Bad --artifact '{text}'. Use <rid>:<kind>:<path> (kind may be empty).");
        }

        var kind = text[(first + 1)..second];
        return new ArtifactInput(Rid.Parse(text[..first]), kind.Length == 0 ? null : kind, text[(second + 1)..]);
    }
}

internal static class ManifestBuilder
{
    public static async Task<ReleaseManifest> BuildAsync(
        string version,
        string channel,
        DateTimeOffset createdUtc,
        IEnumerable<ArtifactInput> inputs,
        ReleasePolicy policy,
        string? notes,
        string? keyId,
        CancellationToken ct = default)
    {
        var artifacts = new List<ReleaseArtifact>();
        foreach (var input in inputs)
        {
            if (!File.Exists(input.Path))
            {
                throw new BuildException($"Artifact not found: {input.Path}");
            }

            var info = new FileInfo(input.Path);
            var kind = input.Kind ?? InferKind(info.Name);
            artifacts.Add(new ReleaseArtifact(input.Rid.Value, kind, info.Name, await Hashing.Sha256Async(input.Path, ct).ConfigureAwait(false), info.Length, null));
        }

        if (artifacts.Count == 0)
        {
            throw new BuildException("No artifacts: pass at least one --artifact <rid>:<kind>:<path>.");
        }

        // 稳定顺序:同样的输入生成同样的字节(被签名的是字节)。
        artifacts.Sort((a, b) => string.CompareOrdinal(a.Platform + "/" + a.Kind + "/" + a.FileName, b.Platform + "/" + b.Kind + "/" + b.FileName));
        return new ReleaseManifest(
            ReleaseManifest.CurrentSchema, "NextCoWork", version, channel, createdUtc, policy, notes, [.. artifacts],
            new ManifestSigning("ed25519", keyId, "pending", null));
    }

    /// <summary>按 Velopack 的产物命名推断种类(文件名里的 channel / 版本各不相同,看后缀与关键字)。</summary>
    public static string InferKind(string fileName)
    {
        var name = fileName.ToLowerInvariant();
        if (name.EndsWith("-delta.nupkg", StringComparison.Ordinal))
        {
            return "velopack-delta";
        }

        if (name.EndsWith("-full.nupkg", StringComparison.Ordinal))
        {
            return "velopack-full";
        }

        if (name.Contains("setup", StringComparison.Ordinal))
        {
            return name.EndsWith(".pkg", StringComparison.Ordinal) ? "installer-pkg" : "velopack-setup";
        }

        if (name.Contains("portable", StringComparison.Ordinal))
        {
            return "portable";
        }

        if (name.EndsWith(".appimage", StringComparison.Ordinal))
        {
            return "appimage";
        }

        if (name.EndsWith(".zip", StringComparison.Ordinal))
        {
            return "zip";
        }

        return name.EndsWith(".json", StringComparison.Ordinal) ? "feed" : "file";
    }

    /// <summary>
    /// 写清单,有密钥就签名。返回签名状态。没有密钥时只写清单、状态为 pending,并明确告知(调用方打日志)。
    /// 先把「signed」状态和 keyId 写进清单再签,所以被签的字节就是最终落盘的字节。
    /// </summary>
    public static async Task<bool> WriteAsync(ReleaseManifest manifest, string manifestPath, string? privateKey, string? keyId)
    {
        var sigFile = manifestPath + ".sig";
        if (privateKey is null)
        {
            await File.WriteAllTextAsync(manifestPath, JsonOut.Serialize(manifest, BuildJson.Default.ReleaseManifest)).ConfigureAwait(false);
            if (File.Exists(sigFile))
            {
                File.Delete(sigFile); // 旧签名对新内容无效,留着会误导
            }

            return false;
        }

        var signed = manifest with { Signing = new ManifestSigning("ed25519", keyId, "signed", Path.GetFileName(sigFile)) };
        var bytes = System.Text.Encoding.UTF8.GetBytes(JsonOut.Serialize(signed, BuildJson.Default.ReleaseManifest));
        await File.WriteAllBytesAsync(manifestPath, bytes).ConfigureAwait(false);
        await File.WriteAllTextAsync(sigFile, Convert.ToBase64String(Ed25519.Sign(bytes, privateKey)) + "\n").ConfigureAwait(false);
        return true;
    }
}
