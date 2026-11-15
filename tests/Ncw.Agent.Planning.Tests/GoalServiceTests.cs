using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Planning.Goals;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Llm;
using Ncw.Core.Domain;

namespace Ncw.Agent.Planning.Tests;

/// <summary>
/// 目标运行时(<see cref="GoalService"/>)。2.x <c>goal-runtime.test.ts</c> 全部用例逐条移植(转录和判定注入成假的,时间走手动时钟),加上 <c>ipc/goal.ts</c> 的设定 / 清除 / 恢复。
/// 「目标是一条 Stop 钩子」在 3.0 里是 <c>turn_end</c> 上的处理器,判定器直接是旁路模型请求(没有 Hooks 扩展:其它 Stop 钩子和目标共存的用例随 Hooks 移植)。
/// </summary>
public sealed class GoalServiceTests : IDisposable
{
    private const string S = GoalRig.S;
    private const string Condition = GoalRig.Condition;
    private static readonly TimeSpan HalfHour = TimeSpan.FromMinutes(30);

    private readonly GoalRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private GoalService Goals => _rig.Goals;

    private void Seed() => _rig.Transcript.Seed(S, Msg.User("u1", "开始"), Msg.Say("a1", "好"));

    private ActiveGoal Activate(string condition = Condition, GoalOrigin origin = GoalOrigin.User)
    {
        Goals.Activate(S, condition, origin);
        return Goals.Store.Get(S)!;
    }

    private static GoalVerdict.NotMet Unmet(string reason = "还有两个红的") => new(reason);

    // ───────────────────────── 目标标记:落在最后一条助手消息上 ─────────────────────────

    [Fact]
    public void Every_set_gets_a_new_identity_even_with_the_same_condition()
    {
        var kickoff = Goals.Activate(S, $"  {Condition}  ", GoalOrigin.User);
        var first = Goals.Store.Get(S)!;

        Assert.Equal(Condition, first.Condition);
        Assert.Equal(GoalOrigin.User, first.Origin);
        Assert.Equal(0, first.Iterations);
        Assert.Equal(GoalRig.TextOf(kickoff!), GoalPrompts.KickoffMessage(Condition));

        Goals.Activate(S, Condition, GoalOrigin.User);
        var second = Goals.Store.Get(S)!;
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(0, second.Iterations);
    }

    [Fact]
    public void Set_and_clear_markers_are_appended_to_the_last_assistant_message_without_adding_a_message()
    {
        Seed();
        var ids = _rig.Transcript.Messages(S).Select(m => m.Id).ToList();

        Goals.Activate(S, Condition, GoalOrigin.User);
        var set = Assert.Single(_rig.Transcript.Statuses(S));
        Assert.True(set.Set);
        Assert.False(set.Met);
        Assert.Equal(Condition, set.Condition);
        Assert.Equal("user", set.Origin);
        Assert.Equal(0, set.Iterations);
        Assert.Equal(ids, _rig.Transcript.Messages(S).Select(m => m.Id).ToList());
        // 正文一个字节没动
        Assert.Equal(new TextPart("好"), _rig.Transcript.Messages(S)[1].Parts[0]);

        var cleared = Goals.Deactivate(S, GoalClearReason.UserClear);
        Assert.True(cleared!.Cleared);
        Assert.False(cleared.Met);
        Assert.Equal(Condition, cleared.Condition);
        Assert.Equal(2, _rig.Transcript.Statuses(S).Count);
        Assert.Contains(_rig.Transcript.Statuses(S), p => p.Cleared == true);
        Assert.Equal(ids, _rig.Transcript.Messages(S).Select(m => m.Id).ToList());
        Assert.Equal(new TextPart("好"), _rig.Transcript.Messages(S)[1].Parts[0]);
        Assert.Null(Goals.Store.Get(S));
    }

