using Ncw.Agent.Llm.Tests.Support;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Llm.Wire;
using static Ncw.Agent.Llm.Tests.Support.Decoding;

namespace Ncw.Agent.Llm.Tests;

public static class EventDump
{
    public static string Of(LlmEvent ev) => ev switch
    {
        BlockOpaque o => $"BlockOpaque({o.Index},{o.Opaque.Protocol},{o.Opaque.Data.GetRawText()})",
        MessageEnd e => $"MessageEnd({e.Stop},{e.Usage})",
        StreamError e => $"StreamError({e.Error.Code},{e.Error.Retryable},{e.Error.Message})",
        _ => ev.ToString()!,
    };

    public static string Of(IEnumerable<LlmEvent> events) => string.Join("\n", events.Select(Of));
}

/// <summary>三种线协议的流式解码:样本按任意字节切分回放,逐事件断言(01-llm §9)。</summary>
public sealed class DecoderTests
{
    private static readonly int[] ChunkSizes = [1, 2, 3, 7, 64, 100_000];

    /// <summary>同一份样本按各种字节切分得到的事件序列必须完全一致。</summary>
    private static async Task<List<LlmEvent>> ReplayAsync(WireProtocol protocol, string sample)
    {
        var bytes = Samples.Bytes(sample);
        var baseline = await DecodeAsync(protocol, bytes, ChunkSizes[0]);
        foreach (var size in ChunkSizes.Skip(1))
        {
            Assert.Equal(EventDump.Of(baseline), EventDump.Of(await DecodeAsync(protocol, bytes, size)));
        }

        return baseline;
    }

    // ───────────────────────── Anthropic ─────────────────────────

    [Fact]
    public async Task Anthropic_sample_decodes_thinking_text_and_tool_call_into_indexed_blocks()
    {
        var events = await ReplayAsync(WireProtocol.Anthropic, "anthropic-thinking-tool.sse");

        Assert.Equal(new MessageStart("claude-sonnet-5-20260101"), events[0]);
        Assert.Equal(new ThinkingDelta(0, "用户要读文件,"), events[1]);
        Assert.Equal(new ThinkingDelta(0, "先调用 Read。"), events[2]);
        // 签名可能分多个 signature_delta 到达,累积到 content_block_stop 再发,并且带协议标签。
        var opaque = Assert.IsType<BlockOpaque>(events[3]);
        Assert.Equal(0, opaque.Index);
        Assert.Equal("anthropic", opaque.Opaque.Protocol);
        Assert.Equal("EqQBCgIYAhIkVLm9sZ", opaque.Opaque.Data.GetProperty("signature").GetString());
        Assert.Equal(new TextDelta(1, "好的,我来读取 "), events[4]);
        Assert.Equal(new TextDelta(1, "README.md。"), events[5]);
        Assert.Equal(new ToolCallStart(2, "toolu_01Read", "Read"), events[6]);
        Assert.Equal(new ToolCallDelta(2, "toolu_01Read", "{\"path\": \"READ"), events[8]);
        Assert.Equal(new ToolCallEnd(2, "toolu_01Read"), events[^2]);
        var end = Assert.IsType<MessageEnd>(events[^1]);
        Assert.Equal(StopReason.ToolUse, end.Stop);
    }

    [Fact]
    public async Task Anthropic_usage_input_and_cache_come_from_message_start_and_output_from_message_delta()
    {
        var events = await ReplayAsync(WireProtocol.Anthropic, "anthropic-thinking-tool.sse");

        var end = Assert.IsType<MessageEnd>(events[^1]);
        // 缓存写入顶层聚合值保留;1 小时那档单独给出,计价用。
        Assert.Equal(new TokenUsage(412, 87, 1000, 3000, 400), end.Usage);
    }

