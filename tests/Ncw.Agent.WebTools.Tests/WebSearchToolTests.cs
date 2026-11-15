using System.Net;
using System.Text.Json;
using Ncw.Agent.WebTools.Search;

namespace Ncw.Agent.WebTools.Tests;

/// <summary><c>web_search</c> 工具(2.x <c>web-search.ts</c> 的语义:结果列表不是答案、三种失败分开说、成功时附中途失败、不碰密钥)。</summary>
public sealed class WebSearchToolTests
{
    private readonly ToolHarness _h = new();

    private sealed class Config(params string[] ids) : ISearchConfigSource
    {
        public ValueTask<IReadOnlyList<SearchProviderStatus>> StatusesAsync(CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<SearchProviderStatus>>([.. ids.Select((id, i) => new SearchProviderStatus(new SearchProviderConfig(id, true, i), true))]);

        public ValueTask<string?> ApiKeyAsync(string id, CancellationToken ct) => ValueTask.FromResult<string?>($"secret-{id}-0000");
    }

    private static WebSearchTool Tool(FakeHttp http, params string[] ids) => new(new SearchService(new Config(ids), http));

    private static FakeHttp Tavily(params (string Title, string Url, string Snippet)[] rows) =>
        new(_ => FakeHttp.Ok(new System.Text.Json.Nodes.JsonObject { ["results"] = new System.Text.Json.Nodes.JsonArray([.. rows.Select(r => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["title"] = r.Title, ["url"] = r.Url, ["content"] = r.Snippet, ["published_date"] = "2026-05-01" })]) }.ToJsonString(), "application/json"));

    [Fact]
    public void Traits_declare_network_and_read_only_and_the_name_is_cc_compatible()
    {
        var tool = Tool(Tavily());
        Assert.Equal("web_search", tool.Info.InternalId);
        Assert.True(tool.Info.Traits.NeedsNetwork);
        Assert.True(tool.Info.Traits.ReadOnly);
        Assert.False(tool.Info.Traits.Destructive);
        Assert.Contains("A LIST OF RESULTS", tool.Info.Description);
        Assert.False(string.IsNullOrWhiteSpace(tool.Info.PromptSnippet));
        Assert.NotEmpty(tool.Info.PromptGuidelines!);
    }

    [Fact]
    public async Task Results_are_numbered_with_title_url_date_and_snippet_and_say_where_they_came_from()
    {
        var result = await _h.Run(Tool(Tavily(("First", "https://a.example", "alpha"), ("Second", "https://b.example", "")), "tavily"), """{"query":"hello"}""");
        Assert.False(result.IsError);
        var text = ToolHarness.TextOf(result);
        Assert.StartsWith("Search results for \"hello\" (via Tavily):", text);
        Assert.Contains("1. First\n   https://a.example · 2026-05-01\n   alpha", text);
        Assert.Contains("2. Second\n   https://b.example", text);
        Assert.DoesNotContain("   \n", text); // 没有摘要就不留空行
    }

    [Fact]
    public async Task The_count_defaults_to_eight_and_is_passed_through_up_to_twenty()
    {
        var http = Tavily(("x", "https://x.example", "x"));
        await _h.Run(Tool(http, "tavily"), """{"query":"q"}""");
        await _h.Run(Tool(http, "tavily"), """{"query":"q","count":20}""");
        Assert.Contains("\"max_results\":8", http.Requests[0].Body);
        Assert.Contains("\"max_results\":20", http.Requests[1].Body);
    }

