namespace Ncw.Desktop.Sidecar;

/// <summary>怎么启动 ncw-agent。<see cref="Lifeline"/>:带 <c>--lifeline stdin</c> 并保持 stdin 管道:桌面端关闭管道就是跨平台的优雅关闭,桌面端崩溃时管道也会断。</summary>
public sealed record AgentLaunchSpec(
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    bool Lifeline);

/// <summary>一个 Agent 进程(自己拉起的,或按 pid 接上的)。</summary>
public interface IAgentProcess : IDisposable
{
    int Id { get; }

    bool HasExited { get; }

    /// <summary>已退出时的退出码。</summary>
    int? ExitCode { get; }

    /// <summary>等进程退出,返回退出码。</summary>
    Task<int> WaitForExitAsync(CancellationToken ct);

    /// <summary>关掉 stdin(<c>--lifeline stdin</c> 的 Agent 据此优雅退出)。接上的进程没有 stdin,什么也不做。</summary>
    void CloseStdin();

    /// <summary>温和地请求终止:Unix 上发 SIGTERM。没有等价物的平台(Windows)返回 false。</summary>
    bool RequestTerminate();

    void Kill();
}

public interface IProcessLauncher
{
    /// <summary>启动。<paramref name="onOutput"/> 收到 stdout / stderr 的每一行(stderr 行带 <c>[stderr] </c> 前缀)。失败抛异常。</summary>
    IAgentProcess Start(AgentLaunchSpec spec, Action<string> onOutput);

    /// <summary>按 pid 接上一个已经在运行的 ncw-agent(用来让过期的 Agent 退出);pid 不存在或不是 ncw-agent 返回 null(防止 pid 复用后误杀别的进程)。</summary>
    IAgentProcess? Attach(int pid);
}

/// <summary>本机通道握手的结果:Agent 的设备 id、版本、协议版本。</summary>
public sealed record AgentHello(string DeviceId, string AgentVersion, int ProtocolVersion);

public interface IAgentHandshake
{
    /// <summary>连接端点并握手(带本机令牌)。连不上抛 IO / Socket 异常;被拒绝抛 <c>ProtocolException</c>(unauthorized / client_outdated …)。</summary>
    Task<AgentHello> ProbeAsync(string endpoint, string token, string clientVersion, CancellationToken ct);
}

/// <summary>「关窗后本机 Agent 继续运行」的开关(docs/design/07 §3「通用」)。</summary>
public interface ISidecarPolicy
{
    bool KeepAgentRunningAfterExit { get; }
}

/// <summary>Agent 输出的落地(诊断页读它)。</summary>
public interface ISidecarLog
{
    void Write(string line);
}

public sealed class NullSidecarLog : ISidecarLog
{
    public void Write(string line)
    {
    }
}
