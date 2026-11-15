using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Terminal;
using Ncw.Protocol.Session;

namespace Ncw.Agent.Host.Terminals;

/// <summary>
/// 控制端的标识与离开处理(06 §9.4 第 4 点)。控制端标识用 <see cref="CallContext.Principal"/>:本机连接是 <c>local</c>,远程连接是对端公钥指纹(TLS 已证明对端持有私钥);
/// 不用 hello 里的 <c>ClientId</c>(任何已配对的控制端都能随便声称)。一个控制端可以有几条连接(两个窗口、重连交叠),
/// 所以按连接计数:<b>最后一条</b>连接结束时才调 <see cref="TerminalManager.ReleaseController"/>(丢它名下的意图、取消它的创建、杀掉它的远程终端;本机终端属于设备,不受影响)。
/// </summary>
internal sealed class TerminalControllers(TerminalManager manager, ILoopDiagnostics log)
{
    private const string TrackedKey = "terminals.controller";

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _connections = new(StringComparer.Ordinal);

    /// <summary>这次调用的控制端标识;这条连接第一次出现时登记它的结束回调(同一连接只登记一次)。</summary>
    public string Track(CallContext context)
    {
        var id = context.Principal;
        if (context.Items.TryAdd(TrackedKey, id))
        {
            lock (_gate)
            {
                _connections[id] = _connections.GetValueOrDefault(id) + 1;
            }

            context.OnClosed(() => Left(id));
        }

        return id;
    }

    private void Left(string id)
    {
        lock (_gate)
        {
            var left = _connections.GetValueOrDefault(id) - 1;
            if (left > 0)
            {
                _connections[id] = left;
                return;
            }

            _connections.Remove(id);
        }

        _ = ReleaseAsync(id);
    }

    private async Task ReleaseAsync(string id)
    {
        try
        {
            await manager.ReleaseController(id).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log.Warn("Releasing a controller's terminals failed.", e);
        }
    }
}
