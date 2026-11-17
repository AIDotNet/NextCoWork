using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Protocol.Methods;
using Ncw.UI.Chrome;
using Ncw.UI.Controls;
using Ncw.UI.Views.Desktop;
using TKCode.Arc;
using static Ncw.UI.Tests.ConversationFixtures;

namespace Ncw.UI.Tests;

/// <summary>消息工具条(复制 / 编辑 / 分支 / 删除一轮)和行内编辑的 Headless 截图与结构断言。截图在 <c>screenshots/message-*.png</c>。悬停显示靠指针,Headless 里不模拟,这里看常驻的两种状态:编辑中、确认删除。</summary>
public class MessageToolbarScreenshotTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private static void Dispatch()
    {
        for (var i = 0; i < 4; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static ClientStores Chat()
    {
        var stores = Base("s-ncw-3");
        Session(stores, "s-ncw-3",
        [
            User("u1", "csharp是什么?", minutesAgo: 5),
            Assistant("a1", 4, Text("C# 是微软开发的一门现代、面向对象的编程语言,运行在 .NET 平台上。\n\n**主要特点**:强类型、多范式、自动内存管理。")),
            User("u2", "它和 Java 有什么区别?", minutesAgo: 2),
            Assistant("a2", 1, Text("两者语法相近,但 C# 有属性、委托、LINQ、async/await 等更多语言级特性。")),
        ]);
        return stores;
    }

    private static (Window Window, ShellViewModel Vm, DesktopShell Shell) Render(ClientStores stores, bool dark, double width = 1200)
    {
        ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
        var vm = new ShellViewModel(stores, new FixedClock(Now));
        var shell = new DesktopShell { DataContext = vm, Chrome = new WindowChrome(78, false) };
        var window = new Window { Width = width, Height = 800, Content = shell };
        window.Show();
        Dispatch();
        return (window, vm, shell);
    }

    private static void Snapshot(Window window, string scenario, bool dark)
    {
        Dispatch();
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"message-{scenario}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark));
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Messages_render_with_a_toolbar_that_rests_hidden_until_hover(bool dark)
    {
        var (window, vm, shell) = Render(Chat(), dark);

        Assert.Equal(2, vm.Conversation.Rows.OfType<UserRowViewModel>().Count());
        Assert.True(vm.Conversation.CanEditHistory);
        // 每条消息一个工具条,静止时不可见(Opacity 0),悬停才显示
        var toolbars = shell.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Classes.Contains("tools")).ToList();
        Assert.True(toolbars.Count >= 4);
        Assert.All(toolbars, t => Assert.Equal(0, t.Opacity));
        Snapshot(window, "resting", dark);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Editing_a_user_message_shows_an_inline_editor_with_three_choices(bool dark)
    {
        var (window, vm, shell) = Render(Chat(), dark);
        var row = vm.Conversation.Rows.OfType<UserRowViewModel>().First();
        vm.Conversation.BeginEditCommand.Execute(row);
        Dispatch();

        var editor = shell.GetVisualDescendants().OfType<NcwTextArea>().FirstOrDefault(t => t.Text == "csharp是什么?");
        Assert.NotNull(editor);
        Assert.True(editor!.IsEffectivelyVisible);
        Snapshot(window, "editing", dark);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Deleting_a_turn_asks_for_a_second_confirmation_in_place(bool dark)
    {
        var (window, vm, shell) = Render(Chat(), dark);
        var row = vm.Conversation.Rows.OfType<UserRowViewModel>().Last();
        vm.Conversation.AskDeleteTurnCommand.Execute(row);
        Dispatch();

        Assert.True(row.IsConfirmingDelete);
        var pinned = shell.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Classes.Contains("pinned")).ToList();
        Assert.Single(pinned);
        Assert.Equal(1, pinned[0].Opacity); // 确认状态钉住,不会因为鼠标移开而消失
        Snapshot(window, "confirm-delete", dark);
    }

    [AvaloniaFact]
    public void A_message_error_shows_a_dismissable_banner()
    {
        var (window, vm, shell) = Render(Chat(), dark: true);
        var field = typeof(ConversationViewModel).GetProperty(nameof(ConversationViewModel.MessageError))!;
        field.SetValue(vm.Conversation, MessageErrorKeys.Busy);
        Dispatch();

        Assert.True(vm.Conversation.HasMessageError);
        Assert.Contains(shell.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Ncw.UI.Localization.NcwTexts.Get(Ncw.UI.Localization.NcwTextKind.MsgErrBusy));
        Snapshot(window, "error", dark: true);
    }

    /// <summary>用 Avalonia 画一张带渐变和图形的 PNG(1x1 的透明图看不出缩略图效果)。</summary>
    private static string PictureDataUrl(Color from, Color to)
    {
        using var target = new RenderTargetBitmap(new PixelSize(320, 200));
        using (var ctx = target.CreateDrawingContext())
        {
            ctx.DrawRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = [new GradientStop(from, 0), new GradientStop(to, 1)],
            }, null, new Rect(0, 0, 320, 200));
            ctx.DrawEllipse(Brushes.White, null, new Point(110, 100), 46, 46);
            ctx.DrawRectangle(Brushes.White, null, new Rect(190, 70, 80, 60));
        }

        using var stream = new MemoryStream();
        target.Save(stream, new PngBitmapEncoderOptions());
        return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
    }

    private static ClientStores ChatWithImages()
    {
        var stores = Base("s-ncw-3");
        string Image(string dataRef) => $"{{\"type\":\"image\",\"mime\":\"image/png\",\"dataRef\":\"{dataRef}\"}}";
        var parts = $"[{Text("这几张图哪张最适合做封面?")},{Image(PictureDataUrl(Colors.SteelBlue, Colors.MediumPurple))},{Image(PictureDataUrl(Colors.Orange, Colors.IndianRed))},{Image("ncw://gone-attachment")}]";
        Session(stores, "s-ncw-3",
        [
            new MessageDto("u1", "user", Ms(Now.AddMinutes(-3)), false, parts),
            Assistant("a1", 2, Text("第一张的色彩对比最强,适合做封面。第三张没能加载,我没看到。")),
        ]);
        return stores;
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Images_in_a_user_message_show_as_thumbnails_with_a_failed_one_that_can_retry(bool dark)
    {
        var (window, vm, shell) = Render(ChatWithImages(), dark);
        var row = vm.Conversation.Rows.OfType<UserRowViewModel>().Single();
        for (var i = 0; i < 10 && row.Images.Any(t => t.IsLoading); i++)
        {
            Dispatch();
        }

        Assert.Equal(3, row.Images.Count);
        Assert.Equal(2, row.Images.Count(t => t.IsReady));  // 两张内联图解码成功
        Assert.True(row.Images[2].IsFailed);                // 附件引用取不到(设备没连上)→ 失败态,可以重试
        var thumbs = shell.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("thumb")).ToList();
        Assert.Equal(3, thumbs.Count);
        Assert.Equal(2, thumbs.SelectMany(t => t.GetVisualDescendants().OfType<Image>()).Count(i => i.Source is Bitmap && i.IsEffectivelyVisible)); // 只数消息里的缩略图(外壳左上角的品牌图标也是位图)
        Snapshot(window, "images", dark);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Opening_a_thumbnail_shows_the_lightbox_and_closing_releases_the_widget_airspace(bool dark)
    {
        var (window, vm, shell) = Render(ChatWithImages(), dark);
        var row = vm.Conversation.Rows.OfType<UserRowViewModel>().Single();
        for (var i = 0; i < 10 && row.Images.Any(t => t.IsLoading); i++)
        {
            Dispatch();
        }

        // 空域的挂起计数是进程内静态的,别的测试可能留着未释放的挂起:这里只断言「我这次操作」引起的变化,不假设初始状态
        var changes = 0;
        void OnChanged() => changes++;
        Ncw.UI.Controls.NcwWidgetAirspace.Changed += OnChanged;
        try
        {
            vm.Conversation.OpenImageCommand.Execute(row.Images[0]);
            Dispatch();

            var layer = shell.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "LightboxLayer");
            Assert.True(layer.IsEffectivelyVisible);
            Assert.Equal(1, changes);                                      // 打开:挂起一次,WebView 小组件让出空域,灯箱才盖得住
            Assert.True(Ncw.UI.Controls.NcwWidgetAirspace.IsSuspended);
            var big = shell.GetVisualDescendants().OfType<Image>().Single(i => i.Name == "LightboxImage");
            Assert.NotNull(big.Source);
            Snapshot(window, "lightbox", dark);

            vm.Conversation.CloseLightboxCommand.Execute(null);
            Dispatch();
            Assert.False(layer.IsEffectivelyVisible);
            Assert.Equal(2, changes);                                      // 关闭:恰好归还这一次
        }
        finally
        {
            Ncw.UI.Controls.NcwWidgetAirspace.Changed -= OnChanged;
        }
    }

    [AvaloniaFact]
    public void Only_the_last_reply_shows_a_regenerate_button()
    {
        var (_, vm, shell) = Render(Chat(), dark: true);
        var replies = vm.Conversation.Rows.OfType<AssistantRowViewModel>().ToList();
        Assert.Equal([false, true], replies.Select(r => r.CanRegenerate));

        // 视觉树里「重新生成」按钮只在最后一段回复的工具条里可见
        var visible = shell.GetVisualDescendants().OfType<Control>()
            .Where(c => c is Ncw.UI.Controls.PlainButton && c.DataContext is AssistantRowViewModel { CanRegenerate: true } && c.IsEffectivelyVisible
                && Avalonia.Automation.AutomationProperties.GetName(c) == Ncw.UI.Localization.NcwTexts.Get(Ncw.UI.Localization.NcwTextKind.MsgRegenerate))
            .ToList();
        Assert.Single(visible);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_ai_reply_offers_branch_and_delete_and_deleting_asks_for_a_second_confirmation(bool dark)
    {
        var (window, vm, shell) = Render(Chat(), dark);
        var replies = vm.Conversation.Rows.OfType<AssistantRowViewModel>().ToList();
        Assert.All(replies, r => Assert.True(r.HasTurn)); // 两段回复前面都有提问:都能分支

        string Name(Ncw.UI.Localization.NcwTextKind kind) => Ncw.UI.Localization.NcwTexts.Get(kind);
        var names = shell.GetVisualDescendants().OfType<Control>()
            .Where(c => c is Ncw.UI.Controls.PlainButton && c.DataContext is AssistantRowViewModel)
            .Select(c => Avalonia.Automation.AutomationProperties.GetName(c)).ToList();
        Assert.Contains(Name(Ncw.UI.Localization.NcwTextKind.MsgBranch), names);
        Assert.Contains(Name(Ncw.UI.Localization.NcwTextKind.MsgDeleteReply), names);

        var last = replies[^1];
        vm.Conversation.AskDeleteReplyCommand.Execute(last);
        Dispatch();
        Assert.True(last.IsConfirmingDelete);
        // 确认状态钉住工具条,出现「删除这条回复?」的文字
        Assert.Contains(shell.GetVisualDescendants().OfType<StackPanel>(), p => p.Classes.Contains("pinned") && p.DataContext == last && p.Opacity == 1);
        Assert.Contains(shell.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Name(Ncw.UI.Localization.NcwTextKind.MsgDeleteReplyConfirm) && t.IsEffectivelyVisible);
        // 确认态只留确认文字和两个按钮:重新生成 / 分支 / 复制都收起来(和用户消息的确认态一致)
        var visibleButtons = shell.GetVisualDescendants().OfType<Control>()
            .Where(c => c is Ncw.UI.Controls.PlainButton && c.DataContext == last && c.IsEffectivelyVisible)
            .Select(c => Avalonia.Automation.AutomationProperties.GetName(c)).ToList();
        Assert.DoesNotContain(Name(Ncw.UI.Localization.NcwTextKind.MsgRegenerate), visibleButtons);
        Assert.DoesNotContain(Name(Ncw.UI.Localization.NcwTextKind.MsgBranch), visibleButtons);
        Assert.DoesNotContain(Name(Ncw.UI.Localization.NcwTextKind.MsgCopy), visibleButtons);
        Snapshot(window, "reply-confirm-delete", dark);
    }
}
