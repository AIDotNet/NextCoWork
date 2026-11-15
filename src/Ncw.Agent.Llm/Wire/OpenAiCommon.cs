using System.Text.Json;
using System.Text.RegularExpressions;
using Ncw.Agent.Llm.Json;

namespace Ncw.Agent.Llm.Wire;

/// <summary>OpenAI 族(Chat / Responses)共用:用量归一、错误分类。</summary>
public static partial class OpenAiCommon
{
    [GeneratedRegex("invalid_api_key|authentication|permission_denied")]
    private static partial Regex AuthCode();

    [GeneratedRegex("context_length|context_window|prompt_too_long")]
    private static partial Regex ContextCode();

    [GeneratedRegex(@"maximum context length|context.{0,30}(exceed|too (long|large))|prompt is too long", RegexOptions.IgnoreCase)]
    private static partial Regex ContextMessage();

    [GeneratedRegex("insufficient_quota|billing|balance")]
    private static partial Regex QuotaCode();

    [GeneratedRegex("rate_limit")]
    private static partial Regex RateLimitCode();

    [GeneratedRegex("server_error|overloaded|temporarily_unavailable")]
    private static partial Regex ServerCode();

    /// <summary>
    /// 从上游错误体里挖出一句人能看懂的话。形状比想象的多:OpenAI 是 <c>{error:{message}}</c>,ChatGPT/Codex 后端是 FastAPI 风格的
    /// <c>{detail}</c>,反代网关常见 <c>{message}</c>,也有 <c>{error:"…"}</c>。兜底不是套话而是把原始 body 原样交出去:
    /// 看不懂的 JSON 也远好过看不见的 JSON。
    /// </summary>
    private static string MessageOf(JsonElement? body, string rawText, int? status)
    {
        var root = body is { ValueKind: JsonValueKind.Object } r ? (JsonElement?)r : null;
        var detail = root.Obj("error") ?? root.Obj("detail") ?? root;
        var found = detail.Str("message") ?? root.Str("detail") ?? root.Str("error") ?? root.Str("message")
            ?? (body is { ValueKind: JsonValueKind.String } s ? s.GetString() : null);
        if (!string.IsNullOrWhiteSpace(found))
        {
            return found;
        }

        if (root is not null)
        {
            var raw = root.Value.GetRawText();
            if (raw != "{}")
            {
                return raw;
            }
        }
        else if (!string.IsNullOrWhiteSpace(rawText))
        {
            return rawText.Trim();
        }

        return status is null ? "Upstream request failed" : $"Upstream request failed (HTTP {status})";
    }

    /// <summary>HTTP 错误和 200 连接里的流内错误共用。<paramref name="bodyText"/> 是原始响应体。</summary>
    public static AgentError ToAgentError(int? status, string bodyText)
    {
        JsonElement? body = Jsn.TryParse(bodyText, out var parsed) ? parsed : null;
        return ToAgentError(status, body, bodyText);
    }

    internal static AgentError ToAgentError(int? status, JsonElement? body, string rawText)
    {
        var root = body is { ValueKind: JsonValueKind.Object } r ? (JsonElement?)r : null;
        var detail = root.Obj("error") ?? root.Obj("detail") ?? root;
        var code = detail.Str("code") ?? detail.Str("type") ?? "";
        var message = MessageOf(body, rawText, status);
        if (message.Length > 4096)
        {
            message = message[..4096];
        }

        if (status is 401 or 403 || AuthCode().IsMatch(code))
        {
            return AgentError.Create(AgentErrorCode.Auth, message, false, status);
        }

        if (ContextCode().IsMatch(code) || ContextMessage().IsMatch(message))
        {
            return AgentError.Create(AgentErrorCode.ContextLength, message, false, status);
        }

        if (QuotaCode().IsMatch(code))
        {
            return AgentError.Create(AgentErrorCode.Provider, message, false, status);
        }

        if (status == 429 || RateLimitCode().IsMatch(code))
        {
            return AgentError.Create(AgentErrorCode.RateLimit, message, status: status);
        }

        var retryable = status is >= 500 or 408 or 409 || ServerCode().IsMatch(code);
        return AgentError.Create(AgentErrorCode.Provider, message, retryable, status);
    }

    internal static AgentError Malformed(string detail) =>
        AgentError.Create(
            AgentErrorCode.Provider,
            $"Invalid upstream response: {detail}",
            retryable: false,
            messageKey: LlmErrorKeys.InvalidResponse,
            messageParams: new Dictionary<string, string> { ["detail"] = detail });

    internal static AgentError Interrupted() =>
        AgentError.Create(
            AgentErrorCode.Network,
            "The upstream connection closed before the response completed.",
            messageKey: LlmErrorKeys.IncompleteResponse);
}

/// <summary>OpenAI 的 prompt_tokens / input_tokens 包含缓存读,而统一用量里 InputTokens 不含:解码时扣掉。</summary>
internal sealed class OpenAiUsage
{
    private long? _input;
    private long? _misses;
    private long? _cached;
    private long _output;
    private long? _reasoning;

    public void Update(JsonElement? usage)
    {
        if (usage is not { ValueKind: JsonValueKind.Object } u)
        {
            return;
        }

        _input = u.Count("prompt_tokens") ?? u.Count("input_tokens") ?? _input;
        _misses = u.Count("prompt_cache_miss_tokens") ?? _misses;
        _cached = u.Obj("prompt_tokens_details").Count("cached_tokens")
            ?? u.Obj("input_tokens_details").Count("cached_tokens")
            ?? u.Count("prompt_cache_hit_tokens") ?? _cached;
        _output = u.Count("completion_tokens") ?? u.Count("output_tokens") ?? _output;
        _reasoning = u.Obj("completion_tokens_details").Count("reasoning_tokens")
            ?? u.Obj("output_tokens_details").Count("reasoning_tokens")
            ?? u.Count("reasoning_tokens") ?? _reasoning;
    }

    public TokenUsage Snapshot()
    {
        var cached = _input is null ? _cached : Math.Min(_cached ?? 0, _input.Value);
        return new TokenUsage(
            _input is null ? _misses ?? 0 : _input.Value - (cached ?? 0),
            _output,
            CacheReadInputTokens: _cached is null ? null : cached ?? 0,
            ReasoningTokens: _reasoning);
    }
}
