using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Planning.Goals;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Core.Domain;

namespace Ncw.Agent.Planning.Tests;

/// <summary>
/// 模型自提目标(<see cref="GoalService.Propose"/>):2.x <c>goal-runtime.test.ts</c> 里 <c>goalProposalsFor</c> 那一组逐条移植。
/// 「只等派发、不等人回答」「提案不占住 Run」「批准条会过期」是这一组钉的三件事。
/// </summary>
public sealed class GoalProposalTests : IDisposable
{
    private const string S = GoalRig.S;
    private const string Condition = GoalRig.Condition;

    private readonly GoalRig _rig = new();
    private readonly RunId _run = new("run-1");

    public GoalProposalTests()
    {
        // 会话得真的在:批准回调会核一次「这条会话还在不在」
        _rig.Transcript.Seed(S, Msg.User("u1", "开始"), Msg.Say("a1", "好"));
        _rig.Fx.Register(_run, S);
    }

    public void Dispose() => _rig.Dispose();

    private GoalService Goals => _rig.Goals;

    private GoalProposal? Pending() => _rig.Fx.Gate.Snapshot().Pending.OfType<GoalProposal>().FirstOrDefault();

    private RespondResult Answer(bool approved)
    {
        var pending = Pending()!;
        return _rig.Fx.Gate.Respond(pending.Id, pending.Version, new GoalProposalResponse(approved), "ctrl");
    }

    private bool CanPropose(int depth = 0, RunId? run = null) => Goals.CanPropose(run ?? _run, S, depth);

    [Fact]
    public void The_five_gates_child_run_non_interactive_planning_disabled_and_already_pending()
    {
        var child = new RunId("child-1");
        _rig.Fx.Register(child, S, depth: 1);
        Assert.False(CanPropose(depth: 1, run: child));
        Assert.Throws<InvalidOperationException>(() => Goals.Propose(child, S, 1, Condition, false));

        _rig.InteractiveSession = false;
        Assert.False(CanPropose());
        _rig.InteractiveSession = true;

        _rig.Planning = true;
        Assert.False(CanPropose());
        _rig.Planning = false;

        _rig.Policy = GoalProposalPolicy.Disabled;
        Assert.False(CanPropose());
        _rig.Policy = GoalProposalPolicy.Auto;

        Assert.True(CanPropose());
        Assert.Equal(GoalProposalStatus.Pending, Goals.Propose(_run, S, 0, Condition, true));
        Assert.False(CanPropose()); // 已有待批
        Assert.Throws<InvalidOperationException>(() => Goals.Propose(_run, S, 0, Condition, true));
    }

    [Fact]
    public void A_run_with_no_interaction_scope_cannot_propose()
    {
        Assert.False(Goals.CanPropose(new RunId("unregistered"), S, 0));
    }

    [Fact]
    public void A_run_that_is_finished_or_already_aborted_cannot_propose()
    {
        _rig.DeadRuns.Add(_run);

        Assert.False(CanPropose());
    }

    [Fact]
    public void Auto_with_the_model_asserting_the_user_said_so_sets_directly_and_sends_the_kickoff_at_once()
    {
        var status = Goals.Propose(_run, S, 0, Condition, askUser: false);

        Assert.Equal(GoalProposalStatus.Set, status);
        Assert.Empty(_rig.Fx.Gate.Snapshot().Pending); // 没有弹窗,一个待决项都没有
        var goal = Goals.Store.Get(S)!;
        Assert.Equal(Condition, goal.Condition);
        Assert.Equal(GoalOrigin.ProposalDirect, goal.Origin);
        Assert.Equal(0, goal.Iterations);
        var (_, input) = Assert.Single(_rig.Delivered);
        Assert.Equal(goal.Id, input.GoalId);
        Assert.Equal(GoalPrompts.KickoffMessage(Condition), GoalRig.TextOf(input.Parts));
    }

    [Fact]
    public void Always_ask_forces_the_approval_even_when_the_model_says_no_need_to_ask()
    {
        _rig.Policy = GoalProposalPolicy.AlwaysAsk;

        var status = Goals.Propose(_run, S, 0, Condition, askUser: false);

        Assert.Equal(GoalProposalStatus.Pending, status);
        var pending = Pending()!;
        Assert.Equal(S, pending.SessionId);
        Assert.Equal(Condition, pending.Condition);
        Assert.Null(Goals.Store.Get(S));
        Assert.Empty(_rig.Delivered);
    }

