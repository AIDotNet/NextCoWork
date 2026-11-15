using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Tools;

namespace Ncw.Agent.Extensions.Tests;

/// <summary>权限扩展的决策链(03 §3 的 11 步),直接对 <c>before_tool</c> 闸门提问。每条 ★ 不变量一个用例。</summary>
public sealed class PermissionExtensionTests
{
    private static readonly FakeTool Read = new("Read", readOnly: true, destructive: false);
    private static readonly FakeTool Write = new("Write");
    private static readonly FakeTool Bash = new("Bash");
    private static readonly FakeTool WebFetch = new("WebFetch", readOnly: true, destructive: false, needsNetwork: true);

    private static string Cmd(string c) => new System.Text.Json.Nodes.JsonObject { ["command"] = c }.ToJsonString();

    private static void AssertAllow(PreflightDecision d) => Assert.IsType<PreflightDecision.AllowDecision>(d);

    [Fact]
    public async Task Extension_is_first_in_order_and_cannot_be_disabled_by_ordering()
    {
        using var h = new Harness();
        Assert.Equal(int.MinValue, h.Extension.Order);
        Assert.Equal("permissions", h.Extension.Id);
    }

    [Fact]
    public async Task Read_only_passes_in_every_mode_without_asking()
    {
        foreach (var mode in PermissionModes.All)
        {
            using var h = new Harness(mode);
            AssertAllow(await h.Decide(Read, """{"file_path":"a"}"""));
            Assert.Empty(h.Gate.Snapshot().Pending);
        }
    }

    [Fact]
    public async Task Ask_mode_prompts_with_the_environment_and_a_suggested_rule_and_allow_once_proceeds()
    {
        using var h = new Harness();
        var t = h.Decide(Bash, Cmd("git status"));
        var p = await h.NextPending();
        Assert.Equal("dev-1", p.Env.DeviceId);
        Assert.Equal("local", p.Env.Environment);
        Assert.Equal("Bash", p.InternalId);
        Assert.Equal("Bash(git status:*)", p.SuggestedRule);
        Assert.True(p.AllowedScopes.HasFlag(RememberScopes.Project));
        h.Respond(p, PermissionChoice.AllowOnce);
        AssertAllow(await t);
    }

    [Fact]
    public async Task Deny_reaches_the_model_with_the_users_reason()
    {
        using var h = new Harness();
        var t = h.Decide(Write, """{"file_path":"a","content":"x"}""");
        h.Respond(await h.NextPending(), PermissionChoice.Deny("not that file"));
        var reason = Harness.Deny(await t);
        Assert.Contains("denied", reason);
        Assert.Contains("not that file", reason);
    }

    [Fact]
    public async Task Auto_mode_allows_non_destructive_writes_and_asks_for_destructive()
    {
        using var h = new Harness(PermissionMode.Auto);
        AssertAllow(await h.Decide(new FakeTool("Mkdir", destructive: false), "{}"));
        var t = h.Decide(Write, """{"file_path":"a"}""");
        await h.NextPending();
        h.Interactions.End(RunStatus.Aborted);
        Assert.IsType<PreflightDecision.DenyDecision>(await t); // run 结束:等待以取消收尾,扩展把它按「失败即关闭」变成拒绝
    }

    [Fact]
    public async Task Full_mode_allows_but_the_network_switch_still_denies_and_closes_the_curl_detour()
    {
        using var h = new Harness(PermissionMode.Full, webSearch: false);
        AssertAllow(await h.Decide(Bash, Cmd("echo hi")));
        var reason = Harness.Deny(await h.Decide(WebFetch, """{"url":"https://example.com"}"""));
        Assert.Contains("curl", reason);
    }

    [Fact]
    public async Task An_allow_rule_cannot_reopen_the_network_switch()
    {
        using var h = new Harness(PermissionMode.Full, webSearch: false);
        await h.Book.AddAsync(RememberScope.Session, h.State.RuleContext, RuleBucket.Allow, "WebFetch", default);
        Assert.IsType<PreflightDecision.DenyDecision>(await h.Decide(WebFetch, """{"url":"https://example.com"}"""));
    }

