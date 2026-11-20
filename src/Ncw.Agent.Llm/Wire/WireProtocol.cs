using System.Text.Json.Nodes;
using Ncw.Agent.Llm.Sse;

namespace Ncw.Agent.Llm.Wire;

/// <summary>
/// 三种线协议。所有供应商(Gemini、Grok、Kimi、DeepSeek、GLM、Ollama……)都通过其中之一接入。
/// 新增协议值要谨慎:透传数据按协议打标签,加一个新值会让已有记录的推理回放全部失效(01-llm §3)。
/// </summary>
public enum WireProtocol
{
    Anthropic,
    OpenAiChat,
    OpenAiResponses,
}

public static class WireProtocols
{
    public const string AnthropicName = "anthropic";
    public const string OpenAiChatName = "openai-chat";
    public const string OpenAiResponsesName = "openai-responses";

    /// <summary>协议名,同时是 <see cref="ProtocolOpaque.Protocol"/> 的标签和 <see cref="Provenance.Api"/>。</summary>
    public static string NameOf(this WireProtocol protocol) => protocol switch
    {
        WireProtocol.Anthropic => AnthropicName,
        WireProtocol.OpenAiChat => OpenAiChatName,
        WireProtocol.OpenAiResponses => OpenAiResponsesName,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
    };

    public static WireProtocol? Parse(string? name) => name switch
    {
        AnthropicName => WireProtocol.Anthropic,
        OpenAiChatName => WireProtocol.OpenAiChat,
        OpenAiResponsesName => WireProtocol.OpenAiResponses,
        _ => null,
    };

    /// <summary>每种协议实际要打的路径(规则在 <see cref="Ncw.Core.Domain.ProviderEndpoints.ChatPath"/>,控制端显示「最终地址」用同一份)。</summary>
    public static string PathOf(this WireProtocol protocol) =>
        Ncw.Core.Domain.ProviderEndpoints.ChatPath(protocol.NameOf()) ?? throw new ArgumentOutOfRangeException(nameof(protocol));

    /// <summary>base 与 path 拼接(规则在 <see cref="Ncw.Core.Domain.ProviderEndpoints.JoinUrl"/>,控制端显示「最终地址」用同一份)。</summary>
    public static string JoinUrl(string baseUrl, string path) => Ncw.Core.Domain.ProviderEndpoints.JoinUrl(baseUrl, path);

    /// <summary>鉴权头。OAuth 的 access token 和 API Key 在鉴权头这一点上同构。编码器不碰凭据,由路由统一加。</summary>
    public static KeyValuePair<string, string> AuthHeader(this WireProtocol protocol, string bearer) =>
        protocol == WireProtocol.Anthropic
            ? new("x-api-key", bearer)
            : new("authorization", "Bearer " + bearer);
}

/// <summary>Anthropic 缓存生存期:5 分钟不写 ttl(兼容旧中转),1 小时必须显式写。</summary>
public enum AnthropicCacheTtl
{
    FiveMinutes,
    OneHour,
}

/// <summary>Responses 协议回传历史思考的方言。</summary>
public enum ReasoningReplay
{
    /// <summary>只回放上游签发的载体(id / summary / encrypted_content),推理正文一律剥掉——官方 OpenAI 输入侧的约束。</summary>
    OpaqueOnly,

    /// <summary>除载体外还要用思考正文补 content——DeepSeek 思考模式的硬校验。</summary>
    TextRequired,
}

/// <summary>编码器的上游事实,由路由判定后传入。编码器自己不知道在给哪个供应商服务。</summary>
public sealed record EncodeOptions(
    string UserId = "",
    AnthropicCacheTtl CacheTtl = AnthropicCacheTtl.FiveMinutes,
    ReasoningReplay ReasoningReplay = ReasoningReplay.OpaqueOnly);

/// <summary>相对路径由 <see cref="WireProtocols.JoinUrl"/> 与 base 拼接。不含凭据。</summary>
public sealed record EncodedRequest(string Path, IReadOnlyDictionary<string, string> Headers, JsonObject Body);

/// <summary>
/// 解码器:喂一个 SSE 事件,吐出零到多个事件。畸形输入的态度统一——跳过而不抛;真正的致命错误走 <see cref="StreamError"/>。
/// </summary>
public interface IWireDecoder
{
    /// <summary>返回 true = 已经产出终局(MessageEnd 或 StreamError),不要再喂了。</summary>
    bool Feed(SseEvent sse, List<LlmEvent> output);

    /// <summary>流结束(EOF)时调用,补上缺失的终局。</summary>
    void End(List<LlmEvent> output);
}

/// <summary>三种协议的编解码分发。纯函数,没有供应商分支。</summary>
public static class WireCodecs
{
    public static EncodedRequest Encode(WireProtocol protocol, LlmRequest request, string upstreamModel, EncodeOptions options) =>
        protocol switch
        {
            WireProtocol.Anthropic => AnthropicEncoder.Encode(request, upstreamModel, options),
            WireProtocol.OpenAiChat => OpenAiChatEncoder.Encode(request, upstreamModel),
            WireProtocol.OpenAiResponses => OpenAiResponsesEncoder.Encode(request, upstreamModel, options),
            _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
        };

    public static IWireDecoder CreateDecoder(WireProtocol protocol) => protocol switch
    {
        WireProtocol.Anthropic => new AnthropicDecoder(),
        WireProtocol.OpenAiChat => new OpenAiChatDecoder(),
        WireProtocol.OpenAiResponses => new OpenAiResponsesDecoder(),
        _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
    };

    /// <summary>把 SSE 事件流解码成统一事件流(测试和路由共用)。终局之后不再读。</summary>
    public static async IAsyncEnumerable<LlmEvent> DecodeAsync(WireProtocol protocol, IAsyncEnumerable<SseEvent> events)
    {
        var decoder = CreateDecoder(protocol);
        var batch = new List<LlmEvent>();
        var finished = false;
        await foreach (var sse in events.ConfigureAwait(false))
        {
            batch.Clear();
            var terminal = decoder.Feed(sse, batch);
            foreach (var ev in batch)
            {
                yield return ev;
            }

            if (terminal)
            {
                finished = true;
                break;
            }
        }

        if (!finished)
        {
            batch.Clear();
            decoder.End(batch);
            foreach (var ev in batch)
            {
                yield return ev;
            }
        }
    }
}
