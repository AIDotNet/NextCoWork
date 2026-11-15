using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ncw.Agent.Mcp.Tests.Support;

/// <summary>假服务器上的一个工具。<see cref="Call"/> 返回 <c>tools/call</c> 的 result(null = 永远不回话)。</summary>
public sealed class FakeTool(string name, Func<JsonObject, CancellationToken, Task<JsonNode?>>? call = null)
{
    public string Name { get; } = name;

    public string? Description { get; init; } = "a tool";

    public JsonNode? InputSchema { get; init; } = new JsonObject { ["type"] = "object" };

    /// <summary>服务器自报的 annotations(readOnlyHint 等):我们一律不采信。</summary>
    public JsonNode? Annotations { get; init; }

    public Func<JsonObject, CancellationToken, Task<JsonNode?>> Call { get; } = call ?? ((_, _) => Task.FromResult<JsonNode?>(Text("ok")));

    public static JsonObject Text(string text, bool isError = false) =>
        new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["isError"] = isError };

    public JsonObject Describe()
    {
        var o = new JsonObject { ["name"] = Name };
        if (Description is not null)
        {
            o["description"] = Description;
        }

        if (InputSchema is not null)
        {
            o["inputSchema"] = InputSchema.DeepClone();
        }

        if (Annotations is not null)
        {
            o["annotations"] = Annotations.DeepClone();
        }

        return o;
    }
}

/// <summary>
/// 测试内的假 MCP 服务器:只有协议逻辑,不关心字节怎么流(stdio 的内存管道、HTTP 的回环监听器都把消息交给 <see cref="HandleAsync"/>)。
/// 它是一个如实遵守协议的替身(不用官方 SDK 的服务端:要表演「不回话」「乱序」「分页」这些异常行为,SDK 服务端做不到)。
/// </summary>
public sealed class FakeMcpServer
{
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _inflight = new();

    public List<FakeTool> Tools { get; } = [];

    /// <summary>每页多少个工具(0 = 不分页)。</summary>
    public int PageSize { get; set; }

    /// <summary>initialize 回的协议版本;null = 回客户端请求的那个。</summary>
    public string? ProtocolVersion { get; set; }

    public string ServerName { get; set; } = "fake-server";

    public bool ToolsCapability { get; set; } = true;

    /// <summary>收到的消息(方法名),按到达顺序。</summary>
    public ConcurrentQueue<string> Methods { get; } = new();

    /// <summary>收到 <c>notifications/cancelled</c> 的 requestId。</summary>
    public ConcurrentQueue<long> Cancelled { get; } = new();

    /// <summary>收到的 initialize 参数(原文)。</summary>
    public string? InitializeParams { get; private set; }

    /// <summary>客户端回给服务器的响应(对服务器发起的请求,例如 ping)。</summary>
    public ConcurrentQueue<string> ClientResponses { get; } = new();

    /// <summary><c>tools/list</c> 回一个 JSON-RPC 错误(握手成功但列不出工具)。</summary>
    public bool ListError { get; set; }

    public int ListCalls;

    /// <summary>服务器主动发消息(通知 / 请求)的出口:由承载(管道 / SSE 流)设置。</summary>
    public Func<string, Task>? Outbound { get; set; }

    public List<JsonObject> Resources { get; } = [];

    public List<JsonObject> Prompts { get; } = [];

