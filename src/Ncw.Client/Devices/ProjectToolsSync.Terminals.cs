using Ncw.Client.Stores;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Devices;

internal sealed partial class ProjectToolsSync
{
    // 终端的输出订阅按「被关注的终端」管理(TerminalWatches:由 TerminalActions 经 DeviceLink.Synced 在每次重连后按游标续订)。
    // 这里管元数据:重连后,断开时被标为未知的终端所在的项目各取一次列表,把状态补回来(没有这一步,游标续上、没有快照的终端会一直是「未知」)。
    private async partial Task AttachTerminalsAsync(DeviceClient client, CancellationToken ct)
    {
        if (!client.Welcome.Features.Contains(Capabilities.Terminals))
        {
            return;
        }

        var id = _deviceId();
        var unknown = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(() => unknown.TrySetResult(
        [
            .. _stores.Terminals.State.Terminals
                .Where(kv => kv.Key.DeviceId == id && !kv.Value.Known)
                .Select(static kv => kv.Value.Info.ProjectId)
                .Distinct(StringComparer.Ordinal),
        ]));
        try
        {
            foreach (var projectId in await unknown.Task.WaitAsync(ct).ConfigureAwait(false))
            {
                var list = await client.CallAsync(TerminalMethods.List, new TerminalsListParams(projectId), ct).ConfigureAwait(false);
                _ui.Post(() => _stores.Terminals.Update(m => TerminalReducer.ApplyList(m, id, projectId, list.Terminals)));
            }
        }
        catch (Exception e) when (e is ProtocolException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // 项目没了 / 又断线:不挡同步;状态保持「未知」,下一次重连或界面的 ListAsync 补上
        }
    }

    /// <summary>设备断开:终端的状态未知(规则 7),不判成结束也不删,重连后新的元数据补上。</summary>
    private partial void MarkTerminalsUnknown()
    {
        var id = _deviceId();
        _ui.Post(() => _stores.Terminals.Update(m => TerminalReducer.MarkDeviceUnknown(m, id)));
    }
}
