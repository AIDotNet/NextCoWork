using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Core.Layout;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>
/// 项目相关的用户意图。项目栏的「+」、项目面板空状态里的按钮、首次引导都调 <see cref="NewProjectAsync"/>(AGENTS.md §3 规则 4);
/// 侧边栏菜单的「从侧边栏移除」调 <see cref="RemoveAsync"/>。
/// </summary>
public sealed class ProjectActions(
    ClientStores stores,
    DeviceRegistry devices,
    Func<IDialogService> dialogs,
    Func<IClientText> text,
    IEnvironmentCommands? environments = null,
    SshInteractionsViewModel? sshInteractions = null,
    Action? openEnvironments = null)
{
    /// <summary>「新建项目」:弹窗选设备、本机文件夹或 SSH 主机远程目录,添加成功后选中新项目。</summary>
    public async Task NewProjectAsync(NewProjectSource initialSource = NewProjectSource.Local)
    {
        using var form = new NewProjectViewModel(stores, this, dialogs, text, stores.Selection.State.Project?.DeviceId,
            environments, sshInteractions, openEnvironments, initialSource);
        if (sshInteractions is not null)
        {
            sshInteractions.SuppressGlobalOverlay = true;
        }

        try
        {
            await dialogs().ShowNewProjectAsync(form);
        }
        finally
        {
            if (sshInteractions is not null)
            {
                sshInteractions.SuppressGlobalOverlay = false;
            }
        }
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

    /// <summary>
    /// 「从侧边栏移除」(docs/design/03 §2):在项目所在的设备上注销它(<c>projects.remove</c>,文件夹里的文件不动),再从项目栏、自定义外观、当前选中里拿掉。
    /// 只改本地布局是不够的:下一次设备推送项目列表(重连、重启)时它会被当成新项目追加回来。
    /// 设备上已经没有的项目(失效项目)只清本地布局,不弹确认;设备没连上时不能移除(说明原因)。
    /// </summary>
    public async Task RemoveAsync(ProjectRef project)
    {
        var t = text();
        if (stores.Workspace.State.Projects.GetValueOrDefault(project) is { } info)
        {
            if (!await dialogs().ConfirmAsync(t.RemoveProjectTitle, t.RemoveProjectMessage(info.Name), t.RemoveProjectLabel))
            {
                return;
            }

            if (devices.Find(project.DeviceId) is not { IsConnected: true } link)
            {
                await dialogs().ShowErrorAsync(t.RemoveProjectFailedTitle, t.ProjectRemoveOffline);
                return;
            }

            try
            {
                await link.CallAsync(DeviceMethods.ProjectsRemove, new ProjectsRemoveParams(project.WorkspaceId), idem: $"projects.remove:{Guid.NewGuid():N}");
            }
            catch (ProtocolException e) when (e.Code == ErrorCodes.NotFound)
            {
                // 设备上已经没有了(别的控制端刚移除过):照样清本地
            }
            catch (ProtocolException e)
            {
                await dialogs().ShowErrorAsync(t.RemoveProjectFailedTitle, e.Code == ErrorCodes.Unavailable ? t.ProjectRemoveOffline : t.ProjectRemoveFailed); // 设备的英文诊断不直接给用户看
                return;
            }
        }

        Forget(project);
    }

    /// <summary>从项目栏、外观和选中里拿掉一个项目(设备的项目镜像由 <c>projects.changed</c> 广播更新,这里不动)。</summary>
    private void Forget(ProjectRef project)
    {
        stores.Layout.Update(state => state.Rail.Contains(project) || state.Appearance.ContainsKey(project)
            ? state with { Rail = RailLayoutOps.Remove(state.Rail, project), Appearance = state.Appearance.Remove(project) }
            : state);
        if (stores.Selection.State.Project == project)
        {
            var next = stores.Layout.State.Rail.Projects().FirstOrDefault(p => p != project && stores.Workspace.State.Projects.ContainsKey(p));
            stores.Selection.Update(_ => new SelectionState(next == default ? null : next, null));
        }
    }
}
