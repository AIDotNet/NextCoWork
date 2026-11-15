using Ncw.Agent.Extensions.Planning.Goals;
using Ncw.Agent.Llm;
using Ncw.Core.Domain;

namespace Ncw.Agent.Planning.Tests;

/// <summary>目标层的纯函数:判定解析(<c>goal.test.ts</c> 的 <c>parseGoalVerdict</c>)、重载恢复(<c>goal-restore.test.ts</c>)、文案常量、标记写入。</summary>
public sealed class GoalPureTests
{
    // ───────────────────────── parseGoalVerdict ─────────────────────────

    [Fact]
    public void Ok_true_is_met()
    {
        Assert.Equal(new GoalVerdict.Met("tests pass"), GoalVerdicts.Parse("""{"ok":true,"reason":"tests pass"}"""));
    }

    [Fact]
    public void Ok_false_is_not_met()
    {
        Assert.Equal(new GoalVerdict.NotMet("still red"), GoalVerdicts.Parse("""{"ok":false,"reason":"still red"}"""));
    }

    [Fact]
    public void Ok_false_with_impossible_is_impossible()
    {
        Assert.Equal(new GoalVerdict.Impossible("no such file"), GoalVerdicts.Parse("""{"ok":false,"impossible":true,"reason":"no such file"}"""));
    }

    [Fact]
    public void A_json_fence_is_stripped()
    {
        Assert.IsType<GoalVerdict.Met>(GoalVerdicts.Parse("```json\n{\"ok\":true,\"reason\":\"done\"}\n```"));
    }

    [Fact]
    public void Surrounding_prose_is_ignored_first_brace_to_last_brace()
    {
        Assert.IsType<GoalVerdict.NotMet>(GoalVerdicts.Parse("""Sure! {"ok":false,"reason":"nope"} hope that helps"""));
    }

    [Fact]
    public void A_missing_reason_is_an_empty_string_not_a_parse_failure()
    {
        Assert.Equal(new GoalVerdict.Met(""), GoalVerdicts.Parse("""{"ok":true}"""));
    }

    [Theory]
    [InlineData("I think so?")]
    [InlineData("")]
    [InlineData("""{"ok":"yes"}""")]
    [InlineData("{}")]
    [InlineData("""{"ok":true,"reason":3}""")]
    [InlineData("""{"ok":false,"impossible":"maybe"}""")]
    public void Garbage_is_skipped_not_not_met_a_tool_fault_is_not_the_users_goal_fault(string text)
    {
        Assert.Equal(new GoalVerdict.Skipped(GoalSkipReason.Error), GoalVerdicts.Parse(text));
    }

    [Fact]
    public void A_json_array_is_not_understood_the_contract_is_an_object()
    {
        Assert.Equal(new GoalVerdict.Skipped(GoalSkipReason.Error), GoalVerdicts.Parse("[true]"));
        Assert.Equal(new GoalVerdict.Skipped(GoalSkipReason.Error), GoalVerdicts.Parse("""[{"ok":true}]"""));
    }

    [Fact]
    public void The_reason_is_trimmed()
    {
        Assert.Equal(new GoalVerdict.NotMet("x"), GoalVerdicts.Parse("""{"ok":false,"reason":"  x  "}"""));
    }

    // ───────────────────────── restorableGoalCondition ─────────────────────────

    private static GoalStatusPart Status(string condition = "让 bun test 全绿", bool met = false, bool? failed = null, bool? cleared = null) =>
        new(met, condition, "还红着", failed, cleared);

    private static Message WithStatus(params GoalStatusPart[] parts) => Msg.Assistant("m1", [new TextPart("好的"), .. parts]);

    [Fact]
    public void No_marker_at_all_means_a_goal_was_never_set()
    {
        Assert.Null(GoalRestore.RestorableCondition([Msg.User("u", "你好")]));
    }

    [Fact]
    public void A_last_marker_that_is_not_met_means_the_goal_was_alive_so_restore_it()
    {
        Assert.Equal("让 bun test 全绿", GoalRestore.RestorableCondition([WithStatus(Status())]));
    }

    [Fact]
    public void A_met_goal_is_over_and_not_restored()
    {
        Assert.Null(GoalRestore.RestorableCondition([WithStatus(Status(met: true))]));
    }

    [Fact]
    public void An_impossible_goal_is_just_as_terminal()
    {
        Assert.Null(GoalRestore.RestorableCondition([WithStatus(Status(failed: true))]));
    }

    [Fact]
    public void A_goal_the_user_cleared_by_hand_is_not_restored_they_cleared_it_because_they_did_not_want_it_running()
    {
        Assert.Null(GoalRestore.RestorableCondition([WithStatus(Status(cleared: true))]));
    }

    [Fact]
    public void Only_the_last_marker_counts_three_goals_in_a_row_restore_the_third()
    {
        var history = new[]
        {
            WithStatus(Status("第一个", met: true)),
            WithStatus(Status("第二个", cleared: true)),
            WithStatus(Status("第三个")),
        };

        Assert.Equal("第三个", GoalRestore.RestorableCondition(history));
    }

