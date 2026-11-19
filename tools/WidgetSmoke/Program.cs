using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Widgets;
using Ncw.Core.Domain;
using Ncw.UI.Controls;

return AppBuilder.Configure<SmokeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);

internal sealed class SmokeApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Window { Width = 520, Height = 160, Title = "widget smoke" };
            _ = Task.Run(async () =>
            {
                var failures = 0;
                try
                {
                    failures += await Dispatcher.UIThread.InvokeAsync(() => new Behaviour().RunAsync(desktop.MainWindow));
                    failures += await Dispatcher.UIThread.InvokeAsync(() => new Attack().RunAsync(desktop.MainWindow));
                }
                catch (Exception e)
                {
                    Console.WriteLine($"SMOKE harness FAIL {e}");
                    failures++;
                }

                Console.WriteLine($"SMOKE done failures={failures}");
                await Dispatcher.UIThread.InvokeAsync(() => desktop.Shutdown(failures));
            });
        }

        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class Confirmations : IWidgetConfirmations
{
    public List<string> Urls { get; } = [];

    public Task<bool> ConfirmOpenUrlAsync(string url)
    {
        Urls.Add(url);
        return Task.FromResult(false); // 冒烟里一律「取消」:什么都不该真的被打开
    }

    public Task<bool> ConfirmActionAsync(WidgetActionPrompt prompt) => Task.FromResult(false);
}

internal sealed class Launcher : IUrlLauncher
{
    public List<string> Opened { get; } = [];

    public Task OpenAsync(string url)
    {
        Opened.Add(url);
        return Task.CompletedTask;
    }
}

/// <summary>一页真实的 WebView:外壳 + 桥 + 把页面发来的原始消息记下来(<c>smoke:report</c> 是页面自己报告检查结果的通道,不进桥)。</summary>
internal abstract class Page
{
    protected readonly Confirmations Confirm = new();
    protected readonly Launcher Launch = new();
    protected readonly Dictionary<string, string> Reports = [];
    protected readonly List<Uri> External = [];
    protected readonly List<double> Heights = [];
    protected readonly List<double> Wheels = [];
    protected NcwWidgetSurface Surface = null!;
    protected WidgetBridge Bridge = null!;
    protected readonly List<string> Failures = [];
    private readonly TaskCompletionSource _ready = new();

    protected abstract string Code { get; }

    protected virtual void OnReady() { }

