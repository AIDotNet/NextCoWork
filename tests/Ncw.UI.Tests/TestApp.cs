using Avalonia;
using Avalonia.Headless;
using Ncw.Client.Stores;
using Ncw.UI;
using Ncw.UI.Controls;

[assembly: AvaloniaTestApplication(typeof(Ncw.UI.Tests.TestApp))]
[assembly: Ncw.UI.Tests.CloseWindowsAfterTest]

namespace Ncw.UI.Tests;

/// <summary>
/// headless 测试宿主:真实的 Skia 渲染,便于截图回归(docs/design/05 §4)。
/// <b>默认关闭动效</b>(<see cref="UiMotionLevel.Off"/>):展开 / 收起、出现 / 消失都立即落到终态,截图不会截到动画中间帧;
/// 其它测试类收尾时也恢复到 Off(不是 Full)。动效本身由 <c>MotionTests</c> / <c>MotionScreenshotTests</c> 单独验证(自己切到 Full + Arc 的 <c>ManualFrameClock</c>)。
/// </summary>
public sealed class TestApp : Application
{
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<TestApp>()
                                .UseSkia()
                                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                                .UseNcw()
                                .AfterSetup(_ =>
                                {
                                    NcwMotion.Apply(UiMotionLevel.Off);
                                    Avalonia.Controls.Window.WindowOpenedEvent.AddClassHandler<Avalonia.Controls.Window>((window, _) =>
                                    {
                                        lock (Opened)
                                        {
                                            Opened.Add(window);
                                        }
                                    });
                                });

        // 每条用例的初始化(AppBuilder.SetupUnsafe)一开始就先把界面调度器拿回来,见 ClaimUiDispatcher
        var runtime = builder.RuntimePlatformServicesInitializer;
        var windowing = builder.WindowingSubsystemInitializer;
        return builder
            .UseRuntimePlatformSubsystem(() => { ClaimUiDispatcher(); runtime?.Invoke(); }, builder.RuntimePlatformServicesName ?? "")
            .UseWindowingSubsystem(() => { ClaimUiDispatcher(); windowing?.Invoke(); }, builder.WindowingSubsystemName ?? "");
    }

    private static readonly System.Reflection.MethodInfo? ResetDispatcher =
        typeof(Avalonia.Threading.Dispatcher).GetMethod("ResetBeforeUnitTests", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

    /// <summary>
    /// 让当前线程(headless 会话线程)成为界面线程。
    /// <para>
    /// ★ headless 每条用例结束时都把全局调度器清空(<c>Dispatcher.ResetForUnitTests</c>),到下一条用例初始化才重建;而 Avalonia 的规则是
    /// 「清空之后第一个访问 <c>Dispatcher.UIThread</c> 的线程就是界面线程」。两条用例之间的整段空档里,任何后台线程访问它一次——
    /// 上一条用例留下的后台任务从线程池上改了视图模型的属性(绑定收到跨线程通知时会去查界面调度器)、协议回调经 <c>AvaloniaUiDispatcher</c> 切线程——
    /// 调度器就绑到了那个线程池线程上,下一条用例在 <c>DefaultRenderLoop.Add</c> 里报「The calling thread cannot access this object」,
    /// 被 xunit 记成那条用例的 Test Case Cleanup Failure(Linux CI 上 7 轮里出现 4 轮,每次落在不同的用例上)。
    /// </para>
    /// 做法:初始化的第一步检查调度器是不是自己的,不是就再清一次、重新取(清空用的是 Avalonia 给单元测试留的内部方法,反射调用;取不到就不做)。
    /// 拿到之后别的线程再访问 <c>Dispatcher.UIThread</c> 得到的就是这一个,抢不走了。
    /// </summary>
    private static void ClaimUiDispatcher()
    {
        for (var attempt = 0; attempt < 16 && !Avalonia.Threading.Dispatcher.UIThread.CheckAccess(); attempt++)
        {
            if (ResetDispatcher is null)
            {
                return;
            }

            ResetDispatcher.Invoke(null, null);
        }
    }

    /// <summary>这一条用例里打开过的窗口。界面线程上加,任何线程都可能来读(钩子是程序集级的,普通 [Fact] 在线程池上跑),所以加锁。</summary>
    private static readonly List<Avalonia.Controls.Window> Opened = [];

    private static int _closedBatches;

    /// <summary>
    /// 关掉用例留下的窗口,并定期把 Skia 的非托管内存收回来。
    /// ★ 每条截图用例都建一个完整的窗口、渲染一帧,用完既不关也不释放;位图是非托管内存,GC 感受不到压力——800 多条用例下来进程涨到十几 GB:
    /// macOS 靠内存压缩扛得住,GitHub 的 Linux runner 直接失联(「The hosted runner lost communication」),Windows 60 分钟跑不完。
    /// <para>
    /// 这里不碰 <c>Dispatcher.UIThread</c>:本钩子也会在线程池线程上被调用(普通 [Fact] 与界面用例并行),而两条用例之间调度器是空的,
    /// 谁先访问谁就成了界面线程(见 <see cref="ClaimUiDispatcher"/>)。只用窗口自己的调度器判断归属:不是当前线程的窗口不碰。
    /// </para>
    /// </summary>
    public static void CloseOpenWindows()
    {
        Avalonia.Controls.Window[] mine;
        lock (Opened)
        {
            mine = [.. Opened.Where(window => window.CheckAccess())];
            if (mine.Length == 0)
            {
                return; // 不在界面线程上,或者这条用例没开过窗口
            }

            Opened.RemoveAll(window => mine.Contains(window));
        }

        try
        {
            foreach (var window in mine)
            {
                window.Close();
            }

            mine[0].Dispatcher.RunJobs();
        }
        catch (Exception)
        {
            // 用例已经结束:清理时出的任何错都不该算到这条用例头上,也不该把下一条带倒
        }

        if (++_closedBatches % 8 == 0)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}

/// <summary>每条用例结束后关掉它打开的窗口(见 <see cref="TestApp.CloseOpenWindows"/>)。程序集级:所有用例都生效。</summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
public sealed class CloseWindowsAfterTestAttribute : Xunit.v3.BeforeAfterTestAttribute
{
    public override void After(System.Reflection.MethodInfo methodUnderTest, Xunit.v3.IXunitTest test) => TestApp.CloseOpenWindows();
}
