using System.Collections.Concurrent;
using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>对话内工作流的控制与订阅抽象(方便测试替换)。</summary>
public interface IWorkflowCommands
{
    Task<ActionResult<Unit>> CancelAsync(string deviceId, string runId, CancellationToken ct = default);
    Task<ActionResult<WorkflowRunSnapshotDto>> ResumeAsync(string deviceId, string runId, CancellationToken ct = default);
    Task<ActionResult<WorkflowRunSnapshotDto>> RetryNodeAsync(string deviceId, string runId, string nodeId, CancellationToken ct = default);
    Task<ActionResult<WorkflowRunsListResult>> ListSessionRunsAsync(string deviceId, string sessionId, CancellationToken ct = default);
    Task<ActionResult<WorkflowRunSnapshotDto>> WatchRunAsync(string deviceId, string runId, CancellationToken ct = default);
}

/// <summary>
/// 对话内 Agent 工作流的用户意图唯一入口(AGENTS.md §3 规则 4)。
/// 真源在 Agent;订阅 <c>workflow/&lt;runId&gt;</c> 并将快照与增量经 <see cref="WorkflowReducer"/> 写入 <see cref="ClientStores.Workflows"/>。
/// </summary>
public sealed class WorkflowActions(DeviceRegistry devices, ClientStores stores) : IWorkflowCommands
{
    private readonly ConcurrentDictionary<WorkflowRunKey, byte> _subscriptions = new();

    public Task<ActionResult<Unit>> CancelAsync(string deviceId, string runId, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId,
            link => link.CallAsync(WorkflowMethods.Cancel, new WorkflowRunRefParams(runId), ct, idem: ServiceCall.Idem("workflows", "cancel")));

    public async Task<ActionResult<WorkflowRunSnapshotDto>> ResumeAsync(string deviceId, string runId, CancellationToken ct = default)
    {
        await EnsureSubscribedAsync(deviceId, runId, ct).ConfigureAwait(false);
        var res = await ServiceCall.RunAsync(devices, deviceId,
            link => link.CallAsync(WorkflowMethods.Resume, new WorkflowRunRefParams(runId), ct, idem: ServiceCall.Idem("workflows", "resume"))).ConfigureAwait(false);
        if (res.IsOk && res.Value is { } snapshot)
        {
            stores.Workflows.Update(s => WorkflowReducer.ApplySnapshot(s, deviceId, snapshot));
        }

        return res;
    }

    public async Task<ActionResult<WorkflowRunSnapshotDto>> RetryNodeAsync(string deviceId, string runId, string nodeId, CancellationToken ct = default)
    {
        await EnsureSubscribedAsync(deviceId, runId, ct).ConfigureAwait(false);
        var res = await ServiceCall.RunAsync(devices, deviceId,
            link => link.CallAsync(WorkflowMethods.RetryNode, new WorkflowRetryNodeParams(runId, nodeId), ct, idem: ServiceCall.Idem("workflows", "retryNode"))).ConfigureAwait(false);
        if (res.IsOk && res.Value is { } snapshot)
        {
            stores.Workflows.Update(s => WorkflowReducer.ApplySnapshot(s, deviceId, snapshot));
        }

        return res;
    }

    public Task<ActionResult<WorkflowRunsListResult>> ListSessionRunsAsync(string deviceId, string sessionId, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId,
            link => link.CallAsync(WorkflowMethods.ListRuns, new WorkflowRunsListParams(SessionId: sessionId), ct));

    public async Task<ActionResult<WorkflowRunSnapshotDto>> WatchRunAsync(string deviceId, string runId, CancellationToken ct = default)
    {
        await EnsureSubscribedAsync(deviceId, runId, ct).ConfigureAwait(false);
        var res = await ServiceCall.RunAsync(devices, deviceId,
            link => link.CallAsync(WorkflowMethods.GetRun, new WorkflowRunRefParams(runId), ct)).ConfigureAwait(false);
        if (res.IsOk && res.Value is { } snapshot)
        {
            stores.Workflows.Update(s => WorkflowReducer.ApplySnapshot(s, deviceId, snapshot));
        }

        return res;
    }

    private async Task EnsureSubscribedAsync(string deviceId, string runId, CancellationToken ct)
    {
        var key = new WorkflowRunKey(deviceId, runId);
        if (!_subscriptions.TryAdd(key, 0))
        {
            return;
        }

        if (devices.Find(deviceId) is not { IsConnected: true } link)
        {
            _subscriptions.TryRemove(key, out _);
            return;
        }

        try
        {
            await link.WatchWorkflowRunAsync(runId, ct).ConfigureAwait(false);
        }
        catch
        {
            _subscriptions.TryRemove(key, out _);
        }
    }
}