    [Theory]
    [InlineData("""{"query":"q","count":0}""")]
    [InlineData("""{"query":"q","count":21}""")]
    [InlineData("""{"query":""}""")]
    [InlineData("""{}""")]
    public async Task Bad_arguments_are_stopped_by_the_schema(string json)
    {
        var http = Tavily();
        var result = await _h.Run(Tool(http, "tavily"), json);
        Assert.True(result.IsError);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task A_long_snippet_is_cut_to_five_hundred_characters()
    {
        var text = ToolHarness.TextOf(await _h.Run(Tool(Tavily(("T", "https://a.example", new string('y', 3000))), "tavily"), """{"query":"q"}"""));
        Assert.Contains(new string('y', 500), text);
        Assert.DoesNotContain(new string('y', 501), text);
    }

    [Fact]
    public async Task Nothing_configured_tells_the_model_to_stop_and_ask_the_user_not_to_change_the_query()
    {
        var http = Tavily();
        var result = await _h.Run(Tool(http), """{"query":"q"}""");
        Assert.True(result.IsError);
        var text = ToolHarness.TextOf(result);
        Assert.Contains("No search provider is configured", text);
        Assert.Contains("Settings", text);
        Assert.Contains("Changing the query will not help", text);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task All_configured_providers_failing_hands_the_model_every_providers_own_words()
    {
        var http = new FakeHttp(_ => FakeHttp.Ok("""{"error":"key expired"}""", "application/json", HttpStatusCode.Unauthorized));
        var result = await _h.Run(Tool(http, "tavily", "exa"), """{"query":"q"}""");
        Assert.True(result.IsError);
        var text = ToolHarness.TextOf(result);
        Assert.Contains("Every configured search provider failed", text);
        Assert.Contains("- Tavily: Tavily returned HTTP 401", text);
        Assert.Contains("key expired", text);
        Assert.Contains("- Exa:", text);
    }

    [Fact]
    public async Task A_provider_that_worked_but_found_nothing_says_rephrasing_is_worth_a_try()
    {
        var result = await _h.Run(Tool(Tavily(), "tavily"), """{"query":"zzzqqq"}""");
        Assert.True(result.IsError);
        var text = ToolHarness.TextOf(result);
        Assert.Contains("no provider had results for \"zzzqqq\"", text);
        Assert.Contains("Rephrasing", text);
    }

    [Fact]
    public async Task A_success_after_a_failure_still_shows_the_failure()
    {
        var http = new FakeHttp(r => r.RequestUri!.Host == "api.tavily.com"
            ? FakeHttp.Ok("{}", "application/json", HttpStatusCode.PaymentRequired)
            : FakeHttp.Ok("""{"results":[{"title":"E","url":"https://e.example","text":"t"}]}""", "application/json"));
        var result = await _h.Run(Tool(http, "tavily", "exa"), """{"query":"q"}""");
        Assert.False(result.IsError);
        var text = ToolHarness.TextOf(result);
        Assert.Contains("(via Exa)", text);
        Assert.Contains("could not be used this time", text);
        Assert.Contains("HTTP 402", text);
    }

    [Fact]
    public async Task The_tool_never_sees_or_leaks_an_api_key()
    {
        var http = Tavily(("T", "https://a.example", "s"));
        var result = await _h.Run(Tool(http, "tavily"), """{"query":"q"}""");
        Assert.DoesNotContain("secret-tavily-0000", ToolHarness.TextOf(result));
        Assert.DoesNotContain("secret-tavily-0000", result.Details!.Value.GetRawText());
    }

    [Fact]
    public async Task Details_carry_the_environment_the_provider_and_the_result_links_but_not_the_snippets()
    {
        var result = await _h.Run(Tool(Tavily(("T", "https://a.example", "private snippet text")), "tavily"), """{"query":"q"}""");
        var details = result.Details!.Value;
        Assert.Equal("webSearch", details.GetProperty("kind").GetString());
        Assert.Equal("dev-1", details.GetProperty("environment").GetProperty("deviceId").GetString());
        Assert.Equal("tavily", details.GetProperty("provider").GetString());
        Assert.Equal(1, details.GetProperty("resultCount").GetInt32());
        Assert.Equal("https://a.example", details.GetProperty("results")[0].GetProperty("url").GetString());
        Assert.DoesNotContain("private snippet text", details.GetRawText());
    }

    [Fact]
    public async Task Aborting_rethrows_the_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var http = new FakeHttp(async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return FakeHttp.Ok("{}");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _h.Run(Tool(http, "tavily"), """{"query":"q"}""", cts.Token));
    }
}
