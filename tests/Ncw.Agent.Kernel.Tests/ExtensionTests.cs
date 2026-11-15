using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Tests.Support;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Tests;

/// <summary>扩展点的组合语义(06 §2):通知 / 就地修改 / 链式变换 / 闸门 / 最后者胜,以及「处理器异常不传播到循环」。</summary>
public sealed class ExtensionTests
{
    private sealed class Ext(string id, int order, Action<IExtensionRegistry> register) : IAgentExtension
    {
        public string Id { get; } = id;

        public int Order { get; } = order;

        public void Register(IExtensionRegistry registry) => register(registry);
    }

    private static readonly ExtensionPoint<string> Notify = new("test_notify");
    private static readonly MutatePoint<List<string>> Mutate = new("test_mutate");
    private static readonly TransformPoint<int> Transform = new("test_transform");
    private static readonly LastWinsPoint<string> LastWins = new("test_last_wins");

    private static ExtensionContext Ctx(CollectingDiagnostics? log = null) => new(new RunId("r"), "s", log ?? new CollectingDiagnostics());

    private static ToolInvocation Call(string name = "t") => new("c1", name, new FakeTool(name), default);

    [Fact]
    public async Task Handlers_run_by_Order_then_registration_order()
    {
        var order = new List<string>();
        var host = new ExtensionHost([
            new Ext("late", 10, r => r.On(Notify, (_, _, _) => { order.Add("late"); return ValueTask.CompletedTask; })),
            new Ext("permissions", -100, r => r.On(Notify, (_, _, _) => { order.Add("permissions"); return ValueTask.CompletedTask; })),
            new Ext("mid-a", 0, r => r.On(Notify, (_, _, _) => { order.Add("mid-a"); return ValueTask.CompletedTask; })),
            new Ext("mid-b", 0, r => r.On(Notify, (_, _, _) => { order.Add("mid-b"); return ValueTask.CompletedTask; })),
        ]);

        await host.NotifyAsync(Notify, "e", Ctx(), default);

        Assert.Equal(["permissions", "mid-a", "mid-b", "late"], order);
    }

    [Fact]
    public async Task A_notify_handler_that_throws_is_logged_and_the_others_still_run()
    {
        var ran = new List<string>();
        var log = new CollectingDiagnostics();
        var host = new ExtensionHost([
            new Ext("bad", 0, r => r.On(Notify, (_, _, _) => throw new InvalidOperationException("boom"))),
            new Ext("good", 1, r => r.On(Notify, (_, _, _) => { ran.Add("good"); return ValueTask.CompletedTask; })),
        ]);

        await host.NotifyAsync(Notify, "e", Ctx(log), default);

        Assert.Equal(["good"], ran);
        Assert.Single(log.Warnings);
    }

    [Fact]
    public async Task Mutate_handlers_modify_the_same_object_in_place_and_a_failure_keeps_earlier_changes()
    {
        var host = new ExtensionHost([
            new Ext("a", 0, r => r.Mutate(Mutate, (l, _, _) => { l.Add("a"); return ValueTask.CompletedTask; })),
            new Ext("bad", 1, r => r.Mutate(Mutate, (l, _, _) => { l.Add("bad-partial"); throw new InvalidOperationException(); })),
            new Ext("c", 2, r => r.Mutate(Mutate, (l, _, _) => { l.Add("c"); return ValueTask.CompletedTask; })),
        ]);
        var value = new List<string>();

        await host.MutateAsync(Mutate, value, Ctx(), default);

        Assert.Equal(["a", "bad-partial", "c"], value);
    }

    [Fact]
    public async Task Transform_handlers_chain_each_output_feeds_the_next_and_a_failing_one_is_skipped()
    {
        var host = new ExtensionHost([
            new Ext("double", 0, r => r.Transform(Transform, (v, _, _) => ValueTask.FromResult(v * 2))),
            new Ext("bad", 1, r => r.Transform<int>(Transform, (_, _, _) => throw new InvalidOperationException())),
            new Ext("plus3", 2, r => r.Transform(Transform, (v, _, _) => ValueTask.FromResult(v + 3))),
        ]);

        Assert.Equal(13, await host.TransformAsync(Transform, 5, Ctx(), default));
    }

