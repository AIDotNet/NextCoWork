using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Ncw.Core.Domain;

/// <summary>工作流节点类型(docs/design/12-workflows.md §4.1)。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkflowNodeKind>))]
public enum WorkflowNodeKind
{
    Trigger,
    Agent,
    Tool,
    Condition,
    Approval,
    Join,
    Plugin,
    Output,
}

/// <summary>画布默认排布方向。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkflowLayoutDirection>))]
public enum WorkflowLayoutDirection
{
    TopToBottom,
    LeftToRight,
}

/// <summary>Join 节点的汇聚策略。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkflowJoinStrategy>))]
public enum WorkflowJoinStrategy
{
    All,
    Any,
    SettleAll,
}

/// <summary>节点失败策略。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkflowOnError>))]
public enum WorkflowOnError
{
    FailFast,
    Continue,
}

/// <summary>工作流整体运行状态(docs/design/12-workflows.md §4.2)。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkflowRunStatus>))]
public enum WorkflowRunStatus
{
    Unknown,
    Queued,
    Running,
    WaitingApproval,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>单个节点的运行状态。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkflowNodeRunStatus>))]
public enum WorkflowNodeRunStatus
{
    Unknown,
    Pending,
    Running,
    WaitingApproval,
    Done,
    Failed,
    Skipped,
    Cancelled,
}

/// <summary>单条边的运行状态。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkflowEdgeRunStatus>))]
public enum WorkflowEdgeRunStatus
{
    Idle,
    Active,
    Skipped,
}

/// <summary>工作流模板作用域。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkflowScope>))]
public enum WorkflowScope
{
    Project,
    Global,
    Ephemeral,
}

/// <summary>画布坐标(null 表示由画布自动排版)。</summary>
public sealed record WorkflowPosition(double X, double Y);

/// <summary>命名端口定义。</summary>
public sealed record WorkflowPortDef(
    string Id,
    string? Label = null,
    string? DataType = null,
    int? MaxConnections = null);

/// <summary>工作流入参声明。</summary>
public sealed record WorkflowInputSpec(
    string Name,
    string Type = "string",
    bool Required = false,
    string? Default = null,
    string? Description = null);

/// <summary>节点配置(按 <see cref="WorkflowNodeKind"/> 使用对应字段,JSON 源生成兼容 NativeAOT)。</summary>
public sealed record WorkflowNodeConfig(
    string? Prompt = null,
    string? Subagent = null,
    string? Model = null,
    string? PermissionMode = null,
    int? MaxTurns = null,
    string? ToolName = null,
    string? ToolArgsJson = null,
    int? TimeoutSec = null,
    string? Expression = null,
    string? ClassifierPrompt = null,
    string? SummaryTemplate = null,
    WorkflowJoinStrategy? Strategy = null,
    string? PluginId = null,
    string? PluginHandler = null,
    string? OutputTemplate = null,
    WorkflowOnError OnError = WorkflowOnError.FailFast,
    int MaxRetries = 0);

/// <summary>DAG 中的一个节点定义。</summary>
public sealed record WorkflowNodeDef(
    string Id,
    WorkflowNodeKind Kind,
    string Title,
    string? Description = null,
    WorkflowPosition? Position = null,
    IReadOnlyList<WorkflowPortDef>? Inputs = null,
    IReadOnlyList<WorkflowPortDef>? Outputs = null,
    WorkflowNodeConfig? Config = null);

/// <summary>DAG 中的一条有向边。</summary>
public sealed record WorkflowEdgeDef(
    string Id,
    string Source,
    string Target,
    string? SourcePort = null,
    string? TargetPort = null);

/// <summary>完整工作流定义(JSON 落盘与协议共享)。</summary>
public sealed record WorkflowDefinition(
    string Id,
    string Name,
    IReadOnlyList<WorkflowNodeDef> Nodes,
    IReadOnlyList<WorkflowEdgeDef> Edges,
    string? Description = null,
    WorkflowLayoutDirection LayoutDirection = WorkflowLayoutDirection.TopToBottom,
    IReadOnlyList<WorkflowInputSpec>? Inputs = null,
    int SchemaVersion = 1);

