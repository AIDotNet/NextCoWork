namespace Ncw.Core.Domain;

/// <summary>一个项目 = 某台设备上的某个工作区。项目栏、分组、角标都以它为 key(docs/design/03 §5)。</summary>
public readonly record struct ProjectRef(string DeviceId, string WorkspaceId)
{
    public override string ToString() => $"{DeviceId}/{WorkspaceId}";
}

/// <summary>设备的种类,只影响界面上的标记(docs/design/03 §1、06 §7)。</summary>
public enum DeviceKind
{
    /// <summary>桌面内置的本机 Agent。</summary>
    Local,
    /// <summary>nextco.work 托管的云端 Agent。</summary>
    Cloud,
    /// <summary>另一台电脑上的 Agent(局域网远程连接)。</summary>
    Remote,
}

/// <summary>设备在控制端看来的连接状态。断开时不判定 Run 结束,而是 Unknown(AGENTS.md §3 规则 7)。</summary>
public enum DeviceConnection
{
    Connected,
    Connecting,
    Offline,
    /// <summary>云端 Agent 休眠中,点击即唤醒。</summary>
    Sleeping,
}

/// <summary>项目所在的执行环境类型(docs/design/06)。</summary>
public enum EnvironmentKind
{
    Local,
    Ssh,
}
