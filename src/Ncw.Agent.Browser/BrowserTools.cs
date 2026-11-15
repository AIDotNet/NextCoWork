using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ncw.Agent.Browser.Cdp;
using Ncw.Agent.Browser.Pages;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Tools;
using Ncw.Agent.Tools.Schema;

namespace Ncw.Agent.Browser;

/// <summary>
/// 浏览器工具清单(移植自 2.x <c>browser.ts</c> 的 16 个里的 13 个,另加 <c>browser_wait</c> / <c>browser_text</c>)。
/// 不移植:<c>browser_user_tabs</c> / <c>browser_claim</c>(认领用户在内置浏览器里开的标签——内置浏览器是 3.1)、<c>browser_profiles</c>(2.x 的全局 Profile 注册表;3.0 是每个工作区一个 profile 目录)。
/// </summary>
public static class BrowserTools
{
    public static IReadOnlyList<IAgentTool> Create(BrowserService service) =>
    [
        new BrowserOpenTool(service),
        new BrowserNavigateTool(service),
        new BrowserSnapshotTool(service),
        new BrowserClickTool(service),
        new BrowserTypeTool(service),
        new BrowserPressTool(service),
        new BrowserSelectTool(service),
        new BrowserScrollTool(service),
        new BrowserScreenshotTool(service),
        new BrowserCuaClickTool(service),
        new BrowserCuaDragTool(service),
        new BrowserWaitTool(service),
        new BrowserTextTool(service),
        new BrowserTabsTool(service),
        new BrowserCloseTool(service),
    ];

    /// <summary>浏览器工具的内部 id(Host 登记「已知工具」用)。</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        "browser_open", "browser_navigate", "browser_snapshot", "browser_click", "browser_type", "browser_press", "browser_select",
        "browser_scroll", "browser_screenshot", "browser_cua_click", "browser_cua_drag", "browser_wait", "browser_text", "browser_tabs", "browser_close",
    ];

    internal const string ObserveThenAct =
        "Use a ref only from the latest browser_snapshot. Perform one state-changing action, then snapshot again. "
        + "If a ref fails, do not retry it unchanged; take a fresh snapshot and rebuild the target.";

    internal static JsonObject TabId() => S.String("Browser tab id returned by browser_open or browser_tabs", minLength: 1);

    internal static JsonObject Ref() => S.String("Element ref from the latest browser_snapshot, for example e12", minLength: 2, maxLength: 12);

    internal static JsonObject Number(string description, double? minimum = null)
    {
        var o = new JsonObject { ["type"] = "number", ["description"] = description };
        if (minimum is { } min)
        {
            o["minimum"] = min;
        }

        return o;
    }
}

/// <summary>浏览器工具的公共骨架:调用方身份、可预期失败 → 带 <c>errorKey</c> 的工具错误、Details(带执行环境标识)。</summary>
internal abstract partial class BrowserTool : BuiltinTool
{
    protected BrowserTool(BrowserService service, string name, string description, JsonElement schema, ToolTraits traits, string snippet, IReadOnlyList<string> guidelines)
        : base(name, description, schema, traits with { ConcurrencySafe = false }, snippet, guidelines)
    {
        Service = service;
    }

    protected BrowserService Service { get; }

    [GeneratedRegex(@"^e[1-9]\d*$")]
    private static partial Regex RefPattern();

    protected abstract Task<ToolResult> RunBrowserAsync(JsonElement args, ExecutionToolContext ctx, BrowserCaller caller, CancellationToken ct);

