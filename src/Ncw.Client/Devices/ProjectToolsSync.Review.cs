using Ncw.Client.Stores;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Devices;

internal sealed partial class ProjectToolsSync
{
    /// <summary>变更集没有主题:Run 收口时按需取(<see cref="OnRunSettledReview"/>),撤销 / 重做经 <c>ReviewActions</c> 刷新。连上时没有什么要订阅的。</summary>
    private partial Task AttachReviewAsync(DeviceClient client, CancellationToken ct) => Task.CompletedTask;

    /// <summary>设备断开:变更集摘要标为未知(规则 7),内容保留但界面不当现状。</summary>
    private partial void MarkReviewUnknown()
    {
        var id = _deviceId();
        _ui.Post(() => _stores.Review.Update(m => ReviewReducer.MarkDeviceUnknown(m, id)));
    }

    /// <summary>
    /// 一个 Run 收口了(<c>run_settled</c>,晚于设备在 <c>run_end</c> 时的落库):取它的变更集写进 Store。设备没有 <c>review</c> 能力(老设备)、
    /// 角色不够、没连上就跳过;没改过文件是「没有」,不是错误。子代理 Run 的改动聚合在顶层 Run 名下,取子 Run 的 id 得到「没有」也是正常的。
    /// </summary>
    private partial void OnRunSettledReview(DeviceClient? client, string runId)
    {
        if (client is null || !client.Welcome.Features.Contains(Capabilities.Review))
        {
            return;
        }

        _ = LoadAsync(client, runId);
    }

    private async Task LoadAsync(DeviceClient client, string runId)
    {
        try
        {
            var result = await client.CallAsync(ReviewMethods.ChangeSet, new ReviewRunParams(runId)).ConfigureAwait(false);
            var id = _deviceId();
            _ui.Post(() => _stores.Review.Update(m => ReviewReducer.Apply(m, id, runId, result.ChangeSet)));
        }
        catch (Exception e) when (e is ProtocolException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // 断线了:连上之后界面再打开这一轮时由 ReviewActions 重新取。
        }
    }
}
