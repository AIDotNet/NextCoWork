using System.Text;
using System.Text.Json.Nodes;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Thinking;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Llm.Routing;

/// <summary>
/// 一次上游对话请求的组装:编码管线 + HTTP 请求。路由(<see cref="LlmRouter"/>)和供应商调试(<c>Diagnostics.ProviderProbe</c>)共用这一份,
/// 所以「调试通过」和「对话能用」是同一段代码的结论(docs/design/07「供应商调试」)。消息里的图片引用由调用方先解析好。
/// </summary>
public static class WireRequests
{
    /// <summary>
    /// 编码管线:编码 → 思考参数适配 → 用户请求补丁(禁止改 model/messages/tools/鉴权)→ 再次强制思考偏好
    /// → 重新应用 Anthropic 缓存和身份(补丁不能覆盖)→ 传输装饰。<paramref name="request"/> 的消息已经按目标协议转换过(<c>HistoryTransform.Prepare</c>)。
    /// </summary>
    public static (JsonObject Body, IReadOnlyDictionary<string, string> Headers) EncodeBody(
        ProviderConfig provider, ModelBinding binding, LlmRequest request, WireProtocol protocol, RequestContext context, TransportDecoration decoration)
    {
        var thinkingConfig = ThinkingConfigFor(request, binding, protocol);
        var encoded = WireCodecs.Encode(protocol, request, binding.UpstreamModel, new EncodeOptions(
            context.WorkspaceId, provider.CacheTtl, ReasoningReplayPolicy.For(thinkingConfig, binding.UpstreamModel)));

        var thinkingInput = new ThinkingAdapterInput(
            protocol, binding.UpstreamModel, thinkingConfig, ReasoningFor(request, binding, thinkingConfig),
            request.Options.MaxOutputTokens, binding.Preset, binding.ReasoningEfforts);
        var adapted = ThinkingAdapter.Apply(encoded.Body, thinkingInput);
        var patched = RequestPatches.Apply(adapted, binding.Patches);
        var guarded = ThinkingAdapter.Enforce(patched, thinkingInput);

        // 模型级补丁可以自定义普通参数,但供应商自己的 Anthropic 身份和缓存字段在最终线上边界重新应用:
        // metadata.user_id 和缓存始终强制,TTL 以供应商为准。
        var anthropic = protocol == WireProtocol.Anthropic
            ? AnthropicRequestPolicy.Apply(guarded, context.WorkspaceId, provider.CacheTtl)
            : guarded;
        return (decoration.Body is { } transform ? transform(anthropic) : anthropic, encoded.Headers);
    }

    /// <summary>
    /// 一次发送的 HTTP 请求。每次发送都要重新调用(签名在这里现算:时间戳式签名复用旧值等于没重试)。
    /// <paramref name="credential"/> 为 null = 不带鉴权头(只有调试会这样发:本机服务、免鉴权的网关)。有签名器时不加鉴权头:私钥永远不进请求头。
    /// </summary>
    public static HttpRequestMessage BuildHttp(
        string url, string payload, IReadOnlyDictionary<string, string> fixedHeaders, ProviderCredential? credential, WireProtocol protocol,
        TransportDecoration decoration, string userAgent, string? bearerOverride = null)
    {
        var target = new UriBuilder(url);
        var signed = decoration.Signer?.Sign("POST", new Uri(url).AbsolutePath);
        if (signed?.Query is { Count: > 0 } query)
        {
            var existing = target.Query.TrimStart('?');
            var added = string.Join('&', query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            target.Query = existing.Length == 0 ? added : existing + "&" + added;
        }

        var message = new HttpRequestMessage(HttpMethod.Post, target.Uri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation("user-agent", userAgent);
        foreach (var (key, value) in fixedHeaders)
        {
            if (!key.Equals("content-type", StringComparison.OrdinalIgnoreCase))
            {
                message.Headers.TryAddWithoutValidation(key, value);
            }
        }

        if (decoration.Signer is null && credential is not null)
        {
            var bearer = bearerOverride ?? CredentialFormat.BearerOf(credential);
            var (name, headerValue) = protocol.AuthHeader(bearer);
            message.Headers.TryAddWithoutValidation(name, headerValue);
        }

        foreach (var (key, value) in decoration.Headers ?? new Dictionary<string, string>())
        {
            message.Headers.TryAddWithoutValidation(key, value);
        }

        foreach (var (key, value) in signed?.Headers ?? new Dictionary<string, string>())
        {
            message.Headers.TryAddWithoutValidation(key, value);
        }

        foreach (var drop in decoration.DropHeaders ?? [])
        {
            message.Headers.Remove(drop);
        }

        message.Headers.TryAddWithoutValidation("accept", "text/event-stream");
        return message;
    }

    /// <summary>
    /// 条目没有声明思考配置时的遗留检测:旧的模型导入默认 thinking=false 且没有 ThinkingConfig,
    /// 已知的开关型协议仍应支持显式 Off。
    /// </summary>
    private static ThinkingConfig? ThinkingConfigFor(LlmRequest request, ModelBinding binding, WireProtocol protocol)
    {
        // 自适应思考的 Claude(4.6+,issue #202):目录 / 2.x 别名里的预算或开关声明对它们是错的(budget_tokens、disabled 会被 400),
        // 一律按档位处理,具体线形由 ThinkingAdapter 按型号决定。显式声明不支持 / 总是思考的照旧。
        if (protocol == WireProtocol.Anthropic && binding.Preset is AdapterPreset.Auto or AdapterPreset.Anthropic
            && ClaudeThinking.For(binding.UpstreamModel) is not null
            && binding.Thinking is not { Mode: ThinkingMode.Unsupported or ThinkingMode.Always })
        {
            return new ThinkingConfig(ThinkingMode.Effort, binding.Thinking?.DefaultEnabled ?? true, binding.Thinking?.DefaultEffort);
        }

        if (binding.Thinking is { } declared)
        {
            return declared;
        }

        if (request.Options.Thinking != ThinkingLevel.Off)
        {
            return null;
        }

        var model = binding.UpstreamModel;
        return protocol == WireProtocol.Anthropic
            || ReasoningReplayPolicy.DeepSeekName().IsMatch(model)
            || ReasoningReplayPolicy.GlmName().IsMatch(model)
            || ReasoningReplayPolicy.HunyuanName().IsMatch(model)
            ? new ThinkingConfig(ThinkingMode.Toggle, DefaultEnabled: false)
            : null;
    }

    private static ResolvedThinking? ReasoningFor(LlmRequest request, ModelBinding binding, ThinkingConfig? config)
    {
        // 思考预算的天花板用这次真的会发出去的 max_tokens(全局设置项决定),不和目录里的 maxOutputTokens 取小。
        // Anthropic 的 budget_tokens >= max_tokens 那道 400 由适配器兜底。
        return ThinkingResolver.Resolve(request.Options.Thinking, config, request.Options.MaxOutputTokens, binding.ReasoningEfforts);
    }
}
