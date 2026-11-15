using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Subagents;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Json;

namespace Ncw.Agent.Subagents.Tests;

/// <summary>
/// <c>Task</c> 工具自己的那一半。移植自 2.x 的 <c>task-tool.test.ts</c>:它不建 Run、不碰存储、不发事件,全部经由 <see cref="ISubagentLauncher"/> 这条窄缝交给 <see cref="SubagentService"/>,
/// 所以这里的启动器全是假的;真正的接线由 <see cref="SubagentRunTests"/> 端到端地钉。
/// ★ 权重最高的是<b>结局映射</b>那一组:同样是「子代理没跑成」,「这个名字不存在」「跑完了没说话」「被中止了」三种要给出<b>三种不同的东西</b>,因为模型下一步该做的事完全不同(换个名字 / 换个 prompt / 什么都别做)。
/// </summary>
public sealed class TaskToolTests
{
    private sealed class FakeLauncher(SubagentOutcome outcome) : ISubagentLauncher
    {
        public List<SubagentRequest> Seen { get; } = [];

        public Task<SubagentOutcome> SpawnAsync(SubagentRequest request, CancellationToken ct)
        {
            Seen.Add(request);
            return Task.FromResult(outcome);
        }
    }

    private static readonly RunId Child = new("run-1:sub:1");

    private static AgentDefinition Agent(string name = "researcher", string description = "read-only research, changes no file", string[]? tools = null) =>
        new(name, description, "You only look.", new AgentSource(AgentSourceKind.Project, $"/w/.next-cowork/agents/{name}.md")) { Tools = tools };

    private static TaskTool Make(ISubagentLauncher? launcher = null, params AgentDefinition[] extra) =>
        new(launcher, [BuiltinAgents.GeneralPurpose, .. extra]);

    private static ToolContext Context(int depth = 0, string callId = "call_1") =>
        new(new RunId("run_1"), callId, "s1", depth, null, new NullProgress(), new FileMutationQueue());

    private sealed class NullProgress : IProgress<ToolProgress>
    {
        public void Report(ToolProgress value)
        {
        }
    }

    private static ValueTask<ToolResult> Run(TaskTool tool, string json, ToolContext? ctx = null, CancellationToken ct = default) =>
        tool.ExecuteAsync(new ToolInvocation("call_1", "Task", tool, Jsn.Parse(json)), ctx ?? Context(), ct);

    private const string Args = """{"description":"d","prompt":"p","subagent_type":"researcher"}""";

    private static SubagentOutcome.Finished Finished(RunStatus status = RunStatus.Done, string text = "I checked: the config is in src/config.ts:12.", AgentError? error = null) =>
        new(Child, status, text, Error: error);

    private static string Content(ToolResult result) => ((TextPart)result.Content.Single()).Text;

    // ───────────────────────── 标记与形状 ─────────────────────────

    [Fact]
    public void Invariant_read_only_is_false_plan_mode_removes_the_tool_with_it()
    {
        // 这条钉的是一道白送的闸门:计划模式的只读快照会直接把 Task 摘掉,于是计划模式里没法借子代理绕开只读围栏——不用写一行代码。
        // 标成只读是错的,哪怕子代理自己只用了读工具:它跑的是一整条 agent 循环。
        Assert.False(Make().Info.Traits.ReadOnly);
    }

    [Fact]
    public void It_is_not_destructive_and_needs_no_network_those_are_declared_by_the_tools_inside_the_subagent()
    {
        Assert.False(Make().Info.Traits.Destructive);
        Assert.False(Make().Info.Traits.NeedsNetwork);
    }

    [Fact]
    public void Invariant_the_parameter_names_are_verbatim_Claude_Code_plus_the_background_switch()
    {
        var properties = Make().Info.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name);

