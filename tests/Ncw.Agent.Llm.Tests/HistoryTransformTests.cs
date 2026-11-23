using Ncw.Agent.Llm.History;
using Ncw.Agent.Llm.Tests.Support;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Llm.Tests;

/// <summary>跨模型切换(01-llm §4):每次编码前对历史做一次只读转换,六种情况各一组测试。</summary>
public sealed class HistoryTransformTests
{
    private static readonly Provenance ModelA = new("anthropic", "p1", "claude-a");

    private static HistoryTarget TargetA => HistoryTarget.For(WireProtocol.Anthropic, "p1", "claude-a");

    private static Message AssistantWithThinking(Provenance? source) =>
        Msg.Assistant(source,
            new ThinkingPart("deep", Msg.Opaque("anthropic", """{"signature":"SIG"}""")),
            new TextPart("answer"),
            Msg.Call("toolu_1", "Read"));

    private static IReadOnlyList<Message> History(Provenance? source) =>
    [
        Msg.User("q"),
        AssistantWithThinking(source),
        Msg.ToolResults(("toolu_1", "ok", false)),
    ];

    // 情况 1:同一个 api + provider + model → 原样保留,包括签名和加密推理

    [Fact]
    public void Same_api_provider_and_model_keeps_thinking_and_its_signature()
    {
        var prepared = HistoryTransform.Prepare(History(ModelA), TargetA);

        var thinking = Assert.Single(prepared[1].Parts.OfType<ThinkingPart>());
        Assert.Equal("SIG", thinking.Opaque!.Data.GetProperty("signature").GetString());
    }

    // 情况 2:来源不同 → 丢弃思考块和透传数据

    [Theory]
    [InlineData("openai-chat", "p1", "claude-a")] // api 不同
    [InlineData("anthropic", "p2", "claude-a")] // provider 不同
    [InlineData("anthropic", "p1", "claude-b")] // model 不同
    public void A_different_api_provider_or_model_drops_the_thinking_block(string api, string provider, string model)
    {
        var prepared = HistoryTransform.Prepare(History(new Provenance(api, provider, model)), TargetA);

        Assert.Empty(prepared[1].Parts.OfType<ThinkingPart>());
        Assert.Contains(prepared[1].Parts, p => p is TextPart { Text: "answer" }); // 其余内容保留
        Assert.Contains(prepared[1].Parts, p => p is ToolCallPart);
    }

    [Fact]
    public void A_message_of_unknown_origin_is_treated_as_a_different_source()
    {
        var prepared = HistoryTransform.Prepare(History(null), TargetA);

        Assert.Empty(prepared[1].Parts.OfType<ThinkingPart>());
    }

    [Fact]
    public void Transform_is_read_only_the_stored_history_is_untouched()
    {
        var history = History(new Provenance("openai-chat", "p9", "x"));

        _ = HistoryTransform.Prepare(history, TargetA);

        Assert.Single(history[1].Parts.OfType<ThinkingPart>());
    }

    // 情况 3:工具调用 id 不符合目标协议的格式 → 改写,工具结果同步改写

    [Fact]
    public void A_tool_call_id_the_target_cannot_carry_is_rewritten_and_its_result_follows()
    {
        const string bad = "call_abc|fc_123.with spaces";
        var history = new[]
        {
            Msg.User("q"),
            Msg.Assistant(ModelA, Msg.Call(bad, "Read")),
            Msg.ToolResults((bad, "ok", false)),
        };

        var prepared = HistoryTransform.Prepare(history, TargetA);

        var call = Assert.Single(prepared[1].Parts.OfType<ToolCallPart>());
        var result = Assert.Single(prepared[2].Parts.OfType<ToolResultPart>());
        Assert.True(HistoryTransform.IsValidToolCallId(WireProtocol.Anthropic, call.CallId));
        Assert.Equal(call.CallId, result.CallId);
        Assert.NotEqual(bad, call.CallId);
    }

