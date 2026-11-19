using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ncw.Client.Devices;
using Ncw.Client.Mock;
using Ncw.Client.Stores;
using Ncw.Client.Tests.Updates;
using Ncw.Client.Updates;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.UI.Chrome;
using Ncw.UI.Views.Desktop;
using TKCode.Arc;

namespace Ncw.UI.Tests;

/// <summary>
/// 设置页「关于」(自更新)的 Headless 截图(docs/design/04 §3、07 §3):典型状态(有新版本、下载中)× 四个断点(1500 / 1200 / 900 / 600)× 深浅主题;
/// 其余状态(停用、检查中、已是最新、待重启、安装中、必需更新的宽限期与阻断、签名被篡改、网络失败、已知更新之上的检查失败、测试版通道)在宽屏深色和窄屏浅色各拍一张。
/// 状态都经真实的 <see cref="UpdateService"/> + 假引擎 + 手动时钟走出来,不是手摆的。截图在 <c>bin/Debug/net11.0/screenshots/about-*.png</c>。
/// </summary>
public class AboutScreenshotTests
{
    private sealed record Host(Window Window, ShellViewModel Shell, DesktopShell View, UpdateRig Rig);

    private sealed record Scenario(string Name, Func<UpdateRig, Task> Seed, Action<Host>? Check = null, double Height = 1100, bool Installed = true, string Current = "3.0.0");

    private static readonly ReleaseManifest V301 = UpdateKit.Manifest("3.0.1", notes: "Bug fixes and a faster start.\n- Sessions open quicker\n- Fixed a crash when the network drops");