    protected sealed override async ValueTask<ToolResult> RunAsync(JsonElement args, ExecutionToolContext ctx, CancellationToken ct)
    {
        var runtime = ctx.Runtime;
        var caller = new BrowserCaller(runtime.WorkspaceId ?? runtime.Root?.Value ?? "default", ctx.RunId.Value, ctx.SessionId);
        try
        {
            return await RunBrowserAsync(args, ctx, caller, ct).ConfigureAwait(false);
        }
        catch (BrowserException e)
        {
            throw Fail(e.Message, ErrorDetails(runtime, e.Key));
        }
        catch (CdpDisconnectedException e)
        {
            throw Fail($"The browser went away ({e.Message}). Open the page again with browser_open.", ErrorDetails(runtime, BrowserErrorKeys.BrowserGone));
        }
        catch (CdpException e)
        {
            throw Fail($"The browser rejected the command: {e.Message}", ErrorDetails(runtime, BrowserErrorKeys.NavigationFailed));
        }
        catch (TimeoutException e)
        {
            throw Fail($"The browser did not respond in time: {e.Message}", ErrorDetails(runtime, BrowserErrorKeys.NavigationFailed));
        }
    }

    protected static string RefOf(JsonElement args)
    {
        var reference = RequiredString(args, "ref");
        if (!RefPattern().IsMatch(reference))
        {
            throw new BrowserException(BrowserErrorKeys.StaleObservation, $"\"{reference}\" is not a valid ref. Refs look like e12 and come from browser_snapshot.");
        }

        return reference;
    }

    protected static MouseButton ButtonOf(JsonElement args) => args.Str("button") switch
    {
        "right" => MouseButton.Right,
        "middle" => MouseButton.Middle,
        _ => MouseButton.Left,
    };

    private static JsonElement ErrorDetails(ToolRuntime runtime, string key) =>
        Details(runtime, "browser", w => w.WriteString("errorKey", key));

    protected static JsonElement BrowserDetails(ToolRuntime runtime, string action, BrowserTab? tab, Action<Utf8JsonWriter>? extra = null) =>
        Details(runtime, "browser", w =>
        {
            w.WriteString("action", action);
            if (tab is not null)
            {
                w.WriteString("tabId", tab.Id);
            }

            extra?.Invoke(w);
        });

    protected static ToolResult Ok(string text, JsonElement details) => ToolResult.Text(text, details);
}

internal sealed class BrowserOpenTool(BrowserService service) : BrowserTool(
    service,
    "browser_open",
    "Open a web page in an isolated headless browser page owned by this Agent session (a real Chrome/Edge/Chromium on this device, with its own cookie jar per workspace). "
    + "http, https and file:// URLs are supported. After opening, call browser_snapshot before interacting. Use WebFetch instead when you only need the text of a static page.",
    S.Build(S.Object(["url"], ("url", S.String("A complete http, https, or file URL", minLength: 1, maxLength: 8192)))),
    new ToolTraits(ReadOnly: true, Destructive: false, NeedsNetwork: true),
    "Open a page in a headless browser",
    ["Use browser_snapshot after browser_open or browser_navigate to see the page; refs from older snapshots stop working."])
{
    protected override async Task<ToolResult> RunBrowserAsync(JsonElement args, ExecutionToolContext ctx, BrowserCaller caller, CancellationToken ct)
    {
        var url = ParseUrl(Service, RequiredString(args, "url"));
        var tab = await Service.OpenAsync(caller, ctx.Runtime.Environment, url.AbsoluteUri, ct).ConfigureAwait(false);
        return Ok(
            $"Opened browser tab {tab.Id} at {url.AbsoluteUri}. Use browser_snapshot with tabId \"{tab.Id}\" before interacting.",
            BrowserDetails(ctx.Runtime, "open", tab, w => w.WriteString("url", url.AbsoluteUri)));
    }

    internal static Uri ParseUrl(BrowserService service, string raw)
    {
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var url))
        {
            throw new BrowserException(BrowserErrorKeys.UrlRefused, $"Malformed URL: \"{raw}\". Give a complete address, e.g. https://example.com/page.");
        }

        if (service.UrlPolicy.Check(url) is { } refusal)
        {
            throw new BrowserException(BrowserErrorKeys.UrlRefused, refusal);
        }

        return url;
    }
}

