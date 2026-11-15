using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Llm.Json;

namespace Ncw.Agent.Llm.Wire;

/// <summary>LlmRequest → OpenAI Responses 请求体(2.x <c>encode/openai-responses.ts</c>)。<c>store:false</c>,<c>include</c> 加密推理(无状态续轮,含 ZDR 账户)。</summary>
public static class OpenAiResponsesEncoder
{
    /// <summary>
    /// 回传 reasoning item 时只带输入侧认得的字段(白名单,不是把 status 单独删掉)。透传里存的是上游原样的 output item,
    /// 带着 <c>status</c> 这类只属于输出侧的字段:官方宽容,第三方网关会 400(<c>Unknown parameter: 'input[246].status'</c>),
    /// 而且整段历史都带着它,会话之后每一轮都发不出去。<c>encrypted_content</c> 少传一个字节无状态续轮就丢掉整条推理链,所以不能漏。
    /// </summary>
    private static readonly string[] ReasoningInputFields = ["type", "id", "summary", "content", "encrypted_content"];

    public static EncodedRequest Encode(LlmRequest request, string upstreamModel, EncodeOptions options)
    {
        var body = new JsonObject
        {
            ["model"] = upstreamModel,
            ["input"] = ToInput(request.Messages, options.ReasoningReplay),
        };
        if (request.System.Length > 0)
        {
            body["instructions"] = request.System;
        }

        body["max_output_tokens"] = request.Options.MaxOutputTokens;
        body["stream"] = true;
        body["store"] = false;
        body["include"] = new JsonArray((JsonNode?)"reasoning.encrypted_content");
        if (request.Options.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                tools.Push(new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = tool.ExternalName,
                    ["description"] = tool.Description,
                    ["parameters"] = Jsn.ToNode(tool.InputSchema),
                    ["strict"] = false,
                });
            }

