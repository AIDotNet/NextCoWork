using System.Net;
using System.Text.Json;
using Ncw.Agent.WebTools.Net;
using Ncw.Agent.WebTools.Search;

namespace Ncw.Agent.WebTools.Tests;

/// <summary>搜索服务(移植自 2.x <c>search/__tests__/service.test.ts</c>:故障切换 / 不进链 / 中断 / 条数 / 来源标注 / 各家请求形状;「内置免 Key 兜底」未移植,见 parity)。</summary>
public sealed class SearchServiceTests
{
    private sealed class Config(IEnumerable<SearchProviderStatus> statuses, Dictionary<string, string?>? keys = null) : ISearchConfigSource
    {
        public List<string> KeyReads { get; } = [];

        public ValueTask<IReadOnlyList<SearchProviderStatus>> StatusesAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<SearchProviderStatus>>([.. statuses]);

        public ValueTask<string?> ApiKeyAsync(string id, CancellationToken ct)
        {
            KeyReads.Add(id);
            return ValueTask.FromResult(keys is not null && keys.TryGetValue(id, out var key) ? key : $"key-for-{id}-1234");
        }
    }

    private static SearchProviderStatus On(string id, int priority = 0, bool hasKey = true) => new(new SearchProviderConfig(id, true, priority), hasKey);

