using Ncw.Agent.Extensions.Planning;
using Ncw.Agent.Extensions.Planning.TodoLists;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Core.Domain;

namespace Ncw.Agent.Planning.Tests;

/// <summary>
/// <c>TodoWrite</c> 的单测:2.x <c>builtin/__tests__/todo.test.ts</c> 全部用例逐条移植。
/// 最重要的一条是<b>反向断言</b>:调用两次之后,第二次的结果里不含第一次的任何内容——它钉的是「完全无状态」,谁哪天顺手在工具里加一个字段存清单,这条会立刻红。
/// </summary>
public sealed class TodoWriteToolTests
{
    private static ToolContext Ctx() => new(new RunId("run-1"), "call-1", "s1", 0, null, new Progress(), new FileMutationQueue());

    private static (string Content, string Status) Todo(string content, string status) => (content, status);

    private static async Task<ToolResult> Write(string json, ITranscriptReader? transcript = null)
    {
        var tool = new TodoWriteTool(transcript);
        var check = tool.PrepareArguments(json.Json());
        if (!check.IsValid)
        {
            return ToolResult.Error(check.Error!);
        }

        return await tool.ExecuteAsync(new ToolInvocation("call-1", TodoWriteTool.Name, tool, check.Input), Ctx(), default);
    }

    private static Task<ToolResult> Write(params (string Content, string Status)[] items) => Write(TodoJson.List(items));

    private static string Text(ToolResult r) => ((TextPart)r.Content[0]).Text;

    // ───────────────────────── 标记 ─────────────────────────

    [Fact]
    public void Is_read_only_so_the_plan_mode_permission_gate_never_asks_for_it()
    {
        // ★ 它不碰磁盘、不碰网络,而计划模式恰恰是最需要列清单的时候——标成非只读,计划模式下模型连计划都写不顺
        var traits = new TodoWriteTool().Info.Traits;

        Assert.True(traits.ReadOnly);
        Assert.False(traits.Destructive);
        Assert.False(traits.NeedsNetwork);
    }

    // ───────────────────────── 校验 ─────────────────────────

    [Fact]
    public async Task An_empty_list_is_refused_and_says_why()
    {
        // ★ 空清单被拒,并说清为什么(它会抹掉界面上已有的计划)
        var r = await Write(TodoJson.List());

        Assert.True(r.IsError);
        Assert.Contains("empty", Text(r));
        Assert.Contains("wipes out", Text(r));
    }

    [Fact]
    public async Task One_item_in_progress_is_legal()
    {
        Assert.False((await Write(Todo("跑测试", "in_progress"))).IsError);
    }

    [Fact]
    public async Task Zero_items_in_progress_is_legal_all_pending_or_all_completed()
    {
        Assert.False((await Write(Todo("跑测试", "pending"))).IsError);
        Assert.False((await Write(Todo("跑测试", "completed"))).IsError);
    }

    [Fact]
    public async Task Two_items_in_progress_are_refused_and_the_message_names_which_ones()
    {
        var r = await Write(Todo("改代码", "in_progress"), Todo("跑测试", "in_progress"), Todo("提交", "pending"));

        Assert.True(r.IsError);
        var text = Text(r);
        Assert.Contains("At most one", text);
        Assert.Contains("you sent 2", text);
        // ★ 点名——只说「有多个」的话模型得自己回去数,而它多半会重发一份同样的清单
        Assert.Contains("改代码", text);
        Assert.Contains("跑测试", text);
        Assert.DoesNotContain("提交", text);
        Assert.Contains("pending", text); // 给出改法,不是只说不行
    }

    // ───────────────────────── schema ─────────────────────────

    [Fact]
    public async Task More_than_the_cap_is_bounced_by_the_schema()
    {
        var r = await Write([.. Enumerable.Range(0, Todos.MaxItems + 1).Select(i => Todo($"第 {i} 步", "pending"))]);

        Assert.True(r.IsError);
        Assert.Contains("Invalid arguments", Text(r));
    }

    [Fact]
    public async Task Exactly_the_cap_passes()
    {
        Assert.False((await Write([.. Enumerable.Range(0, Todos.MaxItems).Select(i => Todo($"第 {i} 步", "pending"))])).IsError);
    }

    [Fact]
    public async Task An_unrecognized_status_is_bounced()
    {
        var r = await Write("""{"todos":[{"content":"x","status":"doing","activeForm":"y"}]}""");

        Assert.True(r.IsError);
        Assert.Contains("Invalid arguments", Text(r));
    }

    [Fact]
    public async Task ActiveForm_is_required_without_it_the_UI_line_has_nothing_to_show()
    {
        var r = await Write("""{"todos":[{"content":"x","status":"pending"}]}""");

        Assert.True(r.IsError);
        Assert.Contains("Invalid arguments", Text(r));
    }

