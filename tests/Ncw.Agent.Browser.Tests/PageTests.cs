using System.Text.Json;
using Ncw.Agent.Browser.Cdp;
using Ncw.Agent.Browser.Pages;

namespace Ncw.Agent.Browser.Tests;

/// <summary>
/// 页面语义(移植自 2.x <c>browser/__tests__/input.test.ts</c> 与 <c>aria-snapshot.test.ts</c>):ref 不存在时不派发鼠标事件、一次状态变更后消费快照、坐标动作要求截图并拒绝视口外坐标、拖拽保留每个路径点。
/// 加上 3.0 自己的 CDP 流程:建标签 → 附着 → 隔离世界、导航等 DOMContentLoaded、对话框自动处理。
/// </summary>
public sealed class PageTests : IAsyncLifetime
{
    private readonly FakeCdpServer _server = new();
    private readonly FakePageScript _script = new();
    private CdpConnection _conn = null!;
    private BrowserPage _page = null!;

    public async ValueTask InitializeAsync()
    {
        _server.InstallBrowser();
        _server.Eval = _script.Eval;
        _conn = await CdpConnection.ConnectAsync(_server.Endpoint, CancellationToken.None);
        _page = await BrowserPage.OpenAsync(_conn, TimeProvider.System, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _page.DisposeAsync();
        await _conn.DisposeAsync();
        await _server.DisposeAsync();
    }

    private IEnumerable<CdpRequest> Mouse(string type) => _server.Of("Input.dispatchMouseEvent").Where(r => r.Str("type") == type);

    private static readonly CancellationToken Ct = CancellationToken.None;

    // ── 建页 / 导航 ──

    [Fact]
    public void Opening_a_page_creates_a_target_attaches_flat_and_sets_the_viewport()
    {
        Assert.Equal("about:blank", Assert.Single(_server.Of("Target.createTarget")).Str("url"));
        Assert.True(Assert.Single(_server.Of("Target.attachToTarget")).Params.GetProperty("flatten").GetBoolean());
        var metrics = Assert.Single(_server.Of("Emulation.setDeviceMetricsOverride"));
        Assert.Equal((1280, 720), ((int)metrics.Num("width")!, (int)metrics.Num("height")!));
        Assert.All(_server.Of("Page.enable").Concat(_server.Of("Emulation.setDeviceMetricsOverride")), r => Assert.Equal(_page.SessionId, r.SessionId));
    }

    [Fact]
    public async Task Navigate_returns_after_DOMContentLoaded_not_before()
    {
        _server.NavigateResult = _ => "{\"frameId\":\"F1\",\"loaderId\":\"L9\"}"; // 不发事件
        var slow = _page.NavigateAsync("https://example.com/", TimeSpan.FromMilliseconds(300), Ct);
        var e = await Assert.ThrowsAsync<BrowserException>(() => slow);
        Assert.Equal(BrowserErrorKeys.NavigationFailed, e.Key);
        Assert.Contains("did not finish loading", e.Message);
    }

    [Fact]
    public async Task Navigate_waits_for_an_event_that_arrives_before_the_reply_is_read()
    {
        // 事件先于 navigate 的结果到达:先登记后发命令,事件不会丢
        _server.NavigateResult = request =>
        {
            _server.PushAsync("Page.domContentEventFired", "{\"timestamp\":1}", request.SessionId).GetAwaiter().GetResult();
            return "{\"frameId\":\"F1\",\"loaderId\":\"L1\"}";
        };
        await _page.NavigateAsync("https://example.com/", TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task A_navigation_error_text_is_a_structured_failure()
    {
        _server.NavigateResult = _ => "{\"frameId\":\"F1\",\"errorText\":\"net::ERR_NAME_NOT_RESOLVED\"}";
        var e = await Assert.ThrowsAsync<BrowserException>(() => _page.NavigateAsync("https://nope.invalid/", TimeSpan.FromSeconds(5), Ct));
        Assert.Equal(BrowserErrorKeys.NavigationFailed, e.Key);
        Assert.Contains("ERR_NAME_NOT_RESOLVED", e.Message);
    }

    [Fact]
    public async Task A_same_document_navigation_does_not_wait_for_a_load_event()
    {
        _server.NavigateResult = _ => "{\"frameId\":\"F1\"}"; // 没有 loaderId
        await _page.NavigateAsync("https://example.com/#section", TimeSpan.FromMilliseconds(100), Ct);
    }

    [Fact]
    public async Task Navigation_drops_the_previous_observation_so_old_refs_cannot_be_used()
    {
        await _page.SnapshotAsync(Ct);
        await _page.NavigateAsync("https://example.com/next", TimeSpan.FromSeconds(5), Ct);

        var e = await Assert.ThrowsAsync<BrowserException>(() => _page.ClickRefAsync("e2", MouseButton.Left, 1, Ct));
        Assert.Contains("fresh browser_snapshot", e.Message);
        Assert.Empty(Mouse("mousePressed"));
    }

    // ── 观察 ──

    [Fact]
    public async Task A_snapshot_runs_in_an_isolated_world_with_the_fixed_program()
    {
        var snapshot = await _page.SnapshotAsync(Ct);

        Assert.Contains("[ref=e2]", snapshot.Tree);
        var world = Assert.Single(_server.Of("Page.createIsolatedWorld"));
        Assert.Equal(BrowserPage.WorldName, world.Str("worldName"));
        Assert.Equal("F1", world.Str("frameId"));
        var evaluate = _server.Of("Runtime.evaluate").Single(r => r.Str("expression")!.StartsWith("(function (snapshotId)", StringComparison.Ordinal));
        Assert.True(evaluate.Params.GetProperty("returnByValue").GetBoolean());
        Assert.True(evaluate.Params.GetProperty("awaitPromise").GetBoolean());
        Assert.True(evaluate.Params.TryGetProperty("contextId", out _));
    }

    [Fact]
    public async Task The_isolated_world_is_rebuilt_after_a_navigation_and_after_a_destroyed_context_error()
    {
        await _page.SnapshotAsync(Ct);
        await _page.NavigateAsync("https://example.com/b", TimeSpan.FromSeconds(5), Ct);
        await _page.SnapshotAsync(Ct);
        Assert.Equal(2, _server.Of("Page.createIsolatedWorld").Count());

        var failures = 0;
        _server.Handle("Runtime.evaluate", request =>
        {
            if (failures++ == 0)
            {
                throw new CdpFailure(-32000, "Cannot find context with specified id");
            }

            return $"{{\"result\":{{\"type\":\"object\",\"value\":{_script.Eval(request.Str("expression")!)}}}}}";
        });
        await _page.SnapshotAsync(Ct); // 第一次失败 → 重建世界 → 重试一次
        Assert.Equal(3, _server.Of("Page.createIsolatedWorld").Count());
    }

    [Fact]
    public async Task Page_text_and_wait_for_text_use_visible_text()
    {
        _script.BodyText = "Order #42 confirmed";
        Assert.Equal("Order #42 confirmed", await _page.TextAsync(1000, Ct));
        Assert.Equal("Order", await _page.TextAsync(5, Ct));
        Assert.True(await _page.WaitForTextAsync("confirmed", TimeSpan.FromSeconds(1), Ct));
        Assert.False(await _page.WaitForTextAsync("refunded", TimeSpan.FromMilliseconds(250), Ct));
    }

    [Fact]
    public async Task A_screenshot_reports_the_png_size_and_the_css_viewport()
    {
        var shot = await _page.ScreenshotAsync(Ct);
        Assert.Equal((1280, 720, "image/png"), (shot.Width, shot.Height, shot.Mime));
        Assert.Equal(new Viewport(1280, 720), shot.Viewport);
        Assert.StartsWith("data:image/png;base64,", shot.DataUrl);
        Assert.Equal("png", Assert.Single(_server.Of("Page.captureScreenshot")).Str("format"));
    }

    [Fact]
    public async Task An_oversized_screenshot_is_refused_not_returned()
    {
        _server.Handle("Page.captureScreenshot", _ => $"{{\"data\":\"{new string('A', PageScreenshot.MaxBase64Chars + 4)}\"}}");
        var e = await Assert.ThrowsAsync<BrowserException>(() => _page.ScreenshotAsync(Ct));
        Assert.Contains("too large", e.Message);
    }

    // ── 输入(2.x input.test.ts) ──

    [Fact]
    public async Task A_missing_ref_asks_for_a_new_snapshot_and_dispatches_no_mouse_event()
    {
        await _page.SnapshotAsync(Ct);
        _script.Refs.Remove("e2");

        var e = await Assert.ThrowsAsync<BrowserException>(() => _page.ClickRefAsync("e2", MouseButton.Left, 1, Ct));

        Assert.Contains("no longer present", e.Message);
        Assert.Empty(_server.Of("Input.dispatchMouseEvent"));
    }

    [Fact]
    public async Task A_hidden_ref_is_reported_as_not_visible()
    {
        await _page.SnapshotAsync(Ct);
        _script.HiddenRefs = true;
        var e = await Assert.ThrowsAsync<BrowserException>(() => _page.ClickRefAsync("e2", MouseButton.Left, 1, Ct));
        Assert.Contains("no longer visible", e.Message);
    }

    [Fact]
    public async Task One_state_change_consumes_the_snapshot_so_a_second_click_must_observe_again()
    {
        await _page.SnapshotAsync(Ct);
        await _page.ClickRefAsync("e2", MouseButton.Left, 1, Ct);

        var e = await Assert.ThrowsAsync<BrowserException>(() => _page.ClickRefAsync("e2", MouseButton.Left, 1, Ct));
        Assert.Equal(BrowserErrorKeys.StaleObservation, e.Key);
        Assert.Single(Mouse("mousePressed"));

        await _page.SnapshotAsync(Ct);
        await _page.ClickRefAsync("e2", MouseButton.Left, 1, Ct); // 重新观察后又可以了
        Assert.Equal(2, Mouse("mousePressed").Count());
    }

    [Fact]
    public async Task A_click_moves_presses_and_releases_at_the_element_centre_with_the_right_button_bits()
    {
        await _page.SnapshotAsync(Ct);
        _script.Center = (321.5, 123.25);

        await _page.ClickRefAsync("e1", MouseButton.Right, 2, Ct);

        Assert.Equal(["mouseMoved", "mousePressed", "mouseReleased"], _server.Of("Input.dispatchMouseEvent").Select(r => r.Str("type")));
        var pressed = Mouse("mousePressed").Single();
        Assert.Equal((321.5, 123.25), (pressed.Num("x")!.Value, pressed.Num("y")!.Value));
        Assert.Equal("right", pressed.Str("button"));
        Assert.Equal(2, pressed.Num("buttons"));
        Assert.Equal(2, pressed.Num("clickCount"));
        Assert.Equal(0, Mouse("mouseReleased").Single().Num("buttons"));
    }

    [Fact]
    public async Task A_page_side_stale_snapshot_check_surfaces_as_a_stale_observation()
    {
        await _page.SnapshotAsync(Ct);
        _script.Stale = true; // 页面里的表已经是别的快照了
        var e = await Assert.ThrowsAsync<BrowserException>(() => _page.ClickRefAsync("e1", MouseButton.Left, 1, Ct));
        Assert.Equal(BrowserErrorKeys.StaleObservation, e.Key);
        Assert.Contains("stale", e.Message);
        Assert.Empty(Mouse("mousePressed"));
    }

    [Fact]
    public async Task Typing_focuses_selects_the_old_value_and_inserts_text_then_consumes_the_snapshot()
    {
        await _page.SnapshotAsync(Ct);
        await _page.TypeRefAsync("e3", "hello \"world\"", Ct);

        Assert.Equal("hello \"world\"", Assert.Single(_server.Of("Input.insertText")).Str("text"));
        Assert.Single(Mouse("mousePressed"));
        // 点击聚焦之后再跑一次「全选」程序(整页求值里含 select())
        Assert.Contains(_server.Of("Runtime.evaluate"), r => r.Str("expression")!.Contains("element.select()", StringComparison.Ordinal));
        await Assert.ThrowsAsync<BrowserException>(() => _page.TypeRefAsync("e3", "again", Ct));
    }

    [Fact]
    public async Task A_key_chord_presses_modifiers_first_and_releases_them_last()
    {
        await _page.SnapshotAsync(Ct);
        await _page.PressAsync(["Control", "Shift", "a"], null, Ct);

        var events = _server.Of("Input.dispatchKeyEvent").Select(r => $"{r.Str("type")}:{r.Str("key")}").ToList();
        Assert.Equal(["rawKeyDown:Control", "rawKeyDown:Shift", "rawKeyDown:A", "keyUp:A", "keyUp:Shift", "keyUp:Control"], events);
        // Ctrl 在场 = 命令,不产生文本
        Assert.DoesNotContain(_server.Of("Input.dispatchKeyEvent"), r => r.Str("text") is not null);
        Assert.Equal(2 | 8, (int)_server.Of("Input.dispatchKeyEvent").First().Num("modifiers")!);
    }

    [Fact]
    public async Task A_plain_character_or_shifted_letter_produces_text()
    {
        await _page.SnapshotAsync(Ct);
        await _page.PressAsync(["Shift", "q"], null, Ct);
        var down = _server.Of("Input.dispatchKeyEvent").Single(r => r.Str("type") == "keyDown");
        Assert.Equal("Q", down.Str("text"));
        Assert.Equal("KeyQ", down.Str("code"));
    }

    [Theory]
    [InlineData("Enter", "Enter", 13)]
    [InlineData("esc", "Escape", 27)]
    [InlineData("ArrowDown", "ArrowDown", 40)]
    [InlineData("Space", " ", 32)]
    public void Named_keys_map_to_the_dom_key_and_virtual_key_code(string name, string key, int code)
    {
        var info = BrowserPage.PageInput.KeyInfo(name, false);
        Assert.Equal((key, code), (info.Key, info.KeyCode));
    }

    [Fact]
    public async Task A_key_press_needs_exactly_one_non_modifier_key_and_known_names()
    {
        await _page.SnapshotAsync(Ct);
        await Assert.ThrowsAsync<BrowserException>(() => _page.PressAsync(["Control", "Shift"], null, Ct));
        await Assert.ThrowsAsync<BrowserException>(() => _page.PressAsync(["a", "b"], null, Ct));
        var e = await Assert.ThrowsAsync<BrowserException>(() => _page.PressAsync(["Hyper"], null, Ct));
        Assert.Contains("Unsupported browser key", e.Message);
        Assert.Empty(_server.Of("Input.dispatchKeyEvent"));
    }

    [Fact]
    public async Task Selecting_reports_not_a_select_and_missing_options()
    {
        await _page.SnapshotAsync(Ct);
        await _page.SelectAsync("e2", ["one"], Ct);

        await _page.SnapshotAsync(Ct);
        var e = await Assert.ThrowsAsync<BrowserException>(() => _page.SelectAsync("e2", ["missing"], Ct));
        Assert.Contains("None of the requested select options exist", e.Message);
    }

    [Fact]
    public async Task Scrolling_needs_an_observation_and_defaults_to_the_viewport_centre()
    {
        var e = await Assert.ThrowsAsync<BrowserException>(() => _page.ScrollAsync(0, 400, null, Ct));
        Assert.Contains("before scrolling", e.Message);

        await _page.SnapshotAsync(Ct);
        await _page.ScrollAsync(0, 400, null, Ct);
        var wheel = Assert.Single(Mouse("mouseWheel"));
        Assert.Equal((640, 360, 400), (wheel.Num("x")!.Value, wheel.Num("y")!.Value, wheel.Num("deltaY")!.Value));
    }

    [Fact]
    public async Task Coordinate_actions_need_a_screenshot_and_reject_points_outside_the_viewport()
    {
        var needScreenshot = await Assert.ThrowsAsync<BrowserException>(() => _page.CuaClickAsync(new Point(10, 10), MouseButton.Left, Ct));
        Assert.Contains("browser_screenshot", needScreenshot.Message);

        await _page.ScreenshotAsync(Ct);
        var outside = await Assert.ThrowsAsync<BrowserException>(() => _page.CuaClickAsync(new Point(1281, 10), MouseButton.Left, Ct));
        Assert.Contains("outside the 1280x720 viewport", outside.Message);
        Assert.Empty(_server.Of("Input.dispatchMouseEvent"));

        await _page.CuaClickAsync(new Point(10, 20), MouseButton.Left, Ct); // 截图仍然新鲜(失败的尝试不消耗它)
        Assert.Single(Mouse("mousePressed"));
        // 成功之后观察被消耗
        await Assert.ThrowsAsync<BrowserException>(() => _page.CuaClickAsync(new Point(10, 20), MouseButton.Left, Ct));
    }

    [Fact]
    public async Task A_snapshot_invalidates_the_screenshot_freshness_too()
    {
        await _page.ScreenshotAsync(Ct);
        await _page.SnapshotAsync(Ct);
        await Assert.ThrowsAsync<BrowserException>(() => _page.CuaClickAsync(new Point(10, 20), MouseButton.Left, Ct));
    }

    [Fact]
    public async Task A_drag_preserves_every_path_point_in_order()
    {
        await _page.ScreenshotAsync(Ct);
        var path = new[] { new Point(10, 10), new Point(30, 20), new Point(50, 80), new Point(70, 90) };

        await _page.CuaDragAsync(path, ["Shift"], Ct);

        var events = _server.Of("Input.dispatchMouseEvent").ToList();
        Assert.Equal(["mouseMoved", "mousePressed", "mouseMoved", "mouseMoved", "mouseMoved", "mouseReleased"], events.Select(r => r.Str("type")));
        Assert.Equal([10, 10, 30, 50, 70, 70], events.Select(r => r.Num("x")!.Value));
        Assert.Equal([10, 10, 20, 80, 90, 90], events.Select(r => r.Num("y")!.Value));
        Assert.All(events, r => Assert.Equal(8, r.Num("modifiers")));
    }

    [Fact]
    public async Task A_drag_needs_two_points_all_inside_the_viewport_and_known_modifiers()
    {
        await _page.ScreenshotAsync(Ct);
        await Assert.ThrowsAsync<BrowserException>(() => _page.CuaDragAsync([new Point(1, 1)], [], Ct));
        await Assert.ThrowsAsync<BrowserException>(() => _page.CuaDragAsync([new Point(1, 1), new Point(5000, 5)], [], Ct));
        await Assert.ThrowsAsync<BrowserException>(() => _page.CuaDragAsync([new Point(1, 1), new Point(5, 5)], ["Hyper"], Ct));
        Assert.Empty(_server.Of("Input.dispatchMouseEvent"));
    }

    // ── 对话框 / 生命周期 ──

    [Fact]
    public async Task JavaScript_dialogs_are_dismissed_and_beforeunload_is_let_through()
    {
        await _server.PushAsync("Page.javascriptDialogOpening", "{\"type\":\"confirm\",\"message\":\"sure?\"}", _page.SessionId);
        await _server.PushAsync("Page.javascriptDialogOpening", "{\"type\":\"beforeunload\"}", _page.SessionId);
        await Task.Delay(250);

        var answers = _server.Of("Page.handleJavaScriptDialog").Select(r => r.Params.GetProperty("accept").GetBoolean()).ToList();
        Assert.Equal(2, answers.Count);
        Assert.Contains(false, answers);
        Assert.Contains(true, answers);
    }

    [Fact]
    public async Task A_destroyed_target_marks_the_page_gone_and_disposing_closes_it_once()
    {
        await _server.PushAsync("Target.targetDestroyed", $"{{\"targetId\":\"{_page.TargetId}\"}}");
        await Task.Delay(100);
        Assert.True(_page.Gone);

        var fresh = await BrowserPage.OpenAsync(_conn, TimeProvider.System, Ct);
        await fresh.DisposeAsync();
        await fresh.DisposeAsync();
        Assert.Single(_server.Of("Target.closeTarget"), r => r.Str("targetId") == fresh.TargetId);
    }

    [Fact]
    public async Task Evaluation_exceptions_surface_the_page_error_description()
    {
        _server.Handle("Runtime.evaluate", "{\"exceptionDetails\":{\"text\":\"Uncaught\",\"exception\":{\"description\":\"ReferenceError: nope is not defined\"}}}");
        var e = await Assert.ThrowsAsync<BrowserException>(() => _page.SnapshotAsync(Ct));
        Assert.Contains("ReferenceError", e.Message);
    }
}
