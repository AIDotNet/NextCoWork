using PolyType;

namespace Ncw.Protocol.Methods;

public static partial class Capabilities
{
    /// <summary>对话内 Agent 动态 DAG 工作流编排与监控(docs/design/12-workflows.md)。</summary>
    public const string Workflows = "workflows";
}

public sealed record WorkflowRunRefParams(string RunId);

public sealed record WorkflowRetryNodeParams(string RunId, string NodeId);

public sealed record WorkflowRunsListParams(string? WorkspaceId = null, string? SessionId = null, int Limit = 50);

/// <summary>运行记录摘要。</summary>
public sealed record WorkflowRunListItemDto(
    string RunId,
    string WorkspaceId,
    string? SessionId,
    string? WorkflowId,
    string Title,
    string Status,
    int CompletedNodes,
    int TotalNodes,
    string? EnvironmentId,
    string? Error,
    long TotalTokens,
    long CreatedAt,
    long? StartedAt,
    long? FinishedAt);

public sealed record WorkflowRunsListResult(WorkflowRunListItemDto[] Runs);

/// <summary>单个节点的实时运行状态。</summary>
public sealed record WorkflowNodeRunDto(
    string NodeId,
    string Kind,
    string Status,
    int Attempt,
    string? ChildRunId = null,
    string? ChildSessionId = null,
    string? Summary = null,
    string? OutputText = null,
    string? OutputJson = null,
    string? Error = null,
    long Tokens = 0,
    long? StartedAt = null,
    long? FinishedAt = null);

/// <summary>单条边的运行状态(<c>idle</c> / <c>active</c> / <c>skipped</c>)。</summary>
public sealed record WorkflowEdgeRunDto(string EdgeId, string Status);

/// <summary>
/// <c>workflow/&lt;runId&gt;</c> 主题的首帧快照与 <c>workflows.runs.get</c> 的返回值(首帧与查询同一个类型,规则 5)。
/// </summary>
public sealed record WorkflowRunSnapshotDto(
    string RunId,
    string WorkspaceId,
    string Title,
    string Status,
    string DefinitionJson,
    WorkflowNodeRunDto[] Nodes,
    WorkflowEdgeRunDto[] Edges,
    string? SessionId = null,
    string? WorkflowId = null,
    string? EnvironmentId = null,
    string? OutputText = null,
    string? OutputJson = null,
    string? Error = null,
    long TotalTokens = 0,
    long CreatedAt = 0,
    long? StartedAt = null,
    long? FinishedAt = null,
    long Seq = 0);

/// <summary>工作流增量事件(<c>workflow/&lt;runId&gt;</c> 主题)。</summary>
public sealed record WorkflowRunEventDto(
    string RunId,
    string Kind,
    string? Status = null,
    WorkflowNodeRunDto? Node = null,
    WorkflowEdgeRunDto? Edge = null,
    string? OutputText = null,
    string? OutputJson = null,
    string? Error = null,
    long TotalTokens = 0,
    long? FinishedAt = null);

public static class WorkflowEvents
{
    public const string NodeUpdated = "workflow.node_updated";
    public const string EdgeUpdated = "workflow.edge_updated";
    public const string RunUpdated = "workflow.run_updated";
}

/// <summary>对话内 Agent 工作流的控制与查询方法表(docs/design/12-workflows.md)。</summary>
public static class WorkflowMethods
{
    public const string RunTopicPrefix = "workflow/";

    public static string RunTopic(string runId) => RunTopicPrefix + runId;

    public static readonly MethodDef<WorkflowRunRefParams, Unit> Cancel =
        MethodTable.Command<WorkflowRunRefParams, Unit>("workflows.cancel", Role.Member, Capabilities.Workflows);

    public static readonly MethodDef<WorkflowRunRefParams, WorkflowRunSnapshotDto> Resume =
        MethodTable.Command<WorkflowRunRefParams, WorkflowRunSnapshotDto>("workflows.resume", Role.Member, Capabilities.Workflows);

    public static readonly MethodDef<WorkflowRetryNodeParams, WorkflowRunSnapshotDto> RetryNode =
        MethodTable.Command<WorkflowRetryNodeParams, WorkflowRunSnapshotDto>("workflows.retryNode", Role.Member, Capabilities.Workflows);

    public static readonly MethodDef<WorkflowRunsListParams, WorkflowRunsListResult> ListRuns =
        MethodTable.Query<WorkflowRunsListParams, WorkflowRunsListResult>("workflows.runs.list", Role.Viewer, Capabilities.Workflows);

    public static readonly MethodDef<WorkflowRunRefParams, WorkflowRunSnapshotDto> GetRun =
        MethodTable.Query<WorkflowRunRefParams, WorkflowRunSnapshotDto>("workflows.runs.get", Role.Viewer, Capabilities.Workflows);

    public static readonly IReadOnlyList<MethodDef> All =
        [Cancel, Resume, RetryNode, ListRuns, GetRun];
}

[GenerateShapeFor<WorkflowRunRefParams>]
[GenerateShapeFor<WorkflowRetryNodeParams>]
[GenerateShapeFor<WorkflowRunsListParams>]
[GenerateShapeFor<WorkflowRunListItemDto>]
[GenerateShapeFor<WorkflowRunsListResult>]
[GenerateShapeFor<WorkflowNodeRunDto>]
[GenerateShapeFor<WorkflowEdgeRunDto>]
[GenerateShapeFor<WorkflowRunSnapshotDto>]
[GenerateShapeFor<WorkflowRunEventDto>]
public sealed partial class PayloadShapes;