internal sealed class BrowserNavigateTool(BrowserService service) : BrowserTool(
    service,
    "browser_navigate",
    "Navigate a browser page owned by this session to a new URL. The navigation invalidates all prior refs; call browser_snapshot next.",
    S.Build(S.Object(["tabId", "url"], ("tabId", BrowserTools.TabId()), ("url", S.String("A complete http, https, or file URL", minLength: 1, maxLength: 8192)))),
    new ToolTraits(ReadOnly: true, Destructive: false, NeedsNetwork: true),
    "Navigate an open browser tab to a URL",
    [])
{
    protected override async Task<ToolResult> RunBrowserAsync(JsonElement args, ExecutionToolContext ctx, BrowserCaller caller, CancellationToken ct)
    {
        var tab = Service.Resolve(RequiredString(args, "tabId"), caller);
        var url = BrowserOpenTool.ParseUrl(Service, RequiredString(args, "url"));
        await tab.Page.NavigateAsync(url.AbsoluteUri, Service.NavigationTimeout, ct).ConfigureAwait(false);
        var (current, _) = await tab.Page.InfoAsync(ct).ConfigureAwait(false);
        return Ok(
            $"Browser tab {tab.Id} navigated to {(current.Length == 0 ? url.AbsoluteUri : current)}. Take a fresh browser_snapshot before interacting.",
            BrowserDetails(ctx.Runtime, "navigate", tab, w => w.WriteString("url", url.AbsoluteUri)));
    }
}

internal sealed class BrowserSnapshotTool(BrowserService service) : BrowserTool(
    service,
    "browser_snapshot",
    "Observe the live browser page as a compact accessibility tree. Interactive nodes carry refs such as [ref=e12]. "
    + "This snapshot is the only valid source of refs; navigation or one state-changing action invalidates them. Cross-origin iframe contents are not expanded.",
    S.Build(S.Object(["tabId"], ("tabId", BrowserTools.TabId()))),
    new ToolTraits(ReadOnly: true, Destructive: false, NeedsNetwork: false),
    "Observe a browser page as an accessibility tree with refs",
    [])
{
    protected override async Task<ToolResult> RunBrowserAsync(JsonElement args, ExecutionToolContext ctx, BrowserCaller caller, CancellationToken ct)
    {
        var tab = Service.Resolve(RequiredString(args, "tabId"), caller);
        var snapshot = await tab.Page.SnapshotAsync(ct).ConfigureAwait(false);
        return Ok(snapshot.Format(), BrowserDetails(ctx.Runtime, "snapshot", tab, w =>
        {
            w.WriteString("url", snapshot.Url);
            w.WriteString("title", snapshot.Title);
            w.WriteBoolean("truncated", snapshot.Truncated);
        }));
    }
}

internal sealed class BrowserClickTool(BrowserService service) : BrowserTool(
    service,
    "browser_click",
    $"Click one element by ref. {BrowserTools.ObserveThenAct}",
    S.Build(S.Object(
        ["tabId", "ref"],
        ("tabId", BrowserTools.TabId()),
        ("ref", BrowserTools.Ref()),
        ("button", S.Enum("Mouse button; defaults to left", "left", "right", "middle")),
        ("clickCount", S.Integer("Number of clicks (2 = double click); defaults to 1", 1, 3)))),
    new ToolTraits(ReadOnly: false, Destructive: false, NeedsNetwork: true),
    "Click an element by ref",
    [BrowserTools.ObserveThenAct])
{
    protected override async Task<ToolResult> RunBrowserAsync(JsonElement args, ExecutionToolContext ctx, BrowserCaller caller, CancellationToken ct)
    {
        var tab = Service.Resolve(RequiredString(args, "tabId"), caller);
        var reference = RefOf(args);
        await tab.Page.ClickRefAsync(reference, ButtonOf(args), OptionalInt(args, "clickCount") ?? 1, ct).ConfigureAwait(false);
        return Ok($"Clicked {reference} in browser tab {tab.Id}. Take a fresh browser_snapshot to verify the result.", BrowserDetails(ctx.Runtime, "click", tab, w => w.WriteString("ref", reference)));
    }
}

