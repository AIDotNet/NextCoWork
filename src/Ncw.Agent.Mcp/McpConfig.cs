using System.Buffers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ncw.Agent.Mcp;

/// <summary>MCP 传输(沿用 2.x 的取值:<c>stdio</c> / <c>sse</c> / <c>streamable-http</c>)。</summary>
public enum McpTransportKind
{
    Stdio,

    /// <summary>旧版 HTTP+SSE(2024-11-05):GET 事件流 + <c>endpoint</c> 事件给出 POST 地址。</summary>
    Sse,

    StreamableHttp,
}

/// <summary>
/// 一台 MCP 服务器的配置(2.x 的 <c>McpServerConfig</c>,存放处与 JSON 形状原样沿用:库里的 <c>mcp_servers</c> 表一行一个 JSON)。
/// <para>
/// ★ <b>配置里没有一个明文密钥</b>:stdio 的环境变量、HTTP 的请求头十有八九装的是 token,所以只存<b>键名</b>(<see cref="EnvNames"/> / <see cref="HeaderNames"/>),
/// 值整张表加密后落在 <c>credentials</c> 表的 <see cref="McpSecrets.RefOf"/> 之下(NCK1)。配置能导出,密钥不能。
/// </para>
/// <see cref="Extra"/> 保存读到的、本程序不认识的字段(原样写回):新版本加的字段不会被旧版本改一次配置就抹掉。
/// </summary>
public sealed record McpServerConfig
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>参考图那栏「描述(可选)」,也会拼进工具描述的前缀,给模型一点上下文。</summary>
    public string? Description { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>绑定到某个项目:服务器在那个项目的执行环境里运行 / 连接,并且只对那个项目的会话可见。null = 全局(在设备本机)。</summary>
    public string? WorkspaceId { get; init; }

    public required McpTransportKind Transport { get; init; }

    // ── stdio ──

    public string Command { get; init; } = "";

    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>环境变量的<b>键名</b>;值在 NCK1 凭据库里。</summary>
    public IReadOnlyList<string> EnvNames { get; init; } = [];

    /// <summary>留空 = 项目根(项目绑定的服务器)/ 进程默认目录(全局)。相对路径相对项目根。</summary>
    public string? Cwd { get; init; }

    // ── sse / streamable-http ──

    public string Url { get; init; } = "";

    /// <summary>请求头的<b>键名</b>;值在 NCK1 凭据库里。</summary>
    public IReadOnlyList<string> HeaderNames { get; init; } = [];

    /// <summary>读到的未知字段(键 → 原始 JSON 文本),写回时原样带上。</summary>
    public IReadOnlyDictionary<string, string> Extra { get; init; } = new Dictionary<string, string>();

    public bool IsStdio => Transport == McpTransportKind.Stdio;

    /// <summary>这条配置声明的密钥键名(stdio = 环境变量,其余 = 请求头)。</summary>
    public IReadOnlyList<string> SecretNames => IsStdio ? EnvNames : HeaderNames;

    public string SecretKind => IsStdio ? McpSecrets.Env : McpSecrets.Headers;

    public static string TransportName(McpTransportKind kind) => kind switch
    {
        McpTransportKind.Stdio => "stdio",
        McpTransportKind.Sse => "sse",
        _ => "streamable-http",
    };

    public static bool TryParseTransport(string? name, out McpTransportKind kind)
    {
        switch (name)
        {
            case "stdio":
                kind = McpTransportKind.Stdio;
                return true;
            case "sse":
                kind = McpTransportKind.Sse;
                return true;
            case "streamable-http":
                kind = McpTransportKind.StreamableHttp;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}

/// <summary>配置的校验(2.x <c>ipc/mcp.ts</c> 的 <c>assertValid</c> 与 <c>MCP_SERVER_ID_RE</c>)。失败返回稳定 key,不抛。</summary>
public static partial class McpConfigRules
{
    public const int MaxNameLength = 120;

    public const int MaxDescriptionLength = 500;

    public const int MaxListItems = 64;

    public const int MaxItemLength = 4096;

    /// <summary>serverId 会进工具名(<c>mcp__&lt;id&gt;__&lt;tool&gt;</c>),必须先过这道白名单(2.x §4.4 的投毒防线上游最靠前的一道)。</summary>
    public static bool IsValidId(string? id) => id is not null && ServerId().IsMatch(id);

    /// <summary>环境变量名(stdio)/ HTTP 头名(远程)。</summary>
    public static bool IsValidSecretName(string? name, bool env) =>
        name is not null && name.Length <= 128 && (env ? Ncw.Agent.Environments.CommandSpec.IsValidEnvName(name) : HeaderName().IsMatch(name));

    /// <summary>
    /// 返回 null = 合法;否则是 <c>mcp.*</c> 的稳定 key(参数放进 <paramref name="parameters"/>)。
    /// </summary>
    public static string? Validate(McpServerConfig config, out IReadOnlyDictionary<string, string> parameters)
    {
        var p = new Dictionary<string, string>();
        parameters = p;
        if (!IsValidId(config.Id))
        {
            return McpErrorKeys.InvalidId;
        }

        if (config.Name.Trim().Length == 0 || config.Name.Length > MaxNameLength)
        {
            return McpErrorKeys.NameInvalid;
        }

        if (config.Description is { Length: > MaxDescriptionLength })
        {
            return McpErrorKeys.DescriptionTooLong;
        }

        if (config.WorkspaceId is { Length: 0 })
        {
            return McpErrorKeys.WorkspaceInvalid;
        }

        foreach (var (list, isEnv) in new[] { (config.EnvNames, true), (config.HeaderNames, false) })
        {
            if (list.Count > MaxListItems)
            {
                return McpErrorKeys.TooManyItems;
            }

            foreach (var name in list)
            {
                if (!IsValidSecretName(name, isEnv))
                {
                    p["name"] = name.Length > 64 ? name[..64] : name;
                    return McpErrorKeys.SecretNameInvalid;
                }
            }

            if (list.Distinct(StringComparer.Ordinal).Count() != list.Count)
            {
                return McpErrorKeys.SecretNameInvalid;
            }
        }

        if (config.IsStdio)
        {
            if (config.Command.Trim().Length == 0)
            {
                return McpErrorKeys.CommandRequired;
            }

            if (config.Command.Length > MaxItemLength || config.Command.Contains('\0') || config.Args.Count > MaxListItems * 4
                || config.Args.Any(a => a.Length > MaxItemLength || a.Contains('\0')) || config.Cwd is { Length: > MaxItemLength } || config.Cwd?.Contains('\0') == true)
            {
                return McpErrorKeys.CommandInvalid;
            }

            if (config.HeaderNames.Count > 0)
            {
                return McpErrorKeys.TransportFieldMismatch;
            }

            return null;
        }

        if (config.EnvNames.Count > 0 || config.Command.Length > 0 || config.Args.Count > 0)
        {
            return McpErrorKeys.TransportFieldMismatch;
        }

        if (!Uri.TryCreate(config.Url, UriKind.Absolute, out var uri) || config.Url.Length > MaxItemLength)
        {
            return McpErrorKeys.UrlInvalid;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            p["scheme"] = uri.Scheme;
            return McpErrorKeys.UrlScheme;
        }

        // 带凭据的 URL(https://user:pass@host)不接受:凭据应该放进「请求头」由 NCK1 保管,而不是明文躺在配置里(也会泄漏进日志 / 重定向)。
        if (uri.UserInfo.Length > 0)
        {
            return McpErrorKeys.UrlCredentials;
        }

        return null;
    }

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,32}$")]
    private static partial Regex ServerId();

    // RFC 7230 token
    [GeneratedRegex("^[A-Za-z0-9!#$%&'*+.^_`|~-]{1,128}$")]
    private static partial Regex HeaderName();
}

/// <summary>配置 ↔ JSON(库里 <c>mcp_servers.json</c> 一列,2.x 的形状;全部手写,不走反射)。</summary>
public static class McpConfigJson
{
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "id", "name", "description", "enabled", "workspaceId", "transport", "command", "args", "envNames", "cwd", "url", "headerNames",
    };

    /// <summary>读不懂返回 null(缺 id / name / 传输不认识)。未知字段进 <see cref="McpServerConfig.Extra"/>。</summary>
    public static McpServerConfig? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (Str(root, "id") is not { } id || Str(root, "name") is not { } name
                || !McpServerConfig.TryParseTransport(Str(root, "transport"), out var transport))
            {
                return null;
            }

            var extra = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!Known.Contains(property.Name))
                {
                    extra[property.Name] = property.Value.GetRawText();
                }
            }

            return new McpServerConfig
            {
                Id = id,
                Name = name,
                Description = Str(root, "description"),
                Enabled = !root.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.False,
                WorkspaceId = Str(root, "workspaceId"),
                Transport = transport,
                Command = Str(root, "command") ?? "",
                Args = Strings(root, "args"),
                EnvNames = Strings(root, "envNames"),
                Cwd = Str(root, "cwd"),
                Url = Str(root, "url") ?? "",
                HeaderNames = Strings(root, "headerNames"),
                Extra = extra,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Write(McpServerConfig config)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("id", config.Id);
            w.WriteString("name", config.Name);
            if (config.Description is not null)
            {
                w.WriteString("description", config.Description);
            }

            w.WriteBoolean("enabled", config.Enabled);
            if (config.WorkspaceId is not null)
            {
                w.WriteString("workspaceId", config.WorkspaceId);
            }

            w.WriteString("transport", McpServerConfig.TransportName(config.Transport));
            if (config.IsStdio)
            {
                w.WriteString("command", config.Command);
                Array(w, "args", config.Args);
                Array(w, "envNames", config.EnvNames);
                if (!string.IsNullOrEmpty(config.Cwd))
                {
                    w.WriteString("cwd", config.Cwd);
                }
            }
            else
            {
                w.WriteString("url", config.Url);
                Array(w, "headerNames", config.HeaderNames);
            }

            foreach (var (key, raw) in config.Extra)
            {
                if (!Known.Contains(key))
                {
                    w.WritePropertyName(key);
                    w.WriteRawValue(raw);
                }
            }

            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Array(Utf8JsonWriter w, string name, IReadOnlyList<string> items)
    {
        w.WriteStartArray(name);
        foreach (var item in items)
        {
            w.WriteStringValue(item);
        }

        w.WriteEndArray();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string[] Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
            : [];
}

/// <summary>
/// 密钥的存放(2.x <c>mcpSecretRef</c>):<b>整张 map 加密成一个 JSON blob</b>,不是一个键一行——删服务器时不必先读配置才知道删哪些行,
/// 配置读不出来时仍然清得干净。blob 里可能残留用户上次填过、后来从表单删掉的键,所以读取时只取配置里声明过的键(配置是权威)。
/// </summary>
public static class McpSecrets
{
    public const string Env = "env";

    public const string Headers = "headers";

    public static string RefOf(string serverId, string kind) => $"mcp:{serverId}:{kind}";

    /// <summary>取值:只含配置声明过的键。读不懂的 blob = 当作没配置(返回空表,不抛)。</summary>
    public static Dictionary<string, string> Read(IMcpSecretStore store, McpServerConfig config)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (config.SecretNames.Count == 0 || store.Get(RefOf(config.Id, config.SecretKind)) is not { } raw)
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in config.SecretNames)
                {
                    if (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        result[name] = value.GetString()!;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // 当作没有
        }

        return result;
    }

    /// <summary>整张 map 一次写完(不是逐键累加:表单交上来的就是完整的一份;累加的话用户删掉一行之后值仍留在库里继续生效)。配置没声明的键丢掉。</summary>
    public static void Write(IMcpSecretStore store, McpServerConfig config, IReadOnlyDictionary<string, string> values)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            foreach (var name in config.SecretNames)
            {
                if (values.TryGetValue(name, out var value))
                {
                    w.WriteString(name, value);
                }
            }

            w.WriteEndObject();
        }

        store.Set(RefOf(config.Id, config.SecretKind), System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    /// <summary>删服务器时两个 kind 都删(而不是先读配置再判断):配置读不出来时仍然清得干净;同 id 的新服务器不会默默继承上一个的 token。</summary>
    public static void RemoveAll(IMcpSecretStore store, string serverId)
    {
        store.Remove(RefOf(serverId, Env));
        store.Remove(RefOf(serverId, Headers));
    }
}

/// <summary>
/// 把密钥值从文本里抹掉(连接错误 / 服务器 stderr / 异常消息进诊断和协议之前都要过它)。
/// 值本身、以及「<c>Bearer xyz</c>」这类带前缀的值里的后半段(服务器常常只回显 token)都抹。太短的值(&lt; 4)不抹——会把正常文字抹碎。
/// </summary>
public static class McpScrubber
{
    public const string Mask = "***";

    public static string Scrub(string text, IEnumerable<string> secretValues)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var pieces = new SortedSet<string>(Comparer<string>.Create((a, b) => a.Length != b.Length ? b.Length.CompareTo(a.Length) : string.CompareOrdinal(a, b)));
        foreach (var value in secretValues)
        {
            if (value.Length >= 4)
            {
                pieces.Add(value);
            }

            var space = value.LastIndexOf(' ');
            if (space > 0 && value.Length - space - 1 >= 4)
            {
                pieces.Add(value[(space + 1)..]);
            }
        }

        foreach (var piece in pieces)
        {
            text = text.Replace(piece, Mask, StringComparison.Ordinal);
        }

        return text;
    }
}
