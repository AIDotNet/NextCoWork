using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Ncw.Client.Devices;
using Ncw.Client.Mock;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.UI.Localization;
using Ncw.UI.Views.Desktop;

namespace Ncw.UI;

/// <summary>应用。Arc 不需要 App.axaml 和样式,装配在 <see cref="NcwAppSetup"/>。</summary>
public class NcwApp : Application
{
    private PrefsPersistence? _prefs;
    private INcwBackend? _backend;

    /// <summary>宿主在启动前设置:创建连接设备的后端。为 null 时没有设备(只用于预览)。</summary>
    public static Func<INcwBackend>? BackendFactory { get; set; }

    /// <summary>
    /// 控制端自己的文件(界面偏好等)放在 <c>~/.next-cowork/client/</c>,和 Agent 的数据分开。
    /// 环境变量 <c>NCW_CLIENT_DIR</c> 可以改到别处(开发、测试时隔离用)。
    /// </summary>
    public static string ClientDirectory =>
        Environment.GetEnvironmentVariable("NCW_CLIENT_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".next-cowork", "client");

    /// <summary><c>NCW_MOCK=1</c>:用模拟数据演示界面,不连接 Agent。</summary>
    public static bool UseMockData => Environment.GetEnvironmentVariable("NCW_MOCK") == "1";

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var devices = new DeviceRegistry();
            ClientStores stores;
            if (UseMockData)
            {
                stores = MockWorkspace.Create(DateTimeOffset.Now);
            }
            else
            {
                stores = new ClientStores();
                _backend = BackendFactory?.Invoke();
                _backend?.Start(stores, devices, AvaloniaUiDispatcher.Instance, NcwClientText.Instance);
            }

            _prefs = new PrefsPersistence(stores, new JsonFilePrefsRepository(Path.Combine(ClientDirectory, "ui.json")));
            desktop.MainWindow = new MainWindow { DataContext = new ShellViewModel(stores, devices: devices) };
            desktop.Exit += (_, _) =>
            {
                _prefs?.Dispose();
                // 退出时让 Agent 收尾;最多等 3 秒,不能卡住退出。
                _backend?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
