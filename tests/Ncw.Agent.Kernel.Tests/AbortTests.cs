using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Tests.Support;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Tests;

/// <summary>中止(02 §6)。一次中止要完成:取消上游、取消工具、给没有结果的调用补「已中止」(漏掉会让下一次请求 400)。</summary>
public sealed class AbortTests
{
    /// <summary>在条件成立后取消,并等待结果。</summary>
    private static async Task<RunResult> RunUntilAsync(Rig rig, Task trigger, CancellationTokenSource cts, params Message[] input)
    {
        _ = trigger.ContinueWith(_ => cts.Cancel(), TaskScheduler.Default);
        return await rig.RunAsync(cts.Token, input);
    }

    [Fact]
    public async Task Aborting_the_stream_commits_the_half_reply_marked_Aborted_and_ends_the_run_as_aborted()
    {
        var rig = new Rig();
        rig.Llm.Enqueue(Up.Hang("half an ans"));
        using var cts = new CancellationTokenSource();
        var seen = new TaskCompletionSource();
        rig.Commit = (_, _) => ValueTask.CompletedTask;

        var events = new List<AgentEvent>();
        await foreach (var ev in rig.Loop().RunAsync(rig.Input(Rig.User("u1", "hi")), rig.Hooks(), cts.Token))
        {
            events.Add(ev);
            if (ev is MessageDeltaEvent)
            {
                cts.Cancel(); // 用户看到了第一段字之后点了停止
            }
        }

        EventRules.AssertWellFormed(events);
        var end = events.OfType<RunEndEvent>().Single();
        Assert.Equal((RunStatus.Aborted, null), (end.Status, end.Error));
        var partial = Assert.Single(rig.Committed, m => m.Role == Role.Assistant);
        Assert.Equal(StopReason.Aborted, partial.Stop);
        Assert.Equal("half an ans", ((TextPart)partial.Parts[0]).Text); // 用户已经读到的字不会凭空消失
    }

    [Fact]
    public async Task A_stalled_upstream_that_ignores_cancellation_cannot_hold_up_the_abort()
    {
        var rig = new Rig();
        rig.Llm.Enqueue(Up.HangDeaf());
        using var cts = new CancellationTokenSource();

        var events = new List<AgentEvent>();
        await foreach (var ev in rig.Loop().RunAsync(rig.Input(Rig.User("u1", "hi")), rig.Hooks(), cts.Token))
        {
            events.Add(ev);
            if (ev is MessageDeltaEvent)
            {
                cts.CancelAfter(50);
            }
        }

        Assert.Equal(RunStatus.Aborted, events.OfType<RunEndEvent>().Single().Status);
        Assert.Contains(rig.Committed, m => m.Stop == StopReason.Aborted);
    }

    [Fact]
    public async Task An_unfinished_tool_call_in_the_half_reply_is_dropped_so_it_cannot_become_an_orphan()
    {
        var rig = new Rig().Tool(new FakeTool("echo"));
        rig.Llm.Enqueue(Up.HangInsideToolCall());
        using var cts = new CancellationTokenSource();

        var events = new List<AgentEvent>();
        await foreach (var ev in rig.Loop().RunAsync(rig.Input(Rig.User("u1", "hi")), rig.Hooks(), cts.Token))
        {
            events.Add(ev);
            if (ev is MessageDeltaEvent { Kind: MessageDeltaKind.ToolArgs, Delta: not "" })
            {
                cts.Cancel();
            }
        }

        EventRules.AssertWellFormed(events);
        var partial = Assert.Single(rig.Committed, m => m.Role == Role.Assistant);
        Assert.Empty(partial.Parts.OfType<ToolCallPart>());
        Assert.Equal("let me", ((TextPart)partial.Parts[0]).Text);
        Assert.Empty(Orphans.Find(rig.Committed));
        Assert.Equal(RunStatus.Aborted, events.OfType<RunEndEvent>().Single().Status);
    }

