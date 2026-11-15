using System.Text.Json.Nodes;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Llm.Tests.Support;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Llm.Tests;

/// <summary>三种协议的请求编码(纯函数)。每条对应 01-llm §3 表里的一个 ★ 不变量。</summary>
public sealed class EncoderTests
{
    private static readonly EncodeOptions Options = new("ws_1");

    private static JsonObject AnthropicBody(LlmRequest request, EncodeOptions? options = null) =>
        AnthropicEncoder.Encode(request, "claude-x", options ?? Options).Body;

    private static string Pretty(JsonNode? node) => node?.ToJsonString() ?? "null";

    // ───────────────────────── Anthropic ─────────────────────────

    [Fact]
    public void Anthropic_drops_empty_text_blocks_and_messages_that_end_up_empty()
    {
        var request = Msg.Request([
            Msg.User(new TextPart("")),
            Msg.User("hello"),
            Msg.Assistant(new TextPart("")),
            Msg.User("again"),
        ]);

        var messages = (JsonArray)AnthropicBody(request)["messages"]!;

        // 空 text 块是 400:整条空消息跳过;相邻 user 合并。
        var only = Assert.Single(messages)!;
        Assert.Equal("user", only["role"]!.GetValue<string>());
        Assert.Equal(["hello", "again"], ((JsonArray)only["content"]!).Select(b => b!["text"]!.GetValue<string>()));
    }

    [Fact]
    public void Anthropic_drops_a_thinking_block_that_has_no_signature_but_keeps_a_signed_one_verbatim()
    {
        var request = Msg.Request([
            Msg.User("q"),
            Msg.Assistant(
                new ThinkingPart("unsigned", Msg.Opaque("openai-chat", """{"field":"reasoning_content"}""")),
                new ThinkingPart("no opaque at all"),
                new ThinkingPart("signed", Msg.Opaque("anthropic", """{"signature":"SIG"}""")),
                new ThinkingPart("", Msg.Opaque("anthropic", """{"redacted":"ENC"}""")),
                new TextPart("answer")),
        ]);

        var content = (JsonArray)((JsonArray)AnthropicBody(request)["messages"]!)[1]!["content"]!;

        Assert.Equal(["thinking", "redacted_thinking", "text"], content.Select(b => b!["type"]!.GetValue<string>()));
        Assert.Equal("SIG", content[0]!["signature"]!.GetValue<string>());
        Assert.Equal("ENC", content[1]!["data"]!.GetValue<string>());
    }

    [Fact]
    public void Anthropic_merges_adjacent_same_role_messages_so_roles_strictly_alternate()
    {
        var request = Msg.Request([
            Msg.User("a"),
            Msg.Assistant(Msg.Call("toolu_1", "Read"), Msg.Call("toolu_2", "Read")),
            Msg.ToolResults(("toolu_1", "r1", false)),
            Msg.ToolResults(("toolu_2", "r2", true)),
            Msg.Assistant(new TextPart("done")),
        ]);

        var messages = (JsonArray)AnthropicBody(request)["messages"]!;

        Assert.Equal(["user", "assistant", "user", "assistant"], messages.Select(m => m!["role"]!.GetValue<string>()));
        var results = (JsonArray)messages[2]!["content"]!;
        Assert.Equal(["toolu_1", "toolu_2"], results.Select(b => b!["tool_use_id"]!.GetValue<string>()));
        Assert.True(results[1]!["is_error"]!.GetValue<bool>());
    }

    [Fact]
    public void Anthropic_tool_result_with_images_becomes_a_text_plus_image_array_and_ui_only_parts_are_dropped()
    {
        var request = Msg.Request([
            Msg.User("q"),
            Msg.Assistant(Msg.Call("toolu_1", "Shot")),
            Msg.User(
                new ToolResultPart("toolu_1", new ToolOutput("see image", [new ToolOutputImage("image/png", "data:image/png;base64,QUJD")], Details: Jsn.Parse("""{"card":1}""")), false),
                new ErrorPart(AgentError.Create(AgentErrorCode.Provider, "ui only")),
                new GoalStatusPart(true, "goal"),
                new CompactBoundaryPart("auto", 1, 1, "summary"),
                new TextPart("next")),
        ]);

        var content = (JsonArray)((JsonArray)AnthropicBody(request)["messages"]!)[2]!["content"]!;

        Assert.Equal(2, content.Count);
        var inner = (JsonArray)content[0]!["content"]!;
        Assert.Equal("text", inner[0]!["type"]!.GetValue<string>());
        Assert.Equal("QUJD", inner[1]!["source"]!["data"]!.GetValue<string>());
        Assert.DoesNotContain("card", Pretty(content));
    }

