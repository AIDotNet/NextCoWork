using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Tests.Support;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Tests;

/// <summary>引导消息(steer)与回合末判定(02 §2、§5)。语义按 2.x:取走即消费、id 等于队列项 id、插在工具结果之后。</summary>
public sealed class SteeringTests
{
    private static Message Steer(string id, string text = "steer") => Rig.User(id, text);

    /// <summary>「用户在工具跑着的时候点了插话」:用工具的执行当投递时机,正好落在一轮之内(上游已答完、工具结果还没落盘)。</summary>
    private static (Rig Rig, Queue<Message> Mailbox) WithMailbox()
    {
        var mailbox = new Queue<Message>();
        var rig = new Rig();
        rig.TakeSteering = () =>
        {
            var taken = mailbox.ToList();
            mailbox.Clear(); // 取走即消费
            return taken;
        };
        return (rig, mailbox);
    }

    [Fact]
    public async Task A_steer_posted_during_a_tool_batch_is_injected_after_the_tool_results_and_before_the_next_request()
    {
        var (rig, mailbox) = WithMailbox();
        rig.Tool(new FakeTool("echo", (_, _, _) =>
        {
            mailbox.Enqueue(Steer("q1", "look at the cache hit rate first"));
            return Task.FromResult(ToolResult.Text("ok"));
        }));
        rig.Llm.Enqueue(Up.Call("c1", "echo"), Up.Text("noted"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        var ids = run.Committed.Select(m => m.Id.Value).ToList();
        var resultIndex = run.Committed.ToList().FindIndex(m => m.Parts.OfType<ToolResultPart>().Any());
        Assert.Equal(resultIndex + 1, ids.IndexOf("q1")); // 紧跟在工具结果之后,不会把调用和结果拆开
        Pairing.AssertPaired(run.Llm.Requests[1].Messages);
        Assert.Contains(run.Llm.Requests[1].Messages, m => m.Id.Value == "q1"); // 同一个 Run 的下一次请求里就带着
    }

    [Fact]
    public async Task The_steer_message_id_is_the_queue_item_id_and_delivery_is_the_message_end_with_that_id()
    {
        var (rig, mailbox) = WithMailbox();
        rig.Tool(new FakeTool("echo", (_, _, _) =>
        {
            mailbox.Enqueue(Steer("q1"));
            return Task.FromResult(ToolResult.Text("ok"));
        }));
        rig.Llm.Enqueue(Up.Call("c1", "echo"), Up.Text("noted"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        var delivered = run.Of<MessageEndEvent>().Single(e => e.MessageId.Value == "q1");
        Assert.Equal(Role.User, delivered.Message.Role);
        Assert.Single(run.Of<MessageStartEvent>(), e => e.MessageId.Value == "q1");
    }

    [Fact]
    public async Task A_steer_is_injected_exactly_once_even_over_many_turn_boundaries()
    {
        var (rig, mailbox) = WithMailbox();
        var posted = false;
        rig.Tool(new FakeTool("echo", (_, _, _) =>
        {
            if (!posted)
            {
                posted = true;
                mailbox.Enqueue(Steer("q1"));
            }

            return Task.FromResult(ToolResult.Text("ok"));
        }));
        rig.Llm.Enqueue(Up.Call("c1", "echo"), Up.Call("c2", "echo"), Up.Call("c3", "echo"), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        Assert.Single(run.Committed, m => m.Id.Value == "q1");
        Assert.Empty(mailbox);
    }

    [Fact]
    public async Task Several_steers_are_committed_one_by_one_in_order_not_merged()
    {
        var (rig, mailbox) = WithMailbox();
        rig.Tool(new FakeTool("echo", (_, _, _) =>
        {
            mailbox.Enqueue(Steer("q1", "one"));
            mailbox.Enqueue(Steer("q2", "two"));
            return Task.FromResult(ToolResult.Text("ok"));
        }));
        rig.Llm.Enqueue(Up.Call("c1", "echo"), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        var ids = run.Committed.Select(m => m.Id.Value).ToList();
        Assert.True(ids.IndexOf("q1") < ids.IndexOf("q2"));
        Assert.Equal(ids.IndexOf("q1") + 1, ids.IndexOf("q2"));
    }

    [Fact]
    public async Task A_steer_with_no_content_is_skipped_because_an_empty_message_would_be_rejected_upstream()
    {
        var (rig, mailbox) = WithMailbox();
        rig.Tool(new FakeTool("echo", (_, _, _) =>
        {
            mailbox.Enqueue(new Message(new MessageId("q-empty"), Role.User, [], DateTimeOffset.UnixEpoch));
            return Task.FromResult(ToolResult.Text("ok"));
        }));
        rig.Llm.Enqueue(Up.Call("c1", "echo"), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        Assert.DoesNotContain(run.Committed, m => m.Id.Value == "q-empty");
    }

    [Fact]
    public async Task A_steer_does_not_interrupt_a_running_tool_batch_it_waits_for_the_boundary()
    {
        var (rig, mailbox) = WithMailbox();
        var order = new List<string>();
        rig.Tool(new FakeTool("slow", async (_, _, _) =>
        {
            mailbox.Enqueue(Steer("q1"));
            await Task.Delay(50);
            order.Add("tool finished");
            return ToolResult.Text("ok");
        }));
        rig.Commit = (m, _) =>
        {
            order.Add("commit " + m.Id.Value);
            return ValueTask.CompletedTask;
        };
        rig.Llm.Enqueue(Up.Call("c1", "slow"), Up.Text("done"));

        await rig.RunAsync(Rig.User("u1", "go"));

        Assert.True(order.IndexOf("tool finished") < order.IndexOf("commit q1"));
    }

    [Fact]
    public async Task A_steer_posted_while_the_model_is_about_to_stop_continues_the_run_without_asking_OnTurnEnd()
    {
        // 队列里已经有活要干,不是停。也不必问判定器(2.x ★:否则白花一次目标判定)。
        var (rig, mailbox) = WithMailbox();
        var asked = 0;
        rig.OnTurnEnd = (_, _) =>
        {
            asked++;
            return ValueTask.FromResult(TurnEndDecision.Stop);
        };
        rig.Llm.Enqueue(
            Up.Custom((_, _) =>
            {
                mailbox.Enqueue(Steer("q1", "one more thing"));
                return Up.Text("first answer")(null!, default);
            }),
            Up.Text("handled the extra"));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(2, run.Llm.Requests.Count);
        Assert.Contains(run.Llm.Requests[1].Messages, m => m.Id.Value == "q1");
        Assert.Equal(1, asked); // 只有最后那次 end_turn 问了
        Assert.Equal(RunStatus.Done, run.Status);
    }

    [Fact]
    public async Task A_steer_that_arrives_while_OnTurnEnd_is_deciding_still_gets_through()
    {
        var (rig, mailbox) = WithMailbox();
        var posted = false;
        rig.OnTurnEnd = (_, _) =>
        {
            if (!posted)
            {
                posted = true;
                mailbox.Enqueue(Steer("q-late"));
            }

            return ValueTask.FromResult(TurnEndDecision.Stop);
        };
        rig.Llm.Enqueue(Up.Text("first"), Up.Text("second"));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(2, run.Llm.Requests.Count);
        Assert.Contains(run.Committed, m => m.Id.Value == "q-late");
    }

    [Fact]
    public async Task OnTurnEnd_Finish_ends_the_run_at_once_and_leaves_pending_steers_in_the_queue()
    {
        var (rig, mailbox) = WithMailbox();
        rig.OnTurnEnd = (_, _) =>
        {
            mailbox.Enqueue(Steer("q-left"));
            return ValueTask.FromResult(TurnEndDecision.Finish);
        };
        rig.Llm.Enqueue(Up.Text("first"), Up.Text("never"));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Single(run.Llm.Requests);
        Assert.Single(mailbox); // 没被消费,留给后续的 Run
    }

    // ───────────────────────── OnTurnEnd ─────────────────────────

    [Fact]
    public async Task OnTurnEnd_can_ask_to_continue_with_an_internal_message_and_the_streak_counters_advance()
    {
        var seen = new List<(int Turn, int Streak, bool Hook, int ToolCalls)>();
        var rig = new Rig();
        rig.OnTurnEnd = (end, _) =>
        {
            seen.Add((end.TurnIndex, end.StoppedTurnStreak, end.StopHookActive, end.ToolCallsThisRun));
            return ValueTask.FromResult(end.TurnIndex < 2
                ? TurnEndDecision.Continue(Rig.User("goal-check" + end.TurnIndex, "the goal is not met yet"), new LoopNotice("goal.notMet", "2 left"))
                : TurnEndDecision.Stop);
        };
        rig.Llm.Enqueue(Up.Text("a"), Up.Text("b"), Up.Text("c"));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal([(0, 0, false, 0), (1, 1, true, 0), (2, 2, true, 0)], seen);
        Assert.Equal(3, run.Of<TurnStartEvent>().Count());
        var internalMessage = run.Committed.Single(m => m.Id.Value == "goal-check0");
        Assert.True(internalMessage.Internal);
        Assert.Equal(Role.User, internalMessage.Role);
        Assert.Contains(run.Of<NotificationEvent>(), n => n is { Key: "goal.notMet", Detail: "2 left" });
        Assert.Equal(RunStatus.Done, run.Status);
    }

    [Fact]
    public async Task The_streak_resets_once_the_model_executes_a_tool()
    {
        var streaks = new List<int>();
        var rig = new Rig().Tool(new FakeTool("echo"));
        rig.OnTurnEnd = (end, _) =>
        {
            streaks.Add(end.StoppedTurnStreak);
            return ValueTask.FromResult(end.TurnIndex == 0 ? TurnEndDecision.Continue(Rig.User("nudge", "keep going")) : TurnEndDecision.Stop);
        };
        rig.Llm.Enqueue(Up.Text("idle"), Up.Call("c1", "echo"), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal([0, 0], streaks);
        Assert.Single(run.Results);
    }

    [Fact]
    public async Task OnTurnEnd_throwing_is_swallowed_and_the_run_just_ends_instead_of_retrying_forever()
    {
        var rig = new Rig();
        var calls = 0;
        rig.OnTurnEnd = (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("goal evaluator bug");
        };
        rig.Llm.Enqueue(Up.Text("answer"), Up.Text("must not be requested"));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Single(run.Llm.Requests);
        Assert.Equal(1, calls);
        Assert.Single(rig.Diagnostics.Warnings);
    }

    [Fact]
    public async Task OnTurnEnd_is_not_asked_while_the_model_is_still_calling_tools()
    {
        var asked = new List<int>();
        var rig = new Rig().Tool(new FakeTool("echo"));
        rig.OnTurnEnd = (end, _) =>
        {
            asked.Add(end.TurnIndex);
            return ValueTask.FromResult(TurnEndDecision.Stop);
        };
        rig.Llm.Enqueue(Up.Call("c1", "echo"), Up.Call("c2", "echo"), Up.Text("done"));

        await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal([2], asked);
    }

    [Fact]
    public async Task Continue_without_a_non_empty_message_degrades_to_a_stop_because_resending_the_same_request_would_loop_forever()
    {
        var rig = new Rig();
        rig.OnTurnEnd = (_, _) => ValueTask.FromResult(new TurnEndDecision(TurnEndAction.Continue));
        rig.Llm.Enqueue(Up.Text("answer"), Up.Text("never"));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Single(run.Llm.Requests);
        Assert.Equal(RunStatus.Done, run.Status);
    }

    [Fact]
    public async Task The_assistant_message_and_transcript_are_given_to_OnTurnEnd()
    {
        TurnEnd? captured = null;
        var rig = new Rig();
        rig.OnTurnEnd = (end, _) =>
        {
            captured = end;
            return ValueTask.FromResult(TurnEndDecision.Stop);
        };
        rig.Llm.Enqueue(Up.Text("final"));

        await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal("final", ((TextPart)captured!.Assistant.Parts[0]).Text);
        Assert.Equal(StopReason.EndTurn, captured.Stop);
        Assert.Equal(["u1", "id0001"], captured.Transcript.Select(m => m.Id.Value));
    }
}
