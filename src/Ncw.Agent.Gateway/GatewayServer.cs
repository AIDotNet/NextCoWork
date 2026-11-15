using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Gateway;

/// <summary>一把 API 密钥对应的调用者:密钥名(日志 / 幂等隔离用)和它绑定的角色。</summary>
public sealed record GatewayCaller(string KeyName, Role Role);

/// <summary>API 密钥的核对(Host 用 NCK1 里的密钥实现)。常数时间比较,没有匹配返回 null。</summary>
public interface IGatewayAuth
{
    GatewayCaller? Authenticate(string bearer);
}

/// <summary>
/// 网关的装配参数。<see cref="Router"/> / <see cref="Topics"/> 就是 Agent 的方法表和主题表:<b>同一张表</b>,角色 / 承载 / 幂等键由路由统一检查,网关不重复判断。
/// <see cref="Guard"/> 是 Host 的一道预检(例如 <c>runs.start</c> 在会话忙时拒绝而不是排队:开放网关的 Run 不排队,会话忙就 409)。
/// </summary>
public sealed class GatewayOptions
{
    public required MethodRouter Router { get; init; }

    public required TopicHub Topics { get; init; }

    public required IGatewayAuth Auth { get; init; }

    public Func<MethodDef, byte[], GatewayCaller, ProtocolException?>? Guard { get; init; }

    public string Title { get; init; } = "NextCoWork Agent API";

    public string Version { get; init; } = "1";

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public int MaxConnections { get; init; } = 64;

    /// <summary>同一个来源地址一分钟内最多几次认证失败(超过返回 429,防猜密钥)。</summary>
    public int MaxAuthFailuresPerMinute { get; init; } = 20;
}

