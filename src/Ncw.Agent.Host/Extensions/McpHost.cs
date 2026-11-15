using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Mcp;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Credentials;
using Ncw.Agent.Storage.Sessions;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Extensions;

/// <summary>MCP 的可注入项(测试注入回环 HTTP / 小超时 / 不睡的延迟;生产全部缺省)。</summary>
internal sealed class McpHostOptions
{
    /// <summary>HTTP 端口的工厂。缺省:<see cref="DefaultMcpHttpFactory"/>(不跟随重定向、自动解压、SSH 环境里的服务器经环境的 TCP 通道)。</summary>
    public IMcpHttpFactory? Http { get; init; }

    public McpOptions? Options { get; init; }

    /// <summary>重连退避的睡眠。缺省按时钟睡真的时间(注意:不要复用 Run 重试用的 <c>AgentServices.Delayer</c>,它在测试里常常是「不睡」,共用会让重连循环空转)。</summary>
    public IDelayer? Delayer { get; init; }
}

/// <summary>
/// MCP 在 Host 里的装配:把服务层的端口接到设备事实上——配置在库里的 <c>mcp_servers</c> 表(2.x 的结构,一行一个 JSON)、密钥在 NCK1 凭据库(<c>mcp:&lt;id&gt;:env|headers</c>)、
/// 环境来自项目登记 / 设备本机环境、状态快照经 <c>mcp</c> 主题广播(全量,带单调 Rev)。
/// </summary>
internal static class McpHost
{
    public static McpService Create(ExtensionHostContext context, ToolRegistry tools, McpHostOptions? options = null)
    {
        options ??= new McpHostOptions();
        var sessions = new SessionRepository(context.Database, context.Clock);
        var workspaces = new WorkspaceRepository(context.Database, context.Clock);
        var service = new McpService(new McpServiceDeps
        {
            Configs = new McpConfigTable(context.Database),
            Secrets = new McpCredentialStore(context.Credentials),
            Environments = new McpProjectEnvironments(context),
            Tools = tools,
            Http = options.Http ?? new DefaultMcpHttpFactory(),
            Delayer = options.Delayer ?? new TimeProviderDelayer(context.Clock),
            Clock = context.Clock,
            Log = context.Log,
            Options = options.Options ?? new McpOptions(),
            // 项目绑定的服务器只对那个项目的会话可见:工具上下文只带会话 id,项目从库里查(只在有项目绑定的服务器上才会调到这里)
            WorkspaceOf = ctx => sessions.Get(ctx.SessionId)?.WorkspaceId,
            WorkspaceExists = id => workspaces.Get(id) is not null,
            ClientVersion = AgentHost.Version,
        });
        service.Changed += snapshot =>
            context.Topics.Publish(McpMethods.Topic.Name, McpEvents.Changed, McpMapping.ToDto(snapshot), PayloadShapes.GetShape<McpTopicState>());
        return service;
    }
}

/// <summary>库里的 <c>mcp_servers</c> 表(2.x 原样:<c>id</c> + <c>json</c>)。读不懂的行跳过。</summary>
internal sealed class McpConfigTable(AgentDatabase database) : IMcpConfigStore
{
    public IReadOnlyList<McpServerConfig> List()
    {
        lock (database.Gate)
        {
            using var command = database.Command("SELECT json FROM mcp_servers ORDER BY id");
            using var reader = command.ExecuteReader();
            var list = new List<McpServerConfig>();
            while (reader.Read())
            {
                if (McpConfigJson.Parse(reader.GetString(0)) is { } config)
                {
                    list.Add(config);
                }
            }

            return list;
        }
    }

    public McpServerConfig? Get(string id)
    {
        lock (database.Gate)
        {
            using var command = database.Command("SELECT json FROM mcp_servers WHERE id = $id");
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteScalar() is string json ? McpConfigJson.Parse(json) : null;
        }
    }

    public void Put(McpServerConfig config)
    {
        lock (database.Gate)
        {
            using var command = database.Command("INSERT INTO mcp_servers (id, json) VALUES ($id, $json) ON CONFLICT (id) DO UPDATE SET json = excluded.json");
            command.Parameters.AddWithValue("$id", config.Id);
            command.Parameters.AddWithValue("$json", McpConfigJson.Write(config));
            command.ExecuteNonQuery();
        }
    }

    public bool Remove(string id)
    {
        lock (database.Gate)
        {
            using var command = database.Command("DELETE FROM mcp_servers WHERE id = $id");
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }
}

/// <summary>NCK1 凭据库(<c>credentials</c> 表)的 MCP 适配:明文只经它进出。</summary>
internal sealed class McpCredentialStore(CredentialStore credentials) : IMcpSecretStore
{
    public string? Get(string reference) => credentials.Get(reference);

    public void Set(string reference, string value) => credentials.Set(reference, value);