    [Fact]
    public async Task Aborting_while_a_tool_runs_does_not_wrap_the_cancellation_and_the_unfinished_call_gets_an_aborted_result()
    {
        var started = new TaskCompletionSource();
        // 这个工具完全不理取消令牌:中止仍然要立刻生效。
        var tool = new FakeTool("stuck", async (_, _, _) =>
        {
            started.SetResult();
            await new TaskCompletionSource().Task;
            return ToolResult.Text("never");
        });
        var rig = new Rig().Tool(tool);
        rig.Llm.Enqueue(Up.Call("c1", "stuck"), Up.Text("must not be requested"));
        using var cts = new CancellationTokenSource();

        var run = await RunUntilAsync(rig, started.Task, cts, Rig.User("u1", "go"));

        Assert.Equal(RunStatus.Aborted, run.Status);
        Assert.Single(run.Llm.Requests); // 没有第二次请求:模型没有看到「工具失败」然后继续往下做
        var result = run.ResultOf("c1");
        Assert.True(result.IsError);
        Assert.Equal(LoopMessages.Interrupted, result.Output.Content);
        Assert.NotEqual(LoopMessages.ToolFailed(new OperationCanceledException()), result.Output.Content);
        Assert.True(run.Of<ToolEndEvent>().Single().IsError); // tool_end 一定发出
        Pairing.AssertPaired(run.Committed);
    }

    [Fact]
    public async Task Results_of_tools_that_finished_before_the_abort_are_committed_with_their_real_values()
    {
        // 第 1 个已经真的做完了;不落进记录的话它会被当成孤儿补成「已中断」,模型下一轮据此重做一遍(2.x ★)。
        var bDone = new TaskCompletionSource();
        var a = new FakeTool("a", async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return ToolResult.Text("never");
        });
        var b = new FakeTool("b", (_, _, _) =>
        {
            bDone.TrySetResult();
            return Task.FromResult(ToolResult.Text("b really finished"));
        });
        var rig = new Rig().Tool(a).Tool(b);
        rig.Llm.Enqueue(Up.Calls(("c1", "a", "{}"), ("c2", "b", "{}")));
        using var cts = new CancellationTokenSource();

        var run = await RunUntilAsync(rig, bDone.Task, cts, Rig.User("u1", "go"));

