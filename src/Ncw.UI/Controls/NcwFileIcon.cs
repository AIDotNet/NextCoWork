using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Ncw.UI.Controls;

/// <summary>只用于图标展示的分类,不改变设备文件类型或 Core 的文件分类。</summary>
public enum NcwFileIconKind
{
    File, CSharp, Project, Solution, Build, Avalonia, Xaml, Json, Markdown, Yaml, Toml, Xml,
    TypeScript, JavaScript, Python, Go, Rust, Html, Css, Shell, PowerShell, Sql, Image, Archive,
    License, Docker, Git, Config, Environment, Folder, SourceFolder, TestsFolder, DocsFolder, AssetsFolder, GitFolder,
}

/// <summary>原创的几何轮廓;同色图标仍可以靠轮廓和标记区分。</summary>
public enum NcwFileIconShape
{
    Document, Code, Data, Markup, Style, Image, Archive, Terminal, Database, License, Container, Folder,
}

/// <summary>名称 / 扩展名解析结果。Mark 是语言或格式的图形标记,不是界面文案。</summary>
public sealed record NcwFileIconDescriptor(NcwFileIconKind Kind, NcwFileIconShape Shape, ArcDesignTokenKind Token, string Mark);

/// <summary>
/// 16px 文件图标:文件名优先,再按最长扩展名匹配。全部轮廓为原创几何,不依赖第三方 SVG;
/// 颜色动态绑定 Arc token,Git 状态由行上的独立标记表示,不会改这里的文件类型颜色。
/// </summary>
public sealed class NcwFileIcon : Control
{
    public static readonly StyledProperty<string> PathProperty = AvaloniaProperty.Register<NcwFileIcon, string>(nameof(Path), "");
    public static readonly StyledProperty<bool> IsDirectoryProperty = AvaloniaProperty.Register<NcwFileIcon, bool>(nameof(IsDirectory));
    public static readonly StyledProperty<bool> IsExpandedProperty = AvaloniaProperty.Register<NcwFileIcon, bool>(nameof(IsExpanded));

    private static readonly StyledProperty<IBrush?> AccentBrushProperty = AvaloniaProperty.Register<NcwFileIcon, IBrush?>("AccentBrush");
    private static readonly StyledProperty<IBrush?> SuccessBrushProperty = AvaloniaProperty.Register<NcwFileIcon, IBrush?>("SuccessBrush");
    private static readonly StyledProperty<IBrush?> WarningBrushProperty = AvaloniaProperty.Register<NcwFileIcon, IBrush?>("WarningBrush");
    private static readonly StyledProperty<IBrush?> Series1BrushProperty = AvaloniaProperty.Register<NcwFileIcon, IBrush?>("Series1Brush");
    private static readonly StyledProperty<IBrush?> Series2BrushProperty = AvaloniaProperty.Register<NcwFileIcon, IBrush?>("Series2Brush");
    private static readonly StyledProperty<IBrush?> Series3BrushProperty = AvaloniaProperty.Register<NcwFileIcon, IBrush?>("Series3Brush");
    private static readonly StyledProperty<IBrush?> Series4BrushProperty = AvaloniaProperty.Register<NcwFileIcon, IBrush?>("Series4Brush");
    private static readonly StyledProperty<IBrush?> ForegroundBrushProperty = AvaloniaProperty.Register<NcwFileIcon, IBrush?>("ForegroundBrush");
    private static readonly StyledProperty<IBrush?> SecondaryBrushProperty = AvaloniaProperty.Register<NcwFileIcon, IBrush?>("SecondaryBrush");

