using System.Text;
using Ncw.Agent.Kernel.Loop;

namespace Ncw.Agent.Mcp;

/// <summary>两个 HTTP 传输共用的小工具。</summary>
internal static class HttpTransportSupport
{
    public static Dictionary<string, string> BaseHeaders(IReadOnlyDictionary<string, string> configured)
    {
        // 用户配置的头先放(可能是 Authorization / API key),协议头后放,覆盖同名的:用户头改不了 Content-Type / Accept / Mcp-* 这些协议头。
        var headers = new Dictionary<string, string>(configured, StringComparer.OrdinalIgnoreCase);
        return headers;
    }

    public static async Task<byte[]> ReadBoundedAsync(Stream body, int max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        for (;;)
        {
            var read = await body.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > max)
            {
                throw McpException.With(McpErrorKeys.Protocol, "A response from the MCP server exceeded the size limit.", ("reason", "bodyTooLarge"));
            }

            buffer.Write(chunk, 0, read);
        }
    }

    /// <summary>把一个 JSON 体(单个消息或批量数组)逐条交给回调。读不懂的忽略(服务器的问题不该拖垮连接)。</summary>
    public static async ValueTask DeliverAsync(byte[] json, Func<ReadOnlyMemory<byte>, ValueTask>? received)
    {
        if (received is null || json.Length == 0)
        {
            return;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    await received(Encoding.UTF8.GetBytes(item.GetRawText())).ConfigureAwait(false);
                }

                return;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        await received(json).ConfigureAwait(false);
    }

    public static McpException StatusError(int status, string what) => status switch
    {
        401 or 403 => McpException.With(McpErrorKeys.Unauthorized, $"{what}: the server rejected the credentials (HTTP {status}).", ("status", status.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        _ => McpException.With(McpErrorKeys.Http, $"{what}: HTTP {status}.", ("status", status.ToString(System.Globalization.CultureInfo.InvariantCulture))),
    };
}

/// <summary>
/// Streamable HTTP 传输(MCP 2025-03-26 / 2025-06-18):每条消息一个 POST,<c>Accept: application/json, text/event-stream</c>;
/// 响应是单个 JSON 或一条 SSE 流(流里可以夹带服务器的通知和请求);<c>Mcp-Session-Id</c> 由 initialize 的响应给出、之后每个请求都带;
/// 握手完成后带 <c>MCP-Protocol-Version</c> 头,并(服务器支持时)开一条 GET 事件流收服务器主动发的通知(<c>list_changed</c>);结束时 DELETE 会话。
/// 会话过期(带会话 id 的请求得到 404)= 异常关闭,由连接管理重连(重新握手)。
/// </summary>
public sealed class StreamableHttpTransport(IMcpHttp http, Uri url, IReadOnlyDictionary<string, string> headers, McpOptions options, ILoopDiagnostics? log = null) : IMcpTransport
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _pumps = [];
    private string? _sessionId;
    private string? _protocolVersion;
    private int _disposed;

    public Func<ReadOnlyMemory<byte>, ValueTask>? Received { get; set; }

    public Task Completion => _completion.Task;

    public string? Diagnostics => null;

    /// <summary>服务器分配的会话 id(测试 / 诊断用)。</summary>
    public string? SessionId => Volatile.Read(ref _sessionId);

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    private Dictionary<string, string> RequestHeaders(bool post)
    {
        var h = HttpTransportSupport.BaseHeaders(headers);
        h["Accept"] = post ? "application/json, text/event-stream" : "text/event-stream";
        if (Volatile.Read(ref _sessionId) is { } session)
        {
            h["Mcp-Session-Id"] = session;
        }

        if (Volatile.Read(ref _protocolVersion) is { } version)
        {
            h["MCP-Protocol-Version"] = version;
        }

        return h;
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        if (_completion.Task.IsCompleted)
        {
            throw McpException.ClosedBecause(_completion.Task);
        }

        var hadSession = Volatile.Read(ref _sessionId) is not null;
        var response = await http.SendAsync(new McpHttpRequest("POST", url, RequestHeaders(post: true), message.ToArray(), "application/json"), ct).ConfigureAwait(false);
        var keep = false;
        try
        {
            if (response.Header("Mcp-Session-Id") is { Length: > 0 } session)
            {
                Interlocked.CompareExchange(ref _sessionId, session, null);
            }

            if (response.Status == 404 && hadSession)
            {
                var expired = McpException.With(McpErrorKeys.SessionExpired, "The MCP session expired (HTTP 404); reconnecting starts a new session.");
                _completion.TrySetException(expired);
                throw expired;
            }

            if (response.Status is 202 or 204)
            {
                return;
            }

            if (response.Status != 200)
            {
                throw HttpTransportSupport.StatusError(response.Status, "The MCP server rejected the request");
            }

            if (response.MediaType == "text/event-stream")
            {
                // 流可能一直开到响应送完(甚至更久):后台泵,不占着这次发送
                keep = true;
                var pump = PumpAsync(response, _lifetime.Token);
                lock (_pumps)
                {
                    _pumps.RemoveAll(t => t.IsCompleted);
                    _pumps.Add(pump);
                }

                return;
            }

            var body = await HttpTransportSupport.ReadBoundedAsync(response.Body, options.MaxMessageBytes, ct).ConfigureAwait(false);
            await HttpTransportSupport.DeliverAsync(body, Received).ConfigureAwait(false);
        }
        finally
        {
            if (!keep)
            {
                await response.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task PumpAsync(McpHttpResponse response, CancellationToken ct)
    {
        try
        {
            await foreach (var ev in SseReader.ReadAsync(response.Body, options.MaxMessageBytes, ct).ConfigureAwait(false))
            {
                if ((ev.Type is null or "message") && ev.Data.Length > 0 && Received is { } received)
                {
                    await HttpTransportSupport.DeliverAsync(Encoding.UTF8.GetBytes(ev.Data), received).ConfigureAwait(false);
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (!ct.IsCancellationRequested)
            {
                log?.Warn("An MCP SSE response stream ended abnormally.", e);
            }
        }
        finally
        {
            await response.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void OnInitialized(string protocolVersion)
    {
        Volatile.Write(ref _protocolVersion, protocolVersion);
        var listener = ListenAsync(_lifetime.Token);
        lock (_pumps)
        {
            _pumps.Add(listener);
        }
    }

    /// <summary>服务器事件流(GET):收 <c>list_changed</c> 之类的主动通知。服务器不支持(405 / 404 / 4xx)就静静放弃;流断了按 <c>Last-Event-ID</c> 续,连续失败 3 次放弃。</summary>
    private async Task ListenAsync(CancellationToken ct)
    {
        string? lastId = null;
        var failures = 0;
        while (!ct.IsCancellationRequested && failures < 3)
        {
            McpHttpResponse? response = null;
            try
            {
                var h = RequestHeaders(post: false);
                if (lastId is not null)
                {
                    h["Last-Event-ID"] = lastId;
                }

                response = await http.SendAsync(new McpHttpRequest("GET", url, h), ct).ConfigureAwait(false);
                if (response.Status != 200 || response.MediaType != "text/event-stream")
                {
                    return;
                }

                failures = 0;
                await foreach (var ev in SseReader.ReadAsync(response.Body, options.MaxMessageBytes, ct).ConfigureAwait(false))
                {
                    lastId = ev.Id ?? lastId;
                    if ((ev.Type is null or "message") && ev.Data.Length > 0 && Received is { } received)
                    {
                        await HttpTransportSupport.DeliverAsync(Encoding.UTF8.GetBytes(ev.Data), received).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is McpException or IOException or HttpRequestException)
            {
                failures++;
                log?.Warn("The MCP server event stream failed.", e);
            }
            finally
            {
                if (response is not null)
                {
                    await response.DisposeAsync().ConfigureAwait(false);
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (Volatile.Read(ref _sessionId) is not null && !_completion.Task.IsCompleted)
        {
            // 礼貌地结束会话(失败 / 405 都无所谓)
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await using var response = await http.SendAsync(new McpHttpRequest("DELETE", url, RequestHeaders(post: true)), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is McpException or OperationCanceledException or HttpRequestException)
            {
            }
        }

        Task[] pumps;
        lock (_pumps)
        {
            pumps = [.. _pumps];
        }

        await Task.WhenAny(Task.WhenAll(pumps), Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        _completion.TrySetResult();
        http.Dispose();
        _lifetime.Dispose();
    }
}

/// <summary>
/// 旧版 HTTP+SSE 传输(MCP 2024-11-05,2.x 的 <c>sse</c>):GET 事件流;第一个 <c>endpoint</c> 事件给出 POST 地址(<b>必须与配置地址同源</b>,否则拒绝——
/// 服务器不能把我们的请求和请求头引到别处),之后 <c>message</c> 事件是服务器发来的 JSON-RPC。发送 = POST 到那个地址。流结束 = 关闭。
/// </summary>
public sealed class LegacySseTransport(IMcpHttp http, Uri url, IReadOnlyDictionary<string, string> headers, McpOptions options, ILoopDiagnostics? log = null) : IMcpTransport
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Uri> _endpoint = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private McpHttpResponse? _stream;
    private Task _pump = Task.CompletedTask;
    private int _disposed;

    public Func<ReadOnlyMemory<byte>, ValueTask>? Received { get; set; }

    public Task Completion => _completion.Task;

    public string? Diagnostics => null;

    public void OnInitialized(string protocolVersion)
    {
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var h = HttpTransportSupport.BaseHeaders(headers);
        h["Accept"] = "text/event-stream";
        _stream = await http.SendAsync(new McpHttpRequest("GET", url, h), ct).ConfigureAwait(false);
        if (_stream.Status != 200)
        {
            var status = _stream.Status;
            await _stream.DisposeAsync().ConfigureAwait(false);
            throw HttpTransportSupport.StatusError(status, "The MCP SSE endpoint rejected the connection");
        }

        if (_stream.MediaType != "text/event-stream")
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            throw McpException.With(McpErrorKeys.Protocol, "The MCP SSE endpoint did not return an event stream.", ("reason", "notEventStream"));
        }

        _pump = PumpAsync(_stream);
        await _endpoint.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task PumpAsync(McpHttpResponse stream)
    {
        try
        {
            await foreach (var ev in SseReader.ReadAsync(stream.Body, options.MaxMessageBytes, _lifetime.Token).ConfigureAwait(false))
            {
                if (ev.Type == "endpoint")
                {
                    if (!Uri.TryCreate(url, ev.Data.Trim(), out var endpoint) || !HttpClientMcp.SameOrigin(url, endpoint))
                    {
                        _endpoint.TrySetException(McpException.With(McpErrorKeys.Protocol, "The MCP SSE endpoint event pointed to a different origin; it was refused.", ("reason", "endpointOrigin")));
                        continue;
                    }

                    _endpoint.TrySetResult(endpoint);
                }
                else if ((ev.Type is null or "message") && ev.Data.Length > 0 && Received is { } received)
                {
                    await HttpTransportSupport.DeliverAsync(Encoding.UTF8.GetBytes(ev.Data), received).ConfigureAwait(false);
                }
            }

            _endpoint.TrySetException(McpException.With(McpErrorKeys.Closed, "The MCP SSE stream ended before the endpoint event."));
            _completion.TrySetResult();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            _completion.TrySetResult();
        }
        catch (Exception e) when (e is McpException or IOException or HttpRequestException)
        {
            var error = e as McpException ?? new McpException(McpErrorKeys.Network, "The MCP SSE stream failed.", inner: e);
            _endpoint.TrySetException(error);
            _completion.TrySetException(error);
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        if (_completion.Task.IsCompleted || !_endpoint.Task.IsCompletedSuccessfully)
        {
            throw McpException.ClosedBecause(_completion.Task);
        }

        var h = HttpTransportSupport.BaseHeaders(headers);
        await using var response = await http.SendAsync(new McpHttpRequest("POST", _endpoint.Task.Result, h, message.ToArray(), "application/json"), ct).ConfigureAwait(false);
        if (response.Status is < 200 or >= 300)
        {
            throw HttpTransportSupport.StatusError(response.Status, "The MCP server rejected the request");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_stream is { } stream)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        await Task.WhenAny(_pump, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        _completion.TrySetResult();
        http.Dispose();
        _lifetime.Dispose();
        _ = log;
    }
}
