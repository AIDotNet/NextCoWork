using System.Text;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Pricing;
using Ncw.Agent.Llm.Thinking;

namespace Ncw.Agent.Catalog.Tests;

/// <summary>移植自 2.x 的 <c>models-dev.test.ts</c>(解析 / 阶梯 / 白名单厂商)与 <c>models-dev-catalog.test.ts</c> 里与解析有关的部分。</summary>
public class ModelsDevParserTests
{
    private static ParsedCatalog Parse(string json) => ModelsDevParser.Parse(Encoding.UTF8.GetBytes(json), "2026-11-14");

    [Fact]
    public void The_embedded_seed_parses_with_the_same_parser_as_live_data()
    {
        var parsed = ModelsDevParser.Parse(SeedCatalog.Bytes(), "seed");
        Assert.True(parsed.ModelCount >= 10);
        Assert.Equal(0, parsed.Skipped);
        var sonnet = parsed.Providers.Single(p => p.Id == "anthropic").Models.Single(m => m.Id == "claude-sonnet-4-5");
        Assert.Equal(200_000, sonnet.ContextWindow);
        Assert.Equal(64_000, sonnet.MaxOutputTokens);
        Assert.True(sonnet.Capabilities.Vision);
        Assert.True(sonnet.Capabilities.Reasoning);
        Assert.NotNull(sonnet.Price);
    }

    [Fact]
    public void Only_usd_first_party_vendors_get_a_price()
    {
        // models.dev 的记录没有币种字段:国内厂商里有人民币计价的,混进来会把 ¥ 当成 $。
        var parsed = Parse(Samples.ModelsDev(1, 1));
        Assert.NotNull(parsed.Providers.Single(p => p.Id == "anthropic").Models[0].Price);
        Assert.Null(parsed.Providers.Single(p => p.Id == "deepseek").Models[0].Price);
    }

    [Fact]
    public void Tiers_are_priced_after_the_threshold_and_the_last_tier_is_unbounded()
    {
        // 2.x:每个 tier 条目的费率是「超过 size 之后」的价,所以第 i 档的上界是第 i 个 size。
        var json = """
        {"openai":{"models":{"m":{"id":"m","cost":{"input":1,"output":2,"cache_read":0.1,
          "tiers":[{"tier":{"type":"context","size":272000},"input":2,"output":3,"cache_read":0.2}]}}}}}
        """;
        var price = Parse(json).Providers[0].Models[0].Price!;
        Assert.Equal(2, price.Tiers.Count);
        Assert.Equal(272_000, price.Tiers[0].UpToInputTokens);
        Assert.Equal(1m, price.Tiers[0].Rate.Input);
        Assert.Null(price.Tiers[1].UpToInputTokens);
        Assert.Equal(2m, price.Tiers[1].Rate.Input);
        Assert.Equal(0.2m, price.Tiers[1].Rate.CacheRead);
    }

    [Fact]
    public void The_legacy_context_over_200k_shape_becomes_a_second_tier()
    {
        var json = """{"google":{"models":{"g":{"id":"g","cost":{"input":1.25,"output":10,"context_over_200k":{"input":2.5,"output":15}}}}}}""";
        var price = Parse(json).Providers[0].Models[0].Price!;
        Assert.Equal(new long?[] { 200_000, null }, price.Tiers.Select(t => t.UpToInputTokens).ToArray());
        Assert.Equal(2.5m, price.Tiers[1].Rate.Input);
    }

    [Fact]
    public void A_tier_without_input_or_output_makes_the_model_single_tier_rather_than_invented()
    {
        var json = """{"openai":{"models":{"m":{"id":"m","cost":{"input":1,"output":2,"tiers":[{"tier":{"type":"context","size":100},"input":5}]}}}}}""";
        var price = Parse(json).Providers[0].Models[0].Price!;
        Assert.Single(price.Tiers);
    }

    [Fact]
    public void A_model_without_input_and_output_cost_has_no_price()
    {
        var json = """{"openai":{"models":{"m":{"id":"m","cost":{"input":1}}}}}""";
        Assert.Null(Parse(json).Providers[0].Models[0].Price);
    }

    [Fact]
    public void A_malformed_entry_is_dropped_but_the_rest_of_the_file_is_adopted()
    {
        // 容错的对象是条目,不是整份文件。
        var json = """
        {"anthropic":{"models":{
          "good":{"id":"good","limit":{"context":1000,"output":100}},
          "bad":{"id":"bad","limit":{"context":"lots"}},
          "worse":"not an object"}},
         "broken":"not a provider",
         "empty":{"name":"no models key"}}
        """;
        var parsed = Parse(json);
        Assert.Equal(["good"], parsed.Providers.Single().Models.Select(m => m.Id).ToArray());
        Assert.Equal(4, parsed.Skipped);
    }

    [Fact]
    public void The_root_must_be_an_object_and_the_text_must_be_json()
    {
        Assert.Throws<CatalogParseException>(() => Parse("[]"));
        Assert.Throws<CatalogParseException>(() => Parse("not json"));
    }

