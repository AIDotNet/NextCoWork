using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Ncw.UI.Controls;

namespace Ncw.UI.Tests;

/// <summary>思考块正文(docs/design/03 §6「思考块」):限高、框内滚动、流式时跟到最新、只建看得见的段落、只重切最后一块。</summary>
public class ThinkingViewTests
{
    private static void Dispatch()
    {
        for (var i = 0; i < 3; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static (Window Window, NcwThinkingView View, Panel Host) Render(bool live)
    {
        var view = new NcwThinkingView { IsLive = live };
        var host = new StackPanel { Children = { view } };
        var window = new Window { Width = 600, Height = 800, Content = host };
        window.Show();
        Dispatch();
        return (window, view, host);
    }

    private static string Paragraphs(int count) => string.Join('\n', Enumerable.Range(0, count).Select(i => $"第 {i} 段:先读一下相关代码,再想想怎么改比较稳妥。"));

    [Fact]
    public void Text_is_split_into_paragraphs_and_overlong_paragraphs_at_a_break_near_the_limit()
    {
        Assert.Equal([(0, 1), (2, 3), (4, 4)], NcwThinkingView.Split("a\nb\n", 0));
        Assert.Equal([(2, 3)], NcwThinkingView.Split("a\nb", 2));

        var words = string.Concat(Enumerable.Repeat("word ", 500));   // 2500 字、没有换行
        var pieces = NcwThinkingView.Split(words, 0);
        Assert.True(pieces.Count >= 3);
        Assert.All(pieces, p => Assert.True(p.End - p.Start <= NcwThinkingView.MaxChunk));
        Assert.All(pieces.SkipLast(1), p => Assert.Equal(' ', words[p.End - 1]));   // 切在空白后面,不切开单词
        Assert.Equal(words.Length, pieces[^1].End);
    }

    [AvaloniaFact]
    public void Streaming_only_rewrites_the_last_chunk_and_follows_the_newest_text()
    {
        var (_, view, _) = Render(live: true);
        var text = Paragraphs(80);
        view.Text = text;
        Dispatch();
        var before = view.ChunkTexts.ToArray();
        Assert.Equal(80, before.Length);

        view.Text = text + "继续";
        Dispatch();
        view.Text = text + "继续写\n新的一段";
        Dispatch();
        Assert.Equal(81, view.ChunkTexts.Count);
        Assert.Equal("新的一段", view.ChunkTexts[^1]);
        for (var i = 0; i < 79; i++)
        {
            Assert.Same(before[i], view.ChunkTexts[i]);   // 前面的块原样不动
        }

        // 限高,框里只建看得见的那几段
        Assert.True(view.Bounds.Height <= NcwThinkingView.MaxBodyHeight + 0.5);
        Assert.InRange(view.GetVisualDescendants().OfType<SelectableTextBlock>().Count(), 1, 30);

        // 跟到最新
        var scroll = view.Scroll;
        Assert.True(view.IsFollowing);
        Assert.Equal(scroll.Extent.Height - scroll.Viewport.Height, scroll.Offset.Y, 1);
    }

    [AvaloniaFact]
    public void Scrolling_up_stops_following_and_returning_to_the_bottom_resumes_it()
    {
        var (_, view, _) = Render(live: true);
        var text = Paragraphs(80);
        view.Text = text;
        Dispatch();
        var scroll = view.Scroll;

        scroll.Offset = new Avalonia.Vector(0, 40);   // 用户往回翻
        Dispatch();
        Assert.False(view.IsFollowing);
        view.Text = text += "\n又想到一点";
        Dispatch();
        Assert.Equal(40, scroll.Offset.Y, 1);         // 不抢位置

        scroll.Offset = new Avalonia.Vector(0, scroll.Extent.Height - scroll.Viewport.Height);
        Dispatch();
        view.Text = text + "\n最后一点";
        Dispatch();
        Assert.True(view.IsFollowing);
        Assert.Equal(scroll.Extent.Height - scroll.Viewport.Height, scroll.Offset.Y, 1);
    }

    [AvaloniaFact]
    public void Finished_thinking_opens_at_the_top_and_hidden_views_do_no_work()
    {
        var (_, view, host) = Render(live: false);
        host.IsVisible = false;
        view.Text = Paragraphs(80);
        Dispatch();
        Assert.Empty(view.ChunkTexts);                // 折叠着(看不见):不切块

        host.IsVisible = true;
        Dispatch();
        Assert.Equal(80, view.ChunkTexts.Count);
        Assert.False(view.IsFollowing);
        Assert.Equal(0, view.Scroll.Offset.Y);
    }
}