    [Fact]
    public async Task Deny_rule_wins_even_in_full_mode_and_hooks_never_run()
    {
        var hook = new FakeHook(new HookVerdict(Allow: true));
        var icpt = new FakeInterceptor();
        using var h = new Harness(PermissionMode.Full, hooks: [hook], interceptors: [icpt]);
        await h.Book.AddAsync(RememberScope.Global, h.State.RuleContext, RuleBucket.Deny, "Bash(rm:*)", default);
        var d = await h.Decide(Bash, Cmd("rm -rf build"));
        Assert.Contains("Bash(rm:*)", Harness.Deny(d));
        Assert.Equal(0, hook.Calls);
        Assert.Equal(0, icpt.Calls);
    }

    [Fact]
    public async Task A_deny_rule_is_not_bypassed_by_padding_with_another_command_or_wrappers()
    {
        using var h = new Harness(PermissionMode.Full);
        await h.Book.AddAsync(RememberScope.Session, h.State.RuleContext, RuleBucket.Deny, "Bash(rm:*)", default);
        foreach (var c in new[] { "echo hi && rm -rf build", "sudo rm -rf build", "bash -c 'rm -rf build'", "echo $(rm -rf build)", "FOO=1 rm x" })
        {
            Assert.IsType<PreflightDecision.DenyDecision>(await h.Decide(Bash, Cmd(c)));
        }

        AssertAllow(await h.Decide(Bash, Cmd("echo fine")));
    }

    [Fact]
    public async Task Hook_deny_beats_an_allow_rule_and_a_mode_allow()
    {
        var hook = new FakeHook(new HookVerdict(Deny: "blocked by hook"));
        using var h = new Harness(PermissionMode.Full, hooks: [hook]);
        await h.Book.AddAsync(RememberScope.Session, h.State.RuleContext, RuleBucket.Allow, "Bash", default);
        Assert.Equal("blocked by hook", Harness.Deny(await h.Decide(Bash, Cmd("ls"))));
    }

    [Fact]
    public async Task Hook_ask_forces_a_prompt_even_in_full_mode()
    {
        using var h = new Harness(PermissionMode.Full, hooks: [new FakeHook(new HookVerdict(Ask: true))]);
        var t = h.Decide(Bash, Cmd("ls"));
        h.Respond(await h.NextPending(), PermissionChoice.AllowOnce);
        AssertAllow(await t);
    }

    [Fact]
    public async Task Hook_allow_passes_a_call_that_would_have_prompted()
    {
        using var h = new Harness(hooks: [new FakeHook(new HookVerdict(Allow: true))]);
        AssertAllow(await h.Decide(Bash, Cmd("ls")));
        Assert.Empty(h.Gate.Snapshot().Pending);
    }

    [Fact]
    public async Task An_ask_rule_beats_an_allow_rule_and_the_mode()
    {
        using var h = new Harness(PermissionMode.Full);
        await h.Book.AddAsync(RememberScope.Session, h.State.RuleContext, RuleBucket.Allow, "Bash", default);
        await h.Book.AddAsync(RememberScope.Session, h.State.RuleContext, RuleBucket.Ask, "Bash(ls:*)", default);
        var t = h.Decide(Bash, Cmd("ls"));
        h.Respond(await h.NextPending(), PermissionChoice.Deny());
        Assert.IsType<PreflightDecision.DenyDecision>(await t);
    }

