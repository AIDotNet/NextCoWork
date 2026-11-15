using System.Text.Json;
using System.Text.RegularExpressions;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Browser.Tests;

/// <summary>
/// 工具层(移植自 2.x <c>builtin/__tests__/browser.test.ts</c>):从已绑定页面返回带 ref 的真实快照、把 ref 点击委托给同一页面并拒绝另一个 run、截图作为模型可见的图片返回、file:// 能打开。
/// 另加 3.0 的:URL 筛查、参数校验、特征(traits)与 2.x 一致、Details 带执行环境标识、失败带 errorKey。
/// </summary>
public sealed class ToolTests
{
    [Fact]
    public async Task A_snapshot_comes_from_the_bound_page_with_refs_and_page_metadata()
    {
        await using var h = new Harness();
        var tab = await h.OpenTab();

        var result = await h.Call("browser_snapshot", ("tabId", tab));

        Assert.False(result.IsError);
        var text = Harness.Text(result);
        Assert.Contains("Page: Example", text);
        Assert.Contains("URL: https://example.com/", text);
        Assert.Contains("Viewport: 1280x720", text);
        Assert.Contains("[ref=e2]", text);
    }

    [Fact]
    public async Task A_ref_click_goes_to_the_same_page_and_another_run_in_another_session_is_refused()
    {
        await using var h = new Harness();
        var tab = await h.OpenTab(run: "run-a", session: "s-a");
        await h.Run("browser_snapshot", "run-a", "s-a", "ws-1", ("tabId", tab));

        var rejected = await h.Run("browser_click", "run-b", "s-b", "ws-1", ("tabId", tab), ("ref", "e2"));
        var accepted = await h.Run("browser_click", "run-a", "s-a", "ws-1", ("tabId", tab), ("ref", "e2"));

        Assert.False(accepted.IsError, Harness.Text(accepted));
        Assert.Contains("Clicked e2", Harness.Text(accepted));
        Assert.True(rejected.IsError);
        Assert.Contains("opened by this Agent session", Harness.Text(rejected));
        Assert.Single(h.Server.Of("Input.dispatchMouseEvent"), r => r.Str("type") == "mousePressed");
    }

    [Fact]
    public async Task A_screenshot_is_returned_as_a_model_visible_image_part()
    {
        await using var h = new Harness();
        var tab = await h.OpenTab();

        var result = await h.Call("browser_screenshot", ("tabId", tab));

        Assert.False(result.IsError);
        var image = Assert.Single(result.Content.OfType<ImagePart>());
        Assert.Equal("image/png", image.Mime);
        Assert.StartsWith("data:image/png;base64,", image.DataRef);
        Assert.Contains("image 1280x720 pixels, CSS viewport 1280x720", Harness.Text(result));
        // 图片本身不进 Details(只给界面的结构化数据里放尺寸)
        Assert.DoesNotContain("base64", result.Details!.Value.GetRawText());
        Assert.Equal(1280, result.Details.Value.GetProperty("width").GetInt32());
    }

    [Fact]
    public async Task A_file_url_opens_and_the_other_schemes_do_not()
    {
        await using var h = new Harness();

        var file = await h.Call("browser_open", ("url", "file:///tmp/report.html"));
        Assert.False(file.IsError, Harness.Text(file));
        Assert.Equal("file:///tmp/report.html", h.Server.Of("Page.navigate").Single().Str("url"));

        foreach (var bad in new[] { "javascript:alert(1)", "data:text/html,<h1>x</h1>", "ftp://example.com/x", "chrome://settings", "view-source:https://example.com", "about:srcdoc", "blob:https://example.com/x" })
        {
            var result = await h.Call("browser_open", ("url", bad));
            Assert.True(result.IsError, bad);
            Assert.Equal(BrowserErrorKeys.UrlRefused, Harness.ErrorKey(result));
        }

        Assert.Single(h.Server.Of("Page.navigate"));
    }

    [Theory]
    [InlineData("https://user:pass@example.com/")]
    [InlineData("http://admin@example.com/")]
    [InlineData("file://user:pw@host/etc/passwd")]
    public async Task Credentials_in_the_url_are_refused(string url)
    {
        await using var h = new Harness();
        var result = await h.Call("browser_open", ("url", url));
        Assert.Equal(BrowserErrorKeys.UrlRefused, Harness.ErrorKey(result));
        // .NET 的 Uri 连 file://user:pw@host 都不认(Malformed),其余带凭据的 URL 走专门的说明
        Assert.True(url.StartsWith("file:", StringComparison.Ordinal) || Harness.Text(result).Contains("username or password", StringComparison.Ordinal));
        Assert.Empty(h.Server.Of("Page.navigate"));
    }

