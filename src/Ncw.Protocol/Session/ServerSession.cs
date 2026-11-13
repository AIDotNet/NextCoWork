using System.Collections.Concurrent;
using System.Threading.Channels;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;
using PolyType;

namespace Ncw.Protocol.Session;

/// <summary>一次调用的上下文:调用者是谁、走的什么承载。</summary>
public sealed record CallContext(string ClientId, Role Role, Transports Transport);

/// <summary>
/// 方法路由(docs/design/09 §8):统一检查角色、承载、幂等键,方法实现里不再各自判断(「方法即能力」)。
/// command 的结果按幂等键缓存,续连、换承载后重发拿到同一个结果。
/// </summary>
public sealed class MethodRouter
{
    private readonly Dictionary<string, (MethodDef Def, Func<RequestFrame, CallContext, CancellationToken, ValueTask<byte[]>> Invoke)> _handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<ResponseFrame>> _idempotent = new(StringComparer.Ordinal);

    public void Handle<TParams, TResult>(MethodDef<TParams, TResult> def, Func<TParams, CallContext, CancellationToken, ValueTask<TResult>> handler)
    {
        _handlers[def.Name] = (def, async (request, context, ct) =>
        {
            var parameters = FrameCodec.DecodePayload(request.Params, def.ParamsShape);
            var result = await handler(parameters, context, ct).ConfigureAwait(false);
            return FrameCodec.EncodePayload(result, def.ResultShape);
        });
    }

    public bool Knows(string method) => _handlers.ContainsKey(method);

    public async Task<ResponseFrame> InvokeAsync(RequestFrame request, CallContext context, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(request.Method, out var entry))
        {
            return Fail(request, ErrorCodes.Unsupported, $"Unknown method '{request.Method}'");
        }

        var def = entry.Def;
        if (context.Role < def.MinRole)
        {
            return Fail(request, ErrorCodes.Forbidden, $"'{def.Name}' requires role {def.MinRole}");
        }

        if ((def.Allowed & context.Transport) == 0)
        {
            return Fail(request, ErrorCodes.Forbidden, $"'{def.Name}' is not allowed over {context.Transport}");
        }

        if (def.Kind == MethodKind.Command)
        {
            if (string.IsNullOrEmpty(request.Idem))
            {
                return Fail(request, ErrorCodes.InvalidRequest, $"'{def.Name}' is a command and needs an idempotency key");
            }

            // 同一个幂等键只执行一次;重发拿到同一个结果(id 换成这次请求的)。
            var first = _idempotent.GetOrAdd($"{def.Name}\n{request.Idem}", _ => Execute(request, context, entry.Invoke, ct));
            var response = await first.ConfigureAwait(false);
            if (response is { Ok: false, Error.Retryable: true })
            {
                // 可重试的失败不缓存,下次重发真的再执行一次。
                _idempotent.TryRemove($"{def.Name}\n{request.Idem}", out _);
            }

            return response with { Id = request.Id };
        }

        return await Execute(request, context, entry.Invoke, ct).ConfigureAwait(false);
    }

    private static async Task<ResponseFrame> Execute(
        RequestFrame request, CallContext context, Func<RequestFrame, CallContext, CancellationToken, ValueTask<byte[]>> invoke, CancellationToken ct)
    {
        try
        {
            var result = await invoke(request, context, ct).ConfigureAwait(false);
            return new ResponseFrame(request.Id, true, result, null);
        }
        catch (ProtocolException e)
        {
            return Fail(request, e.Code, e.Message, e.Retryable);
        }
        catch (OperationCanceledException)
        {
            return Fail(request, ErrorCodes.Unavailable, "Cancelled", retryable: true);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 不把内部异常细节发给控制端。
            return Fail(request, ErrorCodes.Internal, "Internal error");
        }
    }

    private static ResponseFrame Fail(RequestFrame request, string code, string message, bool retryable = false) =>
        new(request.Id, false, null, new ProtocolError(code, message, retryable));
}

/// <summary>
/// 主题(docs/design/09 §8.2):注册快照生成函数,发布增量。保留最近一段事件,游标还在范围内就续传,否则给快照。
/// 快照和增量都由这里统一编号,同一主题内 seq 严格递增。
/// </summary>
public sealed class TopicHub
{
    private const int LogCapacity = 256;

    private readonly object _gate = new();
    private readonly Dictionary<string, Topic> _topics = new(StringComparer.Ordinal);

