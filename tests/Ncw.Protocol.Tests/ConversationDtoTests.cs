using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>对话链路 DTO 的线上形状:每个类型往返、多态按名字区分、方法表声明齐全。冻结的向量在 <see cref="GoldenV1Conversation"/>。</summary>
public class ConversationDtoTests
{
    private static T RoundTrip<T>(T value) where T : class
    {
        var shape = PayloadShapes.GetShape<T>();
        return FrameCodec.DecodePayload(FrameCodec.EncodePayload(value, shape), shape);
    }

    public static TheoryData<RunEventDto> RunEvents() =>
    [
        new RunStartEventDto("r1", "s1", "sonnet", "p1"),
        new TurnStartEventDto(0),
        new MessageStartEventDto("m1", "assistant", "sonnet", null),
        new MessageDeltaEventDto("m1", 0, "text", "你好", null, null),
        new MessageDeltaEventDto("m1", 1, "toolArgs", "", "c1", "Bash"),
        new MessageEndEventDto(new MessageDto("m1", "assistant", 1_700_000_000_000, false, "[{\"type\":\"text\",\"text\":\"hi\"}]", "{\"stop\":\"endTurn\"}"),
            new UsageDto(10, 5, null, 3, null, null), new CostDto("known", 1200, "usd"), "endTurn"),
        new MessageDiscardEventDto("m2"),
        new ProviderRetryEventDto(1, 500, "overloaded"),
        new ProviderSwitchEventDto("a", "b", "cooling"),
        new ToolStartEventDto("c1", "Bash", "{\"command\":\"ls\"}", new EnvRefDto("dev", "local", "Local", "local")),
        new ToolProgressEventDto("c1", "working", 0.5, null),
        new ToolEndEventDto("c1", "ok", [new ToolImageDto("image/png", "data:...")], false, null, "{\"kind\":\"bash\"}", false, 12),
        new NotificationEventDto("loop.outputLimit", null),
        new TurnEndEventDto(0),
        new RunEndEventDto("error", new ErrorDto("provider", "boom", false, 500, null, "llm.invalidResponse", new Dictionary<string, string> { ["a"] = "b" })),
        new ContextUsageEventDto(100, 200_000, false, [new ContextSegmentDto("system", 60), new ContextSegmentDto("messages", 40)]),
        new ContextStatusEventDto("compacting", "auto"),
        new InteractionRequestEventDto(new ToolPermissionInteractionDto
        {
            Id = "i1", RunId = "r1", CreatedAt = 5, Version = 7, CallId = "c1", ToolName = "Bash", InternalId = "Bash", InputJson = "{}",
            Env = new EnvRefDto("dev", "local", "Local", "local"), AllowedScopes = ["session", "project"], Risk = "dangerous",
        }),
        new InteractionResolvedEventDto(new ResolutionDto("i1", "r1", "tool_permission", 7, "answered", "client-a", 9, 8), new ToolPermissionResponseDto("allowOnce")),
        new RunSettledEventDto("done"),
    ];

    [Theory]
    [MemberData(nameof(RunEvents))]
    public void Every_run_event_round_trips_and_keeps_its_type(RunEventDto ev)
    {
        var back = RoundTrip(ev);

        Assert.Equal(ev.GetType(), back.GetType());
        Assert.Equal(FrameCodec.EncodePayload(ev, PayloadShapes.GetShape<RunEventDto>()), FrameCodec.EncodePayload(back, PayloadShapes.GetShape<RunEventDto>()));
    }

    public static TheoryData<InteractionDto> Interactions() =>
    [
        new ToolPermissionInteractionDto
        {
            Id = "i1", RunId = "r1", CreatedAt = 1, Version = 2, CallId = "c", ToolName = "Write", InternalId = "Write", InputJson = "{\"file_path\":\"a\"}",
            Env = new EnvRefDto("d", "e", "n", "ssh"), AllowedScopes = [], OutsideWorkspace = true,
        },
        new AskUserInteractionDto { Id = "i2", RunId = "r1", Questions = [new AskUserQuestionDto("h", "q?", [new AskUserOptionDto("a", "desc"), new AskUserOptionDto("b")], false, true)] },
        new PlanApprovalInteractionDto { Id = "i3", RunId = "r1", Plan = "p", PlanId = "pl", Path = "/x" },
        new GoalProposalInteractionDto { Id = "i4", RunId = "r1", SessionId = "s", Condition = "c" },
        new WorkflowApprovalInteractionDto { Id = "i5", RunId = "r1", CallId = "c", WorkflowJson = "{}" },
        new WidgetSubmitInteractionDto { Id = "i6", RunId = "r1", CallId = "c", Title = "t" },
    ];

    [Theory]
    [MemberData(nameof(Interactions))]
    public void Every_interaction_round_trips_with_its_common_fields(InteractionDto interaction)
    {
        var back = RoundTrip(interaction);

        Assert.Equal(interaction.GetType(), back.GetType());
        Assert.Equal(interaction.Id, back.Id);
        Assert.Equal(interaction.Version, back.Version);
        Assert.Equal(FrameCodec.EncodePayload(interaction, PayloadShapes.GetShape<InteractionDto>()), FrameCodec.EncodePayload(back, PayloadShapes.GetShape<InteractionDto>()));
    }