    [Fact]
    public async Task A_plugin_interceptor_can_only_tighten_its_allow_does_not_exist_and_its_ask_forces_a_prompt()
    {
        using var h = new Harness(PermissionMode.Full, interceptors: [new FakeInterceptor(new InterceptorVerdict(Ask: true))]);
        var t = h.Decide(Bash, Cmd("ls"));
        h.Respond(await h.NextPending(), PermissionChoice.AllowOnce);
        AssertAllow(await t);

        using var h2 = new Harness(interceptors: [new FakeInterceptor(new InterceptorVerdict())]);
        var t2 = h2.Decide(Bash, Cmd("ls")); // 弃权 ≠ 放行:仍然要问
        await h2.NextPending();
        h2.Interactions.End(RunStatus.Aborted);
        Assert.IsType<PreflightDecision.DenyDecision>(await t2); // run 结束:等待以取消收尾,扩展把它按「失败即关闭」变成拒绝
    }

    [Fact]
    public async Task Interceptor_deny_denies()
    {
        using var h = new Harness(PermissionMode.Full, interceptors: [new FakeInterceptor(new InterceptorVerdict(Deny: "plugin says no"))]);
        Assert.Equal("plugin says no", Harness.Deny(await h.Decide(Bash, Cmd("ls"))));
    }

    [Fact]
    public async Task A_failing_hook_abstains_but_a_failing_permission_check_denies()
    {
        var hook = new FakeHook { Throws = new InvalidOperationException("hook crashed") };
        using var h = new Harness(PermissionMode.Full, hooks: [hook]);
        AssertAllow(await h.Decide(Bash, Cmd("ls")));
        Assert.NotEmpty(h.Log.Warnings);

        using var h2 = new Harness(PermissionMode.Full);
        h2.Runs.Remove(h2.RunId); // 没有登记权限状态 = 不知道档位和环境 = 失败即关闭
        Assert.Contains("no permission state", Harness.Deny(await h2.Decide(Bash, Cmd("ls"))));
    }

    [Fact]
    public async Task Exceptions_inside_the_permission_extension_are_a_denial()
    {
        using var h = new Harness(PermissionMode.Full, global: new ThrowingStore());
        Assert.Contains("permission check failed", Harness.Deny(await h.Decide(Bash, Cmd("ls"))));
    }

    [Fact]
    public async Task Auto_destructive_goes_to_the_AI_reviewer_allow_and_deny_are_honored()
    {
        var reviewer = new FakeReviewer(ReviewResult.Allow);
        using var h = new Harness(PermissionMode.Auto, reviewer: reviewer);
        AssertAllow(await h.Decide(Bash, Cmd("npm install")));
        reviewer.Result = ReviewResult.Deny;
        Assert.Contains("AI reviewer", Harness.Deny(await h.Decide(Bash, Cmd("npm install"))));
        Assert.Equal(2, reviewer.Calls);
        Assert.Empty(h.Gate.Snapshot().Pending);
    }

    [Fact]
    public async Task An_unknown_or_crashing_reviewer_falls_back_to_a_human_never_to_allow()
    {
        var reviewer = new FakeReviewer(ReviewResult.Unknown);
        using var h = new Harness(PermissionMode.Auto, reviewer: reviewer);
        var t = h.Decide(Bash, Cmd("npm install"));
        h.Respond(await h.NextPending(), PermissionChoice.Deny());
        Assert.IsType<PreflightDecision.DenyDecision>(await t);

        reviewer.Throws = new InvalidOperationException("down");
        var t2 = h.Decide(Bash, Cmd("npm install"), "c2");
        h.Respond(await h.NextPending(), PermissionChoice.Deny());
        Assert.IsType<PreflightDecision.DenyDecision>(await t2);
    }