        Assert.Equal(["description", "prompt", "subagent_type", "run_in_background"], properties);
        Assert.Equal(["description", "prompt", "subagent_type"], Make().Info.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    // ───────────────────────── description 就是那份清单 ─────────────────────────

    [Fact]
    public void Invariant_the_list_follows_the_registry_a_newly_installed_agent_shows_up_in_the_description_at_once()
    {
        // 模型判断「这活派给谁」唯一的依据就是 description 里那几行。所以它必须跟着清单走——写死一份常量的话,用户新加的 agent 文件「存在、能派、但模型永远看不见它」。
        var agents = new List<AgentDefinition> { BuiltinAgents.GeneralPurpose, Agent(tools: ["Read", "Grep", "Glob"]) };
        IReadOnlyList<AgentDefinition> current = agents;
        var tool = new TaskTool(null, () => current);
        Assert.Contains("researcher", tool.Info.Description);
        Assert.Same(tool.Info, tool.Info); // 清单没变就是同一个对象(提示词缓存稳定)

        current = [.. agents, Agent("reviewer", "reviews code")];

        var description = tool.Info.Description;
        Assert.Contains("reviewer", description);
        Assert.Contains("reviews code", description);
    }

    [Fact]
    public void The_builtin_one_is_always_in_the_list() => Assert.Contains("general-purpose", Make().Info.Description);

    [Fact]
    public void Each_agents_tools_are_listed_too_and_a_missing_tools_line_is_written_as_a_star()
    {
        var description = Make(null, Agent(tools: ["Read", "Grep", "Glob"])).Info.Description;

        Assert.Contains("(Tools: Read, Grep, Glob)", description);
        Assert.Contains("(Tools: *)", description);
    }

    [Fact]
    public void Invariant_the_description_says_the_subagent_cannot_see_the_conversation_the_parent_only_reads_this_text() =>
        Assert.Contains("CANNOT SEE THIS CONVERSATION", Make().Info.Description);

    [Fact]
    public void The_description_says_only_one_message_comes_back_so_the_parent_does_not_look_for_the_intermediate_steps()
    {
        var description = Make().Info.Description;

        Assert.Contains("EXACTLY ONE MESSAGE", description);
        Assert.Contains("A SUBAGENT CANNOT ASK THE USER ANYTHING", description);
        Assert.Contains($"{SubagentLimits.MaxDepth} levels", description);
    }

    [Fact]
    public void An_empty_list_says_not_to_call_the_tool() => Assert.Contains("do not call this tool", new TaskTool(null, new List<AgentDefinition>()).Info.Description);

    // ───────────────────────── 派不出去的时候 ─────────────────────────

    [Fact]
    public async Task Invariant_at_the_depth_limit_it_says_why_and_what_to_do_instead_otherwise_the_model_tries_another_name()
    {
        var launcher = new FakeLauncher(Finished());

        var result = await Run(Make(launcher), Args, Context(depth: SubagentLimits.MaxDepth));

        Assert.True(result.IsError);
        Assert.Contains("nesting limit", Content(result));
        Assert.Contains("yourself", Content(result));
        Assert.Contains("do NOT launch another subagent", Content(result));
    }

    [Fact]
    public async Task At_the_depth_limit_the_launcher_is_not_called_at_all()
    {
        var launcher = new FakeLauncher(Finished());

        await Run(Make(launcher), Args, Context(depth: SubagentLimits.MaxDepth));

        Assert.Empty(launcher.Seen);
    }

    [Fact]
    public async Task Below_the_depth_limit_a_subagent_may_launch_another_one()
    {
        var launcher = new FakeLauncher(Finished());

        var result = await Run(Make(launcher), Args, Context(depth: SubagentLimits.MaxDepth - 1));

        Assert.False(result.IsError);
        Assert.Single(launcher.Seen);
    }

    [Fact]
    public async Task Invariant_without_a_launcher_it_is_a_sentence_of_plain_language_not_a_crash()
    {
        // 纯内核测试、以及任何没接启动器的宿主里都会走到这一支。
        var result = await Run(Make(null), Args);

        Assert.True(result.IsError);
        Assert.Contains("cannot be launched", Content(result));
    }

    [Fact]
    public async Task Invariant_a_refusal_reason_is_forwarded_verbatim_without_a_prefix()
    {
        // 启动器给的已经是一段完整的人话(「没有名为 X 的子代理。当前可用:…」)。包一层「子代理 X 失败:」的话,
        // 模型会以为那个子代理存在但坏掉了,然后原样重试一次——而正确的下一步是换一个名字。
        const string reason = "There is no subagent named \"reviewr\". Available: general-purpose, researcher.";

        var result = await Run(Make(new FakeLauncher(new SubagentOutcome.Refused(reason))), Args.Replace("researcher", "reviewr"));

        Assert.True(result.IsError);
        Assert.Equal(reason, Content(result));
        Assert.DoesNotContain("failed", Content(result));
        Assert.Null(result.Subagent);
    }

    // ───────────────────────── 三种结局映射成三种不同的东西 ─────────────────────────

    [Fact]
    public async Task Done_with_text_comes_back_as_is_with_no_wrapping()
    {
        var result = await Run(Make(new FakeLauncher(Finished(text: "Conclusion: in src/a.ts:3"))), Args);

        Assert.False(result.IsError);
        Assert.Equal("Conclusion: in src/a.ts:3", Content(result));
        Assert.Equal(("run-1:sub:1", "done"), (result.Subagent!.ChildRunId, result.Subagent.Status));
        Assert.Equal("Conclusion: in src/a.ts:3", result.Subagent.Summary);
    }

    [Fact]
    public async Task The_receipt_summary_is_the_first_240_characters_the_full_text_is_what_the_model_gets()
    {
        var text = new string('x', 500);

        var result = await Run(Make(new FakeLauncher(Finished(text: text))), Args);

        Assert.Equal(text, Content(result));
        Assert.Equal(SubagentLimits.SummaryChars, result.Subagent!.Summary!.Length);
    }

    [Fact]
    public async Task Invariant_done_with_empty_text_is_reported_as_a_failure_never_an_empty_success_and_says_the_next_step()
    {
        // 空结果被当成成功的话,父代理会认为「查过了,没有」并据此往下做——而真实情况是这次调查根本没发生。
        var result = await Run(Make(new FakeLauncher(Finished(text: "   \n  "))), Args);

        Assert.True(result.IsError);
        Assert.Contains("without producing any text", Content(result));
        Assert.Contains("Retry", Content(result));
        Assert.Equal("error", result.Subagent!.Status);
        Assert.Equal(SubagentErrorKeys.NoOutput, result.Subagent.Error!.MessageKey);
        Assert.Equal("researcher", result.Subagent.Error.MessageParams!["agent"]);
    }

    [Fact]
    public async Task An_error_carries_the_subagents_name_and_the_original_error()
    {
        var result = await Run(Make(new FakeLauncher(Finished(RunStatus.Error, "", AgentError.Create(AgentErrorCode.RateLimit, "upstream 429")))), Args);

        Assert.True(result.IsError);
        Assert.Contains("researcher", Content(result));
        Assert.Contains("upstream 429", Content(result));
        Assert.DoesNotContain("transient", Content(result));
        Assert.Equal(("run-1:sub:1", "error", "upstream 429"), (result.Subagent!.ChildRunId, result.Subagent.Status, result.Subagent.Error!.Message));
    }

    [Fact]
    public async Task A_network_failure_says_it_was_transient_so_the_model_can_tell_a_retry_may_succeed()
    {
        var result = await Run(Make(new FakeLauncher(Finished(RunStatus.Error, "", AgentError.Create(AgentErrorCode.Network, "connection closed")))), Args);

        Assert.Contains("transient network failure", Content(result));
        Assert.Contains("may well succeed", Content(result));
    }

    [Fact]
    public async Task Invariant_aborted_while_the_parent_is_aborted_rethrows_the_cancellation_it_does_not_return_a_failure()
    {
        // 父被中止时会级联中止子,此时父自己也正在中止收尾。返回一个「正常的失败结果」会和中止收尾的孤儿 tool_result 补齐打架:同一个 callId 会被写两条结果。
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Run(Make(new FakeLauncher(Finished(RunStatus.Aborted, ""))), Args, ct: cts.Token));
    }

