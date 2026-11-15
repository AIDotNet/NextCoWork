using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace Ncw.Agent.Browser.Cdp;

/// <summary>CDP 命令返回了 <c>error</c>。</summary>
public sealed class CdpException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>连接断了 / 关了:所有在途命令都以它失败。</summary>
public sealed class CdpDisconnectedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>一条 CDP 事件。<see cref="Params"/> 是独立克隆,可以在回调之外持有。</summary>
public sealed record CdpEvent(string Method, string? SessionId, JsonElement Params);

/// <summary>
/// 最小 CDP 客户端:一条 WebSocket、JSON-RPC 的 <c>id</c> 配对、事件分发。<b>扁平会话</b>(<c>Target.attachToTarget</c> 的 <c>flatten: true</c>):所有页面共用这一条连接,命令和事件带 <c>sessionId</c>。
/// 不用反射序列化:请求用 <see cref="Utf8JsonWriter"/> 手写,响应用 <see cref="JsonDocument"/> 读。
/// </summary>
public sealed class CdpConnection : IAsyncDisposable
{
    /// <summary>单条消息的上限。截图(base64)是最大的合法载荷,8 MiB 的图 + 余量;超了说明对端不正常,断开而不是无限吃内存。</summary>
    public const int MaxMessageBytes = 32 * 1024 * 1024;

    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly List<EventWaiter> _waiters = [];
    private readonly Lock _waiterGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _receiveLoop;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextId;
    private int _disposed;

    /// <summary>每条事件同步回调(在接收循环上):订阅者不许阻塞,要做长事就 <c>Task.Run</c>。</summary>
    public event Action<CdpEvent>? EventReceived;

    public CdpConnection(WebSocket socket)
    {
        _socket = socket;
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    /// <summary>连接 CDP 的 WebSocket 端点。</summary>
    public static async Task<CdpConnection> ConnectAsync(Uri endpoint, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        // Chrome 不需要保活帧,且不带 Origin(带了要 --remote-allow-origins)。
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        try
        {
            await socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new CdpConnection(socket);
    }

    /// <summary>连接关闭(对端断开 / 本地释放 / 协议错误)后完成。</summary>
    public Task Closed => _closed.Task;

    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && _socket.State == WebSocketState.Open;

    /// <summary>发一条命令并等它的结果(<c>result</c> 对象的克隆)。</summary>
    /// <param name="parameters">写 <c>params</c> 对象的内容(已经在对象里,只写属性);<c>null</c> = 不带参数。</param>
    public async Task<JsonElement> SendAsync(string method, Action<Utf8JsonWriter>? parameters = null, string? sessionId = null, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            var buffer = new ArrayBufferWriter<byte>(256);
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteNumber("id", id);
                w.WriteString("method", method);
                if (sessionId is not null)
                {
                    w.WriteString("sessionId", sessionId);
                }

                if (parameters is not null)
                {
                    w.WriteStartObject("params");
                    parameters(w);
                    w.WriteEndObject();
                }

                w.WriteEndObject();
            }

            await _sendGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!IsOpen)
                {
                    throw new CdpDisconnectedException("The browser connection is closed.");
                }

                await _socket.SendAsync(buffer.WrittenMemory, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            catch (WebSocketException e)
            {
                throw new CdpDisconnectedException("The browser connection was lost.", e);
            }
            finally
            {
                _sendGate.Release();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
            try
            {
                return await completion.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"The browser did not answer {method} in time.");
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// 先登记、后发命令:「等一个事件」必须在触发它的命令发出<b>之前</b>登记,否则事件可能在登记前就到了(导航很快的时候)。
    /// </summary>
    public EventWaiter Expect(string method, string? sessionId = null, Func<JsonElement, bool>? predicate = null)
    {
        var waiter = new EventWaiter(this, method, sessionId, predicate);
        lock (_waiterGate)
        {
            _waiters.Add(waiter);
        }

        return waiter;
    }

    private void Remove(EventWaiter waiter)
    {
        lock (_waiterGate)
        {
            _waiters.Remove(waiter);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var ct = _lifetime.Token;
        var chunk = new byte[16 * 1024];
        using var message = new MemoryStream();
        Exception? failure = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                message.SetLength(0);
                ValueWebSocketReceiveResult received;
                do
                {
                    received = await _socket.ReceiveAsync(chunk.AsMemory(), ct).ConfigureAwait(false);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        throw new CdpDisconnectedException("The browser closed the connection.");
                    }

                    message.Write(chunk, 0, received.Count);
                    if (message.Length > MaxMessageBytes)
                    {
                        throw new CdpDisconnectedException("The browser sent a message that is too large.");
                    }
                }
                while (!received.EndOfMessage);

                try
                {
                    Dispatch(message.GetBuffer().AsMemory(0, (int)message.Length));
                }
                catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
                {
                    // 一条形状不对的消息不能杀掉接收循环(对端是不可信的)
                }
            }
        }
        catch (Exception e)
        {
            failure = e;
        }

        var reason = failure as CdpDisconnectedException ?? new CdpDisconnectedException("The browser connection was lost.", failure);
        foreach (var (id, pending) in _pending.ToArray())
        {
            pending.TrySetException(reason);
            _pending.TryRemove(id, out _);
        }

        lock (_waiterGate)
        {
            foreach (var waiter in _waiters)
            {
                waiter.Fail(reason);
            }

            _waiters.Clear();
        }

        _closed.TrySetResult();
    }

    private void Dispatch(ReadOnlyMemory<byte> bytes)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return; // 对端发了坏 JSON:忽略这一条,不拖垮连接
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt64(out var id))
            {
                if (!_pending.TryGetValue(id, out var pending))
                {
                    return;
                }

                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var ci) ? ci : 0;
                    var text = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
                    if (error.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
                    {
                        text = $"{text} {data.GetString()}";
                    }

                    pending.TrySetException(new CdpException(code, text));
                }
                else
                {
                    pending.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
                }

