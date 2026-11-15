using Ncw.Agent.Environments;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Extensions.Planning.Plans;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Tools;
using Ncw.Agent.Llm.Json;

namespace Ncw.Agent.Planning.Tests;

/// <summary>
/// 计划模式的单测:2.x <c>tool/__tests__/plan-file.test.ts</c> 全部用例逐条移植(真临时目录 + <c>LocalEnvironment</c>,真的 Write / Edit 工具),
/// 加上 3.0 新增的围栏同步、批准计划的载入、状态块。每条 ★ 不变量对到 05 §6 的 PM 表。
/// </summary>
public sealed class PlanModeTests : IDisposable
{
    private static readonly string[] Workflow = ["EnterPlanMode", "Write", "Edit", "ExitPlanMode", "Read"];

    private readonly Fixture _fx = new();
    private readonly RunId _run = new("run-1");

    public void Dispose() => _fx.Dispose();

    private static ToolInvocation Call(IAgentTool tool, string json = "{}", string callId = "c1") => new(callId, tool.Info.InternalId, tool, json.Json());

    private async Task<ToolResult> Run(IAgentTool tool, ExecutionToolContext ctx, string json = "{}")
    {
        var check = tool.PrepareArguments(json.Json());
        return check.IsValid ? await tool.ExecuteAsync(Call(tool, check.Input.GetRawText()), ctx, default) : ToolResult.Error(check.Error!);
    }

    private static string Text(ToolResult r) => ((TextPart)r.Content[0]).Text;

    private IReadOnlyList<string> Allowed(PlanService plans, string session = "s1", bool workflow = true) =>
        [.. Workflow.Where(t => plans.Allows(t, session, workflow))];

    private async Task<(PlanService Plans, ActivePlan Plan)> Entered()
    {
        var plans = _fx.Plans();
        _fx.Register(_run);
        var r = await Run(new EnterPlanModeTool(plans), _fx.Ctx(_run), """{"reason":"Requirements are clear."}""");
        Assert.False(r.IsError, Text(r));
        return (plans, plans.ActiveFor("s1")!);
    }

    private async Task<ToolResult> Respond(IAgentTool exit, ExecutionToolContext ctx, PlanApprovalResponse response, Action<PlanApproval>? onRequest = null)
    {
        var task = Run(exit, ctx);
        var pending = await Wait.UntilNotNull(() => _fx.Gate.Snapshot().Pending.OfType<PlanApproval>().FirstOrDefault());
        onRequest?.Invoke(pending);
        Assert.IsType<RespondResult.Accepted>(_fx.Gate.Respond(pending.Id, pending.Version, response, "ctrl"));
        return await task;
    }

    // ───────────────────────── plan-file.test.ts ─────────────────────────

