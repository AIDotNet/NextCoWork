using System.Collections.Immutable;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>
/// 对话镜像的折叠规则(AGENTS.md §3 规则 5–7):重放幂等、状态单调、断开是「未知」、权威广播对账、全量主题只收更新的版本。
/// </summary>
public class ConversationReducerTests
{
    private const string Device = "dev-1";
    private static readonly RunRef Run = new(Device, "r1");
    private static readonly SessionRef Session = new(Device, "s1");

    private static SessionDto Meta(long updatedAt = 1) => new("s1", "w1", null, "chat", "t", null, "sonnet", null, "code", "auto", false, false, 1, updatedAt);

    private static ConversationState WithSession(long queueRev = 0) => ConversationReducer.ApplySession(ConversationState.Empty, Device,
        new SessionTopicState(new SessionPageDto(Meta(), [], false, [], [], []), new QueueDto("s1", queueRev, []), null));

    private static MessageDto Msg(string id, string role = "assistant") => new(id, role, 1, false, "[]");

    private static ConversationState Apply(ConversationState state, long seq, RunEventDto ev) => ConversationReducer.ApplyRunEvent(state, Device, "r1", seq, ev);

    private static RunSnapshotDto Snapshot(string state, long seq, params SeqEventDto[] events) =>
        new("r1", "s1", "w1", null, 0, state, null, 1, null, seq, "fresh", events, [], [], false, state != "running");

    [Fact]
    public void Text_deltas_accumulate_until_the_complete_message_replaces_them_and_lands_in_the_session()
    {
        var state = WithSession();
        state = Apply(state, 1, new RunStartEventDto("r1", "s1", "sonnet"));
        state = Apply(state, 2, new MessageStartEventDto("m1", "assistant"));
        state = Apply(state, 3, new MessageDeltaEventDto("m1", 0, "text", "Hel"));
        state = Apply(state, 4, new MessageDeltaEventDto("m1", 0, "text", "lo"));
        Assert.Equal("Hello", state.Runs[Run].Streaming["m1"]);
        Assert.Equal(RunPhase.Running, state.Runs[Run].Phase);

        state = Apply(state, 5, new MessageEndEventDto(Msg("m1"), null, null, "endTurn"));

        Assert.Empty(state.Runs[Run].Streaming);
        Assert.Equal("m1", Assert.Single(state.Sessions[Session].Messages).Id);
    }

    [Fact]
    public void A_replayed_event_changes_nothing_and_an_old_run_start_cannot_resurrect_a_finished_run()
    {
        var state = WithSession();
        state = Apply(state, 1, new RunStartEventDto("r1", "s1", "sonnet"));
        state = Apply(state, 2, new MessageDeltaEventDto("m1", 0, "text", "x"));
        state = Apply(state, 3, new RunEndEventDto("done"));
        state = Apply(state, 4, new RunSettledEventDto("done"));
        var finished = state;

        // 重放(断线重连后设备补发 / 快照与增量重叠):序号不大于已收到的最后一个 → 丢弃
        Assert.Same(finished, Apply(finished, 2, new MessageDeltaEventDto("m1", 0, "text", "x")));
        Assert.Same(finished, Apply(finished, 1, new RunStartEventDto("r1", "s1", "sonnet")));
        // 就算序号更大(乱序的旧 run_start):阶段只升不降
        var late = Apply(finished, 9, new RunStartEventDto("r1", "s1", "sonnet"));
        Assert.Equal(RunPhase.Done, late.Runs[Run].Phase);
        Assert.True(late.Runs[Run].Settled);
    }

