using Ncw.Agent.Extensions.Permissions;

namespace Ncw.Agent.Extensions.Tests;

/// <summary>
/// 权限判定的<b>顺序</b>(移植自 2.x <c>permission-decision.test.ts</c>)。
/// ★ 最重要的一条是「deny 压得过 hook 的 allow」——钩子是唯一能把权限模型放宽的路径,放宽得不对不会报错、不会崩,只会在某一天变成一次本不该发生的执行。
/// </summary>
public sealed class PermissionDecisionTests
{
    private static readonly GateOutcome Allow = GateOutcome.Allow;
    private static readonly GateOutcome Ask = GateOutcome.Ask;
    private static readonly GateOutcome NetDeny = GateOutcome.Deny("network is off");

    private static FinalDecision Decide(GateOutcome? gate = null, HookVerdict hook = default, string? askRule = null, string? allowRule = null, bool autoReview = false) =>
        PermissionDecisions.DecideAfterHooks(gate ?? Ask, hook, askRule, allowRule, autoReview);

    [Fact]
    public void The_network_switch_comes_first_even_an_allow_rule_must_not_reopen_a_switch_the_user_closed()
    {
        var d = PermissionDecisions.DecideBeforeHooks(NetDeny, null);
        Assert.True(d.Denied);
        Assert.Equal("network is off", d.Reason);
    }

    [Fact]
    public void A_deny_rule_denies_directly_so_hooks_never_run()
    {
        // 这一条的「证明」在类型里:调用方拿到 Denied 就返回了,物理上到不了跑钩子那步(扩展里的 Hooks 只在 DecideBeforeHooks 之后才被调用,有端到端测试盯着)。
        var d = PermissionDecisions.DecideBeforeHooks(Allow, "Bash(rm:*)");
        Assert.True(d.Denied);
        Assert.Contains("Bash(rm:*)", d.Reason);
    }

    [Fact]
    public void Nothing_matched_continues()
    {
        Assert.False(PermissionDecisions.DecideBeforeHooks(Allow, null).Denied);
        Assert.False(PermissionDecisions.DecideBeforeHooks(Ask, null).Denied);
    }

    [Fact]
    public void A_hook_deny_denies_with_its_reason()
    {
        var d = Decide(hook: new HookVerdict(Deny: "touches prod"));
        Assert.Equal(new FinalDecision(FinalKind.Deny, "touches prod"), d);
    }

    [Fact]
    public void A_hook_deny_beats_the_allow_bucket_otherwise_one_click_on_always_allow_bypasses_every_safety_hook_forever()
    {
        Assert.Equal(FinalKind.Deny, Decide(hook: new HookVerdict(Deny: "stop"), allowRule: "Bash(git status:*)").Kind);
    }

    [Fact]
    public void A_hook_deny_beats_a_mode_allow()
    {
        Assert.Equal(FinalKind.Deny, Decide(Allow, new HookVerdict(Deny: "stop")).Kind);
    }

    [Fact]
    public void The_ask_bucket_beats_the_allow_bucket_and_the_mode()
    {
        Assert.Equal(FinalKind.Prompt, Decide(Allow, askRule: "Bash", allowRule: "Bash").Kind);
    }

    [Fact]
    public void A_hook_ask_forces_a_prompt_even_when_the_mode_allows()
    {
        Assert.Equal(FinalKind.Prompt, Decide(Allow, new HookVerdict(Ask: true)).Kind);
    }

    [Fact]
    public void A_hook_allow_can_pass_a_call_that_would_have_prompted()
    {
        Assert.Equal(FinalKind.Allow, Decide(Ask, new HookVerdict(Allow: true)).Kind);
    }

    [Fact]
    public void Without_hooks_a_mode_allow_allows()
    {
        Assert.Equal(FinalKind.Allow, Decide(Allow).Kind);
    }

    [Fact]
    public void Without_hooks_an_allow_bucket_hit_allows()
    {
        Assert.Equal(FinalKind.Allow, Decide(allowRule: "Bash(git status:*)").Kind);
    }

    [Fact]
    public void Auto_plus_destructive_goes_to_the_AI_reviewer()
    {
        Assert.Equal(FinalKind.Review, Decide(autoReview: true).Kind);
    }

    [Fact]
    public void A_forced_prompt_skips_the_AI_review_the_user_asked_to_look_so_a_model_must_not_do_it_for_them()
    {
        Assert.Equal(FinalKind.Prompt, Decide(autoReview: true, askRule: "Bash").Kind);
        Assert.Equal(FinalKind.Prompt, Decide(autoReview: true, hook: new HookVerdict(Ask: true)).Kind);
    }

    [Fact]
    public void Nothing_matched_prompts()
    {
        Assert.Equal(FinalKind.Prompt, Decide().Kind);
    }
}
