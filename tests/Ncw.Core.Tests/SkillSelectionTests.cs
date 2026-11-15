using Ncw.Core.Domain;

namespace Ncw.Core.Tests;

/// <summary>Skill 的工作区选装规则,移植自 2.x <c>kernel/skill/__tests__/activation.test.ts</c>(屏障 <c>SkillActivationBarrier</c> 属于「从会话提炼 Skill」流程,不在这里)。</summary>
public sealed class SkillSelectionTests
{
    [Fact]
    public void Materializes_the_implicit_all_selection_before_adding_or_removing_a_skill()
    {
        Assert.Equal(["a", "b", "c"], SkillSelection.NextActiveIds([], false, ["a", "b"], "c", true));
        Assert.Equal(["b"], SkillSelection.NextActiveIds([], false, ["a", "b"], "a", false));
    }

    [Fact]
    public void Appends_to_an_explicit_selection_without_duplicates()
    {
        Assert.Equal(["a", "b"], SkillSelection.NextActiveIds(["a"], true, ["a", "b"], "b", true));
        Assert.Equal(["a", "b"], SkillSelection.NextActiveIds(["a", "b"], true, ["a", "b"], "b", true));
    }

    [Fact]
    public void Treats_an_empty_explicit_selection_as_nothing_selected()
    {
        Assert.Equal(["b"], SkillSelection.NextActiveIds([], true, ["a", "b"], "b", true));
    }

    [Fact]
    public void Matches_the_written_directory_even_when_frontmatter_uses_another_valid_name()
    {
        Assert.True(SkillSelection.MatchesProjectSkillPath("/repo/.next-cowork/skills/pricing-rule/SKILL.md", ".next-cowork/skills", "pricing-rule"));
        Assert.True(SkillSelection.MatchesProjectSkillPath(".next-cowork\\skills\\pricing-rule\\SKILL.md", ".next-cowork/skills", "pricing-rule"));
        Assert.False(SkillSelection.MatchesProjectSkillPath("/repo/.next-cowork/skills/other/SKILL.md", ".next-cowork/skills", "pricing-rule"));
    }

    [Fact]
    public void Detects_same_length_selection_changes()
    {
        Assert.True(SkillSelection.SameList(["a", "b"], ["a", "b"]));
        Assert.False(SkillSelection.SameList(["a", "b"], ["b", "a"]));
        Assert.False(SkillSelection.SameList(["a"], ["a", "b"]));
    }

    [Fact]
    public void Invariant_an_empty_non_explicit_list_means_everything_but_never_for_an_unavailable_skill()
    {
        Assert.True(SkillSelection.IsActiveInWorkspace("a", false, [], false));
        Assert.False(SkillSelection.IsActiveInWorkspace("a", false, [], true));
        Assert.False(SkillSelection.IsActiveInWorkspace("a", true, [], false));
        Assert.True(SkillSelection.IsActiveInWorkspace("a", false, null, true)); // 没指定工作区:不谈激活
        Assert.True(SkillSelection.IsActiveInWorkspace("a", false, ["a"], true));
        Assert.False(SkillSelection.IsActiveInWorkspace("a", false, ["b"], true));
    }
}