    public async Task NotifyAsync(string method, JsonObject? parameters = null)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        await Outbound!(message.ToJsonString()).ConfigureAwait(false);
    }

    public Task SendRequestAsync(long id, string method) =>
        Outbound!(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method }.ToJsonString());

    /// <summary>处理客户端发来的一条消息。响应经 <paramref name="reply"/> 发出(请求在后台处理,所以响应可以乱序)。</summary>
    public Task HandleAsync(string line, Func<string, Task> reply)
    {
        var message = JsonNode.Parse(line)!.AsObject();
        var method = message["method"]?.GetValue<string>();
        if (method is null)
        {
            ClientResponses.Enqueue(line); // 客户端对我们请求的响应(ping 的回复等)
            return Task.CompletedTask;
        }

        Methods.Enqueue(method);
        var id = message["id"]?.GetValue<long>();
        if (id is null)
        {
            if (method == "notifications/cancelled" && message["params"]?["requestId"]?.GetValue<long>() is { } requestId)
            {
                Cancelled.Enqueue(requestId);
                if (_inflight.TryRemove(requestId, out var cts))
                {
                    cts.Cancel();
                }
            }

            return Task.CompletedTask;
        }

        _ = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource();
            _inflight[id.Value] = cts;
            try
            {
                var result = await ResultAsync(method, message["params"]?.AsObject(), cts.Token).ConfigureAwait(false);
                if (result is null)
                {
                    return; // 不回话
                }

                if (result is JsonObject { } o && o.ContainsKey("__error"))
                {
                    await reply(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = id.Value,
                        ["error"] = new JsonObject { ["code"] = o["code"]!.GetValue<long>(), ["message"] = o["__error"]!.GetValue<string>() },
                    }.ToJsonString()).ConfigureAwait(false);
                    return;
                }

                await reply(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.Value, ["result"] = result }.ToJsonString()).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _inflight.TryRemove(id.Value, out _);
            }
        });
        return Task.CompletedTask;
    }

    private static JsonObject Error(long code, string message) => new() { ["__error"] = message, ["code"] = code };

    private async Task<JsonNode?> ResultAsync(string method, JsonObject? p, CancellationToken ct)
    {
        switch (method)
        {
            case "initialize":
                InitializeParams = p?.ToJsonString();
                var caps = new JsonObject();
                if (ToolsCapability)
                {
                    caps["tools"] = new JsonObject { ["listChanged"] = true };
                }

                caps["resources"] = new JsonObject();
                caps["prompts"] = new JsonObject();
                return new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion ?? p?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
                    ["capabilities"] = caps,
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = "1.2.3" },
                };
            case "ping":
                return new JsonObject();
            case "tools/list":
                Interlocked.Increment(ref ListCalls);
                if (ListError)
                {
                    return Error(-32000, "boom while listing");
                }

                if (!ToolsCapability)
                {
                    return Error(-32601, "Method not found");
                }

                var start = int.TryParse(p?["cursor"]?.GetValue<string>(), out var c) ? c : 0;
                var count = PageSize <= 0 ? Tools.Count : PageSize;
                var page = Tools.Skip(start).Take(count).Select(t => (JsonNode)t.Describe()).ToArray();
                var result = new JsonObject { ["tools"] = new JsonArray(page) };
                if (start + count < Tools.Count)
                {
                    result["nextCursor"] = (start + count).ToString();
                }

                return result;
            case "tools/call":
                var tool = Tools.FirstOrDefault(t => t.Name == p?["name"]?.GetValue<string>());
                if (tool is null)
                {
                    return Error(-32602, "Unknown tool");
                }

                return await tool.Call(p?["arguments"]?.AsObject() ?? [], ct).ConfigureAwait(false);
            case "resources/list":
                return new JsonObject { ["resources"] = new JsonArray(Resources.Select(r => (JsonNode)r.DeepClone()).ToArray()) };
            case "resources/read":
                return new JsonObject { ["contents"] = new JsonArray(new JsonObject { ["uri"] = p?["uri"]?.GetValue<string>(), ["text"] = "resource text", ["mimeType"] = "text/plain" }) };
            case "prompts/list":
                return new JsonObject { ["prompts"] = new JsonArray(Prompts.Select(r => (JsonNode)r.DeepClone()).ToArray()) };
            case "prompts/get":
                return new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "prompt text" } }) };
            default:
                return Error(-32601, "Method not found");
        }
    }

    public static JsonDocument Doc(string json) => JsonDocument.Parse(json);
}
