using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ncw.Agent.Browser.Launch;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Tools;

namespace Ncw.Agent.Browser.Tests;

/// <summary>时钟:手动拨。</summary>
public sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>启动器:不起进程,直接给出假 CDP 服务的端点。记录启动次数与进程是否被「杀」。</summary>
public sealed class FakeLauncher(Uri endpoint) : IBrowserLauncher
{
    private readonly List<TaskCompletionSource> _exits = [];

    public int Launches { get; private set; }

    public int Kills { get; private set; }

    public List<BrowserLaunchRequest> Requests { get; } = [];

    public Func<Task>? BeforeLaunch { get; set; }

    public async Task<LaunchedBrowser> LaunchAsync(BrowserLaunchRequest request, CancellationToken ct)
    {
        if (BeforeLaunch is not null)
        {
            await BeforeLaunch();
        }

        Launches++;
        Requests.Add(request);
        var exited = new TaskCompletionSource();
        _exits.Add(exited);
        return new LaunchedBrowser(endpoint, exited.Task, () =>
        {
            Kills++;
            exited.TrySetResult();
            return ValueTask.CompletedTask;
        }, "/fake/chrome");
    }

    /// <summary>模拟浏览器进程自己退出。</summary>
    public void Crash() => _exits[^1].TrySetResult();
}

/// <summary>一个假「页面」:ref 表、当前快照 id。求值回调按表达式特征应答,所以主机侧的状态机(新鲜度、消耗)被真正地测到。</summary>
public sealed class FakePageScript
{
    public HashSet<string> Refs { get; } = ["e1", "e2", "e3"];

    public string? CurrentSnapshotId { get; private set; }

    public string Tree { get; set; } = "- heading \"Example\" [ref=e1]\n- button \"Continue\" [ref=e2]\n- textbox \"Name\" [ref=e3 value=\"\"]";

    public string Title { get; set; } = "Example";

    public string Url { get; set; } = "https://example.com/";

    public string BodyText { get; set; } = "Hello world";

    public (double X, double Y) Center { get; set; } = (100, 200);

    public bool HiddenRefs { get; set; }

    public bool Stale { get; set; }

    public int Width { get; set; } = 1280;

    public int Height { get; set; } = 720;

    public string? Eval(string expression)
    {
        if (expression.StartsWith("(function (snapshotId)", StringComparison.Ordinal))
        {
            var id = Regex.Match(expression, "\\)\\(\"([^\"]+)\"\\)$").Groups[1].Value;
            CurrentSnapshotId = id;
            return new JsonObject
            {
                ["tree"] = Tree,
                ["title"] = Title,
                ["url"] = Url,
                ["snapshotId"] = id,
                ["viewport"] = new JsonObject { ["width"] = Width, ["height"] = Height },
            }.ToJsonString();
        }

        if (expression.Contains("globalThis.innerWidth", StringComparison.Ordinal) && expression.Length < 200)
        {
            return $"{{\"width\":{Width},\"height\":{Height}}}";
        }

        if (expression.Contains("state.refs.get(", StringComparison.Ordinal))
        {
            var ids = Regex.Matches(expression, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
            // 表达式里依次是 snapshotId、ref(值里的其它字符串字面量不会先于它们出现)
            var snapshot = ids[0];
            var reference = ids[1];
            if (Stale || snapshot != CurrentSnapshotId)
            {
                return "{\"ok\":false,\"error\":\"stale_snapshot\"}";
            }

            if (!Refs.Contains(reference))
            {
                return "{\"ok\":false,\"error\":\"missing_ref\"}";
            }

            if (HiddenRefs)
            {
                return "{\"ok\":false,\"error\":\"hidden_ref\"}";
            }

            if (expression.Contains("HTMLSelectElement", StringComparison.Ordinal))
            {
                return expression.Contains("\"missing\"", StringComparison.Ordinal) ? "{\"ok\":false,\"error\":\"missing_option\"}" : "{\"ok\":true}";
            }

            return FormattableString.Invariant($"{{\"ok\":true,\"x\":{Center.X},\"y\":{Center.Y}}}");
        }

        if (expression.Contains("innerText", StringComparison.Ordinal))
        {
            if (expression.Contains(".includes(", StringComparison.Ordinal))
            {
                var needle = Regex.Match(expression, "includes\\(\"(.*)\"\\)$").Groups[1].Value;
                return BodyText.Contains(needle, StringComparison.Ordinal) ? "true" : "false";
            }

            return JsonValue.Create(BodyText).ToJsonString();
        }

        return null;
    }
}

/// <summary>工具测试的夹具:假 CDP 服务 + 假启动器 + 真的 <see cref="BrowserService"/> + 本机环境。</summary>
public sealed class Harness : IAsyncDisposable
{
    private readonly FileMutationQueue _queue = new();

