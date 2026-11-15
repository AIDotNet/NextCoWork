using System.Diagnostics;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm.Routing;

namespace Ncw.Agent.Mcp;

/// <summary>服务层的注入项(全部是端口:配置存放、密钥库、环境解析、HTTP、时钟、延迟、工具注册表)。</summary>
public sealed class McpServiceDeps
{
    public required IMcpConfigStore Configs { get; init; }

    public required IMcpSecretStore Secrets { get; init; }

    public required IMcpEnvironments Environments { get; init; }

    public required ToolRegistry Tools { get; init; }

    public IMcpHttpFactory Http { get; init; } = new DefaultMcpHttpFactory();

    /// <summary>重连退避的睡眠(测试里不睡真的时间)。</summary>
    public IDelayer Delayer { get; init; } = new TimeProviderDelayer(TimeProvider.System);

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public ILoopDiagnostics Log { get; init; } = NullLoopDiagnostics.Instance;

    public McpOptions Options { get; init; } = new();

    /// <summary>工具调用所在的会话属于哪个项目(项目绑定的服务器只对那个项目的会话可见)。null = 不做项目过滤。</summary>
    public Func<ToolContext, string?>? WorkspaceOf { get; init; }

    /// <summary>项目是否存在(upsert 校验用)。null = 不校验(连接时才会发现)。</summary>
    public Func<string, bool>? WorkspaceExists { get; init; }

    public string ClientVersion { get; init; } = "3.0";
}

/// <summary>
/// MCP 服务(03 §6、06 §3 的 <c>Mcp</c> 一方扩展):配置 / 密钥 / 连接生命周期 / 工具注册。
/// <list type="bullet">
/// <item><b>下线先于关闭</b>:断开时先从注册表注销这台服务器的工具,再关连接(反过来的话,窗口里注册表还挂着指向死连接的工具,模型这一轮正好调到就是一个说不清的超时);正在执行的那次调用不受影响(它持有自己的工具引用)。</item>
/// <item><b>重连是替换</b>:<see cref="ToolRegistry.ReplaceSource"/> 对同一个 internal id 是替换,外部名由 internal id 记忆,所以重连之后历史转录里的工具名仍然对得上。</item>
/// <item><b>同一个 id 的连接同时只有一条在跑</b>:所有改动经一把异步锁;设置页的「重连」双击是常态。</item>
/// <item><b>失败 / 断线按退避重连</b>(<see cref="McpOptions.ReconnectDelays"/>,经 <see cref="IDelayer"/>),用尽停在 <see cref="McpState.Failed"/> 等手动重连;握手成功后计数清零。</item>
/// <item>状态是全量快照 + 单调 <c>Rev</c>(<see cref="Snapshot"/>);<see cref="Changed"/> 在锁外触发,订阅者可能乱序收到,按 Rev 取最大即可。</item>
/// <item><b>密钥值永远不出这个类</b>:只在连接时读出来交给传输;错误文本、stderr、工具结果都过 <see cref="McpScrubber"/>。</item>
/// </list>
/// </summary>
public sealed partial class McpService : IAsyncDisposable
{
    private sealed class Entry(McpServerConfig config)
    {
        public McpServerConfig Config { get; set; } = config;

        public CancellationTokenSource? Cts { get; set; }

        public Task Loop { get; set; } = Task.CompletedTask;

        /// <summary>第一次连接尝试结束时的状态(成功或失败;调用方据此返回「这次操作的结果」,而不是后台重连之后的状态)。</summary>
        public TaskCompletionSource<McpServerStatus> FirstAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public McpState State { get; set; }

        public string? ErrorKey { get; set; }

        public IReadOnlyDictionary<string, string> ErrorParams { get; set; } = new Dictionary<string, string>();

        public IReadOnlyList<string> Tools { get; set; } = [];

        public IReadOnlyList<McpRejectedTool> Rejected { get; set; } = [];

        public DateTimeOffset? ConnectedAt { get; set; }

        public int Attempt { get; set; }

        public string? ServerName { get; set; }

        public string? ServerVersion { get; set; }

        public McpConnection? Connection { get; set; }
    }

    private readonly McpServiceDeps _deps;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _stateGate = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private McpSnapshot _snapshot = McpSnapshot.Empty;
    private long _rev;
    private int _disposed;

    public McpService(McpServiceDeps deps)
    {
        _deps = deps;
    }

    /// <summary>状态变了(全量快照)。在锁外触发,可能与别的更新乱序到达:按 <see cref="McpSnapshot.Rev"/> 取最大的。</summary>
    public event Action<McpSnapshot>? Changed;

    private McpOptions Options => _deps.Options;

    /// <summary>当前的全量状态(不加锁,可以在主题的快照函数里安全调用)。</summary>
    public McpSnapshot Snapshot() => Volatile.Read(ref _snapshot);

    public McpServerStatus? StatusOf(string id) => Snapshot().Servers.FirstOrDefault(s => s.Config.Id == id);

    private static string SourceOf(string id) => $"mcp:{id}";

    // ───────────────────────── 启动 / 停止 ─────────────────────────

