using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Tools;

namespace Ncw.Agent.Extensions.Tests;

/// <summary>
/// 端到端:<c>AgentLoop</c> + 假上游 + <b>真实工具</b>(Bash / Write / Read,跑在 <c>LocalEnvironment</c> 上)+ 真实的权限扩展和交互闸门。
/// 验证接缝:预检顺序 → 权限扩展发起审批 → 控制端回答 → 放行 / 拒绝 → 工具真的执行或没执行 → 下一个请求里模型看到的结果。
/// </summary>
public sealed class EndToEndTests : IDisposable
{
    private readonly Harness _h;
    private readonly List<AgentEvent> _events = [];

    public EndToEndTests() => _h = new Harness();

    public void Dispose() => _h.Dispose();

    private sealed record Outcome(List<AgentEvent> Events, List<Message> Committed, FakeLlm Llm)
    {
        public RunStatus Status => Events.OfType<RunEndEvent>().Single().Status;

        public string ResultText(string callId) =>
            Committed.SelectMany(m => m.Parts).OfType<ToolResultPart>().Single(r => r.CallId == callId).Output.Content;

        public bool IsError(string callId) => Committed.SelectMany(m => m.Parts).OfType<ToolResultPart>().Single(r => r.CallId == callId).IsError;
    }

    private Task<Outcome> StartAsync(FakeLlm llm, CancellationToken ct = default, int depth = 0, RunId? runId = null, ToolRuntime? runtime = null)
    {
        var committed = new List<Message>();
        var tools = BuiltinTools.Create();
        var snapshot = new ToolSnapshot(tools.Select(t => new ToolBinding(t.Info.InternalId, t)));
        var ctx = _h.Context(runId);
        var hooks = _h.Host.CreateLoopHooks(
            ctx,
            (turn, _) => ValueTask.FromResult(TurnPlan.Run(new LlmRequest("system", turn.Transcript, [], turn.Options), snapshot)),
            commit: (m, _) =>
            {
                lock (committed)
                {
                    committed.Add(m);
                }

                return ValueTask.CompletedTask;
            },
            enrichToolContext: ExecutionToolContext.Enricher(runtime ?? _h.Fx.Runtime));
        var loop = new AgentLoop(llm, new SequentialIds(), TimeProvider.System, new NoDelay());
        var input = new LoopInput(
            runId ?? _h.RunId, "session-1", "workspace-1", [],
            [new Message(new MessageId("user-1"), Role.User, [new TextPart("clean the build dir")], DateTimeOffset.UnixEpoch)],
            ModelRef.Unpinned("alias"), new GenerationOptions(4096), new EnvironmentTag("dev-1", "local"), depth);
        return Task.Run(async () =>
        {
            var events = new List<AgentEvent>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await foreach (var ev in loop.RunAsync(input, hooks, ct).WithCancellation(timeout.Token))
            {
                lock (events)
                {
                    events.Add(ev);
                }
            }

            // 编排层的工作:run_end 之后通知扩展(兜底清理交互、摘掉登记)
            await _h.Host.NotifyAsync(Ncw.Agent.Kernel.Extensions.LoopExtensionPoints.RunEnd, events.OfType<RunEndEvent>().Single(), ctx, default);
            return new Outcome(events, committed, llm);
        });
    }

    private static FakeLlm Script(params IReadOnlyList<LlmEvent>[] turns)
    {
        var llm = new FakeLlm();
        foreach (var t in turns)
        {
            llm.Then(t);
        }

        return llm;
    }

    private static string RemoveBuild(string command = "rm -rf build") => Up.J(("command", command), ("description", "remove the build dir"));

    private string Build => _h.Fx.Abs("build");

    private void MakeBuild()
    {
        Directory.CreateDirectory(Build);
        File.WriteAllText(Path.Combine(Build, "out.o"), "x");
    }

    private static string LastToolResult(FakeLlm llm)
    {
        var request = llm.Requests[^1];
        return string.Join('\n', request.Messages.SelectMany(m => m.Parts).OfType<ToolResultPart>().Select(r => r.Output.Content));
    }

    // ───────────────────────── 危险命令 ─────────────────────────

