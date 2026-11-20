using System.Collections.Immutable;
using System.Text.Json;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Stores;

/// <summary>工作流运行的 key(必须带 deviceId,规则 1)。</summary>
public readonly record struct WorkflowRunKey(string DeviceId, string RunId);

/// <summary>控制端持有的对话工作流运行镜像(真源在 Agent)。</summary>
public sealed record WorkflowMirror(
    ImmutableDictionary<WorkflowRunKey, WorkflowRunRecord> Runs)
{
    public static readonly WorkflowMirror Empty = new(
        ImmutableDictionary<WorkflowRunKey, WorkflowRunRecord>.Empty);

    /// <summary>返回某个会话最新的工作流运行(优先返回正在运行的,其次按创建时间最新)。</summary>
    public WorkflowRunRecord? LatestForSession(string deviceId, string sessionId) =>
        Runs.Where(kv => string.Equals(kv.Key.DeviceId, deviceId, StringComparison.Ordinal)
                         && string.Equals(kv.Value.SessionId, sessionId, StringComparison.Ordinal))
            .Select(static kv => kv.Value)
            .OrderByDescending(static r => WorkflowView.IsActive(r.Status) ? 1 : 0)
            .ThenByDescending(static r => r.CreatedAt)
            .FirstOrDefault();
}

/// <summary>工作流 Store 的纯归约器(调用 <see cref="WorkflowView"/> 单调合并,规则 6)。</summary>
public static class WorkflowReducer
{
    public static WorkflowMirror ApplySnapshot(WorkflowMirror state, string deviceId, WorkflowRunSnapshotDto dto)
    {
        var record = FromSnapshot(dto);
        if (record is null)
        {
            return state;
        }

        var key = new WorkflowRunKey(deviceId, dto.RunId);
        var existing = state.Runs.GetValueOrDefault(key);
        var merged = WorkflowView.MergeRun(existing, record);
        return state with { Runs = state.Runs.SetItem(key, merged) };
    }

    public static WorkflowMirror ApplyEvent(WorkflowMirror state, string deviceId, WorkflowRunEventDto evt)
    {
        var key = new WorkflowRunKey(deviceId, evt.RunId);
        if (!state.Runs.TryGetValue(key, out var existing))
        {
            return state;
        }

        var nodes = existing.Nodes;
        if (evt.Node is { } n)
        {
            var incomingNode = FromNodeDto(n);
            var idx = -1;
            for (var i = 0; i < nodes.Length; i++)
            {
                if (string.Equals(nodes[i].NodeId, incomingNode.NodeId, StringComparison.Ordinal))
                {
                    idx = i;
                    break;
                }
            }

            nodes = idx >= 0
                ? nodes.SetItem(idx, WorkflowView.MergeNode(nodes[idx], incomingNode))
                : nodes.Add(incomingNode);
        }

        var edges = existing.Edges;
        if (evt.Edge is { } e)
        {
            var incomingEdge = FromEdgeDto(e);
            var idx = -1;
            for (var i = 0; i < edges.Length; i++)
            {
                if (string.Equals(edges[i].EdgeId, incomingEdge.EdgeId, StringComparison.Ordinal))
                {
                    idx = i;
                    break;
                }
            }

            edges = idx >= 0
                ? edges.SetItem(idx, WorkflowView.MergeEdge(edges[idx], incomingEdge))
                : edges.Add(incomingEdge);
        }

        var updated = existing with
        {
            Status = evt.Status is not null ? ParseRunStatus(evt.Status) : existing.Status,
            Nodes = nodes,
            Edges = edges,
            OutputText = evt.OutputText ?? existing.OutputText,
            OutputJson = evt.OutputJson ?? existing.OutputJson,
            Error = evt.Error ?? existing.Error,
            TotalTokens = Math.Max(existing.TotalTokens, evt.TotalTokens),
            FinishedAt = evt.FinishedAt ?? existing.FinishedAt,
            Seq = existing.Seq + 1,
        };

        var merged = WorkflowView.MergeRun(existing, updated);
        return state with { Runs = state.Runs.SetItem(key, merged) };
    }

    public static WorkflowRunRecord? FromSnapshot(WorkflowRunSnapshotDto dto)
    {
        WorkflowDefinition? def;
        try
        {
            def = JsonSerializer.Deserialize(dto.DefinitionJson, WorkflowJsonContext.Default.WorkflowDefinition);
        }
        catch (JsonException)
        {
            return null;
        }

        if (def is null)
        {
            return null;
        }

        return new WorkflowRunRecord(
            RunId: dto.RunId,
            WorkspaceId: dto.WorkspaceId,
            Title: dto.Title,
            Status: ParseRunStatus(dto.Status),
            Definition: def,
            Nodes: [.. dto.Nodes.Select(FromNodeDto)],
            Edges: [.. dto.Edges.Select(FromEdgeDto)],
            SessionId: dto.SessionId,
            WorkflowId: dto.WorkflowId,
            EnvironmentId: dto.EnvironmentId,
            OutputText: dto.OutputText,
            OutputJson: dto.OutputJson,
            Error: dto.Error,
            TotalTokens: dto.TotalTokens,
            CreatedAt: dto.CreatedAt,
            StartedAt: dto.StartedAt,
            FinishedAt: dto.FinishedAt,
            Seq: dto.Seq);
    }

    private static WorkflowNodeRunRecord FromNodeDto(WorkflowNodeRunDto n) => new(
        NodeId: n.NodeId,
        Kind: Enum.TryParse<WorkflowNodeKind>(n.Kind, ignoreCase: true, out var kind) ? kind : WorkflowNodeKind.Agent,
        Status: ParseNodeStatus(n.Status),
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

    private static WorkflowEdgeRunRecord FromEdgeDto(WorkflowEdgeRunDto e) => new(
        EdgeId: e.EdgeId,
        Status: Enum.TryParse<WorkflowEdgeRunStatus>(e.Status, ignoreCase: true, out var st) ? st : WorkflowEdgeRunStatus.Idle);

    private static WorkflowRunStatus ParseRunStatus(string? s) => s switch
    {
        "queued" => WorkflowRunStatus.Queued,
        "running" => WorkflowRunStatus.Running,
        "waiting_approval" => WorkflowRunStatus.WaitingApproval,
        "completed" => WorkflowRunStatus.Completed,
        "failed" => WorkflowRunStatus.Failed,
        "cancelled" => WorkflowRunStatus.Cancelled,
        _ => WorkflowRunStatus.Unknown,
    };

    private static WorkflowNodeRunStatus ParseNodeStatus(string? s) => s switch
    {
        "pending" => WorkflowNodeRunStatus.Pending,
        "running" => WorkflowNodeRunStatus.Running,
        "waiting_approval" => WorkflowNodeRunStatus.WaitingApproval,
        "done" => WorkflowNodeRunStatus.Done,
        "failed" => WorkflowNodeRunStatus.Failed,
        "skipped" => WorkflowNodeRunStatus.Skipped,
        "cancelled" => WorkflowNodeRunStatus.Cancelled,
        _ => WorkflowNodeRunStatus.Unknown,
    };
}
