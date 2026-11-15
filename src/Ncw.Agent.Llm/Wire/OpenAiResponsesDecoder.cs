using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Llm.Sse;

namespace Ncw.Agent.Llm.Wire;

/// <summary>
/// OpenAI Responses SSE → <see cref="LlmEvent"/>(2.x <c>decode/openai-responses.ts</c>)。
/// output_index、content_index、call_id 是三个独立的命名空间;条目身份按 id → call_id → output_index 依次识别(id 优先,下标只是提示)。
/// </summary>
public sealed class OpenAiResponsesDecoder : IWireDecoder
{
    private sealed class InvalidResponse(string message) : Exception(message);

    private sealed class Segment
    {
        public int Index;
        public string Text = "";
    }

    private sealed class Item
    {
        public int Index;
        public string Kind = "";
        public string Id = "";
        public string CallId = "";
        public string Name = "";
        public string Args = "";
        public bool Started;
        public bool OpaqueSent;

        /// <summary>键是 <c>{lane}:{partIndex}</c>。</summary>
        public readonly Dictionary<string, Segment> Segments = [];

        /// <summary>这个 reasoning item 出现过推理正文:此后摘要不再发出(两者内容高度重复)。</summary>
        public bool PreferContent;
    }

    private enum Lane
    {
        Main,
        Content,
    }

    // 给每个 output item 预留一个命名空间:晚到的内容块必须仍排在下一个 item 之前。
    // 摘要占 [0, ContentBase),推理正文占 [ContentBase, Stride):ReasoningItem 的 summary[] 和 content[] 是并列字段,下标都从 0 数起,
    // 不分开的话 summary_index:0 和 content_index:0 会落进同一个段,拼成一段乱码。
    private const int Stride = 1_000_000;
    private const int ContentBase = 500_000;
    private const int MaxOutputIndex = 2000; // int 下标:2000 * Stride 不溢出

    /// <summary>
    /// 需要我们回 *_output 的工具项(模型在等我们把执行结果送回去):静默丢掉会让模型以为调用过了、我们却什么都没返回,上下文错位且不报错。
    /// 白名单,默认落在「忽略」一侧——托管工具(web_search_call 等)由 OpenAI 自己执行,OpenAI 以后加新的托管类型不会炸我们。
    /// </summary>
    private static readonly HashSet<string> ClientToolItems =
        ["local_shell_call", "shell_call", "apply_patch_call", "custom_tool_call", "computer_call", "mcp_approval_request"];

    private readonly Dictionary<int, Item> _items = [];
    private readonly Dictionary<string, int> _byId = [];
    private readonly Dictionary<string, int> _byCall = [];
    private readonly OpenAiUsage _usage = new();
    private bool _started;
    private bool _refused;
    private bool _done;

    private static int LaneBase(Lane lane) => lane == Lane.Content ? ContentBase : 0;

    /// <summary>
    /// 槽里那条和现在这条是不是两回事。function_call 不能走 reasoning 那条「同类就并进去」的宽松规矩:并行调用时网关在终局 output 里
    /// 重排顺序、重新生成 item id 都很常见,并进去会把 B 的参数追加到 A 上,必然错。
    /// </summary>
    private static bool Conflicts(Item occupant, string kind, string? id, string? callId)
    {
        if (occupant.Kind != kind)
        {
            return true;
        }

        if (kind != "function_call")
        {
            return false;
        }

        return (id is not null && occupant.Id.Length > 0 && occupant.Id != id)
            || (callId is not null && occupant.CallId.Length > 0 && occupant.CallId != callId);
    }

    private Item ItemAt(int hint, string kind, string? id, string? callId)
    {
        if (hint >= MaxOutputIndex)
        {
            throw new InvalidResponse("output_index exceeds supported range");
        }

        int? known = null;
        if (id is not null && _byId.TryGetValue(id, out var byId))
        {
            known = byId;
        }
        else if (callId is not null && _byCall.TryGetValue(callId, out var byCall))
        {
            known = byCall;
        }

        var index = known ?? hint;
        if (known is null && _items.TryGetValue(hint, out var occupant) && Conflicts(occupant, kind, id, callId))
        {
            index = hint + 1;
            while (_items.ContainsKey(index))
            {
                index++;
            }
        }

        if (index >= MaxOutputIndex)
        {
            throw new InvalidResponse("output_index exceeds supported range");
        }

        if (!_items.TryGetValue(index, out var item))
        {
            item = new Item { Index = index * Stride, Kind = kind };
            _items[index] = item;
        }

        if (id is not null && item.Id.Length == 0)
        {
            item.Id = id;
            _byId[id] = index;
        }

        if (callId is not null && item.CallId.Length == 0)
        {
            item.CallId = callId;
            _byCall[callId] = index;
        }

        return item;
    }

