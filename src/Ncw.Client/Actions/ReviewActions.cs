using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>
/// 改动审查相关的用户意图(AGENTS.md §3 规则 4:一个意图一个方法)。变更集的真源在设备库;控制端按 <c>(deviceId, runId)</c> 镜像摘要
/// (<see cref="ClientStores.Review"/>),Run 收口时由连接层自动取一次。摘要不带全文:点开某个文件时按需 <see cref="FileDiffAsync"/>,结果直接交给界面(纯 UI 状态),不进 Store。
/// 撤销 / 重做成功之后这里刷新该 Run 的变更集进 Store(整轮的撤销态以设备为准,以 <c>UpdatedAt</c> 较新的为准,旧响应不会把状态改回去)。
/// </summary>
public sealed class ReviewActions(DeviceRegistry devices, ClientStores stores) : IReviewCommands
{
    /// <summary>取一个顶层 Run 的变更集摘要并合进 Store(打开历史会话的审查卡时用)。没有(没改过文件)是 <c>null</c>,不是错误。</summary>
    public async Task<ActionResult<ReviewChangeSetDto?>> LoadChangeSetAsync(string deviceId, string runId, CancellationToken ct = default)
    {
        var result = await Run(deviceId, link => link.CallAsync(ReviewMethods.ChangeSet, new ReviewRunParams(runId), ct)).ConfigureAwait(false);
        if (!result.IsOk)
        {
            return Fail<ReviewChangeSetResult, ReviewChangeSetDto?>(result);
        }

        Merge(deviceId, runId, result.Value!.ChangeSet);
        return ActionResult<ReviewChangeSetDto?>.Success(result.Value.ChangeSet);
    }

    /// <summary>一个会话里改过文件的各轮(按时间倒序),同时合进 Store。</summary>
    public async Task<ActionResult<ReviewChangeSetDto[]>> ListAsync(string deviceId, string sessionId, CancellationToken ct = default)
    {
        var result = await Run(deviceId, link => link.CallAsync(ReviewMethods.List, new ReviewSessionParams(sessionId), ct)).ConfigureAwait(false);
        if (!result.IsOk)
        {
            return Fail<ReviewListResult, ReviewChangeSetDto[]>(result);
        }

        var sets = result.Value!.ChangeSets;
        Post(deviceId, () => stores.Review.Update(m => ReviewReducer.ApplyMany(m, deviceId, sets)));
        return ActionResult<ReviewChangeSetDto[]>.Success(sets);
    }

    /// <summary>单个文件的 before / after 全文(diff 预览)。没有为 <c>null</c>。</summary>
    public async Task<ActionResult<ReviewFileDiffDto?>> FileDiffAsync(string deviceId, string runId, string path, CancellationToken ct = default)
    {
        var result = await Run(deviceId, link => link.CallAsync(ReviewMethods.FileDiff, new ReviewFileParams(runId, path), ct)).ConfigureAwait(false);
        return result.IsOk ? ActionResult<ReviewFileDiffDto?>.Success(result.Value!.Diff) : Fail<ReviewFileDiffResult, ReviewFileDiffDto?>(result);
    }

    /// <summary>撤销前的预检:磁盘现状已和记录不符的文件路径,界面据此提示用户确认后带 force 重发。</summary>
    public async Task<ActionResult<string[]>> PrecheckUndoAsync(string deviceId, string runId, CancellationToken ct = default)
    {
        var result = await Run(deviceId, link => link.CallAsync(ReviewMethods.PrecheckUndo, new ReviewRunParams(runId), ct)).ConfigureAwait(false);
        return result.IsOk ? ActionResult<string[]>.Success(result.Value!.Conflicts) : Fail<ReviewPrecheckResult, string[]>(result);
    }

    /// <summary>撤销整轮。冲突的文件(磁盘上又被改过)不带 <paramref name="force"/> 时跳过并在结果里标 <c>conflict</c>。成功后刷新这个 Run 的变更集。</summary>
    public async Task<ActionResult<ReviewMutationDto>> UndoAsync(string deviceId, string runId, bool force = false, CancellationToken ct = default)
    {
        var result = await Run(deviceId, link => link.CallAsync(ReviewMethods.Undo, new ReviewUndoParams(runId, force), ct, idem: Idem("undo"))).ConfigureAwait(false);
        if (result.IsOk)
        {
            await RefreshAsync(deviceId, runId, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>恢复(重做)一个已撤销的变更集。成功后刷新这个 Run 的变更集。</summary>
    public async Task<ActionResult<ReviewMutationDto>> RedoAsync(string deviceId, string runId, CancellationToken ct = default)
    {
        var result = await Run(deviceId, link => link.CallAsync(ReviewMethods.Redo, new ReviewRunParams(runId), ct, idem: Idem("redo"))).ConfigureAwait(false);
        if (result.IsOk)
        {
            await RefreshAsync(deviceId, runId, ct).ConfigureAwait(false);
        }

        return result;
    }

    private async Task RefreshAsync(string deviceId, string runId, CancellationToken ct)
    {
        // 刷新失败不改撤销 / 重做本身的结局(它已经在设备上发生了):下次打开时会再取。
        _ = await LoadChangeSetAsync(deviceId, runId, ct).ConfigureAwait(false);
    }

    private void Merge(string deviceId, string runId, ReviewChangeSetDto? set) =>
        Post(deviceId, () => stores.Review.Update(m => ReviewReducer.Apply(m, deviceId, runId, set)));

    /// <summary>Store 只在 UI 线程上读写:经这台设备连接的调度器投递。</summary>
    private void Post(string deviceId, Action change) => devices.Find(deviceId)?.PostToUi(change);

    private static ActionResult<TOut> Fail<TIn, TOut>(ActionResult<TIn> result) => new(result.Outcome, default, result.ErrorKey, result.ErrorParams, result.Detail);

    private static string Idem(string what) => $"review.{what}:{Guid.NewGuid():N}";

    private async Task<ActionResult<T>> Run<T>(string deviceId, Func<DeviceLink, Task<T>> call)
    {
        if (devices.Find(deviceId) is not { IsConnected: true } link)
        {
            return ActionResult<T>.NotConnected();
        }

        try
        {
            return ActionResult<T>.Success(await call(link).ConfigureAwait(false));
        }
        catch (ProtocolException e)
        {
            return ActionResult<T>.From(e);
        }
    }
}