    [Fact]
    public void With_no_assistant_message_yet_the_marker_waits_and_lands_once_on_the_next_one()
    {
        _rig.Transcript.Seed(S, Msg.User("u1", "开始"));
        Goals.Activate(S, Condition, GoalOrigin.User);
        Assert.Empty(_rig.Transcript.Statuses(S)); // 这一刻没有任何东西可以挂

        _rig.Transcript.Seed(S, Msg.User("u1", "开始"), Msg.Say("a1", "我开始了"));
        Goals.FlushMarkers(S);

        var marker = Assert.Single(_rig.Transcript.Statuses(S));
        Assert.True(marker.Set);
        Goals.FlushMarkers(S); // ★ 只放一次:再 flush 不会再补一遍(同一条标记补两遍 = 界面上两条「已设定」)
        Assert.Single(_rig.Transcript.Statuses(S));
    }

    [Fact]
    public void Shutdown_is_not_a_user_clear_it_leaves_the_durable_active_marker_for_the_next_launch()
    {
        Seed();
        Goals.Activate(S, Condition, GoalOrigin.User);

        Goals.EndSession(S);

        Assert.Null(Goals.Store.Get(S));
        var only = Assert.Single(_rig.Transcript.Statuses(S));
        Assert.True(only.Set); // 没有「已清除」标记:下次打开会话,目标自己回来
        Assert.Equal(Condition, GoalRestore.RestorableCondition(_rig.Transcript.Messages(S)));
    }

    // ───────────────────────── restoreGoal ─────────────────────────

    [Fact]
    public void Restore_is_idempotent_restored_origin_zero_iterations_no_kickoff_no_timer()
    {
        _rig.Transcript.Seed(S, Msg.User("u1", "开始"), Msg.Assistant("a1", new TextPart("好"), new GoalStatusPart(false, Condition, "还红着")));

        var goal = Goals.Restore(S);

        Assert.Equal(Condition, goal!.Condition);
        Assert.Equal(GoalOrigin.Restored, goal.Origin);
        Assert.Equal(0, goal.Iterations);
        Assert.Empty(_rig.Delivered);
        Assert.Single(_rig.Transcript.Statuses(S)); // 没有写任何标记
        Assert.Equal(0, Goals.TimerCount);
        Assert.Equal(goal.Id, Goals.Restore(S)!.Id);
        Assert.Single(_rig.Transcript.Statuses(S));
        Assert.Equal(0, Goals.TimerCount);
    }

    [Theory]
    [InlineData("cleared")]
    [InlineData("met")]
    [InlineData("impossible")]
    public void A_last_marker_that_is_terminal_is_not_restored(string terminal)
    {
        var part = new GoalStatusPart(terminal == "met", Condition, "x", Failed: terminal == "impossible" ? true : null, Cleared: terminal == "cleared" ? true : null);
        _rig.Transcript.Seed(S, Msg.Assistant("a1", new TextPart("好"), part));

        Assert.Null(Goals.Restore(S));
        Assert.Null(Goals.Store.Get(S));
        Assert.Single(_rig.Transcript.Statuses(S));
    }

    [Fact]
    public void Restore_reads_the_transcript_only_the_first_time_and_only_the_last_marked_message()
    {
        // ★ 打开长会话时不再把整段转录解析一遍
        _rig.Transcript.Seed(S, Msg.Assistant("a9", new TextPart("好"), new GoalStatusPart(false, Condition, "x")));

        Goals.Restore(S);
        Goals.Restore(S);
        Goals.Get(S);

        Assert.Equal(1, _rig.Transcript.LastWithGoalStatusCalls);
    }

    [Fact]
    public void A_goal_the_user_cleared_does_not_come_back_after_a_restart()
    {
        Seed();
        Goals.Activate(S, Condition, GoalOrigin.User);
        Goals.Clear(S);

        using var next = new GoalService(_rig.Options); // 「重启」:内存状态丢掉,转录还在

        Assert.Null(next.Restore(S));
    }

    // ───────────────────────── 用户设定 / 清除(goal.set / goal.clear) ─────────────────────────

