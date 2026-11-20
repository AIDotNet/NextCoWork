using System.Text.Json;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

public sealed class WorkflowStoreTests
{
    [Fact]
    public void Reducer_merges_snapshots_and_events_monotonically_by_device_and_run()
    {
        var def = new WorkflowDefinition(
            Id: "demo",
            Name: "Demo",
            Nodes: [new("a", WorkflowNodeKind.Tool, "Step A")],
            Edges: []);
        var defJson = JsonSerializer.Serialize(def, WorkflowJsonContext.Default.WorkflowDefinition);

        var snap = new WorkflowRunSnapshotDto(
            RunId: "wfr-1",
            WorkspaceId: "ws-1",
            Title: "Demo",
            Status: "running",
            DefinitionJson: defJson,
            Nodes: [new WorkflowNodeRunDto("a", "tool", "running", 1)],
            Edges: [],
            Seq: 1);

        var state = WorkflowReducer.ApplySnapshot(WorkflowMirror.Empty, "dev-1", snap);
        Assert.True(state.Runs.TryGetValue(new WorkflowRunKey("dev-1", "wfr-1"), out var run));
        Assert.Equal(WorkflowRunStatus.Running, run.Status);

        // 节点进入终态 Done
        state = WorkflowReducer.ApplyEvent(state, "dev-1", new WorkflowRunEventDto(
            "wfr-1",
            WorkflowEvents.NodeUpdated,
            Status: "completed",
            Node: new WorkflowNodeRunDto("a", "tool", "done", 1, OutputText: "ok")));

        run = state.Runs[new WorkflowRunKey("dev-1", "wfr-1")];
        Assert.Equal(WorkflowRunStatus.Completed, run.Status);
        Assert.Equal(WorkflowNodeRunStatus.Done, run.Nodes[0].Status);

        // 重放旧的 running 事件不能把终态降级
        state = WorkflowReducer.ApplyEvent(state, "dev-1", new WorkflowRunEventDto(
            "wfr-1",
            WorkflowEvents.NodeUpdated,
            Status: "running",
            Node: new WorkflowNodeRunDto("a", "tool", "running", 1)));

        run = state.Runs[new WorkflowRunKey("dev-1", "wfr-1")];
        Assert.Equal(WorkflowRunStatus.Completed, run.Status);
        Assert.Equal(WorkflowNodeRunStatus.Done, run.Nodes[0].Status);
    }
}
