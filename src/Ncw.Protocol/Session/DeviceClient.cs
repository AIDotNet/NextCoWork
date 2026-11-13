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
    private readonly TimeSpan _pingInterval;
    private long _nextId;
    private Task _reader = Task.CompletedTask;
    private Task _pinger = Task.CompletedTask;

    private DeviceClient(FrameStream frames, WelcomeFrame welcome, TimeSpan pingInterval)
    {
        _frames = frames;
        Welcome = welcome;
        _pingInterval = pingInterval;
    }

    public WelcomeFrame Welcome { get; }

    /// <summary>连接结束(对端关闭、出错或本地释放)时完成;结果是导致结束的异常,正常关闭为 null。</summary>
    public Task<Exception?> Completion => _completion.Task;

    private readonly TaskCompletionSource<Exception?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>在一条已经连通的字节流上握手。被拒绝时抛 <see cref="ProtocolException"/>。</summary>
    public static async Task<DeviceClient> ConnectAsync(Stream stream, ClientHello hello, CancellationToken ct = default, TimeSpan? pingInterval = null)
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
                    var client = new DeviceClient(frames, welcome, pingInterval ?? TimeSpan.FromSeconds(20));
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
            await using (ct.Register(() => _ = _frames.WriteAsync(new CancelFrame(id)).AsTask()).ConfigureAwait(false))
            {
                response = await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
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

    /// <summary>
    /// 订阅一个主题。<paramref name="onSnapshot"/> 收到权威快照(首次、游标续不上、或设备要求重同步后自动重订);
    /// <paramref name="onEvent"/> 收到之后的增量。返回的对象释放即退订。
    /// </summary>
    public async Task<IAsyncDisposable> SubscribeAsync<TState>(TopicDef<TState> topic, Action<TState, long> onSnapshot, Action<EventFrame> onEvent, CancellationToken ct = default)
    {
        var subscription = new Subscription(this, topic.Name, bytes => onSnapshot(FrameCodec.DecodePayload(bytes.State, topic.StateShape), bytes.Seq), onEvent);
        _subscriptions[topic.Name] = subscription;
        await subscription.SendAsync(cursor: null, ct).ConfigureAwait(false);
        return subscription;
    }

    private void Start()
    {
        _reader = Task.Run(ReadLoopAsync);
        _pinger = Task.Run(PingLoopAsync);
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
                await Task.Delay(_pingInterval, _lifetime.Token).ConfigureAwait(false);
                await _frames.WriteAsync(new PingFrame(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _frames.DisposeAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_reader, _pinger).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _completion.TrySetResult(null);
        _lifetime.Dispose();
    }

    private sealed class Subscription(DeviceClient client, string topic, Action<SnapshotFrame> snapshot, Action<EventFrame> onEvent) : IAsyncDisposable
    {
        private long _seq = -1;

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
