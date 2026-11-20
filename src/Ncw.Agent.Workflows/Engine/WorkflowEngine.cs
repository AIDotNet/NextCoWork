using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Ncw.Agent.Storage;
using Ncw.Core.Domain;

namespace Ncw.Agent.Workflows.Engine;

/// <summary>节点执行上下文(传给外部注入的执行器:Agent 子 Run、Tool、Approval、Plugin)。</summary>
public sealed record WorkflowNodeExecutionContext(
    string RunId,
    string WorkspaceId,
    string? SessionId,
    string? EnvironmentId,
    string PermissionMode,
    int NestingDepth,
    WorkflowNodeDef Node,
    int Attempt,
    IReadOnlyDictionary<string, string> Inputs,
    IReadOnlyDictionary<string, WorkflowNodeRunRecord> CompletedNodes,
    Action<WorkflowNodeRunRecord> ReportProgress,
    IReadOnlyList<string>? UpstreamNodeIds = null);

/// <summary>节点执行结果。</summary>
public sealed record WorkflowNodeExecutionResult(
    bool Succeeded,
    string? Summary = null,
    string? OutputText = null,
    string? OutputJson = null,
    string? Error = null,
    string? ChildRunId = null,
    string? ChildSessionId = null,
    long Tokens = 0,
    IReadOnlyList<string>? ActiveOutputPorts = null);

/// <summary>
/// 节点执行器接口:将 Agent 子 Run / ToolRuntime / InteractionGate / Jaunt 插件解耦出去,
/// 使 <see cref="WorkflowEngine"/> 可在无外部依赖下进行高覆盖率并发与状态机测试。
/// </summary>
public interface IWorkflowNodeExecutor
{
    Task<WorkflowNodeExecutionResult> ExecuteAsync(WorkflowNodeExecutionContext context, CancellationToken ct);
}

/// <summary>引擎对外发布的增量事件(Host 桥接到 <c>workflow/&lt;runId&gt;</c> 主题)。</summary>
public sealed record WorkflowEngineEvent(
    string RunId,
    long Seq,
    string Kind,
    WorkflowRunRecord Snapshot,
    WorkflowNodeRunRecord? Node = null,
    WorkflowEdgeRunRecord? Edge = null);

/// <summary>启动工作流的参数。</summary>
public sealed record WorkflowLaunchRequest(
    string RunId,
    string WorkspaceId,
    WorkflowDefinition Definition,
    IReadOnlyDictionary<string, string> Inputs,
    string? WorkflowId = null,
    string? SessionId = null,
    string? EnvironmentId = null,
    string PermissionMode = "ask",
    int NestingDepth = 0);