    public static TheoryData<InteractionResponseDto> Responses() =>
    [
        new ToolPermissionResponseDto("allowAlways", "project"),
        new ToolPermissionResponseDto("allowEdited", null, "{\"command\":\"echo\"}"),
        new AskUserResponseDto([new AskUserAnswerDto(["a"]), new AskUserAnswerDto(["x", "y"])]),
        new AskUserResponseDto(null),
        new PlanApprovalResponseDto("requestRevision", "shorter"),
        new GoalProposalResponseDto(true),
        new WorkflowApprovalResponseDto("run"),
        new WidgetSubmitResponseDto(null),
    ];

    [Theory]
    [MemberData(nameof(Responses))]
    public void Every_interaction_response_round_trips(InteractionResponseDto response)
    {
        var back = RoundTrip(response);

        Assert.Equal(response.GetType(), back.GetType());
        Assert.Equal(FrameCodec.EncodePayload(response, PayloadShapes.GetShape<InteractionResponseDto>()), FrameCodec.EncodePayload(back, PayloadShapes.GetShape<InteractionResponseDto>()));
    }

    [Fact]
    public void A_dismissed_ask_user_answer_stays_distinct_from_an_empty_one()
    {
        Assert.Null(((AskUserResponseDto)RoundTrip<InteractionResponseDto>(new AskUserResponseDto(null))).Answers);
        Assert.Empty(((AskUserResponseDto)RoundTrip<InteractionResponseDto>(new AskUserResponseDto([]))).Answers!);
    }

    [Fact]
    public void Run_snapshot_and_session_topic_state_round_trip()
    {
        var snapshot = new RunSnapshotDto("r1", "s1", "w1", null, 0, "running", null, 1, null, 3, "fresh",
            [new SeqEventDto(1, new RunStartEventDto("r1", "s1", "m")), new SeqEventDto(3, new TurnStartEventDto(0))], [], [], false, false);
        var back = RoundTrip(snapshot);

        Assert.Equal(2, back.Events.Length);
        Assert.IsType<TurnStartEventDto>(back.Events[1].Event);
        Assert.Equal(3, back.Events[1].Seq);

        var session = new SessionDto("s1", "w1", null, "chat", "t", null, "m", null, "code", "auto", false, true, 1, 2);
        var state = new SessionTopicState(
            new SessionPageDto(session, [new MessageDto("u1", "user", 1, false, "[]")], true, new Dictionary<string, string> { ["u1"] = "r1" }, [], []),
            new QueueDto("s1", 9, [new QueueItemDto("q1", [new InputPartDto(InputPartKinds.Text, "hi")], "pending", 0, 1, null, new SendOptionsDto("m"))]),
            "r1");
        var stateBack = RoundTrip(state);

        Assert.Equal("r1", stateBack.ActiveRunId);
        Assert.Equal(9, stateBack.Queue.Rev);
        Assert.Equal("r1", stateBack.Page.MessageRuns["u1"]);
    }

    [Fact]
    public void Device_topic_state_carries_subagents_and_rev_and_old_shape_still_decodes()
    {
        var shape = PayloadShapes.GetShape<DeviceTopicState>();
        var state = new DeviceTopicState([new ActiveRunDto("r1", "s1", "w1", 2)], 4)
        {
            Subagents = [new ActiveSubagentDto("r1:sub:1", "r1", "s1", "w1", 10)],
            Rev = 12,
        };

        var back = FrameCodec.DecodePayload(FrameCodec.EncodePayload(state, shape), shape);
        Assert.Equal(12, back.Rev);
        Assert.Single(back.Subagents);

        // M2 的设备只发 Runs 和 ProjectsRevision 两个键:缺省为空 / 0(新增字段不改协议版本)。
        byte[] old = [0x82, 0xA4, (byte)'R', (byte)'u', (byte)'n', (byte)'s', 0x90, 0xB0, .. "ProjectsRevision"u8, 0x03];
        var decoded = FrameCodec.DecodePayload(old, shape);
        Assert.Equal(0, decoded.Rev);
        Assert.Empty(decoded.Subagents);
        Assert.Equal(3, decoded.ProjectsRevision);
    }

    [Fact]
    public void Every_conversation_method_declares_kind_role_capability_and_transport()
    {
        Assert.Equal(29, ConversationMethods.All.Count);
        Assert.Equal(ConversationMethods.All.Count, ConversationMethods.All.Select(m => m.Name).Distinct().Count());
        foreach (var method in ConversationMethods.All)
        {
            Assert.Equal(Capabilities.Conversation, method.Capability);
            Assert.Equal(Transports.Any, method.Allowed);
            // 只读的 query 给 viewer;有副作用的 command 至少 member
            Assert.Equal(method.Kind == MethodKind.Query, method.MinRole == Role.Viewer);
            Assert.True(method.Kind == MethodKind.Command || method.MinRole == Role.Viewer);
        }

        Assert.Equal(Role.Owner, ConversationMethods.SessionsDelete.MinRole);
        Assert.Equal(Role.Owner, ConversationMethods.RunsSetPermissionMode.MinRole);
        Assert.Equal(Role.Member, ConversationMethods.InteractionsRespond.MinRole);
        Assert.Equal(Role.Member, ConversationMethods.RunsStart.MinRole);
        Assert.Equal(MethodKind.Command, ConversationMethods.RunsStart.Kind);
        Assert.Equal(MethodKind.Query, ConversationMethods.SessionsGetPage.Kind);
    }

