using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using Ncw.Protocol.Transport;

namespace Ncw.Relay;

/// <summary>
/// 哑管道:把控制端的 WebSocket 和设备的数据 WebSocket 拼成一条字节流——收到一条二进制消息,原样发给另一边,再读下一条。
/// 中继既不解析、也不改写内容(里面是端到端 TLS 1.3 的密文)。
/// <list type="bullet">
/// <item><b>背压</b>:发送完成才读下一条,所以慢的一边会自然地让另一边的读停下来(靠 TCP 窗口),中继每条会话最多缓冲一条消息。</item>
/// <item><b>大小</b>:一条消息超过上限,以 <c>too_large</c> 关闭;出现文本消息(协议违规)以 <c>bad_request</c> 关闭。</item>
/// <item><b>空闲</b>:两个方向都没有数据超过 <see cref="RelayOptions.IdleTimeout"/>,以 <c>idle</c> 关闭(慢消费者同理)。</item>
/// <item><b>带宽</b>:每条会话两个方向合计的字节速率上限(令牌桶,允许 250 毫秒的突发)。</item>
/// </list>
/// </summary>
internal static class Pipe
{
    public sealed record Result(string Reason, long BytesToDevice, long BytesToController);

    public static async Task<Result> RunAsync(WebSocket controller, WebSocket device, string deviceId, RelayOptions options, RelayMetrics metrics, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var activity = new Activity(options.Clock);
        var throttle = options.MaxBytesPerSecondPerSession > 0 ? new Throttle(options.MaxBytesPerSecondPerSession) : null;
        long toDevice = 0;
        long toController = 0;
        string? reason = null;
        var reasonGate = new object();

        void Finish(string why)
        {
            lock (reasonGate)
            {
                reason ??= why;
            }

            lifetime.Cancel();
        }

        // 一边没走关闭握手就断了(设备崩溃、网络断开、RST……):不中止另一边。已经转发给它的数据照常送完,再给它一个关闭帧、等它自己的关闭帧(宽限同正常关闭);
        // 直接中止会让它的 TCP 以 RST 收尾,在途的数据(例如设备刚发出的「拒绝」帧)会被它丢掉,它看到的是「连接丢失」而不是明确的结果。
        async Task EndedAbnormallyAsync(WebSocket survivor)
        {
            if (lifetime.IsCancellationRequested)
            {
                return;
            }

            lock (reasonGate)
            {
                reason ??= "aborted";
            }

            await ForwardCloseAsync(survivor, lifetime.Token, WebSocketCloseStatus.EndpointUnavailable).ConfigureAwait(false);
            StartGrace();
        }

        var graceStarted = 0;

        void StartGrace()
        {
            if (Interlocked.Exchange(ref graceStarted, 1) == 0)
            {
                // 另一边在宽限期内没有回关闭帧(对端死了):中止。
                _ = Task.Delay(CloseGrace, lifetime.Token).ContinueWith(t =>
                {
                    if (!t.IsCanceled)
                    {
                        Finish("closed");
                    }
                }, TaskScheduler.Default);
            }
        }

        async Task PumpAsync(WebSocket from, WebSocket to, bool towardDevice)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(options.MaxMessageBytes);
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    ValueWebSocketReceiveResult received;
                    try
                    {
                        received = await from.ReceiveAsync(buffer.AsMemory(0, options.MaxMessageBytes), lifetime.Token).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException)
                    {
                        await EndedAbnormallyAsync(to).ConfigureAwait(false);
                        return;
                    }

                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        // 正常关闭:把关闭帧转给另一边,让另一个方向把剩下的数据和它自己的关闭帧送完(不取消那边的读)。
                        await ForwardCloseAsync(to, lifetime.Token).ConfigureAwait(false);
                        StartGrace();
                        return;
                    }

                    if (received.MessageType == WebSocketMessageType.Text)
                    {
                        Finish(RelayErrors.BadRequest);
                        return;
                    }

                    if (!received.EndOfMessage)
                    {
                        Finish(RelayErrors.TooLarge);
                        return;
                    }

                    if (throttle is not null)
                    {
                        await throttle.WaitAsync(received.Count, lifetime.Token).ConfigureAwait(false);
                    }

