using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Ncw.Client.Mock;
using Ncw.Client.ViewModels;
using Ncw.UI.Chrome;
using Ncw.UI.Views.Desktop;
using Ncw.UI.Views.Desktop.Conversation;
using Ncw.UI.Views.Desktop.Tree;
using TKCode.Arc;

namespace Ncw.UI.Tests;

/// <summary>
/// 左侧边栏(docs/design/03 §1「左侧边栏」):全局入口 + 项目树 + 账户;⌘B 完全收起。
/// 截图在 <c>screenshots/sidebar-*.png</c>。
/// </summary>
public class SidebarUiTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(8));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private static (Window Window, DesktopShell Shell, ShellViewModel Vm) Render(bool dark = true, double width = 1500)
    {
        ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
        Ncw.UI.Converters.RelativeTimeConverter.Clock = new FixedClock(Now);
        var vm = new ShellViewModel(MockWorkspace.Create(Now), new FixedClock(Now));
        var shell = new DesktopShell { DataContext = vm, Chrome = new WindowChrome(78, false) };
        var window = new Window { Width = width, Height = 860, Content = shell };
        window.Show();
        Dispatch();
        return (window, shell, vm);
    }

    private static void Dispatch()
    {
        for (var i = 0; i < 4; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Snapshot(Window window, string name)
    {
        Dispatch();
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static void Click(Window window, Visual target)
    {
        var at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatch();
    }

    private static SidebarProjectNode Node(Control root, string name) =>
        root.GetVisualDescendants().OfType<SidebarProjectNode>().First(n => n.DataContext is SidebarProjectViewModel p && p.Tile.Name == name);

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Renders_entries_tree_and_account_in_one_column(bool dark)
    {
        var (window, shell, _) = Render(dark);
        try
        {
            var sidebar = shell.GetVisualDescendants().OfType<Sidebar>().Single();
            Assert.True(sidebar.IsEffectivelyVisible);

            // 原来的两栏不在了:项目栏的 40px 图标格、项目面板的搜索框都没有
            Assert.DoesNotContain(shell.GetVisualDescendants().OfType<Control>(), c => c.GetType().Name is "ProjectRail" or "ProjectPanel");

            // 当前项目展开着,当前会话高亮,行尾是「2 待处理」
            var ncw = Node(sidebar, "NextCoWork");
            var rows = ncw.GetVisualDescendants().OfType<SessionRow>().Where(r => r.IsEffectivelyVisible).ToList();
            Assert.Equal(3, rows.Count);
            var open = rows.Single(r => r.DataContext is SessionItemViewModel { IsSelected: true });
            Assert.Contains(open.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == (dark ? "2 待处理" : "2 待处理"));

            // 收起的组里的项目看不见;别的项目没展开,不建会话行
            Assert.DoesNotContain(sidebar.GetVisualDescendants().OfType<SidebarProjectNode>(), n => n.DataContext is SidebarProjectViewModel { Tile.Name: "infra" } && n.IsEffectivelyVisible);
            Snapshot(window, $"sidebar-{(dark ? "dark" : "light")}");
        }
        finally
        {
            ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark));
        }
    }

    [AvaloniaFact]
    public void Clicking_a_project_row_selects_and_expands_it()
    {
        var (window, shell, vm) = Render();
        var octopus = Node(shell, "Octopus");
        Click(window, octopus.FindControl<Control>("RowButton")!);

        Assert.Equal("octopus", vm.Stores.Selection.State.Project?.WorkspaceId);
        Assert.True(((SidebarProjectViewModel)octopus.DataContext!).IsExpanded);
        Assert.Contains(octopus.GetVisualDescendants().OfType<SessionRow>(), r => r.IsEffectivelyVisible);
        Snapshot(window, "sidebar-two-expanded");
    }

    [AvaloniaFact]
    public void Inbox_lists_what_needs_you_in_place_and_opens_it()
    {
        var (window, shell, vm) = Render();
        var sidebar = shell.GetVisualDescendants().OfType<Sidebar>().Single();

        Click(window, sidebar.FindControl<Control>("InboxButton")!);
        Assert.True(vm.Sidebar.IsInboxOpen);
        Snapshot(window, "sidebar-inbox");

        // 数字和 Dock 角标同一份(收件箱的 BadgeCount)
        Assert.Equal(vm.Inbox.BadgeCount, vm.Sidebar.InboxCount);
        if (vm.Sidebar.InboxItems.FirstOrDefault() is { } first)
        {
            vm.Sidebar.OpenInboxItemCommand.Execute(first);
            Assert.Equal(first.Entry.SessionId, vm.Stores.Selection.State.SessionId);
        }
    }

    [AvaloniaFact]
    public void Collapsing_hides_the_sidebar_completely()
    {
        var (window, shell, vm) = Render();
        vm.ToggleSidebarCommand.Execute(null);
        Dispatch();
        Assert.False(vm.IsSidebarOpen);

        // 完全收起:左侧没有任何残留的图标栏,工作区从窗口左边开始
        var main = shell.FindControl<Grid>("Main")!;
        Assert.Equal(0, main.ColumnDefinitions[0].Width.Value);
        Assert.False(shell.GetVisualDescendants().OfType<Sidebar>().Single().IsEffectivelyVisible);
        Snapshot(window, "sidebar-collapsed");
    }

    [AvaloniaFact]
    public void Hovering_a_project_row_swaps_status_for_new_session_and_more()
    {
        var (window, shell, _) = Render();
        var api = Node(shell, "api-server");
        var header = api.HeaderRow;
        var hover = header.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "HoverActions");
        Assert.False(hover.IsVisible);

        var at = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
        window.MouseMove(at);
        Dispatch();
        Assert.True(hover.IsVisible);
        Snapshot(window, "sidebar-hover");
    }
}
