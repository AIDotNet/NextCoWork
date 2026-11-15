using System.Collections.Immutable;
using Ncw.Core.Domain;

namespace Ncw.Core.Tests;

/// <summary>启动阶段、进度、失败、慢启动与出现 / 停留 / 淡出时序(docs/design/03 §10)。时钟全部由测试传入。</summary>
public class StartupProgressTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double ms) => T0.AddMilliseconds(ms);

    private static StartupFacts Desktop() => StartupFacts.Begin(StartupProfile.Desktop, T0);

    private static StartupFacts Reach(StartupFacts f, double ms, params StartupMilestone[] milestones) =>
        milestones.Aggregate(f, (acc, m) => acc.Reach(m, At(ms)));

    private static readonly StartupMilestone[] AppDone =
        [StartupMilestone.InstanceReady, StartupMilestone.WindowShown, StartupMilestone.PlatformReady, StartupMilestone.AccountDecided];

    private static StartupFacts UpToAgent(double ms = 100) => Reach(Desktop(), ms, AppDone);

    private static StartupFacts UpToConnected() =>
        Reach(Reach(UpToAgent(), 300, StartupMilestone.AgentSpawned), 700, StartupMilestone.AgentReady, StartupMilestone.Connected) with { Connection = StartupConnection.Connected };

    private static StartupFacts UpToData() => Reach(UpToConnected(), 800, StartupMilestone.DataReady) with { Data = new StartupData(StartupDataPhase.Ready) };

    private static StartupFacts AllContent() => Reach(UpToData(), 900, StartupMilestone.TopicsSynced, StartupMilestone.ProjectsLoaded, StartupMilestone.SessionsLoaded);

    // ───────────────────────── 阶段与完成条件 ─────────────────────────

    [Fact]
    public void Desktop_has_five_stages_and_the_phone_has_three()
    {
        Assert.Equal([StartupStage.App, StartupStage.Agent, StartupStage.Connect, StartupStage.Data, StartupStage.Content], StartupProgress.StagesOf(StartupProfile.Desktop));
        Assert.Equal([StartupStage.App, StartupStage.Connect, StartupStage.Content], StartupProgress.StagesOf(StartupProfile.Remote));
    }

    [Fact]
    public void Nothing_reached_means_the_first_stage_is_active_and_progress_is_zero()
    {
        var view = StartupProgress.Derive(Desktop(), At(10));

        Assert.Equal(StartupPhase.Loading, view.Phase);
        Assert.Equal(StartupOwner.Startup, view.Owner);
        Assert.Equal(StartupStage.App, view.Current);
        Assert.Equal(StartupStepState.Active, view.Steps[0].State);
        Assert.All(view.Steps.Skip(1), s => Assert.Equal(StartupStepState.Pending, s.State));
        Assert.Equal(0, view.Progress.Fraction);
        Assert.Equal(5, view.Progress.StepsTotal);
        Assert.False(view.Progress.IsDeterminateInStage);
        Assert.Equal(StartupDetail.AppStarting, view.Detail);
    }

    [Theory]
    [InlineData(StartupMilestone.InstanceReady)]
    [InlineData(StartupMilestone.WindowShown)]
    [InlineData(StartupMilestone.PlatformReady)]
    [InlineData(StartupMilestone.AccountDecided)]
    public void The_app_stage_needs_every_one_of_its_milestones(StartupMilestone missing)
    {
        var f = Reach(Desktop(), 50, AppDone.Where(m => m != missing).ToArray());

        Assert.Equal(StartupStage.App, StartupProgress.Derive(f, At(60)).Current);
        Assert.Equal(StartupStage.Agent, StartupProgress.Derive(f.Reach(missing, At(70)), At(80)).Current);
    }

    [Fact]
    public void The_agent_stage_goes_launching_then_handshake_and_only_the_handshake_completes_it()
    {
        var f = UpToAgent();
        Assert.Equal(StartupDetail.AgentLaunching, StartupProgress.Derive(f, At(150)).Detail);

        f = f.Reach(StartupMilestone.AgentSpawned, At(200));
        var view = StartupProgress.Derive(f, At(250));
        Assert.Equal(StartupStage.Agent, view.Current);   // 进程拉起了不算就绪
        Assert.Equal(StartupDetail.AgentHandshake, view.Detail);

        view = StartupProgress.Derive(f.Reach(StartupMilestone.AgentReady, At(600)), At(610));
        Assert.Equal(StartupStage.Connect, view.Current);
        Assert.Equal(StartupDetail.Connecting, view.Detail);
    }

    [Fact]
    public void An_adopted_agent_has_no_spawn_milestone_but_still_completes_on_the_handshake()
    {
        var f = Reach(UpToAgent(), 150, StartupMilestone.AgentReady);

        Assert.Equal(StartupStage.Connect, StartupProgress.Derive(f, At(160)).Current);
    }

    [Fact]
    public void The_content_stage_needs_snapshots_projects_and_sessions()
    {
        var f = UpToData();
        Assert.Equal(StartupDetail.LoadingProjects, StartupProgress.Derive(f, At(850)).Detail);

        f = Reach(f, 850, StartupMilestone.TopicsSynced);
        Assert.Equal(StartupDetail.LoadingProjects, StartupProgress.Derive(f, At(860)).Detail);

        f = Reach(f, 860, StartupMilestone.ProjectsLoaded);
        var view = StartupProgress.Derive(f, At(870));
        Assert.Equal(StartupStage.Content, view.Current);
        Assert.Equal(StartupDetail.LoadingSessions, view.Detail);

        view = StartupProgress.Derive(Reach(f, 880, StartupMilestone.SessionsLoaded), At(890));
        Assert.True(view.AllStepsDone);
        Assert.Equal(StartupPhase.Settling, view.Phase);
        Assert.Equal(StartupDetail.Settling, view.Detail);
    }

    [Fact]
    public void Settling_becomes_revealing_only_after_the_shell_has_laid_out_and_done_after_interactive()
    {
        var f = AllContent();
        Assert.Equal(StartupPhase.Settling, StartupProgress.Derive(f, At(950)).Phase);

        f = f.Reach(StartupMilestone.ShellLaidOut, At(980));
        var revealing = StartupProgress.Derive(f, At(990));
        Assert.Equal(StartupPhase.Revealing, revealing.Phase);
        Assert.Equal(StartupOwner.Startup, revealing.Owner);   // 淡出期间覆盖层还在
        Assert.Equal(1, revealing.Progress.Fraction);

        var done = StartupProgress.Derive(f.Reach(StartupMilestone.Interactive, At(1200)), At(1210));
        Assert.Equal(StartupPhase.Done, done.Phase);
        Assert.Equal(StartupOwner.None, done.Owner);
    }

    // ───────────────────────── 单调(规则 6)与断线(规则 7) ─────────────────────────

    [Fact]
    public void Replaying_an_older_event_does_not_move_a_reached_milestone()
    {
        var f = UpToData();
        var first = f.Reached[StartupMilestone.AgentReady];

        var replayed = f.Reach(StartupMilestone.AgentReady, At(5000)).Reach(StartupMilestone.AgentReady, At(1));

        Assert.Equal(first, replayed.Reached[StartupMilestone.AgentReady]);
        Assert.Same(f, f.Reach(StartupMilestone.AgentReady, At(5000)));
    }

    [Fact]
    public void Progress_never_goes_backwards_while_the_signals_swing_back_and_forth()
    {
        // 一串真实会发生的「抖动」:Agent 重启、连接断了又连上、数据状态回到准备中、接管失败后重试从头来
        var states = new List<StartupFacts>();
        var f = Desktop();
        states.Add(f);
        f = Reach(f, 100, AppDone);
        states.Add(f);
        f = f.Reach(StartupMilestone.AgentSpawned, At(150)) with { Sidecar = new StartupSidecar(StartupSidecarPhase.Starting) };
        states.Add(f);
        f = f.Reach(StartupMilestone.AgentReady, At(600)) with { Sidecar = new StartupSidecar(StartupSidecarPhase.Running) };
        states.Add(f);
        f = f.Reach(StartupMilestone.Connected, At(650)) with { Connection = StartupConnection.Connected };
        states.Add(f);
        f = f with { Connection = StartupConnection.Connecting, Sidecar = new StartupSidecar(StartupSidecarPhase.Restarting, RestartCount: 1) };   // Agent 崩溃重启
        states.Add(f);
        f = (f with { Data = new StartupData(StartupDataPhase.TakingOver, "migrating") }).ReachDataStep(2);
        states.Add(f);
        f = f with { Data = new StartupData(StartupDataPhase.NeedsUser) };   // 失败回退
        states.Add(f);
        f = (f with { Data = new StartupData(StartupDataPhase.TakingOver, "backing-up") }).ReachDataStep(1);   // 重试从头
        states.Add(f);
        f = f with { Connection = StartupConnection.Connected, Sidecar = new StartupSidecar(StartupSidecarPhase.Running, RestartCount: 1) };
        states.Add(f);
        f = f.Reach(StartupMilestone.DataReady, At(5000)) with { Data = new StartupData(StartupDataPhase.Ready) };
        states.Add(f);

        var fractions = states.Select((s, i) => StartupProgress.Derive(s, At(1000 + i)).Progress.Fraction).ToList();

        for (var i = 1; i < fractions.Count; i++)
        {
            Assert.True(fractions[i] >= fractions[i - 1], $"step {i}: {fractions[i]} < {fractions[i - 1]}");
        }
    }

    [Fact]
    public void A_disconnect_before_the_data_stage_keeps_finished_steps_finished_and_only_changes_the_line()
    {
        var f = UpToConnected() with { Connection = StartupConnection.Connecting, Sidecar = new StartupSidecar(StartupSidecarPhase.Restarting, RestartCount: 1) };

        var view = StartupProgress.Derive(f, At(900));

        Assert.True(view.IsReconnecting);
        Assert.Equal(StartupStage.Data, view.Current);
        Assert.Equal(StartupDetail.Reconnecting, view.Detail);
        Assert.All(view.Steps.Take(3), s => Assert.Equal(StartupStepState.Done, s.State));   // 没有回到「正在启动」
        Assert.Equal(StartupPhase.Loading, view.Phase);
        Assert.Null(view.Failure);
    }

    [Fact]
    public void Once_interactive_nothing_brings_the_full_screen_loading_back()
    {
        var f = AllContent().Reach(StartupMilestone.ShellLaidOut, At(950)).Reach(StartupMilestone.Interactive, At(1100));

        foreach (var broken in new[]
        {
            f with { Connection = StartupConnection.Offline },
            f with { Connection = StartupConnection.Connecting },
            f with { Sidecar = new StartupSidecar(StartupSidecarPhase.Failed, "crash-loop") },
            f with { Sidecar = new StartupSidecar(StartupSidecarPhase.Restarting, RestartCount: 3) },
            f with { Data = new StartupData(StartupDataPhase.NeedsUser) },
            f with { Data = new StartupData(StartupDataPhase.TakingOver, "backing-up") },
        })
        {
            var view = StartupProgress.Derive(broken, T0.AddHours(3));

            Assert.Equal(StartupPhase.Done, view.Phase);
            Assert.Equal(StartupOwner.None, view.Owner);
            Assert.Null(view.Failure);
            Assert.False(view.IsSlow);
            Assert.Null(view.NextTimeAt);
        }
    }

    // ───────────────────────── 数据接管:真实的步骤 ─────────────────────────

    [Theory]
    [InlineData("backing-up", 1)]
    [InlineData("migrating", 2)]
    [InlineData("committing", 3)]
    [InlineData("credentials", 4)]
    [InlineData("something-new", 0)]
    [InlineData(null, 0)]
    public void Takeover_steps_map_to_their_ordinal(string? step, int ordinal) => Assert.Equal(ordinal, StartupProgress.DataStepOrdinal(step));

    [Fact]
    public void Takeover_progress_is_determinate_only_inside_the_data_stage_and_counts_finished_steps()
    {
        var f = UpToConnected();
        Assert.False(StartupProgress.Derive(f, At(750)).Progress.IsDeterminateInStage);   // 还没看到接管步骤:不确定

        var fractions = new List<double>();
        foreach (var (step, ordinal) in new[] { ("backing-up", 1), ("migrating", 2), ("committing", 3), ("credentials", 4) })
        {
            f = (f with { Data = new StartupData(StartupDataPhase.TakingOver, step) }).ReachDataStep(ordinal);
            var view = StartupProgress.Derive(f, At(1000 + ordinal));
            Assert.Equal(StartupStage.Data, view.Current);
            Assert.Equal(StartupDetail.DataTakeover, view.Detail);
            Assert.Equal(step, view.DataStep);
            Assert.True(view.Progress.IsDeterminateInStage);
            Assert.Equal(ordinal, view.Progress.SubStep);
            Assert.Equal(4, view.Progress.SubTotal);
            fractions.Add(view.Progress.Fraction);
        }

        // 3 个阶段已完成;数据阶段里每多完成一步,进度多 1/(4×5)
        Assert.Equal(3 / 5.0, fractions[0], 6);
        Assert.Equal(3 / 5.0 + 1 / 20.0, fractions[1], 6);
        Assert.Equal(3 / 5.0 + 2 / 20.0, fractions[2], 6);
        Assert.Equal(3 / 5.0 + 3 / 20.0, fractions[3], 6);
        Assert.True(fractions.Zip(fractions.Skip(1), (a, b) => b > a).All(x => x));
    }

    [Fact]
    public void A_retried_takeover_that_starts_over_does_not_pull_the_bar_back()
    {
        var f = (UpToConnected() with { Data = new StartupData(StartupDataPhase.TakingOver, "committing") }).ReachDataStep(3);
        var before = StartupProgress.Derive(f, At(900)).Progress;

        f = (f with { Data = new StartupData(StartupDataPhase.TakingOver, "backing-up") }).ReachDataStep(1);
        var after = StartupProgress.Derive(f, At(1500)).Progress;

        Assert.Equal(before.Fraction, after.Fraction);
        Assert.Equal(3, after.SubStep);   // 锁存:只增不减
    }

    [Fact]
    public void Finishing_the_takeover_completes_the_data_stage_and_clears_the_step()
    {
        var f = (UpToConnected() with { Data = new StartupData(StartupDataPhase.TakingOver, "credentials") }).ReachDataStep(4);
        f = Reach(f, 2000, StartupMilestone.DataReady) with { Data = new StartupData(StartupDataPhase.Ready) };

        var view = StartupProgress.Derive(f, At(2010));

        Assert.Equal(StartupStage.Content, view.Current);
        Assert.Null(view.DataStep);
        Assert.False(view.Progress.IsDeterminateInStage);
        Assert.Equal(4 / 5.0, view.Progress.Fraction, 6);
    }

    // ───────────────────────── 数据页让位 ─────────────────────────

    [Fact]
    public void Data_that_needs_the_user_hands_the_screen_to_the_data_page_and_takes_it_back_when_busy_again()
    {
        var f = UpToConnected() with { Data = new StartupData(StartupDataPhase.NeedsUser) };

        var gate = StartupProgress.Derive(f, At(900));
        Assert.Equal(StartupPhase.NeedsUser, gate.Phase);
        Assert.Equal(StartupOwner.DataGate, gate.Owner);
        Assert.Null(gate.Failure);
        Assert.False(gate.IsSlow);

        var back = StartupProgress.Derive(f with { Data = new StartupData(StartupDataPhase.TakingOver, "backing-up") }, At(950));
        Assert.Equal(StartupOwner.Startup, back.Owner);   // 用户点了「重试」:又是「在等」,回到加载界面
    }

    [Fact]
    public void The_completed_takeover_summary_also_belongs_to_the_data_page()
    {
        // 接管完成后数据页还要显示摘要、等「继续」:数据已就绪,但整窗仍归数据页
        var f = UpToData() with { Data = new StartupData(StartupDataPhase.NeedsUser) };

        Assert.Equal(StartupOwner.DataGate, StartupProgress.Derive(f, At(900)).Owner);
    }

    [Fact]
    public void The_phone_never_hands_over_to_the_data_page()
    {
        var f = Reach(StartupFacts.Begin(StartupProfile.Remote, T0), 50, AppDone) with { Data = new StartupData(StartupDataPhase.NeedsUser) };

        Assert.Equal(StartupOwner.Startup, StartupProgress.Derive(f, At(60)).Owner);
    }

    // ───────────────────────── 慢启动 ─────────────────────────

    [Fact]
    public void Slow_starts_exactly_at_the_threshold_and_never_while_the_user_is_being_asked()
    {
        var timings = new StartupTimings { SlowAfter = TimeSpan.FromSeconds(8) };
        var f = UpToAgent();

        Assert.False(StartupProgress.Derive(f, At(7999), timings).IsSlow);
        Assert.True(StartupProgress.Derive(f, At(8000), timings).IsSlow);
        Assert.False(StartupProgress.Derive(f with { Data = new StartupData(StartupDataPhase.NeedsUser) }, At(20000), timings).IsSlow);
    }

    [Theory]
    [InlineData(StartupStage.Agent, StartupSlowHint.Agent)]
    [InlineData(StartupStage.Connect, StartupSlowHint.Generic)]
    [InlineData(StartupStage.Data, StartupSlowHint.Generic)]
    [InlineData(StartupStage.Content, StartupSlowHint.Content)]
    public void The_slow_hint_names_the_stage_that_is_taking_long(StartupStage stage, StartupSlowHint expected)
    {
        var f = stage switch
        {
            StartupStage.Agent => UpToAgent(),
            StartupStage.Connect => Reach(UpToAgent(), 200, StartupMilestone.AgentReady),
            StartupStage.Data => UpToConnected(),
            _ => UpToData(),
        };

        Assert.Equal(expected, StartupProgress.Derive(f, At(9000)).SlowHint);
    }

    [Fact]
    public void A_slow_takeover_gets_the_data_hint()
    {
        var f = (UpToConnected() with { Data = new StartupData(StartupDataPhase.TakingOver, "migrating") }).ReachDataStep(2);

        Assert.Equal(StartupSlowHint.Data, StartupProgress.Derive(f, At(12000)).SlowHint);
    }

    [Fact]
    public void Slow_is_measured_from_the_process_start_when_it_was_recorded()
    {
        var f = StartupFacts.Begin(StartupProfile.Desktop, At(500)).Reach(StartupMilestone.ProcessStarted, T0);

        Assert.False(StartupProgress.Derive(f, At(7000)).IsSlow);
        Assert.True(StartupProgress.Derive(f, At(8000)).IsSlow);   // 起点是进程创建,不是开始记录
    }

    [Fact]
    public void The_view_says_when_it_will_change_by_itself()
    {
        var f = UpToAgent();   // Agent 阶段:只有慢启动阈值
        Assert.Equal(T0 + TimeSpan.FromSeconds(8), StartupProgress.Derive(f, At(100)).NextTimeAt);

        // 已经慢了、在「连接」阶段:下一个是阶段超时(阶段从 Agent 就绪那一刻算起)
        var connect = Reach(f, 700, StartupMilestone.AgentReady);
        var view = StartupProgress.Derive(connect, At(9000));
        Assert.True(view.IsSlow);
        Assert.Equal(At(700) + TimeSpan.FromSeconds(30), view.NextTimeAt);

        // 全部完成、没有时间触发的变化
        var settled = AllContent().Reach(StartupMilestone.ShellLaidOut, At(1000));
        Assert.Null(StartupProgress.Derive(settled, At(9000)).NextTimeAt);
    }

    // ───────────────────────── 失败 ─────────────────────────

    [Theory]
    [InlineData("agent-not-found", true)]
    [InlineData("launch-failed", true)]
    [InlineData("already-running-elsewhere", true)]
    [InlineData("crash-loop", true)]
    [InlineData("agent-outdated", false)]
    [InlineData("client-outdated", false)]
    public void Every_sidecar_issue_becomes_a_failure_with_the_right_actions(string issue, bool canRetry)
    {
        var f = UpToAgent() with { Sidecar = new StartupSidecar(StartupSidecarPhase.Failed, issue, 2, Detail: "exit 75") };

        var view = StartupProgress.Derive(f, At(400));

        Assert.Equal(StartupPhase.Failed, view.Phase);
        Assert.Equal(StartupOwner.Startup, view.Owner);
        Assert.Equal(issue, view.Failure!.Reason);
        Assert.Equal(canRetry, view.Failure.CanRetry);
        Assert.True(view.Failure.CanShowDiagnostics);
        Assert.True(view.Failure.CanQuit);
        Assert.Equal("exit 75", view.Failure.Detail);
        Assert.False(view.IsSlow);   // 失败了就不再说「慢」
    }

    [Theory]
    [InlineData("already-running-elsewhere")]
    [InlineData("launch-failed")]
    public void A_problem_that_needs_the_user_is_shown_as_a_failure_while_the_guard_keeps_retrying_and_clears_itself(string issue)
    {
        var f = Reach(UpToAgent(), 200, StartupMilestone.AgentSpawned) with { Sidecar = new StartupSidecar(StartupSidecarPhase.Restarting, issue, 1, TimeSpan.FromSeconds(5), "exit 75") };

        var view = StartupProgress.Derive(f, At(400));

        Assert.Equal(StartupPhase.Failed, view.Phase);
        Assert.Equal(issue, view.Failure!.Reason);
        Assert.True(view.Failure.CanRetry);

        // 守护下一轮成功了(那个进程退出了 / 权限放开了):失败自己消失
        var back = StartupProgress.Derive(f with { Sidecar = new StartupSidecar(StartupSidecarPhase.Starting) }, At(6000));
        Assert.Null(back.Failure);
    }

    [Fact]
    public void An_agent_that_exits_twice_before_ever_becoming_ready_is_a_crash_loop_failure_that_clears_when_a_start_succeeds()
    {
        var f = Reach(UpToAgent(), 200, StartupMilestone.AgentSpawned);

        Assert.Null(StartupProgress.Derive(f with { Sidecar = new StartupSidecar(StartupSidecarPhase.Restarting, RestartCount: 1, Detail: "exit 3") }, At(400)).Failure);

        var twice = StartupProgress.Derive(f with { Sidecar = new StartupSidecar(StartupSidecarPhase.Restarting, RestartCount: 2, Detail: "exit 3") }, At(4000));
        Assert.Equal("crash-loop", twice.Failure!.Reason);
        Assert.Equal("exit 3", twice.Failure.Detail);

        // 这一轮握手成功了:没有失败
        var ok = Reach(f, 5000, StartupMilestone.AgentReady) with { Sidecar = new StartupSidecar(StartupSidecarPhase.Running, RestartCount: 2) };
        Assert.Null(StartupProgress.Derive(ok, At(5010)).Failure);

        // 已经就绪过的 Agent 之后崩溃重启,不算「启动失败」(运行中的事,走断线条)
        var laterCrash = ok with { Sidecar = new StartupSidecar(StartupSidecarPhase.Restarting, RestartCount: 3) };
        Assert.Null(StartupProgress.Derive(laterCrash, At(6000)).Failure);
    }

    [Fact]
    public void A_restarting_agent_is_not_a_failure_but_says_so()
    {
        var f = Reach(UpToAgent(), 200, StartupMilestone.AgentSpawned) with { Sidecar = new StartupSidecar(StartupSidecarPhase.Restarting, RestartCount: 1, RetryIn: TimeSpan.FromSeconds(2)) };

        var view = StartupProgress.Derive(f, At(400));

        Assert.Null(view.Failure);
        Assert.Equal(StartupPhase.Loading, view.Phase);
        Assert.Equal(StartupDetail.AgentRestarting, view.Detail);
    }

    [Fact]
    public void A_failure_is_derived_not_latched_so_it_clears_when_the_agent_recovers()
    {
        var failed = UpToAgent() with { Sidecar = new StartupSidecar(StartupSidecarPhase.Failed, "crash-loop") };
        Assert.Equal(StartupPhase.Failed, StartupProgress.Derive(failed, At(400)).Phase);

        var recovered = Reach(failed, 900, StartupMilestone.AgentReady) with { Sidecar = new StartupSidecar(StartupSidecarPhase.Running) };
        var view = StartupProgress.Derive(recovered, At(910));

        Assert.Null(view.Failure);
        Assert.Equal(StartupPhase.Loading, view.Phase);
        Assert.Equal(StartupStage.Connect, view.Current);
    }

    [Fact]
    public void A_running_agent_that_cannot_be_reached_is_a_connection_failure()
    {
        var f = Reach(UpToAgent(), 700, StartupMilestone.AgentReady) with { Sidecar = new StartupSidecar(StartupSidecarPhase.Running), Connection = StartupConnection.Offline };

        var view = StartupProgress.Derive(f, At(4000));

        Assert.Equal("connection-failed", view.Failure!.Reason);
        Assert.True(view.Failure.CanRetry);
    }

    [Theory]
    [InlineData(StartupStage.Connect, true)]
    [InlineData(StartupStage.Content, true)]
    [InlineData(StartupStage.Agent, false)]   // 守护自己有就绪超时和退避
    [InlineData(StartupStage.Data, false)]    // 接管本来就可能很久
    public void Only_connect_and_content_time_out(StartupStage stage, bool times_out)
    {
        var f = stage switch
        {
            StartupStage.Agent => UpToAgent(),
            StartupStage.Connect => Reach(UpToAgent(), 200, StartupMilestone.AgentReady),
            StartupStage.Data => UpToConnected(),
            _ => UpToData(),
        };
        var stageStart = StartupProgress.StageStartedAt(f, stage);

        Assert.Null(StartupProgress.Derive(f, stageStart.AddSeconds(29.9)).Failure);
        var view = StartupProgress.Derive(f, stageStart.AddSeconds(30));
        Assert.Equal(times_out, view.Failure?.Reason == "timeout");
    }

    [Fact]
    public void A_timeout_clears_itself_when_the_stage_finally_completes()
    {
        var f = UpToData();
        var late = StartupProgress.StageStartedAt(f, StartupStage.Content).AddSeconds(31);
        Assert.Equal("timeout", StartupProgress.Derive(f, late).Failure!.Reason);

        var view = StartupProgress.Derive(Reach(f, 31900, StartupMilestone.TopicsSynced, StartupMilestone.ProjectsLoaded, StartupMilestone.SessionsLoaded), late);

        Assert.Null(view.Failure);
        Assert.True(view.AllStepsDone);
    }

    [Fact]
    public void The_phone_never_shows_a_failure_and_settles_when_every_device_is_offline()
    {
        var f = Reach(StartupFacts.Begin(StartupProfile.Remote, T0), 50, AppDone) with { Connection = StartupConnection.Offline };

        var view = StartupProgress.Derive(f, T0.AddMinutes(5));

        Assert.Null(view.Failure);
        Assert.True(view.AllStepsDone);   // 离线的设备不挡着进入外壳:设备页显示离线
        Assert.Equal(StartupPhase.Settling, view.Phase);
    }

    [Fact]
    public void The_phone_with_no_devices_has_nothing_to_wait_for()
    {
        var f = Reach(StartupFacts.Begin(StartupProfile.Remote, T0), 50, AppDone) with { Connection = StartupConnection.None };

        Assert.True(StartupProgress.Derive(f, At(60)).AllStepsDone);
    }

    [Fact]
    public void The_phone_waits_for_a_connecting_device_and_then_for_its_first_content()
    {
        var f = Reach(StartupFacts.Begin(StartupProfile.Remote, T0), 50, AppDone) with { Connection = StartupConnection.Connecting };
        Assert.Equal(StartupStage.Connect, StartupProgress.Derive(f, At(60)).Current);

        f = Reach(f, 400, StartupMilestone.Connected) with { Connection = StartupConnection.Connected };
        Assert.Equal(StartupStage.Content, StartupProgress.Derive(f, At(410)).Current);

        f = Reach(f, 600, StartupMilestone.TopicsSynced, StartupMilestone.ProjectsLoaded, StartupMilestone.SessionsLoaded);
        Assert.True(StartupProgress.Derive(f, At(610)).AllStepsDone);
    }

    // ───────────────────────── 时间线与诊断 ─────────────────────────

    [Fact]
    public void The_timeline_is_ordered_and_measured_from_the_process_start()
    {
        var f = StartupFacts.Begin(StartupProfile.Desktop, At(40)).Reach(StartupMilestone.ProcessStarted, T0).Reach(StartupMilestone.WindowShown, At(300)).Reach(StartupMilestone.InstanceReady, At(100));

        var timeline = StartupProgress.Timeline(f);

        Assert.Equal([StartupMilestone.ProcessStarted, StartupMilestone.InstanceReady, StartupMilestone.WindowShown], timeline.Select(t => t.Milestone));
        Assert.Equal([0, 100, 300], timeline.Select(t => t.Ms));
    }

    [Fact]
    public void Diagnostics_carry_the_stage_failure_sidecar_and_timeline_and_nothing_secret()
    {
        var f = Reach(UpToAgent(), 200, StartupMilestone.AgentSpawned) with { Sidecar = new StartupSidecar(StartupSidecarPhase.Failed, "crash-loop", 8, Detail: "agent exited with code 3") };
        var view = StartupProgress.Derive(f, At(5000));

        var text = StartupProgress.Diagnostics(f, view, At(5000));

        Assert.Contains("phase: Failed", text);
        Assert.Contains("failure: crash-loop", text);
        Assert.Contains("sidecar: Failed issue=crash-loop restarts=8", text);
        Assert.Contains("agent exited with code 3", text);
        Assert.Contains("AgentSpawned", text);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────────── 出现 / 停留 / 淡出时序 ─────────────────────────

    private static StartupView View(StartupFacts f, double ms) => StartupProgress.Derive(f, At(ms));

    private static StartupPresentation.State Fresh() => new(T0);

    [Fact]
    public void The_backdrop_is_there_from_the_first_frame_but_the_content_waits_for_the_delay()
    {
        var f = UpToAgent();

        var first = StartupPresentation.Decide(View(f, 0), Fresh(), At(0));
        Assert.True(first.IsOpen);
        Assert.False(first.ContentVisible);
        Assert.Equal(At(250), first.WakeAt);

        var before = StartupPresentation.Decide(View(f, 249), first.Next, At(249));
        Assert.False(before.ContentVisible);

        var after = StartupPresentation.Decide(View(f, 250), first.Next, At(250));
        Assert.True(after.ContentVisible);
        Assert.Equal(At(250), after.Next.ContentShownAt);
    }

    [Fact]
    public void A_start_that_is_ready_before_the_delay_never_shows_content_and_reveals_at_once()
    {
        var f = AllContent().Reach(StartupMilestone.ShellLaidOut, At(220));

        var decision = StartupPresentation.Decide(View(f, 230), Fresh(), At(230));

        Assert.True(decision.Reveal);
        Assert.False(decision.ContentVisible);   // 没有一闪而过的加载界面
        Assert.Null(decision.Next.ContentShownAt);
        Assert.Equal(At(230), decision.Next.RevealStartedAt);
    }

    [Fact]
    public void Content_that_has_appeared_stays_for_the_minimum_dwell_even_if_loading_ends_right_after()
    {
        var shown = new StartupPresentation.State(T0, ContentShownAt: At(250));
        var f = AllContent().Reach(StartupMilestone.ShellLaidOut, At(300));

        var early = StartupPresentation.Decide(View(f, 300), shown, At(300));
        Assert.False(early.Reveal);
        Assert.True(early.ContentVisible);
        Assert.Equal(At(850), early.WakeAt);

        var justBefore = StartupPresentation.Decide(View(f, 849), shown, At(849));
        Assert.False(justBefore.Reveal);

        var exactly = StartupPresentation.Decide(View(f, 850), shown, At(850));
        Assert.True(exactly.Reveal);
    }

    [Fact]
    public void A_long_load_reveals_as_soon_as_it_is_ready_without_extra_waiting()
    {
        var shown = new StartupPresentation.State(T0, ContentShownAt: At(250));
        var f = AllContent().Reach(StartupMilestone.ShellLaidOut, At(5000));

        var decision = StartupPresentation.Decide(View(f, 5000), shown, At(5000));

        Assert.True(decision.Reveal);
        Assert.Equal(At(5000), decision.Next.RevealStartedAt);
    }

    [Fact]
    public void Once_the_reveal_has_started_replayed_events_cannot_bring_the_loading_back()
    {
        var revealing = new StartupPresentation.State(T0, At(250), At(900));
        var regressed = UpToAgent() with { Connection = StartupConnection.Offline };   // 重放的旧状态:看起来又没加载完

        var decision = StartupPresentation.Decide(View(regressed, 950), revealing, At(950));

        Assert.True(decision.Reveal);
        Assert.True(decision.IsOpen);
        Assert.True(decision.ContentVisible);
        Assert.Equal(revealing, decision.Next);
    }

    [Fact]
    public void A_failure_shows_its_panel_immediately_without_waiting_for_the_delay()
    {
        var f = UpToAgent() with { Sidecar = new StartupSidecar(StartupSidecarPhase.Failed, "agent-not-found") };

        var decision = StartupPresentation.Decide(View(f, 60), Fresh(), At(60));

        Assert.True(decision.ContentVisible);
        Assert.Equal(At(60), decision.Next.ContentShownAt);
        Assert.False(decision.Reveal);
    }

    [Fact]
    public void A_failure_that_clears_still_respects_the_dwell_for_the_panel_that_was_shown()
    {
        var shown = new StartupPresentation.State(T0, ContentShownAt: At(60));
        var f = AllContent().Reach(StartupMilestone.ShellLaidOut, At(300));

        var decision = StartupPresentation.Decide(View(f, 300), shown, At(300));

        Assert.False(decision.Reveal);
        Assert.Equal(At(660), decision.WakeAt);
    }

    [Fact]
    public void The_whole_layer_yields_to_the_data_page_and_comes_back_without_waiting_for_the_delay_again()
    {
        var shown = new StartupPresentation.State(T0, ContentShownAt: At(250));
        var needsUser = UpToConnected() with { Data = new StartupData(StartupDataPhase.NeedsUser) };

        var yielded = StartupPresentation.Decide(View(needsUser, 3000), shown, At(3000));
        Assert.False(yielded.IsOpen);
        Assert.False(yielded.ContentVisible);

        var back = StartupPresentation.Decide(View(needsUser with { Data = new StartupData(StartupDataPhase.TakingOver, "backing-up") }, 3500), yielded.Next, At(3500));
        Assert.True(back.IsOpen);
        Assert.True(back.ContentVisible);
    }

    [Fact]
    public void A_done_view_closes_the_layer()
    {
        var f = AllContent().Reach(StartupMilestone.ShellLaidOut, At(900)).Reach(StartupMilestone.Interactive, At(1100));

        var decision = StartupPresentation.Decide(View(f, 1200), new StartupPresentation.State(T0, At(250), At(900)), At(1200));

        // 界面层在淡出结束后才写 Interactive;写下之后覆盖层不在了(不论表现层状态是什么)
        Assert.False(decision.IsOpen);
        Assert.False(decision.Reveal);
        Assert.False(StartupPresentation.Decide(View(f, 1200), Fresh(), At(1200)).IsOpen);
    }

    [Fact]
    public void The_wake_time_includes_the_slow_threshold_so_the_hint_appears_without_other_events()
    {
        var shown = new StartupPresentation.State(T0, ContentShownAt: At(250));

        var decision = StartupPresentation.Decide(View(UpToAgent(), 1000), shown, At(1000));

        Assert.Equal(T0 + TimeSpan.FromSeconds(8), decision.WakeAt);
    }
}
