using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>
/// Hooks 相关的用户意图(AGENTS.md §3 规则 4:一个意图一个方法)。钩子配置的真源是设备上的设置文件;控制端按 <c>(deviceId, workspaceId)</c> 镜像清单
/// (<see cref="HooksMirror"/>),读取结果合进镜像;保存 / 删除 / 启停成功之后<b>重拉受影响的清单</b>(全局层的改动影响这台设备上持有的每一份清单,项目层只影响那个项目的)。
/// 其它控制端的改动靠 <c>extensions</c> 主题的 <c>HooksRev</c> 变化触发 <see cref="RefreshHeldAsync"/>。
/// <b>环境变量的值只出不进</b>:保存 / 试运行的请求里带值(控制端 → Agent),镜像和返回值里只有变量名;编辑已有钩子时某个变量的值传 null = 沿用已存的值。
/// 写入类(保存 / 删除 / 启停 / 试运行)需要 owner,viewer / member 得到 <see cref="ActionOutcome.Forbidden"/>。
/// </summary>
public sealed class HooksActions(DeviceRegistry devices, Store<HooksMirror> mirror) : IHooksCommands
{
    private long _seq;

    /// <summary>某个项目的钩子清单(全局层 + 项目层;<paramref name="workspaceId"/> 为 null 只有全局层),合进镜像。member 起。</summary>
    public async Task<ActionResult<HooksListResult>> ListAsync(string deviceId, string? workspaceId = null, CancellationToken ct = default)
    {
        var seq = Interlocked.Increment(ref _seq);
        var result = await Run(deviceId, link => link.CallAsync(HooksMethods.List, new HooksListParams(workspaceId), ct)).ConfigureAwait(false);
        if (result.IsOk)
        {
            Post(deviceId, () => mirror.Update(m => HooksReducer.ApplyList(m, deviceId, workspaceId, result.Value!, seq)));
        }

        return result;
    }

    /// <summary>配置诊断 + 最近的运行期失败(钩子失败不阻断运行,这是用户唯一能发现「它一直在报错」的地方),合进镜像。member 起。</summary>
    public async Task<ActionResult<HooksDiagnosticsResult>> DiagnosticsAsync(string deviceId, string? workspaceId = null, CancellationToken ct = default)
    {
        var seq = Interlocked.Increment(ref _seq);
        var result = await Run(deviceId, link => link.CallAsync(HooksMethods.Diagnostics, new HooksListParams(workspaceId), ct)).ConfigureAwait(false);
        if (result.IsOk)
        {
            Post(deviceId, () => mirror.Update(m => HooksReducer.ApplyDiagnostics(m, deviceId, workspaceId, result.Value!, seq)));
        }

        return result;
    }

    /// <summary>新建或更新一条钩子(<see cref="HookUpsertDto.Id"/> 为空 = 新建)。结果带软警告 key(阻断型钩子用了前缀 matcher)。成功后重拉受影响的清单。owner。</summary>
    public async Task<ActionResult<HooksSaveResult>> SaveAsync(string deviceId, string scope, string? workspaceId, HookUpsertDto hook, CancellationToken ct = default)
    {
        var result = await Run(deviceId, link => link.CallAsync(HooksMethods.Save, new HooksSaveParams(scope, workspaceId, hook), ct, idem: Idem("save"))).ConfigureAwait(false);
        if (result.IsOk)
        {
            await RefreshAffectedAsync(deviceId, scope, workspaceId, ct).ConfigureAwait(false);
        }

        return result;
    }

    public async Task<ActionResult<Unit>> DeleteAsync(string deviceId, string scope, string? workspaceId, string id, CancellationToken ct = default)
    {
        var result = await Run(deviceId, link => link.CallAsync(HooksMethods.Delete, new HooksDeleteParams(scope, workspaceId, id), ct, idem: Idem("delete"))).ConfigureAwait(false);
        if (result.IsOk)
        {
            await RefreshAffectedAsync(deviceId, scope, workspaceId, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>启停一条钩子(只翻转开关,不改内容)。</summary>
    public async Task<ActionResult<Unit>> SetEnabledAsync(string deviceId, string scope, string? workspaceId, string id, bool enabled, CancellationToken ct = default)
    {
        var result = await Run(deviceId, link => link.CallAsync(HooksMethods.SetEnabled, new HooksSetEnabledParams(scope, workspaceId, id, enabled), ct, idem: Idem("setEnabled"))).ConfigureAwait(false);
        if (result.IsOk)
        {
            await RefreshAffectedAsync(deviceId, scope, workspaceId, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// 试运行(草稿,不写盘):command 型真跑一次、返回 退出码 / 输出 / 耗时 / 决定;prompt 型只预览展开结果。<b>真的会在设备上执行命令</b>,所以是 command 且 owner。
    /// 结果直接交给界面(纯 UI 状态),不进镜像。
    /// </summary>
    public Task<ActionResult<HookRunReportDto>> TestAsync(string deviceId, string scope, string? workspaceId, HookUpsertDto draft, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(HooksMethods.Test, new HooksTestParams(scope, workspaceId, draft), ct, idem: Idem("test")));

    /// <summary>
    /// 这台设备上镜像里持有的每一份清单都重拉一遍(<c>extensions</c> 主题的 <c>HooksRev</c> 变了 / 设备重连之后调)。
    /// 失败的不影响其它(那份保持未知,下次再来)。
    /// </summary>
    public async Task RefreshHeldAsync(string deviceId, CancellationToken ct = default)
    {
        foreach (var workspaceId in mirror.State.WorkspacesOf(deviceId))
        {
            _ = await ListAsync(deviceId, workspaceId, ct).ConfigureAwait(false);
        }
    }

    private async Task RefreshAffectedAsync(string deviceId, string scope, string? workspaceId, CancellationToken ct)
    {
        // 刷新失败不改写入本身的结局(它已经在设备上发生了):下次打开 / 下次修订号变化时再取。
        if (scope == HookNames.ScopeGlobal)
        {
            await RefreshHeldAsync(deviceId, ct).ConfigureAwait(false);
            return;
        }

        _ = await ListAsync(deviceId, workspaceId, ct).ConfigureAwait(false);
    }

    /// <summary>Store 只在 UI 线程上读写:经这台设备连接的调度器投递。</summary>
    private void Post(string deviceId, Action change) => devices.Find(deviceId)?.PostToUi(change);

    private static string Idem(string what) => $"hooks.{what}:{Guid.NewGuid():N}";

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
