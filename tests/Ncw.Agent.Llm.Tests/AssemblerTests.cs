using Ncw.Agent.Llm.Json;
using Ncw.Agent.Llm.Tests.Support;

namespace Ncw.Agent.Llm.Tests;

/// <summary>事件流折成最终助手消息(2.x BlockAccumulator / ToolCallAccumulator)。</summary>
public sealed class AssemblerTests
{
    private static StreamAssembler Fold(params LlmEvent[] events) => Decoding.Assemble(events);

    [Fact]
    public void Interleaved_blocks_are_placed_by_index_not_by_arrival_order()
    {
        var assembled = Fold(
            new MessageStart("m"),
            new TextDelta(2, "second text "),
            new ThinkingDelta(0, "think"),
            new TextDelta(1, "first "),
            new TextDelta(2, "more"),
            new MessageEnd(StopReason.EndTurn, new TokenUsage(1, 2)));

        var parts = assembled.Parts();

        Assert.Equal(3, parts.Count);
        Assert.Equal("think", Assert.IsType<ThinkingPart>(parts[0]).Text);
        Assert.Equal("first ", Assert.IsType<TextPart>(parts[1]).Text);
        Assert.Equal("second text more", Assert.IsType<TextPart>(parts[2]).Text);
    }

    /// <summary>手动拨的时钟:时间戳就是毫秒数。</summary>
    private sealed class ManualClock : TimeProvider
    {
        public long Now { get; set; }

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Now;
    }

    private static StreamAssembler FoldAt(ManualClock clock, params (long At, LlmEvent Event)[] events)
    {
        var assembler = new StreamAssembler(clock);
        foreach (var (at, ev) in events)
        {
            clock.Now = at;
            assembler.Apply(ev);
        }

        return assembler;
    }

    [Fact]
    public void A_thinking_block_lasts_from_its_first_delta_until_other_content_starts()
    {
        var clock = new ManualClock();
        var parts = FoldAt(clock,
            (1_000, new MessageStart("m")),
            (1_200, new ThinkingDelta(0, "let me ")),
            (2_500, new ThinkingDelta(0, "think")),
            (4_200, new TextDelta(1, "answer")),
            (9_000, new MessageEnd(StopReason.EndTurn, new TokenUsage(1, 2)))).Parts();

        Assert.Equal(3_000, Assert.IsType<ThinkingPart>(parts[0]).DurationMs);
    }

    [Fact]
    public void A_thinking_block_followed_by_a_tool_call_or_the_end_is_closed_there()
    {
        var clock = new ManualClock();
        var beforeTool = FoldAt(clock,
            (0, new ThinkingDelta(0, "plan")),
            (700, new ToolCallStart(1, "c1", "Bash")),
            (5_000, new ToolCallEnd(1, "c1"))).Parts();
        Assert.Equal(700, Assert.IsType<ThinkingPart>(beforeTool[0]).DurationMs);

        var thinkingOnly = FoldAt(new ManualClock(),
            (100, new ThinkingDelta(0, "a")),
            (1_600, new ThinkingDelta(0, "b")),
            (2_000, new MessageEnd(StopReason.EndTurn, new TokenUsage(1, 2)))).Parts();
        Assert.Equal(1_900, Assert.IsType<ThinkingPart>(Assert.Single(thinkingOnly)).DurationMs);
    }

    [Fact]
    public void A_thinking_block_without_measurable_time_has_no_duration_instead_of_zero()
    {
        // 时钟没走(假时钟)/ 只有透传载体(redacted):不写一个假的 0,界面就不显示「思考了 0 秒」
        var frozen = FoldAt(new ManualClock(), (5, new ThinkingDelta(0, "t")), (5, new TextDelta(1, "x"))).Parts();
        Assert.Null(Assert.IsType<ThinkingPart>(frozen[0]).DurationMs);

        var redacted = FoldAt(new ManualClock(), (0, new BlockOpaque(0, Msg.Opaque("anthropic", """{"redacted":"X"}"""))), (900, new TextDelta(1, "x"))).Parts();
        Assert.Null(Assert.IsType<ThinkingPart>(redacted[0]).DurationMs);
    }

    [Fact]
    public void An_opaque_with_the_same_index_merges_into_the_thinking_block()
    {
        var opaque = Msg.Opaque("anthropic", """{"signature":"S"}""");

        var parts = Fold(new ThinkingDelta(0, "t"), new BlockOpaque(0, opaque)).Parts();

        var thinking = Assert.IsType<ThinkingPart>(Assert.Single(parts));
        Assert.Equal("t", thinking.Text);
        Assert.Same(opaque, thinking.Opaque);
    }

    [Fact]
    public void A_redacted_block_with_no_text_still_becomes_a_thinking_part_so_it_can_be_replayed()
    {
        var parts = Fold(new BlockOpaque(0, Msg.Opaque("anthropic", """{"redacted":"X"}"""))).Parts();

        Assert.Equal("", Assert.IsType<ThinkingPart>(Assert.Single(parts)).Text);
    }

