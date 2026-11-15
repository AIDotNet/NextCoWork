namespace Ncw.Agent.Environments;

public enum EnvironmentKind
{
    /// <summary>设备自己的文件系统和进程(桌面内置或云端沙箱)。</summary>
    Local,

    /// <summary>设备通过 SSH 连上的远程主机。</summary>
    Ssh,
}

/// <summary>
/// 执行环境标识:设备 + 环境。审批卡片和工具卡片都要带上它(AGENTS.md §5),
/// 同一台远程主机从「本机」和从「云端」打开是两个不同的 <see cref="EnvironmentId"/>。
/// </summary>
public readonly record struct EnvironmentId(string DeviceId, string Environment)
{
    /// <summary>每台设备上的本地环境都叫这个名字。</summary>
    public const string LocalName = "local";

    public bool IsLocal => Environment == LocalName;

    public static EnvironmentId Local(string deviceId) => new(deviceId, LocalName);

    public override string ToString() => $"{DeviceId}/{Environment}";
}

/// <summary>环境连接状态。和 <c>environment.status</c> 事件的取值一一对应(见 06 §6);控制端断开时标为 unknown 是控制端自己的事。</summary>
public enum EnvironmentState
{
    /// <summary>还没连接(SSH 环境是惰性连接的:第一次使用时才连)。</summary>
    Idle,
    Connecting,
    Ready,
    Reconnecting,
    Failed,
    Closed,
}

public sealed class EnvironmentStatusChangedEventArgs(EnvironmentId id, EnvironmentState state, int generation, EnvironmentErrorCode? error, string? detail) : EventArgs
{
    public EnvironmentId Id { get; } = id;

    public EnvironmentState State { get; } = state;

    public int Generation { get; } = generation;

    public EnvironmentErrorCode? Error { get; } = error;

    public string? Detail { get; } = detail;
}
