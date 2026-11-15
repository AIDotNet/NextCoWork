using System.Text.Json;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Storage.Sessions;

namespace Ncw.Agent.Session.Tests;

/// <summary>Storage 的 2.x 兼容 JSON ↔ Llm 的 Message / Part(04 §1.1)。</summary>
public sealed class CodecTests
{
    private static MessageRecord Legacy(string role, string parts, bool internalMessage = false) =>
        new("m1", role, parts, 1_700_000_000_000, 1, internalMessage);

    private static Message RoundTrip(Message message) => MessageCodec.FromRecord(MessageCodec.ToRecord(message));

    [Fact]
    public void Every_part_kind_survives_a_roundtrip_with_the_2x_shape()
    {
        var message = new Message(new MessageId("a1"), Role.Assistant,
        [
            new TextPart("你好 \"quoted\""),
            new ThinkingPart("hmm", new ProtocolOpaque("anthropic", Jsn.Parse("""{"signature":"sig"}""")), 1200, 34),
            new ToolCallPart("c1", "Read", Jsn.Parse("""{"file_path":"/a.cs","n":1}""")),
            new ToolResultPart("c1", new ToolOutput("done", [new ToolOutputImage("image/png", "ncw://x")], true, 99, Jsn.Parse("""{"card":1}""")), true, new SubagentReceipt("run-2", "done", "ok")),
            new ImagePart("image/png", "data:image/png;base64,AAAA"),
            new FileRefPart("src/a.cs", "a.cs"),
            new ErrorPart(new AgentError(AgentErrorCode.RateLimit, "slow down", true, 429, 1500, "llm.x", new Dictionary<string, string> { ["k"] = "v" })),
            new GoalStatusPart(false, "ship it", "not yet", true, null),
            new CompactBoundaryPart("auto", 200_000, 8_000, "summary", "keep X", ["/a.cs"], new SummaryModelInfo("cheap", "p2", "low", true)),
        ], DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123), Internal: false);

        var back = RoundTrip(message);