internal sealed class BrowserTypeTool(BrowserService service) : BrowserTool(
    service,
    "browser_type",
    $"Focus one editable element and replace its value with text. {BrowserTools.ObserveThenAct}",
    S.Build(S.Object(["tabId", "ref", "text"], ("tabId", BrowserTools.TabId()), ("ref", BrowserTools.Ref()), ("text", S.String("The text to enter", maxLength: 100_000)))),
    new ToolTraits(ReadOnly: false, Destructive: false, NeedsNetwork: true),
    "Type text into an editable element by ref",
    [BrowserTools.ObserveThenAct])
{
    protected override async Task<ToolResult> RunBrowserAsync(JsonElement args, ExecutionToolContext ctx, BrowserCaller caller, CancellationToken ct)
    {
        var tab = Service.Resolve(RequiredString(args, "tabId"), caller);
        var reference = RefOf(args);
        await tab.Page.TypeRefAsync(reference, RequiredString(args, "text"), ct).ConfigureAwait(false);
        return Ok($"Typed into {reference} in browser tab {tab.Id}. Take a fresh browser_snapshot to verify the result.", BrowserDetails(ctx.Runtime, "type", tab, w => w.WriteString("ref", reference)));
    }
}

internal sealed class BrowserPressTool(BrowserService service) : BrowserTool(
    service,
    "browser_press",
    $"Press one key or key chord, optionally after focusing a ref. Pass modifiers separately, for example [\"Control\", \"Enter\"]. {BrowserTools.ObserveThenAct}",
    S.Build(S.Object(
        ["tabId", "keys"],
        ("tabId", BrowserTools.TabId()),
        ("keys", S.Array("One non-modifier key (Enter, Tab, Escape, Backspace, Delete, Arrow*, Home, End, PageUp, PageDown, Space or a single character) plus optional modifiers (Alt, Control, Meta, Shift)", S.String("A key name", minLength: 1), minItems: 1, maxItems: 5)),
        ("ref", BrowserTools.Ref()))),
    new ToolTraits(ReadOnly: false, Destructive: false, NeedsNetwork: true),
    "Press a key or key chord in a browser page",
    [BrowserTools.ObserveThenAct])
{
    protected override async Task<ToolResult> RunBrowserAsync(JsonElement args, ExecutionToolContext ctx, BrowserCaller caller, CancellationToken ct)
    {
        var tab = Service.Resolve(RequiredString(args, "tabId"), caller);
        var keys = args.Arr("keys").Where(static k => k.ValueKind == JsonValueKind.String).Select(static k => k.GetString()!).ToList();
        var reference = args.Str("ref") is { Length: > 0 } ? RefOf(args) : null;
        await tab.Page.PressAsync(keys, reference, ct).ConfigureAwait(false);
        var chord = string.Join('+', keys);
        return Ok($"Pressed {chord} in browser tab {tab.Id}. Take a fresh browser_snapshot to verify the result.", BrowserDetails(ctx.Runtime, "press", tab, w => w.WriteString("keys", chord)));
    }
}

internal sealed class BrowserSelectTool(BrowserService service) : BrowserTool(
    service,
    "browser_select",
    $"Select one or more option values in a select element. {BrowserTools.ObserveThenAct}",
    S.Build(S.Object(["tabId", "ref", "values"], ("tabId", BrowserTools.TabId()), ("ref", BrowserTools.Ref()), ("values", S.Array("Option values to select", S.String("An option value"), minItems: 1, maxItems: 100)))),
    new ToolTraits(ReadOnly: false, Destructive: false, NeedsNetwork: true),
    "Select option values in a select element by ref",
    [BrowserTools.ObserveThenAct])
{
    protected override async Task<ToolResult> RunBrowserAsync(JsonElement args, ExecutionToolContext ctx, BrowserCaller caller, CancellationToken ct)
    {
        var tab = Service.Resolve(RequiredString(args, "tabId"), caller);
        var reference = RefOf(args);
        var values = args.Arr("values").Where(static v => v.ValueKind == JsonValueKind.String).Select(static v => v.GetString()!).ToList();
        await tab.Page.SelectAsync(reference, values, ct).ConfigureAwait(false);
        return Ok($"Selected option values in {reference}. Take a fresh browser_snapshot to verify the result.", BrowserDetails(ctx.Runtime, "select", tab, w => w.WriteString("ref", reference)));
    }
}

