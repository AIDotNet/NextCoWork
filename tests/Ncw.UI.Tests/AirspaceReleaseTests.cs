using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Ncw.Client.Mock;
using Ncw.Client.ViewModels;
using Ncw.UI.Chrome;
using Ncw.UI.Controls;
using Ncw.UI.Views.Desktop;

namespace Ncw.UI.Tests;

/// <summary>
/// <see cref="NcwWidgetAirspace"/> 是全局计数:挂起不释放,所有窗口里的 widget 都会一直被挂起。
/// 以前 DesktopShell 只在「覆盖层变不可见」时才释放,窗口在覆盖层还开着时被关掉就永远泄漏
/// (以后一旦有多窗口,关掉一个开着设置页的窗口会让别的窗口里的 widget 永久隐藏)。
/// 计数是进程内静态的,别的用例可能留着泄漏:这里只断言「我这次操作」引起的增减。
/// </summary>
public class AirspaceReleaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(8));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private static (Window Window, DesktopShell Shell, ShellViewModel Vm) Render()
    {
        var vm = new ShellViewModel(MockWorkspace.Create(Now), new FixedClock(Now));
        var shell = new DesktopShell { DataContext = vm, Chrome = new WindowChrome(78, false) };
        var window = new Window { Width = 1200, Height = 860, Content = shell };
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

    [AvaloniaFact]
    public void Closing_the_window_while_the_settings_overlay_is_open_gives_the_suspension_back()
    {
        var baseline = NcwWidgetAirspace.ActiveCount;
        var (window, _, vm) = Render();

        vm.Settings.OpenCommand.Execute(null);
        Dispatch();
        Assert.Equal(baseline + 1, NcwWidgetAirspace.ActiveCount);   // 设置页盖住了对话区:WebView 让出空域

        window.Close();
        Dispatch();

        Assert.Equal(baseline, NcwWidgetAirspace.ActiveCount);       // 窗口没了,挂起必须跟着归还,不能留给别的窗口
    }

    [AvaloniaFact]
    public void A_shell_taken_off_the_screen_holds_no_suspension_and_takes_it_again_when_put_back()
    {
        var baseline = NcwWidgetAirspace.ActiveCount;
        var (window, shell, vm) = Render();
        vm.Settings.OpenCommand.Execute(null);
        Dispatch();
        Assert.Equal(baseline + 1, NcwWidgetAirspace.ActiveCount);

        window.Content = null;                                       // 外壳被卸下(例如被别的内容替换)
        Dispatch();
        Assert.Equal(baseline, NcwWidgetAirspace.ActiveCount);

        window.Content = shell;                                      // 放回来:设置页还开着,覆盖层又盖在对话区上
        Dispatch();
        Assert.Equal(baseline + 1, NcwWidgetAirspace.ActiveCount);

        vm.Settings.CloseCommand.Execute(null);
        Dispatch();
        Assert.Equal(baseline, NcwWidgetAirspace.ActiveCount);       // 关掉设置页:恰好归还这一次
        window.Close();
        Dispatch();
        Assert.Equal(baseline, NcwWidgetAirspace.ActiveCount);       // 再关窗口不会多还(幂等)
    }
}
