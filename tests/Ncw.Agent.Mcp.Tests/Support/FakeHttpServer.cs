using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Ncw.Agent.Mcp.Tests.Support;

public enum HttpMode
{
    /// <summary>Streamable HTTP,每个 POST 回单个 JSON。</summary>
    Json,

    /// <summary>Streamable HTTP,每个 POST 回一条 SSE 流(先夹一条通知,再回响应)。</summary>
    Sse,

    /// <summary>旧版 HTTP+SSE:GET /sse 开事件流并发 endpoint 事件,POST /messages 回 202,响应走事件流。</summary>
    Legacy,
}

public sealed record SeenRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>
/// 测试内的真实 HTTP 服务器(<see cref="HttpListener"/> 回环端口):包着 <see cref="FakeMcpServer"/>,覆盖 Streamable HTTP 的两种响应形态、
/// 会话 id、协议版本头、服务器事件流(<c>list_changed</c>)、鉴权失败,以及旧版 HTTP+SSE。生产的 <see cref="HttpClientMcp"/> 原样跑在它上面。
/// </summary>
public sealed class FakeHttpServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly ConcurrentDictionary<string, StreamWriter> _streams = new();
    private readonly object _streamGate = new();
    private StreamWriter? _legacyStream;
    private StreamWriter? _serverStream;
    private int _sessions;

    /// <param name="anyHost">true = 监听所有 Host 头(<c>http://+:port/</c>;Windows 上要管理员权限,只有「经环境的 TCP 通道、Host 头不变」那条用例用它)。</param>
    public FakeHttpServer(FakeMcpServer mcp, HttpMode mode = HttpMode.Json, bool anyHost = false)
    {
        Mcp = mcp;
        Mode = mode;
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        Port = port;
        _listener.Prefixes.Add(anyHost ? $"http://+:{port}/" : $"http://127.0.0.1:{port}/");
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
        mcp.Outbound = PushAsync;
    }

    public FakeMcpServer Mcp { get; }

    public HttpMode Mode { get; }

    public int Port { get; }

    public Uri Url => new($"http://127.0.0.1:{Port}/{(Mode == HttpMode.Legacy ? "sse" : "mcp")}");

    /// <summary>要求的 Authorization 值;null = 不鉴权。不匹配回 401。</summary>
    public string? RequiredAuthorization { get; set; }

    /// <summary>Streamable HTTP 是否发会话 id。</summary>
    public bool UseSessions { get; set; } = true;

    /// <summary>是否支持 GET 服务器事件流(false = 405)。</summary>
    public bool ServerStream { get; set; }

    /// <summary>让服务端把会话判为过期(带会话 id 的请求回 404)。</summary>
    public bool ExpireSessions { get; set; }

    public ConcurrentQueue<SeenRequest> Requests { get; } = new();

    public IEnumerable<SeenRequest> Posts => Requests.Where(r => r.Method == "POST");

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            var headers = request.Headers.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => request.Headers[k]!, StringComparer.OrdinalIgnoreCase);
            string body;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            Requests.Enqueue(new SeenRequest(request.HttpMethod, request.Url!.AbsolutePath, headers, body));
            if (RequiredAuthorization is not null && headers.GetValueOrDefault("Authorization") != RequiredAuthorization)
            {
                response.StatusCode = 401;
                response.Close();
                return;
            }

            if (Mode == HttpMode.Legacy)
            {
                await HandleLegacyAsync(request, response, body).ConfigureAwait(false);
                return;
            }

            await HandleStreamableAsync(request, response, headers, body).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException)
        {
        }
    }

    private async Task HandleStreamableAsync(HttpListenerRequest request, HttpListenerResponse response, Dictionary<string, string> headers, string body)
    {
        if (request.HttpMethod == "DELETE")
        {
            response.StatusCode = 200;
            response.Close();
            return;
        }

        if (request.HttpMethod == "GET")
        {
            if (!ServerStream)
            {
                response.StatusCode = 405;
                response.Close();
                return;
            }

            response.StatusCode = 200;
            response.ContentType = "text/event-stream";
            response.SendChunked = true;
            var writer = new StreamWriter(response.OutputStream, new UTF8Encoding(false)) { AutoFlush = true };
            await writer.WriteAsync(": open\n\n").ConfigureAwait(false);
            lock (_streamGate)
            {
                _serverStream = writer;
            }

            try
            {
                await Task.Delay(Timeout.Infinite, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            return;
        }

        var isInitialize = body.Contains("\"method\":\"initialize\"", StringComparison.Ordinal);
        if (UseSessions && !isInitialize)
        {
            if (ExpireSessions)
            {
                response.StatusCode = 404;
                response.Close();
                return;
            }

            if (!headers.ContainsKey("Mcp-Session-Id"))
            {
                response.StatusCode = 400;
                response.Close();
                return;
            }
        }

        if (UseSessions && isInitialize)
        {
            response.Headers["Mcp-Session-Id"] = $"sess-{Interlocked.Increment(ref _sessions)}";
        }

        var replies = new List<string>();
        await Mcp.HandleAsync(body, line =>
        {
            lock (replies)
            {
                replies.Add(line);
            }

            return Task.CompletedTask;
        }).ConfigureAwait(false);
        var isNotification = !body.Contains("\"id\":", StringComparison.Ordinal) || body.Contains("\"method\":\"notifications/", StringComparison.Ordinal);
        if (isNotification)
        {
            response.StatusCode = 202;
            response.Close();
            return;
        }

        // 请求在后台处理:等响应出来(最多 10 秒;「不回话」的用例由客户端的超时先到)
        var deadline = Environment.TickCount64 + 10_000;
        while (true)
        {
            lock (replies)
            {
                if (replies.Count > 0)
                {
                    break;
                }
            }

            if (_stop.IsCancellationRequested || Environment.TickCount64 > deadline)
            {
                response.Abort();
                return;
            }

            await Task.Delay(5).ConfigureAwait(false);
        }

        string reply;
        lock (replies)
        {
            reply = replies[0];
        }

        response.StatusCode = 200;
        if (Mode == HttpMode.Sse)
        {
            response.ContentType = "text/event-stream";
            response.SendChunked = true;
            var writer = new StreamWriter(response.OutputStream, new UTF8Encoding(false)) { AutoFlush = true };
            await writer.WriteAsync("event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\",\"params\":{\"level\":\"info\",\"data\":\"hello\"}}\n\n").ConfigureAwait(false);
            await writer.WriteAsync($"event: message\ndata: {reply}\n\n").ConfigureAwait(false);
            response.Close();
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(reply);
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    private async Task HandleLegacyAsync(HttpListenerRequest request, HttpListenerResponse response, string body)
    {
        if (request.HttpMethod == "GET")
        {
            response.StatusCode = 200;
            response.ContentType = "text/event-stream";
            response.SendChunked = true;
            var writer = new StreamWriter(response.OutputStream, new UTF8Encoding(false)) { AutoFlush = true };
            lock (_streamGate)
            {
                _legacyStream = writer;
            }

            await writer.WriteAsync("event: endpoint\ndata: /messages?sid=1\n\n").ConfigureAwait(false);
            try
            {
                await Task.Delay(Timeout.Infinite, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            return;
        }

        await Mcp.HandleAsync(body, async line => await PushAsync(line).ConfigureAwait(false)).ConfigureAwait(false);
        response.StatusCode = 202;
        response.Close();
    }

    /// <summary>服务器主动发的消息:旧版传输走事件流;Streamable HTTP 走 GET 事件流(没开就丢)。</summary>
    private async Task PushAsync(string line)
    {
        StreamWriter? writer;
        lock (_streamGate)
        {
            writer = Mode == HttpMode.Legacy ? _legacyStream : _serverStream;
        }

        if (writer is null)
        {
            return;
        }

        try
        {
            await writer.WriteAsync($"event: message\ndata: {line}\n\n").ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Abort();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception e) when (e is ObjectDisposedException or HttpListenerException)
        {
        }
    }
}