    [Fact]
    public void A_user_set_delivers_the_kickoff_as_an_internal_input_tied_to_the_new_goal()
    {
        Seed();

        var result = Assert.IsType<GoalSetResult.Ok>(Goals.SetByUser(S, $" {Condition} "));

        Assert.Equal(Condition, result.Goal.Condition);
        Assert.Equal(InternalDelivery.Launched, result.Delivery);
        var (session, input) = Assert.Single(_rig.Delivered);
        Assert.Equal(S, session);
        Assert.Equal(result.Goal.Id, input.GoalId);
        Assert.Equal(GoalPrompts.KickoffMessage(Condition), GoalRig.TextOf(input.Parts));
    }

    [Fact]
    public void A_user_set_validates_empty_and_too_long_and_changes_nothing()
    {
        Assert.Equal(new GoalSetResult.Invalid(GoalInvalidReason.Empty, 0), Goals.SetByUser(S, "​ ﻿"));
        Assert.Equal(new GoalSetResult.Invalid(GoalInvalidReason.TooLong, GoalConditions.Max + 1), Goals.SetByUser(S, new string('x', GoalConditions.Max + 1)));
        Assert.Null(Goals.Store.Get(S));
        Assert.Empty(_rig.Delivered);
    }

    [Fact]
    public void A_user_clear_restores_first_cancels_proposals_and_stamps_the_cleared_marker()
    {
        _rig.Transcript.Seed(S, Msg.User("u1", "开始"), Msg.Assistant("a1", new TextPart("好"), new GoalStatusPart(false, Condition, "x")));

        Goals.Clear(S); // 内存里恰好没有,但转录里有:清除的是转录里那个

        Assert.Null(Goals.Store.Get(S));
        Assert.Contains(_rig.Transcript.Statuses(S), p => p.Cleared == true);
    }

    [Fact]
    public void Clearing_when_there_is_no_goal_is_a_no_op()
    {
        Seed();

        Goals.Clear(S);

        Assert.Empty(_rig.Transcript.Statuses(S));
    }

    [Fact]
    public void Changes_are_broadcast_in_full_the_set_the_iteration_and_the_clear()
    {
        Seed();
        var changes = new List<GoalChange>();
        Goals.Changed += changes.Add;

        Goals.Activate(S, Condition, GoalOrigin.User);
        Goals.Deactivate(S, GoalClearReason.UserClear);

        Assert.Contains(changes, c => c.Goal is { Condition: Condition });
        Assert.Contains(changes, c => c.Goal is null && c.Cleared == GoalClearReason.UserClear);
        Assert.Contains(changes, c => c.Message is not null); // 带外标记随一次变更带着更新后的助手消息
    }

    [Fact]
    public void A_throwing_subscriber_does_not_stop_the_goal_from_being_set()
    {
        Goals.Changed += _ => throw new InvalidOperationException("boom");

        Goals.Activate(S, Condition, GoalOrigin.User);

        Assert.NotNull(Goals.Store.Get(S));
        Assert.True(_rig.Log.Any("subscriber threw"));
    }

    [Fact]
    public void Tokens_count_from_the_moment_the_goal_was_set()
    {
        _rig.Tokens = 1000;
        Seed();
        Activate();
        _rig.Tokens = 1700;

        var cleared = Goals.Deactivate(S, GoalClearReason.UserClear);

        Assert.Equal(700, cleared!.Tokens);
    }

    // ───────────────────────── handleTurnEnd:判定接线 ─────────────────────────

    [Fact]
    public async Task A_subagent_run_is_skipped_no_evaluation_and_the_goal_does_not_move()
    {
        Activate();
        _rig.Llm.Judge(Up.Verdict(true, "x"));

        var decision = await _rig.TurnEndAsync(subagent: true);

        Assert.IsType<GoalTurnDecision.None>(decision);
        Assert.Empty(_rig.Llm.JudgeRequests);
        Assert.Equal(0, Goals.Store.Get(S)!.Iterations);
    }

