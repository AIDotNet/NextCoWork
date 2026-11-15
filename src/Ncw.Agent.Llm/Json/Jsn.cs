using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ncw.Agent.Llm.Json;

/// <summary>
/// JSON 小工具。请求体用 <see cref="JsonNode"/> 构造(不用反射序列化,AOT 安全),响应用 <see cref="JsonElement"/> 读。
/// 对畸形输入的态度统一:读不出就当没有,不抛(见 decode 层说明)。
/// </summary>
public static class Jsn
{
    public static readonly JsonElement EmptyObject = Parse("{}");

    public static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static bool TryParse(string json, out JsonElement element)
    {
        try
        {
            element = Parse(json);
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }

    public static JsonElement ToElement(JsonNode node) => Parse(node.ToJsonString());

    /// <summary>JsonElement → JsonNode;未定义(default)按 <c>{}</c> 处理。</summary>
    public static JsonNode ToNode(JsonElement element) =>
        element.ValueKind == JsonValueKind.Undefined ? new JsonObject() : JsonNode.Parse(element.GetRawText())!;

    public static JsonObject ObjectOf(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object ? (JsonObject)ToNode(element) : new JsonObject();

    // ── 读 ──

    public static JsonElement? Prop(this JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) ? value : null;

    public static JsonElement? Prop(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    public static JsonElement? Obj(this JsonElement? element, string name) =>
        element.Prop(name) is { ValueKind: JsonValueKind.Object } v ? v : null;

    public static JsonElement? Obj(this JsonElement element, string name) =>
        element.Prop(name) is { ValueKind: JsonValueKind.Object } v ? v : null;

    public static string? Str(this JsonElement? element, string name) =>
        element.Prop(name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    public static string? Str(this JsonElement element, string name) =>
        element.Prop(name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    /// <summary>有限数字(含小数)。</summary>
    public static double? Num(this JsonElement? element, string name) =>
        element.Prop(name) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    public static double? Num(this JsonElement element, string name) =>
        element.Prop(name) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    /// <summary>非负的安全整数(OpenAI 的计数字段)。</summary>
    public static long? Count(this JsonElement? element, string name) =>
        element.Prop(name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var n) && n >= 0 && n <= 9_007_199_254_740_991 ? n : null;

    public static long? Count(this JsonElement element, string name) =>
        element.Prop(name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var n) && n >= 0 && n <= 9_007_199_254_740_991 ? n : null;

    public static bool? Bool(this JsonElement? element, string name) =>
        element.Prop(name) is { ValueKind: JsonValueKind.True or JsonValueKind.False } v ? v.GetBoolean() : null;

    public static bool? Bool(this JsonElement element, string name) =>
        element.Prop(name) is { ValueKind: JsonValueKind.True or JsonValueKind.False } v ? v.GetBoolean() : null;

    public static IReadOnlyList<JsonElement> Arr(this JsonElement? element, string name) =>
        element.Prop(name) is { ValueKind: JsonValueKind.Array } v ? v.EnumerateArray().ToList() : [];

    public static IReadOnlyList<JsonElement> Arr(this JsonElement element, string name) =>
        element.Prop(name) is { ValueKind: JsonValueKind.Array } v ? v.EnumerateArray().ToList() : [];

    public static bool IsObject(this JsonElement element) => element.ValueKind == JsonValueKind.Object;

    // ── 写 ──

    public static JsonArray ArrayOf(IEnumerable<JsonNode?> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add(item);
        }

        return array;
    }

    /// <summary>
    /// 往数组里加一个节点。必须走这里而不是直接 <c>array.Add(new JsonObject…)</c>:后者绑定到泛型的 <c>Add&lt;T&gt;</c>,
    /// 带 RequiresUnreferencedCode,AOT 分析器会报。
    /// </summary>
    public static void Push(this JsonArray array, JsonNode? node) => array.Add(node);

    public static JsonArray ArrayOfStrings(IEnumerable<string> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add((JsonNode?)item);
        }

        return array;
    }

    public static string Serialize(JsonNode node) => node.ToJsonString();

    /// <summary>深拷贝(JsonNode 只能有一个父节点)。</summary>
    public static JsonObject Clone(JsonObject node) => (JsonObject)node.DeepClone();
}
