using System.Text.RegularExpressions;
using Ncw.Client.Widgets;

namespace Ncw.Client.Tests.Widgets;

/// <summary>
/// 外壳页面的装配(2.x 的 <c>widget-protocol.test.ts</c> / <c>widget-runtime-artifact.test.ts</c> / <c>widget-tokens.test.ts</c> 按 3.0 的形状移植):
/// CSP 的每一条、nonce 每页不同、外壳脚本自包含、消息名与 C# 常量一致、主题映射。JS 本身只能在真实 WebView 里跑(<c>NcwWidgetSmokeTests</c>)。
/// </summary>
public class WidgetShellTests
{
    private static readonly string Nonce = "0123456789abcdef0123456789abcdef";

    private static Dictionary<string, string> Directives(string csp) =>
        csp.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Split(' ', 2))
            .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");

    [Fact]
    public void The_policy_denies_everything_by_default_and_opens_only_what_a_widget_needs()
    {
        var d = Directives(WidgetShell.Csp(Nonce));
        Assert.Equal("'none'", d["default-src"]);
        Assert.Equal("'none'", d["connect-src"]);   // 程序化出网默认全拒(比 2.x 的「CDN 白名单」更严)
        Assert.Equal("'none'", d["frame-src"]);
        Assert.Equal("'none'", d["object-src"]);
        Assert.Equal("'none'", d["base-uri"]);
        Assert.Equal("'none'", d["form-action"]);
        Assert.Equal("data: blob:", d["img-src"]);  // 没有外链图片:服务端消毒本来就拿掉它们,这里不比服务端更宽
        Assert.DoesNotContain("https:", d["img-src"]);
    }

    [Fact]
    public void Scripts_need_the_pages_nonce_or_a_whitelisted_cdn_and_never_unsafe_inline_or_eval()
    {
        var csp = WidgetShell.Csp(Nonce);
        var script = Directives(csp)["script-src"];
        Assert.StartsWith($"'nonce-{Nonce}'", script);
        Assert.DoesNotContain("unsafe-inline", script);
        Assert.DoesNotContain("unsafe-eval", script);
        Assert.DoesNotContain("wasm-unsafe-eval", script);
        Assert.DoesNotContain("strict-dynamic", script);
        Assert.DoesNotContain("*", script);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.DoesNotContain("unsafe-hashes", csp);
        // 2.x 的断言:script-src 里不能有 unsafe-inline(有了 nonce 就没有意义了)
        Assert.DoesNotMatch("script-src[^;]*unsafe-inline", csp);
    }

    [Fact]
    public void Only_the_exact_cdn_hosts_are_allowed_and_only_over_https()
    {
        var d = Directives(WidgetShell.Csp(Nonce));
        string[] expected = ["cdn.jsdelivr.net", "unpkg.com", "cdnjs.cloudflare.com", "esm.sh"];
        Assert.Equal(expected.Order(), WidgetShell.CdnHosts.Order());
        foreach (var directive in new[] { "script-src", "style-src", "font-src" })
        {
            var sources = d[directive].Split(' ');
            Assert.Equal(expected.Select(h => "https://" + h).Order(), sources.Where(s => s.StartsWith("https://", StringComparison.Ordinal)).Order());
            Assert.DoesNotContain(sources, s => s.StartsWith("http:", StringComparison.Ordinal) || s.Contains('*', StringComparison.Ordinal));
        }

        Assert.DoesNotContain("https://", d["img-src"]);
        Assert.DoesNotContain("https://", d["connect-src"]);
    }

    [Fact]
    public void Every_page_gets_its_own_unguessable_nonce()
    {
        var nonces = Enumerable.Range(0, 50).Select(_ => WidgetShell.NewNonce()).ToList();
        Assert.Equal(50, nonces.Distinct().Count());
        Assert.All(nonces, n => Assert.Matches("^[0-9a-f]{32}$", n));
        Assert.NotEqual(WidgetShell.Build(nonces[0]), WidgetShell.Build(nonces[1]));
    }

    [Fact]
    public void The_policy_is_the_first_thing_in_the_document_and_there_is_exactly_one_inline_script_carrying_the_nonce()
    {
        var html = WidgetShell.Build(Nonce);
        var csp = html.IndexOf("http-equiv=\"Content-Security-Policy\"", StringComparison.Ordinal);
        Assert.InRange(csp, 0, 200);
        Assert.True(html.IndexOf("<script", StringComparison.Ordinal) > csp); // 策略生效之前没有任何脚本
        Assert.Single(Regex.Matches(html, "<script"));
        Assert.Contains($"<script nonce=\"{Nonce}\">", html);
        Assert.Contains($"<meta name=\"ncw-widget-nonce\" content=\"{Nonce}\">", html);
        Assert.DoesNotContain("<iframe", html);
        Assert.DoesNotContain("<base", html);
        Assert.DoesNotContain("<link", html);
        Assert.DoesNotContain("src=\"", html.Replace("img-src", "", StringComparison.Ordinal)); // 没有任何外部资源引用
    }

    [Fact]
    public void The_shell_carries_the_spec_base_styles_and_a_transparent_background()
    {
        var html = WidgetShell.Build(Nonce);
        Assert.Contains("svg .box", html);
        Assert.Contains("svg .c-purple > rect", html);
        Assert.Contains("--color-text-primary", html);
        Assert.Contains("background: transparent", html);
        Assert.True(html.IndexOf("svg .box", StringComparison.Ordinal) < html.IndexOf("#ncw-root { display: flow-root", StringComparison.Ordinal), "外壳自己的规则要排在基础样式表后面,才盖得住它");
    }

    // ───────────────────────── 运行时脚本 ─────────────────────────

    [Fact]
    public void The_runtime_is_self_contained_and_reaches_for_nothing_dangerous()
    {
        var js = WidgetShell.Runtime;
        // 2.x 的「自包含」断言:不许出现 require / 裸 import(那份运行时是页面里的一条独立脚本)
        Assert.DoesNotContain("require(", js);
        Assert.DoesNotMatch(new Regex(@"^\s*import\s", RegexOptions.Multiline), js);
        Assert.DoesNotContain("import(", js);
        Assert.DoesNotContain("eval(", js);
        Assert.DoesNotContain("new Function", js);
        Assert.DoesNotContain("document.write", js);
        Assert.DoesNotContain("XMLHttpRequest", js);
        Assert.DoesNotContain("fetch(", js);
        Assert.DoesNotContain("WebSocket", js);
        Assert.DoesNotContain("localStorage", js);
        Assert.DoesNotContain("postMessage", js);
        Assert.DoesNotContain("sendPrompt", js.Replace("没有 sendPrompt", "", StringComparison.Ordinal)); // 和 2.x 一样:不给 widget 一条通往 Agent 主循环的输入通道
    }

    [Fact]
    public void Every_message_name_is_spelled_the_same_in_the_runtime_and_in_csharp()
    {
        foreach (var name in WidgetMessages.All)
        {
            Assert.Contains($"'{name}'", WidgetShell.Runtime);
        }

        // 反过来:运行时里的 ncw:widget:* 字面量都在 C# 常量里
        var inJs = Regex.Matches(WidgetShell.Runtime, "'(ncw:widget:[a-z]+)'").Select(m => m.Groups[1].Value).Distinct().Order().ToList();
        Assert.Equal(WidgetMessages.All.Order(), inJs);
        Assert.Contains(WidgetPush.HostObject, WidgetShell.Runtime);
    }

    [Fact]
    public void The_runtime_exposes_the_documented_ncw_api_and_the_bridge_methods_match_the_whitelist()
    {
        var js = WidgetShell.Runtime;
        foreach (var api in new[] { "getState:", "setState:", "submit:", "runAction:", "openUrl:", "onData:" })
        {
            Assert.Contains(api, js);
        }

        // 页面调用的桥方法名 == 宿主的白名单
        var called = Regex.Matches(js, @"call\('([A-Za-z]+)'").Select(m => m.Groups[1].Value).Distinct().Order().ToList();
        Assert.Equal(new[] { WidgetMethods.SetState, WidgetMethods.Submit, WidgetMethods.RunAction, WidgetMethods.OpenUrl }.Order(), called);
        Assert.Contains("Object.freeze(ncw)", js);
        Assert.Contains("invokeCSharpAction", js);
    }

    [Fact]
    public void Scripts_are_rebuilt_in_order_so_a_cdn_library_is_loaded_before_the_inline_script_that_uses_it()
    {
        Assert.Contains("addEventListener('load', resume)", WidgetShell.Runtime);
        Assert.Contains("script.nonce = nonce", WidgetShell.Runtime);
    }

    // ───────────────────────── 推送消息 ─────────────────────────

    [Fact]
    public void Push_messages_are_valid_json_that_can_sit_inside_a_script_statement()
    {
        var evil = "</script><script>alert(1)</script>\u2028\u2029'\"\\";
        foreach (var json in new[] { WidgetPush.Content(evil, "html"), WidgetPush.Loading(evil), WidgetPush.Reply(1, false, error: evil) })
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            Assert.True(doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object);
            Assert.DoesNotContain("</script>", json);
            Assert.DoesNotContain("<", json);
            Assert.DoesNotContain("\u2028", json);
            Assert.DoesNotContain("\u2029", json);
        }

        Assert.Equal($"{WidgetPush.HostObject}.recv({{\"a\":1}});", WidgetPush.Script("{\"a\":1}"));
    }

    [Fact]
    public void The_environment_push_carries_state_data_and_actions_and_drops_state_that_is_not_valid_json()
    {
        var env = new WidgetEnvironment("pick_db", "{\"n\":1}", "[1,2]", true, false,
            [new WidgetActionInfo("say", "Say", [new("word", new WidgetParamSpec("enum", ["hello", "world"], 0, 0, false)), new("n", new WidgetParamSpec("number", [], 1, 5, true))])]);
        using var doc = System.Text.Json.JsonDocument.Parse(WidgetPush.Env(env));
        var e = doc.RootElement.GetProperty("env");
        Assert.Equal(1, e.GetProperty("state").GetProperty("n").GetInt32());
        Assert.Equal(2, e.GetProperty("data").GetArrayLength());
        Assert.True(e.GetProperty("interactive").GetBoolean());
        var action = e.GetProperty("actions")[0];
        Assert.Equal("say", action.GetProperty("id").GetString());
        Assert.Equal("world", action.GetProperty("params").GetProperty("word").GetProperty("values")[1].GetString());
        Assert.Equal(5, action.GetProperty("params").GetProperty("n").GetProperty("max").GetInt32());
        // 动作要跑的工具和入参不在推送里:widget 看不到「要跑什么」
        Assert.DoesNotContain("tool", WidgetPush.Env(env));

        using var broken = System.Text.Json.JsonDocument.Parse(WidgetPush.Env(env with { StateJson = "{not json", DataJson = "}" }));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, broken.RootElement.GetProperty("env").GetProperty("state").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, broken.RootElement.GetProperty("env").GetProperty("data").ValueKind);
    }

    // ───────────────────────── 主题映射(2.x widget-tokens.test.ts) ─────────────────────────

    private static Dictionary<string, string> Source(string fg, string accent) => new()
    {
        ["Foreground"] = fg,
        ["TextSecondary"] = "#666666",
        ["TextMuted"] = "#999999",
        ["Accent"] = accent,
        ["AccentSubtle"] = "rgba(0,113,227,0.14)",
        ["Danger"] = "#e0352b",
        ["DangerSubtle"] = "rgba(224,53,43,0.14)",
        ["Success"] = "#3f7a28",
        ["SuccessSubtle"] = "rgba(63,122,40,0.14)",
        ["Warning"] = "#7a5a16",
        ["WarningSubtle"] = "rgba(122,90,22,0.14)",
        ["Background"] = "#ffffff",
        ["Surface"] = "#f5f5f7",
        ["SurfaceRaised"] = "#fafafa",
        ["SurfaceMuted"] = "#eeeeee",
        ["BodyFontFamily"] = "\"Inter\", system-ui, sans-serif",
        ["MonoFontFamily"] = "ui-monospace, monospace",
    };

    [Fact]
    public void App_tokens_are_translated_into_the_names_the_guidelines_know()
    {
        var t = WidgetTokens.Map(WidgetAppearance.Light, Source("#111111", "#0071e3")).Tokens;
        Assert.Equal("#111111", t["--color-text-primary"]);
        Assert.Equal("#666666", t["--color-text-secondary"]);
        Assert.Equal("#999999", t["--color-text-tertiary"]);
        Assert.Equal("#fafafa", t["--color-background-primary"]);
        Assert.Equal("#f5f5f7", t["--color-background-secondary"]);
        Assert.Equal("#ffffff", t["--color-background-tertiary"]);
        Assert.Equal("#3f7a28", t["--color-text-success"]);
        Assert.Equal("rgba(63,122,40,0.14)", t["--color-background-success"]);
        Assert.Equal("#111111", t["--p"]);
        Assert.Equal("#f5f5f7", t["--bg2"]);
        Assert.Equal("\"Inter\", system-ui, sans-serif", t["--font-sans"]);
    }

    [Fact]
    public void The_info_family_follows_the_accent()
    {
        var blue = WidgetTokens.Map(WidgetAppearance.Dark, Source("#eeeeee", "#0071e3")).Tokens;
        var green = WidgetTokens.Map(WidgetAppearance.Dark, Source("#eeeeee", "#16a34a")).Tokens;
        Assert.Equal("#0071e3", blue["--color-text-info"]);
        Assert.Equal("#0071e3", blue["--color-border-info"]);
        Assert.Equal("#16a34a", green["--color-text-info"]);
        Assert.Equal("#16a34a", green["--color-border-info"]);
    }

    [Fact]
    public void Borders_are_mixed_from_the_text_color_at_three_opacities()
    {
        var t = WidgetTokens.Map(WidgetAppearance.Dark, Source("#ff8000", "#0071e3")).Tokens;
        Assert.Equal("rgba(255,128,0,0.4)", t["--color-border-primary"]);
        Assert.Equal("rgba(255,128,0,0.3)", t["--color-border-secondary"]);
        Assert.Equal("rgba(255,128,0,0.15)", t["--color-border-tertiary"]);
        Assert.Equal("rgba(255,128,0,0.3)", t["--b"]);

        // 文字色本身带透明度也行:透明度相乘
        var translucent = WidgetTokens.Map(WidgetAppearance.Dark, Source("rgba(255,255,255,0.5)", "#0071e3")).Tokens;
        Assert.Equal("rgba(255,255,255,0.2)", translucent["--color-border-primary"]);
    }

    [Fact]
    public void Two_appearances_with_different_sources_produce_different_card_surfaces_and_the_scheme_travels_with_it()
    {
        var light = WidgetTokens.Map(WidgetAppearance.Light, Source("#111111", "#0071e3"));
        var darkSource = Source("#f5f5f7", "#0071e3");
        darkSource["SurfaceRaised"] = "#2c2c2e";
        var dark = WidgetTokens.Map(WidgetAppearance.Dark, darkSource);
        Assert.NotEqual(light.Tokens["--color-background-primary"], dark.Tokens["--color-background-primary"]);
        Assert.Equal(WidgetAppearance.Light, light.Appearance);
        using var doc = System.Text.Json.JsonDocument.Parse(WidgetPush.Theme(dark));
        Assert.Equal("dark", doc.RootElement.GetProperty("appearance").GetString());
    }

    [Fact]
    public void A_missing_source_means_the_variable_is_not_emitted_so_the_shell_default_applies_and_nothing_is_empty()
    {
        var t = WidgetTokens.Map(WidgetAppearance.Dark, new Dictionary<string, string> { ["Foreground"] = "#ffffff", ["Accent"] = "  " }).Tokens;
        Assert.DoesNotContain("--color-text-info", t.Keys);
        Assert.DoesNotContain("--color-text-secondary", t.Keys);
        Assert.All(t.Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
        Assert.Equal("#ffffff", t["--color-text-primary"]);
    }

    [Fact]
    public void Only_the_two_weights_the_guidelines_allow_are_defined_and_unparseable_colors_are_skipped()
    {
        var t = WidgetTokens.Map(WidgetAppearance.Dark, new Dictionary<string, string> { ["Foreground"] = "papayawhip" }).Tokens;
        Assert.Equal("400", t["--font-weight-normal"]);
        Assert.Equal("500", t["--font-weight-medium"]);
        Assert.DoesNotContain("--font-weight-semibold", t.Keys);
        Assert.DoesNotContain("--font-weight-bold", t.Keys);
        Assert.DoesNotContain("--color-border-primary", t.Keys);
    }

    [Fact]
    public void Every_variable_name_is_a_double_dash_name_the_runtime_will_accept()
    {
        var t = WidgetTokens.Map(WidgetAppearance.Dark, Source("#111111", "#0071e3")).Tokens;
        Assert.All(t.Keys, k => Assert.Matches("^--[a-z0-9-]{1,48}$", k));
        Assert.All(t.Values, v => Assert.True(v.Length <= 400));
    }

    [Fact]
    public void Css_colors_are_opaque_hex_or_rgba()
    {
        Assert.Equal("#0a0b0c", WidgetTokens.CssColor(255, 10, 11, 12));
        Assert.Equal("rgba(10,11,12,0.502)", WidgetTokens.CssColor(128, 10, 11, 12));
    }
}
