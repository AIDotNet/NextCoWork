using System.Net;
using System.Net.Sockets;
using System.Text;
using Ncw.Agent.Browser.Launch;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Tools;

namespace Ncw.Agent.Browser.Tests;

/// <summary>
/// 真实 Chrome / Edge / Chromium 冒烟:经 <see cref="LocalEnvironment"/> 起无头浏览器,打本机 <see cref="HttpListener"/> 的页面(不联网)。
/// 设备上找不到浏览器时整组跳过(CI 没装 Chrome 的机器就是这样),跳过原因里写明。
/// 环境变量 <c>NCW_BROWSER_PATH</c> 可以指定要测的可执行文件。
/// </summary>
public sealed class RealBrowserTests : IAsyncLifetime
{
    private const string Page = """
        <!doctype html><html><head><title>Smoke page</title></head><body>
        <h1>Counter</h1>
        <button id="add" onclick="n.textContent = Number(n.textContent) + 1">Add</button>
        <span id="n">0</span>
        <label>Name <input id="name"></label>
        <select aria-label="Color" id="color"><option value="r">Red</option><option value="g">Green</option></select>
        <p id="echo"></p>
        <script>
          document.getElementById('name').addEventListener('input', e => { echo.textContent = 'hello ' + e.target.value; });
          document.getElementById('color').addEventListener('change', e => { document.title = 'color=' + e.target.value; });
          document.addEventListener('keydown', e => { if (e.key === 'Enter') echo.textContent += ' [enter]'; });
        </script></body></html>
        """;

    private readonly string _profiles = Path.Combine(Path.GetTempPath(), "ncw-browser-real-" + Guid.NewGuid().ToString("N")[..10]);
    private readonly HttpListener _http = new();
    private readonly string _origin;
    private readonly FileMutationQueue _queue = new();
    private LocalEnvironment _env = null!;
    private string? _skip;
    private RecordingLauncher _launcher = null!;
    private BrowserService _service = null!;
    private Dictionary<string, IAgentTool> _tools = null!;

