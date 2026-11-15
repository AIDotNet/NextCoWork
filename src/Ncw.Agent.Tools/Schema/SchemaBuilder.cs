using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Llm.Json;

namespace Ncw.Agent.Tools.Schema;

/// <summary>
/// 手写 JSON schema 的小构造器(不用反射,NativeAOT 安全)。产出的 schema 只用校验器认识的那一小撮关键字:
/// <c>type / description / enum / minimum / maximum / minLength / maxLength / minItems / maxItems / items / properties / required / default</c>。
/// </summary>
public static class S
{
    public static JsonObject String(string description, int? minLength = null, int? maxLength = null, string? @default = null)
    {
        var o = new JsonObject { ["type"] = "string", ["description"] = description };
        if (minLength is { } min)
        {
            o["minLength"] = min;
        }

        if (maxLength is { } max)
        {
            o["maxLength"] = max;
        }

        if (@default is not null)
        {
            o["default"] = @default;
        }

        return o;
    }

    public static JsonObject Integer(string description, long? minimum = null, long? maximum = null)
    {
        var o = new JsonObject { ["type"] = "integer", ["description"] = description };
        if (minimum is { } min)
        {
            o["minimum"] = min;
        }

        if (maximum is { } max)
        {
            o["maximum"] = max;
        }

        return o;
    }

    public static JsonObject Boolean(string description) => new() { ["type"] = "boolean", ["description"] = description };

    public static JsonObject Enum(string description, params string[] values) =>
        new() { ["type"] = "string", ["description"] = description, ["enum"] = Jsn.ArrayOfStrings(values) };

    public static JsonObject Array(string description, JsonObject items, int? minItems = null, int? maxItems = null)
    {
        var o = new JsonObject { ["type"] = "array", ["description"] = description, ["items"] = items };
        if (minItems is { } min)
        {
            o["minItems"] = min;
        }

        if (maxItems is { } max)
        {
            o["maxItems"] = max;
        }

        return o;
    }

    /// <summary>对象 schema。<paramref name="required"/> 列出必填的属性名;其余属性可缺省(也接受 <c>null</c>,当作缺省)。</summary>
    public static JsonObject Object(string[] required, params (string Name, JsonObject Schema)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties)
        {
            props[name] = schema;
        }

        var o = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Length > 0)
        {
            o["required"] = Jsn.ArrayOfStrings(required);
        }

        return o;
    }

    public static JsonElement Build(JsonObject schema) => Jsn.ToElement(schema);
}
