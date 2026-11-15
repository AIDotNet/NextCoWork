using Avalonia;
using Avalonia.Headless;
using Ncw.Client.Stores;
using Ncw.UI;
using Ncw.UI.Controls;

[assembly: AvaloniaTestApplication(typeof(Ncw.UI.Tests.TestApp))]

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
                  .AfterSetup(_ => NcwMotion.Apply(UiMotionLevel.Off));
}
