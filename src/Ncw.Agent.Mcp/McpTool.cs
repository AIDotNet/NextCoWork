using System.Buffers;
using System.Text;
using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;

namespace Ncw.Agent.Mcp;

/// <summary>
/// 一个 MCP 工具(<see cref="IAgentTool"/>)。<b>本层唯一的不可信输入收口</b>:服务器交回来的 <c>name</c> / <c>description</c> / <c>inputSchema</c> / <c>annotations</c> 全部是别人写的,
/// 而它们会进系统提示词——工具投毒的入口。所以每一处判断朝同一个方向:<b>凡是「服务器说了算」会削弱一道防线的地方,一律不采信服务器的说法</b>:
/// <list type="bullet">
/// <item><c>ReadOnly = false</c>、<c>Destructive = true</c> 一律保守,不看服务器的 <c>readOnlyHint</c> / <c>destructiveHint</c>(服务器不能靠自报只读绕过用户的审批档位;免审批要由用户的 allow 规则明确放行);</item>
/// <item><c>NeedsNetwork</c> 由<b>我们的传输配置</b>决定:远程(sse / streamable-http)= true,stdio = false(本地子进程;它自己出不出网我们不可能知道);</item>
/// <item>名字过白名单(拼 internal id 之前),描述去控制字符、截断 4096,schema 超过体积上限拒绝。</item>
/// </list>
/// 权限完全走权限扩展(<c>InternalId</c> 就是规则里的工具名)。
/// </summary>
public sealed class McpTool : IAgentTool
{
    public const string Prefix = "mcp__";

    private readonly McpConnection _connection;
    private readonly string _serverId;
    private readonly string _serverName;
    private readonly string _toolName;
    private readonly McpOptions _options;
    private readonly string? _workspaceId;
    private readonly Func<ToolContext, string?>? _workspaceOf;
    private readonly Func<string, string> _scrub;
    private readonly string _environmentJson;

    internal McpTool(
        McpServerConfig config, McpToolDescriptor descriptor, JsonElement schema, McpConnection connection, IExecutionEnvironment environment,
        McpOptions options, Func<ToolContext, string?>? workspaceOf, Func<string, string> scrub)
    {
        _connection = connection;
        _serverId = config.Id;
        _serverName = config.Name;
        _toolName = descriptor.Name;
        _options = options;
        _workspaceId = config.WorkspaceId;
        _workspaceOf = workspaceOf;
        _scrub = scrub;
        _environmentJson = EnvironmentJson(environment);
        Info = new ToolSpec(
            InternalIdOf(config.Id, descriptor.Name),
            Describe(config, descriptor),
            schema,
            new ToolTraits(ReadOnly: false, Destructive: true, NeedsNetwork: !config.IsStdio));
    }

    public ToolSpec Info { get; }

    /// <summary><c>mcp__&lt;server&gt;__&lt;tool&gt;</c>(03 §1;权限规则按它匹配,转录里也是它)。</summary>
    public static string InternalIdOf(string serverId, string toolName) => $"{Prefix}{serverId}__{toolName}";

    public bool IsEnabled(ToolContext ctx) =>
        !_connection.IsClosed && (_workspaceId is null || _workspaceOf is null || _workspaceOf(ctx) == _workspaceId);

    public ToolArgumentCheck PrepareArguments(JsonElement input) => input.ValueKind switch
    {
        JsonValueKind.Object => ToolArgumentCheck.Valid(input),
        JsonValueKind.Undefined or JsonValueKind.Null => ToolArgumentCheck.Valid(JsonDocument.Parse("{}").RootElement.Clone()),
        _ => ToolArgumentCheck.Invalid("Invalid arguments: the input for an MCP tool must be a JSON object."),
    };

