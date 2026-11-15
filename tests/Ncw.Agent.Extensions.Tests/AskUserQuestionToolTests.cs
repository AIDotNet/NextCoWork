using System.Text.Json;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Extensions.Tests.Support;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Tools;

namespace Ncw.Agent.Extensions.Tests;

/// <summary>
/// <c>AskUserQuestion</c> 工具(移植自 2.x <c>ask-user-tool.test.ts</c> 的全部用例,另补 3.0 的:多控制端抢答只有一个成功、子代理三道闸、
/// 工具 Details 带执行环境标识、中止时交互收尾、schema 边界)。
/// </summary>
public sealed class AskUserQuestionToolTests : IDisposable
{
    private readonly Fixture _fx = new();
    private readonly InteractionGate _gate = new(new SequentialIds());
    private readonly PermissionRuns _runs = new();
    private readonly AskUserQuestionTool _tool;
    private int _n;

    public AskUserQuestionToolTests()
    {
        _tool = new AskUserQuestionTool(_runs);
    }

    public void Dispose() => _fx.Dispose();

    private sealed class NoProgress : IProgress<ToolProgress>
    {
        public void Report(ToolProgress value)
        {
        }
    }

    private (RunId Run, ExecutionToolContext Ctx) Start(int depth = 0, bool register = true)
    {
        var run = new RunId($"run-{++_n}");
        if (register)
        {
            _runs.Register(PermissionRunState.FromRuntime(_fx.Runtime, run, "s1", _gate.OpenRun(run, depth), PermissionMode.Full));
        }

        var ctx = new ExecutionToolContext(new ToolContext(run, "c1", "s1", depth, new EnvironmentTag("dev-1", "local"), new NoProgress(), new FileMutationQueue()), _fx.Runtime);
        return (run, ctx);
    }

    private async Task<ToolResult> Call(ExecutionToolContext ctx, string json, CancellationToken ct = default)
    {
        var input = json.Json();
        var check = _tool.PrepareArguments(input);
        if (!check.IsValid)
        {
            return ToolResult.Error(check.Error!);
        }

        return await _tool.ExecuteAsync(new ToolInvocation("c1", "AskUserQuestion", _tool, check.Input), ctx, ct);
    }

    private static string TextOf(ToolResult r) => ((Ncw.Agent.Llm.TextPart)Assert.Single(r.Content)).Text;

    private InteractionRequest PendingOnly() => Assert.Single(_gate.Snapshot().Pending);

    private static AskUserResponse Answers(params string[][] answers) => new(answers.Select(a => (IReadOnlyList<string>)a).ToList());

    private async Task<InteractionRequest> WaitPending()
    {
        for (var i = 0; i < 500 && _gate.Snapshot().Pending.Count == 0; i++)
        {
            await Task.Delay(5);
        }

        return PendingOnly();
    }

    // ───────────────────────── 标记 ─────────────────────────

    [Fact]
    public void Traits_and_prompt_pieces_are_declared()
    {
        Assert.Equal("AskUserQuestion", _tool.Info.InternalId);
        Assert.True(_tool.Info.Traits.ReadOnly);
        Assert.False(_tool.Info.Traits.Destructive);
        Assert.False(_tool.Info.Traits.NeedsNetwork);
        Assert.False(string.IsNullOrWhiteSpace(_tool.Info.PromptSnippet));
        Assert.NotEmpty(_tool.Info.PromptGuidelines!);
    }

    // ───────────────────────── 2.x 用例 ─────────────────────────

    [Fact]
    public async Task Suspends_several_questions_as_they_are_and_hands_answers_back_by_header()
    {
        var (_, ctx) = Start();
        var call = Call(ctx, """
            {"questions":[
              {"header":"范围","question":"改哪些文件?","options":[{"label":"A","description":"只改前端"},{"label":"B"}],"multiSelect":true,"allowFreeform":false},
              {"header":"细节","question":"还有要补充的吗?"}]}
            """);
        var pending = (AskUser)await WaitPending();
        Assert.Equal(2, pending.Questions.Count);
        Assert.Equal("范围", pending.Questions[0].Header);
        Assert.True(pending.Questions[0].MultiSelect);
        Assert.False(pending.Questions[0].AllowFreeform);
        Assert.Equal("只改前端", pending.Questions[0].Options[0].Description);
        Assert.True(pending.Questions[1].AllowFreeform);

        Assert.IsType<RespondResult.Accepted>(_gate.Respond(pending.Id, pending.Version, Answers(["A", "B"], ["随便写的"]), "ctrl-a"));
        var result = await call;
        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(TextOf(result));
        var answers = doc.RootElement.GetProperty("answers");
        Assert.Equal("范围", answers[0].GetProperty("header").GetString());
        Assert.Equal("改哪些文件?", answers[0].GetProperty("question").GetString());
        Assert.Equal(["A", "B"], answers[0].GetProperty("answer").EnumerateArray().Select(static e => e.GetString()!).ToArray());
        Assert.Equal(["随便写的"], answers[1].GetProperty("answer").EnumerateArray().Select(static e => e.GetString()!).ToArray());
    }

