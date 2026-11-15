using System.Text.Json;
using Ncw.Core.Domain;

namespace Ncw.Core.Tests;

/// <summary>
/// 待办的派生纯函数(<see cref="Todos"/>)。2.x 的 <c>shared/__tests__/todo.test.ts</c> 全部用例,加上 <c>todo-derive.test.ts</c> 里「取最近一次 / 什么样的调用不算数 / 入参来自模型」那几组
/// (它们钉的是 <c>latestTodosFrom</c>,现在住在这里:Agent 的状态块 / 补报 / 压缩附件和客户端的清单卡片读同一份)。
/// </summary>
public class TodoDerivationTests
{
    private const string Tool = "TodoWrite";
    private static int _n;

    private static TodoItem Item(string content, TodoStatus status) => new(content, status, $"Doing {content}");

    private static JsonElement List(params (string Content, string Status)[] items)
    {
        var node = new System.Text.Json.Nodes.JsonObject
        {
            ["todos"] = new System.Text.Json.Nodes.JsonArray([.. items.Select(i => (System.Text.Json.Nodes.JsonNode?)new System.Text.Json.Nodes.JsonObject
            {
                ["content"] = i.Content,
                ["status"] = i.Status,
                ["activeForm"] = $"正在{i.Content}",
            })]),
        };
        return JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
    }

    private static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>一对「模型发起调用」+「工具回执」。<paramref name="isError"/> 决定这次调用算不算数。</summary>
    private static TodoEvent[] Call(JsonElement input, bool isError = false, string name = Tool, string? callId = null)
    {
        callId ??= $"c{++_n}";
        return [new TodoEvent.Call(callId, name, input), new TodoEvent.Result(callId, isError)];
    }

    // ───────────────────────── todoPlan:这次更新改了什么 ─────────────────────────

    [Fact]
    public void Plan_marks_added_removed_newly_completed_and_newly_started()
    {
        var plan = Todos.Plan(
            [Item("a", TodoStatus.Completed), Item("b", TodoStatus.InProgress), Item("c", TodoStatus.Pending)],
            [Item("a", TodoStatus.Pending), Item("b", TodoStatus.Pending), Item("gone", TodoStatus.Pending)]);

        Assert.Equal(1, plan.NewlyCompleted);
        Assert.Equal(1, plan.NewlyStarted);
        Assert.Equal(["c"], plan.Added.Select(t => t.Content));
        Assert.Equal(["gone"], plan.Removed.Select(t => t.Content));
    }

    [Fact]
    public void Plan_with_an_unknown_previous_list_is_all_empty_not_everything_added()
    {
        // ★ 没有上一份(老转录、历史被压缩)时把整份清单标成新增是错的,而错的增量比没有增量更糟:用户会以为模型刚刚凭空加了五项
        var plan = Todos.Plan([Item("a", TodoStatus.Completed), Item("b", TodoStatus.Pending)], null);

        Assert.Equal(TodoChange.None, plan);
        Assert.Empty(plan.Added);
        Assert.Empty(plan.Removed);
    }

    [Fact]
    public void Plan_ignores_items_whose_status_did_not_change()
    {
        var same = new[] { Item("a", TodoStatus.Completed), Item("b", TodoStatus.Pending) };
        var plan = Todos.Plan(same, [Item("a", TodoStatus.Completed), Item("b", TodoStatus.Pending)]);

        Assert.Equal(0, plan.NewlyCompleted);
        Assert.Equal(0, plan.NewlyStarted);
        Assert.Empty(plan.Added);
        Assert.Empty(plan.Removed);
    }

    [Fact]
    public void Plan_does_not_count_a_reorder_as_a_change()
    {
        var plan = Todos.Plan([Item("b", TodoStatus.Pending), Item("a", TodoStatus.Pending)], [Item("a", TodoStatus.Pending), Item("b", TodoStatus.Pending)]);

        Assert.Empty(plan.Added);
        Assert.Empty(plan.Removed);
    }

    [Fact]
    public void Plan_consumes_duplicate_contents_one_to_one()
    {
        var plan = Todos.Plan([Item("same", TodoStatus.Pending), Item("same", TodoStatus.Pending)], [Item("same", TodoStatus.Pending)]);

        Assert.Equal(["same"], plan.Added.Select(t => t.Content));
        Assert.Empty(plan.Removed);
    }