    [Fact]
    public async Task Only_the_dispatch_is_awaited_pending_returns_before_anyone_answers_and_approval_installs_the_goal()
    {
        _rig.Policy = GoalProposalPolicy.AlwaysAsk;
        Assert.Equal(GoalProposalStatus.Pending, Goals.Propose(_run, S, 0, Condition, askUser: true));
        Assert.Null(Goals.Store.Get(S));
        Assert.Empty(_rig.Delivered);

        Assert.IsType<RespondResult.Accepted>(Answer(true));
        await Wait.UntilAsync(() => _rig.Delivered.Count == 1, what: "the kickoff"); // 目标先装上、kickoff 随后才发

        var goal = Goals.Store.Get(S)!;
        Assert.Equal(GoalOrigin.ProposalApproved, goal.Origin);
        Assert.Equal(0, goal.Iterations);
        var (_, input) = Assert.Single(_rig.Delivered);
        Assert.Equal(goal.Id, input.GoalId);
        Assert.Equal(GoalPrompts.KickoffMessage(Condition), GoalRig.TextOf(input.Parts));
    }

    [Fact]
    public async Task A_declined_proposal_sets_no_goal_and_sends_no_kickoff()
    {
        Goals.Propose(_run, S, 0, Condition, askUser: true);

        Answer(false);
        await Wait.UntilAsync(() => Pending() is null);
        await Task.Delay(50);

        Assert.Null(Goals.Store.Get(S));
        Assert.Empty(_rig.Delivered);
        Assert.Empty(_rig.Fx.Gate.Snapshot().Pending);
    }

    [Fact]
    public async Task A_proposal_does_not_hold_the_run_it_can_still_be_approved_after_the_run_ended_normally()
    {
        Goals.Propose(_run, S, 0, Condition, askUser: true);
        // Run 正常收尾:不是中止。交互闸门对 SurvivesNormalRunEnd 的提案不清理
        _rig.DeadRuns.Add(_run);
        _rig.Fx.Permissions.Get(_run)!.Interactions.End(Ncw.Agent.Kernel.Loop.RunStatus.Done);
        Assert.NotNull(Pending());

        Answer(true);
        await Wait.UntilAsync(() => _rig.Delivered.Count == 1, what: "the kickoff");

        Assert.Equal(GoalOrigin.ProposalApproved, Goals.Store.Get(S)!.Origin);
    }

    [Fact]
    public async Task A_proposal_can_be_approved_after_the_previous_goal_completed_automatically()
    {
        Goals.Activate(S, "previous goal", GoalOrigin.User);
        Goals.Propose(_run, S, 0, Condition, askUser: true);
        Goals.Deactivate(S, GoalClearReason.Met); // 自动完成不让版本前进

        Answer(true);
        await Wait.UntilAsync(() => _rig.Delivered.Count == 1, what: "the kickoff");

        Assert.Equal(Condition, Goals.Store.Get(S)!.Condition);
        Assert.Equal(GoalOrigin.ProposalApproved, Goals.Store.Get(S)!.Origin);
    }

    [Fact]
    public async Task An_approval_that_has_expired_is_ignored_setting_turned_off_the_user_set_and_cleared_a_goal_the_run_aborted_or_the_session_gone()
    {
        // 设置在审批期间被关掉
        Goals.Propose(_run, S, 0, Condition, askUser: true);
        _rig.Policy = GoalProposalPolicy.Disabled;
        Answer(true);
        await Wait.UntilAsync(() => Pending() is null);
        await Task.Delay(50);
        Assert.Null(Goals.Store.Get(S));
        Assert.Empty(_rig.Delivered);
        _rig.Policy = GoalProposalPolicy.Auto;

        // 用户同时手动设了一个目标又清掉:版本号前进,旧批准条作废
        Goals.Propose(_run, S, 0, Condition, askUser: true);
        var pending = Pending()!;
        Goals.Activate(S, "另一个条件", GoalOrigin.User);
        Goals.Deactivate(S, GoalClearReason.UserClear);
        // (用户设定 / 清除会经 SetByUser / Clear 撤掉待决的提案;这里直接走底层,所以提案还在)
        _rig.Fx.Gate.Respond(pending.Id, pending.Version, new GoalProposalResponse(true), "ctrl");
        await Wait.UntilAsync(() => Pending() is null);
        await Task.Delay(50);
        Assert.Null(Goals.Store.Get(S));
        Assert.Empty(_rig.Delivered);

        // Run 被中止
        Goals.Propose(_run, S, 0, Condition, askUser: true);
        _rig.AbortedRuns.Add(_run);
        Answer(true);
        await Wait.UntilAsync(() => Pending() is null);
        await Task.Delay(50);
        Assert.Null(Goals.Store.Get(S));
        _rig.AbortedRuns.Clear();

        // 会话没了
        Goals.Propose(_run, S, 0, Condition, askUser: true);
        _rig.Transcript.Drop(S);
        Answer(true);
        await Wait.UntilAsync(() => Pending() is null);
        await Task.Delay(50);
        Assert.Null(Goals.Store.Get(S));
        Assert.Empty(_rig.Delivered);
    }

