using Avalonia;
using Ncw.UI;

namespace Ncw.Desktop;

internal static class Program
{
    // 在 BuildAvaloniaApp() 之前不能使用任何 Avalonia / Arc API。
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // 也供 IDE 的 XAML 预览器使用。
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<NcwApp>()
                  .UsePlatformDetect()
                  .UseNcw()
                  .LogToTrace();
}