            body["tools"] = tools;
            body["tool_choice"] = "auto";
        }

        var headers = new Dictionary<string, string> { ["content-type"] = "application/json" };
        return new EncodedRequest(WireProtocols.PathOf(WireProtocol.OpenAiResponses), headers, body);
    }

    /// <summary>
    /// 转录 → Responses 的 <c>input</c> 数组。<paramref name="replay"/> 是回传方言(官方剥掉推理正文 / DeepSeek 要求正文全文回传)。
    /// 出事的 item 会留在转录里被每一轮重放,所以清洁只能放在编码期、不能只在解码期:存量转录下一轮自动不再触发,不需要数据迁移。
    /// </summary>
    public static JsonArray ToInput(IReadOnlyList<Message> messages, ReasoningReplay replay = ReasoningReplay.OpaqueOnly)
    {
        var input = new JsonArray();
        foreach (var message in messages)
        {
            var content = new List<JsonObject>();
            var isUser = message.Role == Role.User;

            void Flush()
            {
                if (content.Count == 0)
                {
                    return;
                }

                JsonNode value = isUser
                    ? Jsn.ArrayOf(content)
                    : string.Concat(content.Select(c => c["text"]?.GetValue<string>() ?? ""));
                input.Push(new JsonObject { ["role"] = isUser ? "user" : "assistant", ["content"] = value });
                content = [];
            }

            foreach (var part in message.Parts)
            {
                switch (part)
                {
                    case TextPart { Text.Length: > 0 } text:
                        content.Add(new JsonObject { ["type"] = isUser ? "input_text" : "output_text", ["text"] = text.Text });
                        break;
                    case ImagePart image:
                        content.Add(InputImage(image.DataRef));
                        break;
                    case FileRefPart file:
                        content.Add(new JsonObject { ["type"] = isUser ? "input_text" : "output_text", ["text"] = FileRefs.Markdown(file) });
                        break;
                    case ThinkingPart thinking:
                        ReplayThinking(thinking, replay, input, Flush);
                        break;
                    case ToolCallPart call:
                        Flush();
                        input.Push(new JsonObject
                        {
                            ["type"] = "function_call",
                            ["call_id"] = call.CallId,
                            ["name"] = call.Name,
                            ["arguments"] = Jsn.ToNode(call.Input).ToJsonString(),
                        });
                        break;
                    case ToolResultPart result:
                    {
                        Flush();
                        var images = result.Output.Images ?? [];
                        JsonNode output;
                        if (images.Count == 0)
                        {
                            output = result.Output.Content;
                        }
                        else
                        {
                            var array = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = result.Output.Content });
                            foreach (var image in images)
                            {
                                array.Push(InputImage(image.DataRef));
                            }

                            output = array;
                        }

                        input.Push(new JsonObject { ["type"] = "function_call_output", ["call_id"] = result.CallId, ["output"] = output });
                        break;
                    }

                    // ErrorPart / GoalStatusPart / CompactBoundaryPart / 子代理只属于界面那一轨:一个字节都不上行。
                }
            }

            Flush();
        }

        return input;
    }

    private static JsonObject InputImage(string dataRef) =>
        new() { ["type"] = "input_image", ["image_url"] = dataRef, ["detail"] = "auto" };

    /// <summary>
    /// 历史思考怎么回传,按上游方言分流。<c>text-required</c>(DeepSeek 思考模式)带 tools 的请求里历史每轮 reasoning_text 必须完整回传,缺失即 400;
    /// 透传只是载体,不是思考正文的唯一来源(流被打断、混进别的协议的思考轮次、字段白名单过滤后没剩正文时,全文仍在 <c>thinking.Text</c> 里)。
    /// <c>opaque-only</c>(官方 OpenAI)反过来更硬:输入侧 reasoning item 的 content 上限是 0,带非空正文整轮 400,所以剥掉 content,
    /// 也不再用正文合成条目。
    /// </summary>
    private static void ReplayThinking(ThinkingPart part, ReasoningReplay replay, JsonArray input, Action flush)
    {
        JsonObject? carrier = null;
        if (part.Opaque is { Protocol: WireProtocols.OpenAiResponsesName } opaque
            && opaque.Data.Obj("item") is { } item
            && item.Str("type") == "reasoning")
        {
            carrier = ReasoningInputItem(item, replay);
        }

        if (replay == ReasoningReplay.TextRequired)
        {
            if (carrier is not null)
            {
                flush();
                if (!CarriesText(carrier) && part.Text.Length > 0)
                {
                    carrier["content"] = new JsonArray(new JsonObject { ["type"] = "reasoning_text", ["text"] = part.Text });
                }

                input.Push(carrier);
            }
            else if (part.Text.Length > 0)
            {
                // 合成的只带 content:不编造 id、不搬 encrypted_content(那是上游签发给它自己的密文,换个服务商毫无意义)。
                flush();
                input.Push(new JsonObject
                {
                    ["type"] = "reasoning",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "reasoning_text", ["text"] = part.Text }),
                });
            }
        }
        else if (carrier is not null && HasReplayableId(carrier))
        {
            flush();
            input.Push(carrier);
        }
    }

    private static JsonObject ReasoningInputItem(JsonElement item, ReasoningReplay replay)
    {
        var output = new JsonObject();
        foreach (var key in ReasoningInputFields)
        {
            if (key == "id" && !HasReplayableId(item))
            {
                continue;
            }

            if (key == "content" && replay != ReasoningReplay.TextRequired)
            {
                continue;
            }

            if (item.Prop(key) is { ValueKind: not JsonValueKind.Undefined } value)
            {
                output[key] = Jsn.ToNode(value);
            }
        }

        return output;
    }

    /// <summary>
    /// 推理 id 必须是非空且不超过 64 字符的字符串。部分上游返回过 428 字符的 id,原样回传会让后续每轮 400。
    /// 不截断也不哈希上游签发的标识(避免伪造引用):opaque-only 跳过无法回传的条目;text-required 允许无 id 的正文载体,只去掉无效 id。
    /// </summary>
    private static bool HasReplayableId(JsonElement item) => item.Str("id") is { Length: > 0 and <= 64 };

    private static bool HasReplayableId(JsonObject item) => item["id"] is JsonValue v && v.TryGetValue<string>(out var id) && id.Length is > 0 and <= 64;

    /// <summary>text-required 的回传物里还有没有上游读得动的推理正文;没有就必须用转录里的 <c>thinking.Text</c> 补 content。</summary>
    private static bool CarriesText(JsonObject item)
    {
        if (item["encrypted_content"] is JsonValue enc && enc.TryGetValue<string>(out var s) && s.Length > 0)
        {
            return true;
        }

        foreach (var key in new[] { "content", "summary" })
        {
            if (item[key] is not JsonArray parts)
            {
                continue;
            }

            foreach (var p in parts)
            {
                if (p is JsonObject o && o["text"] is JsonValue t && t.TryGetValue<string>(out var text) && text.Length > 0)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
