using Ncw.Agent.Widgets.Sanitizing;

namespace Ncw.Agent.Widgets.Tests;

/// <summary>
/// 载荷消毒(3.0 新增,2.x 只靠渲染宿主的 CSP):恶意载荷必须被拿掉、说明规则 id;没有危险物的载荷必须逐字不变(<c>visualize_edit_widget</c> 的 <c>old_string</c> 靠这一点对得上)。
/// 每条断言都是「输出里不再含有这个能执行 / 能外联的东西」,而不是「输出等于某个具体字符串」——后者会把实现细节钉死。
/// </summary>
public sealed class SanitizerTests
{
    private static SanitizeResult Clean(string input, SanitizeMode mode = SanitizeMode.Interactive) => WidgetSanitizer.Sanitize(input, mode);

    private static bool Has(SanitizeResult r, string rule) => r.Findings.Any(f => f.Rule == rule);

    // ───────────────────────── 不改动 ─────────────────────────

    [Theory]
    [InlineData("""<svg viewBox="0 0 680 320" width="680"><defs><linearGradient id="g"><stop offset="0" stop-color="#f00"/></linearGradient></defs><rect width="10" height="10" fill="url(#g)"/></svg>""")]
    [InlineData("""<div class="box" style="display:flex;gap:8px;background:var(--color-bg)"><span>Q1 &amp; Q2</span><button type="button" id="b">Go</button></div>""")]
    [InlineData("""<canvas id="c"></canvas><script src="https://cdnjs.cloudflare.com/ajax/libs/Chart.js/4.4.1/chart.umd.js"></script><script>new Chart(document.getElementById('c'),{type:'bar'});if(1<2){}</script>""")]
    [InlineData("""<style>.a{color:red;background:url(#x)}</style><table><tr><td>1</td></tr></table><a href="https://example.com/x" rel="noopener">link</a>""")]
    [InlineData("""<img src="data:image/png;base64,iVBORw0KGgo=" alt="x"><input type="range" min="0" max="10" value="5"><p>a &lt; b</p>""")]
    public void A_clean_payload_is_returned_byte_for_byte(string input)
    {
        var r = Clean(input);
        Assert.Equal(input, r.Code);
        Assert.Empty(r.Findings);
    }

    // ───────────────────────── 事件处理属性 ─────────────────────────

    [Theory]
    [InlineData("""<img src=x onerror=alert(1)>""", "onerror")]
    [InlineData("""<div onclick="steal()">x</div>""", "onclick")]
    [InlineData("""<div ONCLICK="steal()">x</div>""", "onclick")]
    [InlineData("""<svg onload="alert(1)"><circle/></svg>""", "onload")]
    [InlineData("""<svg/onload=alert(1)>""", "onload")]
    [InlineData("""<a href="#" onmouseover='alert(1)'>x</a>""", "onmouseover")]
    [InlineData("""<div onpointerenter = "alert(1)">x</div>""", "onpointerenter")]
    [InlineData("""<script src="https://cdnjs.cloudflare.com/x.js" onload="init()"></script>""", "onload")]
    [InlineData("""<svg><g onclick="sendPrompt('x')" class="node"><rect/></g></svg>""", "onclick")]
    public void Event_handler_attributes_are_removed(string input, string forbidden)
    {
        var r = Clean(input);
        Assert.DoesNotContain(forbidden, r.Code, StringComparison.OrdinalIgnoreCase);
        Assert.True(Has(r, "attr.event"), string.Join(",", r.Findings));
    }

    // ───────────────────────── URL 方案(含编码绕过) ─────────────────────────