/// <summary>模板加载或静态校验的诊断条目(稳定 key,控制端翻译)。</summary>
public sealed record WorkflowDiagnostic(
    string Code,
    string Message,
    string? Path = null,
    string? NodeId = null,
    string? EdgeId = null);

/// <summary>节点运行记录(单调合并与界面派生共用)。</summary>
public sealed record WorkflowNodeRunRecord(
    string NodeId,
    WorkflowNodeKind Kind,
    WorkflowNodeRunStatus Status,
    int Attempt = 1,
    string? ChildRunId = null,
    string? ChildSessionId = null,
    string? Summary = null,
    string? OutputText = null,
    string? OutputJson = null,
    string? Error = null,
    long Tokens = 0,
    long? StartedAt = null,
    long? FinishedAt = null);

/// <summary>边运行记录。</summary>
public sealed record WorkflowEdgeRunRecord(string EdgeId, WorkflowEdgeRunStatus Status);

/// <summary>工作流运行记录(权威快照与 Store 镜像共用)。</summary>
public sealed record WorkflowRunRecord(
    string RunId,
    string WorkspaceId,
    string Title,
    WorkflowRunStatus Status,
    WorkflowDefinition Definition,
    ImmutableArray<WorkflowNodeRunRecord> Nodes,
    ImmutableArray<WorkflowEdgeRunRecord> Edges,
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

/// <summary>工作流进度的派生视图(纯函数结果,不带本地化文案)。</summary>
public sealed record WorkflowProgress(
    int Completed,
    int Failed,
    int Running,
    int WaitingApproval,
    int Skipped,
    int Total);

/// <summary>
/// 工作流静态校验(docs/design/12-workflows.md §4.3)。Agent 与客户端共用同一份规则:
/// 判环(Kahn 拓扑排序)、ID 规范、边与端口合法性、DataType 匹配、节点 / 边上限。
/// </summary>
public static partial class WorkflowValidator
{
    public const int MaxNodes = 64;
    public const int MaxEdges = 256;
    public const int MaxDefinitionBytes = 512 * 1024;
    public const int MaxNestingDepth = 2;

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidIdRegex();

    public static bool IsValidId(string? id) => !string.IsNullOrEmpty(id) && ValidIdRegex().IsMatch(id);

    public static IReadOnlyList<WorkflowDiagnostic> Validate(WorkflowDefinition definition)
    {
        var errors = new List<WorkflowDiagnostic>();

        if (!IsValidId(definition.Id))
        {
            errors.Add(new WorkflowDiagnostic("workflows.invalid.id", $"Invalid workflow id '{definition.Id}'."));
        }

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            errors.Add(new WorkflowDiagnostic("workflows.invalid.name", "Workflow name is required."));
        }

        if (definition.Nodes.Count == 0)
        {
            errors.Add(new WorkflowDiagnostic("workflows.invalid.empty", "Workflow must contain at least one node."));
            return errors;
        }

        if (definition.Nodes.Count > MaxNodes)
        {
            errors.Add(new WorkflowDiagnostic("workflows.invalid.tooManyNodes", $"Workflow exceeds maximum of {MaxNodes} nodes."));
        }

        if (definition.Edges.Count > MaxEdges)
        {
            errors.Add(new WorkflowDiagnostic("workflows.invalid.tooManyEdges", $"Workflow exceeds maximum of {MaxEdges} edges."));
        }

        var nodesById = new Dictionary<string, WorkflowNodeDef>(StringComparer.Ordinal);
        var triggerCount = 0;
        foreach (var node in definition.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
            {
                errors.Add(new WorkflowDiagnostic("workflows.invalid.nodeId", "Node id is required."));
                continue;
            }

            if (!nodesById.TryAdd(node.Id, node))
            {
                errors.Add(new WorkflowDiagnostic("workflows.invalid.duplicateNodeId", $"Duplicate node id '{node.Id}'.", NodeId: node.Id));
                continue;
            }

            if (node.Kind == WorkflowNodeKind.Trigger)
            {
                triggerCount++;
            }
        }

        if (triggerCount > 1)
        {
            errors.Add(new WorkflowDiagnostic("workflows.invalid.multipleTriggers", "Workflow may contain at most one Trigger node."));
        }

        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        var inDegree = nodesById.Keys.ToDictionary(static id => id, static _ => 0, StringComparer.Ordinal);
        var adjacency = nodesById.Keys.ToDictionary(static id => id, static _ => new List<string>(), StringComparer.Ordinal);
        var sourcePortCounts = new Dictionary<(string NodeId, string PortId), int>();
        var targetPortCounts = new Dictionary<(string NodeId, string PortId), int>();

        foreach (var edge in definition.Edges)
        {
            if (string.IsNullOrWhiteSpace(edge.Id))
            {
                errors.Add(new WorkflowDiagnostic("workflows.invalid.edgeId", "Edge id is required."));
            }
            else if (!edgeIds.Add(edge.Id))
            {
                errors.Add(new WorkflowDiagnostic("workflows.invalid.duplicateEdgeId", $"Duplicate edge id '{edge.Id}'.", EdgeId: edge.Id));
            }

            if (string.Equals(edge.Source, edge.Target, StringComparison.Ordinal))
            {
                errors.Add(new WorkflowDiagnostic("workflows.invalid.selfLoop", $"Edge '{edge.Id}' connects node '{edge.Source}' to itself.", NodeId: edge.Source, EdgeId: edge.Id));
                continue;
            }

            if (!nodesById.TryGetValue(edge.Source, out var sourceNode))
            {
                errors.Add(new WorkflowDiagnostic("workflows.invalid.missingSource", $"Edge '{edge.Id}' references missing source '{edge.Source}'.", EdgeId: edge.Id));
                continue;
            }

            if (!nodesById.TryGetValue(edge.Target, out var targetNode))
            {
                errors.Add(new WorkflowDiagnostic("workflows.invalid.missingTarget", $"Edge '{edge.Id}' references missing target '{edge.Target}'.", EdgeId: edge.Id));
                continue;
            }

            if (targetNode.Kind == WorkflowNodeKind.Trigger)
            {
                errors.Add(new WorkflowDiagnostic("workflows.invalid.triggerInput", $"Trigger node '{targetNode.Id}' cannot have incoming edges.", NodeId: targetNode.Id, EdgeId: edge.Id));
            }

            WorkflowPortDef? sourcePort = null;
            if (edge.SourcePort is { Length: > 0 } sp)
            {
                sourcePort = sourceNode.Outputs?.FirstOrDefault(p => string.Equals(p.Id, sp, StringComparison.Ordinal));
                if (sourcePort is null)
                {
                    errors.Add(new WorkflowDiagnostic("workflows.invalid.missingPort", $"Source port '{sp}' does not exist on node '{sourceNode.Id}'.", NodeId: sourceNode.Id, EdgeId: edge.Id));
                }
                else
                {
                    var key = (sourceNode.Id, sp);
                    sourcePortCounts[key] = sourcePortCounts.GetValueOrDefault(key) + 1;
                    if (sourcePort.MaxConnections is > 0 && sourcePortCounts[key] > sourcePort.MaxConnections.Value)
                    {
                        errors.Add(new WorkflowDiagnostic("workflows.invalid.portMaxConnections", $"Source port '{sp}' on node '{sourceNode.Id}' exceeds max connections.", NodeId: sourceNode.Id, EdgeId: edge.Id));
                    }
                }
            }

            WorkflowPortDef? targetPort = null;
            if (edge.TargetPort is { Length: > 0 } tp)
            {
                targetPort = targetNode.Inputs?.FirstOrDefault(p => string.Equals(p.Id, tp, StringComparison.Ordinal));
                if (targetPort is null)
                {
                    errors.Add(new WorkflowDiagnostic("workflows.invalid.missingPort", $"Target port '{tp}' does not exist on node '{targetNode.Id}'.", NodeId: targetNode.Id, EdgeId: edge.Id));
                }
                else
                {
                    var key = (targetNode.Id, tp);
                    targetPortCounts[key] = targetPortCounts.GetValueOrDefault(key) + 1;
                    if (targetPort.MaxConnections is > 0 && targetPortCounts[key] > targetPort.MaxConnections.Value)
                    {
                        errors.Add(new WorkflowDiagnostic("workflows.invalid.portMaxConnections", $"Target port '{tp}' on node '{targetNode.Id}' exceeds max connections.", NodeId: targetNode.Id, EdgeId: edge.Id));
                    }
                }
            }

            if (sourcePort?.DataType is { Length: > 0 } sType
                && targetPort?.DataType is { Length: > 0 } tType
                && !string.Equals(sType, tType, StringComparison.Ordinal))
            {
                errors.Add(new WorkflowDiagnostic("workflows.invalid.portTypeMismatch", $"Port data type mismatch: '{sType}' -> '{tType}'.", EdgeId: edge.Id));
            }

            adjacency[edge.Source].Add(edge.Target);
            inDegree[edge.Target]++;
        }

        // Kahn 拓扑排序判环
        var queue = new Queue<string>();
        foreach (var (id, deg) in inDegree)
        {
            if (deg == 0)
            {
                queue.Enqueue(id);
            }
        }

        var visited = 0;
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            visited++;
            foreach (var next in adjacency[current])
            {
                if (--inDegree[next] == 0)
                {
                    queue.Enqueue(next);
                }
            }
        }

        if (visited < nodesById.Count)
        {
            errors.Add(new WorkflowDiagnostic("workflows.invalid.cycle", "Workflow graph contains a cycle; only DAGs are allowed."));
        }

        return errors;
    }

    /// <summary>返回确定性的拓扑序(按层、同层按定义顺序)。有环时返回空。</summary>
    public static IReadOnlyList<WorkflowNodeDef> TopologicalOrder(WorkflowDefinition definition)
    {
        var nodesById = new Dictionary<string, WorkflowNodeDef>(StringComparer.Ordinal);
        var orderIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < definition.Nodes.Count; i++)
        {
            var n = definition.Nodes[i];
            if (nodesById.TryAdd(n.Id, n))
            {
                orderIndex[n.Id] = i;
            }
        }

        var inDegree = nodesById.Keys.ToDictionary(static id => id, static _ => 0, StringComparer.Ordinal);
        var adjacency = nodesById.Keys.ToDictionary(static id => id, static _ => new List<string>(), StringComparer.Ordinal);
        foreach (var e in definition.Edges)
        {
            if (nodesById.ContainsKey(e.Source) && nodesById.ContainsKey(e.Target) && !string.Equals(e.Source, e.Target, StringComparison.Ordinal))
            {
                adjacency[e.Source].Add(e.Target);
                inDegree[e.Target]++;
            }
        }

        var ready = new PriorityQueue<string, int>();
        foreach (var (id, deg) in inDegree)
        {
            if (deg == 0)
            {
                ready.Enqueue(id, orderIndex[id]);
            }
        }

        var result = new List<WorkflowNodeDef>(nodesById.Count);
        while (ready.Count > 0)
        {
            var id = ready.Dequeue();
            result.Add(nodesById[id]);
            foreach (var next in adjacency[id])
            {
                if (--inDegree[next] == 0)
                {
                    ready.Enqueue(next, orderIndex[next]);
                }
            }
        }

        return result.Count == nodesById.Count ? result : [];
    }
}

