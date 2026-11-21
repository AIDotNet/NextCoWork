using System.Collections.Concurrent;
using System.Threading.Channels;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;
using PolyType;

namespace Ncw.Protocol.Session;

/// <summary>远程连接的对端(TLS 握手的结果)。本机连接为 null。</summary>
/// <param name="Fingerprint">对端公钥指纹(TLS 已证明对端持有私钥)。</param>
/// <param name="LocalFingerprint">本端(设备)公钥指纹。</param>
/// <param name="Exporter">TLS exporter 值,配对绑定用。</param>
public sealed record SessionPeer(byte[] Fingerprint, byte[] LocalFingerprint, byte[] Exporter, System.Net.IPEndPoint? Remote)
{
    public string FingerprintHex => Convert.ToHexStringLower(Fingerprint);
}

/// <summary>一次调用的上下文:调用者是谁、走的什么承载、远程对端是谁,以及这条会话自己的状态(例如进行中的配对)。</summary>
public sealed record CallContext(string ClientId, Role Role, Transports Transport, SessionPeer? Peer = null)
{
    /// <summary>控制端在 hello 里声明的增量合并窗口(毫秒,手机会放大)。run 主题的事件泵据此按控制端各自合并(docs/design/09 §8.2)。</summary>
    public int CoalesceMs { get; init; } = 16;

    /// <summary>会话级状态,随连接结束丢弃。</summary>
    public ConcurrentDictionary<string, object> Items { get; } = new(StringComparer.Ordinal);

    private readonly object _closeGate = new();
    private List<Action>? _onClosed = [];

    /// <summary>
    /// 登记「这条连接结束了」的回调(终端据此在控制端离开时释放它名下的远程终端)。连接已经结束时立刻在当前线程调用。
    /// 回调里的异常被吞掉:一个订阅者的清理失败不能挡住别的清理。
    /// </summary>
    public void OnClosed(Action callback)
    {
        lock (_closeGate)
        {
            if (_onClosed is { } list)
            {
                list.Add(callback);
                return;
            }
        }

        RunCallback(callback);
    }

    /// <summary>连接结束时由 <see cref="ServerSession"/> 调用,只生效一次。</summary>
    internal void RaiseClosed()
    {
        List<Action>? callbacks;
        lock (_closeGate)
        {
            callbacks = _onClosed;
            _onClosed = null;
        }

        foreach (var callback in callbacks ?? [])
        {
            RunCallback(callback);
        }
    }

    private static void RunCallback(Action callback)
    {
        try
        {
            callback();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 清理回调的问题,不影响连接结束
        }
    }

    /// <summary>调用者的身份范围:本机连接是 local,远程连接是对端公钥指纹。幂等结果按它隔离。</summary>
    public string Principal => Peer?.FingerprintHex ?? "local";
}

/// <summary>
/// 方法路由(docs/design/09 §8):统一检查角色、承载、幂等键,方法实现里不再各自判断(「方法即能力」)。
/// command 的结果按幂等键缓存,续连、换承载后重发拿到同一个结果。缓存有 24 小时的保留期(09 §8.1)和条数上限:
/// 高频的 command(终端的每一次按键输入都是一条 <c>terminals.write</c>)不能让这张表无限增长。
/// </summary>
public sealed class MethodRouter(TimeProvider? clock = null)
{
    /// <summary>幂等结果的保留期(09 §8.1:设备保存 24 小时内的「幂等键 → 结果」)。</summary>
    public static readonly TimeSpan IdempotencyTtl = TimeSpan.FromHours(24);

    /// <summary>幂等结果的条数上限:超过时先淘汰最老的已完成条目(进行中的永不淘汰)。</summary>
    public const int MaxIdempotentEntries = 100_000;

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, (MethodDef Def, Func<RequestFrame, CallContext, CancellationToken, ValueTask<byte[]>> Invoke)> _handlers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _extraCapabilities = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IdempotentEntry> _idempotent = new(StringComparer.Ordinal);
    private long _lastSweep;
    private int _addedSinceSweep;

    private sealed record IdempotentEntry(Task<ResponseFrame> Result, long CreatedAt);

    /// <summary>当前缓存的幂等结果条数(测试用)。</summary>
    internal int IdempotentCount => _idempotent.Count;

    /// <summary>
    /// 方法调用前的统一闸门:角色 / 承载 / 幂等键检查之后、参数解码之前,对每个已注册方法的请求调用一次。宿主用它做「方法即能力」之外的横切前置条件
    /// (例如:参数里指向 SSH 项目的方法,先确保项目的执行环境已连接)。抛 <see cref="ProtocolException"/> = 这次请求以那个错误失败,方法实现不会被调用。
    /// 返回的句柄(可以为 null)在方法实现结束后释放:前置条件需要在整个请求期间保持时用它(例如「环境在用,别回收」的租约)。
    /// </summary>
    public Func<MethodDef, RequestFrame, CallContext, CancellationToken, ValueTask<IDisposable?>>? BeforeInvoke { get; set; }