    [Fact]
    public async Task Loopback_and_lan_pages_are_allowed_by_default_as_in_2x_and_a_stricter_policy_can_be_plugged_in()
    {
        await using (var h = new Harness())
        {
            Assert.False((await h.Call("browser_open", ("url", "http://127.0.0.1:5173/"))).IsError);
            Assert.False((await h.Call("browser_open", ("url", "http://192.168.1.5/"))).IsError);
        }

        await using var strict = new Harness(policy: new DenyPrivatePolicy());
        var denied = await strict.Call("browser_open", ("url", "http://127.0.0.1:5173/"));
        Assert.Equal(BrowserErrorKeys.UrlRefused, Harness.ErrorKey(denied));
        Assert.Contains("private", Harness.Text(denied));
        Assert.Empty(strict.Server.Of("Page.navigate"));
    }

    private sealed class DenyPrivatePolicy : IBrowserUrlPolicy
    {
        public string? Check(Uri url) => url.Host is "127.0.0.1" or "localhost" || url.Host.StartsWith("192.168.", StringComparison.Ordinal) ? "That is a private address." : null;
    }

    [Fact]
    public async Task Navigate_applies_the_same_url_checks_and_reports_the_final_url()
    {
        await using var h = new Harness();
        var tab = await h.OpenTab();

        var bad = await h.Call("browser_navigate", ("tabId", tab), ("url", "javascript:alert(1)"));
        Assert.Equal(BrowserErrorKeys.UrlRefused, Harness.ErrorKey(bad));

        var ok = await h.Call("browser_navigate", ("tabId", tab), ("url", "https://example.com/next"));
        Assert.False(ok.IsError);
        Assert.Contains("navigated to", Harness.Text(ok));
        Assert.Contains("fresh browser_snapshot", Harness.Text(ok));
    }

    [Fact]
    public async Task A_malformed_url_and_missing_arguments_are_tool_errors_not_exceptions()
    {
        await using var h = new Harness();
        Assert.Equal(BrowserErrorKeys.UrlRefused, Harness.ErrorKey(await h.Call("browser_open", ("url", "not a url"))));
        Assert.True((await h.Call("browser_open")).IsError);
        Assert.True((await h.Call("browser_snapshot")).IsError);
        var tab = await h.OpenTab();
        Assert.True((await h.Call("browser_click", ("tabId", tab))).IsError); // 缺 ref
    }

    [Theory]
    [InlineData("12")]
    [InlineData("e0")]
    [InlineData("e1; alert(1)")]
    [InlineData("E3")]
    public async Task A_ref_must_look_like_a_ref(string reference)
    {
        await using var h = new Harness();
        var tab = await h.OpenTab();
        await h.Call("browser_snapshot", ("tabId", tab));

        var result = await h.Call("browser_click", ("tabId", tab), ("ref", reference));

        Assert.True(result.IsError);
        Assert.Empty(h.Server.Of("Input.dispatchMouseEvent"));
    }

    [Fact]
    public async Task Wait_for_text_succeeds_or_times_out_and_a_plain_sleep_works()
    {
        await using var h = new Harness();
        var tab = await h.OpenTab();
        h.Page.BodyText = "Welcome back";

        var found = await h.Call("browser_wait", ("tabId", tab), ("text", "Welcome"));
        Assert.False(found.IsError);
        Assert.True(found.Details!.Value.GetProperty("found").GetBoolean());

        var missing = await h.Call("browser_wait", ("tabId", tab), ("text", "Goodbye"), ("timeoutMs", 100));
        Assert.True(missing.IsError);
        Assert.Contains("did not appear", Harness.Text(missing));

        Assert.False((await h.Call("browser_wait", ("tabId", tab), ("ms", 5))).IsError);
        Assert.True((await h.Call("browser_wait", ("tabId", tab))).IsError); // 两个都没给
    }

    [Fact]
    public async Task Wait_does_not_consume_the_observation_so_refs_still_work_afterwards()
    {
        await using var h = new Harness();
        var tab = await h.OpenTab();
        await h.Call("browser_snapshot", ("tabId", tab));
        await h.Call("browser_wait", ("tabId", tab), ("ms", 1));
        Assert.False((await h.Call("browser_click", ("tabId", tab), ("ref", "e2"))).IsError);
    }