/// <summary>
/// 工作流状态的派生与单调合并纯函数(AGENTS.md §3 规则 3、6、7)。Agent 落库与客户端 Reducer 共用。
/// </summary>
public static class WorkflowView
{
    public static bool IsTerminal(WorkflowRunStatus status) =>
        status is WorkflowRunStatus.Completed or WorkflowRunStatus.Failed or WorkflowRunStatus.Cancelled;

    public static bool IsTerminal(WorkflowNodeRunStatus status) =>
        status is WorkflowNodeRunStatus.Done or WorkflowNodeRunStatus.Failed or WorkflowNodeRunStatus.Skipped or WorkflowNodeRunStatus.Cancelled;

    public static bool IsActive(WorkflowRunStatus status) =>
        status is WorkflowRunStatus.Queued or WorkflowRunStatus.Running or WorkflowRunStatus.WaitingApproval;

    private static int RunRank(WorkflowRunStatus status) => status switch
    {
        WorkflowRunStatus.Unknown => 0,
        WorkflowRunStatus.Queued => 1,
        WorkflowRunStatus.Running or WorkflowRunStatus.WaitingApproval => 2,
        WorkflowRunStatus.Completed or WorkflowRunStatus.Failed or WorkflowRunStatus.Cancelled => 3,
        _ => 0,
    };

