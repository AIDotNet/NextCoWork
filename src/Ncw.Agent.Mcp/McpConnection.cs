using System.Text.Json;
using Ncw.Agent.Kernel.Loop;

namespace Ncw.Agent.Mcp;

/// <summary>服务器列出的一个工具(原样,未消毒)。</summary>
public sealed record McpToolDescriptor(string Name, string? Description, JsonElement InputSchema, bool HasSchema);

/// <summary>工具结果里的一个内容块(<c>text</c> / <c>image</c> / <c>audio</c> / <c>resource</c> / <c>resource_link</c> / 其它)。</summary>
public sealed record McpContentBlock(string Type, string? Text = null, string? MimeType = null, string? Uri = null, string? Name = null);

public sealed record McpCallResult(IReadOnlyList<McpContentBlock> Content, bool IsError);

public sealed record McpResourceInfo(string Uri, string Name, string? Description, string? MimeType);

public sealed record McpPromptInfo(string Name, string? Description, IReadOnlyList<string> Arguments);

/// <summary>握手的结果:服务器名 / 版本 / 协商出的协议版本 / 能力。</summary>
public sealed record McpServerHello(string? Name, string? Version, string ProtocolVersion, bool Tools, bool Resources, bool Prompts);

/// <summary>
/// 一条已握手的连接:<c>initialize</c> / <c>notifications/initialized</c> 之后的 <c>tools/list</c>(含分页)、<c>tools/call</c>、<c>resources/list|read</c>、<c>prompts/list|get</c>。
/// 工具集变化通知(<c>notifications/tools/list_changed</c>)经 <see cref="ToolsListChanged"/> 抛出,由连接管理重列。
/// </summary>
public sealed class McpConnection : IAsyncDisposable
{
    /// <summary>我们能说的协议版本,新的在前。服务器在 initialize 里回一个:在表里就用它,不在表里就失败。</summary>
    public static readonly string[] SupportedVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    public const string ClientName = "NextCoWork";

    private readonly McpSession _session;
    private readonly McpOptions _options;

    private McpConnection(McpSession session, McpServerHello hello, McpOptions options)
    {
        _session = session;
        Hello = hello;
        _options = options;
        session.Notification += (method, _) =>
        {
            if (method == "notifications/tools/list_changed")
            {
                ToolsListChanged?.Invoke();
            }
        };
    }

    public McpServerHello Hello { get; }

    public event Action? ToolsListChanged;

    /// <summary>传输关闭时完成(异常 = 异常关闭)。</summary>
    public Task Closed => _session.Closed;

    public bool IsClosed => _session.Closed.IsCompleted;

    public string? TransportDiagnostics => _session.Transport.Diagnostics;