    [Fact]
    public async Task Creates_one_markdown_file_and_changes_the_available_workflow_tools_by_phase()
    {
        var plans = _fx.Plans();
        _fx.Register(_run);
        Assert.Equal(["EnterPlanMode", "Read"], Allowed(plans));

        var result = await Run(new EnterPlanModeTool(plans), _fx.Ctx(_run), """{"reason":"Requirements are clear."}""");

        Assert.False(result.IsError);
        var active = plans.ActiveFor("s1")!;
        Assert.Matches(@"^\.plan/[A-Za-z0-9]+\.md$", active.Path);
        Assert.Equal("", File.ReadAllText(active.Absolute.Value));
        Assert.Equal(["Write", "Edit", "ExitPlanMode", "Read"], Allowed(plans));
        // 工具回执:结构化,带路径和下一步
        using var doc = System.Text.Json.JsonDocument.Parse(Text(result));
        Assert.Equal("plan_file_entered", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(active.Path, doc.RootElement.GetProperty("path").GetString());

        var second = await Run(new EnterPlanModeTool(plans), _fx.Ctx(_run, callId: "call-2"));
        Assert.True(second.IsError);
        Assert.Contains("already active", Text(second));
    }

    [Fact]
    public async Task Keeps_the_plan_and_its_write_tools_available_in_later_runs_of_the_same_session()
    {
        // ★ 计划按会话存,不按 Run:下一条用户消息 = 新的 RunId、同一条会话。按 RunId 存的话 Write / Edit 又被摘掉,模型手里还攥着那个路径
        var (plans, active) = await Entered();

        Assert.Equal(active.PlanId, plans.ActiveFor("s1")?.PlanId); // 和 RunId 无关
        Assert.Equal(["Write", "Edit", "ExitPlanMode", "Read"], Allowed(plans, "s1"));
        // 另一条会话不受影响——它还没有计划文件
        Assert.Null(plans.ActiveFor("other"));
        Assert.Equal(["EnterPlanMode", "Read"], Allowed(plans, "other"));
    }

    [Fact]
    public async Task Hard_fences_Write_and_Edit_to_the_active_plan_path()
    {
        var (plans, active) = await Entered();
        _fx.Put("src.ts", "old");
        var ctx = _fx.Ctx(_run);

        // 围栏开着(进入计划时工具已经设了):只许改计划文件
        Assert.Equal(active.Absolute, _fx.Runtime.WriteFence);
        var blocked = await Run(new WriteTool(), ctx, J.Obj(("file_path", _fx.Abs("src.ts")), ("content", "no")));
        Assert.True(blocked.IsError);
        Assert.Contains("only modify its active Markdown plan", Text(blocked));
        Assert.Equal("old", _fx.Read("src.ts"));

        var written = await Run(new WriteTool(), ctx, J.Obj(("file_path", active.Absolute.Value), ("content", "# Plan\n\n1. Implement.")));
        Assert.False(written.IsError, Text(written));
        var edited = await Run(new EditTool(), ctx, J.Obj(("file_path", active.Absolute.Value), ("old_string", "Implement."), ("new_string", "Implement and verify.")));
        Assert.False(edited.IsError, Text(edited));
        Assert.Contains("Implement and verify.", File.ReadAllText(active.Absolute.Value));
        _ = plans;
    }

    [Theory]
    [InlineData("short plan")]
    [InlineData("multibyte")]
    [InlineData("one mebibyte")]
    public async Task Presents_the_saved_plan_and_stops_the_run_after_approval(string kind)
    {
        var plan = kind switch
        {
            "short plan" => "# Approved plan",
            "multibyte" => "# Plan\n\n" + new string('步', 27_684) + "\n\n",
            _ => new string('x', 1024 * 1024),
        };
        var (plans, active) = await Entered();
        File.WriteAllText(active.Absolute.Value, plan);

        PlanApproval? presented = null;
        var result = await Respond(new ExitPlanModeTool(plans), _fx.Ctx(_run), new PlanApprovalResponse(PlanApprovalAction.ApproveCurrent), p => presented = p);

        Assert.False(result.IsError, Text(result));
        Assert.Equal(plan, presented!.Plan);
        Assert.Equal(active.PlanId, presented.PlanId);
        Assert.Equal(active.Path, presented.Path);
        Assert.True(result.Terminate); // 批准即终点:Run 到此为止
        using var doc = System.Text.Json.JsonDocument.Parse(Text(result));
        Assert.Equal("plan_file", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("approve_current", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal(active.Path, doc.RootElement.GetProperty("path").GetString());
        // 批准即终点:下一条计划要重新 EnterPlanMode;围栏也撤了
        Assert.Null(plans.ActiveFor("s1"));
        Assert.Null(_fx.Runtime.WriteFence);
        Assert.Equal(["EnterPlanMode", "Read"], Allowed(plans));
    }

    [Theory]
    [InlineData("ascii")]
    [InlineData("utf8")]
    public async Task Rejects_an_oversized_plan_before_presenting_it(string kind)
    {
        var plan = kind == "ascii" ? new string('x', (int)PlanFiles.MaxBytes + 1) : new string('步', (int)(PlanFiles.MaxBytes / 3) + 1);
        var (plans, active) = await Entered();
        File.WriteAllText(active.Absolute.Value, plan);

        var result = await Run(new ExitPlanModeTool(plans), _fx.Ctx(_run));

        Assert.True(result.IsError);
        Assert.Equal($"The plan file is too large ({System.Text.Encoding.UTF8.GetByteCount(plan)} bytes; limit {PlanFiles.MaxBytes}).", Text(result));
        Assert.Empty(_fx.Gate.Snapshot().Pending); // 没有展示
        Assert.False(result.Terminate);
        Assert.Equal(active.PlanId, plans.ActiveFor("s1")?.PlanId); // 计划保持原样
    }

    [Fact]
    public async Task Rejects_a_plan_that_exceeds_the_size_limit_while_review_is_open()
    {
        var (plans, active) = await Entered();
        File.WriteAllText(active.Absolute.Value, "# Draft plan");

        var task = Run(new ExitPlanModeTool(plans), _fx.Ctx(_run));
        var pending = await Wait.UntilNotNull(() => _fx.Gate.Snapshot().Pending.OfType<PlanApproval>().FirstOrDefault());
        File.WriteAllText(active.Absolute.Value, new string('x', (int)PlanFiles.MaxBytes + 1)); // 审阅期间被改坏了
        _fx.Gate.Respond(pending.Id, pending.Version, new PlanApprovalResponse(PlanApprovalAction.ApproveCurrent), "ctrl");
        var result = await task;

        Assert.True(result.IsError);
        Assert.Contains("became invalid before review completed", Text(result));
        Assert.False(result.Terminate);
        Assert.Equal(active.PlanId, plans.ActiveFor("s1")?.PlanId);
    }

    [Fact]
    public async Task Continues_the_same_plan_when_the_user_requests_a_revision()
    {
        var (plans, active) = await Entered();
        File.WriteAllText(active.Absolute.Value, "# Draft plan");

        var result = await Respond(new ExitPlanModeTool(plans), _fx.Ctx(_run), new PlanApprovalResponse(PlanApprovalAction.RequestRevision, "Add tests."));

        Assert.False(result.Terminate);
        Assert.Contains("Add tests.", Text(result));
        Assert.Equal(active.PlanId, plans.ActiveFor("s1")?.PlanId); // 修订往往发生在下一轮:计划必须留着
        Assert.Equal(active.Absolute, _fx.Runtime.WriteFence);
    }

    [Fact]
    public async Task The_receipt_the_model_reads_keeps_non_ascii_feedback_as_it_is()
    {
        // 回放对照抓到的缺陷:回执 JSON 用默认编码器写,中文反馈被写成 \uXXXX(2.x 的 JSON.stringify 不这么做)。
        var (plans, active) = await Entered();
        File.WriteAllText(active.Absolute.Value, "# Draft plan");

        var result = await Respond(new ExitPlanModeTool(plans), _fx.Ctx(_run), new PlanApprovalResponse(PlanApprovalAction.RequestRevision, "加上测试,don't skip <edge> cases"));

        Assert.Contains("\"feedback\":\"加上测试,don't skip <edge> cases\"", Text(result));
        Assert.DoesNotContain("\\u", Text(result));
    }

    [Fact]
    public async Task Rejection_also_ends_the_plan_and_the_run()
    {
        var (plans, active) = await Entered();
        File.WriteAllText(active.Absolute.Value, "# Draft plan");

        var result = await Respond(new ExitPlanModeTool(plans), _fx.Ctx(_run), new PlanApprovalResponse(PlanApprovalAction.Reject));

        Assert.True(result.Terminate);
        Assert.Null(plans.ActiveFor("s1"));
        using var doc = System.Text.Json.JsonDocument.Parse(Text(result));
        Assert.Equal("reject", doc.RootElement.GetProperty("action").GetString());
    }

    [Fact]
    public async Task Releases_the_plan_when_its_file_disappeared_so_a_new_one_can_be_started()
    {
        var (plans, active) = await Entered();
        File.Delete(active.Absolute.Value);

        var result = await Run(new ExitPlanModeTool(plans), _fx.Ctx(_run));

        Assert.True(result.IsError);
        Assert.Contains("no longer exists", Text(result));
        Assert.Null(plans.ActiveFor("s1"));
        Assert.Equal(["EnterPlanMode", "Read"], Allowed(plans));
        Assert.Null(_fx.Runtime.WriteFence);
    }

    // ───────────────────────── ExitPlanMode 的其它守卫 ─────────────────────────

    [Fact]
    public async Task Exit_without_an_active_plan_says_to_call_EnterPlanMode_first()
    {
        var plans = _fx.Plans();
        _fx.Register(_run);

        var result = await Run(new ExitPlanModeTool(plans), _fx.Ctx(_run));

        Assert.True(result.IsError);
        Assert.Contains("Call EnterPlanMode first", Text(result));
    }

    [Fact]
    public async Task An_empty_plan_cannot_be_presented()
    {
        var (plans, active) = await Entered();

        var result = await Run(new ExitPlanModeTool(plans), _fx.Ctx(_run));

        Assert.True(result.IsError);
        Assert.Contains("The plan file is empty", Text(result));
        Assert.Empty(_fx.Gate.Snapshot().Pending);
        Assert.NotNull(plans.ActiveFor("s1"));
        _ = active;
    }

    [Fact]
    public async Task A_plan_path_that_is_a_directory_is_not_a_markdown_file()
    {
        var (plans, active) = await Entered();
        File.Delete(active.Absolute.Value);
        Directory.CreateDirectory(active.Absolute.Value);

        var result = await Run(new ExitPlanModeTool(plans), _fx.Ctx(_run));

        Assert.True(result.IsError);
        Assert.Contains("is a directory", Text(result));
    }

    [Fact]
    public async Task A_whitespace_only_plan_contains_no_plan_text()
    {
        var (plans, active) = await Entered();
        File.WriteAllText(active.Absolute.Value, "  \n\t\n");

        var result = await Run(new ExitPlanModeTool(plans), _fx.Ctx(_run));

        Assert.True(result.IsError);
        Assert.Contains("contains no plan text", Text(result));
    }

    [Fact]
    public async Task A_subagent_cannot_review_a_plan_and_the_tools_are_not_offered_to_it()
    {
        var plans = _fx.Plans();
        _fx.Register(_run, depth: 1);
        var tool = new EnterPlanModeTool(plans);
        var ctx = _fx.Ctx(_run, depth: 1);

        Assert.False(tool.IsEnabled(ctx));
        Assert.False(new ExitPlanModeTool(plans).IsEnabled(ctx));
        Assert.True(tool.IsEnabled(_fx.Ctx(_run, depth: 0)));
    }

    [Fact]
    public async Task Review_needs_an_interaction_scope_that_can_ask()
    {
        var plans = _fx.Plans();
        var sub = new RunId("sub-1");
        _fx.Register(sub, depth: 1); // 子代理:句柄存在但不能提问
        plans.Plans.TryBegin(new ActivePlan("pl", ".plan/pl.md", _fx.P(".plan/pl.md"), "s1"), out _);
        _fx.Put(".plan/pl.md", "# plan");

        var result = await Run(new ExitPlanModeTool(plans), _fx.Ctx(sub, depth: 1));

        Assert.True(result.IsError);
        Assert.Contains("Plan review is not available", Text(result));
    }

    [Fact]
    public async Task Aborting_the_run_aborts_a_pending_plan_approval_and_the_plan_stays()
    {
        var (plans, active) = await Entered();
        File.WriteAllText(active.Absolute.Value, "# plan");
        using var cts = new CancellationTokenSource();

        var task = Task.Run(async () => await new ExitPlanModeTool(plans).ExecuteAsync(Call(new ExitPlanModeTool(plans)), _fx.Ctx(_run), cts.Token));
        await Wait.UntilNotNull(() => _fx.Gate.Snapshot().Pending.OfType<PlanApproval>().FirstOrDefault());
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.Equal(active.PlanId, plans.ActiveFor("s1")?.PlanId);
    }

    // ───────────────────────── 注册表 ─────────────────────────

    [Fact]
    public void The_registry_remembers_a_bounded_number_of_sessions_and_drops_the_oldest()
    {
        var registry = new PlanRegistry();
        for (var i = 0; i < PlanRegistry.MaxScopes + 3; i++)
        {
            Assert.True(registry.TryBegin(new ActivePlan($"p{i}", $".plan/p{i}.md", EnvPath.Posix($"/ws/.plan/p{i}.md"), $"s{i}"), out _));
        }

        Assert.Equal(PlanRegistry.MaxScopes, registry.Count);
        Assert.Null(registry.Get("s0"));
        Assert.Null(registry.Get("s2"));
        Assert.NotNull(registry.Get("s3"));
        Assert.NotNull(registry.Get($"s{PlanRegistry.MaxScopes + 2}"));
    }

    [Fact]
    public void A_session_has_at_most_one_plan_and_leaving_frees_it()
    {
        var registry = new PlanRegistry();
        var first = new ActivePlan("a", ".plan/a.md", EnvPath.Posix("/ws/.plan/a.md"), "s");

        Assert.True(registry.TryBegin(first, out _));
        Assert.False(registry.TryBegin(first with { PlanId = "b" }, out var existing));
        Assert.Equal("a", existing!.PlanId);
        Assert.True(registry.Leave("s"));
        Assert.False(registry.Leave("s"));
        Assert.True(registry.TryBegin(first with { PlanId = "b" }, out _));
    }

    // ───────────────────────── 工具允许列表 ─────────────────────────

    [Fact]
    public void The_allow_list_always_drops_Task_in_the_plan_workflow()
    {
        var plans = _fx.Plans();

        Assert.False(plans.Allows("Task", "s1", workflow: true));
        Assert.True(plans.Allows("Task", "s1", workflow: false)); // 没开计划工作流时 Task 不归计划管
    }

    [Fact]
    public async Task Without_the_plan_workflow_only_the_two_plan_tools_are_hidden()
    {
        var plans = _fx.Plans();

        Assert.False(plans.Allows("EnterPlanMode", "s1", workflow: false));
        Assert.False(plans.Allows("ExitPlanMode", "s1", workflow: false));
        Assert.True(plans.Allows("Write", "s1", workflow: false));
        Assert.True(plans.Allows("Edit", "s1", workflow: false));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_before_run_filter_is_recomputed_every_turn_and_composes_with_an_existing_filter()
    {
        var plans = _fx.Plans();
        _fx.Register(_run);
        var draft = new RunDraft(new RunRequest(_run, [], ModelRef.Unpinned("m"))) { ToolFilter = spec => spec.InternalId != "Bash" };
        await plans.OnBeforeRunAsync(draft, new ExtensionContext(_run, "s1", new CollectingLog()), default);

        bool Visible(string id) => draft.ToolFilter!(new ToolSpec(id, "d", "{}".Json(), new ToolTraits(true, false, false)));

        Assert.False(Visible("Bash")); // 别人设的过滤器还在
        Assert.False(Visible("Write"));
        Assert.True(Visible("EnterPlanMode"));
        await Run(new EnterPlanModeTool(plans), _fx.Ctx(_run)); // 计划在 Run 中间开了:下一回合的表跟着变,不用重设过滤器
        Assert.True(Visible("Write"));
        Assert.True(Visible("ExitPlanMode"));
        Assert.False(Visible("EnterPlanMode"));
        Assert.False(Visible("Task"));
        Assert.False(Visible("Bash"));
    }

    // ───────────────────────── 围栏同步与状态块 ─────────────────────────

    [Fact]
    public async Task The_fence_is_the_same_value_on_the_tool_runtime_and_the_permission_state()
    {
        // ★ 权限层和工具层对「围在哪」永远是同一个答案(03 §3 的 P11)
        var plans = _fx.Plans();
        var state = _fx.Register(_run);
        await Run(new EnterPlanModeTool(plans), _fx.Ctx(_run));

        var fence = plans.FenceFor("s1");
        Assert.NotNull(fence);
        Assert.Equal(fence, _fx.Runtime.WriteFence);
        Assert.Equal(fence, state.PlanFence);

        plans.Plans.Leave("s1");
        plans.SyncFence(_run, "s1", _fx.Ctx(_run)); // 下一次丰富工具上下文时自己收口
        Assert.Null(_fx.Runtime.WriteFence);
        Assert.Null(state.PlanFence);
    }

    [Fact]
    public async Task A_new_run_of_the_same_session_gets_the_fence_without_the_assembler_remembering_to_set_it()
    {
        var plans = _fx.Plans();
        _fx.Register(_run);
        await Run(new EnterPlanModeTool(plans), _fx.Ctx(_run));

        // 下一个 Run:全新的 ToolRuntime(没有围栏)和权限状态
        using var next = new Fixture();
        var runtime = new ToolRuntime { Environment = _fx.Env, Root = _fx.Runtime.Root };
        var nextRun = new RunId("run-2");
        var state = PermissionRunState.FromRuntime(runtime, nextRun, "s1", _fx.Gate.OpenRun(nextRun), PermissionMode.Full);
        _fx.Permissions.Register(state);
        Assert.Null(runtime.WriteFence);

        plans.SyncFence(nextRun, "s1", new ExecutionToolContext(_fx.Ctx(nextRun), runtime));

        Assert.Equal(plans.FenceFor("s1"), runtime.WriteFence);
        Assert.Equal(plans.FenceFor("s1"), state.PlanFence);
    }

    [Fact]
    public async Task The_state_block_carries_the_plan_file_path_and_follows_enter_and_exit_within_a_run()
    {
        // ★ 计划文件路径每回合重新注入:它原本只存在于 EnterPlanMode 的工具结果里,压缩会把它抹掉
        var plans = _fx.Plans();
        _fx.Register(_run);
        var draft = new RunDraft(new RunRequest(_run, [], ModelRef.Unpinned("m")));
        await plans.OnBeforeRunAsync(draft, new ExtensionContext(_run, "s1", new CollectingLog()), default);
        Assert.Empty(draft.StateSections);

        await Run(new EnterPlanModeTool(plans), _fx.Ctx(_run));
        var path = plans.ActiveFor("s1")!.Path;
        Assert.Single(draft.StateSections);
        Assert.Contains($"Active Plan mode file: {path}", draft.StateSections[0]);
        Assert.Contains("Read it before changing it", draft.StateSections[0]);

        File.WriteAllText(plans.ActiveFor("s1")!.Absolute.Value, "# p");
        await Respond(new ExitPlanModeTool(plans), _fx.Ctx(_run), new PlanApprovalResponse(PlanApprovalAction.Reject));
        Assert.Empty(draft.StateSections);
    }

    [Fact]
    public async Task A_run_started_in_the_middle_of_a_plan_sees_the_plan_in_its_first_state_block()
    {
        var (plans, active) = await Entered();
        var next = new RunId("run-2");
        var draft = new RunDraft(new RunRequest(next, [], ModelRef.Unpinned("m")));

        await plans.OnBeforeRunAsync(draft, new ExtensionContext(next, "s1", new CollectingLog()), default);

        Assert.Contains(active.Path, Assert.Single(draft.StateSections));
    }

    [Fact]
    public async Task Compaction_reattaches_the_plan_file_path()
    {
        var (plans, active) = await Entered();
        var draft = new CompactAttachmentDraft("s1", []);

        plans.OnCompactAttachments(draft);

        Assert.Contains(active.Path, Assert.Single(draft.Texts));
        var other = new CompactAttachmentDraft("other", []);
        plans.OnCompactAttachments(other);
        Assert.Empty(other.Texts);
    }

    [Fact]
    public async Task Without_the_workflow_there_is_no_plan_section_in_the_state_block()
    {
        var (plans, _) = await Entered();
        var plain = _fx.Plans(_ => false);
        plain.Plans.TryBegin(plans.ActiveFor("s1")!, out _);
        var next = new RunId("run-2");
        var draft = new RunDraft(new RunRequest(next, [], ModelRef.Unpinned("m")));

        await plain.OnBeforeRunAsync(draft, new ExtensionContext(next, "s1", new CollectingLog()), default);

        Assert.Empty(draft.StateSections);
    }

    // ───────────────────────── 批准的计划(plan-execution.ts) ─────────────────────────

    [Fact]
    public async Task An_approved_plan_is_loaded_into_the_first_state_block_of_the_executing_run()
    {
        var plans = _fx.Plans();
        var exec = new RunId("exec-1");
        _fx.Register(exec);
        _fx.Put(".plan/PL1.md", "# Approved\n\n1. Do the thing.");
        plans.ExpectExecution(exec, new PlanFileRef("PL1", ".plan/PL1.md"));
        var draft = new RunDraft(new RunRequest(exec, [], ModelRef.Unpinned("m")));

        await plans.OnBeforeRunAsync(draft, new ExtensionContext(exec, "s2", new CollectingLog()), default);

        var section = Assert.Single(draft.StateSections);
        Assert.StartsWith("Approved plan file: .plan/PL1.md\n\n# Approved", section);
        Assert.Contains("1. Do the thing.", section);
    }

    [Fact]
    public async Task A_huge_approved_plan_is_split_into_sections_that_fit_the_assembler_and_lose_no_line()
    {
        var plans = _fx.Plans();
        var exec = new RunId("exec-2");
        _fx.Register(exec);
        var lines = Enumerable.Range(0, 2000).Select(i => $"{i}. step number {i} of the plan").ToList();
        _fx.Put(".plan/BIG.md", string.Join('\n', lines));
        plans.ExpectExecution(exec, new PlanFileRef("BIG", ".plan/BIG.md"));
        var draft = new RunDraft(new RunRequest(exec, [], ModelRef.Unpinned("m")));

        await plans.OnBeforeRunAsync(draft, new ExtensionContext(exec, "s2", new CollectingLog()), default);

        Assert.True(draft.StateSections.Count > 1);
        Assert.All(draft.StateSections, s => Assert.True(s.Length <= 4000, $"a section of {s.Length} chars would be clamped by the assembler"));
        var rejoined = string.Join("\n", draft.StateSections);
        Assert.All(lines, line => Assert.Contains(line, rejoined));
    }

    [Theory]
    [InlineData("mismatch")]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("blank")]
    [InlineData("directory")]
    [InlineData("oversized")]
    public async Task An_approved_plan_that_cannot_be_loaded_says_so_in_the_state_block_instead_of_silently_running_without_it(string problem)
    {
        var plans = _fx.Plans();
        var exec = new RunId("exec-3");
        _fx.Register(exec);
        var reference = new PlanFileRef("PL", ".plan/PL.md");
        switch (problem)
        {
            case "mismatch":
                reference = new PlanFileRef("PL", ".plan/other.md");
                break;
            case "empty":
                _fx.Put(".plan/PL.md", "");
                break;
            case "blank":
                _fx.Put(".plan/PL.md", "  \n ");
                break;
            case "directory":
                Directory.CreateDirectory(_fx.Abs(".plan/PL.md"));
                break;
            case "oversized":
                _fx.Put(".plan/PL.md", new string('x', (int)PlanFiles.MaxBytes + 1));
                break;
        }

        plans.ExpectExecution(exec, reference);
        var draft = new RunDraft(new RunRequest(exec, [], ModelRef.Unpinned("m")));
        var log = new CollectingLog();

        await plans.OnBeforeRunAsync(draft, new ExtensionContext(exec, "s2", log), default);

        var section = Assert.Single(draft.StateSections);
        Assert.Contains("could not be loaded", section);
        Assert.Contains("do not guess", section);
    }

    [Fact]
    public async Task Loading_an_approved_plan_rejects_a_path_that_escapes_the_project_root()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await PlanExecution.LoadAsync(_fx.Env, _fx.Runtime.Root!.Value, new PlanFileRef("../../etc/x", PlanFiles.PathOf("../../etc/x")), default));

        Assert.True(ex is EnvironmentException or PlanExecutionException);
    }

    [Fact]
    public void Chunking_prefers_line_boundaries_and_never_splits_a_surrogate_pair()
    {
        var text = string.Concat(Enumerable.Repeat("😀", 5000)); // 一行就超长:硬切
        var chunks = PlanSections.Chunk(text, 3800).ToList();

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 3800));
        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks, c => Assert.False(char.IsHighSurrogate(c[^1])));
    }

    [Fact]
    public async Task Enter_needs_a_workspace()
    {
        using var noRoot = new Fixture(withRoot: false);
        var plans = noRoot.Plans();
        noRoot.Register(_run);

        var result = await Run(new EnterPlanModeTool(plans), noRoot.Ctx(_run));

        Assert.True(result.IsError);
        Assert.Contains("requires an open workspace", Text(result));
    }

    [Fact]
    public async Task Enter_rejects_an_oversized_reason()
    {
        var plans = _fx.Plans();
        _fx.Register(_run);

        var result = await Run(new EnterPlanModeTool(plans), _fx.Ctx(_run), J.Obj(("reason", new string('r', 2001))));

        Assert.True(result.IsError);
        Assert.Contains("Invalid arguments", Text(result));
    }

    [Fact]
    public async Task Tool_results_carry_the_execution_environment_in_their_details()
    {
        var plans = _fx.Plans();
        _fx.Register(_run);

        var result = await Run(new EnterPlanModeTool(plans), _fx.Ctx(_run));

        var env = result.Details!.Value.GetProperty("environment");
        Assert.Equal("dev-1", env.GetProperty("deviceId").GetString());
        Assert.Equal("local", env.GetProperty("type").GetString());
    }
}
