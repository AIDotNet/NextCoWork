using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Mock;
using Ncw.Client.Services;
using Ncw.Client.ViewModels;
using Ncw.UI.Chrome;
using Ncw.UI.Controls;
using Ncw.UI.Localization;
using Ncw.UI.Views.Desktop;
using Xunit;

namespace Ncw.UI.Tests;

/// <summary>
/// Windows 的系统托盘(issue #200):<see cref="NcwAttention"/> 在宿主给了 <see cref="NativeTrayFactory"/> 时不再建 Avalonia 的 TrayIcon,
/// 菜单每次右键时经 <see cref="NativeTrayMenu.Snapshot"/> 从同一份 NativeMenu 取。Win32 那一半(Shell_NotifyIcon / TrackPopupMenuEx)没有真机自动化。
/// </summary>
public class NativeTrayTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(8));

    private sealed class FakeTray : INativeTray
    {
        public required Func<IReadOnlyList<NativeTrayEntry>> Menu { get; init; }

        public required Action Activate { get; init; }

        public string ToolTip { get; private set; } = "";

        public bool Disposed { get; private set; }

        public void SetToolTip(string text) => ToolTip = text;

        public void Dispose() => Disposed = true;
    }

    [AvaloniaFact]
    public void Snapshot_skips_hidden_items_and_keeps_no_dangling_separators()
    {
        var runs = 0;
        var menu = new NativeMenu
        {
            new NativeMenuItemSeparator(),
            new NativeMenuItem("Open") { Command = new RelayCommand(() => runs++) },
            new NativeMenuItemSeparator(),
            new NativeMenuItem("Hidden") { IsVisible = false, Command = new RelayCommand(() => { }) },
            new NativeMenuItemSeparator(),
            new NativeMenuItem("Running: 2") { IsEnabled = false },
            new NativeMenuItem("No command"),
            new NativeMenuItemSeparator(),
        };

        var entries = NativeTrayMenu.Snapshot(menu);

        Assert.Equal(["Open", "", "Running: 2", "No command"], entries.Select(e => e.Text));
        Assert.Equal([false, true, false, false], entries.Select(e => e.IsSeparator));
        Assert.True(entries[0].IsEnabled);
        Assert.False(entries[2].IsEnabled);
        Assert.False(entries[3].IsEnabled);   // 没有命令的项点了也没用,灰掉
        entries[0].Invoke!();
        Assert.Equal(1, runs);
    }

    [AvaloniaFact]
    public void With_a_native_tray_factory_the_tray_is_native_and_its_menu_follows_the_summary()
    {
        var vm = new ShellViewModel(MockWorkspace.Create(Now));
        var window = new Window { Width = 1200, Height = 800, Content = new DesktopShell { DataContext = vm, Chrome = new WindowChrome(78, false) } };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        using var desktop = new ClassicDesktopStyleApplicationLifetime();
        FakeTray? tray = null;

        using (var attention = new NcwAttention(vm, window, desktop, native: null, (tip, menu, activate) => tray = new FakeTray { Menu = menu, Activate = activate }))
        {
            Assert.True(attention.HasTray);
            Assert.NotNull(tray);
            Assert.Equal(ShutdownMode.OnExplicitShutdown, desktop.ShutdownMode);

            attention.SetSummary(new AttentionSummary(2, 1, false));
            Assert.Equal(NcwTexts.Format(NcwTextKind.TrayTooltip, 2, 1), tray.ToolTip);
            Assert.Equal(tray.ToolTip, attention.TrayToolTip);

            var entries = tray.Menu();
            var texts = entries.Where(e => !e.IsSeparator).Select(e => e.Text).ToList();
            Assert.Equal(NcwTexts.Get(NcwTextKind.TrayOpen), texts[0]);
            Assert.Contains(NcwTexts.Format(NcwTextKind.TrayPending, 2), texts);
            Assert.Contains(NcwTexts.Format(NcwTextKind.TrayRunning, 1), texts);
            Assert.DoesNotContain(NcwTexts.Get(NcwTextKind.TrayUnknown), texts);   // 没有断线设备
            Assert.Equal(NcwTexts.Get(NcwTextKind.TrayQuit), texts[^1]);

            // 托盘菜单的「打开」把藏起来的窗口带回来(用户点关闭时藏窗口的路径见 AttentionUiTests;程序调 Close 是真关)
            window.Hide();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.False(window.IsVisible);
            entries.First(e => e.Text == NcwTexts.Get(NcwTextKind.TrayOpen)).Invoke!();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsVisible);

            // 左键单击同样
            window.Hide();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            tray.Activate();
            Assert.True(window.IsVisible);
        }

        Assert.True(tray.Disposed);
        window.Close();
    }
}