    [Fact]
    public void Plan_counts_new_completed_and_new_in_progress_items_as_just_completed_and_just_started()
    {
        var plan = Todos.Plan([Item("new", TodoStatus.Completed), Item("run", TodoStatus.InProgress)], []);

        Assert.Equal(1, plan.NewlyCompleted);
        Assert.Equal(1, plan.NewlyStarted);
        Assert.Equal(2, plan.Added.Count);
    }

    [Fact]
    public void Plan_moving_an_item_back_from_completed_counts_as_neither()
    {
        // 「模型把做完的项改回未完成」不该被算成完成,也不该被算成开始
        var plan = Todos.Plan([Item("a", TodoStatus.Pending)], [Item("a", TodoStatus.Completed)]);

        Assert.Equal(0, plan.NewlyCompleted);
        Assert.Equal(0, plan.NewlyStarted);
        Assert.Empty(plan.Added);
        Assert.Empty(plan.Removed);
    }

    // ───────────────────────── todoHistoryLookup:按 callId 查「这次之前那份」 ─────────────────────────

    [Fact]
    public void History_lookup_gives_each_call_the_list_before_itself_not_the_global_latest()
    {
        var first = Call(List(("a", "pending")), callId: "k1");
        var second = Call(List(("a", "completed"), ("b", "in_progress")), callId: "k2");
        var lookup = Todos.HistoryLookup([.. first, .. second]);

        Assert.Null(lookup("k1"));
        Assert.Equal([Item("a", TodoStatus.Pending) with { ActiveForm = "正在a" }], lookup("k2"));
    }

    [Fact]
    public void History_lookup_skips_a_rejected_call_as_the_previous_list()
    {
        var events = new List<TodoEvent>();
        events.AddRange(Call(List(("a", "pending")), callId: "k1"));
        events.AddRange(Call(List(("bad", "in_progress"), ("bad2", "in_progress")), isError: true, callId: "k2"));
        events.AddRange(Call(List(("a", "completed")), callId: "k3"));
        var lookup = Todos.HistoryLookup(events);

        Assert.Equal("a", lookup("k3")!.Single().Content);
        Assert.Equal(TodoStatus.Pending, lookup("k3")!.Single().Status);
    }

    [Fact]
    public void History_lookup_does_not_count_a_call_with_no_result_yet_as_the_previous_list()
    {
        // 中断时最后一条 tool_call 就是孤儿
        var events = new List<TodoEvent>();
        events.AddRange(Call(List(("a", "pending")), callId: "k1"));
        events.Add(new TodoEvent.Call("orphan", Tool, List(("new", "pending"))));
        var lookup = Todos.HistoryLookup(events);

        Assert.Equal("a", lookup("orphan")!.Single().Content);
    }

    [Fact]
    public void History_lookup_of_an_unknown_call_id_is_null()
    {
        Assert.Null(Todos.HistoryLookup([])("nope"));
    }

    [Fact]
    public void History_lookup_groups_by_the_name_each_call_carries_so_a_suffixed_name_is_its_own_group()
    {
        // ★ 渲染层拿不到工具注册表,所以它不可能按 internalId 查。按转录里每条调用自己带的名字分组,撞名后缀在这一侧不存在
        var hashed = Call(List(("x", "pending")), name: "TodoWrite_a1b2c3d4", callId: "h1");
        var lookup = Todos.HistoryLookup([.. hashed, .. Call(List(("y", "pending")), callId: "k2")]);

        Assert.Null(lookup("h1"));
        Assert.Null(lookup("k2")); // 另一个名字的历史互不干扰
        Assert.Equal("x", Todos.Latest(hashed, "TodoWrite_a1b2c3d4")!.Single().Content);
    }

    // ───────────────────────── latestTodosFrom:取最近一次 ─────────────────────────

    [Fact]
    public void Latest_takes_the_later_list_over_the_earlier_one()
    {
        var events = new List<TodoEvent>();
        events.AddRange(Call(List(("读代码", "completed"))));
        events.AddRange(Call(List(("读代码", "completed"), ("写代码", "in_progress"))));

        Assert.Equal(2, Todos.Latest(events, Tool)!.Count);
    }

    [Fact]
    public void Marks_are_shared_between_the_context_snapshot_and_the_tool_echo()
    {
        Assert.Equal("[x]", Todos.Mark(TodoStatus.Completed));
        Assert.Equal("[~]", Todos.Mark(TodoStatus.InProgress));
        Assert.Equal("[ ]", Todos.Mark(TodoStatus.Pending));
    }

