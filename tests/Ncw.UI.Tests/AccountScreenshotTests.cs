using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ncw.Client.Accounts;
using Ncw.Client.Devices;
using Ncw.Client.Mock;
using Ncw.Client.Stores;
using Ncw.Client.Tests.Accounts;
using Ncw.Client.Tests.Management;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.UI.Chrome;
using Ncw.UI.Views.Desktop;
using TKCode.Arc;

namespace Ncw.UI.Tests;

/// <summary>
/// 首次启动流程、设置页「账户」标签和账户状态条的 Headless 截图(docs/design/07):
/// 典型状态(欢迎、账户已登录)× 四个断点(1500 / 1200 / 900 / 600)× 深浅主题;其余场景(登录进行中、登录码、失败、过期、连不上 / 无法安全保存、要选团队、
/// 二次确认、读取失败、数据 / 供应商 / 完成各步、状态条)在宽屏深色和窄屏浅色各拍一张。状态经真实的 <see cref="AccountService"/> + 假后端 + 手动时钟走出来,不是手摆的。
/// 截图在 <c>bin/Debug/net10.0/screenshots/acct-*.png</c>。
/// </summary>
public class AccountScreenshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(8));

    private sealed record Host(Window Window, ShellViewModel Shell, DesktopShell View, AccountRig Rig);

    private sealed record Scenario(string Name, Func<AccountRig, ShellViewModel, Task> Seed, Action<ShellViewModel>? Open = null, Func<Host, Task>? After = null, Action<Host>? Check = null, double Height = 1000, bool LocalDevice = true);

    private static Host Render(Scenario scenario, double width, bool dark)
    {
        ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light).MotionLevel(TKCode.Arc.Motion.ArcMotionLevel.Off));
        Ncw.UI.Converters.RelativeTimeConverter.Clock = new FixedClock(Now);
        var stores = MockWorkspace.Create(Now);
        var rig = new AccountRig(stores: stores);
        var devices = new DeviceRegistry();
        if (scenario.LocalDevice)
        {
            devices.Local = new DeviceLink(stores, InlineDispatcher.Instance, "local", DeviceKind.Local, "local", _ => throw new InvalidOperationException(), new ClientHello("tests", "3.0.0"));
        }

        var shell = new ShellViewModel(stores, devices: devices, account: rig.Service, links: new LinkRouter(rig.Service));
        scenario.Seed(rig, shell).GetAwaiter().GetResult(); // 窗口建好之前走完状态:服务在线程池上发布状态,界面还没挂上,不会跨线程
        var view = new DesktopShell { DataContext = shell, Chrome = new WindowChrome(78, false) };
        var window = new Window { Width = width, Height = scenario.Height, Content = view };
        window.Show();
        Dispatch(8);
        scenario.Open?.Invoke(shell);
        Dispatch(10);
        var host = new Host(window, shell, view, rig);
        if (scenario.After is { } after)
        {
            after(host).GetAwaiter().GetResult();
            Dispatch(10);
        }

        return host;
    }

    private static void Dispatch(int rounds = 6)
    {
        for (var i = 0; i < rounds; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Snapshot(Host host, string name, double width, bool dark)
    {
        Dispatch();
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        host.Window.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"acct-{name}-{width}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static void Finish() => ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark).MotionLevel(TKCode.Arc.Motion.ArcMotionLevel.Full));

    private static IEnumerable<T> Find<T>(Host host) where T : Avalonia.Visual => host.Window.GetVisualDescendants().OfType<T>().Where(v => v.IsEffectivelyVisible);

    private static void OpenAccount(ShellViewModel shell)
    {
        shell.Settings.OpenCommand.Execute(null);
        shell.Settings.ShowCommand.Execute(SettingsSection.Account);
    }

    // ───────────────────────── 状态怎么走出来 ─────────────────────────

    private static async Task Fresh(AccountRig rig, ShellViewModel _) => await rig.Service.InitializeAsync();

    private static async Task Decided(AccountRig rig, ShellViewModel shell)
    {
        await rig.Service.InitializeAsync();
        await shell.Onboarding.SkipCommand.ExecuteAsync(null); // 离线,Step 前进到「数据」
    }

    private static async Task SignedIn(AccountRig rig, ShellViewModel shell, params string[] teams)
    {
        if (teams.Length > 1)
        {
            rig.Api.Teams = new TeamContext([.. teams.Select(t => FakeAccountApi.Team(t, "Team " + t))], teams[0]);
        }

        rig.Api.Controllers.AddRange([
            new ControllerSession("c1", "Token's MacBook Pro", "macOS", Now, true),
            new ControllerSession("c2", "iPhone 17", "iOS", Now.AddDays(-2), false),
            new ControllerSession("c3", "Office Windows PC", "Windows", Now.AddHours(-5), false),
        ]);
        await rig.SignInAsync();
        shell.Onboarding.FinishCommand.Execute(null);
    }

    private static async Task Expired(AccountRig rig, ShellViewModel shell)
    {
        await SignedIn(rig, shell, "t1");
        rig.Api.OnRefresh = _ => throw new AccountApiException(AccountApiErrorKind.InvalidGrant, "bad", 400, "invalid_grant");
        rig.Clock.Advance(TimeSpan.FromMinutes(14));
        await rig.Service.RefreshAsync();
    }

    // ───────────────────────── 典型状态 × 断点 × 深浅 ─────────────────────────

    private static readonly Scenario[] Typical =
    [
        new("onb-welcome", Fresh, Check: h =>
        {
            Assert.True(h.Shell.Onboarding.IsOpen);
            Assert.NotEmpty(Find<OnboardingOverlay>(h));
        }, Height: 800),
        new("acct-signedin", (r, s) => SignedIn(r, s, "t1", "t2"), OpenAccount, After: async h => await h.Shell.Account.LoadControllersCommand.ExecuteAsync(null), Check: h =>
        {
            Assert.True(h.Shell.Account.ControllersReady);
            Assert.Equal(3, h.Shell.Account.Controllers.Count);
            Assert.NotEmpty(Find<AccountSettingsPage>(h));
        }, Height: 1300),
    ];

    public static IEnumerable<object[]> TypicalMatrix() =>
        from scenario in Typical.Select(s => s.Name)
        from width in new[] { 1500, 1200, 900, 600 }
        from dark in new[] { true, false }
        select new object[] { scenario, width, dark };

    [AvaloniaTheory]
    [MemberData(nameof(TypicalMatrix))]
    public void Typical_state(string name, int width, bool dark)
    {
        var scenario = Typical.Single(s => s.Name == name);
        var host = Render(scenario, width, dark);
        scenario.Check?.Invoke(host);
        Snapshot(host, name, width, dark);
        Finish();
    }

    // ───────────────────────── 其余场景:宽屏深色 + 窄屏浅色 ─────────────────────────

    private static readonly Scenario[] Extra =
    [
        // 首次启动
        new("onb-welcome-browser", Fresh, After: async h => await h.Shell.Account.SignInCommand.ExecuteAsync(null), Check: h =>
        {
            Assert.True(h.Shell.Account.IsBrowserLogin);
            Assert.Single(h.Rig.Launcher.Opened);
        }, Height: 900),
        new("onb-welcome-code", Fresh, After: async h => await h.Shell.Account.SignInWithCodeCommand.ExecuteAsync(null), Check: h => Assert.Equal("ABCD-EFGH", h.Shell.Account.UserCode), Height: 900),
        new("onb-welcome-error", Fresh, After: async h =>
        {
            h.Rig.Launcher.Fail = true;
            await h.Shell.Account.SignInCommand.ExecuteAsync(null);
        }, Check: h => Assert.Equal(AccountKeys.BrowserFailed, h.Shell.Account.LoginError), Height: 900),
        new("onb-data-fresh", Decided, Open: s => SetReady(s, null), Check: h => Assert.True(h.Shell.Onboarding.IsDataStep), Height: 700),
        new("onb-data-takeover", Decided, Open: s => SetReady(s, new TakeoverSummary(6, 214, 2)), Check: h => Assert.True(h.Shell.Onboarding.HasTakeover), Height: 700),
        new("onb-data-connecting", Decided, Check: h => Assert.True(h.Shell.Onboarding.DataConnecting), Height: 700, LocalDevice: false),
        new("onb-provider-offline", Decided, Open: s => { SetReady(s, null); s.Onboarding.NextCommand.Execute(null); }, Check: h => Assert.True(h.Shell.Onboarding.NeedsOwnProvider), Height: 800),
        new("onb-provider-account", async (r, s) =>
        {
            await r.Service.InitializeAsync();
            await r.Service.BeginBrowserLoginAsync();
            await r.Service.HandleLinkAsync(new AuthCallbackLink("C", r.Launcher.Query("state"), null, null));
        }, Open: s => { SetReady(s, null); s.Onboarding.NextCommand.Execute(null); }, Check: h => Assert.True(h.Shell.Onboarding.HasAccountModels), Height: 800),
        new("onb-done", Decided, Open: s => { SetReady(s, null); s.Onboarding.NextCommand.Execute(null); s.Onboarding.NextCommand.Execute(null); }, Check: h => Assert.True(h.Shell.Onboarding.IsDone), Height: 800),

        // 账户页
        new("acct-offline", Decided, OpenAfterOnboarding, Check: h => Assert.True(h.Shell.Account.IsSignedOut), Height: 800),
        new("acct-login-browser", Decided, OpenAfterOnboarding, After: async h => await h.Shell.Account.SignInCommand.ExecuteAsync(null), Check: h => Assert.True(h.Shell.Account.IsBrowserLogin), Height: 900),
        new("acct-login-code", Decided, OpenAfterOnboarding, After: async h => await h.Shell.Account.SignInWithCodeCommand.ExecuteAsync(null), Check: h => Assert.True(h.Shell.Account.IsDeviceCodeLogin), Height: 900),
        new("acct-login-failed", Decided, OpenAfterOnboarding, After: async h =>
        {
            h.Rig.Launcher.Fail = true;
            await h.Shell.Account.SignInCommand.ExecuteAsync(null);
        }, Check: h => Assert.Equal(AccountBannerKind.LoginFailed, h.Shell.Account.Banner), Height: 900),
        new("acct-expired", Expired, OpenAfterOnboarding, Check: h =>
        {
            Assert.True(h.Shell.Account.IsExpired);
            Assert.True(h.Shell.Account.ControllersSignedOut);
        }, Height: 900),
        new("acct-team-required", (r, s) => SignedInNoTeam(r, s), OpenAfterOnboarding, After: async h => await h.Shell.Account.LoadControllersCommand.ExecuteAsync(null), Check: h => Assert.True(h.Shell.Account.NeedsTeam), Height: 1300),
        new("acct-unreachable", async (r, s) =>
        {
            await SignedIn(r, s, "t1");
            r.Vault.Stored = null;
            r.Stores.Account.Update(a => a with
            {
                Health = SessionHealth.Unreachable,
                SecureStorageUnavailable = true,
                Delegations = a.Delegations.SetItem("local", new DeviceDelegation(DelegationStatus.Failed)),
            });
        }, OpenAfterOnboarding, Check: h => Assert.True(h.Shell.Account.SecureStorageUnavailable), Height: 1100),
        new("acct-controllers-error", async (r, s) =>
        {
            await SignedIn(r, s, "t1");
            r.Stores.Account.Update(a => a with { Delegations = a.Delegations.SetItem("local", new DeviceDelegation(DelegationStatus.Attached, Now.AddMinutes(30))) });
        }, OpenAfterOnboarding, After: async h =>
        {
            h.Rig.Api.OnRefresh = _ => throw new AccountApiException(AccountApiErrorKind.Network, "down");
            h.Rig.Clock.Advance(TimeSpan.FromMinutes(16)); // 访问令牌过期,后台刷新又连不上
            await h.Shell.Account.LoadControllersCommand.ExecuteAsync(null);
        }, Check: h => Assert.True(h.Shell.Account.ControllersFailed), Height: 1200),
        new("acct-signout-confirm", (r, s) => SignedIn(r, s, "t1"), OpenAfterOnboarding, After: async h =>
        {
            await h.Shell.Account.LoadControllersCommand.ExecuteAsync(null);
            h.Shell.Account.AskSignOutCommand.Execute(null);
        }, Check: h => Assert.True(h.Shell.Account.ConfirmingSignOut), Height: 1200),
        new("acct-revoke-confirm", (r, s) => SignedIn(r, s, "t1"), OpenAfterOnboarding, After: async h =>
        {
            await h.Shell.Account.LoadControllersCommand.ExecuteAsync(null);
            h.Shell.Account.AskRevokeCommand.Execute(h.Shell.Account.Controllers[1]);
        }, Check: h => Assert.True(h.Shell.Account.HasConfirmingRevoke), Height: 1300),

        // 状态条(主界面上)
        new("bar-expired", Expired, Check: h =>
        {
            Assert.Equal(AccountBannerKind.Expired, h.Shell.Account.Banner);
            Assert.NotEmpty(Find<AccountStatusBar>(h));
        }, Height: 800),
        new("bar-signing-in", async (r, s) =>
        {
            await SignedIn(r, s, "t1");
            await r.Service.BeginBrowserLoginAsync();
        }, Check: h => Assert.Equal(AccountBannerKind.SigningIn, h.Shell.Account.Banner), Height: 800),
        new("bar-failed", async (r, s) =>
        {
            await Decided(r, s);
            s.Onboarding.FinishCommand.Execute(null);
            r.Launcher.Fail = true;
            await r.Service.BeginDeviceCodeLoginAsync();
            r.Api.DeviceStartFailure = new AccountApiException(AccountApiErrorKind.Unsupported, "404", 404);
            await r.Service.BeginDeviceCodeLoginAsync();
        }, Check: h => Assert.Equal(AccountKeys.DeviceCodeUnavailable, h.Shell.Account.LoginError), Height: 800),
    ];

    private static async Task SignedInNoTeam(AccountRig rig, ShellViewModel shell)
    {
        rig.Api.Teams = new TeamContext([FakeAccountApi.Team("t1", "Acme"), FakeAccountApi.Team("t2", "Beta Labs"), FakeAccountApi.Team("t3", "Personal")], null);
        await rig.SignInAsync();
        shell.Onboarding.FinishCommand.Execute(null);
    }

    /// <summary>账户页要在首次启动流程结束之后看(流程盖在设置页下面,但这样更接近真实使用)。</summary>
    private static void OpenAfterOnboarding(ShellViewModel shell)
    {
        shell.Onboarding.FinishCommand.Execute(null);
        OpenAccount(shell);
    }

    private static void SetReady(ShellViewModel shell, TakeoverSummary? summary)
    {
        shell.Stores.Workspace.Update(s => s with
        {
            Devices = s.Devices.SetItem("local", s.Devices["local"] with { Data = new DataStatus(DataStates.Ready, null, null, summary) }),
        });
        shell.DataGate.ContinueCommand.Execute(null); // 接管摘要的数据页点「继续」之后,首次启动的「数据」一步显示结果
    }

    public static IEnumerable<object[]> ExtraMatrix() =>
        from scenario in Extra.Select(s => s.Name)
        from view in new[] { (1500, true), (600, false) }
        select new object[] { scenario, view.Item1, view.Item2 };

    [AvaloniaTheory]
    [MemberData(nameof(ExtraMatrix))]
    public void Other_states(string name, int width, bool dark)
    {
        var scenario = Extra.Single(s => s.Name == name);
        var host = Render(scenario, width, dark);
        scenario.Check?.Invoke(host);
        Snapshot(host, name, width, dark);
        Finish();
    }

    // ───────────────────────── 英文界面(文字更长,看有没有溢出 / 截断) ─────────────────────────

    public static IEnumerable<object[]> EnglishMatrix() =>
        from scenario in new[] { "onb-welcome-code", "onb-provider-offline", "acct-signedin", "acct-expired", "acct-login-failed", "bar-expired" }
        from width in new[] { 1200, 600 }
        select new object[] { scenario, width };

    [AvaloniaTheory]
    [MemberData(nameof(EnglishMatrix))]
    public void English_ui_fits(string name, int width)
    {
        TKCode.Arc.Localization.ArcLanguage.Set(TKCode.Arc.Localization.LanguageTags.EnUS);
        try
        {
            var scenario = Typical.Concat(Extra).Single(s => s.Name == name);
            var host = Render(scenario, width, dark: false);
            scenario.Check?.Invoke(host);
            Snapshot(host, name + "-en", width, dark: false);
        }
        finally
        {
            TKCode.Arc.Localization.ArcLanguage.Set(TKCode.Arc.Localization.LanguageTags.ZhCN);
            Finish();
        }
    }
}