    [Fact]
    public async Task Not_met_adds_a_round_remembers_the_reason_and_injects_the_continuation_prompt()
    {
        Seed();
        Activate();
        var statusesBefore = _rig.Transcript.Statuses(S).Count;
        _rig.Llm.Judge(Up.Verdict(false, "还有两个红的"));

        var cont = Assert.IsType<GoalTurnDecision.Continue>(await _rig.TurnEndAsync());

        Assert.Equal(GoalPrompts.ContinuationMessage(Condition, "还有两个红的"), cont.Inject);
        Assert.Contains(GoalPrompts.StopHookFeedbackPrefix, cont.Inject);
        Assert.Equal(1, cont.Status!.Iterations);
        Assert.False(cont.Status.Met);
        Assert.Equal("还有两个红的", cont.Status.Reason);
        Assert.Equal(Condition, cont.Status.Condition);
        var goal = Goals.Store.Get(S)!;
        Assert.Equal(1, goal.Iterations);
        Assert.Equal("还有两个红的", goal.LastReason);
        // 标记由扩展那一处附加,运行时自己不写——写两遍就是两条
        Assert.Equal(statusesBefore, _rig.Transcript.Statuses(S).Count);
    }

    [Fact]
    public async Task An_empty_reason_is_replaced_by_insufficient_evidence_in_the_continuation()
    {
        Activate();
        _rig.Llm.Judge(Up.Raw("""{"ok":false}"""));

        var cont = Assert.IsType<GoalTurnDecision.Continue>(await _rig.TurnEndAsync());

        Assert.Equal(GoalPrompts.ContinuationMessage(Condition, "insufficient evidence in transcript"), cont.Inject);
    }

    [Fact]
    public async Task Met_finishes_and_clears_the_goal_and_a_full_idle_streak_cannot_stop_it()
    {
        Activate();
        var signal = Goals.Store.Signal(S)!.Value;
        _rig.Llm.Judge(Up.Verdict(true, "bun test 全绿了"));

        var finish = Assert.IsType<GoalTurnDecision.Finish>(await _rig.TurnEndAsync(streak: GoalConditions.IdleStreakCap));

        Assert.Equal("goal met", finish.Note);
        Assert.True(finish.Status!.Met);
        Assert.Equal("bun test 全绿了", finish.Status.Reason);
        Assert.Equal(1, finish.Status.Iterations);
        Assert.Null(finish.IdleStreak); // 空转刹车拦不住
        Assert.Null(Goals.Store.Get(S));
        // 还没回来的那次判定跟着目标一起取消
        Assert.True(signal.IsCancellationRequested);
    }

    [Fact]
    public async Task Impossible_also_finishes_but_the_marker_is_failed()
    {
        Activate();
        _rig.Llm.Judge(Up.Verdict(false, "那个包不存在", impossible: true));

        var finish = Assert.IsType<GoalTurnDecision.Finish>(await _rig.TurnEndAsync());

        Assert.Equal("goal impossible", finish.Note);
        Assert.False(finish.Status!.Met);
        Assert.True(finish.Status.Failed);
        Assert.Equal(1, finish.Status.Iterations);
        Assert.Null(Goals.Store.Get(S));
    }

    [Theory]
    [InlineData(AgentErrorCode.Network)]
    [InlineData(AgentErrorCode.Provider)]
    public async Task A_skipped_judgement_counts_for_no_round_and_does_not_clear_the_goal(AgentErrorCode failure)
    {
        Activate();
        _rig.Llm.Judge(Up.Fail(failure));

        Assert.IsType<GoalTurnDecision.None>(await _rig.TurnEndAsync());

        var goal = Goals.Store.Get(S)!;
        Assert.Equal(0, goal.Iterations);
        Assert.Null(goal.LastReason);
    }

    [Fact]
    public async Task Hitting_the_idle_streak_cap_ends_the_run_with_a_warning_but_keeps_the_goal()
    {
        // ★ 空转刹车:连续 8 个回合没有调用工具 → 强制结束并警告。轮数不是上限:停下来的是「反复空转」,不是「跑了很久」
        Activate();
        _rig.Llm.Judge(Up.Verdict(false, "还红着"));

        var finish = Assert.IsType<GoalTurnDecision.Finish>(await _rig.TurnEndAsync(streak: GoalConditions.IdleStreakCap));

        Assert.Equal(GoalConditions.IdleStreakCap, finish.IdleStreak);
        Assert.Equal(1, finish.Status!.Iterations);
        Assert.False(finish.Status.Met);
        Assert.Equal(1, Goals.Store.Get(S)!.Iterations); // 目标留着
        Assert.Contains(_rig.Telemetry, t => t.StartsWith("goal_idle_streak_stopped"));
    }

