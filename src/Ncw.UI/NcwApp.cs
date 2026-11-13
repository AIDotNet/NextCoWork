using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Ncw.Client.Mock;
using Ncw.Client.ViewModels;
using Ncw.UI.Views.Desktop;

namespace Ncw.UI;

/// <summary>应用。Arc 不需要 App.axaml 和样式,装配在 <see cref="NcwAppSetup"/>。</summary>
public class NcwApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // M1:模拟数据。M2 起由设备协议填充同一组 Store。
            var shell = new ShellViewModel(MockWorkspace.Create(DateTimeOffset.Now));
            desktop.MainWindow = new MainWindow { DataContext = shell };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
