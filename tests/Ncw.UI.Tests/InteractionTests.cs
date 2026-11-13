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
using Ncw.UI.Views.Desktop.Rail;
using TKCode.Arc;

namespace Ncw.UI.Tests;

/// <summary>在真实控件上操作:拖分隔条、右键菜单、项目栏拖放、浅色主题。</summary>
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
        Assert.InRange(vm.SidebarWidth, 352 + 70, 352 + 90);

        // 超过最大宽度会被夹住
        start = Center(splitter, window);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Point(400, 0), RawInputModifiers.LeftMouseButton);
        window.MouseUp(start + new Point(400, 0), MouseButton.Left);
        Dispatch();
        Assert.Equal(Ncw.Client.Stores.UiPrefs.SidebarMaxWidth, vm.SidebarWidth);
        Snapshot(window, "resize-sidebar-max");
    }

    [AvaloniaFact]
    public void Right_click_on_a_project_opens_menu_and_new_group_wraps_it()
    {
        var (window, shell, vm) = Render();
        var tile = shell.GetVisualDescendants().OfType<RailProjectTile>()
            .First(t => t.DataContext is RailProjectViewModel { Name: "NextCoWork" });
        var at = Center(tile, window);

        window.MouseDown(at, MouseButton.Right);
        window.MouseUp(at, MouseButton.Right);
        Dispatch();
        Snapshot(window, "rail-context-menu");

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
    }

    [AvaloniaFact]
    public void Drop_plan_uses_real_tile_positions()
    {
        var (window, shell, vm) = Render();
        var rail = shell.GetVisualDescendants().OfType<ProjectRail>().Single();
        var tiles = rail.GetVisualDescendants().OfType<RailProjectTile>().ToList();
        var ncw = tiles.First(t => t.DataContext is RailProjectViewModel { Name: "NextCoWork" });
        var octopus = tiles.First(t => t.DataContext is RailProjectViewModel { Name: "Octopus" });

        // 拖 Octopus(组内)到 NextCoWork(顶层)的正中:两者新建一组
        var middle = Center(ncw, rail);
        var plan = rail.Plan("project", ((RailProjectViewModel)octopus.DataContext!).Project.ToString(), ncw, middle);
        Assert.IsType<RailDrop.GroupWith>(plan);

        // 拖到 NextCoWork 的上沿:放到它前面(移出原来的组)
        var top = ncw.TranslatePoint(new Point(ncw.Bounds.Width / 2, 3), rail)!.Value;
        plan = rail.Plan("project", ((RailProjectViewModel)octopus.DataContext!).Project.ToString(), ncw, top);
        vm.Rail.Drop(plan);
        Dispatch();

        var first = Assert.IsType<RailProjectViewModel>(vm.Rail.Items[0]);
        Assert.Equal("Octopus", first.Name);
        Assert.False(first.IsInGroup);
        Snapshot(window, "rail-after-drop");
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
        var save = popupVisuals.OfType<TKCode.Arc.Button>().First(b => b.Content as string == "保存");
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

            Assert.Contains("Sessions", texts);
            Assert.Contains("Terminals & agents", texts);
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