    [Fact]
    public async Task A_dangerous_command_matching_a_deny_rule_is_refused_without_asking_and_never_runs()
    {
        MakeBuild();
        await _h.Book.AddAsync(RememberScope.Project, _h.State.RuleContext, RuleBucket.Deny, "Bash(rm:*)", default);
        _h.State.Mode = PermissionMode.Full; // 连完全访问也放宽不了 deny 规则
        var llm = Script(
            Up.Calls(("c1", "Bash", RemoveBuild("echo cleaning && rm -rf build"))),
            Up.Say("ok, I won't"));
        var outcome = await StartAsync(llm);

        Assert.Equal(RunStatus.Done, outcome.Status);
        Assert.True(Directory.Exists(Build), "the command must not have run");
        Assert.True(outcome.IsError("c1"));
        Assert.Contains("Bash(rm:*)", outcome.ResultText("c1"));
        Assert.Contains("Bash(rm:*)", LastToolResult(llm)); // 模型看见了拒绝和原因
        Assert.Empty(_h.Journal.Opened);                    // 没有问人
    }

    [Fact]
    public async Task A_dangerous_command_asks_the_user_with_the_environment_and_risk_and_runs_after_approval()
    {
        MakeBuild();
        var llm = Script(Up.Calls(("c1", "Bash", RemoveBuild())), Up.Say("done"));
        var run = StartAsync(llm);

        var p = await _h.NextPending();
        Assert.Equal("dev-1", p.Env.DeviceId);
        Assert.Equal("local", p.Env.Environment);
        Assert.Equal(CommandRisk.Dangerous, p.Risk);
        Assert.Equal("Bash(rm -rf build)", p.SuggestedRule);
        Assert.Equal("c1", p.CallId);
        Assert.True(Directory.Exists(Build), "nothing may run before the answer");

        Assert.IsType<RespondResult.Accepted>(_h.Respond(p, PermissionChoice.AllowOnce));
        var outcome = await run;
        Assert.Equal(RunStatus.Done, outcome.Status);
        Assert.False(Directory.Exists(Build), "approved, so it ran");
        Assert.False(outcome.IsError("c1"));
        // tool_start 带的执行环境标识和审批卡片上的是同一个
        var start = outcome.Events.OfType<ToolStartEvent>().Single();
        Assert.Equal(p.Env.ToTag(), start.Environment);
        Assert.Empty(_h.Gate.Snapshot().Pending);
    }

    [Fact]
    public async Task A_refusal_reaches_the_model_as_a_visible_tool_result_and_the_command_never_runs()
    {
        MakeBuild();
        var llm = Script(Up.Calls(("c1", "Bash", RemoveBuild())), Up.Say("I will leave it"));
        var run = StartAsync(llm);
        _h.Respond(await _h.NextPending(), PermissionChoice.Deny("keep the build output"));
        var outcome = await run;

        Assert.Equal(RunStatus.Done, outcome.Status);
        Assert.True(Directory.Exists(Build));
        Assert.True(outcome.IsError("c1"));
        var seen = LastToolResult(llm);
        Assert.Contains("denied permission", seen);
        Assert.Contains("keep the build output", seen);
        Assert.Equal(2, llm.Requests.Count); // 拒绝后循环继续,模型得到下一个回合
    }

    [Fact]
    public async Task Edited_arguments_are_what_actually_runs_and_are_rechecked_against_deny_rules()
    {
        MakeBuild();
        File.WriteAllText(_h.Fx.Abs("keep.txt"), "k");
        await _h.Book.AddAsync(RememberScope.Session, _h.State.RuleContext, RuleBucket.Deny, "Bash(rm -rf /:*)", default);
        var llm = Script(Up.Calls(("c1", "Bash", RemoveBuild())), Up.Say("done"));
        var run = StartAsync(llm);
        _h.Respond(await _h.NextPending(), PermissionChoice.AllowEdited(RemoveBuild("echo edited").Json()));
        var outcome = await run;
        Assert.True(Directory.Exists(Build), "the original command was replaced, not run");
        Assert.Contains("edited", outcome.ResultText("c1"));

    }

    // ───────────────────────── 记住 ─────────────────────────

    [Fact]
    public async Task Allow_always_for_the_project_stops_the_next_identical_prefix_from_asking()
    {
        var llm = Script(
            Up.Calls(("c1", "Bash", Up.J(("command", "echo one"), ("description", "say one")))),
            Up.Calls(("c2", "Bash", Up.J(("command", "echo two"), ("description", "say two")))),
            Up.Say("done"));
        var run = StartAsync(llm);
        var p = await _h.NextPending();
        Assert.Equal("Bash(echo:*)", p.SuggestedRule);
        _h.Respond(p, PermissionChoice.AllowAlways(RememberScope.Project));
        var outcome = await run;

        Assert.Equal(RunStatus.Done, outcome.Status);
        Assert.Single(_h.Journal.Opened); // 第二条没有再问
        Assert.Contains("two", outcome.ResultText("c2"));
        Assert.Contains("Bash(echo:*)", _h.Fx.ReadSettings());
    }