    public async ValueTask<ToolResult> ExecuteAsync(ToolInvocation call, ToolContext ctx, CancellationToken ct)
    {
        var check = PrepareArguments(call.Input);
        if (!check.IsValid)
        {
            return ToolResult.Error(check.Error!, Details());
        }

        // 项目绑定的服务器只服务那个项目(2.x ★:快照过滤之外再挡一次,防止旧快照里的工具被别的项目的会话调到)
        if (_workspaceId is not null && _workspaceOf is not null && _workspaceOf(ctx) != _workspaceId)
        {
            return ToolResult.Error($"The MCP server \"{_serverName}\" belongs to a different project and cannot be used from this session.", Details());
        }

        if (_connection.IsClosed)
        {
            return ToolResult.Error(
                $"The MCP server \"{_serverName}\" is not connected right now (it may be restarting). Retry later, or tell the user the server is unavailable.", Details());
        }

        McpCallResult result;
        try
        {
            // ★ 墙钟上限:一台握手成功却不回包的服务器,以前能把整个 Run 永久挂住。到点:取消远端那次调用(notifications/cancelled),收成一次普通的工具失败(进转录,模型换条路)。
            // ★ 用户按停止 = 中止,原样抛:Run 要靠它收成 aborted,不能伪装成工具失败。
            result = await _connection.CallToolAsync(_toolName, check.Input, _options.CallTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (McpException e) when (e.Key == McpErrorKeys.Timeout)
        {
            return ToolResult.Error(
                $"MCP tool {_toolName} did not respond within {_options.CallTimeout.TotalSeconds:0.###} seconds, so the call was cancelled. The server may be stuck; try a different approach or tell the user.", Details());
        }
        catch (McpException e)
        {
            return ToolResult.Error($"MCP tool {_toolName} could not be called: {Clip(_scrub(e.Message), 300)}", Details());
        }
        catch (McpRpcException e)
        {
            return ToolResult.Error($"MCP tool {_toolName} failed: {Clip(_scrub(e.Message), 2000)}", Details());
        }

        var text = _scrub(Flatten(result.Content));
        if (result.IsError)
        {
            return Finish(text.Length == 0 ? $"MCP tool {_toolName} reported a failure but gave no explanation." : text, isError: true);
        }

        return Finish(text.Length == 0 ? $"MCP tool {_toolName} completed without returning any content." : text, isError: false);
    }

    private ToolResult Finish(string text, bool isError)
    {
        var truncated = text.Length > _options.MaxResultChars;
        long? original = null;
        if (truncated)
        {
            original = Encoding.UTF8.GetByteCount(text);
            var keep = _options.MaxResultChars;
            if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
            {
                keep--;
            }

            text = text[..keep] + $"\n\n[Output truncated: {text.Length - keep} more characters were omitted.]";
        }

        return new ToolResult([new Ncw.Agent.Llm.TextPart(text)], Details(), IsError: isError, Truncated: truncated, OriginalBytes: original);
    }

    /// <summary>
    /// MCP 内容块拼成一段文本。工具结果只承载文本,所以图片 / 音频 / 资源块<b>照实说明自己是什么,而不是被静默丢掉</b>:
    /// 丢掉的症状是模型看到一个空结果,换个参数把同一个调用重试三次;说明清楚的话它知道「这个工具返回的是图,我读不了」并告诉用户。
    /// </summary>
    public static string Flatten(IReadOnlyList<McpContentBlock> content)
    {
        var parts = new List<string>();
        foreach (var block in content)
        {
            switch (block.Type)
            {
                case "text":
                    parts.Add(block.Text ?? "");
                    break;
                case "image" or "audio":
                    parts.Add(
                        $"[This tool returned {(block.Type == "image" ? "an image" : "an audio clip")} ({Clip(block.MimeType ?? "unknown type", 80)}). "
                        + "Only text can be passed back to you, so it was not forwarded; ask the user to look at it directly if it matters.]");
                    break;
                case "resource":
                    parts.Add(block.Text is { } text
                        ? $"[Resource {Clip(block.Uri ?? "(no uri)", 300)}]\n{text}"
                        : $"[Resource {Clip(block.Uri ?? "(no uri)", 300)} is binary content with no text form; it was not forwarded.]");
                    break;
                case "resource_link":
                    parts.Add($"[Resource link {Clip(block.Name ?? "", 200)} {Clip(block.Uri ?? "(no uri)", 300)} — the content was not fetched.]");
                    break;
                default:
                    // SDK 会随协议演进加新块,静默忽略等于内容凭空少一段
                    parts.Add($"[A content block of an unknown type ({Clip(PromptText.StripControlChars(block.Type), 40)}) was skipped.]");
                    break;
            }
        }

        return string.Join("\n\n", parts);
    }

    private static string Clip(string text, int max) => PromptText.Clamp(text, max);

    /// <summary>描述前缀带上服务器名:模型同时看着七台服务器的四十个工具,<c>create_issue</c> 属于哪台是它必须知道的事。</summary>
    private static string Describe(McpServerConfig config, McpToolDescriptor tool)
    {
        var head = string.IsNullOrWhiteSpace(config.Description) ? config.Name : $"{config.Name} · {config.Description}";
        var body = tool.Description is { Length: > 0 } d ? d : "(this server provided no description for the tool)";
        return PromptText.Clamp(PromptText.StripControlChars($"[MCP:{head}]\n{body}"), 4096);
    }

    private JsonElement Details()
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("kind", "mcp");
            w.WriteString("server", _serverId);
            w.WriteString("serverName", _serverName);
            w.WriteString("tool", _toolName);
            w.WritePropertyName("environment");
            w.WriteRawValue(_environmentJson);
            w.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    /// <summary>执行环境标识(设备 + 环境 + 名字 + 类型,AGENTS.md §5):服务器在哪里跑,调用就发生在哪里。</summary>
    private static string EnvironmentJson(IExecutionEnvironment environment)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("deviceId", environment.Id.DeviceId);
            w.WriteString("environment", environment.Id.Environment);
            w.WriteString("name", environment.DisplayName);
            w.WriteString("type", environment.Kind == EnvironmentKind.Local ? "local" : "ssh");
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>工具被拒绝接入的记录(原因是稳定 key,设置页照实显示「这台服务器有 2 个工具没接上」)。</summary>
public sealed record McpRejectedTool(string Name, string ReasonKey);

/// <summary>把服务器列出的工具变成 <see cref="McpTool"/>(或拒绝)。</summary>
internal static class McpToolFactory
{
    private static readonly System.Text.RegularExpressions.Regex ToolName = new("^[a-zA-Z0-9_.-]{1,128}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly JsonElement EmptyObjectSchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();

    public static (List<McpTool> Tools, List<McpRejectedTool> Rejected) Build(
        McpServerConfig config, IReadOnlyList<McpToolDescriptor> descriptors, McpConnection connection, IExecutionEnvironment environment,
        McpOptions options, Func<ToolContext, string?>? workspaceOf, Func<string, string> scrub)
    {
        var tools = new List<McpTool>();
        var rejected = new List<McpRejectedTool>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            // ★ 名字这一关必须在拼 internal id 之前过:internal id 会进转录、进日志、进规则匹配;在入口拒掉比在三个下游各自防一遍便宜。
            if (!ToolName.IsMatch(descriptor.Name))
            {
                rejected.Add(new McpRejectedTool(Printable(descriptor.Name), McpErrorKeys.ToolNameInvalid));
                continue;
            }

            if (!seen.Add(descriptor.Name))
            {
                rejected.Add(new McpRejectedTool(descriptor.Name, McpErrorKeys.ToolDuplicate));
                continue;
            }

            if (tools.Count >= options.MaxToolsPerServer)
            {
                rejected.Add(new McpRejectedTool(descriptor.Name, McpErrorKeys.TooManyItems));
                continue;
            }

            var schema = EmptyObjectSchema;
            if (descriptor.HasSchema && descriptor.InputSchema.ValueKind == JsonValueKind.Object)
            {
                if (descriptor.InputSchema.GetRawText().Length > options.MaxSchemaBytes)
                {
                    rejected.Add(new McpRejectedTool(descriptor.Name, McpErrorKeys.ToolSchemaTooLarge));
                    continue;
                }

                if (descriptor.InputSchema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "object")
                {
                    schema = descriptor.InputSchema;
                }
            }

            tools.Add(new McpTool(config, descriptor, schema, connection, environment, options, workspaceOf, scrub));
        }

        return (tools, rejected);
    }

    private static string Printable(string name) => PromptText.Clamp(PromptText.StripControlChars(name), 64);
}
