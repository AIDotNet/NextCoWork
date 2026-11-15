namespace Ncw.Agent.Hooks.Tests;

/// <summary>prompt 型钩子 = 一次判定调用,翻译成同一份 <see cref="HookRunReport"/>(移植自 2.x <c>hooks.ts</c> 的 <c>runPromptHook</c> 映射)。</summary>
public sealed class PromptHookTests
{
    private sealed class FakeEvaluator(Func<EvaluationRequest, EvaluationVerdict> verdict) : IHookEvaluator
    {
        public List<EvaluationRequest> Requests { get; } = [];

        public ValueTask<EvaluationVerdict> EvaluateAsync(EvaluationRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return ValueTask.FromResult(verdict(request));
        }
    }

    private static HookDefinition Prompt(string text = "Is the goal met? $ARGUMENTS") => new()
    {
        Id = "p1",
        Event = HookEvents.Stop,
        Kind = HookKind.Prompt,
        Prompt = text,
        TimeoutMs = 30_000,
    };

    private static HookPayload Payload() => HookFixture.Payload(HookEvents.Stop) with { Status = "done", StopHookActive = false };

    private static Task<HookRunReport> RunAsync(IHookEvaluator? evaluator, HookDefinition? hook = null) =>
        PromptHookRunner.RunAsync(evaluator, hook ?? Prompt(), HookScope.Project, Payload(), "ws", CancellationToken.None);

    [Fact]
    public async Task Met_does_not_block()
    {
        var report = await RunAsync(new FakeEvaluator(_ => new EvaluationVerdict(EvaluationKind.Met, "all green")));

        Assert.Equal(HookOutcome.Ok, report.Outcome);
        Assert.False(report.Blocks);
        Assert.Equal(PromptVerdict.Met, report.PromptVerdict);
        Assert.Equal("all green", report.Reason);
    }

    [Theory]
    [InlineData(EvaluationKind.NotMet, PromptVerdict.NotMet)]
    [InlineData(EvaluationKind.Impossible, PromptVerdict.Impossible)]
    public async Task Not_met_and_impossible_both_block_but_the_raw_verdict_is_kept_so_callers_can_tell_them_apart(EvaluationKind kind, PromptVerdict expected)
    {
        var report = await RunAsync(new FakeEvaluator(_ => new EvaluationVerdict(kind, "tests are still red")));

        Assert.True(report.Blocks);
        Assert.Equal(HookDecision.Deny, report.Decision);
        Assert.Equal("tests are still red", report.Reason);
        Assert.Equal(expected, report.PromptVerdict);
    }

    [Fact]
    public async Task A_skipped_evaluation_does_not_block_it_is_fail_open_like_command_hooks()
    {
        var report = await RunAsync(new FakeEvaluator(_ => new EvaluationVerdict(EvaluationKind.Skipped, "unavailable")));

        Assert.False(report.Blocks);
        Assert.Equal(PromptVerdict.Skipped, report.PromptVerdict);
        Assert.NotEqual(0, report.ExitCode); // 非 0 非 2 的退出码:自动进诊断
    }

    [Fact]
    public async Task A_timed_out_evaluation_is_reported_as_a_timeout()
    {
        var report = await RunAsync(new FakeEvaluator(_ => new EvaluationVerdict(EvaluationKind.Skipped, "timeout")));

        Assert.Equal(HookOutcome.Timeout, report.Outcome);
        Assert.False(report.Blocks);
    }

    [Fact]
    public async Task No_evaluator_means_skipped_a_hook_that_cannot_judge_must_not_stop_a_run()
    {
        var report = await RunAsync(null);

        Assert.Equal(PromptVerdict.Skipped, report.PromptVerdict);
        Assert.False(report.Blocks);
    }

    [Fact]
    public async Task An_evaluator_that_throws_is_a_skip_not_a_crash()
    {
        var report = await PromptHookRunner.RunAsync(new ThrowingEvaluator(), Prompt(), HookScope.Project, Payload(), "ws", CancellationToken.None);

        Assert.False(report.Blocks);
        Assert.Equal(PromptVerdict.Skipped, report.PromptVerdict);
    }

    private sealed class ThrowingEvaluator : IHookEvaluator
    {
        public ValueTask<EvaluationVerdict> EvaluateAsync(EvaluationRequest request, CancellationToken ct) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task The_arguments_placeholder_is_replaced_with_the_payload_once_and_the_hook_input_is_appended()
    {
        var evaluator = new FakeEvaluator(_ => new EvaluationVerdict(EvaluationKind.Met));

        await RunAsync(evaluator);

        var request = Assert.Single(evaluator.Requests);
        Assert.Contains("\"event\":\"Stop\"", request.Question);
        Assert.DoesNotContain("$ARGUMENTS", request.Question);
        Assert.Contains("\n\nHook input:\n{", request.Question);
        Assert.Equal("ws", request.WorkspaceId);
        Assert.Equal(TimeSpan.FromSeconds(30), request.Timeout);
    }

    [Fact]
    public async Task A_placeholder_inside_the_payload_text_is_not_expanded_a_second_time()
    {
        var evaluator = new FakeEvaluator(_ => new EvaluationVerdict(EvaluationKind.Met));
        var payload = Payload() with { Prompt = "$ARGUMENTS" };

        await PromptHookRunner.RunAsync(evaluator, Prompt("Q: $ARGUMENTS"), HookScope.Project, payload, "ws", CancellationToken.None);

        var question = evaluator.Requests[0].Question;
        // 载荷里字面量 "$ARGUMENTS" 原样出现在 prompt 字段里,没有再被展开成载荷本身
        Assert.Contains("\"prompt\":\"$ARGUMENTS\"", question);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(question, System.Text.RegularExpressions.Regex.Escape("\"prompt\":\"$ARGUMENTS\"")).Count);
    }

    [Fact]
    public void Preview_only_substitutes_it_never_calls_a_model()
    {
        var preview = PromptHookRunner.Preview("check: $ARGUMENTS", "{\"a\":1}");

        Assert.Equal("check: {\"a\":1}", preview);
    }
}
