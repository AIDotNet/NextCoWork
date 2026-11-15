namespace Ncw.Agent.Skills.Tests;

/// <summary>系统提示词里的 Skill 目录:只有名字和描述,没有正文;按名字排序;16KB 封顶并说明被丢掉了几条。</summary>
public sealed class SkillSectionTests
{
    private static Skill S(string name, string description = "d", string body = "SECRET-BODY") =>
        new(name, description, "未分类", new SkillSource(SkillSourceKind.Folder, ""), SkillScope.Global, body);

    [Fact]
    public void No_skills_means_no_section()
    {
        Assert.Null(SkillSection.Build([]));
    }

    [Fact]
    public void Invariant_the_catalog_lists_names_and_descriptions_only_never_a_body()
    {
        var text = SkillSection.Build([S("commit", "写提交信息", "BODY-MARKER")])!;

        Assert.StartsWith("# Available Skills", text);
        Assert.Contains("- `commit` — 写提交信息", text);
        Assert.DoesNotContain("BODY-MARKER", text);
        Assert.Contains("THIS IS A CATALOG ONLY", text);
        Assert.Contains("NOT A GRANT OF PERMISSION", text); // 边界声明
    }

    [Fact]
    public void Entries_are_sorted_by_name()
    {
        var text = SkillSection.Build([S("zebra"), S("apple"), S("mango")])!;

        Assert.True(text.IndexOf("`apple`", StringComparison.Ordinal) < text.IndexOf("`mango`", StringComparison.Ordinal));
        Assert.True(text.IndexOf("`mango`", StringComparison.Ordinal) < text.IndexOf("`zebra`", StringComparison.Ordinal));
    }

    [Fact]
    public void The_output_is_byte_identical_for_the_same_input_so_the_cache_prefix_holds()
    {
        Assert.Equal(SkillSection.Build([S("b"), S("a")]), SkillSection.Build([S("a"), S("b")]));
    }

    [Fact]
    public void Control_characters_in_a_description_are_stripped()
    {
        var text = SkillSection.Build([S("commit", $"写{(char)27}[31m提交")])!;

        Assert.DoesNotContain((char)27, text);
    }

    [Fact]
    public void Invariant_the_catalog_is_capped_and_silently_dropping_is_not_allowed_it_says_how_many_were_left_out()
    {
        var many = Enumerable.Range(0, 200).Select(i => S($"skill-{i:D3}", new string('x', 800))).ToList();

        var text = SkillSection.Build(many)!;

        Assert.True(text.Length < SkillSection.CatalogMax + 3000);
        Assert.Matches(@"\(\d+ more Skill\(s\) not listed", text);
    }
}