    /// <summary>
    /// 启动:读库里的配置,已启用的服务器在后台并行连接——<b>一台都不阻塞调用方</b>(首屏路径上,一台连不上的服务器不该让设备晚 30 秒就绪)。
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var config in _deps.Configs.List())
            {
                if (!McpConfigRules.IsValidId(config.Id) || _entries.ContainsKey(config.Id))
                {
                    continue; // 读到不合法的 id(被人手改了库):不连,也不让它进工具名
                }

                await BeginAsync(config).ConfigureAwait(false);
            }
        }
        finally
        {
            _operations.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var entry in _entries.Values.ToList())
            {
                await StopAsync(entry).ConfigureAwait(false);
            }
        }
        finally
        {
            _operations.Release();
        }
    }

    // ───────────────────────── 配置 ─────────────────────────

    /// <summary>
    /// 新增或修改一台服务器。<b>改完必须重连,不能沿用旧连接</b>:命令、地址、密钥任何一处变了,旧连接背后仍然是改之前那个进程 / 那个 token——
    /// 界面显示「已连接」,而它连的是用户刚刚改掉的东西。停用只断开、不删配置。顺序:校验 → 写库 → 起 / 停连接 → 广播(广播带的是写完之后的状态)。
    /// 返回第一次连接尝试结束后的状态(最长 <see cref="McpOptions.ConnectTimeout"/>);失败时退避重连继续在后台。
    /// </summary>
    /// <exception cref="McpException">配置不合法(<see cref="McpException.Key"/> 是 <c>mcp.*</c> 校验 key)。</exception>
    public async Task<McpServerStatus> UpsertAsync(McpServerConfig config, CancellationToken ct = default)
    {
        ValidateOrThrow(config);
        if (config.WorkspaceId is { } workspace && _deps.WorkspaceExists is { } exists && !exists(workspace))
        {
            throw McpException.With(McpErrorKeys.WorkspaceInvalid, "The project the server is bound to does not exist.");
        }

        Entry entry;
        await _operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 不认识的字段沿用库里那一份(控制端的 DTO 不带它们)
            var stored = _deps.Configs.Get(config.Id);
            var saved = stored is null || config.Extra.Count > 0 ? config : config with { Extra = stored.Extra };
            _deps.Configs.Put(saved);
            entry = await BeginAsync(saved).ConfigureAwait(false);
        }
        finally
        {
            _operations.Release();
        }

        return await entry.FirstAttempt.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>删服务器:先断开(工具要从注册表里下掉),再删配置与密钥。返回它原来存不存在。</summary>
    public async Task<bool> RemoveAsync(string id, CancellationToken ct = default)
    {
        await _operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existed = _deps.Configs.Get(id) is not null;
            if (_entries.TryGetValue(id, out var entry))
            {
                await StopAsync(entry).ConfigureAwait(false);
                lock (_stateGate)
                {
                    _entries.Remove(id);
                }

                Publish();
            }

            // ★ 配置和它的密钥一起删:只删配置的话 credentials 里那行密文会永远留着(而且是孤儿:键名存在刚被删掉的配置里,再没有任何东西知道该怎么清理),
            // 下次建一个同 id 的服务器会默默继承上一个的 token——「我明明没填 Authorization,它却连上了」。
            _deps.Configs.Remove(id);
            McpSecrets.RemoveAll(_deps.Secrets, id);
            return existed;
        }
        finally
        {
            _operations.Release();
        }
    }

    public async Task<McpServerStatus> SetEnabledAsync(string id, bool enabled, CancellationToken ct = default)
    {
        Entry entry;
        await _operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var config = _deps.Configs.Get(id) ?? throw McpException.With(McpErrorKeys.NotFound, $"No MCP server with id \"{id}\".");
            var next = config with { Enabled = enabled };
            _deps.Configs.Put(next);
            entry = await BeginAsync(next).ConfigureAwait(false);
        }
        finally
        {
            _operations.Release();
        }

        return await entry.FirstAttempt.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>手动重连(退避用尽之后、或用户想重来)。停用的服务器保持停用。</summary>
    public async Task<McpServerStatus> ReconnectAsync(string id, CancellationToken ct = default)
    {
        Entry entry;
        await _operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var config = _deps.Configs.Get(id) ?? throw McpException.With(McpErrorKeys.NotFound, $"No MCP server with id \"{id}\".");
            entry = await BeginAsync(config).ConfigureAwait(false);
        }
        finally
        {
            _operations.Release();
        }

        return await entry.FirstAttempt.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public IReadOnlyList<McpServerConfig> Configs() => _deps.Configs.List();

    private static void ValidateOrThrow(McpServerConfig config)
    {
        if (McpConfigRules.Validate(config, out var parameters) is { } key)
        {
            throw new McpException(key, $"The MCP server configuration is invalid ({key}).", parameters);
        }
    }

    // ───────────────────────── 密钥 ─────────────────────────

    /// <summary>
    /// 写密钥:<b>整张 map 一次写完</b>(配置没声明的键丢掉),写完重连(值变了,旧连接背后还是旧 token)。返回的只有键名和「是否已设置」。
    /// </summary>
    public async Task<McpSecretsInfo> SetSecretsAsync(string id, IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
    {
        Entry? entry = null;
        await _operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var config = _deps.Configs.Get(id) ?? throw McpException.With(McpErrorKeys.NotFound, $"No MCP server with id \"{id}\".");
            McpSecrets.Write(_deps.Secrets, config, values);
            if (config.Enabled)
            {
                entry = await BeginAsync(config).ConfigureAwait(false);
            }
        }
        finally
        {
            _operations.Release();
        }

        if (entry is not null)
        {
            await entry.FirstAttempt.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        return SecretsInfo(id);
    }

    /// <summary>「这台服务器的密钥现在存了哪几个键」。<b>回的是键名,不是值</b>;解不开(没存过 / 坏了)一律当没有。</summary>
    public McpSecretsInfo SecretsInfo(string id)
    {
        var config = _deps.Configs.Get(id) ?? throw McpException.With(McpErrorKeys.NotFound, $"No MCP server with id \"{id}\".");
        var stored = ReadSecrets(config);
        return new McpSecretsInfo(config.Id, config.SecretKind, [.. config.SecretNames.Select(n => new McpSecretState(n, stored.ContainsKey(n)))]);
    }

    private Dictionary<string, string> ReadSecrets(McpServerConfig config)
    {
        try
        {
            return McpSecrets.Read(_deps.Secrets, config);
        }
        catch (System.Security.Cryptography.CryptographicException e)
        {
            _deps.Log.Warn($"[mcp] the credential store could not be read for server {config.Id}.", e);
            throw;
        }
    }

    // ───────────────────────── 资源 / 提示(只读) ─────────────────────────

    public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(string id, CancellationToken ct = default) =>
        WithConnectionAsync(id, c => c.ListResourcesAsync(ct));

    public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(string id, CancellationToken ct = default) =>
        WithConnectionAsync(id, c => c.ListPromptsAsync(ct));

    private async Task<IReadOnlyList<T>> WithConnectionAsync<T>(string id, Func<McpConnection, Task<IReadOnlyList<T>>> call)
    {
        McpConnection? connection;
        lock (_stateGate)
        {
            connection = _entries.TryGetValue(id, out var entry) && entry.State == McpState.Connected ? entry.Connection : null;
        }

        if (connection is null || connection.IsClosed)
        {
            throw McpException.With(McpErrorKeys.NotConnected, $"The MCP server \"{id}\" is not connected.");
        }

        try
        {
            return await call(connection).ConfigureAwait(false);
        }
        catch (McpRpcException e)
        {
            throw new McpException(McpErrorKeys.Protocol, $"The server rejected the request: {e.Message}", inner: e);
        }
    }

    // ───────────────────────── 测试连接 ─────────────────────────

    /// <summary>
    /// 「测试连接」:<b>真连一次</b>(不是 ping——假连接测不出「命令能跑起来但握手对不上」「token 过期」这两类最常见的失败),列工具,断开。
    /// 不留常驻连接、不碰工具注册表、不改状态。<paramref name="draft"/> 非 null = 测这份(还没保存的)配置,密钥仍取同 id 已存的那份。
    /// </summary>
    public async Task<McpTestResult> TestConnectionAsync(string id, McpServerConfig? draft = null, CancellationToken ct = default)
    {
        var config = draft ?? _deps.Configs.Get(id) ?? throw McpException.With(McpErrorKeys.NotFound, $"No MCP server with id \"{id}\".");
        ValidateOrThrow(config);
        var started = _deps.Clock.GetTimestamp();
        Dictionary<string, string> values = [];
        try
        {
            values = ReadSecrets(config);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return Failed(started, McpException.With(McpErrorKeys.Failed, "The credential store could not be read."), values, null);
        }

        McpConnection? connection = null;
        try
        {
            var opened = await OpenAsync(config, values, ct).ConfigureAwait(false);
            connection = opened.Connection;
            var (tools, rejected) = McpToolFactory.Build(config, opened.Tools, connection, opened.Target.Environment, Options, _deps.WorkspaceOf, t => t);
            return new McpTestResult(
                true, [.. tools.Select(t => t.Info.InternalId[(McpTool.Prefix.Length + config.Id.Length + 2)..])], _deps.Clock.GetElapsedTime(started), null, new Dictionary<string, string>(),
                null, connection.Hello.Name, connection.Hello.Version, connection.Hello.ProtocolVersion, rejected);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is McpException or EnvironmentException or McpRpcException or IOException or InvalidOperationException)
        {
            return Failed(started, e, values, connection);
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private McpTestResult Failed(long started, Exception e, Dictionary<string, string> values, McpConnection? connection)
    {
        var (key, parameters, detail) = Describe(e, values.Values, (e as McpException)?.Diagnostics ?? connection?.TransportDiagnostics);
        return new McpTestResult(false, [], _deps.Clock.GetElapsedTime(started), key, parameters, detail, null, null, null, []);
    }
}
