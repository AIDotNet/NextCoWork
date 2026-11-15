using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Llm.Json;

namespace Ncw.Agent.Tools.Schema;

/// <summary>校验结果:通过时 <see cref="Value"/> 是校验并宽松转换之后的参数,失败时 <see cref="Error"/> 是给模型看的说明。</summary>
public readonly record struct SchemaResult(JsonElement Value, string? Error)
{
    public bool IsValid => Error is null;
}

/// <summary>
/// 小而 AOT 友好的 JSON schema 校验器(03 §1:工具的 <c>PrepareArguments</c> = 校验 + 宽松转换)。
/// 只认 <see cref="S"/> 产出的那一小撮关键字。模型生成的参数经常「差一点」,所以做宽松转换而不是整体拒绝:
/// <list type="bullet">
/// <item><c>"5"</c> → 5(integer / number)、<c>"true"</c> → true(boolean)、数字或布尔 → 字符串(string);</item>
/// <item>可选属性给 <c>null</c> 视为缺省(模型常把「不传」写成 null);</item>
/// <item>缺省的属性补 <c>default</c>;schema 里没有的属性原样保留(不报错,和 2.x 的 zod 默认行为一致)。</item>
/// </list>
/// 错误说明里带属性路径,例如 <c>edits.0.old_string: required property is missing</c>,模型要靠这句话决定怎么重发。
/// </summary>
public static class JsonSchemaValidator
{
    public static SchemaResult Validate(JsonElement schema, JsonElement input)
    {
        var errors = new List<string>();
        var node = Check(schema, input, "", errors);
        if (errors.Count > 0)
        {
            return new SchemaResult(default, string.Join("; ", errors));
        }

        return new SchemaResult(node is null ? Jsn.EmptyObject : Jsn.ToElement(node), null);
    }

    private static JsonNode? Check(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        var type = schema.Str("type");
        switch (type)
        {
            case "object":
                return CheckObject(schema, value, path, errors);
            case "array":
                return CheckArray(schema, value, path, errors);
            case "string":
                return CheckString(schema, value, path, errors);
            case "integer":
                return CheckInteger(schema, value, path, errors);
            case "number":
                return CheckNumber(schema, value, path, errors);
            case "boolean":
                return CheckBoolean(value, path, errors);
            default:
                return Raw(value);
        }
    }

    private static JsonNode? CheckObject(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        // 入参缺失(未定义)按空对象处理:全部属性都可选的工具(如 LS 的 ignore)可以不带参数
        if (value.ValueKind == JsonValueKind.Undefined || value.ValueKind == JsonValueKind.Null)
        {
            value = Jsn.EmptyObject;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add(Message(path, $"expected an object, got {Describe(value)}"));
            return null;
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in schema.Arr("required"))
        {
            if (name.ValueKind == JsonValueKind.String)
            {
                required.Add(name.GetString()!);
            }
        }

        var props = schema.Obj("properties");
        var result = new JsonObject();
        foreach (var property in value.EnumerateObject())
        {
            var child = Join(path, property.Name);
            if (props?.Prop(property.Name) is not { } propSchema)
            {
                result[property.Name] = Raw(property.Value);
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Null && !required.Contains(property.Name))
            {
                continue;
            }

            result[property.Name] = Check(propSchema, property.Value, child, errors);
        }

        if (props is { } all)
        {
            foreach (var property in all.EnumerateObject())
            {
                if (result.ContainsKey(property.Name))
                {
                    continue;
                }

                if (required.Contains(property.Name))
                {
                    errors.Add(Message(Join(path, property.Name), "required property is missing"));
                }
                else if (property.Value.Prop("default") is { } fallback)
                {
                    result[property.Name] = Raw(fallback);
                }
            }
        }

        return result;
    }

    private static JsonNode? CheckArray(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            errors.Add(Message(path, $"expected an array, got {Describe(value)}"));
            return null;
        }

        var items = schema.Obj("items");
        var count = value.GetArrayLength();
        if (schema.Num("minItems") is { } min && count < min)
        {
            errors.Add(Message(path, $"expected at least {min:0} item(s), got {count}"));
        }

        if (schema.Num("maxItems") is { } max && count > max)
        {
            errors.Add(Message(path, $"expected at most {max:0} item(s), got {count}"));
        }

        var result = new JsonArray();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            result.Push(items is { } itemSchema ? Check(itemSchema, item, Join(path, index.ToString(CultureInfo.InvariantCulture)), errors) : Raw(item));
            index++;
        }

        return result;
    }

    private static JsonNode? CheckString(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        string text;
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                text = value.GetString()!;
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                text = value.GetRawText();
                break;
            default:
                errors.Add(Message(path, $"expected a string, got {Describe(value)}"));
                return null;
        }

        if (schema.Num("minLength") is { } min && text.Length < min)
        {
            errors.Add(Message(path, min <= 1 ? "must not be empty" : $"must be at least {min:0} characters"));
        }

        if (schema.Num("maxLength") is { } max && text.Length > max)
        {
            errors.Add(Message(path, $"must be at most {max:0} characters (got {text.Length})"));
        }

        var allowed = schema.Arr("enum");
        if (allowed.Count > 0 && !allowed.Any(e => e.ValueKind == JsonValueKind.String && e.GetString() == text))
        {
            errors.Add(Message(path, $"must be one of {string.Join(", ", allowed.Select(e => e.ToString()))}"));
        }

        return JsonValue.Create(text);
    }

    private static JsonNode? CheckInteger(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        long number;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var exact))
        {
            number = exact;
        }
        else if (TryDouble(value, out var d) && d == Math.Floor(d) && Math.Abs(d) < 9_007_199_254_740_992d)
        {
            number = (long)d;
        }
        else
        {
            errors.Add(Message(path, $"expected an integer, got {Describe(value)}"));
            return null;
        }

        CheckRange(schema, number, path, errors);
        return JsonValue.Create(number);
    }

    private static JsonNode? CheckNumber(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (!TryDouble(value, out var number))
        {
            errors.Add(Message(path, $"expected a number, got {Describe(value)}"));
            return null;
        }

        CheckRange(schema, number, path, errors);
        return JsonValue.Create(number);
    }

    private static JsonNode? CheckBoolean(JsonElement value, string path, List<string> errors)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                return JsonValue.Create(true);
            case JsonValueKind.False:
                return JsonValue.Create(false);
            case JsonValueKind.String when bool.TryParse(value.GetString()!.Trim(), out var parsed):
                return JsonValue.Create(parsed);
            default:
                errors.Add(Message(path, $"expected a boolean, got {Describe(value)}"));
                return null;
        }
    }

    private static void CheckRange(JsonElement schema, double number, string path, List<string> errors)
    {
        if (schema.Num("minimum") is { } min && number < min)
        {
            errors.Add(Message(path, $"must be >= {min:0.##} (got {number:0.##})"));
        }

        if (schema.Num("maximum") is { } max && number > max)
        {
            errors.Add(Message(path, $"must be <= {max:0.##} (got {number:0.##})"));
        }
    }

    private static bool TryDouble(JsonElement value, out double number)
    {
        number = 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out number) && double.IsFinite(number),
            JsonValueKind.String => double.TryParse(value.GetString()!.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number),
            _ => false,
        };
    }

    private static JsonNode? Raw(JsonElement value) =>
        value.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(value.GetRawText());

    private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    private static string Message(string path, string text) => path.Length == 0 ? text : $"{path}: {text}";

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.Undefined => "nothing",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Array => "an array",
        _ => "an object",
    };
}