    [Fact]
    public void Tool_call_arguments_are_parsed_once_at_end_and_an_empty_argument_string_is_an_empty_object()
    {
        var parts = Fold(
            new ToolCallStart(0, "c1", "Now"),
            new ToolCallEnd(0, "c1"),
            new ToolCallStart(1, "c2", "Read"),
            new ToolCallDelta(1, "c2", "{\"pa"),
            new ToolCallDelta(1, "c2", "th\":\"x\"}"),
            new ToolCallEnd(1, "c2")).Parts().OfType<ToolCallPart>().ToList();

        Assert.Equal("{}", parts[0].Input.GetRawText());
        Assert.Equal("x", parts[1].Input.GetProperty("path").GetString());
        Assert.All(parts, p => Assert.Null(p.InputError));
    }

    [Fact]
    public void Invalid_tool_arguments_become_a_tool_error_not_an_exception()
    {
        var call = Assert.Single(Fold(
            new ToolCallStart(0, "c1", "Write"),
            new ToolCallDelta(0, "c1", "{\"content\":\"unterminated"),
            new ToolCallEnd(0, "c1")).Parts().OfType<ToolCallPart>());

        Assert.NotNull(call.InputError);
        Assert.Contains("unterminated", call.InputError);
        Assert.Equal("{}", call.Input.GetRawText()); // 回放编码时发 {},不发坏 JSON
    }

    [Fact]
    public void A_tool_call_that_never_ended_is_flagged_and_reported_as_pending()
    {
        var assembler = Fold(new ToolCallStart(0, "c1", "Write"), new ToolCallDelta(0, "c1", "{\"a\":"));

        Assert.Equal([("c1", "Write")], assembler.PendingToolCalls);
        Assert.NotNull(Assert.Single(assembler.Parts().OfType<ToolCallPart>()).InputError);
    }

    [Fact]
    public void An_error_keeps_the_content_and_usage_received_so_far()
    {
        var assembler = Fold(
            new MessageStart("m", "p1", "anthropic", "claude-a"),
            new TextDelta(0, "partial answer"),
            new StreamError(AgentError.Create(AgentErrorCode.Network, "cut")));

        var message = assembler.ToMessage(new MessageId("m1"), DateTimeOffset.UnixEpoch);

        Assert.Equal("partial answer", Assert.IsType<TextPart>(message.Parts[0]).Text);
        Assert.IsType<ErrorPart>(message.Parts[1]);
        Assert.Equal(new Provenance("anthropic", "p1", "claude-a"), message.Source);
        Assert.Null(message.Stop);
    }

    [Fact]
    public void Cancellation_marks_the_message_aborted_without_an_error_part()
    {
        var message = Fold(new TextDelta(0, "x"), new StreamError(AgentError.Create(AgentErrorCode.Aborted, "Aborted", false)))
            .ToMessage(new MessageId("m1"), DateTimeOffset.UnixEpoch);

        Assert.Equal(StopReason.Aborted, message.Stop);
        Assert.DoesNotContain(message.Parts, p => p is ErrorPart);
    }

    [Fact]
    public void Message_end_supplies_stop_usage_cost_and_latency()
    {
        var assembler = Fold(new MessageEnd(StopReason.MaxTokens, new TokenUsage(3, 4), 250, CostInfo.Of(99, Currency.Cny)));

        Assert.True(assembler.Completed);
        Assert.Equal(StopReason.MaxTokens, assembler.Stop);
        Assert.Equal(new TokenUsage(3, 4), assembler.Usage);
        Assert.Equal(CostInfo.Of(99, Currency.Cny), assembler.Cost);
        Assert.Equal(250, assembler.LatencyMs);
    }

    [Fact]
    public void A_retry_after_message_start_does_not_duplicate_content()
    {
        // 重试只发生在首个内容字节之前:MessageStart 重复发出,但没有内容被重复。
        var parts = Fold(
            new MessageStart("m"), new ProviderRetry(1, 500, "boom"),
            new MessageStart("m", "p2"), new TextDelta(0, "once"),
            new MessageEnd(StopReason.EndTurn, new TokenUsage())).Parts();

        Assert.Equal("once", Assert.IsType<TextPart>(Assert.Single(parts)).Text);
    }

    [Fact]
    public void Tool_names_are_validated_against_the_wire_pattern()
    {
        Assert.True(ToolInfo.IsValidName("Read_file-2"));
        Assert.True(ToolInfo.IsValidName(new string('a', 64)));
        Assert.False(ToolInfo.IsValidName(new string('a', 65)));
        Assert.False(ToolInfo.IsValidName(""));
        Assert.False(ToolInfo.IsValidName("mcp.server:tool"));
        Assert.False(ToolInfo.IsValidName("读文件"));
        Assert.Equal("{}", Jsn.EmptyObject.GetRawText());
    }
}