    private static Host Render(Scenario scenario, double width, bool dark)
    {
        ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light).MotionLevel(TKCode.Arc.Motion.ArcMotionLevel.Off));
        var stores = MockWorkspace.Create(UpdateRig.Start);
        var rig = new UpdateRig(stores: stores, current: scenario.Current, installed: scenario.Installed);
        rig.Service.InitializeAsync().GetAwaiter().GetResult();
        scenario.Seed(rig).GetAwaiter().GetResult(); // 窗口建好之前走完状态:服务在这个线程上发布(InlineDispatcher),界面还没挂上
        var shell = new ShellViewModel(stores, rig.Clock, devices: new DeviceRegistry(), updates: rig.Service);
        var view = new DesktopShell { DataContext = shell, Chrome = new WindowChrome(78, false) };
        var window = new Window { Width = width, Height = scenario.Height, Content = view };
        window.Show();
        Dispatch(8);
        shell.Settings.OpenCommand.Execute(null);
        shell.Settings.ShowCommand.Execute(SettingsSection.About);
        Dispatch(10);
        return new Host(window, shell, view, rig);
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
        host.Window.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"about-{name}-{width}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static void Finish() => ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark).MotionLevel(TKCode.Arc.Motion.ArcMotionLevel.Off));

    private static IEnumerable<T> Find<T>(Host host) where T : Avalonia.Visual => host.Window.GetVisualDescendants().OfType<T>().Where(v => v.IsEffectivelyVisible);

    private static T Named<T>(Host host, string name) where T : Avalonia.Visual => host.Window.GetVisualDescendants().OfType<T>().First(v => (v as Control)?.Name == name);

    private static bool Visible(Host host, string name) => host.Window.GetVisualDescendants().OfType<Control>().Any(c => c.Name == name && c.IsEffectivelyVisible);

    // ───────────────────────── 状态怎么走出来 ─────────────────────────

    private static async Task Available(UpdateRig rig)
    {
        rig.Engine.Offering(V301);
        await rig.Service.CheckAsync();
    }

    private static async Task Downloading(UpdateRig rig)
    {
        await Available(rig);
        rig.Engine.ProgressSteps = [37];
        rig.Engine.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); // 不放行:停在下载中
        _ = rig.Service.DownloadAsync();
    }

    private static async Task Ready(UpdateRig rig)
    {
        await Available(rig);
        await rig.Service.DownloadAsync();
    }

    private static void RunningTasks(Host host, int count)
    {
        ActiveRunIndex Index() => new("local", [.. Enumerable.Range(0, count).Select(i => new ActiveRun("local", "r" + i, "s" + i, "w", 0))]);
        host.Shell.Stores.Workspace.Update(w => w with
        {
            Devices = w.Devices.SetItem("local", w.Devices.GetValueOrDefault("local") is { } d
                ? d with { Index = Index(), Connection = DeviceConnection.Connected }
                : new DeviceInfo("local", "Local", DeviceKind.Local, DeviceConnection.Connected, Index())),
        });
    }

    // ───────────────────────── 典型状态 × 断点 × 深浅 ─────────────────────────

    private static readonly Scenario[] Typical =
    [
        new("available", Available, Check: h =>
        {
            Assert.True(h.Shell.Settings.About.CanDownload);
            Assert.True(Visible(h, "StatusAvailable"));
            Assert.True(Visible(h, "DownloadButton"));
            Assert.False(Visible(h, "RestartButton"));
            Assert.Contains("3.0.1", Named<TextBlock>(h, "StatusAvailable").Text);
            Assert.NotEmpty(Find<AboutSettingsPage>(h));
        }),
        new("downloading", Downloading, Check: h =>
        {
            Assert.True(h.Shell.Settings.About.IsDownloading);
            Assert.True(Visible(h, "ProgressRow"));
            Assert.Equal(37, h.Shell.Settings.About.Progress);
            Assert.Equal("37%", Named<TextBlock>(h, "ProgressText").Text);
            Assert.False(Visible(h, "DownloadButton"));
        }),
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
        new("disabled", _ => Task.CompletedTask, Check: h =>
        {
            Assert.True(h.Shell.Settings.About.IsDisabled);
            Assert.True(Visible(h, "StatusDisabled"));
            Assert.False(h.Shell.Settings.About.CheckCommand.CanExecute(null));
        }, Installed: false, Height: 800),
        new("idle", _ => Task.CompletedTask, Check: h => Assert.True(Visible(h, "StatusIdle")), Height: 800),
        new("checking", rig =>
        {
            rig.Engine.Offering(V301);
            rig.Engine.CheckGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = rig.Service.CheckAsync();
            return Task.CompletedTask;
        }, Check: h =>
        {
            Assert.True(Visible(h, "StatusChecking"));
            Assert.False(h.Shell.Settings.About.CheckCommand.CanExecute(null));
        }, Height: 800),
        new("up-to-date", async rig =>
        {
            rig.Engine.Offering(UpdateKit.Manifest("3.0.0"), offer: false);
            await rig.Service.CheckAsync();
        }, Check: h =>
        {
            Assert.True(Visible(h, "StatusUpToDate"));
            Assert.True(Visible(h, "LastCheckedText"));
            Assert.False(Visible(h, "NeverCheckedText"));
        }, Height: 800),
        new("ready", Ready, Check: h =>
        {
            Assert.True(Visible(h, "RestartButton"));
            Assert.True(Visible(h, "StatusReady"));
            RunningTasks(h, 2);
            Dispatch(6);
            Assert.True(Visible(h, "RunWarning"));
            Assert.Contains("2", Named<TextBlock>(h, "RunWarning").Text);
        }),
        new("installing", async rig =>
        {
            await Ready(rig);
            await rig.Service.RestartAndApplyAsync();
        }, Check: h =>
        {
            Assert.True(Visible(h, "StatusInstalling"));
            Assert.False(h.Shell.Settings.About.RestartCommand.CanExecute(null));
        }),
        new("required-grace", async rig =>
        {
            rig.Engine.Offering(UpdateKit.Manifest("3.0.1", mandatory: true, minimum: "3.0.1", grace: UpdateRig.Start.AddDays(5), notes: "Security fixes."));
            await rig.Service.CheckAsync();
        }, Check: h =>
        {
            Assert.True(Visible(h, "RequiredCard"));
            Assert.False(Visible(h, "BlockingCard"));
            Assert.True(Visible(h, "GraceText"));
        }, Height: 1300),
        new("blocking", async rig =>
        {
            rig.Engine.Offering(UpdateKit.Manifest("3.0.1", mandatory: true, minimum: "3.0.1", notes: "Security fixes."));
            await rig.Service.CheckAsync();
            await rig.Service.DownloadAsync();
        }, Check: h =>
        {
            Assert.True(Visible(h, "BlockingCard"));
            Assert.False(Visible(h, "RequiredCard"));
            Assert.Contains("3.0.0", Named<TextBlock>(h, "BlockingHint").Text);
            Assert.Contains("3.0.1", Named<TextBlock>(h, "BlockingHint").Text);
            Assert.True(Visible(h, "RestartButton"));
        }, Height: 1300),
        new("signature-invalid", rig =>
        {
            rig.Engine.CheckFailure = new UpdateException(UpdateFailureKind.SignatureInvalid, "tampered");
            return rig.Service.CheckAsync();
        }, Check: h =>
        {
            Assert.True(Visible(h, "TrustCard"));
            Assert.False(Visible(h, "FailureCard"));
            Assert.False(Visible(h, "DownloadButton"));
            Assert.True(h.Shell.Settings.About.CheckCommand.CanExecute(null)); // 可以重试
        }, Height: 1000),
        new("network-failure", rig =>
        {
            rig.Engine.CheckFailure = new UpdateException(UpdateFailureKind.Network, "refused");
            return rig.Service.CheckAsync();
        }, Check: h =>
        {
            Assert.True(Visible(h, "FailureCard"));
            Assert.False(Visible(h, "TrustCard"));
        }, Height: 900),
        new("failed-over-known-update", async rig =>
        {
            await Available(rig);
            rig.Engine.CheckFailure = new UpdateException(UpdateFailureKind.SignatureInvalid, "tampered");
            await rig.Service.CheckAsync();
        }, Check: h =>
        {
            Assert.True(Visible(h, "TrustCard"));
            Assert.True(Visible(h, "DownloadButton")); // 之前验过签名的更新还在
        }, Height: 1300),
        new("beta-channel", async rig =>
        {
            rig.Engine.OnCheck = channel => new UpdateCheckResult(UpdateKit.Manifest("3.1.0-beta.2", channel: "beta"), new UpdateOffer("3.1.0-beta.2", UpdateKit.Manifest("3.1.0-beta.2", channel: "beta"), "h"));
            await rig.Service.SetChannelAsync(UpdateChannel.Beta);
        }, Check: h =>
        {
            Assert.True(h.Shell.Settings.About.IsBeta);
            Assert.Contains("3.1.0-beta.2", Named<TextBlock>(h, "StatusAvailable").Text);
        }),
    ];

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
        from scenario in new[] { "available", "blocking", "signature-invalid", "ready" }
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

    // ───────────────────────── 交互 ─────────────────────────

    [AvaloniaFact]
    public void The_buttons_drive_the_service_through_the_actions_and_the_page_follows_the_store()
    {
        var host = Render(Extra.Single(s => s.Name == "idle"), 1200, dark: true);
        host.Rig.Engine.Offering(V301);

        Named<Ncw.UI.Controls.NcwButton>(host, "CheckButton").Command!.Execute(null);
        Dispatch(10);
        Assert.True(Visible(host, "DownloadButton"));

        Named<Ncw.UI.Controls.NcwButton>(host, "DownloadButton").Command!.Execute(null);
        Dispatch(10);
        Assert.True(Visible(host, "RestartButton"));
        Assert.False(Visible(host, "DownloadButton"));

        Named<Ncw.UI.Controls.NcwButton>(host, "RestartButton").Command!.Execute(null);
        Dispatch(10);
        Assert.Equal(["prepare", "exit"], host.Rig.Journal);
        Assert.True(Visible(host, "StatusInstalling"));
        Finish();
    }

    [AvaloniaFact]
    public void The_about_page_has_its_own_entry_in_the_settings_navigation()
    {
        var host = Render(Extra.Single(s => s.Name == "idle"), 1200, dark: false);
        Assert.NotNull(Named<Control>(host, "AboutNav"));
        Assert.True(host.Shell.Settings.IsAboutSection);
        Finish();
    }
}
