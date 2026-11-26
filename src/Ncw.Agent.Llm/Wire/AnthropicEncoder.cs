using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Llm.Json;

namespace Ncw.Agent.Llm.Wire;

/// <summary>LlmRequest → Anthropic Messages API 请求体(2.x <c>encode/anthropic.ts</c>)。</summary>
public static class AnthropicEncoder
{
    public static EncodedRequest Encode(LlmRequest request, string upstreamModel, EncodeOptions options)
    {
        var body = new JsonObject
        {
            ["model"] = upstreamModel,
            ["max_tokens"] = request.Options.MaxOutputTokens,
            ["messages"] = ToMessages(request.Messages),
            ["stream"] = true,
        };
        if (request.System.Length > 0)
        {
            body["system"] = request.System;
        }

        if (request.Tools.Count > 0)
        {
            body["tools"] = ToTools(request.Tools);
        }

        if (request.Options.StopSequences is { Count: > 0 } stop)
        {
            body["stop_sequences"] = Jsn.ArrayOfStrings(stop);
        }

        // 开启 thinking 时 Anthropic 不接受 temperature;思考参数由 ThinkingAdapter 在编码之后写入并清掉它。
        if (request.Options.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        var headers = new Dictionary<string, string>
        {
            ["content-type"] = "application/json",
            ["anthropic-version"] = "2023-06-01",
        };
        return new EncodedRequest(WireProtocols.PathOf(WireProtocol.Anthropic), headers, AnthropicRequestPolicy.Apply(body, options.UserId, options.CacheTtl));
    }

    public static JsonArray ToTools(IReadOnlyList<ToolInfo> tools)
    {
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            // 下发的是 externalName,不是内部 id。
            array.Push(new JsonObject
            {
                ["name"] = tool.ExternalName,
                ["description"] = tool.Description,
                ["input_schema"] = Jsn.ToNode(tool.InputSchema),
            });
        }

        return array;
    }

    /// <summary>
    /// 两条规则都是踩出来的:空 content 的消息是 400,整条跳过;相邻同角色消息合并(Anthropic 要求严格交替,
    /// 并行工具或中断补偿会产生连续的 user 消息)。
    /// </summary>
    public static JsonArray ToMessages(IReadOnlyList<Message> messages)
    {
        var output = new JsonArray();
        string? lastRole = null;
        JsonArray? lastContent = null;
        foreach (var message in messages)
        {
            var blocks = new List<JsonNode>();
            foreach (var part in message.Parts)
            {
                var block = ToBlock(part);
                if (block is not null)
                {
                    blocks.Add(block);
                }
            }

            if (blocks.Count == 0)
            {
                continue;
            }

            var role = message.Role == Role.User ? "user" : "assistant";
            if (lastContent is not null && lastRole == role)
            {
                foreach (var block in blocks)
                {
                    lastContent.Push(block);
                }
            }
            else
            {
                lastContent = Jsn.ArrayOf(blocks);
                lastRole = role;
                output.Push(new JsonObject { ["role"] = role, ["content"] = lastContent });
            }
        }

        return output;
    }

    /// <summary>一个 Part → 一个 Anthropic 内容块;null = 这个块不上行(三种情况都是故意的)。</summary>
    private static JsonNode? ToBlock(Part part)
    {
        switch (part)
        {
            case TextPart text:
                // 空文本块是 400(text content blocks must be non-empty)。
                return text.Text.Length == 0 ? null : new JsonObject { ["type"] = "text", ["text"] = text.Text };

            case ThinkingPart thinking:
            {
                if (thinking.Opaque is not { Protocol: WireProtocols.AnthropicName } opaque)
                {
                    return null;
                }

                if (opaque.Data.Str("redacted") is { } redacted)
                {
                    return new JsonObject { ["type"] = "redacted_thinking", ["data"] = redacted };
                }

                // 没有签名的思考块不能回传(Anthropic 会拒):丢掉整块只是少一段推理上下文,带假签名是 400。
                return opaque.Data.Str("signature") is { } signature && thinking.Text.Length > 0
                    ? new JsonObject { ["type"] = "thinking", ["thinking"] = thinking.Text, ["signature"] = signature }
                    : null;
            }

            case ToolCallPart call:
                return new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = call.CallId,
                    ["name"] = call.Name,
                    ["input"] = Jsn.ToNode(call.Input),
                };

            case ToolResultPart result:
            {
                var images = new List<JsonNode>();
                var omitted = 0;
                foreach (var image in result.Output.Images ?? [])
                {
                    if (ImageBlock(image.DataRef) is { } block)
                    {
                        images.Add(block);
                    }
                    else
                    {
                        omitted++;
                    }
                }

                var text = AppendOmittedImages(result.Output.Content, omitted);
                JsonNode content;
                if (images.Count == 0)
                {
                    content = text;
                }
                else
                {
                    var array = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
                    foreach (var image in images)
                    {
                        array.Push(image);
                    }

                    content = array;
                }

                return new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = result.CallId,
                    ["content"] = content,
                    ["is_error"] = result.IsError,
                };
            }

