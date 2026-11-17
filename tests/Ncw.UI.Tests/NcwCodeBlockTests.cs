using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ncw.UI.Controls;
using TKCode.Arc;

namespace Ncw.UI.Tests;

/// <summary>文件标签页用的「编辑器形态」代码块:无头无外框,横向滚动条吸附在外层滚动区的可视底边。</summary>
public class NcwCodeBlockTests
{
    private static (Window Window, ScrollViewer Outer, NcwCodeBlock Block) Render(bool chromeless, bool sticky)
    {
        ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark).MotionLevel(TKCode.Arc.Motion.ArcMotionLevel.Off));
        var block = new NcwCodeBlock
        {
            Code = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"line {i}" + new string('x', 300))),
            Language = "text",
            ShowLineNumbers = true,
            FileName = "a.env",
            IsChromeless = chromeless,
            StickyScrollBar = sticky,
        };
        var outer = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = block };
        var window = new Window { Width = 800, Height = 500, Content = outer };
        window.Show();
        Settle();
        return (window, outer, block);
    }

    private static void Settle()
    {
        for (var i = 0; i < 6; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(30);
        }
    }

    private static ScrollBar Bar(NcwCodeBlock block) =>
        block.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Name == "PART_HorizontalScrollBar");

    [AvaloniaFact]
    public void Chromeless_hides_the_header_and_the_card_frame()
    {
        var (_, _, plain) = Render(chromeless: false, sticky: false);
        var (_, _, bare) = Render(chromeless: true, sticky: false);

        Assert.True(plain.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "PART_Header").IsVisible);
        Assert.False(bare.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "PART_Header").IsVisible);
        Assert.Equal(default, bare.GetVisualDescendants().OfType<Border>().Single(c => c.Name == "PART_Frame").BorderThickness);
    }

    [AvaloniaFact]
    public void Sticky_bar_stays_at_the_bottom_of_the_viewport_while_scrolling()
    {
        var (_, outer, block) = Render(chromeless: true, sticky: true);
        var bar = Bar(block);

        foreach (var offset in new[] { 0.0, 400, 900 })
        {
            outer.Offset = new Vector(0, offset);
            Settle();
            var bottom = bar.TranslatePoint(new Point(0, bar.Bounds.Height), outer)!.Value.Y;
            Assert.InRange(bottom, outer.Viewport.Height - 1, outer.Viewport.Height + 1);
        }

        // 滚到最后,代码块底边进入可视区:条回到原位(代码块的最下面)
        outer.Offset = new Vector(0, outer.Extent.Height);
        Settle();
        Assert.InRange(bar.TranslatePoint(new Point(0, bar.Bounds.Height), block)!.Value.Y, block.Bounds.Height - 1, block.Bounds.Height + 1);
    }

    [AvaloniaFact]
    public void Without_sticky_the_bar_stays_at_the_end_of_the_block()
    {
        var (_, outer, block) = Render(chromeless: true, sticky: false);
        var bar = Bar(block);
        Assert.True(bar.TranslatePoint(new Point(0, 0), outer)!.Value.Y > outer.Viewport.Height);
    }
}
