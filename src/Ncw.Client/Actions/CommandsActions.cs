using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>
/// 斜杠命令相关的用户意图(AGENTS.md §3 规则 4)。命令是<b>一段提示词模板</b>:<b>展开在客户端发送前完成</b>(<see cref="ExpandAsync"/>,规则只有 Core 的 <see cref="CommandExpansion"/> 一份),
/// 设备收到的永远是普通文本,所以手机和桌面看到的输入一致。设备只回答「有哪些、哪些被关了」;清单按 <c>(deviceId, workspaceId)</c> 镜像(<see cref="CommandsMirror"/>),
/// 其它控制端的改动靠 <c>extensions</c> 主题的 <c>CommandsRev</c> 变化触发 <see cref="RefreshHeldAsync"/>。启停需要 owner。
/// </summary>
public sealed class CommandsActions(DeviceRegistry devices, Store<CommandsMirror> mirror) : ICommandsAdminCommands
{
    private long _seq;

    /// <summary>运行期清单(已滤掉被关的,带正文),合进镜像。输入框弹层读它。viewer 可用。</summary>
    public async Task<ActionResult<CommandsListResult>> ListAsync(string deviceId, string? workspaceId = null, CancellationToken ct = default)
    {
        var seq = Next();
        var result = await Run(deviceId, link => link.CallAsync(CommandsMethods.List, new CommandsScopeParams(workspaceId), ct)).ConfigureAwait(false);
        if (result.IsOk)
        {
            Post(deviceId, () => mirror.Update(m => CommandsReducer.ApplyList(m, deviceId, workspaceId, result.Value!, seq)));
        }

        return result;
    }

    /// <summary>管理清单(被关的也列出来,标 <c>Enabled</c>,不带正文),合进镜像。viewer 可用。</summary>
    public async Task<ActionResult<CommandsListAllResult>> ListAllAsync(string deviceId, string? workspaceId = null, CancellationToken ct = default)
    {
        var seq = Next();
        var result = await Run(deviceId, link => link.CallAsync(CommandsMethods.ListAll, new CommandsScopeParams(workspaceId), ct)).ConfigureAwait(false);
        MergeAll(deviceId, workspaceId, result, seq);
        return result;
    }

    public async Task<ActionResult<CommandsDiagnosticsResult>> DiagnosticsAsync(string deviceId, string? workspaceId = null, CancellationToken ct = default)
    {
        var seq = Next();
        var result = await Run(deviceId, link => link.CallAsync(CommandsMethods.Diagnostics, new CommandsScopeParams(workspaceId), ct)).ConfigureAwait(false);
        if (result.IsOk)
        {
            Post(deviceId, () => mirror.Update(m => CommandsReducer.ApplyDiagnostics(m, deviceId, workspaceId, result.Value!, seq)));
        }

        return result;
    }

    /// <summary>启用 / 关闭一条命令(设备级偏好,owner)。设备返回变更之后的管理清单,合进镜像;其余持有的清单(含运行期清单)随后重拉。</summary>
    public async Task<ActionResult<CommandsListAllResult>> SetEnabledAsync(string deviceId, string name, bool enabled, string? workspaceId = null, CancellationToken ct = default)
    {
        var seq = Next();
        var result = await Run(deviceId, link => link.CallAsync(CommandsMethods.SetEnabled, new CommandsSetEnabledParams(name, enabled, workspaceId), ct, idem: Idem("setEnabled"))).ConfigureAwait(false);
        MergeAll(deviceId, workspaceId, result, seq);
        if (result.IsOk)
        {
            await RefreshHeldAsync(deviceId, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// 草稿 → 真正要发出去的文本(「发送」这个意图的第一步)。<b>不是一次命令调用就原样返回,不碰设备</b>(普通消息零开销);是一次调用时向设备要一份<b>新鲜</b>的运行期清单再展开
    /// (用户刚往 <c>.next-cowork/commands/</c> 里放了文件就敲 <c>/</c>:镜像可能是旧的),并把清单合进镜像。认不出的命令名原样返回(用户可能真的想以 <c>/</c> 开头说一句话)。
    /// 清单取不到(设备没连上……)得到失败结局,<b>不</b>静默地把没展开的 <c>/xxx</c> 当普通文本发出去。
    /// </summary>
    public async Task<ActionResult<string>> ExpandAsync(string deviceId, string? workspaceId, string draft, CancellationToken ct = default)
    {
        if (CommandExpansion.Parse(draft) is null)
        {
            return ActionResult<string>.Success(draft);
        }

        var list = await ListAsync(deviceId, workspaceId, ct).ConfigureAwait(false);
        return list.IsOk
            ? ActionResult<string>.Success(CommandExpansion.Apply(draft, list.Value!.Commands.Select(c => (c.Name, c.Prompt))))
            : new ActionResult<string>(list.Outcome, default, list.ErrorKey, list.ErrorParams, list.Detail);
    }

    /// <summary>这台设备上镜像里持有的每一份清单都重拉一遍(<c>CommandsRev</c> 变了 / 设备重连之后调)。</summary>
    public async Task RefreshHeldAsync(string deviceId, CancellationToken ct = default)
    {
        foreach (var layer in mirror.State.Lists.Where(kv => kv.Key.DeviceId == deviceId).ToList())
        {
            var workspaceId = layer.Key.WorkspaceId;
            if (layer.Value.HasCommands)
            {
                _ = await ListAsync(deviceId, workspaceId, ct).ConfigureAwait(false);
            }

            if (layer.Value.HasAll)
            {
                _ = await ListAllAsync(deviceId, workspaceId, ct).ConfigureAwait(false);
            }
        }
    }

    private void MergeAll(string deviceId, string? workspaceId, ActionResult<CommandsListAllResult> result, long seq)
    {
        if (result.IsOk)
        {
            Post(deviceId, () => mirror.Update(m => CommandsReducer.ApplyListAll(m, deviceId, workspaceId, result.Value!, seq)));
        }
    }

    private long Next() => Interlocked.Increment(ref _seq);

    private void Post(string deviceId, Action change) => devices.Find(deviceId)?.PostToUi(change);

    private static string Idem(string what) => $"commands.{what}:{Guid.NewGuid():N}";

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
