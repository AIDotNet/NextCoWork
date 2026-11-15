using System.Threading.Channels;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Terminal;

/// <summary>
/// 本机 PTY 终端的公共部分:一条有界的输出通道(读线程写、<see cref="PtyStream"/> 读,通道满了读线程就停下,内核里的 PTY 缓冲随之写满、子进程自然被反压),
/// 和「进程已退出 + 输出已读完」才算结束的 <see cref="Exited"/>。
/// </summary>
internal abstract class PtyTerminal : ITerminal
{
    // 16 KiB 一块 × 64 块:最多在托管侧缓存 1 MiB,之后反压到子进程
    private readonly Channel<byte[]> _output = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait,
    });

    private readonly TaskCompletionSource<ProcessExit> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ProcessExit> _processExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _finishLock = new();
    private readonly CancellationTokenSource _disposed = new();
    private ProcessExit? _exitInfo;
    private bool _outputDone;
    private int _killRequested;
    private int _disposeStarted;

    protected PtyTerminal()
    {
        Stream = new PtyStream(this);
    }

    public Stream Stream { get; }

    /// <summary>
    /// 进程已退出,并且输出已被读完(最后的输出都交到了 <see cref="Stream"/> 里)才完成。消费者一直不读输出时,进程退出后最多再等
    /// <see cref="OutputDrainTimeout"/> 就完成(通道里已缓存的数据仍然可读,读到 EOF 为止)。
    /// </summary>
    public Task<ProcessExit> Exited => _exited.Task;

    /// <summary>只看进程:不管输出有没有被消费。终止时等的是它。</summary>
    protected Task<ProcessExit> ProcessExited => _processExited.Task;

    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(5);

    internal ChannelReader<byte[]> OutputReader => _output.Reader;

    protected CancellationToken DisposedToken => _disposed.Token;

    protected bool KillRequested => Volatile.Read(ref _killRequested) == 1;

    protected void MarkKillRequested() => Interlocked.Exchange(ref _killRequested, 1);

    /// <summary>读线程调用:把一块输出交给通道。通道满时阻塞(反压);终端被释放时放弃。</summary>
    protected void Publish(ReadOnlySpan<byte> data)
    {
        var copy = data.ToArray();
        try
        {
            if (!_output.Writer.TryWrite(copy))
            {
                _output.Writer.WriteAsync(copy, _disposed.Token).AsTask().GetAwaiter().GetResult();
            }
        }
        catch (OperationCanceledException)
        {
            // 终端已释放
        }
    }

    /// <summary>读线程调用:输出读完了(EOF)。</summary>
    protected void CompleteOutput()
    {
        _output.Writer.TryComplete();
        lock (_finishLock)
        {
            _outputDone = true;
        }

        TryFinish();
    }

    /// <summary>进程退出了(退出码已知)。</summary>
    protected void SetProcessExit(ProcessExit exit)
    {
        lock (_finishLock)
        {
            _exitInfo ??= exit;
        }

        _processExited.TrySetResult(exit);
        TryFinish();
        if (!_exited.Task.IsCompleted)
        {
            _ = Task.Delay(OutputDrainTimeout).ContinueWith(_ => _exited.TrySetResult(exit), TaskScheduler.Default);
        }
    }

    private void TryFinish()
    {
        ProcessExit exit;
        lock (_finishLock)
        {
            if (_exitInfo is not { } value || !_outputDone)
            {
                return;
            }

            exit = value;
        }

        _exited.TrySetResult(exit);
    }

    internal abstract ValueTask WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

    public abstract ValueTask ResizeAsync(int columns, int rows, CancellationToken ct = default);

    public abstract ValueTask KillAsync(CancellationToken ct = default);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) == 1)
        {
            return;
        }

        if (!_processExited.Task.IsCompleted)
        {
            try
            {
                await KillAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException or InvalidOperationException)
            {
                // 杀不掉就放弃,不让释放卡住
            }
        }

        _disposed.Cancel();
        _output.Writer.TryComplete();
        await ReleaseAsync().ConfigureAwait(false);
        _disposed.Dispose();
    }

    /// <summary>释放原生资源(进程已经结束或已放弃)。</summary>
    protected abstract ValueTask ReleaseAsync();

    protected static void ValidateSize(int columns, int rows)
    {
        if (columns is < 1 or > 32767 || rows is < 1 or > 32767)
        {
            throw new EnvironmentException(EnvironmentErrorCode.InvalidProfile, "terminal size must be 1..32767");
        }
    }
}

/// <summary>PTY 的双向字节流:读 = 终端输出,写 = 键盘输入。不可定位;并发读不被支持(同一时刻只有一个消费者)。</summary>
internal sealed class PtyStream(PtyTerminal owner) : Stream
{
    private byte[] _current = [];
    private int _offset;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        while (_offset >= _current.Length)
        {
            if (!await owner.OutputReader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            if (owner.OutputReader.TryRead(out var next))
            {
                _current = next;
                _offset = 0;
            }
        }

        var count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        var temp = new byte[buffer.Length];
        var read = ReadAsync(temp).AsTask().GetAwaiter().GetResult();
        temp.AsSpan(0, read).CopyTo(buffer);
        return read;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        owner.WriteInputAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        owner.WriteInputAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) =>
        owner.WriteInputAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Write(ReadOnlySpan<byte> buffer) =>
        owner.WriteInputAsync(buffer.ToArray(), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