internal sealed class BrowserScrollTool(BrowserService service) : BrowserTool(
    service,
    "browser_scroll",
    "Scroll the page at an optional viewport coordinate. Observe the page before scrolling and snapshot again afterward.",
    S.Build(S.Object(
        ["tabId", "scrollY"],
        ("tabId", BrowserTools.TabId()),
        ("scrollX", BrowserTools.Number("Horizontal scroll in CSS pixels; defaults to 0")),
        ("scrollY", BrowserTools.Number("Vertical scroll in CSS pixels; positive scrolls down")),
        ("x", BrowserTools.Number("Viewport x of the scroll point; defaults to the centre", 0)),
        ("y", BrowserTools.Number("Viewport y of the scroll point; defaults to the centre", 0)))),
    new ToolTraits(ReadOnly: true, Destructive: false, NeedsNetwork: false),
    "Scroll a browser page",
    [])
{
    protected override async Task<ToolResult> RunBrowserAsync(JsonElement args, ExecutionToolContext ctx, BrowserCaller caller, CancellationToken ct)
    {
        var tab = Service.Resolve(RequiredString(args, "tabId"), caller);
        Point? at = args.Num("x") is { } x && args.Num("y") is { } y ? new Point(x, y) : null;
        await tab.Page.ScrollAsync(args.Num("scrollX") ?? 0, args.Num("scrollY") ?? 0, at, ct).ConfigureAwait(false);
        return Ok($"Scrolled browser tab {tab.Id}. Take a fresh browser_snapshot to inspect the new viewport.", BrowserDetails(ctx.Runtime, "scroll", tab));
    }
}

internal sealed class BrowserScreenshotTool(BrowserService service) : BrowserTool(
    service,
    "browser_screenshot",
    "Capture the current viewport. Use this only for canvas/custom-drawn controls or visual verification; prefer browser_snapshot for normal DOM interaction.",
    S.Build(S.Object(["tabId"], ("tabId", BrowserTools.TabId()))),
    new ToolTraits(ReadOnly: true, Destructive: false, NeedsNetwork: false),
    "Capture a screenshot of a browser page",
    [])
{
    protected override async Task<ToolResult> RunBrowserAsync(JsonElement args, ExecutionToolContext ctx, BrowserCaller caller, CancellationToken ct)
    {
        var tab = Service.Resolve(RequiredString(args, "tabId"), caller);
        var shot = await tab.Page.ScreenshotAsync(ct).ConfigureAwait(false);
        var text = $"Captured browser tab {tab.Id}: image {shot.Width}x{shot.Height} pixels, CSS viewport {shot.Viewport.Width}x{shot.Viewport.Height}. "
            + "Coordinate actions use CSS viewport pixels; scale image coordinates when these sizes differ.";
        return new ToolResult(
            [new TextPart(text), new ImagePart(shot.Mime, shot.DataUrl)],
            BrowserDetails(ctx.Runtime, "screenshot", tab, w =>
            {
                w.WriteNumber("width", shot.Width);
                w.WriteNumber("height", shot.Height);
            }));
    }
}

internal sealed class BrowserCuaClickTool(BrowserService service) : BrowserTool(
    service,
    "browser_cua_click",
    "Click exact CSS viewport coordinates. You MUST take a fresh browser_screenshot first; use browser_click with a ref whenever possible.",
    S.Build(S.Object(
        ["tabId", "x", "y"],
        ("tabId", BrowserTools.TabId()),
        ("x", BrowserTools.Number("Viewport x in CSS pixels", 0)),
        ("y", BrowserTools.Number("Viewport y in CSS pixels", 0)),
        ("button", S.Enum("Mouse button; defaults to left", "left", "right", "middle")))),
    new ToolTraits(ReadOnly: false, Destructive: false, NeedsNetwork: true),
    "Click viewport coordinates after a screenshot",
    ["Prefer browser_click with a ref; coordinate clicks are for canvas and custom-drawn controls."])
{
    protected override async Task<ToolResult> RunBrowserAsync(JsonElement args, ExecutionToolContext ctx, BrowserCaller caller, CancellationToken ct)
    {
        var tab = Service.Resolve(RequiredString(args, "tabId"), caller);
        var point = new Point(args.Num("x") ?? 0, args.Num("y") ?? 0);
        await tab.Page.CuaClickAsync(point, ButtonOf(args), ct).ConfigureAwait(false);
        return Ok($"Clicked ({point.X}, {point.Y}) in browser tab {tab.Id}. Observe again before another action.", BrowserDetails(ctx.Runtime, "cuaClick", tab));
    }
}