    [Fact]
    public async Task An_empty_content_is_bounced()
    {
        Assert.True((await Write("""{"todos":[{"content":"","status":"pending","activeForm":"x"}]}""")).IsError);
    }

    [Fact]
    public async Task Missing_todos_is_bounced_not_treated_as_an_empty_list()
    {
        var r = await Write("{}");

        Assert.True(r.IsError);
        Assert.Contains("Invalid arguments", Text(r));
    }

    // ───────────────────────── 回显 ─────────────────────────

    [Fact]
    public async Task Each_state_has_its_own_mark_in_the_order_the_model_gave()
    {
        var lines = Text(await Write(Todo("读代码", "completed"), Todo("改代码", "in_progress"), Todo("跑测试", "pending"))).Split('\n').ToList();

        Assert.Contains("[x] 读代码", lines);
        Assert.Contains("[~] 改代码", lines);
        Assert.Contains("[ ] 跑测试", lines);
        Assert.True(lines.IndexOf("[x] 读代码") < lines.IndexOf("[~] 改代码"));
    }

    [Fact]
    public async Task The_completed_count_is_done_over_total()
    {
        var r = await Write(Todo("a", "completed"), Todo("b", "completed"), Todo("c", "in_progress"), Todo("d", "pending"));

        Assert.Contains("2/4 completed", Text(r));
    }

    [Fact]
    public async Task A_successful_echo_nudges_to_mark_each_item_done_at_once()
    {
        // ★ 不催一句的话,模型会把一整轮的事做完再一次性全标 completed——界面上就是长时间没动静
        Assert.Contains("As soon as an item is done", Text(await Write(Todo("a", "pending"))));
    }

    [Fact]
    public async Task ActiveForm_stays_out_of_the_echo_it_is_for_the_UI()
    {
        var text = Text(await Write(Todo("跑测试", "pending")));

        Assert.Contains("跑测试", text);
        Assert.DoesNotContain("Doing 跑测试", text);
    }

    [Fact]
    public async Task A_fully_green_list_says_so_and_stops_nudging()
    {
        // 一份已经全绿的清单上再催「做完一项就标掉」没有对象可指,只会把模型推回循环:它反复重发同一份清单
        var r = await Write(Todo("a", "completed"), Todo("b", "completed"));

        Assert.False(r.IsError);
        Assert.Contains("All listed tasks are complete", Text(r));
        Assert.Contains("fully addressed", Text(r));
        Assert.DoesNotContain("As soon as an item is done", Text(r));
    }

    [Fact]
    public async Task An_unfinished_list_keeps_the_per_item_nudge_asks_for_a_check_but_does_not_force_green()
    {
        var text = Text(await Write(Todo("a", "completed"), Todo("b", "pending")));

        Assert.Contains("As soon as an item is done", text);
        Assert.Contains("check it against what actually happened", text);
        Assert.Contains("stay unfinished", text);
        Assert.DoesNotContain("All listed tasks are complete", text);
    }

    // ───────────────────────── 回执里的增量 ─────────────────────────

    private static FixedTranscript Transcript(params Message[] messages) => new(_ => messages);

    private static Message[] Earlier(string name, params (string Content, string Status)[] items) =>
        [Msg.ToolCall("a-before", "before", name, TodoJson.List(items)), Msg.ToolResult("r-before", "before")];

    [Fact]
    public async Task The_previous_list_comes_from_the_transcript_and_the_receipt_reports_what_changed()
    {
        var r = await Write(TodoJson.List(Todo("读代码", "completed"), Todo("跑测试", "pending")), Transcript(Earlier("TodoWrite", Todo("读代码", "pending"))));

        Assert.Contains("1 completed · 1 added", Text(r));
    }

    [Fact]
    public async Task A_dropped_item_is_named_in_the_echo_otherwise_a_task_vanishing_is_only_noticed_by_the_user()
    {
        var r = await Write(TodoJson.List(Todo("读代码", "pending")), Transcript(Earlier("TodoWrite", Todo("读代码", "pending"), Todo("跑测试", "pending"))));

        var text = Text(r);
        Assert.Contains("1 dropped", text);
        Assert.Contains("You dropped 1 item(s)", text);
        Assert.Contains("跑测试", text);
    }

    [Fact]
    public async Task Without_a_transcript_the_receipt_only_counts_and_invents_no_delta()
    {
        var text = Text(await Write(Todo("a", "pending")));

        Assert.Contains("0/1 completed", text);
        Assert.DoesNotContain("dropped", text);
        Assert.DoesNotContain("added", text);
    }

    [Fact]
    public async Task A_transcript_whose_calls_carry_a_different_name_is_not_misread_as_everything_dropped()
    {
        // ★ 撞名后缀会让它一条都取不到:取不到就是「不知道」,不是「全丢了」
        var r = await Write(TodoJson.List(Todo("读代码", "pending")), Transcript(Earlier("TodoWrite_a1b2c3d4", Todo("读代码", "pending"))));

        Assert.DoesNotContain("dropped", Text(r));
    }

