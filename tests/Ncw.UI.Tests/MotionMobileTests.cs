using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Views;
using Ncw.UI.Views.Mobile;
using TKCode.Arc;
using TKCode.Arc.Motion;

namespace Ncw.UI.Tests;

/// <summary>手机壳的动效与几处小缺陷(docs/design/03 §9.4、§9.8):标签切换淡入、点已选中的标签回到根页、扫码确认页的有效期按界面语言格式化。</summary>
public class MotionMobileTests : IDisposable
{
    private readonly ManualFrameClock _clock = new();
    private readonly IDisposable _install;

    public MotionMobileTests() => _install = _clock.Install();

    public void Dispose()
    {
        _install.Dispose();
        NcwMotion.Apply(UiMotionLevel.Off);
        ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark));
        TKCode.Arc.Localization.ArcLanguage.Set(TKCode.Arc.Localization.LanguageTags.ZhCN);
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
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
            Pump();
        }
    }

    private static (Window Window, AdaptiveShell Shell, ShellViewModel Vm, ManualClock Clock) Render(double width = 390, MobileHostServices? host = null)
    {
        var clock = new ManualClock();
        var vm = new ShellViewModel(MobileFixtures.Quiet(), clock, host: host);
        var shell = new AdaptiveShell();
        var window = new Window { Width = width, Height = 800, Content = shell };
        window.DataContext = vm;
        window.Show();
        Pump();
        return (window, shell, vm, clock);
    }

    private static void Tap(Window window, Control target)
    {
        var center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Pump();
    }

    [AvaloniaFact]
    public void Tapping_the_already_selected_tab_returns_to_its_root_page_but_switching_keeps_the_target_stack()
    {
        var (window, shell, vm, _) = Render();
        var tabs = window.GetVisualDescendants().OfType<NcwTabbarItem>().ToList();
        Assert.Equal(4, tabs.Count);
        vm.Navigation.OpenDevicesPage(new ManualAddRoute());      // 「设备」标签里推入了一页
        Pump();
        Assert.Equal(MobileTab.Devices, vm.Navigation.SelectedTab);
        Assert.Single(vm.Navigation.StackOf(MobileTab.Devices));

        Tap(window, tabs[(int)MobileTab.Settings]);               // 切到「设置」:「设备」的栈不动
        Assert.Equal(MobileTab.Settings, vm.Navigation.SelectedTab);
        Assert.Single(vm.Navigation.StackOf(MobileTab.Devices));

        Tap(window, tabs[(int)MobileTab.Devices]);                // 切回「设备」:这一下是切换,不是「再点一次」
        Assert.Equal(MobileTab.Devices, vm.Navigation.SelectedTab);
        Assert.Single(vm.Navigation.StackOf(MobileTab.Devices));

        Tap(window, tabs[(int)MobileTab.Devices]);                // 已选中的「设备」再点一次:回到根页
        Assert.Empty(vm.Navigation.StackOf(MobileTab.Devices));
        Assert.Equal(0, shell.Mobile!.Depth(MobileTab.Devices));

        Tap(window, tabs[(int)MobileTab.Devices]);                // 已经在根页:什么也不发生
        Assert.Equal(MobileTab.Devices, vm.Navigation.SelectedTab);
    }

    [AvaloniaFact]
    public void Switching_tabs_fades_the_new_stack_in_and_leaves_no_residue()
    {
        NcwMotion.Apply(UiMotionLevel.Full);
        var (window, shell, vm, _) = Render();
        var stack = (Control)window.GetVisualDescendants().OfType<ContentControl>().First(c => c.Name == "StackHost").Content!;

        vm.Navigation.SelectTabCommand.Execute(MobileTab.Settings);
        Pump();
        var next = (Control)window.GetVisualDescendants().OfType<ContentControl>().First(c => c.Name == "StackHost").Content!;
        Assert.NotSame(stack, next);
        Assert.True(NcwMotion.IsArriving(next));
        Advance(40);
        Assert.InRange(next.Opacity, 0.01, 0.99);
        Advance(600);
        Assert.False(NcwMotion.IsArriving(next));
        Assert.Equal(1, next.Opacity);
        Assert.NotNull(shell.Mobile);

        NcwMotion.Apply(UiMotionLevel.Off);                       // 关闭:没有淡入
        vm.Navigation.SelectTabCommand.Execute(MobileTab.Inbox);
        Pump();
        var inbox = (Control)window.GetVisualDescendants().OfType<ContentControl>().First(c => c.Name == "StackHost").Content!;
        Assert.False(NcwMotion.IsArriving(inbox));
        Assert.Equal(1, inbox.Opacity);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_qr_expiry_time_follows_the_ui_language_not_the_operating_system_region(bool english)
    {
        TKCode.Arc.Localization.ArcLanguage.Set(english ? TKCode.Arc.Localization.LanguageTags.EnUS : TKCode.Arc.Localization.LanguageTags.ZhCN);
        // 操作系统的区域是 en-US(英文 12 小时制),界面语言不一定是英文:这正是缺陷的触发条件
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("en-US");
        try
        {
            AssertExpiry(english);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static void AssertExpiry(bool english)
    {
        var (window, _, vm, clock) = Render(host: new MobileHostServices { QrScanner = new FakeQrScanner() });
        vm.DevicesPage.AddByQrCommand.Execute(null);
        Pump();
        var expires = new DateTimeOffset(DateTime.Today.AddHours(15).AddMinutes(30)).ToUniversalTime();  // 本地 15:30
        vm.QrPairing.Submit(MobileFixtures.Qr(expires));
        Pump();
        Assert.Equal(QrPairingPhase.Parsed, vm.QrPairing.Phase);

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        var expected = vm.QrPairing.Device!.ExpiresAt.ToLocalTime().ToString("t", new CultureInfo(english ? "en-US" : "zh-CN"));
        Assert.Contains(texts, t => t.Contains(expected));
        if (english)
        {
            Assert.Contains(texts, t => t.Contains("Valid until") && t.Contains("PM"));
        }
        else
        {
            Assert.DoesNotContain(texts, t => t.Contains("PM") || t.Contains("AM"));   // 中文界面不再出现英文的 12 小时制
        }

        Assert.NotNull(clock);
    }
}
