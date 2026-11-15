using System.Net.WebSockets;

namespace Ncw.Protocol.Transport;

/// <summary>
/// 把一条已经「就绪」的 WebSocket 当成字节流(中继承载):每次写 = 一条二进制消息(切成不超过 <see cref="RelayProtocol.SendChunkBytes"/>),
/// 读时忽略消息边界。上面跑的是 TLS,所以这里不关心内容。
/// BouncyCastle 的 TLS 用同步的 <c>Read</c> / <c>Write</c>(见 <see cref="Security.SecureChannel"/>):这里同步方法在异步实现上等待,
/// 和 <see cref="System.Net.Sockets.NetworkStream"/> 阻塞一个线程是同一个代价。底层错误一律转成 <see cref="IOException"/>,这样握手 / 读循环的既有处理照常工作。
/// </summary>
public sealed class WebSocketStream : Stream
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly byte[] _buffer = new byte[RelayProtocol.MaxPipeMessageBytes];
    private int _bufferStart;
    private int _bufferEnd;
    private int _disposed;
    private int _reading;
    private bool _eof;

    public WebSocketStream(WebSocket socket) { _socket = socket; _id = Interlocked.Increment(ref s_next); }
    private static int s_next;
    private readonly int _id;

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override int Read(Span<byte> buffer)
    {
        var array = new byte[buffer.Length];
        var n = Read(array, 0, array.Length);
        array.AsSpan(0, n).CopyTo(buffer);
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Write(ReadOnlySpan<byte> buffer) => Write(buffer.ToArray(), 0, buffer.Length);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct = default)
    {
        if (destination.Length == 0)
        {
            return 0;
        }

        while (_bufferStart == _bufferEnd)
        {
            if (_eof)
            {
                return 0;
            }

            ValueWebSocketReceiveResult result;
            Volatile.Write(ref _reading, 1);
            try
            {
                result = await _socket.ReceiveAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                // 对端没有走关闭握手就断了(或者本地已经释放):对上层来说就是连接断了。
                _eof = true;
                throw new IOException("The relay connection was lost", e);
            }
            finally
            {
                Volatile.Write(ref _reading, 0);
            }

            switch (result.MessageType)
            {
                case WebSocketMessageType.Close:
                    _eof = true;
                    return 0;
                case WebSocketMessageType.Text:
                    throw new IOException("Unexpected text message on a relayed connection");
            }

            if (!result.EndOfMessage)
            {
                throw new IOException("A relayed message exceeded the size limit"); // 中继和对端都不会发这么大的消息
            }

            _bufferStart = 0;
            _bufferEnd = result.Count;
        }

        var n = Math.Min(destination.Length, _bufferEnd - _bufferStart);
        _buffer.AsMemory(_bufferStart, n).CopyTo(destination);
        _bufferStart += n;
        return n;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (!source.IsEmpty)
            {
                var chunk = source[..Math.Min(source.Length, RelayProtocol.SendChunkBytes)];
                await _socket.SendAsync(chunk, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
                source = source[chunk.Length..];
            }
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            throw new IOException("The relay connection was lost", e);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            CloseGracefully();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// 关闭:先发 WebSocket 关闭帧(让此前写出的数据先送达,对端读到 EOF),再等对端的关闭帧,最多 <see cref="CloseGrace"/>,之后中止。
    /// 直接中止会让 TCP 以 RST 收尾,在途的数据(例如设备发出的「拒绝」帧)可能被对端丢弃。有读正挂着的话让它自己收到对端的关闭帧,不另起读。
    /// </summary>
    private void CloseGracefully()
    {
        _ = Task.Run(async () =>
        {
            using var grace = new CancellationTokenSource(CloseGrace);
            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, grace.Token).ConfigureAwait(false);
                }

                if (Volatile.Read(ref _reading) == 0)
                {
                    var buffer = new byte[256];
                    while (_socket.State is WebSocketState.Open or WebSocketState.CloseSent)
                    {
                        if ((await _socket.ReceiveAsync(buffer, grace.Token).ConfigureAwait(false)).MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }
                    }
                }
                else
                {
                    await Task.Delay(CloseGrace, grace.Token).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
            {
            }
            finally
            {
                try
                {
                    _socket.Abort();
                }
                catch (ObjectDisposedException)
                {
                }

                _socket.Dispose();
            }
        });
    }

    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(2);
}
