namespace Ncw.Agent.Widgets.Guidelines;

/// <summary>
/// <c>visualize_read_me</c> 的模块表与装配(2.x <c>visualize-guidelines/index.ts</c>):整套规范有 7 万字,模型先说要画什么、我们再返回那几段,系统提示词因此保持精简。
/// <b>去重是必须的</b>:<c>interactive</c> / <c>chart</c> / <c>mockup</c> 共享 UI 组件与配色,<c>art</c> / <c>diagram</c> 共享 SVG setup——一次请求两个模块时同一段只能出现一次(规范里多处写着「见上 / 见下」)。
/// <para>
/// 与 2.x 的差异:<b>没有 <c>components</c> 模块</b>(那是 TSX widget 的规范,3.0 不做 TSX:编译要 sucrase / JS 工具链,而 AGENTS.md §6 不允许,且渲染宿主的 React 组件库也不存在)。
/// </para>
/// </summary>
public static class WidgetGuidelines
{
    /// <summary>模型在 schema 里看到的枚举顺序;改动要当成产品改动看。</summary>
    public static IReadOnlyList<string> AvailableModules { get; } = ["diagram", "mockup", "interactive", "chart", "art"];

    private static readonly (string Id, string Text) Palette = ("color-palette", GuidelineText.ColorPalette);
    private static readonly (string Id, string Text) Ui = ("ui-components", GuidelineText.UiComponents);
    private static readonly (string Id, string Text) Svg = ("svg-setup", GuidelineText.SvgSetup);

    private static IReadOnlyList<(string Id, string Text)> Sections(string module) => module switch
    {
        "diagram" => [Palette, Svg, ("diagram-types", GuidelineText.DiagramTypes)],
        "mockup" => [Ui, Palette],
        "interactive" => [Ui, Palette],
        "chart" => [Ui, Palette, ("charts", GuidelineText.ChartsChartJs)],
        "art" => [Svg, ("art", GuidelineText.ArtAndIllustration)],
        _ => [],
    };

    public static string Core => GuidelineText.Core;

    /// <summary>某一段设计规范的原文(测试对「共享段落只出现一次」用)。</summary>
    internal static string Section(string id) => id switch
    {
        "color-palette" => GuidelineText.ColorPalette,
        "ui-components" => GuidelineText.UiComponents,
        "svg-setup" => GuidelineText.SvgSetup,
        "diagram-types" => GuidelineText.DiagramTypes,
        "charts" => GuidelineText.ChartsChartJs,
        "art" => GuidelineText.ArtAndIllustration,
        _ => "",
    };

    /// <summary>段落之间空两行、末尾补一个换行(原实现的形状:正文里的标题排版依赖它)。</summary>
    public static string Get(IEnumerable<string> modules)
    {
        var content = new System.Text.StringBuilder(GuidelineText.Core);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            foreach (var (id, text) in Sections(module))
            {
                if (seen.Add(id))
                {
                    content.Append("\n\n\n").Append(text);
                }
            }
        }

        return content.Append('\n').ToString();
    }
}