    public void Handle<TParams, TResult>(MethodDef<TParams, TResult> def, Func<TParams, CallContext, CancellationToken, ValueTask<TResult>> handler)
    {
        _handlers[def.Name] = (def, async (request, context, ct) =>
        {
            using var scope = BeforeInvoke is { } gate ? await gate(def, request, context, ct).ConfigureAwait(false) : null;

            TParams parameters;
            try
            {
                parameters = FrameCodec.DecodePayload(request.Params, def.ParamsShape);
            }
            catch (Nerdbank.MessagePack.MessagePackSerializationException e)
            {
                // 参数畸形(缺字段、类型不对)是调用方的错,不是内部错误;原因里只有字段名,不含内部细节。
                throw new ProtocolException(ErrorCodes.InvalidRequest, $"Invalid parameters for {def.Name}: {(e.InnerException ?? e).Message}");
            }

            var result = await handler(parameters, context, ct).ConfigureAwait(false);
            return FrameCodec.EncodePayload(result, def.ResultShape);
        });
    }

    public bool Knows(string method) => _handlers.ContainsKey(method);

    /// <summary>已注册的全部方法(开放网关的 OpenAPI 导出、HTTP 映射据此:「同一张方法表」)。</summary>
    public IReadOnlyList<MethodDef> Methods => [.. _handlers.Values.Select(h => h.Def).OrderBy(d => d.Name, StringComparer.Ordinal)];

    public MethodDef? Find(string method) => _handlers.TryGetValue(method, out var entry) ? entry.Def : null;

    /// <summary>显式声明设备支持某个后加的能力开关(例如不单独占方法名、而是在既有方法上扩展语义与末尾可选字段的能力)。</summary>
    public void DeclareCapability(string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        _extraCapabilities.Add(capability);
    }

    /// <summary>已注册方法所属的能力与显式声明的能力(去重、排序)。握手时作为 <c>welcome.features</c> 告诉控制端:这台设备支持哪些领域。</summary>
    public string[] Capabilities =>
        [.. _handlers.Values.Select(h => h.Def.Capability).OfType<string>().Concat(_extraCapabilities).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

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
            // 按调用者隔离:别的控制端重放同一个键,拿不到这个控制端的结果。
            var key = $"{context.Principal}\n{def.Name}\n{request.Idem}";
            SweepIdempotent();
            var first = _idempotent.GetOrAdd(key, _ =>
            {
                Interlocked.Increment(ref _addedSinceSweep);
                return new IdempotentEntry(Execute(request, context, entry.Invoke, ct), _clock.GetTimestamp());
            }).Result;
            var response = await first.ConfigureAwait(false);
            if (response is { Ok: false, Error.Retryable: true })
            {
                // 可重试的失败不缓存,下次重发真的再执行一次。
                _idempotent.TryRemove(key, out _);
            }

            return response with { Id = request.Id };
        }

        return await Execute(request, context, entry.Invoke, ct).ConfigureAwait(false);
    }

    /// <summary>每分钟最多扫一次:淘汰超过保留期的已完成条目;条数还超过上限就再淘汰最老的已完成条目,直到回落到上限的 90%。</summary>
    private void SweepIdempotent()
    {
        var now = _clock.GetTimestamp();
        var last = Interlocked.Read(ref _lastSweep);
        // ConcurrentDictionary.Count 要锁全表,不能每次调用都读:按时间(每分钟)或按新增量(每新增 1 万条)才扫
        if (last != 0 && _clock.GetElapsedTime(last, now) < SweepInterval && Volatile.Read(ref _addedSinceSweep) < MaxIdempotentEntries / 10)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastSweep, now, last) != last)
        {
            return;
        }

        Volatile.Write(ref _addedSinceSweep, 0);

        foreach (var (key, value) in _idempotent)
        {
            if (value.Result.IsCompleted && _clock.GetElapsedTime(value.CreatedAt, now) > IdempotencyTtl)
            {
                _idempotent.TryRemove(key, out _);
            }
        }

        if (_idempotent.Count <= MaxIdempotentEntries)
        {
            return;
        }

        var excess = _idempotent.Count - MaxIdempotentEntries * 9 / 10;
        foreach (var (key, _) in _idempotent.Where(static kv => kv.Value.Result.IsCompleted).OrderBy(static kv => kv.Value.CreatedAt).Take(excess).ToList())
        {
            _idempotent.TryRemove(key, out _);
        }
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
            return Fail(request, e.Code, e.Message, e.Retryable, e.Details);
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

    private static ResponseFrame Fail(RequestFrame request, string code, string message, bool retryable = false, byte[]? details = null) =>
        new(request.Id, false, null, new ProtocolError(code, message, retryable, null, details));
}