    /// <summary>
    /// 启动传输并握手。任何一步失败都清理传输再抛 <see cref="McpException"/>(调用方不用管半开的连接)。
    /// </summary>
    public static async Task<McpConnection> OpenAsync(IMcpTransport transport, McpOptions options, string clientVersion, ILoopDiagnostics? log, CancellationToken ct)
    {
        var session = new McpSession(transport, log);
        try
        {
            await transport.StartAsync(ct).ConfigureAwait(false);
            var result = await session.RequestAsync("initialize", w =>
            {
                w.WriteString("protocolVersion", SupportedVersions[0]);
                w.WriteStartObject("capabilities");
                w.WriteEndObject();
                w.WriteStartObject("clientInfo");
                w.WriteString("name", ClientName);
                w.WriteString("version", clientVersion);
                w.WriteEndObject();
            }, options.ConnectTimeout, ct).ConfigureAwait(false);

            var version = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("protocolVersion", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            if (Array.IndexOf(SupportedVersions, version) < 0)
            {
                throw McpException.With(
                    McpErrorKeys.ProtocolVersion,
                    $"The server answered with protocol version '{Clip(version)}', which this client does not support.",
                    ("version", Clip(version)));
            }

            string? name = null, serverVersion = null;
            if (result.TryGetProperty("serverInfo", out var info) && info.ValueKind == JsonValueKind.Object)
            {
                name = Text(info, "name");
                serverVersion = Text(info, "version");
            }

            var capabilities = result.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Object ? caps : default;
            var hello = new McpServerHello(
                name is null ? null : Clip(name), serverVersion is null ? null : Clip(serverVersion), version,
                Has(capabilities, "tools"), Has(capabilities, "resources"), Has(capabilities, "prompts"));

            await session.NotifyAsync("notifications/initialized", null, ct).ConfigureAwait(false);
            transport.OnInitialized(version);
            return new McpConnection(session, hello, options);
        }
        catch (Exception e) when (e is McpRpcException)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new McpException(McpErrorKeys.Protocol, $"The server rejected the initialize handshake: {Clip(e.Message)}", inner: e);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static bool Has(JsonElement caps, string name) => caps.ValueKind == JsonValueKind.Object && caps.TryGetProperty(name, out _);

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Clip(string s) => s.Length <= 200 ? s : s[..200];

    // ───────────────────────── tools ─────────────────────────

    /// <summary>列工具(翻页到底;页数 / 总数有上限)。服务器没有 tools 能力或回 method-not-found = 空表。</summary>
    public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken ct)
    {
        var tools = new List<McpToolDescriptor>();
        string? cursor = null;
        for (var page = 0; page < _options.MaxListPages; page++)
        {
            JsonElement result;
            try
            {
                result = await _session.RequestAsync("tools/list", WriteCursor(cursor), _options.ListToolsTimeout, ct).ConfigureAwait(false);
            }
            catch (McpRpcException e) when (e.Code == McpRpcException.MethodNotFound)
            {
                return tools;
            }
            catch (McpRpcException e)
            {
                throw new McpException(McpErrorKeys.ListTools, $"The server could not list its tools: {Clip(e.Message)}", inner: e);
            }

            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("tools", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var tool in array.EnumerateArray())
                {
                    if (tool.ValueKind == JsonValueKind.Object && Text(tool, "name") is { } name)
                    {
                        var hasSchema = tool.TryGetProperty("inputSchema", out var schema);
                        tools.Add(new McpToolDescriptor(name, Text(tool, "description"), hasSchema ? schema.Clone() : default, hasSchema));
                    }

                    if (tools.Count >= _options.MaxToolsPerServer + 64)
                    {
                        return tools; // 失控的服务器:够了(多出来的由上层拒绝并记原因)
                    }
                }
            }

            cursor = result.ValueKind == JsonValueKind.Object ? Text(result, "nextCursor") : null;
            if (string.IsNullOrEmpty(cursor))
            {
                break;
            }
        }

        return tools;
    }

    private static Action<Utf8JsonWriter>? WriteCursor(string? cursor) =>
        cursor is null ? null : w => w.WriteString("cursor", cursor);

    /// <summary>
    /// 调一个工具。<paramref name="arguments"/> <b>原样转发,不在这里校验</b>(schema 是服务器给的,校验它的人也该是服务器:
    /// 我们照着再实现一遍,只会在两边理解不一致时出现「模型按 schema 传了参,却被我们拒了」)。
    /// 服务器的 JSON-RPC 错误抛 <see cref="McpRpcException"/>,超时 / 关闭抛 <see cref="McpException"/>,调用方取消抛 <see cref="OperationCanceledException"/>。
    /// </summary>
    public async Task<McpCallResult> CallToolAsync(string name, JsonElement arguments, TimeSpan timeout, CancellationToken ct)
    {
        var result = await _session.RequestAsync("tools/call", w =>
        {
            w.WriteString("name", name);
            w.WritePropertyName("arguments");
            if (arguments.ValueKind == JsonValueKind.Object)
            {
                arguments.WriteTo(w);
            }
            else
            {
                w.WriteStartObject();
                w.WriteEndObject();
            }
        }, timeout, ct).ConfigureAwait(false);
        return ParseCallResult(result);
    }

