using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Storage;
using Ncw.Agent.Workflows.Engine;
using Ncw.Agent.Workflows.Tools;
using Ncw.Core.Domain;

namespace Ncw.Agent.Workflows.Tests;

public sealed class WorkflowEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncw-wf-{Guid.NewGuid():N}");
    private readonly AgentDatabase _db;
    private readonly WorkflowRunRepository _repo;

    public WorkflowEngineTests()
    {
        _db = AgentDatabase.Open(_root);
        _repo = new WorkflowRunRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeExecutor(Func<WorkflowNodeExecutionContext, CancellationToken, Task<WorkflowNodeExecutionResult>> handler) : IWorkflowNodeExecutor
    {
        public Task<WorkflowNodeExecutionResult> ExecuteAsync(WorkflowNodeExecutionContext context, CancellationToken ct) => handler(context, ct);
    }

    [Fact]
    public async Task Parallel_fanout_join_and_output_template_work_end_to_end()
    {
        var def = new WorkflowDefinition(
            Id: "parallel-test",
            Name: "Parallel Test",
            Nodes:
            [
                new("start", WorkflowNodeKind.Trigger, "Start"),
                new("a", WorkflowNodeKind.Agent, "Review Logic", Config: new(Prompt: "Check {{inputs.branch}}")),
                new("b", WorkflowNodeKind.Tool, "Run Tests"),
                new("join", WorkflowNodeKind.Join, "Merge", Config: new(Strategy: WorkflowJoinStrategy.All)),
                new("out", WorkflowNodeKind.Output, "Report", Config: new(OutputTemplate: "A={{nodes.a.output}} | B={{nodes.b.output}}")),
            ],
            Edges:
            [
                new("e1", "start", "a"),
                new("e2", "start", "b"),
                new("e3", "a", "join"),
                new("e4", "b", "join"),
                new("e5", "join", "out"),
            ]);

        var engine = new WorkflowEngine(_repo, new FakeExecutor((ctx, _) =>
        {
            var rendered = TemplateResolver.Render(ctx.Node.Config?.Prompt, ctx.Inputs, ctx.CompletedNodes);
            return Task.FromResult(new WorkflowNodeExecutionResult(
                Succeeded: true,
                OutputText: ctx.Node.Id == "a" ? $"logic({rendered})" : "tests-ok",
                Tokens: 10));
        }));

        engine.Start(new WorkflowLaunchRequest("wfr-1", "ws-1", def, new Dictionary<string, string> { ["branch"] = "main" }));
        var done = await engine.WaitForCompletionAsync("wfr-1");

        Assert.Equal(WorkflowRunStatus.Completed, done.Status);
        Assert.Equal("A=logic(Check main) | B=tests-ok", done.OutputText);
        Assert.Equal(20, done.TotalTokens);
        Assert.All(done.Nodes, n => Assert.Equal(WorkflowNodeRunStatus.Done, n.Status));
    }

    [Fact]
    public async Task Condition_branch_propagates_skipped_to_unselected_subgraph()
    {
        var def = new WorkflowDefinition(
            Id: "branch-test",
            Name: "Branch Test",
            Nodes:
            [
                new("start", WorkflowNodeKind.Trigger, "Start"),
                new("cond", WorkflowNodeKind.Condition, "Check Flag",
                    Outputs: [new("true", "Yes"), new("false", "No")],
                    Config: new(Expression: "{{inputs.mode}} == fast")),
                new("fast_step", WorkflowNodeKind.Tool, "Fast Path"),
                new("slow_step", WorkflowNodeKind.Agent, "Slow Path"),
                new("slow_follow", WorkflowNodeKind.Tool, "Slow Follow"),
            ],
            Edges:
            [
                new("e1", "start", "cond"),
                new("e2", "cond", "fast_step", SourcePort: "true"),
                new("e3", "cond", "slow_step", SourcePort: "false"),
                new("e4", "slow_step", "slow_follow"),
            ]);

        var executed = new ConcurrentBag<string>();
        var engine = new WorkflowEngine(_repo, new FakeExecutor((ctx, _) =>
        {
            executed.Add(ctx.Node.Id);
            return Task.FromResult(new WorkflowNodeExecutionResult(true, OutputText: "ok"));
        }));

        engine.Start(new WorkflowLaunchRequest("wfr-branch", "ws-1", def, new Dictionary<string, string> { ["mode"] = "fast" }));
        var done = await engine.WaitForCompletionAsync("wfr-branch");

        Assert.Equal(WorkflowRunStatus.Completed, done.Status);
        Assert.Contains("fast_step", executed);
        Assert.DoesNotContain("slow_step", executed);
        Assert.DoesNotContain("slow_follow", executed);
        Assert.Equal(WorkflowNodeRunStatus.Skipped, done.Nodes.Single(n => n.NodeId == "slow_step").Status);
        Assert.Equal(WorkflowNodeRunStatus.Skipped, done.Nodes.Single(n => n.NodeId == "slow_follow").Status);
    }

    [Fact]
    public async Task Resume_skips_completed_nodes_and_reruns_failed_subgraph_with_incremented_attempt()
    {
        var def = new WorkflowDefinition(
            Id: "resume-test",
            Name: "Resume Test",
            Nodes:
            [
                new("a", WorkflowNodeKind.Tool, "Step A"),
                new("b", WorkflowNodeKind.Agent, "Step B"),
            ],
            Edges: [new("e1", "a", "b")]);

        var aCount = 0;
        var bCount = 0;
        var engine = new WorkflowEngine(_repo, new FakeExecutor((ctx, _) =>
        {
            if (ctx.Node.Id == "a")
            {
                Interlocked.Increment(ref aCount);
                return Task.FromResult(new WorkflowNodeExecutionResult(true, OutputText: "from-a"));
            }

            var call = Interlocked.Increment(ref bCount);
            return Task.FromResult(call == 1
                ? new WorkflowNodeExecutionResult(false, Error: "transient")
                : new WorkflowNodeExecutionResult(true, OutputText: "fixed-" + ctx.CompletedNodes["a"].OutputText));
        }));

        engine.Start(new WorkflowLaunchRequest("wfr-res", "ws-1", def, new Dictionary<string, string>()));
        var failed = await engine.WaitForCompletionAsync("wfr-res");
        Assert.Equal(WorkflowRunStatus.Failed, failed.Status);
        Assert.Equal(1, aCount);
        Assert.Equal(1, bCount);

        engine.Resume("wfr-res");
        var resumed = await engine.WaitForCompletionAsync("wfr-res");
        Assert.Equal(WorkflowRunStatus.Completed, resumed.Status);
        Assert.Equal(1, aCount); // Step A was NOT re-executed!
        Assert.Equal(2, bCount);
        Assert.Equal(2, resumed.Nodes.Single(n => n.NodeId == "b").Attempt);
        Assert.Equal("fixed-from-a", resumed.OutputText);
    }

    [Fact]
    public async Task Permission_mode_only_narrows_and_depth_limit_is_enforced()
    {
        var def = new WorkflowDefinition(
            Id: "perm-test",
            Name: "Perm Test",
            Nodes: [new("a", WorkflowNodeKind.Agent, "A", Config: new(PermissionMode: "full"))],
            Edges: []);

        string? observedPerm = null;
        var engine = new WorkflowEngine(_repo, new FakeExecutor((ctx, _) =>
        {
            observedPerm = ctx.PermissionMode;
            return Task.FromResult(new WorkflowNodeExecutionResult(true, OutputText: "ok"));
        }));

        engine.Start(new WorkflowLaunchRequest("wfr-perm", "ws-1", def, new Dictionary<string, string>(), PermissionMode: "ask"));
        await engine.WaitForCompletionAsync("wfr-perm");
        Assert.Equal("ask", observedPerm);

        Assert.Throws<InvalidOperationException>(() =>
            engine.Start(new WorkflowLaunchRequest("wfr-deep", "ws-1", def, new Dictionary<string, string>(), NestingDepth: WorkflowValidator.MaxNestingDepth + 1)));
    }

    [Fact]
    public async Task Approval_rejection_routes_to_rejected_branch_and_skips_approved_branch()
    {
        var def = new WorkflowDefinition(
            Id: "approval-test",
            Name: "Approval Test",
            Nodes:
            [
                new("start", WorkflowNodeKind.Trigger, "Start"),
                new("gate", WorkflowNodeKind.Approval, "Approve Fix",
                    Outputs: [new("approved", "Approved"), new("rejected", "Rejected")]),
                new("fix", WorkflowNodeKind.Agent, "Apply Fix"),
                new("report", WorkflowNodeKind.Output, "Report Only", Config: new(OutputTemplate: "Decision={{nodes.gate.output}}")),
            ],
            Edges:
            [
                new("e1", "start", "gate"),
                new("e2", "gate", "fix", SourcePort: "approved"),
                new("e3", "gate", "report", SourcePort: "rejected"),
                new("e4", "fix", "report"),
            ]);

        var engine = new WorkflowEngine(_repo, new FakeExecutor((ctx, _) =>
        {
            if (ctx.Node.Kind == WorkflowNodeKind.Approval)
            {
                return Task.FromResult(new WorkflowNodeExecutionResult(true, Summary: "Denied", OutputText: "rejected", ActiveOutputPorts: ["rejected"]));
            }

            return Task.FromResult(new WorkflowNodeExecutionResult(true, OutputText: "should-not-run"));
        }));

        engine.Start(new WorkflowLaunchRequest("wfr-rej", "ws-1", def, new Dictionary<string, string>()));
        var done = await engine.WaitForCompletionAsync("wfr-rej");

        Assert.Equal(WorkflowRunStatus.Completed, done.Status);
        Assert.Equal(WorkflowNodeRunStatus.Skipped, done.Nodes.Single(n => n.NodeId == "fix").Status);
        Assert.Equal("Decision=rejected", done.OutputText);
    }

    [Fact]
    public async Task Cancel_cascades_to_running_node_and_cancels_remaining()
    {
        var def = new WorkflowDefinition(
            Id: "cancel-test",
            Name: "Cancel Test",
            Nodes:
            [
                new("a", WorkflowNodeKind.Agent, "Long Task"),
                new("b", WorkflowNodeKind.Tool, "After Long Task"),
            ],
            Edges: [new("e1", "a", "b")]);

        var started = new TaskCompletionSource();
        var engine = new WorkflowEngine(_repo, new FakeExecutor(async (ctx, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new WorkflowNodeExecutionResult(true);
        }));

        engine.Start(new WorkflowLaunchRequest("wfr-cancel", "ws-1", def, new Dictionary<string, string>()));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(engine.Cancel("wfr-cancel"));

        var done = await engine.WaitForCompletionAsync("wfr-cancel");
        Assert.Equal(WorkflowRunStatus.Cancelled, done.Status);
        Assert.All(done.Nodes, n => Assert.Equal(WorkflowNodeRunStatus.Cancelled, n.Status));
    }

    [Fact]
    public void RunWorkflowTool_builds_valid_dag_from_claude_code_style_steps_and_depends_on()
    {
        using var doc = JsonDocument.Parse("""
            [
              { "id": "a", "title": "Inspect API", "prompt": "Check API endpoints" },
              { "id": "b", "title": "Inspect Storage", "prompt": "Check SQLite schema" },
              { "id": "c", "title": "Summarize", "prompt": "Combine findings into a report", "depends_on": ["a", "b"] }
            ]
            """);

        var (def, error) = RunWorkflowTool.BuildDefinition("Audit Pipeline", doc.RootElement);
        Assert.Null(error);
        Assert.NotNull(def);
        Assert.Equal("Audit Pipeline", def.Name);
        Assert.Equal(3, def.Nodes.Count);
        Assert.All(def.Nodes, n => Assert.Equal(WorkflowNodeKind.Agent, n.Kind));
        Assert.Equal(2, def.Edges.Count);
        Assert.Empty(WorkflowValidator.Validate(def));
    }

    [Fact]
    public void EvaluateCondition_does_not_misparse_operators_inside_interpolated_output()
    {
        var nodes = new Dictionary<string, WorkflowNodeRunRecord>(StringComparer.Ordinal)
        {
            ["a"] = new("a", WorkflowNodeKind.Tool, WorkflowNodeRunStatus.Done, OutputText: "DDDD = f([x, y]) == true"),
            ["b"] = new("b", WorkflowNodeKind.Tool, WorkflowNodeRunStatus.Done, OutputText: "GGGG == true"),
            ["c"] = new("c", WorkflowNodeKind.Tool, WorkflowNodeRunStatus.Done, OutputText: "if (x != y) return z;"),
        };
        var emptyInputs = new Dictionary<string, string>(StringComparer.Ordinal);

        Assert.True(TemplateResolver.EvaluateCondition("{{nodes.a.output}} contains \"DDDD\"", emptyInputs, nodes));
        Assert.True(TemplateResolver.EvaluateCondition("{{nodes.b.output}} contains \"GGGG\"", emptyInputs, nodes));
        Assert.True(TemplateResolver.EvaluateCondition("{{nodes.b.output}} contains \"==\"", emptyInputs, nodes));
        Assert.True(TemplateResolver.EvaluateCondition("{{nodes.c.output}} contains \"return z\"", emptyInputs, nodes));
        Assert.False(TemplateResolver.EvaluateCondition("{{nodes.b.output}} == \"other\"", emptyInputs, nodes));
        Assert.True(TemplateResolver.EvaluateCondition("{{nodes.b.output}} == \"GGGG == true\"", emptyInputs, nodes));
        Assert.True(TemplateResolver.EvaluateCondition("{{nodes.b.output}}", emptyInputs, nodes));
    }
}
