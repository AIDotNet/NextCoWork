using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using Ncw.Agent.Kernel.Loop;

namespace Ncw.Agent.Mcp;

/// <summary>
/// JSON-RPC 2.0 会话(客户端一侧):请求 / 响应按 id 关联(乱序到达、夹带通知都没问题)、请求取消(<c>notifications/cancelled</c>)、墙钟超时、
/// 服务器发来的通知与请求(<c>ping</c> 回 <c>{}</c>;其它请求回 method-not-found——我们没有声明任何客户端能力)。
/// 传输关闭时所有未决请求一起失败。全部 JSON 手写(<see cref="Utf8JsonWriter"/> / <see cref="JsonDocument"/>),不走反射序列化。
/// </summary>
public sealed class McpSession : IAsyncDisposable
{
    private readonly IMcpTransport _transport;
    private readonly ILoopDiagnostics? _log;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private long _nextId;
    private int _closed;
    private McpException? _closedWith;

    public McpSession(IMcpTransport transport, ILoopDiagnostics? log = null)
    {
        _transport = transport;
        _log = log;
        transport.Received = OnReceivedAsync;
        _ = WatchAsync();
    }

    /// <summary>收到服务器通知(方法名, 参数的克隆;无参数时 default)。在传输的读取线程上调用,别阻塞。</summary>
    public event Action<string, JsonElement>? Notification;

    /// <summary>传输关闭(带原因;null = 正常)。</summary>
    public Task Closed => _transport.Completion;

    public IMcpTransport Transport => _transport;

    private async Task WatchAsync()
    {
        Exception? reason = null;
        try
        {
            await _transport.Completion.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            reason = e;
        }

        FailAll(reason as McpException ?? McpException.With(McpErrorKeys.Closed, "The MCP server connection is closed."));
    }

    private void FailAll(McpException error)
    {
        Volatile.Write(ref _closedWith, Volatile.Read(ref _closedWith) ?? error);
        Interlocked.Exchange(ref _closed, 1);
        foreach (var (id, pending) in _pending)
        {
            if (_pending.TryRemove(id, out _))
            {
                pending.TrySetException(error);
            }
        }
    }

    /// <summary>发一个请求并等响应。<paramref name="timeout"/> 到点:发 <c>notifications/cancelled</c>(远端那次调用真的被取消),抛 <see cref="McpErrorKeys.Timeout"/>;
    /// 调用方取消:同样发取消通知,原样抛 <see cref="OperationCanceledException"/>。服务器回的 JSON-RPC 错误抛 <see cref="McpRpcException"/>。</summary>
    public async Task<JsonElement> RequestAsync(string method, Action<Utf8JsonWriter>? writeParams, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _closed) != 0)
        {
            var reason = Volatile.Read(ref _closedWith);
            throw reason is null
                ? McpException.With(McpErrorKeys.Closed, "The MCP server connection is closed.")
                : new McpException(reason.Key, reason.Message, reason.Parameters) { Diagnostics = reason.Diagnostics };
        }

        var id = Interlocked.Increment(ref _nextId);
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = pending;
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            var message = Build(w =>
            {
                w.WriteString("jsonrpc", "2.0");
                w.WriteNumber("id", id);
                w.WriteString("method", method);
                if (writeParams is not null)
                {
                    w.WriteStartObject("params");
                    writeParams(w);
                    w.WriteEndObject();
                }
            });
            await _transport.SendAsync(message, linked.Token).ConfigureAwait(false);
            return await pending.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            _pending.TryRemove(id, out _);
            if (method != "initialize")
            {
                await CancelRemoteAsync(id, ct.IsCancellationRequested ? "cancelled by the client" : "timed out").ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            throw McpException.With(McpErrorKeys.Timeout, $"The MCP server did not answer '{method}' within {timeout.TotalSeconds:0.###} seconds.", ("method", method), ("seconds", timeout.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task CancelRemoteAsync(long requestId, string reason)
    {
        try
        {
            using var brief = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await NotifyAsync("notifications/cancelled", w =>
            {
                w.WriteNumber("requestId", requestId);
                w.WriteString("reason", reason);
            }, brief.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is McpException or OperationCanceledException)
        {
            // 尽力而为:连接已经不在了就算了
        }
    }

    public async Task NotifyAsync(string method, Action<Utf8JsonWriter>? writeParams, CancellationToken ct)
    {
        var message = Build(w =>
        {
            w.WriteString("jsonrpc", "2.0");
            w.WriteString("method", method);
            if (writeParams is not null)
            {
                w.WriteStartObject("params");
                writeParams(w);
                w.WriteEndObject();
            }
        });
        await _transport.SendAsync(message, ct).ConfigureAwait(false);
    }

    private static byte[] Build(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private async ValueTask OnReceivedAsync(ReadOnlyMemory<byte> bytes)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bytes);
        }
        catch (JsonException e)
        {
            _log?.Warn("The MCP server sent a message that is not JSON; it was ignored.", e);
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var hasId = root.TryGetProperty("id", out var idElement) && idElement.ValueKind is JsonValueKind.Number or JsonValueKind.String;
            if (root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String)
            {
                var method = methodElement.GetString()!;
                var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;
                if (!hasId)
                {
                    try
                    {
                        Notification?.Invoke(method, parameters);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        _log?.Warn($"A handler for the MCP notification '{method}' failed.", e);
                    }

                    return;
                }

                await AnswerServerRequestAsync(method, idElement.Clone()).ConfigureAwait(false);
                return;
            }

            if (!hasId || !TryReadId(idElement, out var id) || !_pending.TryRemove(id, out var pending))
            {
                return; // 没人等的响应(已超时 / 已取消):丢弃
            }

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var code = error.TryGetProperty("code", out var c) && c.TryGetInt64(out var n) ? n : -32603;
                var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "Unknown error";
                pending.TrySetException(new McpRpcException(code, message));
                return;
            }

            pending.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
        }
    }

    private static bool TryReadId(JsonElement element, out long id)
    {
        id = 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out id),
            JsonValueKind.String => long.TryParse(element.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out id),
            _ => false,
        };
    }

    private async Task AnswerServerRequestAsync(string method, JsonElement id)
    {
        var message = Build(w =>
        {
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            id.WriteTo(w);
            if (method == "ping")
            {
                w.WriteStartObject("result");
                w.WriteEndObject();
            }
            else
            {
                w.WriteStartObject("error");
                w.WriteNumber("code", McpRpcException.MethodNotFound);
                w.WriteString("message", "Method not found");
                w.WriteEndObject();
            }
        });
        try
        {
            using var brief = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _transport.SendAsync(message, brief.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is McpException or OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        FailAll(McpException.With(McpErrorKeys.Closed, "The MCP server connection is closed."));
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
