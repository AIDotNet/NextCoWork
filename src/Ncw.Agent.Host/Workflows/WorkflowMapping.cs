using System.Text.Json;
using Ncw.Agent.Storage;
using Ncw.Agent.Workflows.Engine;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Agent.Host.Workflows;

internal static class WorkflowMapping
{
    public static WorkflowNodeRunDto ToDto(WorkflowNodeRunRecord n) => new(
        NodeId: n.NodeId,
        Kind: n.Kind.ToString().ToLowerInvariant(),
        Status: WorkflowEngine.FormatNodeStatus(n.Status),
        Attempt: n.Attempt,
        ChildRunId: n.ChildRunId,
        ChildSessionId: n.ChildSessionId,
        Summary: n.Summary,
        OutputText: n.OutputText,
        OutputJson: n.OutputJson,
        Error: n.Error,
        Tokens: n.Tokens,
        StartedAt: n.StartedAt,
        FinishedAt: n.FinishedAt);

    public static WorkflowEdgeRunDto ToDto(WorkflowEdgeRunRecord e) => new(
        EdgeId: e.EdgeId,
        Status: e.Status.ToString().ToLowerInvariant());

    public static WorkflowRunSnapshotDto ToSnapshot(WorkflowRunRecord run) => new(
        RunId: run.RunId,
        WorkspaceId: run.WorkspaceId,
        Title: run.Title,
        Status: WorkflowEngine.FormatRunStatus(run.Status),
        DefinitionJson: JsonSerializer.Serialize(run.Definition, WorkflowJsonContext.Default.WorkflowDefinition),
        Nodes: [.. run.Nodes.Select(ToDto)],
        Edges: [.. run.Edges.Select(ToDto)],
        SessionId: run.SessionId,
        WorkflowId: run.WorkflowId,
        EnvironmentId: run.EnvironmentId,
        OutputText: run.OutputText,
        OutputJson: run.OutputJson,
        Error: run.Error,
        TotalTokens: run.TotalTokens,
        CreatedAt: run.CreatedAt,
        StartedAt: run.StartedAt,
        FinishedAt: run.FinishedAt,
        Seq: run.Seq);

    public static WorkflowRunListItemDto ToListItem(WorkflowRunRow row, IReadOnlyList<WorkflowNodeRunRow> latestNodes)
    {
        var completed = latestNodes.Count(static n => n.Status == "done");
        return new WorkflowRunListItemDto(
            RunId: row.Id,
            WorkspaceId: row.WorkspaceId,
            SessionId: row.SessionId,
            WorkflowId: row.WorkflowId,
            Title: row.Title,
            Status: row.Status,
            CompletedNodes: completed,
            TotalNodes: latestNodes.Count,
            EnvironmentId: row.EnvironmentId,
            Error: row.Error,
            TotalTokens: row.TotalTokens,
            CreatedAt: row.CreatedAt,
            StartedAt: row.StartedAt,
            FinishedAt: row.FinishedAt);
    }

    public static WorkflowRunEventDto ToEvent(WorkflowEngineEvent evt)
    {
        var eventType = evt.Kind switch
        {
            "node_updated" => WorkflowEvents.NodeUpdated,
            "edge_updated" => WorkflowEvents.EdgeUpdated,
            _ => WorkflowEvents.RunUpdated,
        };
        return new WorkflowRunEventDto(
            RunId: evt.RunId,
            Kind: eventType,
            Status: WorkflowEngine.FormatRunStatus(evt.Snapshot.Status),
            Node: evt.Node is { } n ? ToDto(n) : null,
            Edge: evt.Edge is { } e ? ToDto(e) : null,
            OutputText: evt.Snapshot.OutputText,
            OutputJson: evt.Snapshot.OutputJson,
            Error: evt.Snapshot.Error,
            TotalTokens: evt.Snapshot.TotalTokens,
            FinishedAt: evt.Snapshot.FinishedAt);
    }
}