    [Fact]
    public async Task One_turn_below_the_cap_still_continues()
    {
        Activate();
        _rig.Llm.Judge(Up.Verdict(false, "还红着"));

        Assert.IsType<GoalTurnDecision.Continue>(await _rig.TurnEndAsync(streak: GoalConditions.IdleStreakCap - 1));
    }

    [Fact]
    public async Task A_goal_replaced_during_the_evaluation_voids_that_verdict_even_with_the_same_condition()
    {
        Activate();
        var stale = Goals.Store.Signal(S)!.Value;
        _rig.Llm.Judge(Up.Before(() => Goals.Activate(S, Condition, GoalOrigin.User), Up.Verdict(true, "旧目标达成")));

        var decision = await _rig.TurnEndAsync();

        Assert.IsType<GoalTurnDecision.None>(decision);
        var current = Goals.Store.Get(S)!;
        Assert.Equal(Condition, current.Condition);
        Assert.Equal(0, current.Iterations);
        Assert.Null(current.LastReason);
        // 手里那一次判定必须被取消:它判的是已经不存在的那个目标
        Assert.True(stale.IsCancellationRequested);
    }

    [Fact]
    public async Task A_goal_cleared_by_the_user_during_the_evaluation_voids_the_verdict_too()
    {
        Seed();
        Activate();
        _rig.Llm.Judge(Up.Before(() => Goals.Deactivate(S, GoalClearReason.UserClear), Up.Verdict(true, "清掉之前那一刻")));

        Assert.IsType<GoalTurnDecision.None>(await _rig.TurnEndAsync());
        Assert.Null(Goals.Store.Get(S));
    }

    [Fact]
    public async Task An_abort_during_the_evaluation_drops_the_verdict_and_leaves_the_goal_untouched()
    {
        Activate();
        using var cts = new CancellationTokenSource();
        _rig.Llm.Judge(Up.Before(cts.Cancel, Up.Verdict(false, "还红着")));

        Assert.IsType<GoalTurnDecision.None>(await _rig.TurnEndAsync(ct: cts.Token));
        Assert.Equal(0, Goals.Store.Get(S)!.Iterations);
    }

    [Fact]
    public async Task With_no_goal_there_is_no_opinion_and_no_request()
    {
        Assert.IsType<GoalTurnDecision.None>(await _rig.TurnEndAsync());
        Assert.Empty(_rig.Llm.JudgeRequests);
    }

    [Fact]
    public async Task A_pause_during_the_evaluation_rotates_the_identity_and_voids_the_verdict()
    {
        // 手动中止 / 出错:暂停唤醒但保留条件。轮换目标 id,还没回来的判定拿着的是旧 id
        Activate();
        var before = Goals.Store.Get(S)!.Id;
        _rig.Llm.Judge(Up.Before(() => Goals.Pause(S), Up.Verdict(true, "x")));

        Assert.IsType<GoalTurnDecision.None>(await _rig.TurnEndAsync());

        var goal = Goals.Store.Get(S)!;
        Assert.NotEqual(before, goal.Id);
        Assert.Equal(Condition, goal.Condition);
        Assert.Equal(0, goal.Iterations);
    }

    [Fact]
    public async Task The_evaluator_model_is_the_frozen_pair_from_the_run_context_not_whatever_the_setting_is_now()
    {
        Activate();
        _rig.EvaluatorModel = new ModelRef("p-now", "judge-now");
        var context = _rig.Context() with { EvaluatorModel = new ModelRef("p-frozen", "judge-frozen") };
        _rig.Llm.Judge(Up.Verdict(false, "x"));

        await _rig.TurnEndAsync(context: context);

        Assert.Equal(new ModelRef("p-frozen", "judge-frozen"), _rig.Llm.JudgeModels.Single());
    }

