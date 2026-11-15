using System.Collections.Concurrent;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Session;

/// <summary>控制端的 hello 参数(docs/design/09 §7)。</summary>
public sealed record ClientHello(
    string ClientId,
    string ClientVersion,
    string FormFactor = "desktop",
    int CoalesceMs = 16,
    string? LocalToken = null,
    string? ResumeToken = null);

/// <summary>
/// 控制端到一台设备的一条连接:握手、请求与响应配对、订阅分发、心跳。
/// 回调在读循环所在的线程上执行,调用方自己切到 UI 线程。连接结束后 <see cref="Completion"/> 完成,
/// 所有未完成的请求以 <see cref="ProtocolException"/>(unavailable,可重试)失败。
/// </summary>
public sealed class DeviceClient : IAsyncDisposable
{
    private readonly FrameStream _frames;
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<ResponseFrame>> _pending = new();
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HeartbeatOptions _heartbeat;
    private readonly TimeProvider _clock;
    private long _nextId;
    private long _lastInbound;
    private int _transportClosed;
    private Task _reader = Task.CompletedTask;
    private Task _pinger = Task.CompletedTask;
    private Task _watchdog = Task.CompletedTask;
    private Task _transportClose = Task.CompletedTask;

    private DeviceClient(FrameStream frames, WelcomeFrame welcome, HeartbeatOptions heartbeat)
    {
        _frames = frames;
        Welcome = welcome;
        _heartbeat = heartbeat;
        _clock = heartbeat.EffectiveClock;
        _lastInbound = _clock.GetTimestamp();
    }

    /// <summary>距离上一次收到设备的任何一帧过去了多久(按心跳的时钟)。心跳据此判断连接是否已死;测试和诊断也读它。</summary>
    public TimeSpan IdleFor => _clock.GetElapsedTime(Volatile.Read(ref _lastInbound));

    public WelcomeFrame Welcome { get; }

    /// <summary>连接结束(对端关闭、出错或本地释放)时完成;结果是导致结束的异常,正常关闭为 null。</summary>
    public Task<Exception?> Completion => _completion.Task;