    [Fact]
    public void When_the_last_marker_is_terminal_the_live_one_before_it_is_not_dug_up()
    {
        Assert.Null(GoalRestore.RestorableCondition([WithStatus(Status("旧的")), WithStatus(Status("旧的", met: true))]));
    }

    [Fact]
    public void Two_markers_in_one_message_the_later_one_wins()
    {
        var message = WithStatus(Status("先"), Status("后", met: true));

        Assert.Equal("后", GoalRestore.LastStatus([message])?.Condition);
        Assert.Null(GoalRestore.RestorableCondition([message]));
    }

    [Fact]
    public void An_empty_or_invisible_condition_is_not_restored_an_empty_condition_is_judged_unmet_forever()
    {
        Assert.Null(GoalRestore.RestorableCondition([WithStatus(Status(""))]));
        Assert.Null(GoalRestore.RestorableCondition([WithStatus(Status("​"))]));
    }

    [Fact]
    public void A_user_message_with_a_marker_is_ignored_markers_live_on_assistant_messages()
    {
        var odd = Msg.User("u", "x") with { Parts = [Status("伪造")] };

        Assert.Null(GoalRestore.RestorableCondition([odd]));
    }

    // ───────────────────────── 文案常量 ─────────────────────────

    [Fact]
    public void The_continuation_message_carries_the_anchor_prefix_the_condition_and_the_reason()
    {
        Assert.Equal("Stop hook feedback:\n[c]: r", GoalPrompts.ContinuationMessage("c", "r"));
    }

    [Fact]
    public void The_evaluator_question_ends_with_the_condition_and_asks_for_transcript_evidence_only()
    {
        var q = GoalPrompts.EvaluatorQuestion("bun test exits 0");

        Assert.Contains("Answer based on transcript evidence only.", q);
        Assert.EndsWith("Condition: bun test exits 0", q);
    }

    [Fact]
    public void The_evaluator_system_prompt_is_verbatim_including_the_insufficient_evidence_clause_and_the_impossible_caution()
    {
        // ★ 逐字来自 Claude Code 2.1.259。改动它等于改判定器的行为
        Assert.StartsWith("You are evaluating a stop-condition hook in Claude Code.", GoalPrompts.EvaluatorSystem);
        Assert.Contains("insufficient evidence in transcript", GoalPrompts.EvaluatorSystem);
        Assert.Contains("the assistant claiming the goal is impossible is evidence, not proof", GoalPrompts.EvaluatorSystem);
        Assert.Contains("When in doubt, return {\"ok\": false} without \"impossible\".", GoalPrompts.EvaluatorSystem);
    }

    [Fact]
    public void The_truncation_prefix_states_the_omitted_count_and_tells_the_judge_what_to_do_when_the_evidence_may_be_missing()
    {
        var prefix = GoalPrompts.TruncatedTranscriptPrefix(7);

        Assert.Contains("7 earlier messages omitted", prefix);
        Assert.Contains("if the required evidence may be in the omitted prefix", prefix);
    }

    [Fact]
    public void The_kickoff_says_to_start_working_and_not_to_tell_the_user_to_clear_after_success()
    {
        var kickoff = GoalPrompts.KickoffMessage("c");

        Assert.Contains("condition: \"c\"", kickoff);
        Assert.Contains("do not pause to ask the user what to do", kickoff);
        Assert.Contains("do not tell the user to run /goal clear after success", kickoff);
    }

    [Fact]
    public void A_check_in_with_background_work_lists_the_tasks_and_a_final_one_adds_the_paused_tail()
    {
        var tasks = new[] { new BackgroundTaskLine("t1", "Task", "后台子代理") };

        var middle = GoalPrompts.CheckinMessage("c", 30, tasks, final: false);
        var last = GoalPrompts.CheckinMessage("c", 30, tasks, final: true);

        Assert.Contains("«c» is still active, and evaluation has been deferred for 30 min because background work is still running:", middle);
        Assert.Contains("- t1 · Task · 后台子代理", middle);
        Assert.DoesNotContain("idle check-ins paused", middle);
        Assert.Contains("idle check-ins paused until your next message", last);
        Assert.Contains("won't wake this session for another check-in", last);
    }

    [Fact]
    public void A_check_in_without_background_work_says_the_work_is_no_longer_running_and_to_continue()
    {
        var text = GoalPrompts.CheckinMessage("c", 0.2, [], final: false);

        Assert.Contains("deferred for 1 min", text); // 至少 1 分钟
        Assert.Contains("no longer running", text);
        Assert.Contains("Continue toward the goal.", text);
    }

    // ───────────────────────── 标记写入 ─────────────────────────