    [Fact]
    public async Task Without_a_judge_model_the_run_model_is_used()
    {
        Activate();
        _rig.Llm.Judge(Up.Verdict(false, "x"));

        await _rig.TurnEndAsync();

        Assert.Equal(ModelRef.Unpinned("main-model"), _rig.Llm.JudgeModels.Single());
    }

    [Fact]
    public async Task The_judge_reads_the_turn_end_transcript_and_the_goal_condition()
    {
        Activate();
        _rig.Llm.Judge(Up.Verdict(false, "x"));

        await _rig.TurnEndAsync(transcript: [Msg.User("u", "跑测试"), Msg.Say("a", "12 pass, 0 fail")]);

        var prompt = ((TextPart)_rig.Llm.JudgeRequests.Single().Messages[0].Parts[0]).Text;
        Assert.Contains("12 pass, 0 fail", prompt);
        Assert.EndsWith($"Condition: {Condition}", prompt);
    }

    [Fact]
    public async Task A_judge_that_throws_is_a_skipped_judgement_not_a_failed_run()
    {
        Activate();
        _rig.Llm.Judge((_, _) => throw new InvalidOperationException("upstream exploded"));

        Assert.IsType<GoalTurnDecision.None>(await _rig.TurnEndAsync());
        Assert.Equal(0, Goals.Store.Get(S)!.Iterations);
    }

    [Fact]
    public async Task Background_work_defers_the_evaluation_no_round_counted_and_the_judge_is_never_called()
    {
        Activate();
        _rig.Background.Tasks = [new BackgroundTaskLine("t1", "Task", "后台子代理")];

        var finish = Assert.IsType<GoalTurnDecision.Finish>(await _rig.TurnEndAsync());

        Assert.Equal("goal deferred", finish.Note);
        Assert.True(finish.AcceptPendingInput);
        Assert.Empty(_rig.Llm.JudgeRequests); // 推迟期间判定器不该被叫醒:它读的转录里那一轮本来就还没结束
        var goal = Goals.Store.Get(S)!;
        Assert.Equal(0, goal.Iterations);
        Assert.NotNull(goal.DeferredSince);
        Assert.Equal(1, Goals.TimerCount);
    }

    // ───────────────────────── 空闲唤醒 ─────────────────────────

    private async Task<GoalRunContext> DeferGoal()
    {
        Seed();
        Activate();
        _rig.Background.Tasks = [new BackgroundTaskLine("t1", "Task", "后台子代理")];
        var context = _rig.Context();
        var decision = await _rig.TurnEndAsync(context: context);
        Assert.Equal("goal deferred", Assert.IsType<GoalTurnDecision.Finish>(decision).Note);
        return context;
    }

    [Fact]
    public async Task A_failed_delivery_backs_off_without_charging_the_three_injection_allowance()
    {
        await DeferGoal();
        _rig.DeliveryResult = InternalDelivery.Failed;

        _rig.Clock.Advance(HalfHour);
        Assert.Single(_rig.Delivered);
        _rig.Clock.Advance(GoalConditions.CheckinMinDelay);
        Assert.Single(_rig.Delivered);
        _rig.Clock.Advance(TimeSpan.FromMinutes(60) - GoalConditions.CheckinMinDelay);
        Assert.Equal(2, _rig.Delivered.Count);

        var goal = Goals.Store.Get(S)!;
        Assert.Equal(0, goal.IdleCheckinCount);
        Assert.Equal(0, goal.CheckinCount);
        Assert.Contains(_rig.Telemetry, t => t.StartsWith("goal_checkin_delivery_failed"));
    }

    [Fact]
    public async Task When_the_background_work_has_finished_the_next_turn_end_goes_back_to_evaluating()
    {
        await DeferGoal();
        _rig.Background.Tasks = [];
        _rig.Llm.Judge(Up.Verdict(false, "need final verification"));

        var decision = await _rig.TurnEndAsync();

        Assert.IsType<GoalTurnDecision.Continue>(decision);
        var goal = Goals.Store.Get(S)!;
        Assert.Equal(1, goal.Iterations);
        Assert.Equal(0, goal.CheckinCount);
        Assert.Null(goal.DeferredSince);
        Assert.Equal(0, Goals.TimerCount);
    }

