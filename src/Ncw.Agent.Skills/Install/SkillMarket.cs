using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Ncw.Agent.Skills.Install;

/// <summary>市场上的一条 Skill(2.x 的 <c>SkillMarketItem</c>)。图标路径已经按市场 origin 解析成绝对地址(客户端是本地来源,不能自己解析)。</summary>
public sealed record SkillMarketItem(
    string Slug,
    string Name,
    string DisplayName,
    string Description,
    string Category,
    string? IconUrl,
    string? Author,
    string? Version,
    string? Sha256,
    long? FileSize,
    int? DownloadCount,
    int? TriggerCount);

public sealed record SkillMarketVersion(string Version, string? Changelog, string? Sha256, long? FileSize);

public sealed record SkillMarketDetail(SkillMarketItem Item, IReadOnlyList<SkillMarketVersion> Versions);

/// <summary>桌面授权换来的「可以下载这个版本」凭证:版本与权威摘要(下载前就拿到,包体对不上就拒绝)。</summary>
public sealed record SkillInstallGrant(string Version, string Sha256);

/// <summary>下载进度:已收字节 / 总字节(两个来源都没有时 null:不编造估计值,一条走到 80% 就停下的进度条比明说「不知道」更像卡死)。</summary>
public sealed record SkillDownloadProgress(long Received, long? Total);

/// <summary>
/// 市场目录的 HTTP 抓取(可注入:测试用假实现,生产是 <see cref="HttpSkillMarketClient"/>)。所有失败是 <see cref="SkillException"/> + 稳定 key。
/// </summary>
public interface ISkillMarketClient
{
    Task<IReadOnlyList<SkillMarketItem>> ListAsync(string? query, string? category, CancellationToken ct);

    Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken ct);

    Task<SkillMarketDetail> DetailAsync(string slug, CancellationToken ct);

    /// <summary>向市场要「安装授权」(需要账户令牌,没有 → <see cref="SkillErrorKeys.AuthRequired"/>)。强制服务端的 <c>skills:install</c> scope,并给出权威摘要。</summary>
    Task<SkillInstallGrant> GrantAsync(string slug, string version, CancellationToken ct);

    Task<byte[]> DownloadAsync(string slug, string version, int maxBytes, Action<SkillDownloadProgress>? progress, CancellationToken ct);
}

/// <summary>
/// <see cref="ISkillMarketClient"/> 的 <see cref="HttpClient"/> 实现。默认 origin <c>https://nextco.work/api/</c>(与 2.x 一致)。
/// <paramref name="accessToken"/>:账户访问令牌(设备只在 Agent 内存里用,永远不进任何 DTO / 日志);没有令牌 = 安装授权要不到(<c>skills.authRequired</c>),列表 / 详情照常。
/// </summary>
public sealed class HttpSkillMarketClient(HttpClient http, Func<string?>? accessToken = null, Uri? apiBase = null) : ISkillMarketClient
{
    public static readonly Uri DefaultApiBase = new("https://nextco.work/api/");

    private const int ProgressMinIntervalMs = 120;

    private const int ProgressMinBytes = 256 * 1024;

    private Uri Base => apiBase ?? DefaultApiBase;