/// <summary>
/// DAG 工作流调度引擎(docs/design/12-workflows.md §5):
/// 拓扑并发调度、分支跳过传播、Join 汇聚、失败策略与重试、中止级联、SQLite 持久化与断点续跑。
/// </summary>
public sealed class WorkflowEngine(
    WorkflowRunRepository repository,
    IWorkflowNodeExecutor executor,
    TimeProvider? clock = null,
    int maxParallelNodes = 4)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly int _maxParallelNodes = Math.Max(1, maxParallelNodes);
    private readonly ConcurrentDictionary<string, ActiveWorkflow> _active = new(StringComparer.Ordinal);

    public event Action<WorkflowEngineEvent>? Updated;

    public bool HasActiveForSession(string sessionId) =>
        _active.Values.Any(a => string.Equals(a.SessionId, sessionId, StringComparison.Ordinal));

    public WorkflowRunRecord? GetSnapshot(string runId)
    {
        if (_active.TryGetValue(runId, out var active))
        {
            return active.Snapshot();
        }

        return LoadFromRepository(runId);
    }

    public IReadOnlyList<WorkflowRunRow> ListRuns(string? workspaceId = null, string? sessionId = null, int limit = 50) =>
        repository.ListRuns(workspaceId, sessionId, limit);

    /// <summary>启动一份新的工作流(异步后台执行,立即返回初始权威快照)。</summary>
    public WorkflowRunRecord Start(WorkflowLaunchRequest request)
    {
        if (request.NestingDepth > WorkflowValidator.MaxNestingDepth)
        {
            throw new InvalidOperationException($"workflows.error.depthExceeded: Nesting depth {request.NestingDepth} exceeds maximum {WorkflowValidator.MaxNestingDepth}.");
        }

        var errors = WorkflowValidator.Validate(request.Definition);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"{errors[0].Code}: {errors[0].Message}");
        }

        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var defJson = JsonSerializer.Serialize(request.Definition, WorkflowJsonContext.Default.WorkflowDefinition);
        var inputsDict = new Dictionary<string, string>(request.Inputs, StringComparer.Ordinal);
        if (request.Definition.Inputs is { Count: > 0 })
        {
            foreach (var spec in request.Definition.Inputs)
            {
                if (!inputsDict.ContainsKey(spec.Name) && spec.Default is not null)
                {
                    inputsDict[spec.Name] = spec.Default;
                }
            }
        }

        var inputsJson = JsonSerializer.Serialize(inputsDict, WorkflowJsonContext.Default.DictionaryStringString);
        var initialNodes = request.Definition.Nodes
            .Select(n => new WorkflowNodeRunRecord(n.Id, n.Kind, WorkflowNodeRunStatus.Pending, Attempt: 1))
            .ToImmutableArray();
        var initialEdges = request.Definition.Edges
            .Select(e => new WorkflowEdgeRunRecord(e.Id, WorkflowEdgeRunStatus.Idle))
            .ToImmutableArray();

        var initial = new WorkflowRunRecord(
            RunId: request.RunId,
            WorkspaceId: request.WorkspaceId,
            Title: request.Definition.Name,
            Status: WorkflowRunStatus.Running,
            Definition: request.Definition,
            Nodes: initialNodes,
            Edges: initialEdges,
            SessionId: request.SessionId,
            WorkflowId: request.WorkflowId,
            EnvironmentId: request.EnvironmentId,
            CreatedAt: now,
            StartedAt: now,
            Seq: 1);

        var active = new ActiveWorkflow(initial, inputsDict, defJson, inputsJson, request.PermissionMode, request.NestingDepth);
        if (!_active.TryAdd(request.RunId, active))
        {
            throw new InvalidOperationException($"workflows.error.duplicateRunId: Workflow run '{request.RunId}' is already active.");
        }

        PersistRun(active);
        foreach (var node in initialNodes)
        {
            PersistNode(request.RunId, node);
        }

        active.ExecutionTask = Task.Run(() => ExecuteGraphAsync(active, retryNodeIds: null));
        return initial;
    }

    /// <summary>等待某个工作流运行结束(测试与前台工具调用使用)。</summary>
    public async Task<WorkflowRunRecord> WaitForCompletionAsync(string runId, CancellationToken ct = default)
    {
        if (_active.TryGetValue(runId, out var active) && active.ExecutionTask is { } task)
        {
            await task.WaitAsync(ct).ConfigureAwait(false);
        }

        return GetSnapshot(runId) ?? throw new KeyNotFoundException($"Workflow run '{runId}' not found.");
    }

    /// <summary>中止运行中的工作流(级联取消所有在跑节点,幂等)。</summary>
    public bool Cancel(string runId)
    {
        if (!_active.TryGetValue(runId, out var active))
        {
            return false;
        }

        active.Cts.Cancel();
        return true;
    }

    /// <summary>断点续跑失败/中止的工作流:跳过已 Done 节点,递增其余节点 Attempt 重跑。</summary>
    public WorkflowRunRecord Resume(string runId)
    {
        if (_active.ContainsKey(runId))
        {
            throw new InvalidOperationException("workflows.error.alreadyRunning: Workflow is already running.");
        }

        var existing = LoadFromRepository(runId) ?? throw new KeyNotFoundException($"workflows.error.notFound: Workflow run '{runId}' not found.");
        if (existing.Status is not (WorkflowRunStatus.Failed or WorkflowRunStatus.Cancelled))
        {
            throw new InvalidOperationException($"workflows.error.cannotResume: Cannot resume workflow in status '{existing.Status}'.");
        }

        var row = repository.GetRun(runId)!;
        var inputs = JsonSerializer.Deserialize(row.InputsJson, WorkflowJsonContext.Default.DictionaryStringString) ?? [];
        var resetNodes = existing.Nodes.Select(n =>
            n.Status == WorkflowNodeRunStatus.Done
                ? n
                : new WorkflowNodeRunRecord(n.NodeId, n.Kind, WorkflowNodeRunStatus.Pending, Attempt: n.Attempt + 1))
            .ToImmutableArray();

        var resetEdges = existing.Definition.Edges.Select(e =>
        {
            var srcDone = resetNodes.Any(n => n.NodeId == e.Source && n.Status == WorkflowNodeRunStatus.Done);
            var dstDone = resetNodes.Any(n => n.NodeId == e.Target && n.Status == WorkflowNodeRunStatus.Done);
            return new WorkflowEdgeRunRecord(e.Id, srcDone && dstDone ? WorkflowEdgeRunStatus.Active : WorkflowEdgeRunStatus.Idle);
        }).ToImmutableArray();

        var resumed = existing with
        {
            Status = WorkflowRunStatus.Running,
            Nodes = resetNodes,
            Edges = resetEdges,
            Error = null,
            FinishedAt = null,
            Seq = existing.Seq + 1,
        };

        var active = new ActiveWorkflow(resumed, inputs, row.DefinitionJson, row.InputsJson, row.PermissionMode, nestingDepth: 0);
        if (!_active.TryAdd(runId, active))
        {
            throw new InvalidOperationException("workflows.error.alreadyRunning: Workflow is already running.");
        }

        PersistRun(active);
        foreach (var node in resetNodes.Where(static n => n.Status == WorkflowNodeRunStatus.Pending))
        {
            PersistNode(runId, node);
        }

        active.ExecutionTask = Task.Run(() => ExecuteGraphAsync(active, retryNodeIds: null));
        return resumed;
    }

    /// <summary>单节点重试:重置目标节点及其下游子图的 Attempt,保留其余已 Done 节点。</summary>
    public WorkflowRunRecord RetryNode(string runId, string nodeId)
    {
        if (_active.ContainsKey(runId))
        {
            throw new InvalidOperationException("workflows.error.alreadyRunning: Workflow is already running.");
        }

        var existing = LoadFromRepository(runId) ?? throw new KeyNotFoundException($"workflows.error.notFound: Workflow run '{runId}' not found.");
        if (!WorkflowView.IsTerminal(existing.Status))
        {
            throw new InvalidOperationException("workflows.error.notTerminal: Cannot retry node while workflow is running.");
        }

        if (!existing.Definition.Nodes.Any(n => string.Equals(n.Id, nodeId, StringComparison.Ordinal)))
        {
            throw new KeyNotFoundException($"workflows.error.nodeNotFound: Node '{nodeId}' not found.");
        }

        var downstream = CollectDownstream(existing.Definition, nodeId);
        var row = repository.GetRun(runId)!;
        var inputs = JsonSerializer.Deserialize(row.InputsJson, WorkflowJsonContext.Default.DictionaryStringString) ?? [];

        var resetNodes = existing.Nodes.Select(n =>
            downstream.Contains(n.NodeId)
                ? new WorkflowNodeRunRecord(n.NodeId, n.Kind, WorkflowNodeRunStatus.Pending, Attempt: n.Attempt + 1)
                : n)
            .ToImmutableArray();

        var resetEdges = existing.Definition.Edges.Select(e =>
        {
            if (downstream.Contains(e.Source) || downstream.Contains(e.Target))
            {
                return new WorkflowEdgeRunRecord(e.Id, WorkflowEdgeRunStatus.Idle);
            }

            var prev = existing.Edges.FirstOrDefault(x => x.EdgeId == e.Id);
            return prev ?? new WorkflowEdgeRunRecord(e.Id, WorkflowEdgeRunStatus.Idle);
        }).ToImmutableArray();

        var retried = existing with
        {
            Status = WorkflowRunStatus.Running,
            Nodes = resetNodes,
            Edges = resetEdges,
            Error = null,
            FinishedAt = null,
            Seq = existing.Seq + 1,
        };

        var active = new ActiveWorkflow(retried, inputs, row.DefinitionJson, row.InputsJson, row.PermissionMode, nestingDepth: 0);
        if (!_active.TryAdd(runId, active))
        {
            throw new InvalidOperationException("workflows.error.alreadyRunning: Workflow is already running.");
        }

        PersistRun(active);
        foreach (var node in resetNodes.Where(n => downstream.Contains(n.NodeId)))
        {
            PersistNode(runId, node);
        }

        active.ExecutionTask = Task.Run(() => ExecuteGraphAsync(active, retryNodeIds: downstream));
        return retried;
    }

    private async Task ExecuteGraphAsync(ActiveWorkflow active, HashSet<string>? retryNodeIds)
    {
        var def = active.Definition;
        var nodesById = def.Nodes.ToDictionary(static n => n.Id, StringComparer.Ordinal);
        var outgoing = def.Nodes.ToDictionary(static n => n.Id, static _ => new List<WorkflowEdgeDef>(), StringComparer.Ordinal);
        var incoming = def.Nodes.ToDictionary(static n => n.Id, static _ => new List<WorkflowEdgeDef>(), StringComparer.Ordinal);
        foreach (var e in def.Edges)
        {
            outgoing[e.Source].Add(e);
            incoming[e.Target].Add(e);
        }

        // 对于断点续跑/单节点重试,先把已 Done 节点通往待跑节点的出边标为 Active
        foreach (var node in active.GetAllNodes())
        {
            if (node.Status == WorkflowNodeRunStatus.Done)
            {
                foreach (var edge in outgoing[node.NodeId])
                {
                    if (active.GetEdgeStatus(edge.Id) == WorkflowEdgeRunStatus.Idle)
                    {
                        SetEdgeStatus(active, edge.Id, WorkflowEdgeRunStatus.Active);
                    }
                }
            }
        }

        using var sem = new SemaphoreSlim(_maxParallelNodes, _maxParallelNodes);
        var inFlight = new Dictionary<string, Task>(StringComparer.Ordinal);
        var failFastTriggered = false;
        string? firstError = null;

        try
        {
            while (true)
            {
                // 1. 推进所有可以立即 Skipped 或 Ready 的 Pending 节点
                var progressed = true;
                while (progressed)
                {
                    progressed = false;
                    foreach (var defNode in def.Nodes)
                    {
                        var state = active.GetNode(defNode.Id);
                        if (state.Status != WorkflowNodeRunStatus.Pending || inFlight.ContainsKey(defNode.Id))
                        {
                            continue;
                        }

                        var readiness = EvaluateReadiness(defNode, incoming[defNode.Id], active);
                        if (readiness == NodeReadiness.Skip)
                        {
                            var skipped = state with
                            {
                                Status = WorkflowNodeRunStatus.Skipped,
                                FinishedAt = _clock.GetUtcNow().ToUnixTimeMilliseconds(),
                            };
                            UpdateNode(active, skipped);
                            foreach (var edge in outgoing[defNode.Id])
                            {
                                SetEdgeStatus(active, edge.Id, WorkflowEdgeRunStatus.Skipped);
                            }

                            progressed = true;
                        }
                        else if (readiness == NodeReadiness.FailFromUpstream)
                        {
                            var failed = state with
                            {
                                Status = WorkflowNodeRunStatus.Failed,
                                Error = "workflows.error.upstreamFailed",
                                FinishedAt = _clock.GetUtcNow().ToUnixTimeMilliseconds(),
                            };
                            UpdateNode(active, failed);
                            foreach (var edge in outgoing[defNode.Id])
                            {
                                SetEdgeStatus(active, edge.Id, WorkflowEdgeRunStatus.Skipped);
                            }

                            firstError ??= failed.Error;
                            progressed = true;
                        }
                        else if (readiness == NodeReadiness.Ready && !active.Cts.IsCancellationRequested && !failFastTriggered)
                        {
                            var nodeToRun = defNode;
                            var runTask = RunNodeWithSemaphoreAsync(active, nodeToRun, outgoing[nodeToRun.Id], incoming[nodeToRun.Id], sem, () =>
                            {
                                failFastTriggered = true;
                                active.Cts.Cancel();
                            }, err => firstError ??= err);
                            inFlight[nodeToRun.Id] = runTask;
                            progressed = true;
                        }
                    }
                }

                if (inFlight.Count == 0)
                {
                    break;
                }

                var finished = await Task.WhenAny(inFlight.Values).ConfigureAwait(false);
                var finishedKeys = inFlight.Where(kv => kv.Value == finished).Select(kv => kv.Key).ToList();
                foreach (var k in finishedKeys)
                {
                    inFlight.Remove(k);
                }

                await finished.ConfigureAwait(false);
            }

            // 把因取消/FailFast 而未运行的 Pending 节点统一收口为 Cancelled
            var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
            foreach (var node in active.GetAllNodes())
            {
                if (node.Status is WorkflowNodeRunStatus.Pending or WorkflowNodeRunStatus.Running or WorkflowNodeRunStatus.WaitingApproval)
                {
                    UpdateNode(active, node with { Status = WorkflowNodeRunStatus.Cancelled, FinishedAt = now });
                }
            }

            var finalNodes = active.GetAllNodes();
            var anyFailed = finalNodes.Any(static n => n.Status == WorkflowNodeRunStatus.Failed);
            var cancelled = active.Cts.IsCancellationRequested && !failFastTriggered;
            var finalStatus = cancelled
                ? WorkflowRunStatus.Cancelled
                : anyFailed ? WorkflowRunStatus.Failed : WorkflowRunStatus.Completed;

            var outputNode = def.Nodes.LastOrDefault(static n => n.Kind == WorkflowNodeKind.Output);
            string? finalText = null;
            string? finalJson = null;
            if (outputNode is not null)
            {
                var outState = active.GetNode(outputNode.Id);
                if (outState.Status == WorkflowNodeRunStatus.Done)
                {
                    finalText = outState.OutputText;
                    finalJson = outState.OutputJson;
                }
            }

            if (finalText is null)
            {
                // 没有显式 Output 节点时,汇总所有已完成叶节点(无出边的终结步骤)的输出
                var sources = new HashSet<string>(def.Edges.Select(static e => e.Source), StringComparer.Ordinal);
                var leafResults = def.Nodes
                    .Where(n => !sources.Contains(n.Id))
                    .Select(n => (Def: n, Run: active.GetNode(n.Id)))
                    .Where(static x => x.Run.Status == WorkflowNodeRunStatus.Done && !string.IsNullOrEmpty(x.Run.OutputText))
                    .ToList();

                finalText = leafResults.Count switch
                {
                    1 => leafResults[0].Run.OutputText,
                    > 1 => string.Join("\n\n", leafResults.Select(static x => $"### {x.Def.Title} ({x.Def.Id})\n{x.Run.OutputText}")),
                    _ => finalNodes.LastOrDefault(static n => n.Status == WorkflowNodeRunStatus.Done && !string.IsNullOrEmpty(n.OutputText))?.OutputText,
                };
            }

            FinishRun(active, finalStatus, finalText, finalJson, finalStatus == WorkflowRunStatus.Failed ? (firstError ?? "workflows.error.nodeFailed") : null, now);
        }
        catch (Exception ex)
        {
            FinishRun(active, WorkflowRunStatus.Failed, null, null, ex.Message, _clock.GetUtcNow().ToUnixTimeMilliseconds());
        }
        finally
        {
            _active.TryRemove(active.RunId, out _);
            active.Cts.Dispose();
        }
    }

    private enum NodeReadiness { Waiting, Ready, Skip, FailFromUpstream }

    private static NodeReadiness EvaluateReadiness(WorkflowNodeDef node, List<WorkflowEdgeDef> incomingEdges, ActiveWorkflow active)
    {
        if (incomingEdges.Count == 0)
        {
            return NodeReadiness.Ready;
        }

        var edgeStatuses = incomingEdges.Select(e => (Edge: e, Status: active.GetEdgeStatus(e.Id), Source: active.GetNode(e.Source))).ToList();

        if (node.Kind == WorkflowNodeKind.Join)
        {
            var strategy = node.Config?.Strategy ?? WorkflowJoinStrategy.All;
            if (strategy == WorkflowJoinStrategy.Any)
            {
                if (edgeStatuses.Any(static x => x.Status == WorkflowEdgeRunStatus.Active && x.Source.Status == WorkflowNodeRunStatus.Done))
                {
                    return NodeReadiness.Ready;
                }

                if (edgeStatuses.All(static x => WorkflowView.IsTerminal(x.Source.Status)))
                {
                    return edgeStatuses.All(static x => x.Status == WorkflowEdgeRunStatus.Skipped) ? NodeReadiness.Skip : NodeReadiness.FailFromUpstream;
                }

                return NodeReadiness.Waiting;
            }

            if (strategy == WorkflowJoinStrategy.SettleAll)
            {
                if (!edgeStatuses.All(static x => WorkflowView.IsTerminal(x.Source.Status)))
                {
                    return NodeReadiness.Waiting;
                }

                return edgeStatuses.All(static x => x.Status == WorkflowEdgeRunStatus.Skipped) ? NodeReadiness.Skip : NodeReadiness.Ready;
            }
        }

        // 默认 / Join(All): 所有上游必须到达终态(且边已定态)
        if (edgeStatuses.Any(static x => x.Status == WorkflowEdgeRunStatus.Idle))
        {
            return NodeReadiness.Waiting;
        }

        if (edgeStatuses.All(static x => x.Status == WorkflowEdgeRunStatus.Skipped))
        {
            return NodeReadiness.Skip;
        }

        if (edgeStatuses.Any(static x => x.Status == WorkflowEdgeRunStatus.Active && x.Source.Status == WorkflowNodeRunStatus.Failed))
        {
            return NodeReadiness.FailFromUpstream;
        }

        return NodeReadiness.Ready;
    }

    private async Task RunNodeWithSemaphoreAsync(
        ActiveWorkflow active,
        WorkflowNodeDef defNode,
        List<WorkflowEdgeDef> outgoingEdges,
        List<WorkflowEdgeDef> incomingEdges,
        SemaphoreSlim sem,
        Action triggerFailFast,
        Action<string> recordError)
    {
        await sem.WaitAsync(active.Cts.Token).ConfigureAwait(false);
        try
        {
            var current = active.GetNode(defNode.Id);
            var maxRetries = Math.Max(0, defNode.Config?.MaxRetries ?? 0);
            var onError = defNode.Config?.OnError ?? WorkflowOnError.FailFast;

            for (var retry = 0; retry <= maxRetries; retry++)
            {
                if (active.Cts.IsCancellationRequested)
                {
                    UpdateNode(active, current with { Status = WorkflowNodeRunStatus.Cancelled, FinishedAt = _clock.GetUtcNow().ToUnixTimeMilliseconds() });
                    return;
                }

                var attempt = retry == 0 ? current.Attempt : current.Attempt + 1;
                var startedAt = _clock.GetUtcNow().ToUnixTimeMilliseconds();
                current = current with
                {
                    Attempt = attempt,
                    Status = WorkflowNodeRunStatus.Running,
                    Error = null,
                    StartedAt = startedAt,
                    FinishedAt = null,
                };
                UpdateNode(active, current);

                WorkflowNodeExecutionResult result;
                try
                {
                    result = await ExecuteSingleNodeAsync(active, defNode, attempt, incomingEdges, active.Cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (active.Cts.IsCancellationRequested)
                {
                    UpdateNode(active, current with { Status = WorkflowNodeRunStatus.Cancelled, FinishedAt = _clock.GetUtcNow().ToUnixTimeMilliseconds() });
                    return;
                }
                catch (Exception ex)
                {
                    result = new WorkflowNodeExecutionResult(false, Error: ex.Message);
                }

                var finishedAt = _clock.GetUtcNow().ToUnixTimeMilliseconds();
                if (result.Succeeded)
                {
                    var done = current with
                    {
                        Status = WorkflowNodeRunStatus.Done,
                        Summary = result.Summary ?? FirstLine(result.OutputText),
                        OutputText = result.OutputText,
                        OutputJson = result.OutputJson,
                        ChildRunId = result.ChildRunId ?? current.ChildRunId,
                        ChildSessionId = result.ChildSessionId ?? current.ChildSessionId,
                        Tokens = result.Tokens,
                        FinishedAt = finishedAt,
                    };
                    UpdateNode(active, done);
                    ActivateOutgoingEdges(active, outgoingEdges, result.ActiveOutputPorts);
                    return;
                }

                if (retry < maxRetries && !active.Cts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * (retry + 1)), _clock, active.Cts.Token).ConfigureAwait(false);
                    continue;
                }

                var failed = current with
                {
                    Status = WorkflowNodeRunStatus.Failed,
                    Error = result.Error ?? "workflows.error.nodeFailed",
                    ChildRunId = result.ChildRunId ?? current.ChildRunId,
                    ChildSessionId = result.ChildSessionId ?? current.ChildSessionId,
                    Tokens = result.Tokens,
                    FinishedAt = finishedAt,
                };
                UpdateNode(active, failed);
                recordError(failed.Error!);

                if (onError == WorkflowOnError.FailFast)
                {
                    foreach (var edge in outgoingEdges)
                    {
                        SetEdgeStatus(active, edge.Id, WorkflowEdgeRunStatus.Skipped);
                    }

                    triggerFailFast();
                }
                else
                {
                    // Continue 策略:边标为 Active 让下游(如 SettleAll Join)决定如何处理
                    foreach (var edge in outgoingEdges)
                    {
                        SetEdgeStatus(active, edge.Id, WorkflowEdgeRunStatus.Active);
                    }
                }

                return;
            }
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task<WorkflowNodeExecutionResult> ExecuteSingleNodeAsync(
        ActiveWorkflow active,
        WorkflowNodeDef node,
        int attempt,
        List<WorkflowEdgeDef> incomingEdges,
        CancellationToken ct)
    {
        var completedNodes = active.GetCompletedNodesMap();

        switch (node.Kind)
        {
            case WorkflowNodeKind.Trigger:
                return new WorkflowNodeExecutionResult(
                    Succeeded: true,
                    Summary: "Triggered",
                    OutputText: active.InputsJson,
                    OutputJson: active.InputsJson);

            case WorkflowNodeKind.Join:
            {
                var parts = incomingEdges
                    .Select(e => active.GetNode(e.Source))
                    .Where(static n => n.Status == WorkflowNodeRunStatus.Done)
                    .Select(n => $"[{n.NodeId}]\n{n.OutputText ?? n.Summary ?? ""}")
                    .ToArray();
                var joined = string.Join("\n\n", parts);
                return new WorkflowNodeExecutionResult(
                    Succeeded: true,
                    Summary: $"Merged {parts.Length} branch(es)",
                    OutputText: joined);
            }

            case WorkflowNodeKind.Output:
            {
                var rendered = !string.IsNullOrWhiteSpace(node.Config?.OutputTemplate)
                    ? TemplateResolver.Render(node.Config.OutputTemplate, active.Inputs, completedNodes)
                    : string.Join("\n\n", incomingEdges
                        .Select(e => active.GetNode(e.Source))
                        .Where(static n => n.Status == WorkflowNodeRunStatus.Done && !string.IsNullOrEmpty(n.OutputText))
                        .Select(static n => n.OutputText!));
                return new WorkflowNodeExecutionResult(
                    Succeeded: true,
                    Summary: FirstLine(rendered) ?? "Completed",
                    OutputText: rendered);
            }

            case WorkflowNodeKind.Condition when !string.IsNullOrWhiteSpace(node.Config?.Expression):
            {
                var matched = TemplateResolver.EvaluateCondition(node.Config.Expression, active.Inputs, completedNodes);
                var port = matched ? "true" : "false";
                if (node.Outputs is { Count: >= 2 } && !node.Outputs.Any(p => string.Equals(p.Id, port, StringComparison.OrdinalIgnoreCase)))
                {
                    port = matched ? node.Outputs[0].Id : node.Outputs[1].Id;
                }

                return new WorkflowNodeExecutionResult(
                    Succeeded: true,
                    Summary: matched ? "Condition: true" : "Condition: false",
                    OutputText: port,
                    ActiveOutputPorts: [port]);
            }

            default:
            {
                var ctx = new WorkflowNodeExecutionContext(
                    RunId: active.RunId,
                    WorkspaceId: active.WorkspaceId,
                    SessionId: active.SessionId,
                    EnvironmentId: active.EnvironmentId,
                    PermissionMode: active.EffectivePermissionFor(node),
                    NestingDepth: active.NestingDepth,
                    Node: node,
                    Attempt: attempt,
                    Inputs: active.Inputs,
                    CompletedNodes: completedNodes,
                    ReportProgress: updated => UpdateNode(active, updated),
                    UpstreamNodeIds: [.. incomingEdges.Select(static e => e.Source)]);
                return await executor.ExecuteAsync(ctx, ct).ConfigureAwait(false);
            }
        }
    }

    private void ActivateOutgoingEdges(ActiveWorkflow active, List<WorkflowEdgeDef> outgoingEdges, IReadOnlyList<string>? activePorts)
    {
        if (activePorts is null)
        {
            foreach (var edge in outgoingEdges)
            {
                SetEdgeStatus(active, edge.Id, WorkflowEdgeRunStatus.Active);
            }

            return;
        }

        var portSet = new HashSet<string>(activePorts, StringComparer.OrdinalIgnoreCase);
        foreach (var edge in outgoingEdges)
        {
            var isMatch = string.IsNullOrEmpty(edge.SourcePort) || portSet.Contains(edge.SourcePort);
            SetEdgeStatus(active, edge.Id, isMatch ? WorkflowEdgeRunStatus.Active : WorkflowEdgeRunStatus.Skipped);
        }
    }

    private void UpdateNode(ActiveWorkflow active, WorkflowNodeRunRecord node)
    {
        var (snapshot, seq, merged) = active.UpdateNode(node);
        PersistNode(active.RunId, merged);
        PersistRun(active);
        Updated?.Invoke(new WorkflowEngineEvent(active.RunId, seq, "node_updated", snapshot, Node: merged));
    }

    private void SetEdgeStatus(ActiveWorkflow active, string edgeId, WorkflowEdgeRunStatus status)
    {
        var (snapshot, seq, edge) = active.UpdateEdge(edgeId, status);
        Updated?.Invoke(new WorkflowEngineEvent(active.RunId, seq, "edge_updated", snapshot, Edge: edge));
    }

    private void FinishRun(ActiveWorkflow active, WorkflowRunStatus status, string? outputText, string? outputJson, string? error, long finishedAt)
    {
        var (snapshot, seq) = active.Finish(status, outputText, outputJson, error, finishedAt);
        PersistRun(active);
        Updated?.Invoke(new WorkflowEngineEvent(active.RunId, seq, "run_updated", snapshot));
    }

    private void PersistRun(ActiveWorkflow active)
    {
        var snap = active.Snapshot();
        repository.PutRun(new WorkflowRunRow(
            Id: snap.RunId,
            WorkspaceId: snap.WorkspaceId,
            SessionId: snap.SessionId,
            WorkflowId: snap.WorkflowId,
            Title: snap.Title,
            DefinitionJson: active.DefinitionJson,
            InputsJson: active.InputsJson,
            Status: FormatRunStatus(snap.Status),
            OutputText: snap.OutputText,
            OutputJson: snap.OutputJson,
            Error: snap.Error,
            EnvironmentId: snap.EnvironmentId,
            PermissionMode: active.PermissionMode,
            TotalTokens: snap.TotalTokens,
            CreatedAt: snap.CreatedAt,
            StartedAt: snap.StartedAt,
            FinishedAt: snap.FinishedAt));
    }

    private void PersistNode(string runId, WorkflowNodeRunRecord node) =>
        repository.PutNodeRun(new WorkflowNodeRunRow(
            RunId: runId,
            NodeId: node.NodeId,
            Attempt: node.Attempt,
            Kind: node.Kind.ToString().ToLowerInvariant(),
            Status: FormatNodeStatus(node.Status),
            ChildRunId: node.ChildRunId,
            ChildSessionId: node.ChildSessionId,
            OutputText: node.OutputText,
            OutputJson: node.OutputJson,
            Error: node.Error,
            Tokens: node.Tokens,
            StartedAt: node.StartedAt,
            FinishedAt: node.FinishedAt));

    private WorkflowRunRecord? LoadFromRepository(string runId)
    {
        var row = repository.GetRun(runId);
        if (row is null)
        {
            return null;
        }

        var def = JsonSerializer.Deserialize(row.DefinitionJson, WorkflowJsonContext.Default.WorkflowDefinition);
        if (def is null)
        {
            return null;
        }

        var latestNodes = repository.GetLatestNodes(runId).ToDictionary(static n => n.NodeId, StringComparer.Ordinal);
        var nodes = def.Nodes.Select(n =>
        {
            if (latestNodes.TryGetValue(n.Id, out var nr))
            {
                return new WorkflowNodeRunRecord(
                    NodeId: nr.NodeId,
                    Kind: n.Kind,
                    Status: ParseNodeStatus(nr.Status),
                    Attempt: nr.Attempt,
                    ChildRunId: nr.ChildRunId,
                    ChildSessionId: nr.ChildSessionId,
                    Summary: FirstLine(nr.OutputText),
                    OutputText: nr.OutputText,
                    OutputJson: nr.OutputJson,
                    Error: nr.Error,
                    Tokens: nr.Tokens,
                    StartedAt: nr.StartedAt,
                    FinishedAt: nr.FinishedAt);
            }

            return new WorkflowNodeRunRecord(n.Id, n.Kind, WorkflowNodeRunStatus.Pending, Attempt: 1);
        }).ToImmutableArray();

        var nodeMap = nodes.ToDictionary(static n => n.NodeId, StringComparer.Ordinal);
        var edges = def.Edges.Select(e =>
        {
            var src = nodeMap[e.Source].Status;
            var dst = nodeMap[e.Target].Status;
            var status = dst == WorkflowNodeRunStatus.Skipped
                ? WorkflowEdgeRunStatus.Skipped
                : src == WorkflowNodeRunStatus.Done && dst is WorkflowNodeRunStatus.Running or WorkflowNodeRunStatus.Done or WorkflowNodeRunStatus.Failed or WorkflowNodeRunStatus.WaitingApproval
                    ? WorkflowEdgeRunStatus.Active
                    : WorkflowEdgeRunStatus.Idle;
            return new WorkflowEdgeRunRecord(e.Id, status);
        }).ToImmutableArray();

        return new WorkflowRunRecord(
            RunId: row.Id,
            WorkspaceId: row.WorkspaceId,
            Title: row.Title,
            Status: ParseRunStatus(row.Status),
            Definition: def,
            Nodes: nodes,
            Edges: edges,
            SessionId: row.SessionId,
            WorkflowId: row.WorkflowId,
            EnvironmentId: row.EnvironmentId,
            OutputText: row.OutputText,
            OutputJson: row.OutputJson,
            Error: row.Error,
            TotalTokens: row.TotalTokens,
            CreatedAt: row.CreatedAt,
            StartedAt: row.StartedAt,
            FinishedAt: row.FinishedAt);
    }

    private static HashSet<string> CollectDownstream(WorkflowDefinition def, string rootNodeId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { rootNodeId };
        var queue = new Queue<string>();
        queue.Enqueue(rootNodeId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var e in def.Edges)
            {
                if (string.Equals(e.Source, current, StringComparison.Ordinal) && result.Add(e.Target))
                {
                    queue.Enqueue(e.Target);
                }
            }
        }

        return result;
    }

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var line = text.Trim().Split('\n')[0].Trim();
        return line.Length > 120 ? line[..120] + "…" : line;
    }

    public static string FormatRunStatus(WorkflowRunStatus status) => status switch
    {
        WorkflowRunStatus.Queued => "queued",
        WorkflowRunStatus.Running => "running",
        WorkflowRunStatus.WaitingApproval => "waiting_approval",
        WorkflowRunStatus.Completed => "completed",
        WorkflowRunStatus.Failed => "failed",
        WorkflowRunStatus.Cancelled => "cancelled",
        _ => "unknown",
    };

    public static WorkflowRunStatus ParseRunStatus(string? s) => s switch
    {
        "queued" => WorkflowRunStatus.Queued,
        "running" => WorkflowRunStatus.Running,
        "waiting_approval" => WorkflowRunStatus.WaitingApproval,
        "completed" => WorkflowRunStatus.Completed,
        "failed" => WorkflowRunStatus.Failed,
        "cancelled" => WorkflowRunStatus.Cancelled,
        _ => WorkflowRunStatus.Unknown,
    };

    public static string FormatNodeStatus(WorkflowNodeRunStatus status) => status switch
    {
        WorkflowNodeRunStatus.Pending => "pending",
        WorkflowNodeRunStatus.Running => "running",
        WorkflowNodeRunStatus.WaitingApproval => "waiting_approval",
        WorkflowNodeRunStatus.Done => "done",
        WorkflowNodeRunStatus.Failed => "failed",
        WorkflowNodeRunStatus.Skipped => "skipped",
        WorkflowNodeRunStatus.Cancelled => "cancelled",
        _ => "unknown",
    };

    public static WorkflowNodeRunStatus ParseNodeStatus(string? s) => s switch
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

    private sealed class ActiveWorkflow(
        WorkflowRunRecord initial,
        Dictionary<string, string> inputs,
        string definitionJson,
        string inputsJson,
        string permissionMode,
        int nestingDepth)
    {
        private readonly Lock _gate = new();
        private WorkflowRunRecord _record = initial;
        private readonly Dictionary<string, WorkflowNodeRunRecord> _nodes = initial.Nodes.ToDictionary(static n => n.NodeId, StringComparer.Ordinal);
        private readonly Dictionary<string, WorkflowEdgeRunStatus> _edges = initial.Edges.ToDictionary(static e => e.EdgeId, static e => e.Status, StringComparer.Ordinal);

        public string RunId { get; } = initial.RunId;
        public string WorkspaceId { get; } = initial.WorkspaceId;
        public string? SessionId { get; } = initial.SessionId;
        public string? EnvironmentId { get; } = initial.EnvironmentId;
        public WorkflowDefinition Definition { get; } = initial.Definition;
        public string DefinitionJson { get; } = definitionJson;
        public string InputsJson { get; } = inputsJson;
        public IReadOnlyDictionary<string, string> Inputs { get; } = inputs;
        public string PermissionMode { get; } = permissionMode;
        public int NestingDepth { get; } = nestingDepth;
        public CancellationTokenSource Cts { get; } = new();
        public Task? ExecutionTask { get; set; }

        /// <summary>权限只收窄(WF5):min(工作流发起档位, 节点声明档位)。</summary>
        public string EffectivePermissionFor(WorkflowNodeDef node)
        {
            var declared = node.Config?.PermissionMode;
            if (string.IsNullOrWhiteSpace(declared))
            {
                return PermissionMode;
            }

            return PermRank(declared) < PermRank(PermissionMode) ? declared : PermissionMode;
        }

        private static int PermRank(string mode) => mode switch
        {
            "ask" => 0,
            "auto" => 1,
            "full" => 2,
            _ => 0,
        };

        public WorkflowNodeRunRecord GetNode(string nodeId)
        {
            lock (_gate)
            {
                return _nodes[nodeId];
            }
        }

        public ImmutableArray<WorkflowNodeRunRecord> GetAllNodes()
        {
            lock (_gate)
            {
                return [.. Definition.Nodes.Select(n => _nodes[n.Id])];
            }
        }

        public IReadOnlyDictionary<string, WorkflowNodeRunRecord> GetCompletedNodesMap()
        {
            lock (_gate)
            {
                return _nodes.Where(static kv => kv.Value.Status == WorkflowNodeRunStatus.Done)
                    .ToDictionary(static kv => kv.Key, static kv => kv.Value, StringComparer.Ordinal);
            }
        }

        public WorkflowEdgeRunStatus GetEdgeStatus(string edgeId)
        {
            lock (_gate)
            {
                return _edges[edgeId];
            }
        }

        public WorkflowRunRecord Snapshot()
        {
            lock (_gate)
            {
                return _record;
            }
        }

        public (WorkflowRunRecord Snapshot, long Seq, WorkflowNodeRunRecord Merged) UpdateNode(WorkflowNodeRunRecord incoming)
        {
            lock (_gate)
            {
                var merged = WorkflowView.MergeNode(_nodes.GetValueOrDefault(incoming.NodeId), incoming);
                _nodes[incoming.NodeId] = merged;
                var waiting = _nodes.Values.Any(static n => n.Status == WorkflowNodeRunStatus.WaitingApproval);
                var runStatus = WorkflowView.IsTerminal(_record.Status)
                    ? _record.Status
                    : waiting ? WorkflowRunStatus.WaitingApproval : WorkflowRunStatus.Running;
                var totalTokens = _nodes.Values.Sum(static n => n.Tokens);
                var seq = _record.Seq + 1;
                _record = _record with
                {
                    Status = runStatus,
                    Nodes = [.. Definition.Nodes.Select(n => _nodes[n.Id])],
                    TotalTokens = totalTokens,
                    Seq = seq,
                };
                return (_record, seq, merged);
            }
        }

        public (WorkflowRunRecord Snapshot, long Seq, WorkflowEdgeRunRecord Edge) UpdateEdge(string edgeId, WorkflowEdgeRunStatus status)
        {
            lock (_gate)
            {
                _edges[edgeId] = status;
                var seq = _record.Seq + 1;
                _record = _record with
                {
                    Edges = [.. Definition.Edges.Select(e => new WorkflowEdgeRunRecord(e.Id, _edges[e.Id]))],
                    Seq = seq,
                };
                return (_record, seq, new WorkflowEdgeRunRecord(edgeId, status));
            }
        }

        public (WorkflowRunRecord Snapshot, long Seq) Finish(WorkflowRunStatus status, string? outputText, string? outputJson, string? error, long finishedAt)
        {
            lock (_gate)
            {
                var seq = _record.Seq + 1;
                _record = _record with
                {
                    Status = status,
                    OutputText = outputText,
                    OutputJson = outputJson,
                    Error = error,
                    FinishedAt = finishedAt,
                    Seq = seq,
                };
                return (_record, seq);
            }
        }
    }
}
