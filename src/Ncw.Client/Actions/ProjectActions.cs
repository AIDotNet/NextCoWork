using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>
/// 项目相关的用户意图。项目栏的「+」、项目面板空状态里的按钮、首次引导都调 <see cref="NewProjectAsync"/>(AGENTS.md §3 规则 4)。
/// </summary>
public sealed class ProjectActions(ClientStores stores, DeviceRegistry devices, Func<IDialogService> dialogs, Func<IClientText> text)
{
    /// <summary>「新建项目」:弹窗选设备、填文件夹路径(本机设备可以浏览),添加成功后选中新项目。</summary>
    public async Task NewProjectAsync()
    {
        using var form = new NewProjectViewModel(stores, this, dialogs, text, stores.Selection.State.Project?.DeviceId);
        await dialogs().ShowNewProjectAsync(form);
    }

    /// <summary>在 <paramref name="deviceId"/> 上把 <paramref name="path"/>(那台设备上的文件夹)登记为项目,并选中它。</summary>
    public async Task<ActionResult<ProjectRef>> AddAsync(string deviceId, string path, string? name = null, CancellationToken ct = default)
    {
        if (devices.Find(deviceId) is not { IsConnected: true } link)
        {
            return ActionResult<ProjectRef>.NotConnected();
        }

        try
        {
            var project = await link.AddProjectAsync(path, name, ct);
            stores.Selection.Update(_ => new SelectionState(project, null));
            return ActionResult<ProjectRef>.Success(project);
        }
        catch (ProtocolException e)
        {
            return ActionResult<ProjectRef>.From(e);
        }
    }
}