    // ───────────────────────── 多端先到先得 ─────────────────────────

    [Fact]
    public async Task Two_controllers_answer_at_the_same_time_only_one_succeeds_and_the_run_follows_the_winner()
    {
        MakeBuild();
        var llm = Script(Up.Calls(("c1", "Bash", RemoveBuild())), Up.Say("done"));
        var run = StartAsync(llm);
        var p = await _h.NextPending();

        using var barrier = new Barrier(2);
        RespondResult Go(string who, PermissionChoice choice)
        {
            barrier.SignalAndWait();
            return _h.Gate.Respond(p.Id, p.Version, new ToolPermissionResponse(choice), who);
        }

        var phone = Task.Run(() => Go("phone", PermissionChoice.AllowOnce));
        var desktop = Task.Run(() => Go("desktop", PermissionChoice.Deny("no")));
        var results = await Task.WhenAll(phone, desktop);
        var win = Assert.Single(results.OfType<RespondResult.Accepted>());
        var lose = Assert.Single(results.OfType<RespondResult.Conflict>());
        Assert.Equal(win.Resolution.ResolvedBy, lose.Resolution!.ResolvedBy);

        var outcome = await run;
        var approved = win.Resolution.ResolvedBy == "phone";
        Assert.Equal(approved, !Directory.Exists(Build)); // 执行与否恰好跟随赢家
        Assert.Equal(!approved, outcome.IsError("c1"));
        Assert.Single(_h.Journal.Settled);
    }

    [Fact]
    public async Task A_late_answer_after_the_first_is_a_conflict_that_names_who_handled_it()
    {
        var llm = Script(Up.Calls(("c1", "Bash", RemoveBuild("echo hi"))), Up.Say("done"));
        var run = StartAsync(llm);
        var p = await _h.NextPending();
        _h.Respond(p, PermissionChoice.AllowOnce, "phone");
        await run;
        var late = Assert.IsType<RespondResult.Conflict>(_h.Respond(p, PermissionChoice.Deny(), "desktop"));
        Assert.Equal("phone", late.Resolution!.ResolvedBy);
        Assert.Equal(ResolutionStatus.Answered, late.Resolution.Status);
    }

    // ───────────────────────── 中止 ─────────────────────────

    [Fact]
    public async Task Aborting_the_run_while_an_approval_is_pending_cleans_it_up_and_pairs_the_tool_call()
    {
        MakeBuild();
        var llm = Script(Up.Calls(("c1", "Bash", RemoveBuild())), Up.Say("never reached"));
        using var cts = new CancellationTokenSource();
        var run = StartAsync(llm, cts.Token);
        var p = await _h.NextPending();
        await cts.CancelAsync();
        var outcome = await run;

        Assert.Equal(RunStatus.Aborted, outcome.Status);
        Assert.Empty(_h.Gate.Snapshot().Pending);
        Assert.True(Directory.Exists(Build), "an aborted approval never runs the command");
        Assert.True(outcome.IsError("c1")); // 调用有配对的「已中止」结果,下一次请求不会 400
        var late = Assert.IsType<RespondResult.Conflict>(_h.Respond(p, PermissionChoice.AllowOnce));
        Assert.Equal(ResolutionStatus.Aborted, late.Resolution!.Status);
        Assert.Equal(ResolutionStatus.Aborted, _h.Journal.Settled.Single().Status);
        Assert.Single(llm.Requests);
    }

    // ───────────────────────── 顺序预检 ─────────────────────────

    [Fact]
    public async Task Approval_cards_appear_one_at_a_time_in_call_order_and_only_approved_calls_run()
    {
        var llm = Script(
            Up.Calls(
                ("c1", "Bash", Up.J(("command", "echo first"), ("description", "first"))),
                ("c2", "Bash", Up.J(("command", "echo second"), ("description", "second")))),
            Up.Say("done"));
        var run = StartAsync(llm);

        var first = await _h.NextPending();
        Assert.Equal("c1", first.CallId);
        await Task.Delay(100);
        Assert.Single(_h.Gate.Snapshot().Pending); // 第二张卡片要等第一张回答之后才出现
        _h.Respond(first, PermissionChoice.AllowOnce);

        await Wait.UntilAsync(() => _h.Gate.Snapshot().Pending.Any(x => ((ToolPermission)x).CallId == "c2"));
        var second = (ToolPermission)_h.Gate.Snapshot().Pending.Single();
        _h.Respond(second, PermissionChoice.Deny("nope"));
        var outcome = await run;

        Assert.Contains("first", outcome.ResultText("c1"));
        Assert.False(outcome.IsError("c1"));
        Assert.True(outcome.IsError("c2"));
    }

