using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>
/// 终端相关的用户意图(AGENTS.md §3 规则 4:一个意图一个方法)。终端的真源在设备的 <c>TerminalManager</c>;元数据(状态、退出码、大小)镜像在 <see cref="ClientStores.Terminals"/>
/// (key 带 deviceId,状态单调、设备断开标为未知),输出字节不进 Store,经 <see cref="ITerminalSink"/> 直接交给显示端。
/// <para>
/// 典型流程:本机项目 <see cref="CreateAsync"/> 直接创建;SSH 项目先 <see cref="PrepareAsync"/> 拿到带环境标识的意图(<see cref="TerminalPrepareResult.Intent"/>),
/// 界面显示审批卡片,用户同意后 <see cref="ApproveAsync"/> 拿凭据,再 <see cref="CreateAsync"/>(带 <c>approval</c>)。然后 <see cref="AttachAsync"/> 接上输出。
/// </para>
/// </summary>
public sealed class TerminalActions(DeviceRegistry devices, ClientStores stores) : ITerminalCommands
{
    /// <summary>一个项目下的终端(全量),结果同时折进 Store(不在列表里的被移除)。viewer 可用。</summary>
    public async Task<ActionResult<TerminalDto[]>> ListAsync(string deviceId, string projectId, CancellationToken ct = default)
    {
        var result = await Run(deviceId, link => link.CallAsync(TerminalMethods.List, new TerminalsListParams(projectId), ct)).ConfigureAwait(false);
        if (!result.IsOk || result.Value is not { } list)
        {
            return new ActionResult<TerminalDto[]>(result.Outcome, null, result.ErrorKey, result.ErrorParams, result.Detail);
        }

        Mirror(deviceId, (m, id) => TerminalReducer.ApplyList(m, id, projectId, list.Terminals));
        return ActionResult<TerminalDto[]>.Success(list.Terminals);
    }

    /// <summary>创建前的握手:本机项目直接得到终端(<see cref="TerminalPrepareResult.Terminal"/>),SSH 项目得到要审批的意图。</summary>
    public async Task<ActionResult<TerminalPrepareResult>> PrepareAsync(string deviceId, string projectId, int columns, int rows, string? terminalId = null, string? cwd = null, CancellationToken ct = default)
    {
        var result = await Run(deviceId, link => link.CallAsync(TerminalMethods.Prepare, new TerminalsPrepareParams(projectId, columns, rows, terminalId, cwd), ct, idem: Idem("prepare"))).ConfigureAwait(false);
        if (result.IsOk && result.Value?.Terminal is { } terminal)
        {
            Mirror(deviceId, (m, id) => TerminalReducer.ApplyInfo(m, id, terminal, 0));
        }

        return result;
    }

    /// <summary>批准(或拒绝)一个远程终端意图。批准返回一次性凭据(<see cref="TerminalApproveResult.Approval"/>),拒绝返回空。</summary>
    public Task<ActionResult<TerminalApproveResult>> ApproveAsync(string deviceId, string intentId, bool approved, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(TerminalMethods.Approve, new TerminalsApproveParams(intentId, approved), ct, idem: Idem("approve")));

    /// <summary>创建终端(<paramref name="terminalId"/> 给了就复用:同一个 id 的活终端直接返回)。SSH 项目要带 <paramref name="approval"/>。</summary>
    public async Task<ActionResult<TerminalDto>> CreateAsync(string deviceId, string projectId, int columns, int rows, string? terminalId = null, string? cwd = null, string? approval = null, CancellationToken ct = default)
    {
        var result = await Run(deviceId, link => link.CallAsync(TerminalMethods.Create, new TerminalsCreateParams(projectId, columns, rows, terminalId, cwd, approval), ct, idem: Idem("create"))).ConfigureAwait(false);
        if (result.IsOk && result.Value is { } terminal)
        {
            Mirror(deviceId, (m, id) => TerminalReducer.ApplyInfo(m, id, terminal, 0));
        }

        return result;
    }

