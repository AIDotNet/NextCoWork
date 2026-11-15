using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.UI.Chrome;
using Ncw.UI.Controls;
using Ncw.UI.Localization;
using Ncw.UI.Views;
using Ncw.UI.Views.Desktop;
using Ncw.UI.Views.Mobile;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Localization;
using TKCode.Arc.Motion;

namespace Ncw.UI.Tests;

/// <summary>
/// 启动加载界面(docs/design/03 §10):真实的 Store + Recorder + StartupViewModel + 视图,时钟和动画帧都由测试推进。
/// 覆盖:各阶段的版式(四个断点 × 深浅主题,截图在 <c>screenshots/startup-*.png</c>)、慢启动提示、失败面板、数据页让位、手机壳的简化版式、
/// 动画中间帧(标识弧长、阶段打勾、淡出透明度)、淡出之后的释放、减弱 / 关闭级别终态一致、窗口外框与拖动区域、中英文。
/// </summary>
public class StartupOverlayTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualFrameClock _frames = new();
    private readonly IDisposable _install;

    public StartupOverlayTests() => _install = _frames.Install();

    public void Dispose()
    {
        _install.Dispose();
        NcwMotion.Apply(UiMotionLevel.Off);
        ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark));
        ArcLanguage.Set(LanguageTags.EnUS);
    }

    // ───────────────────────── 脚手架 ─────────────────────────

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = T0;

        public void Advance(double ms) => Now = Now.AddMilliseconds(ms);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeHost : IStartupHost
    {
        public int Retries { get; private set; }

        public void Retry() => Retries++;

        public string? Diagnostics() => "agent log tail";
    }

    private sealed class FakeFiles : ILocalFiles
    {
        public string? Copied { get; private set; }

        public Task CopyTextAsync(string text)
        {
            Copied = text;
            return Task.CompletedTask;
        }

        public Task<bool> RevealAsync(string absolutePath) => Task.FromResult(false);

        public Task<bool> OpenAsync(string absolutePath) => Task.FromResult(false);

        public Task<bool> SaveAsync(string suggestedName, byte[] data) => Task.FromResult(false);
    }

    private sealed class Rig
    {
        public Clock Clock { get; } = new();

        public ClientStores Stores { get; }

        public ShellViewModel Vm { get; }

        public FakeHost Host { get; } = new();

        public int Exits { get; private set; }

        public Window Window { get; }

        public Control Root { get; }

        public StartupViewModel Startup => Vm.Startup;

        public Rig(double width, bool dark, UiMotionLevel level = UiMotionLevel.Off, bool mobile = false, double height = 860, WindowChrome? chrome = null, StartupProfile profile = StartupProfile.Desktop)
        {
            ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
            NcwMotion.Apply(level);
            Stores = new ClientStores(Clock);
            var devices = new DeviceRegistry
            {
                Local = new DeviceLink(Stores, InlineDispatcher.Instance, "local", DeviceKind.Local, "local", _ => throw new InvalidOperationException(), new ClientHello("tests", "3.0.0")),
            };
            Stores.Startup.Begin(profile);
            Stores.Startup.Reach(StartupMilestone.WindowShown);   // 窗口在起点就显示出来了(真实的应用里是 MainWindow.Opened);延迟出现从这一刻算
            Vm = new ShellViewModel(Stores, Clock, devices: devices) { StartupHost = Host, RequestExit = () => Exits++ };
            Root = mobile
                ? new AdaptiveShell { DataContext = Vm }
                : new DesktopShell { DataContext = Vm, Chrome = chrome ?? new WindowChrome(78, false) };
            Window = new Window { Width = width, Height = height, Content = Root };
            Window.Show();
            Pump();
        }

        public StartupOverlay? Overlay => Window.GetVisualDescendants().OfType<StartupOverlay>().SingleOrDefault();

        public void Reach(params StartupMilestone[] milestones)
        {
            foreach (var m in milestones)
            {
                Stores.Startup.Reach(m);
            }

            Pump();
        }

        public void AppReady() => Reach(StartupMilestone.InstanceReady, StartupMilestone.WindowShown, StartupMilestone.PlatformReady, StartupMilestone.AccountDecided);

        public void Sidecar(StartupSidecarPhase phase, string? issue = null, int restarts = 0)
        {
            Stores.Startup.SetSidecar(new StartupSidecar(phase, issue, restarts, Detail: issue is null ? null : "detail"));
            Pump();
        }

        public void Device(DeviceConnection connection, DataStatus? data = null)
        {
            Stores.Workspace.Update(s => s with
            {
                Devices = s.Devices.SetItem("local", new DeviceInfo("local", "local", DeviceKind.Local, connection, null, connection == DeviceConnection.Connected ? data : null)),
            });
            Pump();
        }

        /// <summary>推进时钟并让 ViewModel 在到点时重算(视图的一次性定时器做的事)。</summary>
        public void Wait(double ms)
        {
            Clock.Advance(ms);
            Startup.Tick();
            Pump();
        }

        public void ReachAgent()
        {
            AppReady();
            Sidecar(StartupSidecarPhase.Running);
            Reach(StartupMilestone.AgentSpawned, StartupMilestone.AgentReady);
        }

        public void LoadEverything()
        {
            ReachAgent();
            Device(DeviceConnection.Connected, Ready);
            Reach(StartupMilestone.TopicsSynced, StartupMilestone.ProjectsLoaded, StartupMilestone.SessionsLoaded);
        }

        public IEnumerable<string> VisibleTexts() =>
            Overlay is { } o
                ? o.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).Select(t => t.Text!)
                : [];

        public IReadOnlyList<Grid> Rows() => Overlay!.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("step")).ToList();
    }

    private static DataStatus Ready => new(DataStates.Ready, null, null, null);

    private static DataStatus Taking(string step) => new(DataStates.TakingOver, step, null, null);

    private static void Pump(int rounds = 3)
    {
        for (var i = 0; i < rounds; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>最多泵 <paramref name="max"/> 轮,直到条件成立(视图用动画帧回调报告外壳布局,Headless 下要多泵几轮)。</summary>
    private static bool PumpUntil(Func<bool> condition, int max = 60)
    {
        for (var i = 0; i < max && !condition(); i++)
        {
            Pump(1);
        }

        return condition();
    }

    private void Frames(int ms)
    {
        for (var elapsed = 0; elapsed < ms; elapsed += 16)
        {
            _frames.Advance(TimeSpan.FromMilliseconds(Math.Min(16, ms - elapsed)));
            Pump(2);
        }

        Pump();
    }

    private static void Snapshot(Window window, string scenario, double width, bool dark, string suffix = "")
    {
        Pump();
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"startup-{scenario}-{width}-{(dark ? "dark" : "light")}{suffix}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    public static IEnumerable<object[]> Matrix() =>
        from width in new[] { 1500, 1200, 900, 600 }
        from dark in new[] { true, false }
        select new object[] { width, dark };

    private static NcwButton RetryButton(Rig rig) => rig.Overlay!.GetVisualDescendants().OfType<NcwButton>().First(b => Equals(b.Content, NcwTexts.Get(NcwTextKind.Retry)));

    // ───────────────────────── 阶段版式:四个断点 × 深浅 ─────────────────────────

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Each_stage_renders_with_its_real_state_at_every_breakpoint_and_theme(int width, bool dark)
    {
        var rig = new Rig(width, dark);

        // 第一帧:只有底色,没有内容(延迟出现)
        Assert.True(rig.Overlay!.IsVisible);
        Assert.False(rig.Startup.IsContentVisible);
        Assert.DoesNotContain("NextCoWork", rig.VisibleTexts());
        Snapshot(rig.Window, "first-frame", width, dark);

        // 正在启动 Agent
        rig.AppReady();
        rig.Sidecar(StartupSidecarPhase.Starting);
        rig.Reach(StartupMilestone.AgentSpawned);
        rig.Wait(300);
        Assert.True(rig.Startup.IsContentVisible);
        Assert.Contains(NcwTexts.Get(NcwTextKind.StartupDetailAgentHandshake), rig.VisibleTexts());
        if (width >= 720)                                 // 窄于 720 不画整张清单(compact),清单的行也不会被创建
        {
            var rows = rig.Rows();
            Assert.Equal(5, rows.Count);
            Assert.Contains("done", rows[0].Classes);
            Assert.Contains("active", rows[1].Classes);
            Assert.DoesNotContain("active", rows[2].Classes);
            Assert.DoesNotContain("done", rows[2].Classes);
        }
        else
        {
            Assert.Contains("compact", rig.Overlay!.Classes);
            Assert.Contains(NcwTexts.Get(NcwTextKind.StartupStageAgent), rig.VisibleTexts());   // 当前阶段的名字
        }

        Snapshot(rig.Window, "agent", width, dark);

        // 连接设备
        rig.Sidecar(StartupSidecarPhase.Running);
        rig.Reach(StartupMilestone.AgentReady);
        Assert.Contains(NcwTexts.Get(NcwTextKind.StartupDetailConnecting), rig.VisibleTexts());
        Snapshot(rig.Window, "connect", width, dark);

        // 数据迁移中(带确定进度:四段里点亮了前三段)
        rig.Device(DeviceConnection.Connected, Taking(DataSteps.Committing));
        Assert.True(rig.Startup.IsDeterminate);
        Assert.Contains(NcwTexts.Get(NcwTextKind.DataStepCommitting), rig.VisibleTexts());
        var segs = rig.Overlay!.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("seg")).ToList();
        Assert.Equal(4, segs.Count);
        Assert.Equal([true, true, true, false], segs.Select(s => s.Classes.Contains("on")));
        Assert.True(segs[0].IsEffectivelyVisible);
        Snapshot(rig.Window, "takeover", width, dark);

        // 加载首批数据
        rig.Device(DeviceConnection.Connected, Ready);
        rig.Reach(StartupMilestone.TopicsSynced, StartupMilestone.ProjectsLoaded);
        Assert.Contains(NcwTexts.Get(NcwTextKind.StartupDetailLoadingSessions), rig.VisibleTexts());
        Assert.False(segs[0].IsEffectivelyVisible);   // 不在接管了:四段进度收起
        Snapshot(rig.Window, "content", width, dark);

        // 慢启动:一行温和的提示(不是错误面板)
        rig.Wait(9_000);
        Assert.True(rig.Startup.IsSlow);
        Assert.Contains(NcwTexts.Get(NcwTextKind.StartupSlowContent), rig.VisibleTexts());
        Assert.False(rig.Startup.IsFailed);
        Snapshot(rig.Window, "slow", width, dark);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void A_sidecar_that_cannot_start_shows_a_clear_error_panel_instead_of_spinning(int width, bool dark)
    {
        var rig = new Rig(width, dark);
        rig.AppReady();

        rig.Sidecar(StartupSidecarPhase.Failed, "agent-not-found");

        Assert.True(rig.Startup.IsFailed);
        var texts = rig.VisibleTexts().ToList();
        Assert.Contains(NcwTexts.Get(NcwTextKind.StartupFailTitleAgentNotFound), texts);
        Assert.Contains(NcwTexts.Get(NcwTextKind.StartupFailHintAgentNotFound), texts);
        Assert.DoesNotContain(rig.Rows(), r => r.IsEffectivelyVisible);                  // 没有清单,没有转圈
        Assert.False(rig.Overlay!.FindControl<NcwProgressArc>("Comet")!.IsEffectivelyVisible);
        Assert.Equal(StartupPhase.Failed, rig.Startup.Phase);
        var buttons = rig.Overlay!.GetVisualDescendants().OfType<NcwButton>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string).ToList();
        Assert.Contains(NcwTexts.Get(NcwTextKind.Retry), buttons);
        Assert.Contains(NcwTexts.Get(NcwTextKind.StartupShowDiagnostics), buttons);
        Assert.Contains(NcwTexts.Get(NcwTextKind.StartupCopyDiagnostics), buttons);
        Assert.Contains(NcwTexts.Get(NcwTextKind.StartupQuit), buttons);
        Snapshot(rig.Window, "failure-agent-not-found", width, dark);

        // 版本不匹配:重试没用,不给「重试」
        rig.Sidecar(StartupSidecarPhase.Failed, "client-outdated");
        var versionButtons = rig.Overlay!.GetVisualDescendants().OfType<NcwButton>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string).ToList();
        Assert.DoesNotContain(NcwTexts.Get(NcwTextKind.Retry), versionButtons);
        Assert.Contains(NcwTexts.Get(NcwTextKind.StartupFailTitleClientOutdated), rig.VisibleTexts());
        Snapshot(rig.Window, "failure-version", width, dark);

        // 展开诊断
        rig.Startup.ToggleDiagnosticsCommand.Execute(null);
        Frames(400);
        Assert.Contains(rig.Overlay!.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.IsEffectivelyVisible && t.Text!.Contains("client-outdated"));
        Snapshot(rig.Window, "failure-diagnostics", width, dark);
    }

    public static IEnumerable<object[]> MasterKeyMatrix() =>
        from reason in new[] { "master-key-missing", "master-key-unreadable" }
        from width in new[] { 1500, 1200, 900, 600 }
        from dark in new[] { true, false }
        select new object[] { reason, width, dark };

    [AvaloniaTheory]
    [MemberData(nameof(MasterKeyMatrix))]
    public void A_refused_start_for_the_master_key_explains_the_cause_in_plain_words_and_offers_retry_diagnostics_copy_and_quit(string reason, int width, bool dark)
    {
        var rig = new Rig(width, dark);
        rig.AppReady();
        const string dataRoot = "/Users/someone/.next-cowork/data";

        // 守护第一次拉起就收到了 Agent 的拒绝(退出码 65):没有重启,第一帧就是失败面板
        rig.Stores.Startup.SetSidecar(new StartupSidecar(StartupSidecarPhase.Failed, reason, 0, Detail: $"agent refused to start (exit code 65): agent.masterKey.{(reason == "master-key-missing" ? "missing" : "unreadable")} credential.key; data root: {dataRoot}"));
        Pump();

        Assert.True(rig.Startup.IsFailed);
        Assert.Equal(reason, rig.Startup.FailureReason);
        Assert.True(rig.Startup.CanRetry);
        var missing = reason == "master-key-missing";
        var texts = rig.VisibleTexts().ToList();
        Assert.Contains(NcwTexts.Get(missing ? NcwTextKind.StartupFailTitleMasterKeyMissing : NcwTextKind.StartupFailTitleMasterKeyUnreadable), texts);
        var hint = NcwTexts.Get(missing ? NcwTextKind.StartupFailHintMasterKeyMissing : NcwTextKind.StartupFailHintMasterKeyUnreadable);
        Assert.Contains(hint, texts);
        Assert.Contains("credential.key", hint);                                                          // 说清楚是哪个文件
        Assert.DoesNotContain(NcwTexts.Get(NcwTextKind.StartupFailTitleCrashLoop), texts);                // 不是「崩溃循环」
        Assert.DoesNotContain(NcwTexts.Get(NcwTextKind.StartupFailTitleUnknown), texts);
        Assert.DoesNotContain(rig.Rows(), r => r.IsEffectivelyVisible);                                   // 没有清单,没有转圈
        Assert.False(rig.Overlay!.FindControl<NcwProgressArc>("Comet")!.IsEffectivelyVisible);
        var buttons = rig.Overlay!.GetVisualDescendants().OfType<NcwButton>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string).ToList();
        Assert.Contains(NcwTexts.Get(NcwTextKind.Retry), buttons);
        Assert.Contains(NcwTexts.Get(NcwTextKind.StartupShowDiagnostics), buttons);
        Assert.Contains(NcwTexts.Get(NcwTextKind.StartupCopyDiagnostics), buttons);
        Assert.Contains(NcwTexts.Get(NcwTextKind.StartupQuit), buttons);
        Snapshot(rig.Window, $"failure-{reason}", width, dark);

        // 诊断里有数据目录和文件名,没有任何密钥内容(这里本来就没有:事实里只有结构化的 key 和字段名)
        rig.Startup.ToggleDiagnosticsCommand.Execute(null);
        Frames(400);
        Assert.Contains(rig.Overlay!.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.IsEffectivelyVisible && t.Text!.Contains(dataRoot) && t.Text.Contains("credential.key") && t.Text.Contains(reason));
        Snapshot(rig.Window, $"failure-{reason}-diagnostics", width, dark);

        // 重试:一个动作;用户把密钥放回去之后守护重新拉起,失败面板消失
        RetryButton(rig).Command!.Execute(null);
        Assert.Equal(1, rig.Host.Retries);
        rig.Sidecar(StartupSidecarPhase.Starting);
        Assert.False(rig.Startup.IsFailed);
        Assert.DoesNotContain(NcwTexts.Get(missing ? NcwTextKind.StartupFailTitleMasterKeyMissing : NcwTextKind.StartupFailTitleMasterKeyUnreadable), rig.VisibleTexts());
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_phone_shell_ignores_the_master_key_reasons_because_there_is_no_local_agent(bool dark)
    {
        var rig = new Rig(390, dark, mobile: true, profile: StartupProfile.Remote);
        rig.Stores.Startup.SetSidecar(new StartupSidecar(StartupSidecarPhase.Failed, "master-key-missing", 0, Detail: "x"));
        Pump();

        Assert.False(rig.Startup.IsFailed);   // 手机壳没有本机 Agent:设备连不上不是启动失败,也不崩
    }

    [AvaloniaTheory]
    [InlineData(1200, true)]
    [InlineData(1200, false)]
    public void Data_that_is_blocked_hands_the_screen_to_the_data_page_without_two_full_screen_layers(int width, bool dark)
    {
        var rig = new Rig(width, dark);
        rig.Wait(300);
        rig.ReachAgent();
        rig.Device(DeviceConnection.Connected, Taking(DataSteps.BackingUp));
        var gate = rig.Window.GetVisualDescendants().OfType<DataGateOverlay>().Single();
        Assert.True(rig.Overlay!.IsVisible);
        Assert.False(gate.IsVisible);                                   // 接管中:只有加载层,它显示接管步骤

        rig.Device(DeviceConnection.Connected, new DataStatus(DataStates.Blocked, null, DataReasons.LegacyAppRunning, null));

        Assert.False(rig.Startup.OwnsScreen);
        Assert.True(gate.IsVisible);                                    // 需要用户处理:数据页接管整窗
        Assert.False(rig.Overlay.IsVisible);
        Assert.Single(new Control[] { gate, rig.Overlay! }, c => c.IsVisible);
        Snapshot(rig.Window, "handoff-blocked", width, dark);

        rig.Device(DeviceConnection.Connected, Taking(DataSteps.BackingUp));   // 用户点了数据页的「重试」
        Assert.True(rig.Overlay.IsVisible);
        Assert.False(gate.IsVisible);
        Assert.Single(new Control[] { gate, rig.Overlay! }, c => c.IsVisible);
    }

    // ───────────────────────── 动作:重试 / 复制诊断 / 退出 ─────────────────────────

    [AvaloniaFact]
    public void The_error_panel_buttons_run_the_one_action_each_and_a_retry_that_works_clears_the_panel()
    {
        var rig = new Rig(1200, true);
        var files = new FakeFiles();
        rig.Startup.LocalFiles = files;
        rig.AppReady();
        rig.Sidecar(StartupSidecarPhase.Failed, "crash-loop", restarts: 8);

        RetryButton(rig).Command!.Execute(null);
        Assert.Equal(1, rig.Host.Retries);

        var copy = rig.Overlay!.GetVisualDescendants().OfType<NcwButton>().First(b => Equals(b.Content, NcwTexts.Get(NcwTextKind.StartupCopyDiagnostics)));
        copy.Command!.Execute(null);
        Assert.Contains("failure: crash-loop", files.Copied);
        Assert.Contains("agent log tail", files.Copied);

        var quit = rig.Overlay!.GetVisualDescendants().OfType<NcwButton>().First(b => Equals(b.Content, NcwTexts.Get(NcwTextKind.StartupQuit)));
        quit.Command!.Execute(null);
        Assert.Equal(1, rig.Exits);

        rig.Sidecar(StartupSidecarPhase.Starting);   // 守护收到重试,重新拉起
        Assert.False(rig.Startup.IsFailed);
        Assert.DoesNotContain(NcwTexts.Get(NcwTextKind.StartupFailTitleCrashLoop), rig.VisibleTexts());
    }

    // ───────────────────────── 手机壳:同一个 Core 纯函数与 Store 的简化版式 ─────────────────────────

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_phone_shell_shows_the_simplified_loading_layout(bool dark)
    {
        var rig = new Rig(390, dark, mobile: true, height: 800, profile: StartupProfile.Remote);
        var shell = (AdaptiveShell)rig.Root;
        Assert.Equal(ShellKind.Mobile, shell.Kind);
        Assert.NotNull(rig.Overlay);
        Assert.True(rig.Overlay!.IsVisible);
        Assert.Equal(3, rig.Startup.Steps.Length);                      // 应用 → 连接 → 内容

        rig.Stores.Workspace.Update(s => s with     // 配对过的设备在连接中(没有设备的手机壳没有什么要等的,会直接进入外壳)
        {
            Devices = s.Devices.SetItem("pc", new DeviceInfo("pc", "pc", DeviceKind.Remote, DeviceConnection.Connecting, null)),
        });
        rig.AppReady();
        rig.Wait(300);

        Assert.Contains("compact", rig.Overlay.Classes);
        var texts = rig.VisibleTexts().ToList();
        Assert.Contains(NcwTexts.Get(NcwTextKind.StartupStageConnect), texts);   // 当前阶段的名字
        Assert.Contains(NcwTexts.Get(NcwTextKind.StartupDetailConnecting), texts);
        Assert.DoesNotContain(rig.Rows(), r => r.IsEffectivelyVisible);          // 没有整张清单
        Snapshot(rig.Window, "mobile-connect", 390, dark);

        // 设备离线不是失败:外壳照常进入
        rig.Stores.Workspace.Update(s => s with
        {
            Devices = s.Devices.SetItem("pc", new DeviceInfo("pc", "pc", DeviceKind.Remote, DeviceConnection.Offline, null)),
        });
        Pump();
        Assert.False(rig.Startup.IsFailed);
        Assert.True(rig.Startup.Phase is StartupPhase.Settling or StartupPhase.Revealing);   // 所有阶段都完成了(视图随后报告外壳布局)
        Snapshot(rig.Window, "mobile-settling", 390, dark);
    }

    // ───────────────────────── 窗口外框与拖动区域 ─────────────────────────

    [AvaloniaFact]
    public void The_window_buttons_stay_above_the_layer_and_the_top_row_press_is_not_swallowed()
    {
        var rig = new Rig(1200, true, chrome: new WindowChrome(0, true));
        rig.Wait(300);
        var shell = (DesktopShell)rig.Root;
        var captions = shell.GetVisualDescendants().OfType<CaptionButtons>().Single();
        Assert.True(captions.IsVisible);
        var children = shell.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "Root").Children;
        var topRight = shell.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "TopRight");
        Assert.True(children.IndexOf(topRight) > children.IndexOf(rig.Overlay!), "caption buttons must be drawn above the loading layer");

        // 窗口按钮的位置上,命中的是按钮而不是加载层
        var point = captions.TranslatePoint(new Point(captions.Bounds.Width - 10, 10), rig.Window)!.Value;
        var hit = rig.Window.InputHitTest(point) as Visual;
        Assert.NotNull(hit);
        Assert.True(captions.IsVisualAncestorOf(hit) || ReferenceEquals(captions, hit), $"hit {hit}");

        // 顶部 40px 里没被控件处理的按下:加载层不拦截(MainWindow 在冒泡到它之后用来拖动窗口)
        var handledAtOverlay = (bool?)null;
        rig.Overlay!.AddHandler(InputElement.PointerPressedEvent, (_, e) => handledAtOverlay = e.Handled, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        rig.Window.MouseDown(new Point(300, 12), MouseButton.Left);
        rig.Window.MouseUp(new Point(300, 12), MouseButton.Left);
        Assert.False(handledAtOverlay);

        // 加载层盖着时右上角的检查器开关不出现(它在加载层之上,会悬在空白上)
        Assert.False(shell.GetVisualDescendants().OfType<NcwButton>().First(b => Equals(Avalonia.Automation.AutomationProperties.GetName(b), NcwTexts.Get(NcwTextKind.RightPanel))).IsVisible);
    }

    // ───────────────────────── 动画中间帧(Full + ManualFrameClock) ─────────────────────────

    [AvaloniaFact]
    public void The_mark_ring_sweeps_with_the_real_progress_and_stops_exactly_on_it()
    {
        var rig = new Rig(1200, true, UiMotionLevel.Full);
        rig.Wait(300);
        var ring = rig.Overlay!.FindControl<NcwProgressArc>("Ring")!;
        Frames(900);
        Assert.Equal(0, ring.DisplayProgress);

        rig.AppReady();                                   // 5 个阶段里的第 1 个完成 → 目标 0.2
        Assert.Equal(0.2, ring.Progress, 6);
        Frames(48);
        Assert.InRange(ring.DisplayProgress, 0.001, 0.199);   // 中间帧:弧在路上,不是瞬间到位
        Assert.True(ring.IsMoving);

        Frames(1500);
        Assert.Equal(0.2, ring.DisplayProgress, 4);
        Assert.False(ring.IsMoving);
        Snapshot(rig.Window, "ring-settled", 1200, true);
    }

    [AvaloniaFact]
    public void A_finished_stage_gets_a_check_that_fades_in_and_its_row_dims_smoothly()
    {
        var rig = new Rig(1200, true, UiMotionLevel.Full);
        rig.Wait(300);
        Frames(900);
        var first = rig.Rows()[0];
        var cell = first.GetVisualDescendants().OfType<Panel>().First(p => p.Width == 16 && p.Height == 16);
        var check = (Panel)cell.Children[0];
        Assert.False(check.IsVisible);                    // 还没完成:没有勾

        rig.AppReady();                                   // App 阶段完成
        Frames(60);
        Assert.True(check.IsVisible);
        Assert.InRange(check.Opacity, 0.01, 0.99);        // 打勾在淡入的途中
        Snapshot(rig.Window, "check-mid", 1200, true);

        Frames(500);
        Assert.Equal(1, check.Opacity);
        Assert.Contains("done", first.Classes);
    }

    [AvaloniaFact]
    public void The_reveal_fades_the_layer_out_and_releases_everything_it_held()
    {
        var rig = new Rig(1200, true, UiMotionLevel.Full);
        rig.Wait(300);                                    // 内容出现(250 ms)
        Frames(500);
        var overlay = rig.Overlay!;
        var released = 0;
        overlay.Released += (_, _) => released++;
        rig.Wait(300);
        rig.LoadEverything();
        PumpUntil(() => rig.Stores.Startup.State.Facts!.Has(StartupMilestone.ShellLaidOut));   // 视图在数据进去后的第一次布局完成、再过一轮低优先级调度后报告「外壳已布局」
        Assert.True(rig.Stores.Startup.State.Facts!.Has(StartupMilestone.ShellLaidOut), "the view reports the shell layout after the first layout pass with the data");
        Assert.False(rig.Startup.IsRevealing);            // 内容出现了:最短停留 600 ms 还没满
        Assert.True(overlay.IsWaitingForWake);            // 一次性定时器在等最短停留结束

        rig.Wait(600);
        Assert.True(rig.Startup.IsRevealing);
        Assert.True(rig.Startup.OwnsScreen);              // 淡出期间整窗仍归它
        Frames(70);
        Assert.InRange(overlay.Opacity, 0.01, 0.99);      // 淡出的中间帧
        Snapshot(rig.Window, "reveal-mid", 1200, true);
        Assert.Equal(0, released);

        Frames(600);
        Assert.Equal(1, released);                        // 淡出完成:报告可交互并摘掉自己
        Assert.True(overlay.IsReleased);
        Assert.Null(rig.Overlay);
        Assert.False(overlay.IsWaitingForWake);
        Assert.True(rig.Stores.Startup.State.Facts!.Has(StartupMilestone.Interactive));
        Assert.Equal(StartupPhase.Done, rig.Startup.Phase);
        Assert.Empty(rig.Window.GetVisualDescendants().OfType<NcwProgressArc>());   // 标识的弧和短弧(含它们的运动值、旋转循环)随之离开界面树
        Snapshot(rig.Window, "revealed", 1200, true);

        // 释放之后:断线重连不再弹加载层
        rig.Device(DeviceConnection.Offline);
        rig.Sidecar(StartupSidecarPhase.Failed, "crash-loop");
        Assert.Null(rig.Overlay);
        Assert.False(rig.Startup.IsFailed);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void The_fade_out_shows_the_finished_shell_underneath_at_every_breakpoint_and_theme(int width, bool dark)
    {
        var rig = new Rig(width, dark, UiMotionLevel.Full);
        Frames(64);
        rig.Wait(300);                                    // 内容出现
        Frames(300);
        rig.LoadEverything();
        PumpUntil(() => rig.Stores.Startup.State.Facts!.Has(StartupMilestone.ShellLaidOut));
        rig.Wait(700);                                    // 最短停留已满:开始淡出
        Assert.True(rig.Startup.IsRevealing);
        var overlay = rig.Overlay!;
        var shell = rig.Window.GetVisualDescendants().OfType<DesktopShell>().Single();
        var before = shell.Bounds;

        Frames(80);
        Assert.InRange(overlay.Opacity, 0.01, 0.99);      // 淡出中间帧:加载层半透明,下面是已经布局好的外壳
        Assert.Equal(before, shell.Bounds);               // 淡出期间外壳没有任何布局变化
        Snapshot(rig.Window, "reveal-mid", width, dark, "-matrix");

        Frames(600);
        Assert.Null(rig.Overlay);
        Assert.Equal(before, shell.Bounds);               // 释放之后也没有跳动
        Snapshot(rig.Window, "revealed", width, dark, "-matrix");
    }

    // ───────────────────────── 入场编排(docs/design/03 §10.4) ─────────────────────────

    private static Control Part(StartupOverlay overlay, string name) => overlay.FindControl<Control>(name)!;

    private static List<Panel> RowShells(StartupOverlay overlay) =>
        overlay.GetVisualDescendants().OfType<Panel>().Where(p => p.Classes.Contains("rowShell")).ToList();

    private static Rig ShownAt(UiMotionLevel level, double width = 1200, bool dark = true)
    {
        var rig = new Rig(width, dark, level);
        return rig;
    }

    [AvaloniaFact]
    public void The_entrance_script_is_built_from_arc_tokens_only_and_runs_top_to_bottom()
    {
        var rig = ShownAt(UiMotionLevel.Full);
        Frames(64);
        rig.Wait(300);                                    // 内容出现:入场开始
        var steps = rig.Overlay!.LastEntrance;

        Assert.Equal(TimeSpan.FromMilliseconds(80), NcwMotion.StaggerLine);   // 就是 Arc 的 token,不是这里的数字
        Assert.Equal(TimeSpan.FromMilliseconds(35), NcwMotion.StaggerItem);
        Assert.Equal(TimeSpan.FromMilliseconds(480), NcwMotion.Duration(NcwMotionTier.Slow));

        var mark = steps[0];
        Assert.Equal((NcwArrival.Grow, TimeSpan.Zero, NcwMotionTier.Slow), (mark.Kind, mark.Delay, mark.Tier));
        Assert.Same(Part(rig.Overlay, "Mark"), mark.Part);

        var title = steps[1];
        Assert.Equal((NcwArrival.Rise, NcwMotion.StaggerLine), (title.Kind, title.Delay));
        Assert.Same(Part(rig.Overlay, "Title"), title.Part);

        var rows = steps.Where(x => x.Part.Classes.Contains("rowShell")).ToList();
        Assert.Equal(5, rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            Assert.Equal(NcwMotion.StaggerLine * 2 + NcwMotion.StaggerItem * i, rows[i].Delay);   // 一行一行错峰往下
            Assert.Equal(NcwArrival.Rise, rows[i].Kind);
        }

        Assert.Same(RowShells(rig.Overlay)[0], rows[0].Part);   // 顺序就是清单从上到下

        var detail = steps.Single(x => ReferenceEquals(x.Part, Part(rig.Overlay, "DetailStack")));
        Assert.Equal(NcwMotion.StaggerLine * 2 + NcwMotion.StaggerItem * 5, detail.Delay);   // 清单进完之后
        var comet = steps.Single(x => ReferenceEquals(x.Part, Part(rig.Overlay, "CometHost")));
        Assert.Equal(NcwMotion.Duration(NcwMotionTier.Slow), comet.Delay);                   // 轨道画完之后才开始转
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_entrance_unfolds_in_order_and_ends_exactly_at_rest(bool dark)
    {
        var rig = ShownAt(UiMotionLevel.Full, 1200, dark);
        Frames(64);
        rig.Wait(300);                                    // 内容出现:入场在这一刻开始(此时还没有任何一帧动画时钟)
        var overlay = rig.Overlay!;
        var mark = Part(overlay, "Mark");
        var title = Part(overlay, "Title");
        var ring = overlay.FindControl<NcwProgressArc>("Ring")!;
        var rows = RowShells(overlay);
        var comet = Part(overlay, "CometHost");

        // t = 0:一切都在起点(透明),轨道还没画;没有「已经完整可见」的一帧
        Assert.True(overlay.FindControl<StackPanel>("ContentRoot")!.IsVisible);
        Assert.Equal(0, mark.Opacity);
        Assert.Equal(0, title.Opacity);
        Assert.All(rows, r => Assert.Equal(0, r.Opacity));
        Assert.Equal(0, ring.TrackSweep);
        Assert.True(overlay.IsEntering);
        Snapshot(rig.Window, "entrance-000ms", 1200, dark);

        // t ≈ 96:标识已经在长出来(缩放在 GrowFrom 和 1 之间),标题刚开始,清单还没开始(第一行的延迟是 160)
        Frames(96);
        Assert.InRange(mark.Opacity, 0.01, 0.99);
        var scale = Assert.IsType<ScaleTransform>(mark.RenderTransform);
        Assert.InRange(scale.ScaleX, NcwMotion.GrowFrom + 0.001, 0.999);
        Assert.InRange(title.Opacity, 0.001, 0.99);
        Assert.True(title.Opacity < mark.Opacity, "the title starts after the mark");
        Assert.All(rows, r => Assert.Equal(0, r.Opacity));
        Assert.InRange(ring.TrackSweep, 0.01, 0.99);
        Snapshot(rig.Window, "entrance-096ms", 1200, dark);

        // t ≈ 224:第一行已经进来了,最后一行(延迟 300)还没开始;一行比一行晚
        Frames(128);
        Assert.InRange(rows[0].Opacity, 0.01, 0.99);
        Assert.Equal(0, rows[4].Opacity);
        Assert.True(rows[0].Opacity >= rows[1].Opacity && rows[1].Opacity >= rows[2].Opacity, "rows come in one after another");
        Snapshot(rig.Window, "entrance-224ms", 1200, dark);

        // t ≈ 368:大部分到位,最后一行刚开始进来
        Frames(144);
        Assert.InRange(rows[4].Opacity, 0.01, 0.99);
        Snapshot(rig.Window, "entrance-368ms", 1200, dark);

        // t ≈ 512:轨道已经画完,短弧刚开始淡入
        Frames(144);
        Assert.Equal(1, ring.TrackSweep);
        Snapshot(rig.Window, "entrance-512ms", 1200, dark);

        // 全部走完:精确落在静止状态(不透明度、变换都恢复成原来的),没有进行中的动画
        Frames(600);
        Assert.False(overlay.IsEntering);
        Assert.Equal(1, mark.Opacity);
        Assert.Null(mark.RenderTransform);
        Assert.Equal(1, title.Opacity);
        Assert.Null(title.RenderTransform);
        Assert.All(rows, r =>
        {
            Assert.Equal(1, r.Opacity);
            Assert.Null(r.RenderTransform);
        });
        Assert.Equal(1, comet.Opacity);
        Assert.False(ring.IsSweeping);
        Snapshot(rig.Window, "entrance-final", 1200, dark);
    }

    [AvaloniaFact]
    public void At_the_reduced_level_the_entrance_is_one_short_fade_with_no_stagger_no_scale_no_sweep()
    {
        var rig = ShownAt(UiMotionLevel.Reduced);
        Frames(64);
        rig.Wait(300);
        var overlay = rig.Overlay!;
        var mark = Part(overlay, "Mark");
        var title = Part(overlay, "Title");
        var rows = RowShells(overlay);
        var ring = overlay.FindControl<NcwProgressArc>("Ring")!;

        Assert.Equal(1, ring.TrackSweep);                       // 轨道不画,直接就位
        Frames(64);
        Assert.InRange(mark.Opacity, 0.01, 0.99);               // 短淡入(Fast 档):所有部分一起,没有错峰
        Assert.Equal(mark.Opacity, title.Opacity, 3);
        Assert.Equal(mark.Opacity, rows[4].Opacity, 3);
        Assert.Null(mark.RenderTransform);                      // 没有缩放、没有位移
        Assert.Null(title.RenderTransform);

        Frames(200);                                            // Fast = 160 ms:全部落定
        Assert.Equal(1, mark.Opacity);
        Assert.Equal(1, title.Opacity);
        Assert.All(rows, r => Assert.Equal(1, r.Opacity));
    }

    [AvaloniaFact]
    public void At_the_off_level_the_entrance_is_instant()
    {
        var rig = ShownAt(UiMotionLevel.Off);
        rig.Wait(300);
        var overlay = rig.Overlay!;

        Assert.Equal(1, Part(overlay, "Mark").Opacity);
        Assert.Equal(1, Part(overlay, "Title").Opacity);
        Assert.All(RowShells(overlay), r => Assert.Equal(1, r.Opacity));
        Assert.Equal(1, overlay.FindControl<NcwProgressArc>("Ring")!.TrackSweep);
        Assert.False(overlay.IsEntering);
        Assert.Null(Part(overlay, "Mark").RenderTransform);
    }

    [AvaloniaFact]
    public void The_entrance_plays_once_and_does_not_replay_when_the_layer_yields_and_comes_back()
    {
        var rig = ShownAt(UiMotionLevel.Full);
        Frames(64);
        rig.ReachAgent();
        rig.Wait(300);
        Frames(900);
        var script = rig.Overlay!.LastEntrance;
        Assert.NotEmpty(script);

        rig.Device(DeviceConnection.Connected, new DataStatus(DataStates.Blocked, null, DataReasons.LegacyAppRunning, null));   // 让位给数据页
        Frames(400);
        rig.Device(DeviceConnection.Connected, Taking(DataSteps.BackingUp));                                                  // 用户点了「重试」,加载层接回来
        Frames(400);

        Assert.Same(script, rig.Overlay!.LastEntrance);         // 同一份脚本,没有重播
        Assert.False(rig.Overlay.IsEntering);
        Assert.Equal(1, Part(rig.Overlay, "Mark").Opacity);
        Assert.Equal(1, Part(rig.Overlay, "Title").Opacity);
    }

    [AvaloniaFact]
    public void A_failure_that_arrives_with_the_content_enters_as_the_mark_and_the_panel_only()
    {
        var rig = ShownAt(UiMotionLevel.Full);
        Frames(64);
        rig.AppReady();
        rig.Sidecar(StartupSidecarPhase.Failed, "agent-not-found");   // 失败立刻显示内容,不等延迟

        var steps = rig.Overlay!.LastEntrance;

        Assert.Equal(2, steps.Count);
        Assert.Same(Part(rig.Overlay, "Mark"), steps[0].Part);
        Assert.Same(Part(rig.Overlay, "FailurePanel"), steps[1].Part);
        Assert.Equal(NcwMotion.StaggerLine, steps[1].Delay);
        Frames(64);
        Assert.InRange(steps[0].Part.Opacity, 0.01, 0.99);
        Frames(600);
        Assert.Equal(1, steps[1].Part.Opacity);
    }

    [AvaloniaFact]
    public void The_comet_has_a_fading_tail_and_the_check_grows_in()
    {
        var rig = ShownAt(UiMotionLevel.Full);
        Frames(64);
        rig.Wait(300);
        Frames(900);
        var overlay = rig.Overlay!;
        var tail = overlay.FindControl<NcwProgressArc>("CometTail")!;
        var head = overlay.FindControl<NcwProgressArc>("Comet")!;

        Assert.True(tail.Progress > head.Progress);             // 拖尾比彗头长
        Assert.True(tail.Opacity < 1);                          // 而且淡
        Assert.NotNull(tail.RenderTransform);                   // 拖尾转到彗头后面(XAML 里的 rotate(-72deg))

        rig.AppReady();                                         // 第一阶段完成:勾「长出来」(缩放 + 淡入)
        Frames(60);
        var cell = RowShells(overlay)[0].GetVisualDescendants().OfType<Panel>().First(x => x.Width == 16 && x.Height == 16);
        var check = (Panel)cell.Children[0];
        var scale = Assert.IsType<ScaleTransform>(check.RenderTransform);
        Assert.InRange(scale.ScaleX, NcwMotion.GrowFrom, 0.999);
        Frames(500);
        Assert.Null(check.RenderTransform);
        Assert.Equal(1, check.Opacity);
    }

    [AvaloniaFact]
    public void A_timer_that_fires_a_little_early_does_not_strand_the_layer()
    {
        // 真窗口冒烟里出现过:UI 定时器比 ViewModel 的时钟早响几毫秒,重算出来的「下一个时刻」没变、不发通知,如果不再武装,最短停留永远等不到头
        var rig = new Rig(1200, true);
        rig.Wait(300);                                    // 内容在 300 ms 出现(ContentShownAt),最短停留到 900 ms
        rig.LoadEverything();
        PumpUntil(() => rig.Stores.Startup.State.Facts!.Has(StartupMilestone.ShellLaidOut));
        var overlay = rig.Overlay!;
        Assert.False(rig.Startup.IsRevealing);

        rig.Clock.Advance(599);                           // 899 ms:定时器早响了
        overlay.RunWake();

        Assert.False(rig.Startup.IsRevealing);            // 还没到
        Assert.True(overlay.IsWaitingForWake);            // 但是已经再武装了
        rig.Clock.Advance(1);
        overlay.RunWake();
        Assert.True(rig.Stores.Startup.State.Facts!.Has(StartupMilestone.Interactive) || rig.Startup.IsRevealing);   // 到点了:开始淡出(关闭级别立即走完)
    }

    [AvaloniaFact]
    public void A_start_that_finishes_inside_the_delay_never_shows_content_and_the_backdrop_fades_straight_out()
    {
        var rig = new Rig(1200, true, UiMotionLevel.Full);
        Frames(64);                                       // 动画时钟已经在走(第一帧之后)
        rig.Wait(100);
        rig.LoadEverything();
        PumpUntil(() => rig.Startup.IsRevealing);
        var overlay = rig.Overlay!;

        Assert.False(rig.Startup.IsContentVisible);
        Assert.True(rig.Startup.IsRevealing, $"phase={rig.Startup.Phase} presented={rig.Startup.IsPresented} owns={rig.Startup.OwnsScreen} awaiting={rig.Startup.IsAwaitingShell} laid={rig.Stores.Startup.State.Facts!.Has(StartupMilestone.ShellLaidOut)} released={overlay.IsReleased} vis={overlay.IsVisible} queued={typeof(StartupOverlay).GetField("_shellReportQueued", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(overlay)} top={TopLevel.GetTopLevel(overlay) is not null}");   // 已经就绪、外壳也布局了:直接淡出,没有一闪而过的标识
        Assert.DoesNotContain("NextCoWork", rig.VisibleTexts());
        Frames(700);
        Assert.True(overlay.IsReleased);
    }

    // ───────────────────────── 减弱 / 关闭:终态一致 ─────────────────────────

    [AvaloniaTheory]
    [InlineData(UiMotionLevel.Off)]
    [InlineData(UiMotionLevel.Reduced)]
    [InlineData(UiMotionLevel.Full)]
    public void The_final_state_is_identical_at_every_motion_level_and_the_real_stage_text_is_always_shown(UiMotionLevel level)
    {
        var rig = new Rig(900, false, level);
        rig.Wait(300);
        rig.ReachAgent();
        rig.Device(DeviceConnection.Connected, Taking(DataSteps.Migrating));
        Frames(900);

        // 终态与动效级别无关:阶段文字、进度弧、四段进度都是真实的值
        var ring = rig.Overlay!.FindControl<NcwProgressArc>("Ring")!;
        Assert.Contains(NcwTexts.Get(NcwTextKind.DataStepMigrating), rig.VisibleTexts());
        Assert.Equal(rig.Startup.Fraction, ring.DisplayProgress, 4);
        Assert.False(ring.IsMoving);
        Assert.Equal(3 / 5.0 + 1 / 20.0, rig.Startup.Fraction, 6);
        var comet = rig.Overlay!.FindControl<NcwProgressArc>("Comet")!;
        Assert.Equal(level != UiMotionLevel.Off, comet.IsEffectivelyVisible);   // 一步要多久并不知道:短弧在等待期间一直转(完整)/ 呼吸(减弱);关闭级别不画
        Snapshot(rig.Window, $"level-{level.ToString().ToLowerInvariant()}", 900, false);

        rig.Device(DeviceConnection.Connected, Ready);
        rig.Reach(StartupMilestone.TopicsSynced, StartupMilestone.ProjectsLoaded, StartupMilestone.SessionsLoaded);
        Pump(8);
        rig.Wait(700);
        Frames(900);

        Assert.Null(rig.Overlay);                                           // 三档的终态都是:加载层已摘掉
        Assert.Equal(StartupPhase.Done, rig.Startup.Phase);
        Assert.False(rig.Startup.OwnsScreen);
    }

    [AvaloniaTheory]
    [InlineData(UiMotionLevel.Off, false)]
    [InlineData(UiMotionLevel.Reduced, true)]
    [InlineData(UiMotionLevel.Full, true)]
    public void The_working_arc_spins_in_full_pulses_in_reduced_and_is_not_drawn_at_all_when_off(UiMotionLevel level, bool visible)
    {
        var rig = new Rig(1200, true, level);
        rig.Wait(300);
        rig.AppReady();
        Frames(200);

        var comet = rig.Overlay!.FindControl<NcwProgressArc>("Comet")!;

        Assert.True(rig.Startup.IsIndeterminate);                           // 还在等:短弧在转
        Assert.Equal(visible, comet.IsEffectivelyVisible);
    }

    // ───────────────────────── 窗口底色 ─────────────────────────

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_main_window_background_is_the_same_token_as_the_loading_backdrop(bool dark)
    {
        ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
        var window = new MainWindow();
        window.Show();
        Pump();

        var token = ArcTokens.GetColor(window, ArcDesignTokenKind.Background);

        Assert.Equal(token, Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(window.Background).Color);
    }

    // ───────────────────────── 中英文 ─────────────────────────

    [AvaloniaFact]
    public void Every_startup_text_exists_in_both_languages()
    {
        var kinds = Enum.GetValues<NcwTextKind>().Where(k => k.ToString().StartsWith("Startup", StringComparison.Ordinal)).ToList();
        Assert.True(kinds.Count >= 40);

        ArcLanguage.Set(LanguageTags.EnUS);
        var en = kinds.ToDictionary(k => k, k => NcwTexts.Get(k));
        ArcLanguage.Set(LanguageTags.ZhCN);
        var zh = kinds.ToDictionary(k => k, k => NcwTexts.Get(k));

        Assert.All(kinds, k =>
        {
            Assert.False(string.IsNullOrWhiteSpace(en[k]), $"{k} (en)");
            Assert.False(string.IsNullOrWhiteSpace(zh[k]), $"{k} (zh)");
            Assert.NotEqual(en[k], zh[k]);
        });
    }

    [AvaloniaFact]
    public void The_layer_follows_the_language_and_unknown_failure_reasons_get_a_generic_message()
    {
        var rig = new Rig(1200, true);
        rig.AppReady();
        rig.Sidecar(StartupSidecarPhase.Failed, "something-new");
        Assert.Contains(NcwTexts.Get(NcwTextKind.StartupFailTitleUnknown), rig.VisibleTexts());

        rig.Sidecar(StartupSidecarPhase.Failed, "launch-failed");
        ArcLanguage.Set(LanguageTags.ZhCN);
        Pump();
        Assert.Contains("本机 Agent 无法启动", rig.VisibleTexts());
        Snapshot(rig.Window, "failure-zh", 1200, true);
    }
}