    public static McpCallResult ParseCallResult(JsonElement result)
    {
        var blocks = new List<McpContentBlock>();
        var isError = false;
        if (result.ValueKind == JsonValueKind.Object)
        {
            isError = result.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;
            if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var type = Text(block, "type") ?? "";
                    switch (type)
                    {
                        case "text":
                            blocks.Add(new McpContentBlock(type, Text(block, "text") ?? ""));
                            break;
                        case "image" or "audio":
                            blocks.Add(new McpContentBlock(type, MimeType: Text(block, "mimeType")));
                            break;
                        case "resource" when block.TryGetProperty("resource", out var resource) && resource.ValueKind == JsonValueKind.Object:
                            blocks.Add(new McpContentBlock(type, Text(resource, "text"), Text(resource, "mimeType"), Text(resource, "uri")));
                            break;
                        case "resource_link":
                            blocks.Add(new McpContentBlock(type, Uri: Text(block, "uri"), Name: Text(block, "name"), MimeType: Text(block, "mimeType")));
                            break;
                        default:
                            blocks.Add(new McpContentBlock(type));
                            break;
                    }
                }
            }
        }

        return new McpCallResult(blocks, isError);
    }

    // ───────────────────────── resources / prompts ─────────────────────────

    public async Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct)
    {
        var list = new List<McpResourceInfo>();
        foreach (var item in await ListPagedAsync("resources/list", "resources", ct).ConfigureAwait(false))
        {
            if (Text(item, "uri") is { } uri)
            {
                list.Add(new McpResourceInfo(Clip(uri), Clip(Text(item, "name") ?? uri), Text(item, "description") is { } d ? Clip(d) : null, Text(item, "mimeType")));
            }
        }

        return list;
    }

    /// <summary>读一个资源的文本(二进制内容返回说明占位,不转达)。</summary>
    public async Task<IReadOnlyList<McpContentBlock>> ReadResourceAsync(string uri, CancellationToken ct)
    {
        var result = await _session.RequestAsync("resources/read", w => w.WriteString("uri", uri), _options.ListToolsTimeout, ct).ConfigureAwait(false);
        var blocks = new List<McpContentBlock>();
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("contents", out var contents) && contents.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in contents.EnumerateArray())
            {
                if (c.ValueKind == JsonValueKind.Object)
                {
                    blocks.Add(new McpContentBlock("resource", Text(c, "text"), Text(c, "mimeType"), Text(c, "uri") ?? uri));
                }
            }
        }

        return blocks;
    }

    public async Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct)
    {
        var list = new List<McpPromptInfo>();
        foreach (var item in await ListPagedAsync("prompts/list", "prompts", ct).ConfigureAwait(false))
        {
            if (Text(item, "name") is { } name)
            {
                var args = item.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Array
                    ? a.EnumerateArray().Select(x => Text(x, "name")).OfType<string>().Select(Clip).ToList()
                    : [];
                list.Add(new McpPromptInfo(Clip(name), Text(item, "description") is { } d ? Clip(d) : null, args));
            }
        }

        return list;
    }

    /// <summary>取一个提示(<c>prompts/get</c>)的消息文本。</summary>
    public async Task<IReadOnlyList<string>> GetPromptAsync(string name, IReadOnlyDictionary<string, string>? arguments, CancellationToken ct)
    {
        var result = await _session.RequestAsync("prompts/get", w =>
        {
            w.WriteString("name", name);
            if (arguments is { Count: > 0 })
            {
                w.WriteStartObject("arguments");
                foreach (var (k, v) in arguments)
                {
                    w.WriteString(k, v);
                }

                w.WriteEndObject();
            }
        }, _options.ListToolsTimeout, ct).ConfigureAwait(false);
        var texts = new List<string>();
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in messages.EnumerateArray())
            {
                if (m.ValueKind == JsonValueKind.Object && m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Object && Text(c, "text") is { } text)
                {
                    texts.Add(text);
                }
            }
        }

        return texts;
    }

    private async Task<List<JsonElement>> ListPagedAsync(string method, string property, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        string? cursor = null;
        for (var page = 0; page < _options.MaxListPages; page++)
        {
            JsonElement result;
            try
            {
                result = await _session.RequestAsync(method, WriteCursor(cursor), _options.ListToolsTimeout, ct).ConfigureAwait(false);
            }
            catch (McpRpcException e) when (e.Code == McpRpcException.MethodNotFound)
            {
                return items;
            }
            catch (McpRpcException e)
            {
                throw new McpException(McpErrorKeys.Protocol, $"The server rejected {method}: {Clip(e.Message)}", inner: e);
            }

            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array)
            {
                items.AddRange(array.EnumerateArray().Select(x => x.Clone()));
            }

            cursor = result.ValueKind == JsonValueKind.Object ? Text(result, "nextCursor") : null;
            if (string.IsNullOrEmpty(cursor) || items.Count > 2000)
            {
                break;
            }
        }

        return items;
    }

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