    public void Register<TState>(TopicDef<TState> def, Func<TState> snapshot) =>
        _topics[def.Name] = new Topic(() => FrameCodec.EncodePayload(snapshot(), def.StateShape));

    /// <summary>发布一条增量,推给所有订阅者。</summary>
    public void Publish<TData>(string topic, string type, TData data, ITypeShape<TData> shape)
    {
        var bytes = FrameCodec.EncodePayload(data, shape);
        List<Func<Frame, ValueTask>> sinks;
        EventFrame frame;
        lock (_gate)
        {
            var t = _topics[topic];
            t.Seq++;
            frame = new EventFrame(topic, t.Seq, type, bytes);
            t.Log.Enqueue(frame);
            while (t.Log.Count > LogCapacity)
            {
                t.Log.Dequeue();
            }

            sinks = [.. t.Sinks];
        }

        foreach (var sink in sinks)
        {
            _ = sink(frame).AsTask();
        }
    }

    public bool Knows(string topic) => _topics.ContainsKey(topic);

    /// <summary>订阅:返回需要立刻发给控制端的帧(快照,或从游标之后的补发),并登记后续推送。</summary>
    internal IReadOnlyList<Frame> Subscribe(string topic, long? cursor, Func<Frame, ValueTask> sink)
    {
        lock (_gate)
        {
            var t = _topics[topic];
            t.Sinks.Add(sink);
            if (cursor is { } c && c <= t.Seq && (t.Log.Count == 0 ? c == t.Seq : t.Log.Peek().Seq <= c + 1))
            {
                return [.. t.Log.Where(e => e.Seq > c)];
            }

            return [new SnapshotFrame(topic, t.Seq, t.Snapshot())];
        }
    }

    internal void Unsubscribe(string topic, Func<Frame, ValueTask> sink)
    {
        lock (_gate)
        {
            if (_topics.TryGetValue(topic, out var t))
            {
                t.Sinks.Remove(sink);
            }
        }
    }

    private sealed class Topic(Func<byte[]> snapshot)
    {
        public Func<byte[]> Snapshot { get; } = snapshot;

        public long Seq { get; set; }

        public Queue<EventFrame> Log { get; } = new();

        public List<Func<Frame, ValueTask>> Sinks { get; } = [];
    }
}

/// <summary>设备端的握手策略。</summary>
public sealed record ServerOptions(
    string DeviceId,
    string DeviceVersion,
    Transports Transport,
    /// <summary>根据 hello 决定角色;返回 null 拒绝(本机通道核对一次性本机令牌)。</summary>
    Func<HelloFrame, Role?> Authenticate,
    TimeSpan? IdleTimeout = null);

/// <summary>
/// 设备端的一条连接:握手 → 读循环(请求并发执行、订阅、心跳)。连接结束时取消所有进行中的请求、注销订阅。
/// </summary>
public sealed class ServerSession(FrameStream frames, MethodRouter router, TopicHub topics, ServerOptions options)
{
    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _inflight = new();
    private readonly Dictionary<string, (Func<Frame, ValueTask> Sink, Channel<Frame> Queue)> _subscriptions = new(StringComparer.Ordinal);

