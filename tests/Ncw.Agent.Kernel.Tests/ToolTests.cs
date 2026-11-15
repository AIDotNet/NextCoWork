using System.Text.Json;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Tests.Support;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Json;

namespace Ncw.Agent.Kernel.Tests;

/// <summary>工具协议与执行(02 §4):顺序预检、并行执行、错误收敛、截断、AfterTool、Terminate、进度。</summary>
public sealed class ToolTests
{
    private static ToolTraits Unsafe => new(ReadOnly: false, Destructive: true, NeedsNetwork: false, ConcurrencySafe: false);

    // ───────────────────────── 并行与顺序 ─────────────────────────

    [Fact]
    public async Task Tools_in_one_batch_run_in_parallel_and_results_keep_the_call_order_in_one_message()
    {
        // a 要等 b 开始之后才能结束:只有并行执行才不会死锁;完成顺序是 b、a,写回顺序仍是 a、b。
        var bStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = new FakeTool("a", async (_, _, ct) =>
        {
            await bStarted.Task.WaitAsync(ct);
            return ToolResult.Text("A");
        });
        var b = new FakeTool("b", (_, _, _) =>
        {
            bStarted.SetResult();
            return Task.FromResult(ToolResult.Text("B"));
        });
        var rig = new Rig().Tool(a).Tool(b);
        rig.Llm.Enqueue(Up.Calls(("c1", "a", "{}"), ("c2", "b", "{}")), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        var resultMessages = run.Committed.Where(m => m.Parts.OfType<ToolResultPart>().Any()).ToList();
        var single = Assert.Single(resultMessages);
        Assert.Equal(["c1", "c2"], single.Parts.OfType<ToolResultPart>().Select(r => r.CallId));
        Assert.Equal(["A", "B"], single.Parts.OfType<ToolResultPart>().Select(r => r.Output.Content));
        var ends = run.Of<ToolEndEvent>().Select(e => e.CallId).ToList();
        Assert.Equal(["c2", "c1"], ends); // tool_end 按完成顺序发
    }

    [Fact]
    public async Task A_batch_with_a_non_concurrency_safe_tool_runs_sequentially_in_call_order()
    {
        var order = new List<string>();
        var slow = new FakeTool("slow", async (_, _, _) =>
        {
            order.Add("slow:begin");
            await Task.Delay(30);
            order.Add("slow:end");
            return ToolResult.Text("s");
        }, Unsafe);
        var quick = new FakeTool("quick", (_, _, _) =>
        {
            order.Add("quick");
            return Task.FromResult(ToolResult.Text("q"));
        });
        var rig = new Rig().Tool(slow).Tool(quick);
        rig.Llm.Enqueue(Up.Calls(("c1", "slow", "{}"), ("c2", "quick", "{}")), Up.Text("done"));

        await rig.RunAsync(Rig.User("u1", "go"));

        Assert.Equal(["slow:begin", "slow:end", "quick"], order);
    }

    [Fact]
    public async Task Preflight_is_sequential_the_next_call_is_not_checked_until_the_previous_answer_arrives()
    {
        var gate = new TaskCompletionSource<PreflightDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = new List<string>();
        var rig = new Rig().Tool(new FakeTool("a")).Tool(new FakeTool("b"));
        rig.BeforeTool = (call, _) =>
        {
            asked.Add(call.CallId);
            return call.CallId == "c1" ? new ValueTask<PreflightDecision>(gate.Task) : ValueTask.FromResult(PreflightDecision.Allow);
        };
        rig.Llm.Enqueue(Up.Calls(("c1", "a", "{}"), ("c2", "b", "{}")), Up.Text("done"));

        var running = rig.RunAsync(Rig.User("u1", "go"));
        await Task.Delay(100);
        Assert.Equal(["c1"], asked); // 审批卡片一张一张出现:c1 没回答之前 c2 不会发起
        gate.SetResult(PreflightDecision.Allow);
        var run = await running;

        Assert.Equal(["c1", "c2"], asked);
        Assert.Equal(["c1", "c2"], run.Of<ToolStartEvent>().Select(e => e.CallId));
        Assert.Equal(RunStatus.Done, run.Status);
    }

    [Fact]
    public async Task All_tool_starts_precede_any_tool_end_for_executed_calls_because_preflight_finishes_first()
    {
        var rig = new Rig().Tool(new FakeTool("a")).Tool(new FakeTool("b"));
        rig.Llm.Enqueue(Up.Calls(("c1", "a", "{}"), ("c2", "b", "{}")), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        var kinds = run.Kinds.Where(k => k.StartsWith("Tool")).ToList();
        Assert.Equal(["ToolStart", "ToolStart", "ToolEnd", "ToolEnd"], kinds);
    }

    [Fact]
    public async Task A_denied_call_is_a_visible_failure_and_does_not_affect_the_other_calls_in_the_batch()
    {
        var a = new FakeTool("a");
        var b = new FakeTool("b");
        var rig = new Rig().Tool(a).Tool(b);
        rig.BeforeTool = (call, _) => ValueTask.FromResult(call.Name == "a" ? PreflightDecision.Deny("nope, not allowed") : PreflightDecision.Allow);
        rig.Llm.Enqueue(Up.Calls(("c1", "a", "{}"), ("c2", "b", "{}")), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        Assert.Equal(0, a.Calls);
        Assert.Equal(1, b.Calls);
        var denied = run.ResultOf("c1");
        Assert.True(denied.IsError);
        Assert.Equal("nope, not allowed", denied.Output.Content); // 理由让模型看见
        Assert.False(run.ResultOf("c2").IsError);
        Assert.Equal(RunStatus.Done, run.Status);
    }

    [Fact]
    public async Task A_denial_without_a_reason_still_tells_the_model_it_was_denied()
    {
        var rig = new Rig().Tool(new FakeTool("a"));
        rig.BeforeTool = (_, _) => ValueTask.FromResult(PreflightDecision.Deny());
        rig.Llm.Enqueue(Up.Call("c1", "a"), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        Assert.Equal(LoopMessages.DefaultDenied, run.ResultOf("c1").Output.Content);
    }

    [Fact]
    public async Task An_edited_call_runs_with_the_edited_arguments_after_being_validated_again()
    {
        var tool = new FakeTool("a");
        tool.Prepare = input => input.Str("x") == "bad" ? ToolArgumentCheck.Invalid("x must not be bad") : ToolArgumentCheck.Valid(input);
        var rig = new Rig().Tool(tool);
        rig.BeforeTool = (call, _) => ValueTask.FromResult<PreflightDecision>(
            call.CallId == "c1" ? PreflightDecision.Edit(Jsn.Parse("""{"x":"edited"}""")) : PreflightDecision.Edit(Jsn.Parse("""{"x":"bad"}""")));
        rig.Llm.Enqueue(Up.Calls(("c1", "a", """{"x":"orig"}"""), ("c2", "a", """{"x":"orig"}""")), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        var invocation = Assert.Single(tool.Invocations);
        Assert.Equal("edited", invocation.Input.Str("x"));
        Assert.Equal("x must not be bad", run.ResultOf("c2").Output.Content);
        Assert.True(run.ResultOf("c2").IsError);
    }

    [Fact]
    public async Task A_throwing_permission_hook_denies_the_call_instead_of_running_it()
    {
        var tool = new FakeTool("a");
        var rig = new Rig().Tool(tool);
        rig.BeforeTool = (_, _) => throw new InvalidOperationException("policy store unavailable");
        rig.Llm.Enqueue(Up.Call("c1", "a"), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        Assert.Equal(0, tool.Calls);
        Assert.True(run.ResultOf("c1").IsError);
        Assert.Contains("policy store unavailable", run.ResultOf("c1").Output.Content);
    }

    // ───────────────────────── 预检失败:成对 ─────────────────────────

    [Fact]
    public async Task Bad_json_arguments_become_a_failed_result_that_echoes_the_raw_arguments_and_the_call_stays_paired()
    {
        var tool = new FakeTool("a");
        var rig = new Rig().Tool(tool);
        rig.Llm.Enqueue(Up.Call("c1", "a", "{\"path\": "), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        Assert.Equal(0, tool.Calls);
        var assistant = run.Assistants[0];
        var call = Assert.Single(assistant.Parts.OfType<ToolCallPart>());
        Assert.NotNull(call.InputError);
        Assert.Equal(JsonValueKind.Object, call.Input.ValueKind); // 仍输出一个调用块(输入为 {})
        var result = run.ResultOf("c1");
        Assert.True(result.IsError);
        Assert.Contains("{\"path\": ", result.Output.Content); // 回显原始参数
        Pairing.AssertPaired(run.Committed);
    }

    [Fact]
    public async Task An_unknown_tool_name_is_a_tool_error_listing_the_available_names()
    {
        var rig = new Rig().Tool(new FakeTool("alpha")).Tool(new FakeTool("beta"));
        rig.Llm.Enqueue(Up.Call("c1", "gamma"), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        var result = run.ResultOf("c1");
        Assert.True(result.IsError);
        Assert.Contains("gamma", result.Output.Content);
        Assert.Contains("alpha, beta", result.Output.Content);
        Assert.Equal(RunStatus.Done, run.Status);
        Pairing.AssertPaired(run.Committed);
    }

    [Fact]
    public async Task A_tool_taken_offline_after_the_snapshot_is_still_executed_from_the_snapshot()
    {
        // 正在执行的调用持有自己的工具引用,之后注册表怎么变都不影响它(03 §1)。
        var tool = new FakeTool("a");
        var rig = new Rig().Tool(tool);
        rig.Llm.Enqueue(Up.Call("c1", "a"), Up.Text("done"));
        rig.Prepare = (turn, _) =>
        {
            var snapshot = rig.Snapshot();
            return ValueTask.FromResult(TurnPlan.Run(new LlmRequest("s", turn.Transcript, [], turn.Options), turn.TurnIndex == 0 ? snapshot : ToolSnapshot.Empty));
        };

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        Assert.Equal(1, tool.Calls);
        Assert.Empty(rig.Llm.Requests[1].Tools);
        Assert.False(run.ResultOf("c1").IsError);
    }

    [Fact]
    public async Task Argument_validation_failure_is_a_tool_error_and_the_tool_does_not_run()
    {
        var tool = new FakeTool("a") { Prepare = _ => ToolArgumentCheck.Invalid("path is required") };
        var rig = new Rig().Tool(tool);
        rig.Llm.Enqueue(Up.Call("c1", "a"), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        Assert.Equal(0, tool.Calls);
        Assert.Equal("path is required", run.ResultOf("c1").Output.Content);
    }

    [Fact]
    public async Task Lenient_argument_coercion_is_applied_before_the_tool_runs()
    {
        var tool = new FakeTool("a") { Prepare = input => ToolArgumentCheck.Valid(Jsn.Parse("{\"n\":" + input.Str("n") + "}")) };
        var rig = new Rig().Tool(tool);
        rig.Llm.Enqueue(Up.Call("c1", "a", """{"n":"5"}"""), Up.Text("done"));

        await rig.RunAsync(Rig.User("u1", "go"));

        Assert.Equal(5, tool.Invocations.Single().Input.GetProperty("n").GetInt32());
    }

    // ───────────────────────── 工具结果 ─────────────────────────

    [Fact]
    public async Task A_tool_that_throws_becomes_a_tool_error_and_the_run_continues()
    {
        var rig = new Rig().Tool(new FakeTool("boom", (_, _, _) => throw new InvalidOperationException("disk exploded")));
        rig.Llm.Enqueue(Up.Call("c1", "boom"), Up.Text("recovered"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        Assert.Equal(RunStatus.Done, run.Status);
        var result = run.ResultOf("c1");
        Assert.True(result.IsError);
        Assert.Equal("Tool execution failed: InvalidOperationException: disk exploded", result.Output.Content);
        Assert.True(run.Of<ToolEndEvent>().Single().IsError);
        Assert.Equal(2, run.Llm.Requests.Count);
        Assert.Equal("recovered", ((TextPart)run.Assistants[^1].Parts[0]).Text);
    }

    [Fact]
    public async Task A_cancellation_that_the_tool_raised_on_its_own_without_an_abort_is_a_tool_failure_not_an_abort()
    {
        // 工具内部超时(例如 HttpClient 超时)抛的 OperationCanceledException,不是用户中止。
        var rig = new Rig().Tool(new FakeTool("slow", (_, _, _) => throw new TaskCanceledException("inner timeout")));
        rig.Llm.Enqueue(Up.Call("c1", "slow"), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.True(run.ResultOf("c1").IsError);
    }

    [Fact]
    public async Task A_tool_that_reports_its_own_error_is_recorded_as_is_and_the_loop_continues()
    {
        var rig = new Rig().Tool(new FakeTool("a", (_, _, _) => Task.FromResult(ToolResult.Error("file not found"))));
        rig.Llm.Enqueue(Up.Call("c1", "a"), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        Assert.True(run.ResultOf("c1").IsError);
        Assert.Equal(2, run.Llm.Requests.Count);
    }

    [Fact]
    public async Task Details_go_to_the_record_and_the_event_but_the_content_for_the_model_is_only_the_content()
    {
        var details = Jsn.Parse("""{"diff":"@@ -1 +1 @@"}""");
        var rig = new Rig().Tool(new FakeTool("edit", (_, _, _) => Task.FromResult(ToolResult.Text("edited", details))));
        rig.Llm.Enqueue(Up.Call("c1", "edit"), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        var end = run.Of<ToolEndEvent>().Single();
        Assert.Equal("edited", end.Output.Content);
        Assert.Equal("@@ -1 +1 @@", end.Output.Details!.Value.Str("diff"));
        Assert.Equal(end.Output, run.ResultOf("c1").Output);
    }

    [Fact]
    public async Task Text_parts_are_joined_and_images_travel_in_the_output()
    {
        var content = new List<Part> { new TextPart("a"), new ImagePart("image/png", "data:image/png;base64,AA=="), new TextPart("b") };
        var rig = new Rig().Tool(new FakeTool("shot", (_, _, _) => Task.FromResult(new ToolResult(content))));
        rig.Llm.Enqueue(Up.Call("c1", "shot"), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        var output = run.ResultOf("c1").Output;
        Assert.Equal("a\nb", output.Content);
        Assert.Equal([new ToolOutputImage("image/png", "data:image/png;base64,AA==")], output.Images);
    }

    [Fact]
    public async Task Oversized_output_is_truncated_on_the_producing_side_with_a_visible_marker()
    {
        var big = new string('x', 70_000);
        var rig = new Rig().Tool(new FakeTool("cat", (_, _, _) => Task.FromResult(ToolResult.Text(big))));
        rig.Llm.Enqueue(Up.Call("c1", "cat"), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        var output = run.ResultOf("c1").Output;
        Assert.True(output.Truncated);
        Assert.Equal(70_000, output.OriginalBytes);
        Assert.StartsWith(new string('x', 65_536), output.Content);
        Assert.Contains("[Output truncated: 70000 characters in total; only the first 65536 were kept]", output.Content);
        Assert.Equal(output, run.Of<ToolEndEvent>().Single().Output); // 界面、记录、模型是同一份
    }

    [Fact]
    public async Task A_tool_that_truncated_its_own_output_keeps_its_own_markers_when_under_the_limit()
    {
        var rig = new Rig().Tool(new FakeTool("bash", (_, _, _) =>
            Task.FromResult(new ToolResult([new TextPart("tail only")], Truncated: true, OriginalBytes: 123_456))));
        rig.Llm.Enqueue(Up.Call("c1", "bash"), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        var output = run.ResultOf("c1").Output;
        Assert.Equal("tail only", output.Content);
        Assert.True(output.Truncated);
        Assert.Equal(123_456, output.OriginalBytes);
    }

    [Fact]
    public async Task Truncation_never_splits_a_surrogate_pair()
    {
        var text = new string('a', 9) + "😀" + new string('b', 10); // 😀 占下标 9、10
        var rig = new Rig { Options = new AgentLoopOptions { MaxToolOutputChars = 10 } }.Tool(new FakeTool("cat", (_, _, _) => Task.FromResult(ToolResult.Text(text))));
        rig.Llm.Enqueue(Up.Call("c1", "cat"), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        Assert.StartsWith(new string('a', 9) + "\n", run.ResultOf("c1").Output.Content);
    }

    // ───────────────────────── AfterTool ─────────────────────────

    [Fact]
    public async Task AfterTool_can_append_feedback_and_flip_IsError_to_true()
    {
        var rig = new Rig().Tool(new FakeTool("a", (_, _, _) => Task.FromResult(ToolResult.Text("out"))));
        rig.AfterTool = (_, r, _) => ValueTask.FromResult(r with { Content = [.. r.Content, new TextPart("<hook-feedback>lint failed</hook-feedback>")], IsError = true });
        rig.Llm.Enqueue(Up.Call("c1", "a"), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        var result = run.ResultOf("c1");
        Assert.Equal("out\n<hook-feedback>lint failed</hook-feedback>", result.Output.Content);
        Assert.True(result.IsError);
        var end = run.Of<ToolEndEvent>().Single();
        Assert.Equal((result.Output, true), (end.Output, end.IsError)); // 界面、记录、模型看到的是同一份
    }

    [Fact]
    public async Task AfterTool_cannot_rewrite_the_output_flip_an_error_to_success_or_change_Terminate()
    {
        var details = Jsn.Parse("""{"k":1}""");
        var rig = new Rig().Tool(new FakeTool("a", (_, _, _) => Task.FromResult(ToolResult.Error("original failure", details))));
        rig.AfterTool = (_, r, _) => ValueTask.FromResult(new ToolResult([new TextPart("rewritten")], null, IsError: false, Terminate: true));
        rig.Llm.Enqueue(Up.Call("c1", "a"), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        var result = run.ResultOf("c1");
        Assert.Equal("original failure", result.Output.Content);
        Assert.True(result.IsError);
        Assert.Equal(2, run.Llm.Requests.Count); // Terminate 没有被偷偷加上
    }

    [Fact]
    public async Task A_throwing_AfterTool_is_swallowed_and_the_result_is_used_as_is()
    {
        var rig = new Rig().Tool(new FakeTool("a"));
        rig.AfterTool = (_, _, _) => throw new InvalidOperationException("hook bug");
        rig.Llm.Enqueue(Up.Call("c1", "a"), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal("a:ok", run.ResultOf("c1").Output.Content);
        Assert.Single(rig.Diagnostics.Warnings);
    }

    // ───────────────────────── Terminate ─────────────────────────

    [Fact]
    public async Task A_single_Terminate_result_ends_the_run_without_another_request_but_the_turn_is_still_closed()
    {
        var rig = new Rig().Tool(new FakeTool("exit", (_, _, _) => Task.FromResult(ToolResult.Text("bye", terminate: true))));
        rig.Llm.Enqueue(Up.Call("c1", "exit"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Single(run.Llm.Requests);
        Assert.Equal("TurnEnd", run.Kinds[^2]);
        Pairing.AssertPaired(run.Committed);
    }

    [Fact]
    public async Task Terminate_needs_every_result_in_the_batch_to_terminate()
    {
        var rig = new Rig()
            .Tool(new FakeTool("exit", (_, _, _) => Task.FromResult(ToolResult.Text("bye", terminate: true))))
            .Tool(new FakeTool("other"));
        rig.Llm.Enqueue(Up.Calls(("c1", "exit", "{}"), ("c2", "other", "{}")), Up.Text("continued"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        Assert.Equal(2, run.Llm.Requests.Count);
        Assert.Equal(RunStatus.Done, run.Status);
    }

    [Fact]
    public async Task All_calls_terminating_in_a_parallel_batch_ends_the_run()
    {
        var rig = new Rig()
            .Tool(new FakeTool("e1", (_, _, _) => Task.FromResult(ToolResult.Text("a", terminate: true))))
            .Tool(new FakeTool("e2", (_, _, _) => Task.FromResult(ToolResult.Text("b", terminate: true))));
        rig.Llm.Enqueue(Up.Calls(("c1", "e1", "{}"), ("c2", "e2", "{}")));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        Assert.Single(run.Llm.Requests);
    }

    [Fact]
    public async Task A_Terminate_result_that_is_an_error_does_not_end_the_run()
    {
        var rig = new Rig().Tool(new FakeTool("exit", (_, _, _) => Task.FromResult(ToolResult.Error("failed") with { Terminate = true })));
        rig.Llm.Enqueue(Up.Call("c1", "exit"), Up.Text("retrying"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        Assert.Equal(2, run.Llm.Requests.Count);
    }

    // ───────────────────────── 上下文、进度、环境 ─────────────────────────

    [Fact]
    public async Task The_tool_context_carries_run_call_session_depth_and_the_environment_tag()
    {
        var tool = new FakeTool("a");
        var rig = new Rig { Environment = new EnvironmentTag("dev-1", "ssh:build") }.Tool(tool);
        rig.Llm.Enqueue(Up.Call("c1", "a"), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        var ctx = Assert.Single(tool.Contexts);
        Assert.Equal((new RunId("run-1"), "c1", "session-1", 0, new EnvironmentTag("dev-1", "ssh:build")), (ctx.RunId, ctx.CallId, ctx.SessionId, ctx.Depth, ctx.Environment));
        // 工具卡片和审批必须带执行环境标识(AGENTS.md §5)。
        Assert.Equal(new EnvironmentTag("dev-1", "ssh:build"), run.Of<ToolStartEvent>().Single().Environment);
    }

    [Fact]
    public async Task The_session_layer_can_replace_the_tool_context_with_a_derived_type()
    {
        var tool = new FakeTool("a");
        var rig = new Rig().Tool(tool);
        rig.Enrich = ctx => new RichContext(ctx, "extra");
        rig.Llm.Enqueue(Up.Call("c1", "a"), Up.Text("done"));

        await rig.RunAsync(Rig.User("u1", "go"));

        Assert.Equal("extra", Assert.IsType<RichContext>(Assert.Single(tool.Contexts)).Extra);
    }

    private sealed record RichContext : ToolContext
    {
        public RichContext(ToolContext inner, string extra)
            : base(inner)
        {
            Extra = extra;
        }

        public string Extra { get; }
    }

    [Fact]
    public async Task Tool_progress_is_a_volatile_event_between_start_and_end_and_late_progress_is_dropped()
    {
        IProgress<ToolProgress>? captured = null;
        var rig = new Rig().Tool(new FakeTool("build", (_, ctx, _) =>
        {
            captured = ctx.Progress;
            ctx.Progress.Report(new ToolProgress("compiling", 0.5));
            return Task.FromResult(ToolResult.Text("built"));
        }));
        rig.Llm.Enqueue(Up.Call("c1", "build"), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));
        captured!.Report(new ToolProgress("too late")); // 结束之后迟到的进度

        var progress = run.Of<ToolProgressEvent>().Single();
        Assert.Equal(("c1", "compiling", 0.5), (progress.CallId, progress.Progress.Message, progress.Progress.Fraction));
        var kinds = run.Kinds.Where(k => k.StartsWith("Tool")).ToList();
        Assert.Equal(["ToolStart", "ToolProgress", "ToolEnd"], kinds);
        // 进度不进记录。
        Assert.DoesNotContain("compiling", string.Concat(run.Results.Select(r => r.Output.Content)));
    }

    [Fact]
    public async Task The_tool_end_event_reports_the_duration_measured_with_the_injected_clock()
    {
        var rig = new Rig().Tool(new FakeTool("a", async (_, _, _) =>
        {
            await Task.Delay(40);
            return ToolResult.Text("ok");
        }));
        rig.Llm.Enqueue(Up.Call("c1", "a"), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        Assert.True(run.Of<ToolEndEvent>().Single().DurationMs >= 30);
    }

    // ───────────────────────── 同一文件串行 ─────────────────────────

    [Fact]
    public async Task Parallel_calls_that_mutate_the_same_file_are_serialized_through_the_queue_while_other_files_run_alongside()
    {
        var activeOnA = 0;
        var maxSameFile = 0;
        var otherRanWhileHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<ToolResult> Edit(ToolInvocation call, ToolContext ctx, CancellationToken ct)
        {
            var path = call.Input.Str("path")!;
            await ctx.FileMutations.RunAsync(path, async _ =>
            {
                if (path == "/a")
                {
                    maxSameFile = Math.Max(maxSameFile, Interlocked.Increment(ref activeOnA));
                }

                if (path == "/a" && call.CallId == "c1")
                {
                    await otherRanWhileHeld.Task.WaitAsync(ct); // c1 持有 /a 时,/b 必须能同时执行
                }

                if (path == "/b")
                {
                    otherRanWhileHeld.TrySetResult();
                }

                await Task.Delay(20, ct);
                if (path == "/a")
                {
                    Interlocked.Decrement(ref activeOnA);
                }

            }, ct);
            return ToolResult.Text("edited " + path);
        }

        var rig = new Rig().Tool(new FakeTool("edit", Edit, new ToolTraits(false, true, false)));
        rig.Llm.Enqueue(Up.Calls(("c1", "edit", """{"path":"/a"}"""), ("c2", "edit", """{"path":"/a"}"""), ("c3", "edit", """{"path":"/b"}""")), Up.Text("done"));

        var run = await rig.RunAsync(Rig.User("u1", "go"));

        Assert.Equal(1, maxSameFile);
        Assert.Equal(3, run.Results.Count(r => !r.IsError));
    }
}
