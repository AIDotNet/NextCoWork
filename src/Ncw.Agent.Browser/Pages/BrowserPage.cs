using System.Buffers;
using System.Text.Json;
using Ncw.Agent.Browser.Cdp;

namespace Ncw.Agent.Browser.Pages;

/// <summary>
/// 一个浏览器页面(CDP target + 扁平会话)。语义移植自 2.x 的 <c>CdpBrowserPage</c> / <c>CdpPageSession</c>:
/// 页面侧程序跑在<b>隔离世界</b>(页面脚本碰不到 ref 表)、导航销毁隔离世界(过期 ref 必然失败)、状态变更消耗快照(见 <see cref="BrowserPage.Input"/>)。
/// </summary>
public sealed partial class BrowserPage : IAsyncDisposable
{
    public const string WorldName = "__nextcowork_browser_automation__";

    public static readonly Viewport DefaultViewport = new(1280, 720);

    private readonly CdpConnection _connection;
    private readonly TimeProvider _clock;
    private int? _contextId;
    private int _disposed;

    private BrowserPage(CdpConnection connection, string targetId, string sessionId, TimeProvider clock)
    {
        _connection = connection;
        TargetId = targetId;
        SessionId = sessionId;
        _clock = clock;
        connection.EventReceived += OnEvent;
    }

    internal CdpConnection Connection => _connection;

    public string TargetId { get; }

    public string SessionId { get; }

    /// <summary>页面被关掉 / 崩溃(<c>Target.targetDestroyed</c> / <c>Inspector.targetCrashed</c>)。</summary>
    public bool Gone { get; private set; }