    [Fact]
    public async Task Aborted_while_only_the_child_was_stopped_gives_the_model_a_clear_result_and_an_aborted_receipt()
    {
        // 用户在卡片上只停了子代理:父 Run 还活着,工具要给模型一个清楚的结果,而不是一个会被当成「工具异常」的取消。
        var result = await Run(Make(new FakeLauncher(Finished(RunStatus.Aborted, ""))), Args);

        Assert.True(result.IsError);
        Assert.Contains("was stopped by the user", Content(result));
        Assert.Equal("aborted", result.Subagent!.Status);
    }

    // ───────────────────────── 传给启动器的东西 ─────────────────────────

    [Fact]
    public async Task The_three_inputs_go_across_with_the_call_id_and_the_parent_run_id()
    {
        var launcher = new FakeLauncher(Finished());

        await Run(Make(launcher), """{"description":"find config","prompt":"find where config is read","subagent_type":"researcher"}""", Context(callId: "call_42"));

        Assert.Equal([new SubagentRequest(new RunId("run_1"), "call_42", "researcher", "find where config is read", "find config")], launcher.Seen);
    }

    [Fact]
    public async Task Run_in_background_passes_the_flag_through_and_maps_the_background_outcome_to_a_success()
    {
        var launcher = new FakeLauncher(new SubagentOutcome.Background(Child));

        var result = await Run(Make(launcher), """{"description":"d","prompt":"p","subagent_type":"researcher","run_in_background":true}""", Context(callId: "call_bg"));

        Assert.False(result.IsError);
        Assert.Contains("started in the background", Content(result));
        Assert.True(launcher.Seen.Single().Background);
        Assert.Equal(("run-1:sub:1", "running", true), (result.Subagent!.ChildRunId, result.Subagent.Status, result.Subagent.Background));
    }

