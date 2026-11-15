using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>
/// 子代理 / 目标 / 计划的线上形状:每个类型往返、多态事件按名字区分、「键存在但值为空 = 清除」的补丁语义可区分、方法表声明齐全。
/// 冻结的向量在 <see cref="GoldenV1Orchestration"/>(只增向量,不改已冻结的旧向量)。
/// </summary>
public class OrchestrationDtoTests
{
    private static T RoundTrip<T>(T value) where T : class
    {
        var shape = PayloadShapes.GetShape<T>();
        return FrameCodec.DecodePayload(FrameCodec.EncodePayload(value, shape), shape);
    }

    private static string Hex<T>(T value) => Convert.ToHexStringLower(FrameCodec.EncodePayload(value, PayloadShapes.GetShape<T>()));

    public static TheoryData<RunEventDto> SubagentEvents() =>
    [
        new SubagentStartEventDto("c1", "r1:sub:1", "s1:sub:r1:sub:1", "general-purpose", "查配置读取处", "blue", "sonnet", true, 1_700_000_000_000),
        new SubagentUpdateEventDto("c1", "r1:sub:1"),
        new SubagentUpdateEventDto("c1", "r1:sub:1", "tool", new StringPatchDto("Read"), new StringPatchDto("src/config.ts"), 3, 1, new UsageDto(10, 5),
            new SubagentContextUsageDto(100, 200_000, false), new SubagentNoticePatchDto(new SubagentNoticeDto("retry", "overloaded", 2)), 17, 1_700_000_000_500),
        new SubagentEndEventDto("c1", "r1:sub:1", "error", "summary", new ErrorDto("provider", "上游 502", false, 502, null, "llm.invalidResponse"), 21, 1_700_000_001_000),
    ];

    [Theory]
    [MemberData(nameof(SubagentEvents))]
    public void Every_subagent_event_round_trips_through_the_run_event_union_and_keeps_its_type(RunEventDto ev)
    {
        var back = RoundTrip(ev);

        Assert.Equal(ev.GetType(), back.GetType());
        Assert.Equal(Hex(ev), Hex(back));
    }

    [Fact]
    public void An_absent_patch_and_a_clearing_patch_are_different_on_the_wire()
    {
        // 键不存在 = 保持;键存在而值为空 = 清除(Core 的 Patch 语义)
        var keep = RoundTrip<RunEventDto>(new SubagentUpdateEventDto("c1", "r1:sub:1", ToolCalls: 2));
        var clear = RoundTrip<RunEventDto>(new SubagentUpdateEventDto("c1", "r1:sub:1", CurrentTool: new StringPatchDto(null), Notice: new SubagentNoticePatchDto(null)));

        Assert.Null(((SubagentUpdateEventDto)keep).CurrentTool);
        Assert.Null(((SubagentUpdateEventDto)keep).Notice);
        var cleared = (SubagentUpdateEventDto)clear;
        Assert.NotNull(cleared.CurrentTool);
        Assert.Null(cleared.CurrentTool.Value);
        Assert.NotNull(cleared.Notice);
        Assert.Null(cleared.Notice.Value);
        Assert.Null(cleared.CurrentTarget);
    }

