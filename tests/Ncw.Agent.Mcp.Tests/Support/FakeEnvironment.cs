using System.IO.Pipelines;
using System.Text;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Mcp.Tests.Support;

/// <summary>
/// 假进程:stdin / stdout / stderr 是内存管道,服务端逻辑(<see cref="FakeMcpServer"/>)跑在测试进程里的任务上。
/// 关 stdin(EOF)= 守规矩的服务器自行退出;<see cref="KillAsync"/> = 被杀;<see cref="ExitNow"/> = 崩溃。
/// </summary>
public sealed class FakeProcess : IEnvProcess
{
    private readonly Pipe _stdin = new();
    private readonly Pipe _stdout = new();
    private readonly Pipe _stderr = new();
    private readonly TaskCompletionSource<ProcessExit> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stream _stdinWriter;
    private readonly Stream _stdoutWriter;
    private readonly Stream _stderrWriter;

    public FakeProcess(EnvironmentId environment, FakeMcpServer? server, bool exitOnEof = true)
    {
        EnvironmentId = environment;
        _stdinWriter = _stdin.Writer.AsStream();
        _stdoutWriter = _stdout.Writer.AsStream();
        _stderrWriter = _stderr.Writer.AsStream();
        Stdin = _stdinWriter;
        Stdout = _stdout.Reader.AsStream();
        Stderr = _stderr.Reader.AsStream();
        if (server is not null)
        {
            server.Outbound = WriteLineAsync;
            _ = Task.Run(() => ServeAsync(server, exitOnEof));
        }
    }

    public EnvironmentId EnvironmentId { get; }

    public int Generation => 0;

    public Stream? Stdin { get; }

    public Stream Stdout { get; }

    public Stream Stderr { get; }

    public Task<ProcessExit> Exited => _exited.Task;

    public bool Killed { get; private set; }

    public async Task WriteLineAsync(string line)
    {
        await _stdoutWriter.WriteAsync(Encoding.UTF8.GetBytes(line + "\n")).ConfigureAwait(false);
        await _stdoutWriter.FlushAsync().ConfigureAwait(false);
    }

    public async Task WriteRawAsync(string text)
    {
        await _stdoutWriter.WriteAsync(Encoding.UTF8.GetBytes(text)).ConfigureAwait(false);
        await _stdoutWriter.FlushAsync().ConfigureAwait(false);
    }

    public async Task WriteStderrAsync(string text)
    {
        await _stderrWriter.WriteAsync(Encoding.UTF8.GetBytes(text)).ConfigureAwait(false);
        await _stderrWriter.FlushAsync().ConfigureAwait(false);
    }

    private async Task ServeAsync(FakeMcpServer server, bool exitOnEof)
    {
        try
        {
            using var reader = new StreamReader(_stdin.Reader.AsStream(), Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Length > 0)
                {
                    await server.HandleAsync(line, WriteLineAsync).ConfigureAwait(false);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        if (exitOnEof)
        {
            ExitNow(0);
        }
    }

    /// <summary>进程自己退出(崩溃 / 正常结束):管道关掉,<see cref="Exited"/> 完成。</summary>
    public void ExitNow(int code)
    {
        _stdoutWriter.Dispose();
        _stderrWriter.Dispose();
        _exited.TrySetResult(new ProcessExit(code, null, ProcessExitReason.Exited));
    }

    public ValueTask KillAsync(CancellationToken ct = default)
    {
        Killed = true;
        _stdoutWriter.Dispose();
        _stderrWriter.Dispose();
        _exited.TrySetResult(new ProcessExit(null, 9, ProcessExitReason.Killed));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await KillAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// 测试用的执行环境:文件系统 / 路径 / 事实委托给一个真实的本机环境,<b>进程和 TCP 通道可替换</b>(假进程 + 假服务器、假 socket),并记下每次启动的命令。
/// <see cref="Kind"/> 可以伪装成 SSH(测「服务器在远程环境里」的路径)。
/// </summary>
public sealed class TestEnvironment(IExecutionEnvironment inner, EnvironmentKind? kind = null) : IExecutionEnvironment
{
    public List<CommandSpec> Spawned { get; } = [];

    public List<FakeProcess> Processes { get; } = [];

    /// <summary>每次 <see cref="OpenProcessAsync"/> 调用:返回假进程(或抛异常)。null = 走真实的本机进程。</summary>
    public Func<CommandSpec, FakeProcess?>? ProcessFactory { get; set; }

    /// <summary>任意的 <see cref="IEnvProcess"/> 实现(测传输对「写失败 / 退出」的时序处理,<see cref="FakeProcess"/> 的内存管道做不到断管)。</summary>
    public Func<CommandSpec, IEnvProcess?>? RawProcessFactory { get; set; }

    public Exception? StartFailure { get; set; }

    public Func<string, int, CancellationToken, Task<Stream>>? TcpFactory { get; set; }

    public EnvironmentId Id => inner.Id;

    public EnvironmentKind Kind { get; } = kind ?? inner.Kind;

    public string DisplayName => inner.DisplayName;

    public int Generation => inner.Generation;

    public EnvironmentState State => inner.State;

    public event EventHandler<EnvironmentStatusChangedEventArgs>? StatusChanged
    {
        add => inner.StatusChanged += value;
        remove => inner.StatusChanged -= value;
    }

    public EnvironmentFacts Facts => inner.Facts;

    public PathGuard Paths => inner.Paths;

    public IEnvironmentFs Fs => inner.Fs;

    public ValueTask EnsureReadyAsync(CancellationToken ct = default) => inner.EnsureReadyAsync(ct);

    public Task<EnvironmentFacts> ProbeToolsAsync(CancellationToken ct = default) => inner.ProbeToolsAsync(ct);

    public async Task<IEnvProcess> OpenProcessAsync(CommandSpec spec, CancellationToken ct = default)
    {
        lock (Spawned)
        {
            Spawned.Add(spec);
        }

        if (StartFailure is { } failure)
        {
            throw failure;
        }

        if (ProcessFactory?.Invoke(spec) is { } fake)
        {
            lock (Processes)
            {
                Processes.Add(fake);
            }

            return fake;
        }

        if (RawProcessFactory?.Invoke(spec) is { } raw)
        {
            return raw;
        }

        return await inner.OpenProcessAsync(spec, ct).ConfigureAwait(false);
    }

    public Task<ITerminal> OpenTerminalAsync(TerminalSpec spec, CancellationToken ct = default) => inner.OpenTerminalAsync(spec, ct);

    public Task<Stream> OpenTcpAsync(string host, int port, CancellationToken ct = default) =>
        TcpFactory is { } factory ? factory(host, port, ct) : inner.OpenTcpAsync(host, port, ct);

    public Task<IPortForward> OpenLocalForwardAsync(string host, int port, CancellationToken ct = default) => inner.OpenLocalForwardAsync(host, port, ct);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