internal sealed class BrowserCuaDragTool(BrowserService service) : BrowserTool(
    service,
    "browser_cua_drag",
    "Drag through every supplied CSS viewport point. You MUST take a fresh browser_screenshot first; points are preserved in order.",
    S.Build(S.Object(
        ["tabId", "path"],
        ("tabId", BrowserTools.TabId()),
        ("path", S.Array("Points to drag through, in order", S.Object(["x", "y"], ("x", BrowserTools.Number("Viewport x", 0)), ("y", BrowserTools.Number("Viewport y", 0))), minItems: 2, maxItems: 200)),
        ("keys", S.Array("Modifier keys held during the drag", S.Enum("A modifier", "Alt", "Control", "Meta", "Shift"), maxItems: 4)))),
    new ToolTraits(ReadOnly: false, Destructive: false, NeedsNetwork: true),
    "Drag across viewport coordinates after a screenshot",
    [])
{
    protected override async Task<ToolResult> RunBrowserAsync(JsonElement args, ExecutionToolContext ctx, BrowserCaller caller, CancellationToken ct)
    {
        var tab = Service.Resolve(RequiredString(args, "tabId"), caller);
        var path = args.Arr("path").Select(static p => new Point(p.Num("x") ?? 0, p.Num("y") ?? 0)).ToList();
        var keys = args.Arr("keys").Where(static k => k.ValueKind == JsonValueKind.String).Select(static k => k.GetString()!).ToList();
        await tab.Page.CuaDragAsync(path, keys, ct).ConfigureAwait(false);
        return Ok($"Dragged across {path.Count} points in browser tab {tab.Id}. Observe again before another action.", BrowserDetails(ctx.Runtime, "cuaDrag", tab));
    }
}

internal sealed class BrowserWaitTool(BrowserService service) : BrowserTool(
    service,
    "browser_wait",
    "Wait for text to appear on the page, or sleep for a fixed time. Pass text to wait until it shows up in the page's visible text (up to timeoutMs), or ms for a plain pause. Waiting does not invalidate refs.",
    S.Build(S.Object(
        ["tabId"],
        ("tabId", BrowserTools.TabId()),
        ("text", S.String("Visible text to wait for", minLength: 1, maxLength: 2000)),
        ("timeoutMs", S.Integer("How long to wait for text, in milliseconds; defaults to 10000, at most 30000", 100, 30_000)),
        ("ms", S.Integer("Sleep this long instead of waiting for text, at most 10000", 0, 10_000)))),
    new ToolTraits(ReadOnly: true, Destructive: false, NeedsNetwork: false),
    "Wait for text to appear on a browser page",
    [])
{
    protected override async Task<ToolResult> RunBrowserAsync(JsonElement args, ExecutionToolContext ctx, BrowserCaller caller, CancellationToken ct)
    {
        var tab = Service.Resolve(RequiredString(args, "tabId"), caller);
        if (args.Str("text") is { Length: > 0 } text)
        {
            var timeout = TimeSpan.FromMilliseconds(OptionalInt(args, "timeoutMs") ?? 10_000);
            var found = await tab.Page.WaitForTextAsync(text, timeout, ct).ConfigureAwait(false);
            var details = BrowserDetails(ctx.Runtime, "wait", tab, w => w.WriteBoolean("found", found));
            return found
                ? Ok($"The text appeared in browser tab {tab.Id}. Take a fresh browser_snapshot to see the page.", details)
                : ToolResult.Error($"The text did not appear within {timeout.TotalSeconds:0.#} seconds. The page may still be loading, or the text may differ; take a browser_snapshot to check.", details);
        }

        if (OptionalInt(args, "ms") is { } ms)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(ms), ct).ConfigureAwait(false);
            return Ok($"Waited {ms} ms.", BrowserDetails(ctx.Runtime, "wait", tab));
        }

        throw Fail("Pass either text (wait for it to appear) or ms (sleep).");
    }
}