    [Fact]
    public async Task The_call_being_executed_does_not_count_as_its_own_previous_list()
    {
        // 这次调用自己已经在库里(assistant 消息先于工具执行落库),但还没有回执
        var own = Msg.ToolCall("a-now", "call-1", "TodoWrite", TodoJson.List(Todo("读代码", "completed")));
        var r = await Write(TodoJson.List(Todo("读代码", "completed")), Transcript(own));

        Assert.Contains("1/1 completed", Text(r));
        Assert.DoesNotContain("1 completed ·", Text(r)); // 没有增量标注:不知道上一份
    }

    [Fact]
    public async Task A_rejected_earlier_call_is_not_the_previous_list()
    {
        var rejected = new[]
        {
            Msg.ToolCall("a1", "k1", "TodoWrite", TodoJson.List(Todo("x", "in_progress"), Todo("y", "in_progress"))),
            Msg.ToolResult("r1", "k1", "At most one…", isError: true),
        };
        var r = await Write(TodoJson.List(Todo("x", "pending")), Transcript(rejected));

        Assert.DoesNotContain("dropped", Text(r));
    }

    // ───────────────────────── 无状态 ─────────────────────────

    [Fact]
    public async Task The_second_call_knows_nothing_about_the_first_the_tool_is_stateless()
    {
        // ★ 钉的是文件头那段设计说明:转录本身就是那份状态。再存一份的话,那一份会在中断 / 重试 / 编辑历史之后和转录分叉——一份谁也不看、却会显示在界面上的「幽灵清单」
        var tool = new TodoWriteTool();
        await Write(Todo("第一批", "completed"));
        var r = await Write(Todo("第二批", "pending"));

        Assert.Contains("第二批", Text(r));
        Assert.DoesNotContain("第一批", Text(r));
        Assert.Contains("0/1 completed", Text(r));
        _ = tool;
    }

    [Fact]
    public async Task Details_carry_the_structured_list_for_the_UI_and_never_reach_the_model()
    {
        var r = await Write(Todo("读代码", "completed"), Todo("改代码", "in_progress"));

        var details = r.Details!.Value;
        Assert.Equal("todo", details.GetProperty("kind").GetString());
        Assert.Equal(2, details.GetProperty("todos").GetArrayLength());
        Assert.Equal("in_progress", details.GetProperty("todos")[1].GetProperty("status").GetString());
        Assert.DoesNotContain("details", Text(r));
    }

    // ───────────────────────── 描述里那三条硬规则 ─────────────────────────

    private static readonly string D = new TodoWriteTool().Info.Description;

    [Fact]
    public void The_description_demands_the_full_list_otherwise_the_model_sends_deltas_and_the_list_shrinks()
    {
        Assert.Contains("send the complete list", D);
    }

    [Fact]
    public void The_description_says_at_most_one_in_progress_the_pair_of_the_validation()
    {
        Assert.Contains("AT MOST ONE item may be in_progress", D);
    }

    [Fact]
    public void The_description_requires_updating_right_after_reading_the_result_and_before_the_next_item()
    {
        Assert.Contains("read the result you just got", D);
        Assert.Contains("before starting the next item", D);
        Assert.Contains("Send progress updates separately from work or verification calls", D);
        Assert.Contains("wait for a successful receipt", D);
    }

    [Fact]
    public void The_description_says_zero_in_progress_is_legal_when_done_or_everything_is_waiting()
    {
        // ★ 只写上界的话,模型会读成「必须一直有一项在动」,于是受阻那一轮把一项其实没在动的勾成进行中——界面上的「正在……」就成了谎话
        Assert.Contains("Zero items in_progress is correct", D);
        Assert.Contains("not started or waiting", D);
    }

    [Fact]
    public void The_description_says_no_item_may_stay_in_progress_when_all_are_complete_and_to_recheck_before_the_final_answer()
    {
        Assert.Contains("no item may be left in_progress", D);
        Assert.Contains("check it against what actually happened", D);
        Assert.Contains("An unchanged, accurate list needs no duplicate call", D);
    }

    [Fact]
    public void The_description_says_prose_is_no_substitute_and_nothing_completes_while_verification_is_in_flight()
    {
        Assert.Contains("is not a substitute for the tool call", D);
        Assert.Contains("still in flight", D);
    }

    [Fact]
    public void The_description_keeps_blocked_failed_or_unverified_items_unfinished_with_the_reason()
    {
        Assert.Contains("blocked, failed, or not verified yet", D);
        Assert.Contains("done AND verified", D);
    }

    [Fact]
    public void The_description_also_says_when_not_to_use_it_otherwise_answering_a_question_needs_a_list_first()
    {
        Assert.Contains("When NOT to use it", D);
    }

    private sealed class Progress : IProgress<ToolProgress>
    {
        public void Report(ToolProgress value)
        {
        }
    }
}
