using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>
/// 项目相关的用户意图。项目栏的「+」、空状态里的按钮都调这里(AGENTS.md §3 规则 4)。
/// </summary>
public sealed class ProjectActions(ClientStores stores, DeviceRegistry devices, Func<IDialogService> dialogs, Func<IClientText> text)
{
    /// <summary>选一个本机文件夹,在本机设备上登记为项目,并选中它。</summary>
    public async Task AddLocalProjectAsync()
    {
        var t = text();
        var path = await dialogs().PickFolderAsync(t.AddProjectPickTitle);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (devices.Local is not { IsConnected: true } link)
        {
            await dialogs().ShowErrorAsync(t.AddProjectFailedTitle, t.DeviceNotConnected);
            return;
        }

        try
        {
            var project = await link.AddProjectAsync(path);
            stores.Selection.Update(_ => new SelectionState(project, null));
        }
        catch (ProtocolException e)
        {
            await dialogs().ShowErrorAsync(t.AddProjectFailedTitle, e.Message);
        }
    }
}
