using System.Collections.Immutable;
using System.Text.Json;
using Ncw.Core.Domain;

namespace Ncw.Core.Tests;

public sealed class WorkflowDomainTests
{
    private static WorkflowDefinition SampleDag() => new(
        Id: "pr-review",
        Name: "PR Review",
        Nodes:
        [
            new("start", WorkflowNodeKind.Trigger, "Start"),
            new("a", WorkflowNodeKind.Agent, "Review Logic"),
            new("b", WorkflowNodeKind.Tool, "Run Tests"),
            new("done", WorkflowNodeKind.Output, "Summary"),
        ],
        Edges:
        [
            new("e1", "start", "a"),
            new("e2", "start", "b"),
            new("e3", "a", "done"),
            new("e4", "b", "done"),
        ]);

    [Fact]
    public void Valid_dag_passes_validation_and_topological_order_is_deterministic()
    {
        var def = SampleDag();
        Assert.Empty(WorkflowValidator.Validate(def));

        var order = WorkflowValidator.TopologicalOrder(def);
        Assert.Equal(["start", "a", "b", "done"], order.Select(n => n.Id).ToArray());
    }

    [Fact]
    public void Cycle_is_rejected_by_validator_and_topological_order_returns_empty()
    {
        var cyclic = new WorkflowDefinition(
            Id: "cyclic-flow",
            Name: "Cyclic",
            Nodes:
            [
                new("n1", WorkflowNodeKind.Agent, "One"),
                new("n2", WorkflowNodeKind.Agent, "Two"),
            ],
            Edges:
            [
                new("e1", "n1", "n2"),
                new("e2", "n2", "n1"),
            ]);

        var errors = WorkflowValidator.Validate(cyclic);
        Assert.Contains(errors, e => e.Code == "workflows.invalid.cycle");
        Assert.Empty(WorkflowValidator.TopologicalOrder(cyclic));
    }

    [Fact]
    public void Port_data_type_mismatch_and_trigger_incoming_edge_are_rejected()
    {
        var def = new WorkflowDefinition(
            Id: "bad-ports",
            Name: "Bad Ports",
            Nodes:
            [
                new("t", WorkflowNodeKind.Trigger, "Trigger"),
                new("a", WorkflowNodeKind.Agent, "A", Outputs: [new("out", "Out", DataType: "diff")]),
                new("b", WorkflowNodeKind.Agent, "B", Inputs: [new("in", "In", DataType: "json")]),
            ],
            Edges:
            [
                new("e1", "a", "t"),
                new("e2", "a", "b", SourcePort: "out", TargetPort: "in"),
            ]);

        var errors = WorkflowValidator.Validate(def);
        Assert.Contains(errors, e => e.Code == "workflows.invalid.triggerInput");
        Assert.Contains(errors, e => e.Code == "workflows.invalid.portTypeMismatch");
    }

    [Fact]
    public void Node_merge_is_monotonic_within_attempt_and_higher_attempt_wins()
    {
        var done = new WorkflowNodeRunRecord("a", WorkflowNodeKind.Agent, WorkflowNodeRunStatus.Done, Attempt: 1, OutputText: "ok");
        var replayedRunning = new WorkflowNodeRunRecord("a", WorkflowNodeKind.Agent, WorkflowNodeRunStatus.Running, Attempt: 1);

        var merged = WorkflowView.MergeNode(done, replayedRunning);
        Assert.Equal(WorkflowNodeRunStatus.Done, merged.Status);
        Assert.Equal("ok", merged.OutputText);

        var retryRunning = new WorkflowNodeRunRecord("a", WorkflowNodeKind.Agent, WorkflowNodeRunStatus.Running, Attempt: 2);
        var retried = WorkflowView.MergeNode(done, retryRunning);
        Assert.Equal(WorkflowNodeRunStatus.Running, retried.Status);
        Assert.Equal(2, retried.Attempt);
    }

    [Fact]
    public void Disconnected_device_marks_active_workflow_unknown_but_keeps_terminal()
    {
        Assert.Equal(WorkflowRunStatus.Unknown, WorkflowView.DeriveStatus(WorkflowRunStatus.Running, DeviceConnection.Offline));
        Assert.Equal(WorkflowRunStatus.Unknown, WorkflowView.DeriveStatus(WorkflowRunStatus.WaitingApproval, DeviceConnection.Connecting));
        Assert.Equal(WorkflowRunStatus.Completed, WorkflowView.DeriveStatus(WorkflowRunStatus.Completed, DeviceConnection.Offline));
        Assert.False(WorkflowView.CanCancel(WorkflowRunStatus.Running, DeviceConnection.Offline));
    }

    [Fact]
    public void Json_round_trip_works_via_source_generated_context()
    {
        var def = SampleDag();
        var json = JsonSerializer.Serialize(def, WorkflowJsonContext.Default.WorkflowDefinition);
        var back = JsonSerializer.Deserialize(json, WorkflowJsonContext.Default.WorkflowDefinition);
        Assert.NotNull(back);
        Assert.Equal(def.Id, back.Id);
        Assert.Equal(def.Nodes.Count, back.Nodes.Count);
        Assert.Equal(def.Edges.Count, back.Edges.Count);
    }
}
