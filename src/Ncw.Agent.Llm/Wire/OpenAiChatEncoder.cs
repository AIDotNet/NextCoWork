using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ncw.Agent.Llm.Json;

namespace Ncw.Agent.Llm.Wire;

/// <summary>LlmRequest → OpenAI Chat Completions 请求体(2.x <c>encode/openai-chat.ts</c>)。</summary>
public static partial class OpenAiChatEncoder
{
    /// <summary>o 系列和 gpt-5+ 要 max_completion_tokens;兼容供应商(DeepSeek、GLM 等)仍用 max_tokens,模型级补丁可以覆盖任何一个。</summary>
    [GeneratedRegex(@"(?:^|/)(?:o[1-9](?:$|[-.])|gpt-(?:[5-9]|\d{2,})(?:$|[-.]))", RegexOptions.IgnoreCase)]
    private static partial Regex ModernTokenParameter();

    public static EncodedRequest Encode(LlmRequest request, string upstreamModel)
    {
        var messages = ToMessages(request.Messages);
        if (request.System.Length > 0)
        {
            messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = request.System });
        }

        var body = new JsonObject
        {
            ["model"] = upstreamModel,
            ["messages"] = messages,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            [ModernTokenParameter().IsMatch(upstreamModel) ? "max_completion_tokens" : "max_tokens"] = request.Options.MaxOutputTokens,
        };
        if (request.Options.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        if (request.Options.StopSequences is { Count: > 0 } stop)
        {
            body["stop"] = Jsn.ArrayOfStrings(stop);
        }

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                tools.Push(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.ExternalName,
                        ["description"] = tool.Description,
                        ["parameters"] = Jsn.ToNode(tool.InputSchema),
                    },
                });
            }

            body["tools"] = tools;
            body["tool_choice"] = "auto";
        }

        var headers = new Dictionary<string, string> { ["content-type"] = "application/json" };
        return new EncodedRequest(WireProtocols.PathOf(WireProtocol.OpenAiChat), headers, body);
    }

    /// <summary>
    /// 工具结果单独成 <c>tool</c> 消息(必须紧跟助手的调用,中间不能插用户提醒);Chat 的 tool 消息不能带图片,
    /// 图片挪到随后的用户消息里,保证工具调用与回执的相邻关系。
    /// </summary>
    public static JsonArray ToMessages(IReadOnlyList<Message> messages)
    {
        var output = new JsonArray();
        foreach (var message in messages)
        {
            var content = new List<JsonNode>();
            var calls = new JsonArray();
            string? reasoning = null;
            foreach (var part in message.Parts)
            {
                switch (part)
                {
                    case ToolResultPart result:
                    {
                        var omitted = 0;
                        foreach (var image in result.Output.Images ?? [])
                        {
                            if (DataUrls.TryNormalizeImageUrl(image.DataRef, out var toolImageUrl))
                            {
                                content.Add(ImageContent(toolImageUrl));
                            }
                            else
                            {
                                omitted++;
                            }
                        }

                        output.Push(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = result.CallId,
                            ["content"] = AnthropicEncoder.AppendOmittedImages(result.Output.Content, omitted),
                        });
                        break;
                    }

                    case TextPart { Text.Length: > 0 } text:
                        content.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                        break;
                    case ImagePart image:
                        content.Add(DataUrls.TryNormalizeImageUrl(image.DataRef, out var imageUrl)
                            ? ImageContent(imageUrl)
                            : new JsonObject { ["type"] = "text", ["text"] = DataUrls.UnavailableImagePlaceholder });
                        break;
                    case FileRefPart file:
                        content.Add(new JsonObject { ["type"] = "text", ["text"] = FileRefs.Markdown(file) });
                        break;
                    case ToolCallPart call when message.Role == Role.Assistant:
                        calls.Push(new JsonObject
                        {
                            ["id"] = call.CallId,
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = Jsn.ToNode(call.Input).ToJsonString() },
                        });
                        break;
                    case ThinkingPart thinking when message.Role == Role.Assistant && IsChatReasoning(thinking):
                        reasoning = (reasoning ?? "") + thinking.Text;
                        break;
                }
            }

            if (content.Count == 0 && calls.Count == 0)
            {
                continue;
            }

            var entry = new JsonObject { ["role"] = message.Role == Role.User ? "user" : "assistant" };
            if (content.Count == 0)
            {
                entry["content"] = null;
            }
            else if (content.All(c => c["type"]!.GetValue<string>() == "text"))
            {
                entry["content"] = string.Concat(content.Select(c => c["text"]!.GetValue<string>()));
            }
            else
            {
                entry["content"] = Jsn.ArrayOf(content);
            }

            if (calls.Count > 0)
            {
                entry["tool_calls"] = calls;
            }

            if (reasoning is not null)
            {
                entry["reasoning_content"] = reasoning;
            }

            output.Push(entry);
        }

        return output;
    }

    private static JsonObject ImageContent(string dataRef) =>
        new() { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = dataRef } };

    /// <summary>只回放本协议的推理,绝不回放 Anthropic 的签名或 Responses 的条目。</summary>
    private static bool IsChatReasoning(ThinkingPart part) =>
        part.Opaque is { Protocol: WireProtocols.OpenAiChatName } opaque && opaque.Data.Str("field") == "reasoning_content";
}
