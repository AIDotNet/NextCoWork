using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Llm.Sse;

namespace Ncw.Agent.Llm.Wire;

/// <summary>
/// OpenAI Chat Completions SSE → <see cref="LlmEvent"/>(2.x <c>decode/openai-chat.ts</c>)。Chat 把文本和推理拍平,工具调用独立编号。
/// 没有 <c>finish_reason</c> 的 EOF 永远不算成功(哪怕中转发了 <c>[DONE]</c>)。
/// </summary>
public sealed class OpenAiChatDecoder : IWireDecoder
{
    private sealed class Call
    {
        public int Index;
        public string Id = "";
        public string Name = "";
        public string Args = "";
        public bool Started;
    }

    private readonly OpenAiUsage _usage = new();
    private readonly SortedDictionary<int, Call> _calls = [];
    private int _nextIndex;
    private int? _textIndex;
    private int? _thinkingIndex;
    private bool _started;
    private string? _finish;
    private bool _refused;
    private bool _done;

    /// <summary>
    /// 上游这一条 tool_calls 该并进哪一格。只在它没给 <c>index</c> 时用(Google 的 OpenAI 兼容层把每个调用一次性给全,一个 index 都不带)。
    /// 三步顺序不能换:同 id 已存在 → 并进去;position 那一格还可用 → 用它;否则另开一格
    /// (并行调用一条一条发且都省略 index 时,并进去会把两个调用串成一个,参数拼成非法 JSON)。
    /// </summary>
    private int SlotFor(string id, int position)
    {
        if (id.Length > 0)
        {
            foreach (var (key, call) in _calls)
            {
                if (call.Id.Length > 0 && (call.Id == id || (!call.Started && id.StartsWith(call.Id, StringComparison.Ordinal))))
                {
                    return key;
                }
            }
        }

        if (!_calls.TryGetValue(position, out var held) || !held.Started || held.Id.Length == 0 || held.Id == id)
        {
            return position;
        }

        return _calls.Keys.Max() + 1;
    }

    private static void StartCall(Call call, List<LlmEvent> output)
    {
        if (call.Started || call.Id.Length == 0 || call.Name.Length == 0)
        {
            return;
        }

        call.Started = true;
        output.Add(new ToolCallStart(call.Index, call.Id, call.Name));
        if (call.Args.Length > 0)
        {
            output.Add(new ToolCallDelta(call.Index, call.Id, call.Args));
        }

        call.Args = "";
    }

    public bool Feed(SseEvent sse, List<LlmEvent> output)
    {
        if (_done)
        {
            return true;
        }

        var text = sse.Data.Trim();
        if (text == "[DONE]")
        {
            _done = true;
            Finish(output);
            return true;
        }

        if (text.Length == 0)
        {
            return false;
        }

        if (!Jsn.TryParse(text, out var data))
        {
            return Fail(output, OpenAiCommon.Malformed("invalid Chat Completions JSON"));
        }

        if (!data.IsObject())
        {
            return false;
        }

        if (data.Prop("error") is not null || sse.Event == "error")
        {
            return Fail(output, OpenAiCommon.ToAgentError(null, data, text));
        }

        _usage.Update(data.Obj("usage"));
        if (data.Prop("choices") is not { ValueKind: JsonValueKind.Array } choices)
        {
            return false;
        }

        JsonElement? choice = null;
        var position = 0;
        foreach (var c in choices.EnumerateArray())
        {
            if (c.ValueKind == JsonValueKind.Object && (c.Count("index") == 0 || (position == 0 && c.Prop("index") is null)))
            {
                choice = c;
                break;
            }

            position++;
        }

        if (choice is not { } picked)
        {
            return false; // 只有 usage 的块,或别的 choice
        }

        if (!_started)
        {
            _started = true;
            output.Add(new MessageStart(data.Str("model") ?? "<unknown>"));
        }

        var delta = picked.Obj("delta") ?? picked.Obj("message");

        // 有的中转在 finish_reason 块之后还继续吐游离内容(它们的多路复用器缓冲竞态):丢掉,别把一个完整的响应打断。
        if (_finish is not null && delta is { } stray && stray.EnumerateObject().Any())
        {
            return false;
        }

        if (delta is { } d)
        {
            if (d.Str("reasoning_content") is { } thinking)
            {
                if (_thinkingIndex is null)
                {
                    _thinkingIndex = _nextIndex++;
                    output.Add(new BlockOpaque(_thinkingIndex.Value, new ProtocolOpaque(WireProtocols.OpenAiChatName,
                        Jsn.ToElement(new JsonObject { ["field"] = "reasoning_content" }))));
                }

                if (thinking.Length > 0)
                {
                    output.Add(new ThinkingDelta(_thinkingIndex.Value, thinking));
                }
            }

            var refusal = d.Str("refusal");
            if (!string.IsNullOrEmpty(refusal))
            {
                _refused = true;
            }

            var content = d.Str("content") ?? refusal;
            if (!string.IsNullOrEmpty(content))
            {
                _textIndex ??= _nextIndex++;
                output.Add(new TextDelta(_textIndex.Value, content));
            }

            if (d.Prop("tool_calls") is { ValueKind: JsonValueKind.Array } toolCalls)
            {
                var slotPosition = 0;
                foreach (var raw in toolCalls.EnumerateArray())
                {
                    if (!AcceptToolCall(raw, slotPosition++, output))
                    {
                        return true;
                    }
                }
            }
        }

        _finish = picked.Str("finish_reason") ?? _finish;
        return false;
    }