    [Fact]
    public async Task Anthropic_output_tokens_is_cumulative_so_it_is_assigned_not_added()
    {
        var sse = TypedFrames(
            ("message_start", """{"type":"message_start","message":{"model":"m","usage":{"input_tokens":5}}}"""),
            ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"stop_sequence"},"usage":{"output_tokens":10}}"""),
            ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":25}}"""),
            ("message_stop", """{"type":"message_stop"}"""));

        var end = Assert.IsType<MessageEnd>((await DecodeTextAsync(WireProtocol.Anthropic, sse))[^1]);

        Assert.Equal(25, end.Usage.OutputTokens);
    }

    [Fact]
    public async Task Anthropic_cache_write_total_is_derived_from_the_5m_and_1h_breakdown_when_the_aggregate_is_missing()
    {
        var sse = TypedFrames(
            ("message_start", """{"type":"message_start","message":{"model":"m","usage":{"input_tokens":1,"cache_creation":{"ephemeral_5m_input_tokens":30,"ephemeral_1h_input_tokens":70}}}}"""),
            ("message_stop", """{"type":"message_stop"}"""));

        var end = Assert.IsType<MessageEnd>((await DecodeTextAsync(WireProtocol.Anthropic, sse))[^1]);

        Assert.Equal(100, end.Usage.CacheCreationInputTokens);
        Assert.Equal(70, end.Usage.CacheCreation1hInputTokens);
    }

    [Fact]
    public async Task Anthropic_reasoning_tokens_are_a_subset_of_output_not_added_again()
    {
        var sse = TypedFrames(
            ("message_start", """{"type":"message_start","message":{"model":"m","usage":{"input_tokens":1}}}"""),
            ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":100,"thinking_tokens":40}}"""),
            ("message_stop", """{"type":"message_stop"}"""));

        var end = Assert.IsType<MessageEnd>((await DecodeTextAsync(WireProtocol.Anthropic, sse))[^1]);

        Assert.Equal(100, end.Usage.OutputTokens);
        Assert.Equal(40, end.Usage.ReasoningTokens);
    }

    [Fact]
    public async Task Anthropic_event_type_is_read_from_data_type_not_from_the_event_line()
    {
        // 中间隔一层兼容网关时 event: 行更容易被改写(或干脆不发)。
        var sse = """
            event: message
            data: {"type":"message_start","message":{"model":"m","usage":{"input_tokens":1}}}

            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hi"}}

            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}

            data: {"type":"message_stop"}


            """;

        var events = await DecodeTextAsync(WireProtocol.Anthropic, sse);

        Assert.Contains(new TextDelta(0, "hi"), events);
        Assert.IsType<MessageEnd>(events[^1]);
    }

    [Fact]
    public async Task Anthropic_tool_call_with_zero_deltas_still_has_start_and_end()
    {
        var sse = TypedFrames(
            ("message_start", """{"type":"message_start","message":{"model":"m","usage":{"input_tokens":1}}}"""),
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_1","name":"Now","input":{}}}"""),
            ("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":3}}"""),
            ("message_stop", """{"type":"message_stop"}"""));

        var events = await DecodeTextAsync(WireProtocol.Anthropic, sse);

        Assert.Equal([new ToolCallStart(0, "toolu_1", "Now"), new ToolCallEnd(0, "toolu_1")], events.Skip(1).Take(2));
    }

    [Fact]
    public async Task Anthropic_parallel_tool_calls_do_not_cross_talk()
    {
        var sse = TypedFrames(
            ("message_start", """{"type":"message_start","message":{"model":"m","usage":{"input_tokens":1}}}"""),
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_a","name":"Read"}}"""),
            ("content_block_start", """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_b","name":"Grep"}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"q\":1}"}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"p\":2}"}}"""),
            ("content_block_stop", """{"type":"content_block_stop","index":1}"""),
            ("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            ("message_stop", """{"type":"message_stop"}"""));

        var events = await DecodeTextAsync(WireProtocol.Anthropic, sse);

        Assert.Contains(new ToolCallDelta(1, "toolu_b", "{\"q\":1}"), events);
        Assert.Contains(new ToolCallDelta(0, "toolu_a", "{\"p\":2}"), events);
        Assert.Contains(new ToolCallEnd(1, "toolu_b"), events);
        Assert.Contains(new ToolCallEnd(0, "toolu_a"), events);
    }

    [Fact]
    public async Task Anthropic_input_json_delta_without_a_block_start_is_dropped_instead_of_inventing_a_call_id()
    {
        var sse = TypedFrames(
            ("message_start", """{"type":"message_start","message":{"model":"m","usage":{"input_tokens":1}}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":3,"delta":{"type":"input_json_delta","partial_json":"{}"}}"""),
            ("message_stop", """{"type":"message_stop"}"""));

        var events = await DecodeTextAsync(WireProtocol.Anthropic, sse);

        Assert.DoesNotContain(events, e => e is ToolCallDelta);
    }

    [Fact]
    public async Task Anthropic_redacted_thinking_is_passed_through_as_an_opaque_block()
    {
        var sse = TypedFrames(
            ("message_start", """{"type":"message_start","message":{"model":"m","usage":{"input_tokens":1}}}"""),
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"redacted_thinking","data":"ENCRYPTED"}}"""),
            ("message_stop", """{"type":"message_stop"}"""));

        var events = await DecodeTextAsync(WireProtocol.Anthropic, sse);

        var opaque = Assert.IsType<BlockOpaque>(events[1]);
        Assert.Equal("ENCRYPTED", opaque.Opaque.Data.GetProperty("redacted").GetString());
    }

    [Fact]
    public async Task Anthropic_thinking_block_without_a_signature_emits_no_opaque()
    {
        var sse = TypedFrames(
            ("message_start", """{"type":"message_start","message":{"model":"m","usage":{"input_tokens":1}}}"""),
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"hm"}}"""),
            ("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            ("message_stop", """{"type":"message_stop"}"""));

        var events = await DecodeTextAsync(WireProtocol.Anthropic, sse);

        Assert.DoesNotContain(events, e => e is BlockOpaque);
    }

    [Fact]
    public async Task Anthropic_malformed_frames_are_skipped_not_thrown()
    {
        var sse = "data: [DONE]\n\ndata: 42\n\ndata: {\"type\":\"future_event\"}\n\n"
            + TypedFrames(("message_start", """{"type":"message_start","message":{"model":"m","usage":{"input_tokens":1}}}"""),
                ("message_stop", """{"type":"message_stop"}"""));

        var events = await DecodeTextAsync(WireProtocol.Anthropic, sse);

        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task Anthropic_in_stream_error_is_normalised()
    {
        var sse = TypedFrames(
            ("message_start", """{"type":"message_start","message":{"model":"m","usage":{"input_tokens":1}}}"""),
            ("error", """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"""));

        var error = Assert.IsType<StreamError>((await DecodeTextAsync(WireProtocol.Anthropic, sse))[^1]);

        Assert.Equal(AgentErrorCode.Provider, error.Error.Code);
        Assert.True(error.Error.Retryable);
    }

    [Fact]
    public async Task Anthropic_stream_that_ends_without_message_stop_is_a_retryable_network_error()
    {
        var sse = TypedFrames(
            ("message_start", """{"type":"message_start","message":{"model":"m","usage":{"input_tokens":1}}}"""),
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"half"}}"""));

        var error = Assert.IsType<StreamError>((await DecodeTextAsync(WireProtocol.Anthropic, sse))[^1]);

        Assert.Equal(AgentErrorCode.Network, error.Error.Code);
        Assert.True(error.Error.Retryable);
    }

    [Fact]
    public async Task Anthropic_empty_stream_adds_no_error_of_its_own()
    {
        Assert.Empty(await DecodeTextAsync(WireProtocol.Anthropic, ""));
    }

    [Fact]
    public async Task Anthropic_unknown_stop_reason_degrades_to_end_turn()
    {
        var sse = TypedFrames(
            ("message_start", """{"type":"message_start","message":{"model":"m","usage":{"input_tokens":1}}}"""),
            ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"pause_turn"},"usage":{"output_tokens":1}}"""),
            ("message_stop", """{"type":"message_stop"}"""));

        Assert.Equal(StopReason.EndTurn, Assert.IsType<MessageEnd>((await DecodeTextAsync(WireProtocol.Anthropic, sse))[^1]).Stop);
    }

    [Fact]
    public async Task Anthropic_stop_reason_max_tokens_is_reported_and_the_cut_off_tool_call_is_closed()
    {
        var events = await ReplayAsync(WireProtocol.Anthropic, "anthropic-max-tokens.sse");

        Assert.Equal(StopReason.MaxTokens, Assert.IsType<MessageEnd>(events[^1]).Stop);
        var assembled = Assemble(events);
        var call = Assert.Single(assembled.Parts().OfType<ToolCallPart>());
        // 参数在 end 时一次性解析,失败变成工具错误而不是异常。
        Assert.NotNull(call.InputError);
        Assert.Contains("hello wor", call.InputError);
    }

    // ───────────────────────── OpenAI Chat ─────────────────────────

    [Fact]
    public async Task Chat_sample_flattens_reasoning_and_text_and_assembles_interleaved_tool_calls()
    {
        var events = await ReplayAsync(WireProtocol.OpenAiChat, "openai-chat-reasoning-tools.sse");

        Assert.Equal(new MessageStart("deepseek-v4-2026"), events[0]);
        var opaque = Assert.IsType<BlockOpaque>(events[1]);
        Assert.Equal("openai-chat", opaque.Opaque.Protocol);
        Assert.Equal("reasoning_content", opaque.Opaque.Data.GetProperty("field").GetString());
        Assert.Contains(new ThinkingDelta(0, "先读文件,"), events);
        Assert.Contains(new TextDelta(1, "我先看看这两个文件。"), events);

        var assembled = Assemble(events);
        var calls = assembled.Parts().OfType<ToolCallPart>().ToList();
        Assert.Equal(["call_a1", "call_b2"], calls.Select(c => c.CallId));
        Assert.Equal("a.md", calls[0].Input.GetProperty("path").GetString());
        Assert.Equal("b.md", calls[1].Input.GetProperty("path").GetString());
        Assert.All(calls, c => Assert.Null(c.InputError));
        Assert.Equal(StopReason.ToolUse, assembled.Stop);
    }

    [Fact]
    public async Task Chat_prompt_tokens_include_the_cache_so_input_is_prompt_minus_cached()
    {
        var events = await ReplayAsync(WireProtocol.OpenAiChat, "openai-chat-reasoning-tools.sse");

        var end = Assert.IsType<MessageEnd>(events[^1]);
        Assert.Equal(new TokenUsage(200, 60, null, 800, null, 25), end.Usage);
    }

    [Fact]
    public async Task Chat_finish_reason_length_is_max_tokens_and_the_truncated_tool_call_is_closed_and_flagged()
    {
        var events = await ReplayAsync(WireProtocol.OpenAiChat, "openai-chat-length.sse");

        var end = Assert.IsType<MessageEnd>(events[^1]);
        Assert.Equal(StopReason.MaxTokens, end.Stop);
        Assert.Contains(events, e => e is ToolCallEnd { CallId: "call_w" });
        var call = Assert.Single(Assemble(events).Parts().OfType<ToolCallPart>());
        Assert.NotNull(call.InputError);
    }

    [Fact]
    public async Task Chat_eof_without_finish_reason_is_never_a_success_even_after_done()
    {
        var events = await ReplayAsync(WireProtocol.OpenAiChat, "openai-chat-no-finish.sse");

        var error = Assert.IsType<StreamError>(events[^1]);
        Assert.Equal(AgentErrorCode.Network, error.Error.Code);
        Assert.True(error.Error.Retryable);
        Assert.DoesNotContain(events, e => e is MessageEnd);
    }

    [Fact]
    public async Task Chat_eof_without_done_and_without_finish_reason_is_also_an_error()
    {
        var sse = Frames("""{"model":"m","choices":[{"index":0,"delta":{"content":"hi"}}]}""");

        Assert.IsType<StreamError>((await DecodeTextAsync(WireProtocol.OpenAiChat, sse))[^1]);
    }

    [Fact]
    public async Task Chat_tool_call_frozen_whole_without_an_index_is_accepted()
    {
        // Google 的 OpenAI 兼容层把每个调用一次性给全,一个 index 都不带。
        var sse = Frames(
            """{"model":"gemini","choices":[{"index":0,"delta":{"tool_calls":[{"id":"call_1","type":"function","function":{"name":"Read","arguments":"{\"path\":\"a\"}"}}]}}]}""",
            """{"model":"gemini","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            "[DONE]");

        var events = await DecodeTextAsync(WireProtocol.OpenAiChat, sse);

        var end = Assert.IsType<MessageEnd>(events[^1]);
        Assert.Equal(StopReason.ToolUse, end.Stop); // finish=stop 但有工具调用
        Assert.Equal("a", Assert.Single(Assemble(events).Parts().OfType<ToolCallPart>()).Input.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Chat_parallel_calls_without_indexes_each_get_their_own_slot()
    {
        // 并进去会把两个调用串成一个:参数拼成非法 JSON,回传时还少一个 id,下一轮直接 400。
        var sse = Frames(
            """{"model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"id":"call_1","type":"function","function":{"name":"Read","arguments":"{\"p\":1}"}}]}}]}""",
            """{"model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"id":"call_2","type":"function","function":{"name":"Read","arguments":"{\"p\":2}"}}]}}]}""",
            """{"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""");

        var calls = Assemble(await DecodeTextAsync(WireProtocol.OpenAiChat, sse)).Parts().OfType<ToolCallPart>().ToList();

        Assert.Equal(["call_1", "call_2"], calls.Select(c => c.CallId));
        Assert.Equal([1, 2], calls.Select(c => c.Input.GetProperty("p").GetInt32()));
    }

    [Fact]
    public async Task Chat_stray_content_after_the_finish_reason_is_dropped()
    {
        var sse = Frames(
            """{"model":"m","choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":null}]}""",
            """{"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            """{"model":"m","choices":[{"index":0,"delta":{"content":"GHOST"}}]}""",
            "[DONE]");

        var events = await DecodeTextAsync(WireProtocol.OpenAiChat, sse);

        Assert.DoesNotContain(events, e => e is TextDelta { Text: "GHOST" });
        Assert.IsType<MessageEnd>(events[^1]);
    }

    [Fact]
    public async Task Chat_content_filter_and_refusal_map_to_refusal()
    {
        var filtered = Frames("""{"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"content_filter"}]}""", "[DONE]");
        var refused = Frames("""{"model":"m","choices":[{"index":0,"delta":{"refusal":"I cannot"}}]}""", """{"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""");

        Assert.Equal(StopReason.Refusal, Assert.IsType<MessageEnd>((await DecodeTextAsync(WireProtocol.OpenAiChat, filtered))[^1]).Stop);
        Assert.Equal(StopReason.Refusal, Assert.IsType<MessageEnd>((await DecodeTextAsync(WireProtocol.OpenAiChat, refused))[^1]).Stop);
    }

    [Fact]
    public async Task Chat_an_in_stream_error_object_ends_with_a_classified_error()
    {
        var sse = Frames("""{"error":{"message":"rate limit reached","type":"rate_limit_error","code":"rate_limit_exceeded"}}""");

        var error = Assert.IsType<StreamError>(Assert.Single(await DecodeTextAsync(WireProtocol.OpenAiChat, sse)));

        Assert.Equal(AgentErrorCode.RateLimit, error.Error.Code);
    }

    [Fact]
    public async Task Chat_a_non_streaming_json_body_with_message_instead_of_delta_is_decoded()
    {
        var json = """{"id":"c","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"whole"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1}}""";

        var events = await DecodeTextAsync(WireProtocol.OpenAiChat, Frames(json));

        Assert.Contains(new TextDelta(0, "whole"), events);
        Assert.Equal(StopReason.EndTurn, Assert.IsType<MessageEnd>(events[^1]).Stop);
    }

    // ───────────────────────── OpenAI Responses ─────────────────────────

    [Fact]
    public async Task Responses_sample_decodes_reasoning_summary_text_and_function_call_with_namespaced_indexes()
    {
        var events = await ReplayAsync(WireProtocol.OpenAiResponses, "openai-responses-reasoning-tool.sse");

        Assert.Equal(new MessageStart("gpt-5-2026"), events[0]);
        Assert.Equal(new ThinkingDelta(0, "检查参数。"), events[1]);
        var opaque = Assert.IsType<BlockOpaque>(events[2]);
        Assert.Equal(0, opaque.Index); // 和思考文字落在同一个下标上,才会被合进同一个思考块
        Assert.Equal("openai-responses", opaque.Opaque.Protocol);
        Assert.Equal("gAAAAABencrypted", opaque.Opaque.Data.GetProperty("item").GetProperty("encrypted_content").GetString());
        Assert.Equal(new TextDelta(1_000_000, "我来读文件。"), events[3]);
        Assert.Equal(new ToolCallStart(2_000_000, "call_r1", "Read"), events[4]);
        Assert.Equal(new ToolCallDelta(2_000_000, "call_r1", "{\"path\":"), events[5]);
        Assert.Equal(new ToolCallEnd(2_000_000, "call_r1"), events[^2]);
        var end = Assert.IsType<MessageEnd>(events[^1]);
        Assert.Equal(StopReason.ToolUse, end.Stop);
        // input_tokens 含缓存:500 - 300;reasoning 是 output 的子集。
        Assert.Equal(new TokenUsage(200, 70, null, 300, null, 20), end.Usage);
    }

    [Fact]
    public async Task Responses_the_final_snapshot_does_not_duplicate_what_was_already_streamed()
    {
        var events = await ReplayAsync(WireProtocol.OpenAiResponses, "openai-responses-reasoning-tool.sse");

        Assert.Single(events.OfType<ThinkingDelta>());
        Assert.Single(events.OfType<ToolCallStart>());
        Assert.Single(events.OfType<BlockOpaque>());
        var assembled = Assemble(events);
        Assert.Equal("a.md", Assert.Single(assembled.Parts().OfType<ToolCallPart>()).Input.GetProperty("path").GetString());
        Assert.Equal("我来读文件。", Assert.Single(assembled.Parts().OfType<TextPart>()).Text);
    }

    [Fact]
    public async Task Responses_incomplete_with_max_output_tokens_is_stop_reason_length()
    {
        var events = await ReplayAsync(WireProtocol.OpenAiResponses, "openai-responses-incomplete.sse");

        var end = Assert.IsType<MessageEnd>(events[^1]);
        Assert.Equal(StopReason.MaxTokens, end.Stop);
        Assert.Contains(new TextDelta(0, "被截断的回答"), events);
    }

    [Fact]
    public async Task Responses_stream_without_a_terminal_response_is_an_interrupted_error()
    {
        var sse = TypedFrames(
            ("response.created", """{"type":"response.created","response":{"object":"response","model":"m","status":"in_progress"}}"""),
            ("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"id":"msg","type":"message","content":[]}}"""),
            ("response.output_text.delta", """{"type":"response.output_text.delta","item_id":"msg","output_index":0,"content_index":0,"delta":"hi"}"""));

        var error = Assert.IsType<StreamError>((await DecodeTextAsync(WireProtocol.OpenAiResponses, sse))[^1]);

        Assert.Equal(AgentErrorCode.Network, error.Error.Code);
        Assert.True(error.Error.Retryable);
    }

    [Fact]
    public async Task Responses_item_identity_follows_id_so_a_reused_output_index_is_a_new_item_not_a_protocol_error()
    {
        // 网关重用 output_index 或在终局 output 里重新生成 id 都很常见:内容一条不丢,好过整轮归零。
        var sse = TypedFrames(
            ("response.created", """{"type":"response.created","response":{"object":"response","model":"m","status":"in_progress"}}"""),
            ("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"id":"fc_a","type":"function_call","call_id":"call_a","name":"Read","arguments":""}}"""),
            ("response.function_call_arguments.done", """{"type":"response.function_call_arguments.done","item_id":"fc_a","output_index":0,"arguments":"{\"p\":1}"}"""),
            ("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"id":"fc_b","type":"function_call","call_id":"call_b","name":"Read","arguments":""}}"""),
            ("response.function_call_arguments.done", """{"type":"response.function_call_arguments.done","item_id":"fc_b","output_index":0,"arguments":"{\"p\":2}"}"""),
            ("response.completed", """{"type":"response.completed","response":{"object":"response","model":"m","status":"completed","output":[],"usage":{"input_tokens":1,"output_tokens":1}}}"""));

        var calls = Assemble(await DecodeTextAsync(WireProtocol.OpenAiResponses, sse)).Parts().OfType<ToolCallPart>().ToList();

        Assert.Equal(["call_a", "call_b"], calls.Select(c => c.CallId));
        Assert.Equal([1, 2], calls.Select(c => c.Input.GetProperty("p").GetInt32()));
    }

    [Fact]
    public async Task Responses_a_reserialised_arguments_snapshot_is_accepted_but_a_different_one_is_an_error()
    {
        string Stream(string doneArguments) => TypedFrames(
            ("response.created", """{"type":"response.created","response":{"object":"response","model":"m","status":"in_progress"}}"""),
            ("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"id":"fc","type":"function_call","call_id":"c","name":"Read","arguments":""}}"""),
            ("response.function_call_arguments.delta", """{"type":"response.function_call_arguments.delta","item_id":"fc","output_index":0,"delta":"{\"p\":1}"}"""),
            ("response.function_call_arguments.done", "{\"type\":\"response.function_call_arguments.done\",\"item_id\":\"fc\",\"output_index\":0,\"arguments\":" + System.Text.Json.Nodes.JsonValue.Create(doneArguments)!.ToJsonString() + "}"),
            ("response.completed", """{"type":"response.completed","response":{"object":"response","model":"m","status":"completed","output":[],"usage":{"input_tokens":1,"output_tokens":1}}}"""));

        var same = await DecodeTextAsync(WireProtocol.OpenAiResponses, Stream("{ \"p\" : 1 }"));
        var different = await DecodeTextAsync(WireProtocol.OpenAiResponses, Stream("{\"p\":2}"));

        Assert.IsType<MessageEnd>(same[^1]);
        // 工具参数会被原样交给工具去执行,快照和流式内容语义不一致时宁可报错。
        Assert.Equal(AgentErrorCode.Provider, Assert.IsType<StreamError>(different[^1]).Error.Code);
    }

    [Fact]
    public async Task Responses_an_output_item_that_needs_a_client_side_answer_is_rejected_loudly()
    {
        var sse = TypedFrames(
            ("response.created", """{"type":"response.created","response":{"object":"response","model":"m","status":"in_progress"}}"""),
            ("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"id":"x","type":"local_shell_call"}}"""));

        var error = Assert.IsType<StreamError>((await DecodeTextAsync(WireProtocol.OpenAiResponses, sse))[^1]);

        Assert.Contains("local_shell_call", error.Error.Message);
        Assert.False(error.Error.Retryable);
    }

    [Fact]
    public async Task Responses_hosted_tool_items_are_ignored()
    {
        var sse = TypedFrames(
            ("response.created", """{"type":"response.created","response":{"object":"response","model":"m","status":"in_progress"}}"""),
            ("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"id":"ws","type":"web_search_call"}}"""),
            ("response.completed", """{"type":"response.completed","response":{"object":"response","model":"m","status":"completed","output":[],"usage":{"input_tokens":1,"output_tokens":1}}}"""));

        Assert.IsType<MessageEnd>((await DecodeTextAsync(WireProtocol.OpenAiResponses, sse))[^1]);
    }

    [Fact]
    public async Task Responses_reasoning_content_wins_over_the_summary_and_only_one_opaque_is_sent()
    {
        var sse = TypedFrames(
            ("response.created", """{"type":"response.created","response":{"object":"response","model":"m","status":"in_progress"}}"""),
            ("response.output_item.done", """{"type":"response.output_item.done","output_index":0,"item":{"id":"rs","type":"reasoning","summary":[{"type":"summary_text","text":"摘要"}],"content":[{"type":"reasoning_text","text":"正文"}],"encrypted_content":"enc"}}"""),
            ("response.completed", """{"type":"response.completed","response":{"object":"response","model":"m","status":"completed","output":[],"usage":{"input_tokens":1,"output_tokens":1}}}"""));

        var events = await DecodeTextAsync(WireProtocol.OpenAiResponses, sse);

        Assert.Equal("正文", Assert.Single(events.OfType<ThinkingDelta>()).Text);
        Assert.Equal(500_000, Assert.Single(events.OfType<BlockOpaque>()).Index); // 内容 lane,和文字段同一个下标
    }

    [Fact]
    public async Task Responses_failed_response_is_a_classified_error()
    {
        var sse = TypedFrames(("response.failed", """{"type":"response.failed","response":{"object":"response","status":"failed","error":{"code":"server_error","message":"boom"}}}"""));

        var error = Assert.IsType<StreamError>((await DecodeTextAsync(WireProtocol.OpenAiResponses, sse))[^1]);

        Assert.Equal("boom", error.Error.Message);
        Assert.True(error.Error.Retryable);
    }

    // ───────────────────────── 错误分类 ─────────────────────────

    [Fact]
    public void Anthropic_http_errors_are_classified()
    {
        Assert.Equal((AgentErrorCode.Auth, false), Pair(AnthropicErrors.FromHttp(401, """{"error":{"type":"authentication_error","message":"bad key"}}""")));
        Assert.Equal((AgentErrorCode.Auth, false), Pair(AnthropicErrors.FromHttp(403, "{}")));
        Assert.Equal((AgentErrorCode.RateLimit, true), Pair(AnthropicErrors.FromHttp(429, """{"error":{"type":"rate_limit_error","message":"slow down"}}""")));
        Assert.Equal((AgentErrorCode.ContextLength, false), Pair(AnthropicErrors.FromHttp(400, """{"error":{"type":"invalid_request_error","message":"prompt is too long: 250000 tokens > 200000"}}""")));
        Assert.Equal((AgentErrorCode.Provider, false), Pair(AnthropicErrors.FromHttp(400, """{"error":{"type":"invalid_request_error","message":"bad field"}}""")));
        Assert.Equal((AgentErrorCode.Provider, true), Pair(AnthropicErrors.FromHttp(529, """{"error":{"type":"overloaded_error","message":"Overloaded"}}""")));
        Assert.Equal((AgentErrorCode.Provider, true), Pair(AnthropicErrors.FromHttp(502, "<html>Bad gateway</html>")));
    }

    [Fact]
    public void Anthropic_cache_rejections_are_cache_unsupported_whatever_shape_the_relay_uses()
    {
        var official = AnthropicErrors.FromHttp(400, """{"error":{"type":"invalid_request_error","message":"cache_control is not supported"}}""", AnthropicCacheTtl.OneHour, "Relay");
        var stripped = AnthropicErrors.FromHttp(400, """{"type":"invalid_request_error","message":"Unknown field: cache_control"}""");
        var plainText = AnthropicErrors.FromHttp(400, "cache_control is not supported by this endpoint");
        var typeOnly = AnthropicErrors.FromHttp(422, """{"error":{"type":"cache_control_not_supported"}}""");

        Assert.All([official, stripped, plainText, typeOnly], e =>
        {
            Assert.Equal(AgentErrorCode.CacheUnsupported, e.Code);
            Assert.False(e.Retryable);
        });
        Assert.Equal(LlmErrorKeys.CacheUnsupported, official.MessageKey);
        Assert.Equal("1h", official.MessageParams!["ttl"]);
        Assert.Equal(LlmErrorKeys.CacheUnsupportedUnnamed, stripped.MessageKey);
    }

    [Fact]
    public void Request_echoes_in_error_bodies_are_not_evidence_for_cache_classification()
    {
        // 校验器把用户的请求原样回显时,里面的 cache_control 不该触发缓存误判(否则普通 400 被当配置错误,不再切换供应商)。
        var echo = AnthropicErrors.FromHttp(400, """{"error":{"type":"invalid_request_error","message":"messages.0: bad role"},"request":{"cache_control":{"type":"ephemeral"}},"details":{"messages":[{"cache_control":1}]}}""");

        Assert.Equal(AgentErrorCode.Provider, echo.Code);
        Assert.DoesNotContain("ephemeral", echo.Message);
    }

    [Fact]
    public void OpenAi_http_errors_are_classified_including_fastapi_style_bodies()
    {
        Assert.Equal((AgentErrorCode.Auth, false), Pair(OpenAiCommon.ToAgentError(401, """{"error":{"message":"Incorrect API key","code":"invalid_api_key"}}""")));
        Assert.Equal((AgentErrorCode.RateLimit, true), Pair(OpenAiCommon.ToAgentError(429, """{"error":{"message":"slow","code":"rate_limit_exceeded"}}""")));
        Assert.Equal((AgentErrorCode.Provider, false), Pair(OpenAiCommon.ToAgentError(429, """{"error":{"message":"You exceeded your quota","code":"insufficient_quota"}}""")));
        Assert.Equal((AgentErrorCode.ContextLength, false), Pair(OpenAiCommon.ToAgentError(400, """{"error":{"message":"This model's maximum context length is 128000 tokens","code":"context_length_exceeded"}}""")));
        Assert.Equal((AgentErrorCode.Provider, true), Pair(OpenAiCommon.ToAgentError(503, """{"error":{"message":"overloaded"}}""")));
        Assert.Equal((AgentErrorCode.Provider, false), Pair(OpenAiCommon.ToAgentError(400, """{"detail":"Unsupported parameter"}""")));
        Assert.Equal("Unsupported parameter", OpenAiCommon.ToAgentError(400, """{"detail":"Unsupported parameter"}""").Message);
    }

    [Fact]
    public void A_generic_gateway_message_keeps_the_real_cause_from_the_rest_of_the_error_object()
    {
        // issue #200:聚合网关把真正的原因放在 message 以外
        var error = OpenAiCommon.ToAgentError(400, """{"error":{"message":"Upstream rejected the request","code":400,"metadata":{"raw":"image format webp is not supported"}}}""");

        Assert.StartsWith("Upstream rejected the request (metadata: ", error.Message);
        Assert.Contains("image format webp is not supported", error.Message);
        Assert.Equal("slow", OpenAiCommon.ToAgentError(429, """{"error":{"message":"slow","type":"rate_limit","code":"x"}}""").Message);
    }

    [Fact]
    public void Connection_failures_carry_the_inner_exception_cause()
    {
        var e = new HttpRequestException("An error occurred while sending the request.", new IOException("The SSL connection could not be established."));

        Assert.Equal("An error occurred while sending the request. → The SSL connection could not be established.", LlmRouter.ExceptionChain(e));
    }

    [Fact]
    public void OpenAi_unrecognised_error_bodies_are_handed_over_raw_rather_than_a_generic_sentence()
    {
        var error = OpenAiCommon.ToAgentError(400, """{"weird":{"shape":1}}""");

        Assert.Contains("weird", error.Message);
        Assert.Equal("Upstream request failed (HTTP 400)", OpenAiCommon.ToAgentError(400, "").Message);
    }

    private static (AgentErrorCode, bool) Pair(AgentError e) => (e.Code, e.Retryable);
}