    // Resolve 也供无图形平台的纯测试使用;几何只在真正绘制时初始化。
    private static class Shapes
    {
        public static readonly Geometry Page = Geometry.Parse("M 3,1.5 L 9,1.5 L 13,5.5 L 13,14.5 L 3,14.5 Z M 9,1.5 L 9,5.5 L 13,5.5");
        public static readonly Geometry ClosedFolder = Geometry.Parse("M 1,4 L 1,13.5 L 15,13.5 L 15,5 L 7.5,5 L 6,2.5 L 1,2.5 Z");
        public static readonly Geometry OpenFolder = Geometry.Parse("M 1,6 L 1,3 L 6,3 L 7.5,5 L 14,5 L 14,6 M 1,6 L 3,13.5 L 14,13.5 L 15.5,6 Z");
        public static readonly Geometry ImageShape = Geometry.Parse("M 1.5,3 L 14.5,3 L 14.5,13 L 1.5,13 Z M 2,12 L 6,7.5 L 9,10 L 11,8 L 14,12");
        public static readonly Geometry DatabaseShape = Geometry.Parse("M 2,4 C 2,1 14,1 14,4 C 14,7 2,7 2,4 L 2,12 C 2,15 14,15 14,12 L 14,4 M 2,8 C 2,11 14,11 14,8");
        public static readonly Geometry ContainerShape = Geometry.Parse("M 1.5,5 L 14.5,5 L 14.5,12.5 L 1.5,12.5 Z M 4,5 L 4,12.5 M 6.5,5 L 6.5,12.5 M 9,5 L 9,12.5 M 11.5,5 L 11.5,12.5 M 5,2.5 L 11,2.5 L 11,5");
        public static readonly Geometry LicenseShape = Geometry.Parse("M 3,1.5 L 13,1.5 L 13,11 L 10,11 L 8,14.5 L 6,11 L 3,11 Z");
        public static readonly Geometry CodeShape = Geometry.Parse("M 5,5 L 1.5,8 L 5,11 M 11,5 L 14.5,8 L 11,11 M 9.5,3 L 6.5,13");
        public static readonly Geometry TerminalShape = Geometry.Parse("M 1.5,3 L 14.5,3 L 14.5,13 L 1.5,13 Z M 4,6 L 6.5,8 L 4,10 M 8,10 L 12,10");
    }