/// <summary>设备端的握手策略。</summary>
public sealed record ServerOptions(
    string DeviceId,
    string DeviceVersion,
    Transports Transport,
    /// <summary>
    /// 根据 hello 和远程对端决定角色;返回 null 拒绝。本机通道核对一次性本机令牌(对端为 null);
    /// 远程通道按对端公钥指纹查信任记录,没配对的给 <see cref="Role.Unpaired"/>。
    /// </summary>
    Func<HelloFrame, SessionPeer?, Role?> Authenticate,
    TimeSpan? IdleTimeout = null,
    /// <summary>hello 阶段拒绝之后的「拒绝送达」策略(null = <see cref="RejectLinger.Default"/>)。</summary>
    RejectLinger? Linger = null);

/// <summary>
/// 设备端的一条连接:握手 → 读循环(请求并发执行、订阅、心跳)。连接结束时取消所有进行中的请求、注销订阅。
/// </summary>
public sealed class ServerSession(FrameStream frames, MethodRouter router, TopicHub topics, ServerOptions options, SessionPeer? peer = null)
{
    /// <summary>握手后的调用者(没握手成功为 null)。</summary>
    public CallContext? Context { get; private set; }

    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _inflight = new();
    private readonly Dictionary<string, (Action Close, Channel<Frame> Queue)> _subscriptions = new(StringComparer.Ordinal);
    private long _pendingBytes;

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

            foreach (var (_, entry) in _subscriptions)
            {
                entry.Close();
                entry.Queue.Writer.TryComplete();
            }

