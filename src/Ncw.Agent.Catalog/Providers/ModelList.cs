using System.Text.Json;
using System.Text.RegularExpressions;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Catalog;

/// <summary>从上游 <c>GET …/models</c> 拉回来的一条。<b>这不是配置,是上游报上来的事实</b>:两族协议的模型列表端点都不提供窗口、能力。</summary>
public sealed record FetchedModel(string Id, string? DisplayName = null);

/// <summary>「从服务商拉取模型列表」的请求构造、响应解析、失败归类(2.x <c>kernel/upstream/model-list.ts</c>)。纯函数,不发请求。</summary>
public static partial class ModelList
{
    /// <summary>Anthropic 分页一页的上限(官方 max)。直接顶满,不做翻页循环:没有任何一家上游有 1000 个模型。</summary>
    public const int AnthropicPageLimit = 1000;

    /// <summary>
    /// 拉列表的路径。两族协议的版本段约定是反的(Anthropic 的 base 不带 <c>/v1</c>,OpenAI 族的带):Responses 没有自己的列表端点,和 Chat 共用 <c>/models</c>。
    /// 和 <see cref="WireProtocols.PathOf"/> 是同一套约定的两张表,改一张必须改另一张。
    /// </summary>
    public static string PathOf(WireProtocol protocol) => protocol == WireProtocol.Anthropic ? "/v1/models" : "/models";

    public sealed record ListRequest(string Url, IReadOnlyDictionary<string, string> Headers);

    /// <summary>
    /// 拼出「拉模型列表」这一次请求。<paramref name="apiKey"/> 为 null = 不带鉴权头(OpenRouter / DeepInfra 这类免鉴权也能拉):
    /// 传 null 而不是空串,因为空串会拼出 <c>Authorization: Bearer </c>,有些网关会当成「鉴权失败」,让用户去查一把他根本没填的 key。
    /// Anthropic 默认只回 20 条,必须显式要一页大的——漏了它的表现不是报错,是列表少了一截。
    /// </summary>
    public static ListRequest Request(WireProtocol protocol, string baseUrl, string? apiKey)
    {
        var url = WireProtocols.JoinUrl(baseUrl, PathOf(protocol));
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["accept"] = "application/json" };
        if (protocol == WireProtocol.Anthropic)
        {
            headers["anthropic-version"] = "2023-06-01";
            if (apiKey is not null)
            {
                headers["x-api-key"] = apiKey;
            }

            return new ListRequest($"{url}?limit={AnthropicPageLimit}", headers);
        }

        if (apiKey is not null)
        {
            headers["authorization"] = $"Bearer {apiKey}";
        }

