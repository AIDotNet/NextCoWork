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
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
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

    /// <summary>这一条用例里打开过的窗口。界面线程上加,任何线程都可能来读(钩子是程序集级的,普通 [Fact] 在线程池上跑),所以加锁。</summary>
    private static readonly List<Avalonia.Controls.Window> Opened = [];

    private static int _closedBatches;

    /// <summary>
    /// 关掉用例留下的窗口,并定期把 Skia 的非托管内存收回来。
    /// ★ 每条截图用例都建一个完整的窗口、渲染一帧,用完既不关也不释放;位图是非托管内存,GC 感受不到压力——800 多条用例下来进程涨到十几 GB:
    /// macOS 靠内存压缩扛得住,GitHub 的 Linux runner 直接失联(「The hosted runner lost communication」),Windows 60 分钟跑不完。
    /// <para>
    /// ★ 这里<b>不能碰 <c>Dispatcher.UIThread</c></b>:headless 每条用例都重建应用,重建时会重置界面调度器,之后第一个访问
    /// <c>Dispatcher.UIThread</c> 的线程就成了「界面线程」。本钩子也会在线程池线程上被调用(普通 [Fact] 与界面用例并行),它在那个空档里访问一次,
    /// 调度器就绑到了线程池线程上,正在重建应用的那条界面用例随即在 <c>DefaultRenderLoop.Add</c> 里报
    /// 「The calling thread cannot access this object」(Linux CI 上出现过两次,都在加了这个钩子之后;这是按堆栈推出来的原因,没有单独复现)。
    /// 所以只用窗口自己的调度器判断归属:不是当前线程的窗口不碰。
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