            await lifetime.CancelAsync().ConfigureAwait(false);
            Context?.RaiseClosed();

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
            await RejectAsync(new RejectFrame(ErrorCodes.InvalidRequest, "Expected hello"), ct).ConfigureAwait(false);
            return null;
        }

        var version = Math.Min(hello.MaxVersion, ProtocolInfo.Version);
        if (version < Math.Max(hello.MinVersion, ProtocolInfo.MinVersion))
        {
            var code = hello.MaxVersion < ProtocolInfo.MinVersion ? ErrorCodes.ClientOutdated : ErrorCodes.Unsupported;
            await RejectAsync(new RejectFrame(code, $"Device speaks protocol {ProtocolInfo.MinVersion}..{ProtocolInfo.Version}"), ct).ConfigureAwait(false);
            return null;
        }

        if (options.Authenticate(hello, peer) is not { } role)
        {
            await RejectAsync(new RejectFrame(ErrorCodes.Unauthorized, "Not authorized"), ct).ConfigureAwait(false);
            return null;
        }

        await frames.WriteAsync(new WelcomeFrame(
            version, options.DeviceId, options.DeviceVersion, role.ToString().ToLowerInvariant(), router.Capabilities,
            Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ct).ConfigureAwait(false);
        Context = new CallContext(hello.ClientId, role, options.Transport, peer) { CoalesceMs = Math.Clamp(hello.CoalesceMs, 0, 2000) };
        return Context;
    }

    /// <summary>
    /// hello 阶段的所有拒绝都走这里:发出拒绝帧,然后「拒绝送达」(<see cref="RejectLinger"/>)——排空对端还在发的数据、等它先关闭,再由调用方释放连接,
    /// 这样拒绝帧不会被 RST 丢掉,控制端拿到的是明确的拒绝码(不是可重试的「连接丢失」)。
    /// </summary>
    private async Task RejectAsync(RejectFrame reject, CancellationToken ct)
    {
        await frames.WriteAsync(reject, ct).ConfigureAwait(false);
        await (options.Linger ?? RejectLinger.Default).DrainAsync(frames.Inner, ct).ConfigureAwait(false);
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

            case SubscribeFrame subscribe when topics.Knows(subscribe.Topic) && !topics.CanSubscribe(subscribe.Topic, context):
                // 未配对的连接不能看任何状态;remote 主题(含配对码)只给本机 owner。
                _ = frames.WriteAsync(new ResponseFrame(subscribe.Id, false, null,
                    new ProtocolError(ErrorCodes.Forbidden, $"Not allowed to subscribe to '{subscribe.Topic}'", false)), ct).AsTask();
                break;

            case SubscribeFrame subscribe:
                Subscribe(subscribe, context, ct);
                break;

            case UnsubscribeFrame unsubscribe:
                if (_subscriptions.Remove(unsubscribe.Topic, out var entry))
                {
                    entry.Close();
                    entry.Queue.Writer.TryComplete();
                }

                break;

            case PingFrame ping:
                _ = frames.WriteAsync(new PongFrame(ping.Time), ct).AsTask();
                break;
        }
    }

    private void Subscribe(SubscribeFrame subscribe, CallContext context, CancellationToken ct)
    {
        void Fail(string code, string message, byte[]? details = null, bool retryable = false) =>
            _ = frames.WriteAsync(new ResponseFrame(subscribe.Id, false, null, new ProtocolError(code, message, retryable, null, details)), ct).AsTask();

        if (!topics.Knows(subscribe.Topic))
        {
            Fail(ErrorCodes.NotFound, $"Unknown topic '{subscribe.Topic}'");
            return;
        }

        if (_subscriptions.Remove(subscribe.Topic, out var previous))
        {
            previous.Close();
            previous.Queue.Writer.TryComplete();
        }

        // 每个订阅一条有序队列:先写确认和初始帧(快照或补发),再按顺序写后续增量。
        // 不这样做的话,快照写出之前发布的增量可能先到,控制端会看到乱序。
        var queue = Channel.CreateUnbounded<Frame>(new UnboundedChannelOptions { SingleReader = true });
        Action close;
        if (topics.FindSource(subscribe.Topic) is { } source)
        {
            // 来源主题:来源自己写初始帧(快照或补发)和后续实时帧;队列保证它们在确认之后按写入顺序送出。
            IDisposable? opened;
            try
            {
                opened = source.Open(subscribe.Topic, subscribe.Cursor, new QueueWriter(this, queue, context));
            }
            catch (ProtocolException e)
            {
                queue.Writer.TryComplete();
                Fail(e.Code, e.Message, e.Details, e.Retryable);
                return;
            }

            if (opened is null)
            {
                queue.Writer.TryComplete();
                Fail(ErrorCodes.NotFound, $"Unknown topic '{subscribe.Topic}'");
                return;
            }

            close = opened.Dispose;
        }
        else
        {
            Func<Frame, ValueTask> sink = frame =>
            {
                Enqueue(queue, frame);
                return ValueTask.CompletedTask;
            };

            // 登记和取快照在同一把锁里完成,不会漏掉两者之间发布的事件。
            IReadOnlyList<Frame>? initial;
            try
            {
                initial = topics.Subscribe(subscribe.Topic, subscribe.Cursor, sink);
            }
            catch (ProtocolException e)
            {
                // 快照生成函数可以拒绝(例如设备数据还没就绪:unavailable,可重试)。
                queue.Writer.TryComplete();
                Fail(e.Code, e.Message, e.Details, e.Retryable);
                return;
            }

            if (initial is null)
            {
                queue.Writer.TryComplete();
                Fail(ErrorCodes.NotFound, $"Unknown topic '{subscribe.Topic}'");
                return;
            }

            foreach (var frame in initial)
            {
                Enqueue(queue, frame);
            }

            close = () => topics.Unsubscribe(subscribe.Topic, sink);
        }

        _subscriptions[subscribe.Topic] = (close, queue);
        _ = Task.Run(async () =>
        {
            try
            {
                await frames.WriteAsync(new ResponseFrame(subscribe.Id, true, null, null), ct).ConfigureAwait(false);
                await foreach (var frame in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    await frames.WriteAsync(frame, ct).ConfigureAwait(false);
                    Interlocked.Add(ref _pendingBytes, -Estimate(frame));
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }, CancellationToken.None);
    }

    private void Enqueue(Channel<Frame> queue, Frame frame)
    {
        Interlocked.Add(ref _pendingBytes, Estimate(frame));
        if (!queue.Writer.TryWrite(frame))
        {
            Interlocked.Add(ref _pendingBytes, -Estimate(frame)); // 队列已关闭(退订 / 连接结束)
        }
    }

    private static long Estimate(Frame frame) => frame switch
    {
        EventFrame e => e.Data.Length + e.Topic.Length + e.Type.Length + 32,
        SnapshotFrame s => s.State.Length + s.Topic.Length + 32,
        _ => 64,
    };

    private sealed class QueueWriter(ServerSession session, Channel<Frame> queue, CallContext context) : ITopicWriter
    {
        public CallContext Context { get; } = context;

        public void Write(Frame frame) => session.Enqueue(queue, frame);

        public bool TryWrite(Frame frame)
        {
            if (Interlocked.Read(ref session._pendingBytes) > ProtocolInfo.MaxPendingBytes)
            {
                return false;
            }

            session.Enqueue(queue, frame);
            return true;
        }
    }
}
