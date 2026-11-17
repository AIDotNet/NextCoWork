namespace Ncw.Client.Updates;

/// <summary>更新流程里可以预见的失败。<see cref="Kind"/> 进状态(界面翻译),<see cref="Exception.Message"/> 只给日志。</summary>
public sealed class UpdateException(UpdateFailureKind kind, string detail, Exception? inner = null) : Exception(detail, inner)
{
    public UpdateFailureKind Kind { get; } = kind;
}

/// <summary>
/// feed 的位置。默认指向设计文档约定的后端(04 §3、05 B3),开发和测试可以指向本地目录或本地 HTTP 服务(<c>NCW_UPDATE_FEED</c>)。
/// 模板里的 <c>{channel}</c> 换成 <c>stable</c> / <c>beta</c>,<c>{rid}</c> 换成平台标识(<c>osx-arm64</c>、<c>win-x64</c>…)。
/// </summary>
/// <remarks>
/// <b>每个平台一个 feed 目录</b>(<c>{rid}</c>):vpk 的产物文件名里没有平台标识,<c>releases.&lt;channel&gt;.json</c> 也是同名文件,
/// 几个平台放同一个目录会互相覆盖。后端按这个布局实现(docs/design/04 §7.4「后端」)。
/// </remarks>
public sealed record FeedLocation(Uri? Http, string? Directory)
{
    /// <summary>设计文档约定的后端地址(04 §3、05 B3 的 v3 feed)。</summary>
    public const string DefaultTemplate = "https://nextco.work/api/client/updates/v3/{channel}/{rid}/";

    public const string ManifestName = "release-manifest.json";

    public const string SignatureName = "release-manifest.json.sig";

    public string Describe => Http?.AbsoluteUri ?? Directory ?? "";

    /// <summary>
    /// 解析模板。<c>https://</c> 或本机回环上的 <c>http://</c>(开发)是 HTTP feed;绝对路径或 <c>file://</c> 是本地目录。
    /// 其他(包括公网的明文 <c>http://</c>)返回 null:更新包不走明文。
    /// </summary>
    public static FeedLocation? Resolve(string template, UpdateChannel channel, string rid)
    {
        var text = template.Replace("{channel}", UpdateChannels.Id(channel), StringComparison.Ordinal).Replace("{rid}", rid, StringComparison.Ordinal).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
            {
                return new FeedLocation(null, uri.LocalPath);
            }

            if (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            {
                return new FeedLocation(EnsureSlash(uri), null);
            }

            // Windows 的盘符路径(C:\feed)也会被 Uri 当成「scheme = c」:按路径处理;其他 scheme(ftp、公网 http)不接受
            return Path.IsPathRooted(text) ? new FeedLocation(null, Path.GetFullPath(text)) : null;
        }

        // 没有 scheme:相对路径(开发时在仓库根目录里指向 artifacts/…)
        return new FeedLocation(null, Path.GetFullPath(text));
    }

    private static Uri EnsureSlash(Uri uri) => uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
}

/// <summary>读 feed 里的小文件(清单、签名)。大文件(安装包)不走这里,由更新引擎下载。</summary>
public interface IFeedReader
{
    /// <summary>读一个文件。不存在返回 null;连不上、超过 <paramref name="maxBytes"/> 抛 <see cref="UpdateException"/>(<see cref="UpdateFailureKind.Network"/> / <see cref="UpdateFailureKind.FeedInvalid"/>)。</summary>
    Task<byte[]?> ReadAsync(string name, int maxBytes, CancellationToken ct);
}

/// <summary>本地目录 feed。文件名不能带路径分隔符(不能逃出 feed 目录)。</summary>
public sealed class DirectoryFeedReader(string directory) : IFeedReader
{
    public async Task<byte[]?> ReadAsync(string name, int maxBytes, CancellationToken ct)
    {
        if (name.AsSpan().IndexOfAny('/', '\\') >= 0 || name is "." or "..")
        {
            throw new UpdateException(UpdateFailureKind.FeedInvalid, $"bad feed file name '{name}'");
        }

        var path = Path.Combine(directory, name);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            if (new FileInfo(path).Length > maxBytes)
            {
                throw new UpdateException(UpdateFailureKind.FeedInvalid, $"{name} is larger than {maxBytes} bytes");
            }

            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException(UpdateFailureKind.Network, $"cannot read {path}: {e.Message}", e);
        }
    }
}

/// <summary>HTTP(S) feed。大小在读的过程中卡(不信任 Content-Length)。</summary>
public sealed class HttpFeedReader(HttpClient client, Uri baseUri) : IFeedReader
{
    public async Task<byte[]?> ReadAsync(string name, int maxBytes, CancellationToken ct)
    {
        if (name.AsSpan().IndexOfAny('/', '\\') >= 0 || name is "." or "..")
        {
            throw new UpdateException(UpdateFailureKind.FeedInvalid, $"bad feed file name '{name}'");
        }

        var uri = new Uri(baseUri, name);
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException(UpdateFailureKind.Network, $"{uri} answered HTTP {(int)response.StatusCode}");
            }

            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    throw new UpdateException(UpdateFailureKind.FeedInvalid, $"{name} is larger than {maxBytes} bytes");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            throw new UpdateException(UpdateFailureKind.Network, $"cannot reach {uri}: {e.Message}", e);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException(UpdateFailureKind.Network, $"{uri} timed out");
        }
    }
}

/// <summary>
/// 「读清单 → 验签」这一步(引擎和测试共用):清单和签名都从同一个 feed 读,验签之前不信任里面任何东西。
/// </summary>
public static class SignedManifest
{
    /// <summary>清单上限:产物条目不多,64 KiB 绰绰有余;更大一定有问题。</summary>
    public const int MaxManifestBytes = 256 * 1024;

    public static async Task<ReleaseManifest> ReadAsync(IFeedReader feed, ReleaseKeyRing keys, UpdateChannel channel, CancellationToken ct)
    {
        var bytes = await feed.ReadAsync(FeedLocation.ManifestName, MaxManifestBytes, ct).ConfigureAwait(false)
            ?? throw new UpdateException(UpdateFailureKind.FeedInvalid, "the feed has no release-manifest.json");
        var sig = await feed.ReadAsync(FeedLocation.SignatureName, 4096, ct).ConfigureAwait(false);
        var result = ManifestVerifier.Verify(bytes, sig is null ? null : System.Text.Encoding.ASCII.GetString(sig), keys, channel);
        return result.Manifest ?? throw new UpdateException(result.Failure!.Value, result.Detail ?? "the manifest was rejected");
    }
}