internal sealed class BrowserTextTool(BrowserService service) : BrowserTool(
    service,
    "browser_text",
    "Read the visible text of the page (what a reader sees, without structure or refs). Cheaper than browser_snapshot when you only need to read content; use browser_snapshot to interact.",
    S.Build(S.Object(["tabId"], ("tabId", BrowserTools.TabId()))),
    new ToolTraits(ReadOnly: true, Destructive: false, NeedsNetwork: false),
    "Read the visible text of a browser page",
    [])
{
    private const int MaxChars = 60_000;

    protected override async Task<ToolResult> RunBrowserAsync(JsonElement args, ExecutionToolContext ctx, BrowserCaller caller, CancellationToken ct)
    {
        var tab = Service.Resolve(RequiredString(args, "tabId"), caller);
        var text = await tab.Page.TextAsync(MaxChars + 1, ct).ConfigureAwait(false);
        var (url, title) = await tab.Page.InfoAsync(ct).ConfigureAwait(false);
        var truncated = text.Length > MaxChars;
        var body = text.Length == 0 ? "(The page has no visible text.)" : truncated ? text[..MaxChars] : text;
        return Ok(
            $"Page: {(title.Length == 0 ? "(untitled)" : title)}\nURL: {url}\n\n{body}{(truncated ? $"\n\n[Text truncated at {MaxChars} characters.]" : "")}",
            BrowserDetails(ctx.Runtime, "text", tab, w => w.WriteBoolean("truncated", truncated)));
    }
}

internal sealed class BrowserTabsTool(BrowserService service) : BrowserTool(
    service,
    "browser_tabs",
    "List browser tabs controlled by this Agent session: id, URL, title.",
    S.Build(S.Object([])),
    new ToolTraits(ReadOnly: true, Destructive: false, NeedsNetwork: false),
    "List open browser tabs",
    [])
{
    protected override async Task<ToolResult> RunBrowserAsync(JsonElement args, ExecutionToolContext ctx, BrowserCaller caller, CancellationToken ct)
    {
        var tabs = Service.List(caller);
        if (tabs.Count == 0)
        {
            return Ok("No controlled browser tabs are open in this workspace.", BrowserDetails(ctx.Runtime, "tabs", null, w => w.WriteNumber("count", 0)));
        }

        var lines = new List<string>();
        foreach (var tab in tabs)
        {
            var (url, title) = await tab.Page.InfoAsync(ct).ConfigureAwait(false);
            lines.Add($"{tab.Id}\t{title}\t{url}");
        }

        return Ok(string.Join('\n', lines), BrowserDetails(ctx.Runtime, "tabs", null, w => w.WriteNumber("count", tabs.Count)));
    }
}

internal sealed class BrowserCloseTool(BrowserService service) : BrowserTool(
    service,
    "browser_close",
    "Close one browser tab controlled by this Agent session.",
    S.Build(S.Object(["tabId"], ("tabId", BrowserTools.TabId()))),
    new ToolTraits(ReadOnly: true, Destructive: false, NeedsNetwork: false),
    "Close a browser tab",
    [])
{
    protected override async Task<ToolResult> RunBrowserAsync(JsonElement args, ExecutionToolContext ctx, BrowserCaller caller, CancellationToken ct)
    {
        var tab = Service.Resolve(RequiredString(args, "tabId"), caller);
        await Service.CloseAsync(tab).ConfigureAwait(false);
        return Ok($"Closed browser tab {tab.Id}.", BrowserDetails(ctx.Runtime, "close", tab));
    }
}
