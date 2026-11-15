using System.Diagnostics;
using Ncw.Agent.WebTools.Fetch;

namespace Ncw.Agent.WebTools.Tests;

/// <summary>HTML → 文本(移植自 2.x <c>web.test.ts</c> 的正文用例,并补 3.0 的线性时间保证、<c>a &lt; b</c>、实体边界、控制字符清洗)。</summary>
public sealed class HtmlTextTests
{
    [Fact]
    public void Tags_are_dropped_and_block_elements_keep_their_line_breaks()
    {
        var text = HtmlText.ToText("""
            <html><head><title>T</title><style>p { color: red }</style><script>var x = "<b>nope</b>";</script></head>
            <body><h1>Title</h1><p>First   paragraph</p><p>Second<br>line</p><ul><li>one</li><li>two</li></ul></body></html>
            """);
        Assert.DoesNotContain("color: red", text);
        Assert.DoesNotContain("nope", text);
        Assert.Contains("Title", text);
        Assert.Contains("First paragraph", text); // 行内空白压成一个
        Assert.Contains("Second\nline", text);
        Assert.Contains("- one\n- two", text);
        Assert.False(text.Contains("<", StringComparison.Ordinal));
    }

    [Fact]
    public void Script_style_noscript_svg_and_template_content_is_removed_case_insensitively()
    {
        var text = HtmlText.ToText("a<SCRIPT type='x'>alert(1)</SCRIPT>b<Style>.x{}</STYLE>c<noscript>enable js</noscript>d<svg><path d='M0'/></svg>e<template><p>hidden</p></template>f");
        Assert.Equal("a b c d e f", text);
    }

    [Fact]
    public void Comments_are_removed_including_an_unterminated_one()
    {
        Assert.Equal("a b", HtmlText.ToText("a<!-- note <b>x</b> -->b"));
        Assert.Equal("a", HtmlText.ToText("a<!-- never closed <p>secret</p>"));
    }

    [Fact]
    public void Entities_are_decoded_named_decimal_and_hex_and_unknown_ones_stay_as_written()
    {
        Assert.Equal("a & b < c > d \"e\" 'f' g", HtmlText.ToText("a &amp; b &lt; c &gt; d &quot;e&quot; &apos;f&apos; g"));
        Assert.Equal("it's", HtmlText.ToText("it&#39;s"));
        Assert.Equal("Aé", HtmlText.ToText("&#65;&#xE9;"));
        Assert.Equal("© — …", HtmlText.ToText("&copy; &mdash; &hellip;"));
        Assert.Equal("&bogus; &#0; &#xD800; &#1114112;", HtmlText.ToText("&bogus; &#0; &#xD800; &#1114112;")); // 不认识 / 非法码点原样保留,不抛
        Assert.Equal("AT&T", HtmlText.ToText("AT&T")); // 没有分号
        Assert.Equal("&amp;", HtmlText.ToText("&amp;amp;")); // 只解一层
    }

    [Fact]
    public void A_less_than_that_is_not_a_tag_stays_as_text()
    {
        Assert.Equal("if a < b and c > d then", HtmlText.ToText("if a < b and c > d then"));
        Assert.Equal("1<2", HtmlText.ToText("1<2"));
        Assert.Equal("x <", HtmlText.ToText("x <"));
        Assert.Equal("x < ", HtmlText.ToText("x < ").TrimEnd() + " "); // 末尾空白被 trim 是正常的
    }

    [Fact]
    public void An_unclosed_tag_at_the_end_is_kept_literally()
    {
        Assert.Equal("a <b", HtmlText.ToText("a <b"));
    }

    [Fact]
    public void Blank_runs_collapse_to_a_single_blank_line_and_lines_are_trimmed()
    {
        Assert.Equal("a\n\nb", HtmlText.ToText("<p>a</p><p></p><p></p><p></p><p>b</p>"));
        Assert.Equal("a\nb", HtmlText.ToText("  a  \n   b   "));
    }

