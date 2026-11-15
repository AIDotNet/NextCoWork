using System.Text.Json;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Llm.Sse;

namespace Ncw.Agent.Llm.Wire;

/// <summary>
/// Anthropic SSE → <see cref="LlmEvent"/>(2.x <c>decode/anthropic.ts</c>)。上游的形状是「按 index 的内容块 + 增量」,和我们的一样,基本是直译。
/// 对畸形输入统一「跳过,不抛」:一个 decode 循环里的 throw 会让 Run 无声消失,而起因可能只是兼容网关多发了一行 <c>data: [DONE]</c>。
/// </summary>
public sealed class AnthropicDecoder : IWireDecoder
{
    private enum BlockKind
    {
        Text,
        Thinking,
        ToolUse,
        Other,
    }

    private readonly Dictionary<int, BlockKind> _kinds = [];
    private readonly Dictionary<int, string> _callIds = [];

    /// <summary>thinking 块的签名可能分多个 signature_delta 到达,累积到 content_block_stop 再发。</summary>
    private readonly Dictionary<int, string> _signatures = [];

    private long _input;
    private long _output;
    private long? _cacheCreation;
    private long? _cacheCreation1h;
    private long? _cacheRead;
    private long? _reasoning;
    private StopReason _stop = StopReason.EndTurn;
    private bool _sawStart;
    private bool _sawEnd;

    private TokenUsage Usage() => new(_input, _output, _cacheCreation, _cacheRead, _cacheCreation1h, _reasoning);

    public bool Feed(SseEvent sse, List<LlmEvent> output)
    {
        if (!Jsn.TryParse(sse.Data, out var data) || !data.IsObject())
        {
            return false; // `data: [DONE]` 之类的非 JSON 行、或不是对象:跳过
        }

        // 以 data.type 为准,不以 event: 行为准:中间隔一层兼容网关时 event: 更容易被改写(或干脆不发)。
        var type = data.Str("type") ?? sse.Event;
        switch (type)
        {
            case "message_start":
            {
                var message = data.Obj("message");
                var usage = message.Obj("usage");
                _input = usage.Count("input_tokens") ?? 0;
                var creation = usage.Count("cache_creation_input_tokens");
                var read = usage.Count("cache_read_input_tokens");
                var detail = usage.Obj("cache_creation");
                var write5m = detail.Count("ephemeral_5m_input_tokens");
                var write1h = detail.Count("ephemeral_1h_input_tokens");
                if (creation is not null)
                {
                    _cacheCreation = creation;
                }
                else if (write5m is not null || write1h is not null)
                {
                    _cacheCreation = (write5m ?? 0) + (write1h ?? 0);
                }

                if (write1h is not null)
                {
                    _cacheCreation1h = write1h;
                }

                if (read is not null)
                {
                    _cacheRead = read;
                }

                _reasoning = ReasoningOf(usage) ?? _reasoning;
                _sawStart = true;
                output.Add(new MessageStart(message.Str("model") ?? "<unknown>"));
                return false;
            }

            case "content_block_start":
                BlockStart(data, output);
                return false;

            case "content_block_delta":
                BlockDelta(data, output);
                return false;

            case "content_block_stop":
                BlockStop(data, output);
                return false;

            case "message_delta":
            {
                _stop = AnthropicErrors.ToStopReason(data.Obj("delta").Str("stop_reason"));
                var usage = data.Obj("usage");
                // output_tokens 只在这里给,而且是累计值不是增量:赋值不累加(写成 += 的话,上游多发一条 message_delta 费用就翻倍,且静默)。
                _output = usage.Count("output_tokens") ?? _output;

                // 有些兼容网关 message_start 不带 input 用量,完整账目放在终局 message_delta 里:保留这些上游报的字段。
                _input = usage.Count("input_tokens") ?? _input;
                _cacheRead = usage.Count("cache_read_input_tokens") ?? _cacheRead;
                _cacheCreation = usage.Count("cache_creation_input_tokens") ?? _cacheCreation;
                _reasoning = ReasoningOf(usage) ?? _reasoning;
                return false;
            }

            case "message_stop":
                _sawEnd = true;
                output.Add(new MessageEnd(_stop, Usage()));
                return true;

            case "error":
                output.Add(new StreamError(AnthropicErrors.FromStream(data)));
                return true;

            default: // ping 和未知类型:忽略(上游随时可能加新事件)
                return false;
        }
    }

