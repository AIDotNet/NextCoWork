using System.Buffers;
using System.Text.Json;
using Ncw.Agent.WebTools.Net;

namespace Ncw.Agent.WebTools.Search;

public sealed record AdapterInput(string Query, int Count);

/// <summary>
/// 一家搜索服务。<b>失败就抛,不返回空数组</b>:上层靠异常和空结果区分「这家坏了,换下一家」与「这家好好的,就是没搜到」。
/// HTTP 一律走注入的 <see cref="IWebHttp"/>(测试塞假的,不联网)。
/// </summary>
public delegate Task<IReadOnlyList<SearchResult>> SearchAdapter(AdapterInput input, string apiKey, IWebHttp http, CancellationToken ct);

/// <summary>
/// 六个适配器(2.x <c>search/adapters/*</c>)。每个文件头记着核对到的那一版请求 / 响应形状,这里沿用;「哪家有适配器」的唯一出处是 <see cref="Table"/>:
/// 豆包和 Bing 在目录里标了 unavailable,它们没有适配器,也不该有(写成全表就得为这两家各造一个「抛异常」的假适配器,那种占位实现迟早会被当成「已经接上了,只是有 bug」)。
/// </summary>
public static class SearchAdapters
{
    public static IReadOnlyDictionary<string, SearchAdapter> Table { get; } = new Dictionary<string, SearchAdapter>(StringComparer.Ordinal)
    {
        [SearchProviders.Tavily] = Tavily,
        [SearchProviders.Exa] = Exa,
        [SearchProviders.Brave] = Brave,
        [SearchProviders.Serper] = Serper,
        [SearchProviders.SerpApi] = SerpApi,
        [SearchProviders.Metaso] = Metaso,
    };

    /// <summary>响应正文里截多长进错误信息。够看清 <c>{"error":"invalid api key"}</c>,又不至于把一整页 HTML 倒进去。</summary>
    private const int ErrorBodyChars = 300;

    /// <summary>单条正文截多长(Exa 给的是整页正文,原样堆十条能占掉半个上下文窗口)。</summary>
    private const int ExaTextChars = 1200;

    // Tavily —— POST https://api.tavily.com/search;Authorization: Bearer;{ query, max_results, search_depth };{ results: [{ title, url, content, score, published_date? }] }
    // content 是 Tavily 替我们摘好的正文片段,不是 SERP 那种一行摘要——这正是它在目录里排第一的理由。basic 就够:advanced 贵一个量级。
    private static async Task<IReadOnlyList<SearchResult>> Tavily(AdapterInput input, string apiKey, IWebHttp http, CancellationToken ct)
    {
        var body = Json(w =>
        {
            w.WriteString("query", input.Query);
            w.WriteNumber("max_results", input.Count);
            w.WriteString("search_depth", "basic");
        });
        using var request = Post("https://api.tavily.com/search", body);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        using var doc = await SendAsync(http, request, "Tavily", ct).ConfigureAwait(false);
        var rows = Harvest.ArrayAt(doc.RootElement, "results");
        return Map(SearchProviders.Tavily, Harvest.All(rows.Count > 0 ? rows : Harvest.FindResultArray(doc.RootElement)));
    }

    // Exa —— POST https://api.exa.ai/search;x-api-key;{ query, numResults, contents: { text: { maxCharacters } } };{ results: [{ title, url, publishedDate?, text? }] }
    // ★ contents.text 不能省:省了返回里只有标题和链接,模型拿到一串 URL 却不知道里面写了什么,只能对每一条再调一次 WebFetch——一次搜索变成十次抓取。
    private static async Task<IReadOnlyList<SearchResult>> Exa(AdapterInput input, string apiKey, IWebHttp http, CancellationToken ct)
    {
        var body = Json(w =>
        {
            w.WriteString("query", input.Query);
            w.WriteNumber("numResults", input.Count);
            w.WriteStartObject("contents");
            w.WriteStartObject("text");
            w.WriteNumber("maxCharacters", ExaTextChars);
            w.WriteEndObject();
            w.WriteEndObject();
        });
        using var request = Post("https://api.exa.ai/search", body);
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        using var doc = await SendAsync(http, request, "Exa", ct).ConfigureAwait(false);
        var rows = Harvest.ArrayAt(doc.RootElement, "results");
        return Map(SearchProviders.Exa, Harvest.All(rows.Count > 0 ? rows : Harvest.FindResultArray(doc.RootElement)), ExaTextChars);
    }