    protected async Task<int> RunPageAsync(Window window, int seconds, Func<Task> whileRunning)
    {
        Surface = new NcwWidgetSurface { Height = 200 };
        window.Content = Surface;
        Bridge = new WidgetBridge(new WidgetRef("smoke", "s1", "c1"), new WidgetActions(new DeviceRegistry()), Confirm, () => Launch,
            () => new WidgetBridgeContext(new WidgetFacts(WidgetCall.Done, true, false, false, false, false), []), json => Surface.Post(json));
        Bridge.HeightChanged += Heights.Add;
        Bridge.Wheel += Wheels.Add;
        Bridge.Ready += () => Dispatcher.UIThread.Post(() =>
        {
            Surface.MarkReady();
            _ready.TrySetResult();
            OnReady();
        });
        Surface.MessageReceived += body =>
        {
            if (body.Contains("\"smoke:report\"", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(body);
                Reports[doc.RootElement.GetProperty("k").GetString()!] = doc.RootElement.GetProperty("v").GetRawText();
                return;
            }

            _ = Bridge.HandleAsync(body);
        };
        Surface.ExternalRequested += uri =>
        {
            External.Add(uri);
            _ = Bridge.OpenExternalAsync(uri);
        };
        Surface.Failed += (failure, detail) => Failures.Add($"{failure}:{detail}");
        Surface.LoadShell(WidgetShell.Build(WidgetShell.NewNonce()));
        Console.WriteLine($"SMOKE info {GetType().Name} engine={Surface.EngineInfo ?? "(pending)"}");

        if (await Task.WhenAny(_ready.Task, Task.Delay(TimeSpan.FromSeconds(20))) != _ready.Task)
        {
            Check("page-ready", false, "the shell never reported ready");
            return Finish();
        }

        Check("page-ready", true, "");
        await whileRunning();
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        Console.WriteLine($"SMOKE info {GetType().Name} engine={Surface.EngineInfo}");
        return 0;
    }

    private int _failed;

    protected void Check(string name, bool pass, string detail)
    {
        Console.WriteLine($"SMOKE {name} {(pass ? "PASS" : "FAIL")} {detail}");
        if (!pass)
        {
            _failed++;
        }
    }

    protected int Finish() => _failed;

    protected string? Report(string key) => Reports.GetValueOrDefault(key);
}

/// <summary>正常行为 + 被 CSP / 导航策略挡住的各种越界尝试。</summary>
internal sealed class Behaviour : Page
{
    protected override string Code => """
        <div id="box" style="height:240px">content
          <div id="inner" style="height:40px;overflow:auto"><div style="height:200px"></div></div>
          <div id="zoom" style="height:20px"></div>
        </div>
        <a id="lnk" href="https://docs.example.com/page">link</a>
        <form id="f" action="https://example.com/post" method="post"><input name="a"></form>
        <script>
        (function () {
          function report(k, v) { invokeCSharpAction(JSON.stringify({ type: 'smoke:report', k: k, v: v })); }
          report('ran', true);
          report('api', ['getState', 'setState', 'submit', 'runAction', 'openUrl', 'onData'].every(function (n) { return typeof ncw[n] === 'function'; }));
          try { window.ncw = 1; } catch (e) {}
          report('frozen', typeof ncw === 'object');
          var violations = [];
          document.addEventListener('securitypolicyviolation', function (e) { violations.push(e.violatedDirective); });
          fetch('https://example.com/').then(function () { report('fetch_blocked', false); }, function () { report('fetch_blocked', true); });
          try { eval('1'); report('eval_blocked', false); } catch (e) { report('eval_blocked', true); }
          try { new Function('return 1')(); report('function_blocked', false); } catch (e) { report('function_blocked', true); }
          var handlerRan = false;
          window.__h = function () { handlerRan = true; };
          var d = document.createElement('div');
          d.innerHTML = '<img src="x" onerror="window.__h()">';
          document.body.appendChild(d);
          var s = document.createElement('script');
          s.textContent = 'window.__inj = true';
          document.body.appendChild(s);
          new Image().src = 'https://example.com/pixel.png';
          setTimeout(function () {
            report('handler_blocked', handlerRan === false);
            report('injected_script_blocked', window.__inj !== true);
            report('csp_violations', violations.length);
          }, 700);
          var before = location.href;
          setTimeout(function () { location.href = 'https://example.com/nav'; }, 800);
          setTimeout(function () { report('window_open_null', window.open('https://example.com/w') === null); }, 1000);
          setTimeout(function () { document.getElementById('lnk').click(); }, 1200);
          setTimeout(function () { document.getElementById('f').submit(); }, 1400);
          ncw.onData(function (data) { report('ondata', JSON.stringify(data)); });
          setTimeout(function () {
            report('state', JSON.stringify(ncw.getState()));
            report('css_token', getComputedStyle(document.documentElement).getPropertyValue('--color-text-primary').trim());
          }, 1800);
          ncw.setState({ x: 1 }).then(function () { report('setstate_reply', 'ok'); }, function (e) { report('setstate_reply', String(e.message)); });
          ncw.openUrl('javascript:alert(1)').then(function () { report('js_url', 'opened'); }, function (e) { report('js_url', String(e.message)); });
          setTimeout(function () { report('still_here', location.href === before); }, 6000);
          // 滚轮:页面不消费的交给宿主;里面能滚的元素、被脚本拦下的(图表缩放)、双指缩放(ctrlKey)不交
          document.getElementById('zoom').addEventListener('wheel', function (e) { e.preventDefault(); }, { passive: false });
          function wheel(id, init) { init.bubbles = true; init.cancelable = true; document.getElementById(id).dispatchEvent(new WheelEvent('wheel', init)); }
          setTimeout(function () { wheel('box', { deltaY: 30 }); }, 2000);
          setTimeout(function () { wheel('inner', { deltaY: 25 }); }, 2200);
          setTimeout(function () { wheel('zoom', { deltaY: 1000 }); }, 2400);
          setTimeout(function () { wheel('box', { deltaY: 500, ctrlKey: true }); }, 2600);
          setTimeout(function () { wheel('box', { deltaY: -2, deltaMode: 1 }); }, 2800);
        })();
        </script>
        """;

    protected override void OnReady()
    {
        var theme = WidgetTokens.Map(WidgetAppearance.Dark, new Dictionary<string, string> { ["Foreground"] = "#123456", ["Accent"] = "#0071e3" });
        Surface.Post(WidgetPush.Theme(theme));
        Surface.Post(WidgetPush.Env(new WidgetEnvironment("smoke", "{\"slider\":7}", null, false, false, [])));
        Surface.Post(WidgetPush.Content(Code, "html"));
        DispatcherTimer.RunOnce(() => Surface.Post(WidgetPush.Env(new WidgetEnvironment("smoke", "{\"slider\":7}", "{\"q\":2}", false, false, []))), TimeSpan.FromSeconds(1.2));
    }

    public Task<int> RunAsync(Window window) => RunPageAsync(window, 8, () => Task.CompletedTask).ContinueWith(t => t.Result + Verify(), TaskScheduler.Default);