    private static string Results(params (string Title, string Url, string Snippet)[] rows) =>
        new System.Text.Json.Nodes.JsonObject { ["results"] = new System.Text.Json.Nodes.JsonArray([.. rows.Select(r => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["title"] = r.Title, ["url"] = r.Url, ["content"] = r.Snippet })]) }.ToJsonString();

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => FakeHttp.Ok(body, "application/json", status);

    private static HttpResponseMessage Body(string host, HttpRequestMessage request) =>
        host switch
        {
            "api.tavily.com" => Json(Results(("T1", "https://t1.example", "tavily snippet"))),
            "api.exa.ai" => Json("""{"results":[{"title":"E1","url":"https://e1.example","text":"exa text"}]}"""),
            "api.search.brave.com" => Json("""{"web":{"results":[{"title":"B1","url":"https://b1.example","description":"brave desc"}]}}"""),
            "google.serper.dev" => Json("""{"organic":[{"title":"S1","link":"https://s1.example","snippet":"serper snippet"}],"answerBox":{"title":"A","link":"https://answerbox.example"}}"""),
            "serpapi.com" => Json("""{"organic_results":[{"title":"P1","link":"https://p1.example","snippet":"serpapi snippet"}]}"""),
            "metaso.cn" => Json("""{"data":{"webpages":[{"title":"M1","link":"https://m1.example","snippet":"metaso"}]}}"""),
            _ => throw new InvalidOperationException(host),
        };

    private static SearchService Service(Config config, FakeHttp http, SearchOptions? options = null) => new(config, http, options);

    // ───────────────────────── 故障切换 ─────────────────────────

    [Fact]
    public async Task A_500_from_the_first_provider_falls_through_to_the_second_and_hands_over_its_results()
    {
        var http = new FakeHttp(r => r.RequestUri!.Host == "api.tavily.com" ? Json("""{"error":"boom"}""", HttpStatusCode.InternalServerError) : Body(r.RequestUri.Host, r));
        var outcome = await Service(new Config([On("tavily", 0), On("exa", 1)]), http).RunAsync("q", 5, default);

        Assert.Equal("exa", outcome.Provider);
        Assert.Equal("E1", Assert.Single(outcome.Results).Title);
        var failure = Assert.Single(outcome.Failures); // 成功时仍然带着一路上的失败:用户那个坏掉的 Key 只有在这里才看得见
        Assert.Equal("tavily", failure.ProviderId);
        Assert.Contains("HTTP 500", failure.Message);
        Assert.Contains("boom", failure.Message);
    }

    [Fact]
    public async Task An_empty_result_also_falls_through_because_zero_results_usually_means_a_renamed_field()
    {
        var http = new FakeHttp(r => r.RequestUri!.Host == "api.tavily.com" ? Json("""{"results":[]}""") : Body(r.RequestUri.Host, r));
        var outcome = await Service(new Config([On("tavily", 0), On("brave", 1)]), http).RunAsync("q", 5, default);
        Assert.Equal("brave", outcome.Provider);
        Assert.Contains("no results", Assert.Single(outcome.Failures).Message);
    }

    [Fact]
    public async Task Everything_failing_returns_no_results_with_every_reason_and_does_not_claim_nothing_is_configured()
    {
        var http = new FakeHttp(_ => Json("""{"error":"invalid api key"}""", HttpStatusCode.Unauthorized));
        var outcome = await Service(new Config([On("tavily", 0), On("exa", 1), On("brave", 2)]), http).RunAsync("q", 5, default);
        Assert.Empty(outcome.Results);
        Assert.Null(outcome.Provider);
        Assert.False(outcome.NothingConfigured);
        Assert.Equal(["tavily", "exa", "brave"], outcome.Failures.Select(static f => f.ProviderId)); // 顺序即尝试顺序
        Assert.All(outcome.Failures, static f => Assert.Contains("HTTP 401", f.Message));
    }

    [Fact]
    public async Task Providers_are_tried_by_priority_not_by_the_order_they_were_passed_in()
    {
        var http = new FakeHttp(r => Body(r.RequestUri!.Host, r));
        var outcome = await Service(new Config([On("exa", 5), On("tavily", 1), On("brave", 3)]), http).RunAsync("q", 5, default);
        Assert.Equal("tavily", outcome.Provider);
        Assert.Single(http.Requests); // 第一家就成功,后面的不再打
    }

    // ───────────────────────── 根本不进链 ─────────────────────────

    [Fact]
    public async Task A_provider_without_a_key_sends_no_request()
    {
        var http = new FakeHttp(r => Body(r.RequestUri!.Host, r));
        var outcome = await Service(new Config([On("tavily", 0, hasKey: false), On("exa", 1)]), http).RunAsync("q", 5, default);
        Assert.Equal("exa", outcome.Provider);
        Assert.DoesNotContain(http.Requests, static r => r.Url.Host == "api.tavily.com");
    }

    [Fact]
    public async Task A_disabled_provider_is_not_in_the_chain()
    {
        var http = new FakeHttp(r => Body(r.RequestUri!.Host, r));
        var off = new SearchProviderStatus(new SearchProviderConfig("tavily", false, 0), true);
        var outcome = await Service(new Config([off, On("exa", 1)]), http).RunAsync("q", 5, default);
        Assert.Equal("exa", outcome.Provider);
    }

    [Fact]
    public async Task Unavailable_providers_are_skipped_even_if_somebody_marked_them_enabled_with_a_key()
    {
        var http = new FakeHttp(r => Body(r.RequestUri!.Host, r));
        var outcome = await Service(new Config([On("bing", 0), On("doubao", 1), On("brave", 2)]), http).RunAsync("q", 5, default);
        Assert.Equal("brave", outcome.Provider);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task With_nothing_configured_there_are_no_requests_and_the_outcome_says_so()
    {
        var http = new FakeHttp(_ => throw new InvalidOperationException("no request expected"));
        var outcome = await Service(new Config([On("tavily", 0, hasKey: false), new SearchProviderStatus(new SearchProviderConfig("exa", false, 1), true)]), http).RunAsync("q", 5, default);
        Assert.Empty(outcome.Results);
        Assert.True(outcome.NothingConfigured);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task A_stored_key_that_cannot_be_read_is_recorded_and_the_chain_moves_on()
    {
        var http = new FakeHttp(r => Body(r.RequestUri!.Host, r));
        var config = new Config([On("tavily", 0), On("exa", 1)], new Dictionary<string, string?> { ["tavily"] = null, ["exa"] = "exa-key-5678" });
        var outcome = await Service(config, http).RunAsync("q", 5, default);
        Assert.Equal("exa", outcome.Provider);
        Assert.Contains("could not be read", Assert.Single(outcome.Failures).Message);
    }

    // ───────────────────────── 中断 / 超时 ─────────────────────────

    [Fact]
    public async Task Aborting_the_run_throws_at_once_and_does_not_try_the_next_provider()
    {
        using var cts = new CancellationTokenSource();
        var http = new FakeHttp(async (r, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return Body(r.RequestUri!.Host, r);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(new Config([On("tavily", 0), On("exa", 1)]), http).RunAsync("q", 5, cts.Token));
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task One_providers_timeout_only_interrupts_that_provider_and_the_next_one_still_runs()
    {
        var http = new FakeHttp(async (r, ct) =>
        {
            if (r.RequestUri!.Host == "api.tavily.com")
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return Body(r.RequestUri.Host, r);
        });
        var outcome = await Service(new Config([On("tavily", 0), On("exa", 1)]), http, new SearchOptions { PerProviderTimeout = TimeSpan.FromMilliseconds(100) }).RunAsync("q", 5, default);
        Assert.Equal("exa", outcome.Provider);
        Assert.Contains("timed out", Assert.Single(outcome.Failures).Message);
    }

    // ───────────────────────── 条数 / 标注 ─────────────────────────

    [Fact]
    public async Task Extra_results_are_cut_to_the_requested_count()
    {
        var http = new FakeHttp(_ => Json(Results(("1", "https://1.example", "a"), ("2", "https://2.example", "b"), ("3", "https://3.example", "c"), ("4", "https://4.example", "d"))));
        var outcome = await Service(new Config([On("tavily")]), http).RunAsync("q", 2, default);
        Assert.Equal(["1", "2"], outcome.Results.Select(static r => r.Title));
    }

    [Fact]
    public async Task Every_result_is_tagged_with_its_provider()
    {
        var http = new FakeHttp(r => Body(r.RequestUri!.Host, r));
        foreach (var id in new[] { "tavily", "exa", "brave", "serper", "serpapi", "metaso" })
        {
            var outcome = await Service(new Config([On(id)]), http).RunAsync("q", 5, default);
            Assert.All(outcome.Results, r => Assert.Equal(id, r.Provider));
            Assert.NotEmpty(outcome.Results);
        }
    }

    [Fact]
    public async Task Serper_takes_only_organic_results_never_the_answer_box()
    {
        var http = new FakeHttp(r => Body(r.RequestUri!.Host, r));
        var outcome = await Service(new Config([On("serper")]), http).RunAsync("q", 5, default);
        Assert.DoesNotContain(outcome.Results, static r => r.Url.Contains("answerbox"));
    }

    [Fact]
    public async Task Exa_snippets_are_capped()
    {
        var http = new FakeHttp(_ => Json($$"""{"results":[{"title":"E","url":"https://e.example","text":"{{new string('x', 5000)}}"}]}"""));
        var outcome = await Service(new Config([On("exa")]), http).RunAsync("q", 5, default);
        Assert.Equal(1200, outcome.Results[0].Snippet.Length);
    }

    // ───────────────────────── 适配器的请求形状 ─────────────────────────

    [Fact]
    public async Task Authentication_follows_each_providers_documentation()
    {
        var http = new FakeHttp(r => Body(r.RequestUri!.Host, r));
        foreach (var id in new[] { "tavily", "exa", "brave", "serper", "serpapi", "metaso" })
        {
            await Service(new Config([On(id)]), http).RunAsync("the query", 3, default);
        }

        Dictionary<string, string> H(string host) => http.Requests.Single(r => r.Url.Host == host).Headers;
        Assert.Equal("Bearer key-for-tavily-1234", H("api.tavily.com")["authorization"]);
        Assert.Equal("key-for-exa-1234", H("api.exa.ai")["x-api-key"]);
        Assert.Equal("key-for-brave-1234", H("api.search.brave.com")["x-subscription-token"]);
        Assert.Equal("application/json", H("api.search.brave.com")["accept"]);
        Assert.Equal("key-for-serper-1234", H("google.serper.dev")["x-api-key"]);
        Assert.Equal("Bearer key-for-metaso-1234", H("metaso.cn")["authorization"]);
        Assert.Contains("api_key=key-for-serpapi-1234", http.Requests.Single(r => r.Url.Host == "serpapi.com").Url.Query); // 这家没有请求头方案
        Assert.All(http.Requests, r => Assert.Contains("NextCoWork", r.Headers["user-agent"]));
    }

    [Fact]
    public async Task The_query_and_count_really_reach_the_request_body_or_query_string()
    {
        var http = new FakeHttp(r => Body(r.RequestUri!.Host, r));
        foreach (var id in new[] { "tavily", "exa", "brave", "serper", "serpapi", "metaso" })
        {
            await Service(new Config([On(id)]), http).RunAsync("rust 异步 \"quotes\" & more", 7, default);
        }

        string BodyOf(string host) => http.Requests.Single(r => r.Url.Host == host).Body;
        using (var tavily = JsonDocument.Parse(BodyOf("api.tavily.com")))
        {
            Assert.Equal("rust 异步 \"quotes\" & more", tavily.RootElement.GetProperty("query").GetString());
            Assert.Equal(7, tavily.RootElement.GetProperty("max_results").GetInt32());
            Assert.Equal("basic", tavily.RootElement.GetProperty("search_depth").GetString());
        }

        using (var exa = JsonDocument.Parse(BodyOf("api.exa.ai")))
        {
            Assert.Equal(7, exa.RootElement.GetProperty("numResults").GetInt32());
            Assert.Equal(1200, exa.RootElement.GetProperty("contents").GetProperty("text").GetProperty("maxCharacters").GetInt32()); // 不能省:省了模型要对每条再调一次 WebFetch
        }

        using (var serper = JsonDocument.Parse(BodyOf("google.serper.dev")))
        {
            Assert.Equal("rust 异步 \"quotes\" & more", serper.RootElement.GetProperty("q").GetString());
            Assert.Equal(7, serper.RootElement.GetProperty("num").GetInt32());
        }

        using (var metaso = JsonDocument.Parse(BodyOf("metaso.cn")))
        {
            Assert.Equal("rust 异步 \"quotes\" & more", metaso.RootElement.GetProperty("question").GetString());
            Assert.False(metaso.RootElement.GetProperty("stream").GetBoolean());
        }

        var brave = http.Requests.Single(r => r.Url.Host == "api.search.brave.com").Url;
        Assert.Equal("rust 异步 \"quotes\" & more", System.Web.HttpUtility.ParseQueryString(brave.Query)["q"]);
        Assert.Equal("7", System.Web.HttpUtility.ParseQueryString(brave.Query)["count"]);
        var serpapi = http.Requests.Single(r => r.Url.Host == "serpapi.com").Url;
        Assert.Equal("rust 异步 \"quotes\" & more", System.Web.HttpUtility.ParseQueryString(serpapi.Query)["q"]);
    }

    [Fact]
    public async Task SerpApis_error_never_contains_the_key_even_though_it_travels_in_the_url()
    {
        var http = new FakeHttp(_ => Json("""{"error":"Invalid API key"}""", HttpStatusCode.Unauthorized));
        var outcome = await Service(new Config([On("serpapi")], new Dictionary<string, string?> { ["serpapi"] = "SUPER-SECRET-KEY-9999" }), http).RunAsync("q", 3, default);
        var message = Assert.Single(outcome.Failures).Message;
        Assert.Contains("HTTP 401", message);
        Assert.DoesNotContain("SUPER-SECRET-KEY-9999", message);
    }

    [Fact]
    public async Task A_response_that_is_not_json_is_a_failure_with_a_plain_message()
    {
        var http = new FakeHttp(_ => FakeHttp.Ok("<html>gateway error</html>", "text/html"));
        var outcome = await Service(new Config([On("tavily")]), http).RunAsync("q", 3, default);
        Assert.Contains("not valid JSON", Assert.Single(outcome.Failures).Message);
    }

    [Fact]
    public async Task The_error_body_in_a_failure_is_clipped_to_a_few_hundred_characters()
    {
        var http = new FakeHttp(_ => Json(new string('x', 5000), HttpStatusCode.BadGateway));
        var outcome = await Service(new Config([On("tavily")]), http).RunAsync("q", 3, default);
        Assert.True(Assert.Single(outcome.Failures).Message.Length < 500);
    }

    [Fact]
    public async Task Metaso_has_no_documented_response_so_the_result_array_is_found_by_shape()
    {
        var http = new FakeHttp(_ => Json("""{"code":0,"data":{"webpages":[{"title":"A","link":"https://a.example","snippet":"x"},{"title":"B","link":"https://b.example"}],"relatedQuestions":[{"title":"noise"}]}}"""));
        var outcome = await Service(new Config([On("metaso")]), http).RunAsync("q", 1, default);
        Assert.Equal(["A"], outcome.Results.Select(static r => r.Title)); // 按 count 截断
    }

    // ───────────────────────── 测试按钮 ─────────────────────────

    [Fact]
    public async Task Test_succeeds_with_a_latency_and_returns_no_results()
    {
        var http = new FakeHttp(r => Body(r.RequestUri!.Host, r));
        var result = await Service(new Config([On("tavily")]), http).TestAsync("tavily", default);
        Assert.True(result.Ok);
        Assert.NotNull(result.LatencyMs);
        Assert.Null(result.Key);
        Assert.Contains("\"max_results\":1", http.Requests.Single().Body); // 只要 1 条:连通性测试不花一次完整额度
    }

    [Fact]
    public async Task Test_says_the_key_is_fine_when_the_query_returned_nothing()
    {
        var http = new FakeHttp(_ => Json("""{"results":[]}"""));
        var result = await Service(new Config([On("tavily")]), http).TestAsync("tavily", default);
        Assert.True(result.Ok);
        Assert.Equal("websearch.test.noResults", result.Key);
    }

    [Fact]
    public async Task Test_failures_carry_a_stable_key_and_the_servers_own_words()
    {
        var http = new FakeHttp(_ => Json("""{"error":"quota exceeded"}""", HttpStatusCode.TooManyRequests));
        var result = await Service(new Config([On("tavily")]), http).TestAsync("tavily", default);
        Assert.False(result.Ok);
        Assert.Equal("websearch.test.failed", result.Key);
        Assert.Contains("quota exceeded", result.Detail);
    }

    [Fact]
    public async Task Test_of_an_unavailable_unknown_or_keyless_provider_never_sends_a_request()
    {
        var http = new FakeHttp(_ => throw new InvalidOperationException("no request expected"));
        var keyless = new Config([On("tavily")], new Dictionary<string, string?> { ["tavily"] = null });
        Assert.Equal("websearch.test.unavailable", (await Service(keyless, http).TestAsync("bing", default)).Key);
        Assert.Equal("websearch.test.unavailable", (await Service(keyless, http).TestAsync("ghost", default)).Key);
        Assert.Equal("websearch.test.noKey", (await Service(keyless, http).TestAsync("tavily", default)).Key);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Test_is_capped_by_a_hard_timeout()
    {
        var http = new FakeHttp(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json("{}");
        });
        var options = new SearchOptions { PerProviderTimeout = TimeSpan.FromSeconds(30), TestTimeout = TimeSpan.FromMilliseconds(100) };
        var result = await Service(new Config([On("tavily")]), http, options).TestAsync("tavily", default);
        Assert.False(result.Ok);
        Assert.Equal("websearch.test.failed", result.Key);
    }

    [Fact]
    public async Task An_ssrf_block_from_the_http_layer_is_a_plain_failure_not_a_crash()
    {
        var http = new FakeHttp((_, _) => throw new SsrfBlockedException("api.tavily.com", IPAddress.Loopback, "Refusing to reach \"api.tavily.com\""));
        var outcome = await Service(new Config([On("tavily")]), http).RunAsync("q", 3, default);
        Assert.Contains("Refusing to reach", Assert.Single(outcome.Failures).Message);
    }
}