    [Fact]
    public void Id_rewriting_is_a_stable_function_of_the_id_so_every_turn_gets_the_same_result()
    {
        var a = HistoryTransform.NormalizeToolCallId(WireProtocol.Anthropic, "bad id!");
        var b = HistoryTransform.NormalizeToolCallId(WireProtocol.Anthropic, "bad id!");
        var other = HistoryTransform.NormalizeToolCallId(WireProtocol.Anthropic, "bad id?");

        Assert.Equal(a, b);
        Assert.NotEqual(a, other);
        Assert.Equal("toolu_ok-1", HistoryTransform.NormalizeToolCallId(WireProtocol.Anthropic, "toolu_ok-1")); // 合法的不动
    }

    [Fact]
    public void Chat_ids_have_a_tighter_length_limit_than_anthropic()
    {
        var id = new string('a', 50);

        Assert.True(HistoryTransform.IsValidToolCallId(WireProtocol.Anthropic, id));
        Assert.False(HistoryTransform.IsValidToolCallId(WireProtocol.OpenAiChat, id));
        Assert.True(HistoryTransform.IsValidToolCallId(WireProtocol.OpenAiChat, HistoryTransform.NormalizeToolCallId(WireProtocol.OpenAiChat, id)));
    }

    // 情况 4:有调用没结果 → 补一个 isError 的合成结果

    [Fact]
    public void A_call_without_a_result_gets_a_synthetic_error_result_right_after_the_assistant_message()
    {
        var history = new[]
        {
            Msg.User("q"),
            Msg.Assistant(ModelA, Msg.Call("toolu_1", "Read"), Msg.Call("toolu_2", "Read")),
            Msg.User("a follow-up the user typed meanwhile"),
        };

        var prepared = HistoryTransform.Prepare(history, TargetA);

        Assert.Equal(4, prepared.Count);
        var synthetic = prepared[2].Parts.OfType<ToolResultPart>().ToList();
        Assert.Equal(["toolu_1", "toolu_2"], synthetic.Select(r => r.CallId));
        Assert.All(synthetic, r => Assert.True(r.IsError));
        Assert.Equal("a follow-up the user typed meanwhile", Assert.IsType<TextPart>(Assert.Single(prepared[3].Parts)).Text);
    }