    [Fact]
    public void Conflict_details_travel_inside_the_error_frame()
    {
        var details = new InteractionConflictDto(ConflictReasons.AlreadyResolved,
            new ResolutionDto("i1", "r1", "tool_permission", 7, "answered", "client-a", 99, 8));
        var response = new ResponseFrame(5, false, null, new ProtocolError(ErrorCodes.Conflict, "already resolved", false, null,
            FrameCodec.EncodePayload(details, PayloadShapes.GetShape<InteractionConflictDto>())));

        var back = (ResponseFrame)FrameCodec.Decode(FrameCodec.Encode(response));
        var exception = ProtocolException.From(back.Error!);

        Assert.Equal(ErrorCodes.Conflict, exception.Code);
        var decoded = FrameCodec.DecodePayload(exception.Details!, PayloadShapes.GetShape<InteractionConflictDto>());
        Assert.Equal("client-a", decoded.Resolution!.ResolvedBy);
        Assert.Equal(99, decoded.Resolution.At);
    }

    [Fact]
    public void Golden_vectors_v1_conversation()
    {
        static string Hex<T>(T value) => Convert.ToHexStringLower(FrameCodec.EncodePayload(value, PayloadShapes.GetShape<T>()));

        Assert.Equal(GoldenV1Conversation.RunStartEvent, Hex<RunEventDto>(new RunStartEventDto("r1", "s1", "sonnet", null)));
        Assert.Equal(GoldenV1Conversation.MessageEndEvent, Hex<RunEventDto>(new MessageEndEventDto(
            new MessageDto("m1", "assistant", 1700000000000, false, "[{\"type\":\"text\",\"text\":\"hi\"}]", null), new UsageDto(10, 5), null, "endTurn")));
        Assert.Equal(GoldenV1Conversation.ToolStartEvent, Hex<RunEventDto>(new ToolStartEventDto("c1", "Bash", "{\"command\":\"ls\"}", new EnvRefDto("dev", "local", "Local", "local"))));
        Assert.Equal(GoldenV1Conversation.PermissionInteraction, Hex<InteractionDto>(new ToolPermissionInteractionDto
        {
            Id = "i1", RunId = "r1", CreatedAt = 5, Version = 7, CallId = "c1", ToolName = "Bash", InternalId = "Bash", InputJson = "{}",
            Env = new EnvRefDto("dev", "local", "Local", "local"), AllowedScopes = ["session"], Risk = "dangerous",
        }));
        Assert.Equal(GoldenV1Conversation.PermissionResponse, Hex<InteractionResponseDto>(new ToolPermissionResponseDto("allowAlways", "project")));
        Assert.Equal(GoldenV1Conversation.RunsStartParams, Hex(new RunsStartParams("r1", "s1", "m1", [new InputPartDto("text", "hi")], new SendOptionsDto("sonnet"))));
        Assert.Equal(GoldenV1Conversation.QueueSnapshot, Hex(new QueueDto("s1", 3,
            [new QueueItemDto("q1", [new InputPartDto("text", "hi")], "pending", 0, 1, null, new SendOptionsDto("sonnet"))])));
        Assert.Equal(GoldenV1Conversation.RunSnapshot, Hex(new RunSnapshotDto("r1", "s1", "w1", null, 0, "running", null, 1, null, 2, "fresh",
            [new SeqEventDto(1, new RunStartEventDto("r1", "s1", "m"))], [], [], false, false)));
        Assert.Equal(GoldenV1Conversation.ConflictDetails, Hex(new InteractionConflictDto("alreadyResolved",
            new ResolutionDto("i1", "r1", "tool_permission", 7, "answered", "client-a", 99, 8))));
        Assert.Equal(GoldenV1Conversation.ConflictResponseFrame, Convert.ToHexStringLower(FrameCodec.Encode(
            new ResponseFrame(5, false, null, new ProtocolError("conflict", "already resolved", false, null, [0x80])))));
        Assert.Equal(GoldenV1Conversation.DeviceTopicState, Hex(new DeviceTopicState([new ActiveRunDto("r1", "s1", "w1", 2)], 4)
        {
            Subagents = [new ActiveSubagentDto("r1:sub:1", "r1", "s1", "w1", 10)],
            Rev = 12,
        }));
        Assert.Equal(GoldenV1Conversation.InteractionsSnapshot, Hex(new InteractionsDto(9,
        [
            new AskUserInteractionDto { Id = "i2", RunId = "r1", Version = 9, Questions = [new AskUserQuestionDto("h", "q?", [new AskUserOptionDto("a")], false, true)] },
        ])));
    }
}