    private static void Text(Item item, Lane lane, int partIndex, string value, bool complete, List<LlmEvent> output)
    {
        if (partIndex >= ContentBase)
        {
            throw new InvalidResponse("content index exceeds supported range");
        }

        if (item.PreferContent && lane == Lane.Main && item.Kind == "reasoning")
        {
            return;
        }

        var key = $"{(lane == Lane.Content ? "content" : "main")}:{partIndex}";
        if (!item.Segments.TryGetValue(key, out var segment))
        {
            segment = new Segment { Index = item.Index + LaneBase(lane) + partIndex };
            item.Segments[key] = segment;
        }

        // 快照对不上时保留已经流式收到的内容继续跑,不中止:规范只把 .done 的 text 描述为 "final text content",
        // 没有任何措辞保证它等于所有 delta 的拼接。工具参数不适用这条,见 ArgumentsDelta。
        if (complete && !value.StartsWith(segment.Text, StringComparison.Ordinal))
        {
            return;
        }

        var delta = complete ? value[segment.Text.Length..] : value;
        segment.Text += delta;
        if (delta.Length > 0)
        {
            output.Add(item.Kind == "reasoning" ? new ThinkingDelta(segment.Index, delta) : new TextDelta(segment.Index, delta));
        }
    }

    private static bool SameArguments(string a, string b)
    {
        // 网关重新序列化会改缩进、空格、转义和数字写法,这些一个都不影响工具拿到的入参。任一边解析不出来就算不同。
        if (!Jsn.TryParse(a, out var ea) || !Jsn.TryParse(b, out var eb))
        {
            return false;
        }

        return ea.GetRawText() == eb.GetRawText() || JsonNode.DeepEquals(JsonNode.Parse(a), JsonNode.Parse(b));
    }

    private static void ArgumentsDelta(Item item, string value, bool complete, List<LlmEvent> output)
    {
        if (!item.Started)
        {
            if (item.CallId.Length == 0 || item.Name.Length == 0)
            {
                throw new InvalidResponse("missing function call identity");
            }

            item.Started = true;
            output.Add(new ToolCallStart(item.Index, item.CallId, item.Name));
        }

        // 不能像文本那样无条件降级:工具参数会被原样交给工具去执行,快照和流式内容不一致意味着手上这份入参是错的,
        // 而 ToolCallDelta 是只进不退的。宁可报错也不能拿错的入参去调用工具。
        // 但「不是前缀」不等于「不一样」:空快照(网关只是没给终局 arguments)和只是重新序列化(JSON 值相同)都放行。
        if (complete && !value.StartsWith(item.Args, StringComparison.Ordinal))
        {
            if (value.Length > 0 && !SameArguments(value, item.Args))
            {
                throw new InvalidResponse("completed arguments do not match streamed arguments");
            }

            return;
        }

        var delta = complete ? value[item.Args.Length..] : value;
        item.Args += delta;
        if (delta.Length > 0)
        {
            output.Add(new ToolCallDelta(item.Index, item.CallId, delta));
        }
    }

