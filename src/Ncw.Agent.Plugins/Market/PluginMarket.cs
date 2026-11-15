using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ncw.Agent.Plugins;

/// <summary>市场上的一条插件(2.x <c>PluginMarketItem</c>)。<see cref="Permissions"/> 是这一版声明的<b>必选能力</b>(列表里就要显示:装插件这个决定的全部信息量就在这一行);<see cref="Skills"/> 是它自带的 Skill 名(每一轮对话都在场的唯一一类贡献)。</summary>
public sealed record PluginMarketItem(
    string PluginId,
    string Slug,
    string Publisher,
    string DisplayName,
    string Description,
    string Category,
    string? IconUrl,
    int DownloadCount,
    string? Version,
    string? Engines,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> Skills,
    string Author);

public sealed record PluginMarketPage(IReadOnlyList<PluginMarketItem> Items, bool HasNext, int Page);

public sealed record PluginMarketVersion(string Version, string? Changelog, bool? PermissionEscalated, long? FileSize);

public sealed record PluginMarketDetail(PluginMarketItem Item, IReadOnlyList<PluginMarketVersion> Versions);

/// <summary>市场给的「可以下载这个版本」凭证:版本与权威摘要(下载前就拿到,包体对不上就拒绝)。</summary>
public sealed record PluginInstallGrant(string Version, string Sha256);

public sealed record PluginDownloadProgress(long Received, long? Total);

/// <summary>市场目录与下载(可注入:测试用假实现,生产是 <see cref="HttpPluginMarketClient"/>)。所有失败是 <see cref="PluginException"/> + 稳定 key。</summary>
public interface IPluginMarketClient
{
    /// <param name="clientVersion">本机应用版本:市场按它做灰度 / 过滤,列表里就不会出现这台机器装不上的插件。</param>
    Task<PluginMarketPage> ListAsync(string? query, string? category, int page, int pageSize, string clientVersion, CancellationToken ct);

    Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken ct);

    Task<PluginMarketDetail> DetailAsync(string slug, CancellationToken ct);

    /// <summary>向市场要「安装授权」(需要账户令牌,没有 → <see cref="PluginErrorKeys.AuthRequired"/>)。强制服务端的 <c>plugins:install</c> scope,并给出权威摘要。</summary>
    Task<PluginInstallGrant> GrantAsync(string slug, string version, CancellationToken ct);

    Task<byte[]> DownloadAsync(string slug, string version, int maxBytes, Action<PluginDownloadProgress>? progress, CancellationToken ct);
}

/// <summary>
/// <see cref="IPluginMarketClient"/> 的 <see cref="HttpClient"/> 实现。默认 origin <c>https://nextco.work/api/</c>(与 2.x 一致)。四步安装流程与 Skills 原样相同,一步都不能省:
/// ① 授权(带账户令牌,拿<b>权威</b> sha256 + version)→ ② 校验返回的 version 与请求一致、sha256 形如 64 位 hex → ③ 下载(≤ 20MB,边读边卡)→ ④ 安装器再校验一次 sha256。
/// 账户令牌只在 Agent 内存里用,永远不进任何 DTO / 日志。
/// </summary>
public sealed class HttpPluginMarketClient(HttpClient http, Func<string?>? accessToken = null, Uri? apiBase = null) : IPluginMarketClient
{
    public static readonly Uri DefaultApiBase = new("https://nextco.work/api/");

    private const int ProgressMinIntervalMs = 120;
    private const int ProgressMinBytes = 256 * 1024;

    private Uri Base => apiBase ?? DefaultApiBase;

