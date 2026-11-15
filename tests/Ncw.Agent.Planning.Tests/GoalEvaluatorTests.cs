using Ncw.Agent.Extensions.Planning.Goals;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Llm.Thinking;
using Ncw.Core.Domain;

namespace Ncw.Agent.Planning.Tests;

/// <summary>
/// 判定调用(<see cref="GoalEvaluator"/>)。2.x <c>goal-evaluate.test.ts</c> 全部用例逐条移植:四种结论、请求形状、只有完整回复才算数、中断与超时(手动时钟,不睡真的时间)、失败与重试、转录的回合边界与预算。
/// 三条正确性硬约束:一次判定一个墙钟、只有完整 end_turn 才算回复、中断之后绝不返回 met。
/// </summary>
public sealed class GoalEvaluatorTests
{
    private static ModelBinding Binding(string alias, int window = 200_000) => new(alias, "p1", "judge-1", window, 4096);

    private static readonly IReadOnlyList<Message> History = [Msg.User("u1", "把测试修好"), Msg.Say("a1", "bun test 全绿了")];

    private sealed class Judge : ILlmClient
    {
        private readonly List<Script> _scripts;

        public Judge(params Script[] scripts) => _scripts = [.. scripts];

        public List<LlmRequest> Requests { get; } = [];

        public List<ModelRef> Models { get; } = [];

        public IAsyncEnumerable<LlmEvent> StreamAsync(ModelRef model, LlmRequest request, RequestContext context, CancellationToken ct = default)
        {
            lock (Requests)
            {
                Requests.Add(request);
                Models.Add(model);
            }

            var script = _scripts[Math.Min(Requests.Count - 1, _scripts.Count - 1)];
            return script(request, ct);
        }

        public string PromptOf(int index) => ((TextPart)Requests[index].Messages[0].Parts[0]).Text;
    }

    private static EvaluateGoalInput Input(
        ILlmClient llm, IReadOnlyList<Message>? messages = null, ModelRef? model = null, ModelRef? fallback = null,
        int window = 200_000, Func<ModelRef, ModelBinding?>? resolve = null, TimeSpan? timeout = null, TimeProvider? clock = null) =>
        new(llm, model ?? ModelRef.Unpinned("judge"), fallback ?? ModelRef.Unpinned("main"), "达成了吗?", messages ?? History,
            new RequestContext("w1", "run-1:goal", "s1"), resolve ?? (m => Binding(m.Alias, window)), timeout ?? TimeSpan.FromSeconds(30), clock);

    private static Task<GoalVerdict> Eval(EvaluateGoalInput input, CancellationToken ct = default) => GoalEvaluator.EvaluateAsync(input, ct);

    private static Script ContextLength() => Up.Fail(AgentErrorCode.ContextLength);

    // ───────────────────────── 四种结论 ─────────────────────────

    [Fact]
    public async Task Ok_true_is_met()
    {
        var verdict = await Eval(Input(new Judge(Up.Raw("""{"ok":true,"reason":"bun test 全绿了"}"""))));

        Assert.Equal(new GoalVerdict.Met("bun test 全绿了"), verdict);
    }

    [Fact]
    public async Task Ok_false_is_not_met()
    {
        Assert.IsType<GoalVerdict.NotMet>(await Eval(Input(new Judge(Up.Raw("""{"ok":false,"reason":"还有两个红的"}""")))));
    }

    [Fact]
    public async Task Impossible_is_impossible()
    {
        Assert.IsType<GoalVerdict.Impossible>(await Eval(Input(new Judge(Up.Raw("""{"ok":false,"impossible":true,"reason":"那个包不存在"}""")))));
    }

    [Fact]
    public async Task Garbage_is_skipped_error_not_not_met()
    {
        Assert.Equal(new GoalVerdict.Skipped(GoalSkipReason.Error), await Eval(Input(new Judge(Up.Raw("大概可以了吧")))));
    }

    // ───────────────────────── 请求形状 ─────────────────────────

    [Fact]
    public async Task The_request_has_no_tools_thinking_off_1024_output_and_the_verbatim_system()
    {
        var judge = new Judge(Up.Raw("""{"ok":true}"""));

        await Eval(Input(judge));

        var request = Assert.Single(judge.Requests);
        Assert.Empty(request.Tools);
        Assert.Equal(ThinkingLevel.Off, request.Options.Thinking);
        Assert.Equal(1024, request.Options.MaxOutputTokens);
        Assert.Equal(GoalPrompts.EvaluatorSystem, request.System);
        Assert.Single(request.Messages);
    }

