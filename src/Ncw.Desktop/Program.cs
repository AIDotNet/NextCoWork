using System.Diagnostics;
using Avalonia;
using Ncw.Client.Updates;
using Ncw.Desktop.Updates;
using Ncw.UI;
using Velopack;

namespace Ncw.Desktop;

internal static class Program
{
    // 在 BuildAvaloniaApp() 之前不能使用任何 Avalonia / Arc API。
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack 的启动钩子必须是进程里最先做的事(docs/design/04 §3):安装 / 更新 / 卸载时它按命令行里的 --veloapp-* 执行钩子并直接退出;
        // 更新器重启应用时也经过这里。所以它在单实例、协议链接注册、Avalonia 之前 —— 钩子进程不能去抢单实例锁或建窗口。
        // 已下载但还没装的更新在这里(没有窗口、sidecar 还没起)应用并重启,是安全的时机。开发时直接 dotnet run 不是安装出来的应用,什么都不做。
        var velopack = VelopackApp.Build();
        if (OperatingSystem.IsWindows())
        {
            // Windows 上安装 / 更新后的钩子进程(限时 15–30 秒):重写协议注册(指向新版本的可执行文件);其他平台的钩子 Velopack 不支持
            velopack = velopack
                .OnAfterInstallFastCallback(_ => UrlSchemeRegistration.EnsureRegistered())
                .OnAfterUpdateFastCallback(_ => UrlSchemeRegistration.EnsureRegistered());
        }

        velopack.Run();

        // 仅开发:无界面的更新演练(只认环境变量,不是面向用户的入口,见 Updates/DevUpdateRehearsal.cs)。
        if (DevUpdateRehearsal.IsRequested(Environment.GetEnvironmentVariable))
        {
            return DevUpdateRehearsal.RunAsync(Environment.GetEnvironmentVariable, Console.Out).GetAwaiter().GetResult();
        }

        // nextcowork:// 链接(Windows / Linux 作为启动参数传入)。已有实例在运行时转给它再退出(单实例转发,docs/design/07 §2)。
        var link = args.FirstOrDefault(a => a.StartsWith(UrlSchemeRegistration.Scheme + ":", StringComparison.OrdinalIgnoreCase));
        var pipe = SingleInstance.PipeName(NcwApp.ClientDirectory);
        using var primary = SingleInstance.TryBecomePrimary(pipe, SingleInstance.LockPath(NcwApp.ClientDirectory), NcwLinkInbox.Submit);
        if (primary is null)
        {
            var forwarded = SingleInstance.TrySend(pipe, link is null ? SingleInstance.ActivateMessage : SingleInstance.LinkPrefix + link, TimeSpan.FromSeconds(3));
            if (forwarded)
            {
                return 0;
            }

            // 管道被占着却连不上(主实例卡住了):继续启动自己,不因为转发失败而打不开应用
        }

        if (link is not null)
        {
            NcwLinkInbox.Submit(SingleInstance.LinkPrefix + link);
        }

        _ = UrlSchemeRegistration.EnsureRegistered();
        LocalBackend? backend = null;
        NcwApp.BackendFactory = () => backend = new LocalBackend();
        NcwApp.UpdateServiceFactory = context => CreateUpdateService(context, () => backend?.Sidecar);
        NcwApp.RefreshTokenVaultFactory = static () => new AccountVault(NcwApp.ClientDirectory);
        Ncw.UI.Controls.NcwMotion.SystemReducesMotionProbe = SystemMotionProbe.ReducesMotion;   // 「动效 · 跟随系统」读系统的「减少动态效果」
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>自更新服务:Velopack 引擎(验签的 feed 源)+ 桌面宿主(先收尾 sidecar 再退出)。feed 位置、公钥见 <see cref="UpdateConfiguration"/>。</summary>
    private static IUpdateService CreateUpdateService(NcwUpdateContext context, Func<LocalAgentSidecar?> sidecar)
    {
        var engine = new VelopackUpdateEngine(UpdateConfiguration.FromEnvironment(Environment.GetEnvironmentVariable), log: Log);
        return new UpdateService(context.Stores, engine, new DesktopUpdateHost(sidecar, context.RequestExit, log: Log),
            new JsonFileUpdateMetaStore(Path.Combine(NcwApp.ClientDirectory, "update.json")), context.Ui, log: Log);
    }

    private static void Log(string message) => Trace.WriteLine(message);

    // 也供 IDE 的 XAML 预览器使用。
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<NcwApp>()
                  .UsePlatformDetect()
                  .UseNcw()
                  .LogToTrace();
}
