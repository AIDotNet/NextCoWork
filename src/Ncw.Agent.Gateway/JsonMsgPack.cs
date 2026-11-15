using System.Buffers;
using System.Text.Json;
using Nerdbank.MessagePack;

namespace Ncw.Agent.Gateway;

/// <summary>
/// JSON 与线上 MessagePack 的互转(开放网关的 HTTP 映射):协议的载荷是字符串键的 MessagePack(<c>Ncw.Protocol</c> 的 DTO,键名 = 属性名),所以 JSON 的键名也是同一套
/// (<c>{"SessionId": "…"}</c>),网关不另造一套命名。无类型、零反射:不认识具体 DTO,只做结构转换,具体的校验和默认值仍由方法表里的类型形状在反序列化时做。
/// 限制:二进制字段(<c>byte[]</c>)在 JSON 里是 base64 字符串,<b>只在响应方向</b>能转;请求方向无法判断一个字符串该不该是二进制,所以带二进制参数的方法(附件分块、导入分块)不在网关里暴露。
/// </summary>
public static class JsonMsgPack
{
    public const int MaxDepth = 64;

    /// <summary>JSON → MessagePack。非法 JSON 抛 <see cref="JsonException"/>;嵌套过深抛 <see cref="JsonException"/>。</summary>
    public static byte[] FromJson(ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.Length == 0 ? "{}"u8.ToArray() : json.ToArray(), new JsonDocumentOptions { MaxDepth = MaxDepth });
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        Write(doc.RootElement, ref writer);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void Write(JsonElement element, ref MessagePackWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteMapHeader(element.EnumerateObject().Count());
                foreach (var property in element.EnumerateObject())
                {
                    writer.Write(property.Name);
                    Write(property.Value, ref writer);
                }

                break;
            case JsonValueKind.Array:
                writer.WriteArrayHeader(element.GetArrayLength());
                foreach (var item in element.EnumerateArray())
                {
                    Write(item, ref writer);
                }

                break;
            case JsonValueKind.String:
                writer.Write(element.GetString());
                break;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var l))
                {
                    writer.Write(l);
                }
                else if (element.TryGetUInt64(out var u))
                {
                    writer.Write(u);
                }
                else
                {
                    writer.Write(element.GetDouble());
                }

                break;
            case JsonValueKind.True:
                writer.Write(true);
                break;
            case JsonValueKind.False:
                writer.Write(false);
                break;
            default:
                writer.WriteNil();
                break;
        }
    }

    /// <summary>MessagePack → JSON(UTF-8)。映射的键必须是字符串(协议保证);二进制 → base64 字符串。</summary>
    public static byte[] ToJson(ReadOnlyMemory<byte> msgpack)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            var reader = new MessagePackReader(msgpack);
            Visit(ref reader, json, 0);
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void Visit(ref MessagePackReader reader, Utf8JsonWriter json, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException("The payload is nested too deeply.");
        }

        switch (reader.NextMessagePackType)
        {
            case MessagePackType.Nil:
                reader.ReadNil();
                json.WriteNullValue();
                break;
            case MessagePackType.Boolean:
                json.WriteBooleanValue(reader.ReadBoolean());
                break;
            case MessagePackType.Integer:
                // 无符号:正 fixint(0x00–0x7f)和 uint8..uint64(0xcc–0xcf);其余(负 fixint、int8..int64)是有符号。
                var code = reader.NextCode;
                if (code <= 0x7f || code is >= 0xcc and <= 0xcf)
                {
                    json.WriteNumberValue(reader.ReadUInt64());
                }
                else
                {
                    json.WriteNumberValue(reader.ReadInt64());
                }

                break;
            case MessagePackType.Float:
                json.WriteNumberValue(reader.ReadDouble());
                break;
            case MessagePackType.String:
                json.WriteStringValue(reader.ReadString());
                break;
            case MessagePackType.Binary:
                json.WriteBase64StringValue(reader.ReadBytes()?.ToArray() ?? []);
                break;
            case MessagePackType.Array:
                var length = reader.ReadArrayHeader();
                json.WriteStartArray();
                for (var i = 0; i < length; i++)
                {
                    Visit(ref reader, json, depth + 1);
                }

                json.WriteEndArray();
                break;
            case MessagePackType.Map:
                var count = reader.ReadMapHeader();
                json.WriteStartObject();
                for (var i = 0; i < count; i++)
                {
                    json.WritePropertyName(reader.ReadString() ?? "");
                    Visit(ref reader, json, depth + 1);
                }

                json.WriteEndObject();
                break;
            default:
                reader.Skip(new SerializationContext());
                json.WriteNullValue();
                break;
        }
    }
}
