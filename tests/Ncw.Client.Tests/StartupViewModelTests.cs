using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;

namespace Ncw.Client.Tests;

/// <summary>
/// 启动加载界面的 ViewModel:真实的 Store 订阅 + 真实的 Recorder + Core 的纯函数,时钟由测试推进
/// (延迟出现、最短停留、淡出触发、慢启动、失败面板、重试、让位给数据页、进入可交互之后不再出现)。
/// </summary>
public class StartupViewModelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = T0;

        public void Advance(double ms) => Now = Now.AddMilliseconds(ms);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeHost : IStartupHost
    {
        public int Retries { get; private set; }

        public string? Extra { get; set; }

        public void Retry() => Retries++;

        public string? Diagnostics() => Extra;
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

        public ShellViewModel Shell { get; }

        public FakeHost Host { get; } = new();

        public FakeFiles Files { get; } = new();

        public int Exits { get; private set; }

        public StartupViewModel Startup => Shell.Startup;

        public Rig(bool tracking = true, StartupProfile profile = StartupProfile.Desktop, bool shown = true)
        {
            Stores = new ClientStores(Clock);
            var devices = new DeviceRegistry
            {
                Local = new DeviceLink(Stores, InlineDispatcher.Instance, "local", DeviceKind.Local, "local",
                    _ => throw new InvalidOperationException(), new ClientHello("tests", "3.0.0")),
            };
            Shell = new ShellViewModel(Stores, Clock, devices: devices) { StartupHost = Host, RequestExit = () => Exits++ };
            Shell.Startup.LocalFiles = Files;
            if (tracking)
            {
                Stores.Startup.Begin(profile);
                if (shown)
                {
                    Stores.Startup.Reach(StartupMilestone.WindowShown);   // 窗口在起点就显示出来了(真实的应用里是 MainWindow.Opened)
                }
            }
        }

        public void Reach(params StartupMilestone[] milestones)
        {
            foreach (var m in milestones)
            {
                Stores.Startup.Reach(m);
            }
        }

        public void AppReady() => Reach(StartupMilestone.InstanceReady, StartupMilestone.WindowShown, StartupMilestone.PlatformReady, StartupMilestone.AccountDecided);

        public void Sidecar(StartupSidecarPhase phase, string? issue = null) => Stores.Startup.SetSidecar(new StartupSidecar(phase, issue, Detail: issue is null ? null : "detail"));

        public void Device(DeviceConnection connection, DataStatus? data = null) =>
            Stores.Workspace.Update(s => s with
            {
                Devices = s.Devices.SetItem("local", new DeviceInfo("local", "local", DeviceKind.Local, connection, null, connection == DeviceConnection.Connected ? data : null)),
            });

        public void Content() => Reach(StartupMilestone.TopicsSynced, StartupMilestone.ProjectsLoaded, StartupMilestone.SessionsLoaded);

        /// <summary>走完整个启动:Agent 就绪、连上、数据就绪、首批内容(返回时只差外壳布局)。</summary>
        public void LoadEverything()
        {
            AppReady();
            Sidecar(StartupSidecarPhase.Running);
            Reach(StartupMilestone.AgentSpawned, StartupMilestone.AgentReady);
            Device(DeviceConnection.Connected, new DataStatus(DataStates.Ready, null, null, null));
            Content();
        }
    }

    private static DataStatus Ready => new(DataStates.Ready, null, null, null);

    private static DataStatus Taking(string step) => new(DataStates.TakingOver, step, null, null);

    // ───────────────────────── 不在跟踪时:和没有这个功能一样 ─────────────────────────

    [Fact]
    public void Without_tracking_nothing_is_shown_and_the_data_page_behaves_as_before()
    {
        var rig = new Rig(tracking: false);
        rig.Device(DeviceConnection.Connected, Taking(DataSteps.BackingUp));

        Assert.False(rig.Startup.IsPresented);
        Assert.False(rig.Startup.OwnsScreen);
        Assert.True(rig.Shell.DataGate.IsPresented);   // 数据页照旧显示接管进度
    }

    // ───────────────────────── 第一帧、延迟出现、最短停留、淡出 ─────────────────────────

    [Fact]
    public void The_first_frame_has_the_backdrop_but_the_content_waits_for_the_delay()
    {
        var rig = new Rig();

        Assert.True(rig.Startup.IsPresented);
        Assert.True(rig.Startup.OwnsScreen);
        Assert.False(rig.Startup.IsContentVisible);
        Assert.Equal(T0 + TimeSpan.FromMilliseconds(250), rig.Startup.NextWake);
        Assert.Equal(TimeSpan.FromMilliseconds(250), rig.Startup.UntilWake);

        rig.Clock.Advance(249);
        rig.Startup.Tick();
        Assert.False(rig.Startup.IsContentVisible);

        rig.Clock.Advance(1);
        rig.Startup.Tick();
        Assert.True(rig.Startup.IsContentVisible);
    }

    [Fact]
    public void Time_before_the_window_is_displayed_does_not_count_as_content_that_has_been_shown()
    {
        var rig = new Rig(shown: false);
        rig.Clock.Advance(4000);                      // UI 线程在创建外壳,窗口还没出来
        rig.Startup.Tick();

        Assert.True(rig.Startup.IsPresented);         // 底色在(窗口出来的第一帧就是它)
        Assert.False(rig.Startup.IsContentVisible);
        Assert.Equal(T0 + TimeSpan.FromSeconds(8), rig.Startup.NextWake);   // 没有「延迟出现」的唤醒

        rig.Reach(StartupMilestone.WindowShown);      // 窗口显示出来了:延迟从这一刻算
        Assert.False(rig.Startup.IsContentVisible);
        Assert.Equal(T0 + TimeSpan.FromMilliseconds(4250), rig.Startup.NextWake);

        rig.Clock.Advance(250);
        rig.Startup.Tick();
        Assert.True(rig.Startup.IsContentVisible);
    }

    [Fact]
    public void A_start_that_finishes_before_the_delay_never_shows_content()
    {
        var rig = new Rig();
        rig.Clock.Advance(100);
        rig.LoadEverything();
        Assert.True(rig.Startup.IsAwaitingShell);   // 数据齐了,等外壳布局

        rig.Startup.ReportShellLaidOut();

        Assert.True(rig.Startup.IsRevealing);
        Assert.False(rig.Startup.IsPresented);       // 触发淡出(NcwMotion.IsPresent 变假)
        Assert.True(rig.Startup.OwnsScreen);         // 淡出期间整窗仍归加载层
        Assert.False(rig.Startup.IsContentVisible);  // 内容从没出现过:没有一闪而过
    }

    [Fact]
    public void Content_that_appeared_stays_for_the_minimum_dwell_then_the_reveal_starts_by_itself()
    {
        var rig = new Rig();
        rig.Clock.Advance(250);
        rig.Startup.Tick();                           // 内容出现(250 ms)
        Assert.True(rig.Startup.IsContentVisible);

        rig.Clock.Advance(50);
        rig.LoadEverything();                         // 300 ms 就绪
        rig.Startup.ReportShellLaidOut();
        Assert.False(rig.Startup.IsRevealing);        // 还没满 600 ms
        Assert.Equal(T0 + TimeSpan.FromMilliseconds(850), rig.Startup.NextWake);

        rig.Clock.Advance(549);
        rig.Startup.Tick();
        Assert.False(rig.Startup.IsRevealing);

        rig.Clock.Advance(1);
        rig.Startup.Tick();
        Assert.True(rig.Startup.IsRevealing);
        Assert.False(rig.Startup.IsPresented);
        Assert.True(rig.Startup.IsContentVisible);    // 淡出的是完整的加载界面,不是先把内容撤掉
    }

    [Fact]
    public void The_shell_layout_report_is_ignored_until_every_step_is_done()
    {
        var rig = new Rig();
        rig.AppReady();

        rig.Startup.ReportShellLaidOut();

        Assert.False(rig.Startup.IsAwaitingShell);
        Assert.False(rig.Stores.Startup.State.Facts!.Has(StartupMilestone.ShellLaidOut));
        Assert.Equal(StartupPhase.Loading, rig.Startup.Phase);
    }

    [Fact]
    public void Finishing_the_reveal_records_interactive_and_releases_everything()
    {
        var rig = new Rig();
        rig.LoadEverything();
        rig.Startup.ReportShellLaidOut();
        Assert.True(rig.Startup.IsRevealing);
        var changes = 0;
        rig.Startup.PropertyChanged += (_, _) => changes++;

        rig.Startup.ReportRevealed();
        rig.Startup.ReportRevealed();                 // 只报告一次

        Assert.True(rig.Stores.Startup.State.Facts!.Has(StartupMilestone.Interactive));
        Assert.False(rig.Startup.OwnsScreen);
        Assert.False(rig.Startup.IsPresented);
        Assert.Equal(StartupPhase.Done, rig.Startup.Phase);
        Assert.True(rig.Shell.DataGate.IsPresented == rig.Shell.DataGate.IsVisible);
        var after = changes;

        // 释放之后:不再订阅任何东西——Store 怎么变,这个 ViewModel 都不再有任何通知
        rig.Device(DeviceConnection.Offline);
        rig.Sidecar(StartupSidecarPhase.Failed, "crash-loop");
        rig.Clock.Advance(60_000);
        rig.Startup.Tick();
        Assert.Equal(after, changes);
        Assert.False(rig.Startup.IsFailed);
        Assert.Null(rig.Startup.NextWake);
    }

    [Fact]
    public void After_interactive_a_disconnect_does_not_bring_the_full_screen_loading_back()
    {
        var rig = new Rig();
        rig.LoadEverything();
        rig.Startup.ReportShellLaidOut();
        rig.Startup.ReportRevealed();

        rig.Device(DeviceConnection.Connecting);
        rig.Device(DeviceConnection.Offline);

        Assert.False(rig.Startup.OwnsScreen);
        Assert.False(rig.Startup.IsPresented);
        Assert.False(rig.Startup.IsFailed);
    }

    // ───────────────────────── 事实经 Recorder 锁存 ─────────────────────────

    [Fact]
    public void The_recorder_latches_connected_and_data_ready_and_a_later_disconnect_does_not_undo_them()
    {
        var rig = new Rig();
        rig.AppReady();
        rig.Sidecar(StartupSidecarPhase.Running);
        rig.Reach(StartupMilestone.AgentReady);

        rig.Device(DeviceConnection.Connected, Ready);
        var facts = rig.Stores.Startup.State.Facts!;
        Assert.True(facts.Has(StartupMilestone.Connected));
        Assert.True(facts.Has(StartupMilestone.DataReady));
        var at = facts.Reached[StartupMilestone.Connected];

        rig.Clock.Advance(500);
        rig.Device(DeviceConnection.Connecting);      // Agent 重启:设备断开、数据状态未知
        rig.Device(DeviceConnection.Connected, Ready);

        Assert.Equal(at, rig.Stores.Startup.State.Facts!.Reached[StartupMilestone.Connected]);   // 时间戳是第一次的
        Assert.Equal(StartupStage.Content, rig.Startup.Current);
    }

    [Fact]
    public void A_disconnect_during_startup_marks_reconnecting_and_does_not_go_back_to_starting()
    {
        var rig = new Rig();
        rig.AppReady();
        rig.Sidecar(StartupSidecarPhase.Running);
        rig.Reach(StartupMilestone.AgentReady);
        rig.Device(DeviceConnection.Connected, Taking(DataSteps.Migrating));
        var before = rig.Startup.Fraction;

        rig.Device(DeviceConnection.Connecting);

        Assert.True(rig.Startup.IsReconnecting);
        Assert.Equal(StartupDetail.Reconnecting, rig.Startup.Detail);
        Assert.Equal(3, rig.Startup.Steps.Count(s => s.IsDone));
        Assert.True(rig.Startup.Fraction >= before);
    }

    [Fact]
    public void The_account_stage_waits_for_the_account_record_to_be_read()
    {
        var rig = new Rig();
        rig.Reach(StartupMilestone.InstanceReady, StartupMilestone.WindowShown, StartupMilestone.PlatformReady);
        Assert.Equal(StartupStage.App, rig.Startup.Current);

        rig.Stores.Account.Update(a => a with { Mode = AccountMode.Offline });

        Assert.Equal(StartupStage.Agent, rig.Startup.Current);
    }

    // ───────────────────────── 数据接管:和数据页读同一个事实 ─────────────────────────

    [Fact]
    public void The_takeover_step_and_progress_come_from_the_same_fact_the_data_page_reads()
    {
        var rig = new Rig();
        rig.AppReady();
        rig.Sidecar(StartupSidecarPhase.Running);
        rig.Reach(StartupMilestone.AgentReady);

        var fractions = new List<double>();
        foreach (var step in new[] { DataSteps.BackingUp, DataSteps.Migrating, DataSteps.Committing, DataSteps.Credentials })
        {
            rig.Device(DeviceConnection.Connected, Taking(step));

            Assert.Equal(rig.Shell.DataGate.Step, rig.Startup.DataStep);   // 同一个事实
            Assert.True(rig.Startup.IsDeterminate);
            Assert.True(rig.Startup.OwnsScreen);                            // 加载层显示接管进度
            Assert.False(rig.Shell.DataGate.IsPresented);                   // 数据页不同时盖上来
            fractions.Add(rig.Startup.Fraction);
        }

        Assert.True(fractions.Zip(fractions.Skip(1), (a, b) => b > a).All(x => x));
        Assert.Equal([1, 2, 3, 4], new[] { DataSteps.BackingUp, DataSteps.Migrating, DataSteps.Committing, DataSteps.Credentials }.Select(StartupProgress.DataStepOrdinal));
    }

    [Fact]
    public void Blocked_data_hands_the_screen_to_the_data_page_and_retry_hands_it_back()
    {
        var rig = new Rig();
        rig.Clock.Advance(300);
        rig.Startup.Tick();
        rig.AppReady();
        rig.Sidecar(StartupSidecarPhase.Running);
        rig.Reach(StartupMilestone.AgentReady);

        rig.Device(DeviceConnection.Connected, new DataStatus(DataStates.Blocked, null, DataReasons.LegacyAppRunning, null));
        Assert.False(rig.Startup.OwnsScreen);
        Assert.False(rig.Startup.IsPresented);
        Assert.True(rig.Shell.DataGate.IsPresented);                         // 需要用户处理:数据页接管
        Assert.Equal(StartupPhase.NeedsUser, rig.Startup.Phase);
        Assert.False(rig.Startup.IsSlow);

        rig.Device(DeviceConnection.Connected, Taking(DataSteps.BackingUp)); // 用户点了数据页的「重试」
        Assert.True(rig.Startup.OwnsScreen);
        Assert.True(rig.Startup.IsPresented);
        Assert.True(rig.Startup.IsContentVisible);                           // 回来不用再等延迟
        Assert.False(rig.Shell.DataGate.IsPresented);
    }

    [Fact]
    public void The_completed_takeover_summary_stays_with_the_data_page_until_continue()
    {
        var rig = new Rig();
        rig.AppReady();
        rig.Sidecar(StartupSidecarPhase.Running);
        rig.Reach(StartupMilestone.AgentReady);

        rig.Device(DeviceConnection.Connected, new DataStatus(DataStates.Ready, null, null, new TakeoverSummary(23, 631, 0)));
        Assert.Equal(DataGateMode.Completed, rig.Shell.DataGate.Mode);
        Assert.False(rig.Startup.OwnsScreen);
        Assert.True(rig.Shell.DataGate.IsPresented);

        rig.Shell.DataGate.ContinueCommand.Execute(null);

        Assert.True(rig.Startup.OwnsScreen);                                  // 继续之后:还差首批内容,加载层接回来
        Assert.False(rig.Shell.DataGate.IsPresented);
        Assert.Equal(StartupStage.Content, rig.Startup.Current);
    }

    [Fact]
    public void A_retried_takeover_that_starts_over_does_not_pull_the_progress_back()
    {
        var rig = new Rig();
        rig.AppReady();
        rig.Sidecar(StartupSidecarPhase.Running);
        rig.Reach(StartupMilestone.AgentReady);
        rig.Device(DeviceConnection.Connected, Taking(DataSteps.Committing));
        var high = rig.Startup.Fraction;

        rig.Device(DeviceConnection.Connected, new DataStatus(DataStates.Failed, null, DataReasons.Internal, null));
        rig.Device(DeviceConnection.Connected, Taking(DataSteps.BackingUp));

        Assert.Equal(high, rig.Startup.Fraction);
    }

    // ───────────────────────── 慢启动 ─────────────────────────

    [Fact]
    public void A_slow_start_gets_a_gentle_hint_exactly_when_the_threshold_passes()
    {
        var rig = new Rig();
        rig.AppReady();
        rig.Sidecar(StartupSidecarPhase.Starting);
        rig.Reach(StartupMilestone.AgentSpawned);
        rig.Clock.Advance(250);
        rig.Startup.Tick();                           // 内容已经出现,下一个时刻是慢启动阈值
        Assert.False(rig.Startup.IsSlow);
        Assert.Equal(T0 + TimeSpan.FromSeconds(8), rig.Startup.NextWake);

        rig.Clock.Advance(7_749);
        rig.Startup.Tick();
        Assert.False(rig.Startup.IsSlow);

        rig.Clock.Advance(1);
        rig.Startup.Tick();
        Assert.True(rig.Startup.IsSlow);
        Assert.Equal(StartupSlowHint.Agent, rig.Startup.SlowHint);
        Assert.False(rig.Startup.IsFailed);
    }

    // ───────────────────────── 失败面板、重试、诊断、退出 ─────────────────────────

    [Theory]
    [InlineData(StartupSidecarPhase.Failed, "agent-not-found")]
    [InlineData(StartupSidecarPhase.Failed, "launch-failed")]
    [InlineData(StartupSidecarPhase.Failed, "already-running-elsewhere")]
    [InlineData(StartupSidecarPhase.Failed, "crash-loop")]
    public void A_sidecar_failure_shows_the_panel_immediately_with_a_retry(StartupSidecarPhase phase, string issue)
    {
        var rig = new Rig();
        rig.AppReady();

        rig.Sidecar(phase, issue);

        Assert.True(rig.Startup.IsFailed);
        Assert.Equal(issue, rig.Startup.FailureReason);
        Assert.True(rig.Startup.CanRetry);
        Assert.True(rig.Startup.IsContentVisible);   // 失败不等延迟
        Assert.True(rig.Startup.IsPresented);
    }

    [Theory]
    [InlineData("client-outdated")]
    [InlineData("agent-outdated")]
    public void A_version_mismatch_has_no_retry_because_retrying_cannot_help(string issue)
    {
        var rig = new Rig();
        rig.AppReady();

        rig.Sidecar(StartupSidecarPhase.Failed, issue);

        Assert.True(rig.Startup.IsFailed);
        Assert.False(rig.Startup.CanRetry);
    }

    [Fact]
    public void A_connection_that_stays_down_is_a_failure_and_a_timeout_is_a_failure_that_can_clear()
    {
        var rig = new Rig();
        rig.AppReady();
        rig.Sidecar(StartupSidecarPhase.Running);
        rig.Reach(StartupMilestone.AgentReady);
        rig.Device(DeviceConnection.Offline);
        Assert.Equal("connection-failed", rig.Startup.FailureReason);

        rig.Device(DeviceConnection.Connecting);
        Assert.False(rig.Startup.IsFailed);
        rig.Clock.Advance(30_000);
        rig.Startup.Tick();
        Assert.Equal("timeout", rig.Startup.FailureReason);

        rig.Device(DeviceConnection.Connected, Ready);
        rig.Content();
        Assert.False(rig.Startup.IsFailed);
        Assert.True(rig.Startup.IsAwaitingShell);
    }

    [Fact]
    public void Retry_goes_to_the_host_and_a_recovered_agent_clears_the_panel()
    {
        var rig = new Rig();
        rig.AppReady();
        rig.Sidecar(StartupSidecarPhase.Failed, "crash-loop");
        Assert.True(rig.Startup.IsFailed);

        rig.Startup.RetryCommand.Execute(null);

        Assert.Equal(1, rig.Host.Retries);

        rig.Sidecar(StartupSidecarPhase.Starting);   // 守护收到重试:重新拉起
        Assert.False(rig.Startup.IsFailed);          // 失败是派生的,不是锁存的
        rig.Sidecar(StartupSidecarPhase.Running);
        rig.Reach(StartupMilestone.AgentReady);
        Assert.Equal(StartupStage.Connect, rig.Startup.Current);
    }

    [Fact]
    public async Task Copy_diagnostics_puts_the_timeline_and_the_hosts_extra_text_on_the_clipboard()
    {
        var rig = new Rig();
        rig.Stores.Startup.Reach(StartupMilestone.ProcessStarted, T0);
        rig.AppReady();
        rig.Sidecar(StartupSidecarPhase.Failed, "launch-failed");
        rig.Host.Extra = "agent log tail";

        await rig.Startup.CopyDiagnosticsCommand.ExecuteAsync(null);

        Assert.NotNull(rig.Files.Copied);
        Assert.Contains("failure: launch-failed", rig.Files.Copied);
        Assert.Contains("InstanceReady", rig.Files.Copied);
        Assert.Contains("agent log tail", rig.Files.Copied);
    }

    [Fact]
    public void Toggling_diagnostics_fills_the_text_and_flips_the_pure_ui_flag()
    {
        var rig = new Rig();
        rig.AppReady();
        rig.Sidecar(StartupSidecarPhase.Failed, "crash-loop");
        Assert.False(rig.Startup.IsDiagnosticsOpen);

        rig.Startup.ToggleDiagnosticsCommand.Execute(null);

        Assert.True(rig.Startup.IsDiagnosticsOpen);
        Assert.Contains("crash-loop", rig.Startup.DiagnosticsText);
    }

    [Fact]
    public void Quit_goes_through_the_applications_exit_flow()
    {
        var rig = new Rig();
        rig.Sidecar(StartupSidecarPhase.Failed, "crash-loop");

        rig.Startup.QuitCommand.Execute(null);

        Assert.Equal(1, rig.Exits);
    }

    // ───────────────────────── 手机壳:同一个函数与 Store ─────────────────────────

    [Fact]
    public void The_phone_waits_for_its_device_and_does_not_fail_when_the_device_is_offline()
    {
        var rig = new Rig(profile: StartupProfile.Remote);
        rig.AppReady();
        rig.Stores.Workspace.Update(s => s with
        {
            Devices = s.Devices.SetItem("pc", new DeviceInfo("pc", "pc", DeviceKind.Remote, DeviceConnection.Connecting, null)),
        });
        Assert.Equal(StartupStage.Connect, rig.Startup.Current);

        rig.Stores.Workspace.Update(s => s with
        {
            Devices = s.Devices.SetItem("pc", new DeviceInfo("pc", "pc", DeviceKind.Remote, DeviceConnection.Offline, null)),
        });

        Assert.False(rig.Startup.IsFailed);
        Assert.True(rig.Startup.IsAwaitingShell);   // 离线的设备不挡着进入外壳
    }

    // ───────────────────────── 事实的写入口 ─────────────────────────

    [Fact]
    public void Signals_from_other_threads_are_posted_to_the_ui_thread_with_the_time_they_happened()
    {
        var stores = new ClientStores(new Clock());
        stores.Startup.Begin(StartupProfile.Desktop);
        var queue = new Queue<Action>();
        var signals = new StartupSignals(stores, new QueueDispatcher(queue));

        signals.Milestone(StartupMilestone.TopicsSynced);
        signals.Sidecar(new StartupSidecar(StartupSidecarPhase.Running));

        Assert.False(stores.Startup.State.Facts!.Has(StartupMilestone.TopicsSynced));   // 还在队列里:Store 只在 UI 线程上写
        while (queue.Count > 0)
        {
            queue.Dequeue()();
        }

        Assert.True(stores.Startup.State.Facts!.Has(StartupMilestone.TopicsSynced));
        Assert.Equal(StartupSidecarPhase.Running, stores.Startup.State.Facts!.Sidecar!.Phase);
    }

    private sealed class QueueDispatcher(Queue<Action> queue) : IUiDispatcher
    {
        public void Post(Action action) => queue.Enqueue(action);
    }

    [Fact]
    public void Signals_do_nothing_when_startup_is_not_being_tracked()
    {
        var stores = new ClientStores(new Clock());

        new StartupSignals(stores, InlineDispatcher.Instance).Milestone(StartupMilestone.TopicsSynced);

        Assert.False(stores.Startup.IsTracking);
        Assert.Null(stores.Startup.State.Facts);
    }

    [Fact]
    public void Beginning_twice_does_not_reset_what_has_been_latched()
    {
        var stores = new ClientStores(new Clock());
        stores.Startup.Begin(StartupProfile.Desktop);
        stores.Startup.Reach(StartupMilestone.WindowShown);

        stores.Startup.Begin(StartupProfile.Remote);

        Assert.Equal(StartupProfile.Desktop, stores.Startup.State.Facts!.Profile);
        Assert.True(stores.Startup.State.Facts.Has(StartupMilestone.WindowShown));
    }

    // ───────────────────────── 连接层:立刻重连 ─────────────────────────

    [Fact]
    public async Task Retry_now_skips_the_backoff_wait_and_resets_the_failure_count()
    {
        var stores = new ClientStores();
        var attempts = 0;
        var secondAt = DateTimeOffset.MinValue;
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var third = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = new DeviceLink(stores, InlineDispatcher.Instance, "local", DeviceKind.Local, "local", _ =>
        {
            attempts++;
            if (attempts == 2)
            {
                secondAt = DateTimeOffset.UtcNow;
                second.TrySetResult();
            }

            if (attempts == 3)
            {
                third.TrySetResult();
            }

            throw new InvalidOperationException("down");
        }, new ClientHello("tests", "3.0.0"));
        link.Start();
        try
        {
            await second.Task.WaitAsync(TimeSpan.FromSeconds(10));   // 第一次失败后退避 200 ms,第二次失败后要等 1 秒
            await Task.Delay(100);                                    // 让它进入第二次退避

            link.RetryNow();

            await third.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(DateTimeOffset.UtcNow - secondAt < TimeSpan.FromMilliseconds(900), "retry waited for the 1 s backoff");
        }
        finally
        {
            await link.DisposeAsync();
        }
    }
}