    [Fact]
    public void A_snapshot_rebuilds_the_run_from_its_log_and_never_downgrades_what_is_already_known()
    {
        var snapshot = Snapshot("done", 6,
            new SeqEventDto(1, new RunStartEventDto("r1", "s1", "sonnet")),
            new SeqEventDto(2, new ToolStartEventDto("c1", "Write", "{}", new EnvRefDto(Device, "local", "Local", "local"))),
            new SeqEventDto(3, new ToolEndEventDto("c1", "ok", null, false, null, null, false, 5)),
            new SeqEventDto(4, new MessageEndEventDto(Msg("m1"), null, null, "endTurn")),
            new SeqEventDto(5, new RunEndEventDto("done")),
            new SeqEventDto(6, new RunSettledEventDto("done")));

        var state = ConversationReducer.ApplyRunSnapshot(WithSession(), Device, snapshot, knownRev: 3);

        var view = state.Runs[Run];
        Assert.Equal((RunPhase.Done, 6L, true), (view.Phase, view.Seq, view.Settled));
        Assert.Equal(ToolCallStatus.Done, view.Tools["c1"].Status);
        Assert.Equal("local", view.Tools["c1"].Environment!.Environment);
        Assert.Single(state.Sessions[Session].Messages);

        // 一份更旧的快照(例如重订阅时的竞争)不能把已收口的 Run 改回运行中
        var older = ConversationReducer.ApplyRunSnapshot(state, Device, Snapshot("running", 2, new SeqEventDto(1, new RunStartEventDto("r1", "s1", "sonnet"))), 3);
        Assert.Equal((RunPhase.Done, 6L), (older.Runs[Run].Phase, older.Runs[Run].Seq));
    }

    /// <summary>
    /// 快照的返回值和推送的事件走两条路,返回值可以比它之后的事件晚到(Windows CI 上撞到过:Run 已经收口,流式文本却留着)。
    /// 迟到的旧快照只能补上镜像里没有的东西:已经被 <c>message_end</c> 清掉的流式文本不能回来,已经结束的工具不能变回运行中。
    /// </summary>
    [Fact]
    public void A_late_snapshot_does_not_bring_back_streaming_text_or_a_running_tool_that_newer_events_already_finished()
    {
        var state = WithSession();
        state = Apply(state, 1, new RunStartEventDto("r1", "s1", "sonnet"));
        state = Apply(state, 2, new ToolStartEventDto("c1", "Write", "{}", new EnvRefDto(Device, "local", "Local", "local")));
        state = Apply(state, 3, new MessageDeltaEventDto("m1", 0, "text", "All done."));
        state = Apply(state, 4, new ToolEndEventDto("c1", "ok", null, false, null, null, false, 5));
        state = Apply(state, 5, new MessageEndEventDto(Msg("m1"), null, null, "endTurn"));
        state = Apply(state, 6, new RunEndEventDto("done"));
        Assert.Empty(state.Runs[Run].Streaming);

        // 在第 3 个事件那一刻拍的快照,现在才到
        var late = ConversationReducer.ApplyRunSnapshot(state, Device, Snapshot("running", 3,
            new SeqEventDto(1, new RunStartEventDto("r1", "s1", "sonnet")),
            new SeqEventDto(2, new ToolStartEventDto("c1", "Write", "{}", new EnvRefDto(Device, "local", "Local", "local"))),
            new SeqEventDto(3, new MessageDeltaEventDto("m1", 0, "text", "All done."))), 3);

        var view = late.Runs[Run];
        Assert.Equal((RunPhase.Done, 6L), (view.Phase, view.Seq));
        Assert.Empty(view.Streaming);
        Assert.Equal(ToolCallStatus.Done, view.Tools["c1"].Status);
        Assert.Single(late.Sessions[Session].Messages);
    }

    /// <summary>
    /// 「编辑并重发 / 重新生成」先把历史截断,再起新的一轮。上一轮 Run 的快照这时才到的话,不能把已经被截断掉的回复重新塞回会话:
    /// 镜像早就收过这些事件(序号不大于已收到的 = 重放),快照里的它们同样不该再作用到会话上。
    /// Windows CI 上撞到过:会话里是「旧回复 | 问题 | 新回复」三条。
    /// </summary>
    [Fact]
    public void A_late_snapshot_of_a_finished_run_does_not_put_back_messages_that_were_truncated_since()
    {
        var state = WithSession();
        state = Apply(state, 1, new RunStartEventDto("r1", "s1", "sonnet"));
        state = Apply(state, 2, new MessageEndEventDto(Msg("old-reply"), null, null, "endTurn"));
        state = Apply(state, 3, new RunEndEventDto("done"));
        Assert.Single(state.Sessions[Session].Messages);

        // 重新生成:历史被截断,新的一轮写进了新的提问
        state = ConversationReducer.ApplyHistoryReset(state, Device, new SessionPageDto(Meta(), [Msg("question", "user")], false, [], [], []));

        var late = ConversationReducer.ApplyRunSnapshot(state, Device, Snapshot("done", 3,
            new SeqEventDto(1, new RunStartEventDto("r1", "s1", "sonnet")),
            new SeqEventDto(2, new MessageEndEventDto(Msg("old-reply"), null, null, "endTurn")),
            new SeqEventDto(3, new RunEndEventDto("done"))), 3);

        Assert.Equal(["question"], late.Sessions[Session].Messages.Select(m => m.Id));
        Assert.Equal(RunPhase.Done, late.Runs[Run].Phase);
    }

