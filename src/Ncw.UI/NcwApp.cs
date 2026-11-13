using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Ncw.Client.Mock;
using Ncw.Client.Services;
using Ncw.Client.ViewModels;
using Ncw.UI.Views.Desktop;

namespace Ncw.UI;

/// <summary>应用。Arc 不需要 App.axaml 和样式,装配在 <see cref="NcwAppSetup"/>。</summary>
public class NcwApp : Application
{
    private PrefsPersistence? _prefs;

    /// <summary>
    /// 控制端自己的文件(界面偏好等)放在 <c>~/.next-cowork/client/</c>,和 Agent 的数据根 <c>~/.next-cowork/data/</c> 分开。
    /// 环境变量 <c>NCW_CLIENT_DIR</c> 可以改到别处(开发、测试时隔离用)。
    /// </summary>
    public static string ClientDirectory =>
        Environment.GetEnvironmentVariable("NCW_CLIENT_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".next-cowork", "client");

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // M1:模拟数据。M2 起由设备协议填充同一组 Store。
            var stores = MockWorkspace.Create(DateTimeOffset.Now);
            _prefs = new PrefsPersistence(stores, new JsonFilePrefsRepository(Path.Combine(ClientDirectory, "ui.json")));
            desktop.MainWindow = new MainWindow { DataContext = new ShellViewModel(stores) };
            desktop.Exit += (_, _) => _prefs?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