    public Harness(int maxTabs = 8, TimeSpan? idle = null, IBrowserUrlPolicy? policy = null)
    {
        Server = new FakeCdpServer();
        Server.InstallBrowser();
        Page = new FakePageScript();
        Server.Eval = Page.Eval;
        Launcher = new FakeLauncher(Server.Endpoint);
        Clock = new ManualClock();
        Env = new LocalEnvironment("dev-1");
        Service = new BrowserService(new BrowserOptions
        {
            ProfileRoot = EnvPath.Parse(Path.Combine(Path.GetTempPath(), "ncw-browser-test-profiles"), Style),
            Launcher = Launcher,
            Clock = Clock,
            ReapInterval = TimeSpan.Zero,
            MaxTabsPerWorkspace = maxTabs,
            IdleTimeout = idle ?? TimeSpan.FromMinutes(10),
            NavigationTimeout = TimeSpan.FromSeconds(5),
            UrlPolicy = policy ?? DefaultBrowserUrlPolicy.Instance,
        });
        Tools = BrowserTools.Create(Service).ToDictionary(t => t.Info.InternalId);
    }

    public static PathStyle Style => OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Posix;

    public FakeCdpServer Server { get; }

    public FakePageScript Page { get; }

    public FakeLauncher Launcher { get; }

    public ManualClock Clock { get; }

    public LocalEnvironment Env { get; }

    public BrowserService Service { get; }

    public Dictionary<string, IAgentTool> Tools { get; }

    public IExecutionEnvironment? EnvironmentOverride { get; set; }

    public async Task<ToolResult> Run(string tool, string run = "run-1", string session = "session-1", string workspace = "ws-1", params (string Name, object? Value)[] args)
    {
        var impl = Tools[tool];
        var input = Args(args);
        var check = impl.PrepareArguments(input);
        if (!check.IsValid)
        {
            return ToolResult.Error(check.Error!);
        }

        var environment = EnvironmentOverride ?? Env;
        var tag = new EnvironmentTag(environment.Id.DeviceId, environment.Id.Environment);
        var inner = new ToolContext(new RunId(run), "call-1", session, 0, tag, new Progress<ToolProgress>(), _queue);
        var runtime = new ToolRuntime { Environment = environment, WorkspaceId = workspace };
        var ctx = new ExecutionToolContext(inner, runtime);
        return await impl.ExecuteAsync(new ToolInvocation("call-1", tool, impl, check.Input), ctx, CancellationToken.None);
    }

    public Task<ToolResult> Call(string tool, params (string Name, object? Value)[] args) => Run(tool, "run-1", "session-1", "ws-1", args);

    /// <summary>开一个标签并返回它的 id。</summary>
    public async Task<string> OpenTab(string url = "https://example.com/", string run = "run-1", string session = "session-1", string workspace = "ws-1")
    {
        var result = await Run("browser_open", run, session, workspace, ("url", url));
        Assert.False(result.IsError, Text(result));
        return Regex.Match(Text(result), "tab-[0-9a-f]+").Value;
    }

    public static string Text(ToolResult result) => string.Concat(result.Content.OfType<TextPart>().Select(p => p.Text));

    public static string ErrorKey(ToolResult result) =>
        result.Details is { } d && d.TryGetProperty("errorKey", out var k) ? k.GetString() ?? "" : "";

    public static JsonElement Args(params (string Name, object? Value)[] args)
    {
        var o = new JsonObject();
        foreach (var (name, value) in args)
        {
            o[name] = value switch
            {
                null => null,
                string s => JsonValue.Create(s),
                int i => JsonValue.Create(i),
                double d => JsonValue.Create(d),
                bool b => JsonValue.Create(b),
                JsonNode n => n,
                _ => throw new ArgumentException($"unsupported test arg {value.GetType()}"),
            };
        }

        return Jsn.ToElement(o);
    }

    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync();
        await Server.DisposeAsync();
    }
}