    [Fact]
    public async Task Auto_does_not_send_dangerous_commands_to_the_reviewer_and_a_forced_ask_skips_it_too()
    {
        var reviewer = new FakeReviewer(ReviewResult.Allow);
        using var h = new Harness(PermissionMode.Auto, reviewer: reviewer);
        var t = h.Decide(Bash, Cmd("rm -rf build"));
        var p = await h.NextPending();
        Assert.Equal(CommandRisk.Dangerous, p.Risk);
        Assert.Equal("Bash(rm -rf build)", p.SuggestedRule); // 危险命令只建议原样命令,不给 rm:* 前缀
        Assert.Equal(0, reviewer.Calls);
        h.Respond(p, PermissionChoice.Deny());
        await t;

        await h.Book.AddAsync(RememberScope.Session, h.State.RuleContext, RuleBucket.Ask, "Bash(ls:*)", default);
        var t2 = h.Decide(Bash, Cmd("ls"), "c2");
        await h.NextPending();
        Assert.Equal(0, reviewer.Calls);
        h.Interactions.End(RunStatus.Aborted);
        Assert.IsType<PreflightDecision.DenyDecision>(await t2); // run 结束:等待以取消收尾,扩展把它按「失败即关闭」变成拒绝
    }

    [Fact]
    public async Task A_subagent_is_denied_on_the_spot_with_the_rule_it_could_be_given_and_nothing_is_pending()
    {
        using var h = new Harness();
        var childRun = new RunId("child");
        var child = h.State.ForSubagent(childRun, h.Gate.OpenRun(childRun, 1), PermissionMode.Auto);
        h.Runs.Register(child);
        var reason = Harness.Deny(await h.Decide(Bash, Cmd("git status"), run: childRun));
        Assert.Contains("subagent cannot ask", reason);
        Assert.Contains("Bash(git status:*)", reason);
        Assert.Empty(h.Gate.Snapshot().Pending);
    }

    [Fact]
    public async Task A_subagent_can_still_run_what_an_allow_rule_covers_and_read_only_tools()
    {
        using var h = new Harness();
        await h.Book.AddAsync(RememberScope.Session, h.State.RuleContext, RuleBucket.Allow, "Bash(git status:*)", default);
        var childRun = new RunId("child");
        h.Runs.Register(h.State.ForSubagent(childRun, h.Gate.OpenRun(childRun, 1), PermissionMode.Full));
        AssertAllow(await h.Decide(Bash, Cmd("git status"), run: childRun));
        AssertAllow(await h.Decide(Read, """{"file_path":"a"}""", run: childRun));
    }

    [Fact]
    public void A_subagents_mode_is_the_min_of_the_parents_live_mode_and_its_definition_and_cannot_be_raised()
    {
        using var h = new Harness(PermissionMode.Full);
        var childRun = new RunId("child");
        var child = h.State.ForSubagent(childRun, h.Gate.OpenRun(childRun, 1), PermissionMode.Auto);
        Assert.Equal(PermissionMode.Auto, child.Mode);
        h.State.Mode = PermissionMode.Ask;              // 父中途调低,子立刻跟着降
        Assert.Equal(PermissionMode.Ask, child.Mode);
        h.State.Mode = PermissionMode.Full;
        child.Mode = PermissionMode.Full;               // 子永远不能被调到比定义更宽
        Assert.Equal(PermissionMode.Auto, child.Mode);
        Assert.Equal(PermissionMode.Auto, child.StartMode);
        Assert.Throws<ArgumentException>(() => h.State.ForSubagent(new RunId("x"), h.Gate.OpenRun(new RunId("x"), 0), PermissionMode.Ask));
    }

    [Fact]
    public async Task The_mode_is_live_changing_it_mid_run_takes_effect_on_the_next_call()
    {
        using var h = new Harness(PermissionMode.Ask);
        var t = h.Decide(Bash, Cmd("ls"));
        await h.NextPending();
        h.Interactions.End(RunStatus.Aborted);
        Assert.IsType<PreflightDecision.DenyDecision>(await t); // run 结束:等待以取消收尾,扩展把它按「失败即关闭」变成拒绝

        using var h2 = new Harness(PermissionMode.Ask);
        h2.State.Mode = PermissionMode.Full;
        AssertAllow(await h2.Decide(Bash, Cmd("ls")));
        Assert.Equal(PermissionMode.Ask, h2.State.StartMode);
    }

    // ── 记住本次选择 ──

