namespace Ncw.Agent.Catalog.Tests;

/// <summary>移植自 2.x 的 <c>model-selection.test.ts</c>(键、子代理三档)与 <c>provider-write.test.ts</c> 里「锁定了供应商的设置项在别名表变动后怎么修」一组。</summary>
public class ModelSelectionTests
{
    private static ModelAliasRecord A(string provider, string alias) => new(alias, provider, alias);

    private static ModelSelection? Repair(ModelSelection current, ModelSelection fallback, params ModelAliasRecord[] aliases)
    {
        var (bindings, names) = ModelSelectionRepair.Alive(aliases);
        return ModelSelectionRepair.Repair(current, bindings, names, fallback);
    }

    private static readonly ModelSelection None = new("");

    // ── 键 ──

    [Fact]
    public void The_key_is_provider_slash_alias_and_only_the_first_slash_separates()
    {
        Assert.Equal("gw/m", ModelSelections.Key("gw", "m"));
        Assert.Equal("gw/openrouter/claude-sonnet-4", ModelSelections.Key("gw", "openrouter/claude-sonnet-4"));
        Assert.Equal(new ModelSelection("openrouter/claude-sonnet-4", "gw"), ModelSelections.ParseKey("gw/openrouter/claude-sonnet-4"));
        Assert.Equal(new ModelSelection("m"), ModelSelections.ParseKey("m")); // 旧的持久化值:裸别名
        Assert.Equal(new ModelSelection("m"), ModelSelections.ParseKey("/m"));
        Assert.Equal("", ModelSelections.Key("gw", "")); // 空别名 = 未指定,和「跟随对话」共用空值
        Assert.Equal("/m", ModelSelections.Key(null, "m"));
    }

    [Fact]
    public void A_selection_converts_to_the_router_ref()
    {
        Assert.Equal(new Ncw.Agent.Llm.ModelRef("gw", "m"), new ModelSelection("m", "gw").ToRef());
        Assert.Equal(new Ncw.Agent.Llm.ModelRef(null, "m"), new ModelSelection("m").ToRef());
    }

    // ── 子代理 ──

    [Fact]
    public void A_subagent_uses_the_declared_pair_then_the_configured_default_then_the_parent()
    {
        var parent = new ModelSelection("big", "p1");
        var configured = new ModelSelection("cheap", "p2");
        var declared = new ModelSelection("special", null);

        // 声明了别名但没声明供应商 = 只认别名,不沿用父亲锁的那家(否则拼出「A 家的别名 + B 家的锁」)。
        Assert.Equal(declared, ModelSelections.ForSubagent(declared, configured, parent));
        Assert.Equal(configured, ModelSelections.ForSubagent(null, configured, parent));
        Assert.Equal(parent, ModelSelections.ForSubagent(null, new ModelSelection("  "), parent));
    }

    // ── 修复 ──

    [Fact]
    public void Nothing_is_changed_when_the_selection_is_empty_or_still_alive()
    {
        Assert.Null(Repair(None, new ModelSelection("x"), A("a", "m")));
        Assert.Null(Repair(new ModelSelection("m", "a"), None, A("a", "m")));
        Assert.Null(Repair(new ModelSelection("m"), None, A("a", "m")));
    }

    [Fact]
    public void A_pinned_provider_that_lost_the_alias_but_it_still_exists_elsewhere_is_unlocked_not_renamed_and_not_switched()
    {
        var repaired = Repair(new ModelSelection("m", "gone"), new ModelSelection("fallback", "p"), A("other", "m"));
        Assert.Equal(new ModelSelection("m"), repaired);
    }

    [Fact]
    public void A_pinned_provider_that_was_the_only_one_falls_back_to_the_given_pair()
    {
        var fallback = new ModelSelection("first", "p");
        Assert.Equal(fallback, Repair(new ModelSelection("m", "gone"), fallback, A("p", "first")));
        Assert.Equal(None, Repair(new ModelSelection("m", "gone"), None));
    }

    [Fact]
    public void An_unpinned_alias_that_disappeared_falls_back()
    {
        Assert.Equal(None, Repair(new ModelSelection("m"), None, A("p", "other")));
    }

    [Fact]
    public void Disabling_a_provider_is_reversible_so_the_selection_is_not_rewritten()
    {
        // 判据看别名表(不看供应商启没启用):用户明天会开回来。
        Assert.Null(Repair(new ModelSelection("m", "p"), None, A("p", "m")));
    }
}