        return new ListRequest(url, headers);
    }

    /// <summary>
    /// 「拉模型列表」的完整鉴权形状,和对话请求共用同一套来源(传输装饰器):签名式凭据(今天只有 Ollama Cloud,accessToken 槽里是 SSH 私钥)
    /// <b>绝不带 bearer</b>——2.x 实测事故:拿它拼 <c>Bearer -----BEGIN OPENSSH PRIVATE KEY-----…</c>,多行头抛异常,私钥整段进了错误弹窗。
    /// </summary>
    public static ListRequest RequestFor(ProviderConfig provider, WireProtocol protocol, ProviderCredential? credential, ITransportDecorator transport, RequestContext context)
    {
        var signer = credential is OAuthCredential
            ? transport.Decorate(provider, protocol, credential, context).Signer
            : null;
        var key = credential is null or SignatureCredential || signer is not null ? null : CredentialFormat.BearerOf(credential);
        var request = Request(protocol, provider.BaseUrl, key);
        if (signer is null)
        {
            return request;
        }

        var target = new UriBuilder(request.Url);
        var signed = signer.Sign("GET", new Uri(request.Url).AbsolutePath);
        var query = System.Web.HttpUtility.ParseQueryString(target.Query);
        foreach (var (name, value) in signed.Query ?? new Dictionary<string, string>())
        {
            query[name] = value;
        }

        target.Query = query.ToString();
        var headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in signed.Headers ?? new Dictionary<string, string>())
        {
            headers[name] = value;
        }

        return new ListRequest(target.Uri.ToString(), headers);
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]+-----[\s\S]*?-----END [A-Z ]+-----")]
    private static partial Regex PrivateKeyBlock();

    /// <summary>把文本里的密钥材料抹掉(兜底:任何未来的「头里带了不该带的东西」都先在这里被拦成 <c>[redacted key]</c>)。</summary>
    public static string RedactSecrets(string text) => PrivateKeyBlock().Replace(text, "[redacted key]");

    /// <summary>
    /// 解模型列表。容错的对象是「外壳」(<c>{data:[…]}</c> / <c>{models:[…]}</c> / 裸数组),不是「条目」:条目里的 <c>id</c> 是这个协议唯一真正稳定的东西。
    /// 认不出 id 的条目直接丢掉,不给它编一个(编出来的会被当成 upstreamModel 存进别名表,然后在某次真实对话里以 404 出现)。
    /// 保持上游顺序:Anthropic 是新的在前,那个顺序有信息量。
    /// </summary>
    public static IReadOnlyList<FetchedModel> Parse(WireProtocol protocol, JsonElement payload)
    {
        var result = new List<FetchedModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Envelope(payload))
        {
            if (IdOf(row) is not { } id || !seen.Add(id))
            {
                continue;
            }

            // OpenAI 族不读 display_name:那是 Anthropic 的字段。
            var name = protocol == WireProtocol.Anthropic ? DisplayNameOf(row) : null;
            result.Add(new FetchedModel(id, name));
        }

        return result;
    }

    private static IEnumerable<JsonElement> Envelope(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
        {
            return payload.EnumerateArray();
        }

        if (payload.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        foreach (var name in new[] { "data", "models" })
        {
            if (payload.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
            {
                return array.EnumerateArray();
            }
        }

        return [];
    }

    private static string? IdOf(JsonElement row)
    {
        if (row.ValueKind == JsonValueKind.String)
        {
            return Trimmed(row.GetString());
        }

        return row.ValueKind == JsonValueKind.Object && row.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? Trimmed(id.GetString())
            : null;
    }

    private static string? DisplayNameOf(JsonElement row) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty("display_name", out var n) && n.ValueKind == JsonValueKind.String
            ? Trimmed(n.GetString())
            : null;

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>失败归类。<see cref="Key"/> 供控制端翻译,<see cref="Detail"/> 是从响应里抠出来的一句话(不是整页 HTML)。</summary>
    public sealed record Failure(string Key, int Status, string? Detail);

    /// <summary>
    /// 把一次失败的拉取变成能给用户看的东西。不要把响应体原样往外送:网关和反代出错时回的往往是整页 HTML,几 KB 的标签糊在弹窗里,
    /// 用户看不出「这是网关拦的」还是「key 错了」。能解出 JSON 里的 message 就只给它,解不出就截成一行,一看就是 HTML 的不给。
    /// 401 / 403 与 404 单独归类——它们的修法(去填 key / 检查地址里的版本段)和别的失败完全不同。
    /// </summary>
    public static Failure Classify(int status, string body)
    {
        var detail = ExtractMessage(body);
        var key = status is 401 or 403 ? ProviderErrorKeys.FetchRejected
            : status == 404 ? ProviderErrorKeys.FetchNotFound
            : ProviderErrorKeys.FetchFailed;
        return new Failure(key, status, detail);
    }

    private static string? ExtractMessage(string body)
    {
        JsonElement parsed;
        try
        {
            using var document = JsonDocument.Parse(body);
            parsed = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            var line = body.Trim().Split('\n')[0].Trim();
            return line.Length == 0 || line.StartsWith('<') ? null : Truncate(line);
        }

        if (parsed.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (parsed.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String)
            {
                return Truncate(error.GetString()!);
            }

            if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(message.GetString()))
            {
                return Truncate(message.GetString()!);
            }
        }

        return parsed.TryGetProperty("message", out var top) && top.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(top.GetString())
            ? Truncate(top.GetString()!)
            : null;
    }

    private static string Truncate(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed[..200] + "…";
    }
}