            case ImagePart image:
                return ImageBlock(image.DataRef) ?? new JsonObject { ["type"] = "text", ["text"] = DataUrls.UnavailableImagePlaceholder };

            case FileRefPart file:
                // 不读文件、不传字节:只把路径当 markdown 链接告诉模型,它自己用工具去读。
                return new JsonObject { ["type"] = "text", ["text"] = FileRefs.Markdown(file) };

            default:
                // ErrorPart / GoalStatusPart / CompactBoundaryPart 只属于界面那一轨:回传给模型,它会开始为我们的 bug 道歉。
                return null;
        }
    }

    private static JsonNode? ImageBlock(string dataRef) =>
        DataUrls.TryParseImage(dataRef, out var mime, out var data)
            ? new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mime, ["data"] = data },
            }
            : null;

    internal static string AppendOmittedImages(string text, int omitted) =>
        omitted == 0
            ? text
            : text.Length == 0
                ? string.Join('\n', Enumerable.Repeat(DataUrls.UnavailableImagePlaceholder, omitted))
                : text + "\n" + string.Join('\n', Enumerable.Repeat(DataUrls.UnavailableImagePlaceholder, omitted));
}

internal static class FileRefs
{
    /// <summary>用标准 markdown 链接语法:模型对 <c>[text](target)</c> 训练得多。</summary>
    public static string Markdown(FileRefPart file) => $"[{file.Name}]({file.Path})";
}

/// <summary>
/// Anthropic 的身份与缓存策略(供应商拥有,用户补丁覆盖不了):<c>metadata.user_id</c> = 工作区 id;
/// 顶层 <c>cache_control</c>(自动缓存增长中的历史)+ 稳定前缀上的显式断点(system 优先,其次最后一个工具)。
/// 编码后和用户补丁之后各应用一次,保证两个位置同一个 TTL。
/// </summary>
public static class AnthropicRequestPolicy
{
    public static JsonObject Apply(JsonObject body, string userId, AnthropicCacheTtl ttl)
    {
        var next = Jsn.Clone(body);

        var metadata = next["metadata"] as JsonObject ?? new JsonObject();
        metadata["user_id"] = userId;
        next["metadata"] = metadata;

        // 清掉适配器带来的断点,再按供应商策略恢复。
        if (next["system"] is { } system)
        {
            next["system"] = WithoutBreakpoints(system);
        }

        if (next["tools"] is { } tools)
        {
            next["tools"] = WithoutBreakpoints(tools);
        }

        next["cache_control"] = CacheControl(ttl);

        if (next["system"] is JsonValue systemText && systemText.TryGetValue<string>(out var text) && text.Length > 0)
        {
            next["system"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text, ["cache_control"] = CacheControl(ttl) });
            return next;
        }

        if (next["system"] is JsonArray { Count: > 0 } systemBlocks)
        {
            for (var i = systemBlocks.Count - 1; i >= 0; i--)
            {
                if (systemBlocks[i] is JsonObject block)
                {
                    block["cache_control"] = CacheControl(ttl);
                    return next;
                }
            }
        }

        // 没有 system 时,最后一个工具是缓存顺序里最后一个稳定块。
        if (next["tools"] is JsonArray { Count: > 0 } toolArray)
        {
            for (var i = toolArray.Count - 1; i >= 0; i--)
            {
                if (toolArray[i] is JsonObject tool)
                {
                    tool["cache_control"] = CacheControl(ttl);
                    break;
                }
            }
        }

        return next;
    }

    private static JsonObject CacheControl(AnthropicCacheTtl ttl)
    {
        // 5 分钟是默认值:不写 ttl 也让旧的 Anthropic 兼容中转继续可用;1 小时必须显式。
        var control = new JsonObject { ["type"] = "ephemeral" };
        if (ttl == AnthropicCacheTtl.OneHour)
        {
            control["ttl"] = "1h";
        }

        return control;
    }

    private static JsonNode WithoutBreakpoints(JsonNode value)
    {
        if (value is not JsonArray array)
        {
            return value.DeepClone();
        }

        var copy = new JsonArray();
        foreach (var item in array)
        {
            if (item is JsonObject obj && obj.ContainsKey("cache_control"))
            {
                var clone = Jsn.Clone(obj);
                clone.Remove("cache_control");
                copy.Push(clone);
            }
            else
            {
                copy.Push(item?.DeepClone());
            }
        }

        return copy;
    }
}