    [Fact]
    public void Anthropic_always_enables_prompt_caching_with_the_breakpoint_on_the_system_prompt_and_5m_writes_no_ttl()
    {
        var body = AnthropicBody(Msg.Request("hi", "SYS", [Msg.Tool()]));

        Assert.Equal("""{"type":"ephemeral"}""", Pretty(body["cache_control"]));
        var system = (JsonArray)body["system"]!;
        Assert.Equal("SYS", system[0]!["text"]!.GetValue<string>());
        Assert.Equal("""{"type":"ephemeral"}""", Pretty(system[0]!["cache_control"]));
        Assert.Null(((JsonArray)body["tools"]!)[0]!["cache_control"]); // 断点只放在一个地方
    }

    [Fact]
    public void Anthropic_one_hour_ttl_is_explicit_on_both_the_top_level_marker_and_the_breakpoint()
    {
        var body = AnthropicBody(Msg.Request("hi", "SYS"), new EncodeOptions("ws_1", AnthropicCacheTtl.OneHour));

        Assert.Equal("""{"type":"ephemeral","ttl":"1h"}""", Pretty(body["cache_control"]));
        Assert.Equal("""{"type":"ephemeral","ttl":"1h"}""", Pretty(((JsonArray)body["system"]!)[0]!["cache_control"]));
    }

    [Fact]
    public void Anthropic_without_a_system_prompt_the_breakpoint_goes_on_the_last_tool()
    {
        var body = AnthropicBody(Msg.Request("hi", "", [Msg.Tool("A"), Msg.Tool("B")]));

        Assert.Null(body["system"]);
        var tools = (JsonArray)body["tools"]!;
        Assert.Null(tools[0]!["cache_control"]);
        Assert.NotNull(tools[1]!["cache_control"]);
        Assert.NotNull(body["cache_control"]);
    }

    [Fact]
    public void Anthropic_metadata_user_id_is_the_workspace_id()
    {
        Assert.Equal("ws_1", AnthropicBody(Msg.Request("hi"))["metadata"]!["user_id"]!.GetValue<string>());
    }

    [Fact]
    public void Anthropic_policy_reapplied_after_a_patch_restores_identity_ttl_and_breakpoints()
    {
        var body = AnthropicBody(Msg.Request("hi", "SYS", [Msg.Tool()]));
        // 模拟一个宽松的模型补丁:换身份、删缓存、塞一个假断点。
        body["metadata"] = new JsonObject { ["user_id"] = "someone-else", ["extra"] = "kept" };
        body.Remove("cache_control");
        ((JsonObject)((JsonArray)body["system"]!)[0]!).Remove("cache_control");
        ((JsonObject)((JsonArray)body["tools"]!)[0]!)["cache_control"] = new JsonObject { ["type"] = "ephemeral", ["ttl"] = "1h" };

        var fixedBody = AnthropicRequestPolicy.Apply(body, "ws_1", AnthropicCacheTtl.FiveMinutes);

        Assert.Equal("ws_1", fixedBody["metadata"]!["user_id"]!.GetValue<string>());
        Assert.Equal("kept", fixedBody["metadata"]!["extra"]!.GetValue<string>()); // 无害的中转专用键保留
        Assert.Equal("""{"type":"ephemeral"}""", Pretty(fixedBody["cache_control"]));
        Assert.Equal("""{"type":"ephemeral"}""", Pretty(((JsonArray)fixedBody["system"]!)[0]!["cache_control"]));
        Assert.Null(((JsonArray)fixedBody["tools"]!)[0]!["cache_control"]);
    }

    [Fact]
    public void Anthropic_uses_the_external_tool_name_and_sends_stop_sequences_and_temperature()
    {
        var request = Msg.Request("hi", tools: [Msg.Tool("Read")]) with
        {
            Options = new GenerationOptions(100, Temperature: 0.3, StopSequences: ["END"]),
        };

        var body = AnthropicBody(request);

        Assert.Equal("Read", ((JsonArray)body["tools"]!)[0]!["name"]!.GetValue<string>());
        Assert.Equal("object", ((JsonArray)body["tools"]!)[0]!["input_schema"]!["type"]!.GetValue<string>());
        Assert.Equal("END", ((JsonArray)body["stop_sequences"]!)[0]!.GetValue<string>());
        Assert.Equal(0.3, body["temperature"]!.GetValue<double>());
    }