    [Fact]
    public async Task Invariant_a_queued_dispatch_is_said_to_be_queued_never_started()
    {
        // 并发满的时候这次派发是真的还没开始跑。谎报成已启动的话,模型会在几秒后就去等它的产出,而它可能还要等上几分钟。
        var result = await Run(Make(new FakeLauncher(new SubagentOutcome.Background(Child, Queued: true))), Args);

        Assert.False(result.IsError);
        Assert.Contains("queued behind the running ones", Content(result));
        Assert.Contains("starts automatically", Content(result));
        Assert.DoesNotContain("started in the background", Content(result));
    }

    [Fact]
    public async Task Invariant_launched_in_the_foreground_and_moved_to_the_background_by_the_user_it_says_so_and_not_to_launch_it_again()
    {
        var result = await Run(Make(new FakeLauncher(new SubagentOutcome.Background(new RunId("run_1:sub:2"), Detached: true))), Args);

        Assert.False(result.IsError);
        Assert.Contains("The user moved this subagent to the background", Content(result));
        Assert.Contains("do NOT launch it again", Content(result));
        Assert.Equal(("run_1:sub:2", "running", true), (result.Subagent!.ChildRunId, result.Subagent.Status, result.Subagent.Background));
    }

    [Fact]
    public async Task The_receipt_carries_the_color_of_the_definition()
    {
        var result = await Run(Make(new FakeLauncher(new SubagentOutcome.Background(Child, Color: "blue"))), Args);

        Assert.Equal("blue", result.Subagent!.Color);
    }

    [Fact]
    public async Task Invariant_the_name_is_not_corrected_in_any_way_falling_back_to_general_purpose_is_the_worst_failure()
    {
        var launcher = new FakeLauncher(new SubagentOutcome.Refused("There is no subagent named \"RESEARCHER\"."));

        await Run(Make(launcher), Args.Replace("researcher", "RESEARCHER"));

        Assert.Equal("RESEARCHER", launcher.Seen.Single().AgentType);
    }

    [Fact]
    public async Task Invalid_arguments_are_stopped_at_the_schema_they_are_never_launched()
    {
        var launcher = new FakeLauncher(Finished());
        var tool = Make(launcher);

        var empty = tool.PrepareArguments(Jsn.Parse("""{"description":"d","prompt":"","subagent_type":"researcher"}"""));
        var result = await Run(tool, """{"description":"d","prompt":"","subagent_type":"researcher"}""");

        Assert.False(empty.IsValid);
        Assert.True(result.IsError);
        Assert.Empty(launcher.Seen);
    }

    [Fact]
    public void Invalid_arguments_missing_fields_and_overlong_values_are_rejected()
    {
        var tool = Make();

        Assert.False(tool.PrepareArguments(Jsn.Parse("""{"description":"d","prompt":"p"}""")).IsValid);
        Assert.False(tool.PrepareArguments(Jsn.Parse($$"""{"description":"{{new string('x', 201)}}","prompt":"p","subagent_type":"a"}""")).IsValid);
        Assert.True(tool.PrepareArguments(Jsn.Parse(Args)).IsValid);
    }
}