    [Fact]
    public async Task Planning_started_during_the_approval_also_voids_it()
    {
        Goals.Propose(_run, S, 0, Condition, askUser: true);
        _rig.Planning = true;

        Answer(true);
        await Wait.UntilAsync(() => Pending() is null);
        await Task.Delay(50);

        Assert.Null(Goals.Store.Get(S));
    }

    [Fact]
    public async Task A_user_set_or_clear_withdraws_the_pending_proposal()
    {
        Goals.Propose(_run, S, 0, Condition, askUser: true);
        Assert.NotNull(Pending());

        Goals.SetByUser(S, "用户自己设的");

        Assert.Null(Pending());
        await Task.Delay(50);
        Assert.Equal("用户自己设的", Goals.Store.Get(S)!.Condition);

        Goals.Propose(_run, S, 0, Condition, askUser: true);
        Goals.Clear(S);
        Assert.Null(Pending());
    }

    [Fact]
    public async Task Aborting_the_run_withdraws_the_pending_proposal_without_a_goal()
    {
        Goals.Propose(_run, S, 0, Condition, askUser: true);

        _rig.Fx.Permissions.Get(_run)!.Interactions.End(Ncw.Agent.Kernel.Loop.RunStatus.Aborted);
        await Task.Delay(50);

        Assert.Null(Pending());
        Assert.Null(Goals.Store.Get(S));
    }
}

/// <summary>
/// <c>ProposeGoal</c> 工具:2.x <c>goal-tool.test.ts</c> 全部用例逐条移植。最重要的两条是反向断言——工具结果里<b>没有</b>审批结果(模型永远收不到),工具<b>不等</b>用户回答。
/// </summary>
public sealed class ProposeGoalToolTests : IDisposable
{
    private const string S = GoalRig.S;
    private const string Condition = "bun test 的退出码为 0(转录里有这次运行的输出)";

    private readonly GoalRig _rig = new();
    private readonly RunId _run = new("run-1");
    private readonly ProposeGoalTool _tool;

    public ProposeGoalToolTests()
    {
        _rig.Transcript.Seed(S, Msg.User("u1", "开始"), Msg.Say("a1", "好"));
        _rig.Fx.Register(_run, S);
        _tool = new ProposeGoalTool(_rig.Goals);
    }

    public void Dispose() => _rig.Dispose();

    private ToolContext Ctx(int depth = 0) => new(_run, "call-1", S, depth, null, new Progress(), new FileMutationQueue());

    private async Task<ToolResult> Run(string json, ToolContext? ctx = null)
    {
        var check = _tool.PrepareArguments(json.Json());
        return check.IsValid
            ? await _tool.ExecuteAsync(new ToolInvocation("call-1", ProposeGoalTool.Name, _tool, check.Input), ctx ?? Ctx(), default)
            : ToolResult.Error(check.Error!);
    }

    private static string Text(ToolResult r) => ((TextPart)r.Content[0]).Text;

    private GoalProposal? Pending() => _rig.Fx.Gate.Snapshot().Pending.OfType<GoalProposal>().FirstOrDefault();

    private static string Args(string condition, bool? askUser = null)
    {
        var o = new System.Text.Json.Nodes.JsonObject { ["condition"] = condition };
        if (askUser is { } ask)
        {
            o["ask_user"] = ask;
        }

        return o.ToJsonString();
    }