    [Fact]
    public async Task An_empty_judge_model_falls_back_as_a_pair_model_and_provider_together()
    {
        var seen = new List<ModelRef>();
        var judge = new Judge(Up.Raw("""{"ok":true}"""));

        await Eval(Input(judge, model: new ModelRef("p-judge", "   "), fallback: new ModelRef("p-run", "main"), resolve: m =>
        {
            seen.Add(m);
            return Binding(m.Alias);
        }));

        Assert.Equal([new ModelRef("p-run", "main")], seen);
        Assert.Equal(new ModelRef("p-run", "main"), Assert.Single(judge.Models));
    }

    [Fact]
    public async Task A_named_judge_model_uses_its_own_provider_not_the_runs()
    {
        var judge = new Judge(Up.Raw("""{"ok":true}"""));

        await Eval(Input(judge, model: new ModelRef("p-judge", "judge"), fallback: new ModelRef("p-run", "main")));

        Assert.Equal(new ModelRef("p-judge", "judge"), Assert.Single(judge.Models));
    }

    [Fact]
    public async Task No_model_at_all_is_skipped_no_model_and_sends_nothing()
    {
        var judge = new Judge(Up.Raw("""{"ok":true}"""));

        var verdict = await Eval(Input(judge, model: ModelRef.Unpinned(""), fallback: ModelRef.Unpinned("")));

        Assert.Equal(new GoalVerdict.Skipped(GoalSkipReason.NoModel), verdict);
        Assert.Empty(judge.Requests);
    }

    [Fact]
    public async Task An_alias_the_catalog_cannot_resolve_is_skipped_no_model()
    {
        var verdict = await Eval(Input(new Judge(Up.Raw("""{"ok":true}""")), resolve: _ => null));

        Assert.Equal(new GoalVerdict.Skipped(GoalSkipReason.NoModel), verdict);
    }

    [Fact]
    public async Task An_empty_transcript_is_skipped_transcript_empty()
    {
        Assert.Equal(new GoalVerdict.Skipped(GoalSkipReason.TranscriptEmpty), await Eval(Input(new Judge(Up.Raw("""{"ok":true}""")), messages: [])));
    }

    [Fact]
    public async Task A_transcript_of_only_ui_blocks_sends_no_request()
    {
        var messages = new[] { Msg.Assistant("a", new ThinkingPart("我觉得应该已经好了"), new GoalStatusPart(true, "让测试全绿", "我觉得好了")) };
        var judge = new Judge(Up.Raw("""{"ok":true}"""));

        Assert.Equal(new GoalVerdict.Skipped(GoalSkipReason.TranscriptEmpty), await Eval(Input(judge, messages: messages)));
        Assert.Empty(judge.Requests);
    }

    [Fact]
    public async Task A_thinking_level_the_model_cannot_turn_off_is_lowered_to_the_minimum_available_never_thrown()
    {
        var binding = Binding("judge") with { Thinking = new ThinkingConfig(ThinkingMode.Effort), ReasoningEfforts = [ReasoningEffort.Low, ReasoningEffort.High] };
        var judge = new Judge(Up.Raw("""{"ok":true}"""));

        await Eval(Input(judge, resolve: _ => binding));

        Assert.Equal(ThinkingLevel.Low, judge.Requests[0].Options.Thinking);
    }

    // ───────────────────────── 只有完整的回复才算数 ─────────────────────────

    private const string OkJson = """{"ok":true,"reason":"bun test 全绿了"}""";

    [Fact]
    public async Task A_stream_that_dies_midway_with_no_message_end_is_skipped_not_met()
    {
        var script = Up.Events(new TextDelta(0, OkJson));

        Assert.Equal(new GoalVerdict.Skipped(GoalSkipReason.Error), await Eval(Input(new Judge(script))));
    }

    [Fact]
    public async Task A_reply_that_stops_at_max_tokens_is_skipped_even_if_the_json_is_complete()
    {
        var script = Up.Events(new TextDelta(0, OkJson), new MessageEnd(StopReason.MaxTokens, Up.Usage));

        Assert.Equal(new GoalVerdict.Skipped(GoalSkipReason.Error), await Eval(Input(new Judge(script))));
    }

