using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Tests.Support;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Tests;

/// <summary>错误、断流续传、输出上限(02 §2、§7)。★ 重试只在模型层(首字节之前);循环层只做「流中断续传」,且只在本回合未执行工具时。</summary>
public sealed class StreamFailureTests
{
    private static readonly TimeSpan[] DefaultBackoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(45)];

    // ───────────────────────── 出错 ─────────────────────────

    [Fact]
    public async Task A_stream_error_commits_the_partial_reply_with_an_error_part_and_ends_the_run_as_error()
    {
        var error = AgentError.Create(AgentErrorCode.Auth, "invalid key", false);
        var rig = new Rig();
        rig.Llm.Enqueue(Up.Fail(error, "par"));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal((RunStatus.Error, error), (run.End.Status, run.End.Error));
        var assistant = Assert.Single(run.Assistants);
        Assert.Equal("par", ((TextPart)assistant.Parts[0]).Text);
        Assert.Equal(error, Assert.IsType<ErrorPart>(assistant.Parts[^1]).Error);
        Assert.Equal("TurnEnd", run.Kinds[^2]); // 出错也关回合
    }

    [Fact]
    public async Task An_error_with_no_content_still_commits_a_message_holding_the_error_part_with_a_matching_start()
    {
        var rig = new Rig();
        rig.Llm.Enqueue(Up.Fail(AgentError.Create(AgentErrorCode.NoHealthyProvider, "all cooling", false)));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        var assistant = Assert.Single(run.Assistants);
        Assert.IsType<ErrorPart>(Assert.Single(assistant.Parts));
        Assert.Equal(RunStatus.Error, run.Status);
    }

    [Fact]
    public async Task Calls_stranded_by_a_stream_failure_after_their_end_get_an_error_result_so_the_pairing_holds()
    {
        // 流在第一个调用收尾之后才失败:调用块已经在助手消息里,必须补结果(2.x ★)。
        var rig = new Rig().Tool(new FakeTool("echo"));
        rig.Llm.Enqueue(Up.Events(
            new MessageStart("m", "p", "anthropic", "up"),
            new ToolCallStart(0, "c1", "echo"),
            new ToolCallEnd(0, "c1"),
            new StreamError(AgentError.Create(AgentErrorCode.Provider, "500", false))));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.Equal(LoopMessages.StreamFailed, run.ResultOf("c1").Output.Content);
        Assert.True(run.ResultOf("c1").IsError);
        Assert.Equal(0, ((FakeTool)rig.Tools[0]).Calls); // 出错的回合不执行工具
        Pairing.AssertPaired(run.Committed);
        Assert.Contains(run.Of<ToolEndEvent>(), e => e.CallId == "c1" && e.IsError);
    }

    [Fact]
    public async Task A_tool_call_cut_off_before_its_end_is_dropped_from_the_failed_message_so_it_cannot_orphan()
    {
        var rig = new Rig().Tool(new FakeTool("echo"));
        rig.Llm.Enqueue(Up.Events(
            new MessageStart("m", "p", "anthropic", "up"),
            new TextDelta(0, "ok"),
            new ToolCallStart(1, "half", "echo"),
            new ToolCallDelta(1, "half", "{\"a\":"),
            new StreamError(AgentError.Create(AgentErrorCode.Provider, "500", false))));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Empty(run.Assistants[0].Parts.OfType<ToolCallPart>());
        Assert.Empty(run.Results);
        Pairing.AssertPaired(run.Committed);
    }

    [Fact]
    public async Task An_upstream_that_throws_ends_the_run_as_error_without_the_exception_escaping_and_keeps_the_partial()
    {
        var rig = new Rig();
        rig.Llm.Enqueue(Up.Throws(new InvalidOperationException("upstream bug")));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.Equal(AgentErrorCode.Unknown, run.End.Error!.Code);
        Assert.Equal("partial", ((TextPart)run.Assistants[0].Parts[0]).Text);
    }

    [Fact]
    public async Task A_failing_PrepareTurn_ends_the_run_as_error_with_the_internal_key_and_does_not_throw()
    {
        var rig = new Rig();
        rig.Prepare = (_, _) => throw new InvalidOperationException("assembler bug");

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.Equal(LoopErrorKeys.Internal, run.End.Error!.MessageKey);
        Assert.Contains("assembler bug", run.End.Error.Message);
    }

    [Fact]
    public async Task A_failing_CommitMessage_ends_the_run_as_error_instead_of_losing_the_message_silently()
    {
        var rig = new Rig { Commit = (m, _) => m.Role == Role.Assistant ? throw new IOException("disk full") : ValueTask.CompletedTask };
        rig.Llm.Enqueue(Up.Text("hi"));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.Contains("disk full", run.End.Error!.Message);
    }

    [Fact]
    public async Task Cleanup_that_itself_fails_does_not_hide_the_real_outcome()
    {
        // 落库一直失败:工具结果提交时抛异常,收尾里补孤儿时又抛一次,Run 仍然以错误结束而不是卡住或抛出去。
        var rig = new Rig().Tool(new FakeTool("echo"));
        rig.Commit = (m, _) => m.Parts.OfType<ToolResultPart>().Any() ? throw new IOException("disk full") : ValueTask.CompletedTask;
        rig.Llm.Enqueue(Up.Call("c1", "echo"), Up.Text("never"));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.Single(run.Llm.Requests);
        Assert.NotEmpty(rig.Diagnostics.Warnings);
    }

    // ───────────────────────── 断流续传 ─────────────────────────

    [Fact]
    public async Task A_network_break_mid_stream_discards_the_half_reply_waits_and_asks_again_and_the_half_never_reaches_the_record()
    {
        var delayer = new RecordingDelayer();
        var rig = new Rig { Delayer = delayer };
        rig.Llm.Enqueue(Up.Fail(Up.NetworkError, "half an ans"), Up.Text("complete answer"));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(2, run.Llm.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1)], delayer.Delays);
        var assistant = Assert.Single(run.Assistants);
        Assert.Equal("complete answer", ((TextPart)assistant.Parts[0]).Text);
        // 界面被告知丢弃那一份增量,然后看到退避提示,然后是新的一份。
        var kinds = run.Kinds.SkipWhile(k => k != "MessageDiscard").ToList();
        Assert.Equal(["MessageDiscard", "ProviderRetry", "MessageStart"], kinds.Take(3));
        var retry = run.Of<ProviderRetryEvent>().Single();
        Assert.Equal((1, 1000, "connection reset"), (retry.Attempt, retry.DelayMs, retry.Reason));
        Assert.Single(run.Of<TurnStartEvent>()); // 仍然是同一个回合
        // 第二次请求里没有半截回答:历史一字未变。
        Assert.Equal(run.Llm.Requests[0].Messages.Select(m => m.Id), run.Llm.Requests[1].Messages.Select(m => m.Id));
    }

    [Fact]
    public async Task A_stream_that_ends_without_MessageEnd_and_without_an_error_counts_as_a_network_break_and_is_resumed()
    {
        var rig = new Rig();
        rig.Llm.Enqueue(Up.Cut(), Up.Text("ok"));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(2, run.Llm.Requests.Count);
        Assert.Single(run.Assistants);
    }

    [Fact]
    public async Task Resuming_uses_the_backoff_table_1_2_5_15_45_seconds_and_gives_up_after_the_last_entry()
    {
        var delayer = new RecordingDelayer();
        var rig = new Rig { Delayer = delayer };
        for (var i = 0; i < 6; i++)
        {
            rig.Llm.Enqueue(Up.Fail(Up.NetworkError));
        }

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(DefaultBackoff, delayer.Delays);
        Assert.Equal(6, run.Llm.Requests.Count); // 首次 + 5 次续传,不无限重来
        Assert.Equal((RunStatus.Error, AgentErrorCode.Network), (run.End.Status, run.End.Error!.Code));
        Assert.Equal(5, run.Of<ProviderRetryEvent>().Count());
        Assert.Single(run.Assistants); // 只有最后那次的错误消息
    }

    [Fact]
    public async Task With_an_empty_backoff_table_nothing_is_resumed()
    {
        var rig = new Rig { Options = new AgentLoopOptions { ResumeDelays = [] } };
        rig.Llm.Enqueue(Up.Fail(Up.NetworkError, "par"), Up.Text("never"));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.Single(run.Llm.Requests);
    }

    [Theory]
    [InlineData(AgentErrorCode.RateLimit)]
    [InlineData(AgentErrorCode.Provider)]
    [InlineData(AgentErrorCode.ContextLength)]
    [InlineData(AgentErrorCode.NoHealthyProvider)]
    [InlineData(AgentErrorCode.Auth)]
    public async Task Only_network_errors_are_resumed_everything_else_is_the_model_layers_business_and_ends_the_run(AgentErrorCode code)
    {
        // 限流、5xx、冷却中的供应商走模型层那条信息更全的路(认 Retry-After、会换供应商);这里再套一个盲循环只会长时间空转。
        var delayer = new RecordingDelayer();
        var rig = new Rig { Delayer = delayer };
        rig.Llm.Enqueue(Up.Fail(AgentError.Create(code, "x", retryable: true), "par"), Up.Text("never"));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.Single(run.Llm.Requests);
        Assert.Empty(delayer.Delays);
    }

    [Fact]
    public async Task A_resume_never_replays_tools_a_complete_tool_call_in_a_broken_attempt_is_discarded_not_executed()
    {
        var tool = new FakeTool("echo");
        var rig = new Rig().Tool(tool);
        rig.Llm.Enqueue(
            // 第一次:工具调用已经完整收尾,随后断流。它不能被执行——否则续传会把它再执行一遍。
            Up.Events(
                new MessageStart("m", "p", "anthropic", "up"),
                new ToolCallStart(0, "c-old", "echo"),
                new ToolCallEnd(0, "c-old"),
                new StreamError(Up.NetworkError)),
            Up.Call("c-new", "echo"),
            Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(1, tool.Calls);
        Assert.Equal("c-new", tool.Invocations.Single().CallId);
        Assert.Equal(["c-new"], run.Results.Select(r => r.CallId));
        Pairing.AssertPaired(run.Committed);
    }

    [Fact]
    public async Task The_tool_is_never_executed_twice_across_a_resume_inside_a_longer_run()
    {
        var tool = new FakeTool("echo");
        var rig = new Rig().Tool(tool);
        rig.Llm.Enqueue(Up.Call("c1", "echo"), Up.Fail(Up.NetworkError, "par"), Up.Text("done"));

        await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(1, tool.Calls);
    }

    // ───────────────────────── 输出上限 ─────────────────────────

    [Fact]
    public async Task Hitting_the_output_limit_with_tool_calls_fails_every_call_with_a_resend_hint_and_executes_none()
    {
        var a = new FakeTool("a");
        var b = new FakeTool("b");
        var rig = new Rig().Tool(a).Tool(b);
        rig.Llm.Enqueue(
            Up.Events(Up.CallEvents("long", StopReason.MaxTokens, ("c1", "a", "{\"x\":1}"), ("c2", "b", "{\"y\":")).ToArray()),
            Up.Call("c3", "a", "{\"x\":1}"),
            Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal((1, 0), (a.Calls, b.Calls)); // 第一回合两个调用都没执行(哪怕 c1 的参数看起来完整);只有重发的 c3 执行了
        Assert.Equal(LoopMessages.OutputLimit, run.ResultOf("c1").Output.Content);
        Assert.Equal(LoopMessages.OutputLimit, run.ResultOf("c2").Output.Content);
        Assert.True(run.ResultOf("c1").IsError && run.ResultOf("c2").IsError);
        Assert.Equal(3, run.Llm.Requests.Count); // 模型拿到提示后有机会重发
        Assert.Contains("Re-issue", LoopMessages.OutputLimit);
        Assert.Equal(["c1", "c2"], run.Of<ToolEndEvent>().Take(2).Select(e => e.CallId));
        Pairing.AssertPaired(run.Committed);
    }

    [Fact]
    public async Task The_output_limit_error_results_are_one_message_in_call_order_and_the_assistant_message_is_kept()
    {
        var rig = new Rig().Tool(new FakeTool("a"));
        rig.Llm.Enqueue(Up.Truncated("c1", "a", "{\"x\":"), Up.Text("ok"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        Assert.Equal(StopReason.MaxTokens, run.Assistants[0].Stop);
        Assert.NotNull(run.Assistants[0].Parts.OfType<ToolCallPart>().Single().InputError);
        Assert.Single(run.Committed, m => m.Parts.OfType<ToolResultPart>().Any());
    }

    [Fact]
    public async Task Hitting_the_output_limit_without_tool_calls_is_an_error_with_a_translatable_key()
    {
        var rig = new Rig();
        rig.Llm.Enqueue(Up.Text("cut off mid-sen", StopReason.MaxTokens));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.Equal((AgentErrorCode.Provider, LoopErrorKeys.OutputLimit, false), (run.End.Error!.Code, run.End.Error.MessageKey, run.End.Error.Retryable));
        Assert.IsType<ErrorPart>(run.Assistants[0].Parts[^1]); // 回放时整条跳过
        Assert.Equal("cut off mid-sen", ((TextPart)run.Assistants[0].Parts[0]).Text);
    }

    // ───────────────────────── 被动压缩(RecoverContextLength) ─────────────────────────

    [Fact]
    public async Task A_context_length_rejection_with_no_content_asks_the_hook_once_then_retries_the_turn()
    {
        var recovered = 0;
        var rig = new Rig { RecoverContextLength = (_, _) => { recovered++; return ValueTask.FromResult(true); } };
        rig.Llm.Enqueue(Up.Fail(AgentError.Create(AgentErrorCode.ContextLength, "prompt is too long")), Up.Text("ok"));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(1, recovered);
        Assert.Equal(2, run.Llm.Requests.Count);
        Assert.DoesNotContain(run.Assistants, m => m.Parts.Any(p => p is ErrorPart)); // 被拒的那一次不留错误消息
    }

    [Fact]
    public async Task The_hook_is_asked_at_most_once_per_turn_and_a_second_rejection_is_reported()
    {
        var recovered = 0;
        var rig = new Rig { RecoverContextLength = (_, _) => { recovered++; return ValueTask.FromResult(true); } };
        var tooLong = AgentError.Create(AgentErrorCode.ContextLength, "prompt is too long");
        rig.Llm.Enqueue(Up.Fail(tooLong), Up.Fail(tooLong));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(1, recovered);
        Assert.Equal(RunStatus.Error, run.Status);
        Assert.Equal(AgentErrorCode.ContextLength, run.End.Error!.Code);
    }

    [Fact]
    public async Task A_context_length_rejection_after_content_started_is_not_recoverable()
    {
        var recovered = 0;
        var rig = new Rig { RecoverContextLength = (_, _) => { recovered++; return ValueTask.FromResult(true); } };
        rig.Llm.Enqueue(Up.Fail(AgentError.Create(AgentErrorCode.ContextLength, "too long"), partial: "some text"));

        var run = await rig.RunAsync(Rig.User("u1", "hi"));

        Assert.Equal(0, recovered);
        Assert.Equal(RunStatus.Error, run.Status);
    }

    [Fact]
    public async Task A_hook_that_declines_or_throws_leaves_the_original_error()
    {
        foreach (var decline in new Func<TurnContext, CancellationToken, ValueTask<bool>>[]
                 {
                     (_, _) => ValueTask.FromResult(false),
                     (_, _) => throw new InvalidOperationException("boom"),
                 })
        {
            var rig = new Rig { RecoverContextLength = decline };
            rig.Llm.Enqueue(Up.Fail(AgentError.Create(AgentErrorCode.ContextLength, "too long")));

            var run = await rig.RunAsync(Rig.User("u1", "hi"));

            Assert.Equal(RunStatus.Error, run.Status);
            Assert.Equal(AgentErrorCode.ContextLength, run.End.Error!.Code);
        }
    }

    // ───────────────────────── 孤儿修复 ─────────────────────────

    [Fact]
    public async Task Orphans_left_by_a_crashed_run_are_closed_before_the_new_user_message_and_never_after_it()
    {
        var rig = new Rig { AllowUnstartedToolEnd = true };
        rig.Context.Add(Rig.User("old-u", "earlier"));
        rig.Context.Add(new Message(new MessageId("old-a"), Role.Assistant, [new ToolCallPart("lost", "echo", Ncw.Agent.Llm.Json.Jsn.EmptyObject)], DateTimeOffset.UnixEpoch));
        rig.Llm.Enqueue(Up.Text("ok"));

        var run = await rig.RunAsync(Rig.User("new-u", "next"));

        // 提交顺序:修复结果 → 新用户消息 → 新助手消息。新消息不能插在调用和结果之间。
        Assert.Equal(3, run.Committed.Count);
        Assert.Equal(LoopMessages.RecoveredInterrupted, ((ToolResultPart)run.Committed[0].Parts[0]).Output.Content);
        Assert.Equal("new-u", run.Committed[1].Id.Value);
        var sent = run.Llm.Requests[0].Messages;
        Pairing.AssertPaired(sent);
        Assert.Equal(["old-u", "old-a"], sent.Take(2).Select(m => m.Id.Value));
        Assert.Equal(Role.User, sent[2].Role);
        Assert.Equal("lost", Assert.IsType<ToolResultPart>(sent[2].Parts[0]).CallId);
        Assert.Contains(run.Of<ToolEndEvent>(), e => e.CallId == "lost" && e.IsError);
        Assert.DoesNotContain(run.Of<ToolStartEvent>(), e => e.CallId == "lost"); // 上个 Run 的卡片在历史里,这里只补 end
    }

    [Fact]
    public async Task Repair_is_idempotent_a_paired_history_gets_no_extra_message()
    {
        var rig = new Rig();
        rig.Context.Add(new Message(new MessageId("a"), Role.Assistant, [new ToolCallPart("c", "echo", Ncw.Agent.Llm.Json.Jsn.EmptyObject)], DateTimeOffset.UnixEpoch));
        rig.Context.Add(new Message(new MessageId("r"), Role.User, [new ToolResultPart("c", new ToolOutput("fine"), false)], DateTimeOffset.UnixEpoch));
        rig.Llm.Enqueue(Up.Text("ok"));

        var run = await rig.RunAsync(Rig.User("u1", "next"));

        Assert.Equal(["u1", "id0001"], run.Committed.Select(m => m.Id.Value));
    }

    [Fact]
    public void Orphans_Find_reports_calls_without_any_result_in_order()
    {
        Message Assistant(params string[] ids) => new(new MessageId("a" + string.Concat(ids)), Role.Assistant, ids.Select(i => (Part)new ToolCallPart(i, "t", Ncw.Agent.Llm.Json.Jsn.EmptyObject)).ToList(), DateTimeOffset.UnixEpoch);
        var result = new Message(new MessageId("r"), Role.User, [new ToolResultPart("b", new ToolOutput("x"), false)], DateTimeOffset.UnixEpoch);

        var orphans = Orphans.Find([Assistant("a", "b", "c"), result]);

        Assert.Equal(["a", "c"], orphans.Select(o => o.CallId));
    }
}
