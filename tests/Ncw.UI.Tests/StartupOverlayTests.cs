using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
        Assert.False(comet.IsEffectivelyVisible);                           // 接管有真实步骤:不画「不确定」的短弧
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

        Assert.True(rig.Startup.IsIndeterminate);                           // 当前阶段没有真实步骤:不确定
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