    [Fact]
    public async Task Allow_always_session_is_remembered_in_memory_only()
    {
        using var h = new Harness();
        var t = h.Decide(Bash, Cmd("git status"));
        h.Respond(await h.NextPending(), PermissionChoice.AllowAlways(RememberScope.Session));
        AssertAllow(await t);
        AssertAllow(await h.Decide(Bash, Cmd("git status --short"), "c2")); // 不再问
        Assert.Null(h.Fx.ReadSettings());
        Assert.Empty(h.Gate.Snapshot().Pending);
    }

    [Fact]
    public async Task Allow_always_project_writes_settings_local_json_and_survives_a_new_session()
    {
        using var h = new Harness();
        var t = h.Decide(Bash, Cmd("npm run build"));
        h.Respond(await h.NextPending(), PermissionChoice.AllowAlways(RememberScope.Project));
        AssertAllow(await t);
        Assert.Contains("Bash(npm run:*)", h.Fx.ReadSettings());
        var other = new RuleStoreContext("another-session", h.Fx.Env, h.State.Root);
        Assert.Contains("Bash(npm run:*)", (await h.Book.ReadAsync(other, default)).Allow);
    }

    [Fact]
    public async Task Allow_always_global_goes_to_the_injected_permanent_store()
    {
        var global = MemoryRuleStore.Process();
        using var h = new Harness(global: global);
        var t = h.Decide(Bash, Cmd("git status"));
        h.Respond(await h.NextPending(), PermissionChoice.AllowAlways(RememberScope.Global));
        AssertAllow(await t);
        Assert.Equal(["Bash(git status:*)"], (await global.ReadAsync(h.State.RuleContext, default)).Allow);
        Assert.Null(h.Fx.ReadSettings());
    }

    [Fact]
    public async Task Without_a_workspace_the_project_scope_is_not_offered()
    {
        using var h = new Harness(withRoot: false);
        var t = h.Decide(Bash, Cmd("ls"));
        var p = await h.NextPending();
        Assert.False(p.AllowedScopes.HasFlag(RememberScopes.Project));
        Assert.IsType<RespondResult.Invalid>(h.Respond(p, PermissionChoice.AllowAlways(RememberScope.Project)));
        h.Respond(p, PermissionChoice.AllowOnce);
        await t;
    }

    [Fact]
    public async Task A_failed_write_does_not_turn_the_click_on_allow_into_a_denial()
    {
        using var h = new Harness();
        h.Fx.PutSettings("{ not json"); // 读得了(读为空)、写会被拒
        var t = h.Decide(Bash, Cmd("git status"));
        h.Respond(await h.NextPending(), PermissionChoice.AllowAlways(RememberScope.Project));
        AssertAllow(await t);
        Assert.Equal("{ not json", h.Fx.ReadSettings());
        Assert.Contains(h.Log.Warnings, w => w.Message.Contains("could not remember"));
    }

    // ── allow_edited ──

    [Fact]
    public async Task Allow_edited_returns_the_edited_input_and_does_not_prompt_again()
    {
        using var h = new Harness();
        var t = h.Decide(Bash, Cmd("npm install"));
        h.Respond(await h.NextPending(), PermissionChoice.AllowEdited(Cmd("npm ci").Json()));
        var edit = Assert.IsType<PreflightDecision.EditDecision>(await t);
        Assert.Equal("npm ci", edit.Input.GetProperty("command").GetString());
        Assert.Empty(h.Gate.Snapshot().Pending);
    }

    [Fact]
    public async Task Allow_edited_is_rechecked_against_deny_rules_the_2x_hole()
    {
        using var h = new Harness();
        await h.Book.AddAsync(RememberScope.Session, h.State.RuleContext, RuleBucket.Deny, "Bash(rm:*)", default);
        var t = h.Decide(Bash, Cmd("npm install"));
        h.Respond(await h.NextPending(), PermissionChoice.AllowEdited(Cmd("echo hi; rm -rf /").Json()));
        var reason = Harness.Deny(await t);
        Assert.Contains("edited call is not permitted", reason);
        Assert.Contains("Bash(rm:*)", reason);
    }

