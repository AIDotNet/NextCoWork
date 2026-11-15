using Ncw.Desktop.Sidecar;
using Ncw.Protocol.Wire;

namespace Ncw.Desktop.Tests;

/// <summary>守护逻辑:假进程 + 假握手 + 假时钟,不起任何真实进程。</summary>
public class SidecarSupervisorTests
{
    private static SidecarTimings Fast(int maxFailures = 3) => new()
    {
        MaxConsecutiveFailures = maxFailures,
        ReadyTimeout = TimeSpan.FromSeconds(10),
        ProbeInterval = TimeSpan.FromMilliseconds(500),
        StableAfter = TimeSpan.FromSeconds(60),
        HealthInterval = TimeSpan.FromSeconds(5),
    };

    // ---- 拉起与握手 ----

    [Fact]
    public async Task Starts_the_agent_in_local_mode_with_the_token_only_in_the_environment_and_reports_running()
    {
        await using var rig = new Rig();
        rig.Sidecar.Start();
        await rig.Sidecar.WaitReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await rig.UntilPhase(SidecarPhase.Running);

        var (spec, process) = Assert.Single(rig.Launcher.Starts);
        Assert.Equal("/fake/ncw-agent", spec.Executable);
        Assert.Equal(["--mode", "local", "--data-root", rig.Paths.DataRoot], spec.Arguments.Take(4));
        Assert.Equal(rig.Sidecar.Token, spec.Environment["NCW_LOCAL_TOKEN"]);
        Assert.DoesNotContain(rig.Sidecar.Token, spec.Arguments); // 令牌不上命令行(进程列表看得到)
        Assert.Equal(Rig.DesktopVersion, rig.Sidecar.Status.AgentVersion);
        Assert.Equal(SidecarIssue.None, rig.Sidecar.Status.Issue);

        var state = rig.StateFile.Read()!;
        Assert.Equal(process.Id, state.Pid);
        Assert.Equal(rig.Sidecar.Token, state.Token);
        Assert.Equal(Rig.DesktopVersion, state.Version);
    }

    [Fact]
    public async Task Without_keep_the_agent_gets_a_stdin_lifeline_and_parent_watch_so_a_desktop_crash_leaves_no_orphan()
    {
        await using var rig = new Rig(keep: false);
        rig.Sidecar.Start();
        await rig.UntilPhase(SidecarPhase.Running);
        var spec = rig.Launcher.Starts[0].Spec;
        Assert.True(spec.Lifeline);
        Assert.Equal(["--lifeline", "stdin"], spec.Arguments.SkipWhile(a => a != "--lifeline").Take(2));
        Assert.Contains("--parent-pid", spec.Arguments);
        Assert.False(rig.StateFile.Read()!.Detached);
    }

    [Fact]
    public async Task With_keep_the_agent_is_detached_no_lifeline_no_parent_watch()
    {
        await using var rig = new Rig(keep: true);
        rig.Sidecar.Start();
        await rig.UntilPhase(SidecarPhase.Running);
        var spec = rig.Launcher.Starts[0].Spec;
        Assert.False(spec.Lifeline);
        Assert.DoesNotContain("--lifeline", spec.Arguments);
        Assert.DoesNotContain("--parent-pid", spec.Arguments);
        Assert.True(rig.StateFile.Read()!.Detached);
    }

