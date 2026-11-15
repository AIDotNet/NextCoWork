namespace Ncw.Agent.Hooks.Tests;

/// <summary>保存前的校验。移植自 2.x 渲染层的 <c>hook-form.test.ts</c> 里属于领域规则的几组(<c>validateHook</c> / <c>warnHook</c> / prompt 型草稿);表单草稿 ⇄ upsert 的转换是界面的事。</summary>
public sealed class HookValidationTests
{
    private static HookDefinition Draft(Func<HookDefinition, HookDefinition>? change = null)
    {
        var hook = new HookDefinition { Id = "h", Event = HookEvents.PreToolUse, Command = "echo hi", TimeoutMs = 10_000 };
        return change is null ? hook : change(hook);
    }

    [Fact]
    public void An_empty_command_is_refused()
    {
        Assert.Equal(HookErrorKeys.EmptyCommand, HookValidation.Validate(Draft(h => h with { Command = "   " })));
    }

    [Fact]
    public void An_empty_matcher_is_valid_it_means_every_trigger()
    {
        Assert.Null(HookValidation.Validate(Draft(h => h with { Matcher = "" })));
    }

    [Theory]
    [InlineData("Bash")]
    [InlineData("Write(src/*.ts)")]
    public void Permission_rule_syntax_is_a_valid_matcher(string matcher)
    {
        Assert.Null(HookValidation.Validate(Draft(h => h with { Matcher = matcher })));
    }

    [Fact]
    public void A_broken_matcher_is_refused()
    {
        Assert.Equal(HookErrorKeys.BadMatcher, HookValidation.Validate(Draft(h => h with { Matcher = "Bash(((( " })));
    }

    [Fact]
    public void The_timeout_must_be_positive_and_within_the_ceiling()
    {
        Assert.Equal(HookErrorKeys.BadTimeout, HookValidation.Validate(Draft(h => h with { TimeoutMs = 0 })));
        Assert.Equal(HookErrorKeys.BadTimeout, HookValidation.Validate(Draft(h => h with { TimeoutMs = -5000 })));
        Assert.Equal(HookErrorKeys.TimeoutTooLong, HookValidation.Validate(Draft(h => h with { TimeoutMs = 601_000 })));
        Assert.Null(HookValidation.Validate(Draft(h => h with { TimeoutMs = 600_000 })));
    }

    [Fact]
    public void An_unknown_event_is_refused()
    {
        Assert.Equal(HookErrorKeys.BadEvent, HookValidation.Validate(Draft(h => h with { Event = "SessionStart" })));
    }

    [Fact]
    public void A_blocking_event_with_a_prefix_matcher_warns_because_the_shell_continuation_guard_points_the_wrong_way()
    {
        Assert.Equal(HookValidation.WeakMatcher, HookValidation.Warn(Draft(h => h with { Event = HookEvents.PreToolUse, Matcher = "Bash(rm:*)" })));
    }

    [Fact]
    public void Non_blocking_events_do_not_warn_they_cannot_stop_anything_anyway()
    {
        Assert.Null(HookValidation.Warn(Draft(h => h with { Event = HookEvents.PostToolUse, Matcher = "Bash(rm:*)" })));
        Assert.Null(HookValidation.Warn(Draft(h => h with { Event = HookEvents.Stop, Matcher = "Bash(rm:*)" })));
    }

    [Fact]
    public void A_bare_tool_name_and_an_exact_matcher_do_not_warn()
    {
        Assert.Null(HookValidation.Warn(Draft(h => h with { Matcher = "Bash" })));
        Assert.Null(HookValidation.Warn(Draft(h => h with { Matcher = "Write(src/a.ts)" })));
    }

    // ───────────────────────── prompt 型 ─────────────────────────

    private static HookDefinition PromptDraft(string prompt = "Is it done?", string? command = null) =>
        new() { Id = "h", Event = HookEvents.Stop, Kind = HookKind.Prompt, Prompt = prompt, Command = command, TimeoutMs = 30_000 };

    [Fact]
    public void An_empty_prompt_is_refused_whether_or_not_a_command_is_filled_in()
    {
        Assert.Equal(HookErrorKeys.EmptyPrompt, HookValidation.Validate(PromptDraft("  ", command: "echo hi")));
    }

    [Fact]
    public void A_prompt_hook_does_not_check_the_command_because_it_never_runs_one()
    {
        Assert.Null(HookValidation.Validate(PromptDraft()));
    }

    [Fact]
    public void Oversized_text_is_refused()
    {
        Assert.Equal(HookErrorKeys.TooLong, HookValidation.Validate(PromptDraft(new string('x', HookLimits.PromptMax + 1))));
        Assert.Equal(HookErrorKeys.TooLong, HookValidation.Validate(Draft(h => h with { Command = new string('x', HookLimits.CommandMax + 1) })));
    }

    [Fact]
    public void Default_timeouts_are_per_event_and_per_kind()
    {
        Assert.Equal(30_000, HookLimits.DefaultTimeout(HookEvents.Stop, HookKind.Prompt));
        Assert.Equal(60_000, HookLimits.DefaultTimeout(HookEvents.Stop, HookKind.Command));
        Assert.Equal(10_000, HookLimits.DefaultTimeout(HookEvents.PreToolUse, HookKind.Command));
    }

    [Theory]
    [InlineData("1BAD")]
    [InlineData("NCW_RUN_ID")]
    [InlineData("has space")]
    public void Environment_names_must_be_valid_and_not_use_the_reserved_prefix(string name)
    {
        var env = new Dictionary<string, string> { [name] = "v" };

        Assert.Equal(HookErrorKeys.BadEnv, HookValidation.Validate(Draft(h => h with { Env = env })));
    }

    [Fact]
    public void Environment_values_have_a_size_cap_and_no_nul()
    {
        Assert.Equal(HookErrorKeys.BadEnv, HookValidation.Validate(Draft(h => h with { Env = new Dictionary<string, string> { ["A"] = "a\0b" } })));
        Assert.Equal(HookErrorKeys.BadEnv, HookValidation.Validate(Draft(h => h with { Env = new Dictionary<string, string> { ["A"] = new string('x', HookLimits.EnvValueMax + 1) } })));
    }
}
