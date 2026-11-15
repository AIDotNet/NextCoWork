namespace Ncw.Agent.Environments;

public enum OutputStreamKind
{
    Stdout,
    Stderr,
}

/// <summary>流式输出回调:每读到一块就调用一次(在读取线程上,不要阻塞)。回调抛出的异常被吞掉——订阅者的问题不是命令的问题。</summary>
public delegate void OutputSink(OutputStreamKind stream, ReadOnlySpan<byte> data);

public sealed record RunOptions
{
    /// <summary>超时后终止整棵进程树并返回 <see cref="ProcessOutcome.TimedOut"/>。<c>null</c> = 不超时。</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>stdout、stderr 各自使用的截断策略。默认保留尾部 512 KiB。</summary>
    public IOutputPolicy OutputPolicy { get; init; } = OutputPolicies.Default;

    /// <summary>启动后写入 stdin 并关闭。非空时自动使用 <see cref="StdinMode.Pipe"/>。</summary>
    public ReadOnlyMemory<byte> Input { get; init; }

    /// <summary>流式输出(完整、未截断的原始字节),给进度展示或把完整输出落临时文件用。</summary>
    public OutputSink? OnOutput { get; init; }

    /// <summary>进程退出后继续等管道读完的最长时间。后台子进程还攥着管道时不能让调用一直挂着。</summary>
    public TimeSpan DrainGrace { get; init; } = TimeSpan.FromSeconds(1);
}

public enum ProcessOutcome
{
    Exited,
    TimedOut,
    Cancelled,
    ConnectionLost,
}

/// <summary>
/// 一次性执行的结果。取消和超时<b>不抛异常</b>:进程树已被终止,已经读到的输出照常返回(工具要把它给模型看)。
/// </summary>
public sealed record ProcessResult(ProcessOutcome Outcome, int? ExitCode, int? Signal, OutputSnapshot Stdout, OutputSnapshot Stderr)
{
    public bool Succeeded => Outcome == ProcessOutcome.Exited && ExitCode == 0;
}

public static class ProcessRunner
{
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 在环境里执行一条命令,收集(按策略截断的)输出,支持流式回调、超时和取消。
    /// 启动失败抛 <see cref="EnvironmentException"/>;进程自己的失败体现在结果里。
    /// </summary>
    public static async Task<ProcessResult> RunAsync(this IExecutionEnvironment environment, CommandSpec spec, RunOptions? options = null, CancellationToken ct = default)
    {
        options ??= new RunOptions();
        if (options.Input.Length > 0 && spec.Stdin == StdinMode.Closed)
        {
            spec = spec with { Stdin = StdinMode.Pipe };
        }

        ct.ThrowIfCancellationRequested();
        var process = await environment.OpenProcessAsync(spec, ct).ConfigureAwait(false);
        await using (process.ConfigureAwait(false))
        {
            var stdout = options.OutputPolicy.CreateBuffer();
            var stderr = options.OutputPolicy.CreateBuffer();
            var readers = Task.WhenAll(
                Pump(process.Stdout, OutputStreamKind.Stdout, stdout, options.OnOutput),
                Pump(process.Stderr, OutputStreamKind.Stderr, stderr, options.OnOutput));
            if (process.Stdin is { } stdin)
            {
                _ = WriteInputAsync(stdin, options.Input);
            }

            var cancelled = new TaskCompletionSource();
            await using var registration = ct.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), cancelled).ConfigureAwait(false);
            using var timeoutCts = new CancellationTokenSource();
            var timedOut = options.Timeout is { } timeout && timeout > TimeSpan.Zero
                ? Task.Delay(timeout, timeoutCts.Token)
                : Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);

            var winner = await Task.WhenAny(process.Exited, timedOut, cancelled.Task).ConfigureAwait(false);
            var outcome = ProcessOutcome.Exited;
            if (winner == timedOut)
            {
                outcome = ProcessOutcome.TimedOut;
            }
            else if (winner == cancelled.Task)
            {
                outcome = ProcessOutcome.Cancelled;
            }

            if (outcome != ProcessOutcome.Exited)
            {
                await process.KillAsync(CancellationToken.None).ConfigureAwait(false);
            }

            ProcessExit exit;
            try
            {
                exit = await process.Exited.WaitAsync(KillWait).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                exit = new ProcessExit(null, null, ProcessExitReason.Killed);
            }

            await timeoutCts.CancelAsync().ConfigureAwait(false);

            // 进程已经没了,但后台子进程可能还攥着管道:给读取一个宽限期,超时就放弃(释放进程时流会被关掉)
            await Task.WhenAny(readers, Task.Delay(options.DrainGrace)).ConfigureAwait(false);

            if (outcome == ProcessOutcome.Exited && exit.Reason == ProcessExitReason.ConnectionLost)
            {
                outcome = ProcessOutcome.ConnectionLost;
            }

            return new ProcessResult(outcome, exit.ExitCode, exit.Signal, stdout.Snapshot(), stderr.Snapshot());
        }
    }

    private static async Task Pump(Stream stream, OutputStreamKind kind, IOutputBuffer buffer, OutputSink? sink)
    {
        var chunk = new byte[16 * 1024];
        try
        {
            for (;;)
            {
                var read = await stream.ReadAsync(chunk).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                // 超出预算后仍然消费数据,只是不再保留:不读的话管道写满,子进程会卡在 write 上永不退出
                buffer.Append(chunk.AsSpan(0, read));
                try
                {
                    sink?.Invoke(kind, chunk.AsSpan(0, read));
                }
                catch (Exception)
                {
                    // 订阅者的问题
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or EnvironmentException)
        {
            // 进程被杀或连接断开时流会以异常结束,不是调用方要处理的错误
        }
    }

    private static async Task WriteInputAsync(Stream stdin, ReadOnlyMemory<byte> input)
    {
        try
        {
            if (input.Length > 0)
            {
                await stdin.WriteAsync(input).ConfigureAwait(false);
                await stdin.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or EnvironmentException)
        {
            // 进程没读完就退出了(管道已关),不是错误
        }
        finally
        {
            try
            {
                await stdin.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or EnvironmentException)
            {
            }
        }
    }
}
