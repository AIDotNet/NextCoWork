using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Tests.Support;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Tests;

/// <summary>回合循环(02 §2)与事件流(02 §3):假上游 + 假工具。</summary>
public sealed class LoopTests
{
    [Fact]
    public async Task A_single_turn_streams_message_events_and_commits_user_then_assistant()
    {
        var rig = new Rig();
        rig.Llm.Enqueue(Up.Text("hello"));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(
            ["RunStart", "MessageStart", "MessageEnd", "TurnStart", "MessageStart", "MessageDelta", "MessageEnd", "TurnEnd", "RunEnd"],
            run.Kinds);
        Assert.Equal(["u1", "id0001"], run.Committed.Select(m => m.Id.Value));
        var assistant = run.Assistants.Single();
        Assert.Equal("hello", Assert.IsType<TextPart>(Assert.Single(assistant.Parts)).Text);
        Assert.Equal(StopReason.EndTurn, assistant.Stop);
        Assert.Equal(new Provenance("anthropic", "p", "fake-up"), assistant.Source);
    }

    [Fact]
    public async Task PrepareTurn_sees_the_last_real_usage_and_the_message_it_was_measured_after()
    {
        var turns = new List<TurnContext>();
        var echo = new FakeTool("echo");
        var rig = new Rig().Tool(echo);
        rig.Prepare = (turn, _) =>
        {
            turns.Add(turn);
            return ValueTask.FromResult(TurnPlan.Run(new LlmRequest("system", turn.Transcript, [], turn.Options), rig.Snapshot()));
        };
        rig.Llm.Enqueue(Up.Call("c1", "echo"), Up.Text("done"));

        await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(2, turns.Count);
        Assert.Null(turns[0].LastUsage);
        Assert.Null(turns[0].LastUsageAnchor);
        Assert.Equal(Up.Usage, turns[1].LastUsage);
        Assert.Equal("u1", turns[1].LastUsageAnchor!.Value.Value); // 第一次请求发出时,转录的最后一条是用户输入
    }

    [Fact]
    public async Task The_user_input_is_committed_with_the_client_supplied_id_and_announced_with_start_and_end()
    {
        var rig = new Rig();
        rig.Llm.Enqueue(Up.Text("ok"));

        var run = await rig.RunAsync(Rig.User("client-id", "hi"));

        var start = run.Of<MessageStartEvent>().First();
        Assert.Equal(("client-id", Role.User), (start.MessageId.Value, start.Role));
        Assert.Equal("client-id", run.Of<MessageEndEvent>().First().MessageId.Value);
    }

    [Fact]
    public async Task The_assistant_message_carries_the_same_id_in_start_deltas_and_end_with_usage_cost_and_stop()
    {
        var rig = new Rig();
        rig.Llm.Enqueue(Up.Text("a"));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        var start = run.Of<MessageStartEvent>().Single(s => s.Role == Role.Assistant);
        var delta = run.Of<MessageDeltaEvent>().Single();
        var end = run.Of<MessageEndEvent>().Single(e => e.Message.Role == Role.Assistant);
        Assert.Equal(start.MessageId, delta.MessageId);
        Assert.Equal(start.MessageId, end.MessageId);
        Assert.Equal(("fake-model", "p"), (start.Model, start.ProviderId));
        Assert.Equal((MessageDeltaKind.Text, 0, "a"), (delta.Kind, delta.ContentIndex, delta.Delta));
        Assert.Equal(Up.Usage, end.Usage);
        Assert.Equal(StopReason.EndTurn, end.Stop);
        Assert.Equal(CostInfo.NotMetered, end.Cost);
    }