    [Fact]
    public async Task LastWins_takes_the_last_replacement_each_handler_sees_the_original_and_null_abstains()
    {
        var seen = new List<string>();
        var host = new ExtensionHost([
            new Ext("a", 0, r => r.LastWins(LastWins, (v, _, _) => { seen.Add(v); return ValueTask.FromResult<string?>("from-a"); })),
            new Ext("b", 1, r => r.LastWins(LastWins, (v, _, _) => { seen.Add(v); return ValueTask.FromResult<string?>("from-b"); })),
            new Ext("c", 2, r => r.LastWins(LastWins, (v, _, _) => ValueTask.FromResult<string?>(null))),
        ]);

        Assert.Equal("from-b", await host.LastWinsAsync(LastWins, "original", Ctx(), default));
        Assert.Equal(["original", "original"], seen);
    }

    [Fact]
    public async Task The_first_deny_short_circuits_the_before_tool_gate_and_later_handlers_never_run()
    {
        var ran = new List<string>();
        var host = new ExtensionHost([
            new Ext("permissions", -100, r => r.Gate(LoopExtensionPoints.BeforeTool, (_, _, _) => { ran.Add("permissions"); return ValueTask.FromResult(PreflightDecision.Deny("deny rule")); })),
            new Ext("hook", 0, r => r.Gate(LoopExtensionPoints.BeforeTool, (_, _, _) => { ran.Add("hook"); return ValueTask.FromResult(PreflightDecision.Allow); })),
        ]);

        var decision = await host.GateAsync(LoopExtensionPoints.BeforeTool, Call(), Ctx(), default);

        Assert.Equal(PreflightDecision.Deny("deny rule"), decision);
        Assert.Equal(["permissions"], ran);
    }

    [Fact]
    public async Task Allow_needs_nobody_to_object_a_later_deny_still_wins_over_an_earlier_allow()
    {
        var host = new ExtensionHost([
            new Ext("a", 0, r => r.Gate(LoopExtensionPoints.BeforeTool, (_, _, _) => ValueTask.FromResult(PreflightDecision.Allow))),
            new Ext("b", 1, r => r.Gate(LoopExtensionPoints.BeforeTool, (_, _, _) => ValueTask.FromResult(PreflightDecision.Deny("late objection")))),
        ]);

        Assert.IsType<PreflightDecision.DenyDecision>(await host.GateAsync(LoopExtensionPoints.BeforeTool, Call(), Ctx(), default));
    }

    [Fact]
    public async Task With_no_handlers_a_gate_returns_its_default_allow()
    {
        var host = new ExtensionHost([]);

        Assert.Same(PreflightDecision.Allow, await host.GateAsync(LoopExtensionPoints.BeforeTool, Call(), Ctx(), default));
    }

    [Fact]
    public async Task A_before_tool_edit_is_returned_when_nobody_denies()
    {
        var edit = PreflightDecision.Edit(Ncw.Agent.Llm.Json.Jsn.Parse("""{"x":1}"""));
        var host = new ExtensionHost([
            new Ext("a", 0, r => r.Gate(LoopExtensionPoints.BeforeTool, (_, _, _) => ValueTask.FromResult(edit))),
            new Ext("b", 1, r => r.Gate(LoopExtensionPoints.BeforeTool, (_, _, _) => ValueTask.FromResult(PreflightDecision.Allow))),
        ]);

        Assert.Same(edit, await host.GateAsync(LoopExtensionPoints.BeforeTool, Call(), Ctx(), default));
    }

    [Fact]
    public async Task A_gate_handler_that_throws_abstains_so_the_call_is_allowed_like_hook_failures()
    {
        var host = new ExtensionHost([
            new Ext("hook", 0, r => r.Gate(LoopExtensionPoints.BeforeTool, (_, _, _) => throw new InvalidOperationException("hook crashed"))),
        ]);

        Assert.Same(PreflightDecision.Allow, await host.GateAsync(LoopExtensionPoints.BeforeTool, Call(), Ctx(), default));
    }