    [Fact]
    public async Task Idle_injections_come_at_30_60_and_120_minutes_the_third_carries_the_paused_tail_and_then_no_more()
    {
        await DeferGoal();
        Assert.Equal(1, Goals.TimerCount);

        _rig.Clock.Advance(HalfHour);
        Assert.Single(_rig.Delivered);
        Assert.Contains(Condition, _rig.DeliveredText()[0]);
        Assert.DoesNotContain("idle check-ins paused", _rig.DeliveredText()[0]);

        _rig.Clock.Advance(TimeSpan.FromMinutes(60));
        Assert.Equal(2, _rig.Delivered.Count);
        Assert.DoesNotContain("idle check-ins paused", _rig.DeliveredText()[1]);

        _rig.Clock.Advance(TimeSpan.FromMinutes(120));
        Assert.Equal(3, _rig.Delivered.Count);
        Assert.Contains("idle check-ins paused until your next message", _rig.DeliveredText()[2]);
        var id = Goals.Store.Get(S)!.Id;
        Assert.All(_rig.Delivered, d => Assert.Equal(id, d.Input.GoalId));

        // ★ 到顶之后只是挂空定时器:再等 4 小时也不会多注入一次
        _rig.Clock.Advance(TimeSpan.FromMinutes(240));
        Assert.Equal(3, _rig.Delivered.Count);
        var goal = Goals.Store.Get(S)!;
        Assert.Equal(GoalConditions.IdleCheckinCap, goal.IdleCheckinCount);
        Assert.Equal(3, goal.CheckinCount);
    }

    [Fact]
    public async Task An_idle_check_in_starts_an_internal_run_with_the_runs_send_options()
    {
        var context = await DeferGoal();

        _rig.Clock.Advance(HalfHour);

        Assert.Equal(context.Options, Assert.Single(_rig.Delivered).Input.Options);
    }

    [Fact]
    public async Task An_idle_check_in_is_only_sent_when_the_session_is_idle()
    {
        await DeferGoal();
        _rig.Background.Idle = false; // 有 Run 占着:它自己的回合末会处理

        _rig.Clock.Advance(HalfHour);

        Assert.Empty(_rig.Delivered);
        Assert.Equal(0, Goals.Store.Get(S)!.IdleCheckinCount);
    }

    [Fact]
    public async Task Clearing_the_goal_takes_the_timer_with_it_no_alarm_for_a_dead_goal()
    {
        await DeferGoal();
        Assert.Equal(1, Goals.TimerCount);

        Goals.Deactivate(S, GoalClearReason.UserClear);

        Assert.Null(Goals.Store.Get(S));
        Assert.Equal(0, Goals.TimerCount);
        _rig.Clock.Advance(TimeSpan.FromMinutes(240));
        Assert.Empty(_rig.Delivered);
    }

    [Fact]
    public async Task Stopping_everything_leaves_no_timer()
    {
        await DeferGoal();
        Assert.Equal(1, Goals.TimerCount);

        Goals.StopAll();

        Assert.Equal(0, Goals.TimerCount);
        Assert.Equal(0, _rig.Clock.PendingTimers);
    }

    [Fact]
    public async Task When_the_session_is_gone_the_timer_ends_the_goal_itself_and_injects_nothing()
    {
        await DeferGoal();
        _rig.Transcript.Drop(S);

        _rig.Clock.Advance(HalfHour);

        Assert.Empty(_rig.Delivered);
        Assert.Null(Goals.Store.Get(S));
        Assert.Equal(0, Goals.TimerCount);
    }

    [Fact]
    public async Task A_manual_pause_keeps_the_goal_but_never_wakes_again()
    {
        await DeferGoal();

        Goals.Pause(S);

        var goal = Goals.Store.Get(S)!;
        Assert.Equal(Condition, goal.Condition);
        Assert.Equal(GoalOrigin.User, goal.Origin);
        Assert.NotNull(goal.DeferredSince);
        Assert.Equal(0, Goals.TimerCount);
        _rig.Clock.Advance(TimeSpan.FromHours(24));
        Assert.Empty(_rig.Delivered);
    }

