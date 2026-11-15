using System.Collections.Immutable;
using System.Globalization;

namespace Ncw.Client.Widgets;

public enum WidgetAppearance
{
    Dark,
    Light,
}

/// <summary>推给页面的主题:外观 + 一组 CSS 变量(名字是设计规范认得的那套 <c>--color-text-primary</c> 等)。</summary>
public sealed record WidgetTheme(WidgetAppearance Appearance, ImmutableSortedDictionary<string, string> Tokens);

/// <summary>
/// 应用主题 → 设计规范认得的 CSS 变量(2.x <c>renderer/theme/widget-tokens.ts</c> 的移植)。
/// 规范正文(<c>Ncw.Agent.Widgets</c> 的 <c>Guidelines</c>)里每处颜色都写成 <c>var(--color-text-secondary)</c> 这类变量,而界面自己的 token 叫 Arc 的
/// <c>TextSecondary</c>:这里把后者翻译成前者,模型照规范写出来的 widget 就跟着用户的主题走(深浅、强调色)。
/// <para>输入 <paramref name="source"/> 是 UI 层从 Arc token 解析出来的具体值(颜色是 <c>#rrggbb</c> / <c>#aarrggbb</c>,字体是 CSS font-family 串),键是 <see cref="Sources"/> 里的名字。
/// 本函数<b>不写死任何颜色</b>:源里缺哪项,那个变量就不输出,页面落回外壳样式表里的默认值——所以 Client 层没有色值。</para>
/// </summary>
public static class WidgetTokens
{
    /// <summary>需要 UI 层解析的 Arc token 名(<c>ArcDesignTokenKind</c> 的成员名)。</summary>
    public static IReadOnlyList<string> Sources { get; } =
    [
        "Background", "Surface", "SurfaceRaised", "SurfaceMuted",
        "Foreground", "TextSecondary", "TextMuted",
        "Accent", "AccentForeground", "AccentSubtle",
        "Success", "Warning", "Danger", "SuccessSubtle", "WarningSubtle", "DangerSubtle",
        "BodyFontFamily", "MonoFontFamily",
    ];

    /// <summary>规范里的圆角(Claude 量出来的数,Arc 没有对应的档位,原样取用;不是颜色)。</summary>
    private static readonly (string Name, string Value)[] Metrics =
    [
        ("--border-radius-xs", "4px"), ("--border-radius-sm", "6px"), ("--border-radius-md", "8px"),
        ("--border-radius-lg", "12px"), ("--border-radius-xl", "16px"), ("--border-radius-full", "9999px"),
        ("--border-width-regular", "0.5px"), ("--font-weight-normal", "400"), ("--font-weight-medium", "500"),
    ];

    public static WidgetTheme Map(WidgetAppearance appearance, IReadOnlyDictionary<string, string> source)
    {
        var tokens = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        void Put(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                tokens[name] = value;
            }
        }

        string? Get(string key) => source.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        var fg = Get("Foreground");
        var secondary = Get("TextSecondary");
        var muted = Get("TextMuted");
        var accent = Get("Accent");
        var danger = Get("Danger");
        var success = Get("Success");
        var warning = Get("Warning");

        // ── 文字 ──
        Put("--color-text-primary", fg);
        Put("--color-text-secondary", secondary);
        Put("--color-text-tertiary", muted);
        Put("--color-text-inverse", Get("Background"));
        Put("--color-text-info", accent);          // info 族取 accent:链接 / 强调的颜色跟着换色走(2.x 同)
        Put("--color-text-danger", danger);
        Put("--color-text-success", success);
        Put("--color-text-warning", warning);
        Put("--color-text-disabled", muted);

        // ── 背景:primary = 卡片面,secondary = 面板,tertiary = 最底层 ──
        Put("--color-background-primary", Get("SurfaceRaised"));
        Put("--color-background-secondary", Get("Surface"));
        Put("--color-background-tertiary", Get("Background"));
        Put("--color-background-inverse", fg);
        Put("--color-background-ghost", "transparent");
        Put("--color-background-info", Get("AccentSubtle"));
        Put("--color-background-danger", Get("DangerSubtle"));
        Put("--color-background-success", Get("SuccessSubtle"));
        Put("--color-background-warning", Get("WarningSubtle"));

        // ── 描边:规范里 border-primary / secondary / tertiary 是文字色的 40% / 30% / 15%(同 2.x:换主题时边框跟着文字一起变) ──
        Put("--color-border-primary", Alpha(fg, 0.40));
        Put("--color-border-secondary", Alpha(fg, 0.30));
        Put("--color-border-tertiary", Alpha(fg, 0.15));
        Put("--color-border-info", accent);
        Put("--color-border-danger", danger);
        Put("--color-border-success", success);
        Put("--color-border-warning", warning);

        // ── 基础样式表里 svg .box / .t / .ts / .arr / .leader 用的五个短名字 ──
        Put("--p", fg);
        Put("--s", secondary);
        Put("--t", muted);
        Put("--bg2", Get("Surface"));
        Put("--b", Alpha(fg, 0.30));

        Put("--font-sans", Get("BodyFontFamily"));
        Put("--font-mono", Get("MonoFontFamily"));

        foreach (var (name, value) in Metrics)
        {
            tokens[name] = value;
        }

        return new WidgetTheme(appearance, tokens.ToImmutable());
    }

    /// <summary>UI 层把 Arc 的颜色(ARGB 四个字节)变成 CSS:不透明用 <c>#rrggbb</c>,带透明度用 <c>rgba()</c>。</summary>
    public static string CssColor(byte a, byte r, byte g, byte b) =>
        a == 255
            ? string.Create(CultureInfo.InvariantCulture, $"#{r:x2}{g:x2}{b:x2}")
            : string.Create(CultureInfo.InvariantCulture, $"rgba({r},{g},{b},{a / 255.0:0.###})");

    /// <summary>
    /// CSS 颜色(<c>#rrggbb</c> 或 <c>rgba(r,g,b,a)</c>)→ 乘上一个透明度的 <c>rgba()</c>(用算好的 rgba 而不是 <c>color-mix()</c>:各平台的 WebKit / WebView2 版本不一,rgba 到处都认)。
    /// 解析不了返回 null(那个变量不输出)。
    /// </summary>
    public static string? Alpha(string? color, double alpha)
    {
        if (color is null)
        {
            return null;
        }

        int r, g, b;
        var baseAlpha = 1.0;
        if (color is { Length: 7 } && color[0] == '#')
        {
            if (!int.TryParse(color.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
                || !int.TryParse(color.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
                || !int.TryParse(color.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
            {
                return null;
            }
        }
        else if (color.StartsWith("rgba(", StringComparison.Ordinal) && color.EndsWith(')'))
        {
            var parts = color[5..^1].Split(',');
            if (parts.Length != 4
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out r)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out g)
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out b)
                || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out baseAlpha))
            {
                return null;
            }
        }
        else
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture, $"rgba({r},{g},{b},{baseAlpha * alpha:0.###})");
    }
}