    [Fact]
    public void Content_after_the_root_object_is_rejected()
    {
        Assert.Throws<CatalogParseException>(() => Parse("""{"x":{"models":{}}} {"y":1}"""));
        Assert.Throws<CatalogParseException>(() => Parse("""{"x":{"models":{"m":{"id":"m"}}}"""));
        Assert.Throws<CatalogParseException>(() => Parse(""));
    }

    [Fact]
    public void Providers_are_ordered_by_key_and_trailing_commas_are_accepted()
    {
        var parsed = Parse("""{"zeta":{"models":{"a":{"id":"a"},}},"alpha":{"models":{"b":{"id":"b"}}},}""");
        Assert.Equal(["alpha", "zeta"], parsed.Providers.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Repeated_values_across_models_share_one_instance()
    {
        // 上万个模型里输入输出类型、推理强度、能力组合、家族名只有少数几种:共用实例,不是每个模型一份
        var json = """
        {"a":{"models":{"m1":{"id":"m1","family":"fam","reasoning":true,"modalities":{"input":["Text","image"],"output":["text"]},"reasoning_options":[{"type":"effort","values":["low","high"]}]}}},
         "b":{"models":{"m1":{"id":"m1","family":"fam","reasoning":true,"modalities":{"input":["text","IMAGE"],"output":["text"]},"reasoning_options":[{"type":"effort","values":["low","high"]}]}}}}
        """;
        var parsed = Parse(json);
        var first = parsed.Providers[0].Models[0];
        var second = parsed.Providers[1].Models[0];
        Assert.Same(first.Id, second.Id);
        Assert.Same(first.Family, second.Family);
        Assert.Same(first.Capabilities, second.Capabilities);
        Assert.Same(first.ReasoningEfforts, second.ReasoningEfforts);
        Assert.Equal(["text", "image"], first.Capabilities.InputModalities);
    }

    [Fact]
    public void Find_prefers_first_party_among_duplicate_ids_and_keeps_provider_order_within_a_rank()
    {
        var parsed = Parse("""{"agg1":{"models":{"m":{"id":"m"}}},"agg2":{"models":{"m":{"id":"m"}}},"openai":{"models":{"m":{"id":"m"}}}}""");
        var snapshot = new CatalogSnapshot(1, CatalogSource.Cache, null, parsed.Providers);
        Assert.Equal("openai", snapshot.Find(null, "m")!.ProviderId);
        Assert.Equal("openai", snapshot.Find(null, "M")!.ProviderId);

        var aggregators = new CatalogSnapshot(1, CatalogSource.Cache, null, [.. parsed.Providers.Where(p => p.Id != "openai")]);
        Assert.Equal("agg1", aggregators.Find(null, "m")!.ProviderId);
    }

    [Fact]
    public void An_output_limit_above_the_context_window_is_clamped()
    {
        // 聚合源偶尔两者写反。
        var model = Parse("""{"x":{"models":{"m":{"id":"m","limit":{"context":1000,"output":5000}}}}}""").Providers[0].Models[0];
        Assert.Equal(1000, model.MaxOutputTokens);
    }

    [Fact]
    public void Reasoning_effort_options_are_read_and_unknown_values_ignored()
    {
        var json = """{"x":{"models":{"m":{"id":"m","reasoning":true,"reasoning_options":[{"type":"budget"},{"type":"effort","values":["low","high","turbo","high"]}]}}}}""";
        var model = Parse(json).Providers[0].Models[0];
        Assert.Equal([ReasoningEffort.Low, ReasoningEffort.High], model.ReasoningEfforts!.ToArray());
    }

    [Fact]
    public void Unknown_fields_are_ignored_and_modalities_are_lower_cased()
    {
        var json = """{"x":{"id":"x","name":"X","mystery":1,"models":{"m":{"id":"m","modalities":{"input":["Text","IMAGE"],"output":["text"]},"surprise":{"a":1}}}}}""";
        var model = Parse(json).Providers[0].Models[0];
        Assert.True(model.Capabilities.Vision);
        Assert.True(model.Capabilities.IsChatModel);
    }

    [Fact]
    public void An_image_only_output_model_is_not_a_chat_model()
    {
        var json = """{"x":{"models":{"img":{"id":"img","modalities":{"input":["text"],"output":["image"]}}}}}""";
        Assert.False(Parse(json).Providers[0].Models[0].Capabilities.IsChatModel);
    }

    [Fact]
    public void The_catalog_price_feeds_the_pricing_calculator()
    {
        var model = Parse(Samples.ModelsDev(1)).Providers[0].Models[0];
        var micros = PricingCalculator.PriceOf(model.Price!, new TokenUsage(1_000_000, 1_000_000), DateTimeOffset.UtcNow);
        Assert.Equal(18_000_000, micros!.Micros);
    }
}