    private bool AcceptToolCall(JsonElement tool, int position, List<LlmEvent> output)
    {
        var fn = tool.Obj("function");
        var id = tool.Str("id") ?? "";
        var name = fn.Str("name") ?? "";
        var upstreamIndex = tool.Count("index") is { } explicitIndex ? (int)explicitIndex : SlotFor(id, position);
        if (!tool.IsObject() || (tool.Prop("function") is { } f && f.ValueKind != JsonValueKind.Object)
            || (tool.Str("type") is { } type && type != "function"))
        {
            Fail(output, OpenAiCommon.Malformed("invalid tool call"));
            return false;
        }

        if (!_calls.TryGetValue(upstreamIndex, out var call))
        {
            call = new Call { Index = _nextIndex++ };
            _calls[upstreamIndex] = call;
        }

        if (call.Started && ((id.Length > 0 && id != call.Id) || (name.Length > 0 && name != call.Name)))
        {
            Fail(output, OpenAiCommon.Malformed("tool identity changed during streaming"));
            return false;
        }

        if (!call.Started)
        {
            if (id != call.Id)
            {
                call.Id += id;
            }

            if (name != call.Name)
            {
                call.Name += name;
            }
        }

        var args = fn.Str("arguments") ?? "";
        if (call.Started)
        {
            if (args.Length > 0)
            {
                output.Add(new ToolCallDelta(call.Index, call.Id, args));
            }
        }
        else
        {
            call.Args += args;
            if (args.Length > 0)
            {
                StartCall(call, output);
            }
        }

        return true;
    }

    private bool Fail(List<LlmEvent> output, AgentError error)
    {
        _done = true;
        output.Add(new StreamError(error));
        return true;
    }

    private void Finish(List<LlmEvent> output)
    {
        // 没有 finish_reason 的 EOF 永远不算成功,即使中转发了 [DONE]。
        if (!_started || _finish is null)
        {
            output.Add(new StreamError(OpenAiCommon.Interrupted()));
            return;
        }

        var stop = _finish == "length" ? StopReason.MaxTokens
            : _finish == "content_filter" || _refused ? StopReason.Refusal
            : _finish == "tool_calls" || (_finish == "stop" && _calls.Count > 0) ? StopReason.ToolUse
            : StopReason.EndTurn;
        if (stop == StopReason.ToolUse)
        {
            if (_calls.Count == 0 || _calls.Values.Any(c => c.Id.Length == 0 || c.Name.Length == 0)
                || _calls.Values.Select(c => c.Id).Distinct().Count() != _calls.Count)
            {
                output.Add(new StreamError(OpenAiCommon.Malformed("missing or duplicate tool call identity")));
                return;
            }
        }

        // finish=length 时也要把已经开始的工具调用收尾(参数大概率被截断,由上层判失败并提示重发);没有身份的残片丢弃。
        foreach (var call in _calls.Values.OrderBy(c => c.Index))
        {
            if (stop is StopReason.ToolUse or StopReason.MaxTokens)
            {
                StartCall(call, output);
                if (call.Started)
                {
                    output.Add(new ToolCallEnd(call.Index, call.Id));
                }
            }
        }

        output.Add(new MessageEnd(stop, _usage.Snapshot()));
    }

    public void End(List<LlmEvent> output)
    {
        if (_done)
        {
            return;
        }

        _done = true;
        // EOF 没有 [DONE]:同样要有 finish_reason 才算成功。
        Finish(output);
    }
}