    [Fact]
    public async Task Waits_for_the_handshake_not_just_the_process_and_polls_until_the_endpoint_is_up()
    {
        await using var rig = new Rig(Fast());
        rig.Launcher.OnStart = p => p.Listening = false; // 进程起来了但还没监听
        rig.Sidecar.Start();
        await Rig.Until(() => rig.Launcher.Starts.Count == 1, "spawn");
        await Rig.Until(() => rig.Handshake.Probes >= 1, "first probe");
        Assert.False(rig.Sidecar.WaitReadyAsync(CancellationToken.None).IsCompleted);
        Assert.Equal(SidecarPhase.Starting, rig.Sidecar.Status.Phase);

        rig.Launcher.Last.Listening = true;
        await rig.AdvanceUntil(() => rig.Sidecar.Status.Phase == SidecarPhase.Running, "running", TimeSpan.FromMilliseconds(500));
        await rig.Sidecar.WaitReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_missing_agent_is_reported_and_found_later_without_restarting_the_desktop()
    {
        await using var rig = new Rig(Fast());
        rig.AgentPath = null;
        rig.Sidecar.Start();
        await Rig.Until(() => rig.Sidecar.Status.Issue == SidecarIssue.AgentNotFound, "not found");
        Assert.Empty(rig.Launcher.Starts);

        rig.AgentPath = "/fake/ncw-agent"; // 构建完成
        await rig.AdvanceUntil(() => rig.Sidecar.Status.Phase == SidecarPhase.Running, "running after it appeared");
        Assert.Single(rig.Launcher.Starts);
    }

    [Fact]
    public async Task A_launch_failure_backs_off_and_retries()
    {
        await using var rig = new Rig(Fast());
        var attempts = 0;
        rig.Launcher.FailWith = _ => ++attempts < 3 ? new System.ComponentModel.Win32Exception("blocked by security software") : null;
        rig.Sidecar.Start();
        await Rig.Until(() => rig.Sidecar.Status is { Phase: SidecarPhase.Restarting, Issue: SidecarIssue.LaunchFailed }, "launch failed");
        Assert.Contains("blocked", rig.Sidecar.Status.Detail);
        await rig.AdvanceUntil(() => rig.Sidecar.Status.Phase == SidecarPhase.Running, "running");
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task A_handshake_that_never_succeeds_times_out_kills_the_agent_and_retries()
    {
        await using var rig = new Rig(Fast());
        rig.Launcher.OnStart = p => p.Listening = false;
        rig.Launcher.OnStart += p => { if (rig.Launcher.Starts.Count >= 2) { p.Listening = true; } };
        rig.Sidecar.Start();
        await Rig.Until(() => rig.Launcher.Starts.Count == 1, "spawn");
        await rig.AdvanceUntil(() => rig.Launcher.Starts.Count >= 2, "second spawn", TimeSpan.FromMilliseconds(500));
        Assert.True(rig.Launcher.Starts[0].Process.HasExited); // 第一个被收掉了
        await rig.AdvanceUntil(() => rig.Sidecar.Status.Phase == SidecarPhase.Running, "running", TimeSpan.FromMilliseconds(500));
    }

    // ---- 崩溃重启与退避 ----

    [Fact]
    public async Task A_crash_restarts_with_backoff_and_a_fresh_ready_signal()
    {
        await using var rig = new Rig(Fast());
        rig.Sidecar.Start();
        await rig.UntilPhase(SidecarPhase.Running);
        var first = rig.Launcher.Last;

        first.Exit(139);
        await Rig.Until(() => rig.Sidecar.Status.Phase == SidecarPhase.Restarting, "restarting");
        Assert.Equal(TimeSpan.FromSeconds(1), rig.Sidecar.Status.RetryIn);
        Assert.Equal(1, rig.Sidecar.Status.RestartCount);
        Assert.Contains("139", rig.Sidecar.Status.Detail);
        Assert.False(rig.Sidecar.WaitReadyAsync(CancellationToken.None).IsCompleted); // 重启期间不能拿旧的「就绪」

        await rig.AdvanceUntil(() => rig.Launcher.Starts.Count == 2, "restart");
        await rig.UntilPhase(SidecarPhase.Running);
        await rig.Sidecar.WaitReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, rig.Sidecar.Status.RestartCount);
        Assert.NotEqual(first.Id, rig.Launcher.Last.Id);
    }

    [Fact]
    public async Task Repeated_quick_crashes_back_off_exponentially_then_stop_with_crash_loop_until_retry()
    {
        await using var rig = new Rig(Fast(maxFailures: 4));
        rig.Launcher.OnStart = p => p.Exit(1); // 一起来就崩
        rig.Sidecar.Start();

        var delays = new List<TimeSpan>();
        var seen = 0;
        while (rig.Sidecar.Status.Phase != SidecarPhase.Failed)
        {
            await Rig.Until(() => rig.Sidecar.Status.Phase is SidecarPhase.Restarting or SidecarPhase.Failed, "restarting or failed");
            if (rig.Sidecar.Status is { Phase: SidecarPhase.Restarting, RetryIn: { } d } && rig.Launcher.Starts.Count > seen)
            {
                seen = rig.Launcher.Starts.Count;
                delays.Add(d);
                rig.Clock.Advance(d);
                await Task.Delay(10);
            }
            else
            {
                await Task.Delay(2);
            }
        }

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], delays);
        Assert.Equal(SidecarIssue.CrashLoop, rig.Sidecar.Status.Issue);
        var starts = rig.Launcher.Starts.Count;
        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(30);
        Assert.Equal(starts, rig.Launcher.Starts.Count); // 不再自动重启

