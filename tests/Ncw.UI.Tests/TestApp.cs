using Avalonia;
using Avalonia.Headless;
using Ncw.UI;

[assembly: AvaloniaTestApplication(typeof(Ncw.UI.Tests.TestApp))]

namespace Ncw.UI.Tests;

/// <summary>headless 测试宿主:真实的 Skia 渲染,便于截图回归(docs/design/05 §4)。</summary>
public sealed class TestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
                  .UseSkia()
                  .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                  .UseNcw();
}