    /// <summary>接上一个终端的输出(订阅 <c>terminal/&lt;id&gt;</c>):接收器先收到整个缓冲,之后是新输出;断线重连后按游标续传(不丢不重),续不上就再给一次整个缓冲。viewer 可用。</summary>
    public async Task<ActionResult<Unit>> AttachAsync(string deviceId, string terminalId, ITerminalSink sink, CancellationToken ct = default)
    {
        if (devices.Find(deviceId) is not { } link)
        {
            return ActionResult<Unit>.NotConnected();
        }

        try
        {
            await TerminalWatches.Of(link, stores).AttachAsync(terminalId, sink, ct).ConfigureAwait(false);
            return ActionResult<Unit>.Success(Unit.Value);
        }
        catch (ProtocolException e)
        {
            return ActionResult<Unit>.From(e);
        }
    }

    /// <summary>不再接收一个终端的输出(页签切走 / 关闭)。终端本身不受影响,回来再 <see cref="AttachAsync"/> 拿回缓冲。</summary>
    public async Task DetachAsync(string deviceId, string terminalId)
    {
        if (devices.Find(deviceId) is { } link)
        {
            await TerminalWatches.Of(link, stores).DetachAsync(terminalId).ConfigureAwait(false);
        }
    }

    /// <summary>不订阅,只取当前的环形缓冲(手机的只读查看)。viewer 可用。</summary>
    public Task<ActionResult<TerminalBufferDto>> GetBufferAsync(string deviceId, string terminalId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(TerminalMethods.GetBuffer, new TerminalRefParams(terminalId), ct));

    /// <summary>键盘输入(一次最多 64 KiB)。每次调用一个新的幂等键;重发同一份输入要用同一个 <paramref name="idem"/>(设备只写一次)。</summary>
    public Task<ActionResult<Unit>> WriteAsync(string deviceId, string terminalId, byte[] data, string? idem = null, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(TerminalMethods.Write, new TerminalsWriteParams(terminalId, data), ct, idem: idem ?? Idem("write")));

    public Task<ActionResult<Unit>> ResizeAsync(string deviceId, string terminalId, int columns, int rows, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(TerminalMethods.Resize, new TerminalsResizeParams(terminalId, columns, rows), ct, idem: Idem("resize")));

    /// <summary>终止终端进程;会话保留为「已退出」(可回看输出),退出经主题到达。页签关闭用 <see cref="CloseAsync"/>。</summary>
    public Task<ActionResult<Unit>> KillAsync(string deviceId, string terminalId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(TerminalMethods.Kill, new TerminalRefParams(terminalId), ct, idem: Idem("kill")));

    /// <summary>页签关闭:设备上终止(若还在跑)并移除,同时不再接收它的输出、从 Store 里去掉。</summary>
    public async Task<ActionResult<Unit>> CloseAsync(string deviceId, string terminalId, CancellationToken ct = default)
    {
        var result = await Run(deviceId, link => link.CallAsync(TerminalMethods.Close, new TerminalRefParams(terminalId), ct, idem: Idem("close"))).ConfigureAwait(false);
        if (result.IsOk || result.Outcome == ActionOutcome.NotFound)
        {
            await DetachAsync(deviceId, terminalId).ConfigureAwait(false);
            Mirror(deviceId, (m, id) => TerminalReducer.ApplyRemoved(m, id, terminalId));
        }

        return result;
    }

    private static string Idem(string what) => $"terminals.{what}:{Guid.NewGuid():N}";

    /// <summary>在 UI 线程上更新终端镜像(Store 只在 UI 线程上读写)。</summary>
    private void Mirror(string deviceId, Func<TerminalMirror, string, TerminalMirror> change)
    {
        if (devices.Find(deviceId) is { } link)
        {
            link.PostToUi(() => stores.Terminals.Update(m => change(m, link.DeviceId)));
        }
    }

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
