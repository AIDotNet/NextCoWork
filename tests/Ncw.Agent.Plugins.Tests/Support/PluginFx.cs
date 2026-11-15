using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Session;

namespace Ncw.Agent.Plugins.Tests.Support;

public sealed class MemoryKv : IPluginKv
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Get(string key)
    {
        lock (_values)
        {
            return _values.GetValueOrDefault(key);
        }
    }

    public void Set(string key, string json)
    {
        lock (_values)
        {
            _values[key] = json;
        }
    }
}

public sealed class MemorySecrets : IPluginSecrets
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public string? Get(string reference) => Values.GetValueOrDefault(reference);

    public void Set(string reference, string value) => Values[reference] = value;

    public bool Remove(string reference) => Values.Remove(reference);
}

public sealed class FakeHttp : IPluginHttp
{
    public List<PluginHttpRequest> Requests { get; } = [];

    public Func<PluginHttpRequest, PluginHttpResponse> Handler { get; set; } = _ => new PluginHttpResponse(200, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("ok"), false);

    public Task<PluginHttpResponse> SendAsync(PluginHttpRequest request, int maxBodyBytes, CancellationToken ct)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        return Task.FromResult(Handler(request));
    }
}

public sealed class FakeWorkspaces(IExecutionEnvironment env, string root) : IPluginWorkspaces
{
    public PluginWorkspaceTarget? Resolve(string workspaceId) =>
        workspaceId == "w1" ? new PluginWorkspaceTarget("w1", "Project One", env, EnvPath.Posix(root)) : null;
}

public sealed class CapturingLog : ILoopDiagnostics
{
    public List<string> Lines { get; } = [];

    public void Warn(string message, Exception? error = null)
    {
        lock (Lines)
        {
            Lines.Add(message);
        }
    }
}

/// <summary>
/// 真临时目录 + 本机执行环境 + 内存 kv / 假网络 / 假市场:插件装在 <c>data/plugins</c>,项目是 <c>project</c>。★ 不打桩文件系统:路径逃逸、软链这类用例靠真的 realpath 才测得出来。
/// </summary>
public sealed class PluginFx : IAsyncDisposable
{
    public PluginFx(Action<PluginManagerOptionsBuilder>? configure = null)
    {
        Root = Path.Combine(Path.GetTempPath(), $"ncw-plugin-{Guid.NewGuid():N}"[..24]);
        Directory.CreateDirectory(Root);
        // macOS 的 /var → /private/var:先取 realpath,项目根和包路径才和守卫的结果一致
        Root = Path.GetFullPath(new DirectoryInfo(Root).ResolveLinkTarget(true)?.FullName ?? Root);
        Env = new LocalEnvironment("dev");
        PluginRoot = Path.Combine(Root, "data", "plugins");
        ProjectDir = Path.Combine(Root, "project");
        Directory.CreateDirectory(ProjectDir);
        Kv = new MemoryKv();
        Secrets = new MemorySecrets();
        Http = new FakeHttp();
        Workspaces = new FakeWorkspaces(Env, ProjectDir);
        Registry = new ToolRegistry();
        Log = new CapturingLog();
        var builder = new PluginManagerOptionsBuilder();
        configure?.Invoke(builder);
        Market = builder.Market;
        Manager = new PluginManager(new PluginManagerOptions
        {
            Environment = Env,
            Root = EnvPath.Posix(PluginRoot),
            Kv = Kv,
            Tools = Registry,
            Secrets = Secrets,
            Http = Http,
            Workspaces = Workspaces,
            Market = builder.Market,
            Log = Log,
            Limits = builder.Limits ?? new PluginRuntimeLimits { ActivateTimeout = TimeSpan.FromSeconds(5), CommandTimeout = TimeSpan.FromSeconds(5), ToolTimeout = TimeSpan.FromSeconds(5), InteractiveToolTimeout = TimeSpan.FromSeconds(10), InteractionTimeout = TimeSpan.FromSeconds(10) },
            ApiVersion = builder.ApiVersion ?? PluginApiVersion.Current,
            AppInfo = new PluginAppInfo("NextCoWork", "3.0.0-test", "en-US"),
            WorkspaceOfSession = _ => "w1",
        });
    }

    public string Root { get; }

    public LocalEnvironment Env { get; }

    public string PluginRoot { get; }

    public string ProjectDir { get; }

    public MemoryKv Kv { get; }

    public MemorySecrets Secrets { get; }

    public FakeHttp Http { get; }

    public FakeWorkspaces Workspaces { get; }

    public ToolRegistry Registry { get; }

    public CapturingLog Log { get; }

    public IPluginMarketClient? Market { get; }

    public PluginManager Manager { get; }

    public EnvPath P(string path) => EnvPath.Posix(path);

    // ───────────────────────── 夹具包 ─────────────────────────

    public static string FixtureDirectory(string id) => Path.Combine(AppContext.BaseDirectory, "Fixtures", id);

    /// <summary>把 <c>Fixtures/&lt;id&gt;</c> 打成 zip(顶层目录 = 插件 id)。<paramref name="patch"/> 可以改写 / 删除 / 新增某个文件(返回 null = 删掉)。</summary>
    public static byte[] FixtureZip(string id, Func<string, byte[], byte[]?>? patch = null, params (string Path, string Content)[] extra)
    {
        var root = FixtureDirectory(id);
        var entries = new List<(string, byte[])>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var bytes = File.ReadAllBytes(file);
            var patched = patch is null ? bytes : patch(relative, bytes);
            if (patched is not null)
            {
                entries.Add(($"{id}/{relative}", patched));
            }
        }

