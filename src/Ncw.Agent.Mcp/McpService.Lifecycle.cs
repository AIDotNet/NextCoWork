using Ncw.Agent.Environments;

namespace Ncw.Agent.Mcp;

public sealed partial class McpService
{
    private sealed record Opened(McpConnection Connection, IReadOnlyList<McpToolDescriptor> Tools, McpRuntimeTarget Target);

    // ───────────────────────── 状态 ─────────────────────────

    private McpServerStatus ToStatus(Entry e) =>
        new(e.Config, e.State, e.ErrorKey, e.ErrorParams, e.Tools, e.Rejected, e.ConnectedAt, e.Attempt, e.ServerName, e.ServerVersion);

    /// <summary>改一个条目的状态并广播。改动在锁里;快照也在锁里生成(Rev 与内容一致);事件在锁外触发。</summary>
    private void CompleteFirst(Entry entry)
    {
        McpServerStatus status;
        lock (_stateGate)
        {
            status = ToStatus(entry);
        }

        entry.FirstAttempt.TrySetResult(status);
    }

    private void Update(Entry entry, Action<Entry> change)
    {
        lock (_stateGate)
        {
            change(entry);
        }

        Publish();
    }

    private void Publish()
    {
        McpSnapshot snapshot;
        lock (_stateGate)
        {
            _rev++;
            snapshot = new McpSnapshot(_rev, [.. _entries.Values.OrderBy(e => e.Config.Id, StringComparer.Ordinal).Select(ToStatus)]);
            Volatile.Write(ref _snapshot, snapshot);
        }

        try
        {
            Changed?.Invoke(snapshot);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _deps.Log.Warn("An MCP state subscriber failed.", e);
        }
    }

    // ───────────────────────── 起 / 停一台 ─────────────────────────

    /// <summary>(持有 <c>_operations</c>)用这份配置重新开始:先把旧的彻底收干净(不收的话工具会重复注册在两条连接上),再起新的。</summary>
    private async Task<Entry> BeginAsync(McpServerConfig config)
    {
        if (_entries.TryGetValue(config.Id, out var old))
        {
            await StopAsync(old).ConfigureAwait(false);
        }

        var entry = new Entry(config) { State = config.Enabled ? McpState.Connecting : McpState.Disabled };
        lock (_stateGate)
        {
            _entries[config.Id] = entry;
        }

        Publish();
        if (!config.Enabled)
        {
            CompleteFirst(entry);
            return entry;
        }

        entry.Cts = new CancellationTokenSource();
        var token = entry.Cts.Token;
        entry.Loop = Task.Run(() => RunAsync(entry, token), CancellationToken.None);
        return entry;
    }

    private async Task StopAsync(Entry entry)
    {
        if (entry.Cts is { } cts)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await entry.Loop.ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _deps.Log.Warn($"[mcp] {entry.Config.Id}: the connection loop ended with an error.", e);
            }

