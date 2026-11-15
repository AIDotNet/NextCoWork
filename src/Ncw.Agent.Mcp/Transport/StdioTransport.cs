using System.Text;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Loop;

namespace Ncw.Agent.Mcp;

/// <summary>
/// stdio 传输:<b>经 <see cref="IExecutionEnvironment.OpenProcessAsync"/> 启动进程</b>(AGENTS.md §5:服务器可以在设备本机或项目的 SSH 环境里跑),
/// 换行分隔的 JSON;stderr 单独读取、只留尾部(诊断,从不当成协议输出、也从不进工具结果)。
/// 关闭:先关 stdin 给服务器一个干净的退出机会(MCP stdio 的约定收尾信号;2.x 实测过,只杀本机一侧不会让远端进程退出),宽限期后杀进程树。
/// </summary>
public sealed class StdioTransport(IExecutionEnvironment environment, CommandSpec command, McpOptions options, ILoopDiagnostics? log = null) : IMcpTransport
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly byte[] _stderrTail = new byte[options.StderrTailBytes];
    private int _stderrLength;
    private readonly Lock _stderrGate = new();
    private IEnvProcess? _process;
    private Task _readers = Task.CompletedTask;
    private int _closing;

    public Func<ReadOnlyMemory<byte>, ValueTask>? Received { get; set; }

    public Task Completion => _completion.Task;

    /// <summary>进程退出信息(退出后才有)。</summary>
    public ProcessExit? Exit { get; private set; }

    public string? Diagnostics
    {
        get
        {
            lock (_stderrGate)
            {
                return _stderrLength == 0 ? null : Encoding.UTF8.GetString(_stderrTail, 0, _stderrLength);
            }
        }
    }

    public void OnInitialized(string protocolVersion)
    {
    }

    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            _process = await environment.OpenProcessAsync(command with { Stdin = StdinMode.Pipe }, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.ProcessStartFailed or EnvironmentErrorCode.NotFound)
        {
            throw new McpException(McpErrorKeys.CommandNotFound, $"The command could not be started: {e.Message}", inner: e);
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.Disconnected or EnvironmentErrorCode.ConnectionFailed or EnvironmentErrorCode.StaleGeneration)
        {
            throw new McpException(McpErrorKeys.EnvironmentUnavailable, $"The execution environment is not reachable ({e.Code}).", inner: e);
        }
        catch (EnvironmentException e)
        {
            throw new McpException(McpErrorKeys.StartFailed, $"The command could not be started ({e.Code}): {e.Message}", inner: e);
        }

        var process = _process;
        _readers = Task.WhenAll(PumpStdoutAsync(process), PumpStderrAsync(process));
        _ = WatchAsync(process);
    }

    private async Task WatchAsync(IEnvProcess process)
    {
        ProcessExit exit;
        try
        {
            exit = await process.Exited.ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            exit = new ProcessExit(null, null, ProcessExitReason.ConnectionLost);
        }

        Exit = exit;
        // 让读取循环把管道里剩下的消息读完再报关闭(进程退出前写的最后一条响应不能丢)
        await Task.WhenAny(_readers, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        if (Volatile.Read(ref _closing) != 0)
        {
            _completion.TrySetResult();
            return;
        }

        var parameters = new Dictionary<string, string>();
        if (exit.ExitCode is { } code)
        {
            parameters["exitCode"] = code.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (exit.Reason == ProcessExitReason.ConnectionLost)
        {
            _completion.TrySetException(new McpException(McpErrorKeys.EnvironmentUnavailable, "The connection to the execution environment was lost; the server's state is unknown.", parameters));
        }
        else
        {
            _completion.TrySetException(new McpException(McpErrorKeys.ProcessExited, $"The MCP server process exited{(exit.ExitCode is { } c ? $" with code {c}" : "")}.", parameters));
        }
    }

    private async Task PumpStdoutAsync(IEnvProcess process)
    {
        var chunk = new byte[16 * 1024];
        var line = new ArrayBufferWriter();
        try
        {
            for (;;)
            {
                var read = await process.Stdout.ReadAsync(chunk).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                var start = 0;
                for (var i = 0; i < read; i++)
                {
                    if (chunk[i] != (byte)'\n')
                    {
                        continue;
                    }

                    line.Append(chunk.AsSpan(start, i - start));
                    start = i + 1;
                    await DeliverAsync(line).ConfigureAwait(false);
                }

                line.Append(chunk.AsSpan(start, read - start));
                if (line.Length > options.MaxMessageBytes)
                {
                    Fail(McpException.With(McpErrorKeys.Protocol, "A line of MCP server output exceeded the size limit.", ("reason", "lineTooLarge")));
                    return;
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or EnvironmentException)
        {
            // 进程被杀或连接断开时流会以异常结束:由 WatchAsync 报告关闭原因
        }
    }

    private async ValueTask DeliverAsync(ArrayBufferWriter line)
    {
        var span = line.Written;
        if (span.Length > 0 && span[^1] == (byte)'\r')
        {
            span = span[..^1];
        }

        if (span.Length > 0 && Received is { } received)
        {
            try
            {
                await received(span.ToArray()).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log?.Warn("An MCP message handler failed.", e);
            }
        }

        line.Clear();
    }

    private async Task PumpStderrAsync(IEnvProcess process)
    {
        var chunk = new byte[4096];
        try
        {
            for (;;)
            {
                var read = await process.Stderr.ReadAsync(chunk).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                // 只留尾部;读完后仍持续消费,不让管道写满卡住服务器
                lock (_stderrGate)
                {
                    var capacity = _stderrTail.Length;
                    if (read >= capacity)
                    {
                        Buffer.BlockCopy(chunk, read - capacity, _stderrTail, 0, capacity);
                        _stderrLength = capacity;
                    }
                    else
                    {
                        var overflow = Math.Max(0, _stderrLength + read - capacity);
                        if (overflow > 0)
                        {
                            Buffer.BlockCopy(_stderrTail, overflow, _stderrTail, 0, _stderrLength - overflow);
                            _stderrLength -= overflow;
                        }

                        Buffer.BlockCopy(chunk, 0, _stderrTail, _stderrLength, read);
                        _stderrLength += read;
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or EnvironmentException)
        {
        }
    }

    private void Fail(McpException error)
    {
        _completion.TrySetException(error);
        if (_process is { } process)
        {
            _ = process.KillAsync(CancellationToken.None).AsTask();
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        if (_process?.Stdin is not { } stdin || _completion.Task.IsCompleted)
        {
            throw McpException.ClosedBecause(_completion.Task);
        }

        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stdin.WriteAsync(message, ct).ConfigureAwait(false);
            await stdin.WriteAsync("\n"u8.ToArray(), ct).ConfigureAwait(false);
            await stdin.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or EnvironmentException)
        {
            // ★ 写失败(管道已断)几乎总是因为服务器进程已经退出了——它的退出码和 stderr 尾部才是 owner 要的根因,由 WatchAsync 在进程退出后报告。
            // 写失败可能比它更早到(进程一启动就退出时尤其如此:整程序集并发、CPU 吃紧时稳定出现),这时直接报「连接已关闭」就把根因丢了,
            // 同一个坏服务器有时报 processExited(带退出码)、有时报 closed。所以有界地等一下进程退出的定案(最多 ExitAttribution),拿到就按它报,拿不到才是通用的「已关闭」。
            try
            {
                await Task.WhenAny(_completion.Task, Task.Delay(ExitAttribution, ct)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 调用方取消了:不再等,按已知的原因报
            }

            var because = McpException.ClosedBecause(_completion.Task);
            throw because.Key == McpErrorKeys.Closed ? new McpException(McpErrorKeys.Closed, "The MCP server connection is closed.", inner: e) : because;
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>写失败后等进程退出定案的上限(见 <see cref="SendAsync"/>)。</summary>
    private static readonly TimeSpan ExitAttribution = TimeSpan.FromSeconds(2);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
        {
            return;
        }

        if (_process is { } process)
        {
            try
            {
                if (process.Stdin is { } stdin)
                {
                    await stdin.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or EnvironmentException)
            {
                // 通道可能已经断了
            }

            await Task.WhenAny(process.Exited, Task.Delay(options.GracefulExit)).ConfigureAwait(false);
            try
            {
                await process.KillAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is EnvironmentException or ObjectDisposedException)
            {
            }

            await Task.WhenAny(_readers, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
            await process.DisposeAsync().ConfigureAwait(false);
        }

        _completion.TrySetResult();
    }

    /// <summary>可增长的字节缓冲(一行一清)。</summary>
    private sealed class ArrayBufferWriter
    {
        private byte[] _buffer = new byte[1024];

        public int Length { get; private set; }

        public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, Length);

        public void Append(ReadOnlySpan<byte> bytes)
        {
            if (Length + bytes.Length > _buffer.Length)
            {
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, Length + bytes.Length));
            }

            bytes.CopyTo(_buffer.AsSpan(Length));
            Length += bytes.Length;
        }

        public void Clear() => Length = 0;
    }
}