    [Fact]
    public async Task The_permission_extension_fails_closed_its_exception_is_a_denial()
    {
        var host = new ExtensionHost([
            new Ext("permissions", -100, r => r.Gate(
                LoopExtensionPoints.BeforeTool,
                (_, _, _) => throw new InvalidOperationException("rules file corrupt"),
                e => PreflightDecision.Deny("permission check failed: " + e.Message))),
        ]);

        var decision = await host.GateAsync(LoopExtensionPoints.BeforeTool, Call(), Ctx(), default);

        Assert.Equal(PreflightDecision.Deny("permission check failed: rules file corrupt"), decision);
    }

    [Fact]
    public async Task Cancellation_is_never_swallowed_by_the_dispatcher()
    {
        using var cts = new CancellationTokenSource();
        var host = new ExtensionHost([
            new Ext("slow", 0, r => r.On(Notify, (_, _, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; })),
        ]);

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await host.NotifyAsync(Notify, "e", Ctx(), cts.Token));
    }

    [Fact]
    public async Task Turn_end_gate_takes_the_first_handler_that_wants_to_continue_by_Order()
    {
        var host = new ExtensionHost([
            new Ext("todos", 5, r => r.Gate(LoopExtensionPoints.TurnEnd, (_, _, _) => ValueTask.FromResult(TurnEndDecision.Continue(Rig.User("todo-nudge", "finish your todos"))))),
            new Ext("goals", 1, r => r.Gate(LoopExtensionPoints.TurnEnd, (_, _, _) => ValueTask.FromResult(TurnEndDecision.Continue(Rig.User("goal-nudge", "goal not met"))))),
            new Ext("quiet", 0, r => r.Gate(LoopExtensionPoints.TurnEnd, (_, _, _) => ValueTask.FromResult(TurnEndDecision.Stop))),
        ]);
        var end = new TurnEnd(new RunId("r"), "s", 0, Rig.User("a", "x"), StopReason.EndTurn, [], 0, 0, false);

        var decision = await host.GateAsync(LoopExtensionPoints.TurnEnd, end, Ctx(), default);

        Assert.Equal("goal-nudge", decision.InternalMessage!.Id.Value);
    }

    // ───────────────────────── 接进循环 ─────────────────────────

    [Fact]
    public async Task The_loop_hooks_built_from_extensions_enforce_a_permission_deny_end_to_end()
    {
        var tool = new FakeTool("Bash");
        var host = new ExtensionHost([
            new Ext("permissions", -100, r => r.Gate(LoopExtensionPoints.BeforeTool, (call, _, _) =>
                ValueTask.FromResult(call.Name == "Bash" ? PreflightDecision.Deny("Bash is not allowed here") : PreflightDecision.Allow))),
        ]);
        var rig = new Rig().Tool(tool);
        var hooks = host.CreateLoopHooks(Ctx(), (turn, _) => ValueTask.FromResult(TurnPlan.Run(new LlmRequest("s", turn.Transcript, [], turn.Options), rig.Snapshot())), commit: (m, _) =>
        {
            rig.Committed.Add(m);
            return ValueTask.CompletedTask;
        });
        rig.Llm.Enqueue(Up.Call("c1", "Bash"), Up.Text("ok"));

        var events = new List<AgentEvent>();
        await foreach (var ev in rig.Loop().RunAsync(rig.Input(Rig.User("u1", "go")), hooks))
        {
            events.Add(ev);
        }

        EventRules.AssertWellFormed(events);
        Assert.Equal(0, tool.Calls);
        Assert.Equal("Bash is not allowed here", rig.Committed.SelectMany(m => m.Parts).OfType<ToolResultPart>().Single().Output.Content);
    }

    [Fact]
    public async Task Prepare_turn_extensions_modify_the_draft_in_place_for_example_filtering_tools_and_stopping()
    {
        var allowed = new FakeTool("allowed");
        var secret = new FakeTool("secret");
        var host = new ExtensionHost([
            new Ext("plan-mode", 0, r => r.Mutate(LoopExtensionPoints.PrepareTurn, (draft, _, _) =>
            {
                draft.Tools = new ToolSnapshot(draft.Tools.Names.Where(n => n != "secret").Select(n => new ToolBinding(n, n == "allowed" ? allowed : secret)));
                draft.Request = draft.Request! with { System = draft.Request.System + "+plan" };
                return ValueTask.CompletedTask;
            })),
        ]);
        var rig = new Rig().Tool(allowed).Tool(secret);
        var hooks = host.CreateLoopHooks(Ctx(), (turn, _) => ValueTask.FromResult(TurnPlan.Run(new LlmRequest("sys", turn.Transcript, [], turn.Options), rig.Snapshot())));
        rig.Llm.Enqueue(Up.Text("ok"));

        await foreach (var _ in rig.Loop().RunAsync(rig.Input(Rig.User("u1", "go")), hooks))
        {
        }

        Assert.Equal(["allowed"], rig.Llm.Requests[0].Tools.Select(t => t.ExternalName));
        Assert.Equal("sys+plan", rig.Llm.Requests[0].System);
    }

    [Fact]
    public async Task A_prepare_turn_extension_can_stop_the_run()
    {
        var host = new ExtensionHost([
            new Ext("budget", 0, r => r.Mutate(LoopExtensionPoints.PrepareTurn, (draft, _, _) =>
            {
                draft.Stop(RunStatus.Error, AgentError.Create(AgentErrorCode.Unknown, "budget exhausted", false));
                return ValueTask.CompletedTask;
            })),
        ]);
        var rig = new Rig();
        var hooks = host.CreateLoopHooks(Ctx(), (turn, _) => ValueTask.FromResult(TurnPlan.Run(new LlmRequest("s", turn.Transcript, [], turn.Options))));

        var events = new List<AgentEvent>();
        await foreach (var ev in rig.Loop().RunAsync(rig.Input(Rig.User("u1", "go")), hooks))
        {
            events.Add(ev);
        }

        Assert.Equal("budget exhausted", events.OfType<RunEndEvent>().Single().Error!.Message);
        Assert.Empty(rig.Llm.Requests);
    }

    [Fact]
    public async Task The_before_provider_request_extension_replaces_the_final_request_last_one_wins()
    {
        var host = new ExtensionHost([
            new Ext("debug", 0, r => r.LastWins(LoopExtensionPoints.BeforeProviderRequest, (req, _, _) => ValueTask.FromResult<LlmRequest?>(req with { System = "debug" }))),
            new Ext("patch", 1, r => r.LastWins(LoopExtensionPoints.BeforeProviderRequest, (req, _, _) => ValueTask.FromResult<LlmRequest?>(req with { System = "patched" }))),
        ]);
        var rig = new Rig();
        var hooks = host.CreateLoopHooks(Ctx(), (turn, _) => ValueTask.FromResult(TurnPlan.Run(new LlmRequest("s", turn.Transcript, [], turn.Options))));
        rig.Llm.Enqueue(Up.Text("ok"));

        await foreach (var _ in rig.Loop().RunAsync(rig.Input(Rig.User("u1", "go")), hooks))
        {
        }

        Assert.Equal("patched", rig.Llm.Requests[0].System);
    }

    [Fact]
    public async Task After_tool_extensions_chain_and_the_loop_still_enforces_append_only()
    {
        var host = new ExtensionHost([
            new Ext("hooks", 0, r => r.Transform(LoopExtensionPoints.AfterTool, (p, _, _) =>
                ValueTask.FromResult(p with { Result = p.Result with { Content = [.. p.Result.Content, new TextPart("lint ok")] } }))),
            new Ext("rogue", 1, r => r.Transform(LoopExtensionPoints.AfterTool, (p, _, _) =>
                ValueTask.FromResult(p with { Result = ToolResult.Text("rewritten") }))),
        ]);
        var rig = new Rig().Tool(new FakeTool("a", (_, _, _) => Task.FromResult(ToolResult.Text("out"))));
        var hooks = host.CreateLoopHooks(Ctx(), (turn, _) => ValueTask.FromResult(TurnPlan.Run(new LlmRequest("s", turn.Transcript, [], turn.Options), rig.Snapshot())), commit: (m, _) =>
        {
            rig.Committed.Add(m);
            return ValueTask.CompletedTask;
        });
        rig.Llm.Enqueue(Up.Call("c1", "a"), Up.Text("done"));

        await foreach (var _ in rig.Loop().RunAsync(rig.Input(Rig.User("u1", "go")), hooks))
        {
        }

        // rogue 把内容整个改写了:不是「原内容 + 追加」,被忽略(回落到原始输出)。
        Assert.Equal("out", rig.Committed.SelectMany(m => m.Parts).OfType<ToolResultPart>().Single().Output.Content);
    }
}