    [Fact]
    public void Latest_ignores_a_call_the_tool_rejected_and_takes_the_earlier_successful_one()
    {
        // ★ 循环是先提交 tool_call、后校验的:「两个 in_progress」这种被明确拒绝的列表原样躺在转录里。盲取最近一条 = 把工具拒绝过的东西当成当前进度
        var events = new List<TodoEvent>();
        events.AddRange(Call(List(("读代码", "in_progress"))));
        events.AddRange(Call(List(("A", "in_progress"), ("B", "in_progress"), ("C", "in_progress")), isError: true));

        Assert.Equal([new TodoItem("读代码", TodoStatus.InProgress, "正在读代码")], Todos.Latest(events, Tool));
    }

    [Fact]
    public void Latest_ignores_a_call_with_no_result_yet()
    {
        var events = new List<TodoEvent>();
        events.AddRange(Call(List(("读代码", "completed"))));
        events.Add(new TodoEvent.Call("orphan", Tool, List(("新的", "pending"))));

        Assert.Equal("读代码", Todos.Latest(events, Tool)![0].Content);
    }

    [Fact]
    public void Latest_with_a_name_that_does_not_match_is_silently_nothing()
    {
        // ★ 转录里存的是外部名。撞名时它带哈希后缀,那时字面匹配会永远返回 null,而且不报任何错——所以调用方必须按注册表查。这条用例把这个失败模式本身写下来
        var events = Call(List(("读代码", "completed")));

        Assert.Null(Todos.Latest(events, "TodoWrite_a1b2c3d4"));
    }

    [Fact]
    public void Latest_of_nothing_is_null()
    {
        Assert.Null(Todos.Latest([], Tool));
        Assert.Null(Todos.Latest([new TodoEvent.Call("x", "Other", List(("a", "pending")))], Tool));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"hi\"")]
    [InlineData("{\"todos\":\"hi\"}")]
    [InlineData("{\"todos\":[]}")]
    [InlineData("{\"todos\":[null]}")]
    [InlineData("{\"todos\":[{\"content\":\"缺 activeForm\",\"status\":\"pending\"}]}")]
    [InlineData("{\"todos\":[{\"content\":\"x\",\"status\":\"进行中\",\"activeForm\":\"x\"}]}")]
    public void A_bad_shape_from_the_model_is_not_a_list_and_never_throws(string json)
    {
        Assert.Null(Todos.Latest(Call(Raw(json)), Tool));
    }

    [Fact]
    public void Good_items_stay_and_bad_ones_are_skipped_half_a_list_beats_none()
    {
        var r = Todos.Latest(Call(Raw("""{"todos":[{"content":"A","status":"pending","activeForm":"正在 A"},42]}""")), Tool);

        Assert.Equal([new TodoItem("A", TodoStatus.Pending, "正在 A")], r);
    }

    [Fact]
    public void Items_over_the_cap_are_cut_even_when_the_model_bypasses_the_schema_limit()
    {
        var many = Enumerable.Range(0, Todos.MaxItems + 20).Select(i => ($"t{i}", "pending")).ToArray();

        Assert.Equal(Todos.MaxItems, Todos.Latest(Call(List(many)), Tool)!.Count);
    }

    [Fact]
    public void Only_tool_calls_count_text_that_merely_mentions_todos_does_not()
    {
        // 一条 text part 里写着 todos 不算:映射层只把 tool_call / tool_result 压成事件,所以事件序列里根本没有它
        Assert.Null(Todos.Latest([], Tool));
    }

    // ───────────────────────── 视图(客户端的清单卡片) ─────────────────────────

    [Fact]
    public void The_view_counts_done_picks_the_current_item_and_knows_when_everything_is_complete()
    {
        var view = TodoView.Of([Item("a", TodoStatus.Completed), Item("b", TodoStatus.InProgress), Item("c", TodoStatus.Pending)]);

        Assert.Equal(1, view.Completed);
        Assert.Equal(3, view.Total);
        Assert.Equal("b", view.Current!.Content);
        Assert.False(view.IsComplete);
        Assert.True(TodoView.Of([Item("a", TodoStatus.Completed)]).IsComplete);
        Assert.False(TodoView.Of([]).IsComplete); // 空清单不是一份清单
    }

    [Fact]
    public void Wire_names_round_trip()
    {
        foreach (var status in Enum.GetValues<TodoStatus>())
        {
            Assert.True(Todos.TryParseStatus(Todos.WireName(status), out var back));
            Assert.Equal(status, back);
        }

        Assert.False(Todos.TryParseStatus("doing", out _));
    }
}
