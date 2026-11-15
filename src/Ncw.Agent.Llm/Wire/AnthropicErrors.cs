using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ncw.Agent.Llm.Json;

namespace Ncw.Agent.Llm.Wire;

/// <summary>
/// Anthropic 的错误体 + HTTP 状态码 → <see cref="AgentError"/>。非 2xx 响应不是 SSE 而是一个 JSON 体(兼容中转站还可能是纯文本或 HTML),
/// 由路由拿着响应分类。分类规则属于「协议知识」,和路由的「重试与切换策略」是两件事。
/// </summary>
public static partial class AnthropicErrors
{
    private const int SummaryLimit = 4096;

    [GeneratedRegex(@"cache[_ -]?control|cache[_ -]?breakpoint|(?:^|[^a-z0-9])(ephemeral|ttl)(?:$|[^a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex CacheField();

    [GeneratedRegex(@"prompt is too long|context.{0,20}too long|maximum.{0,20}tokens", RegexOptions.IgnoreCase)]
    private static partial Regex ContextTooLong();

    /// <summary>请求回显不是错误证据:任意深度都剥掉,再参与分类和面向用户的摘要。</summary>
    private static readonly HashSet<string> RequestEchoFields =
        ["request", "request_body", "requestBody", "input", "messages", "system", "tools", "body", "payload"];

    private sealed record Details(string Message, string Kind, bool MentionsCacheField, string Raw, string UpstreamMessage);

    private static string Summary(string value)
    {
        if (value.Length <= SummaryLimit)
        {
            return value;
        }

        // 大型中转响应把有用的字段名埋在 details 里时,有界摘录围绕证据取,而不是取无关的前 4 KB。
        var match = CacheField().Match(value);
        if (!match.Success)
        {
            return value[..SummaryLimit] + "…";
        }

        var before = (SummaryLimit - match.Length) / 3;
        var start = Math.Max(0, match.Index - before);
        var end = Math.Min(value.Length, start + SummaryLimit);
        var adjustedStart = Math.Max(0, end - SummaryLimit);
        return (adjustedStart > 0 ? "…" : "") + value[adjustedStart..end] + (end < value.Length ? "…" : "");
    }

    private static JsonNode? StripEcho(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var copy = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    if (!RequestEchoFields.Contains(key))
                    {
                        copy[key] = StripEcho(value);
                    }
                }

                return copy;
            }

            case JsonArray array:
            {
                var copy = new JsonArray();
                foreach (var item in array)
                {
                    copy.Add(StripEcho(item));
                }

                return copy;
            }

            default:
                return node?.DeepClone();
        }
    }

    private static Details Describe(string bodyText, int status)
    {
        JsonElement? root = null;
        string? serialized = null;
        var isStructured = false;
        if (Jsn.TryParse(bodyText, out var parsed) && parsed.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            isStructured = true;
            if (parsed.ValueKind == JsonValueKind.Object)
            {
                root = parsed;
            }

            serialized = StripEcho(Jsn.ToNode(parsed))?.ToJsonString() ?? "";
        }

        var nested = root.Obj("error");
        var nestedMessage = nested.Str("message");
        var rootMessage = root.Str("message");
        var errorText = root.Prop("error") is { ValueKind: JsonValueKind.String } e ? e.GetString()!.Trim() : "";
        var text = isStructured ? "" : bodyText.Trim();
        var raw = Summary(text.Length > 0 ? text : serialized ?? "");
        var supplied = nestedMessage ?? rootMessage ?? (errorText.Length > 0 ? errorText : null);
        var unbounded = supplied ?? (raw.Length == 0 ? $"Upstream returned {status}" : $"Upstream returned {status}: {raw}");
        var message = Summary(unbounded);
        var kind = nested.Str("type") ?? root.Str("type") ?? "";
        var mentions = new[] { message, kind, text, serialized ?? "" }.Any(v => CacheField().IsMatch(v));
        return new Details(message, kind, mentions, raw, Summary(supplied ?? raw));
    }

    /// <summary>上游的 stop_reason → <see cref="StopReason"/>。未知值映射成 EndTurn 而不是抛错:内容已经收到了,不该让这一轮失败。</summary>
    public static StopReason ToStopReason(string? raw) => raw switch
    {
        "end_turn" => StopReason.EndTurn,
        "tool_use" => StopReason.ToolUse,
        "max_tokens" => StopReason.MaxTokens,
        "stop_sequence" => StopReason.StopSequence,
        "refusal" => StopReason.Refusal,
        _ => StopReason.EndTurn,
    };

    public static AgentError FromHttp(int status, string bodyText, AnthropicCacheTtl cacheTtl = AnthropicCacheTtl.FiveMinutes, string? providerName = null)
    {
        var d = Describe(bodyText, status);

        // 缓存兼容性错误必须覆盖官方 JSON、剥壳 JSON、纯文本/HTML 三类:否则纯文本 "cache_control is not supported"
        // 会被误当普通 400,随后重试/换供应商,既违背配置错误语义又重复产生费用。
        if (status is 400 or 422 && d.MentionsCacheField)
        {
            var standard = d.Kind.Length > 0 && !d.UpstreamMessage.Contains(d.Kind, StringComparison.OrdinalIgnoreCase)
                ? $"{d.Kind}: {d.UpstreamMessage}"
                : d.UpstreamMessage;
            // 中转站可能只在非标准 details 里放缓存拒绝原因,而 message 是笼统的:附上有界摘要(已剥请求回显)。
            var upstreamError = d.Raw.Length > 0 && CacheField().IsMatch(d.Raw) && !CacheField().IsMatch($"{d.UpstreamMessage}\n{d.Kind}")
                ? $"{standard}; {d.Raw}"
                : standard;
            var ttl = cacheTtl == AnthropicCacheTtl.OneHour ? "1h" : "5m";
            return AgentError.Create(
                AgentErrorCode.CacheUnsupported,
                providerName is null ? upstreamError : $"{providerName}: {upstreamError}",
                retryable: false,
                status: status,
                messageKey: providerName is null ? LlmErrorKeys.CacheUnsupportedUnnamed : LlmErrorKeys.CacheUnsupported,
                messageParams: new Dictionary<string, string> { ["provider"] = providerName ?? "", ["ttl"] = ttl, ["detail"] = upstreamError });
        }

        switch (status)
        {
            case 401 or 403:
                return AgentError.Create(AgentErrorCode.Auth, d.Message, false, status);
            case 429:
                return AgentError.Create(AgentErrorCode.RateLimit, d.Message, status: status);
            case 400:
                // 上下文超长在 Anthropic 是 400 + invalid_request_error,只能靠 message 认。
                return ContextTooLong().IsMatch(d.Message)
                    ? AgentError.Create(AgentErrorCode.ContextLength, d.Message, false, status)
                    : AgentError.Create(AgentErrorCode.Provider, d.Message, false, status);
            case 529 or >= 500:
                return AgentError.Create(AgentErrorCode.Provider, d.Message, true, status);
            default:
                return AgentError.Create(AgentErrorCode.Provider, d.Kind.Length == 0 ? d.Message : $"{d.Kind}: {d.Message}", false, status);
        }
    }

    /// <summary>SSE 流里的 <c>event: error</c>(不是 HTTP 错误,连接已经是 200 了)。</summary>
    internal static AgentError FromStream(JsonElement data)
    {
        var err = data.Obj("error");
        var kind = err.Str("type") ?? "";
        var message = err.Str("message") ?? "Upstream reported an error in the stream";
        return kind switch
        {
            "overloaded_error" => AgentError.Create(AgentErrorCode.Provider, message, true),
            "rate_limit_error" => AgentError.Create(AgentErrorCode.RateLimit, message),
            _ => AgentError.Create(AgentErrorCode.Provider, kind.Length == 0 ? message : $"{kind}: {message}", false),
        };
    }
}
