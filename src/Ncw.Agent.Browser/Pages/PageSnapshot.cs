using System.Text.Json;

namespace Ncw.Agent.Browser.Pages;

public readonly record struct Viewport(int Width, int Height);

/// <summary>一次观察:页面侧程序返回的可访问性树,已校验、截断、规范化。</summary>
public sealed record PageSnapshot(string Tree, string Title, string Url, string SnapshotId, Viewport Viewport, bool Truncated)
{
    public const int MaxChars = 80_000;

    /// <summary>
    /// 页面返回的值要当成不可信输入(2.x <c>normalizeBrowserSnapshot</c>):缺字段 / 非法 viewport 抛错,树限制在固定预算,尾部空白去掉。
    /// 隔离世界里跑的是我们的固定程序,但 <c>Runtime.evaluate</c> 的结果仍越过了一道被页面控制的渲染进程边界。
    /// </summary>
    public static PageSnapshot Normalize(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object)
        {
            throw new BrowserException(BrowserErrorKeys.StaleObservation, "The browser returned an invalid accessibility snapshot.");
        }

        string? Str(string name) => raw.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var tree = Str("tree");
        var title = Str("title");
        var url = Str("url");
        var id = Str("snapshotId");
        if (tree is null || title is null || url is null || string.IsNullOrEmpty(id))
        {
            throw new BrowserException(BrowserErrorKeys.StaleObservation, "The browser returned an incomplete accessibility snapshot.");
        }

        var viewport = ViewportOf(raw.TryGetProperty("viewport", out var vp) ? vp : default);
        var lines = tree.Split('\n').Select(static l => l.TrimEnd('\t', '\r', ' '));
        var normalized = string.Join('\n', lines).Trim();
        var truncated = normalized.Length > MaxChars;
        return new PageSnapshot(
            truncated ? Ncw.Agent.Browser.Pages.PageText.Head(normalized, MaxChars) : normalized,
            PageText.Head(title, 500),
            PageText.Head(url, 8192),
            id,
            viewport,
            truncated);
    }

    public static Viewport ViewportOf(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number && w.TryGetDouble(out var width)
            && value.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number && h.TryGetDouble(out var height)
            && double.IsFinite(width) && double.IsFinite(height) && width >= 0 && height >= 0)
        {
            return new Viewport((int)Math.Round(width), (int)Math.Round(height));
        }

        throw new BrowserException(BrowserErrorKeys.StaleObservation, "The browser returned an invalid viewport.");
    }

    public string Format()
    {
        var body = Tree.Length == 0 ? "(The page has no accessible content.)" : Tree;
        var parts = new List<string>
        {
            $"Page: {(Title.Length == 0 ? "(untitled)" : Title)}",
            $"URL: {Url}",
            $"Viewport: {Viewport.Width}x{Viewport.Height}",
            "",
            body,
        };
        if (Truncated)
        {
            parts.Add("");
            parts.Add($"[Snapshot truncated at {MaxChars} characters.]");
        }

        return string.Join('\n', parts);
    }
}

internal static class PageText
{
    /// <summary>保留开头 <paramref name="max"/> 个字符,不切开代理对。</summary>
    public static string Head(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var keep = max;
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        return text[..keep];
    }
}