    // ───────────────────────── 元数据与描述 ─────────────────────────

    [Fact]
    public void It_is_read_only_not_destructive_no_network_but_not_concurrency_safe()
    {
        var traits = _tool.Info.Traits;

        Assert.Equal("ProposeGoal", _tool.Info.InternalId);
        Assert.True(traits.ReadOnly);
        Assert.False(traits.Destructive);
        Assert.False(traits.NeedsNetwork);
        Assert.False(traits.ConcurrencySafe); // 它改共享状态
    }

    [Fact]
    public void The_description_writes_down_that_the_judge_only_reads_the_conversation_and_the_limit_is_the_constant()
    {
        // ★ 「判定器只看对话」必须写死在描述里:判定器跑不了命令、读不了文件,而描述是模型唯一会读的规格
        var d = _tool.Info.Description;

        Assert.Contains("it cannot run commands or read files", d);
        Assert.Contains("from the conversation alone", d);
        Assert.Contains($"in at most {GoalConditions.ProposalMax} characters", d);
        Assert.Contains("keep working while it is handled", d);
        Assert.Contains("do not ask about the decision and do not re-propose the same or a reworded condition", d);
        Assert.Contains("with no dialog", d);
    }

    // ───────────────────────── 五道门 ─────────────────────────

    [Fact]
    public void A_missing_gate_means_the_tool_is_not_offered()
    {
        Assert.True(_tool.IsEnabled(Ctx()));
        Assert.False(_tool.IsEnabled(Ctx(depth: 1))); // 1. 子 Run 里没人可问

        _rig.InteractiveSession = false; // 2. 非交互会话
        Assert.False(_tool.IsEnabled(Ctx()));
        _rig.InteractiveSession = true;

        _rig.Policy = GoalProposalPolicy.Disabled; // 3. 设置
        Assert.False(_tool.IsEnabled(Ctx()));
        _rig.Policy = GoalProposalPolicy.Auto;

        _rig.Planning = true; // 5. 计划模式
        Assert.False(_tool.IsEnabled(Ctx()));
        _rig.Planning = false;

        Assert.False(_tool.IsEnabled(new ToolContext(new RunId("unregistered"), "c", S, 0, null, new Progress(), new FileMutationQueue())));
    }

    [Fact]
    public async Task The_same_judgement_stands_at_the_door_of_the_call_and_nothing_is_forwarded()
    {
        // ★ 快照是这一轮开头取的,而设置、计划模式、待批状态都会在回合之中变化
        _rig.Policy = GoalProposalPolicy.Disabled;
        var disabled = await Run(Args(Condition));
        _rig.Policy = GoalProposalPolicy.Auto;
        _rig.InteractiveSession = false;
        var nonInteractive = await Run(Args(Condition));
        _rig.InteractiveSession = true;
        var child = await Run(Args(Condition), Ctx(depth: 1));

        foreach (var r in new[] { disabled, nonInteractive, child })
        {
            Assert.True(r.IsError);
            Assert.Contains("unavailable", Text(r));
        }

        Assert.Null(Pending());
        Assert.Null(_rig.Goals.Store.Get(S));
        Assert.Empty(_rig.Delivered);
    }

    // ───────────────────────── 入参与规范化 ─────────────────────────