    [Fact]
    public void Only_the_missing_results_are_synthesised()
    {
        var history = new[]
        {
            Msg.User("q"),
            Msg.Assistant(ModelA, Msg.Call("toolu_1", "Read"), Msg.Call("toolu_2", "Read")),
            Msg.ToolResults(("toolu_1", "real", false)),
        };

        var prepared = HistoryTransform.Prepare(history, TargetA);

        var all = prepared.SelectMany(m => m.Parts.OfType<ToolResultPart>()).ToList();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, r => r.CallId == "toolu_1" && !r.IsError);
        Assert.Contains(all, r => r.CallId == "toolu_2" && r.IsError);
    }

    [Fact]
    public void A_dangling_call_at_the_end_of_history_also_gets_a_result()
    {
        var prepared = HistoryTransform.Prepare([Msg.User("q"), Msg.Assistant(ModelA, Msg.Call("toolu_1", "Read"))], TargetA);

        Assert.Equal("toolu_1", Assert.Single(prepared[^1].Parts.OfType<ToolResultPart>()).CallId);
    }

    // 情况 5:出错或中止的助手消息 → 回放时整条跳过

    [Fact]
    public void Errored_and_aborted_assistant_messages_are_skipped_and_their_orphan_results_dropped()
    {
        var history = new[]
        {
            Msg.User("q"),
            Msg.Assistant(ModelA, new TextPart("partial"), new ErrorPart(AgentError.Create(AgentErrorCode.Network, "cut"))),
            Msg.User("retry"),
            Msg.Assistant(ModelA, new TextPart("half")) with { Stop = StopReason.Aborted },
            Msg.User(new ToolResultPart("toolu_gone", new ToolOutput("orphan"), false), new TextPart("go on")),
            Msg.Assistant(ModelA, new TextPart("fine")),
        };

        var prepared = HistoryTransform.Prepare(history, TargetA);

        Assert.DoesNotContain(prepared, m => m.Parts.Any(p => p is TextPart { Text: "partial" or "half" }));
        Assert.DoesNotContain(prepared, m => m.Parts.Any(p => p is ToolResultPart));
        Assert.Contains(prepared, m => m.Parts.Any(p => p is TextPart { Text: "go on" }));
    }

    [Fact]
    public void A_result_that_belongs_to_an_older_assistant_turn_is_dropped_as_an_orphan()
    {
        var history = new[]
        {
            Msg.User("q"),
            Msg.Assistant(ModelA, Msg.Call("toolu_1", "Read")),
            Msg.ToolResults(("toolu_1", "ok", false)),
            Msg.Assistant(ModelA, new TextPart("next")),
            Msg.ToolResults(("toolu_1", "duplicate late", false)),
        };

        var prepared = HistoryTransform.Prepare(history, TargetA);

        Assert.Single(prepared.SelectMany(m => m.Parts.OfType<ToolResultPart>()));
    }

    // 情况 6:目标模型不支持图片 → 图片换成占位文本

    [Fact]
    public void Images_become_placeholder_text_when_the_target_does_not_support_them()
    {
        var history = new[]
        {
            Msg.User(new TextPart("look"), new ImagePart("image/png", "data:image/png;base64,QQ==")),
            Msg.Assistant(ModelA, Msg.Call("toolu_1", "Shot")),
            Msg.User(new ToolResultPart("toolu_1", new ToolOutput("shot", [new ToolOutputImage("image/png", "data:image/png;base64,QQ==")]), false)),
        };

        var prepared = HistoryTransform.Prepare(history, HistoryTarget.For(WireProtocol.Anthropic, "p1", "claude-a", supportsImages: false));

        Assert.DoesNotContain(prepared.SelectMany(m => m.Parts), p => p is ImagePart);
        Assert.Contains(prepared[0].Parts, p => p is TextPart { Text: HistoryTransform.ImagePlaceholder });
        var result = Assert.Single(prepared[2].Parts.OfType<ToolResultPart>());
        Assert.Null(result.Output.Images);
        Assert.Contains(HistoryTransform.ImagePlaceholder, result.Output.Content);
    }

    [Fact]
    public void Images_of_a_turn_the_upstream_rejected_are_not_replayed_but_unrelated_failures_keep_them()
    {
        // issue #200:带图的一轮被 400 拒掉后,后面的纯文字消息也会因为历史里的图片一直失败
        var rejected = new[]
        {
            Msg.User(new TextPart("what is this"), new ImagePart("image/png", "data:image/png;base64,QQ==")),
            Msg.Assistant(ModelA, new ErrorPart(AgentError.Create(AgentErrorCode.Provider, "Upstream rejected the request", false, 400))),
            Msg.User("what is this?"),
        };
        var rateLimited = new[]
        {
            Msg.User(new TextPart("what is this"), new ImagePart("image/png", "data:image/png;base64,QQ==")),
            Msg.Assistant(ModelA, new ErrorPart(AgentError.Create(AgentErrorCode.RateLimit, "slow", true, 429))),
            Msg.User("again"),
        };

        var prepared = HistoryTransform.Prepare(rejected, TargetA);
        var kept = HistoryTransform.Prepare(rateLimited, TargetA);

        Assert.DoesNotContain(prepared.SelectMany(m => m.Parts), p => p is ImagePart);
        Assert.Contains(prepared[0].Parts, p => p is TextPart { Text: HistoryTransform.RejectedImagePlaceholder });
        Assert.Contains(kept.SelectMany(m => m.Parts), p => p is ImagePart);
    }

    [Fact]
    public void Images_are_kept_when_the_target_supports_them()
    {
        var history = new[] { Msg.User(new ImagePart("image/png", "data:image/png;base64,QQ==")) };

        Assert.IsType<ImagePart>(Assert.Single(HistoryTransform.Prepare(history, TargetA)[0].Parts));
    }
}