    [Fact]
    public async Task A_question_without_options_defaults_to_free_form_answers()
    {
        var (_, ctx) = Start();
        var call = Call(ctx, """{"questions":[{"header":"想法","question":"你怎么想?"}]}""");
        var pending = (AskUser)await WaitPending();
        Assert.Empty(pending.Questions[0].Options);
        Assert.True(pending.Questions[0].AllowFreeform);
        Assert.False(pending.Questions[0].MultiSelect);
        _gate.Respond(pending.Id, pending.Version, Answers(["好"]), "ctrl-a");
        Assert.False((await call).IsError);
    }

    [Fact]
    public async Task A_question_that_cannot_be_answered_is_stopped_before_it_suspends()
    {
        var (_, ctx) = Start();
        var result = await Call(ctx, """{"questions":[{"header":"x","question":"y","allowFreeform":false}]}""");
        Assert.True(result.IsError);
        Assert.Contains("Question 1 has no options and does not allow a freeform answer", TextOf(result));
        Assert.Empty(_gate.Snapshot().Pending); // 没有挂起任何东西
    }

    [Fact]
    public async Task Dismissing_the_dialog_tells_the_model_there_is_no_answer_instead_of_an_empty_one()
    {
        var (_, ctx) = Start();
        var call = Call(ctx, """{"questions":[{"header":"x","question":"y"}]}""");
        var pending = await WaitPending();
        _gate.Respond(pending.Id, pending.Version, new AskUserResponse(null), "ctrl-a");
        var result = await call;
        Assert.True(result.IsError);
        Assert.Contains("dismissed", TextOf(result));
        Assert.Contains("Do not assume an answer", TextOf(result));
    }

    [Theory]
    [InlineData("""{"questions":[]}""")]
    [InlineData("""{"questions":[{"header":"a","question":"q"},{"header":"b","question":"q"},{"header":"c","question":"q"},{"header":"d","question":"q"},{"header":"e","question":"q"}]}""")]
    [InlineData("""{}""")]
    [InlineData("""{"questions":[{"header":"a"}]}""")]
    [InlineData("""{"questions":[{"header":"a","question":"q","options":[{"label":"1"},{"label":"2"},{"label":"3"},{"label":"4"},{"label":"5"},{"label":"6"},{"label":"7"},{"label":"8"},{"label":"9"}]}]}""")]
    [InlineData("""{"questions":[{"header":"a header that is much longer than twenty-four characters","question":"q"}]}""")]
    public async Task The_schema_stops_a_question_count_outside_1_to_4_missing_fields_more_than_8_options_and_a_long_header(string json)
    {
        var (_, ctx) = Start();
        var result = await Call(ctx, json);
        Assert.True(result.IsError);
        Assert.Empty(_gate.Snapshot().Pending);
    }

    [Fact]
    public async Task Duplicate_or_blank_option_labels_are_refused_before_they_reach_the_user()
    {
        var (_, ctx) = Start();
        var dup = await Call(ctx, """{"questions":[{"header":"a","question":"q","options":[{"label":"X"},{"label":"X"}]}]}""");
        Assert.True(dup.IsError);
        var blank = await Call(ctx, """{"questions":[{"header":"a","question":"q","options":[{"label":"  "}]}]}""");
        Assert.True(blank.IsError);
        Assert.Empty(_gate.Snapshot().Pending);
    }

    // ───────────────────────── 3.0:抢答 / 子代理 / 环境标识 / 中止 ─────────────────────────

