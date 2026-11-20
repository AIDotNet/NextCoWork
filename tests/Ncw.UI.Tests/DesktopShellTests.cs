using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ncw.Client.Mock;
using Ncw.Client.ViewModels;
using Ncw.UI.Chrome;
using Ncw.UI.Views.Desktop;

namespace Ncw.UI.Tests;

public class DesktopShellTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(8));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private static (Window Window, DesktopShell Shell, ShellViewModel Vm) Render(double width, double height = 860, WindowChrome? chrome = null)
    {
        Ncw.UI.Converters.RelativeTimeConverter.Clock = new FixedClock(Now);
        var vm = new ShellViewModel(MockWorkspace.Create(Now), new FixedClock(Now));
        // 截图按 macOS 外框(左上角给红绿灯留 78px);另有一个用例覆盖 Windows / Linux 的窗口按钮。
        var shell = new DesktopShell { DataContext = vm, Chrome = chrome ?? new WindowChrome(78, false) };
        var window = new Window { Width = width, Height = height, Content = shell };
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

    private static void Snapshot(Window window, string name)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }


    [AvaloniaTheory]
    [InlineData(1500, ShellBreakpoint.Wide, true, true)]
    [InlineData(1200, ShellBreakpoint.Medium, true, false)]
    [InlineData(900, ShellBreakpoint.Narrow, false, false)]
    public void Columns_follow_breakpoints(double width, ShellBreakpoint expected, bool sidebar, bool inspector)
    {
        var (window, shell, _) = Render(width);

        Assert.Equal(expected, shell.Breakpoint);
        Assert.Equal(sidebar, shell.IsSidebarVisible);
        Assert.Equal(inspector, shell.IsInspectorVisible);
        Snapshot(window, $"shell-{width}");
    }

    [AvaloniaFact]
    public void Toggles_hide_sidebar_and_inspector()
    {
        var (window, shell, vm) = Render(1500);

        vm.ToggleSidebarCommand.Execute(null);
        vm.ToggleInspectorCommand.Execute(null);
        Dispatch();

        Assert.False(shell.IsSidebarVisible);
        Assert.False(shell.IsInspectorVisible);
        Snapshot(window, "shell-1500-collapsed");
    }

    [AvaloniaFact]
    public void Windows_and_linux_chrome_draws_caption_buttons()
    {
        var (window, shell, _) = Render(1500, chrome: new WindowChrome(0, true));

        Assert.True(shell.FindControl<Ncw.UI.Chrome.CaptionButtons>("Captions")!.IsVisible);
        Snapshot(window, "shell-1500-windows-chrome");
    }

    [AvaloniaFact]
    public void Rail_shows_groups_and_selection_from_stores()
    {
        var (window, _, vm) = Render(1500);

        var groups = vm.Rail.Items.OfType<RailGroupViewModel>().ToList();
        Assert.Equal(2, groups.Count);
        Assert.False(groups[0].IsCollapsed);
        Assert.True(groups[1].IsCollapsed);

        var selected = vm.Rail.Items.OfType<RailProjectViewModel>().Single(p => p.IsSelected);
        Assert.Equal("NextCoWork", selected.Name);
        Assert.Equal(2, selected.PendingCount);

        // 展开收起的组;组的角标来自成员之和。
        groups[1].ToggleCommand.Execute(null);
        Dispatch();
        Assert.False(groups[1].IsCollapsed);
        Snapshot(window, "shell-1500-groups-expanded");
    }

    [AvaloniaFact]
    public void Selecting_a_project_updates_panel_and_tabs()
    {
        var (window, _, vm) = Render(1500);
        var work = vm.Rail.Items.OfType<RailGroupViewModel>().First();
        var api = work.Members.Single(m => m.Name == "api-server");

        api.SelectCommand.Execute(null);
        Dispatch();

        Assert.Equal("api-server", vm.Panel.Name);
        Assert.True(vm.Panel.IsSsh);
        Assert.Contains(vm.Panel.Sessions, s => s.IsRunning);
        Assert.True(api.IsSelected);
        Assert.True(work.ContainsSelection);
        Snapshot(window, "shell-1500-ssh-project");
    }

    [AvaloniaFact]
    public void Right_panel_defaults_to_file_manager()
    {
        var (window, shell, vm) = Render(1500);

        Assert.True(vm.Inspector.IsFiles);
        Assert.Contains(shell.GetVisualDescendants().OfType<Ncw.UI.Controls.NcwTreeView>(), t => t.Name == "Tree" && t.IsEffectivelyVisible);
        Snapshot(window, "panel-files");
    }

    [AvaloniaFact]
    public void Plus_adds_git_and_subagents_appear_with_the_session()
    {
        var (window, _, vm) = Render(1500);

        vm.Inspector.AddCommand.Execute(Ncw.Client.ViewModels.InspectorTabKind.Git);
        Dispatch();
        Snapshot(window, "panel-git");

        vm.Inspector.ActivateCommand.Execute(Ncw.Client.ViewModels.InspectorTabKind.Subagents);
        Dispatch();
        Assert.True(vm.Inspector.IsSubagents);
        Snapshot(window, "panel-subagents");
    }

    /// <summary>真的在「+」上点一下:菜单打开、点选 Git 后页签出现。</summary>
    [AvaloniaFact]
    public void Clicking_plus_opens_menu_and_choosing_git_adds_the_tab()
    {
        var (window, shell, vm) = Render(1500);
        var panel = shell.FindControl<InspectorPanel>("Inspector")!;
        var menu = panel.FindControl<Ncw.UI.Controls.NcwDropdownMenu>("AddMenu")!;

        Click(window, menu);
        Assert.True(menu.IsOpen);
        Snapshot(window, "panel-plus-menu");

        var entry = window.GetVisualDescendants().Concat(TopLevelPopups(window))
            .OfType<TKCode.Arc.MenuEntry>()
            .First(e => (e.Header as string) == "Git");
        Click(window, entry);

        Assert.True(vm.Inspector.IsGit);
    }

    /// <summary>右上角「⋯」:当前会话的操作 + 全局命令;点「命令面板」走和 ⌘K 同一个入口。</summary>
    [AvaloniaFact]
    public void Clicking_more_opens_session_and_global_actions_and_runs_the_command()
    {
        var (window, shell, vm) = Render(1500);
        var workspace = shell.FindControl<WorkspaceArea>("Workspace")!;
        var menu = workspace.FindControl<Ncw.UI.Controls.NcwDropdownMenu>("MoreMenu")!;
        Assert.NotNull(vm.Panel.ActiveSession);

        Click(window, menu);
        Assert.True(menu.IsOpen);
        Snapshot(window, "workspace-more-menu");

        var entries = window.GetVisualDescendants().Concat(TopLevelPopups(window)).OfType<TKCode.Arc.MenuEntry>().ToList();
        var headers = entries.Select(e => e.Header as string).ToList();
        Assert.Contains(Ncw.UI.Localization.NcwTexts.Get(Ncw.UI.Localization.NcwTextKind.Rename), headers);
        Assert.Contains(Ncw.UI.Localization.NcwTexts.Get(Ncw.UI.Localization.NcwTextKind.DeleteSession), headers);
        var paletteTitle = Ncw.UI.Controls.ShortcutText.Title(Ncw.Client.Shortcuts.AppCommand.CommandPalette);
        Click(window, entries.First(e => (e.Header as string) == paletteTitle));

        Assert.True(vm.Palette.IsOpen);
    }

    /// <summary>侧边栏底部的主题切换按钮:点一次切到浅色,再点一次切回深色,偏好和 Arc 主题同步。</summary>
    [AvaloniaFact]
    public void Clicking_theme_switcher_toggles_appearance_preference()
    {
        var (window, shell, vm) = Render(1500);
        try
        {
            Ncw.UI.Controls.NcwAppearance.Apply(Ncw.Client.Stores.UiThemePreference.Dark);
            Dispatch();
            var switcher = shell.GetVisualDescendants().OfType<Ncw.UI.Controls.NcwThemeSwitcher>().Single();

            switcher.Toggle();
            Dispatch();
            Assert.Equal(Ncw.Client.Stores.UiThemePreference.Light, vm.Settings.Appearance.Theme);

            switcher.Toggle();
            Dispatch();
            Assert.Equal(Ncw.Client.Stores.UiThemePreference.Dark, vm.Settings.Appearance.Theme);
        }
        finally
        {
            Ncw.UI.Controls.NcwAppearance.Apply(Ncw.Client.Stores.UiThemePreference.Dark);
        }
    }

    /// <summary>右侧面板「+」菜单的每一项都要有非空图标。</summary>
    [AvaloniaFact]
    public void Inspector_add_menu_items_all_have_icons()
    {
        var (window, shell, _) = Render(1500);
        var panel = shell.FindControl<InspectorPanel>("Inspector")!;
        var entries = panel.FindControl<Ncw.UI.Controls.NcwDropdownMenu>("AddMenu")!.ItemsSource!
            .OfType<TKCode.Arc.MenuEntry>()
            .ToList();
        Assert.NotEmpty(entries);
        Assert.All(entries, entry =>
        {
            var geometry = Assert.IsAssignableFrom<Avalonia.Media.Geometry>(entry.Icon);
            Assert.False(geometry.Bounds.Width <= 0 && geometry.Bounds.Height <= 0);
        });
    }

    private static IEnumerable<Avalonia.Visual> TopLevelPopups(Window window) =>
        window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>()
            .Where(p => p.Child is not null)
            .SelectMany(p => p.Child!.GetVisualDescendants().Prepend(p.Child!));

    private static void Click(Window window, Avalonia.Visual target)
    {
        var center = target.TranslatePoint(new Avalonia.Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)
                     ?? throw new InvalidOperationException("target is not in the window");
        window.MouseMove(center);
        window.MouseDown(center, Avalonia.Input.MouseButton.Left);
        window.MouseUp(center, Avalonia.Input.MouseButton.Left);
        Dispatch();
    }
}