    private void AcceptItem(JsonElement value, int outputIndex, bool complete, List<LlmEvent> output)
    {
        var kind = value.Str("type") ?? throw new InvalidResponse("missing output item type");
        if (ClientToolItems.Contains(kind))
        {
            throw new InvalidResponse($"unsupported output item: {kind}");
        }

        var item = ItemAt(outputIndex, kind, value.Str("id"), value.Str("call_id"));
        switch (kind)
        {
            case "function_call":
            {
                var callId = value.Str("call_id") ?? item.CallId;
                var name = value.Str("name") ?? item.Name;
                if (item.Started && (item.CallId != callId || item.Name != name))
                {
                    throw new InvalidResponse("function identity changed");
                }

                item.CallId = callId;
                item.Name = name;
                ArgumentsDelta(item, value.Str("arguments") ?? "", true, output);
                break;
            }

            case "message" when value.Prop("content") is { ValueKind: JsonValueKind.Array } content:
            {
                var i = 0;
                foreach (var part in content.EnumerateArray())
                {
                    if (part.Str("type") == "refusal")
                    {
                        _refused = true;
                    }

                    if ((part.Str("text") ?? part.Str("refusal")) is { } body)
                    {
                        Text(item, Lane.Main, i, body, true, output);
                    }

                    i++;
                }

                break;
            }

            case "reasoning":
            {
                var content = value.Arr("content");
                if (content.Count > 0)
                {
                    item.PreferContent = true;
                }

                for (var i = 0; i < content.Count; i++)
                {
                    if (content[i].Str("text") is { } body)
                    {
                        Text(item, Lane.Content, i, body, true, output);
                    }
                }

                if (!item.PreferContent)
                {
                    var summary = value.Arr("summary");
                    for (var i = 0; i < summary.Count; i++)
                    {
                        if (summary[i].Str("text") is { } body)
                        {
                            Text(item, Lane.Main, i, body, true, output);
                        }
                    }
                }

                if (complete && !item.OpaqueSent)
                {
                    item.OpaqueSent = true;
                    // 完整条目(含 encrypted_content)只发一次。下标要跟着实际生效的那条 lane 走:opaque 自己不带文字,
                    // 靠和文字段落在同一个 index 上才会被合进同一个思考块——落错 lane 界面上会多出一个空的思考块。
                    var data = new JsonObject { ["item"] = Jsn.ToNode(value) };
                    output.Add(new BlockOpaque(
                        item.Index + LaneBase(item.PreferContent ? Lane.Content : Lane.Main),
                        new ProtocolOpaque(WireProtocols.OpenAiResponsesName, Jsn.ToElement(data))));
                }

                break;
            }
        }
    }

    public bool Feed(SseEvent sse, List<LlmEvent> output)
    {
        if (_done)
        {
            return true;
        }

        try
        {
            return Process(sse, output);
        }
        catch (InvalidResponse e)
        {
            _done = true;
            output.Add(new StreamError(OpenAiCommon.Malformed(e.Message)));
            return true;
        }
    }