                    options.Tap?.Invoke(deviceId, towardDevice, buffer.AsMemory(0, received.Count));
                    try
                    {
                        await to.SendAsync(buffer.AsMemory(0, received.Count), WebSocketMessageType.Binary, endOfMessage: true, lifetime.Token).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException)
                    {
                        await EndedAbnormallyAsync(from).ConfigureAwait(false);
                        return;
                    }

                    activity.Touch();
                    metrics.Bytes(towardDevice, received.Count);
                    if (towardDevice)
                    {
                        Interlocked.Add(ref toDevice, received.Count);
                    }
                    else
                    {
                        Interlocked.Add(ref toController, received.Count);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 另一边结束了或被掐断:原因已经记下。
            }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                Finish("aborted");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        async Task WatchdogAsync()
        {
            var period = TimeSpan.FromMilliseconds(Math.Clamp(options.IdleTimeout.TotalMilliseconds / 4, 20, 5000));
            using var timer = new PeriodicTimer(period, options.Clock);
            try
            {
                while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
                {
                    if (activity.IdleFor() >= options.IdleTimeout)
                    {
                        metrics.ClosedByIdle();
                        Finish(RelayErrors.Idle);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        var watchdog = WatchdogAsync();
        // 外部取消(吊销、中继关闭)也要结束拼接;原因由调用方(会话上记的)给出。
        await using var external = ct.Register(() => lifetime.Cancel()).ConfigureAwait(false);
        await Task.WhenAll(PumpAsync(controller, device, towardDevice: true), PumpAsync(device, controller, towardDevice: false)).ConfigureAwait(false);
        lifetime.Cancel(); // 两个方向都结束了:停掉看门狗
        await watchdog.ConfigureAwait(false);

        string final;
        lock (reasonGate)
        {
            final = reason ?? (ct.IsCancellationRequested ? RelayErrors.Unavailable : "closed");
        }

        await CloseBothAsync(controller, device, final).ConfigureAwait(false);
        return new Result(final, Interlocked.Read(ref toDevice), Interlocked.Read(ref toController));
    }

    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(3);

    private static async Task ForwardCloseAsync(WebSocket to, CancellationToken ct, WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure)
    {
        try
        {
            if (to.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await to.CloseOutputAsync(status, null, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // 另一边已经断了:它那个方向的读会报错并收尾。
        }
    }

    /// <summary>带着原因关闭一条 WebSocket:先发文本 <c>error</c>(控制端 / 设备读得到明确的原因),再发关闭帧。尽力而为,失败就中止。</summary>
    public static async Task CloseAsync(WebSocket socket, string errorCode, string? message = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                var text = RelayProtocol.Serialize(new RelayMessage("error") { Code = errorCode, Message = message });
                await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, timeout.Token).ConfigureAwait(false);
            }

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(RelayProtocol.CloseStatusOf(errorCode), errorCode, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            socket.Abort();
        }
    }

    private static async Task CloseBothAsync(WebSocket a, WebSocket b, string reason)
    {
        // 拼接阶段的两边已经只有二进制;关闭时不再发文本消息,只发关闭帧(状态码对应原因)。
        async Task One(WebSocket socket)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await socket.CloseOutputAsync(RelayProtocol.CloseStatusOf(reason), reason, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                socket.Abort();
            }
        }

        await Task.WhenAll(One(a), One(b)).ConfigureAwait(false);
    }

    /// <summary>最近一次有数据的时间。</summary>
    private sealed class Activity(TimeProvider clock)
    {
        private long _last = clock.GetTimestamp();

        public void Touch() => Interlocked.Exchange(ref _last, clock.GetTimestamp());

        public TimeSpan IdleFor() => clock.GetElapsedTime(Interlocked.Read(ref _last));
    }

    /// <summary>令牌桶:每处理一块就把「欠的时间」往后推,欠超过突发额度就等。</summary>
    private sealed class Throttle(long bytesPerSecond)
    {
        private const double BurstSeconds = 0.25;
        private readonly object _gate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _nextFree;

        public Task WaitAsync(int count, CancellationToken ct)
        {
            double wait;
            lock (_gate)
            {
                var now = _clock.Elapsed.TotalSeconds;
                _nextFree = Math.Max(_nextFree, now) + (double)count / bytesPerSecond;
                wait = _nextFree - now - BurstSeconds;
            }

            return wait > 0 ? Task.Delay(TimeSpan.FromSeconds(wait), ct) : Task.CompletedTask;
        }
    }
}