    private static readonly (string Suffix, NcwFileIconDescriptor Icon)[] Extensions =
    [
        (".d.ts", Icon(NcwFileIconKind.TypeScript, NcwFileIconShape.Code, ArcDesignTokenKind.Accent, "TS")),
        (".tar.gz", Icon(NcwFileIconKind.Archive, NcwFileIconShape.Archive, ArcDesignTokenKind.Warning, "")),
        (".tar.bz2", Icon(NcwFileIconKind.Archive, NcwFileIconShape.Archive, ArcDesignTokenKind.Warning, "")),
        (".tar.xz", Icon(NcwFileIconKind.Archive, NcwFileIconShape.Archive, ArcDesignTokenKind.Warning, "")),
        (".csproj", Icon(NcwFileIconKind.Project, NcwFileIconShape.Document, ArcDesignTokenKind.Series3, "C#")),
        (".slnx", Icon(NcwFileIconKind.Solution, NcwFileIconShape.Data, ArcDesignTokenKind.Accent, "S")),
        (".sln", Icon(NcwFileIconKind.Solution, NcwFileIconShape.Data, ArcDesignTokenKind.Accent, "S")),
        (".props", Icon(NcwFileIconKind.Build, NcwFileIconShape.Data, ArcDesignTokenKind.Warning, "+")),
        (".targets", Icon(NcwFileIconKind.Build, NcwFileIconShape.Data, ArcDesignTokenKind.Warning, "+")),
        (".axaml", Icon(NcwFileIconKind.Avalonia, NcwFileIconShape.Markup, ArcDesignTokenKind.Series3, "A")),
        (".xaml", Icon(NcwFileIconKind.Xaml, NcwFileIconShape.Markup, ArcDesignTokenKind.Accent, "X")),
        (".cs", Icon(NcwFileIconKind.CSharp, NcwFileIconShape.Document, ArcDesignTokenKind.Series3, "C#")),
        (".json", Icon(NcwFileIconKind.Json, NcwFileIconShape.Data, ArcDesignTokenKind.Warning, "{}")),
        (".md", Icon(NcwFileIconKind.Markdown, NcwFileIconShape.Document, ArcDesignTokenKind.Accent, "MD")),
        (".markdown", Icon(NcwFileIconKind.Markdown, NcwFileIconShape.Document, ArcDesignTokenKind.Accent, "MD")),
        (".yml", Icon(NcwFileIconKind.Yaml, NcwFileIconShape.Data, ArcDesignTokenKind.Series4, "Y")),
        (".yaml", Icon(NcwFileIconKind.Yaml, NcwFileIconShape.Data, ArcDesignTokenKind.Series4, "Y")),
        (".toml", Icon(NcwFileIconKind.Toml, NcwFileIconShape.Data, ArcDesignTokenKind.Warning, "T")),
        (".xml", Icon(NcwFileIconKind.Xml, NcwFileIconShape.Markup, ArcDesignTokenKind.Warning, "X")),
        (".tsx", Icon(NcwFileIconKind.TypeScript, NcwFileIconShape.Document, ArcDesignTokenKind.Accent, "TX")),
        (".ts", Icon(NcwFileIconKind.TypeScript, NcwFileIconShape.Document, ArcDesignTokenKind.Accent, "TS")),
        (".jsx", Icon(NcwFileIconKind.JavaScript, NcwFileIconShape.Document, ArcDesignTokenKind.Warning, "JX")),
        (".js", Icon(NcwFileIconKind.JavaScript, NcwFileIconShape.Document, ArcDesignTokenKind.Warning, "JS")),
        (".py", Icon(NcwFileIconKind.Python, NcwFileIconShape.Document, ArcDesignTokenKind.Series2, "PY")),
        (".go", Icon(NcwFileIconKind.Go, NcwFileIconShape.Document, ArcDesignTokenKind.Series1, "GO")),
        (".rs", Icon(NcwFileIconKind.Rust, NcwFileIconShape.Document, ArcDesignTokenKind.Series4, "RS")),
        (".html", Icon(NcwFileIconKind.Html, NcwFileIconShape.Markup, ArcDesignTokenKind.Series4, "")),
        (".htm", Icon(NcwFileIconKind.Html, NcwFileIconShape.Markup, ArcDesignTokenKind.Series4, "")),
        (".css", Icon(NcwFileIconKind.Css, NcwFileIconShape.Style, ArcDesignTokenKind.Accent, "#")),
        (".scss", Icon(NcwFileIconKind.Css, NcwFileIconShape.Style, ArcDesignTokenKind.Accent, "#")),
        (".sh", Icon(NcwFileIconKind.Shell, NcwFileIconShape.Terminal, ArcDesignTokenKind.Success, "")),
        (".bash", Icon(NcwFileIconKind.Shell, NcwFileIconShape.Terminal, ArcDesignTokenKind.Success, "")),
        (".ps1", Icon(NcwFileIconKind.PowerShell, NcwFileIconShape.Terminal, ArcDesignTokenKind.Accent, "")),
        (".sql", Icon(NcwFileIconKind.Sql, NcwFileIconShape.Database, ArcDesignTokenKind.Warning, "")),
        .. new[] { ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".bmp", ".ico", ".avif", ".tif", ".tiff" }
            .Select(s => (s, Icon(NcwFileIconKind.Image, NcwFileIconShape.Image, ArcDesignTokenKind.Success, ""))),
        .. new[] { ".zip", ".7z", ".tar", ".gz", ".bz2", ".xz", ".rar" }
            .Select(s => (s, Icon(NcwFileIconKind.Archive, NcwFileIconShape.Archive, ArcDesignTokenKind.Warning, ""))),
    ];

    private NcwFileIconDescriptor _icon = Resolve("", false);

    static NcwFileIcon()
    {
        AffectsRender<NcwFileIcon>(PathProperty, IsDirectoryProperty, IsExpandedProperty, AccentBrushProperty,
            SuccessBrushProperty, WarningBrushProperty, Series1BrushProperty, Series2BrushProperty, Series3BrushProperty, Series4BrushProperty,
            ForegroundBrushProperty, SecondaryBrushProperty);
    }

