using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Session.Tests.Support;
using Ncw.Agent.Storage.Sessions;

namespace Ncw.Agent.Session.Tests;

/// <summary>
/// AgentSession:多轮对话落库与重新加载、R6 只在消息完成时落库、孤儿修复、中止后的状态、会话互斥与 run_settled、旧透传记录补标签、流中断续传。
/// 移植自 2.x 的 <c>agent-session.test.ts</c> 里属于会话层的那些(循环层的那些已在 <c>Ncw.Agent.Kernel.Tests</c>)。
/// 真 Storage(临时目录里的 SQLite 文件)+ 假上游 + 测试里的假工具。
/// </summary>
public sealed class SessionTests
{
    private static string TextOf(Message m) => string.Concat(m.Parts.OfType<TextPart>().Select(p => p.Text));

    private static string Json(Message m) => MessageCodec.PartsToJson(m.Parts);

    private static Message Seed(string id, Role role, params Part[] parts) =>
        new(new MessageId(id), role, parts, DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));

    // ───────────── 单轮与多轮 ─────────────

    [Fact]
    public async Task A_single_turn_commits_the_user_input_then_the_reply_and_reloads_identically()
    {
        using var h = new Harness();
        h.Llm.Turn(Up.Text("hello there"));

        var ran = await h.RunAsync("hi", id: "client-1");

        Assert.Equal(RunStatus.Done, ran.Status);
        var stored = h.Stored();
        Assert.Equal(["client-1", "id0001"], stored.Select(m => m.Id.Value).Where(id => id == "client-1" || id.StartsWith("id")).Take(2));
        Assert.Equal(("hi", "hello there"), (TextOf(stored[0]), TextOf(stored[1])));
        Assert.Equal([Role.User, Role.Assistant], stored.Select(m => m.Role));
        // 助手消息的来源和停止原因落在 meta 里:跨模型切换(只回放同源的思考块)要用
        Assert.Equal(new Provenance("anthropic", "p1", "fake-up"), stored[1].Source);
        Assert.Equal(StopReason.EndTurn, stored[1].Stop);
        Assert.Single(h.Repo.CommittedInputIds(Harness.SessionId, ["client-1"])); // 用户消息的消费回执和消息同一个事务
    }

    [Fact]
    public async Task The_client_supplied_message_id_is_used_and_announced_so_delivery_is_confirmed_by_the_same_id()
    {
        using var h = new Harness();

        var ran = await h.RunAsync("hi", id: "my-ulid");

        var end = ran.Of<MessageEndEvent>().First();
        Assert.Equal("my-ulid", end.MessageId.Value);
        Assert.Equal("my-ulid", h.Stored()[0].Id.Value);
    }

    [Fact]
    public async Task Several_runs_accumulate_and_a_brand_new_session_object_sees_the_same_history_in_its_request()
    {
        using var h = new Harness(tools: [new FakeTool("echo")]);
        h.Llm.Turn(Up.Text("first answer"), Up.Call("c1", "echo"), Up.Text("second answer"));
        await h.RunAsync("one");
        await h.RunAsync("two");
        var storedBefore = h.Stored();

        var fresh = h.NewSession(); // 进程重启:内存里什么都没有,唯一的真源是库
        await h.RunAsync("three", session: fresh);

        var request = h.Llm.Requests[^1];
        Assert.Equal(storedBefore.Select(m => m.Id), request.Messages.Take(storedBefore.Count).Select(m => m.Id));
        Assert.Equal("three", TextOf(request.Messages[^1]));
        Assert.Equal(storedBefore.Count + 2, h.Stored().Count);
        // 落库的 JSON 逐字不变:装饰只作用于发出去的副本,记录永不被修改
        for (var i = 0; i < storedBefore.Count; i++)
        {
            Assert.Equal(Json(storedBefore[i]), Json(h.Stored()[i]));
        }
    }

    [Fact]
    public async Task A_tool_round_trip_is_persisted_as_call_then_result_in_order_and_stays_paired_after_reload()
    {
        var echo = new FakeTool("echo");
        using var h = new Harness(tools: [echo]);
        h.Llm.Turn(Up.Call("c1", "echo", """{"x":1}"""), Up.Text("done"));

        var ran = await h.RunAsync("go");

        Assert.Equal(1, ran.Outcome.ToolCalls);
        var stored = h.Stored();
        Assert.Equal([Role.User, Role.Assistant, Role.User, Role.Assistant], stored.Select(m => m.Role));
        Assert.Equal("echo", stored[1].Parts.OfType<ToolCallPart>().Single().Name);
        var result = stored[2].Parts.OfType<ToolResultPart>().Single();
        Assert.Equal(("c1", "echo ran", false), (result.CallId, result.Output.Content, result.IsError));
        Assert.Empty(Orphans.Find(stored));
        // 第二个请求里带着工具结果
        Assert.Contains(h.Llm.Requests[1].Messages, m => m.Parts.OfType<ToolResultPart>().Any(r => r.CallId == "c1"));
    }

    [Fact]
    public async Task An_empty_input_continues_without_adding_a_user_message()
    {
        using var h = new Harness();
        h.SeedHistory(Seed("u0", Role.User, new TextPart("earlier")));
        h.Llm.Turn(Up.Text("continuing"));

        var ran = await h.RunAsync("");

        Assert.Equal(RunStatus.Done, ran.Status);
        Assert.Equal(["u0", "id0001"], h.Stored().Select(m => m.Id.Value));
        Assert.Single(h.Llm.Requests[0].Messages);
    }

    [Fact]
    public async Task A_session_that_does_not_exist_does_not_start_a_run_and_says_so_with_a_key()
    {
        using var h = new Harness();
        var ghost = new AgentSession("ghost", "ws", new AgentSessionDeps
        {
            Sessions = h.Repo, Llm = h.Llm, Ids = new SeqIds(), Clock = new FakeClock(), Delayer = new NoDelay(), Environment = h.Env, WorkspaceRoot = h.RootPath,
        });
        var sink = new ListRunSink();

        var outcome = await ghost.RunAsync(h.Request("hi"), sink);

        Assert.False(outcome.Started);
        Assert.Equal(SessionErrorKeys.NotFound, outcome.Error!.MessageKey);
        Assert.Empty(sink.Events);
        Assert.Empty(h.Llm.Requests);
    }

    [Fact]
    public async Task The_outcome_sums_usage_and_counts_tool_calls()
    {
        using var h = new Harness(tools: [new FakeTool("echo")]);
        h.Llm.Turn(Up.Call("c1", "echo"), Up.Text("done"));

        var ran = await h.RunAsync("go");

        Assert.Equal(new TokenUsage(20, 10), ran.Outcome.Usage);
        Assert.Equal(1, ran.Outcome.ToolCalls);
    }

    // ───────────── R6:只在消息完成时落库 ─────────────

    private sealed class ProbeSink(Func<AgentEvent, Task> probe) : IRunSink
    {
        public List<AgentEvent> Events { get; } = [];

        public async ValueTask EmitAsync(AgentEvent ev, CancellationToken ct)
        {
            Events.Add(ev);
            await probe(ev);
        }
    }

    [Fact]
    public async Task Streaming_deltas_are_events_only_the_message_is_in_the_database_by_the_time_message_end_is_announced()
    {
        using var h = new Harness();
        var gate = new TaskCompletionSource();
        h.Llm.Turn(Up.Custom(StreamThenWait(gate.Task)));
        var seenDuringStream = -1;
        var committedAtEnd = false;
        var sink = new ProbeSink(ev =>
        {
            if (ev is MessageDeltaEvent)
            {
                seenDuringStream = h.Repo.History(Harness.SessionId).Count(m => m.Role == "assistant"); // 此刻助手消息还在流式输出
                gate.TrySetResult();
            }

            if (ev is MessageEndEvent { Message.Role: Role.Assistant } end)
            {
                committedAtEnd = h.Repo.History(Harness.SessionId).Any(m => m.Id == end.MessageId.Value); // R6:先落库、后发 message_end
            }

            return Task.CompletedTask;
        });

        await h.Session.RunAsync(h.Request("hi"), sink);

        Assert.Equal(0, seenDuringStream);
        Assert.True(committedAtEnd);
    }

    private static Func<LlmRequest, CancellationToken, IAsyncEnumerable<LlmEvent>> StreamThenWait(Task gate) => (_, _) => Gen(gate);

    private static async IAsyncEnumerable<LlmEvent> Gen(Task gate)
    {
        yield return new MessageStart("fake", "p1", "anthropic", "fake-up");
        yield return new TextDelta(0, "partial");
        await gate;
        yield return new MessageEnd(StopReason.EndTurn, Up.Usage);
    }

    [Fact]
    public async Task Context_usage_is_announced_before_the_request_is_sent_with_window_and_verdict()
    {
        using var h = new Harness();

        var ran = await h.RunAsync("hi");

        var kinds = ran.Events.Select(e => e.GetType().Name).ToList();
        Assert.True(kinds.IndexOf(nameof(ContextUsageEvent)) < kinds.IndexOf(nameof(TurnStartEvent)));
        Assert.True(kinds.IndexOf(nameof(ContextUsageEvent)) < kinds.IndexOf(nameof(MessageDeltaEvent)));
        var usage = ran.Of<ContextUsageEvent>().Single();
        Assert.Equal((200_000, false), (usage.Window, usage.ShouldCompact));
        Assert.Equal(usage.Used, usage.Segments.Sum(s => s.Tokens));
    }

    [Fact]
    public async Task The_billing_threshold_clamps_the_window_unless_max_context_is_on_but_the_hard_check_always_uses_the_protocol_window()
    {
        var huge = Harness.Alias with { ContextWindow = 1_000_000 };
        using var h = new Harness(resolve: m => huge);

        var clamped = await h.RunAsync("hi");
        var open = await h.RunAsync("hi again", b => b.MaxContext = true);

        Assert.Equal(272_000, clamped.Of<ContextUsageEvent>().Single().Window);
        Assert.Equal(1_000_000, open.Of<ContextUsageEvent>().Single().Window);
    }

    // ───────────── 孤儿修复 ─────────────

    [Fact]
    public async Task Orphans_left_by_a_crashed_run_are_closed_before_the_new_user_message_and_never_after_it()
    {
        using var h = new Harness();
        h.SeedHistory(Seed("u0", Role.User, new TextPart("earlier")), Seed("a0", Role.Assistant, new ToolCallPart("lost", "echo", Jsn.EmptyObject)));

        await h.RunAsync("next", id: "new-user");

        var ids = h.Stored().Select(m => m.Id.Value).ToList();
        Assert.Equal(["u0", "a0"], ids.Take(2));
        var repair = h.Stored()[2];
        Assert.Equal(LoopMessages.RecoveredInterrupted, repair.Parts.OfType<ToolResultPart>().Single().Output.Content);
        Assert.Equal("new-user", ids[3]);
        Assert.Empty(Orphans.Find(h.Stored()));
        // 上游看到的请求里调用和结果是紧邻的
        var request = h.Llm.Requests[0].Messages;
        Assert.Equal("lost", request[2].Parts.OfType<ToolResultPart>().Single().CallId);
    }

    [Fact]
    public void Repairing_on_load_without_running_commits_one_failure_result_and_is_idempotent()
    {
        using var h = new Harness();
        h.SeedHistory(
            Seed("u0", Role.User, new TextPart("q")),
            Seed("a0", Role.Assistant, new ToolCallPart("c1", "x", Jsn.EmptyObject), new ToolCallPart("c2", "y", Jsn.EmptyObject)));

        Assert.Equal(2, h.Session.RepairOrphans());
        Assert.Equal(0, h.Session.RepairOrphans());

        var results = h.Stored()[2].Parts.OfType<ToolResultPart>().ToList();
        Assert.Equal(["c1", "c2"], results.Select(r => r.CallId));
        Assert.All(results, r => Assert.True(r.IsError));
        Assert.Equal(3, h.Stored().Count);
    }

    [Fact]
    public async Task A_message_that_only_has_a_cut_off_tool_call_from_a_failed_stream_leaves_no_orphan_behind()
    {
        using var h = new Harness(tools: [new FakeTool("echo")]);
        h.Llm.Turn(Up.Events(
            new MessageStart("fake", "p1", "anthropic", "fake-up"),
            new TextDelta(0, "let me"),
            new ToolCallStart(1, "c1", "echo"),
            new ToolCallDelta(1, "c1", "{\"x\":"),
            new StreamError(AgentError.Create(AgentErrorCode.Provider, "boom"))));

        var ran = await h.RunAsync("go");

        Assert.Equal(RunStatus.Error, ran.Status);
        Assert.Empty(Orphans.Find(h.Stored()));
        Assert.IsType<ErrorPart>(h.Stored()[1].Parts[^1]); // 失败也留在转录里,重载之后看得见
    }

    // ───────────── 中止 ─────────────

    [Fact]
    public async Task Aborting_mid_stream_keeps_the_half_reply_marked_aborted_releases_the_lock_and_settles_last()
    {
        using var h = new Harness();
        h.Llm.Turn(Up.Hang("half an answer"));
        using var cts = new CancellationTokenSource();
        var sink = new ProbeSink(ev =>
        {
            if (ev is MessageDeltaEvent)
            {
                cts.Cancel();
            }

            return Task.CompletedTask;
        });

        var outcome = await h.Session.RunAsync(h.Request("hi"), sink, cts.Token);

        Assert.Equal(RunStatus.Aborted, outcome.Status);
        Assert.False(h.Session.IsBusy);
        var stored = h.Stored();
        Assert.Equal("half an answer", TextOf(stored[1]));
        Assert.Equal(StopReason.Aborted, stored[1].Stop); // 回放时整条跳过的依据,重载之后仍在
        Assert.IsType<RunEndEvent>(sink.Events[^2]);
        Assert.IsType<RunSettledEvent>(sink.Events[^1]);
        // 中止之后会话可以继续
        h.Llm.Turn(Up.Text("fine"));
        Assert.Equal(RunStatus.Done, (await h.RunAsync("again")).Status);
    }

    [Fact]
    public async Task Aborting_while_a_tool_runs_closes_the_call_with_an_interrupted_result_that_is_persisted()
    {
        using var h = new Harness(tools: [new FakeTool("slow", async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return ToolResult.Text("never");
        })]);
        h.Llm.Turn(Up.Call("c1", "slow"));
        using var cts = new CancellationTokenSource();
        var sink = new ProbeSink(ev =>
        {
            if (ev is ToolStartEvent)
            {
                cts.CancelAfter(50);
            }

            return Task.CompletedTask;
        });

        var outcome = await h.Session.RunAsync(h.Request("go"), sink, cts.Token);

        Assert.Equal(RunStatus.Aborted, outcome.Status);
        Assert.Empty(Orphans.Find(h.Stored()));
        var result = h.Stored().SelectMany(m => m.Parts).OfType<ToolResultPart>().Single();
        Assert.Equal((LoopMessages.Interrupted, true), (result.Output.Content, result.IsError));
        Assert.False(h.Session.IsBusy);
    }

    // ───────────── 会话互斥与 run_settled ─────────────

    [Fact]
    public async Task A_second_run_in_the_same_session_is_refused_without_events_and_the_idle_only_operations_throw()
    {
        using var h = new Harness();
        h.Llm.Turn(Up.Hang());
        using var cts = new CancellationTokenSource();
        var streaming = new TaskCompletionSource();
        var first = h.Session.RunAsync(h.Request("one"), new ProbeSink(ev =>
        {
            if (ev is MessageDeltaEvent)
            {
                streaming.TrySetResult();
            }

            return Task.CompletedTask;
        }), cts.Token);
        await streaming.Task; // 第一个 Run 确实在流式输出了(取消得太早的话,上游脚本不会被消费)

        var sink = new ListRunSink();
        var refused = await h.Session.RunAsync(h.Request("two"), sink);

        Assert.True(h.Session.IsBusy);
        Assert.False(refused.Started);
        Assert.Equal(("session.busy", AgentErrorCode.Conflict), (refused.Error!.MessageKey, refused.Error.Code));
        Assert.Empty(sink.Events); // 没有开始就没有 run_start,也没有 run_settled
        Assert.Throws<SessionBusyException>(() => h.Session.RepairOrphans());
        await Assert.ThrowsAsync<SessionBusyException>(() => h.Session.CompactNowAsync(h.Model, ThinkingLevel.Auto));

        await cts.CancelAsync();
        await first;
        Assert.False(h.Session.IsBusy);
        Assert.Equal(RunStatus.Done, (await h.RunAsync("three")).Status);
    }

    [Fact]
    public async Task Run_settled_comes_after_everything_is_committed_and_the_session_lock_is_released()
    {
        var order = new List<string>();
        var ext = new Ext("watch", 0, r =>
        {
            r.On(LoopExtensionPoints.RunEnd, (e, _, _) =>
            {
                order.Add("ext:run_end");
                return ValueTask.CompletedTask;
            });
            r.On(SessionExtensionPoints.RunSettled, (e, _, _) =>
            {
                order.Add("ext:run_settled");
                return ValueTask.CompletedTask;
            });
        });
        using var h = new Harness([ext]);
        var stateAtEnd = (Busy: true, Count: 0);
        var stateAtSettled = (Busy: true, Count: 0);
        var sink = new ProbeSink(ev =>
        {
            switch (ev)
            {
                case RunEndEvent:
                    order.Add("sink:run_end");
                    stateAtEnd = (h.Session.IsBusy, h.Repo.History(Harness.SessionId).Count);
                    break;
                case RunSettledEvent:
                    order.Add("sink:run_settled");
                    stateAtSettled = (h.Session.IsBusy, h.Repo.History(Harness.SessionId).Count);
                    break;
            }

            return Task.CompletedTask;
        });

        await h.Session.RunAsync(h.Request("hi"), sink);

        Assert.Equal(["sink:run_end", "ext:run_end", "sink:run_settled", "ext:run_settled"], order);
        Assert.Equal(2, stateAtEnd.Count); // run_end 时消息已经落库(扩展的 run_end 在落库之后)
        Assert.False(stateAtSettled.Busy); // settled:锁已释放
        Assert.Equal(2, stateAtSettled.Count);
        Assert.Equal(RunStatus.Done, ((RunSettledEvent)sink.Events[^1]).Status);
    }

    [Fact]
    public async Task A_sink_that_throws_never_fails_the_run()
    {
        using var h = new Harness();

        var outcome = await h.Session.RunAsync(h.Request("hi"), new ProbeSink(_ => throw new InvalidOperationException("sink down")));

        Assert.Equal(RunStatus.Done, outcome.Status);
        Assert.Equal(2, h.Stored().Count);
        Assert.NotEmpty(h.Log.Warnings);
    }

    // ───────────── 断流续传 ─────────────

    [Fact]
    public async Task A_stream_that_breaks_with_a_network_error_is_resumed_and_the_broken_half_never_reaches_the_database()
    {
        using var h = new Harness();
        h.Llm.Turn(Up.Fail(Up.NetworkError, partial: "broken half"), Up.Text("whole answer"));

        var ran = await h.RunAsync("hi");

        Assert.Equal(RunStatus.Done, ran.Status);
        Assert.Single(ran.Of<ProviderRetryEvent>());
        var stored = h.Stored();
        Assert.Equal(["hi", "whole answer"], stored.Select(TextOf));
        Assert.Equal(2, h.Llm.Requests.Count);
    }

    // ───────────── 项目指令与装饰 ─────────────

    [Fact]
    public async Task Project_instructions_reach_the_model_on_the_first_user_message_but_never_the_database()
    {
        using var h = new Harness();
        File.WriteAllText(Path.Combine(h.Root, "AGENTS.md"), "这个仓库用 pnpm,不要用 npm。");

        await h.RunAsync("hi");

        var sent = h.Llm.Requests[0].Messages[0];
        Assert.Contains("<project-instructions>", TextOf(sent));
        Assert.Contains("这个仓库用 pnpm", TextOf(sent));
        Assert.Equal("hi", ((TextPart)sent.Parts[^1]).Text);
        var stored = h.Repo.History(Harness.SessionId)[0];
        Assert.DoesNotContain("project-instructions", stored.PartsJson);
        Assert.DoesNotContain("pnpm", stored.PartsJson); // 用户在自己的聊天气泡里不会读到整篇 AGENTS.md
    }

    [Fact]
    public async Task The_prefix_of_the_request_is_byte_identical_between_the_turns_of_one_run_and_the_system_prompt_does_not_change()
    {
        using var h = new Harness(tools: [new FakeTool("echo")]);
        File.WriteAllText(Path.Combine(h.Root, "AGENTS.md"), "rules");
        h.Llm.Turn(Up.Call("c1", "echo"), Up.Call("c2", "echo"), Up.Text("done"));

        await h.RunAsync("go");

        var requests = h.Llm.Requests;
        Assert.Equal(3, requests.Count);
        Assert.Equal(requests[0].System, requests[2].System); // 运行级常量(日期、环境、指令)定死在 Run 开始时
        for (var turn = 1; turn < 3; turn++)
        {
            for (var i = 0; i < requests[turn - 1].Messages.Count; i++)
            {
                Assert.Equal(Json(requests[turn - 1].Messages[i]), Json(requests[turn].Messages[i]));
            }
        }
    }

    [Fact]
    public async Task A_git_repository_adds_a_state_block_to_the_run_input_message_as_of_the_start_of_the_run()
    {
        using var h = new Harness();
        var spec = Ncw.Agent.Environments.CommandSpec.Exec("git", "init", "-q", "-b", "main") with { WorkingDirectory = h.RootPath };
        try
        {
            if (!(await Ncw.Agent.Environments.ProcessRunner.RunAsync(h.Env, spec)).Succeeded)
            {
                Assert.Skip("git init failed");
            }
        }
        catch (Ncw.Agent.Environments.EnvironmentException)
        {
            Assert.Skip("git is not available");
        }

        await h.RunAsync("hi");

        var sent = TextOf(h.Llm.Requests[0].Messages[0]);
        Assert.Contains("Git: branch main", sent);
        Assert.Contains("as of the start of this run", sent);
        Assert.DoesNotContain("Git:", h.Repo.History(Harness.SessionId)[0].PartsJson);
    }

    [Fact]
    public async Task Without_project_instructions_or_git_the_first_message_is_sent_as_is()
    {
        using var h = new Harness();

        await h.RunAsync("hi");

        Assert.Equal(["hi"], h.Llm.Requests[0].Messages[0].Parts.OfType<TextPart>().Select(p => p.Text));
    }

    [Fact]
    public async Task Turning_project_instructions_off_in_the_run_settings_skips_the_file_read()
    {
        using var h = new Harness();
        File.WriteAllText(Path.Combine(h.Root, "AGENTS.md"), "rules");

        await h.RunAsync("hi", b => b.Settings = new SessionSettings { ProjectInstructions = false });

        Assert.DoesNotContain("project-instructions", TextOf(h.Llm.Requests[0].Messages[0]));
    }

    [Fact]
    public async Task The_system_prompt_carries_environment_facts_personalization_and_the_tool_section()
    {
        using var h = new Harness(tools: [new FakeTool("Read", snippet: "Read file contents", guidelines: ["Use Read instead of cat."])]);

        await h.RunAsync("hi", b => b.Settings = new SessionSettings { Personalization = new Personalization(Instructions: "answer in Chinese") });

        var system = h.Llm.Requests[0].System;
        Assert.Contains("Workspace root: " + h.Root, system);
        Assert.Contains("Execution location: local machine", system);
        Assert.Contains("answer in Chinese", system);
        Assert.Contains("- Read: Read file contents", system);
        Assert.Contains("Use Read instead of cat.", system);
        Assert.Equal(["Read"], h.Llm.Requests[0].Tools.Select(t => t.ExternalName));
    }

    // ───────────── 2.x 旧记录 ─────────────

    [Fact]
    public async Task A_legacy_anthropic_thinking_record_is_sent_with_its_opaque_tagged_anthropic()
    {
        using var h = new Harness();
        h.Repo.Commit(Harness.SessionId, new MessageRecord("u0", "user", """[{"type":"text","text":"old question"}]""", 1_700_000_000_000));
        h.Repo.Commit(Harness.SessionId, new MessageRecord("a0", "assistant",
            """[{"type":"thinking","text":"old thought","opaque":{"signature":"sig-123"}},{"type":"text","text":"old answer"}]""", 1_700_000_000_001));

        await h.RunAsync("follow up");

        var thinking = h.Llm.Requests[0].Messages[1].Parts.OfType<ThinkingPart>().Single();
        Assert.Equal("anthropic", thinking.Opaque!.Protocol);
        Assert.Equal("sig-123", thinking.Opaque.Data.Str("signature"));
        Assert.Null(h.Llm.Requests[0].Messages[1].Source); // 2.x 没有来源:换模型时丢弃思考块(01-llm §4)
    }

    // ───────────── 文件引用归一化 ─────────────

    [Fact]
    public async Task A_dropped_file_inside_the_workspace_becomes_a_workspace_relative_path_and_one_outside_stays_absolute()
    {
        using var h = new Harness();
        Directory.CreateDirectory(Path.Combine(h.Root, "src"));
        File.WriteAllText(Path.Combine(h.Root, "src", "a.cs"), "x");
        var outside = Path.Combine(Path.GetTempPath(), $"ncw-outside-{Guid.NewGuid():N}.txt");
        File.WriteAllText(outside, "x");
        try
        {
            var request = new RunRequest(new RunId("r1"),
                [new Message(new MessageId("u1"), Role.User, [new TextPart("see"), new FileRefPart(Path.Combine(h.Root, "src", "a.cs"), "a.cs"), new FileRefPart(outside, "o.txt"), new FileRefPart("src/a.cs", "rel")], DateTimeOffset.UnixEpoch)],
                h.Model);

            await h.Session.RunAsync(request, new ListRunSink());

            var refs = h.Stored()[0].Parts.OfType<FileRefPart>().ToList();
            Assert.Equal("src/a.cs", refs[0].Path); // 与工具回执同一种写法
            Assert.Equal(Path.GetFullPath(outside).Replace("/private", "", StringComparison.Ordinal), refs[1].Path.Replace("/private", "", StringComparison.Ordinal));
            Assert.Equal("src/a.cs", refs[2].Path);
            Assert.Equal("see", TextOf(h.Stored()[0])); // 其他 part 一个字节都不动
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task The_public_normaliser_gives_the_orchestrator_the_same_rewrite_for_queued_steering_messages()
    {
        using var h = new Harness();
        File.WriteAllText(Path.Combine(h.Root, "b.cs"), "x");

        var parts = await h.Session.NormalizeInputAsync([new FileRefPart(Path.Combine(h.Root, "b.cs"), "b.cs")]);

        Assert.Equal("b.cs", ((FileRefPart)parts[0]).Path);
    }

    // ───────────── 引导消息 ─────────────

    [Fact]
    public async Task A_steering_message_is_committed_with_its_own_id_after_the_tool_results_and_consumed_once()
    {
        using var h = new Harness(tools: [new FakeTool("echo")]);
        h.Llm.Turn(Up.Call("c1", "echo"), Up.Text("done"));
        var queue = new Queue<Message>([Harness.User("steer-1", "also check the tests")]);

        await h.RunAsync("go", b => b.TakeSteering = () => queue.Count > 0 ? [queue.Dequeue()] : []);

        var ids = h.Stored().Select(m => m.Id.Value).ToList();
        Assert.Equal("steer-1", ids[3]);
        Assert.Equal(Role.User, h.Stored()[2].Role);
        Assert.Single(h.Stored()[2].Parts.OfType<ToolResultPart>()); // 插在工具结果之后,不拆开调用和结果
        Assert.Contains("steer-1", h.Repo.CommittedInputIds(Harness.SessionId, ["steer-1"]));
        Assert.Contains(h.Llm.Requests[1].Messages, m => m.Id.Value == "steer-1");
    }

    // ───────────── 硬校验 ─────────────

    [Fact]
    public async Task A_conversation_that_cannot_fit_the_protocol_window_fails_before_any_request_with_a_translatable_error()
    {
        using var h = new Harness();
        h.SeedHistory(Seed("u0", Role.User, new TextPart(new string('x', 4 * 195_000))));

        var ran = await h.RunAsync("hi", b => b.Settings = new SessionSettings { AutoCompact = false });

        Assert.Equal(RunStatus.Error, ran.Status);
        Assert.Equal((AgentErrorCode.ContextLength, SessionErrorKeys.ContextTooLong), (ran.Outcome.Error!.Code, ran.Outcome.Error.MessageKey));
        Assert.Empty(h.Llm.Requests); // 一个字节都没发
        Assert.Contains("used", ran.Outcome.Error.MessageParams!.Keys);
        Assert.NotEmpty(ran.Of<ContextUsageEvent>());
    }

    // ───────────── 工具上下文与扩展 ─────────────

    [Fact]
    public async Task Tools_get_the_environment_handle_the_project_root_and_the_environment_tag_and_the_orchestrator_can_enrich_further()
    {
        var echo = new FakeTool("echo");
        using var h = new Harness(tools: [echo]);
        h.Llm.Turn(Up.Call("c1", "echo"), Up.Text("done"));
        var enriched = false;

        var ran = await h.RunAsync("go", b => b.Enrich = c =>
        {
            enriched = c is SessionToolContext;
            return c;
        });

        var context = Assert.IsType<SessionToolContext>(echo.LastContext);
        Assert.Same(h.Env, context.Execution);
        Assert.Equal(h.RootPath, context.WorkspaceRoot);
        Assert.Equal(new EnvironmentTag("dev", "local"), context.Environment);
        Assert.True(enriched); // 编排层的派生类型套在会话层的外面
        Assert.Equal(new EnvironmentTag("dev", "local"), ran.Of<ToolStartEvent>().Single().Environment); // 审批和工具卡片都带执行环境标识
    }

    [Fact]
    public async Task Extensions_contribute_tools_system_sections_and_run_parameters()
    {
        var extra = new FakeTool("ext_tool");
        var ext = new Ext("demo", 0, r =>
        {
            r.RegisterTool(extra);
            r.ContributeSystemSection("permissions", _ => "Permission mode: ask before writing.");
            r.ContributeSystemSection("skills", info => $"# Available Skills\nsession {info.SessionId}");
            r.ContributeSystemSection("empty", _ => null);
            r.Mutate(SessionExtensionPoints.BeforeRun, (draft, _, _) =>
            {
                draft.ModePrompt = "# Plan mode\nread only";
                draft.StateSections.Add("Your todo list:\n[ ] write tests");
                draft.HeadSections.Add("Hook context: be careful");
                return ValueTask.CompletedTask;
            });
        });
        using var h = new Harness([ext], tools: [new FakeTool("Read"), new FakeTool("Write")]);

        await h.RunAsync("hi");

        var request = h.Llm.Requests[0];
        Assert.Equal(["Read", "Write", "ext_tool"], request.Tools.Select(t => t.ExternalName));
        Assert.Contains("Permission mode: ask before writing.", request.System);
        Assert.Contains("# Available Skills\nsession s1", request.System);
        Assert.Contains("# Plan mode\nread only", request.System);
        Assert.True(request.System.IndexOf("Permission mode", StringComparison.Ordinal) < request.System.IndexOf("# Plan mode", StringComparison.Ordinal));
        var first = TextOf(request.Messages[0]);
        Assert.Contains("Hook context: be careful", first);
        Assert.Contains("Your todo list:", first);
        Assert.DoesNotContain("todo list", h.Repo.History(Harness.SessionId)[0].PartsJson);
    }

    [Fact]
    public async Task A_before_run_tool_filter_only_narrows_the_snapshot_and_can_never_add()
    {
        var ext = new Ext("narrow", 0, r => r.Mutate(SessionExtensionPoints.BeforeRun, (draft, _, _) =>
        {
            draft.ToolFilter = spec => spec.InternalId != "Write";
            return ValueTask.CompletedTask;
        }));
        using var h = new Harness([ext], tools: [new FakeTool("Read"), new FakeTool("Write")]);

        await h.RunAsync("hi");

        Assert.Equal(["Read"], h.Llm.Requests[0].Tools.Select(t => t.ExternalName));
    }

    [Fact]
    public async Task The_input_extension_point_can_rewrite_the_user_message_before_it_is_committed()
    {
        var ext = new Ext("rewrite", 0, r => r.Transform(SessionExtensionPoints.Input, (payload, _, _) =>
            ValueTask.FromResult(new InputPayload([.. payload.Messages.Select(m => m with { Parts = [new TextPart("expanded: " + TextOf(m))] })]))));
        using var h = new Harness([ext]);

        await h.RunAsync("/cmd");

        Assert.Equal("expanded: /cmd", TextOf(h.Stored()[0]));
    }

    [Fact]
    public async Task Session_start_fires_once_per_session_object_not_per_run()
    {
        var starts = 0;
        var ext = new Ext("start", 0, r => r.On(SessionExtensionPoints.SessionStart, (_, _, _) =>
        {
            starts++;
            return ValueTask.CompletedTask;
        }));
        using var h = new Harness([ext]);

        await h.RunAsync("one");
        await h.RunAsync("two");

        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task A_tool_with_an_invalid_name_is_left_out_of_the_snapshot_with_a_diagnostic_instead_of_breaking_the_run()
    {
        using var h = new Harness(tools: [new FakeTool("good"), new FakeTool("bad name!"), new FakeTool("good")]);

        var ran = await h.RunAsync("hi");

        Assert.Equal(RunStatus.Done, ran.Status);
        Assert.Equal(["good"], h.Llm.Requests[0].Tools.Select(t => t.ExternalName));
        Assert.Equal(2, h.Log.Warnings.Count(w => w.Contains("left out of the snapshot", StringComparison.Ordinal)));
    }
}
