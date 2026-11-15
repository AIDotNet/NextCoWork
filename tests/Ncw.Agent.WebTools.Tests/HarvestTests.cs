using System.Text.Json;
using Ncw.Agent.WebTools.Search;

namespace Ncw.Agent.WebTools.Tests;

/// <summary>字段挖掘(移植自 2.x <c>search/__tests__/harvest.test.ts</c> 的全部用例)。</summary>
public sealed class HarvestTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Pick_takes_the_first_non_empty_string_in_candidate_order()
    {
        Assert.Equal("b", Harvest.Pick(J("""{"x":"b","y":"c"}"""), ["a", "x", "y"]));
        Assert.Equal("c", Harvest.Pick(J("""{"a":"","x":"  ","y":"c"}"""), ["a", "x", "y"])); // 空串和只有空白的串都当没有
        Assert.Equal("trim me", Harvest.Pick(J("""{"a":"  trim me  "}"""), ["a"])); // 顺手 trim
        Assert.Null(Harvest.Pick(J("""{"a":1,"b":null,"c":["x"]}"""), ["a", "b", "c"])); // 非字符串不算数
        Assert.Null(Harvest.Pick(J("[1,2]"), ["a"])); // 不是对象就直接没有
        Assert.Null(Harvest.Pick(J("\"s\""), ["a"]));
    }

    [Fact]
    public void ArrayAt_descends_by_path_and_gives_empty_when_the_path_breaks_or_is_not_an_array()
    {
        Assert.Equal(2, Harvest.ArrayAt(J("""{"web":{"results":[1,2]}}"""), "web", "results").Count);
        Assert.Equal(2, Harvest.ArrayAt(J("[1,2]")).Count); // 空路径就是根本身
        Assert.Empty(Harvest.ArrayAt(J("""{"web":{}}"""), "web", "results"));
        Assert.Empty(Harvest.ArrayAt(J("""{"web":[1]}"""), "web", "results"));
        Assert.Empty(Harvest.ArrayAt(J("""{"web":{"results":"nope"}}"""), "web", "results"));
    }

    [Fact]
    public void FindResultArray_finds_the_array_that_looks_like_results_within_two_levels()
    {
        Assert.Single(Harvest.FindResultArray(J("""[{"url":"https://a"}]""")));
        Assert.Single(Harvest.FindResultArray(J("""{"data":[{"link":"https://a"}]}""")));
        Assert.Single(Harvest.FindResultArray(J("""{"data":{"items":[{"href":"https://a"}]}}""")));
        Assert.Empty(Harvest.FindResultArray(J("""{"a":{"b":{"c":[{"url":"https://a"}]}}}"""))); // 第三层不再往下找
        Assert.Empty(Harvest.FindResultArray(J("""{"data":[{"title":"no url"}]}"""))); // 没有像 url 的字段就不算结果列表
        Assert.Single(Harvest.FindResultArray(J("""{"related":[{"title":"x"}],"results":[{"url":"https://a"}]}"""))); // 略过前面不像的
        Assert.Empty(Harvest.FindResultArray(J("""{"a":1,"b":"x"}""")));
    }

    [Fact]
    public void One_drops_items_without_a_url_falls_back_to_url_as_title_and_empty_snippet()
    {
        Assert.Null(Harvest.One(J("""{"title":"t","snippet":"s"}""")));
        Assert.Equal("https://a", Harvest.One(J("""{"url":"https://a"}"""))!.Title);
        Assert.Equal("", Harvest.One(J("""{"url":"https://a"}"""))!.Snippet);
    }

    [Fact]
    public void One_understands_every_providers_field_names_and_digs_out_a_date()
    {
        Assert.Equal(new Harvest.Item("t", "https://a", "c", null), Harvest.One(J("""{"title":"t","url":"https://a","content":"c"}"""))); // Tavily
        Assert.Equal(new Harvest.Item("t", "https://a", "d", "3 days ago"), Harvest.One(J("""{"title":"t","url":"https://a","description":"d","page_age":"3 days ago"}"""))); // Brave
        Assert.Equal(new Harvest.Item("t", "https://a", "s", "2026-01-01"), Harvest.One(J("""{"title":"t","link":"https://a","snippet":"s","date":"2026-01-01"}"""))); // Serper / SerpApi
        Assert.Equal(new Harvest.Item("t", "https://a", "x", "2025-05-05"), Harvest.One(J("""{"title":"t","url":"https://a","text":"x","publishedDate":"2025-05-05"}"""))); // Exa
    }

    [Fact]
    public void All_drops_the_unusable_deduplicates_by_trimmed_url_keeps_order_and_never_throws()
    {
        var rows = J("""[{"title":"1","url":"https://a"},{"title":"junk"},{"title":"2","url":"https://b"},{"title":"dup","url":"  https://a  "},{"title":"3","url":"https://c"}]""").EnumerateArray();
        Assert.Equal(["https://a", "https://b", "https://c"], Harvest.All(rows).Select(static i => i.Url));
        Assert.Empty(Harvest.All(J("""[1,"x",null,{"a":1}]""").EnumerateArray()));
    }
}