    [Fact]
    public async Task Allow_edited_is_rechecked_against_hook_denials_and_the_network_switch()
    {
        var hook = new FakeHook();
        using var h = new Harness(hooks: [hook], webSearch: false);
        var t = h.Decide(Bash, Cmd("ls"));
        hook.Verdict = new HookVerdict(Deny: "edited input is bad");
        h.Respond(await h.NextPending(), PermissionChoice.AllowEdited(Cmd("ls -R").Json()));
        Assert.Equal("edited input is bad", Harness.Deny(await t));
        Assert.Equal("ls -R", hook.Inputs[^1].GetProperty("command").GetString()); // 钩子对改写后的参数再判了一次
    }

    // ── 路径边界 / 规则的规范化匹配 ──

    [Fact]
    public async Task An_allow_rule_matches_the_canonical_path_so_dot_dot_cannot_escape_it()
    {
        using var h = new Harness();
        await h.Book.AddAsync(RememberScope.Session, h.State.RuleContext, RuleBucket.Allow, "Write(src/*)", default);
        h.Fx.Put("src/a.txt", "x");
        AssertAllow(await h.Decide(Write, """{"file_path":"src/a.txt","content":"x"}"""));
        AssertAllow(await h.Decide(Write, new System.Text.Json.Nodes.JsonObject { ["file_path"] = h.Fx.Abs("src/a.txt") }.ToJsonString(), "c2")); // 绝对写法同一个文件
        // 2.x 里原始字符串 `src/../../outside/x` 能被 `src/*` 匹配上;规范化之后落在根外,不匹配,要问
        var t = h.Decide(Write, """{"file_path":"src/../../outside/x","content":"x"}""", "c3");
        var p = await h.NextPending();
        Assert.True(p.OutsideWorkspace);
        h.Interactions.End(RunStatus.Aborted);
        Assert.IsType<PreflightDecision.DenyDecision>(await t); // run 结束:等待以取消收尾,扩展把它按「失败即关闭」变成拒绝
    }

    [Fact]
    public async Task A_deny_rule_matches_both_the_raw_and_the_canonical_spelling()
    {
        using var h = new Harness(PermissionMode.Full);
        await h.Book.AddAsync(RememberScope.Session, h.State.RuleContext, RuleBucket.Deny, "Write(secrets/*)", default);
        Assert.IsType<PreflightDecision.DenyDecision>(await h.Decide(Write, """{"file_path":"./secrets/k","content":"x"}"""));
        Assert.IsType<PreflightDecision.DenyDecision>(await h.Decide(Write, """{"file_path":"a/../secrets/k","content":"x"}"""));
        Assert.IsType<PreflightDecision.DenyDecision>(await h.Decide(Write, new System.Text.Json.Nodes.JsonObject { ["file_path"] = h.Fx.Abs("secrets/k") }.ToJsonString()));
        AssertAllow(await h.Decide(Write, """{"file_path":"public/k","content":"x"}"""));
    }

    [Fact]
    public async Task Outside_the_project_is_flagged_on_the_approval_and_inside_is_not()
    {
        using var h = new Harness();
        var t = h.Decide(Write, new System.Text.Json.Nodes.JsonObject { ["file_path"] = Path.Combine(h.Fx.Outside, "x.txt") }.ToJsonString());
        Assert.True((await h.NextPending()).OutsideWorkspace);
        h.Interactions.End(RunStatus.Aborted);
        Assert.IsType<PreflightDecision.DenyDecision>(await t); // run 结束:等待以取消收尾,扩展把它按「失败即关闭」变成拒绝

        using var h2 = new Harness();
        var t2 = h2.Decide(Write, """{"file_path":"a.txt"}""");
        Assert.False((await h2.NextPending()).OutsideWorkspace);
        h2.Interactions.End(RunStatus.Aborted);
        Assert.IsType<PreflightDecision.DenyDecision>(await t2); // run 结束:等待以取消收尾,扩展把它按「失败即关闭」变成拒绝
    }

