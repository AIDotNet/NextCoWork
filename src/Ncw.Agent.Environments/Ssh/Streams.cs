namespace Ncw.Agent.Environments.Ssh;

/// <summary>转发所有调用到内部流,子类只覆盖要改的部分。</summary>
internal abstract class ForwardingStream(Stream inner) : Stream
{
    protected Stream Inner { get; } = inner;

    public override bool CanRead => Inner.CanRead;

    public override bool CanSeek => Inner.CanSeek;

    public override bool CanWrite => Inner.CanWrite;

    public override long Length => Inner.Length;

    public override long Position
    {
        get => Inner.Position;
        set => Inner.Position = value;
    }

    public override void Flush() => Inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => Inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => Inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => Inner.Seek(offset, origin);

    public override void SetLength(long value) => Inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => Inner.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => Inner.WriteAsync(buffer, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => Inner.DisposeAsync();
}

/// <summary>
/// SFTP 文件流的包装:把 SSH.NET 的异常映射成 <see cref="EnvironmentException"/>(连接断开会让这一代的连接被标记为损坏),
/// 并在关闭写流(提交到远端)之后执行收尾动作(例如设置权限位)。
/// </summary>
internal sealed class SshFileStream(Stream inner, SshLink link, string path, bool mutating, Func<ValueTask>? afterClose = null) : ForwardingStream(inner)
{
    private bool _closed;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return await Inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not EnvironmentException)
        {
            throw Wrap(e);
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            await Inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not EnvironmentException)
        {
            throw Wrap(e);
        }
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not EnvironmentException)
        {
            throw Wrap(e);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        try
        {
            await Inner.DisposeAsync().ConfigureAwait(false);
            if (afterClose is not null)
            {
                await afterClose().ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException and not EnvironmentException)
        {
            throw Wrap(e);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    private EnvironmentException Wrap(Exception e)
    {
        if (SshErrors.IsConnectionLoss(e))
        {
            link.MarkBroken();
        }

        return SshErrors.Map(e, path, mutating);
    }
}

/// <summary>
/// 剥掉 stderr 开头的 <c>NCW-PID:&lt;n&gt;\n</c> 行(见 <see cref="RemoteCommands"/>),把 pid 交给终止逻辑,其余字节原样透传。
/// 开头不是这个标记(例如登录 shell 在包装脚本之前就失败了)时,已读到的字节全部当作普通输出。
/// </summary>
internal sealed class PidMarkerStream : Stream
{
    private const int MaxHeader = 64;
    private readonly Stream _inner;
    private readonly TaskCompletionSource<int?> _pid = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[] _pending = [];
    private int _pendingOffset;
    private bool _headerDone;

    public PidMarkerStream(Stream inner) => _inner = inner;

    /// <summary>pid;没有收到标记(或流在标记之前结束)时为 <c>null</c>。</summary>
    public Task<int?> Pid => _pid.Task;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_headerDone)
        {
            await ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_pendingOffset < _pending.Length)
        {
            var take = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
            _pending.AsMemory(_pendingOffset, take).CopyTo(buffer);
            _pendingOffset += take;
            return take;
        }

        return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pid.TrySetResult(null);
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private async ValueTask ReadHeaderAsync(CancellationToken ct)
    {
        var header = new List<byte>();
        var chunk = new byte[MaxHeader];
        try
        {
            for (;;)
            {
                var read = await _inner.ReadAsync(chunk, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                header.AddRange(chunk.AsSpan(0, read).ToArray());
                var newline = header.IndexOf((byte)'\n');
                if (newline >= 0)
                {
                    var line = System.Text.Encoding.ASCII.GetString([.. header.Take(newline)]);
                    if (line.StartsWith(RemoteCommands.PidMarker, StringComparison.Ordinal)
                        && int.TryParse(line.AsSpan(RemoteCommands.PidMarker.Length), out var pid))
                    {
                        _pid.TrySetResult(pid);
                        header.RemoveRange(0, newline + 1);
                    }

                    break;
                }

                if (header.Count >= MaxHeader)
                {
                    break;
                }
            }
        }
        finally
        {
            _headerDone = true;
            _pid.TrySetResult(null);
            _pending = [.. header];
            _pendingOffset = 0;
        }
    }
}
