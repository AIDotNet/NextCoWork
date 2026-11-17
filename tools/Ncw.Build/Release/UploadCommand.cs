using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ncw.Build.Cli;
using Ncw.Build.Core;
using Ncw.Build.Publish;

namespace Ncw.Build.Release;

/// <summary>
/// upload:把一个平台的 Velopack 输出目录发布到后端的 3.x 更新源(05 B3;后端 <c>/api/client/updates/v3</c>,见 docs/design/04 §7.4「后端」)。
/// <para>
/// 两步,和后端的存储模型一一对应:① 清单里列出的每个文件按 sha256 上传(<c>HEAD</c> 先问,已有就跳过,重跑不重传);
/// ② 一次 <c>publish</c> 把清单原始字节 + 分离签名交上去,后端核对每个文件都在、大小一致,再原子地换上新快照。
/// 第 ② 步之前,客户端看到的一直是上一份完整的快照。
/// </para>
/// <para>
/// <b>上传前先在本地做客户端会做的那些检查</b>(后端没有发布公钥、不验签):签名必须是 <c>signed</c> 并且用信任列表里的公钥验得过,
/// 通道、平台一致,每个文件的 sha256 / 大小和清单一致。验不过就不上传——否则发出去的是一份所有客户端都会拒绝的更新(而且会显示成「签名有问题」)。
/// </para>
/// </summary>
internal sealed class UploadCommand : IBuildCommand
{
    public const string TokenEnv = "NCW_RELEASE_UPLOAD_TOKEN";
    public const string ServerEnv = "NCW_RELEASE_SERVER";
    public const string DefaultServer = "https://nextco.work";

    /// <summary>测试用:换掉 HTTP 处理器。</summary>
    internal static Func<HttpMessageHandler>? HandlerFactory { get; set; }

    public CommandSpec Spec { get; } = new("upload",
        "Publish one platform's Velopack output (packages + signed release-manifest.json) to the 3.x update feed on the backend.",
        ["rid", "channel", "dir", "manifest", "server", "public-key"],
        ["allow-older", "dry-run"],
        [
            "upload --rid osx-arm64 --channel stable",
            "upload --rid win-x64 --dir artifacts/velopack/win-x64/stable --dry-run",
        ]);

    public async Task<int> RunAsync(BuildContext ctx, ParsedArgs args, CancellationToken ct)
    {
        var rid = Rid.Parse(args.Require("rid"));
        var channel = args.Get("channel") ?? "stable";
        var dir = args.Get("dir") ?? ctx.Repo.VelopackDir(rid, channel);
        var manifestPath = args.Get("manifest") ?? Path.Combine(dir, "release-manifest.json");
        var dryRun = args.Has("dry-run");
        var server = ServerUri(args.Get("server") ?? ctx.EnvNonEmpty(ServerEnv) ?? DefaultServer);
        var token = ctx.EnvNonEmpty(TokenEnv);
        if (token is null && !dryRun)
        {
            throw new BuildException($"Set {TokenEnv} (the backend's ClientUpdates:UploadToken), or pass --dry-run to only run the local checks.");
        }

        ctx.Log.Step($"upload {rid} ({channel}) from {dir}");
        var (manifest, bytes, signature, files) = await CheckLocallyAsync(ctx, args, rid, channel, dir, manifestPath, ct).ConfigureAwait(false);
        ctx.Log.Info($"local checks OK: {manifest.Version}, {files.Count} file(s), signature verified");
        if (dryRun)
        {
            ctx.Log.Info($"--dry-run: nothing sent to {server}");
            return 0;
        }

        using var http = new HttpClient(HandlerFactory?.Invoke() ?? new SocketsHttpHandler(), disposeHandler: true)
        {
            BaseAddress = new Uri(server, "api/client/updates/v3/"),
            Timeout = TimeSpan.FromMinutes(30),
        };
        http.DefaultRequestHeaders.Add("X-Client-Upload-Token", token);

        long sent = 0;
        foreach (var (artifact, path) in files)
        {
            using (var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"blobs/{artifact.Sha256}"), ct).ConfigureAwait(false))
            {
                if (head.StatusCode == HttpStatusCode.OK && head.Content.Headers.ContentLength == artifact.Size)
                {
                    ctx.Log.Info($"already there  {artifact.FileName}");
                    continue;
                }

                EnsureAuthorized(head);
            }

            await using var stream = File.OpenRead(path);
            using var content = new StreamContent(stream, 1 << 16);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = artifact.Size;
            using var put = await http.PutAsync($"blobs/{artifact.Sha256}", content, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(put, $"uploading {artifact.FileName}", ct).ConfigureAwait(false);
            sent += artifact.Size;
            ctx.Log.Info($"uploaded       {artifact.FileName}  {artifact.Size / 1024.0 / 1024.0:F1} MiB");
        }

