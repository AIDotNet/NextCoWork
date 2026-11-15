using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>
/// 目标与待办的镜像。目标:首帧(<c>goal</c> 主题快照、<c>goal.get</c>)和后续变更(<c>goal.changed</c>)是同一个全量类型,key 带 <c>deviceId</c>,只收版本不更旧的,清除留墓碑,断线是未知。
/// 待办:没有表,是会话消息的派生视图(Core 的 <see cref="Todos"/> / <see cref="TodoView"/>),只认最近一次<b>成功</b>的 <c>TodoWrite</c>。
/// </summary>
public class GoalTodoMirrorTests
{
    private const string Device = "dev-1";
    private static readonly SessionRef Session = new(Device, "s1");

    private static SessionDto Meta() => new("s1", "w1", null, "chat", "t", null, "sonnet", null, "code", "auto", false, false, 1, 1);

    private static ConversationState WithSession(params MessageDto[] messages) => ConversationReducer.ApplySession(
        ConversationState.Empty, Device, new SessionTopicState(new SessionPageDto(Meta(), messages, false, [], [], []), new QueueDto("s1", 0, []), null));

    private static GoalDto Goal(string id = "g1", string condition = "make the tests green", int iterations = 0, string? reason = null, string origin = "user") =>
        new("s1", id, condition, origin, 1_000, 50, iterations, reason, null, 0, null, 0, 0);

    private static GoalChangeDto Change(GoalDto? goal, long rev, string? cleared = null, MessageDto? message = null) => new("s1", goal, cleared, message, rev);

    // ───────────────────────── 目标 ─────────────────────────

