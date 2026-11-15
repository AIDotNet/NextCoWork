using Ncw.Client.Stores;
using Ncw.Protocol.Session;

namespace Ncw.Client.Devices;

internal sealed partial class ProjectToolsSync
{
    /// <summary>git 没有推送主题(概览由 <c>GitActions</c> 在用户打开面板和每次写操作之后拉):连上时不需要订阅什么。</summary>
    private partial Task AttachGitAsync(DeviceClient client, CancellationToken ct) => Task.CompletedTask;

    /// <summary>设备断开:这台设备上所有项目的 git 概览标为未知(规则 7)。</summary>
    private partial void MarkGitUnknown()
    {
        var id = _deviceId();
        _ui.Post(() => _stores.Git.Update(m => GitReducer.MarkDeviceUnknown(m, id)));
    }
}