        entries.AddRange(extra.Select(e => ($"{id}/{e.Path}", Encoding.UTF8.GetBytes(e.Content))));
        return Zip([.. entries]);
    }

    /// <summary>改写清单的 JSON(<paramref name="edit"/> 拿到解析后的清单文本,返回新文本)。</summary>
    public static byte[] FixtureZipWithManifest(string id, Func<string, string> edit, params (string Path, string Content)[] extra) =>
        FixtureZip(id, (path, bytes) => path == "package.json" ? Encoding.UTF8.GetBytes(edit(Encoding.UTF8.GetString(bytes))) : bytes, extra);

    public static byte[] Zip(params (string Path, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path);
                using var output = entry.Open();
                output.Write(content);
            }
        }

        return stream.ToArray();
    }

    public static byte[] Zip(params (string Path, string Content)[] entries) => Zip(entries.Select(e => (e.Path, Encoding.UTF8.GetBytes(e.Content))).ToArray());

    /// <summary>从零造一个最小的合法包(只有清单 + 入口):<paramref name="main"/> 是入口 JS 的源码。</summary>
    public static byte[] MinimalZip(string name, string main, string? manifestExtra = null, string publisher = "acme", string version = "1.0.0") =>
        Zip(
            ($"{publisher}.{name}/package.json", $$"""
            {
              "name": "{{name}}", "publisher": "{{publisher}}", "version": "{{version}}",
              "engines": { "nextcowork": "^0.3.0" }, "main": "./main.js"{{(manifestExtra is null ? "" : "," + manifestExtra)}}
            }
            """),
            ($"{publisher}.{name}/main.js", main));

    // ───────────────────────── 动作 ─────────────────────────

    /// <summary>安装 → 授权 → 启用。返回启用之后的状态。</summary>
    public async Task<InstalledPlugin> InstallEnableAsync(byte[] zip, params string[] grant)
    {
        var installed = await Manager.InstallZipAsync(zip);
        var id = installed.Id;
        // 必选能力要全批了才能启用:授权界面默认把必选全勾上
        await Manager.GrantAsync(id, installed.Manifest.Permissions.Concat(grant));
        return await Manager.SetEnabledAsync(id, true);
    }

    public InstalledPlugin Plugin(string id) => Manager.Snapshot().Plugins.Single(p => p.Id == id);

    /// <summary>读插件私有 kv(<c>plugins.kv.&lt;id&gt;</c>)里的全局键。</summary>
    public string? Stored(string id, string key)
    {
        var json = Kv.Get($"plugins.kv.{id}");
        if (json is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(key, out var v) ? v.GetString() : null;
    }

    public async ValueTask DisposeAsync()
    {
        await Manager.DisposeAsync();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class PluginManagerOptionsBuilder
{
    public IPluginMarketClient? Market { get; set; }

    public PluginRuntimeLimits? Limits { get; set; }

    public string? ApiVersion { get; set; }
}

public static class ToolHarness
{
    /// <summary>造一个带执行环境(项目根)的工具上下文:和真实 Run 里 <c>ExecutionToolContext</c> 同一个形状。</summary>
    public static Ncw.Agent.Tools.ExecutionToolContext Context(PluginFx fx, string callId = "call-1", IProgress<Ncw.Agent.Kernel.Tools.ToolProgress>? progress = null, string runId = "run-1")
    {
        var inner = new Ncw.Agent.Kernel.Tools.ToolContext(
            new Ncw.Agent.Kernel.RunId(runId), callId, "session-1", 0, new Ncw.Agent.Kernel.EnvironmentTag("dev", "local"), progress ?? new Progress<Ncw.Agent.Kernel.Tools.ToolProgress>(), new Ncw.Agent.Kernel.Tools.FileMutationQueue());
        return new Ncw.Agent.Tools.ExecutionToolContext(inner, new Ncw.Agent.Tools.ToolRuntime { Environment = fx.Env, Root = fx.P(fx.ProjectDir) });
    }

    public static Ncw.Agent.Kernel.Session.RegisteredTool Find(PluginFx fx, string pluginId, string toolName)
    {
        var id = PluginToolNames.InternalId(pluginId, toolName);
        return fx.Registry.Find(id) ?? throw new InvalidOperationException($"Tool {id} is not registered; registered: {string.Join(", ", fx.Registry.List().Select(t => t.InternalId))}");
    }

    public static async Task<Ncw.Agent.Kernel.Tools.ToolResult> CallAsync(PluginFx fx, string pluginId, string toolName, string inputJson, Ncw.Agent.Kernel.Tools.ToolContext? ctx = null, CancellationToken ct = default)
    {
        var registered = Find(fx, pluginId, toolName);
        using var doc = System.Text.Json.JsonDocument.Parse(inputJson);
        var call = new Ncw.Agent.Kernel.Tools.ToolInvocation(ctx?.CallId ?? "call-1", registered.ExternalName, registered.Tool, doc.RootElement.Clone());
        return await registered.Tool.ExecuteAsync(call, ctx ?? Context(fx), ct);
    }

    public static string TextOf(Ncw.Agent.Kernel.Tools.ToolResult result) =>
        string.Concat(result.Content.OfType<Ncw.Agent.Llm.TextPart>().Select(p => p.Text));
}
