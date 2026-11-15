using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Llm.History;

/// <summary>历史要发给哪个目标:来源三元组 + 协议 + 是否支持图片。</summary>
public sealed record HistoryTarget(Provenance Target, WireProtocol Protocol, bool SupportsImages = true)
{
    public static HistoryTarget For(WireProtocol protocol, string provider, string model, bool supportsImages = true) =>
        new(new Provenance(protocol.NameOf(), provider, model), protocol, supportsImages);
}

/// <summary>
/// 跨模型切换(取自 pi,01-llm §4):每次编码请求前对历史做一次只读转换,不改存储。
/// </summary>
/// <remarks>
/// 规则:同一个 api+provider+model 的助手消息原样保留(含签名和加密推理);来源不同(含来源未知)丢弃思考块;
/// 工具调用 id 不合目标协议的格式时改写(工具结果同步改写,改写是 id 的纯函数,同一个 id 每轮得到同一个结果,不破坏缓存);
/// 有调用没结果补一个 isError 的合成结果;没有对应调用的结果丢弃;出错或中止的助手消息整条跳过;目标不支持图片时换占位文本。
/// 文本块没有签名字段,「文本去掉签名」在本模型里天然成立。
/// </remarks>
public static partial class HistoryTransform
{
    /// <summary>补出来的结果内容。给模型看的,不是界面文字。</summary>
    public const string SyntheticResultText = "No result was recorded for this tool call.";

    public const string ImagePlaceholder = "[image omitted: the current model does not accept image input]";

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex AnthropicToolId();

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,40}$")]
    private static partial Regex ChatToolId();

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex ResponsesToolId();

    public static bool IsValidToolCallId(WireProtocol protocol, string id) => protocol switch
    {
        WireProtocol.Anthropic => AnthropicToolId().IsMatch(id),
        WireProtocol.OpenAiChat => ChatToolId().IsMatch(id),
        WireProtocol.OpenAiResponses => ResponsesToolId().IsMatch(id),
        _ => true,
    };

    /// <summary>不合法的 id → 稳定的合法 id(<c>call_</c> + SHA-256 前 24 个十六进制字符,共 29 字符)。</summary>
    public static string NormalizeToolCallId(WireProtocol protocol, string id)
    {
        if (IsValidToolCallId(protocol, id))
        {
            return id;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        return "call_" + Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    public static bool IsSameSource(Provenance? source, Provenance target) =>
        source is not null
        && string.Equals(source.Api, target.Api, StringComparison.Ordinal)
        && string.Equals(source.Provider, target.Provider, StringComparison.Ordinal)
        && string.Equals(source.Model, target.Model, StringComparison.Ordinal);

    public static IReadOnlyList<Message> Prepare(IReadOnlyList<Message> messages, HistoryTarget target)
    {
        var output = new List<Message>(messages.Count);
        HashSet<string> currentCalls = [];
        HashSet<string> answered = [];
        var idMap = new Dictionary<string, string>();

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.Role == Role.Assistant)
            {
                // 出错或中止的助手消息回放时整条跳过;它的工具调用也随之作废,后面对应的结果成为孤儿。
                if (message.Stop == StopReason.Aborted || message.Parts.Any(p => p is ErrorPart))
                {
                    currentCalls = [];
                    answered = [];
                    continue;
                }

                var kept = TransformAssistant(message, target, idMap);
                currentCalls = kept.Parts.OfType<ToolCallPart>().Select(c => c.CallId).ToHashSet();
                answered = [];
                output.Add(kept);

                // 补合成结果:前瞻到下一条助手消息为止,哪些调用没有结果。紧跟在助手消息之后插入(tool_result 必须在紧随的 user 消息里)。
                var missing = new List<ToolResultPart>();
                if (currentCalls.Count > 0)
                {
                    var seen = new HashSet<string>();
                    for (var j = i + 1; j < messages.Count && messages[j].Role != Role.Assistant; j++)
                    {
                        foreach (var result in messages[j].Parts.OfType<ToolResultPart>())
                        {
                            seen.Add(NormalizeId(target, idMap, result.CallId));
                        }
                    }

                    foreach (var call in kept.Parts.OfType<ToolCallPart>())
                    {
                        if (!seen.Contains(call.CallId))
                        {
                            missing.Add(new ToolResultPart(call.CallId, new ToolOutput(SyntheticResultText), IsError: true));
                        }
                    }
                }

                if (missing.Count > 0)
                {
                    foreach (var result in missing)
                    {
                        answered.Add(result.CallId);
                    }

                    output.Add(new Message(
                        new MessageId($"{message.Id}:synthetic-results"),
                        Role.User,
                        missing.Cast<Part>().ToList(),
                        message.CreatedAt,
                        Internal: true));
                }

                continue;
            }

            // user 消息:没有对应调用(或已有结果)的 tool_result 丢弃;图片按目标能力处理。
            var parts = new List<Part>(message.Parts.Count);
            foreach (var part in message.Parts)
            {
                if (part is ToolResultPart result)
                {
                    var id = NormalizeId(target, idMap, result.CallId);
                    if (!currentCalls.Contains(id) || !answered.Add(id))
                    {
                        continue;
                    }

                    parts.Add(StripImages(result with { CallId = id }, target));
                }
                else
                {
                    parts.Add(StripImages(part, target));
                }
            }

            if (parts.Count > 0 || message.Parts.Count == 0)
            {
                output.Add(message with { Parts = parts });
            }
        }

        return output;
    }

    private static string NormalizeId(HistoryTarget target, Dictionary<string, string> idMap, string id)
    {
        if (!idMap.TryGetValue(id, out var normalized))
        {
            normalized = NormalizeToolCallId(target.Protocol, id);
            idMap[id] = normalized;
        }

        return normalized;
    }

    private static Message TransformAssistant(Message message, HistoryTarget target, Dictionary<string, string> idMap)
    {
        var same = IsSameSource(message.Source, target.Target);
        var parts = new List<Part>(message.Parts.Count);
        foreach (var part in message.Parts)
        {
            switch (part)
            {
                // 来源不同:丢弃思考块(包括里面的透传数据)。同源原样保留,包括签名和加密推理。
                case ThinkingPart when !same:
                    break;
                case ToolCallPart call:
                    parts.Add(call with { CallId = NormalizeId(target, idMap, call.CallId) });
                    break;
                default:
                    parts.Add(StripImages(part, target));
                    break;
            }
        }

        return message with { Parts = parts };
    }

    private static Part StripImages(Part part, HistoryTarget target)
    {
        if (target.SupportsImages)
        {
            return part;
        }

        switch (part)
        {
            case ImagePart:
                return new TextPart(ImagePlaceholder);
            case ToolResultPart { Output.Images.Count: > 0 } result:
                return result with
                {
                    Output = result.Output with
                    {
                        Images = null,
                        Content = result.Output.Content + "\n" + string.Join("\n", result.Output.Images!.Select(_ => ImagePlaceholder)),
                    },
                };
            default:
                return part;
        }
    }
}