    private int Verify()
    {
        Check("script-ran", Report("ran") == "true", "the widget's inline script ran (nonce)");
        Check("ncw-api", Report("api") == "true", "window.ncw exposes getState/setState/submit/runAction/openUrl/onData");
        Check("ncw-frozen", Report("frozen") == "true", "window.ncw cannot be replaced");
        Check("csp-fetch", Report("fetch_blocked") == "true", "fetch to the network is blocked");
        Check("csp-eval", Report("eval_blocked") == "true", "eval is blocked");
        Check("csp-new-function", Report("function_blocked") == "true", "new Function is blocked");
        Check("csp-inline-handler", Report("handler_blocked") == "true", "inline onerror handler did not run");
        Check("csp-injected-script", Report("injected_script_blocked") == "true", "a script injected without the nonce did not run");
        Check("csp-violations", int.TryParse(Report("csp_violations"), out var n) && n >= 4, $"securitypolicyviolation events: {Report("csp_violations")}");
        Check("window-open", Report("window_open_null") == "true", "window.open returned null (no new window)");
        Check("navigation-blocked", Report("still_here") == "true", $"the page is still the shell after location.href / link click / form submit (nav requests seen: {string.Join(",", External)})");
        Check("navigation-offered-to-browser-with-confirm", Confirm.Urls.Contains("https://example.com/nav") && Confirm.Urls.Contains("https://docs.example.com/page"), $"confirmations: {string.Join(",", Confirm.Urls)}");
        Check("nothing-launched", Launch.Opened.Count == 0, $"launched: {string.Join(",", Launch.Opened)}");
        Check("javascript-url-rejected", Report("js_url") is { } js && js.Contains("bad-args", StringComparison.Ordinal), Report("js_url") ?? "(none)");
        Check("bridge-roundtrip", Report("setstate_reply") is { } r && r.Contains("offline", StringComparison.Ordinal), $"setState reply: {Report("setstate_reply")}");
        Check("env-state", Report("state") == "\"{\\\"slider\\\":7}\"", Report("state") ?? "(none)");
        Check("env-data", Report("ondata") == "\"{\\\"q\\\":2}\"", Report("ondata") ?? "(none)");
        Check("theme-token", Report("css_token") == "\"#123456\"", Report("css_token") ?? "(none)");
        Check("height-report", Heights.Count > 0 && Heights[^1] is >= 240 and <= 300, $"heights: {string.Join(",", Heights.TakeLast(3))}");
        Check("wheel-forwarded", Wheels.SequenceEqual([30, -80]), $"wheels: {string.Join(",", Wheels)} (expected 30,-80: the scrollable child, the preventDefault and the pinch are kept by the page)");
        Check("no-surface-failure", Failures.Count == 0, string.Join(",", Failures));
        return Finish() - 0;
    }
}

/// <summary>恶意载荷:垃圾 / 超大 / 伪造类型 / 不在白名单的方法 / 洪水。桥必须不崩、锁死、一件事都不做。</summary>
internal sealed class Attack : Page
{
    protected override string Code => """
        <div>flood</div>
        <script>
        (function () {
          function raw(s) { invokeCSharpAction(s); }
          for (var i = 0; i < 400; i++) { raw('garbage ' + i); }
          raw('x'.repeat(400000));
          raw(JSON.stringify({ type: 'ncw:widget:content', html: '<script>steal()<\/script>' }));
          raw(JSON.stringify({ type: 'ncw:widget:call', id: 1, method: 'readFile', args: { path: '/etc/passwd' } }));
          raw(JSON.stringify({ type: 'ncw:widget:call', id: 2, method: 'runAction', args: { id: 'rm_rf', params: { target: '/' } } }));
          for (var j = 3; j < 300; j++) { raw(JSON.stringify({ type: 'ncw:widget:call', id: j, method: 'submit', args: { value: 'x' } })); }
          invokeCSharpAction(JSON.stringify({ type: 'smoke:report', k: 'flood_done', v: true }));
        })();
        </script>
        """;

    protected override void OnReady()
    {
        Surface.Post(WidgetPush.Content(Code, "html"));
    }

    public Task<int> RunAsync(Window window) => RunPageAsync(window, 4, () => Task.CompletedTask).ContinueWith(t => t.Result + Verify(), TaskScheduler.Default);

    private int Verify()
    {
        Check("attack-flood-delivered", Report("flood_done") == "true", "the page sent the whole flood");
        Check("attack-bridge-locked", Bridge.IsLocked, $"strikes: {Bridge.Strikes}");
        Check("attack-nothing-launched", Launch.Opened.Count == 0 && Confirm.Urls.Count == 0, "no browser launch, no dialog");
        Check("attack-no-surface-failure", Failures.Count == 0, string.Join(",", Failures));
        return Finish();
    }
}
