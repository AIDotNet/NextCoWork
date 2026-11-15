using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Ncw.Agent.Browser.Tests;

/// <summary>一条收到的 CDP 命令。</summary>
public sealed record CdpRequest(long Id, string Method, string? SessionId, JsonElement Params)
{
    public string? Str(string name) => Params.ValueKind == JsonValueKind.Object && Params.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public double? Num(string name) => Params.ValueKind == JsonValueKind.Object && Params.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : null;
}

public sealed class CdpFailure(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// 假 CDP 服务(HttpListener + WebSocket,真的 TCP):脚本化应答、记录收到的命令、能主动推事件。
/// <see cref="InstallBrowser"/> 装上一个够用的「浏览器」:建标签、附着、导航(带 DOMContentLoaded 事件)、隔离世界、截图、求值(由测试提供)。
/// </summary>
public sealed class FakeCdpServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, Func<CdpRequest, string?>> _handlers = new();
    private readonly ConcurrentQueue<CdpRequest> _log = new();
    private readonly List<WebSocket> _sockets = [];
    private readonly Lock _gate = new();
    private readonly Task _accept;
    private int _targets;

    public FakeCdpServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Endpoint = new Uri($"ws://127.0.0.1:{port}/devtools/browser/fake");
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _accept = Task.Run(AcceptLoop);
    }

    public Uri Endpoint { get; }

    public IReadOnlyList<CdpRequest> Requests => [.. _log];

    public IEnumerable<CdpRequest> Of(string method) => Requests.Where(r => r.Method == method);

    /// <summary>求值回调:收到 <c>Runtime.evaluate</c> 的表达式,返回 <c>result.value</c> 的 JSON 文本(null = undefined)。</summary>
    public Func<string, string?> Eval { get; set; } = _ => null;

    /// <summary>每次 <c>Page.navigate</c> 的结局:null = 正常(新文档,发 DOMContentLoaded);否则按返回值作为 result JSON 原样回。</summary>
    public Func<CdpRequest, string?>? NavigateResult { get; set; }

    public int ConnectionCount { get; private set; }

    public void Handle(string method, Func<CdpRequest, string?> handler) => _handlers[method] = handler;

    public void Handle(string method, string resultJson) => _handlers[method] = _ => resultJson;

    public Task PushAsync(string method, string paramsJson, string? sessionId = null)
    {
        var message = new StringBuilder("{\"method\":").Append(Q(method));
        if (sessionId is not null)
        {
            message.Append(",\"sessionId\":\"").Append(sessionId).Append('"');
        }

        message.Append(",\"params\":").Append(paramsJson).Append('}');
        return BroadcastAsync(message.ToString());
    }

    /// <summary>主动断开所有连接(模拟浏览器崩溃 / 进程退出)。</summary>
    public async Task DropConnectionsAsync()
    {
        WebSocket[] sockets;
        lock (_gate)
        {
            sockets = [.. _sockets];
        }

        foreach (var socket in sockets)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "gone", cts.Token);
            }
            catch
            {
                socket.Abort();
            }
        }
    }

    public void InstallBrowser()
    {
        Handle("Target.createTarget", _ => $"{{\"targetId\":\"T{Interlocked.Increment(ref _targets)}\"}}");
        Handle("Target.attachToTarget", r => $"{{\"sessionId\":\"S-{r.Str("targetId")}\"}}");
        Handle("Target.closeTarget", "{\"success\":true}");
        Handle("Target.getTargetInfo", r => $"{{\"targetInfo\":{{\"targetId\":\"{r.Str("targetId")}\",\"url\":\"{CurrentUrl}\",\"title\":\"{CurrentTitle}\"}}}}");
        Handle("Browser.setDownloadBehavior", "{}");
        Handle("Page.enable", "{}");
        Handle("Emulation.setDeviceMetricsOverride", "{}");
        Handle("Page.getFrameTree", "{\"frameTree\":{\"frame\":{\"id\":\"F1\"}}}");
        Handle("Page.createIsolatedWorld", _ => $"{{\"executionContextId\":{Interlocked.Increment(ref _worlds)}}}");
        Handle("Page.handleJavaScriptDialog", "{}");
        Handle("Input.dispatchMouseEvent", "{}");
        Handle("Input.dispatchKeyEvent", "{}");
        Handle("Input.insertText", "{}");
        Handle("Page.captureScreenshot", _ => $"{{\"data\":\"{Convert.ToBase64String(Png(1280, 720))}\"}}");
        Handle("Page.navigate", request =>
        {
            if (NavigateResult?.Invoke(request) is { } custom)
            {
                return custom;
            }

            CurrentUrl = request.Str("url") ?? "";
            _ = Task.Run(async () =>
            {
                await Task.Delay(5);
                await PushAsync("Page.frameNavigated", "{\"frame\":{\"id\":\"F1\"}}", request.SessionId);
                await PushAsync("Page.domContentEventFired", "{\"timestamp\":1}", request.SessionId);
            });
            return "{\"frameId\":\"F1\",\"loaderId\":\"L1\"}";
        });
        Handle("Runtime.evaluate", request =>
        {
            var expression = request.Str("expression") ?? "";
            var value = Eval(expression);
            return value is null ? "{\"result\":{\"type\":\"undefined\"}}" : $"{{\"result\":{{\"type\":\"object\",\"value\":{value}}}}}";
        });
    }

    internal static string Q(string text) => System.Text.Json.Nodes.JsonValue.Create(text).ToJsonString();

    private int _worlds;

    public string CurrentUrl { get; set; } = "about:blank";

    public string CurrentTitle { get; set; } = "";

    /// <summary>一张合法头的 PNG(只有 IHDR;解码器不会用它,工具只读尺寸)。</summary>
    public static byte[] Png(int width, int height)
    {
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), 13);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }

            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                continue;
            }

            HttpListenerWebSocketContext ws;
            try
            {
                ws = await context.AcceptWebSocketAsync(null);
            }
            catch
            {
                continue;
            }

            lock (_gate)
            {
                _sockets.Add(ws.WebSocket);
                ConnectionCount++;
            }

            _ = Task.Run(() => Serve(ws.WebSocket));
        }
    }

    private async Task Serve(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open && !_stop.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, _stop.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                using var doc = JsonDocument.Parse(message.ToArray());
                var root = doc.RootElement;
                var request = new CdpRequest(
                    root.GetProperty("id").GetInt64(),
                    root.GetProperty("method").GetString()!,
                    root.TryGetProperty("sessionId", out var s) ? s.GetString() : null,
                    root.TryGetProperty("params", out var p) ? p.Clone() : default);
                _log.Enqueue(request);
                var reply = Answer(request);
                await SendAsync(socket, reply);
            }
        }
        catch
        {
            // 连接断了:结束这个会话
        }
    }

    private string Answer(CdpRequest request)
    {
        if (!_handlers.TryGetValue(request.Method, out var handler))
        {
            return $"{{\"id\":{request.Id},\"error\":{{\"code\":-32601,\"message\":\"'{request.Method}' wasn't found\"}}}}";
        }

        try
        {
            var result = handler(request) ?? "{}";
            return $"{{\"id\":{request.Id},\"result\":{result}}}";
        }
        catch (CdpFailure e)
        {
            return $"{{\"id\":{request.Id},\"error\":{{\"code\":{e.Code},\"message\":{Q(e.Message)}}}}}";
        }
    }

    private async Task BroadcastAsync(string text)
    {
        WebSocket[] sockets;
        lock (_gate)
        {
            sockets = [.. _sockets.Where(s => s.State == WebSocketState.Open)];
        }

        foreach (var socket in sockets)
        {
            await SendAsync(socket, text);
        }
    }

    private readonly SemaphoreSlim _send = new(1, 1);

    private async Task SendAsync(WebSocket socket, string text)
    {
        await _send.WaitAsync();
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, _stop.Token);
        }
        catch
        {
        }
        finally
        {
            _send.Release();
        }
    }

    /// <summary>用任意字节原样发一条文本消息(坏 JSON / 超大消息的测试用)。</summary>
    public Task SendRawAsync(string text) => BroadcastAsync(text);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Abort();
        foreach (var socket in _sockets.ToArray())
        {
            socket.Abort();
        }

        try
        {
            await _accept;
        }
        catch
        {
        }
    }
}
