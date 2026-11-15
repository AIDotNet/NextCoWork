using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Ncw.Client.Accounts;
using Ncw.Client.Devices;
using Ncw.Client.Mock;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.Updates;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.UI.Controls;
using Ncw.UI.Localization;
using Ncw.UI.Views.Desktop;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Ncw.UI;

/// <summary>宿主创建自更新服务时需要的东西:Store、UI 线程调度、「发起应用退出」。</summary>
public sealed record NcwUpdateContext(ClientStores Stores, IUiDispatcher Ui, Action RequestExit);

/// <summary>应用。Arc 不需要 App.axaml 和样式,装配在 <see cref="NcwAppSetup"/>。</summary>
public class NcwApp : Application
{
    private PrefsPersistence? _prefs;
    private IDisposable? _motion;
    private INcwBackend? _backend;
    private IUpdateService? _updates;

    /// <summary>宿主在启动前设置:创建连接设备的后端。为 null 时没有设备(只用于预览)。</summary>
    public static Func<INcwBackend>? BackendFactory { get; set; }

    /// <summary>
    /// 宿主在启动前设置:创建自更新服务(桌面端用 Velopack,docs/design/04 §3)。为 null 时没有自更新,设置页的「关于」只显示版本。
    /// 手机宿主不设置(商店分发,不自更新)。
    /// </summary>
    public static Func<NcwUpdateContext, IUpdateService?>? UpdateServiceFactory { get; set; }

    /// <summary>
    /// 宿主在启动前记下的早期里程碑(进程创建时间、单实例锁拿到的时间):Avalonia 起来之前就发生了,界面起来后一并写进启动事实(docs/design/03 §10)。
    /// </summary>
    public static IReadOnlyList<(StartupMilestone Milestone, DateTimeOffset At)> EarlyMilestones { get; set; } = [];

    /// <summary>
    /// 宿主在主窗口创建之后、显示之前调用:参数是窗口和加载界面底色(Arc 的 Background token 此刻解析出的 RGB)。
    /// 桌面宿主据此把原生窗口的背景色设成同一个底色——窗口出现的第一帧就是加载界面的底色,不先白屏再变深色(docs/design/03 §10.6)。
    /// </summary>
    public static Action<Avalonia.Controls.Window, byte, byte, byte>? NativeWindowBackground { get; set; }

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
                // 启动加载界面(docs/design/03 §10):先开始跟踪,再拉起后端——后端的第一个信号就已经能记下来
                stores.Startup.Begin(StartupProfile.Desktop);
                foreach (var (milestone, at) in EarlyMilestones)
                {
                    stores.Startup.Reach(milestone, at);
                }

                stores.Startup.Reach(StartupMilestone.FrameworkReady);

