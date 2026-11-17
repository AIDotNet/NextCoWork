using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Ncw.Client.Mock;
using Ncw.Client.ViewModels;
using Ncw.Core.Layout;
using Ncw.UI.Chrome;
using Ncw.UI.Views.Desktop;
using Ncw.UI.Views.Desktop.Tree;
using TKCode.Arc;

namespace Ncw.UI.Tests;

/// <summary>在真实控件上操作:拖分隔条、右键菜单、侧边栏拖放、浅色主题。</summary>
public class InteractionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(8));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private static (Window Window, DesktopShell Shell, ShellViewModel Vm) Render(double width = 1500)
    {
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
        for (var i = 0; i < 3; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static Point Center(Visual target, Visual root) =>
        target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), root)
        ?? throw new InvalidOperationException("not in the tree");

    private static void Snapshot(Window window, string name)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    [AvaloniaFact]
    public void Dragging_the_splitters_resizes_and_clamps_columns()
    {
        var (window, shell, vm) = Render();
        var splitter = shell.FindControl<GridSplitter>("LeftSplitter")!;
        var start = Center(splitter, window);

        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Point(80, 0), RawInputModifiers.LeftMouseButton);
        window.MouseUp(start + new Point(80, 0), MouseButton.Left);
        Dispatch();
        Assert.InRange(vm.SidebarWidth, Ncw.Client.Stores.UiPrefs.SidebarDefaultWidth + 70, Ncw.Client.Stores.UiPrefs.SidebarDefaultWidth + 90);

        // 超过最大宽度会被夹住
        start = Center(splitter, window);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Point(400, 0), RawInputModifiers.LeftMouseButton);
        window.MouseUp(start + new Point(400, 0), MouseButton.Left);
        Dispatch();
        Assert.Equal(Ncw.Client.Stores.UiPrefs.SidebarMaxWidth, vm.SidebarWidth);
        Snapshot(window, "resize-sidebar-max");
    }

    private static SidebarProjectNode Node(Control root, string name) =>
        root.GetVisualDescendants().OfType<SidebarProjectNode>().First(n => n.DataContext is SidebarProjectViewModel { Tile.Name: var n2 } && n2 == name);

    [AvaloniaFact]
    public void Right_click_on_a_project_opens_menu_and_new_group_wraps_it()
    {
        var (window, shell, vm) = Render();
        var at = Center(Node(shell, "NextCoWork").HeaderRow, window);

        window.MouseDown(at, MouseButton.Right);
        window.MouseUp(at, MouseButton.Right);
        Dispatch();
        Snapshot(window, "sidebar-context-menu");

        var entry = window.GetVisualDescendants()
            .Concat(window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>()
                .Where(p => p.Child is not null).SelectMany(p => p.Child!.GetVisualDescendants().Prepend(p.Child!)))
            .OfType<MenuEntry>()
            .FirstOrDefault(e => (e.Header as string) == "新建项目组");
        Assert.NotNull(entry);
        entry.Command!.Execute(entry.CommandParameter);
        Dispatch();

        var project = vm.Rail.Items.OfType<RailGroupViewModel>().First().Members.Single();
        Assert.Equal("NextCoWork", project.Name);
        Assert.True(project.IsInGroup);
        // 侧边栏跟着变:NextCoWork 现在在一个组的标题下面
        Assert.Contains(vm.Sidebar.Items.OfType<SidebarGroupViewModel>(), g => g.Members.Any(m => m.Tile.Name == "NextCoWork"));
    }

    [AvaloniaFact]
    public void Drop_plan_uses_real_row_positions()
    {
        var (window, shell, vm) = Render();
        var sidebar = shell.GetVisualDescendants().OfType<Sidebar>().Single();
        var ncw = Node(sidebar, "NextCoWork").HeaderRow;
        var octopus = vm.Sidebar.Items.OfType<SidebarGroupViewModel>().SelectMany(g => g.Members).First(m => m.Tile.Name == "Octopus").Project.ToString();

        // 拖 Octopus(组内)到 NextCoWork(顶层)标题行的正中:两者新建一组
        var middle = Center(ncw, sidebar);
        Assert.IsType<RailDrop.GroupWith>(sidebar.Plan("project", octopus, ncw, middle));

        // 拖到 NextCoWork 标题行的上沿:放到它前面(移出原来的组)
        var top = ncw.TranslatePoint(new Point(ncw.Bounds.Width / 2, 2), sidebar)!.Value;
        var plan = sidebar.Plan("project", octopus, ncw, top);
        vm.Sidebar.Drop(plan);
        Dispatch();

        var first = Assert.IsType<SidebarProjectViewModel>(vm.Sidebar.Items[0]);
        Assert.Equal("Octopus", first.Tile.Name);
        Assert.False(first.Tile.IsInGroup);

        // 指针在展开出来的会话上(不是标题行)= 放到这个项目(连同会话)后面
        var session = Node(sidebar, "NextCoWork").GetVisualDescendants().OfType<Ncw.UI.Views.Desktop.Conversation.SessionRow>().First();
        var onSession = Center(session, sidebar);
        var after = Assert.IsType<RailDrop.MoveProject>(sidebar.Plan("project", octopus, session, onSession));
        Assert.IsType<RailTarget.TopLevel>(after.Target);
        Snapshot(window, "sidebar-after-drop");
    }

    [AvaloniaFact]
    public void Light_theme_renders()
    {
        ArcTheme.Apply(t => t.Appearance(ArcAppearance.Light));
        try
        {
            var (window, _, _) = Render();
            Snapshot(window, "shell-1500-light");
        }
        finally
        {
            ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark));
        }
    }

    [AvaloniaFact]
    public void Narrow_window_opens_sidebar_as_drawer_and_scrim_closes_it()
    {
        var (window, shell, vm) = Render(900);
        Assert.False(shell.IsSidebarVisible);

        vm.ToggleSidebarCommand.Execute(null);
        Dispatch();
        Assert.True(shell.IsDrawerVisible);
        Assert.True(shell.IsSidebarVisible);
        Snapshot(window, "shell-900-drawer");

        // 点遮罩(抽屉右边的工作区位置)关闭
        window.MouseDown(new Point(800, 400), MouseButton.Left);
        window.MouseUp(new Point(800, 400), MouseButton.Left);
        Dispatch();
        Assert.False(shell.IsDrawerVisible);
        Assert.False(vm.IsDrawerOpen);
    }

    [AvaloniaFact]
    public async Task Rename_group_shows_arc_dialog_and_saves()
    {
        var (window, _, vm) = Render();
        var group = vm.Rail.Items.OfType<RailGroupViewModel>().First();

        var pending = group.RenameCommand.ExecuteAsync(null);
        Dispatch();

        var popupVisuals = window.GetVisualDescendants()
            .Concat(window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>()
                .Where(p => p.Child is not null).SelectMany(p => p.Child!.GetVisualDescendants()))
            .ToList();
        var input = popupVisuals.OfType<Input>().Single();
        Assert.Equal("工作", input.Text);
        Snapshot(window, "rename-dialog");

        input.Text = "前端";
        var save = popupVisuals.OfType<TKCode.Arc.Button>().First(b => b.Content as string == "保存" && b.IsEffectivelyVisible); // 检查器里(文件预览的)「保存」按钮此刻是隐藏的,不能点它
        Click(window, save);
        await pending;

        Assert.Equal("前端", vm.Rail.Items.OfType<RailGroupViewModel>().First().Name);
    }

    [AvaloniaFact]
    public void English_ui_follows_language_switch()
    {
        TKCode.Arc.Localization.ArcLanguage.Set(TKCode.Arc.Localization.LanguageTags.EnUS);
        try
        {
            var (window, shell, vm) = Render();
            var texts = shell.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToHashSet();

            Assert.Contains("Projects", texts);   // 侧边栏项目树的标题
            Assert.Contains("Inbox", texts);
            Assert.Contains("2 pending", texts);  // 会话行的待处理数
            Assert.Contains("Files", texts); // 右侧面板选中的页签
            Assert.Contains("3 min ago", texts);
            Assert.DoesNotContain("会话", texts);
            Snapshot(window, "shell-1500-en");

            vm.Inspector.ActivateCommand.Execute(InspectorTabKind.Subagents);
            Dispatch();
            texts = shell.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToHashSet();
            Assert.Contains("Running", texts);
            Assert.Contains("Grep · 14 tool calls · 2:00", texts);
        }
        finally
        {
            TKCode.Arc.Localization.ArcLanguage.Set(TKCode.Arc.Localization.LanguageTags.ZhCN);
        }
    }

    private static void Click(Window window, Visual target)
    {
        var at = Center(target, window);
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatch();
    }
}