    public async Task RunAsync(CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var idle = options.IdleTimeout ?? TimeSpan.FromSeconds(60);
        try
        {
            if (await HandshakeAsync(lifetime.Token).ConfigureAwait(false) is not { } context)
            {
                return;
            }

            while (!lifetime.IsCancellationRequested)
            {
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                idleCts.CancelAfter(idle);
                Frame? frame;
                try
                {
                    frame = await frames.ReadAsync(idleCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                {
                    break; // 空闲超时:对端没有心跳,断开
                }

                if (frame is null)
                {
                    break;
                }

                Dispatch(frame, context, lifetime.Token);
            }
        }
        catch (ProtocolException)
        {
            // 畸形帧、超长帧:断开,不静默继续。
        }
        catch (IOException)
        {
        }
        finally
        {
            foreach (var cts in _inflight.Values)
            {
                cts.Cancel();
            }

            foreach (var (topic, entry) in _subscriptions)
            {
                topics.Unsubscribe(topic, entry.Sink);
                entry.Queue.Writer.TryComplete();
            }

            await lifetime.CancelAsync().ConfigureAwait(false);

            // 关掉底层连接:对端据此得知会话结束,挂起的请求以可重试的失败结束。
            await frames.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<CallContext?> HandshakeAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        if (await frames.ReadAsync(timeout.Token).ConfigureAwait(false) is not HelloFrame hello)
        {
            await frames.WriteAsync(new RejectFrame(ErrorCodes.InvalidRequest, "Expected hello"), ct).ConfigureAwait(false);
            return null;
        }

        var version = Math.Min(hello.MaxVersion, ProtocolInfo.Version);
        if (version < Math.Max(hello.MinVersion, ProtocolInfo.MinVersion))
        {
            var code = hello.MaxVersion < ProtocolInfo.MinVersion ? ErrorCodes.ClientOutdated : ErrorCodes.Unsupported;
            await frames.WriteAsync(new RejectFrame(code, $"Device speaks protocol {ProtocolInfo.MinVersion}..{ProtocolInfo.Version}"), ct).ConfigureAwait(false);
            return null;
        }

        if (options.Authenticate(hello) is not { } role)
        {
            await frames.WriteAsync(new RejectFrame(ErrorCodes.Unauthorized, "Not authorized"), ct).ConfigureAwait(false);
            return null;
        }

        await frames.WriteAsync(new WelcomeFrame(
            version, options.DeviceId, options.DeviceVersion, role.ToString().ToLowerInvariant(), [],
            Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ct).ConfigureAwait(false);
        return new CallContext(hello.ClientId, role, options.Transport);
    }

    private void Dispatch(Frame frame, CallContext context, CancellationToken ct)
    {
        switch (frame)
        {
            case RequestFrame request:
                var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _inflight[request.Id] = cts;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var response = await router.InvokeAsync(request, context, cts.Token).ConfigureAwait(false);
                        await frames.WriteAsync(response, ct).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
                    {
                    }
                    finally
                    {
                        _inflight.TryRemove(request.Id, out _);
                        cts.Dispose();
                    }
                }, CancellationToken.None);
                break;

            case CancelFrame cancel:
                if (_inflight.TryGetValue(cancel.Id, out var running))
                {
                    running.Cancel();
                }

                break;

            case SubscribeFrame subscribe:
                Subscribe(subscribe, ct);
                break;

            case UnsubscribeFrame unsubscribe:
                if (_subscriptions.Remove(unsubscribe.Topic, out var entry))
                {
                    topics.Unsubscribe(unsubscribe.Topic, entry.Sink);
                    entry.Queue.Writer.TryComplete();
                }

                break;

            case PingFrame ping:
                _ = frames.WriteAsync(new PongFrame(ping.Time), ct).AsTask();
                break;
        }
    }

    private void Subscribe(SubscribeFrame subscribe, CancellationToken ct)
    {
        if (!topics.Knows(subscribe.Topic))
        {
            _ = frames.WriteAsync(new ResponseFrame(subscribe.Id, false, null,
                new ProtocolError(ErrorCodes.NotFound, $"Unknown topic '{subscribe.Topic}'", false)), ct).AsTask();
            return;
        }

        if (_subscriptions.Remove(subscribe.Topic, out var previous))
        {
            topics.Unsubscribe(subscribe.Topic, previous.Sink);
            previous.Queue.Writer.TryComplete();
        }

        // 每个订阅一条有序队列:先写确认和初始帧(快照或补发),再按顺序写后续增量。
        // 不这样做的话,快照写出之前发布的增量可能先到,控制端会看到乱序。
        var queue = Channel.CreateUnbounded<Frame>(new UnboundedChannelOptions { SingleReader = true });
        Func<Frame, ValueTask> sink = frame =>
        {
            queue.Writer.TryWrite(frame);
            return ValueTask.CompletedTask;
        };
        _subscriptions[subscribe.Topic] = (sink, queue);

        // 登记和取快照在同一把锁里完成,不会漏掉两者之间发布的事件。
        var initial = topics.Subscribe(subscribe.Topic, subscribe.Cursor, sink);
        _ = Task.Run(async () =>
        {
            try
            {
                await frames.WriteAsync(new ResponseFrame(subscribe.Id, true, null, null), ct).ConfigureAwait(false);
                foreach (var frame in initial)
                {
                    await frames.WriteAsync(frame, ct).ConfigureAwait(false);
                }

                await foreach (var frame in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    await frames.WriteAsync(frame, ct).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }, CancellationToken.None);
    }
}