                _backend = BackendFactory?.Invoke();
                _backend?.Start(stores, devices, AvaloniaUiDispatcher.Instance, NcwClientText.Instance);
            }

            _prefs = new PrefsPersistence(stores, new JsonFilePrefsRepository(Path.Combine(ClientDirectory, "ui.json")));
            _motion = NcwMotion.Bind(stores);   // 动效级别:偏好 → Arc 主题(docs/design/03 §9)
            stores.Startup.Reach(StartupMilestone.PlatformReady);
            ShellViewModel? shell = null;
            if (UseMockData)
            {
                MockAccount(stores);
            }
            else
            {
                // 账户(docs/design/07 §2):每个控制端各自登录,刷新令牌放系统钥匙串;后端暂时不存在时登录会失败并给出提示,本地功能不受影响。
                _account = new AccountService(
                    stores, new HttpAccountApi(HttpAccountApi.CreateDefaultClient(), AccountEndpointsFromEnvironment()),
                    RefreshTokenVaultFactory?.Invoke() ?? UnsupportedRefreshTokenVault.Instance,
                    new JsonFileAccountMetaStore(Path.Combine(ClientDirectory, "account.json")),
                    () => shell!.Launcher, new DeviceDelegationTarget(devices), AvaloniaUiDispatcher.Instance,
                    listener: new LoopbackLoginCallbackListener(done => NcwTexts.Get(done ? NcwTextKind.AccountCallbackDone : NcwTextKind.AccountCallbackFailed)));
            }

            if (!UseMockData)
            {
                // 退出请求走应用自己的退出流程(窗口关闭、Exit 事件里让 Agent 收尾);必须在 UI 线程上调 Shutdown
                _updates = UpdateServiceFactory?.Invoke(new NcwUpdateContext(stores, AvaloniaUiDispatcher.Instance,
                    () => Avalonia.Threading.Dispatcher.UIThread.Post(() => desktop.Shutdown())));
            }

            shell = new ShellViewModel(stores, devices: devices, inspector: UseMockData ? MockInspectorServices.Create(stores) : null,
                account: _account, links: _account is null ? null : new LinkRouter(_account), updates: _updates);
            shell.Settings.Appearance.SystemReducesMotion = () => NcwMotion.SystemReducesMotionProbe();
            if (!UseMockData)
            {
                // 启动加载界面的宿主能力:本机 Agent 守护的重试 / 诊断,和「退出」走应用自己的退出流程(让 Agent 收尾)
                shell.StartupHost = _backend?.StartupHost;
                shell.RequestExit = () => Avalonia.Threading.Dispatcher.UIThread.Post(() => desktop.Shutdown());
                if (_account is null)
                {
                    stores.Startup.Reach(StartupMilestone.AccountDecided);   // 没有账户服务:没有要读的账户记录
                }
            }

            var window = new MainWindow { DataContext = shell };
            desktop.MainWindow = window;
            if (!UseMockData)
            {
                stores.Startup.Reach(StartupMilestone.ShellCreated);
                var background = ArcTokens.GetColor(window, ArcDesignTokenKind.Background);
                NativeWindowBackground?.Invoke(window, background.R, background.G, background.B);
                window.Opened += (_, _) => stores.Startup.Reach(StartupMilestone.WindowShown);
            }

            if (_updates is not null)
            {
                _ = _updates.InitializeAsync();
            }

            if (_account is not null)
            {
                AttachLinks(desktop, shell.Links!);
                _ = _account.InitializeAsync();
            }

            desktop.Exit += (_, _) =>
            {
                NcwLinkInbox.Detach();
                _updates?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
                _account?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
                _motion?.Dispose();
                _prefs?.Dispose();
                // 退出时让 Agent 收尾;最多等 3 秒,不能卡住退出。
                _backend?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            };
        }

        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            // 手机宿主(Ncw.Mobile.iOS / Ncw.Mobile.Android,docs/design/04 §8):单视图,根视图是按宽度切换外壳的容器(手机宽度下是 MobileShell,iPad 横屏是桌面外壳)。
            // 手机只连远程设备:宿主的后端(BackendFactory)创建 RemoteDeviceManager,身份存进 Keychain / Keystore(IControllerIdentityStore)。
            var devices = new DeviceRegistry();
            var stores = new ClientStores();
            // 启动加载界面(简化版式):同一个 Core 纯函数和 Store;手机没有本机 Agent,阶段是 应用 → 连接 → 内容,设备连不上不算失败
            stores.Startup.Begin(StartupProfile.Remote);
            foreach (var (milestone, at) in EarlyMilestones)
            {
                stores.Startup.Reach(milestone, at);
            }

            stores.Startup.Reach(StartupMilestone.InstanceReady);   // 手机没有单实例
            stores.Startup.Reach(StartupMilestone.AccountDecided);  // 手机壳还没有账户页
            _backend = BackendFactory?.Invoke();
            _backend?.Start(stores, devices, AvaloniaUiDispatcher.Instance, NcwClientText.Instance);
            _prefs = new PrefsPersistence(stores, new JsonFilePrefsRepository(Path.Combine(ClientDirectory, "ui.json")));
            _motion = NcwMotion.Bind(stores);   // 动效级别:偏好 → Arc 主题(docs/design/03 §9)
            stores.Startup.Reach(StartupMilestone.PlatformReady);
            var mobileShell = new ShellViewModel(stores, devices: devices, host: MobileHostFactory?.Invoke());
            mobileShell.Settings.Appearance.SystemReducesMotion = () => NcwMotion.SystemReducesMotionProbe();
            var adaptive = new Views.AdaptiveShell { DataContext = mobileShell };
            adaptive.AttachedToVisualTree += (_, _) => stores.Startup.Reach(StartupMilestone.WindowShown);
            single.MainView = adaptive;
            AttachMobileLinks(mobileShell);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private AccountService? _account;

    /// <summary>
    /// 手机宿主把系统转来的 <c>nextcowork://</c> 链接(Android intent-filter / iOS URL scheme)投进 <see cref="NcwLinkInbox"/>,这里路由:
    /// 配对二维码 → 扫码页待确认、<c>inbox</c> → 收件箱、登录回调 → 账户流程(手机壳还没有账户页时如实忽略)。链接不可信,不会自动配对。
    /// </summary>
    private static void AttachMobileLinks(ShellViewModel shell)
    {
        var handler = new MobileLinkHandler(shell);
        NcwLinkInbox.Attach(
            raw => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = handler.HandleAsync(raw)),
            static () => { });
    }

    /// <summary>宿主在启动前设置:创建账户刷新令牌的系统钥匙串保管(macOS Keychain / Windows DPAPI)。为 null 时拒绝保存刷新令牌(登录只在本次运行里有效,并提示用户)。</summary>
    public static Func<IRefreshTokenVault>? RefreshTokenVaultFactory { get; set; }

    /// <summary><c>NCW_ACCOUNT_ORIGIN</c>:账户后端的地址(开发 / 预发布环境用);默认 <c>https://nextco.work/</c>。</summary>
    private static AccountEndpoints AccountEndpointsFromEnvironment() =>
        Environment.GetEnvironmentVariable("NCW_ACCOUNT_ORIGIN") is { Length: > 0 } origin && Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? AccountEndpoints.Default with { Origin = uri }
            : AccountEndpoints.Default;

    /// <summary>
    /// 把系统转来的 <c>nextcowork://</c> 链接接到链接路由(macOS 的 OpenUri 激活;Windows / Linux 由单实例转发和启动参数经 <see cref="NcwLinkInbox"/> 到这里),
    /// 并在收到链接或「回到前台」请求时把主窗口带到前台。链接本身不可信:解析和白名单在 <see cref="NcwLinkParser"/>,登录靠 state 校验,配对要用户确认。
    /// </summary>
    private void AttachLinks(IClassicDesktopStyleApplicationLifetime desktop, LinkRouter router)
    {
        NcwLinkInbox.Attach(
            raw => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = router.HandleAsync(raw)),
            () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (desktop.MainWindow is { } window)
                {
                    if (window.WindowState == Avalonia.Controls.WindowState.Minimized)
                    {
                        window.WindowState = Avalonia.Controls.WindowState.Normal;
                    }

                    window.Activate();
                }
            }));
        if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e is ProtocolActivatedEventArgs protocol)
                {
                    NcwLinkInbox.Submit("link " + protocol.Uri.OriginalString);
                }
            };
        }
    }

    /// <summary>模拟数据模式:账户显示成已登录的演示用户(不连任何后端)。</summary>
    private static void MockAccount(ClientStores stores) =>
        stores.Account.Update(s => s with
        {
            Mode = AccountMode.Authenticated,
            Session = new AccountSession(new AccountUser("demo", "demo", "demo@example.com", "Demo", null), [new TeamOption("t1", "Demo Team", "team", "owner", 3, true)], "t1", false),
        });

    /// <summary>手机宿主在启动前设置:创建平台服务(扫码、推送令牌、附件选择)。桌面不设置。</summary>
    public static Func<MobileHostServices>? MobileHostFactory { get; set; }
}