    [Fact]
    public async Task Two_controllers_racing_to_answer_only_one_succeeds_and_the_tool_sees_the_winner()
    {
        var (_, ctx) = Start();
        var call = Call(ctx, """{"questions":[{"header":"选哪个","question":"A 还是 B?","options":[{"label":"A"},{"label":"B"}]}]}""");
        var pending = await WaitPending();

        // 两个控制端基于同一份快照同时回答不同的答案
        using var start = new ManualResetEventSlim();
        var phone = Task.Run(() => { start.Wait(); return _gate.Respond(pending.Id, pending.Version, Answers(["A"]), "phone"); });
        var desktop = Task.Run(() => { start.Wait(); return _gate.Respond(pending.Id, pending.Version, Answers(["B"]), "desktop"); });
        start.Set();
        var results = await Task.WhenAll(phone, desktop);
        Assert.Equal(1, results.Count(static r => r is RespondResult.Accepted));
        var loser = Assert.Single(results.OfType<RespondResult.Conflict>());
        Assert.Equal(ConflictReason.AlreadyResolved, loser.Reason);
        Assert.NotNull(loser.Resolution?.ResolvedBy); // 后到的据此显示「已在另一台设备上处理」

        var winner = results.OfType<RespondResult.Accepted>().Single().Resolution.ResolvedBy;
        using var doc = JsonDocument.Parse(TextOf(await call));
        var answer = doc.RootElement.GetProperty("answers")[0].GetProperty("answer")[0].GetString();
        Assert.Equal(winner == "phone" ? "A" : "B", answer); // 工具拿到的是赢家的答案,不是后到的
    }

    [Fact]
    public async Task A_subagent_cannot_ask_the_snapshot_has_no_tool_and_a_leaked_call_is_refused_without_suspending()
    {
        var (_, parentCtx) = Start();
        Assert.True(_tool.IsEnabled(parentCtx));

        var (_, childCtx) = Start(depth: 1);
        Assert.False(_tool.IsEnabled(childCtx)); // 闸一:快照里没有它

        var result = await Call(childCtx, """{"questions":[{"header":"x","question":"y"}]}"""); // 闸二:就算漏过去,作用域 depth > 0 发起即拒绝
        Assert.True(result.IsError);
        Assert.Contains("only the top-level run can ask", TextOf(result));
        Assert.Empty(_gate.Snapshot().Pending);
    }

    [Fact]
    public async Task A_run_without_a_registered_interaction_scope_is_refused()
    {
        var (_, ctx) = Start(register: false);
        var result = await Call(ctx, """{"questions":[{"header":"x","question":"y"}]}""");
        Assert.True(result.IsError);
        Assert.Empty(_gate.Snapshot().Pending);
    }

    [Fact]
    public async Task Details_carry_the_execution_environment_and_never_the_answers()
    {
        var (_, ctx) = Start();
        var call = Call(ctx, """{"questions":[{"header":"x","question":"y"}]}""");
        var pending = await WaitPending();
        _gate.Respond(pending.Id, pending.Version, Answers(["秘密答案"]), "ctrl-a");
        var result = await call;
        var details = result.Details!.Value;
        Assert.Equal("askUser", details.GetProperty("kind").GetString());
        Assert.Equal("dev-1", details.GetProperty("environment").GetProperty("deviceId").GetString());
        Assert.Equal("local", details.GetProperty("environment").GetProperty("type").GetString());
        Assert.DoesNotContain("秘密答案", details.GetRawText());
    }

    [Fact]
    public async Task Aborting_the_run_cancels_the_pending_question_and_clears_the_gate()
    {
        var (_, ctx) = Start();
        using var cts = new CancellationTokenSource();
        var call = Call(ctx, """{"questions":[{"header":"x","question":"y"}]}""", cts.Token);
        await WaitPending();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Empty(_gate.Snapshot().Pending);
    }

    [Fact]
    public async Task A_stale_version_is_a_conflict_and_the_question_stays_pending()
    {
        var (_, ctx) = Start();
        var call = Call(ctx, """{"questions":[{"header":"x","question":"y"}]}""");
        var pending = await WaitPending();
        Assert.IsType<RespondResult.Conflict>(_gate.Respond(pending.Id, pending.Version + 7, Answers(["z"]), "ctrl-a"));
        Assert.Single(_gate.Snapshot().Pending);
        _gate.Respond(pending.Id, pending.Version, Answers(["z"]), "ctrl-a");
        Assert.False((await call).IsError);
    }
}
