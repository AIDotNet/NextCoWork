using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.UI.Chrome;
using Ncw.UI.Controls;
using Ncw.UI.Views.Desktop;
using Ncw.UI.Views.Desktop.Conversation;
using TKCode.Arc;
using TKCode.Arc.Motion;
using static Ncw.UI.Tests.ConversationFixtures;

namespace Ncw.UI.Tests;

/// <summary>
/// 动效在整套界面里的行为(docs/design/03 §9):展开 / 收起的<b>最终状态</b>在四个断点 × 深浅主题 × 动效级别(关闭 / 减弱)下完全一致,
/// 动画<b>进行到一半</b>的那一帧(Full + Arc 的 <see cref="ManualFrameClock"/>)能被截到并断言,
/// 以及左右栏滑动、新行出现、设置覆盖层进出这些「整窗级」的过渡。截图在 <c>bin/Debug/net10.0/screenshots/motion-*.png</c>。
/// </summary>
public class MotionScreenshotTests : IDisposable
{
    private readonly ManualFrameClock _clock = new();
    private readonly IDisposable _install;

    public MotionScreenshotTests() => _install = _clock.Install();

    public void Dispose()
    {
        _install.Dispose();
        NcwMotion.Apply(UiMotionLevel.Off);
        ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark));
    }

    public static IEnumerable<object[]> Matrix() =>
        from width in new[] { 1500, 1200, 900, 600 }
        from dark in new[] { true, false }
        from level in new[] { UiMotionLevel.Off, UiMotionLevel.Reduced }
        select new object[] { width, dark, level };

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private static (Window Window, DesktopShell Shell, ShellViewModel Vm) Render(ClientStores stores, double width, bool dark, double height = 860)
    {
        ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
        Ncw.UI.Converters.RelativeTimeConverter.Clock = new FixedClock(Now);
        var vm = new ShellViewModel(stores, new FixedClock(Now));
        var shell = new DesktopShell { DataContext = vm, Chrome = new WindowChrome(78, false) };
        var window = new Window { Width = width, Height = height, Content = shell };
        window.Show();
        Pump();
        return (window, shell, vm);
    }

    private static void Pump(int rounds = 4)
    {
        for (var i = 0; i < rounds; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private void Advance(int ms)
    {
        for (var elapsed = 0; elapsed < ms; elapsed += 16)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(Math.Min(16, ms - elapsed)));
            Pump(2);
        }

        Pump();
    }

    private static void Snapshot(Window window, string scenario, double width, bool dark, string? suffix = null)
    {
        Pump();
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"motion-{scenario}-{width}-{(dark ? "dark" : "light")}{suffix}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static NcwCollapsible DetailOf(Window window, ToolRowViewModel row) =>
        window.GetVisualDescendants().OfType<ToolCard>().First(c => ReferenceEquals(c.DataContext, row)).GetVisualDescendants().OfType<NcwCollapsible>().First();

    /// <summary>虚拟化列表:内容变高 / 变矮、跟随最新之后,屏幕外的行可能被回收,所以每次都按行重新找容器(找不到 = 在屏幕外,没有实体)。</summary>
    private static NcwCollapsible? TryDetailOf(Window window, ToolRowViewModel row) =>
        window.GetVisualDescendants().OfType<ToolCard>().FirstOrDefault(c => ReferenceEquals(c.DataContext, row))?.GetVisualDescendants().OfType<NcwCollapsible>().First();

    // ───────────────────────── 最终状态:展开 / 收起 × 四个断点 × 深浅 × 关闭 / 减弱 ─────────────────────────

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Expand_and_collapse_end_in_the_same_state_at_every_motion_level(int width, bool dark, UiMotionLevel level)
    {
        NcwMotion.Apply(level);
        var (window, _, vm) = Render(ConversationScreenshotTests.Tools(), width, dark);
        var tools = vm.Conversation.Rows.OfType<ToolRowViewModel>().ToDictionary(t => t.Row.CallId);
        var c3 = DetailOf(window, tools["c3"]);
        var c2 = DetailOf(window, tools["c2"]);
        Assert.False(c3.IsVisible);                                // 默认折叠:不占位
        Assert.Equal(0, c3.Progress);

        tools["c3"].IsExpanded = true;
        tools["c2"].IsExpanded = true;
        Advance(900);                                              // 减弱级别只有短淡入;足够长就一定落定

        foreach (var c in new[] { c3, c2 })
        {
            Assert.True(c.IsVisible);
            Assert.Equal(1, c.Progress);
            Assert.Equal(1, c.Opacity);
            Assert.False(c.IsMoving);
            Assert.True(c.Bounds.Height > 20);                     // 摘要真的展开了
        }

        Snapshot(window, $"tools-expanded-{level.ToString().ToLowerInvariant()}", width, dark);

        tools["c3"].IsExpanded = false;
        Advance(900);

        c3 = TryDetailOf(window, tools["c3"])!;
        Assert.False(c3.IsVisible);                                // 收起:回到完全不占位(不可见的控件不参与布局)
        Assert.Equal(0, c3.Progress);
        Assert.True(tools["c2"].IsExpanded);                       // 另一张的展开状态(ViewModel 里的纯 UI 状态)不受影响
        if (TryDetailOf(window, tools["c2"]) is { } other)         // 它若还在屏幕上,展开着
        {
            Assert.True(other.IsVisible);
            Assert.Equal(1, other.Progress);
        }

        Snapshot(window, $"tools-collapsed-{level.ToString().ToLowerInvariant()}", width, dark);
    }

    // ───────────────────────── 动画进行到一半 ─────────────────────────

    [AvaloniaTheory]
    [InlineData(1200, true)]
    [InlineData(1200, false)]
    public void A_tool_card_detail_can_be_captured_half_open(int width, bool dark)
    {
        NcwMotion.Apply(UiMotionLevel.Full);
        var (window, _, vm) = Render(ConversationScreenshotTests.Tools(), width, dark);
        var row = vm.Conversation.Rows.OfType<ToolRowViewModel>().First(t => t.Row.CallId == "c3");
        var detail = DetailOf(window, row);
        var cardHeightClosed = detail.GetVisualAncestors().OfType<ToolCard>().First().Bounds.Height;

        row.IsExpanded = true;
        Advance(70);

        Assert.True(detail.IsMoving);
        Assert.InRange(detail.Progress, 0.05, 0.95);               // 中间帧:没有一步到位
        var cardHeightMid = detail.GetVisualAncestors().OfType<ToolCard>().First().Bounds.Height;
        Assert.True(cardHeightMid > cardHeightClosed);             // 卡片已经开始变高
        Snapshot(window, "tool-half-open", width, dark);

        Advance(1200);
        var cardHeightOpen = detail.GetVisualAncestors().OfType<ToolCard>().First().Bounds.Height;
        Assert.True(cardHeightOpen > cardHeightMid);               // 之后继续长到终点
        Assert.Equal(1, detail.Progress);

        row.IsExpanded = false;                                    // 收起到一半抓一帧,再被打断(又展开)
        Advance(70);
        Assert.InRange(detail.Progress, 0.05, 0.95);
        Snapshot(window, "tool-half-closed", width, dark);
        row.IsExpanded = true;
        Advance(1500);
        Assert.Equal(1, detail.Progress);
        Assert.Equal(cardHeightOpen, detail.GetVisualAncestors().OfType<ToolCard>().First().Bounds.Height, 0.5);
    }

    // ───────────────────────── 新行出现 ─────────────────────────

    // ───────────────────────── 左右栏 ─────────────────────────

    private static T Part<T>(Control host, string name) where T : Control => host.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_sidebar_slides_out_as_an_overlay_while_the_workspace_relayouts_once_and_ends_clean(bool dark)
    {
        NcwMotion.Apply(UiMotionLevel.Full);
        var (window, shell, vm) = Render(ConversationScreenshotTests.Tools(), 1500, dark);
        var left = Part<Grid>(shell, "LeftArea");
        var main = Part<Grid>(shell, "Main");
        Assert.True(shell.IsSidebarVisible);
        var width = left.Bounds.Width;

        vm.ToggleSidebarCommand.Execute(null);
        Pump();
        Advance(70);

        Assert.False(shell.IsSidebarVisible);                    // 逻辑上已收起
        Assert.True(left.IsVisible);                              // 但还在滑出(留在原处当覆盖层)
        Assert.Equal(0, main.ColumnDefinitions[0].ActualWidth);   // 工作区一次到位,不随动画逐帧重排
        var slide = Assert.IsType<TranslateTransform>(left.RenderTransform);
        Assert.InRange(slide.X, -width, -1);                      // 位移只动 RenderTransform
        Assert.Equal(1, left.Opacity);                            // 整幅滑动的面板只滑不淡(半透明压在内容上文字会叠在一起)
        Snapshot(window, "sidebar-sliding-out", 1500, dark);

        Advance(900);
        Assert.False(left.IsVisible);
        Assert.Null(left.RenderTransform);
        Assert.Equal(1, left.Opacity);
        Assert.Equal(1, Grid.GetColumnSpan(left));                 // 覆盖层形态已复原

        vm.ToggleSidebarCommand.Execute(null);                    // 再展开:列宽立即就位,栏自己从左边滑入
        Pump();
        Advance(60);
        Assert.True(shell.IsSidebarVisible);
        Assert.InRange(Assert.IsType<TranslateTransform>(left.RenderTransform).X, -width, -1);
        Advance(900);
        Assert.Null(left.RenderTransform);
        Assert.Equal(1, left.Opacity);
        Assert.True(left.Bounds.Width > 300);
    }

    [AvaloniaFact]
    public void Reduced_motion_replaces_the_sidebar_slide_with_a_short_fade()
    {
        NcwMotion.Apply(UiMotionLevel.Reduced);
        var (_, shell, vm) = Render(ConversationScreenshotTests.Tools(), 1500, true);
        var left = Part<Grid>(shell, "LeftArea");

        vm.ToggleSidebarCommand.Execute(null);                    // 减弱:收起没有过渡
        Pump();
        Assert.False(left.IsVisible);
        Assert.Null(left.RenderTransform);

        vm.ToggleSidebarCommand.Execute(null);                    // 展开:只有短淡入,没有位移
        Pump();
        Advance(40);
        Assert.True(left.IsVisible);
        Assert.Null(left.RenderTransform);
        Assert.InRange(left.Opacity, 0.01, 0.99);
        Advance(400);
        Assert.Equal(1, left.Opacity);
        Assert.Equal(352, Part<Grid>(shell, "Main").ColumnDefinitions[0].Width.Value);
    }

    [AvaloniaFact]
    public void Toggling_the_sidebar_again_mid_slide_reverses_from_where_it_is_and_never_gets_stuck()
    {
        NcwMotion.Apply(UiMotionLevel.Full);
        var (_, shell, vm) = Render(ConversationScreenshotTests.Tools(), 1500, true);
        var left = Part<Grid>(shell, "LeftArea");

        vm.ToggleSidebarCommand.Execute(null);
        Advance(50);
        var midX = Assert.IsType<TranslateTransform>(left.RenderTransform).X;
        vm.ToggleSidebarCommand.Execute(null);                    // 滑出到一半又点开
        Advance(16);
        Assert.True(Assert.IsType<TranslateTransform>(left.RenderTransform).X >= midX - 1);   // 从当前位置接着走,不跳回起点
        Advance(900);

        Assert.True(shell.IsSidebarVisible);
        Assert.True(left.IsVisible);
        Assert.Null(left.RenderTransform);
        Assert.Equal(1, left.Opacity);
        Assert.Equal(1, Grid.GetColumnSpan(left));
        Assert.Equal(352, Part<Grid>(shell, "Main").ColumnDefinitions[0].Width.Value);
    }

    [AvaloniaFact]
    public void The_inspector_slides_out_to_the_right_and_comes_back()
    {
        NcwMotion.Apply(UiMotionLevel.Full);
        var (_, shell, vm) = Render(ConversationScreenshotTests.Tools(), 1500, true);
        var column = Part<Border>(shell, "InspectorColumn");
        Assert.True(shell.IsInspectorVisible);

        vm.ToggleInspectorCommand.Execute(null);
        Advance(70);
        Assert.False(shell.IsInspectorVisible);
        Assert.True(column.IsVisible);
        Assert.InRange(Assert.IsType<TranslateTransform>(column.RenderTransform).X, 1, 600);   // 向右滑出
        Advance(900);
        Assert.False(column.IsVisible);
        Assert.Null(column.RenderTransform);
        Assert.Equal(4, Grid.GetColumn(column));

        vm.ToggleInspectorCommand.Execute(null);
        Advance(900);
        Assert.True(shell.IsInspectorVisible);
        Assert.Null(column.RenderTransform);
        Assert.Equal(1, column.Opacity);
    }

    [AvaloniaTheory]
    [InlineData(1200)]
    [InlineData(1024)]
    [InlineData(900)]
    public void Below_1440_the_inspector_opens_as_a_right_drawer_with_a_scrim_and_never_touches_the_docked_preference(double width)
    {
        NcwMotion.Apply(UiMotionLevel.Off);
        var (window, shell, vm) = Render(ConversationScreenshotTests.Tools(), width, true);
        var column = Part<Border>(shell, "InspectorColumn");
        var scrim = Part<Border>(shell, "DrawerScrim");
        var dockedPreference = vm.IsInspectorOpen;
        Assert.False(shell.IsInspectorVisible);                      // 默认不占位

        vm.ToggleInspectorCommand.Execute(null);                     // 右上角的检查器开关 / ⌘⌥I
        Pump();

        Assert.True(vm.IsInspectorNarrow);
        Assert.True(shell.IsInspectorDrawerVisible);
        Assert.True(shell.IsInspectorVisible);
        Assert.True(scrim.IsVisible);
        Assert.Equal(dockedPreference, vm.IsInspectorOpen);          // 停靠偏好不变(宽窗口下次照旧)
        Assert.Equal(HorizontalAlignment.Right, column.HorizontalAlignment);
        Assert.Equal(UiPrefs.InspectorDefaultWidth, column.Bounds.Width);
        Assert.True(column.Bounds.Right >= window.Bounds.Width - 1); // 贴在窗口右边,盖在工作区上
        Assert.Equal(0, Part<Grid>(shell, "Main").ColumnDefinitions[4].ActualWidth);   // 工作区没有被挤窄
        Snapshot(window, "inspector-drawer", width, true);

        window.MouseDown(new Point(60, 400), MouseButton.Left);      // 点遮罩关闭
        window.MouseUp(new Point(60, 400), MouseButton.Left);
        Pump();
        Assert.False(shell.IsInspectorDrawerVisible);
        Assert.False(vm.IsInspectorDrawerOpen);
        Assert.False(shell.IsInspectorVisible);
        Assert.False(scrim.IsVisible);

        vm.ToggleInspectorCommand.Execute(null);
        Pump();
        window.Width = 1500;                                         // 宽到能停靠:抽屉状态收掉,按偏好停靠
        Pump();
        Assert.False(vm.IsInspectorNarrow);
        Assert.False(vm.IsInspectorDrawerOpen);
        Assert.False(shell.IsInspectorDrawerVisible);
        Assert.Equal(dockedPreference, shell.IsInspectorVisible);
    }

    [AvaloniaFact]
    public void The_inspector_drawer_slides_in_from_the_right_under_full_motion()
    {
        NcwMotion.Apply(UiMotionLevel.Full);
        var (_, shell, vm) = Render(ConversationScreenshotTests.Tools(), 1200, true);
        var column = Part<Border>(shell, "InspectorColumn");

        vm.ToggleInspectorCommand.Execute(null);
        Advance(70);
        Assert.True(column.IsVisible);
        Assert.InRange(Assert.IsType<TranslateTransform>(column.RenderTransform).X, 1, 600);
        Advance(900);
        Assert.Null(column.RenderTransform);

        vm.ToggleInspectorCommand.Execute(null);
        Advance(70);
        Assert.True(column.IsVisible);                                // 滑出中
        Advance(900);
        Assert.False(column.IsVisible);
        Assert.Null(column.RenderTransform);
        Assert.Equal(4, Grid.GetColumn(column));
    }

    [AvaloniaFact]
    public void Crossing_a_breakpoint_by_resizing_the_window_does_not_animate_the_panels()
    {
        NcwMotion.Apply(UiMotionLevel.Full);
        var (window, shell, _) = Render(ConversationScreenshotTests.Tools(), 1500, true);
        var left = Part<Grid>(shell, "LeftArea");

        window.Width = 900;                                       // 缩窗口:跨过断点,左侧区域收成抽屉形态
        Pump();
        Advance(16);

        Assert.False(shell.IsSidebarVisible);
        Assert.False(left.IsVisible);                              // 立即消失,不是滑出
        Assert.Null(left.RenderTransform);
    }

    [AvaloniaFact]
    public void The_narrow_window_drawer_and_its_scrim_animate_in_and_out()
    {
        NcwMotion.Apply(UiMotionLevel.Full);
        var (window, shell, vm) = Render(ConversationScreenshotTests.Tools(), 900, true);
        var scrim = Part<Border>(shell, "DrawerScrim");
        var left = Part<Grid>(shell, "LeftArea");
        Assert.False(scrim.IsVisible);

        vm.ToggleSidebarCommand.Execute(null);
        Advance(60);
        Assert.True(shell.IsDrawerVisible);
        Assert.True(scrim.IsVisible);
        Assert.InRange(scrim.Opacity, 0.01, 0.99);                 // 遮罩淡入
        Assert.Null(scrim.RenderTransform);                         // 遮罩只淡不动
        Assert.InRange(Assert.IsType<TranslateTransform>(left.RenderTransform).X, -400, -1);   // 抽屉从左滑入
        Snapshot(window, "drawer-opening", 900, true);

        Advance(900);
        Assert.Equal(1, scrim.Opacity);
        Assert.Null(left.RenderTransform);

        vm.CloseDrawerCommand.Execute(null);
        Advance(60);
        Assert.True(scrim.IsVisible);                               // 淡出中,点击已经被取消的遮罩前仍在
        Advance(900);
        Assert.False(scrim.IsVisible);
        Assert.False(left.IsVisible);
        Assert.False(shell.IsDrawerVisible);
        Assert.Equal(1, scrim.Opacity);
    }

    // ───────────────────────── 设置覆盖层与外观页 ─────────────────────────

    [AvaloniaFact]
    public void The_settings_overlay_fades_in_and_out_and_hides_only_after_the_exit()
    {
        NcwMotion.Apply(UiMotionLevel.Full);
        var (window, shell, vm) = Render(ConversationScreenshotTests.Tools(), 1200, true);
        var overlay = window.GetVisualDescendants().OfType<SettingsOverlay>().Single();
        Assert.False(overlay.IsVisible);

        vm.Settings.OpenCommand.Execute(null);
        Advance(60);
        Assert.True(overlay.IsVisible);
        Assert.InRange(overlay.Opacity, 0.01, 0.99);
        Advance(600);
        Assert.Equal(1, overlay.Opacity);

        vm.Settings.CloseCommand.Execute(null);
        Advance(60);
        Assert.True(overlay.IsVisible);                            // 退出中
        Advance(600);
        Assert.False(overlay.IsVisible);
        Assert.Equal(1, overlay.Opacity);
        Assert.NotNull(shell);
    }

    [AvaloniaFact]
    public void The_appearance_page_changes_the_prefs_store_and_the_motion_level_follows()
    {
        var stores = ConversationScreenshotTests.Tools();
        using var bind = NcwMotion.Bind(stores);
        var (window, _, vm) = Render(stores, 1200, true);
        vm.Settings.OpenCommand.Execute(null);
        vm.Settings.ShowCommand.Execute(SettingsSection.Appearance);
        Advance(600);
        var page = window.GetVisualDescendants().OfType<AppearanceSettingsPage>().Single();
        Assert.True(page.IsEffectivelyVisible);

        vm.Settings.Appearance.Motion = UiMotionPreference.Off;
        Pump();
        Assert.Equal(ArcMotionLevel.Off, NcwMotion.EffectiveLevel(page));

        vm.Settings.Appearance.Motion = UiMotionPreference.Reduced;
        Pump();
        Assert.Equal(ArcMotionLevel.Reduced, NcwMotion.EffectiveLevel(page));
        Snapshot(window, "appearance", 1200, true);
    }
}
