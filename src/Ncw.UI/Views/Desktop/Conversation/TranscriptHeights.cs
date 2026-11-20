using Ncw.Client.ViewModels;
using Ncw.UI.Controls;

namespace Ncw.UI.Views.Desktop.Conversation;

/// <summary>
/// 转录各种行「还没量过时大约多高」(<see cref="NcwTranscriptPanel"/> 用)。只按数据粗算:字数 × 字宽 / 可用宽度 → 行数 × 行高,
/// 不碰控件、不做文字排版。数字取自 <c>ConversationView.axaml</c> 里各模板的边距和 Arc 的正文字号;估得系统性偏高 / 偏低由面板按行类型校准,
/// 所以这里只求「长的估得长、短的估得短」,比 Avalonia 默认的「全部按视口里几行的平均值」好得多。
/// </summary>
public sealed class TranscriptHeights : IRowHeightEstimator
{
    public static TranscriptHeights Instance { get; } = new();

    private const double BodyLine = 22;     // 正文(Markdown)行高
    private const double HintLine = 19;     // 小号提示文字 / 等宽行
    private const double LatinWidth = 7.6;  // 正文里一个拉丁字符大约多宽
    private const double WideWidth = 14.5;  // 一个 CJK / 全角字符大约多宽
    private const double ToolbarRow = 28;   // 消息下面的工具条(悬停才显示,但一直占位)
    private const double OneLineRow = 28;   // 工具行 / 思考块的标题行(含上下边距)
    private const double UserBubbleMax = 560;
    private const int ThinkingEstimateChars = 4000; // 展开的思考块封顶 NcwThinkingView.MaxBodyHeight:估到这么多字早就超过了,不必量全文

    public double? Estimate(object item, double width) => item switch
    {
        AssistantRowViewModel a => 8 + Markdown(a.Text, width) + (a.IsStreaming ? 12 : ToolbarRow),
        UserRowViewModel u => 12 + 18 + Plain(u.Text, Math.Min(width, UserBubbleMax) - 26, BodyLine)
                              + (u.HasFiles ? HintLine + 6 : 0) + (u.HasThumbs ? 124 : u.HasImageCountOnly ? HintLine + 6 : 0) + ToolbarRow,
        ThinkingRowViewModel t => OneLineRow + (t.IsExpanded ? Math.Min(Plain(t.Text.Length > ThinkingEstimateChars ? t.Text[..ThinkingEstimateChars] : t.Text, width - 22, HintLine), NcwThinkingView.MaxBodyHeight) + 4 : 0),
        ToolRowViewModel t => OneLineRow + (t.IsExpanded ? (t.PreviewLines.Count + (t.HasMore ? 1 : 0)) * HintLine + 6 : 0),
        WorkGroupRowViewModel => OneLineRow,
        WidgetRowViewModel => 44 + 160,
        WorkflowRunCardViewModel w => w.IsExpanded ? 52 + 260 : 48,
        CompactRowViewModel => 36,
        StatusRowViewModel => 30,
        _ => null,
    };

    /// <summary>Markdown 的大致高度:围栏代码块按行数(不折行,横向滚动)加上框的边距,其余按段落折行、段间距 10。</summary>
    internal static double Markdown(string text, double width)
    {
        if (text.Length == 0)
        {
            return BodyLine;
        }

        var height = 0.0;
        var inFence = false;
        var paragraph = false;
        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                height += inFence ? 24 + 8 : 0; // 代码块的上下内边距与间距
                paragraph = false;
                continue;
            }

            if (inFence)
            {
                height += HintLine;
                continue;
            }

            if (line.IsEmpty)
            {
                if (paragraph)
                {
                    height += 10;
                    paragraph = false;
                }

                continue;
            }

            paragraph = true;
            var lineHeight = line.StartsWith("#") ? BodyLine + 8 : BodyLine;
            height += Lines(line, width) * lineHeight;
        }

        return Math.Max(BodyLine, height);
    }

    internal static double Plain(string text, double width, double lineHeight)
    {
        var lines = 0;
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            lines += Lines(line, width);
        }

        return Math.Max(1, lines) * lineHeight;
    }

    /// <summary>一行文字在这个宽度下折成几行(宽字符按全角宽算)。</summary>
    private static int Lines(ReadOnlySpan<char> line, double width)
    {
        if (line.IsEmpty)
        {
            return 1;
        }

        var pixels = 0.0;
        foreach (var c in line)
        {
            pixels += c >= 0x2E80 ? WideWidth : LatinWidth;
        }

        return Math.Max(1, (int)Math.Ceiling(pixels / Math.Max(80, width)));
    }
}
