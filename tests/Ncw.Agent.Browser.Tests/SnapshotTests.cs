using System.Text.Json;
using Ncw.Agent.Browser.Pages;

namespace Ncw.Agent.Browser.Tests;

/// <summary>移植自 2.x <c>aria-snapshot.test.ts</c>:页面返回的值是不可信输入。</summary>
public sealed class SnapshotTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Trailing_whitespace_is_normalised_and_page_ref_metadata_is_kept()
    {
        var snapshot = PageSnapshot.Normalize(Json("""
            {"tree":"- heading \"Example\" [ref=e1]  \n  - button \"Go\" [ref=e2]\t\n\n","title":"Example","url":"https://example.com/","snapshotId":"s1","viewport":{"width":1280.4,"height":720}}
            """));

        Assert.Equal("- heading \"Example\" [ref=e1]\n  - button \"Go\" [ref=e2]", snapshot.Tree);
        Assert.Equal(new Viewport(1280, 720), snapshot.Viewport);
        Assert.False(snapshot.Truncated);
        Assert.Equal("Page: Example\nURL: https://example.com/\nViewport: 1280x720\n\n" + snapshot.Tree, snapshot.Format());
    }

    [Fact]
    public void A_hostile_page_cannot_exceed_the_fixed_budget()
    {
        var huge = new string('x', PageSnapshot.MaxChars + 5000);
        var snapshot = PageSnapshot.Normalize(Json($"{{\"tree\":\"{huge}\",\"title\":\"{new string('t', 900)}\",\"url\":\"https://e.com/\",\"snapshotId\":\"s\",\"viewport\":{{\"width\":1,\"height\":1}}}}"));

        Assert.True(snapshot.Truncated);
        Assert.Equal(PageSnapshot.MaxChars, snapshot.Tree.Length);
        Assert.Equal(500, snapshot.Title.Length);
        Assert.EndsWith($"[Snapshot truncated at {PageSnapshot.MaxChars} characters.]", snapshot.Format());
    }

    [Theory]
    [InlineData("""{"title":"t","url":"u","snapshotId":"s","viewport":{"width":1,"height":1}}""")]
    [InlineData("""{"tree":"x","title":"t","url":"u","snapshotId":"","viewport":{"width":1,"height":1}}""")]
    [InlineData("""{"tree":"x","title":"t","url":"u","snapshotId":"s","viewport":{"width":-1,"height":1}}""")]
    [InlineData("""{"tree":"x","title":"t","url":"u","snapshotId":"s","viewport":{"width":"1","height":1}}""")]
    [InlineData("""{"tree":"x","title":"t","url":"u","snapshotId":"s"}""")]
    [InlineData("[]")]
    [InlineData("\"tree\"")]
    public void Missing_fields_and_invalid_viewports_are_rejected(string raw) =>
        Assert.Throws<BrowserException>(() => PageSnapshot.Normalize(Json(raw)));

    [Fact]
    public void An_empty_page_says_so()
    {
        var snapshot = PageSnapshot.Normalize(Json("""{"tree":"","title":"","url":"about:blank","snapshotId":"s","viewport":{"width":10,"height":10}}"""));
        Assert.Contains("(The page has no accessible content.)", snapshot.Format());
        Assert.StartsWith("Page: (untitled)", snapshot.Format());
    }

    [Fact]
    public void The_snapshot_expression_embeds_the_id_as_an_escaped_json_literal()
    {
        var separator = ((char)0x2028).ToString();
        var expression = SnapshotScript.Expression("a\"b\\c\n</script>" + separator);
        Assert.EndsWith("(\"a\\\"b\\\\c\\n</script>\\u2028\")", expression);
        Assert.Contains("globalThis.__ncwBrowserState", expression);
    }

    [Fact]
    public void The_png_size_reader_tolerates_garbage()
    {
        Assert.Equal((1280, 720), PageScreenshot.PngSize(FakeCdpServer.Png(1280, 720)));
        Assert.Equal((0, 0), PageScreenshot.PngSize([1, 2, 3]));
        Assert.Equal((0, 0), PageScreenshot.PngSize(new byte[64]));
    }
}
