using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ncw.Client.Mock;
using Ncw.Client.Services;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.UI.Chrome;
using Ncw.UI.Controls;
using Ncw.UI.Localization;
using Ncw.UI.Views.Desktop;

namespace Ncw.UI.Tests;

/// <summary>桌面提醒的界面层(docs/design/03 §11.2):通知文字、点通知打开那一条、托盘开着时关窗只是藏起来、通知设置页。</summary>
public class AttentionUiTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(8));

    private sealed class FakeNative : INativeAttention
    {
        public List<(string Title, string Body, string Key)> Shown { get; } = [];

        public int Badge { get; private set; } = -1;

        public int AttentionRequests { get; private set; }

        public bool SupportsNotifications => true;

        public bool SupportsBadge => false;

        public event Action<string>? Activated;

        public void Notify(string title, string body, string key) => Shown.Add((title, body, key));

        public void SetBadge(int count) => Badge = count;

        public void RequestAttention(Window window) => AttentionRequests++;

        public void Click(string key) => Activated?.Invoke(key);
    }

    private static void Dispatch()
    {
        for (var i = 0; i < 3; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    [AvaloniaFact]
    public void Notice_text_names_what_happened_and_where()
    {
        var (title, body) = NcwAttention.Describe(new AttentionNotice("i:1", InboxKind.ToolApproval, "local", "ws", "s1", "NextCoWork", "Fix CI", "Bash", "rm -rf build", null, Dangerous: true));
        Assert.Equal(NcwTexts.Get(NcwTextKind.AttnToolApprovalDangerous), title);
        Assert.Equal("NextCoWork · Fix CI\nBash rm -rf build", body);

        var (doneTitle, doneBody) = NcwAttention.Describe(new AttentionNotice("r:1", InboxKind.RunCompleted, "local", "ws", "s1", "NextCoWork", null, null, null, null, false));
        Assert.Equal(NcwTexts.Get(NcwTextKind.AttnCompleted), doneTitle);
        Assert.Equal("NextCoWork", doneBody);
    }

    [AvaloniaFact]
    public void Sink_forwards_to_native_and_closing_hides_when_the_tray_is_on()
    {
        var vm = new ShellViewModel(MockWorkspace.Create(Now));
        var window = new Window { Width = 1200, Height = 800, Content = new DesktopShell { DataContext = vm, Chrome = new WindowChrome(78, false) } };
        window.Show();
        Dispatch();
        var native = new FakeNative();
        using var attention = new NcwAttention(vm, window, desktop: null, native);

        Assert.Same(attention, vm.AttentionSink);
        Assert.False(vm.Settings.Notifications.IsBadgeSupported);
        Assert.True(vm.Settings.Notifications.IsSystemNotificationSupported);

        attention.Notify(new AttentionNotice("i:1", InboxKind.Question, "local", "ws", "s1", "P", "S", null, null, "Which target?", false));
        Assert.Equal("Which target?", native.Shown.Single().Body.Split('\n')[^1]);

        attention.SetBadge(3);
        Assert.Equal(3, native.Badge);

        // 没有桌面生命周期(测试)时不建托盘,关窗照常关
        Assert.False(attention.HasTray);
        window.Close();
        Dispatch();
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void Quit_is_intercepted_when_tasks_are_running_and_allowed_after_confirm_or_bypass()
    {
        var vm = new ShellViewModel(new Ncw.Client.Stores.ClientStores());
        var window = new Window { Width = 1200, Height = 800, Content = new DesktopShell { DataContext = vm, Chrome = new WindowChrome(78, false) } };
        window.Show();
        Dispatch();
        var native = new FakeNative();
        using var attention = new NcwAttention(vm, window, desktop: null, native);

        // 没有运行中任务时不拦截
        attention.SetSummary(new AttentionSummary(0, 0, false));
        Assert.False(attention.TryInterceptShutdown());

        // 有任务运行时拦截并弹出确认;用户点取消后仍拦截
        var promptedCounts = new List<int>();
        var confirmResult = false;
        attention.ConfirmQuitAsync = count =>
        {
            promptedCounts.Add(count);
            return Task.FromResult(confirmResult);
        };
        attention.SetSummary(new AttentionSummary(0, 2, false));
        Assert.True(attention.TryInterceptShutdown());
        Dispatch();
        Assert.Equal([2], promptedCounts);

        // 用户确认后下次放行
        confirmResult = true;
        Assert.True(attention.TryInterceptShutdown());
        Dispatch();
        Assert.Equal([2, 2], promptedCounts);
        Assert.False(attention.TryInterceptShutdown());

        window.Close();
        Dispatch();
    }

    [AvaloniaTheory]
    [InlineData(1500, "light")]
    [InlineData(1500, "dark")]
    [InlineData(600, "light")]
    public void Notifications_settings_page(double width, string theme)
    {
        TestThemes.Apply(theme);
        try
        {
            var vm = new ShellViewModel(MockWorkspace.Create(Now));
            var window = new Window { Width = width, Height = 860, Content = new DesktopShell { DataContext = vm, Chrome = new WindowChrome(78, false) } };
            window.Show();
            vm.Settings.Notifications.IsBadgeSupported = width >= 1000; // 窄屏这张顺便覆盖「不支持」的说明
            vm.OpenSettingsSection(SettingsSection.Notifications);
            Dispatch();
            Dispatch();

            var page = window.GetVisualDescendants().OfType<NotificationsSettingsPage>().Single();
            Assert.True(page.IsEffectivelyVisible);

            vm.Settings.Notifications.NotifyCompleted = false;
            Assert.False(vm.Stores.Prefs.State.AttentionOrDefault.NotifyCompleted);
            Dispatch();

            var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
            Directory.CreateDirectory(dir);
            window.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"settings-notifications-{width}-{theme}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        finally
        {
            TestThemes.Apply("light");
        }
    }
}