    [Fact]
    public async Task A_reply_that_stops_at_tool_use_is_skipped_the_judge_has_no_tools()
    {
        var script = Up.Events(new TextDelta(0, OkJson), new MessageEnd(StopReason.ToolUse, Up.Usage));

        Assert.IsType<GoalVerdict.Skipped>(await Eval(Input(new Judge(script))));
    }

    // ───────────────────────── 中断与超时 ─────────────────────────

    [Fact]
    public async Task A_call_that_is_already_aborted_sends_no_request()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var judge = new Judge(Up.Raw("""{"ok":true}"""));

        Assert.Equal(new GoalVerdict.Skipped(GoalSkipReason.Error), await Eval(Input(judge), cts.Token));
        Assert.Empty(judge.Requests);
    }

    [Fact]
    public async Task An_upstream_that_hangs_and_ignores_the_token_times_out_at_the_wall_clock()
    {
        var clock = new ManualClock();
        var task = Eval(Input(new Judge(Up.Stuck()), timeout: TimeSpan.FromSeconds(1), clock: clock));
        await Wait.UntilAsync(() => clock.PendingTimers == 1, what: "the wall clock timer");

        clock.Advance(TimeSpan.FromMilliseconds(1001));

        Assert.Equal(new GoalVerdict.Skipped(GoalSkipReason.Timeout), await task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task The_caller_aborting_a_running_evaluation_is_skipped_error_not_a_timeout()
    {
        using var cts = new CancellationTokenSource();
        var judge = new Judge(Up.Stuck());
        var task = Eval(Input(judge), cts.Token);
        await Wait.UntilAsync(() => judge.Requests.Count == 1);

        cts.Cancel();

        Assert.Equal(new GoalVerdict.Skipped(GoalSkipReason.Error), await task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Valid_json_followed_by_the_caller_aborting_is_never_met()
    {
        // 结论还没落地就中断了——那份材料是完整的,但这次判定已经不算数
        using var cts = new CancellationTokenSource();
        var gate = new TaskCompletionSource();
        var script = Up.Events(new MessageStart("f", "p1"), new TextDelta(0, """{"ok":true,"reason":"测试全绿了"}"""), new MessageEnd(StopReason.EndTurn, Up.Usage));
        Script then = (req, ct) => Tail(script, gate.Task, req, ct);
        var task = Eval(Input(new Judge(then)), cts.Token);

        await Task.Delay(50);
        cts.Cancel();
        gate.SetResult();

        Assert.Equal(new GoalVerdict.Skipped(GoalSkipReason.Error), await task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static async IAsyncEnumerable<LlmEvent> Tail(Script inner, Task hold, LlmRequest req, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var ev in inner(req, ct).WithCancellation(ct))
        {
            yield return ev;
        }

        await hold.ConfigureAwait(false); // 事件都给完了,收尾前再卡一下
    }

    // ───────────────────────── 失败与重试 ─────────────────────────

    [Fact]
    public async Task Context_length_retries_once_with_a_smaller_budget()
    {
        var judge = new Judge(ContextLength(), Up.Raw("""{"ok":true,"reason":"ok"}"""));

        Assert.IsType<GoalVerdict.Met>(await Eval(Input(judge)));
        Assert.Equal(2, judge.Requests.Count);
    }

    [Fact]
    public async Task Two_context_length_failures_give_up_no_third_attempt()
    {
        var judge = new Judge(ContextLength());

        Assert.Equal(new GoalVerdict.Skipped(GoalSkipReason.Error), await Eval(Input(judge)));
        Assert.Equal(2, judge.Requests.Count);
    }

    [Fact]
    public async Task Another_upstream_error_is_skipped_and_never_retried()
    {
        var judge = new Judge(Up.Fail(AgentErrorCode.Network));

        Assert.IsType<GoalVerdict.Skipped>(await Eval(Input(judge)));
        Assert.Single(judge.Requests);
    }

    [Fact]
    public async Task The_retry_is_really_smaller_a_quarter_window_not_the_same_request_again()
    {
        var turns = Enumerable.Range(0, 30).Select(i => Msg.User($"u{i}", new string('x', 400))).ToList();
        var judge = new Judge(ContextLength(), Up.Raw("""{"ok":true,"reason":"ok"}"""));

        await Eval(Input(judge, messages: turns, window: 8_000));

        Assert.Equal(2, judge.Requests.Count);
        Assert.True(TokenEstimate.OfText(judge.PromptOf(1)) < TokenEstimate.OfText(judge.PromptOf(0)));
        // 两份都被裁过
        Assert.Contains("truncated", judge.PromptOf(0));
        Assert.Contains("truncated", judge.PromptOf(1));
    }

    [Fact]
    public async Task Both_attempts_share_one_wall_clock_a_retry_does_not_double_the_timeout()
    {
        var clock = new ManualClock();
        var firstGate = new TaskCompletionSource();
        Script first = (req, ct) => AfterGate(firstGate.Task, ContextLength(), req, ct);
        var judge = new Judge(first, Up.Stuck());
        var task = Eval(Input(judge, timeout: TimeSpan.FromSeconds(1), clock: clock));

        await Wait.UntilAsync(() => judge.Requests.Count == 1);
        clock.Advance(TimeSpan.FromMilliseconds(600)); // 第一次耗掉 600ms 才报 context_length
        firstGate.SetResult();
        await Wait.UntilAsync(() => judge.Requests.Count == 2);
        Assert.False(task.IsCompleted);

        clock.Advance(TimeSpan.FromMilliseconds(401)); // 只剩 400ms:第二次挂住,到 1000ms 整个判定就得结束

        Assert.Equal(new GoalVerdict.Skipped(GoalSkipReason.Timeout), await task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static async IAsyncEnumerable<LlmEvent> AfterGate(Task gate, Script then, LlmRequest req, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        await foreach (var ev in then(req, ct).WithCancellation(ct))
        {
            yield return ev;
        }
    }

    [Fact]
    public async Task A_timeout_that_fires_between_the_two_attempts_sends_no_second_request()
    {
        var clock = new ManualClock();
        var gate = new TaskCompletionSource();
        Script first = (req, ct) => AfterGate(gate.Task, ContextLength(), req, ct);
        var judge = new Judge(first, Up.Raw("""{"ok":true}"""));
        var task = Eval(Input(judge, timeout: TimeSpan.FromSeconds(1), clock: clock));
        await Wait.UntilAsync(() => judge.Requests.Count == 1);

        clock.Advance(TimeSpan.FromSeconds(2)); // 墙钟到点(第一次的流不看令牌之外的东西)
        gate.SetResult();

        Assert.Equal(new GoalVerdict.Skipped(GoalSkipReason.Timeout), await task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Single(judge.Requests);
    }

    // ───────────────────────── renderTranscript:回合边界与预算 ─────────────────────────

    private static List<Message> MarkedTurns()
    {
        var messages = new List<Message>();
        for (var i = 0; i < 20; i++)
        {
            var mark = $"MARK-{(char)('A' + i)}";
            messages.Add(Msg.User($"u{i}", $"{mark} {new string('x', 400)}"));
            messages.Add(Msg.Say($"a{i}", $"answer-{mark}"));
        }

        return messages;
    }

    [Fact]
    public void When_everything_fits_nothing_is_cut_and_no_prefix_is_added()
    {
        var text = GoalTranscript.Render(History, 100_000);

        Assert.DoesNotContain("truncated", text);
        Assert.Contains("把测试修好", text);
    }

    [Fact]
    public void The_cut_part_becomes_an_explicit_prefix_and_what_stays_is_the_latest_whole_turns()
    {
        var text = GoalTranscript.Render(MarkedTurns(), 560);

        Assert.Contains("Earlier conversation truncated to fit the hook evaluator's context window", text);
        Assert.Contains("earlier messages omitted", text);
        Assert.Contains("insufficient evidence in transcript", text);
        // 最近那一回合整条都在(user 和它的 assistant 回复)
        Assert.Contains("MARK-T", text);
        Assert.Contains("answer-MARK-T", text);
        Assert.DoesNotContain("MARK-A ", text);
        // ★ 留下的第一行永远是一个回合的起点,不是半截 tool_result
        Assert.StartsWith("user: ", text.Split('\n')[1]);
    }

    [Fact]
    public void When_the_latest_turn_alone_does_not_fit_the_whole_transcript_is_withheld_not_an_older_one()
    {
        // ★ 只发更旧的回合,等于让判定器拿着一份缺了最新一轮的证据去判「达成」,而那条前缀说的是「更早的那些被裁掉了」,方向正好相反
        var messages = new[]
        {
            Msg.User("u0", "MARK-EARLY"),
            Msg.Say("a0", "answer-EARLY"),
            Msg.User("u1", $"MARK-LATE {new string('x', 4000)}"),
        };

        var text = GoalTranscript.Render(messages, 200);

        Assert.Contains("insufficient evidence in transcript", text);
        Assert.DoesNotContain("MARK-LATE", text);
        Assert.DoesNotContain("MARK-EARLY", text);
    }

    [Fact]
    public async Task When_the_budget_cannot_hold_even_one_evidence_line_nothing_is_sent_and_it_is_skipped()
    {
        var messages = new[]
        {
            Msg.User("u0", "MARK-EARLY"),
            Msg.Say("a0", "answer-EARLY"),
            Msg.User("u1", $"MARK-LATE {new string('x', 4000)}"),
        };
        var judge = new Judge(Up.Raw("""{"ok":true}"""));

        var verdict = await Eval(Input(judge, messages: messages, window: 3_000));

        Assert.Equal(new GoalVerdict.Skipped(GoalSkipReason.TranscriptEmpty), verdict);
        Assert.Empty(judge.Requests);
    }

    [Fact]
    public void The_budget_is_counted_with_the_token_estimator_not_chars_over_four_a_cjk_character_is_one_token()
    {
        Assert.Contains("x", GoalTranscript.Render([Msg.User("u", new string('x', 200))], 100));
        Assert.DoesNotContain("好", GoalTranscript.Render([Msg.User("u", new string('好', 200))], 100));
        Assert.Contains("好", GoalTranscript.Render([Msg.User("u", new string('好', 200))], 300));
    }

    [Fact]
    public void What_is_kept_is_the_most_recent_not_the_oldest()
    {
        var messages = new[] { Msg.User("old", $"很久以前 {new string('x', 400)}"), Msg.User("new", "刚刚说的话") };

        var text = GoalTranscript.Render(messages, 100);

        Assert.Contains("刚刚说的话", text);
        Assert.Contains("truncated", text);
        Assert.DoesNotContain("很久以前", text);
    }

    [Fact]
    public void Thinking_never_reaches_the_judge_a_drafted_belief_is_not_evidence()
    {
        var messages = new[] { Msg.Assistant("a", new ThinkingPart("我觉得应该已经好了"), new TextPart("改完了")) };

        var text = GoalTranscript.Render(messages, 100_000);

        Assert.DoesNotContain("我觉得应该已经好了", text);
        Assert.Contains("改完了", text);
    }

    [Fact]
    public void The_goal_marker_does_not_reach_the_judge_either_it_is_our_own_stamp()
    {
        var messages = new[] { Msg.Assistant("a", new TextPart("改完了"), new GoalStatusPart(false, "让测试全绿", "还红着")) };

        Assert.DoesNotContain("还红着", GoalTranscript.Render(messages, 100_000));
    }

    [Fact]
    public void Tool_calls_and_results_are_evidence()
    {
        var messages = new[] { Msg.ToolCall("a", "c1", "Bash", """{"command":"bun test"}"""), Msg.ToolResult("u", "c1", "12 pass, 0 fail") };

        var text = GoalTranscript.Render(messages, 100_000);

        Assert.Contains("bun test", text);
        Assert.Contains("12 pass, 0 fail", text);
    }

    [Fact]
    public void A_tool_result_carries_its_call_id_and_error_state_so_the_evidence_can_be_attributed()
    {
        var messages = new[] { Msg.ToolCall("a", "call-7", "Bash", """{"command":"bun test"}"""), Msg.ToolResult("u", "call-7", "1 fail", isError: true) };

        var text = GoalTranscript.Render(messages, 100_000);

        Assert.Contains("call-7", text);
        Assert.Contains("[error]", text);
        Assert.Contains("1 fail", text);
    }

    [Fact]
    public void A_file_reference_contributes_only_its_path()
    {
        var messages = new[] { Msg.User("u", "x") with { Parts = [new FileRefPart("/tmp/report.txt", "report.txt")] }, Msg.Say("a", "看过了") };

        var text = GoalTranscript.Render(messages, 100_000);

        Assert.Contains("/tmp/report.txt", text);
        Assert.Contains("看过了", text);
    }

    [Fact]
    public void A_leading_orphan_tool_result_never_starts_a_kept_suffix()
    {
        // 第一条回合起点之前的内容(没配对上的 tool_result)自成一组,但不是回合边界
        var messages = new[] { Msg.ToolResult("u0", "c0", "孤儿输出" + new string('x', 400)), Msg.User("u1", "新问题"), Msg.Say("a1", "回答") };

        var text = GoalTranscript.Render(messages, 100); // 前缀本身约 75 token

        Assert.DoesNotContain("孤儿输出", text);
        Assert.Contains("新问题", text);
    }
}
