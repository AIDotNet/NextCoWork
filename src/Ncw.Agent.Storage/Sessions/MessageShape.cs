using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Ncw.Agent.Storage.Sessions;

/// <summary>
/// 消息内容块(<c>parts</c> 的 JSON)里 Storage 需要读的那几样:是不是「纯工具回执」、可见文字、有没有某种块。
/// 其余一律不解释(内容块的类型化模型在 Llm 的 <c>Part</c>,转换属于会话层)。全部容错:坏 JSON、非数组当空。
/// </summary>
public static class MessageShape
{
    public const string EmptyParts = "[]";

    /// <summary>读出时的归一:是 JSON 数组就原样用,否则(坏 JSON、对象、标量)当空数组 —— 2.x 的 <c>getHistory</c> 同样的容错。</summary>
    public static string Normalize(string? partsJson)
    {
        if (string.IsNullOrEmpty(partsJson))
        {
            return EmptyParts;
        }

        try
        {
            using var document = JsonDocument.Parse(partsJson);
            return document.RootElement.ValueKind == JsonValueKind.Array ? partsJson : EmptyParts;
        }
        catch (JsonException)
        {
            return EmptyParts;
        }
    }

    /// <summary>写入时的校验:必须是 JSON 数组。</summary>
    public static bool IsPartsArray(string partsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(partsJson);
            return document.RootElement.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>2.x 的 <c>isToolResultOnly</c>:user 消息且非空、每个块都是 <c>tool_result</c>(工具回执,不是用户说的话)。</summary>
    public static bool IsToolResultOnly(string role, string partsJson)
    {
        if (role != "user")
        {
            return false;
        }

        var any = false;
        foreach (var type in PartTypes(partsJson))
        {
            if (type != "tool_result")
            {
                return false;
            }

            any = true;
        }

        return any;
    }

    /// <summary>2.x 的 <c>isTurnStart</c>:一条可见提问(一轮的开头)。</summary>
    public static bool IsTurnStart(MessageRecord message) => message.Role == "user" && !IsToolResultOnly(message.Role, message.PartsJson);

    public static bool HasPartType(string partsJson, string type) => PartTypes(partsJson).Any(t => t == type);

    /// <summary>所有 <c>text</c> 块的文字按原样连起来(2.x 的 <c>visibleText</c>)。</summary>
    public static string VisibleText(string partsJson)
    {
        var builder = new StringBuilder();
        foreach (var part in Parts(partsJson))
        {
            if (part.ValueKind == JsonValueKind.Object
                && part.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "text"
                && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                builder.Append(text.GetString());
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 编辑一条提问之后的 parts(2.x 的 <c>editedParts</c>):新文字 + 原来的非文字块。
    /// 附件与其它结构化块留着,只把可见文字换成一个规范的 text 块,旧文字的碎片不会残留。
    /// </summary>
    public static string EditedParts(string originalPartsJson, string text)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartArray();
            if (text != "")
            {
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", text);
                writer.WriteEndObject();
            }

            foreach (var part in Parts(originalPartsJson))
            {
                var isText = part.ValueKind == JsonValueKind.Object
                    && part.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "text";
                if (!isText)
                {
                    part.WriteTo(writer);
                }
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static IEnumerable<string?> PartTypes(string partsJson)
    {
        foreach (var part in Parts(partsJson))
        {
            yield return part.ValueKind == JsonValueKind.Object && part.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                ? type.GetString()
                : null;
        }
    }

    /// <summary>数组元素的克隆(脱离文档生命周期,调用方可以随便用);坏 JSON / 非数组得到空序列。</summary>
    private static List<JsonElement> Parts(string partsJson)
    {
        var list = new List<JsonElement>();
        try
        {
            using var document = JsonDocument.Parse(partsJson);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    list.Add(element.Clone());
                }
            }
        }
        catch (JsonException)
        {
        }

        return list;
    }
}
