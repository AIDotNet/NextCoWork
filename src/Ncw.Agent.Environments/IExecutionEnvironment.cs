namespace Ncw.Agent.Environments;

public sealed record TerminalSpec(EnvPath? WorkingDirectory = null, int Columns = 80, int Rows = 24, IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>PTY 终端。<see cref="Stream"/> 是双向的:读到终端输出,写入键盘输入。</summary>
public interface ITerminal : IAsyncDisposable
{
    Stream Stream { get; }

    Task<ProcessExit> Exited { get; }

    ValueTask ResizeAsync(int columns, int rows, CancellationToken ct = default);

    ValueTask KillAsync(CancellationToken ct = default);
}

/// <summary>把环境里的一个 TCP 端口映射到设备的本机回环地址上(预览远程开发服务器用)。释放即关闭映射。</summary>
public interface IPortForward : IAsyncDisposable
{
    string LocalHost { get; }

    int LocalPort { get; }
}

/// <summary>
/// 执行环境(移植自 2.x 的 <c>environment/contract.ts</c>,06 §2)。
/// 一个项目 = (设备, 执行环境, 根目录);工具只通过这个接口访问文件和进程。
/// <para>
/// <b>连接代(Generation)</b>:断线重连后 +1。进程、流、监视器在发起时记下当时的代,连接中断后这些句柄一律失败,
/// 不会悄悄「复活」到新连接上;之后新发起的操作使用新的代。需要判断一个长期持有的引用是否过期时用 <see cref="EnvironmentExtensions.EnsureGeneration"/>。
/// </para>
/// </summary>
public interface IExecutionEnvironment : IAsyncDisposable
{
    EnvironmentId Id { get; }

    EnvironmentKind Kind { get; }

    /// <summary>给界面和审批卡片用的名字(例如 <c>dev-server</c>)。</summary>
    string DisplayName { get; }

    int Generation { get; }

    EnvironmentState State { get; }

    event EventHandler<EnvironmentStatusChangedEventArgs>? StatusChanged;

    /// <summary>连接成功之前(SSH 首次连接中)访问会抛 <see cref="EnvironmentErrorCode.Disconnected"/>;本机恒可用。</summary>
    EnvironmentFacts Facts { get; }

    /// <summary>路径规则与路径守卫,风格和 <see cref="Facts"/> 一致。</summary>
    PathGuard Paths { get; }

    IEnvironmentFs Fs { get; }

    /// <summary>确保已连接(SSH 首次连接,或等待进行中的重连)。本机立即返回。</summary>
    ValueTask EnsureReadyAsync(CancellationToken ct = default);

    /// <summary>探测环境里可用的工具(<c>git</c>、<c>rg</c> 等)并更新 <see cref="Facts"/>。</summary>
    Task<EnvironmentFacts> ProbeToolsAsync(CancellationToken ct = default);

    Task<IEnvProcess> OpenProcessAsync(CommandSpec spec, CancellationToken ct = default);

    /// <summary>PTY。本机的 PTY 由 Ncw.Agent.Terminal 提供(forkpty / ConPTY),没有注入时抛 <see cref="EnvironmentErrorCode.Unsupported"/>。</summary>
    Task<ITerminal> OpenTerminalAsync(TerminalSpec spec, CancellationToken ct = default);

    /// <summary>从环境内部发起一条 TCP 连接(SSH 是 direct-tcpip 通道)。</summary>
    Task<Stream> OpenTcpAsync(string host, int port, CancellationToken ct = default);

    /// <summary>把环境里的 <paramref name="host"/>:<paramref name="port"/> 映射到设备回环地址上的一个端口。</summary>
    Task<IPortForward> OpenLocalForwardAsync(string host, int port, CancellationToken ct = default);
}

public static class EnvironmentExtensions
{
    /// <summary>当前连接代和调用方记下的不一致时抛 <see cref="EnvironmentErrorCode.StaleGeneration"/>。</summary>
    public static void EnsureGeneration(this IExecutionEnvironment environment, int expected)
    {
        if (environment.Generation != expected)
        {
            throw new EnvironmentException(EnvironmentErrorCode.StaleGeneration, $"expected {expected}, current {environment.Generation}");
        }
    }
}