    [Fact]
    public async Task A_suggested_path_rule_is_root_relative_and_matches_its_own_call()
    {
        using var h = new Harness();
        var t = h.Decide(Write, new System.Text.Json.Nodes.JsonObject { ["file_path"] = h.Fx.Abs("src/a.ts") }.ToJsonString());
        var p = await h.NextPending();
        Assert.Equal("Write(src/a.ts)", p.SuggestedRule);
        h.Respond(p, PermissionChoice.AllowAlways(RememberScope.Session));
        await t;
        AssertAllow(await h.Decide(Write, """{"file_path":"./src/a.ts"}""", "c2"));
    }

    // ── 计划模式写入围栏 ──

    [Fact]
    public async Task Plan_mode_trusts_Write_and_Edit_only_for_the_fenced_plan_file()
    {
        using var h = new Harness();
        h.State.PlanFence = h.Fx.P(".plan/p1.md");
        AssertAllow(await h.Decide(Write, """{"file_path":".plan/p1.md","content":"# plan"}"""));
        AssertAllow(await h.Decide(new FakeTool("Edit"), new System.Text.Json.Nodes.JsonObject { ["file_path"] = h.Fx.Abs(".plan/p1.md") }.ToJsonString(), "c2"));
        // 围栏外的文件:不是只读,照常问(写工具自己也会拒绝)
        var t = h.Decide(Write, """{"file_path":"src/a.ts","content":"x"}""", "c3");
        await h.NextPending();
        h.Interactions.End(RunStatus.Aborted);
        Assert.IsType<PreflightDecision.DenyDecision>(await t); // run 结束:等待以取消收尾,扩展把它按「失败即关闭」变成拒绝
    }

    [Fact]
    public async Task Without_a_fence_Write_is_not_trusted_even_if_the_plan_workflow_is_on()
    {
        using var h = new Harness();
        var t = h.Decide(Write, """{"file_path":".plan/p1.md","content":"x"}""");
        await h.NextPending();
        h.Interactions.End(RunStatus.Aborted);
        Assert.IsType<PreflightDecision.DenyDecision>(await t); // run 结束:等待以取消收尾,扩展把它按「失败即关闭」变成拒绝
    }

    [Fact]
    public async Task EnterPlanMode_and_ExitPlanMode_are_trusted_only_when_the_plan_workflow_is_enabled()
    {
        using var h = new Harness();
        var enter = new FakeTool("EnterPlanMode", readOnly: false, destructive: false);
        var t = h.Decide(enter, "{}");
        await h.NextPending();
        h.Interactions.End(RunStatus.Aborted);
        Assert.IsType<PreflightDecision.DenyDecision>(await t); // run 结束:等待以取消收尾,扩展把它按「失败即关闭」变成拒绝

        using var h2 = new Harness(); // PlanWorkflow 是 init 属性,这里换一个带开关的状态
        h2.Runs.Remove(h2.RunId);
        var state = new PermissionRunState(h2.RunId, "session-1", h2.Fx.Env, h2.Interactions, PermissionMode.Ask) { Root = h2.State.Root, PlanWorkflow = true, WebSearch = true };
        h2.Runs.Register(state);
        AssertAllow(await h2.Decide(enter, "{}"));
        AssertAllow(await h2.Decide(new FakeTool("ExitPlanMode", readOnly: false, destructive: false), "{}", "c2"));
    }

    [Fact]
    public void The_fence_comes_from_the_same_ToolRuntime_the_write_tool_uses()
    {
        using var fx = new Fixture();
        var gate = new InteractionGate(new SequentialIds());
        var rt = new Ncw.Agent.Tools.ToolRuntime { Environment = fx.Env, Root = fx.Runtime.Root, WriteFence = fx.P(".plan/p.md") };
        var state = PermissionRunState.FromRuntime(rt, new RunId("r"), "s", gate.OpenRun(new RunId("r")), PermissionMode.Ask);
        Assert.Equal(rt.WriteFence, state.PlanFence);
        Assert.Equal(rt.Root, state.Root);
        Assert.Same(rt.Environment, state.Environment);
    }