    [Fact]
    public void Definitions_goals_plans_and_session_events_round_trip()
    {
        var info = RoundTrip(new SubagentsListResult(
        [
            new AgentInfoDto("reviewer", "reviews", "project", "/p/.next-cowork/agents/reviewer.md", ["Read", "Grep"], "sonnet", "auto", "blue", true),
            new AgentInfoDto("general-purpose", "everything", "builtin", "", null, null, null, null, false),
        ]));
        Assert.Equal(["Read", "Grep"], info.Agents[0].Tools!);
        Assert.Null(info.Agents[1].Tools);

        var diagnostics = RoundTrip(new SubagentsDiagnosticsResult([new AgentDiagnosticDto("/p/a.md", "agents.toolsUnknown", "unknown tool", new Dictionary<string, string> { ["tool"] = "Foo" })]));
        Assert.Equal("Foo", diagnostics.Diagnostics[0].Params!["tool"]);

        var goal = new GoalDto("s1", "g1", "make the tests green", "user", 1_700_000_000_000, 50, 2, "still red", 1_700_000_100_000, 1, null, 0, 1234);
        var change = RoundTrip(new GoalChangeDto("s1", goal, null, new MessageDto("m1", "assistant", 1, false, "[]"), 9));
        Assert.Equal((goal, 9L), (change.Goal, change.Rev));
        Assert.Null(RoundTrip(new GoalChangeDto("s1", null, "met", null, 10)).Goal);
        Assert.Equal("userClear", RoundTrip(new GoalChangeDto("s1", null, "userClear", null, 11)).Cleared);
        Assert.Single(RoundTrip(new GoalsTopicState(4, [goal])).Goals);
        var set = RoundTrip(new GoalSetResultDto("set", goal, "launched"));
        Assert.Equal(("set", "launched"), (set.Outcome, set.Delivery));
        Assert.Equal(4001, RoundTrip(new GoalSetResultDto(GoalOutcomes.TooLong, Length: 4001)).Length);
        Assert.Equal("high", RoundTrip(new GoalSetParams("s1", "x", new SendOptionsDto("sonnet", Thinking: "high"))).Options!.Thinking);

        var execute = RoundTrip(new RunsExecutePlanParams(
            new RunsStartParams("r2", "s1", "m2", [new InputPartDto("text", "implement")], new SendOptionsDto("sonnet", Mode: "code")), new PlanFileDto("plan1", ".plan/plan1.md")));
        Assert.Equal((".plan/plan1.md", "code"), (execute.Plan.Path, execute.Start.Options.Mode));

        var stopped = RoundTrip(new SubagentStoppedDto("s1", "c1", "r1:sub:1", "s1:sub:r1:sub:1", "general-purpose", "done", true));
        Assert.Equal(("c1", "done", true), (stopped.CallId, stopped.Status, stopped.Background));
        Assert.Equal("injecting", RoundTrip(new SubagentReportDto("s1", "c1", "injecting")).Status);
        Assert.Equal("pending", RoundTrip(new SubagentsReportResult("pending")).Status);
    }

    [Fact]
    public void Every_orchestration_method_declares_kind_role_capability_and_transport_and_commands_need_a_member_or_owner()
    {
        Assert.Equal(8, OrchestrationMethods.All.Count);
        Assert.Equal(OrchestrationMethods.All.Count, OrchestrationMethods.All.Select(m => m.Name).Distinct().Count());
        foreach (var method in OrchestrationMethods.All)
        {
            Assert.Contains(method.Capability, new[] { Capabilities.Subagents, Capabilities.Planning });
            Assert.Equal(Transports.Any, method.Allowed);
            // 只读的 query 给 viewer;有副作用的 command 至少 member(路由据此要求幂等键)
            Assert.Equal(method.Kind == MethodKind.Query, method.MinRole == Role.Viewer);
        }

        Assert.Equal((MethodKind.Query, Role.Viewer, Capabilities.Subagents), (OrchestrationMethods.SubagentsList.Kind, OrchestrationMethods.SubagentsList.MinRole, OrchestrationMethods.SubagentsList.Capability));
        Assert.Equal(Role.Viewer, OrchestrationMethods.SubagentsDiagnostics.MinRole);
        Assert.Equal((MethodKind.Command, Role.Owner), (OrchestrationMethods.SubagentsSetEnabled.Kind, OrchestrationMethods.SubagentsSetEnabled.MinRole));
        Assert.Equal((MethodKind.Command, Role.Member), (OrchestrationMethods.SubagentsReport.Kind, OrchestrationMethods.SubagentsReport.MinRole));
        Assert.Equal((MethodKind.Query, Role.Viewer, Capabilities.Planning), (OrchestrationMethods.GoalGet.Kind, OrchestrationMethods.GoalGet.MinRole, OrchestrationMethods.GoalGet.Capability));
        Assert.Equal((MethodKind.Command, Role.Member), (OrchestrationMethods.GoalSet.Kind, OrchestrationMethods.GoalSet.MinRole));
        Assert.Equal((MethodKind.Command, Role.Member), (OrchestrationMethods.GoalClear.Kind, OrchestrationMethods.GoalClear.MinRole));
        Assert.Equal((MethodKind.Command, Role.Member), (OrchestrationMethods.RunsExecutePlan.Kind, OrchestrationMethods.RunsExecutePlan.MinRole));
        Assert.Equal(("goal", Role.Viewer, Transports.Any), (OrchestrationMethods.GoalTopic.Name, OrchestrationMethods.GoalTopic.MinRole, OrchestrationMethods.GoalTopic.Allowed));
        // runs.background 仍在对话链路里:同一个方法,现在有了可挪的 Run
        Assert.Equal((MethodKind.Command, Role.Member, Capabilities.Conversation), (ConversationMethods.RunsBackground.Kind, ConversationMethods.RunsBackground.MinRole, ConversationMethods.RunsBackground.Capability));
    }

