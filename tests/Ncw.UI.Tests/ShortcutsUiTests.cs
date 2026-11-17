using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Ncw.Client.Mock;
using Ncw.Client.Shortcuts;
using Ncw.Client.ViewModels;
using Ncw.UI.Chrome;
using Ncw.UI.Controls;
using Ncw.UI.Views.Desktop;

namespace Ncw.UI.Tests;

/// <summary>快捷键路由、命令面板、快捷键设置页(docs/design/03 §11)。</summary>
public class ShortcutsUiTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(8));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private static (Window Window, DesktopShell Shell, ShellViewModel Vm) Render(double width = 1500, double height = 860)
    {
        ShortcutText.IsMac = true;
        Ncw.UI.Converters.RelativeTimeConverter.Clock = new FixedClock(Now);
        var vm = new ShellViewModel(MockWorkspace.Create(Now), new FixedClock(Now));
        var shell = new DesktopShell { DataContext = vm, Chrome = new WindowChrome(78, false) };
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

    [AvaloniaFact]
    public void Real_key_presses_reach_the_shell_through_the_window()
    {
        var (window, shell, vm) = Render();
        Assert.NotNull(shell.Shortcuts);

        window.KeyPress(Key.B, RawInputModifiers.Meta, PhysicalKey.B, "b");
        Dispatch();
        Assert.False(vm.IsSidebarOpen);
        Assert.False(shell.IsSidebarVisible);

        window.KeyPress(Key.B, RawInputModifiers.Meta, PhysicalKey.B, "b");
        Dispatch();
        Assert.True(vm.IsSidebarOpen);

        var second = vm.Stores.Layout.State.Rail.Projects().Skip(1).First();
        window.KeyPress(Key.D2, RawInputModifiers.Meta, PhysicalKey.Digit2, "2");
        Dispatch();
        Assert.Equal(second, vm.Stores.Selection.State.Project);
    }

    [AvaloniaFact]
    public void Custom_shortcut_replaces_the_default_immediately()
    {
        var (_, shell, vm) = Render();
        vm.Stores.Prefs.Update(p => p with { Shortcuts = ShortcutCatalog.Assign(p.ShortcutOverrides, AppCommand.ToggleSidebar, "Meta+Shift+E", mac: true) });

        Assert.False(shell.Shortcuts!.Handle(Key.B, KeyModifiers.Meta));
        Assert.True(vm.IsSidebarOpen);
        Assert.True(shell.Shortcuts.Handle(Key.E, KeyModifiers.Meta | KeyModifiers.Shift));
        Assert.False(vm.IsSidebarOpen);
    }

    [AvaloniaFact]
    public void Shortcuts_pause_while_recording()
    {
        var (_, shell, vm) = Render();
        NcwShortcutRouter.IsRecording = true;
        try
        {
            Assert.False(shell.Shortcuts!.Handle(Key.B, KeyModifiers.Meta));
            Assert.True(vm.IsSidebarOpen);
        }
        finally
        {
            NcwShortcutRouter.IsRecording = false;
        }
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Command_palette_opens_with_cmd_k_and_runs_the_choice(string theme)
    {
        TestThemes.Apply(theme);
        try
        {
            var (window, shell, vm) = Render();

            window.KeyPress(Key.K, RawInputModifiers.Meta, PhysicalKey.K, "k");
            Dispatch();
            Assert.True(vm.Palette.IsOpen);
            var layer = shell.GetVisualDescendants().OfType<CommandPalette>().Single();
            Assert.True(layer.IsVisible);
            Snapshot(window, $"palette-open-1500-{theme}");

            vm.Palette.Query = ShortcutText.Title(AppCommand.ToggleInspector);
            Dispatch();
            Snapshot(window, $"palette-query-1500-{theme}");
            Assert.Equal(AppCommand.ToggleInspector, vm.Palette.Active?.Command);

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatch();
            Assert.False(vm.Palette.IsOpen);
            Assert.False(vm.IsInspectorOpen);

            window.KeyPress(Key.K, RawInputModifiers.Meta, PhysicalKey.K, "k");
            Dispatch();
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatch();
            Assert.False(vm.Palette.IsOpen);
        }
        finally
        {
            TestThemes.Apply("light");
        }
    }

    [AvaloniaTheory]
    [InlineData(1500, "light")]
    [InlineData(1500, "dark")]
    [InlineData(600, "light")]
    public void Shortcuts_settings_page_lists_and_records(double width, string theme)
    {
        TestThemes.Apply(theme);
        try
        {
            var (window, _, vm) = Render(width);
            vm.OpenSettingsSection(SettingsSection.Shortcuts);
            Dispatch();
            Dispatch();

            var page = window.GetVisualDescendants().OfType<ShortcutsSettingsPage>().Single();
            Assert.True(page.IsEffectivelyVisible);

            var terminal = vm.Settings.Shortcuts.Rows.Single(r => r.Command == AppCommand.OpenTerminal);
            vm.Settings.Shortcuts.StartRecordingCommand.Execute(terminal);
            Dispatch();
            Assert.True(NcwShortcutRouter.IsRecording);

            window.KeyPress(Key.T, RawInputModifiers.Meta | RawInputModifiers.Shift, PhysicalKey.T, "t");
            Dispatch();
            Assert.False(NcwShortcutRouter.IsRecording);
            Assert.Equal("Shift+Meta+T", terminal.Gesture);
            Assert.True(vm.Settings.IsOpen); // 录制吞掉了按键,没有关掉设置
            Snapshot(window, $"settings-shortcuts-{width}-{theme}");
        }
        finally
        {
            NcwShortcutRouter.IsRecording = false;
            TestThemes.Apply("light");
        }
    }
}

/// <summary>截图的明暗主题(和其他截图测试一样直接改 Arc 主题,动效保持关闭)。</summary>
internal static class TestThemes
{
    public static void Apply(string theme) =>
        TKCode.Arc.ArcTheme.Apply(t => t.Appearance(theme == "dark" ? TKCode.Arc.ArcAppearance.Dark : TKCode.Arc.ArcAppearance.Light)
            .MotionLevel(TKCode.Arc.Motion.ArcMotionLevel.Off));
}

/// <summary>设置页的可用性改进(docs/design/03 §11.6):搜索、骨架占位、「通用」的常用 / 高级。</summary>
public class SettingsUsabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(8));

    private static void Dispatch()
    {
        for (var i = 0; i < 3; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Search_finds_a_setting_by_its_description_and_jumps_there(string theme)
    {
        TestThemes.Apply(theme);
        try
        {
            var vm = new ShellViewModel(MockWorkspace.Create(Now));
            var window = new Window { Width = 1500, Height = 860, Content = new DesktopShell { DataContext = vm, Chrome = new WindowChrome(78, false) } };
            window.Show();
            vm.OpenSettingsSection(SettingsSection.Account);
            Dispatch();

            vm.Settings.SearchQuery = Ncw.UI.Localization.NcwTexts.Get(Ncw.UI.Localization.NcwTextKind.NotifyTrayLabel);
            Dispatch();
            var hit = Assert.Single(vm.Settings.SearchHits);
            Assert.Equal(SettingsSection.Notifications, hit.Section);
            var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
            Directory.CreateDirectory(dir);
            window.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"settings-search-1500-{theme}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());

            vm.Settings.OpenHitCommand.Execute(hit);
            Dispatch();
            Assert.Equal(SettingsSection.Notifications, vm.Settings.Section);
            Assert.False(vm.Settings.IsSearching);
        }
        finally
        {
            TestThemes.Apply("light");
        }
    }
}
