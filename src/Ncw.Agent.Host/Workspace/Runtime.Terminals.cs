using Ncw.Agent.Host.Terminals;
using Ncw.Agent.Terminal;

namespace Ncw.Agent.Host.Workspace;

internal sealed partial class WorkspaceToolsRuntime
{
    /// <summary>设备上的终端会话表(一个设备一个,本机和 SSH 项目共用)。</summary>
    public TerminalManager Terminals { get; private set; } = null!;

    /// <summary>控制端连接的计数:一个控制端的最后一条连接结束时才释放它名下的远程终端。</summary>
    internal TerminalControllers TerminalControllers { get; private set; } = null!;

    partial void InitTerminals()
    {
        Terminals = new TerminalManager(Services.Terminals ?? new TerminalManagerOptions { Time = Clock });
        TerminalControllers = new TerminalControllers(Terminals, Log);
        // 终端也不是租约持有者:这个环境上还有活终端的项目,它的 SSH 环境就不能被空闲回收
        Conversation.Environments.AddBusyProbe(id => Conversation.Workspaces.List().Any(w => w.ConnectionId == id && Terminals.List(w.Id).Any(static t => t.Alive)));
    }

    private partial ValueTask ProjectRemovedTerminalsAsync(string workspaceId) => new(Terminals.CloseScopeAsync(workspaceId));

    private partial ValueTask DisposeTerminalsAsync() => Terminals.DisposeAsync();
}
