using Ncw.Agent.Kernel.Subagents;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Subagents.Tests;

/// <summary>子代理思考档位的三档选择。移植自 2.x 的 <c>subagent-thinking.test.ts</c>。</summary>
public sealed class SubagentThinkingTests
{
    [Fact]
    public void A_level_declared_in_the_agent_file_beats_the_settings_column() =>
        Assert.Equal(ThinkingLevel.High, SubagentThinkingSelection.Select(ThinkingLevel.High, SubagentThinking.Of(ThinkingLevel.Low), ThinkingLevel.Medium));

    [Fact]
    public void Without_a_declaration_the_settings_column_beats_the_parent_run() =>
        Assert.Equal(ThinkingLevel.Low, SubagentThinkingSelection.Select(null, SubagentThinking.Of(ThinkingLevel.Low), ThinkingLevel.Medium));

    [Fact]
    public void When_the_settings_say_follow_the_conversation_the_parents_level_for_this_turn_is_used() =>
        Assert.Equal(ThinkingLevel.Medium, SubagentThinkingSelection.Select(null, SubagentThinking.Inherit, ThinkingLevel.Medium));

    [Fact]
    public void Even_with_all_three_sources_empty_it_does_not_crash_it_falls_back_to_the_parent() =>
        Assert.Equal(ThinkingLevel.Auto, SubagentThinkingSelection.Select(null, SubagentThinking.Inherit, ThinkingLevel.Auto));

    [Fact]
    public void Inherit_is_its_own_value_and_not_auto_which_means_the_models_own_default()
    {
        Assert.True(SubagentThinking.Inherit.IsInherit);
        Assert.False(SubagentThinking.Of(ThinkingLevel.Auto).IsInherit);
        // 用户把主力模型调到「高」之后,「跟随」的子代理该跟着高;「auto」是另一个意思。
        Assert.Equal(ThinkingLevel.High, SubagentThinkingSelection.Select(null, SubagentThinking.Inherit, ThinkingLevel.High));
        Assert.Equal(ThinkingLevel.Auto, SubagentThinkingSelection.Select(null, SubagentThinking.Of(ThinkingLevel.Auto), ThinkingLevel.High));
    }

    [Theory]
    [InlineData("auto", ThinkingLevel.Auto)]
    [InlineData("off", ThinkingLevel.Off)]
    [InlineData("minimal", ThinkingLevel.Minimal)]
    [InlineData("low", ThinkingLevel.Low)]
    [InlineData("medium", ThinkingLevel.Medium)]
    [InlineData("high", ThinkingLevel.High)]
    [InlineData("xhigh", ThinkingLevel.XHigh)]
    [InlineData("max", ThinkingLevel.Max)]
    public void Every_file_level_name_parses(string name, ThinkingLevel expected)
    {
        Assert.True(SubagentThinkingSelection.TryParseLevel(name, out var level));
        Assert.Equal(expected, level);
    }

    [Theory]
    [InlineData("inherit")]
    [InlineData("on")]
    [InlineData("")]
    [InlineData("HIGH")]
    public void Anything_else_is_not_a_file_level_and_inherit_is_not_a_legal_file_value(string name) =>
        Assert.False(SubagentThinkingSelection.TryParseLevel(name, out _));

    [Fact]
    public void The_file_level_names_are_exactly_the_enum()
    {
        Assert.Equal(Enum.GetValues<ThinkingLevel>().Select(l => l.ToString().ToLowerInvariant()).Order(), SubagentThinkingSelection.LevelNames.Order());
    }
}
