using System.Buffers;
using System.Text.Json;

namespace Ncw.Agent.Plugins;

/// <summary>
/// 插件工具卡片的消毒器(2.x <c>sanitizeToolCard</c> 的数据面):卡片是<b>只走 UI 轨</b>的(模型永远看不到),但它由控制端的可信渲染器解释,所以内容是不可信输入的收口——
/// 只认白名单原语,未知块 / 越界字段被丢弃或钳制(不是报错),字符串限长,image 只认 <c>data:</c> / <c>ncw://</c>,link 只认 https,总体积有上限;
/// <c>frame</c> 型(插件自己的 HTML)3.0 不支持(没有 React 视图 / 卡片视图)。不合法的卡片整张丢弃,只保留文本。
/// </summary>
public static class PluginCards
{
    public const int MaxBytes = 64 * 1024;
    public const int MaxBlocks = 50;
    private const int MaxText = 4096;

    private static readonly string[] Tones = ["neutral", "info", "ok", "warn", "danger"];

    /// <summary>消毒。返回 null = 不合法(丢弃)。输出是重新写出来的 JSON(只含白名单字段)。</summary>
    public static JsonElement? Sanitize(JsonElement card)
    {
        if (card.ValueKind != JsonValueKind.Object || Str(card, "kind") != "declarative" || !card.TryGetProperty("blocks", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("kind", "declarative");
            w.WriteStartArray("blocks");
            var count = 0;
            foreach (var block in blocks.EnumerateArray())
            {
                if (count >= MaxBlocks)
                {
                    break;
                }

                if (WriteBlock(w, block))
                {
                    count++;
                }
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        if (buffer.WrittenCount > MaxBytes)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    private static bool WriteBlock(Utf8JsonWriter w, JsonElement b)
    {
        if (b.ValueKind != JsonValueKind.Object || Str(b, "type") is not { } type)
        {
            return false;
        }

        switch (type)
        {
            case "keyValue":
                Block(w, type, () =>
                {
                    w.WriteStartArray("rows");
                    foreach (var row in Array(b, "rows", 50))
                    {
                        w.WriteStartObject();
                        w.WriteString("label", Clip(Str(row, "label")));
                        w.WriteString("value", Clip(Str(row, "value")));
                        Tone(w, row);
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                });
                return true;
            case "table":
                Block(w, type, () =>
                {
                    w.WriteStartArray("columns");
                    foreach (var column in Array(b, "columns", 20))
                    {
                        w.WriteStringValue(Clip(column.ValueKind == JsonValueKind.String ? column.GetString() : null));
                    }

                    w.WriteEndArray();
                    w.WriteStartArray("rows");
                    foreach (var row in Array(b, "rows", 200))
                    {
                        w.WriteStartArray();
                        if (row.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var cell in row.EnumerateArray().Take(20))
                            {
                                w.WriteStringValue(Clip(cell.ValueKind == JsonValueKind.String ? cell.GetString() : cell.ToString()));
                            }
                        }

                        w.WriteEndArray();
                    }

                    w.WriteEndArray();
                });
                return true;
            case "status":
                Block(w, type, () =>
                {
                    w.WriteString("label", Clip(Str(b, "label")));
                    Tone(w, b);
                });
                return true;
            case "text" or "markdown":
                Block(w, type, () => w.WriteString("value", Clip(Str(b, "value"))));
                return true;
            case "code":
                Block(w, type, () =>
                {
                    w.WriteString("value", Clip(Str(b, "value")));
                    if (Str(b, "language") is { Length: > 0 and <= 32 } language)
                    {
                        w.WriteString("language", language);
                    }
                });
                return true;
            case "image":
                if (Str(b, "dataRef") is not { } dataRef || !(dataRef.StartsWith("data:", StringComparison.Ordinal) || dataRef.StartsWith("ncw://", StringComparison.Ordinal)))
                {
                    return false;
                }

                Block(w, type, () =>
                {
                    w.WriteString("dataRef", dataRef);
                    if (Str(b, "alt") is { } alt)
                    {
                        w.WriteString("alt", Clip(alt));
                    }
                });
                return true;
            case "progress":
                Block(w, type, () =>
                {
                    w.WriteNumber("fraction", b.TryGetProperty("fraction", out var f) && f.ValueKind == JsonValueKind.Number ? Math.Clamp(f.GetDouble(), 0, 1) : 0);
                    if (Str(b, "label") is { } label)
                    {
                        w.WriteString("label", Clip(label));
                    }
                });
                return true;
            case "link":
                if (Str(b, "href") is not { } href || !href.StartsWith("https://", StringComparison.Ordinal) || !Uri.TryCreate(href, UriKind.Absolute, out _))
                {
                    return false;
                }

                Block(w, type, () =>
                {
                    w.WriteString("href", href);
                    if (Str(b, "label") is { } label)
                    {
                        w.WriteString("label", Clip(label));
                    }
                });
                return true;
            case "button":
                if (Str(b, "actionId") is not { Length: > 0 and <= 128 } actionId)
                {
                    return false;
                }

                Block(w, type, () =>
                {
                    w.WriteString("actionId", actionId);
                    w.WriteString("label", Clip(Str(b, "label")));
                    Tone(w, b);
                    if (b.TryGetProperty("confirm", out var confirm) && Str(confirm, "titleKey") is { Length: > 0 and <= 200 } titleKey)
                    {
                        w.WriteStartObject("confirm");
                        w.WriteString("titleKey", titleKey);
                        w.WriteEndObject();
                    }
                });
                return true;
            case "list":
                Block(w, type, () =>
                {
                    w.WriteStartArray("items");
                    foreach (var item in Array(b, "items", 100))
                    {
                        w.WriteStartObject();
                        w.WriteString("label", Clip(Str(item, "label")));
                        Tone(w, item);
                        if (Str(item, "hint") is { } hint)
                        {
                            w.WriteString("hint", Clip(hint));
                        }

                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                });
                return true;
            case "metric":
                Block(w, type, () =>
                {
                    w.WriteString("label", Clip(Str(b, "label")));
                    w.WriteString("value", Clip(Str(b, "value")));
                    if (Str(b, "delta") is { } delta)
                    {
                        w.WriteString("delta", Clip(delta));
                    }

                    Tone(w, b);
                });
                return true;
            case "divider":
                Block(w, type, () => { });
                return true;
            default:
                return false;
        }
    }

    private static void Block(Utf8JsonWriter w, string type, Action body)
    {
        w.WriteStartObject();
        w.WriteString("type", type);
        body();
        w.WriteEndObject();
    }

    private static void Tone(Utf8JsonWriter w, JsonElement e)
    {
        if (Str(e, "tone") is { } tone && Tones.Contains(tone, StringComparer.Ordinal))
        {
            w.WriteString("tone", tone);
        }
    }

    private static IEnumerable<JsonElement> Array(JsonElement e, string name, int max) =>
        e.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().Take(max) : [];

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Clip(string? value) => value is null ? "" : value.Length > MaxText ? value[..MaxText] : value;
}