    private static int NodeRank(WorkflowNodeRunStatus status) => status switch
    {
        WorkflowNodeRunStatus.Unknown => 0,
        WorkflowNodeRunStatus.Pending => 1,
        WorkflowNodeRunStatus.Running or WorkflowNodeRunStatus.WaitingApproval => 2,
        WorkflowNodeRunStatus.Done or WorkflowNodeRunStatus.Failed or WorkflowNodeRunStatus.Skipped or WorkflowNodeRunStatus.Cancelled => 3,
        _ => 0,
    };

    /// <summary>单调合并节点状态:更高 Attempt 胜出;同 Attempt 内终态压过运行中,首个终态胜出。</summary>
    public static WorkflowNodeRunRecord MergeNode(WorkflowNodeRunRecord? existing, WorkflowNodeRunRecord incoming)
    {
        if (existing is null)
        {
            return incoming;
        }

        if (incoming.Attempt > existing.Attempt)
        {
            return incoming;
        }

        if (incoming.Attempt < existing.Attempt)
        {
            return existing;
        }

        var existingRank = NodeRank(existing.Status);
        var incomingRank = NodeRank(incoming.Status);

        if (existingRank == 3 && incomingRank < 3)
        {
            return existing;
        }

        if (existingRank == 3 && incomingRank == 3)
        {
            // 首个终态胜出,但补齐缺失的可选元数据
            return existing with
            {
                ChildRunId = existing.ChildRunId ?? incoming.ChildRunId,
                ChildSessionId = existing.ChildSessionId ?? incoming.ChildSessionId,
                Summary = existing.Summary ?? incoming.Summary,
                OutputText = existing.OutputText ?? incoming.OutputText,
                OutputJson = existing.OutputJson ?? incoming.OutputJson,
                Error = existing.Error ?? incoming.Error,
                Tokens = Math.Max(existing.Tokens, incoming.Tokens),
                StartedAt = existing.StartedAt ?? incoming.StartedAt,
                FinishedAt = existing.FinishedAt ?? incoming.FinishedAt,
            };
        }

        if (incomingRank < existingRank)
        {
            return existing;
        }

        return incoming with
        {
            ChildRunId = incoming.ChildRunId ?? existing.ChildRunId,
            ChildSessionId = incoming.ChildSessionId ?? existing.ChildSessionId,
            StartedAt = incoming.StartedAt ?? existing.StartedAt,
        };
    }