    public NcwFileIcon()
    {
        ArcTokens.Bind(this, AccentBrushProperty, ArcDesignTokenKind.Accent);
        ArcTokens.Bind(this, SuccessBrushProperty, ArcDesignTokenKind.Success);
        ArcTokens.Bind(this, WarningBrushProperty, ArcDesignTokenKind.Warning);
        ArcTokens.Bind(this, Series1BrushProperty, ArcDesignTokenKind.Series1);
        ArcTokens.Bind(this, Series2BrushProperty, ArcDesignTokenKind.Series2);
        ArcTokens.Bind(this, Series3BrushProperty, ArcDesignTokenKind.Series3);
        ArcTokens.Bind(this, Series4BrushProperty, ArcDesignTokenKind.Series4);
        ArcTokens.Bind(this, ForegroundBrushProperty, ArcDesignTokenKind.Foreground);
        ArcTokens.Bind(this, SecondaryBrushProperty, ArcDesignTokenKind.TextSecondary);
    }

    public string Path
    {
        get => GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    public bool IsDirectory
    {
        get => GetValue(IsDirectoryProperty);
        set => SetValue(IsDirectoryProperty, value);
    }

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    /// <summary>跨平台路径只取末级名称,不访问文件系统;文件夹展开状态不改变分类。</summary>
    public static NcwFileIconDescriptor Resolve(string path, bool dir)
    {
        var normalized = (path ?? "").Replace('\\', '/').TrimEnd('/');
        var name = normalized[(normalized.LastIndexOf('/') + 1)..].ToLowerInvariant();
        if (dir)
        {
            return name switch
            {
                "src" or "source" => Icon(NcwFileIconKind.SourceFolder, NcwFileIconShape.Folder, ArcDesignTokenKind.Accent, ">"),
                "test" or "tests" => Icon(NcwFileIconKind.TestsFolder, NcwFileIconShape.Folder, ArcDesignTokenKind.Success, "T"),
                "doc" or "docs" => Icon(NcwFileIconKind.DocsFolder, NcwFileIconShape.Folder, ArcDesignTokenKind.Accent, "D"),
                "assets" or "images" => Icon(NcwFileIconKind.AssetsFolder, NcwFileIconShape.Folder, ArcDesignTokenKind.Success, "*"),
                ".git" => Icon(NcwFileIconKind.GitFolder, NcwFileIconShape.Folder, ArcDesignTokenKind.Series4, "G"),
                _ => Icon(NcwFileIconKind.Folder, NcwFileIconShape.Folder, ArcDesignTokenKind.Warning, ""),
            };
        }

        var named = name switch
        {
            "license" or "licence" or "license.md" or "license.txt" or "copying" => Icon(NcwFileIconKind.License, NcwFileIconShape.License, ArcDesignTokenKind.Warning, "="),
            "dockerfile" or "containerfile" => Icon(NcwFileIconKind.Docker, NcwFileIconShape.Container, ArcDesignTokenKind.Accent, ""),
            ".gitignore" or ".gitattributes" or ".gitmodules" => Icon(NcwFileIconKind.Git, NcwFileIconShape.Data, ArcDesignTokenKind.Series4, "G"),
            ".editorconfig" or "nuget.config" => Icon(NcwFileIconKind.Config, NcwFileIconShape.Data, ArcDesignTokenKind.TextSecondary, "+"),
            "global.json" => Icon(NcwFileIconKind.Build, NcwFileIconShape.Data, ArcDesignTokenKind.Accent, "+"),
            "package.json" => Icon(NcwFileIconKind.Project, NcwFileIconShape.Data, ArcDesignTokenKind.Series4, "{}"),
            ".env" => Icon(NcwFileIconKind.Environment, NcwFileIconShape.Data, ArcDesignTokenKind.Success, "E"),
            _ => null,
        };
        if (named is not null)
        {
            return named;
        }

        if (name.StartsWith(".env.", StringComparison.Ordinal))
        {
            return Icon(NcwFileIconKind.Environment, NcwFileIconShape.Data, ArcDesignTokenKind.Success, "E");
        }

        if (name.StartsWith("dockerfile.", StringComparison.Ordinal))
        {
            return Icon(NcwFileIconKind.Docker, NcwFileIconShape.Container, ArcDesignTokenKind.Accent, "");
        }

        NcwFileIconDescriptor? matched = null;
        var longest = 0;
        foreach (var (suffix, icon) in Extensions)
        {
            if (suffix.Length > longest && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                matched = icon;
                longest = suffix.Length;
            }
        }

        return matched ?? Icon(NcwFileIconKind.File, NcwFileIconShape.Document, ArcDesignTokenKind.TextSecondary, "");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PathProperty || change.Property == IsDirectoryProperty)
        {
            _icon = Resolve(Path, IsDirectory);
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(16, 16);

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
        {
            return;
        }

        var icon = _icon;
        var brush = GetValue(icon.Token switch
        {
            ArcDesignTokenKind.Accent => AccentBrushProperty,
            ArcDesignTokenKind.Success => SuccessBrushProperty,
            ArcDesignTokenKind.Warning => WarningBrushProperty,
            ArcDesignTokenKind.Series1 => Series1BrushProperty,
            ArcDesignTokenKind.Series2 => Series2BrushProperty,
            ArcDesignTokenKind.Series3 => Series3BrushProperty,
            ArcDesignTokenKind.Series4 => Series4BrushProperty,
            ArcDesignTokenKind.Foreground => ForegroundBrushProperty,
            _ => SecondaryBrushProperty,
        });
        if (brush is null)
        {
            return;
        }

        using var transform = context.PushTransform(Matrix.CreateScale(size / 16, size / 16)
            * Matrix.CreateTranslation((Bounds.Width - size) / 2, (Bounds.Height - size) / 2));
        var pen = new Pen(brush, 1.15, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var geometry = icon.Shape switch
        {
            NcwFileIconShape.Folder => IsExpanded ? Shapes.OpenFolder : Shapes.ClosedFolder,
            NcwFileIconShape.Image => Shapes.ImageShape,
            NcwFileIconShape.Database => Shapes.DatabaseShape,
            NcwFileIconShape.Container => Shapes.ContainerShape,
            NcwFileIconShape.License => Shapes.LicenseShape,
            NcwFileIconShape.Code or NcwFileIconShape.Markup => icon.Mark.Length == 0 ? Shapes.CodeShape : Shapes.Page,
            NcwFileIconShape.Terminal => Shapes.TerminalShape,
            _ => Shapes.Page,
        };
        if (icon.Shape is not (NcwFileIconShape.Code or NcwFileIconShape.Markup) || icon.Mark.Length != 0)
        {
            using var opacity = context.PushOpacity(0.13);
            context.DrawGeometry(brush, null, geometry);
        }

        context.DrawGeometry(null, pen, geometry);
        if (icon.Shape == NcwFileIconShape.Image)
        {
            context.DrawEllipse(brush, null, new Point(11.3, 5.7), 1, 1);
        }
        else if (icon.Shape == NcwFileIconShape.Archive)
        {
            for (var y = 5.5; y < 13; y += 2)
            {
                context.DrawRectangle(brush, null, new Rect(y % 4 < 2 ? 7 : 8, y, 1.5, 1));
            }
        }
        else if (icon.Mark.Length != 0)
        {
            var text = new FormattedText(icon.Mark, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, FontWeight.Bold), icon.Mark.Length > 1 ? 6 : 7, brush);
            var y = icon.Shape == NcwFileIconShape.Folder ? 6.5 : 7;
            context.DrawText(text, new Point((16 - text.Width) / 2, y));
        }
    }

    private static NcwFileIconDescriptor Icon(NcwFileIconKind kind, NcwFileIconShape shape, ArcDesignTokenKind token, string mark) => new(kind, shape, token, mark);
}