    [Fact]
    public async Task The_default_is_to_ask_a_forgotten_flag_is_not_a_decision_made_for_the_user()
    {
        var r = await Run(Args(Condition));

        Assert.False(r.IsError);
        Assert.Equal(Condition, Pending()!.Condition);
        using var doc = System.Text.Json.JsonDocument.Parse(Text(r));
        Assert.True(doc.RootElement.GetProperty("askUser").GetBoolean());
        Assert.Equal("pending", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_explicit_ask_user_false_sets_the_goal_directly()
    {
        var r = await Run(Args(Condition, askUser: false));

        using var doc = System.Text.Json.JsonDocument.Parse(Text(r));
        Assert.False(doc.RootElement.GetProperty("askUser").GetBoolean());
        Assert.Equal("set", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(Condition, _rig.Goals.Store.Get(S)!.Condition);
        Assert.Null(Pending());
    }

    [Fact]
    public async Task The_condition_is_forwarded_normalized_invisible_characters_stripped()
    {
        await Run(Args("  ​让测试全绿﻿  "));

        Assert.Equal("让测试全绿", Pending()!.Condition);
    }

    [Fact]
    public async Task A_condition_that_is_empty_once_normalized_is_refused_and_not_forwarded()
    {
        // ★ 一串零宽空格看起来是「写了东西」,判定器收到的却是一个空条件
        var r = await Run(Args("​​\u0000 ‏"));

        Assert.True(r.IsError);
        Assert.Contains("empty once invisible characters are stripped", Text(r));
        Assert.Null(Pending());
    }

    [Fact]
    public async Task The_length_boundary_exactly_500_passes_501_is_refused_not_truncated()
    {
        var atLimit = new string('a', GoalConditions.ProposalMax);
        var ok = await Run(Args(atLimit));
        Assert.False(ok.IsError);
        Assert.Equal(atLimit, Pending()!.Condition);

        _rig.Fx.Gate.AbortAll();
        var bad = await Run(Args(new string('a', GoalConditions.ProposalMax + 1)));
        Assert.True(bad.IsError);
        Assert.Null(Pending());
    }

    [Fact]
    public async Task A_condition_whose_normalized_length_is_over_the_limit_is_refused_with_its_real_length()
    {
        // 不可见字符不算长度:先规范化再量(schema 的 maxLength 量的是原文,所以绕过 schema 直接调)
        var direct = await _tool.ExecuteAsync(
            new ToolInvocation("c", ProposeGoalTool.Name, _tool, Args(new string('a', GoalConditions.ProposalMax + 3)).Json()), Ctx(), default);

        Assert.True(direct.IsError);
        Assert.Null(Pending());
    }

    [Fact]
    public async Task An_empty_blank_or_missing_condition_is_stopped_by_the_schema()
    {
        Assert.True((await Run("""{"condition":"   "}""")).IsError); // 过得了 schema(长度 3),过不了规范化
        Assert.True((await Run("{}")).IsError);
        Assert.True((await Run("""{"condition":""}""")).IsError);
        Assert.Null(Pending());
        // 偏离 2.x:校验器对 string 做宽松转换(数字 → 字符串),zod 是直接拒绝。转换之后仍然要过规范化和长度门,所以没有放宽任何一道
        Assert.False((await Run("""{"condition":42}""")).IsError);
        Assert.Equal("42", Pending()!.Condition);
    }

    // ───────────────────────── 两种结局 ─────────────────────────

    [Fact]
    public async Task It_waits_only_for_the_dispatch_not_for_a_human_pending_returns_at_once_with_the_three_do_nots()
    {
        var r = await Run(Args(Condition)).WaitAsync(TimeSpan.FromSeconds(10)); // 没有人回答,依然立刻返回

        using var doc = System.Text.Json.JsonDocument.Parse(Text(r));
        Assert.Equal("pending", doc.RootElement.GetProperty("status").GetString());
        Assert.False(r.IsError);
        var message = doc.RootElement.GetProperty("message").GetString()!;
        // 三句「别做」:等不到结果,所以别等、别问、别提第二次
        Assert.Contains("do not wait for the decision", message);
        Assert.Contains("do not ask the user about it", message);
        Assert.Contains("do not propose this condition again", message);
        Assert.Contains("No new goal is active until a kickoff message arrives.", message);
    }

    [Fact]
    public async Task A_direct_set_says_the_kickoff_follows_and_to_keep_working()
    {
        var r = await Run(Args(Condition, askUser: false));

        using var doc = System.Text.Json.JsonDocument.Parse(Text(r));
        var message = doc.RootElement.GetProperty("message").GetString()!;
        Assert.Equal(Condition, doc.RootElement.GetProperty("condition").GetString());
        Assert.Contains("A kickoff message follows", message);
        Assert.Contains("keep working", message);
    }

    [Fact]
    public async Task The_result_has_exactly_four_fields_the_approval_outcome_is_never_one_of_them()
    {
        // ★ 批没批由 kickoff 消息告诉模型,不走这里。多一个字段 = 模型会开始等一个它本来就等不到的回答
        var r = await Run(Args(Condition));

        using var doc = System.Text.Json.JsonDocument.Parse(Text(r));
        Assert.Equal(["askUser", "condition", "message", "status"], doc.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    private sealed class Progress : IProgress<ToolProgress>
    {
        public void Report(ToolProgress value)
        {
        }
    }
}