    [Fact]
    public void Starting_a_run_gives_an_immediate_running_mirror_that_the_first_events_then_extend()
    {
        var state = ConversationReducer.ApplyRunStarted(WithSession(), Run, "s1", knownRev: 4);
        Assert.Equal(RunPhase.Running, state.Runs[Run].Phase);
        Assert.Equal(4, state.Runs[Run].StartedAtRev);

        // 事件先于返回值到达:不重建
        var withEvents = Apply(state, 1, new RunStartEventDto("r1", "s1", "sonnet"));
        Assert.Same(withEvents, ConversationReducer.ApplyRunStarted(withEvents, Run, "s1", knownRev: 9));
    }

    [Fact]
    public void A_disconnect_makes_running_runs_unknown_not_finished_and_the_resume_restores_them()
    {
        var running = Apply(WithSession(), 1, new RunStartEventDto("r1", "s1", "sonnet"));
        var other = ConversationReducer.ApplyRunStarted(running, new RunRef("dev-2", "x"), "s9", 0);
        var finished = ConversationReducer.ApplyRunStarted(other, new RunRef(Device, "done"), "s2", 0);
        finished = ConversationReducer.ApplyRunEvent(finished, Device, "done", 1, new RunEndEventDto("done"));

        var unknown = ConversationReducer.MarkDeviceUnknown(finished, Device);

        Assert.Equal(RunPhase.Unknown, unknown.Runs[Run].Phase);
        Assert.Equal(RunPhase.Done, unknown.Runs[new RunRef(Device, "done")].Phase);          // 已收口的不动
        Assert.Equal(RunPhase.Running, unknown.Runs[new RunRef("dev-2", "x")].Phase);        // 别的设备不动

        // 重连后续传:运行中的事件把它拉回运行中;终态压过一切
        var back = Apply(unknown, 2, new TurnStartEventDto(0));
        Assert.Equal(RunPhase.Unknown, back.Runs[Run].Phase); // turn_start 不改阶段
        var resumed = Apply(back, 3, new RunStartEventDto("r1", "s1", "sonnet"));
        Assert.Equal(RunPhase.Running, resumed.Runs[Run].Phase);
        Assert.Equal(RunPhase.Aborted, Apply(resumed, 4, new RunEndEventDto("aborted")).Runs[Run].Phase);
    }

    private static ActiveRunIndex Index(long rev, params string[] liveRuns) =>
        new(Device, [.. liveRuns.Select(r => new ActiveRun(Device, r, "s1", "w1", 0))]) { Rev = rev };