    // ───────────────────────── 子代理 ─────────────────────────

    [Fact]
    public async Task A_subagent_run_is_denied_with_a_clear_reason_and_never_hangs_waiting_for_a_human()
    {
        var childRun = new RunId("child-1");
        _h.Runs.Register(_h.State.ForSubagent(childRun, _h.Gate.OpenRun(childRun, 1), PermissionMode.Auto));
        var llm = Script(Up.Calls(("c1", "Bash", RemoveBuild("echo hi"))), Up.Say("reporting back"));
        var outcome = await StartAsync(llm, depth: 1, runId: childRun);

        Assert.Equal(RunStatus.Done, outcome.Status);
        Assert.True(outcome.IsError("c1"));
        Assert.Contains("subagent cannot ask", outcome.ResultText("c1"));
        Assert.Contains("Bash(echo:*)", outcome.ResultText("c1"));
        Assert.Empty(_h.Journal.Opened);
    }

    // ───────────────────────── 读写与围栏 ─────────────────────────

    [Fact]
    public async Task Reads_never_ask_and_a_write_inside_the_project_asks_once_then_writes_for_real()
    {
        _h.Fx.Put("a.txt", "hello");
        var target = _h.Fx.Abs("b.txt");
        var llm = Script(
            Up.Calls(("c1", "Read", Up.J(("file_path", _h.Fx.Abs("a.txt"))))),
            Up.Calls(("c2", "Write", Up.J(("file_path", target), ("content", "new")))),
            Up.Say("done"));
        var run = StartAsync(llm);
        var p = await _h.NextPending();
        Assert.Equal("c2", p.CallId); // Read 没有问
        Assert.Equal("Write(b.txt)", p.SuggestedRule);
        Assert.False(p.OutsideWorkspace);
        _h.Respond(p, PermissionChoice.AllowOnce);
        var outcome = await run;
        Assert.Equal("new", File.ReadAllText(target));
        Assert.False(outcome.IsError("c2"));
    }

    [Fact]
    public async Task The_plan_fence_lets_the_plan_file_through_without_asking_and_the_write_tool_enforces_it_too()
    {
        var plan = _h.Fx.P(".plan/p1.md");
        var rt = new ToolRuntime { Environment = _h.Fx.Env, Root = _h.Fx.Runtime.Root, WriteFence = plan };
        _h.Runs.Remove(_h.RunId);
        var state = PermissionRunState.FromRuntime(rt, _h.RunId, "session-1", _h.Interactions, PermissionMode.Ask);
        state.WebSearch = true;
        _h.Runs.Register(state);
        var llm = Script(
            Up.Calls(("c1", "Write", Up.J(("file_path", plan.Value), ("content", "# plan")))),
            Up.Say("done"));
        var outcome = await StartAsync(llm, runtime: rt);

        Assert.Equal(RunStatus.Done, outcome.Status);
        Assert.Equal("# plan", File.ReadAllText(plan.Value));
        Assert.Empty(_h.Journal.Opened);

        // 围栏外的文件:权限层不当作只读(要问),写工具自己也会拒绝
                var state2 = PermissionRunState.FromRuntime(rt, _h.RunId, "session-1", _h.Gate.OpenRun(_h.RunId), PermissionMode.Ask);
        state2.WebSearch = true;
        _h.Runs.Register(state2);
        var llm2 = Script(Up.Calls(("c2", "Write", Up.J(("file_path", _h.Fx.Abs("src.txt")), ("content", "x")))), Up.Say("done"));
        var run2 = StartAsync(llm2, runtime: rt);
        var p = await _h.NextPending();
        _h.Respond(p, PermissionChoice.AllowOnce);
        var outcome2 = await run2;
        Assert.True(outcome2.IsError("c2"));
        Assert.Contains("Plan mode may only modify", outcome2.ResultText("c2"));
        Assert.False(File.Exists(_h.Fx.Abs("src.txt")));
    }
}
