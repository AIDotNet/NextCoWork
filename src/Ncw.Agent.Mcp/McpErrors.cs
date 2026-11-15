namespace Ncw.Agent.Mcp;

/// <summary>稳定的 <c>mcp.*</c> key(控制端按它翻译成界面文字;异常消息和诊断是英文,给日志和排错)。本层不产出界面文字。</summary>
public static class McpErrorKeys
{
    // ── 配置校验 ──
    public const string InvalidId = "mcp.invalidId";
    public const string NameInvalid = "mcp.nameInvalid";
    public const string DescriptionTooLong = "mcp.descriptionTooLong";
    public const string WorkspaceInvalid = "mcp.workspaceInvalid";
    public const string TooManyItems = "mcp.tooManyItems";
    public const string SecretNameInvalid = "mcp.secretNameInvalid";
    public const string CommandRequired = "mcp.commandRequired";
    public const string CommandInvalid = "mcp.commandInvalid";
    public const string TransportFieldMismatch = "mcp.transportFieldMismatch";
    public const string UrlInvalid = "mcp.urlInvalid";
    public const string UrlScheme = "mcp.urlScheme";
    public const string UrlCredentials = "mcp.urlCredentials";
    public const string NotFound = "mcp.notFound";
    public const string NotConnected = "mcp.notConnected";
    public const string Unsupported = "mcp.unsupported";

    // ── 连接 ──
    public const string CommandNotFound = "mcp.connect.commandNotFound";
    public const string StartFailed = "mcp.connect.startFailed";
    public const string Timeout = "mcp.connect.timeout";
    public const string ProcessExited = "mcp.connect.processExited";
    public const string Protocol = "mcp.connect.protocol";
    public const string ProtocolVersion = "mcp.connect.protocolVersion";
    public const string Http = "mcp.connect.http";
    public const string Unauthorized = "mcp.connect.unauthorized";
    public const string EnvironmentUnavailable = "mcp.connect.environmentUnavailable";
    public const string ProjectMissing = "mcp.connect.projectMissing";
    public const string ListTools = "mcp.connect.listTools";
    public const string Network = "mcp.connect.network";
    public const string Failed = "mcp.connect.failed";
    public const string SessionExpired = "mcp.connect.sessionExpired";
    public const string Closed = "mcp.connect.closed";

    // ── 工具被拒绝接入的原因 ──
    public const string ToolNameInvalid = "mcp.tool.nameInvalid";
    public const string ToolSchemaInvalid = "mcp.tool.schemaInvalid";
    public const string ToolSchemaTooLarge = "mcp.tool.schemaTooLarge";
    public const string ToolDuplicate = "mcp.tool.duplicate";
    public const string ToolConflict = "mcp.tool.conflict";
}

/// <summary>
/// 一次 MCP 失败。<see cref="Key"/> 是稳定 key,<see cref="Parameters"/> 是不含任何敏感信息的结构化参数(状态码、退出码、秒数),
/// <see cref="Exception.Message"/> 是英文诊断(<b>可能含服务器给的文本</b>:出站前必须过 <see cref="McpScrubber"/>)。
/// </summary>
public sealed class McpException(string key, string message, IReadOnlyDictionary<string, string>? parameters = null, Exception? inner = null) : Exception(message, inner)
{
    public string Key { get; } = key;

    /// <summary>传输的诊断尾部(stdio:服务器 stderr)。<b>可能含密钥值或路径</b>:只能脱敏后进诊断和 owner 才看得到的测试结果。</summary>
    public string? Diagnostics { get; set; }

    public IReadOnlyDictionary<string, string> Parameters { get; } = parameters ?? new Dictionary<string, string>();

    /// <summary>关闭原因:传输已经异常关闭时给出它的失败(同一个 key / 参数 / 诊断,新的异常对象),否则是通用的「连接已关闭」。</summary>
    public static McpException ClosedBecause(Task completion)
    {
        if (completion.IsFaulted && completion.Exception?.InnerException is McpException reason)
        {
            return new McpException(reason.Key, reason.Message, reason.Parameters) { Diagnostics = reason.Diagnostics };
        }

        return With(McpErrorKeys.Closed, "The MCP server connection is closed.");
    }

    public static McpException With(string key, string message, params (string Name, string Value)[] parameters) =>
        new(key, message, parameters.ToDictionary(p => p.Name, p => p.Value));
}

/// <summary>服务器回了一个 JSON-RPC 错误(<c>error</c> 成员)。工具调用里它是一次工具失败,不是崩溃。</summary>
public sealed class McpRpcException(long code, string message) : Exception(message)
{
    public long Code { get; } = code;

    public const long MethodNotFound = -32601;
}
