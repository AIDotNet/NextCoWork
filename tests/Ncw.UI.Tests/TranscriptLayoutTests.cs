using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Protocol.Methods;
using Ncw.UI.Chrome;
using Ncw.UI.Controls;
using Ncw.UI.Views.Desktop;
using Ncw.UI.Views.Desktop.Conversation;
using static Ncw.UI.Tests.ConversationFixtures;

namespace Ncw.UI.Tests;

/// <summary>
/// 转录行的版式缺陷回归:长命令压到右侧的环境标识上(两段文字叠在一起)、工具名被固定列宽截成「visuali…」;
/// 以及 WebView 外壳只摆在可见段上(原生视图不受 Avalonia 裁剪,滚出视口的部分会盖住输入框)。
/// </summary>
public class TranscriptLayoutTests
{
    private static void Dispatch()
    {
        for (var i = 0; i < 4; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static Rect BoundsIn(Visual visual, Visual root) =>
        visual.TransformToVisual(root) is { } m ? new Rect(visual.Bounds.Size).TransformToAABB(m) : default;

    [AvaloniaFact]
    public void A_long_command_is_trimmed_before_the_status_column_and_long_tool_names_keep_their_width()
    {
        var stores = Base("s-ncw-1");
        var command = "cd /Users/token/code/ArcGalleryServer && grep -rn \\\"CreateVersion7\\\" src --include='*.cs' | head; echo done; " + string.Concat(Enumerable.Repeat("more args ", 30));
        Session(stores, "s-ncw-1",
        [
            User("u1", "看看"),
            Assistant("a1", 2, Call("c1", "Bash", $"{{\"command\":\"{command}\"}}"), Call("c2", "visualize_read_me", "{}")),
            ToolResults("r1", 1, Result("c1", "x"), Result("c2", "spec")),
        ]);
        var vm = new ShellViewModel(stores);
        var shell = new DesktopShell { DataContext = vm, Chrome = new WindowChrome(78, false) };
        var window = new Window { Width = 1000, Height = 800, Content = shell };
        window.Show();
        Dispatch();

        // 让命令那一行带上非本机的环境标识(SSH 一定画在行上)
        var tool = vm.Conversation.Rows.OfType<ToolRowViewModel>().Single(t => t.Row.CallId == "c1");
        tool.Update(tool.Row with { Env = new EnvRefDto("local", "dev", "dev-server", "ssh") });
        Dispatch();

        var card = window.GetVisualDescendants().OfType<ToolCard>().Single(c => ReferenceEquals(c.DataContext, tool));
        var target = card.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("toolTarget"));
        var env = card.GetVisualDescendants().OfType<TextBlock>().Single(t => t.IsEffectivelyVisible && t.Text == "本机 · dev-server");
        Assert.True(BoundsIn(target, card).Right <= BoundsIn(env, card).Left + 0.5,
            $"target {BoundsIn(target, card)} must end before the environment label {BoundsIn(env, card)}");
        Assert.True(target.TextLayout.TextLines.Count == 1 && target.TextLayout.TextLines[0].HasCollapsed, "a long command is trimmed with an ellipsis");

        var readMe = vm.Conversation.Rows.OfType<ToolRowViewModel>().Single(t => t.Row.CallId == "c2");
        var nameBlock = window.GetVisualDescendants().OfType<ToolCard>().Single(c => ReferenceEquals(c.DataContext, readMe))
            .GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("toolName"));
        Assert.False(nameBlock.TextLayout.TextLines[0].HasCollapsed, "visualize_read_me is not cut down to 'visuali…'");
    }

    private sealed record Block(double Height);

    private sealed class ExactEstimator : IRowHeightEstimator
    {
        public double? Estimate(object item, double width) => item is Block b ? b.Height : null;
    }