    private static long? ReasoningOf(JsonElement? usage) =>
        (long?)usage.Num("thinking_tokens") ?? (long?)usage.Num("reasoning_tokens") ?? (long?)usage.Obj("output_tokens_details").Num("reasoning_tokens");

    private void BlockStart(JsonElement data, List<LlmEvent> output)
    {
        if (data.Num("index") is not { } rawIndex || data.Obj("content_block") is not { } block)
        {
            return;
        }

        var index = (int)rawIndex;
        switch (block.Str("type"))
        {
            case "tool_use":
            {
                var callId = block.Str("id") ?? $"call_{index}";
                _kinds[index] = BlockKind.ToolUse;
                _callIds[index] = callId;
                output.Add(new ToolCallStart(index, callId, block.Str("name") ?? "<unknown>"));
                break;
            }

            case "thinking":
            {
                _kinds[index] = BlockKind.Thinking;
                // start 时 signature 一般是空串,真值走 signature_delta。
                if (block.Str("signature") is { Length: > 0 } signature)
                {
                    _signatures[index] = signature;
                }

                if (block.Str("thinking") is { Length: > 0 } text)
                {
                    output.Add(new ThinkingDelta(index, text));
                }

                break;
            }

            case "redacted_thinking":
                // 整块不透明,没有任何 delta,一次到位。必须原样回传,否则下一轮 Anthropic 认为思考链被篡改。
                _kinds[index] = BlockKind.Other;
                output.Add(new BlockOpaque(index, new ProtocolOpaque(WireProtocols.AnthropicName, Jsn.ToElement(
                    new System.Text.Json.Nodes.JsonObject { ["redacted"] = block.Str("data") ?? "" }))));
                break;

            case var other:
            {
                _kinds[index] = other == "text" ? BlockKind.Text : BlockKind.Other;
                if (block.Str("text") is { Length: > 0 } text)
                {
                    output.Add(new TextDelta(index, text));
                }

                break;
            }
        }
    }

    private void BlockDelta(JsonElement data, List<LlmEvent> output)
    {
        if (data.Num("index") is not { } rawIndex || data.Obj("delta") is not { } delta)
        {
            return;
        }

        var index = (int)rawIndex;
        switch (delta.Str("type"))
        {
            case "text_delta":
                if (delta.Str("text") is { } text)
                {
                    output.Add(new TextDelta(index, text));
                }

                break;
            case "thinking_delta": // 注意字段名是 thinking,不是 text
                if (delta.Str("thinking") is { } thinking)
                {
                    output.Add(new ThinkingDelta(index, thinking));
                }

                break;
            case "signature_delta":
                if (delta.Str("signature") is { } signature)
                {
                    _signatures[index] = _signatures.GetValueOrDefault(index, "") + signature;
                }

                break;
            case "input_json_delta":
                // callId 缺失说明漏了 content_block_start:丢掉这个 delta 好过编一个 callId(编出来的 id 回传时不匹配任何 tool_use,直接 400)。
                if (_callIds.TryGetValue(index, out var callId) && delta.Str("partial_json") is { } args)
                {
                    output.Add(new ToolCallDelta(index, callId, args));
                }

                break;
        }
    }

    private void BlockStop(JsonElement data, List<LlmEvent> output)
    {
        if (data.Num("index") is not { } rawIndex)
        {
            return;
        }

        var index = (int)rawIndex;
        if (_signatures.Remove(index, out var signature) && signature.Length > 0)
        {
            output.Add(new BlockOpaque(index, new ProtocolOpaque(WireProtocols.AnthropicName, Jsn.ToElement(
                new System.Text.Json.Nodes.JsonObject { ["signature"] = signature }))));
        }

        if (_kinds.GetValueOrDefault(index) == BlockKind.ToolUse && _callIds.Remove(index, out var callId))
        {
            output.Add(new ToolCallEnd(index, callId));
        }

        _kinds.Remove(index);
    }

    public void End(List<LlmEvent> output)
    {
        // 流结束了但没收到 message_stop = 连接被中途掐断。不补这条的话,上层会等一个永远不来的 MessageEnd(回复停在半句话上,转圈不停)。
        // 压根没开始的空流不补错误:那是路由的事。
        if (_sawStart && !_sawEnd)
        {
            output.Add(new StreamError(OpenAiCommon.Interrupted()));
        }
    }
}