/// <summary>
/// 开放网关的 HTTP 服务器(docs/design/09 §11):同一张方法表的 HTTP 映射。
/// <list type="bullet">
/// <item><c>POST /api/&lt;method&gt;</c>,body 是参数的 JSON(键名 = 协议 DTO 的属性名),<c>Authorization: Bearer &lt;密钥&gt;</c>;command 必须带 <c>Idempotency-Key</c> 头(没带 = 400,和协议一致);
/// 成功 <c>{"ok":true,"result":…}</c>,失败 <c>{"ok":false,"error":{code,message,retryable,details?}}</c> 加对应状态码。</item>
/// <item><c>GET /api/topics/&lt;topic&gt;?cursor=</c>:SSE。<c>event: snapshot | evt | resync</c>,和协议同一套订阅语义(游标续得上只补发增量、续不上给快照);每 20 秒一条 keepalive 注释。</item>
/// <item><c>GET /api/openapi.json</c>:由方法表导出的 OpenAPI 3.1。</item>
/// <item><b>角色</b>:密钥绑定角色,请求按该角色、<see cref="Transports.Remote"/> 承载进路由——所以本机专用的方法(危险操作、密钥、远程开关)网关天然调不到。</item>
/// <item><b>安全</b>:常数时间比较密钥;认证失败按来源地址限速(429);请求头 ≤ 16 KiB、body ≤ 4 MiB、读超时 15 秒、并发连接有上限;每个响应都 <c>Connection: close</c>(SSE 除外);
/// 绑定回环地址时只收回环来源,绑定全部地址时只收回环 / 私有网段来源(和远程开关同一套局域网判定)。注意:这是明文 HTTP,<b>只用在可信的局域网</b>(密钥在网络上是明文的);TLS 随中继 / 证书方案一起做。</item>
/// <item>NativeAOT:自带最小 HTTP/1.1(<see cref="TcpListener"/>),不依赖 ASP.NET Core;JSON ↔ MessagePack 是无类型的结构转换(<see cref="JsonMsgPack"/>),零反射。</item>
/// </list>
/// </summary>
public sealed class GatewayServer : IAsyncDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = ProtocolInfo.MaxFrameBytes;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(20);

    private readonly GatewayOptions _options;
    private readonly TcpListener _listener;
    private readonly bool _lanAllowed;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private readonly ConcurrentDictionary<string, (long WindowStart, int Failures)> _failures = new(StringComparer.Ordinal);
    private readonly Task _acceptLoop;
    private long _requestIds;

    private GatewayServer(GatewayOptions options, TcpListener listener, bool lanAllowed)
    {
        _options = options;
        _listener = listener;
        _lanAllowed = lanAllowed;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>实际监听的端口。</summary>
    public int Port { get; }

    /// <summary>监听 <paramref name="port"/>(0 = 系统分配)。<paramref name="allowLan"/> 假 = 只绑回环。端口被占用抛 <see cref="SocketException"/>。</summary>
    public static GatewayServer Start(GatewayOptions options, int port, bool allowLan)
    {
        var listener = new TcpListener(allowLan ? IPAddress.Any : IPAddress.Loopback, port);
        listener.Start();
        return new GatewayServer(options, listener, allowLan);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }

            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            if (remote is null || !(IPAddress.IsLoopback(remote) || (_lanAllowed && LanTransport.IsLanAddress(remote))) || _connections.Count >= _options.MaxConnections)
            {
                client.Dispose(); // 公网来源 / 连接太多:直接断,不回任何字节
                continue;
            }

            var task = Task.Run(() => HandleAsync(client, remote));
            _connections[task] = 0;
            _ = task.ContinueWith(t => _connections.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(TcpClient client, IPAddress remote)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                timeout.CancelAfter(ReadTimeout);
                var request = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                if (request.Error is not null)
                {
                    await WriteErrorAsync(stream, request.Error.Value.Status, request.Error.Value.Code, request.Error.Value.Message).ConfigureAwait(false);
                    return;
                }

                await RouteAsync(stream, request, remote).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or ObjectDisposedException or JsonException)
            {
                // 对端断了 / 读超时:没有什么可回的
            }
        }
    }

    // ───────────────────────── 路由 ─────────────────────────

    private async Task RouteAsync(NetworkStream stream, HttpRequest request, IPAddress remote)
    {
        if (!request.Path.StartsWith("/api/", StringComparison.Ordinal))
        {
            await WriteErrorAsync(stream, 404, ErrorCodes.NotFound, "Not found.").ConfigureAwait(false);
            return;
        }

        var caller = Authenticate(request, remote);
        if (caller is null)
        {
            await WriteErrorAsync(stream, IsLimited(remote) ? 429 : 401, IsLimited(remote) ? ErrorCodes.RateLimited : ErrorCodes.Unauthorized,
                "A valid API key is required.", extraHeaders: "WWW-Authenticate: Bearer\r\n").ConfigureAwait(false);
            return;
        }

        var context = new CallContext($"gateway:{caller.KeyName}", caller.Role, Transports.Remote);
        var name = request.Path["/api/".Length..];
        if (request.Method == "GET" && name == "openapi.json")
        {
            var document = OpenApiExporter.Export(_options.Router.Methods, _options.Title, _options.Version);
            await WriteAsync(stream, 200, "application/json", Encoding.UTF8.GetBytes(document)).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && name.StartsWith("topics/", StringComparison.Ordinal))
        {
            await StreamTopicAsync(stream, Uri.UnescapeDataString(name["topics/".Length..]), request.Query, context).ConfigureAwait(false);
            return;
        }

        if (request.Method != "POST")
        {
            await WriteErrorAsync(stream, 405, ErrorCodes.InvalidRequest, "Use POST /api/<method>.", extraHeaders: "Allow: POST\r\n").ConfigureAwait(false);
            return;
        }

        await InvokeAsync(stream, Uri.UnescapeDataString(name), request, caller, context).ConfigureAwait(false);
    }

    private async Task InvokeAsync(NetworkStream stream, string name, HttpRequest request, GatewayCaller caller, CallContext context)
    {
        if (_options.Router.Find(name) is not { } def || !GatewayExposure.Exposes(def))
        {
            await WriteErrorAsync(stream, 404, ErrorCodes.NotFound, $"Unknown method '{name}'.").ConfigureAwait(false);
            return;
        }

        byte[] parameters;
        try
        {
            parameters = JsonMsgPack.FromJson(request.Body);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            await WriteErrorAsync(stream, 400, ErrorCodes.InvalidRequest, "The body is not valid JSON.").ConfigureAwait(false);
            return;
        }

        if (_options.Guard?.Invoke(def, parameters, caller) is { } denied)
        {
            await WriteProtocolErrorAsync(stream, new ProtocolError(denied.Code, denied.Message, denied.Retryable, null, denied.Details)).ConfigureAwait(false);
            return;
        }

        var frame = new RequestFrame((ulong)Interlocked.Increment(ref _requestIds), name, parameters, request.Header("idempotency-key"));
        ResponseFrame response;
        try
        {
            response = await _options.Router.InvokeAsync(frame, context, _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (response is { Ok: true })
        {
            using var body = new MemoryStream();
            body.Write("{\"ok\":true,\"result\":"u8);
            body.Write(response.Result is { Length: > 0 } result ? JsonMsgPack.ToJson(result) : "null"u8);
            body.Write("}"u8);
            await WriteAsync(stream, 200, "application/json", body.ToArray()).ConfigureAwait(false);
            return;
        }

        await WriteProtocolErrorAsync(stream, response.Error ?? new ProtocolError(ErrorCodes.Internal, "Internal error", false)).ConfigureAwait(false);
    }

    // ───────────────────────── SSE ─────────────────────────

    private async Task StreamTopicAsync(NetworkStream stream, string topic, string query, CallContext context)
    {
        long? cursor = null;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv[0] == "cursor" && kv.Length == 2 && long.TryParse(kv[1], out var parsed))
            {
                cursor = parsed;
            }
        }

        if (!_options.Topics.Knows(topic))
        {
            await WriteErrorAsync(stream, 404, ErrorCodes.NotFound, $"Unknown topic '{topic}'.").ConfigureAwait(false);
            return;
        }

        if (!_options.Topics.CanSubscribe(topic, context))
        {
            await WriteErrorAsync(stream, 403, ErrorCodes.Forbidden, $"The key's role cannot subscribe to '{topic}'.").ConfigureAwait(false);
            return;
        }

        var queue = Channel.CreateBounded<Frame>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        IDisposable? sourceSubscription = null;
        Func<Frame, ValueTask>? sink = null;
        IReadOnlyList<Frame>? initial = null;
        try
        {
            if (_options.Topics.FindSource(topic) is { } source)
            {
                sourceSubscription = source.Open(topic, cursor, new QueueWriter(context, queue));
                if (sourceSubscription is null)
                {
                    await WriteErrorAsync(stream, 404, ErrorCodes.NotFound, $"Unknown topic '{topic}'.").ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                sink = frame => queue.Writer.WriteAsync(frame);
                initial = _options.Topics.Subscribe(topic, cursor, sink);
                if (initial is null)
                {
                    sink = null;
                    await WriteErrorAsync(stream, 404, ErrorCodes.NotFound, $"Unknown topic '{topic}'.").ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (ProtocolException e)
        {
            await WriteProtocolErrorAsync(stream, new ProtocolError(e.Code, e.Message, e.Retryable, null, e.Details)).ConfigureAwait(false);
            return;
        }

        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: keep-alive\r\nX-Accel-Buffering: no\r\n\r\n"), _stopping.Token).ConfigureAwait(false);
            foreach (var frame in initial ?? [])
            {
                await WriteSseAsync(stream, frame).ConfigureAwait(false);
            }

            // 对端关了连接就收尾:读一个字节读到 0 = 关了
            using var gone = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            var watcher = Task.Run(async () =>
            {
                try
                {
                    var buffer = new byte[1];
                    while (await stream.ReadAsync(buffer, gone.Token).ConfigureAwait(false) > 0)
                    {
                    }
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
                {
                }

                await gone.CancelAsync().ConfigureAwait(false);
            });
            while (!gone.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(gone.Token);
                wait.CancelAfter(KeepAlive);
                try
                {
                    var frame = await queue.Reader.ReadAsync(wait.Token).ConfigureAwait(false);
                    await WriteSseAsync(stream, frame).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!gone.IsCancellationRequested)
                {
                    await stream.WriteAsync(": keepalive\n\n"u8.ToArray(), gone.Token).ConfigureAwait(false);
                }
            }

            await gone.CancelAsync().ConfigureAwait(false);
            await watcher.ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or ObjectDisposedException or ChannelClosedException)
        {
            // 订阅者走了
        }
        finally
        {
            if (sink is not null)
            {
                _options.Topics.Unsubscribe(topic, sink);
            }

            sourceSubscription?.Dispose();
            queue.Writer.TryComplete();
        }
    }

    private static async Task WriteSseAsync(NetworkStream stream, Frame frame)
    {
        using var body = new MemoryStream();
        switch (frame)
        {
            case SnapshotFrame snapshot:
                body.Write(Encoding.UTF8.GetBytes($"event: snapshot\nid: {snapshot.Seq}\ndata: {{\"topic\":{JsonSerializer.Serialize(snapshot.Topic, GatewayJson.Default.String)},\"seq\":{snapshot.Seq},\"state\":"));
                body.Write(JsonMsgPack.ToJson(snapshot.State));
                body.Write("}\n\n"u8);
                break;
            case EventFrame evt:
                body.Write(Encoding.UTF8.GetBytes($"event: evt\nid: {evt.Seq}\ndata: {{\"topic\":{JsonSerializer.Serialize(evt.Topic, GatewayJson.Default.String)},\"seq\":{evt.Seq},\"type\":{JsonSerializer.Serialize(evt.Type, GatewayJson.Default.String)},\"data\":"));
                body.Write(JsonMsgPack.ToJson(evt.Data));
                body.Write("}\n\n"u8);
                break;
            case ResyncFrame resync:
                body.Write(Encoding.UTF8.GetBytes($"event: resync\ndata: {{\"topic\":{JsonSerializer.Serialize(resync.Topic, GatewayJson.Default.String)},\"reason\":{JsonSerializer.Serialize(resync.Reason, GatewayJson.Default.String)}}}\n\n"));
                break;
            default:
                return;
        }

        await stream.WriteAsync(body.ToArray()).ConfigureAwait(false);
    }

    /// <summary>来源主题(<c>run/&lt;id&gt;</c>)的写入口:帧进有界队列,SSE 循环取走。队列满时增量丢弃(<see cref="TryWrite"/> 返回 false),不能丢的帧等位置。</summary>
    private sealed class QueueWriter(CallContext context, Channel<Frame> queue) : ITopicWriter
    {
        public CallContext Context { get; } = context;

        public void Write(Frame frame) => _ = queue.Writer.WriteAsync(frame);

        public bool TryWrite(Frame frame) => queue.Writer.TryWrite(frame);
    }

    // ───────────────────────── 认证 ─────────────────────────

    private GatewayCaller? Authenticate(HttpRequest request, IPAddress remote)
    {
        var header = request.Header("authorization");
        if (header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            RecordFailure(remote);
            return null;
        }

        var caller = _options.Auth.Authenticate(header["Bearer ".Length..].Trim());
        if (caller is null)
        {
            RecordFailure(remote);
        }

        return caller;
    }

    private bool IsLimited(IPAddress remote) =>
        _failures.TryGetValue(remote.ToString(), out var f) && f.Failures > _options.MaxAuthFailuresPerMinute && Now() - f.WindowStart < 60_000;

    private void RecordFailure(IPAddress remote)
    {
        var now = Now();
        _failures.AddOrUpdate(remote.ToString(), _ => (now, 1), (_, old) => now - old.WindowStart >= 60_000 ? (now, 1) : (old.WindowStart, old.Failures + 1));
    }

    private long Now() => _options.Clock.GetUtcNow().ToUnixTimeMilliseconds();

    // ───────────────────────── HTTP ─────────────────────────

    private sealed record HttpRequest(string Method, string Path, string Query, Dictionary<string, string> Headers, byte[] Body, (int Status, string Code, string Message)? Error = null)
    {
        public string? Header(string name) => Headers.GetValueOrDefault(name);
    }

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeaderBytes + 1];
        var length = 0;
        var end = -1;
        while (end < 0)
        {
            if (length == buffer.Length)
            {
                return Bad(431, ErrorCodes.TooLarge, "The request headers are too large.");
            }

            var read = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            length += read;
            end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
        }

        var head = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
        var line = head[0].Split(' ');
        if (line.Length != 3 || !line[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            return Bad(400, ErrorCodes.InvalidRequest, "Malformed request line.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in head.Skip(1))
        {
            var colon = h.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers[h[..colon].Trim()] = h[(colon + 1)..].Trim();
            }
        }

        var target = line[1];
        var q = target.IndexOf('?', StringComparison.Ordinal);
        var path = q < 0 ? target : target[..q];
        var query = q < 0 ? "" : target[(q + 1)..];
        var bodyLength = 0;
        if (headers.TryGetValue("content-length", out var cl) && (!int.TryParse(cl, out bodyLength) || bodyLength < 0))
        {
            return Bad(400, ErrorCodes.InvalidRequest, "Invalid Content-Length.");
        }

        if (headers.ContainsKey("transfer-encoding"))
        {
            return Bad(501, ErrorCodes.Unsupported, "Chunked request bodies are not supported; send Content-Length.");
        }

        if (bodyLength > MaxBodyBytes)
        {
            return Bad(413, ErrorCodes.TooLarge, "The request body is too large.");
        }

        var body = new byte[bodyLength];
        var have = Math.Min(bodyLength, length - (end + 4));
        Array.Copy(buffer, end + 4, body, 0, have);
        while (have < bodyLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(have), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            have += read;
        }

        return new HttpRequest(line[0], path, query, headers, body);

        static HttpRequest Bad(int status, string code, string message) => new("", "", "", [], [], (status, code, message));
    }

    private static string StatusText(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        413 => "Payload Too Large",
        429 => "Too Many Requests",
        431 => "Request Header Fields Too Large",
        501 => "Not Implemented",
        503 => "Service Unavailable",
        _ => "Internal Server Error",
    };

    /// <summary>协议错误码 → HTTP 状态码。<c>busy</c> 是 409(会话忙 / 有运行中的工作,可重试);<c>unavailable</c> 是 503。</summary>
    public static int StatusOf(string code) => code switch
    {
        ErrorCodes.InvalidRequest => 400,
        ErrorCodes.Unauthorized => 401,
        ErrorCodes.Forbidden => 403,
        ErrorCodes.NotFound => 404,
        ErrorCodes.Conflict => 409,
        ErrorCodes.Busy => 409,
        ErrorCodes.TooLarge => 413,
        ErrorCodes.RateLimited => 429,
        ErrorCodes.Unsupported => 501,
        ErrorCodes.Unavailable => 503,
        ErrorCodes.ClientOutdated => 426,
        _ => 500,
    };

    private static Task WriteProtocolErrorAsync(NetworkStream stream, ProtocolError error)
    {
        using var body = new MemoryStream();
        using (var json = new Utf8JsonWriter(body))
        {
            json.WriteStartObject();
            json.WriteBoolean("ok", false);
            json.WriteStartObject("error");
            json.WriteString("code", error.Code);
            json.WriteString("message", error.Message);
            json.WriteBoolean("retryable", error.Retryable);
            if (error.RetryAfterMs is { } after)
            {
                json.WriteNumber("retryAfterMs", after);
            }

            if (error.Details is { Length: > 0 } details)
            {
                json.WritePropertyName("details");
                json.WriteRawValue(JsonMsgPack.ToJson(details));
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        return WriteAsync(stream, StatusOf(error.Code), "application/json", body.ToArray());
    }

    private static Task WriteErrorAsync(NetworkStream stream, int status, string code, string message, string extraHeaders = "") =>
        WriteAsync(stream, status, "application/json", Encoding.UTF8.GetBytes(
            $"{{\"ok\":false,\"error\":{{\"code\":{JsonSerializer.Serialize(code, GatewayJson.Default.String)},\"message\":{JsonSerializer.Serialize(message, GatewayJson.Default.String)},\"retryable\":false}}}}"), extraHeaders);

    private static async Task WriteAsync(NetworkStream stream, int status, string contentType, byte[] body, string extraHeaders = "")
    {
        var head = $"HTTP/1.1 {status} {StatusText(status)}\r\nContent-Type: {contentType}; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n{extraHeaders}\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
            await Task.WhenAll(_connections.Keys).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 每个连接自己吞异常
        }

        _stopping.Dispose();
    }
}

/// <summary>网关自己的 JSON 序列化上下文(只序列化字符串:零反射)。</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class GatewayJson : System.Text.Json.Serialization.JsonSerializerContext;