        Assert.Equal(message.Parts.Count, back.Parts.Count);
        Assert.Equal("你好 \"quoted\"", ((TextPart)back.Parts[0]).Text);
        var thinking = (ThinkingPart)back.Parts[1];
        Assert.Equal((1200, 34), (thinking.DurationMs, thinking.Tokens));
        Assert.Equal("sig", thinking.Opaque!.Data.Str("signature"));
        var call = (ToolCallPart)back.Parts[2];
        Assert.Equal(("c1", "Read"), (call.CallId, call.Name));
        Assert.Equal("/a.cs", call.Input.Str("file_path"));
        var result = (ToolResultPart)back.Parts[3];
        Assert.True(result.IsError);
        Assert.Equal(("done", true, 99L), (result.Output.Content, result.Output.Truncated, result.Output.OriginalBytes));
        Assert.Equal("ncw://x", result.Output.Images![0].DataRef);
        Assert.Equal(1, result.Output.Details!.Value.Num("card"));
        Assert.Equal(new SubagentReceipt("run-2", "done", "ok"), result.Subagent);
        Assert.Equal(new ImagePart("image/png", "data:image/png;base64,AAAA"), back.Parts[4]);
        Assert.Equal(new FileRefPart("src/a.cs", "a.cs"), back.Parts[5]);
        var error = ((ErrorPart)back.Parts[6]).Error;
        Assert.Equal((AgentErrorCode.RateLimit, "slow down", true, 429, 1500, "llm.x"), (error.Code, error.Message, error.Retryable, error.Status, error.RetryAfterMs, error.MessageKey));
        Assert.Equal("v", error.MessageParams!["k"]);
        var goal = (GoalStatusPart)back.Parts[7];
        Assert.Equal((false, "ship it", "not yet", true), (goal.Met, goal.Condition, goal.Reason, goal.Failed));
        var boundary = (CompactBoundaryPart)back.Parts[8];
        Assert.Equal(["/a.cs"], boundary.RestoredFiles);
        Assert.Equal(new SummaryModelInfo("cheap", "p2", "low", true), boundary.SummaryModel);
        Assert.Equal(message.CreatedAt, back.CreatedAt);
    }

    [Fact]
    public void The_written_json_is_the_2x_shape_and_keeps_non_ascii_unescaped()
    {
        var record = MessageCodec.ToRecord(new Message(new MessageId("a1"), Role.Assistant,
            [new TextPart("你好"), new ToolCallPart("c1", "Read", Jsn.Parse("""{"file_path":"/a"}"""))], DateTimeOffset.UnixEpoch));

        Assert.Equal("""[{"type":"text","text":"你好"},{"type":"tool_call","callId":"c1","name":"Read","input":{"file_path":"/a"}}]""", record.PartsJson);
        Assert.Equal(("assistant", 1), (record.Role, record.SchemaVersion));
    }

    [Fact]
    public void A_2x_anthropic_opaque_without_a_protocol_label_gets_tagged_anthropic()
    {
        // 2.x 的 Anthropic 透传没有 protocol 字段:不补标签,签名永远回放不了(标签对不上的编码器一律忽略)
        var message = MessageCodec.FromRecord(Legacy("assistant",
            """[{"type":"thinking","text":"t","opaque":{"signature":"abc"}},{"type":"thinking","text":"","opaque":{"redacted":"ZGF0YQ=="}}]"""));

        var signed = (ThinkingPart)message.Parts[0];
        Assert.Equal("anthropic", signed.Opaque!.Protocol);
        Assert.Equal("abc", signed.Opaque.Data.Str("signature"));
        var redacted = (ThinkingPart)message.Parts[1];
        Assert.Equal(("anthropic", "ZGF0YQ=="), (redacted.Opaque!.Protocol, redacted.Opaque.Data.Str("redacted")));
    }

    [Fact]
    public void Anthropic_opaque_is_written_back_without_the_label_so_2x_still_reads_it()
    {
        var record = MessageCodec.ToRecord(new Message(new MessageId("a"), Role.Assistant,
            [new ThinkingPart("t", new ProtocolOpaque("anthropic", Jsn.Parse("""{"signature":"abc"}""")))], DateTimeOffset.UnixEpoch));

        Assert.Equal("""[{"type":"thinking","text":"t","opaque":{"signature":"abc"}}]""", record.PartsJson);
    }

    [Fact]
    public void OpenAI_opaque_carries_its_label_in_both_directions_and_the_data_keeps_the_rest()
    {
        var parts = MessageCodec.PartsFromJson(
            """[{"type":"thinking","text":"","opaque":{"protocol":"openai-responses","item":{"type":"reasoning","id":"rs_1"}}},{"type":"thinking","text":"r","opaque":{"protocol":"openai-chat","field":"reasoning_content"}}]""");

        var responses = (ThinkingPart)parts[0];
        Assert.Equal("openai-responses", responses.Opaque!.Protocol);
        Assert.Equal("rs_1", responses.Opaque.Data.Obj("item").Str("id"));
        Assert.Null(responses.Opaque.Data.Prop("protocol")); // 标签拆出来了,不留在 Data 里
        Assert.Equal(("openai-chat", "reasoning_content"), (((ThinkingPart)parts[1]).Opaque!.Protocol, ((ThinkingPart)parts[1]).Opaque!.Data.Str("field")));

        var written = MessageCodec.PartsToJson(parts);
        Assert.Contains("\"protocol\":\"openai-responses\"", written);
        Assert.Contains("\"item\":{\"type\":\"reasoning\",\"id\":\"rs_1\"}", written);
        Assert.Contains("\"protocol\":\"openai-chat\"", written);
    }

    [Fact]
    public void An_opaque_nobody_recognises_is_dropped_not_guessed()
    {
        var part = (ThinkingPart)MessageCodec.PartsFromJson("""[{"type":"thinking","text":"x","opaque":{"weird":1}}]""").Single();

        Assert.Null(part.Opaque);
    }

    [Fact]
    public void Source_and_stop_reason_live_in_the_meta_column_and_survive_a_roundtrip()
    {
        var message = new Message(new MessageId("a"), Role.Assistant, [new TextPart("x")], DateTimeOffset.UnixEpoch,
            Source: new Provenance("anthropic", "p1", "claude-up"), Stop: StopReason.Aborted);

        var record = MessageCodec.ToRecord(message);
        var back = MessageCodec.FromRecord(record);

        Assert.Equal("""{"source":{"api":"anthropic","provider":"p1","model":"claude-up"},"stop":"aborted"}""", record.MetaJson);
        Assert.Equal(new Provenance("anthropic", "p1", "claude-up"), back.Source);
        Assert.Equal(StopReason.Aborted, back.Stop);
    }

    [Fact]
    public void A_2x_message_has_no_meta_and_therefore_an_unknown_source()
    {
        var back = MessageCodec.FromRecord(Legacy("assistant", """[{"type":"text","text":"old"}]"""));

        Assert.Null(back.Source);
        Assert.Null(back.Stop);
        Assert.Null(MessageCodec.ToRecord(new Message(new MessageId("u"), Role.User, [new TextPart("x")], DateTimeOffset.UnixEpoch)).MetaJson);
    }

    [Fact]
    public void Internal_flag_and_role_roundtrip()
    {
        var back = RoundTrip(new Message(new MessageId("i"), Role.User, [new TextPart("x")], DateTimeOffset.UnixEpoch, Internal: true));

        Assert.True(back.Internal);
        Assert.Equal(Role.User, back.Role);
    }

    [Fact]
    public void Reading_is_forgiving_bad_json_unknown_blocks_and_half_blocks_are_skipped_never_thrown()
    {
        Assert.Empty(MessageCodec.PartsFromJson("not json"));
        Assert.Empty(MessageCodec.PartsFromJson("""{"type":"text"}"""));
        Assert.Empty(MessageCodec.PartsFromJson(""));
        var parts = MessageCodec.PartsFromJson(
            """[{"type":"weird_block","callId":"c"},{"type":"subagent","callId":"c"},{"type":"text"},{"type":"tool_call","name":"x"},42,{"type":"text","text":"kept"}]""");

        Assert.Equal("kept", ((TextPart)Assert.Single(parts)).Text);
    }

    [Fact]
    public void The_background_subagent_receipt_and_the_report_marker_keep_their_2x_shape_and_survive_a_roundtrip()
    {
        var error = AgentError.Create(AgentErrorCode.Network, "reset", messageKey: "x.y");
        var receipt = new SubagentReceipt("run-1:sub:1", "error", "short", true, "blocked", "blue", error);
        var message = new Message(new MessageId("m"), Role.User,
        [
            new ToolResultPart("c1", new ToolOutput("started"), false, receipt),
            new SubagentReportPart("c1", "run-1:sub:1", "short"),
        ], DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));

        var json = MessageCodec.PartsToJson(message.Parts);
        var back = MessageCodec.PartsFromJson(json);

        Assert.Contains("\"subagent\":{\"childRunId\":\"run-1:sub:1\",\"status\":\"error\",\"summary\":\"short\"", json);
        Assert.Contains("\"background\":true,\"reportStatus\":\"blocked\"", json);
        Assert.Contains("{\"type\":\"subagent\",\"callId\":\"c1\",\"childRunId\":\"run-1:sub:1\",\"summary\":\"short\"}", json);
        var result = (ToolResultPart)back[0];
        Assert.Equal(("blocked", true, "blue"), (result.Subagent!.ReportStatus, result.Subagent.Background, result.Subagent.Color));
        Assert.Equal(("reset", "x.y"), (result.Subagent.Error!.Message, result.Subagent.Error.MessageKey));
        Assert.Equal(new SubagentReportPart("c1", "run-1:sub:1", "short"), back[1]);
    }

    [Fact]
    public void A_2x_card_becomes_details_and_extra_2x_output_fields_do_not_break_reading()
    {
        var part = (ToolResultPart)MessageCodec.PartsFromJson(
            """[{"type":"tool_result","callId":"c","output":{"content":"x","card":{"kind":"diff"},"videos":[{"url":"u"}],"workflow":{}},"isError":false}]""").Single();

        Assert.Equal("x", part.Output.Content);
        Assert.Equal("diff", part.Output.Details!.Value.Str("kind"));
    }

    [Fact]
    public void Error_codes_use_the_2x_snake_case_names()
    {
        var json = MessageCodec.PartsToJson([new ErrorPart(AgentError.Create(AgentErrorCode.ContextLength, "too long")), new ErrorPart(AgentError.Create(AgentErrorCode.NoHealthyProvider, "x"))]);

        Assert.Contains("\"code\":\"context_length\"", json);
        Assert.Contains("\"code\":\"no_healthy_provider\"", json);
    }

    [Fact]
    public void A_tool_call_without_an_input_gets_an_empty_object_and_undefined_input_is_written_as_one()
    {
        var read = (ToolCallPart)MessageCodec.PartsFromJson("""[{"type":"tool_call","callId":"c","name":"n"}]""").Single();
        Assert.Equal(JsonValueKind.Object, read.Input.ValueKind);

        var json = MessageCodec.PartsToJson([new ToolCallPart("c", "n", default)]);
        Assert.Contains("\"input\":{}", json);
    }
}