    /// <summary>在已有的浏览器连接上新开一个标签页并附着。</summary>
    public static async Task<BrowserPage> OpenAsync(CdpConnection connection, TimeProvider clock, CancellationToken ct)
    {
        var created = await connection.SendAsync("Target.createTarget", w =>
        {
            w.WriteString("url", "about:blank");
        }, ct: ct).ConfigureAwait(false);
        var targetId = StringOf(created, "targetId") ?? throw new BrowserException(BrowserErrorKeys.LaunchFailed, "The browser did not create a page.");

        string sessionId;
        try
        {
            var attached = await connection.SendAsync("Target.attachToTarget", w =>
            {
                w.WriteString("targetId", targetId);
                w.WriteBoolean("flatten", true);
            }, ct: ct).ConfigureAwait(false);
            sessionId = StringOf(attached, "sessionId") ?? throw new BrowserException(BrowserErrorKeys.LaunchFailed, "The browser did not attach to the page.");
        }
        catch
        {
            await CloseTargetQuietly(connection, targetId).ConfigureAwait(false);
            throw;
        }

        var page = new BrowserPage(connection, targetId, sessionId, clock);
        try
        {
            await page.Send("Page.enable", ct: ct).ConfigureAwait(false);
            await page.Send("Emulation.setDeviceMetricsOverride", w =>
            {
                w.WriteNumber("width", DefaultViewport.Width);
                w.WriteNumber("height", DefaultViewport.Height);
                w.WriteNumber("deviceScaleFactor", 1);
                w.WriteBoolean("mobile", false);
            }, ct).ConfigureAwait(false);
            return page;
        }
        catch
        {
            await page.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task CloseTargetQuietly(CdpConnection connection, string targetId)
    {
        try
        {
            await connection.SendAsync("Target.closeTarget", w => w.WriteString("targetId", targetId), timeout: TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch
        {
            // 浏览器可能已经没了
        }
    }

    internal static string? StringOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private Task<JsonElement> Send(string method, Action<Utf8JsonWriter>? parameters = null, CancellationToken ct = default, TimeSpan? timeout = null) =>
        _connection.SendAsync(method, parameters, SessionId, ct, timeout);

    private void OnEvent(CdpEvent ev)
    {
        if (ev.Method == "Target.targetDestroyed" && StringOf(ev.Params, "targetId") == TargetId)
        {
            Gone = true;
            return;
        }

        if (ev.SessionId != SessionId)
        {
            return;
        }

        switch (ev.Method)
        {
            case "Inspector.targetCrashed":
                Gone = true;
                break;
            case "Page.frameNavigated":
                // 主框架(没有 parentId)导航:隔离世界已销毁
                if (ev.Params.TryGetProperty("frame", out var frame) && frame.ValueKind == JsonValueKind.Object && !frame.TryGetProperty("parentId", out _))
                {
                    _contextId = null;
                    InvalidateObservation();
                }

                break;
            case "Page.javascriptDialogOpening":
                // 对话框会把页面挂住。alert / confirm / prompt 一律「取消」(不替模型做决定);beforeunload 放行,否则导航走不了。
                var accept = StringOf(ev.Params, "type") == "beforeunload";
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Send("Page.handleJavaScriptDialog", w => w.WriteBoolean("accept", accept), timeout: TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    }
                    catch
                    {
                        // 页面已经没了
                    }
                });
                break;
        }
    }

    // ───────────────────────── 页面侧求值(隔离世界) ─────────────────────────

    private static bool IsDestroyedContext(Exception e) =>
        e.Message.Contains("execution context", StringComparison.OrdinalIgnoreCase)
        || e.Message.Contains("Cannot find context", StringComparison.OrdinalIgnoreCase)
        || e.Message.Contains("context was destroyed", StringComparison.OrdinalIgnoreCase);

    /// <summary>在隔离世界里求值,返回 <c>result.value</c>(按值)。上下文被销毁时重建一次再试。</summary>
    internal async Task<JsonElement> EvaluateAsync(string expression, CancellationToken ct)
    {
        try
        {
            return await EvaluateOnceAsync(expression, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is CdpException or BrowserException && IsDestroyedContext(e))
        {
            _contextId = null;
            return await EvaluateOnceAsync(expression, ct).ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> EvaluateOnceAsync(string expression, CancellationToken ct)
    {
        var contextId = await ContextIdAsync(ct).ConfigureAwait(false);
        var raw = await Send("Runtime.evaluate", w =>
        {
            w.WriteString("expression", expression);
            w.WriteNumber("contextId", contextId);
            w.WriteBoolean("awaitPromise", true);
            w.WriteBoolean("returnByValue", true);
            w.WriteBoolean("userGesture", true);
        }, ct).ConfigureAwait(false);

        if (raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("exceptionDetails", out var exception) && exception.ValueKind == JsonValueKind.Object)
        {
            var description = exception.TryGetProperty("exception", out var detail) ? StringOf(detail, "description") : null;
            throw new BrowserException(BrowserErrorKeys.NavigationFailed, description ?? StringOf(exception, "text") ?? "Browser page evaluation failed.");
        }

        return raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("result", out var result) && result.TryGetProperty("value", out var value) ? value : default;
    }

    private async Task<int> ContextIdAsync(CancellationToken ct)
    {
        if (_contextId is { } cached)
        {
            return cached;
        }

        var tree = await Send("Page.getFrameTree", ct: ct).ConfigureAwait(false);
        string? frameId = null;
        if (tree.ValueKind == JsonValueKind.Object && tree.TryGetProperty("frameTree", out var ft) && ft.TryGetProperty("frame", out var frame))
        {
            frameId = StringOf(frame, "id");
        }

        if (string.IsNullOrEmpty(frameId))
        {
            throw new BrowserException(BrowserErrorKeys.BrowserGone, "The browser page has no main frame.");
        }

        var world = await Send("Page.createIsolatedWorld", w =>
        {
            w.WriteString("frameId", frameId);
            w.WriteString("worldName", WorldName);
            // CDP 参数名本身就拼错了(Univeral),照抄
            w.WriteBoolean("grantUniveralAccess", false);
        }, ct).ConfigureAwait(false);
        if (world.ValueKind != JsonValueKind.Object || !world.TryGetProperty("executionContextId", out var idElement) || idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt32(out var id))
        {
            throw new BrowserException(BrowserErrorKeys.BrowserGone, "Unable to create the browser automation world.");
        }

        _contextId = id;
        return id;
    }

    // ───────────────────────── 导航 / 观察 ─────────────────────────

    public async Task NavigateAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        InvalidateObservation();
        _contextId = null;
        using var loaded = _connection.Expect("Page.domContentEventFired", SessionId);
        var result = await Send("Page.navigate", w => w.WriteString("url", url), ct).ConfigureAwait(false);
        if (StringOf(result, "errorText") is { Length: > 0 } error)
        {
            throw new BrowserException(BrowserErrorKeys.NavigationFailed, $"Navigation to {url} failed: {error}.");
        }

        // 同文档导航(只改 #hash)没有 loaderId,也就没有 DOMContentLoaded 可等
        if (StringOf(result, "loaderId") is null)
        {
            return;
        }

        try
        {
            await loaded.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new BrowserException(BrowserErrorKeys.NavigationFailed, $"The page at {url} did not finish loading within {timeout.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>当前的地址与标题(来自浏览器进程的 target 信息,不进页面)。</summary>
    public async Task<(string Url, string Title)> InfoAsync(CancellationToken ct)
    {
        var raw = await _connection.SendAsync("Target.getTargetInfo", w => w.WriteString("targetId", TargetId), ct: ct).ConfigureAwait(false);
        if (raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("targetInfo", out var info))
        {
            return (StringOf(info, "url") ?? "", StringOf(info, "title") ?? "");
        }

        return ("", "");
    }

    public async Task<Viewport> ViewportAsync(CancellationToken ct)
    {
        var value = await EvaluateAsync("({ width: globalThis.innerWidth, height: globalThis.innerHeight })", ct).ConfigureAwait(false);
        return PageSnapshot.ViewportOf(value);
    }

    public async Task<PageSnapshot> SnapshotAsync(CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var snapshot = PageSnapshot.Normalize(await EvaluateAsync(SnapshotScript.Expression(id), ct).ConfigureAwait(false));
        Input.SetSnapshot(snapshot.SnapshotId);
        return snapshot;
    }

    /// <summary>页面可见文本(<c>innerText</c>)。只读,不动 ref 表。</summary>
    public async Task<string> TextAsync(int maxChars, CancellationToken ct)
    {
        var value = await EvaluateAsync("(document.body ? document.body.innerText : '')", ct).ConfigureAwait(false);
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        return PageText.Head(text, maxChars);
    }

    /// <summary>等页面可见文本里出现 <paramref name="text"/>(轮询,不拉 Runtime 事件)。返回是否在期限内出现。</summary>
    public async Task<bool> WaitForTextAsync(string text, TimeSpan timeout, CancellationToken ct)
    {
        var needle = SnapshotScript.QuoteJson(text);
        var expression = $"(document.body ? document.body.innerText : '').includes({needle})";
        var started = _clock.GetTimestamp();
        while (true)
        {
            var found = await EvaluateAsync(expression, ct).ConfigureAwait(false);
            if (found.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (_clock.GetElapsedTime(started) >= timeout)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(150), _clock, ct).ConfigureAwait(false);
        }
    }

    public async Task<PageScreenshot> ScreenshotAsync(CancellationToken ct)
    {
        var raw = await Send("Page.captureScreenshot", w => w.WriteString("format", "png"), ct).ConfigureAwait(false);
        var base64 = StringOf(raw, "data") ?? throw new BrowserException(BrowserErrorKeys.BrowserGone, "The browser returned no screenshot data.");
        if (base64.Length > PageScreenshot.MaxBase64Chars)
        {
            throw new BrowserException(BrowserErrorKeys.NavigationFailed, "The screenshot is too large to return. Scroll to a smaller region or use browser_snapshot.");
        }

        var bytes = Convert.FromBase64String(base64);
        var (width, height) = PageScreenshot.PngSize(bytes);
        var viewport = await ViewportAsync(ct).ConfigureAwait(false);
        Input.SetScreenshotReady();
        return new PageScreenshot(base64, "image/png", width, height, viewport);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _connection.EventReceived -= OnEvent;
        Input.Invalidate();
        if (!Gone && _connection.IsOpen)
        {
            await CloseTargetQuietly(_connection, TargetId).ConfigureAwait(false);
        }

        Gone = true;
    }
}

/// <summary>截图。<see cref="Base64"/> 原样来自浏览器(不再编码一遍)。</summary>
public sealed record PageScreenshot(string Base64, string Mime, int Width, int Height, Viewport Viewport)
{
    /// <summary>8 MiB 的原图 ≈ 11.2M 字符。超了拒绝(2.x 同一个上限)。</summary>
    public const int MaxBase64Chars = 11_200_000;

    public string DataUrl => $"data:{Mime};base64,{Base64}";

    /// <summary>读 PNG 的 IHDR(第 16–23 字节,大端)。不是合法 PNG 就返回 0×0,不挡截图。</summary>
    public static (int Width, int Height) PngSize(ReadOnlySpan<byte> png)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (png.Length < 24 || !png[..8].SequenceEqual(signature))
        {
            return (0, 0);
        }

        return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png[16..20]), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png[20..24]));
    }
}