        rig.Launcher.OnStart = null;
        rig.Sidecar.Retry();
        await rig.UntilPhase(SidecarPhase.Running);
        Assert.Equal(starts + 1, rig.Launcher.Starts.Count);
    }

    [Fact]
    public async Task Running_stably_resets_the_failure_count()
    {
        await using var rig = new Rig(Fast(maxFailures: 3));
        rig.Sidecar.Start();
        await rig.UntilPhase(SidecarPhase.Running);

        // 先快速崩一次(failures = 1,等 1 秒),再跑满稳定时间后崩:下一次退避应该又是 1 秒,而不是 2 秒。
        rig.Launcher.Last.Exit(1);
        await Rig.Until(() => rig.Sidecar.Status.Phase == SidecarPhase.Restarting, "restart 1");
        await rig.AdvanceUntil(() => rig.Launcher.Starts.Count == 2, "spawn 2");
        await rig.UntilPhase(SidecarPhase.Running);

        rig.Clock.Advance(TimeSpan.FromSeconds(61));
        rig.Launcher.Last.Exit(1);
        await Rig.Until(() => rig.Sidecar.Status is { Phase: SidecarPhase.Restarting, RestartCount: 2 }, "restart 2");
        Assert.Equal(TimeSpan.FromSeconds(1), rig.Sidecar.Status.RetryIn);
    }

    [Fact]
    public async Task Retry_skips_the_remaining_backoff()
    {
        await using var rig = new Rig(Fast());
        rig.Sidecar.Start();
        await rig.UntilPhase(SidecarPhase.Running);
        rig.Launcher.Last.Exit(1);
        await Rig.Until(() => rig.Sidecar.Status.Phase == SidecarPhase.Restarting, "restarting");
        rig.Sidecar.Retry();
        await Rig.Until(() => rig.Launcher.Starts.Count == 2, "immediate restart"); // 没有推进时钟
    }

    // ---- 单实例 ----

    [Fact]
    public async Task Exit_code_75_means_another_agent_holds_the_data_root_and_is_reported_then_gives_up()
    {
        await using var rig = new Rig(Fast(maxFailures: 3));
        rig.Launcher.OnStart = p => p.Exit(LocalAgentSidecar.AlreadyRunningExitCode);
        rig.Sidecar.Start();
        await Rig.Until(() => rig.Sidecar.Status is { Phase: SidecarPhase.Restarting, Issue: SidecarIssue.AlreadyRunningElsewhere }, "conflict reported");
        await rig.AdvanceUntil(() => rig.Sidecar.Status.Phase == SidecarPhase.Failed, "gave up", TimeSpan.FromSeconds(5));
        Assert.Equal(SidecarIssue.AlreadyRunningElsewhere, rig.Sidecar.Status.Issue);
    }

    // ---- 拒绝启动(退出码 65 / 78):重启没用 ----

    private static void Refuses(FakeLauncher launcher, int code, string line)
    {
        launcher.OnStart = p => p.Exit(code);
        launcher.OnOutput = (_, output) => output(line);
    }

    [Fact]
    public async Task Exit_code_65_with_a_missing_master_key_goes_straight_to_failed_without_backoff_or_restarts()
    {
        await using var rig = new Rig(Fast());
        Refuses(rig.Launcher, 65, "[stderr] ncw-agent startup-failed agent.masterKey.missing credential.key");
        rig.Sidecar.Start();

        await Rig.Until(() => rig.Sidecar.Status is { Phase: SidecarPhase.Failed, Issue: SidecarIssue.MasterKeyMissing }, "master key missing reported");

        var status = rig.Sidecar.Status;
        Assert.Equal(0, status.RestartCount);
        Assert.Null(status.RetryIn);
        Assert.Contains("agent.masterKey.missing credential.key", status.Detail);
        Assert.Contains("exit code 65", status.Detail);
        Assert.Contains(rig.Paths.DataRoot, status.Detail); // 数据目录进诊断
        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(30);
        Assert.Single(rig.Launcher.Starts);                 // 不重启
        lock (rig.Statuses)
        {
            Assert.DoesNotContain(rig.Statuses, s => s.Phase == SidecarPhase.Restarting); // 也没有进入过退避
        }
    }

    [Fact]
    public async Task Exit_code_65_with_an_unreadable_master_key_is_its_own_reason()
    {
        await using var rig = new Rig(Fast());
        Refuses(rig.Launcher, 65, "ncw-agent startup-failed agent.masterKey.unreadable credential.key");
        rig.Sidecar.Start();

        await Rig.Until(() => rig.Sidecar.Status is { Phase: SidecarPhase.Failed, Issue: SidecarIssue.MasterKeyUnreadable }, "master key unreadable reported");
        Assert.Single(rig.Launcher.Starts);
    }

    [Theory]
    [InlineData(78, "ncw-agent startup-failed cloud.config.invalid dataRoot")]   // 78:配置不合法,重启也不会好,原因认不出来就是「拉不起来」
    [InlineData(65, "ncw-agent startup-failed some.future.key field")]
    [InlineData(65, "something else entirely")]                                    // 没有结构化的那一行
    [InlineData(78, "ncw-agent startup-failed agent.masterKey.missing a=b")]       // 守护不信任输出:不是标识符的内容不进诊断
    public async Task Exit_codes_65_and_78_without_a_recognized_reason_still_do_not_restart(int code, string line)
    {
        await using var rig = new Rig(Fast());
        Refuses(rig.Launcher, code, line);
        rig.Sidecar.Start();

        await Rig.Until(() => rig.Sidecar.Status is { Phase: SidecarPhase.Failed, Issue: SidecarIssue.LaunchFailed }, "failed");
        Assert.DoesNotContain("a=b", rig.Sidecar.Status.Detail);
        Assert.Contains($"exit code {code}", rig.Sidecar.Status.Detail);
        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(30);
        Assert.Single(rig.Launcher.Starts);
    }

    [Fact]
    public async Task Retry_after_the_master_key_is_back_recovers_and_retry_while_it_is_still_missing_fails_again_without_backoff()
    {
        await using var rig = new Rig(Fast());
        Refuses(rig.Launcher, 65, "ncw-agent startup-failed agent.masterKey.missing credential.key");
        rig.Sidecar.Start();
        await Rig.Until(() => rig.Sidecar.Status is { Phase: SidecarPhase.Failed, Issue: SidecarIssue.MasterKeyMissing }, "failed");

        rig.Sidecar.Retry();                                                // 密钥还没放回去:再拒绝一次,仍然是失败(不是退避)
        await Rig.Until(() => rig.Launcher.Starts.Count == 2, "second attempt");
        await Rig.Until(() => rig.Sidecar.Status is { Phase: SidecarPhase.Failed, Issue: SidecarIssue.MasterKeyMissing }, "failed again");
        lock (rig.Statuses)
        {
            Assert.DoesNotContain(rig.Statuses, s => s.Phase == SidecarPhase.Restarting);
        }

        rig.Launcher.OnStart = null;                                        // 用户把 credential.key 放回去了
        rig.Launcher.OnOutput = null;
        rig.Sidecar.Retry();
        await rig.UntilPhase(SidecarPhase.Running);
        Assert.Equal(SidecarIssue.None, rig.Sidecar.Status.Issue);
        Assert.Equal(3, rig.Launcher.Starts.Count);
    }

    [Fact]
    public async Task A_plain_crash_exit_code_is_still_a_crash_and_restarts()
    {
        await using var rig = new Rig(Fast());
        Refuses(rig.Launcher, 134, "ncw-agent startup-failed agent.masterKey.missing credential.key"); // 退出码不是 65 / 78:不信输出,仍按崩溃处理
        rig.Sidecar.Start();

        await Rig.Until(() => rig.Sidecar.Status is { Phase: SidecarPhase.Restarting }, "restarting");
        await rig.AdvanceUntil(() => rig.Launcher.Starts.Count >= 2, "restart");
    }

    // ---- 版本 ----

    [Fact]
    public async Task A_newer_agent_means_the_desktop_is_outdated_and_the_agent_is_left_alone()
    {
        await using var rig = new Rig(Fast());
        rig.Launcher.AgentVersion = "3.1.0";
        rig.Sidecar.Start();
        await Rig.Until(() => rig.Sidecar.Status is { Phase: SidecarPhase.Failed, Issue: SidecarIssue.ClientOutdated }, "client outdated");
        Assert.Equal("3.1.0", rig.Sidecar.Status.AgentVersion);
        Assert.Contains("3.1.0", rig.Sidecar.Status.Detail);
        Assert.Equal(0, rig.Launcher.Last.StdinClosed + rig.Launcher.Last.Terminated + rig.Launcher.Last.Killed);
        Assert.False(rig.Sidecar.WaitReadyAsync(CancellationToken.None).IsCompleted); // 不能连
    }

    [Fact]
    public async Task The_agent_rejecting_an_old_client_is_reported_as_client_outdated_and_not_retried()
    {
        await using var rig = new Rig(Fast());
        rig.Handshake.Reject = _ => new ProtocolException(ErrorCodes.ClientOutdated, "Device speaks protocol 2..3");
        rig.Sidecar.Start();
        await Rig.Until(() => rig.Sidecar.Status is { Phase: SidecarPhase.Failed, Issue: SidecarIssue.ClientOutdated }, "client outdated");
        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(30);
        Assert.Single(rig.Launcher.Starts);
    }

    [Fact]
    public async Task A_freshly_started_agent_that_is_older_than_the_desktop_means_a_broken_install_and_is_not_restarted_in_a_loop()
    {
        await using var rig = new Rig(Fast());
        rig.Launcher.AgentVersion = "2.9.0";
        rig.Sidecar.Start();
        await Rig.Until(() => rig.Sidecar.Status is { Phase: SidecarPhase.Failed, Issue: SidecarIssue.AgentOutdated }, "agent outdated");
        Assert.True(rig.Launcher.Last.HasExited);
        Assert.Single(rig.Launcher.Starts);
    }

    // ---- 接上还在运行的 Agent ----

    private static FakeProcess LeftBehind(Rig rig, string version, bool detached = true, string token = "left-token")
    {
        var old = new FakeProcess(555) { Version = version };
        rig.Launcher.Running[old.Id] = old;
        rig.StateFile.Write(new SidecarState(old.Id, token, detached, version, rig.Clock.GetUtcNow()));
        return old;
    }

    [Fact]
    public async Task Adopts_a_compatible_agent_left_running_and_reuses_its_token_without_spawning()
    {
        await using var rig = new Rig(keep: true);
        var old = LeftBehind(rig, Rig.DesktopVersion);
        var sidecar = rig.Make();
        Assert.Equal("left-token", sidecar.Token);
        sidecar.Start();
        await rig.UntilPhase(SidecarPhase.Running, sidecar);
        Assert.Empty(rig.Launcher.Starts);
        Assert.Equal(0, old.Terminated + old.Killed + old.StdinClosed);
        await sidecar.DisposeAsync(); // keep = true:留下
        Assert.False(old.HasExited);
        Assert.Equal(SidecarPhase.Stopped, sidecar.Status.Phase);
        Assert.NotNull(rig.StateFile.Read()); // 下次还能接上
    }

    [Fact]
    public async Task A_detached_agent_adopted_after_the_user_turned_keep_off_is_stopped_on_exit()
    {
        await using var rig = new Rig(keep: false);
        var old = LeftBehind(rig, Rig.DesktopVersion, detached: true);
        var sidecar = rig.Make();
        sidecar.Start();
        await rig.UntilPhase(SidecarPhase.Running, sidecar);
        await rig.DisposeSidecarAsync(sidecar);
        Assert.Equal(1, old.Terminated); // 没有 stdin 可关,直接 SIGTERM
        Assert.True(old.HasExited);
        Assert.Null(rig.StateFile.Read());
    }

    [Fact]
    public async Task An_agent_we_did_not_start_and_that_was_not_left_for_us_is_never_stopped()
    {
        await using var rig = new Rig(keep: false);
        var other = LeftBehind(rig, Rig.DesktopVersion, detached: false); // 别的桌面实例拉起的(带着它自己的 stdin 管道)
        var sidecar = rig.Make();
        sidecar.Start();
        await rig.UntilPhase(SidecarPhase.Running, sidecar);
        await rig.DisposeSidecarAsync(sidecar);
        Assert.Equal(0, other.StdinClosed + other.Terminated + other.Killed);
        Assert.False(other.HasExited);
    }

    [Fact]
    public async Task An_older_agent_left_over_from_the_previous_app_version_is_replaced_by_the_bundled_one()
    {
        await using var rig = new Rig(Fast(), keep: true);
        var old = LeftBehind(rig, "2.9.0");
        var sidecar = rig.Make();
        sidecar.Start();
        await rig.UntilPhase(SidecarPhase.Running, sidecar);
        Assert.Equal(1, old.Terminated);
        Assert.True(old.HasExited);
        Assert.Single(rig.Launcher.Starts); // 新的是包里的
        Assert.Equal(Rig.DesktopVersion, sidecar.Status.AgentVersion);
        Assert.NotEqual(old.Id, rig.StateFile.Read()!.Pid);
        await rig.DisposeSidecarAsync(sidecar);
    }

    [Fact]
    public async Task A_newer_agent_left_running_is_not_touched_and_the_desktop_asks_for_an_update()
    {
        await using var rig = new Rig(Fast(), keep: true);
        var newer = LeftBehind(rig, "3.2.0");
        var sidecar = rig.Make();
        sidecar.Start();
        await Rig.Until(() => sidecar.Status is { Phase: SidecarPhase.Failed, Issue: SidecarIssue.ClientOutdated }, "client outdated");
        Assert.Empty(rig.Launcher.Starts);
        Assert.Equal(0, newer.StdinClosed + newer.Terminated + newer.Killed);
        await rig.DisposeSidecarAsync(sidecar);
        Assert.False(newer.HasExited);
    }

    [Fact]
    public async Task A_stale_state_file_whose_process_is_gone_is_discarded_and_a_new_agent_started()
    {
        await using var rig = new Rig();
        var gone = LeftBehind(rig, Rig.DesktopVersion);
        gone.Exit(0);
        var sidecar = rig.Make();
        sidecar.Start();
        await rig.UntilPhase(SidecarPhase.Running, sidecar);
        Assert.Single(rig.Launcher.Starts);
        Assert.Equal("left-token", rig.Launcher.Starts[0].Spec.Environment["NCW_LOCAL_TOKEN"]); // 令牌沿用
        await rig.DisposeSidecarAsync(sidecar);
    }

    [Fact]
    public async Task An_agent_that_refuses_our_token_is_reported_as_held_by_someone_else_and_never_replaced()
    {
        await using var rig = new Rig(Fast());
        var other = LeftBehind(rig, Rig.DesktopVersion);
        rig.Handshake.Reject = _ => new ProtocolException(ErrorCodes.Unauthorized, "Not authorized");
        var sidecar = rig.Make();
        sidecar.Start();
        await Rig.Until(() => sidecar.Status is { Phase: SidecarPhase.Failed, Issue: SidecarIssue.AlreadyRunningElsewhere }, "held elsewhere");
        Assert.Empty(rig.Launcher.Starts);
        Assert.False(other.HasExited);
        await rig.DisposeSidecarAsync(sidecar);
    }

    [Fact]
    public async Task An_adopted_agent_without_a_process_handle_is_health_checked_and_replaced_when_it_vanishes()
    {
        await using var rig = new Rig(Fast(), keep: true);
        var old = LeftBehind(rig, Rig.DesktopVersion);
        var sidecar = rig.Make();
        sidecar.Start();
        await rig.UntilPhase(SidecarPhase.Running, sidecar);

        old.Listening = false; // 卡死:进程还在但不应答握手
        await rig.AdvanceUntil(() => sidecar.Status.Phase == SidecarPhase.Restarting || rig.Launcher.Starts.Count > 0, "noticed", TimeSpan.FromSeconds(5));
        await rig.AdvanceUntil(() => rig.Launcher.Starts.Count >= 1 && sidecar.Status.Phase == SidecarPhase.Running, "replaced", TimeSpan.FromSeconds(1));
        await rig.DisposeSidecarAsync(sidecar);
    }

    // ---- 退出 ----

    [Fact]
    public async Task Exit_without_keep_closes_stdin_and_waits_for_a_clean_exit_then_removes_the_state()
    {
        await using var rig = new Rig(keep: false);
        rig.Sidecar.Start();
        await rig.UntilPhase(SidecarPhase.Running);
        var process = rig.Launcher.Last;
        await rig.DisposeSidecarAsync();
        Assert.Equal(1, process.StdinClosed);
        Assert.Equal(0, process.Terminated + process.Killed);
        Assert.Equal(0, process.ExitCode);
        Assert.Null(rig.StateFile.Read());
        Assert.Equal(SidecarPhase.Stopped, rig.Sidecar.Status.Phase);
    }

    [Fact]
    public async Task Exit_escalates_from_stdin_to_terminate_to_kill_when_the_agent_does_not_leave()
    {
        await using var rig = new Rig(keep: false);
        rig.Sidecar.Start();
        await rig.UntilPhase(SidecarPhase.Running);
        var process = rig.Launcher.Last;
        process.ExitOnStdinClose = false;
        process.ExitOnTerminate = false;
        await rig.DisposeSidecarAsync();
        Assert.Equal(1, process.StdinClosed);
        Assert.Equal(1, process.Terminated);
        Assert.Equal(1, process.Killed);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task Exit_skips_terminate_where_the_platform_has_no_signal()
    {
        await using var rig = new Rig(keep: false);
        rig.Sidecar.Start();
        await rig.UntilPhase(SidecarPhase.Running);
        var process = rig.Launcher.Last;
        process.ExitOnStdinClose = false;
        process.SupportsTerminate = false; // Windows
        await rig.DisposeSidecarAsync();
        Assert.Equal(1, process.Killed);
    }

    [Fact]
    public async Task Exit_with_keep_leaves_the_detached_agent_running_untouched()
    {
        await using var rig = new Rig(keep: true);
        rig.Sidecar.Start();
        await rig.UntilPhase(SidecarPhase.Running);
        var process = rig.Launcher.Last;
        await rig.DisposeSidecarAsync();
        Assert.False(process.HasExited);
        Assert.Equal(0, process.StdinClosed + process.Terminated + process.Killed);
        Assert.NotNull(rig.StateFile.Read());
        Assert.Equal(SidecarPhase.Stopped, rig.Sidecar.Status.Phase);
        Assert.Contains("left running", rig.Sidecar.Status.Detail);
    }

    [Fact]
    public async Task Turning_keep_on_after_the_agent_started_with_a_lifeline_still_stops_it_cleanly_and_applies_next_start()
    {
        await using var rig = new Rig(keep: false);
        rig.Sidecar.Start();
        await rig.UntilPhase(SidecarPhase.Running);
        var process = rig.Launcher.Last;
        rig.Policy.KeepAgentRunningAfterExit = true; // 运行中打开开关:这个 Agent 带着 stdin 管道,桌面端退出它也会走
        await rig.DisposeSidecarAsync();
        Assert.True(process.HasExited);
        Assert.Equal(1, process.StdinClosed);
    }

    [Fact]
    public async Task Dispose_while_waiting_for_a_restart_does_not_start_another_agent()
    {
        await using var rig = new Rig(Fast());
        rig.Sidecar.Start();
        await rig.UntilPhase(SidecarPhase.Running);
        rig.Launcher.Last.Exit(1);
        await Rig.Until(() => rig.Sidecar.Status.Phase == SidecarPhase.Restarting, "restarting");
        await rig.DisposeSidecarAsync();
        Assert.Single(rig.Launcher.Starts);
    }

    [Fact]
    public async Task Agent_output_is_forwarded_to_the_log()
    {
        await using var rig = new Rig();
        rig.Sidecar.Start();
        await rig.UntilPhase(SidecarPhase.Running);
        Assert.Contains(rig.Log.Lines, l => l.StartsWith("ncw-agent ready", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_token_is_stable_across_desktop_sessions_via_the_state_file()
    {
        await using var rig = new Rig();
        rig.Sidecar.Start();
        await rig.UntilPhase(SidecarPhase.Running);
        var token = rig.Sidecar.Token;
        await rig.DisposeSidecarAsync();
        // 正常退出会删状态文件 → 新会话是新令牌;keep 时保留 → 同一个令牌
        Assert.NotEqual(token, rig.Make().Token);

        await using var keepRig = new Rig(keep: true);
        keepRig.Sidecar.Start();
        await keepRig.UntilPhase(SidecarPhase.Running);
        await keepRig.DisposeSidecarAsync();
        Assert.Equal(keepRig.Sidecar.Token, keepRig.Make().Token);
    }
}