    /// <summary>单调合并边状态:Active / Skipped 压过 Idle。</summary>
    public static WorkflowEdgeRunRecord MergeEdge(WorkflowEdgeRunRecord? existing, WorkflowEdgeRunRecord incoming)
    {
        if (existing is null || existing.Status == WorkflowEdgeRunStatus.Idle)
        {
            return incoming;
        }

        return incoming.Status == WorkflowEdgeRunStatus.Idle ? existing : incoming;
    }

    /// <summary>单调合并整个工作流运行记录(允许 Resume 将终态重新推进到 Running 仅当新快照 Seq 更大且存在更高 Attempt 节点)。</summary>
    public static WorkflowRunRecord MergeRun(WorkflowRunRecord? existing, WorkflowRunRecord incoming)
    {
        if (existing is null)
        {
            return incoming;
        }

        var existingNodes = existing.Nodes.ToDictionary(static n => n.NodeId, StringComparer.Ordinal);
        var hasNewAttempt = incoming.Nodes.Any(n => existingNodes.TryGetValue(n.NodeId, out var prev) && n.Attempt > prev.Attempt);

        var mergedNodes = ImmutableArray.CreateBuilder<WorkflowNodeRunRecord>(Math.Max(existing.Nodes.Length, incoming.Nodes.Length));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var inc in incoming.Nodes)
        {
            seen.Add(inc.NodeId);
            existingNodes.TryGetValue(inc.NodeId, out var prev);
            mergedNodes.Add(MergeNode(prev, inc));
        }