    private static (Window Window, ScrollViewer Scroller, NcwTranscriptPanel Panel, ItemsControl List) Transcript(
        System.Collections.ObjectModel.ObservableCollection<Block> items, IRowHeightEstimator? estimator)
    {
        var list = new ItemsControl
        {
            ItemsSource = items,
            ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new NcwTranscriptPanel { Estimator = estimator }),
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Block>((b, _) => new Border { Height = b?.Height ?? 0 }),
        };
        var scroller = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var window = new Window { Width = 500, Height = 600, Content = scroller };
        window.Show();
        Dispatch();
        var panel = list.GetVisualDescendants().OfType<NcwTranscriptPanel>().Single();
        return (window, scroller, panel, list);
    }

    /// <summary>一长一短交替(工具行 26 / 回答 300):Avalonia 的 VirtualizingStackPanel 会按视口里几行的平均值估全部。</summary>
    private static System.Collections.ObjectModel.ObservableCollection<Block> Mixed(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new Block(i % 4 == 0 ? 300 : 26))];

    [AvaloniaFact]
    public void With_an_estimator_the_extent_is_right_before_anything_was_measured_and_only_the_viewport_is_realized()
    {
        var items = Mixed(400);
        var (_, scroller, panel, _) = Transcript(items, new ExactEstimator());

        Assert.Equal(items.Sum(b => b.Height), scroller.Extent.Height, 0.5);
        var (first, last) = panel.RealizedRange;
        Assert.True(last - first + 1 < 60, $"only rows near the viewport are realized, got {first}..{last}");
        Assert.True(panel.Children.Count < 60);
    }

    [AvaloniaFact]
    public void Measured_heights_are_remembered_so_the_extent_converges_and_stays()
    {
        var items = Mixed(300);
        var (_, scroller, panel, _) = Transcript(items, estimator: null);
        var total = items.Sum(b => b.Height);

        // 一屏一屏往下翻一遍:每一行都被量过
        for (var guard = 0; guard < 400 && scroller.Offset.Y < scroller.Extent.Height - scroller.Viewport.Height - 1; guard++)
        {
            scroller.Offset = new Vector(0, scroller.Offset.Y + scroller.Viewport.Height * 0.8);
            Dispatch();
        }

        Assert.Equal(total, scroller.Extent.Height, 0.5);

        // 回到顶上:量过的高度都记得,总高度不再变(滚动条不会再跳)
        scroller.Offset = new Vector(0, 0);
        Dispatch();
        Assert.Equal(total, scroller.Extent.Height, 0.5);
        Assert.Equal(300, panel.HeightOf(0), 0.5);
        Assert.Equal(26, panel.HeightOf(299), 0.5);
    }

    [AvaloniaFact]
    public void Scroll_into_view_realizes_a_far_row_and_removing_rows_shrinks_the_extent()
    {
        var items = Mixed(300);
        var (_, scroller, panel, list) = Transcript(items, new ExactEstimator());

        list.ScrollIntoView(250);
        Dispatch();
        var container = list.ContainerFromIndex(250);
        Assert.NotNull(container);
        var top = container!.TransformToVisual(scroller)!.Value.Transform(default(Point)).Y;
        Assert.InRange(top, -1, scroller.Viewport.Height);

        var before = scroller.Extent.Height;
        items.RemoveAt(0);
        Dispatch();
        Assert.Equal(before - 300, scroller.Extent.Height, 0.5);
        Assert.Equal(26, panel.HeightOf(0), 0.5);
    }

    [AvaloniaFact]
    public void Streaming_markdown_appends_only_the_new_text_and_completes_in_place()
    {
        var markdown = new NcwStreamingMarkdown { IsLive = true, Source = "Hello" };
        var window = new Window { Width = 500, Height = 400, Content = markdown };
        window.Show();
        Dispatch();
        Assert.True(markdown.IsAppending);
        var stream = markdown.Stream;
        Assert.NotNull(stream);

        markdown.Source = "Hello **world**";
        Dispatch();
        Assert.Same(stream, markdown.Stream); // 同一条流,只推了差量

        markdown.IsLive = false; // 落库,文字是延续:收尾,不重载
        Dispatch();
        Assert.False(markdown.IsAppending);
        Assert.Same(stream, markdown.Stream);

        markdown.Source = "Something else entirely"; // 容器回收给另一行 / 文字整个换了
        Dispatch();
        Assert.Null(markdown.Stream);
        Assert.Equal("Something else entirely", markdown.Text);
    }

    [AvaloniaFact]
    public void The_web_view_is_placed_only_on_the_visible_slice_of_the_card()
    {
        var surface = new NcwWidgetSurface { Height = 400 };
        var content = new StackPanel();
        content.Children.Add(new Border { Height = 300 });
        content.Children.Add(surface);
        content.Children.Add(new Border { Height = 1000 });
        var scroller = new ScrollViewer { Content = content, Height = 500, VerticalAlignment = VerticalAlignment.Top };
        var window = new Window { Width = 600, Height = 800, Content = scroller, Background = Brushes.Black };
        window.Show();
        Dispatch();

        // 卡片在 300–700,视口 0–500:只有上面 200 可见
        Assert.Equal(new Rect(0, 0, surface.Bounds.Width, 200), surface.VisibleRect);

        // 往下滚 450:卡片在视口里是 -150–250,可见段从卡片内 150 开始、高 250
        scroller.Offset = new Vector(0, 450);
        Dispatch();
        Assert.Equal(new Rect(0, 150, surface.Bounds.Width, 250), surface.VisibleRect);

        // 视口底部的浮层(「回到最新」)让出 60
        NcwWidgetClip.SetBottomInset(scroller, 60);
        scroller.InvalidateArrange();
        Dispatch();
        Assert.Equal(new Rect(0, 150, surface.Bounds.Width, 250), surface.VisibleRect); // 卡片下沿 250 < 440,不受影响
        scroller.Offset = new Vector(0, 100);
        Dispatch();
        Assert.Equal(new Rect(0, 0, surface.Bounds.Width, 240), surface.VisibleRect); // 卡片在 200–600,视口有效到 500 − 60 = 440

        // 完全滚出去:整个隐藏
        scroller.Offset = new Vector(0, 900);
        Dispatch();
        Assert.Equal(default, surface.VisibleRect);
        surface.Dispose();
    }
}
