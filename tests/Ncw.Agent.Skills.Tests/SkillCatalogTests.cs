namespace Ncw.Agent.Skills.Tests;

/// <summary>Skill 目录(2.x 的 <c>SkillRegistry</c>,但 3.0 是不可变快照)与「这一轮下发哪几条」的合并规则。</summary>
public sealed class SkillCatalogTests
{
    private static Skill S(string name, bool clientAssets = false) =>
        new(name, "d", "未分类", new SkillSource(SkillSourceKind.Folder, ""), SkillScope.Global, "正文", ClientAssetsOnly: clientAssets);

    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    [Fact]
    public void Invariant_the_list_is_sorted_by_name_so_the_prompt_prefix_does_not_jitter_with_directory_order()
    {
        // 目录里的顺序会直接变成系统提示词里的顺序;抖动的前缀让上游的 prompt cache 整体失效。
        var catalog = new SkillCatalog([S("zebra"), S("apple"), S("mango")], []);

        Assert.Equal(["apple", "mango", "zebra"], catalog.Skills.Select(s => s.Name));
    }

    [Fact]
    public void Invariant_an_empty_selection_means_everything_not_nothing()
    {
        // 出厂的 activeSkillIds 是 [],按字面理解就是「一条都不生效」——用户装完 Skill 却发现模型没反应。
        var catalog = new SkillCatalog([S("a"), S("b")], []);

        Assert.Equal(["a", "b"], catalog.Resolve([]).Select(s => s.Name));
        Assert.Equal(["a", "b"], catalog.Resolve(null).Select(s => s.Name));
    }

    [Fact]
    public void A_listed_selection_is_an_allow_list_and_unknown_ids_are_ignored()
    {
        var catalog = new SkillCatalog([S("a"), S("b")], []);

        Assert.Equal(["b"], catalog.Resolve(["b", "不存在的"]).Select(s => s.Name));
    }

    [Fact]
    public void In_explicit_mode_an_empty_selection_stays_empty()
    {
        var catalog = new SkillCatalog([S("a"), S("b")], []);

        Assert.Empty(catalog.Resolve([], explicitSelection: true));
        Assert.Equal(2, catalog.Resolve([], explicitSelection: false).Count);
    }

    [Fact]
    public void Get_finds_by_exact_name()
    {
        var catalog = new SkillCatalog([S("a")], []);

        Assert.NotNull(catalog.Get("a"));
        Assert.Null(catalog.Get("A"));
    }

    [Fact]
    public void Invariant_the_merge_is_global_switch_and_workspace_selection_and_assets_available()
    {
        var a = S("a");
        var assets = S("needs-device", clientAssets: true);
        var disabled = new HashSet<string> { "off" };

        Assert.True(SkillCatalog.IsActive(a, None, null));
        Assert.True(SkillCatalog.IsActive(a, None, new WorkspaceSkillSelection([], false)));          // 空清单 = 全都要
        Assert.False(SkillCatalog.IsActive(a, None, new WorkspaceSkillSelection([], true)));          // 显式空 = 一条都不要
        Assert.False(SkillCatalog.IsActive(a, None, new WorkspaceSkillSelection(["b"], true)));
        Assert.True(SkillCatalog.IsActive(a, None, new WorkspaceSkillSelection(["a"], true)));
        Assert.False(SkillCatalog.IsActive(S("off"), disabled, null));                                // 全局关掉的不出现
        Assert.False(SkillCatalog.IsActive(assets, None, null));                                      // 本机资源不可用的永远不激活
    }
}