    [Fact]
    public void Anthropic_request_headers_carry_the_version_but_never_a_credential()
    {
        var encoded = AnthropicEncoder.Encode(Msg.Request("hi"), "claude-x", Options);

        Assert.Equal("2023-06-01", encoded.Headers["anthropic-version"]);
        Assert.DoesNotContain(encoded.Headers.Keys, k => k is "x-api-key" or "authorization");
        Assert.Equal("/v1/messages", encoded.Path);
    }

    // ───────────────────────── OpenAI Chat ─────────────────────────

    [Fact]
    public void Chat_tool_results_become_tool_messages_and_their_images_move_to_a_following_user_message()
    {
        var request = Msg.Request([
            Msg.User("q"),
            Msg.Assistant(Msg.Call("call_1", "Shot"), Msg.Call("call_2", "Shot")),
            Msg.User(
                new ToolResultPart("call_1", new ToolOutput("one", [new ToolOutputImage("image/png", "data:image/png;base64,QQ==")]), false),
                new ToolResultPart("call_2", new ToolOutput("two"), false),
                new TextPart("reminder")),
        ]);

        var messages = (JsonArray)OpenAiChatEncoder.Encode(request, "gpt-4o").Body["messages"]!;

        var roles = messages.Select(m => m!["role"]!.GetValue<string>()).ToList();
        Assert.Equal(["system", "user", "assistant", "tool", "tool", "user"], roles);
        Assert.Equal("one", messages[3]!["content"]!.GetValue<string>()); // tool 消息的 content 是纯字符串
        var user = (JsonArray)messages[5]!["content"]!;
        Assert.Equal(["image_url", "text"], user.Select(p => p!["type"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("gpt-5", true)]
    [InlineData("gpt-5.2-codex", true)]
    [InlineData("openai/gpt-6-mini", true)]
    [InlineData("o3", true)]
    [InlineData("o4-mini", true)]
    [InlineData("gpt-4o", false)]
    [InlineData("deepseek-chat", false)]
    [InlineData("glm-4.6", false)]
    [InlineData("gpt-50-turbo-fake", true)]
    public void Chat_o_series_and_gpt5_use_max_completion_tokens_others_use_max_tokens(string model, bool modern)
    {
        var body = OpenAiChatEncoder.Encode(Msg.Request("hi"), model).Body;

        Assert.Equal(modern, body.ContainsKey("max_completion_tokens"));
        Assert.Equal(!modern, body.ContainsKey("max_tokens"));
        Assert.True(body["stream_options"]!["include_usage"]!.GetValue<bool>());
    }

    [Fact]
    public void Chat_replays_reasoning_content_only_from_its_own_protocol()
    {
        var request = Msg.Request([
            Msg.User("q"),
            Msg.Assistant(
                new ThinkingPart("chat thought", Msg.Opaque("openai-chat", """{"field":"reasoning_content"}""")),
                new ThinkingPart("anthropic thought", Msg.Opaque("anthropic", """{"signature":"S"}""")),
                new TextPart("a")),
        ]);

        var assistant = ((JsonArray)OpenAiChatEncoder.Encode(request, "deepseek-chat").Body["messages"]!)[2]!;

        Assert.Equal("chat thought", assistant["reasoning_content"]!.GetValue<string>());
    }

    [Fact]
    public void Chat_tool_call_arguments_are_a_json_string()
    {
        var request = Msg.Request([Msg.User("q"), Msg.Assistant(Msg.Call("call_1", "Read", """{"path":"a"}""")), Msg.ToolResults(("call_1", "x", false))]);

        var assistant = ((JsonArray)OpenAiChatEncoder.Encode(request, "gpt-4o").Body["messages"]!)[2]!;

        Assert.Equal("""{"path":"a"}""", assistant["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>());
        Assert.Null(assistant["content"]);
    }

    // ───────────────────────── OpenAI Responses ─────────────────────────

    private static JsonArray ResponsesInput(IReadOnlyList<Message> messages, ReasoningReplay replay = ReasoningReplay.OpaqueOnly) =>
        (JsonArray)OpenAiResponsesEncoder.Encode(Msg.Request(messages), "gpt-5", new EncodeOptions("ws", ReasoningReplay: replay)).Body["input"]!;

    private static ThinkingPart ReasoningFrom(string itemJson, string text = "思考") => new(text, Msg.Opaque("openai-responses", $$"""{"item":{{itemJson}}}"""));

    [Fact]
    public void Responses_is_stateless_and_asks_for_encrypted_reasoning()
    {
        var body = OpenAiResponsesEncoder.Encode(Msg.Request("hi", "SYS", [Msg.Tool()]), "gpt-5", Options).Body;

        Assert.False(body["store"]!.GetValue<bool>());
        Assert.Equal("reasoning.encrypted_content", ((JsonArray)body["include"]!)[0]!.GetValue<string>());
        Assert.Equal("SYS", body["instructions"]!.GetValue<string>());
        Assert.Equal("Read", ((JsonArray)body["tools"]!)[0]!["name"]!.GetValue<string>());
        Assert.False(((JsonArray)body["tools"]!)[0]!["strict"]!.GetValue<bool>());
    }

    [Fact]
    public void Responses_opaque_only_replay_uses_a_field_whitelist_and_strips_reasoning_text()
    {
        // 上游原样的 output item 带 status:第三方网关会 400,而且整段历史都带着它。content 在官方输入侧上限是 0。
        var item = """{"type":"reasoning","id":"rs_1","status":"completed","summary":[{"type":"summary_text","text":"s"}],"content":[{"type":"reasoning_text","text":"正文"}],"encrypted_content":"ENC","extra":1}""";
        var input = ResponsesInput([Msg.User("q"), Msg.Assistant(ReasoningFrom(item), new TextPart("a"))]);

        var reasoning = input.Select(i => (JsonObject)i!).Single(i => i["type"]?.GetValue<string>() == "reasoning");

        Assert.Equal(["type", "id", "summary", "encrypted_content"], reasoning.Select(kv => kv.Key));
    }

    [Fact]
    public void Responses_a_reasoning_id_over_64_characters_is_skipped_not_truncated()
    {
        var item = $$"""{"type":"reasoning","id":"{{new string('x', 65)}}","encrypted_content":"ENC"}""";
        var input = ResponsesInput([Msg.User("q"), Msg.Assistant(ReasoningFrom(item), new TextPart("a"))]);

        Assert.DoesNotContain(input, i => i!["type"]?.GetValue<string>() == "reasoning");
    }

    [Fact]
    public void Responses_text_required_dialect_replays_the_text_even_without_a_carrier_and_fills_missing_content()
    {
        var noCarrier = ResponsesInput([Msg.User("q"), Msg.Assistant(new ThinkingPart("从别的协议来的思考"), new TextPart("a"))], ReasoningReplay.TextRequired);
        var bareCarrier = ResponsesInput(
            [Msg.User("q"), Msg.Assistant(ReasoningFrom("""{"type":"reasoning","id":"rs"}""", "补正文"), new TextPart("a"))], ReasoningReplay.TextRequired);

        var synthesised = noCarrier.Select(i => (JsonObject)i!).Single(i => i["type"]?.GetValue<string>() == "reasoning");
        Assert.Equal("从别的协议来的思考", synthesised["content"]![0]!["text"]!.GetValue<string>());
        Assert.False(synthesised.ContainsKey("id")); // 不编造 id
        Assert.False(synthesised.ContainsKey("encrypted_content"));

        var filled = bareCarrier.Select(i => (JsonObject)i!).Single(i => i["type"]?.GetValue<string>() == "reasoning");
        Assert.Equal("补正文", filled["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("rs", filled["id"]!.GetValue<string>());
    }

    [Fact]
    public void Responses_opaque_only_never_synthesises_a_reasoning_item_from_text()
    {
        var input = ResponsesInput([Msg.User("q"), Msg.Assistant(new ThinkingPart("text only"), new TextPart("a"))]);

        Assert.DoesNotContain(input, i => i!["type"]?.GetValue<string>() == "reasoning");
    }

    [Fact]
    public void Responses_function_calls_and_outputs_and_assistant_text_are_flat_items()
    {
        var input = ResponsesInput([
            Msg.User("q"),
            Msg.Assistant(new TextPart("a"), Msg.Call("call_1", "Read", """{"p":1}"""), new TextPart("b")),
            Msg.ToolResults(("call_1", "result", false)),
        ]);

        Assert.Equal(
            ["message:user", "message:assistant", "function_call", "message:assistant", "function_call_output"],
            input.Select(i => i!["type"]?.GetValue<string>() ?? "message:" + i["role"]!.GetValue<string>()));
        Assert.Equal("a", input[1]!["content"]!.GetValue<string>()); // assistant 的 content 是拼好的字符串
        Assert.Equal("result", input[4]!["output"]!.GetValue<string>());
    }
}