    [Fact]
    public async Task Text_returns_the_visible_text_with_page_metadata_and_marks_truncation()
    {
        await using var h = new Harness();
        var tab = await h.OpenTab();
        h.Server.CurrentTitle = "Docs";
        h.Page.BodyText = new string('x', 70_000);

        var result = await h.Call("browser_text", ("tabId", tab));

        var text = Harness.Text(result);
        Assert.StartsWith("Page: Docs\nURL: https://example.com/", text);
        Assert.Contains("[Text truncated at 60000 characters.]", text);
        Assert.True(result.Details!.Value.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Every_result_and_every_failure_carries_the_execution_environment_identity()
    {
        await using var h = new Harness();
        var tab = await h.OpenTab();
        var ok = await h.Call("browser_snapshot", ("tabId", tab));
        var failed = await h.Call("browser_snapshot", ("tabId", "tab-nonexistent"));

        foreach (var result in new[] { ok, failed })
        {
            var environment = result.Details!.Value.GetProperty("environment");
            Assert.Equal("dev-1", environment.GetProperty("deviceId").GetString());
            Assert.Equal("local", environment.GetProperty("environment").GetString());
            Assert.Equal("local", environment.GetProperty("type").GetString());
            Assert.Equal("browser", result.Details.Value.GetProperty("kind").GetString());
        }

        Assert.Equal(BrowserErrorKeys.TabNotFound, Harness.ErrorKey(failed));
    }

    [Fact]
    public async Task A_lost_connection_during_a_call_is_a_browser_gone_error()
    {
        await using var h = new Harness();
        var tab = await h.OpenTab();
        await h.Server.DropConnectionsAsync();
        await Task.Delay(50);

        var result = await h.Call("browser_snapshot", ("tabId", tab));

        Assert.True(result.IsError);
        Assert.Contains(Harness.ErrorKey(result), new[] { BrowserErrorKeys.BrowserGone, BrowserErrorKeys.TabNotFound });
    }

    [Fact]
    public async Task Cancellation_propagates_instead_of_becoming_a_tool_error()
    {
        await using var h = new Harness();
        var tab = await h.OpenTab();
        h.Server.Handle("Runtime.evaluate", _ =>
        {
            Thread.Sleep(400);
            return "{}";
        });
        var impl = h.Tools["browser_snapshot"];
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        var inner = new ToolContext(new Ncw.Agent.Kernel.RunId("run-1"), "c", "session-1", 0, null, new Progress<ToolProgress>(), new FileMutationQueue());
        var ctx = new Ncw.Agent.Tools.ExecutionToolContext(inner, new Ncw.Agent.Tools.ToolRuntime { Environment = h.Env, WorkspaceId = "ws-1" });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await impl.ExecuteAsync(new ToolInvocation("c", "browser_snapshot", impl, Harness.Args(("tabId", tab))), ctx, cts.Token));
    }

    // ── 工具清单 / 元数据 ──

    [Fact]
    public async Task The_tool_set_has_unique_names_descriptions_snippets_and_a_schema_each()
    {
        await using var h = new Harness();
        var specs = h.Tools.Values.Select(t => t.Info).ToList();

        Assert.Equal(BrowserTools.Names.Order(), specs.Select(s => s.InternalId).Order());
        Assert.Equal(specs.Count, specs.Select(s => s.InternalId).Distinct().Count());
        Assert.All(specs, s =>
        {
            Assert.Matches("^[a-z_]+$", s.InternalId);
            Assert.True(s.Description.Length > 30, s.InternalId);
            Assert.False(string.IsNullOrWhiteSpace(s.PromptSnippet), s.InternalId);
            Assert.NotNull(s.PromptGuidelines);
            Assert.Equal(JsonValueKind.Object, s.InputSchema.ValueKind);
            Assert.Equal("object", s.InputSchema.GetProperty("type").GetString());
        });
    }

    [Fact]
    public async Task Traits_follow_2x_and_all_browser_tools_are_serial()
    {
        await using var h = new Harness();
        (bool ReadOnly, bool NeedsNetwork) T(string name) => (h.Tools[name].Info.Traits.ReadOnly, h.Tools[name].Info.Traits.NeedsNetwork);

        // 2.x browser.ts:打开 / 导航只读且联网;点击类写且联网;观察类只读不联网
        Assert.Equal((true, true), T("browser_open"));
        Assert.Equal((true, true), T("browser_navigate"));
        foreach (var write in new[] { "browser_click", "browser_type", "browser_press", "browser_select", "browser_cua_click", "browser_cua_drag" })
        {
            Assert.Equal((false, true), T(write));
        }

        foreach (var read in new[] { "browser_snapshot", "browser_scroll", "browser_screenshot", "browser_tabs", "browser_close", "browser_wait", "browser_text" })
        {
            Assert.Equal((true, false), T(read));
        }

        Assert.All(h.Tools.Values, t =>
        {
            Assert.False(t.Info.Traits.Destructive);
            Assert.False(t.Info.Traits.ConcurrencySafe, t.Info.InternalId); // 一个页面只有一个状态:同批串行
        });
    }

    [Fact]
    public async Task The_write_tools_tell_the_model_to_observe_then_act()
    {
        await using var h = new Harness();
        foreach (var name in new[] { "browser_click", "browser_type", "browser_press", "browser_select" })
        {
            Assert.Contains("latest browser_snapshot", h.Tools[name].Info.Description);
            Assert.Contains(h.Tools[name].Info.PromptGuidelines!, g => g.Contains("Perform one state-changing action", StringComparison.Ordinal));
        }

        Assert.Contains("MUST take a fresh browser_screenshot", h.Tools["browser_cua_click"].Info.Description);
    }

    [Fact]
    public async Task Argument_validation_accepts_loose_numbers_and_rejects_out_of_range_values()
    {
        await using var h = new Harness();
        var tab = await h.OpenTab();
        await h.Call("browser_snapshot", ("tabId", tab));

        // clickCount 超范围 → 预检失败,不会触达页面
        var bad = await h.Call("browser_click", ("tabId", tab), ("ref", "e2"), ("clickCount", 9));
        Assert.True(bad.IsError);
        Assert.Empty(h.Server.Of("Input.dispatchMouseEvent"));
        // 字符串数字被宽松转换
        var loose = await h.Call("browser_click", ("tabId", tab), ("ref", "e2"), ("clickCount", "2"));
        Assert.False(loose.IsError, Harness.Text(loose));
        Assert.Equal(2, h.Server.Of("Input.dispatchMouseEvent").First(r => r.Str("type") == "mousePressed").Num("clickCount"));
    }

    [Fact]
    public async Task Press_type_select_scroll_and_drag_work_end_to_end_through_the_tools()
    {
        await using var h = new Harness();
        var tab = await h.OpenTab();

        await h.Call("browser_snapshot", ("tabId", tab));
        Assert.False((await h.Call("browser_type", ("tabId", tab), ("ref", "e3"), ("text", "Ada"))).IsError);

        await h.Call("browser_snapshot", ("tabId", tab));
        var press = await h.Call("browser_press", ("tabId", tab), ("keys", new System.Text.Json.Nodes.JsonArray("Control", "Enter")));
        Assert.False(press.IsError, Harness.Text(press));
        Assert.Contains("Control+Enter", Harness.Text(press));

        await h.Call("browser_snapshot", ("tabId", tab));
        Assert.False((await h.Call("browser_select", ("tabId", tab), ("ref", "e2"), ("values", new System.Text.Json.Nodes.JsonArray("a")))).IsError);

        await h.Call("browser_snapshot", ("tabId", tab));
        Assert.False((await h.Call("browser_scroll", ("tabId", tab), ("scrollY", 300), ("x", 10), ("y", 20))).IsError);

        await h.Call("browser_screenshot", ("tabId", tab));
        var path = new System.Text.Json.Nodes.JsonArray(
            new System.Text.Json.Nodes.JsonObject { ["x"] = 5, ["y"] = 5 },
            new System.Text.Json.Nodes.JsonObject { ["x"] = 50, ["y"] = 60 });
        var drag = await h.Call("browser_cua_drag", ("tabId", tab), ("path", path));
        Assert.False(drag.IsError, Harness.Text(drag));
        Assert.Contains("Dragged across 2 points", Harness.Text(drag));

        await h.Call("browser_screenshot", ("tabId", tab));
        Assert.False((await h.Call("browser_cua_click", ("tabId", tab), ("x", 10), ("y", 20), ("button", "right"))).IsError);
    }

    [Fact]
    public async Task Observation_tools_say_what_to_do_when_the_page_has_nothing_yet()
    {
        await using var h = new Harness();
        var none = await h.Call("browser_tabs");
        Assert.Contains("No controlled browser tabs", Harness.Text(none));
        var stale = await h.Call("browser_click", ("tabId", await h.OpenTab()), ("ref", "e1"));
        Assert.True(stale.IsError);
        Assert.Equal(BrowserErrorKeys.StaleObservation, Harness.ErrorKey(stale));
        Assert.Matches(new Regex("browser_snapshot"), Harness.Text(stale));
    }
}