    public async Task<PluginMarketPage> ListAsync(string? query, string? category, int page, int pageSize, string clientVersion, CancellationToken ct)
    {
        var parameters = new List<string> { $"page={page}", $"pageSize={pageSize}", "client=" + Uri.EscapeDataString(clientVersion) };
        if (!string.IsNullOrEmpty(query))
        {
            parameters.Add("q=" + Uri.EscapeDataString(query.Length > 100 ? query[..100] : query));
        }

        if (!string.IsNullOrEmpty(category))
        {
            parameters.Add("category=" + Uri.EscapeDataString(category));
        }

        using var payload = await GetDataAsync("plugins?" + string.Join('&', parameters), ct).ConfigureAwait(false);
        var items = new List<PluginMarketItem>();
        var hasNext = false;
        if (payload.RootElement.ValueKind == JsonValueKind.Object)
        {
            if (payload.RootElement.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                items.AddRange(array.EnumerateArray().Select(Normalize).OfType<PluginMarketItem>());
            }

            hasNext = payload.RootElement.TryGetProperty("hasNext", out var next) && next.ValueKind == JsonValueKind.True;
        }

        return new PluginMarketPage(items, hasNext, page);
    }

    public async Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken ct)
    {
        using var payload = await GetDataAsync("plugins/categories", ct).ConfigureAwait(false);
        var result = new List<string>();
        if (payload.RootElement.ValueKind == JsonValueKind.Object && payload.RootElement.TryGetProperty("categories", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            result.AddRange(array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
        }

        return result;
    }

    public async Task<PluginMarketDetail> DetailAsync(string slug, CancellationToken ct)
    {
        using var payload = await GetDataAsync($"plugins/{Uri.EscapeDataString(slug)}", ct).ConfigureAwait(false);
        var root = payload.RootElement;
        var plugin = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("plugin", out var p) && p.ValueKind == JsonValueKind.Object ? p : root;
        var item = Normalize(plugin) ?? throw new PluginException(PluginErrorKeys.MarketFailed, "The market returned an unreadable plugin.");
        var versions = new List<PluginMarketVersion>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("versions", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in array.EnumerateArray())
            {
                if (Str(v, "version") is { } version)
                {
                    versions.Add(new PluginMarketVersion(version, Str(v, "changelog"), v.ValueKind == JsonValueKind.Object && v.TryGetProperty("permissionEscalated", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : null, Long(v, "fileSize")));
                }
            }
        }

        return new PluginMarketDetail(item, versions);
    }

    public async Task<PluginInstallGrant> GrantAsync(string slug, string version, CancellationToken ct)
    {
        var token = accessToken?.Invoke();
        if (string.IsNullOrEmpty(token))
        {
            throw new PluginException(PluginErrorKeys.AuthRequired, "Installing from the market needs an account sign-in.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Base, $"client/plugins/{Uri.EscapeDataString(slug)}/install"))
        {
            Content = new StringContent(new JsonObject { ["version"] = version }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw response.StatusCode switch
            {
                HttpStatusCode.Forbidden => new PluginException(PluginErrorKeys.ScopeRequired, "The account may not install plugins."),
                HttpStatusCode.Unauthorized => new PluginException(PluginErrorKeys.AuthRequired, "The account sign-in was rejected."),
                HttpStatusCode.NotFound => new PluginException(PluginErrorKeys.VersionUnavailable, "The market does not have this version."),
                _ => new PluginException(PluginErrorKeys.MarketFailed, $"The market answered {(int)response.StatusCode}.", new Dictionary<string, string> { ["status"] = ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture) }),
            };
        }

        using var doc = await ParseAsync(response, ct).ConfigureAwait(false);
        var data = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("data", out var d) ? d : doc.RootElement;
        var granted = Str(data, "version");
        var sha = Str(data, "sha256");
        if (granted != version || sha is null || sha.Length != 64 || !sha.All(Uri.IsHexDigit))
        {
            throw new PluginException(PluginErrorKeys.DigestMismatch, "The market did not return a usable digest for this version.");
        }

        return new PluginInstallGrant(granted, sha);
    }

    public async Task<byte[]> DownloadAsync(string slug, string version, int maxBytes, Action<PluginDownloadProgress>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Base, $"plugins/{Uri.EscapeDataString(slug)}/versions/{Uri.EscapeDataString(version)}/download"));
        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw response.StatusCode == HttpStatusCode.NotFound
                ? new PluginException(PluginErrorKeys.VersionUnavailable, "The market does not have this version.")
                : new PluginException(PluginErrorKeys.MarketFailed, $"The market answered {(int)response.StatusCode}.");
        }

        var declared = response.Content.Headers.ContentLength;
        if (declared > maxBytes)
        {
            throw new PluginException(PluginErrorKeys.PackageTooLarge, "The package is larger than the allowed size.");
        }

        long reportedBytes = 0;
        long reportedAt = 0;
        var clock = Stopwatch.StartNew();
        progress?.Invoke(new PluginDownloadProgress(0, declared));
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[64 * 1024];
                for (;;)
                {
                    var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    // 边读边卡上限,不是读完再看:一个声称 1MB、实际 2GB 的响应已经把内存吃光了
                    if (buffer.Length + read > maxBytes)
                    {
                        throw new PluginException(PluginErrorKeys.PackageTooLarge, "The package is larger than the allowed size.");
                    }

                    buffer.Write(chunk, 0, read);
                    var now = clock.ElapsedMilliseconds;
                    if (reportedAt == 0 || now - reportedAt >= ProgressMinIntervalMs || buffer.Length - reportedBytes >= ProgressMinBytes)
                    {
                        reportedAt = Math.Max(now, 1);
                        reportedBytes = buffer.Length;
                        progress?.Invoke(new PluginDownloadProgress(buffer.Length, declared));
                    }
                }

                if (reportedBytes != buffer.Length)
                {
                    progress?.Invoke(new PluginDownloadProgress(buffer.Length, declared)); // 节流会吞掉最后一帧
                }

                return buffer.ToArray();
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            throw new PluginException(PluginErrorKeys.MarketFailed, "The download was interrupted.", null, e);
        }
    }

    // ───────────────────────── 内部 ─────────────────────────

    private async Task<JsonDocument> GetDataAsync(string relative, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Base, relative));
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new PluginException(PluginErrorKeys.MarketFailed, $"The market answered {(int)response.StatusCode}.", new Dictionary<string, string> { ["status"] = ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        using var doc = await ParseAsync(response, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        // 市场的信封是 {data: ...};缺了就把整体当数据
        return JsonDocument.Parse((root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) ? data : root).GetRawText());
    }

    private static async Task<JsonDocument> ParseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException e)
        {
            throw new PluginException(PluginErrorKeys.MarketFailed, "The market returned an unreadable response.", null, e);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(request, completion, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new PluginException(PluginErrorKeys.MarketFailed, "The market could not be reached.", null, e);
        }
    }

    /// <summary>
    /// 老服务端不返回 <c>permissions</c> / <c>skills</c> 时兜成空数组(不能让 null 进去);<c>skills</c> 逐条转字符串(它们要直接画到卡片上,而服务端返回的是 JSON,混进数字或 null 不会有任何东西拦它)。
    /// </summary>
    private PluginMarketItem? Normalize(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || Str(e, "slug") is not { } slug)
        {
            return null;
        }

        string author = "";
        if (e.TryGetProperty("author", out var a))
        {
            author = a.ValueKind == JsonValueKind.Object ? Str(a, "name") ?? "" : a.ValueKind == JsonValueKind.String ? a.GetString()! : "";
        }

        var publisher = Str(e, "publisher") ?? "";
        var name = Str(e, "name") ?? slug;
        return new PluginMarketItem(
            Str(e, "pluginId") ?? (publisher.Length > 0 ? $"{publisher}.{name}" : slug), slug, publisher, Str(e, "displayName") ?? name, Str(e, "description") ?? "", Str(e, "category") ?? "",
            ResolveIcon(Str(e, "iconUrl")), (int)Math.Clamp(Long(e, "downloadCount") ?? 0, 0, int.MaxValue), Str(e, "version"), Str(e, "engines"),
            Strings(e, "permissions"), Strings(e, "skills"), author);
    }

    private static List<string> Strings(JsonElement e, string name)
    {
        var list = new List<string>();
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
        {
            list.AddRange(array.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString()).Where(s => s.Length > 0));
        }

        return list;
    }

    /// <summary>图标:服务器路径按 API 根解析成绝对地址(客户端在本地来源上跑);只认 http / https,别的当没有。</summary>
    private string? ResolveIcon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(Base, value.Trim(), out var url))
        {
            return null;
        }

        return url.Scheme is "http" or "https" ? url.AbsoluteUri : null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}