    [Fact]
    public void A_goal_change_is_a_full_replacement_the_core_type_with_every_field_and_the_clear_reason_survives()
    {
        var state = OrchestrationReducer.ApplyGoalChange(WithSession(), Device, Change(Goal(iterations: 1, reason: "tests are still red"), rev: 3));
        var goal = state.GoalOf(Session)!;
        Assert.Equal(("g1", "make the tests green", GoalOrigin.User, 1, "tests are still red", 50L), (goal.Id, goal.Condition, goal.Origin, goal.Iterations, goal.LastReason, goal.TokensAtStart));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_000), goal.SetAt);

        // 全量替换:下一份是新目标(另一个 id),旧的字段不残留
        state = OrchestrationReducer.ApplyGoalChange(state, Device, Change(Goal("g2", "ship it"), rev: 4));
        Assert.Equal(("g2", "ship it", 0, (string?)null), (state.GoalOf(Session)!.Id, state.GoalOf(Session)!.Condition, state.GoalOf(Session)!.Iterations, state.GoalOf(Session)!.LastReason));

        state = OrchestrationReducer.ApplyGoalChange(state, Device, Change(null, rev: 5, cleared: "met"));
        Assert.Null(state.GoalOf(Session));
        Assert.Equal(GoalClearReason.Met, state.Goals[Session].Cleared);
    }

    [Fact]
    public void Only_a_version_that_is_not_older_is_accepted_and_a_clear_leaves_a_tombstone_that_a_late_set_cannot_resurrect()
    {
        var state = OrchestrationReducer.ApplyGoalChange(WithSession(), Device, Change(Goal(), rev: 5));
        state = OrchestrationReducer.ApplyGoalChange(state, Device, Change(null, rev: 7, cleared: "userClear"));

        // 乱序到达的旧设定(rev 6)和旧的 goal.get 结果:被墓碑挡住
        var late = OrchestrationReducer.ApplyGoalChange(state, Device, Change(Goal("g-old"), rev: 6));
        Assert.Same(state, late);
        Assert.Same(state, OrchestrationReducer.ApplyGoalState(state, Device, new GoalStateDto("s1", Goal("g-old"), 6)));
        Assert.Null(late.GoalOf(Session));

        // goal.get 的结果优先级更低:同版本时以事件为准(设备的版本号是读目标之后读的,可能比那份目标更新)
        var event7 = OrchestrationReducer.ApplyGoalChange(WithSession(), Device, Change(Goal("from-event"), rev: 7));
        Assert.Equal("from-event", OrchestrationReducer.ApplyGoalState(event7, Device, new GoalStateDto("s1", Goal("stale-get"), 7)).GoalOf(Session)!.Id);
        Assert.Equal("fresh-get", OrchestrationReducer.ApplyGoalState(event7, Device, new GoalStateDto("s1", Goal("fresh-get"), 8)).GoalOf(Session)!.Id);

        // 同版本再来一遍是幂等的;更新的设定正常进来
        Assert.Same(state, OrchestrationReducer.ApplyGoalChange(state, Device, Change(null, rev: 7, cleared: "userClear")));
        Assert.Equal("g-new", OrchestrationReducer.ApplyGoalChange(state, Device, Change(Goal("g-new"), rev: 8)).GoalOf(Session)!.Id);
    }

    [Fact]
    public void The_snapshot_and_goal_get_use_the_same_rule_and_a_goal_missing_from_a_newer_snapshot_is_cleared()
    {
        var state = OrchestrationReducer.ApplyGoalsSnapshot(WithSession(), Device, new GoalsTopicState(4, [Goal()]));
        Assert.Equal("g1", state.GoalOf(Session)!.Id);
        // goal.get 的结果与快照同一个类型规则
        Assert.Equal("g9", OrchestrationReducer.ApplyGoalState(state, Device, new GoalStateDto("s1", Goal("g9"), 6)).GoalOf(Session)!.Id);

        // 重连后的快照里已经没有这个目标(断线期间清掉了):镜像里的清掉;别的设备的不动
        var other = OrchestrationReducer.ApplyGoalChange(state, "dev-2", Change(Goal("on-dev-2"), rev: 2));
        var after = OrchestrationReducer.ApplyGoalsSnapshot(other, Device, new GoalsTopicState(9, []));
        Assert.Null(after.GoalOf(Session));
        Assert.Equal("on-dev-2", after.GoalOf(new SessionRef("dev-2", "s1"))!.Id);
        // 比镜像更旧的快照不能把目标清掉
        Assert.Equal("g1", OrchestrationReducer.ApplyGoalsSnapshot(state, Device, new GoalsTopicState(2, [])).GoalOf(Session)!.Id);
    }

    [Fact]
    public void A_goal_marker_message_written_out_of_band_replaces_the_message_in_the_session_mirror()
    {
        var plain = new MessageDto("a1", "assistant", 1, false, "[{\"type\":\"text\",\"text\":\"done\"}]");
        var stamped = plain with { PartsJson = "[{\"type\":\"text\",\"text\":\"done\"},{\"type\":\"goal_status\",\"met\":true,\"condition\":\"x\"}]" };
        var state = OrchestrationReducer.ApplyGoalChange(WithSession(plain), Device, Change(null, rev: 3, cleared: "met", message: stamped));

        Assert.Equal(stamped.PartsJson, Assert.Single(state.Sessions[Session].Messages).PartsJson); // 按 id 替换,不新增消息
    }

    [Fact]
    public void A_dropped_connection_makes_the_goal_unknown_not_gone_and_a_newer_snapshot_restores_it()
    {
        var state = OrchestrationReducer.ApplyGoalChange(WithSession(), Device, Change(Goal(), rev: 3));

        var unknown = ConversationReducer.MarkDeviceUnknown(state, Device);
        Assert.True(unknown.Goals[Session].Unknown);
        Assert.NotNull(unknown.GoalOf(Session)); // 不判成清除

        var back = OrchestrationReducer.ApplyGoalsSnapshot(unknown, Device, new GoalsTopicState(4, [Goal(iterations: 2)]));
        Assert.False(back.Goals[Session].Unknown);
        Assert.Equal(2, back.GoalOf(Session)!.Iterations);
    }

    [Fact]
    public void Goal_enums_round_trip_through_the_one_wire_table_and_unknown_values_read_as_unknown()
    {
        foreach (var origin in Enum.GetValues<GoalOrigin>())
        {
            Assert.Equal(origin, GoalWire.ParseOrigin(GoalWire.ToWire(origin)));
        }

        foreach (var reason in Enum.GetValues<GoalClearReason>())
        {
            Assert.Equal(reason, GoalWire.ParseClearReason(GoalWire.ToWire(reason)));
        }

        Assert.Null(GoalWire.ParseOrigin("something-new"));
        Assert.Null(GoalWire.ParseClearReason(null));
        // 不认识的来源不报错:按「已恢复」处理
        Assert.Equal(GoalOrigin.Restored, OrchestrationReducer.ToGoal(Goal(origin: "something-new")).Origin);
    }

    // ───────────────────────── 待办 ─────────────────────────

    private const string Unfinished = """{"todos":[{"content":"read the code","status":"completed","activeForm":"Reading the code"},{"content":"fix the bug","status":"in_progress","activeForm":"Fixing the bug"}]}""";
    private const string Finished = """{"todos":[{"content":"read the code","status":"completed","activeForm":"Reading the code"},{"content":"fix the bug","status":"completed","activeForm":"Fixing the bug"}]}""";

    private static MessageDto TodoCall(string id, string callId, string input) =>
        new(id, "assistant", 1, false, $"[{{\"type\":\"tool_call\",\"callId\":\"{callId}\",\"name\":\"TodoWrite\",\"input\":{input}}}]");

    private static MessageDto Result(string id, string callId, bool isError = false) =>
        new(id, "tool", 2, false, $"[{{\"type\":\"tool_result\",\"callId\":\"{callId}\",\"output\":{{\"content\":\"ok\"}},\"isError\":{(isError ? "true" : "false")}}}]");

    [Fact]
    public void The_todo_view_is_derived_from_the_latest_successful_TodoWrite_and_follows_every_message_that_arrives()
    {
        var state = WithSession();
        Assert.Null(state.Sessions[Session].Todo);

        // 调用先到、回执还没有:还没有「成功」的清单(中断时的孤儿也不算)
        state = ConversationReducer.ApplyMessage(state, Session, TodoCall("m1", "t1", Unfinished));
        Assert.Null(state.Sessions[Session].Todo);

        // 回执成功了:清单出现(Core 的视图:进度、当前项、是否全部完成)
        state = ConversationReducer.ApplyMessage(state, Session, Result("m2", "t1"));
        var todo = state.Sessions[Session].Todo!;
        Assert.Equal((1, 2, "fix the bug", false), (todo.Completed, todo.Total, todo.Current!.Content, todo.IsComplete));

        // 被工具拒绝的新清单(循环先提交后校验,拒绝的清单原样躺在转录里)不算数:仍是上一份
        state = ConversationReducer.ApplyMessage(state, Session, TodoCall("m3", "t2", """{"todos":[]}"""));
        state = ConversationReducer.ApplyMessage(state, Session, Result("m4", "t2", isError: true));
        Assert.Equal(1, state.Sessions[Session].Todo!.Completed);

        // 成功的新清单替换它
        state = ConversationReducer.ApplyMessage(state, Session, TodoCall("m5", "t3", Finished));
        state = ConversationReducer.ApplyMessage(state, Session, Result("m6", "t3"));
        Assert.True(state.Sessions[Session].Todo!.IsComplete);
        Assert.Null(state.Sessions[Session].Todo!.Current);
    }

    [Fact]
    public void The_todo_view_is_rebuilt_from_a_session_snapshot_and_a_history_reset_and_unrelated_messages_leave_it_alone()
    {
        var messages = new[] { TodoCall("m1", "t1", Unfinished), Result("m2", "t1") };
        var state = WithSession(messages);
        Assert.Equal(2, state.Sessions[Session].Todo!.Total);

        // 和待办无关的消息:同一个视图对象(没有无谓的变化通知)
        var todo = state.Sessions[Session].Todo;
        var plain = ConversationReducer.ApplyMessage(state, Session, new MessageDto("m3", "assistant", 3, false, "[{\"type\":\"text\",\"text\":\"hello\"}]"));
        Assert.Same(todo, plain.Sessions[Session].Todo);

        // 历史被改写(删除这一轮):清单跟着没了——待办没有表,持久化 = 转录本身
        var reset = ConversationReducer.ApplyHistoryReset(state, Device, new SessionPageDto(Meta(), [], false, [], [], []));
        Assert.Null(reset.Sessions[Session].Todo);
    }
}
