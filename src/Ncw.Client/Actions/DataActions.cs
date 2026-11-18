using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>设置 · 数据 · 回退到 2.x 的三个意图(只对本机设备;<see cref="DataActions"/>)。抽成接口是为了让设置页可以单测。</summary>
public interface IDataRollbackCommands
{
    Task<ActionResult<DataRollbackInfo>> GetRollbackInfoAsync(CancellationToken ct = default);

    /// <summary>登记回退请求,然后让本机 Agent 优雅退出(回退在它关库之后执行),最后退出应用。请求失败时什么都不停、不退出。</summary>
    Task<ActionResult<DataRollbackInfo>> RollBackToLegacyAsync(CancellationToken ct = default);

    Task<ActionResult<DataRollbackInfo>> CancelRollbackAsync(CancellationToken ct = default);
}

/// <summary>
/// 设备数据相关的用户意图:接管页上的「重试」(在「已回退到 2.x」的状态下就是「重新迁移」),以及设置 · 数据 · 回退到 2.x(docs/design/04 §5)。
/// </summary>
/// <param name="host">本机宿主(桌面:能让 sidecar 不管「关窗后继续运行」地退出);没有时回退请求照样登记,Agent 下次关库时执行。</param>
/// <param name="exit">退出应用;没有时(测试、预览)不退出。</param>
public sealed class DataActions(DeviceRegistry devices, Func<IStartupHost?>? host = null, Func<Action?>? exit = null) : IDataRollbackCommands
{
    /// <summary>重试本机设备的数据准备。结果经 data 主题进入 Store;连接断了就等重连后的快照。</summary>
    public async Task RetryLocalAsync()
    {
        if (devices.Local is not { IsConnected: true } link)
        {
            return;
        }

        try
        {
            await link.RetryDataAsync();
        }
        catch (ProtocolException)
        {
            // 失败的原因同样在 data 主题里,界面照 Store 显示。
        }
    }

    public Task<ActionResult<DataRollbackInfo>> GetRollbackInfoAsync(CancellationToken ct = default) =>
        Local(link => link.CallAsync(DeviceMethods.DataRollbackInfo, Unit.Value, ct));

    public async Task<ActionResult<DataRollbackInfo>> RollBackToLegacyAsync(CancellationToken ct = default)
    {
        var requested = await Local(link => link.CallAsync(DeviceMethods.DataRequestRollback, new DataRollbackParams(true), ct, idem: ServiceCall.Idem("data", "requestRollback")))
            .ConfigureAwait(false);
        if (!requested.IsOk)
        {
            return requested;
        }

        // 回退在 Agent 关库之后执行:先让它退出(不管「关窗后继续运行」),再退出应用,好让用户接着用 2.x 打开这份数据
        if (host?.Invoke() is { } h)
        {
            await h.StopLocalAgentAsync().ConfigureAwait(false);
        }

        exit?.Invoke()?.Invoke();
        return requested;
    }

    public Task<ActionResult<DataRollbackInfo>> CancelRollbackAsync(CancellationToken ct = default) =>
        Local(link => link.CallAsync(DeviceMethods.DataCancelRollback, Unit.Value, ct, idem: ServiceCall.Idem("data", "cancelRollback")));

    private Task<ActionResult<DataRollbackInfo>> Local(Func<DeviceLink, Task<DataRollbackInfo>> call) =>
        devices.Local is { } link ? ServiceCall.RunAsync(devices, link.DeviceId, call) : Task.FromResult(ActionResult<DataRollbackInfo>.NotConnected());
}
