using System.Text.Json;

namespace Ncw.Agent.WebTools.Search;

/// <summary>
/// 从各家的响应里把 title / url / snippet 挖出来(2.x <c>search/harvest.ts</c>)。核对文档时反复出现同一句话:「字段名因 SDK / 版本略有不同」,
/// 而搜索服务的响应我们不控制,一次改名的症状是「搜索突然一条结果都没有了」且不报错(HTTP 是 200)。所以每个适配器给出<b>首选路径</b>(文档上写的那个),
/// 取不到时落到这里按一组候选名兜底。纯函数。
/// </summary>
public static class Harvest
{
    private static readonly string[] TitleKeys = ["title", "name", "heading"];
    private static readonly string[] UrlKeys = ["url", "link", "href", "source", "displayUrl", "display_url"];
    private static readonly string[] SnippetKeys = ["content", "snippet", "description", "text", "summary", "abstract", "excerpt", "body"];
    private static readonly string[] DateKeys = ["publishedAt", "published_date", "publishedDate", "date", "page_age", "time"];

    public sealed record Item(string Title, string Url, string Snippet, string? PublishedAt);

    /// <summary>第一个是非空字符串的键。空串当没有——<c>{title: ''}</c> 和缺字段是同一回事;顺手 trim。</summary>
    public static string? Pick(JsonElement element, IReadOnlyList<string> keys)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()?.Trim() is { Length: > 0 } text)
            {
                return text;
            }
        }

        return null;
    }

    /// <summary>按路径取数组。取不到、或取到的不是数组,一律返回空(调用方据此报「零结果」)。</summary>
    public static IReadOnlyList<JsonElement> ArrayAt(JsonElement root, params string[] path)
    {
        var cursor = root;
        foreach (var segment in path)
        {
            if (cursor.ValueKind != JsonValueKind.Object || !cursor.TryGetProperty(segment, out cursor))
            {
                return [];
            }
        }

        return cursor.ValueKind == JsonValueKind.Array ? [.. cursor.EnumerateArray()] : [];
    }

    /// <summary>
    /// 找出响应里<b>最像结果列表</b>的那个数组:元素是对象、且至少有一个能当 url 的字段。只在首选路径落空时才用(秘塔那种响应结构没有公开文档的家只能靠它)。
    /// 只往下找两层:再深就开始把「相关搜索」「广告位」这类数组也捞上来了,那些东西混进结果里比没有结果更糟——模型会认真引用它们。
    /// </summary>
    public static IReadOnlyList<JsonElement> FindResultArray(JsonElement root, int depth = 0)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            var items = root.EnumerateArray().ToList();
            return items.Any(e => Pick(e, UrlKeys) is not null) ? items : [];
        }

        if (root.ValueKind != JsonValueKind.Object || depth >= 2)
        {
            return [];
        }

        foreach (var property in root.EnumerateObject())
        {
            var found = FindResultArray(property.Value, depth + 1);
            if (found.Count > 0)
            {
                return found;
            }
        }

        return [];
    }

    /// <summary>一条原始结果 → 我们的形状。<b>没有 url 就丢掉</b>:一条模型无法核实、无法引用的搜索结果,除了占上下文和增加编造的素材之外没有别的作用。</summary>
    public static Item? One(JsonElement raw)
    {
        var url = Pick(raw, UrlKeys);
        return url is null ? null : new Item(Pick(raw, TitleKeys) ?? url, url, Pick(raw, SnippetKeys) ?? "", Pick(raw, DateKeys));
    }

    /// <summary>同一个 url 出现两次通常是「网页结果 + 精选摘要」重复,留第一条;保持原有顺序;全是垃圾时给空数组,不抛。</summary>
    public static IReadOnlyList<Item> All(IEnumerable<JsonElement> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<Item>();
        foreach (var row in rows)
        {
            if (One(row) is { } item && seen.Add(item.Url))
            {
                list.Add(item);
            }
        }

        return list;
    }
}
