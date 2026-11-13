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
        var vm = new ShellViewModel(MockWorkspace.Create(Now));
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
}
