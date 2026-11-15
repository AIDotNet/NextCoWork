namespace Ncw.Protocol.Session;

/// <summary>排空的结局(测试和诊断用)。</summary>
public enum LingerOutcome
{
    /// <summary>对端先关闭了(正常:它读到了拒绝帧)。此后释放连接不会再有 RST。</summary>
    PeerClosed,

    /// <summary>对端一直在写,读掉的字节超过了上限:中止。</summary>
    ByteLimit,

    /// <summary>对端既不关闭也不再写,到时限了:中止。</summary>
    TimedOut,

    /// <summary>同时在排空的被拒连接已满:不排空,直接中止(没有拒绝服务面)。</summary>
    Busy,

    /// <summary>关闭了排空(时限 0),或会话自己被取消。</summary>
    Skipped,
}

/// <summary>
/// 「拒绝送达」(lingering close,docs/design/09 §7 / §9.2):设备在 hello 阶段决定拒绝(未授权、客户端过旧、协议不兼容、令牌绑定不符……)之后,
/// <b>不能立刻关闭连接</b>:关闭一条还有对端未读 / 在途数据的 TCP 连接会让内核发 RST,而 RST 会把还没被对端读走的拒绝帧丢掉——
/// 控制端看到的是「连接丢失」(可重试的断线,不停重连),不是明确的「未授权 / 已吊销」(<c>DeviceClient</c> 据此标 Revoked、不再重试)。
/// 做法:发完拒绝帧后只读、丢弃对端继续发来的数据,直到对端自己关闭(它读到拒绝帧就会关)——这时再释放不会有 RST。
/// <para>
/// 不引入拒绝服务面:每条被拒连接最多排空 <see cref="MaxBytes"/> 字节、最多 <see cref="Timeout"/>,同时排空的连接最多 <see cref="MaxConcurrent"/> 条,
/// 超限一律中止(等同于改动之前的行为)。时钟可注入(测试用)。线上格式不变:这只是关闭时机。
/// </para>
/// </summary>
public sealed class RejectLinger
{
    /// <summary>默认策略(进程内共享,并发上限也是进程内共享的)。</summary>
    public static RejectLinger Default { get; } = new();

    private readonly TimeProvider _clock;
    private int _active;

    public RejectLinger(TimeSpan? timeout = null, int maxBytes = 64 * 1024, int maxConcurrent = 32, TimeProvider? clock = null)
    {
        Timeout = timeout ?? TimeSpan.FromSeconds(2);
        MaxBytes = maxBytes;
        MaxConcurrent = maxConcurrent;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>排空的时限(≤ 0 = 不排空,发完拒绝帧就释放)。</summary>
    public TimeSpan Timeout { get; }

    /// <summary>每条连接最多读掉并丢弃的字节数。</summary>
    public int MaxBytes { get; }

    /// <summary>同时排空的连接数上限。</summary>
    public int MaxConcurrent { get; }

    /// <summary>当前正在排空的连接数(测试用)。</summary>
    public int Active => Volatile.Read(ref _active);

    /// <summary>
    /// 读并丢弃 <paramref name="stream"/> 里对端发来的数据,直到对端关闭(读到 0 或读出错)、字节上限、时限、或 <paramref name="ct"/> 取消。
    /// 不关闭流:由调用方在返回后释放。
    /// </summary>
    public async ValueTask<LingerOutcome> DrainAsync(Stream stream, CancellationToken ct)
    {
        if (Timeout <= TimeSpan.Zero || MaxBytes <= 0 || ct.IsCancellationRequested)
        {
            return LingerOutcome.Skipped;
        }

        if (Interlocked.Increment(ref _active) > MaxConcurrent)
        {
            Interlocked.Decrement(ref _active);
            return LingerOutcome.Busy;
        }

        try
        {
            using var deadline = new CancellationTokenSource(Timeout, _clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            var buffer = new byte[4096];
            var total = 0L;
            while (true)
            {
                int n;
                try
                {
                    n = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return ct.IsCancellationRequested ? LingerOutcome.Skipped : LingerOutcome.TimedOut;
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    return LingerOutcome.PeerClosed; // 对端已经没了(RST / 断开):没有什么要保护的了
                }

                if (n == 0)
                {
                    return LingerOutcome.PeerClosed;
                }

                total += n;
                if (total > MaxBytes)
                {
                    return LingerOutcome.ByteLimit;
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }
}
