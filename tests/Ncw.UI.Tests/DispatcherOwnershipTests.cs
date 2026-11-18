using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace Ncw.UI.Tests;

/// <summary>
/// 用例之间有后台线程访问 <c>Dispatcher.UIThread</c> 时,下一条用例照样能初始化(<see cref="TestApp"/> 里的「夺回调度器」)。
/// 两条用例各留下一个持续访问的后台线程:不管 xunit 先跑哪一条,后跑的那条的初始化都落在前一条的线程还在抢的时候。
/// 去掉 TestApp 的那一步,后跑的那条必然以 Test Case Cleanup Failure(跨线程访问)失败。
/// </summary>
public class DispatcherOwnershipTests
{
    private static void LeaveAThreadTouchingTheUiDispatcher()
    {
        var thread = new Thread(() =>
        {
            var until = Environment.TickCount64 + 3000;
            while (Environment.TickCount64 < until)
            {
                _ = Dispatcher.UIThread.CheckAccess();
                Thread.SpinWait(200);
            }
        })
        {
            IsBackground = true,
            Name = "ncw-test-dispatcher-thief",
        };
        thread.Start();
    }

    private static void TheUiThreadIsThisOne()
    {
        Assert.True(Dispatcher.UIThread.CheckAccess());
        var window = new Window { Content = new TextBlock { Text = "ok" } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public void A_background_thread_left_by_one_test_cannot_take_the_ui_dispatcher_from_the_next()
    {
        TheUiThreadIsThisOne();
        LeaveAThreadTouchingTheUiDispatcher();
    }

    [AvaloniaFact]
    public void And_the_other_way_round()
    {
        TheUiThreadIsThisOne();
        LeaveAThreadTouchingTheUiDispatcher();
    }
}