    private bool Process(SseEvent sse, List<LlmEvent> output)
    {
        var text = sse.Data.Trim();
        if (text == "[DONE]")
        {
            // 终局靠 response.completed;[DONE] 之前没有它就是被截断。
            _done = true;
            output.Add(new StreamError(OpenAiCommon.Interrupted()));
            return true;
        }

        if (text.Length == 0)
        {
            return false;
        }

        if (!Jsn.TryParse(text, out var data))
        {
            throw new InvalidResponse("invalid Responses JSON");
        }

        if (!data.IsObject())
        {
            return false;
        }

        var type = data.Str("type") ?? sse.Event;
        var response = data.Obj("response") ?? (data.Str("object") == "response" ? data : (JsonElement?)null);
        if (type == "error" || (data.Prop("error") is not null && response is null) || type == "response.failed"
            || response.Str("status") == "failed")
        {
            _done = true;
            output.Add(new StreamError(OpenAiCommon.ToAgentError(null, response ?? data, text)));
            return true;
        }

        if (!_started && (response is not null || type.StartsWith("response.", StringComparison.Ordinal)))
        {
            _started = true;
            output.Add(new MessageStart(response.Str("model") ?? "<unknown>"));
        }

        _usage.Update(response.Obj("usage"));
        var outputIndex = data.Count("output_index") is { } oi ? (int?)Math.Min(oi, int.MaxValue) : null;

        if (type is "response.output_item.added" or "response.output_item.done")
        {
            if (outputIndex is null)
            {
                throw new InvalidResponse("missing output_index");
            }

            AcceptItem(data.Prop("item") ?? default, outputIndex.Value, type.EndsWith(".done", StringComparison.Ordinal), output);
        }
        else if (type is "response.function_call_arguments.delta" or "response.function_call_arguments.done")
        {
            // 先按 item_id 认:网关重用 output_index 时,只按下标找会认错人。
            var done = type.EndsWith(".done", StringComparison.Ordinal);
            var itemId = data.Str("item_id");
            Item? item = null;
            if (itemId is not null && _byId.TryGetValue(itemId, out var known))
            {
                _items.TryGetValue(known, out item);
            }
            else if (outputIndex is { } index)
            {
                _items.TryGetValue(index, out item);
            }

            if (item is not { Kind: "function_call" })
            {
                throw new InvalidResponse("arguments arrived before function call");
            }

            ArgumentsDelta(item, (done ? data.Str("arguments") : data.Str("delta")) ?? "", done, output);
        }
        else if (type is "response.output_text.delta" or "response.output_text.done"
            or "response.refusal.delta" or "response.refusal.done"
            or "response.reasoning_summary_text.delta" or "response.reasoning_summary_text.done"
            or "response.reasoning_text.delta" or "response.reasoning_text.done")
        {
            if (outputIndex is null)
            {
                throw new InvalidResponse("missing output_index");
            }

            var summary = type.Contains("reasoning_summary", StringComparison.Ordinal);
            // 摘要和正文是两组独立事件,不是一个的两种写法:只认摘要那组的话,推理正文会被整段静默丢掉。
            var reasoningText = type.StartsWith("response.reasoning_text", StringComparison.Ordinal);
            var isRefusal = type.Contains("refusal", StringComparison.Ordinal);
            if (isRefusal)
            {
                _refused = true;
            }

            var item = ItemAt(outputIndex.Value, summary || reasoningText ? "reasoning" : "message", data.Str("item_id"), null);
            if (reasoningText)
            {
                item.PreferContent = true;
            }

            var done = type.EndsWith(".done", StringComparison.Ordinal);
            var value = done ? (isRefusal ? data.Str("refusal") : data.Str("text")) : data.Str("delta");

            // .done 缺字段时跳过而不是报错:流式内容已经在手里了。.delta 缺 delta 则这个事件本身没有意义,按协议违规处理。
            if (value is null)
            {
                if (!done)
                {
                    throw new InvalidResponse("missing text delta");
                }
            }
            else
            {
                var partIndex = (int)(summary ? data.Count("summary_index") : data.Count("content_index")).GetValueOrDefault();
                Text(item, reasoningText ? Lane.Content : Lane.Main, partIndex, value, done, output);
            }
        }

        if (type is "response.completed" or "response.incomplete" || data.Str("object") == "response")
        {
            if (response is not { } terminal)
            {
                throw new InvalidResponse("missing terminal response");
            }

            var position = 0;
            foreach (var value in terminal.Arr("output"))
            {
                AcceptItem(value, position++, true, output);
            }

            var incomplete = type == "response.incomplete" || terminal.Str("status") == "incomplete";
            var reason = terminal.Obj("incomplete_details").Str("reason");
            if (data.Str("object") == "response" && terminal.Str("status") != "completed" && !incomplete)
            {
                throw new InvalidResponse("non-terminal JSON response");
            }

            var calls = _items.Values.Where(i => i.Kind == "function_call").OrderBy(i => i.Index).ToList();
            var stop = reason switch
            {
                "max_output_tokens" => StopReason.MaxTokens,
                "content_filter" => StopReason.Refusal,
                // max_messages / steered 等其它值:不报错走默认档,上游加新枚举值不该让整轮失败。
                _ => _refused ? StopReason.Refusal : calls.Count > 0 ? StopReason.ToolUse : StopReason.EndTurn,
            };
            if (stop is StopReason.ToolUse or StopReason.MaxTokens)
            {
                if (calls.Select(c => c.CallId).Distinct().Count() != calls.Count)
                {
                    throw new InvalidResponse("duplicate call_id");
                }

                foreach (var call in calls.Where(c => c.Started))
                {
                    output.Add(new ToolCallEnd(call.Index, call.CallId));
                }
            }

            _done = true;
            output.Add(new MessageEnd(stop, _usage.Snapshot()));
            return true;
        }

        return false;
    }

    public void End(List<LlmEvent> output)
    {
        if (_done)
        {
            return;
        }

        _done = true;
        output.Add(new StreamError(OpenAiCommon.Interrupted()));
    }
}
