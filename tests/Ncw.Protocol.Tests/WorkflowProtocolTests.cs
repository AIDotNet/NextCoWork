using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

public sealed class WorkflowProtocolTests
{
    [Fact]
    public void Workflow_methods_declare_roles_capabilities_and_unique_names()
    {
        Assert.Equal(5, WorkflowMethods.All.Count);
        Assert.Equal(WorkflowMethods.All.Count, WorkflowMethods.All.Select(static m => m.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(WorkflowMethods.All, m => Assert.Equal(Capabilities.Workflows, m.Capability));
        Assert.Equal(MethodKind.Command, WorkflowMethods.Cancel.Kind);
        Assert.Equal(MethodKind.Query, WorkflowMethods.ListRuns.Kind);
    }

    [Fact]
    public void Workflow_snapshot_and_event_round_trip_in_messagepack()
    {
        var snapshot = new WorkflowRunSnapshotDto(
            RunId: "wfr_1",
            WorkspaceId: "ws_1",
            Title: "PR Review",
            Status: "running",
            DefinitionJson: "{}",
            Nodes: [new WorkflowNodeRunDto("n1", "agent", "running", 1, ChildRunId: "wfr_1:node:n1:a1", ChildSessionId: "s_child")],
            Edges: [new WorkflowEdgeRunDto("e1", "active")],
            SessionId: "s_1",
            WorkflowId: "pr-review",
            EnvironmentId: "local",
            TotalTokens: 120,
            CreatedAt: 1000,
            StartedAt: 1010,
            Seq: 3);

        var bytes = FrameCodec.EncodePayload(snapshot, PayloadShapes.GetShape<WorkflowRunSnapshotDto>());
        var decoded = FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<WorkflowRunSnapshotDto>());
        Assert.Equal(snapshot.RunId, decoded.RunId);
        Assert.Equal(snapshot.Status, decoded.Status);
        Assert.Single(decoded.Nodes);
        Assert.Equal("s_child", decoded.Nodes[0].ChildSessionId);
        Assert.Single(decoded.Edges);
        Assert.Equal("active", decoded.Edges[0].Status);

        var evt = new WorkflowRunEventDto("wfr_1", WorkflowEvents.NodeUpdated, Node: snapshot.Nodes[0], TotalTokens: 120);
        var evtBytes = FrameCodec.EncodePayload(evt, PayloadShapes.GetShape<WorkflowRunEventDto>());
        var evtDecoded = FrameCodec.DecodePayload(evtBytes, PayloadShapes.GetShape<WorkflowRunEventDto>());
        Assert.Equal(WorkflowEvents.NodeUpdated, evtDecoded.Kind);
        Assert.Equal("n1", evtDecoded.Node?.NodeId);
    }
}