    [Fact]
    public async Task The_turn_the_user_speaks_in_resets_the_idle_count_and_the_next_turn_end_re_arms()
    {
        var context = await DeferGoal();
        _rig.Clock.Advance(HalfHour);
        Assert.Equal(1, Goals.Store.Get(S)!.IdleCheckinCount);

        Goals.BindRun(S, context, userMessage: false);
        Assert.Equal(1, Goals.Store.Get(S)!.IdleCheckinCount);
        Goals.BindRun(S, context, userMessage: true);
        Assert.Equal(0, Goals.Store.Get(S)!.IdleCheckinCount);
        // 旧定时器已经清掉,由下一次回合末重新算
        Assert.Equal(0, Goals.TimerCount);

        await _rig.TurnEndAsync(context: context);
        Assert.Equal(1, Goals.TimerCount);
    }

    [Fact]
    public async Task A_deferred_turn_end_injects_a_check_in_once_the_checkpoint_has_passed()
    {
        var context = await DeferGoal();
        _rig.Background.Idle = false; // 会话不空闲:空闲定时器不会注入,留给 Run 自己的回合末
        _rig.Clock.Advance(TimeSpan.FromMinutes(31)); // 检查点(30 分钟)已过

        var cont = Assert.IsType<GoalTurnDecision.Continue>(await _rig.TurnEndAsync(context: context));

        Assert.Contains("Goal check-in", cont.Inject);
        Assert.Contains("- t1 · Task · 后台子代理", cont.Inject);
        Assert.Equal(1, Goals.Store.Get(S)!.CheckinCount);
    }

    [Fact]
    public async Task The_check_in_interval_is_configurable_and_has_a_one_minute_floor()
    {
        using var fast = new GoalRig(b => b.CheckinInterval = TimeSpan.FromSeconds(5));
        fast.Transcript.Seed(S, Msg.User("u1", "开始"), Msg.Say("a1", "好"));
        fast.Goals.Activate(S, Condition, GoalOrigin.User);
        fast.Background.Tasks = [new BackgroundTaskLine("t1", "Task", "x")];
        await fast.TurnEndAsync();

        fast.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(fast.Delivered);
        fast.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Single(fast.Delivered);
    }

    // ───────────────────────── Run 生命周期 ─────────────────────────

    [Fact]
    public async Task Release_after_a_normal_run_drops_the_timer_and_context_unless_deferred()
    {
        Seed();
        Activate();
        Goals.BindRun(S, _rig.Context(), userMessage: true);
        Assert.NotNull(Goals.ContextOf(S));

        Goals.Release(S);

        Assert.Null(Goals.ContextOf(S));

        // 推迟态要留着:后台还在跑,空闲唤醒要用它们
        await DeferGoal();
        Goals.Release(S);
        Assert.NotNull(Goals.ContextOf(S));
        Assert.Equal(1, Goals.TimerCount);
    }

    [Fact]
    public void Replacing_the_goal_keeps_the_runs_coordination_context()
    {
        Seed();
        Activate();
        Goals.BindRun(S, _rig.Context("run-7"), userMessage: false);

        Goals.Activate(S, "另一个条件", GoalOrigin.User);

        Assert.Equal(new RunId("run-7"), Goals.ContextOf(S)!.RunId);
    }

    [Fact]
    public void A_user_set_or_clear_advances_the_revision_but_an_automatic_completion_does_not()
    {
        Seed();
        var r0 = Goals.Store.Revision(S);
        Activate();
        var r1 = Goals.Store.Revision(S);
        Assert.True(r1 > r0);

        Goals.Deactivate(S, GoalClearReason.Met);
        Assert.Equal(r1, Goals.Store.Revision(S));
        Activate();
        Goals.Deactivate(S, GoalClearReason.UserClear);
        Assert.True(Goals.Store.Revision(S) > r1);
    }
}
