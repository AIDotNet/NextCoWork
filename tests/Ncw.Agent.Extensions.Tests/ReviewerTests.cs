using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Extensions.Tests;

/// <summary>「为我批准」的 AI 审核器(2.x <c>approval.ts</c> 的 <c>reviewSensitiveOperation</c>,2.x 没有单测;按它注释里写明的承诺补)。</summary>
public sealed class ReviewerTests
{
    private static ReviewRequest Request(string json = """{"command":"git push"}""") =>
        new(new RunId("r1"), "s1", "w1", new ModelRef(null, "rev"), "ship it", "Bash", json.Json());

    [Theory]
    [InlineData("{\"decision\":\"allow\"}", ReviewResult.Allow)]
    [InlineData("{\"decision\":\"deny\"}", ReviewResult.Deny)]
    [InlineData("```json\n{\"decision\":\"allow\"}\n```", ReviewResult.Allow)]
    [InlineData("```\n{\"decision\": \"deny\"}\n```", ReviewResult.Deny)]
    [InlineData("Sure. {\"decision\":\"deny\"} hope that helps", ReviewResult.Deny)]
    [InlineData("decision: allow", ReviewResult.Allow)]
    [InlineData("decision = \"deny\"", ReviewResult.Deny)]
    [InlineData("allow", ReviewResult.Allow)]
    [InlineData("Allowed.", ReviewResult.Allow)]
    [InlineData("允许", ReviewResult.Allow)]
    [InlineData("拒绝。", ReviewResult.Deny)]
    [InlineData("deny", ReviewResult.Deny)]
    public void Parses_what_models_actually_say(string text, ReviewResult expected) => Assert.Equal(expected, LlmPermissionReviewer.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("I'm not sure.")]
    [InlineData("{\"decision\":\"maybe\"}")]
    [InlineData("{\"decision\":1}")]
    [InlineData("{'decision':'allow'}")]                       // 不是 JSON,也不是 decision: allow 的写法(2.x 同)
    [InlineData("[1,2]")]
    [InlineData("I would not allow.")]                        // 2.x 的未锚定正则会把它读成允许
    [InlineData("Do not deny. I allow.")]
    [InlineData("decision: allow ... actually decision: deny")]  // 两种都沾边 = 含糊
    [InlineData("{\"decision\":")]
    public void Anything_ambiguous_is_unknown_so_it_falls_back_to_a_human(string text) => Assert.Equal(ReviewResult.Unknown, LlmPermissionReviewer.Parse(text));

    [Fact]
    public async Task Reviews_with_a_tool_less_128_token_request_that_carries_the_task_tool_and_input()
    {
        var llm = new FakeLlm().Then(Up.Say("{\"decision\":\"allow\"}"));
        var result = await new LlmPermissionReviewer(llm).ReviewAsync(Request(), default);
        Assert.Equal(ReviewResult.Allow, result);
        var sent = Assert.Single(llm.Requests);
        Assert.Empty(sent.Tools);
        Assert.Equal(128, sent.Options.MaxOutputTokens);
        Assert.Contains("conservative", sent.System);
        var prompt = ((TextPart)sent.Messages.Single().Parts.Single()).Text;
        Assert.Contains("User task: ship it", prompt);
        Assert.Contains("Tool: Bash", prompt);
        Assert.Contains("git push", prompt);
    }

    [Fact]
    public async Task The_task_and_input_are_clipped_so_the_reviewer_request_stays_small()
    {
        var long8 = new string('x', 50_000);
        var req = Request($$"""{"command":"{{long8}}"}""") with { UserTask = long8 };
        var prompt = LlmPermissionReviewer.BuildPrompt(req);
        Assert.True(prompt.Length < 8000 + 12000 + 600);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Every_failure_is_unknown_never_allow_a_dead_reviewer_must_not_wave_things_through()
    {
        Assert.Equal(ReviewResult.Unknown, await new LlmPermissionReviewer(new FakeLlm().Then([new StreamError(AgentError.Create(AgentErrorCode.Provider, "boom", false))])).ReviewAsync(Request(), default));
        Assert.Equal(ReviewResult.Unknown, await new LlmPermissionReviewer(new FakeLlm()).ReviewAsync(Request(), default)); // 没有脚本 = 上游报错
        Assert.Equal(ReviewResult.Unknown, await new LlmPermissionReviewer(new ThrowingLlm()).ReviewAsync(Request(), default));
        Assert.Equal(ReviewResult.Unknown, await new LlmPermissionReviewer(new FakeLlm().Then(Up.Say("hmm"))).ReviewAsync(Request(), default));
    }

    [Fact]
    public async Task No_reviewer_model_configured_is_unknown_without_calling_the_upstream()
    {
        var llm = new FakeLlm();
        Assert.Equal(ReviewResult.Unknown, await new LlmPermissionReviewer(llm).ReviewAsync(Request() with { Model = null }, default));
        Assert.Equal(ReviewResult.Unknown, await new LlmPermissionReviewer(llm).ReviewAsync(Request() with { Model = new ModelRef(null, "  ") }, default));
        Assert.Empty(llm.Requests);
    }

    [Fact]
    public async Task Cancellation_propagates_it_is_not_an_unknown()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await new LlmPermissionReviewer(new ThrowingLlm(new OperationCanceledException(cts.Token))).ReviewAsync(Request(), cts.Token));
    }

    private sealed class ThrowingLlm(Exception? error = null) : Ncw.Agent.Llm.Routing.ILlmClient
    {
        public async IAsyncEnumerable<LlmEvent> StreamAsync(ModelRef model, LlmRequest request, RequestContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            throw error ?? new InvalidOperationException("boom");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }
}
