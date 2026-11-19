using System.Collections.Concurrent;
using System.Globalization;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Platform;

namespace Ncw.UI.Controls;

/// <summary>
/// 供应商的 logo(docs/design/07 §3「添加供应商」):一块圆角底板 + 单色标志。标志是 <c>Assets/ProviderLogos/&lt;服务&gt;.svg</c>
/// (models.dev 的单色 logo,MIT,见同目录的许可证),颜色跟随 <see cref="Foreground"/>(Arc token),不带品牌色——深浅主题、强调色预设下都一致。
/// 认不出的服务(<see cref="Service"/> 为 null 或没有对应文件)画名字的首字母。
/// 只认 logo 里用到的那部分 SVG:<c>viewBox</c>、<c>path</c> 的 <c>d</c> / <c>fill</c> / <c>stroke</c> / <c>stroke-width</c> / <c>opacity</c> / <c>fill-rule</c> / <c>transform</c>;
/// 不引入 SVG 渲染库。解析结果按服务缓存,同一个 logo 只解析一次。
/// </summary>
public sealed class NcwProviderLogo : Control
{
    public static readonly StyledProperty<string?> ServiceProperty = AvaloniaProperty.Register<NcwProviderLogo, string?>(nameof(Service));

    /// <summary>没有 logo 时取首字母的名字。</summary>
    public static readonly StyledProperty<string?> FallbackTextProperty = AvaloniaProperty.Register<NcwProviderLogo, string?>(nameof(FallbackText));

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<NcwProviderLogo>();

    /// <summary>底板;null = 不画底板。</summary>
    public static readonly StyledProperty<IBrush?> BackgroundProperty = AvaloniaProperty.Register<NcwProviderLogo, IBrush?>(nameof(Background));

    public static readonly StyledProperty<IBrush?> BorderBrushProperty = AvaloniaProperty.Register<NcwProviderLogo, IBrush?>(nameof(BorderBrush));

    static NcwProviderLogo()
    {
        AffectsRender<NcwProviderLogo>(ServiceProperty, FallbackTextProperty, ForegroundProperty, BackgroundProperty, BorderBrushProperty);
    }

    public string? Service
    {
        get => GetValue(ServiceProperty);
        set => SetValue(ServiceProperty, value);
    }

    public string? FallbackText
    {
        get => GetValue(FallbackTextProperty);
        set => SetValue(FallbackTextProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? BorderBrush
    {
        get => GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    /// <summary>这个服务有没有内置 logo(测试、以及界面决定要不要露首字母)。</summary>
    public static bool HasLogo(string? service) => Load(service) is not null;

    protected override Size MeasureOverride(Size availableSize) => new(Width is >= 0 ? Width : 24, Height is >= 0 ? Height : 24);

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
        {
            return;
        }

        var tile = new Rect((Bounds.Width - size) / 2, (Bounds.Height - size) / 2, size, size);
        var radius = size * 0.24;
        if (Background is not null || BorderBrush is not null)
        {
            context.DrawRectangle(Background, BorderBrush is null ? null : new Pen(BorderBrush, 1), tile.Deflate(0.5), radius, radius);
        }

        var foreground = Foreground ?? Brushes.Gray;
        if (Load(Service) is { } logo)
        {
            // 标志占底板的 64%(有底板时)或 100%(没底板时),按 viewBox 等比放进去、居中
            var inner = Background is null && BorderBrush is null ? tile : tile.Deflate(size * 0.18);
            var scale = Math.Min(inner.Width / logo.ViewBox.Width, inner.Height / logo.ViewBox.Height);
            var dx = inner.X + ((inner.Width - (logo.ViewBox.Width * scale)) / 2) - (logo.ViewBox.X * scale);
            var dy = inner.Y + ((inner.Height - (logo.ViewBox.Height * scale)) / 2) - (logo.ViewBox.Y * scale);
            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy)))
            {
                foreach (var shape in logo.Shapes)
                {
                    using (context.PushOpacity(shape.Opacity))
                    {
                        context.DrawGeometry(shape.Fill ? foreground : null, shape.StrokeWidth is { } w ? new Pen(foreground, w) : null, shape.Geometry);
                    }
                }
            }

            return;
        }

        var initials = Initials(FallbackText);
        if (initials.Length == 0)
        {
            return;
        }