    [Theory]
    [InlineData("""<a href="javascript:alert(1)">x</a>""")]
    [InlineData("""<a href="JaVaScRiPt:alert(1)">x</a>""")]
    [InlineData("<a href=\"java\tscript:alert(1)\">x</a>")]
    [InlineData("<a href=\"java\nscript:alert(1)\">x</a>")]
    [InlineData("""<a href="  javascript:alert(1)">x</a>""")]
    [InlineData("""<a href="jav&#x61;script:alert(1)">x</a>""")]
    [InlineData("""<a href="jav&#97script:alert(1)">x</a>""")]
    [InlineData("""<a href="&#106;&#97;&#118;&#97;&#115;&#99;&#114;&#105;&#112;&#116;&#58;alert(1)">x</a>""")]
    [InlineData("""<a href="javascript&colon;alert(1)">x</a>""")]
    [InlineData("""<a href="java&Tab;script:alert(1)">x</a>""")]
    [InlineData("""<a href="vbscript:msgbox(1)">x</a>""")]
    [InlineData("""<a href="data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==">x</a>""")]
    [InlineData("""<a href="data:application/javascript,alert(1)">x</a>""")]
    [InlineData("""<a href="blob:https://x/1">x</a>""")]
    [InlineData("""<svg><a xlink:href="javascript:alert(1)"><text>x</text></a></svg>""")]
    [InlineData("""<svg><a href="javascript:alert(1)"><text>x</text></a></svg>""")]
    [InlineData("""<div data-x="javascript:alert(1)">x</div>""")]
    [InlineData("""<iframe srcdoc="<script>alert(1)</script>"></iframe>""")]
    public void Script_schemes_are_removed_however_they_are_spelled(string input)
    {
        var r = Clean(input);
        Assert.DoesNotContain("javascript", r.Code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbscript", r.Code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:text", r.Code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:application", r.Code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("blob:", r.Code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("srcdoc", r.Code, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(r.Findings);
    }

    [Fact]
    public void Plain_links_and_fragments_survive_but_external_images_do_not()
    {
        Assert.Empty(Clean("""<a href="https://example.com/a?b=1&amp;c=2">x</a><a href="#top">y</a><a href="mailto:a@b.c">z</a>""").Findings);

        var img = Clean("""<img src="https://evil.example/pixel.gif?u=1" alt="x">""");
        Assert.DoesNotContain("evil.example", img.Code);
        Assert.True(Has(img, "url.external"));

        var protocolRelative = Clean("""<img src="//evil.example/p.gif">""");
        Assert.DoesNotContain("evil.example", protocolRelative.Code);

        var srcset = Clean("""<img src="data:image/png;base64,AA==" srcset="https://evil.example/a.png 2x">""");
        Assert.DoesNotContain("evil.example", srcset.Code);
    }

    [Fact]
    public void Image_data_urls_are_kept_and_other_data_urls_are_not()
    {
        Assert.Empty(Clean("""<img src="data:image/webp;base64,AAAA">""").Findings);
        Assert.Empty(Clean("""<svg><image href="data:image/svg+xml;utf8,%3Csvg%3E%3C/svg%3E" width="1" height="1"/></svg>""").Findings);
        var bad = Clean("""<img src="data:text/html,<script>alert(1)</script>">""");
        Assert.DoesNotContain("data:text", bad.Code);
        Assert.Contains("url.scheme", bad.Findings.Select(f => f.Rule));
    }

    // ───────────────────────── 脚本与样式的外链 ─────────────────────────

    [Theory]
    [InlineData("""<script src="https://evil.example/x.js"></script>""", "evil.example")]
    [InlineData("""<script src="http://cdnjs.cloudflare.com/x.js"></script>""", "http://")]
    [InlineData("""<script src="//cdnjs.cloudflare.com/x.js"></script>""", "cloudflare")]
    [InlineData("""<script src="https://cdnjs.cloudflare.com.evil.example/x.js"></script>""", "evil.example")]
    [InlineData("""<script src="https://user:pw@cdnjs.cloudflare.com/x.js"></script>""", "user:pw")]
    [InlineData("""<script src="https://cdnjs.cloudflare.com:8443/x.js"></script>""", "8443")]
    [InlineData("""<script src="https://notcdnjs.cloudflare.com/x.js"></script>""", "notcdnjs")]
    [InlineData("""<script src="data:text/javascript,alert(1)"></script>""", "data:")]
    [InlineData("""<script src=x.js></script>""", "x.js")]
    public void External_scripts_outside_the_cdn_allowlist_are_removed(string input, string forbidden)
    {
        var r = Clean(input);
        Assert.DoesNotContain(forbidden, r.Code);
        Assert.True(Has(r, "script.external"), string.Join(",", r.Findings));
    }

    [Theory]
    [InlineData("https://cdnjs.cloudflare.com/ajax/libs/Chart.js/4.4.1/chart.umd.js")]
    [InlineData("https://cdn.jsdelivr.net/npm/d3@7")]
    [InlineData("https://unpkg.com/react@18/umd/react.production.min.js")]
    [InlineData("https://esm.sh/mermaid@11")]
    public void Scripts_from_the_four_allowed_cdns_are_kept(string url)
    {
        var input = $"<script src=\"{url}\"></script>";
        Assert.Equal(input, Clean(input).Code);
    }

    [Fact]
    public void Links_are_kept_only_for_cdn_stylesheets()
    {
        var kept = """<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/x/a.css">""";
        Assert.Equal(kept, Clean(kept).Code);

        var evil = Clean("""<link rel="stylesheet" href="https://evil.example/a.css"><link rel="preload" href="https://cdn.jsdelivr.net/a.js" as="script"><link href="x.css">""");
        Assert.DoesNotContain("<link", evil.Code);
        Assert.True(Has(evil, "link.external"));
    }

    [Theory]
    [InlineData("""<script type="text/template"><img src=x onerror=alert(1)></script>""")]
    [InlineData("""<script type="importmap">{"imports":{"a":"https://evil.example/a.js"}}</script>""")]
    [InlineData("""<script type="speculationrules">{}</script>""")]
    public void Exotic_script_types_are_removed(string input)
    {
        var r = Clean(input);
        Assert.DoesNotContain("<script", r.Code);
        Assert.True(Has(r, "script.type"));
    }

    [Fact]
    public void Inline_scripts_are_kept_for_the_interactive_rendering_but_not_for_the_snapshot()
    {
        const string input = "<div id=\"o\"></div><script>document.getElementById('o').textContent = 1 < 2 ? 'a' : 'b';</script>";
        Assert.Equal(input, Clean(input).Code);

        var snapshot = Clean(input, SanitizeMode.Static);
        Assert.DoesNotContain("<script", snapshot.Code);
        Assert.Contains("<div id=\"o\"></div>", snapshot.Code);
        Assert.True(Has(snapshot, "script.static"));
    }

    [Fact]
    public void The_nonce_attribute_is_stripped_so_a_payload_cannot_claim_the_hosts_nonce()
    {
        var r = Clean("""<script nonce="abc123">alert(1)</script><style nonce="abc123">a{color:red}</style>""");
        Assert.DoesNotContain("nonce", r.Code);
    }

    [Fact]
    public void The_snapshot_has_no_scripts_no_links_and_no_external_loads()
    {
        const string input = """
            <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/x/a.css">
            <script src="https://cdnjs.cloudflare.com/x.js"></script>
            <svg viewBox="0 0 10 10"><circle r="4"/></svg>
            <script>boom()</script>
            """;
        var snapshot = Clean(Clean(input).Code, SanitizeMode.Static);
        Assert.DoesNotContain("<script", snapshot.Code);
        Assert.DoesNotContain("<link", snapshot.Code);
        Assert.DoesNotContain("http", snapshot.Code);
        Assert.Contains("<circle r=\"4\"/>", snapshot.Code);
    }

    // ───────────────────────── 元素 ─────────────────────────

    [Theory]
    [InlineData("""<iframe src="https://evil.example"></iframe>""", "<iframe")]
    [InlineData("""<IFRAME SRC=//evil.example></IFRAME>""", "iframe")]
    [InlineData("""<iframe><img src=x onerror=alert(1)></iframe>""", "onerror")]
    [InlineData("""<object data="x.swf"><param name="a" value="b"></object>""", "<object")]
    [InlineData("""<embed src="x.swf">""", "<embed")]
    [InlineData("""<applet code="x"></applet>""", "<applet")]
    [InlineData("""<base href="https://evil.example/">""", "<base")]
    [InlineData("""<meta http-equiv="refresh" content="0;url=https://evil.example">""", "refresh")]
    [InlineData("""<noscript><p title="</noscript><img src=x onerror=alert(1)>"></noscript>""", "onerror")]
    [InlineData("""<template><img src=x onerror=alert(1)></template>""", "onerror")]
    [InlineData("""<math><mtext><img src=x onerror=alert(1)></mtext></math>""", "onerror")]
    [InlineData("""<xmp><img src=x onerror=alert(1)></xmp>""", "onerror")]
    [InlineData("""<noembed><img src=x onerror=alert(1)></noembed>""", "onerror")]
    [InlineData("""<plaintext><img src=x onerror=alert(1)>""", "onerror")]
    [InlineData("""<form action="https://evil.example/steal"><input name="a"></form>""", "evil.example")]
    [InlineData("""<button formaction="https://evil.example/steal">x</button>""", "evil.example")]
    public void Dangerous_elements_are_removed(string input, string forbidden)
    {
        var r = Clean(input);
        Assert.DoesNotContain(forbidden, r.Code, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(r.Findings);
    }

    [Fact]
    public void Unknown_tags_lose_the_tag_and_keep_the_text()
    {
        var r = Clean("<video src=\"x.mp4\">caption</video><marquee>hi</marquee>");
        Assert.Equal("captionhi", r.Code);
        Assert.Equal(2, r.Findings.Count(f => f.Rule == "element.unsupported"));
    }

    [Fact]
    public void Document_wrappers_are_dropped_but_their_content_stays()
    {
        var r = Clean("<!DOCTYPE html><html><head><title>t</title></head><body><p>hello</p></body></html>");
        Assert.Equal("<p>hello</p>", r.Code);
    }

    // ───────────────────────── SVG ─────────────────────────

    [Theory]
    [InlineData("""<svg><script>alert(1)</script></svg>""", "alert")]
    [InlineData("""<svg><foreignObject><iframe src="x"></iframe><div onclick="x()">a</div></foreignObject></svg>""", "foreignObject")]
    [InlineData("""<svg><FOREIGNOBJECT><body onload=alert(1)></FOREIGNOBJECT></svg>""", "onload")]
    [InlineData("""<svg><use href="https://evil.example/x.svg#a"/></svg>""", "evil.example")]
    [InlineData("""<svg><use xlink:href="data:image/svg+xml;base64,AAAA#a"/></svg>""", "base64")]
    [InlineData("""<svg><animate attributeName="href" values="javascript:alert(1)"/></svg>""", "javascript")]
    [InlineData("""<svg><set attributeName="onmouseover" to="alert(1)"/></svg>""", "onmouseover")]
    [InlineData("""<svg><a xlink:href="javascript:alert(1)"><circle r="1"/></a></svg>""", "javascript")]
    [InlineData("""<svg><image href="https://evil.example/p.png"/></svg>""", "evil.example")]
    [InlineData("""<svg><feImage href="https://evil.example/p.png"/></svg>""", "evil.example")]
    [InlineData("""<svg><style>@import url(https://evil.example/a.css);</style></svg>""", "evil.example")]
    public void Svg_attack_surface_is_removed(string input, string forbidden)
    {
        var r = Clean(input);
        Assert.DoesNotContain(forbidden, r.Code, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(r.Findings);
    }

    [Fact]
    public void Svg_fragment_references_animation_and_gradients_survive()
    {
        const string input = """<svg viewBox="0 0 10 10"><defs><path id="p" d="M0 0h10"/></defs><use href="#p" stroke="url(#g)"/><circle r="2"><animateTransform attributeName="transform" type="rotate" from="0" to="360" dur="2s" repeatCount="indefinite"/></circle><text><textPath href="#p">hi</textPath></text></svg>""";
        Assert.Equal(input, Clean(input).Code);
    }

    [Fact]
    public void Svg_style_content_cannot_smuggle_markup_in_either_parsing_mode()
    {
        // 在 SVG 里 <style> 的内容按普通标记解析,在 HTML 里按原始文本解析:块内的 '<' 被转义成 CSS 的 \3c,两种解释下都没有标签。
        var r = Clean("<svg><style><img src=x onerror=alert(1)></style></svg>");
        Assert.DoesNotContain("<img", r.Code);
        Assert.Contains("\\3c img", r.Code);
    }

    [Fact]
    public void Html_breakout_tags_inside_svg_return_to_html_context()
    {
        // <p> 会把解析器带回 HTML,此后的 <style> 是 RAWTEXT:里面的东西不能当标签。
        var r = Clean("<svg><p><style><img src=x onerror=alert(1)></style>");
        Assert.DoesNotContain("<img", r.Code);
    }

    [Fact]
    public void The_root_svg_size_is_read_for_layout_hints()
    {
        Assert.Equal(new SizeHint(680, 320), Clean("""<svg viewBox="0 0 680 320"></svg>""").Size);
        Assert.Equal(new SizeHint(100, 50), Clean("""<svg width="100px" height="50" viewBox="0 0 1 1"></svg>""").Size);
        Assert.Null(Clean("<div></div>").Size);
    }

    // ───────────────────────── CSS ─────────────────────────

    [Theory]
    [InlineData("""<div style="background:url(http://evil.example/a.png)">x</div>""", "evil.example")]
    [InlineData("""<div style="background:url('https://evil.example/a.png')">x</div>""", "evil.example")]
    [InlineData("""<div style="width:expression(alert(1))">x</div>""", "expression")]
    [InlineData("""<div style="behavior:url(x.htc)">x</div>""", "behavior")]
    [InlineData("""<div style="-moz-binding:url(x)">x</div>""", "moz-binding")]
    [InlineData("""<div style="background:\75rl(http://evil.example/a)">x</div>""", "evil.example")]
    [InlineData("""<div style="background:u/**/rl(http://evil.example/a)">x</div>""", "evil.example")]
    [InlineData("""<div style="background:&#x75;rl(http://evil.example/a)">x</div>""", "evil.example")]
    [InlineData("""<div style="background:image-set('https://evil.example/a.png' 1x)">x</div>""", "evil.example")]
    [InlineData("""<div style="background:url(javascript:alert(1))">x</div>""", "javascript")]
    [InlineData("""<style>@import url(https://evil.example/a.css);</style>""", "evil.example")]
    [InlineData("""<style>@IMPORT "https://evil.example/a.css";</style>""", "evil.example")]
    [InlineData("""<style>@\69mport 'https://evil.example/a.css';</style>""", "evil.example")]
    [InlineData("""<style>a{background:url(https://evil.example/a.png)}</style>""", "evil.example")]
    [InlineData("""<style>@font-face{font-family:x;src:url(https://evil.example/f.woff)}</style>""", "evil.example")]
    public void Unsafe_css_is_removed(string input, string forbidden)
    {
        var r = Clean(input);
        Assert.DoesNotContain(forbidden, r.Code, StringComparison.OrdinalIgnoreCase);
        Assert.True(Has(r, "css.unsafe"), string.Join(",", r.Findings));
    }

    [Fact]
    public void Css_that_only_references_fragments_and_image_data_is_kept()
    {
        const string input = """<div style="fill:url(#g);background:url(data:image/png;base64,AAAA) no-repeat"></div><style>.x{mask:url("#m");filter:url(#f)}</style>""";
        Assert.Equal(input, Clean(input).Code);
    }

    [Fact]
    public void A_less_than_sign_inside_css_is_escaped_so_the_block_cannot_close_early()
    {
        var r = Clean("<style>a[title=\"</style><img src=x onerror=alert(1)>\"]{color:red}</style>");
        Assert.DoesNotContain("onerror", r.Code);
    }

    // ───────────────────────── 分词:嵌套、大小写、注释 ─────────────────────────

    [Theory]
    [InlineData("<scr<script>ipt>alert(1)</scr</script>ipt>")]
    [InlineData("<ScRiPt src=//evil.example/x.js></ScRiPt>")]
    [InlineData("<script/src=//evil.example/x.js></script>")]
    [InlineData("<script\t\n src=\"//evil.example/x.js\"></script >")]
    [InlineData("<<!-- -->script>alert(1)<<!-- -->/script>")]
    [InlineData("<!-- <script src=https://evil.example/x.js></script> -->")]
    [InlineData("<!--><script src=https://evil.example/x.js></script>")]
    [InlineData("<![CDATA[<script>alert(1)</script>]]>")]
    [InlineData("<?xml version=\"1.0\"?><script src=https://evil.example/x.js></script>")]
    [InlineData("<scr\0ipt src=https://evil.example/x.js></scr\0ipt>")]
    public void Tokenizer_tricks_do_not_produce_a_script(string input)
    {
        var r = Clean(input);
        Assert.DoesNotContain("evil.example", r.Code);
        Assert.DoesNotContain("<script", r.Code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<<", r.Code);
    }

    [Fact]
    public void A_script_in_odd_case_is_still_just_an_inline_script()
    {
        Assert.Equal("<SCRIPT>alert(1)</script>", Clean("<SCRIPT>alert(1)</SCRIPT>").Code);
        Assert.Equal("&lt;<script>script>alert(1)<</script>/script>", Clean("<<script>script>alert(1)<</script>/script>").Code);
    }

    [Fact]
    public void Event_handlers_on_dropped_document_wrappers_go_with_the_tag()
    {
        Assert.DoesNotContain("onload", Clean("<body onload=alert(1)>x").Code);
    }

    [Fact]
    public void Removing_a_comment_does_not_glue_text_into_a_new_tag()
    {
        var r = Clean("<<!-- x -->img src=x onerror=alert(1)>");
        Assert.DoesNotContain("<img", r.Code);
        Assert.Contains("&lt;", r.Code);
    }

    [Fact]
    public void An_unterminated_tag_or_quote_swallows_nothing_dangerous()
    {
        Assert.Equal("ok", Clean("ok<a href=\"https://x").Code);
        Assert.Equal("ok", Clean("ok<img src=x onerror=alert(1)").Code);
    }

    [Fact]
    public void A_lone_less_than_sign_in_text_is_escaped()
    {
        Assert.Equal("1 &lt; 2 > 0 &lt;", Clean("1 < 2 > 0 <").Code);
    }

    [Fact]
    public void Unterminated_raw_text_elements_are_closed()
    {
        Assert.EndsWith("</script>", Clean("<script>var a = 1;").Code);
        Assert.EndsWith("</style>", Clean("<style>a{color:red}").Code);
        Assert.EndsWith("</textarea>", Clean("<textarea>hi").Code);
    }

    [Fact]
    public void Textarea_content_is_inert_text_and_stays_verbatim()
    {
        const string input = "<textarea id=\"t\"><img src=x onerror=alert(1)></textarea>";
        Assert.Equal(input, Clean(input).Code);
    }

    // ───────────────────────── 属性的其它形状 ─────────────────────────

    [Fact]
    public void Attribute_names_and_duplicates_are_handled()
    {
        var r = Clean("<div class=\"a\" class=\"b\" =x \"bad\"=1 data-ok=\"1\">x</div>");
        Assert.DoesNotContain("class=\"b\"", r.Code);
        Assert.Contains("data-ok=\"1\"", r.Code);
        Assert.DoesNotContain("\"bad\"", r.Code);
    }

    [Fact]
    public void Rewritten_tags_escape_quotes_in_kept_values()
    {
        var r = Clean("<div title='say \"hi\"' onclick=x>y</div>");
        Assert.Equal("<div title=\"say &quot;hi&quot;\">y</div>", r.Code);
    }

    [Fact]
    public void Unknown_namespaces_are_dropped_but_the_standard_ones_stay()
    {
        var kept = "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"></svg>";
        Assert.Equal(kept, Clean(kept).Code);
        Assert.DoesNotContain("evil", Clean("<svg xmlns=\"http://evil.example/ns\"></svg>").Code);
    }

    [Fact]
    public void Deep_nesting_is_capped()
    {
        var r = Clean(string.Concat(Enumerable.Repeat("<div>", 600)) + "x");
        Assert.True(Has(r, "element.depth"));
    }

    [Fact]
    public void Pathological_inputs_finish_quickly()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Clean(string.Concat(Enumerable.Repeat("</a>", 30_000)));
        Clean(string.Concat(Enumerable.Repeat("<a ", 30_000)));
        Clean(string.Concat(Enumerable.Repeat("<!--", 30_000)));
        Clean(string.Concat(Enumerable.Repeat("<div>", 30_000)) + string.Concat(Enumerable.Repeat("</p>", 30_000)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), sw.Elapsed.ToString());
    }

    [Fact]
    public void Sanitizing_twice_changes_nothing_more()
    {
        var once = Clean("<div onclick=x><script src=//evil.example></script><a href=javascript:1>a</a><svg><foreignObject>q</foreignObject></svg>b</div>");
        var twice = Clean(once.Code);
        Assert.Equal(once.Code, twice.Code);
        Assert.Empty(twice.Findings);
    }

    [Fact]
    public void Findings_are_aggregated_with_counts()
    {
        var r = Clean("<a onclick=x>1</a><a onclick=y>2</a><b onclick=z>3</b>");
        var f = Assert.Single(r.Findings);
        Assert.Equal(("attr.event", "onclick", 3), (f.Rule, f.Detail, f.Count));
    }
}