    public async Task<IReadOnlyList<SkillMarketItem>> ListAsync(string? query, string? category, CancellationToken ct)
    {
        var parameters = new List<string>();
        if (!string.IsNullOrEmpty(query))
        {
            parameters.Add("q=" + Uri.EscapeDataString(query.Length > 100 ? query[..100] : query));
        }

        if (!string.IsNullOrEmpty(category))
        {
            parameters.Add("category=" + Uri.EscapeDataString(category));
        }

        var payload = await GetDataAsync("skills" + (parameters.Count == 0 ? "" : "?" + string.Join('&', parameters)), ct).ConfigureAwait(false);
        using (payload)
        {
            var items = new List<SkillMarketItem>();
            if (payload.RootElement.ValueKind == JsonValueKind.Object && payload.RootElement.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in array.EnumerateArray())
                {
                    if (Normalize(element) is { } item)
                    {
                        items.Add(item);
                    }
                }
            }

            return items;
        }
    }

    public async Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken ct)
    {
        using var payload = await GetDataAsync("skills/categories", ct).ConfigureAwait(false);
        var result = new List<string>();
        if (payload.RootElement.ValueKind == JsonValueKind.Object && payload.RootElement.TryGetProperty("categories", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            result.AddRange(array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
        }

        return result;
    }

    public async Task<SkillMarketDetail> DetailAsync(string slug, CancellationToken ct)
    {
        using var payload = await GetDataAsync($"skills/{Uri.EscapeDataString(slug)}", ct).ConfigureAwait(false);
        var root = payload.RootElement;
        var skill = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("skill", out var s) && s.ValueKind == JsonValueKind.Object ? s : root;
        var item = Normalize(skill) ?? throw new SkillException(SkillErrorKeys.NetworkFailed, "The market returned an unreadable Skill.");
        var versions = new List<SkillMarketVersion>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("versions", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in array.EnumerateArray())
            {
                if (Str(v, "version") is { } version)
                {
                    versions.Add(new SkillMarketVersion(version, Str(v, "changelog"), Str(v, "sha256"), Long(v, "fileSize")));
                }
            }
        }

        return new SkillMarketDetail(item, versions);
    }

    public async Task<SkillInstallGrant> GrantAsync(string slug, string version, CancellationToken ct)
    {
        var token = accessToken?.Invoke();
        if (string.IsNullOrEmpty(token))
        {
            throw new SkillException(SkillErrorKeys.AuthRequired, "Installing from the market needs an account sign-in.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Base, $"client/skills/{Uri.EscapeDataString(slug)}/install"))
        {
            Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, string> { ["version"] = version }, GrantJsonContext.Default.DictionaryStringString), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw response.StatusCode switch
            {
                HttpStatusCode.Forbidden => new SkillException(SkillErrorKeys.ScopeRequired, "The account may not install Skills."),
                HttpStatusCode.Unauthorized => new SkillException(SkillErrorKeys.AuthRequired, "The account sign-in was rejected."),
                HttpStatusCode.NotFound => new SkillException(SkillErrorKeys.VersionUnavailable, "The market does not have this version."),
                _ => new SkillException(SkillErrorKeys.NetworkFailed, $"The market answered {(int)response.StatusCode}."),
            };
        }

        using var doc = await ParseAsync(response, ct).ConfigureAwait(false);
        var data = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("data", out var d) ? d : doc.RootElement;
        var granted = Str(data, "version");
        var sha = Str(data, "sha256");
        if (granted != version || sha is null || sha.Length != 64 || !sha.All(Uri.IsHexDigit))
        {
            throw new SkillException(SkillErrorKeys.DigestMismatch, "The market did not return a usable digest for this version.");
        }

        return new SkillInstallGrant(granted, sha);
    }

    public async Task<byte[]> DownloadAsync(string slug, string version, int maxBytes, Action<SkillDownloadProgress>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Base, $"skills/{Uri.EscapeDataString(slug)}/versions/{Uri.EscapeDataString(version)}/download"));
        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw response.StatusCode == HttpStatusCode.NotFound
                ? new SkillException(SkillErrorKeys.VersionUnavailable, "The market does not have this version.")
                : new SkillException(SkillErrorKeys.NetworkFailed, $"The market answered {(int)response.StatusCode}.");
        }

        var declared = response.Content.Headers.ContentLength;
        if (declared > maxBytes)
        {
            throw new SkillException(SkillErrorKeys.PackageTooLarge, "The package is larger than the allowed size.");
        }

        long reportedBytes = 0;
        long reportedAt = 0;
        var clock = Stopwatch.StartNew();
        progress?.Invoke(new SkillDownloadProgress(0, declared));
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

                    if (buffer.Length + read > maxBytes)
                    {
                        throw new SkillException(SkillErrorKeys.PackageTooLarge, "The package is larger than the allowed size.");
                    }

                    buffer.Write(chunk, 0, read);
                    // 节流是必需的,不是优化:一个 chunk 一帧的话,20MB 的包会推出上千个事件;进度条本身只有 100 个可见状态
                    var now = clock.ElapsedMilliseconds;
                    if (reportedAt == 0 || now - reportedAt >= ProgressMinIntervalMs || buffer.Length - reportedBytes >= ProgressMinBytes)
                    {
                        reportedAt = Math.Max(now, 1);
                        reportedBytes = buffer.Length;
                        progress?.Invoke(new SkillDownloadProgress(buffer.Length, declared));
                    }
                }

                if (reportedBytes != buffer.Length)
                {
                    progress?.Invoke(new SkillDownloadProgress(buffer.Length, declared)); // 节流会把最后一帧吞掉,条子停在 97% 而包其实已经下完了
                }

                return buffer.ToArray();
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            throw new SkillException(SkillErrorKeys.NetworkFailed, "The download was interrupted.", null, e);
        }
    }

    // ───────────────────────── 内部 ─────────────────────────

    private async Task<JsonDocument> GetDataAsync(string relative, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Base, relative));
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new SkillException(SkillErrorKeys.NetworkFailed, $"The market answered {(int)response.StatusCode}.");
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
            throw new SkillException(SkillErrorKeys.NetworkFailed, "The market returned an unreadable response.", null, e);
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
            throw new SkillException(SkillErrorKeys.NetworkFailed, "The market could not be reached.", null, e);
        }
    }

    private SkillMarketItem? Normalize(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || Str(e, "slug") is not { } slug)
        {
            return null;
        }

        string? author = null;
        if (e.TryGetProperty("author", out var a))
        {
            author = a.ValueKind == JsonValueKind.Object ? Str(a, "name") : a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        }

        var name = Str(e, "name") ?? slug;
        return new SkillMarketItem(
            slug, name, Str(e, "displayName") ?? name, Str(e, "description") ?? "", Str(e, "category") ?? SkillLoader.Uncategorized,
            ResolveIcon(Str(e, "iconUrl")), author, Str(e, "version"), Str(e, "sha256"), Long(e, "fileSize"), Int(e, "downloadCount"), Int(e, "triggerCount"));
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

    private static int? Int(JsonElement e, string name) => Long(e, name) is { } n && n is >= int.MinValue and <= int.MaxValue ? (int)n : null;
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class GrantJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