    // Brave —— GET https://api.search.brave.com/res/v1/web/search?q&count;X-Subscription-Token + Accept: application/json;{ web: { results: [{ title, url, description, page_age? }] } }
    // ★ 结果数组在 web.results 下,不是顶层 results(顶层还并排放着 news / videos / faq)。取错一层的症状是零结果而非报错。
    private static async Task<IReadOnlyList<SearchResult>> Brave(AdapterInput input, string apiKey, IWebHttp http, CancellationToken ct)
    {
        var url = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(input.Query)}&count={input.Count}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("X-Subscription-Token", apiKey);
        using var doc = await SendAsync(http, request, "Brave Search", ct).ConfigureAwait(false);
        var rows = Harvest.ArrayAt(doc.RootElement, "web", "results");
        return Map(SearchProviders.Brave, Harvest.All(rows.Count > 0 ? rows : Harvest.FindResultArray(doc.RootElement)));
    }

    // Serper —— POST https://google.serper.dev/search;X-API-KEY;{ q, num };{ organic: [{ title, link, snippet, date? }] }
    // ★ 只取 organic。answerBox 那类块是 Google 自己抽出来的答案——混进结果列表,模型会把「Google 说的」当成一个有出处的网页来引用,而它没有能核实的 url。
    private static async Task<IReadOnlyList<SearchResult>> Serper(AdapterInput input, string apiKey, IWebHttp http, CancellationToken ct)
    {
        var body = Json(w =>
        {
            w.WriteString("q", input.Query);
            w.WriteNumber("num", input.Count);
        });
        using var request = Post("https://google.serper.dev/search", body);
        request.Headers.TryAddWithoutValidation("X-API-KEY", apiKey);
        using var doc = await SendAsync(http, request, "Serper", ct).ConfigureAwait(false);
        // 文档里两个名字都出现过(organic / organic_results),两个都试
        var rows = Harvest.ArrayAt(doc.RootElement, "organic").Concat(Harvest.ArrayAt(doc.RootElement, "organic_results")).ToList();
        return Map(SearchProviders.Serper, Harvest.All(rows.Count > 0 ? rows : Harvest.FindResultArray(doc.RootElement)));
    }

    // SerpApi —— GET https://serpapi.com/search.json?engine=google&q&num&api_key;{ organic_results: [...] }
    // ★ Key 进 URL 是这家的接口决定的。出错时不能把 URL 拼进错误信息,否则 Key 会跟着日志和界面上的错误提示走:下面只把服务商名字交给 SendAsync,URL 一个字都不给它。
    private static async Task<IReadOnlyList<SearchResult>> SerpApi(AdapterInput input, string apiKey, IWebHttp http, CancellationToken ct)
    {
        var url = $"https://serpapi.com/search.json?engine=google&q={Uri.EscapeDataString(input.Query)}&num={input.Count}&api_key={Uri.EscapeDataString(apiKey)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var doc = await SendAsync(http, request, "SerpApi", ct).ConfigureAwait(false);
        var rows = Harvest.ArrayAt(doc.RootElement, "organic_results");
        return Map(SearchProviders.SerpApi, Harvest.All(rows.Count > 0 ? rows : Harvest.FindResultArray(doc.RootElement)));
    }

    // 秘塔搜索 —— POST https://metaso.cn/api/open/search/v2;Authorization: Bearer;{ question, lang, stream }
    // ★ 这一家的响应字段没有公开文档,只有请求侧是核对过的,所以是唯一完全靠 FindResultArray 兜底的:按「哪个数组的元素长得像结果」去找,而不是猜一个字段名写死
    // (猜错的症状是 HTTP 200 + 零结果,看起来像「没搜到」,切换链会静静地跳过这一家,谁也不会发现它其实一直是坏的)。
    private static async Task<IReadOnlyList<SearchResult>> Metaso(AdapterInput input, string apiKey, IWebHttp http, CancellationToken ct)
    {
        var body = Json(w =>
        {
            w.WriteString("question", input.Query);
            w.WriteString("lang", "zh");
            w.WriteBoolean("stream", false);
        });
        using var request = Post("https://metaso.cn/api/open/search/v2", body);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        using var doc = await SendAsync(http, request, "Metaso", ct).ConfigureAwait(false);
        return Map(SearchProviders.Metaso, [.. Harvest.All(Harvest.FindResultArray(doc.RootElement)).Take(input.Count)]);
    }

    // ───────────────────────── 共用 ─────────────────────────

    private static IReadOnlyList<SearchResult> Map(string provider, IReadOnlyList<Harvest.Item> items, int? snippetLimit = null) =>
        [.. items.Select(h => new SearchResult(h.Title, h.Url, snippetLimit is { } limit && h.Snippet.Length > limit ? h.Snippet[..limit] : h.Snippet, provider, h.PublishedAt))];

    private static byte[] Json(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static HttpRequestMessage Post(string url, byte[] body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return request;
    }

    /// <summary>
    /// 六个适配器共用的 HTTP 收尾:非 2xx <b>抛</b>(401 该切下一家,零结果不该),错误信息带状态码和响应正文的开头(切换链全败时这些话会拼在一起交给模型,
    /// 「Tavily: 401 Unauthorized」比「搜索失败」有用得多——用户据此知道该去改哪个 Key);响应不是合法 JSON 也抛。
    /// </summary>
    private static async Task<JsonDocument> SendAsync(IWebHttp http, HttpRequestMessage request, string who, CancellationToken ct)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", "NextCoWork/3.0");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = text.Length > ErrorBodyChars ? text[..ErrorBodyChars].Trim() : text.Trim();
            throw new SearchProviderException($"{who} returned HTTP {(int)response.StatusCode}{(string.IsNullOrEmpty(response.ReasonPhrase) ? "" : " " + response.ReasonPhrase)}{(detail.Length == 0 ? "" : ": " + detail)}");
        }

        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new SearchProviderException($"{who} returned something that is not valid JSON.");
        }
    }
}

/// <summary>搜索服务这一家失败了(非 2xx、响应不是 JSON……);消息是英文,会原样拼进给模型的失败说明。</summary>
public sealed class SearchProviderException(string message) : Exception(message);