    [Fact]
    public void Appending_a_marker_keeps_the_existing_parts_byte_for_byte_including_fields_this_version_does_not_know()
    {
        var raw = """[{"type":"text","text":"好","futureField":{"x":1}}]""";

        var json = GoalMarkers.AppendParts(raw, [new GoalStatusPart(false, "c", Set: true, Id: "g1")]);

        Assert.StartsWith(raw[..^1] + ",", json);
        Assert.Contains("\"type\":\"goal_status\"", json);
        Assert.EndsWith("]", json);
        Assert.Equal(2, System.Text.Json.JsonDocument.Parse(json).RootElement.GetArrayLength());
    }

    [Fact]
    public void Appending_to_an_empty_parts_array_works()
    {
        var json = GoalMarkers.AppendParts("[]", [new GoalStatusPart(false, "c")]);

        Assert.Equal(1, System.Text.Json.JsonDocument.Parse(json).RootElement.GetArrayLength());
    }

    [Fact]
    public void The_codec_round_trips_every_goal_status_field_the_ui_needs()
    {
        var part = new GoalStatusPart(true, "c", "ok", Failed: null, Cleared: null, Set: true, Origin: "user", Iterations: 3, DurationMs: 1500, Tokens: 42, Id: "g1", CreatedAt: 1700000000000);

        var back = (GoalStatusPart)Ncw.Agent.Kernel.Session.MessageCodec.PartsFromJson(Ncw.Agent.Kernel.Session.MessageCodec.PartsToJson([part]))[0];

        Assert.Equal(part, back);
    }

    [Fact]
    public void The_codec_still_reads_a_2x_marker_that_has_only_the_old_fields()
    {
        var back = (GoalStatusPart)Ncw.Agent.Kernel.Session.MessageCodec.PartsFromJson("""[{"type":"goal_status","met":false,"condition":"c","cleared":true,"iterations":2,"durationMs":9,"tokens":7}]""")[0];

        Assert.True(back.Cleared);
        Assert.Equal(2, back.Iterations);
        Assert.Equal(9, back.DurationMs);
        Assert.Null(back.Set);
        Assert.Null(back.Id);
    }

    [Fact]
    public void A_marker_is_stamped_with_an_id_and_creation_time_when_it_has_none()
    {
        var rig = new MemoryGoalTranscript();
        rig.Seed("s", Msg.User("u", "hi"), Msg.Say("a", "好"));
        var markers = new GoalMarkers(rig, new SeqIds("m"), new ManualClock());

        var message = markers.Record("s", new GoalStatusPart(false, "c", Set: true));

        var stored = Assert.Single(rig.Statuses("s"));
        Assert.Equal("m0001", stored.Id);
        Assert.NotNull(stored.CreatedAt);
        Assert.Equal(2, message!.Parts.Count);
    }

    [Fact]
    public void A_stale_replace_is_retried_against_a_fresh_read()
    {
        var rig = new MemoryGoalTranscript { StaleOnce = 2 };
        rig.Seed("s", Msg.Say("a", "好"));
        var markers = new GoalMarkers(rig, new SeqIds("m"), new ManualClock());

        Assert.NotNull(markers.Record("s", new GoalStatusPart(false, "c")));
        Assert.Single(rig.Statuses("s"));
    }

    [Fact]
    public void A_marker_that_cannot_be_attached_is_queued_and_attached_exactly_once_when_an_assistant_message_exists()
    {
        var rig = new MemoryGoalTranscript();
        rig.Seed("s", Msg.User("u", "hi")); // 一条助手消息都没有
        var markers = new GoalMarkers(rig, new SeqIds("m"), new ManualClock());

        Assert.Null(markers.Record("s", new GoalStatusPart(false, "c", Set: true)));
        Assert.Equal(1, markers.PendingCount("s"));
        Assert.Empty(rig.Statuses("s"));

        rig.Seed("s", Msg.User("u", "hi"), Msg.Say("a1", "我开始了"));
        Assert.NotNull(markers.FlushPending("s"));
        Assert.Single(rig.Statuses("s"));
        // ★ 队列只放一次:下一条助手消息不会再被补一遍(同一条标记补两遍 = 界面上两条「已设定」)
        Assert.Null(markers.FlushPending("s"));
        Assert.Equal(0, markers.PendingCount("s"));
        Assert.Single(rig.Statuses("s"));
    }

    [Fact]
    public void Flushing_with_still_no_assistant_message_keeps_the_marker_queued_in_order()
    {
        var rig = new MemoryGoalTranscript();
        rig.Seed("s", Msg.User("u", "hi"));
        var markers = new GoalMarkers(rig, new SeqIds("m"), new ManualClock());
        markers.Record("s", new GoalStatusPart(false, "c1", Set: true));
        markers.Record("s", new GoalStatusPart(false, "c2", Set: true));

        Assert.Null(markers.FlushPending("s"));
        Assert.Equal(2, markers.PendingCount("s"));
        rig.Seed("s", Msg.Say("a1", "好"));
        markers.FlushPending("s");
        Assert.Equal(["c1", "c2"], rig.Statuses("s").Select(s => s.Condition));
    }
}