        var body = JsonSerializer.Serialize(new UploadPublishRequest(Convert.ToBase64String(bytes), signature, args.Has("allow-older")), BuildJson.Default.UploadPublishRequest);
        using var publish = await http.PostAsync($"{channel}/{rid.Value}/publish", new StringContent(body, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(publish, "publishing", ct).ConfigureAwait(false);
        ctx.Log.Info($"published {manifest.Version} to {new Uri(server, $"api/client/updates/v3/{channel}/{rid.Value}/")} ({sent / 1024.0 / 1024.0:F1} MiB sent)");
        return 0;
    }

    private static async Task<(ReleaseManifest Manifest, byte[] Bytes, string Signature, List<(ReleaseArtifact Artifact, string Path)> Files)> CheckLocallyAsync(
        BuildContext ctx, ParsedArgs args, Rid rid, string channel, string dir, string manifestPath, CancellationToken ct)
    {
        if (!File.Exists(manifestPath))
        {
            throw new BuildException($"No release manifest at {manifestPath}. Run `manifest --scan {dir} --rid {rid} --channel {channel} --out {dir}` first.");
        }

        var bytes = await File.ReadAllBytesAsync(manifestPath, ct).ConfigureAwait(false);
        var manifest = JsonOut.Deserialize(Encoding.UTF8.GetString(bytes), BuildJson.Default.ReleaseManifest, manifestPath);
        if (manifest.Signing.Status != "signed")
        {
            throw new BuildException($"The manifest is not signed (signing.status = \"{manifest.Signing.Status}\"). Run `manifest` again with the release signing key.");
        }

        var sigPath = manifestPath + ".sig";
        if (!File.Exists(sigPath))
        {
            throw new BuildException($"No detached signature next to the manifest: {sigPath}");
        }

        var signature = (await File.ReadAllTextAsync(sigPath, ct).ConfigureAwait(false)).Trim();
        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(signature);
        }
        catch (FormatException)
        {
            throw new BuildException($"{sigPath} is not base64.");
        }

        var publicKey = PublicKeyFor(ctx, args, manifest.Signing.KeyId);
        if (!Ed25519.Verify(bytes, raw, publicKey))
        {
            throw new BuildException("The manifest does not verify against the release public key. Clients would reject this update; nothing was uploaded.");
        }

        if (manifest.Channel != channel)
        {
            throw new BuildException($"The manifest is for channel '{manifest.Channel}', not '{channel}'.");
        }

        if (!manifest.Artifacts.Any(a => a.FileName == $"releases.{channel}.json"))
        {
            throw new BuildException($"The manifest does not list releases.{channel}.json (Velopack reads it first). Was it made with `manifest --scan`?");
        }

        var files = new List<(ReleaseArtifact, string)>();
        var dirs = new[] { dir, Path.GetDirectoryName(Path.GetFullPath(manifestPath))! };
        foreach (var artifact in manifest.Artifacts)
        {
            if (artifact.Platform != rid.Value)
            {
                throw new BuildException($"{artifact.FileName} is for {artifact.Platform}, not {rid}: one feed per platform.");
            }

            var path = dirs.Select(d => Path.Combine(d, artifact.FileName)).FirstOrDefault(File.Exists)
                ?? throw new BuildException($"Missing file: {artifact.FileName} (looked in {string.Join(", ", dirs.Distinct())}).");
            if (new FileInfo(path).Length != artifact.Size || await Hashing.Sha256Async(path, ct).ConfigureAwait(false) != artifact.Sha256)
            {
                throw new BuildException($"{artifact.FileName} does not match the manifest (size or sha256). Re-run `manifest` after the last change to the folder.");
            }

            files.Add((artifact, path));
        }

        return (manifest, bytes, signature, files);
    }

    /// <summary>
    /// 验签用的公钥:<c>--public-key</c> &gt; 和 <c>publish-desktop</c> 共用的信任列表 <c>NCW_RELEASE_PUBLIC_KEYS</c>(按清单的 keyId 取)
    /// &gt; <c>NCW_RELEASE_PUBLIC_KEY</c>。用信任列表最好:验的正是客户端内置的那组公钥。
    /// </summary>
    private static string PublicKeyFor(BuildContext ctx, ParsedArgs args, string? keyId)
    {
        if (args.Get("public-key") is { } explicitKey)
        {
            return explicitKey;
        }

        if (ctx.EnvNonEmpty(PublishPlans.ReleasePublicKeysEnv) is { } list)
        {
            var keys = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => e.Split(':', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
            if (keyId is not null && keys.TryGetValue(keyId, out var match))
            {
                return match;
            }

            throw new BuildException($"The manifest is signed with key '{keyId ?? "(none)"}', which is not in {PublishPlans.ReleasePublicKeysEnv} ({string.Join(", ", keys.Keys)}). Clients built with that list would reject it.");
        }

        return ctx.EnvNonEmpty("NCW_RELEASE_PUBLIC_KEY")
            ?? throw new BuildException($"Pass --public-key, or set {PublishPlans.ReleasePublicKeysEnv} (kid:base64,…) or NCW_RELEASE_PUBLIC_KEY, so the signature can be checked before upload.");
    }

    /// <summary>和客户端同一条规则:公网只走 HTTPS,本机回环上的 http 只给开发。</summary>
    internal static Uri ServerUri(string text)
    {
        if (!Uri.TryCreate(text.EndsWith('/') ? text : text + "/", UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            throw new BuildException($"--server must be an https:// URL (or http:// on loopback for development): {text}");
        }

        return uri;
    }

    private static void EnsureAuthorized(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new BuildException($"The backend rejected the upload token ({TokenEnv}).");
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        EnsureAuthorized(response);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // 后端的 /api 组统一包成 { code, message, data };取不到 message 就给原文的前一段
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var message = text;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            {
                message = m.GetString()!;
            }
        }
        catch (JsonException)
        {
        }

        throw new BuildException($"{what} failed: HTTP {(int)response.StatusCode} {message[..Math.Min(message.Length, 500)]}");
    }
}

/// <summary>后端 <c>POST /v3/{channel}/{rid}/publish</c> 的请求体:清单原始字节的 base64(签名针对原始字节,JSON 不能在传输中被改写)+ 分离签名。</summary>
internal sealed record UploadPublishRequest(string Manifest, string Signature, bool AllowOlder);