    public RealBrowserTests()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _origin = $"http://127.0.0.1:{port}";
        _http.Prefixes.Add(_origin + "/");
        _http.Start();
        _ = Task.Run(ServeAsync);
    }

    public async ValueTask InitializeAsync()
    {
        _env = new LocalEnvironment("dev-1");
        try
        {
            await new BrowserLocator(Environment.GetEnvironmentVariable).LocateAsync(_env, null, CancellationToken.None);
        }
        catch (BrowserException e)
        {
            _skip = $"no Chrome / Edge / Chromium on this machine ({e.Key})";
        }

        Directory.CreateDirectory(_profiles);
        _launcher = new RecordingLauncher(new ProcessBrowserLauncher(new BrowserLocator(Environment.GetEnvironmentVariable), TimeSpan.FromSeconds(45)));
        _service = new BrowserService(new BrowserOptions
        {
            ProfileRoot = EnvPath.Parse(_profiles, Harness.Style),
            GetEnvironmentVariable = Environment.GetEnvironmentVariable,
            Launcher = _launcher,
            ReapInterval = TimeSpan.Zero,
            NavigationTimeout = TimeSpan.FromSeconds(20),
        });
        _tools = BrowserTools.Create(_service).ToDictionary(t => t.Info.InternalId);
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        _http.Abort();
        try
        {
            Directory.Delete(_profiles, recursive: true);
        }
        catch
        {
            // Chrome 刚退出,目录里可能还有文件在落盘:临时目录,不影响结果
        }
    }

    private async Task ServeAsync()
    {
        while (_http.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _http.GetContextAsync();
            }
            catch
            {
                return;
            }

            var path = context.Request.Url!.AbsolutePath;
            string body;
            switch (path)
            {
                case "/set":
                    context.Response.AddHeader("Set-Cookie", "sid=abc123; Path=/; Max-Age=3600");
                    body = "<title>set</title><p>cookie set</p>";
                    break;
                case "/echo":
                    body = $"<title>echo</title><p>cookie: [{context.Request.Headers["Cookie"]}]</p>";
                    break;
                case "/second":
                    body = "<title>Second</title><p>second page</p>";
                    break;
                case "/dialog":
                    body = "<title>dialog</title><script>alert('hi'); document.title = 'after-alert';</script><p>ok</p>";
                    break;
                default:
                    body = Page;
                    break;
            }

            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private async Task<ToolResult> Run(string tool, string workspace, params (string Name, object? Value)[] args)
    {
        var impl = _tools[tool];
        var check = impl.PrepareArguments(Harness.Args(args));
        Assert.True(check.IsValid, check.Error);
        var inner = new ToolContext(new RunId("run-1"), "c", "session-1", 0, new EnvironmentTag(_env.Id.DeviceId, _env.Id.Environment), new Progress<ToolProgress>(), _queue);
        var ctx = new ExecutionToolContext(inner, new ToolRuntime { Environment = _env, WorkspaceId = workspace });
        return await impl.ExecuteAsync(new ToolInvocation("c", tool, impl, check.Input), ctx, CancellationToken.None);
    }

    /// <summary>工具调用成功;失败时把工具给模型的错误文本带进断言信息(CI 上偶发失败时要知道是哪一种错)。</summary>
    private static void Ok(ToolResult result) => Assert.False(result.IsError, Harness.Text(result));

    private async Task<string> Open(string workspace, string path)
    {
        var result = await Run("browser_open", workspace, ("url", _origin + path));
        Assert.False(result.IsError, Harness.Text(result));
        return System.Text.RegularExpressions.Regex.Match(Harness.Text(result), "tab-[0-9a-f]+").Value;
    }

    private static string RefOf(string snapshot, string role, string name)
    {
        var line = snapshot.Split('\n').First(l => l.Contains($"- {role}", StringComparison.Ordinal) && l.Contains(name, StringComparison.Ordinal));
        return System.Text.RegularExpressions.Regex.Match(line, @"ref=(e\d+)").Groups[1].Value;
    }

    [Fact]
    public async Task Open_snapshot_click_type_select_press_screenshot_and_close_against_a_real_browser()
    {
        Assert.SkipUnless(_skip is null, _skip ?? "");
        var tab = await Open("ws-a", "/");

        // 快照:标题、可访问性树、button / textbox / combobox 都有 ref
        var snapshot = Harness.Text(await Run("browser_snapshot", "ws-a", ("tabId", tab)));
        Assert.Contains("Page: Smoke page", snapshot);
        Assert.Contains("heading \"Counter\"", snapshot);
        var button = RefOf(snapshot, "button", "Add");
        RefOf(snapshot, "textbox", "Name");
        RefOf(snapshot, "combobox", "Color");

        // 点击:页面里的计数从 0 变 1
        Ok(await Run("browser_click", "ws-a", ("tabId", tab), ("ref", button)));
        Assert.Contains(" 1 ", Harness.Text(await Run("browser_text", "ws-a", ("tabId", tab))).Replace("\n", " ") + " ");

        // 点击消费了快照:同一个 ref 不能再用
        var stale = await Run("browser_click", "ws-a", ("tabId", tab), ("ref", button));
        Assert.True(stale.IsError);
        Assert.Equal(BrowserErrorKeys.StaleObservation, Harness.ErrorKey(stale));

        // 输入:替换输入框的值并触发 input 事件
        snapshot = Harness.Text(await Run("browser_snapshot", "ws-a", ("tabId", tab)));
        Ok(await Run("browser_type", "ws-a", ("tabId", tab), ("ref", RefOf(snapshot, "textbox", "Name")), ("text", "Ada")));
        var waited = await Run("browser_wait", "ws-a", ("tabId", tab), ("text", "hello Ada"), ("timeoutMs", 5000));
        Assert.False(waited.IsError, Harness.Text(waited));

        // 下拉:change 事件改了标题
        snapshot = Harness.Text(await Run("browser_snapshot", "ws-a", ("tabId", tab)));
        Ok(await Run("browser_select", "ws-a", ("tabId", tab), ("ref", RefOf(snapshot, "combobox", "Color")), ("values", new System.Text.Json.Nodes.JsonArray("g"))));
        Assert.Contains("Page: color=g", Harness.Text(await Run("browser_snapshot", "ws-a", ("tabId", tab))));

        // 按键:聚焦输入框后按 Enter,keydown 监听把 [enter] 追加到回显
        snapshot = Harness.Text(await Run("browser_snapshot", "ws-a", ("tabId", tab)));
        Ok(await Run("browser_press", "ws-a", ("tabId", tab), ("keys", new System.Text.Json.Nodes.JsonArray("Enter")), ("ref", RefOf(snapshot, "textbox", "Name"))));
        Ok(await Run("browser_wait", "ws-a", ("tabId", tab), ("text", "[enter]"), ("timeoutMs", 5000)));

        // 截图:真 PNG,尺寸等于视口
        var shot = await Run("browser_screenshot", "ws-a", ("tabId", tab));
        var image = Assert.Single(shot.Content.OfType<ImagePart>());
        var png = Convert.FromBase64String(image.DataRef["data:image/png;base64,".Length..]);
        Assert.Equal((1280, 720), PageSize(png));

        // 坐标点击:截图之后可用
        Ok(await Run("browser_cua_click", "ws-a", ("tabId", tab), ("x", 5), ("y", 5)));

        // 导航 + 标签清单 + 关闭
        Ok(await Run("browser_navigate", "ws-a", ("tabId", tab), ("url", _origin + "/second")));
        Assert.Contains("second page", Harness.Text(await Run("browser_text", "ws-a", ("tabId", tab))));
        Assert.Contains(tab, Harness.Text(await Run("browser_tabs", "ws-a")));
        Ok(await Run("browser_close", "ws-a", ("tabId", tab)));
        Assert.Contains("No controlled", Harness.Text(await Run("browser_tabs", "ws-a")));
    }

    [Fact]
    public async Task Each_workspace_has_its_own_profile_so_cookies_do_not_leak_between_workspaces()
    {
        Assert.SkipUnless(_skip is null, _skip ?? "");
        var a = await Open("ws-a", "/set");
        var aEcho = await Open("ws-a", "/echo");
        Assert.Contains("sid=abc123", Harness.Text(await Run("browser_text", "ws-a", ("tabId", aEcho))));

        var b = await Open("ws-b", "/echo");
        var text = Harness.Text(await Run("browser_text", "ws-b", ("tabId", b)));
        Assert.Contains("cookie: []", text);

        Assert.Equal(2, _launcher.Launched.Count);
        Assert.NotEqual(_launcher.Requests[0].ProfileDirectory, _launcher.Requests[1].ProfileDirectory);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task A_javascript_alert_does_not_hang_the_page()
    {
        Assert.SkipUnless(_skip is null, _skip ?? "");
        var tab = await Open("ws-a", "/dialog");
        var waited = await Run("browser_wait", "ws-a", ("tabId", tab), ("text", "ok"), ("timeoutMs", 8000));
        Assert.False(waited.IsError, Harness.Text(waited));
        Assert.Contains("Page: after-alert", Harness.Text(await Run("browser_snapshot", "ws-a", ("tabId", tab))));
    }

    [Fact]
    public async Task A_bad_host_is_a_navigation_error_and_leaves_no_tab()
    {
        Assert.SkipUnless(_skip is null, _skip ?? "");
        var result = await Run("browser_open", "ws-a", ("url", "http://nonexistent-host.invalid/"));
        Assert.True(result.IsError);
        Assert.Equal(BrowserErrorKeys.NavigationFailed, Harness.ErrorKey(result));
        Assert.Contains("ERR_", Harness.Text(result));
        Assert.Equal(0, _service.OpenTabCount);
    }

    [Fact]
    public async Task Disposing_the_service_terminates_the_browser_process_and_the_profile_is_released()
    {
        Assert.SkipUnless(_skip is null, _skip ?? "");
        await Open("ws-a", "/");
        var launched = Assert.Single(_launcher.Launched);
        Assert.False(launched.Exited.IsCompleted);

        await _service.DisposeAsync();

        await launched.Exited.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, _service.RunningBrowserCount);
    }

    private static (int, int) PageSize(byte[] png) => Ncw.Agent.Browser.Pages.PageScreenshot.PngSize(png);

    private sealed class RecordingLauncher(IBrowserLauncher inner) : IBrowserLauncher
    {
        public List<LaunchedBrowser> Launched { get; } = [];

        public List<BrowserLaunchRequest> Requests { get; } = [];

        public async Task<LaunchedBrowser> LaunchAsync(BrowserLaunchRequest request, CancellationToken ct)
        {
            var launched = await inner.LaunchAsync(request, ct);
            Launched.Add(launched);
            Requests.Add(request);
            return launched;
        }
    }
}