            cts.Dispose();
            entry.Cts = null;
        }

        // 即使连接从没成功过,注册表里也可能留着上一轮的工具
        _deps.Tools.UnregisterSource(SourceOf(entry.Config.Id));
        CompleteFirst(entry);
    }

    // ───────────────────────── 连接循环 ─────────────────────────

    private async Task RunAsync(Entry entry, CancellationToken ct)
    {
        var config = entry.Config;
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            McpConnection? connection = null;
            McpException? failure = null;
            Dictionary<string, string> values = [];
            try
            {
                try
                {
                    values = ReadSecrets(config);
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    throw McpException.With(McpErrorKeys.Failed, "The credential store could not be read.");
                }

                var opened = await OpenAsync(config, values, ct).ConfigureAwait(false);
                connection = opened.Connection;
                var secretValues = values.Values.ToArray();
                string Scrub(string text) => McpScrubber.Scrub(text, secretValues);
                var rejected = Register(config, opened.Tools, connection, opened.Target.Environment, Scrub, out var toolNames);
                Update(entry, e =>
                {
                    e.State = McpState.Connected;
                    e.ErrorKey = null;
                    e.ErrorParams = new Dictionary<string, string>();
                    e.Tools = toolNames;
                    e.Rejected = rejected;
                    e.ConnectedAt = _deps.Clock.GetUtcNow();
                    e.Attempt = 0;
                    e.Connection = connection;
                    e.ServerName = connection.Hello.Name;
                    e.ServerVersion = connection.Hello.Version;
                });
                attempt = 0;
                CompleteFirst(entry);
                WireRelist(entry, config, connection, opened.Target.Environment, Scrub, ct);

                try
                {
                    await connection.Closed.WaitAsync(ct).ConfigureAwait(false);
                    failure = McpException.With(McpErrorKeys.Closed, "The MCP server closed the connection.");
                }
                catch (McpException e)
                {
                    failure = e;
                    failure.Diagnostics ??= connection.TransportDiagnostics;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 停止:收尾在 finally
            }
            catch (Exception e) when (e is McpException or EnvironmentException or McpRpcException or IOException or InvalidOperationException)
            {
                failure = e as McpException ?? FromOther(e);
            }
            finally
            {
                // ★ 下线先于关闭:先从注册表注销,再关连接。
                _deps.Tools.UnregisterSource(SourceOf(config.Id));
                if (connection is not null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (ct.IsCancellationRequested || failure is null)
            {
                break;
            }

            attempt++;
            // 日志里只有脱敏后的诊断消息,不带服务器 stderr(路径 / 命令输出):stderr 只出现在 owner 的「测试连接」结果里
            var (key, parameters, detail) = Describe(failure, values.Values, null);
            _deps.Log.Warn($"[mcp] {config.Id} connection failed: {key}{(detail is null ? "" : $": {detail}")}");
            var exhausted = attempt > Options.ReconnectDelays.Count;
            Update(entry, e =>
            {
                e.State = McpState.Failed;
                e.ErrorKey = key;
                e.ErrorParams = parameters;
                e.Tools = [];
                e.Rejected = [];
                e.ConnectedAt = null;
                e.Attempt = attempt;
                e.Connection = null;
            });
            CompleteFirst(entry);
            if (exhausted || !IsRetryable(failure.Key))
            {
                break;
            }

            try
            {
                await _deps.Delayer.DelayAsync(Options.ReconnectDelays[attempt - 1], ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            Update(entry, e => e.State = McpState.Connecting);
        }

        CompleteFirst(entry);
    }

    /// <summary>配置性的失败(命令不存在、项目没了)重试也没用:停在失败,等用户改配置或手动重连。网络 / 进程 / 超时类失败才退避重连。</summary>
    private static bool IsRetryable(string key) =>
        key is not (McpErrorKeys.CommandNotFound or McpErrorKeys.ProjectMissing or McpErrorKeys.ProtocolVersion or McpErrorKeys.Unauthorized or McpErrorKeys.StartFailed);

    private static McpException FromOther(Exception e) => e switch
    {
        EnvironmentException { Code: EnvironmentErrorCode.Disconnected or EnvironmentErrorCode.ConnectionFailed or EnvironmentErrorCode.StaleGeneration } =>
            new McpException(McpErrorKeys.EnvironmentUnavailable, $"The execution environment is not reachable ({((EnvironmentException)e).Code}).", inner: e),
        EnvironmentException env => new McpException(McpErrorKeys.Failed, $"The execution environment failed ({env.Code}).", inner: e),
        McpRpcException rpc => new McpException(McpErrorKeys.Protocol, $"The server answered with an error: {rpc.Message}", inner: e),
        _ => new McpException(McpErrorKeys.Failed, $"The connection failed: {e.Message}", inner: e),
    };

    /// <summary>注册这一批工具(替换这个来源下原有的);返回被拒绝的(含与别的来源冲突被注册表丢弃的)。</summary>
    private List<McpRejectedTool> Register(
        McpServerConfig config, IReadOnlyList<McpToolDescriptor> descriptors, McpConnection connection, IExecutionEnvironment environment,
        Func<string, string> scrub, out List<string> names)
    {
        var (tools, rejected) = McpToolFactory.Build(config, descriptors, connection, environment, Options, _deps.WorkspaceOf, scrub);
        var dropped = _deps.Tools.ReplaceSource(SourceOf(config.Id), tools);
        names = [];
        foreach (var tool in tools)
        {
            var id = tool.Info.InternalId;
            var name = id[(McpTool.Prefix.Length + config.Id.Length + 2)..];
            if (dropped.Contains(id))
            {
                rejected.Add(new McpRejectedTool(name, McpErrorKeys.ToolConflict));
            }
            else
            {
                names.Add(name);
            }
        }

        return rejected;
    }

    /// <summary>
    /// <c>notifications/tools/list_changed</c> → 重列并替换这个来源的工具。合并并发:正在列时再来的通知只标「脏」,列完再来一轮(最终一定追平最新)。
    /// 只对「当前这条连接」生效(连接已换掉就丢弃)。
    /// </summary>
    private void WireRelist(Entry entry, McpServerConfig config, McpConnection connection, IExecutionEnvironment environment, Func<string, string> scrub, CancellationToken ct)
    {
        var running = 0;
        var dirty = 0;

        async Task RunAsync()
        {
            try
            {
                while (Interlocked.Exchange(ref dirty, 0) == 1 && !ct.IsCancellationRequested)
                {
                    var descriptors = await connection.ListToolsAsync(ct).ConfigureAwait(false);
                    lock (_stateGate)
                    {
                        if (!ReferenceEquals(entry.Connection, connection))
                        {
                            return;
                        }
                    }

                    var rejected = Register(config, descriptors, connection, environment, scrub, out var names);
                    Update(entry, e =>
                    {
                        e.Tools = names;
                        e.Rejected = rejected;
                    });
                }
            }
            catch (Exception e) when (e is McpException or McpRpcException or OperationCanceledException)
            {
                _deps.Log.Warn($"[mcp] {config.Id}: re-listing the tools failed; the previous tool set stays.", e);
            }
            finally
            {
                Volatile.Write(ref running, 0);
            }

            if (Volatile.Read(ref dirty) == 1 && !ct.IsCancellationRequested && Interlocked.CompareExchange(ref running, 1, 0) == 0)
            {
                _ = RunAsync();
            }
        }

        connection.ToolsListChanged += () =>
        {
            Volatile.Write(ref dirty, 1);
            if (Interlocked.CompareExchange(ref running, 1, 0) == 0)
            {
                _ = RunAsync();
            }
        };
    }

    // ───────────────────────── 建一条连接 ─────────────────────────

    private async Task<Opened> OpenAsync(McpServerConfig config, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        var target = await _deps.Environments.ResolveAsync(config, ct).ConfigureAwait(false);
        var transport = await CreateTransportAsync(config, target, values, ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Options.ConnectTimeout);
        McpConnection connection;
        try
        {
            connection = await McpConnection.OpenAsync(transport, Options, _deps.ClientVersion, _deps.Log, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new McpException(McpErrorKeys.Timeout, $"Connecting did not finish within {Options.ConnectTimeout.TotalSeconds:0.###} seconds.", new Dictionary<string, string>
            {
                ["seconds"] = Options.ConnectTimeout.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            })
            {
                Diagnostics = transport.Diagnostics,
            };
        }
        catch (McpException e)
        {
            e.Diagnostics ??= transport.Diagnostics;
            throw;
        }

        try
        {
            var tools = await connection.ListToolsAsync(ct).ConfigureAwait(false);
            return new Opened(connection, tools, target);
        }
        catch
        {
            // 握手过了但列不出工具:连接留不得,不然它会一直挂着占着子进程
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<IMcpTransport> CreateTransportAsync(McpServerConfig config, McpRuntimeTarget target, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        if (config.IsStdio)
        {
            EnvPath? cwd = target.Root;
            if (!string.IsNullOrWhiteSpace(config.Cwd))
            {
                try
                {
                    cwd = (await target.Environment.Paths.ResolveAsync(target.Root, config.Cwd, ct).ConfigureAwait(false)).Absolute;
                }
                catch (EnvironmentException e)
                {
                    throw new McpException(McpErrorKeys.CommandInvalid, $"The working directory could not be resolved ({e.Code}).", inner: e);
                }
            }

            var command = new CommandSpec
            {
                Program = config.Command.Trim(),
                Args = config.Args,
                WorkingDirectory = cwd,
                Environment = values.Count == 0 ? null : new Dictionary<string, string>(values),
            };
            return new StdioTransport(target.Environment, command, Options, _deps.Log);
        }

        foreach (var (name, value) in values)
        {
            if (value.Contains('\r') || value.Contains('\n') || value.Contains('\0'))
            {
                throw McpException.With(McpErrorKeys.Failed, $"The value of the header \"{name}\" contains a line break and was not sent.");
            }
        }

        var uri = new Uri(config.Url);
        var http = _deps.Http.Create(target, uri);
        return config.Transport == McpTransportKind.Sse
            ? new LegacySseTransport(http, uri, values, Options, _deps.Log)
            : new StreamableHttpTransport(http, uri, values, Options, _deps.Log);
    }

    // ───────────────────────── 错误 → key / 脱敏文字 ─────────────────────────

    /// <summary>失败 → (稳定 key, 结构化参数, 脱敏的英文诊断)。诊断含服务器 stderr 的尾部,<b>只进日志和 owner 的测试结果,不进主题</b>。</summary>
    private static (string Key, IReadOnlyDictionary<string, string> Parameters, string? Detail) Describe(Exception e, IEnumerable<string> secretValues, string? diagnostics)
    {
        var secrets = secretValues.ToArray();
        var mcp = e as McpException ?? FromOther(e);
        var detail = McpScrubber.Scrub(mcp.Message, secrets);
        if (diagnostics is { Length: > 0 })
        {
            detail += "\nstderr: " + McpScrubber.Scrub(diagnostics.Trim(), secrets);
        }

        if (detail.Length > 2000)
        {
            detail = detail[..2000];
        }

        return (mcp.Key, mcp.Parameters, detail);
    }
}