    private readonly TaskCompletionSource<Exception?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>在一条已经连通的字节流上握手。被拒绝时抛 <see cref="ProtocolException"/>。</summary>
    public static async Task<DeviceClient> ConnectAsync(Stream stream, ClientHello hello, CancellationToken ct = default, HeartbeatOptions? heartbeat = null)
    {
        var frames = new FrameStream(stream);
        try
        {
            await frames.WriteAsync(new HelloFrame(
                ProtocolInfo.MinVersion, ProtocolInfo.Version, hello.ClientId, hello.ClientVersion, hello.FormFactor,
                [], hello.CoalesceMs, hello.LocalToken, hello.ResumeToken), ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var reply = await frames.ReadAsync(timeout.Token).ConfigureAwait(false);
            switch (reply)
            {
                case WelcomeFrame welcome:
                    var client = new DeviceClient(frames, welcome, heartbeat ?? HeartbeatOptions.Default);
                    client.Start();
                    return client;
                case RejectFrame reject:
                    throw new ProtocolException(reject.Code, reject.Message);
                default:
                    throw new ProtocolException(ErrorCodes.InvalidRequest, "Expected welcome");
            }
        }
        catch
        {
            await frames.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>调用一个方法。command 自动带上新的幂等键;重发同一操作请用 <paramref name="idem"/> 传入同一个键。</summary>
    public async Task<TResult> CallAsync<TParams, TResult>(MethodDef<TParams, TResult> method, TParams parameters, CancellationToken ct = default, string? idem = null)
    {
        var id = (ulong)Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<ResponseFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var key = method.Kind == MethodKind.Command ? idem ?? Guid.NewGuid().ToString("N") : null;
            await _frames.WriteAsync(new RequestFrame(id, method.Name, FrameCodec.EncodePayload(parameters, method.ParamsShape), key), ct).ConfigureAwait(false);

            ResponseFrame response;
            try
            {
                response = await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested && !tcs.Task.IsCompleted)
            {
                // ★ 取消要真的送到设备上(设备上还在等的处理器据此收尾,例如 SSH 连接里等用户回答的提问)。
                // 不能用 ct.Register 回调去发:取消时 WaitAsync 的续体可能在同一个线程上内联运行、先释放掉登记,回调就永远不会执行。
                _ = SendCancelAsync(id);
                throw;
            }

            if (!response.Ok)
            {
                throw ProtocolException.From(response.Error ?? new ProtocolError(ErrorCodes.Internal, "Unknown error", false));
            }

            return FrameCodec.DecodePayload(response.Result ?? [], method.ResultShape);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendCancelAsync(ulong id)
    {
        try
        {
            await _frames.WriteAsync(new CancelFrame(id)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // 连接已经断了:设备那边会随连接结束一并取消
        }
    }

    /// <summary>
    /// 订阅一个主题。<paramref name="onSnapshot"/> 收到权威快照(首次、游标续不上、或设备要求重同步后自动重订);
    /// <paramref name="onEvent"/> 收到之后的增量。返回的对象释放即退订。
    /// </summary>
    /// <param name="cursor">
    /// 续传游标(上次收到的最后一个 seq,docs/design/09 §8.2):设备游标还在日志里就只补发之后的增量(没有快照),否则给权威快照。
    /// 重连(换了一个 <see cref="DeviceClient"/>)后按它续上;不带游标就是首次订阅,拿快照。
    /// </param>
    public async Task<IAsyncDisposable> SubscribeAsync<TState>(TopicDef<TState> topic, Action<TState, long> onSnapshot, Action<EventFrame> onEvent, CancellationToken ct = default, long? cursor = null)
    {
        var subscription = new Subscription(this, topic.Name, bytes => onSnapshot(FrameCodec.DecodePayload(bytes.State, topic.StateShape), bytes.Seq), onEvent, cursor);
        _subscriptions[topic.Name] = subscription;
        try
        {
            await subscription.SendAsync(cursor, ct).ConfigureAwait(false);
        }
        catch
        {
            _subscriptions.TryRemove(topic.Name, out _);
            throw;
        }

        return subscription;
    }

    private void Start()
    {
        _reader = Task.Run(ReadLoopAsync);
        _pinger = Task.Run(PingLoopAsync);
        _watchdog = Task.Run(WatchdogAsync);
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                if (await _frames.ReadAsync(_lifetime.Token).ConfigureAwait(false) is not { } frame)
                {
                    break;
                }

                // 收到设备的任何一帧都说明连接还活着(pong 只是设备空闲时保证有帧可收)。
                Volatile.Write(ref _lastInbound, _clock.GetTimestamp());

                switch (frame)
                {
                    case ResponseFrame response when _pending.TryGetValue(response.Id, out var tcs):
                        tcs.TrySetResult(response);
                        break;
                    case SnapshotFrame snapshot when _subscriptions.TryGetValue(snapshot.Topic, out var s):
                        s.OnSnapshot(snapshot);
                        break;
                    case EventFrame evt when _subscriptions.TryGetValue(evt.Topic, out var s):
                        s.OnEvent(evt);
                        break;
                    case ResyncFrame resync when _subscriptions.TryGetValue(resync.Topic, out var s):
                        // 设备要求重同步:不带游标重新订阅,会收到新的快照。
                        _ = s.SendAsync(cursor: null, _lifetime.Token);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            failure = e;
        }
        finally
        {
            var closed = new ProtocolException(ErrorCodes.Unavailable, "Connection closed", retryable: true);
            foreach (var tcs in _pending.Values)
            {
                tcs.TrySetException(closed);
            }

            _completion.TrySetResult(failure);
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task PingLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(_heartbeat.EffectiveInterval, _clock, _lifetime.Token).ConfigureAwait(false);
                await _frames.WriteAsync(new PingFrame(_clock.GetUtcNow().ToUnixTimeMilliseconds()), _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// 看门狗:连续 <see cref="HeartbeatOptions.IdleTimeout"/> 没有收到任何帧,就判定连接已死、主动断开。
    /// 它和 ping 是两条独立的循环:对端静默消失时 ping 的写可能一直挂着(发送缓冲写满、写不可取消),看门狗不能被它拖住。
    /// </summary>
    private async Task WatchdogAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var remaining = _heartbeat.IdleTimeout - IdleFor;
                if (remaining <= TimeSpan.Zero)
                {
                    Fail(new HeartbeatTimeoutException(_heartbeat.IdleTimeout));
                    return;
                }

                await Task.Delay(remaining, _clock, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>连接被判定为死亡:先把原因交给 <see cref="Completion"/>,再取消读循环、让未完成的请求失败、关掉传输(后台关,不挂住调用方)。</summary>
    private void Fail(Exception reason)
    {
        _completion.TrySetResult(reason);
        var closed = new ProtocolException(ErrorCodes.Unavailable, "Connection closed", retryable: true);
        foreach (var tcs in _pending.Values)
        {
            tcs.TrySetException(closed);
        }

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _ = CloseTransportAsync();
    }

    /// <summary>
    /// 关掉传输(幂等,只做一次)。TLS 的收尾会写 close_notify:对端静默消失、发送缓冲又写满时这个写会一直挂着,
    /// 所以等待有上限(系统时钟,不走注入的时钟),超过就放手,让调用方继续重连。
    /// </summary>
    private Task CloseTransportAsync()
    {
        if (Interlocked.Exchange(ref _transportClosed, 1) == 0)
        {
            _transportClose = Task.Run(async () =>
            {
                try
                {
                    await _frames.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
                {
                }
            });
        }

        return _transportClose;
    }

    private static readonly TimeSpan TransportCloseBound = TimeSpan.FromSeconds(5);

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await CloseTransportAsync().WaitAsync(TransportCloseBound).ConfigureAwait(false);
            await Task.WhenAll(_reader, _pinger, _watchdog).WaitAsync(TransportCloseBound).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _completion.TrySetResult(null);
        _lifetime.Dispose();
    }

    private sealed class Subscription(DeviceClient client, string topic, Action<SnapshotFrame> snapshot, Action<EventFrame> onEvent, long? cursor = null) : IAsyncDisposable
    {
        private long _seq = cursor ?? -1;

        public async Task SendAsync(long? cursor, CancellationToken ct)
        {
            var id = (ulong)Interlocked.Increment(ref client._nextId);
            var tcs = new TaskCompletionSource<ResponseFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            client._pending[id] = tcs;
            try
            {
                await client._frames.WriteAsync(new SubscribeFrame(id, topic, cursor), ct).ConfigureAwait(false);
                var response = await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
                if (!response.Ok)
                {
                    throw ProtocolException.From(response.Error!);
                }
            }
            finally
            {
                client._pending.TryRemove(id, out _);
            }
        }

        public void OnSnapshot(SnapshotFrame frame)
        {
            _seq = frame.Seq;
            snapshot(frame);
        }

        public void OnEvent(EventFrame frame)
        {
            // 同一主题内 seq 严格递增;收到旧的(重放)直接丢弃。
            if (frame.Seq <= _seq)
            {
                return;
            }

            _seq = frame.Seq;
            onEvent(frame);
        }

        public async ValueTask DisposeAsync()
        {
            if (client._subscriptions.TryRemove(topic, out _) && !client._lifetime.IsCancellationRequested)
            {
                try
                {
                    await client._frames.WriteAsync(new UnsubscribeFrame(topic)).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                }
            }
        }
    }
}