        foreach (var prev in existing.Nodes)
        {
            if (seen.Add(prev.NodeId))
            {
                mergedNodes.Add(prev);
            }
        }

        var existingEdges = existing.Edges.ToDictionary(static e => e.EdgeId, StringComparer.Ordinal);
        var mergedEdges = ImmutableArray.CreateBuilder<WorkflowEdgeRunRecord>(Math.Max(existing.Edges.Length, incoming.Edges.Length));
        var seenEdges = new HashSet<string>(StringComparer.Ordinal);
        foreach (var inc in incoming.Edges)
        {
            seenEdges.Add(inc.EdgeId);
            existingEdges.TryGetValue(inc.EdgeId, out var prev);
            mergedEdges.Add(hasNewAttempt ? inc : MergeEdge(prev, inc));
        }

        foreach (var prev in existing.Edges)
        {
            if (seenEdges.Add(prev.EdgeId))
            {
                mergedEdges.Add(prev);
            }
        }

        var status = incoming.Status;
        if (!hasNewAttempt)
        {
            var eRank = RunRank(existing.Status);
            var iRank = RunRank(incoming.Status);
            if (eRank == 3 && iRank < 3)
            {
                status = existing.Status;
            }
            else if (eRank == 3 && iRank == 3)
            {
                status = existing.Status;
            }
            else if (iRank < eRank)
            {
                status = existing.Status;
            }
        }

        return incoming with
        {
            Status = status,
            Nodes = mergedNodes.ToImmutable(),
            Edges = mergedEdges.ToImmutable(),
            TotalTokens = Math.Max(existing.TotalTokens, incoming.TotalTokens),
            StartedAt = existing.StartedAt ?? incoming.StartedAt,
            FinishedAt = IsTerminal(status) ? (existing.FinishedAt ?? incoming.FinishedAt) : null,
            Seq = Math.Max(existing.Seq, incoming.Seq),
        };
    }

    /// <summary>派生设备可见状态(规则 7:设备断开时非终态一律标 Unknown,绝不判结束)。</summary>
    public static WorkflowRunStatus DeriveStatus(WorkflowRunStatus status, DeviceConnection connection) =>
        connection != DeviceConnection.Connected && !IsTerminal(status) ? WorkflowRunStatus.Unknown : status;

    public static WorkflowProgress ProgressOf(WorkflowRunRecord run)
    {
        int completed = 0, failed = 0, running = 0, waiting = 0, skipped = 0;
        foreach (var node in run.Nodes)
        {
            switch (node.Status)
            {
                case WorkflowNodeRunStatus.Done: completed++; break;
                case WorkflowNodeRunStatus.Failed: failed++; break;
                case WorkflowNodeRunStatus.Running: running++; break;
                case WorkflowNodeRunStatus.WaitingApproval: waiting++; break;
                case WorkflowNodeRunStatus.Skipped:
                case WorkflowNodeRunStatus.Cancelled: skipped++; break;
            }
        }

        return new WorkflowProgress(completed, failed, running, waiting, skipped, run.Nodes.Length);
    }

    public static bool CanCancel(WorkflowRunStatus status, DeviceConnection connection) =>
        connection == DeviceConnection.Connected && IsActive(status);

    public static bool CanResume(WorkflowRunStatus status, DeviceConnection connection) =>
        connection == DeviceConnection.Connected && status is WorkflowRunStatus.Failed or WorkflowRunStatus.Cancelled;

    public static bool CanRetryNode(WorkflowRunStatus runStatus, WorkflowNodeRunStatus nodeStatus, DeviceConnection connection) =>
        connection == DeviceConnection.Connected && IsTerminal(runStatus) && nodeStatus is WorkflowNodeRunStatus.Failed or WorkflowNodeRunStatus.Cancelled;
}

/// <summary>NativeAOT 零反射的 Workflow JSON 序列化上下文。</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(WorkflowDefinition))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class WorkflowJsonContext : JsonSerializerContext;
