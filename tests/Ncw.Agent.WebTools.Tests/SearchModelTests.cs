using Ncw.Agent.WebTools.Search;

namespace Ncw.Agent.WebTools.Tests;

/// <summary>搜索提供方目录与纯函数(2.x <c>shared/domain/search.ts</c> 的 <c>reorderProviders</c> / <c>searchChain</c> / <c>defaultProviderConfigs</c>、库里「只存动过的那几家」的左连接)。</summary>
public sealed class SearchModelTests
{
    private static SearchProviderStatus S(string id, bool enabled, int priority, bool hasKey) => new(new SearchProviderConfig(id, enabled, priority), hasKey);

    [Fact]
    public void The_catalog_has_the_eight_providers_in_default_order_and_two_are_unavailable_with_a_stable_reason_key()
    {
        Assert.Equal(["tavily", "exa", "brave", "serper", "serpapi", "metaso", "doubao", "bing"], SearchProviders.Catalog.Select(static m => m.Id));
        Assert.Equal(["doubao", "bing"], SearchProviders.Catalog.Where(static m => m.UnavailableKey is not null).Select(static m => m.Id));
        Assert.StartsWith("websearch.unavailable.", SearchProviders.Find("bing")!.UnavailableKey);
        Assert.False(SearchProviders.IsUsable("bing"));
        Assert.True(SearchProviders.IsUsable("tavily"));
        Assert.False(SearchProviders.IsKnown("nope"));
        // 有适配器的恰好是「还能用」的那六家
        Assert.Equal(SearchProviders.Catalog.Where(static m => m.UnavailableKey is null).Select(static m => m.Id).Order(), SearchAdapters.Table.Keys.Order());
    }

    [Fact]
    public void The_secret_ref_is_a_literal_prefix_that_matches_the_2x_physical_ref() => Assert.Equal("websearch:tavily", SearchProviders.SecretRef("tavily"));

    [Fact]
    public void Last4_is_withheld_for_short_keys()
    {
        Assert.Equal("5678", SearchProviders.Last4("tvly-12345678"));
        Assert.Null(SearchProviders.Last4("short"));
        Assert.Null(SearchProviders.Last4("1234567")); // 7 位:显示末四位等于泄露了过半
        Assert.Equal("4567", SearchProviders.Last4("01234567"));
        Assert.Null(SearchProviders.Last4(null));
    }

    [Fact]
    public void Defaults_are_all_disabled_in_catalog_order()
    {
        var defaults = SearchProviders.Defaults();
        Assert.All(defaults, static c => Assert.False(c.Enabled));
        Assert.Equal(Enumerable.Range(0, 8), defaults.Select(static c => c.Priority));
    }

    [Fact]
    public void Merge_joins_the_stored_rows_onto_the_full_catalog_sorted_by_priority_with_catalog_order_as_the_tiebreak()
    {
        // 库里只存「用户动过的那几家」:一家都没配过时表是空的
        Assert.Equal(SearchProviders.Defaults(), SearchProviders.Merge(Array.Empty<SearchProviderConfig>()));

        // 只写了其中两家:没存过的仍带着目录序号当 priority,和新编号撞得上——撞了用目录顺序兜底(确定)
        var merged = SearchProviders.Merge([new SearchProviderConfig("brave", true, 0), new SearchProviderConfig("exa", false, 1)]);
        Assert.Equal(8, merged.Count);
        Assert.Equal("tavily", merged[0].Id); // priority 0 撞上 brave 的 0:目录里 tavily 在前
        Assert.Equal("brave", merged[1].Id);
        Assert.True(merged.Single(static c => c.Id == "brave").Enabled);
        Assert.Equal(merged.Select(static c => c.Id).Distinct().Count(), merged.Count);
    }

    [Fact]
    public void Merge_ignores_unknown_ids_and_takes_the_last_row_of_a_duplicate() =>
        Assert.True(SearchProviders.Merge([new SearchProviderConfig("ghost", true, 0), new SearchProviderConfig("exa", false, 5), new SearchProviderConfig("exa", true, 6)]).Single(static c => c.Id == "exa").Enabled);

    [Fact]
    public void ApplyOrder_puts_the_given_ids_first_then_the_rest_in_their_old_relative_order_and_renumbers_without_gaps()
    {
        var current = SearchProviders.Defaults();
        var result = SearchProviders.ApplyOrder(current, ["metaso", "exa"]);
        Assert.Equal(["metaso", "exa", "tavily", "brave", "serper", "serpapi", "doubao", "bing"], result.Select(static c => c.Id));
        Assert.Equal(Enumerable.Range(0, 8), result.Select(static c => c.Priority)); // 连续的 0..n-1
    }

    [Fact]
    public void ApplyOrder_ignores_unknown_and_repeated_ids_and_keeps_the_enabled_flags_it_is_a_sort_not_a_membership_change()
    {
        var current = SearchProviders.Merge([new SearchProviderConfig("tavily", true, 0)]);
        var result = SearchProviders.ApplyOrder(current, ["ghost", "brave", "brave", "tavily"]);
        Assert.Equal(8, result.Count);
        Assert.Equal(["brave", "tavily"], result.Take(2).Select(static c => c.Id));
        Assert.True(result.Single(static c => c.Id == "tavily").Enabled);
    }

    [Fact]
    public void Chain_keeps_only_enabled_usable_providers_that_have_a_key_ordered_by_priority()
    {
        var chain = SearchProviders.Chain([
            S("brave", true, 2, true),
            S("tavily", true, 0, false), // 开着但没 Key:不进链(否则每次搜索白跑一趟 HTTP)
            S("exa", false, 1, true), // 关着
            S("bing", true, 3, true), // 用不了
            S("serper", true, 1, true),
            S("serpapi", true, 5, true),
        ]);
        Assert.Equal(["serper", "brave", "serpapi"], chain.Select(static s => s.Config.Id));
    }
}