    [Fact]
    public void A_json_body_is_not_html_so_angle_brackets_in_it_are_untouched_by_the_tool_but_ToText_on_html_only()
    {
        // 工具只对 HTML 调 ToText;这里证明 LooksLikeHtml 的判据
        Assert.True(HtmlText.LooksLikeHtml("  <!DOCTYPE html><html></html>"));
        Assert.True(HtmlText.LooksLikeHtml("<HTML lang=en>"));
        Assert.False(HtmlText.LooksLikeHtml("""{"a":"<b>x</b>"}"""));
        Assert.False(HtmlText.LooksLikeHtml("plain text"));
    }

    [Fact]
    public void Hundreds_of_thousands_of_unclosed_script_tags_are_linear_not_quadratic()
    {
        // 2.x 的 /<(script)\b[^>]*>[\s\S]*?<\/\1>/ 在这种输入上是平方级:每个没闭合的 <script> 都要扫到文末
        var evil = string.Concat(Enumerable.Repeat("<script>", 200_000)) + "tail";
        var clock = Stopwatch.StartNew();
        var text = HtmlText.ToText(evil);
        Assert.True(clock.ElapsedMilliseconds < 3000, $"took {clock.ElapsedMilliseconds} ms");
        Assert.Contains("tail", text);
    }

    [Fact]
    public void Hundreds_of_thousands_of_lone_less_thans_and_unclosed_tags_are_linear()
    {
        var clock = Stopwatch.StartNew();
        HtmlText.ToText(string.Concat(Enumerable.Repeat("<a ", 300_000)));
        HtmlText.ToText(string.Concat(Enumerable.Repeat("<!-- ", 300_000)));
        HtmlText.ToText(string.Concat(Enumerable.Repeat("<style>x", 300_000)));
        Assert.True(clock.ElapsedMilliseconds < 5000, $"took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void A_deeply_nested_page_does_not_overflow_the_stack()
    {
        var html = string.Concat(Enumerable.Repeat("<div>", 100_000)) + "core" + string.Concat(Enumerable.Repeat("</div>", 100_000));
        Assert.Equal("core", HtmlText.ToText(html));
    }

    [Fact]
    public void FragmentToText_makes_one_line_without_highlight_tags_or_nbsp()
    {
        Assert.Equal("the best result here", HtmlText.FragmentToText("the <b>best</b>&nbsp;result<br>here"));
    }

    // ───────────────────────── content-type 白名单 ─────────────────────────

    [Theory]
    [InlineData("text/html")]
    [InlineData("text/html; charset=utf-8")]
    [InlineData("TEXT/PLAIN")]
    [InlineData("text/markdown")]
    [InlineData("text/csv")]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/xml")]
    [InlineData("application/ld+json")]
    [InlineData("application/vnd.api+json")]
    [InlineData("application/atom+xml")]
    [InlineData("application/rss+xml")]
    [InlineData("application/javascript")]
    [InlineData("application/yaml")]
    [InlineData("image/svg+xml")]
    public void Textual_types_are_accepted(string type) => Assert.True(HtmlText.IsTextual(type), type);

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("application/zip")]
    [InlineData("application/octet-stream")]
    [InlineData("image/png")]
    [InlineData("video/mp4")]
    [InlineData("audio/mpeg")]
    [InlineData("font/woff2")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("garbage")]
    public void Everything_else_is_refused_a_whitelist_not_a_blacklist(string? type) => Assert.False(HtmlText.IsTextual(type), type);

    // ───────────────────────── 控制字符 ─────────────────────────

    [Fact]
    public void Control_characters_zero_width_and_bidi_overrides_are_stripped_from_untrusted_text()
    {
        var dirty = "ok\u0000\u0007\u001b[31m​hidden‮evil⁦x⁩﻿\u007f\u0085 tail\tkeep\nnewline";
        var clean = HtmlText.StripControlChars(dirty);
        Assert.Equal("ok[31mhiddenevilx tail\tkeep\nnewline", clean);
    }

    [Fact]
    public void Lone_surrogates_become_the_replacement_character_and_pairs_survive()
    {
        Assert.Equal("a�b", HtmlText.StripControlChars("a\ud800b"));
        Assert.Equal("😀", HtmlText.StripControlChars("😀"));
    }
}
