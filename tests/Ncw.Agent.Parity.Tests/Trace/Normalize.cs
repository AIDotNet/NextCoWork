using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ncw.Agent.Parity.Tests.Trace;

/// <summary>消息 id 规范化:场景里显式给出的(历史、输入、插话)保持原样,其余按首次出现的顺序编号 m1、m2…(两边用同一份算法)。</summary>
public sealed class IdMap(IReadOnlySet<string> known)
{
    private readonly Dictionary<string, string> _seen = [];

    public string Map(string id)
    {
        if (known.Contains(id))
        {
            return id;
        }

        if (!_seen.TryGetValue(id, out var mapped))
        {
            mapped = $"m{_seen.Count + 1}";
            _seen[id] = mapped;
        }

        return mapped;
    }
}

/// <summary>
/// 两边共用的规范化(只做「去掉不确定的东西」,不放宽任何比较):去掉时间戳 / 耗时 / schemaVersion / tokens、去 null;
/// 错误块和错误对象只保留 <c>code</c> 与 <c>messageKey</c>(消息文字是英文诊断,不是契约);错误码用 2.x 的 snake_case 写法。
/// </summary>
public static partial class Normalize
{
    private static readonly HashSet<string> DropKeys = ["createdAt", "at", "durationMs", "startedAt", "schemaVersion", "tokens"];

    public static JsonNode? Scrub(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
            {
                var copy = new JsonObject();
                foreach (var (key, value) in o)
                {
                    if (DropKeys.Contains(key) || value is null)
                    {
                        continue;
                    }

                    copy[key] = Scrub(value);
                }

                return copy;
            }

            case JsonArray a:
                return new JsonArray([.. a.Select(Scrub)]);
            default:
                return node?.DeepClone();
        }
    }

    public static JsonObject Part(JsonNode part)
    {
        var scrubbed = Scrub(part)!.AsObject();
        if (scrubbed["type"]?.GetValue<string>() == "error")
        {
            return new JsonObject { ["type"] = "error", ["error"] = ErrorShape(scrubbed["error"]!.AsObject()) };
        }

        return scrubbed;
    }

    public static JsonObject ErrorShape(JsonObject error)
    {
        var shape = new JsonObject { ["code"] = error["code"]!.GetValue<string>() };
        if (error["messageKey"]?.GetValue<string>() is { } key)
        {
            shape["messageKey"] = key;
        }

        return shape;
    }

    public static string SnakeCase(string pascal) => PascalBoundary().Replace(pascal, "_$1").ToLowerInvariant();

    [GeneratedRegex("(?<=[a-z0-9])([A-Z])")]
    private static partial Regex PascalBoundary();

    public static JsonObject Message(string role, bool isInternal, string id, JsonArray parts, IdMap ids)
    {
        var message = new JsonObject { ["role"] = role };
        if (isInternal)
        {
            message["internal"] = true;
        }

        message["id"] = ids.Map(id);
        message["parts"] = new JsonArray([.. parts.Select(p => (JsonNode)Part(p!))]);
        return message;
    }

    public static JsonArray ParseArray(string json) => JsonNode.Parse(json)!.AsArray();

    public static JsonNode ToNode(JsonElement element) => JsonNode.Parse(element.GetRawText())!;
}
