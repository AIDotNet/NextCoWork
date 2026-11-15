using System.Collections.Concurrent;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm.Routing;

namespace Ncw.Agent.Mcp.Tests.Support;

public sealed class MemoryConfigStore : IMcpConfigStore
{
    private readonly Dictionary<string, string> _rows = new(StringComparer.Ordinal);

    public IReadOnlyList<McpServerConfig> List()
    {
        lock (_rows)
        {
            return [.. _rows.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => McpConfigJson.Parse(r.Value)).OfType<McpServerConfig>()];
        }
    }

    public McpServerConfig? Get(string id)
    {
        lock (_rows)
        {
            return _rows.TryGetValue(id, out var json) ? McpConfigJson.Parse(json) : null;
        }
    }

    public void Put(McpServerConfig config)
    {
        lock (_rows)
        {
            _rows[config.Id] = McpConfigJson.Write(config);
        }
    }

    public bool Remove(string id)
    {
        lock (_rows)
        {
            return _rows.Remove(id);
        }
    }

    /// <summary>落盘的原文(断言「配置里没有明文密钥」)。</summary>
    public string Dump()
    {
        lock (_rows)
        {
            return string.Join("\n", _rows.Values);
        }
    }
}

public sealed class MemorySecretStore : IMcpSecretStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Get(string reference) => _values.GetValueOrDefault(reference);

    public void Set(string reference, string value) => _values[reference] = value;

    public bool Remove(string reference) => _values.TryRemove(reference, out _);

    public IReadOnlyCollection<string> References => [.. _values.Keys];
}

/// <summary>不睡:记下每次请求的延迟,立即返回(退避序列可断言,测试不等真时间)。</summary>
public sealed class RecordingDelayer : IDelayer
{
    public ConcurrentQueue<TimeSpan> Delays { get; } = new();

    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        Delays.Enqueue(delay);
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

/// <summary>卡住直到被放行:用来观察「正在退避等待」这一刻的状态。</summary>
public sealed class GatedDelayer : IDelayer
{
    private readonly SemaphoreSlim _gate = new(0);

    public ConcurrentQueue<TimeSpan> Delays { get; } = new();

    public void Release(int count = 1) => _gate.Release(count);

    public async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        Delays.Enqueue(delay);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
    }
}

public sealed class TestLog : ILoopDiagnostics
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public void Warn(string message, Exception? error = null) => Lines.Enqueue(error is null ? message : $"{message} ({error.GetType().Name}: {error.Message})");

    public string All => string.Join("\n", Lines);
}

public sealed class TestEnvironments(TestEnvironment local, Func<string, TestEnvironment?>? projects = null) : IMcpEnvironments
{
    public ValueTask<McpRuntimeTarget> ResolveAsync(McpServerConfig config, CancellationToken ct)
    {
        if (config.WorkspaceId is null)
        {
            return ValueTask.FromResult(new McpRuntimeTarget(local, null));
        }

        var env = projects?.Invoke(config.WorkspaceId) ?? throw McpException.With(McpErrorKeys.ProjectMissing, "The project does not exist.");
        return ValueTask.FromResult(new McpRuntimeTarget(env, McpRig.ProjectRoot(env)));
    }
}

/// <summary>装好的 MCP 服务 + 假的一切(内存配置 / 密钥、假环境、不睡的延迟、真的工具注册表)。</summary>
public sealed class McpRig : IAsyncDisposable
{
    public McpRig(McpOptions? options = null, IDelayer? delayer = null, IMcpHttpFactory? http = null, Func<string, TestEnvironment?>? projects = null)
    {
        Local = new TestEnvironment(new LocalEnvironment("dev-1"));
        Options = options ?? FastOptions();
        Delayer = delayer ?? new RecordingDelayer();
        Service = new McpService(new McpServiceDeps
        {
            Configs = Configs,
            Secrets = Secrets,
            Environments = new TestEnvironments(Local, projects),
            Tools = Tools,
            Http = http ?? new DefaultMcpHttpFactory(),
            Delayer = Delayer,
            Log = Log,
            Options = Options,
            WorkspaceOf = ctx => ctx.SessionId.StartsWith("proj-a", StringComparison.Ordinal) ? "proj-a" : "proj-b",
        });
    }

    /// <summary>项目根:一个真实存在的目录(相对 cwd 要对它做 realpath)。</summary>
    public static EnvPath ProjectRoot(IExecutionEnvironment env) => EnvPath.Parse(Path.GetTempPath().TrimEnd('/', '\\'), env.Facts.PathStyle);

    public static McpOptions FastOptions() => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ListToolsTimeout = TimeSpan.FromSeconds(5),
        CallTimeout = TimeSpan.FromSeconds(5),
        ReconnectDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)],
        GracefulExit = TimeSpan.FromMilliseconds(50),
    };

    public TestEnvironment Local { get; }

    public McpOptions Options { get; }

    public IDelayer Delayer { get; }

    public MemoryConfigStore Configs { get; } = new();

    public MemorySecretStore Secrets { get; } = new();

    public ToolRegistry Tools { get; } = new();

    public TestLog Log { get; } = new();

    public McpService Service { get; }

    /// <summary>让 stdio 服务器跑在假进程里。每次启动给一个新的假服务器实例(<see cref="FakeMcpServer"/> 是一对一的)。</summary>
    public List<FakeMcpServer> Servers { get; } = [];

    public void ServeStdio(Func<FakeMcpServer> make)
    {
        Local.ProcessFactory = _ =>
        {
            var server = make();
            lock (Servers)
            {
                Servers.Add(server);
            }

            return new FakeProcess(Local.Id, server);
        };
    }

    public static McpServerConfig Stdio(string id = "demo", bool enabled = true) => new()
    {
        Id = id,
        Name = "Demo",
        Enabled = enabled,
        Transport = McpTransportKind.Stdio,
        Command = "fake-mcp",
        Args = ["--stdio"],
    };

    public static McpServerConfig Remote(string id, Uri url, McpTransportKind kind = McpTransportKind.StreamableHttp, string[]? headers = null) => new()
    {
        Id = id,
        Name = "Remote",
        Transport = kind,
        Url = url.ToString(),
        HeaderNames = headers ?? [],
    };

    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 15_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}");
            }

            await Task.Delay(10);
        }
    }

    public Task WaitForAsync(string id, McpState state, int timeoutMs = 15_000) =>
        UntilAsync(() => Service.StatusOf(id)?.State == state, $"{id} to be {state}", timeoutMs);

    public IAgentTool Tool(string internalId) => Tools.Find(internalId)?.Tool ?? throw new InvalidOperationException($"{internalId} is not registered");

    public static ToolContext Context(string sessionId = "s1", CancellationToken ct = default) =>
        new(new RunId("r1"), "c1", sessionId, 0, null, new Progress<ToolProgress>(), new FileMutationQueue());

    public static Task<ToolResult> CallAsync(IAgentTool tool, string json = "{}", ToolContext? context = null, CancellationToken ct = default) =>
        tool.ExecuteAsync(new ToolInvocation("c1", tool.Info.InternalId, tool, System.Text.Json.JsonDocument.Parse(json).RootElement.Clone()), context ?? Context(), ct).AsTask();

    public static string TextOf(ToolResult result) => string.Concat(result.Content.OfType<Ncw.Agent.Llm.TextPart>().Select(p => p.Text));

    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync();
    }
}