        Assert.Equal(RunStatus.Aborted, run.Status);
        var message = Assert.Single(run.Committed, m => m.Parts.OfType<ToolResultPart>().Any());
        Assert.Equal(["c1", "c2"], message.Parts.OfType<ToolResultPart>().Select(r => r.CallId)); // 仍按调用顺序、一条消息
        Assert.Equal(LoopMessages.Interrupted, run.ResultOf("c1").Output.Content);
        Assert.Equal("b really finished", run.ResultOf("c2").Output.Content);
        Assert.False(run.ResultOf("c2").IsError);
    }

    [Fact]
    public async Task Calls_that_had_not_started_when_the_abort_arrived_in_a_sequential_batch_get_aborted_results()
    {
        var started = new TaskCompletionSource();
        var first = new FakeTool("first", async (_, _, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return ToolResult.Text("x");
        }, new ToolTraits(false, true, false, ConcurrencySafe: false));
        var second = new FakeTool("second");
        var rig = new Rig().Tool(first).Tool(second);
        rig.Llm.Enqueue(Up.Calls(("c1", "first", "{}"), ("c2", "second", "{}")));
        using var cts = new CancellationTokenSource();

        var run = await RunUntilAsync(rig, started.Task, cts, Rig.User("u1", "go"));

        Assert.Equal(0, second.Calls); // 遇到中止立即停,后面的不再开始
        Assert.Equal(LoopMessages.Interrupted, run.ResultOf("c1").Output.Content);
        Assert.Equal(LoopMessages.Interrupted, run.ResultOf("c2").Output.Content);
        Pairing.AssertPaired(run.Committed);
    }

    [Fact]
    public async Task Aborting_while_waiting_for_an_approval_closes_every_call_of_the_batch()
    {
        var asked = new TaskCompletionSource();
        var rig = new Rig().Tool(new FakeTool("a")).Tool(new FakeTool("b"));
        rig.BeforeTool = async (call, ct) =>
        {
            asked.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct); // 一直等用户回答
            return PreflightDecision.Allow;
        };
        rig.Llm.Enqueue(Up.Calls(("c1", "a", "{}"), ("c2", "b", "{}")));
        using var cts = new CancellationTokenSource();

        var run = await RunUntilAsync(rig, asked.Task, cts, Rig.User("u1", "go"));

        Assert.Equal(RunStatus.Aborted, run.Status);
        Assert.All(["c1", "c2"], id => Assert.Equal(LoopMessages.Interrupted, run.ResultOf(id).Output.Content));
        Assert.Equal(2, run.Of<ToolEndEvent>().Count());
        Pairing.AssertPaired(run.Committed);
    }

    [Fact]
    public async Task An_approval_hook_that_ignores_the_token_cannot_hold_up_the_abort()
    {
        var asked = new TaskCompletionSource();
        var rig = new Rig().Tool(new FakeTool("a"));
        rig.BeforeTool = async (_, _) =>
        {
            asked.TrySetResult();
            await new TaskCompletionSource().Task;
            return PreflightDecision.Allow;
        };
        rig.Llm.Enqueue(Up.Call("c1", "a"));
        using var cts = new CancellationTokenSource();

        var run = await RunUntilAsync(rig, asked.Task, cts, Rig.User("u1", "go"));

        Assert.Equal(RunStatus.Aborted, run.Status);
        Pairing.AssertPaired(run.Committed);
    }

    [Fact]
    public async Task Aborting_during_the_resume_backoff_ends_at_once_without_another_request()
    {
        var delayer = new BlockingDelayer();
        var rig = new Rig { Delayer = delayer };
        rig.Llm.Enqueue(Up.Fail(Up.NetworkError, "par"), Up.Text("never"));
        using var cts = new CancellationTokenSource();

        var run = await RunUntilAsync(rig, delayer.Entered.Task, cts, Rig.User("u1", "hi"));

        Assert.Equal(RunStatus.Aborted, run.Status);
        Assert.Single(run.Llm.Requests);
        Assert.Empty(run.Assistants); // 被丢弃的那半截不进记录
    }

    [Fact]
    public async Task Aborting_before_the_first_event_leaves_no_assistant_message_and_still_ends_cleanly()
    {
        var rig = new Rig();
        rig.Llm.Enqueue(Up.Text("never"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var run = await rig.RunAsync(cts.Token, Rig.User("u1", "hi"));

        Assert.Equal(RunStatus.Aborted, run.Status);
        Assert.Empty(run.Assistants);
        Assert.Equal(["u1"], run.Committed.Select(m => m.Id.Value));
    }

    [Fact]
    public async Task Abandoning_the_enumeration_early_is_an_abort_and_still_runs_the_cleanup()
    {
        // 消费者(编排层)提前放弃枚举:Run 要走中止收尾(补结果),而不是把调用留在记录里没有结果。
        var started = new TaskCompletionSource();
        var rig = new Rig().Tool(new FakeTool("stuck", async (_, _, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return ToolResult.Text("x");
        }));
        rig.Llm.Enqueue(Up.Call("c1", "stuck"));

        await using (var enumerator = rig.Loop().RunAsync(rig.Input(Rig.User("u1", "go")), rig.Hooks()).GetAsyncEnumerator())
        {
            while (await enumerator.MoveNextAsync())
            {
                if (enumerator.Current is ToolStartEvent)
                {
                    await started.Task.WaitAsync(TimeSpan.FromSeconds(10)); // 工具真的开始执行了
                    break;
                }
            }
        } // 离开 using = 提前放弃

        Pairing.AssertPaired(rig.Committed);
        Assert.Equal(LoopMessages.Interrupted, rig.Committed.SelectMany(m => m.Parts).OfType<ToolResultPart>().Single().Output.Content);
    }

    [Fact]
    public async Task Cancellation_requested_while_the_loop_is_inside_OnTurnEnd_ends_the_run_as_aborted()
    {
        var entered = new TaskCompletionSource();
        var rig = new Rig();
        rig.OnTurnEnd = async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return TurnEndDecision.Stop;
        };
        rig.Llm.Enqueue(Up.Text("hi"));
        using var cts = new CancellationTokenSource();

        var run = await RunUntilAsync(rig, entered.Task, cts, Rig.User("u1", "hi"));

        Assert.Equal(RunStatus.Aborted, run.Status);
        Assert.Contains(run.Assistants, m => m.Stop == StopReason.EndTurn); // 已经完成的回答保留
    }
}