    [Fact]
    public async Task Thinking_and_tool_argument_deltas_are_forwarded_with_their_content_index()
    {
        var rig = new Rig().Tool(new FakeTool("echo"));
        rig.Llm.Enqueue(
            Up.Events(
                new MessageStart("m", "p", "anthropic", "up"),
                new ThinkingDelta(0, "hmm"),
                new ToolCallStart(1, "c1", "echo"),
                new ToolCallDelta(1, "c1", "{}"),
                new ToolCallEnd(1, "c1"),
                new MessageEnd(StopReason.ToolUse, Up.Usage)),
            Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        var deltas = run.Of<MessageDeltaEvent>().Take(3).ToList();
        Assert.Equal((MessageDeltaKind.Thinking, 0, "hmm"), (deltas[0].Kind, deltas[0].ContentIndex, deltas[0].Delta));
        // 工具调用的第一条增量带工具名(增量为空串),让界面在参数还在流式输出时就能画卡片。
        Assert.Equal((MessageDeltaKind.ToolArgs, 1, "c1", "echo", ""), (deltas[1].Kind, deltas[1].ContentIndex, deltas[1].CallId, deltas[1].ToolName, deltas[1].Delta));
        Assert.Equal("{}", deltas[2].Delta);
    }

    [Fact]
    public async Task A_tool_turn_then_a_text_turn_makes_two_requests_and_the_second_sees_the_result()
    {
        var rig = new Rig().Tool(new FakeTool("echo"));
        rig.Llm.Enqueue(Up.Call("c1", "echo", """{"x":1}"""), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(2, run.Llm.Requests.Count);
        Assert.Equal(["TurnStart", "TurnEnd", "TurnStart", "TurnEnd"], run.Kinds.Where(k => k.StartsWith("Turn", StringComparison.Ordinal)));
        var second = run.Llm.Requests[1].Messages;
        Pairing.AssertPaired(second);
        var result = Assert.Single(second[^1].Parts.OfType<ToolResultPart>());
        Assert.Equal(("c1", "echo:ok", false), (result.CallId, result.Output.Content, result.IsError));
        Assert.Equal(["u1", "id0001", "id0002", "id0003"], run.Committed.Select(m => m.Id.Value));
        Pairing.AssertPaired(run.Committed);
    }

    [Fact]
    public async Task The_tool_snapshot_of_the_plan_is_the_only_tool_list_the_model_sees_and_the_one_used_for_lookup()
    {
        var rig = new Rig().Tool(new FakeTool("echo"));
        rig.Prepare = (turn, _) =>
        {
            // 请求里自带的工具列表会被快照覆盖:不能出现「下发的列表里有,执行时却找不到」的错位。
            var request = new LlmRequest("s", turn.Transcript, [new ToolInfo("stale", "x", Ncw.Agent.Llm.Json.Jsn.EmptyObject)], turn.Options);
            return ValueTask.FromResult(TurnPlan.Run(request, rig.Snapshot()));
        };
        rig.Llm.Enqueue(Up.Text("hi"));

        await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(["echo"], rig.Llm.Requests[0].Tools.Select(t => t.ExternalName));
    }

    [Fact]
    public async Task The_request_context_carries_run_session_and_workspace_ids()
    {
        var rig = new Rig();
        rig.Llm.Enqueue(Up.Text("hi"));

        await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(new RequestContext("workspace-1", "run-1", "session-1"), rig.Llm.Contexts.Single());
    }

    [Fact]
    public async Task Empty_new_messages_means_continue_and_commits_no_user_message()
    {
        var rig = new Rig();
        rig.Context.Add(Rig.User("old", "earlier"));
        rig.Llm.Enqueue(Up.Text("continuing"));

        var run = await rig.RunAsync();

        Assert.Equal(["id0001"], run.Committed.Select(m => m.Id.Value));
        Assert.Equal(["old"], rig.Llm.Requests[0].Messages.Select(m => m.Id.Value));
    }

    [Fact]
    public async Task An_empty_assistant_message_is_never_committed_and_no_message_events_are_emitted_for_it()
    {
        var rig = new Rig();
        rig.Llm.Enqueue(Up.Events(new MessageStart("m"), new MessageEnd(StopReason.EndTurn, Up.Usage)));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Empty(run.Assistants);
        Assert.Single(run.Of<MessageEndEvent>()); // 只有用户消息
    }

    [Fact]
    public async Task An_assistant_message_that_started_streaming_but_ends_up_empty_is_discarded_not_left_dangling()
    {
        var rig = new Rig();
        rig.Llm.Enqueue(Up.Events(new MessageStart("m"), new TextDelta(0, ""), new MessageEnd(StopReason.EndTurn, Up.Usage)));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Empty(run.Assistants);
        Assert.Single(run.Of<MessageDiscardEvent>());
    }

    [Fact]
    public async Task There_is_no_turn_limit_and_the_loop_does_not_recurse()
    {
        // 一个跑几百轮的目标曾把 2.x 的 turn() 叠成几百层栈帧,最后死于栈溢出。
        const int turns = 1500;
        var rig = new Rig().Tool(new FakeTool("echo"));
        for (var i = 0; i < turns; i++)
        {
            rig.Llm.Enqueue(Up.Call($"c{i}", "echo"));
        }

        rig.Llm.Enqueue(Up.Text("finally"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(turns + 1, run.Of<TurnStartEvent>().Count());
        Assert.Equal(turns, run.Results.Count());
    }

    [Fact]
    public async Task PrepareTurn_can_stop_the_run_before_any_turn_starts()
    {
        var rig = new Rig();
        var error = AgentError.Create(AgentErrorCode.ContextLength, "too long", false);
        rig.Prepare = (_, _) => ValueTask.FromResult(TurnPlan.Stop(RunStatus.Error, error));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(["RunStart", "MessageStart", "MessageEnd", "RunEnd"], run.Kinds);
        Assert.Equal((RunStatus.Error, error), (run.End.Status, run.End.Error));
        Assert.Empty(run.Llm.Requests);
    }

    [Fact]
    public async Task Events_returned_by_PrepareTurn_are_emitted_before_the_turn_starts()
    {
        var rig = new Rig();
        rig.Prepare = (turn, _) => ValueTask.FromResult(TurnPlan.Run(
            new LlmRequest("s", turn.Transcript, [], turn.Options), null, [new NotificationEvent("ctx.usage", "42")]));
        rig.Llm.Enqueue(Up.Text("hi"));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        var kinds = run.Kinds.ToList();
        Assert.True(kinds.IndexOf("Notification") < kinds.IndexOf("TurnStart"));
    }

    [Fact]
    public async Task PrepareTurn_sees_the_growing_transcript_and_the_turn_index()
    {
        var rig = new Rig().Tool(new FakeTool("echo"));
        var seen = new List<(int Turn, int Count, int ToolCalls)>();
        rig.Prepare = (turn, _) =>
        {
            seen.Add((turn.TurnIndex, turn.Transcript.Count, turn.ToolCallsThisRun));
            return ValueTask.FromResult(TurnPlan.Run(new LlmRequest("s", turn.Transcript, [], turn.Options), rig.Snapshot()));
        };
        rig.Context.Add(Rig.User("old", "earlier"));
        rig.Llm.Enqueue(Up.Call("c1", "echo"), Up.Text("done"));

        await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal([(0, 2, 0), (1, 4, 1)], seen);
    }

    [Fact]
    public async Task BeforeProviderRequest_can_replace_the_final_request()
    {
        var rig = new Rig();
        var hooks = rig.Hooks();
        var patched = new LoopHooks
        {
            PrepareTurn = hooks.PrepareTurn,
            BeforeProviderRequest = (r, _) => ValueTask.FromResult(r with { System = "patched" }),
        };
        rig.Llm.Enqueue(Up.Text("hi"));

        await foreach (var _ in rig.Loop().RunAsync(rig.Input(Rig.User("u1", "hi")), patched))
        {
        }

        Assert.Equal("patched", rig.Llm.Requests[0].System);
    }

    [Fact]
    public async Task A_refusal_or_stop_sequence_ends_the_run_normally_without_asking_OnTurnEnd()
    {
        var rig = new Rig();
        var asked = 0;
        rig.OnTurnEnd = (_, _) =>
        {
            asked++;
            return ValueTask.FromResult(TurnEndDecision.Stop);
        };
        rig.Llm.Enqueue(Up.Text("no", StopReason.Refusal));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task Many_runs_can_share_one_loop_instance_concurrently()
    {
        var loop = new AgentLoop(new FakeLlm().Enqueue(Up.Text("a"), Up.Text("b")), new SequentialIds(), new FakeClock(), new RecordingDelayer());
        static LoopHooks Hooks() => new()
        {
            PrepareTurn = (t, _) => ValueTask.FromResult(TurnPlan.Run(new LlmRequest("s", t.Transcript, [], t.Options))),
        };
        async Task<RunStatus> One(string id)
        {
            var input = new LoopInput(new RunId(id), "s", "w", [], [Rig.User(id, "x")], ModelRef.Unpinned("a"), new GenerationOptions(10));
            RunStatus? status = null;
            await foreach (var ev in loop.RunAsync(input, Hooks()))
            {
                if (ev is RunEndEvent end)
                {
                    status = end.Status;
                }
            }

            return status!.Value;
        }

        var results = await Task.WhenAll(One("r1"), One("r2"));

        Assert.All(results, s => Assert.Equal(RunStatus.Done, s));
    }
}