    [Fact]
    public void The_authoritative_broadcast_closes_a_run_whose_end_event_was_lost_but_a_stale_one_cannot()
    {
        // 控制端在广播版本 5 的时候发起了这个 Run
        var state = ConversationReducer.ApplyRunStarted(WithSession(), Run, "s1", knownRev: 5);

        // 版本不更新的广播里没有它是正常的(刚起的 Run 还没出现在那份旧广播里)→ 不能据此判结束
        Assert.Equal(RunPhase.Running, ConversationReducer.Reconcile(state, Index(5)).Runs[Run].Phase);
        // 更新的广播里还有它 → 仍在运行
        Assert.Equal(RunPhase.Running, ConversationReducer.Reconcile(state, Index(6, "r1")).Runs[Run].Phase);

        // 更新的广播里没有它 → 已经结束(哪怕 run_end 丢了):收口,不再算运行中
        var closedState = ConversationReducer.Reconcile(state, Index(7));
        var closed = closedState.Runs[Run];
        Assert.Equal(RunPhase.Done, closed.Phase);
        Assert.True(closed.Settled);
        Assert.True(closed.PhaseGuessed);

        // 广播比事件先到是常态(不同主题):随后到的真实结局替换推断,不会把「已中止」锁成「已完成」;真实终态之后推断不再生效
        var real = Apply(closedState, 3, new RunEndEventDto("aborted"));
        Assert.Equal((RunPhase.Aborted, false), (real.Runs[Run].Phase, real.Runs[Run].PhaseGuessed));
        Assert.Equal(RunPhase.Aborted, ConversationReducer.Reconcile(real, Index(9)).Runs[Run].Phase);
        var viaSnapshot = ConversationReducer.ApplyRunSnapshot(closedState, Device, Snapshot("error", 4, new SeqEventDto(4, new RunEndEventDto("error"))), 5);
        Assert.Equal((RunPhase.Error, false), (viaSnapshot.Runs[Run].Phase, viaSnapshot.Runs[Run].PhaseGuessed));

        // 断线后未知的 Run:广播里有 → 恢复运行中;没有 → 收口
        var unknown = ConversationReducer.MarkDeviceUnknown(state, Device);
        Assert.Equal(RunPhase.Running, ConversationReducer.Reconcile(unknown, Index(8, "r1")).Runs[Run].Phase);
        Assert.Equal(RunPhase.Done, ConversationReducer.Reconcile(unknown, Index(8)).Runs[Run].Phase);

        // 别的设备的广播不碰这台设备的 Run
        Assert.Same(state, ConversationReducer.Reconcile(state, new ActiveRunIndex("dev-2", []) { Rev = 99 }));
    }

    [Fact]
    public void Full_state_topics_only_accept_the_newer_version()
    {
        var interactions = ConversationReducer.ApplyInteractions(ConversationState.Empty, Device,
            new InteractionsDto(5, [new GoalProposalInteractionDto { Id = "i1", RunId = "r1", SessionId = "s1", Condition = "c", Version = 5 }]));
        var stale = ConversationReducer.ApplyInteractions(interactions, Device, new InteractionsDto(3, []));
        Assert.Same(interactions, stale);
        Assert.Empty(ConversationReducer.ApplyInteractions(interactions, Device, new InteractionsDto(6, [])).InteractionsOf(Device).Pending);
        Assert.Empty(ConversationState.Empty.InteractionsOf("never-seen").Pending);

        var session = WithSession(queueRev: 10);
        var item = new QueueItemDto("q1", [new InputPartDto(InputPartKinds.Text, "x")], QueueStates.Pending, 0, 1, null, new SendOptionsDto("sonnet"));
        Assert.Same(session, ConversationReducer.ApplyQueue(session, Device, new QueueDto("s1", 9, [item])));
        Assert.Single(ConversationReducer.ApplyQueue(session, Device, new QueueDto("s1", 11, [item])).Sessions[Session].Queue.Items);

        // 更新较旧的元数据不覆盖新的
        var renamed = ConversationReducer.ApplySessionUpdated(session, Device, Meta(5) with { Title = "new" });
        Assert.Equal("t", ConversationReducer.ApplySessionUpdated(renamed, Device, Meta(2) with { Title = "old" }).Sessions[Session].Meta.Title.Replace("new", "t"));
    }

    [Fact]
    public void Messages_upsert_by_id_history_reset_replaces_and_deleted_sessions_vanish()
    {
        var state = WithSession();
        state = ConversationReducer.ApplyMessage(state, Session, Msg("m1"));
        state = ConversationReducer.ApplyMessage(state, Session, Msg("m2"));
        state = ConversationReducer.ApplyMessage(state, Session, Msg("m1") with { PartsJson = "[1]" }); // 同 id:替换,不重复
        Assert.Equal(["m1", "m2"], state.Sessions[Session].Messages.Select(m => m.Id));
        Assert.Equal("[1]", state.Sessions[Session].Messages[0].PartsJson);

        state = ConversationReducer.ApplyHistoryReset(state, Device, new SessionPageDto(Meta(), [Msg("m9")], true, [], [], []));
        Assert.Equal(["m9"], state.Sessions[Session].Messages.Select(m => m.Id));
        Assert.True(state.Sessions[Session].HasMore);

        // 没打开的会话收到消息:不凭空建出一个会话
        Assert.Empty(ConversationReducer.ApplyMessage(ConversationState.Empty, Session, Msg("x")).Sessions);
        Assert.Empty(ConversationReducer.RemoveSession(state, Session).Sessions);
    }
}
