using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel.Tools;

namespace Ncw.Agent.Extensions.Tests;

/// <summary>
/// <c>PermissionExtension.PreflightActionAsync</c>(<c>widgets.runAction</c> 的预检):和 Run 里同一张表,但不发起交互、不走 AI 审核,
/// 并且<b>非只读的动作第一次一定问</b>(哪怕档位是 full)——widget 是模型写的,按钮背后是它预先写好的命令。
/// </summary>
public sealed class PreflightActionTests
{
    private static readonly FakeTool Read = new("Read", readOnly: true, destructive: false);
    private static readonly FakeTool Bash = new("Bash");
    private static readonly FakeTool WebFetch = new("WebFetch", readOnly: true, destructive: false, needsNetwork: true);

    private static string Cmd(string c) => new System.Text.Json.Nodes.JsonObject { ["command"] = c }.ToJsonString();

    private static async Task<ActionPreflight> Run(Harness h, IAgentTool tool, string json) =>
        await h.Extension.PreflightActionAsync(h.State, new ToolInvocation("a1", tool.Info.InternalId, tool, json.Json()), h.Log, default);

    [Fact]
    public async Task A_read_only_action_is_allowed_in_every_mode_and_never_opens_an_interaction()
    {
        foreach (var mode in PermissionModes.All)
        {
            using var h = new Harness(mode);
            Assert.Equal(ActionPreflightKind.Allow, (await Run(h, Read, """{"file_path":"a"}""")).Kind);
            Assert.Empty(h.Gate.Snapshot().Pending);
        }
    }

    [Theory]
    [InlineData(PermissionMode.Ask)]
    [InlineData(PermissionMode.Auto)]
    [InlineData(PermissionMode.Full)]
    public async Task A_non_read_only_action_asks_first_even_in_full_mode(PermissionMode mode)
    {
        using var h = new Harness(mode);
        Assert.Equal(ActionPreflightKind.Ask, (await Run(h, Bash, Cmd("echo hi"))).Kind);
        Assert.Empty(h.Gate.Snapshot().Pending); // 问的事交给调用方(控制端画确认框),闸门里不挂交互
    }

    [Fact]
    public async Task An_explicit_allow_rule_is_the_only_thing_that_lets_a_non_read_only_action_skip_the_question()
    {
        using var h = new Harness(PermissionMode.Full);
        await h.Book.AddAsync(RememberScope.Session, h.State.RuleContext, RuleBucket.Allow, "Bash(echo:*)", default);
        Assert.Equal(ActionPreflightKind.Allow, (await Run(h, Bash, Cmd("echo hi"))).Kind);
        Assert.Equal(ActionPreflightKind.Ask, (await Run(h, Bash, Cmd("rm -rf build"))).Kind);
    }

    [Fact]
    public async Task A_hook_allow_does_not_count_as_the_users_blessing()
    {
        using var h = new Harness(PermissionMode.Full, hooks: [new FakeHook(new HookVerdict(Allow: true))]);
        Assert.Equal(ActionPreflightKind.Ask, (await Run(h, Bash, Cmd("echo hi"))).Kind);
    }

    [Fact]
    public async Task A_deny_rule_denies_and_no_hook_runs()
    {
        var hook = new FakeHook(new HookVerdict(Allow: true));
        using var h = new Harness(PermissionMode.Full, hooks: [hook]);
        await h.Book.AddAsync(RememberScope.Global, h.State.RuleContext, RuleBucket.Deny, "Bash(rm:*)", default);
        var verdict = await Run(h, Bash, Cmd("rm -rf build"));
        Assert.Equal(ActionPreflightKind.Deny, verdict.Kind);
        Assert.Contains("Bash(rm:*)", verdict.Reason);
        Assert.Equal(0, hook.Calls);
    }

    [Fact]
    public async Task A_hook_deny_wins_and_a_hook_ask_forces_the_question_even_for_a_read_only_action()
    {
        using (var h = new Harness(hooks: [new FakeHook(new HookVerdict(Deny: "blocked by policy"))]))
        {
            var verdict = await Run(h, Read, """{"file_path":"a"}""");
            Assert.Equal((ActionPreflightKind.Deny, "blocked by policy"), (verdict.Kind, verdict.Reason));
        }

        using var asking = new Harness(hooks: [new FakeHook(new HookVerdict(Ask: true))]);
        Assert.Equal(ActionPreflightKind.Ask, (await Run(asking, Read, """{"file_path":"a"}""")).Kind);
    }

    [Fact]
    public async Task The_network_switch_denies_in_every_mode()
    {
        using var h = new Harness(PermissionMode.Full, webSearch: false);
        var verdict = await Run(h, WebFetch, """{"url":"https://example.com"}""");
        Assert.Equal(ActionPreflightKind.Deny, verdict.Kind);
        Assert.Contains("curl", verdict.Reason);
    }

    [Fact]
    public async Task A_failing_hook_abstains_and_the_action_is_still_judged_by_the_table()
    {
        using var h = new Harness(hooks: [new FakeHook { Throws = new InvalidOperationException("boom") }]);
        Assert.Equal(ActionPreflightKind.Allow, (await Run(h, Read, """{"file_path":"a"}""")).Kind);
    }
}