                return;
            }

            if (root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String)
            {
                var method = methodElement.GetString()!;
                var sessionId = root.TryGetProperty("sessionId", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;
                var ev = new CdpEvent(method, sessionId, parameters);
                lock (_waiterGate)
                {
                    foreach (var waiter in _waiters.ToArray())
                    {
                        if (waiter.Offer(ev))
                        {
                            _waiters.Remove(waiter);
                        }
                    }
                }

                try
                {
                    EventReceived?.Invoke(ev);
                }
                catch
                {
                    // 订阅者的异常不能杀接收循环
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
            // 已经取消过
        }

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, closing.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // 尽力而为
        }

        try
        {
            await _receiveLoop.ConfigureAwait(false);
        }
        catch
        {
            // 接收循环自己收尾,不会抛
        }

        _socket.Dispose();
        _lifetime.Dispose();
        _sendGate.Dispose();
    }

    /// <summary>一次性的事件等待。<see cref="Dispose"/> 摘除登记(没等到也要摘)。</summary>
    public sealed class EventWaiter : IDisposable
    {
        private readonly CdpConnection _owner;
        private readonly string _method;
        private readonly string? _sessionId;
        private readonly Func<JsonElement, bool>? _predicate;
        private readonly TaskCompletionSource<JsonElement> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal EventWaiter(CdpConnection owner, string method, string? sessionId, Func<JsonElement, bool>? predicate)
        {
            _owner = owner;
            _method = method;
            _sessionId = sessionId;
            _predicate = predicate;
        }

        internal bool Offer(CdpEvent ev)
        {
            if (ev.Method != _method || (_sessionId is not null && ev.SessionId != _sessionId) || (_predicate is not null && !_predicate(ev.Params)))
            {
                return false;
            }

            _completion.TrySetResult(ev.Params);
            return true;
        }

        internal void Fail(Exception e) => _completion.TrySetException(e);

        /// <summary>等到事件(返回它的 params);超时抛 <see cref="TimeoutException"/>。</summary>
        public async Task<JsonElement> WaitAsync(TimeSpan timeout, CancellationToken ct)
        {
            try
            {
                return await _completion.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"{_method} did not arrive within {timeout.TotalSeconds:0.#} seconds.");
            }
        }

        public void Dispose() => _owner.Remove(this);
    }
}