    // ── 系统提示词的权限事实 ──

    [Fact]
    public async Task The_system_section_is_generated_by_the_same_function_as_the_gate_and_follows_the_live_mode()
    {
        using var h = new Harness(PermissionMode.Ask, webSearch: false);
        var section = h.Host.CollectSystemSections(new SessionInfo("session-1", "w", 0, null), h.Log).Single();
        Assert.Equal("permissions", section.Name);
        Assert.Equal(PermissionGate.Facts(PermissionMode.Ask, false), section.Text);
        h.State.Mode = PermissionMode.Full;
        Assert.Equal(PermissionGate.Facts(PermissionMode.Full, false), h.Host.CollectSystemSections(new SessionInfo("session-1", "w", 0, null), h.Log).Single().Text);
        Assert.Empty(h.Host.CollectSystemSections(new SessionInfo("unknown", "w", 0, null), h.Log));
        await Task.CompletedTask;
    }

    // ── AutoAllowReadOnlyBash(选项,缺省关) ──

    [Fact]
    public async Task Read_only_Bash_is_not_auto_allowed_by_default_but_is_with_the_option()
    {
        using var h = new Harness();
        var t = h.Decide(Bash, Cmd("ls -la"));
        await h.NextPending();
        h.Interactions.End(RunStatus.Aborted);
        Assert.IsType<PreflightDecision.DenyDecision>(await t); // run 结束:等待以取消收尾,扩展把它按「失败即关闭」变成拒绝

        using var h2 = new Harness(options: new PermissionOptions { AutoAllowReadOnlyBash = true });
        AssertAllow(await h2.Decide(Bash, Cmd("ls -la")));
        var t2 = h2.Decide(Bash, Cmd("ls -la > out.txt"), "c2"); // 带重定向不是只读
        await h2.NextPending();
        h2.Interactions.End(RunStatus.Aborted);
        Assert.IsType<PreflightDecision.DenyDecision>(await t2); // run 结束:等待以取消收尾,扩展把它按「失败即关闭」变成拒绝
    }

    // ── 清理 ──

    [Fact]
    public async Task Cancelling_the_run_token_while_waiting_for_the_user_propagates_and_cleans_up()
    {
        using var h = new Harness();
        using var cts = new CancellationTokenSource();
        var t = h.Decide(Bash, Cmd("ls"), ct: cts.Token);
        await h.NextPending();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t); // 令牌触发的取消原样传播,不伪装成拒绝
        Assert.Empty(h.Gate.Snapshot().Pending);
    }

    [Fact]
    public async Task The_run_end_notification_clears_leftovers_and_removes_the_state()
    {
        using var h = new Harness();
        var t = h.Decide(Bash, Cmd("ls"));
        await h.NextPending();
        await h.Host.NotifyAsync(LoopExtensionPoints.RunEnd, new RunEndEvent(RunStatus.Aborted), h.Context(), default);
        Assert.IsType<PreflightDecision.DenyDecision>(await t); // run 结束:等待以取消收尾,扩展把它按「失败即关闭」变成拒绝
        Assert.Empty(h.Gate.Snapshot().Pending);
        Assert.Equal(0, h.Runs.Count);
    }

    private sealed class ThrowingStore : IPermissionRuleStore
    {
        public ValueTask<RuleBuckets> ReadAsync(RuleStoreContext context, CancellationToken ct) => throw new InvalidOperationException("store down");

        public ValueTask<RuleWriteResult> AddAsync(RuleStoreContext context, RuleBucket bucket, string rule, CancellationToken ct) => throw new InvalidOperationException("store down");
    }
}