        var text = new FormattedText(initials, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, size * (initials.Length > 1 ? 0.38 : 0.46), foreground)
        {
            TextAlignment = TextAlignment.Left,
        };
        context.DrawText(text, new Point(tile.X + ((size - text.Width) / 2), tile.Y + ((size - text.Height) / 2)));
    }

    /// <summary>首字母:拉丁字母取前两个字母 / 数字(<c>OpenAI</c> → <c>Op</c>),其余取第一个字(<c>百川</c> → <c>百</c>)。</summary>
    internal static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var trimmed = name.Trim();
        if (char.IsAsciiLetterOrDigit(trimmed[0]))
        {
            var letters = new string([.. trimmed.Where(char.IsAsciiLetterOrDigit).Take(2)]);
            return letters.Length > 1 ? $"{char.ToUpperInvariant(letters[0])}{char.ToLowerInvariant(letters[1])}" : letters.ToUpperInvariant();
        }

        return char.IsSurrogate(trimmed[0]) && trimmed.Length > 1 ? trimmed[..2] : trimmed[..1];
    }

    // ───────────────────────── 解析 logo ─────────────────────────

    internal sealed record LogoShape(Geometry Geometry, bool Fill, double? StrokeWidth, double Opacity);

    internal sealed record Logo(Rect ViewBox, IReadOnlyList<LogoShape> Shapes);

    private static readonly ConcurrentDictionary<string, Logo?> Cache = new(StringComparer.Ordinal);

    internal static Logo? Load(string? service)
    {
        if (string.IsNullOrEmpty(service) || !IsSafeName(service))
        {
            return null;
        }

        return Cache.GetOrAdd(service, static id =>
        {
            var uri = new Uri($"avares://Ncw.UI/Assets/ProviderLogos/{id}.svg");
            if (!AssetLoader.Exists(uri))
            {
                return null;
            }

            using var stream = AssetLoader.Open(uri);
            return Parse(stream);
        });
    }

    private static bool IsSafeName(string id) => id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    /// <summary>解析失败(格式不对、没有可画的路径)返回 null,界面退回首字母。</summary>
    internal static Logo? Parse(Stream svg)
    {
        try
        {
            using var reader = XmlReader.Create(svg, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true });
            Rect? viewBox = null;
            var rootFill = true;
            var shapes = new List<LogoShape>();
            var skipDepth = -1;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (skipDepth >= 0 && reader.Depth > skipDepth)
                {
                    continue;
                }

                skipDepth = -1;
                switch (reader.LocalName)
                {
                    case "svg":
                        viewBox = ParseViewBox(reader.GetAttribute("viewBox"))
                            ?? new Rect(0, 0, Number(reader.GetAttribute("width")) ?? 24, Number(reader.GetAttribute("height")) ?? 24);
                        rootFill = reader.GetAttribute("fill") is not "none";
                        break;
                    case "defs" or "clipPath" or "mask":
                        // 裁剪 / 蒙版区域都是整张 viewBox(stepfun):不画、也不当成形状
                        skipDepth = reader.Depth;
                        break;
                    case "path" when reader.GetAttribute("d") is { Length: > 0 } d:
                        shapes.Add(Shape(reader, d, rootFill));
                        break;
                }
            }

            return viewBox is { Width: > 0, Height: > 0 } box && shapes.Count > 0 ? new Logo(box, shapes) : null;
        }
        catch (Exception e) when (e is XmlException or FormatException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static LogoShape Shape(XmlReader path, string d, bool rootFill)
    {
        // SVG 默认 nonzero;Avalonia 路径语法默认 evenodd,F1 = nonzero
        var evenOdd = path.GetAttribute("fill-rule") is "evenodd";
        var geometry = Geometry.Parse((evenOdd ? "F0 " : "F1 ") + d);
        if (ParseTransform(path.GetAttribute("transform")) is { } matrix)
        {
            geometry.Transform = new MatrixTransform(matrix);
        }

        var fill = path.GetAttribute("fill") switch
        {
            "none" => false,
            null => rootFill,
            _ => true,
        };
        var stroke = path.GetAttribute("stroke") is { } s && s != "none" ? Number(path.GetAttribute("stroke-width")) ?? 1 : (double?)null;
        return new LogoShape(geometry, fill, stroke, Math.Clamp(Number(path.GetAttribute("opacity")) ?? 1, 0, 1));
    }

    private static Rect? ParseViewBox(string? value)
    {
        var parts = Numbers(value);
        return parts.Length == 4 ? new Rect(parts[0], parts[1], parts[2], parts[3]) : null;
    }

    /// <summary>只认单个 <c>scale</c> / <c>translate</c> / <c>matrix</c>(logo 里实际出现的)。</summary>
    private static Matrix? ParseTransform(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var open = value.IndexOf('(', StringComparison.Ordinal);
        var close = value.IndexOf(')', StringComparison.Ordinal);
        if (open < 0 || close < open)
        {
            return null;
        }

        var args = Numbers(value[(open + 1)..close]);
        return (value[..open].Trim(), args.Length) switch
        {
            ("scale", 1) => Matrix.CreateScale(args[0], args[0]),
            ("scale", 2) => Matrix.CreateScale(args[0], args[1]),
            ("translate", 1) => Matrix.CreateTranslation(args[0], 0),
            ("translate", 2) => Matrix.CreateTranslation(args[0], args[1]),
            ("matrix", 6) => new Matrix(args[0], args[1], args[2], args[3], args[4], args[5]),
            _ => null,
        };
    }

    private static double[] Numbers(string? value) =>
        value is null
            ? []
            : [.. value.Split([' ', ',', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(v => Number(v) ?? double.NaN).Where(v => !double.IsNaN(v))];

    private static double? Number(string? value) =>
        value is not null && double.TryParse(value.TrimEnd('p', 'x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
}