    public bool Remove(string reference) => credentials.Remove(reference);
}

/// <summary>全局服务器在设备本机环境里运行;项目绑定的服务器在那个项目的执行环境里(项目已删除 / 环境暂时不可用是稳定 key,不是异常细节)。</summary>
internal sealed class McpProjectEnvironments(ExtensionHostContext context) : IMcpEnvironments
{
    public ValueTask<McpRuntimeTarget> ResolveAsync(McpServerConfig config, CancellationToken ct)
    {
        if (config.WorkspaceId is not { } workspaceId)
        {
            return ValueTask.FromResult(new McpRuntimeTarget(context.LocalEnvironment, null));
        }

        return ResolveProjectAsync(workspaceId, ct);
    }

    private async ValueTask<McpRuntimeTarget> ResolveProjectAsync(string workspaceId, CancellationToken ct)
    {
        try
        {
            if (context.EnsureProjectReady is { } ensure)
            {
                await ensure(workspaceId, ct).ConfigureAwait(false);
            }

            var target = context.Project(workspaceId);
            return new McpRuntimeTarget(target.Environment, target.Root);
        }
        catch (ProtocolException e) when (e.Code == ErrorCodes.NotFound)
        {
            throw McpException.With(McpErrorKeys.ProjectMissing, "The project the server is bound to does not exist.");
        }
        catch (ProtocolException e)
        {
            throw new McpException(McpErrorKeys.EnvironmentUnavailable, "The project's execution environment is not available.", inner: e);
        }
    }
}

/// <summary>服务层类型 → 协议 DTO(只有键名、状态、稳定 key 和结构化参数;没有任何密钥值,也没有服务器给的错误原文)。</summary>
internal static class McpMapping
{
    public static McpTopicState ToDto(McpSnapshot snapshot) => new(snapshot.Rev, [.. snapshot.Servers.Select(ToDto)]);

    public static McpServerStatusDto ToDto(McpServerStatus s) => new(
        ToDto(s.Config),
        s.State switch
        {
            McpState.Disabled => McpStates.Disabled,
            McpState.Connecting => McpStates.Connecting,
            McpState.Connected => McpStates.Connected,
            _ => McpStates.Failed,
        },
        s.ErrorKey,
        s.ErrorParams.Count == 0 ? null : new Dictionary<string, string>(s.ErrorParams),
        [.. s.Tools],
        [.. s.Rejected.Select(r => new McpRejectedToolDto(r.Name, r.ReasonKey))],
        s.ConnectedAt?.ToUnixTimeMilliseconds(),
        s.Attempt,
        s.ServerName,
        s.ServerVersion);

    public static McpServerConfigDto ToDto(McpServerConfig c) => c.IsStdio
        ? new McpServerConfigDto(c.Id, c.Name, McpTransports.Stdio, c.Enabled, c.Description, c.WorkspaceId, c.Command, [.. c.Args], [.. c.EnvNames], c.Cwd)
        : new McpServerConfigDto(
            c.Id, c.Name, McpServerConfig.TransportName(c.Transport), c.Enabled, c.Description, c.WorkspaceId, Url: c.Url, HeaderNames: [.. c.HeaderNames]);

    /// <summary>DTO → 配置。传输名不认识是 <c>invalid_request</c>(带稳定 key);其余校验在服务层(<see cref="McpConfigRules"/>)。</summary>
    public static McpServerConfig FromDto(McpServerConfigDto d)
    {
        if (!McpServerConfig.TryParseTransport(d.Transport, out var transport))
        {
            throw ExtensionErrors.Coded(ErrorCodes.InvalidRequest, "Unknown MCP transport.", McpErrorKeys.Unsupported);
        }

        return new McpServerConfig
        {
            Id = d.Id,
            Name = d.Name,
            Description = string.IsNullOrWhiteSpace(d.Description) ? null : d.Description,
            Enabled = d.Enabled,
            WorkspaceId = string.IsNullOrEmpty(d.WorkspaceId) ? null : d.WorkspaceId,
            Transport = transport,
            Command = d.Command ?? "",
            Args = d.Args ?? [],
            EnvNames = d.EnvNames ?? [],
            Cwd = string.IsNullOrWhiteSpace(d.Cwd) ? null : d.Cwd,
            Url = d.Url ?? "",
            HeaderNames = d.HeaderNames ?? [],
        };
    }

    public static McpTestResultDto ToDto(McpTestResult r) => new(
        r.Ok, [.. r.Tools], (long)r.Elapsed.TotalMilliseconds, r.ErrorKey, r.ErrorParams.Count == 0 ? null : new Dictionary<string, string>(r.ErrorParams),
        r.Detail, r.ServerName, r.ServerVersion, r.ProtocolVersion, [.. r.Rejected.Select(x => new McpRejectedToolDto(x.Name, x.ReasonKey))]);

    public static McpSecretsInfoDto ToDto(McpSecretsInfo i) => new(i.Id, i.Kind, [.. i.Entries.Select(e => new McpSecretEntryDto(e.Name, e.IsSet))]);
}