    [Fact]
    public void Golden_vectors_v1_orchestration()
    {
        Assert.Equal(GoldenV1Orchestration.SubagentStartEvent, Hex<RunEventDto>(new SubagentStartEventDto(
            "c1", "r1:sub:1", "s1:sub:r1:sub:1", "general-purpose", "find config", "blue", "sonnet", false, 10)));
        Assert.Equal(GoldenV1Orchestration.SubagentUpdateEvent, Hex<RunEventDto>(new SubagentUpdateEventDto(
            "c1", "r1:sub:1", "tool", new StringPatchDto("Read"), new StringPatchDto(null), 3, null, null, new SubagentContextUsageDto(100, 200000, false), null, 7, 11)));
        Assert.Equal(GoldenV1Orchestration.SubagentEndEvent, Hex<RunEventDto>(new SubagentEndEventDto(
            "c1", "r1:sub:1", "done", "all good", null, 9, 12)));
        Assert.Equal(GoldenV1Orchestration.GoalChange, Hex(new GoalChangeDto(
            "s1", new GoalDto("s1", "g1", "make the tests green", "user", 1000, 50, 1, "still red", null, 0, null, 0, 7), null, null, 5)));
        Assert.Equal(GoldenV1Orchestration.GoalClearedChange, Hex(new GoalChangeDto("s1", null, "met", null, 6)));
        Assert.Equal(GoldenV1Orchestration.GoalsSnapshot, Hex(new GoalsTopicState(5, [new GoalDto("s1", "g1", "x", "user", 1000, 0, 0, null, null, 0, null, 0, 0)])));
        Assert.Equal(GoldenV1Orchestration.GoalSetParams, Hex(new GoalSetParams("s1", "ship it", new SendOptionsDto("sonnet"))));
        Assert.Equal(GoldenV1Orchestration.GoalSetResult, Hex(new GoalSetResultDto("set", new GoalDto("s1", "g1", "x", "user", 1000, 0, 0, null, null, 0, null, 0, 0), "launched")));
        Assert.Equal(GoldenV1Orchestration.ExecutePlanParams, Hex(new RunsExecutePlanParams(
            new RunsStartParams("r2", "s1", "m2", [new InputPartDto("text", "go")], new SendOptionsDto("sonnet", Mode: "code")), new PlanFileDto("p1", ".plan/p1.md"))));
        Assert.Equal(GoldenV1Orchestration.AgentsList, Hex(new SubagentsListResult(
            [new AgentInfoDto("reviewer", "reviews", "project", "/p/reviewer.md", ["Read"], null, "auto", "blue", true)])));
        Assert.Equal(GoldenV1Orchestration.SubagentStopped, Hex(new SubagentStoppedDto("s1", "c1", "r1:sub:1", "s1:sub:r1:sub:1", "general-purpose", "done", true)));
        Assert.Equal(GoldenV1Orchestration.SubagentReport, Hex(new SubagentReportDto("s1", "c1", "reported")));
    }
}
