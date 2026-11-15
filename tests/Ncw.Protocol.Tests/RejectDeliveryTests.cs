using System.Net.Sockets;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Security;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>
/// 「拒绝送达」(docs/design/09 §7 / §9.2,docs/parity.md「并发不稳定测试的根因与修复」):设备在 hello 阶段决定拒绝之后,
/// 控制端读到的必须是明确的拒绝码,不能因为设备关闭连接时对端还有未读数据、TCP 发 RST 把在途的拒绝帧丢掉而看到「连接丢失」(DeviceClient 会把它当可重试的断线)。
/// 确定性做法:测试里的 <see cref="TcpLikePipe"/> 精确模拟 TCP 的这条规则(关闭时本端有未读数据 → 对端的接收缓冲被清空、下一次读抛 IOException),
/// 不靠 sleep 或系统负载;局域网那一条另外用真实 TCP + TLS 验证「释放连接时没有未读数据」。
/// </summary>
public sealed class RejectDeliveryTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private static readonly PeerIdentity Device = PeerIdentity.Create("device");
    private static readonly PeerIdentity Controller = PeerIdentity.Create("controller");

    // ---- 工具 ---------------------------------------------------------------------------------------------------

    private static HelloFrame Hello(int min = ProtocolInfo.MinVersion, int max = ProtocolInfo.Version) =>
        new(min, max, "controller", "3.0.0", "desktop", [], 16, null, null);

    /// <summary>四种 hello 阶段的拒绝路径:各自要发的第一帧,以及设备应给的拒绝码。</summary>
    private static (Frame First, string Code) Case(string kind) => kind switch
    {
        "unauthorized" => (Hello(), ErrorCodes.Unauthorized), // 设备的 Authenticate 返回 null(未授权 / 令牌绑定不符)
        "client_outdated" => (Hello(max: ProtocolInfo.MinVersion - 1, min: ProtocolInfo.MinVersion - 1), ErrorCodes.ClientOutdated),
        "unsupported" => (Hello(min: ProtocolInfo.Version + 1, max: ProtocolInfo.Version + 1), ErrorCodes.Unsupported),
        "expected_hello" => (new PingFrame(1), ErrorCodes.InvalidRequest),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static ServerSession NewSession(Stream stream, RejectLinger? linger, Func<HelloFrame, SessionPeer?, Role?>? authenticate = null) =>
        new(new FrameStream(stream), new MethodRouter(), new TopicHub(),
            new ServerOptions("dev-1", "3.0.0-test", Transports.Local, authenticate ?? ((_, _) => null), Linger: linger));

    private static byte[] Junk(int length) => [.. Enumerable.Repeat((byte)0x5a, length)];

    // ---- 确定性回归:关闭时有未读数据 → 对端丢掉在途的拒绝帧 ----------------------------------------------------------

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("client_outdated")]
    [InlineData("unsupported")]
    [InlineData("expected_hello")]
    public async Task Every_hello_rejection_reaches_a_controller_that_still_has_data_in_flight(string kind)
    {
        var (client, server) = TcpLikePipe.Create();
        var frames = new FrameStream(client);
        var (first, code) = Case(kind);
        await frames.WriteAsync(first);
        await client.WriteAsync(Junk(256)); // 控制端流水线发出、设备还没读的数据(例如紧跟 hello 的请求)

        using var cts = new CancellationTokenSource(Wait);
        var linger = new RejectLinger();
        var run = NewSession(server, linger).RunAsync(cts.Token);
        // 控制端晚一点才读:先等设备把它要做的都做完——排空中(新行为)或者已经释放连接(旧行为,此刻拒绝帧已经被 RST 丢掉)。这样结果不取决于调度先后。
        await UntilAsync(() => run.IsCompleted || linger.Active == 1);
        var reply = await frames.ReadAsync(cts.Token);

        Assert.Equal(code, Assert.IsType<RejectFrame>(reply).Code);
        client.Dispose(); // 控制端读到拒绝后关闭:设备据此结束排空并释放
        await run.WaitAsync(Wait);
    }

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("client_outdated")]
    [InlineData("unsupported")]
    [InlineData("expected_hello")]
    public async Task Without_the_linger_the_same_rejection_is_lost_to_a_reset(string kind)
    {
        // 对照:关掉排空(时限 0 = 改动之前的行为),同样的时序下控制端读到的是「连接被重置」,不是拒绝——证明上面的模拟确实复现了这个缺陷。
        var (client, server) = TcpLikePipe.Create();
        var frames = new FrameStream(client);
        await frames.WriteAsync(Case(kind).First);
        await client.WriteAsync(Junk(256));

        using var cts = new CancellationTokenSource(Wait);
        await NewSession(server, new RejectLinger(TimeSpan.Zero)).RunAsync(cts.Token);

        var lost = await Assert.ThrowsAsync<IOException>(() => frames.ReadAsync(cts.Token).AsTask());
        Assert.Contains("reset", lost.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_device_client_gets_a_protocol_exception_not_a_lost_connection()
    {
        // 真实的 DeviceClient:hello 写出后紧跟着又写了数据(例如排队的请求)。设备拒绝,客户端必须得到 ProtocolException(Unauthorized):
        // DeviceClient 的上层(DeviceLink)对它标 Revoked、不再重试;对 IOException 则会无限重连。
        var (client, server) = TcpLikePipe.Create();
        using var cts = new CancellationTokenSource(Wait);
        var linger = new RejectLinger();
        using var junkSent = new ManualResetEventSlim();
        // 设备在「紧跟 hello 的数据已经到了」之后才拒绝
        var serverRun = NewSession(server, linger, (_, _) =>
        {
            Assert.True(junkSent.Wait(Wait));
            return null;
        }).RunAsync(cts.Token);
        // 客户端晚一点才读(同上):等设备排空中或已经释放
        var clientSide = new TrailingJunkStream(client, afterWrites: 2, Junk(128), beforeRead: () => UntilAsync(() => serverRun.IsCompleted || linger.Active == 1), onJunkSent: junkSent.Set); // FrameStream 写一帧 = 头 + 体两次写
        var rejected = await Assert.ThrowsAsync<ProtocolException>(() => DeviceClient.ConnectAsync(clientSide, new ClientHello("controller", "3.0.0"), cts.Token));

        Assert.Equal(ErrorCodes.Unauthorized, rejected.Code);
        await serverRun.WaitAsync(Wait);
    }

    // ---- 局域网:真实 TCP + TLS ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("client_outdated")]
    public async Task Over_real_tcp_and_tls_the_device_only_releases_the_connection_after_the_peer_has_closed(string kind)
    {
        using var listener = LanTransport.Listen(0, lanOnly: false);
        using var cts = new CancellationTokenSource(Wait);
        UnreadProbe? probe = null;

        var served = Task.Run(async () =>
        {
            await foreach (var (stream, remote) in listener.AcceptAsync(cts.Token))
            {
                probe = new UnreadProbe((NetworkStream)stream);
                var channel = await SecureChannel.AcceptAsync(probe, Device, cts.Token);
                var peer = new SessionPeer(channel.PeerFingerprint, channel.LocalFingerprint, channel.Exporter, remote);
                var session = new ServerSession(new FrameStream(channel.Stream), new MethodRouter(), new TopicHub(),
                    new ServerOptions("dev-1", "3.0.0-test", Transports.Remote, (_, _) =>
                    {
                        // 等到控制端紧跟 hello 发出的数据已经到了本机的接收缓冲(此刻设备还没读它),再拒绝
                        Assert.True(SpinWait.SpinUntil(() => probe!.DataAvailable, Wait), "the in-flight data never arrived");
                        return null;
                    }), peer);
                await session.RunAsync(cts.Token);
                await channel.DisposeAsync();
                return;
            }
        });

        var (first, code) = Case(kind);
        var transport = await LanTransport.ConnectAsync("127.0.0.1", listener.Port, cts.Token);
        await using var channelClient = await SecureChannel.ConnectAsync(transport, Controller, Device.Fingerprint, cts.Token);
        var frames = new FrameStream(channelClient.Stream);
        await frames.WriteAsync(first, cts.Token);
        await channelClient.Stream.WriteAsync(Junk(200), cts.Token);

        var reply = await frames.ReadAsync(cts.Token);
        Assert.Equal(code, Assert.IsType<RejectFrame>(reply).Code);

        await channelClient.DisposeAsync(); // 控制端读到拒绝后关闭
        await served.WaitAsync(Wait);
        Assert.Equal(0, probe!.UnreadAtDispose); // 设备释放 TCP 连接时,控制端的数据已经读完、对端已关闭:不会有 RST
    }

    // ---- 拒绝服务边界 -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_peer_that_keeps_writing_is_cut_off_at_the_byte_limit()
    {
        var linger = new RejectLinger(maxBytes: 1024);
        var (client, server) = TcpLikePipe.Create();
        await client.WriteAsync(Junk(8192));
        Assert.Equal(LingerOutcome.ByteLimit, await linger.DrainAsync(server, CancellationToken.None));
        Assert.Equal(0, linger.Active);

        // 整条会话:控制端 hello 之后疯狂写、从不关闭,设备在上限处中止(RunAsync 结束,连接被释放),不会一直读下去。
        var (client2, server2) = TcpLikePipe.Create();
        var frames = new FrameStream(client2);
        await frames.WriteAsync(Hello());
        await client2.WriteAsync(Junk(8192));
        using var cts = new CancellationTokenSource(Wait);
        await NewSession(server2, linger).RunAsync(cts.Token).WaitAsync(Wait);
        Assert.Equal(0, linger.Active);
    }

    [Fact]
    public async Task A_peer_that_never_closes_is_cut_off_by_the_deadline()
    {
        var clock = new ManualTimeProvider();
        var linger = new RejectLinger(TimeSpan.FromSeconds(2), clock: clock);
        var (client, server) = TcpLikePipe.Create();
        var drain = linger.DrainAsync(server, CancellationToken.None).AsTask();

        await UntilAsync(() => linger.Active == 1);
        Assert.False(drain.IsCompleted); // 对端沉默、不关闭:一直等着
        clock.Advance(TimeSpan.FromSeconds(1.9));
        Assert.False(drain.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal(LingerOutcome.TimedOut, await drain.WaitAsync(Wait));
        Assert.Equal(0, linger.Active);
        client.Dispose();

        // 整条会话也一样
        var (client2, server2) = TcpLikePipe.Create();
        await new FrameStream(client2).WriteAsync(Hello());
        var run = NewSession(server2, linger).RunAsync(CancellationToken.None);
        await UntilAsync(() => linger.Active == 1);
        clock.Advance(TimeSpan.FromSeconds(3));
        await run.WaitAsync(Wait);
        Assert.Equal(0, linger.Active);
    }

    [Fact]
    public async Task Rejected_connections_past_the_concurrency_cap_are_aborted_at_once()
    {
        var linger = new RejectLinger(maxConcurrent: 1);
        var (clientA, serverA) = TcpLikePipe.Create();
        var first = linger.DrainAsync(serverA, CancellationToken.None).AsTask();
        await UntilAsync(() => linger.Active == 1);

        var (clientB, serverB) = TcpLikePipe.Create();
        Assert.Equal(LingerOutcome.Busy, await linger.DrainAsync(serverB, CancellationToken.None)); // 超限:不排空,立刻返回(调用方随即中止)
        Assert.Equal(1, linger.Active);

        // 整条会话:超限的被拒连接在发出拒绝帧之后立即被释放(和改动之前一样),不等对端
        var (clientC, serverC) = TcpLikePipe.Create();
        var framesC = new FrameStream(clientC);
        await framesC.WriteAsync(Hello());
        await NewSession(serverC, linger).RunAsync(CancellationToken.None).WaitAsync(Wait);

        clientA.Dispose(); // 第一条的对端关闭:名额释放,之后的被拒连接又能排空
        Assert.Equal(LingerOutcome.PeerClosed, await first.WaitAsync(Wait));
        Assert.Equal(0, linger.Active);
        var (clientD, serverD) = TcpLikePipe.Create();
        var again = linger.DrainAsync(serverD, CancellationToken.None).AsTask();
        await UntilAsync(() => linger.Active == 1);
        clientD.Dispose();
        Assert.Equal(LingerOutcome.PeerClosed, await again.WaitAsync(Wait));
        clientB.Dispose();
    }

    [Fact]
    public async Task A_session_cancelled_while_lingering_ends_without_waiting_for_the_peer()
    {
        var linger = new RejectLinger();
        var (client, server) = TcpLikePipe.Create();
        await new FrameStream(client).WriteAsync(Hello());
        using var cts = new CancellationTokenSource();
        var run = NewSession(server, linger).RunAsync(cts.Token);
        await UntilAsync(() => linger.Active == 1);
        await cts.CancelAsync();
        try
        {
            await run.WaitAsync(Wait);
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Equal(0, linger.Active);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var until = Environment.TickCount64 + 10_000;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < until, "condition not reached");
            await Task.Yield();
        }
    }

    // ---- 测试替身 -----------------------------------------------------------------------------------------------------

    /// <summary>在 <see cref="NetworkStream"/> 外面包一层:释放时记下本机接收缓冲里还有没有没读的数据(内核据此决定 FIN 还是 RST)。</summary>
    private sealed class UnreadProbe(NetworkStream inner) : Stream
    {
        public int UnreadAtDispose { get; private set; } = -1;

        public bool DataAvailable => inner.DataAvailable;

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && UnreadAtDispose < 0)
            {
                try
                {
                    UnreadAtDispose = inner.DataAvailable ? 1 : 0;
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    UnreadAtDispose = 0;
                }

                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>第 <c>afterWrites</c> 次写完之后紧跟着再写一段数据(模拟 hello 之后流水线发出的数据)。</summary>
    private sealed class TrailingJunkStream(Stream inner, int afterWrites, byte[]? junk = null, Func<Task>? beforeRead = null, Action? onJunkSent = null) : Stream
    {
        private int _writes;

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (beforeRead is not null)
            {
                await beforeRead();
            }

            return await inner.ReadAsync(buffer, ct);
        }

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await inner.WriteAsync(buffer, ct);
            if (junk is not null && Interlocked.Increment(ref _writes) == afterWrites)
            {
                await inner.WriteAsync(junk, ct);
                onJunkSent?.Invoke();
            }
        }

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// 内存里的一对双向字节流,精确模拟 TCP 在「关闭」上的这条规则:一端关闭时,如果它自己的接收缓冲里还有没读的数据,内核发 RST——
/// 对端接收缓冲里还没被读走的数据被丢弃,对端接下来的读抛 <see cref="IOException"/>(Connection reset by peer);
/// 没有未读数据则发 FIN,对端先读完已到的数据、再读到 0。
/// </summary>
internal static class TcpLikePipe
{
    public static (Stream A, Stream B) Create()
    {
        var a = new End();
        var b = new End();
        a.Peer = b;
        b.Peer = a;
        return (a, b);
    }

    private sealed class End : Stream
    {
        private readonly object _gate = new();
        private readonly Queue<byte[]> _chunks = new();
        private int _offset;
        private TaskCompletionSource _signal = NewSignal();
        private bool _fin;
        private bool _reset;
        private bool _closed;

        public End Peer { get; set; } = null!;

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void Signal()
        {
            var signal = _signal;
            _signal = NewSignal();
            signal.TrySetResult();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct = default)
        {
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_closed, this);
                    if (_reset)
                    {
                        throw new IOException("Connection reset by peer");
                    }

                    if (_chunks.Count > 0)
                    {
                        var chunk = _chunks.Peek();
                        var n = Math.Min(destination.Length, chunk.Length - _offset);
                        chunk.AsMemory(_offset, n).CopyTo(destination);
                        _offset += n;
                        if (_offset == chunk.Length)
                        {
                            _chunks.Dequeue();
                            _offset = 0;
                        }

                        return n;
                    }

                    if (_fin)
                    {
                        return 0;
                    }

                    wait = _signal.Task;
                }

                await wait.WaitAsync(ct).ConfigureAwait(false);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
            }

            Peer.Deliver(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        private void Deliver(byte[] data)
        {
            lock (_gate)
            {
                if (_closed || _reset)
                {
                    return; // 对端已经关了 / 重置了:数据丢弃(真实 TCP 里这会招来 RST,这里不需要)
                }

                _chunks.Enqueue(data);
                Signal();
            }
        }

        private void Fin()
        {
            lock (_gate)
            {
                _fin = true;
                Signal();
            }
        }

        private void Reset()
        {
            lock (_gate)
            {
                _reset = true;
                _chunks.Clear();
                Signal();
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            bool unread;
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
                unread = _chunks.Count > 0;
                Signal();
            }

            if (unread)
            {
                Peer.Reset();
            }
            else
            {
                Peer.Fin();
            }
        }
    }
}

/// <summary>手动拨动的时钟,支持定时器(<see cref="CancellationTokenSource"/> 的时限走它)。</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _now; // 毫秒

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => Volatile.Read(ref _now);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += (long)by.TotalMilliseconds;
            due = [.. _timers.Where(t => t.IsDue(_now))];
        }

        foreach (var timer in due)
        {
            timer.Fire(Volatile.Read(ref _now));
        }
    }

    private long Now => Volatile.Read(ref _now);

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private long _dueAt = -1;
        private long _period = -1;

        public bool IsDue(long now) => _dueAt >= 0 && now >= _dueAt;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? -1 : owner.Now + (long)dueTime.TotalMilliseconds;
                _period = period == Timeout.InfiniteTimeSpan ? -1 : (long)period.TotalMilliseconds;
            }

            return true;
        }

        public void Fire(long now)
        {
            lock (owner._gate)
            {
                if (!IsDue(now))
                {
                    return;
                }

                _dueAt = _period > 0 ? now + _period : -1;
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                _dueAt = -1;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
