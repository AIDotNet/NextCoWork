using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>
/// 子代理卡片的镜像(AGENTS.md §3 规则 5–7)。移植自 2.x 的 <c>subagent-sync</c>(权威广播收口)、<c>subagent-panel-feed</c>(双份投喂)、<c>subagent-card</c>(卡片的展示判据)、
/// <c>subagent-report-row</c>(汇报行与全文);归约规则全在 Core 的 <see cref="SubagentView"/>,这里验证的是「事件 → 参数」的接线、卡片的键、对账与单调。
/// 逐字转录面板 / DOM 级的展开与按钮不在这里(那是 UI 层);面板「点开才订阅」和全文读取的动作在 <c>Ncw.Integration.Tests</c>。
/// </summary>
public class SubagentMirrorTests
{
    private const string Device = "dev-1";
    private const string ParentSession = "parent-session";
    private const string Child = "parent-run:sub:1";
    private const string ChildSession = "parent-session:sub:parent-run:sub:1";
    private const string Call = "task-1";
    private static readonly SessionRef Parent = new(Device, ParentSession);
    private static readonly SubagentRef Card = new(Device, ParentSession, Call);

    private static SessionDto Meta(string id) => new(id, "w1", null, "chat", "t", null, "sonnet", null, "code", "auto", false, false, 1, 1);

    private static ConversationState WithSession(string id = ParentSession, params MessageDto[] messages) => ConversationReducer.ApplySession(
        ConversationState.Empty, Device, new SessionTopicState(new SessionPageDto(Meta(id), messages, false, [], [], []), new QueueDto(id, 0, []), null));

    private static ConversationState Apply(ConversationState state, long seq, RunEventDto ev, long knownRev = 1) =>
        ConversationReducer.ApplyRunEvent(state, Device, "parent-run", seq, ev, knownRev);

    private static ConversationState Start(ConversationState state, long seq = 1, bool background = false, long at = 10_000) =>
        Apply(Apply(state, 1, new RunStartEventDto("parent-run", ParentSession, "sonnet")), seq + 1,
            new SubagentStartEventDto(Call, Child, ChildSession, "general-purpose", "查配置读取处", "blue", "sonnet", background, at));

    private static ConversationState ActiveIndex(ConversationState state, long rev, params string[] liveChildren) =>
        ConversationReducer.Reconcile(state, new ActiveRunIndex(Device, [new ActiveRun(Device, "parent-run", ParentSession, "w1", 0)])
        {
            Subagents = [.. liveChildren.Select(c => new ActiveSubagent(Device, c, "parent-run", ParentSession, "w1", DateTimeOffset.UnixEpoch))],
            Rev = rev,
        });

    private static MessageDto Receipt(string status, bool background = true, string? summary = "all done", string? reportStatus = null)
    {
        var sub = $"{{\"childRunId\":\"{Child}\",\"status\":\"{status}\"" + (summary is null ? "" : $",\"summary\":\"{summary}\"") + (background ? ",\"background\":true" : "") + (reportStatus is null ? "" : $",\"reportStatus\":\"{reportStatus}\"") + "}";
        return new MessageDto("m-receipt", "tool", 20_000, false, $"[{{\"type\":\"tool_result\",\"callId\":\"{Call}\",\"output\":{{\"content\":\"ok\"}},\"isError\":false,\"subagent\":{sub}}}]");
    }

    private static MessageDto TaskCall() =>
        new("m-call", "assistant", 9_000, false, $"[{{\"type\":\"tool_call\",\"callId\":\"{Call}\",\"name\":\"Task\",\"input\":{{\"description\":\"查配置读取处\",\"subagent_type\":\"general-purpose\",\"prompt\":\"x\"}}}}]");

    // ───────────────────────── 事件 → 卡片 ─────────────────────────

    [Fact]
    public void Start_update_and_end_events_build_one_card_keyed_by_device_session_and_call()
    {
        var state = Start(WithSession());
        var card = state.Subagents[Card];
        Assert.Equal((SubagentStatus.Running, Child, ChildSession, "general-purpose", false), (card.Record.Status, card.Record.ChildRunId, card.Record.ChildSessionId, card.Record.AgentType, card.Record.Background));
        Assert.Equal(("sonnet", "blue"), (card.Telemetry.Model, card.Telemetry.Color));

        // 遥测:键存在但值为空 = 清除;键不存在 = 保持(Core 的 Patch 语义,线上是 StringPatchDto)
        state = Apply(state, 3, new SubagentUpdateEventDto(Call, Child, "tool", new StringPatchDto("Read"), new StringPatchDto("src/config.ts"), ToolCalls: 1, At: 11_000));
        Assert.Equal(("Read", "src/config.ts", 1, "tool"), (state.Subagents[Card].Record.CurrentTool, state.Subagents[Card].Record.CurrentTarget, state.Subagents[Card].Record.ToolCalls, state.Subagents[Card].Telemetry.Phase));
        state = Apply(state, 4, new SubagentUpdateEventDto(Call, Child, "thinking", ToolCalls: 1, ContextUsage: new SubagentContextUsageDto(10, 100, false)));
        Assert.Equal(("Read", "src/config.ts", "thinking"), (state.Subagents[Card].Record.CurrentTool, state.Subagents[Card].Record.CurrentTarget, state.Subagents[Card].Telemetry.Phase)); // 没带的键保持
        state = Apply(state, 5, new SubagentUpdateEventDto(Call, Child, CurrentTool: new StringPatchDto(null), CurrentTarget: new StringPatchDto(null)));
        Assert.Null(state.Subagents[Card].Record.CurrentTool);
        Assert.Equal(10, state.Subagents[Card].Telemetry.ContextUsage!.Used);

        state = Apply(state, 6, new SubagentEndEventDto(Call, Child, "done", "short summary", null, 9, 12_000));
        var done = state.Subagents[Card];
        Assert.Equal((SubagentStatus.Done, "short summary"), (done.Record.Status, done.Telemetry.Summary));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(12_000), done.Record.EndedAt);
        Assert.Single(state.Subagents);
    }

    [Fact]
    public void A_terminal_card_is_never_revived_by_replayed_start_or_late_updates_and_the_first_terminal_wins()
    {
        var state = Start(WithSession());
        state = Apply(state, 3, new SubagentEndEventDto(Call, Child, "error", "boom", new ErrorDto("provider", "上游 502", false, 502), null, 12_000));
        var ended = state.Subagents[Card];

        // 断线重连:快照重放整段日志(start 在 end 之前再来一遍),卡片仍是终态
        var replay = ConversationReducer.ApplyRunSnapshot(state, Device,
            new RunSnapshotDto("parent-run", ParentSession, "w1", null, 0, "running", null, 1, null, 3, "lost",
            [
                new SeqEventDto(1, new RunStartEventDto("parent-run", ParentSession, "sonnet")),
                new SeqEventDto(2, new SubagentStartEventDto(Call, Child, ChildSession, "general-purpose", "查配置读取处", "blue", "sonnet", false, 10_000)),
                new SeqEventDto(3, new SubagentUpdateEventDto(Call, Child, "tool", new StringPatchDto("Read"), ToolCalls: 5, At: 11_000)),
                new SeqEventDto(4, new SubagentEndEventDto(Call, Child, "error", "boom", new ErrorDto("provider", "上游 502", false, 502), null, 12_000)),
            ], [], [Child], false, false), knownRev: 2);
        Assert.Equal(SubagentStatus.Error, replay.Subagents[Card].Record.Status);
        Assert.Null(replay.Subagents[Card].Record.CurrentTool);

        // 乱序的旧 start / update、另一个终态:都改不了它(第一个终态胜出)
        var late = Apply(Apply(Apply(state, 20, new SubagentStartEventDto(Call, Child, ChildSession, "general-purpose", "x", null, "sonnet", false, 1_000)), 21,
            new SubagentUpdateEventDto(Call, Child, "tool", new StringPatchDto("Bash"), ToolCalls: 99, At: 13_000)), 22, new SubagentEndEventDto(Call, Child, "done"));
        Assert.Equal(SubagentStatus.Error, late.Subagents[Card].Record.Status);
        Assert.Equal(ended.Record.ToolCalls, late.Subagents[Card].Record.ToolCalls);
        Assert.Equal("上游 502", late.Subagents[Card].Telemetry.Error!.Message); // 失败原因留在卡片上,不必开面板
    }

    [Fact]
    public void A_queued_dispatch_shows_its_position_from_the_structured_details_and_loses_it_when_it_starts()
    {
        var state = Apply(WithSession(), 1, new RunStartEventDto("parent-run", ParentSession, "sonnet"));
        state = Apply(state, 2, new ToolStartEventDto("task-1", "Task", "{\"description\":\"查配置读取处\",\"subagent_type\":\"general-purpose\"}", null));
        state = Apply(state, 3, new ToolProgressEventDto("task-1", "Queued, 2 ahead.", null, "{\"kind\":\"queued\",\"ahead\":2}"));
        Assert.Equal(2, state.Runs[new RunRef(Device, "parent-run")].Tools["task-1"].QueuedAhead);
        Assert.Empty(state.Subagents); // 还没拿到名额:没有卡片

        // 别的进度文字不当成排位
        Assert.Equal(2, Apply(state, 4, new ToolProgressEventDto("task-1", "working", 0.5, "{\"kind\":\"other\"}")).Runs[new RunRef(Device, "parent-run")].Tools["task-1"].QueuedAhead);

        state = Apply(state, 5, new SubagentStartEventDto("task-1", Child, ChildSession, "general-purpose", "查配置读取处", null, "sonnet", false, 10_000));
        Assert.Null(state.Runs[new RunRef(Device, "parent-run")].Tools["task-1"].QueuedAhead);
        Assert.Equal(SubagentStatus.Running, state.Subagents[Card].Record.Status);
    }

    [Fact]
    public void A_queued_background_dispatch_is_a_queued_card_whichever_of_the_receipt_and_the_progress_arrives_first_and_starting_promotes_it()
    {
        const string Input = "{\"description\":\"查配置读取处\",\"subagent_type\":\"general-purpose\",\"run_in_background\":true}";
        var begun = Apply(WithSession(ParentSession, TaskCall()), 1, new RunStartEventDto("parent-run", ParentSession, "sonnet"));
        begun = Apply(begun, 2, new ToolStartEventDto(Call, "Task", Input, null));

        // 先收到回执(会话主题先到):回执只说「运行中」,卡片先是运行中;随后到的排位把它纠正为排队中
        var receiptFirst = ConversationReducer.ApplyMessage(begun, Parent, Receipt("running"));
        Assert.Equal(SubagentStatus.Running, receiptFirst.Subagents[Card].Record.Status);
        receiptFirst = Apply(receiptFirst, 3, new ToolProgressEventDto(Call, "Queued", null, "{\"kind\":\"queued\",\"ahead\":1}"));
        Assert.Equal(SubagentStatus.Queued, receiptFirst.Subagents[Card].Record.Status);

        // 先收到排位、再收到回执:仍是排队中;回执重复到达也不把它顶成运行中
        var progressFirst = Apply(begun, 3, new ToolProgressEventDto(Call, "Queued", null, "{\"kind\":\"queued\",\"ahead\":1}"));
        progressFirst = ConversationReducer.ApplyMessage(ConversationReducer.ApplyMessage(progressFirst, Parent, Receipt("running")), Parent, Receipt("running"));
        Assert.Equal(SubagentStatus.Queued, progressFirst.Subagents[Card].Record.Status);

        // 拿到名额:subagent_start 把它升为运行中(排位清掉);之后迟到的回执不改它,而且排队中的状态不会倒回来
        var started = Apply(progressFirst, 4, new SubagentStartEventDto(Call, Child, ChildSession, "general-purpose", "查配置读取处", null, "sonnet", true, 10_000));
        Assert.Equal((SubagentStatus.Running, null), (started.Subagents[Card].Record.Status, started.Runs[new RunRef(Device, "parent-run")].Tools[Call].QueuedAhead));
        Assert.Equal(SubagentStatus.Running, ConversationReducer.ApplyMessage(started, Parent, Receipt("running")).Subagents[Card].Record.Status);

        // 父 Run 结束了:排着队的派发被取消,排位不再留着
        var ended = Apply(progressFirst, 5, new RunEndEventDto("aborted"));
        Assert.Null(ended.Runs[new RunRef(Device, "parent-run")].Tools[Call].QueuedAhead);
    }

    [Fact]
    public void An_update_that_arrives_without_its_start_is_filled_in_from_the_Task_call_when_it_carries_a_time()
    {
        // 日志被硬上限裁剪过、或续传补发先于快照:没见过 subagent_start,但见过 Task 的入参
        var state = Apply(WithSession(), 1, new RunStartEventDto("parent-run", ParentSession, "sonnet"));
        state = Apply(state, 2, new ToolStartEventDto(Call, "Task", "{\"description\":\"查配置读取处\",\"subagent_type\":\"general-purpose\"}", null));
        state = Apply(state, 3, new SubagentUpdateEventDto(Call, Child, "tool", new StringPatchDto("Read"), ToolCalls: 2, At: 11_000));

        var card = state.Subagents[Card];
        Assert.Equal(("general-purpose", "查配置读取处", SubagentStatus.Running, 2), (card.Record.AgentType, card.Record.Description, card.Record.Status, card.Record.ToolCalls));
        // 没有时间就不编一个
        Assert.Empty(Apply(Apply(Apply(WithSession(), 1, new RunStartEventDto("parent-run", ParentSession, "sonnet")), 2, new ToolStartEventDto(Call, "Task", "{}", null)),
            3, new SubagentUpdateEventDto(Call, Child)).Subagents);
    }

    // ───────────────────────── subagent-sync:权威广播收口 ─────────────────────────

    [Fact]
    public void A_card_whose_child_the_device_no_longer_reports_is_settled_from_the_durable_receipt()
    {
        var state = Start(WithSession(ParentSession, TaskCall()), background: true);
        Assert.Equal(SubagentStatus.Running, state.Subagents[Card].Record.Status);

        // 子 Run 已被回收、结束事件丢了:库里的回执说它跑完了
        state = ConversationReducer.ApplyMessage(state, Parent, Receipt("done"), knownRev: 1);
        // (回执本身一到就合进来了:终态压过运行中;这里再验证广播不会把它改回去)
        Assert.Equal((SubagentStatus.Done, "all done"), (state.Subagents[Card].Record.Status, state.Subagents[Card].Telemetry.Summary));
        var reconciled = ActiveIndex(state, rev: 5);
        Assert.Equal(SubagentStatus.Done, reconciled.Subagents[Card].Record.Status);
        Assert.False(reconciled.Subagents[Card].Guessed);
    }

    [Fact]
    public void A_card_the_receipt_cannot_settle_is_guessed_aborted_and_the_real_end_replaces_the_guess()
    {
        // 回执只说「运行中」(后台子代理转后台那一刻写的)、结束事件又丢了
        var state = Start(WithSession(ParentSession, TaskCall(), Receipt("running")), background: true);

        var guessed = ActiveIndex(state, rev: 5);
        Assert.Equal((SubagentStatus.Aborted, true), (guessed.Subagents[Card].Record.Status, guessed.Subagents[Card].Guessed));

        // 广播和事件走不同的主题,谁先到不保证:随后到的真实结局替换推断(第一个终态胜出只对真实的终态成立)
        var real = Apply(guessed, 3, new SubagentEndEventDto(Call, Child, "done", "the real summary", null, null, 12_000));
        Assert.Equal((SubagentStatus.Done, false, "the real summary"), (real.Subagents[Card].Record.Status, real.Subagents[Card].Guessed, real.Subagents[Card].Telemetry.Summary));

        // 父会话主题的 subagent.stopped(后台子代理比父 Run 活得久时,subagent_end 进不去)同样替换推断
        var viaStopped = OrchestrationReducer.ApplySubagentStopped(guessed, Device, new SubagentStoppedDto(ParentSession, Call, Child, ChildSession, "general-purpose", "done", true), knownRev: 5);
        Assert.Equal((SubagentStatus.Done, false), (viaStopped.Subagents[Card].Record.Status, viaStopped.Subagents[Card].Guessed));

        // 库里真实的回执(终态)到达也替换推断
        var viaReceipt = ConversationReducer.ApplyMessage(guessed, Parent, Receipt("done"), knownRev: 5);
        Assert.Equal((SubagentStatus.Done, false), (viaReceipt.Subagents[Card].Record.Status, viaReceipt.Subagents[Card].Guessed));
    }

    [Fact]
    public void A_card_is_left_alone_while_the_device_still_reports_its_child_and_an_older_index_proves_nothing()
    {
        var state = Start(WithSession(), at: 10_000);

        Assert.Equal(SubagentStatus.Running, ActiveIndex(state, rev: 5, Child).Subagents[Card].Record.Status);
        // 版本不比这份镜像创建时新的索引:刚起的子 Run 不在旧广播里是正常的,不能据此判结束
        Assert.Equal(SubagentStatus.Running, ActiveIndex(state, rev: 1).Subagents[Card].Record.Status);
        // 别的设备的广播不动它
        var other = ConversationReducer.Reconcile(state, new ActiveRunIndex("dev-2", []) { Rev = 9 });
        Assert.Equal(SubagentStatus.Running, other.Subagents[Card].Record.Status);
    }

    // ───────────────────────── 规则 7:断线是未知 ─────────────────────────

    [Fact]
    public void A_dropped_connection_makes_unfinished_cards_unknown_freezes_their_clock_and_any_news_restores_them()
    {
        var state = Start(WithSession(), at: 10_000);
        state = Apply(state, 3, new SubagentUpdateEventDto(Call, Child, "tool", ToolCalls: 1, At: 70_000));
        var later = DateTimeOffset.FromUnixTimeMilliseconds(500_000);

        var unknown = ConversationReducer.MarkDeviceUnknown(state, Device);
        var card = unknown.Subagents[Card];
        Assert.True(card.Unknown);
        Assert.Equal(SubagentStatus.Running, card.Record.Status); // 不判成结束
        // 不继续计时:耗时停在最后一次听到动静的那一刻(60 秒),不随 now 增长
        Assert.Equal(TimeSpan.FromSeconds(60), card.ViewAt(later).Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(490), state.Subagents[Card].ViewAt(later).Elapsed);

        // 重连后的权威广播里还有它 → 恢复;或者收到它的新消息
        Assert.False(ActiveIndex(unknown, rev: 5, Child).Subagents[Card].Unknown);
        Assert.False(Apply(unknown, 4, new SubagentUpdateEventDto(Call, Child, "thinking", At: 80_000)).Subagents[Card].Unknown);
        // 已收口的不受影响
        var finished = Apply(state, 4, new SubagentEndEventDto(Call, Child, "done", null, null, null, 90_000));
        Assert.False(ConversationReducer.MarkDeviceUnknown(finished, Device).Subagents[Card].Unknown);
    }

    // ───────────────────────── 回执 ─────────────────────────

    [Fact]
    public void Receipts_in_the_transcript_rebuild_cards_and_merge_with_the_live_ones_terminal_beats_running_either_way_round()
    {
        // 只有回执(重启后第一次打开):类型和描述取自那次 Task 调用的入参;回执里没有子会话 id,所以点不开
        var cold = WithSession(ParentSession, TaskCall(), Receipt("done", background: true, reportStatus: "pending"));
        var card = cold.Subagents[Card];
        Assert.Equal(("general-purpose", "查配置读取处", SubagentStatus.Done, true, SubagentReportStatus.Pending), (card.Record.AgentType, card.Record.Description, card.Record.Status, card.Record.Background, card.Record.ReportStatus));
        var view = card.ViewAt(DateTimeOffset.FromUnixTimeMilliseconds(99_000));
        Assert.False(view.CanOpen);
        Assert.Equal(SubagentReportStatus.Pending, view.Report); // 后台跑完:待汇报

        // 实时的先到(运行中)、库里的终态后到:合进来仍是终态,遥测取实时值
        var live = Apply(Apply(WithSession(ParentSession, TaskCall()), 1, new RunStartEventDto("parent-run", ParentSession, "sonnet")), 2,
            new SubagentStartEventDto(Call, Child, ChildSession, "general-purpose", "查配置读取处", null, "sonnet", true, 10_000));
        live = Apply(live, 3, new SubagentUpdateEventDto(Call, Child, ToolCalls: 7, At: 11_000));
        var merged = ConversationReducer.ApplyMessage(live, Parent, Receipt("done", reportStatus: "reported"));
        Assert.Equal((SubagentStatus.Done, 7, SubagentReportStatus.Reported, ChildSession), (merged.Subagents[Card].Record.Status, merged.Subagents[Card].Record.ToolCalls, merged.Subagents[Card].Record.ReportStatus, merged.Subagents[Card].Record.ChildSessionId));

        // 库里的是旧的「运行中」、实时已经是终态:仍是终态(回执不能把它改回去)
        var ended = Apply(live, 4, new SubagentEndEventDto(Call, Child, "done", null, null, null, 12_000));
        Assert.Equal(SubagentStatus.Done, ConversationReducer.ApplyMessage(ended, Parent, Receipt("running")).Subagents[Card].Record.Status);
    }

    [Fact]
    public void The_report_progress_only_moves_forward_reported_is_the_end_point()
    {
        var state = Apply(Start(WithSession(), background: true), 3, new SubagentEndEventDto(Call, Child, "done", null, null, null, 12_000));
        Report(ref state, "pending", SubagentReportStatus.Pending);
        Report(ref state, "injecting", SubagentReportStatus.Injecting);
        Report(ref state, "reported", SubagentReportStatus.Reported);
        // 迟到的旧进度退不回去
        Report(ref state, "pending", SubagentReportStatus.Reported);
        Report(ref state, "blocked", SubagentReportStatus.Reported);

        static void Report(ref ConversationState s, string wire, SubagentReportStatus expected)
        {
            s = OrchestrationReducer.ApplySubagentReport(s, Device, new SubagentReportDto(ParentSession, Call, wire));
            Assert.Equal(expected, s.Subagents[Card].Record.ReportStatus);
        }
    }

    [Fact]
    public void The_report_row_is_read_from_the_internal_message_block_and_unknown_or_broken_parts_are_skipped()
    {
        var row = new MessageDto("m-report", "user", 1, true,
            "[{\"type\":\"text\",\"text\":\"Background subagent result …\"},{\"type\":\"subagent\",\"callId\":\"task-1\",\"childRunId\":\"parent-run:sub:1\",\"summary\":\"short\"},{\"type\":\"weird\"}]");
        var found = Assert.Single(SubagentReceipts.ReportRowsOf(row));
        Assert.Equal(("m-report", "task-1", "parent-run:sub:1", "short"), (found.MessageId, found.CallId, found.ChildRunId, found.Summary));
        Assert.Empty(SubagentReceipts.ReportRowsOf(new MessageDto("x", "user", 1, true, "not json")));
        Assert.Empty(SubagentReceipts.ReportRowsOf(new MessageDto("x", "user", 1, true, "[{\"type\":\"subagent\"}]")));
        Assert.Equal("Background subagent result …", SubagentReceipts.TextOf(row));
    }

    // ───────────────────────── subagent-card:卡片的展示判据 ─────────────────────────

    [Fact]
    public void The_card_view_follows_the_core_rules_stop_background_open_and_the_report_banner_belongs_to_background_runs_only()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(70_000);
        var running = Start(WithSession()).Subagents[Card].ViewAt(now);
        Assert.Equal((true, true, true, true, TimeSpan.FromSeconds(60)), (running.CanStop, running.CanBackground, running.CanOpen, running.IsActive, running.Elapsed));
        Assert.Equal(SubagentReportStatus.None, running.Report);

        // 跑完了就没有停止 / 转后台;前台跑完不显示任何「已汇报」(它的结果就是 tool_result,没有回传这一步)
        var foreground = Apply(Start(WithSession()), 3, new SubagentEndEventDto(Call, Child, "done", null, null, null, 12_000)).Subagents[Card].ViewAt(now);
        Assert.Equal((false, false, SubagentReportStatus.None, true), (foreground.CanStop, foreground.CanBackground, foreground.Report, foreground.IsTerminal));
        // 后台跑完:待汇报,而且已经是后台的就不能再转
        var background = Apply(Start(WithSession(), background: true), 3, new SubagentEndEventDto(Call, Child, "done", null, null, null, 12_000)).Subagents[Card].ViewAt(now);
        Assert.Equal(SubagentReportStatus.Pending, background.Report);
        Assert.False(Start(WithSession(), background: true).Subagents[Card].ViewAt(now).CanBackground);
    }

    [Fact]
    public void Cards_of_one_session_are_ordered_running_first_then_newest_first_and_never_leak_across_sessions_or_devices()
    {
        var state = Start(WithSession(), at: 10_000);
        state = Apply(state, 3, new SubagentStartEventDto("task-2", "parent-run:sub:2", "x", "code-editor", "second", null, "sonnet", false, 20_000));
        state = Apply(state, 4, new SubagentEndEventDto("task-2", "parent-run:sub:2", "done", null, null, null, 21_000));
        state = Apply(state, 5, new SubagentStartEventDto("task-3", "parent-run:sub:3", "y", "code-editor", "third", null, "sonnet", false, 30_000));
        state = ConversationReducer.ApplyRunEvent(state, "dev-2", "parent-run", 1, new RunStartEventDto("parent-run", ParentSession, "sonnet"), 0);
        state = ConversationReducer.ApplyRunEvent(state, "dev-2", "parent-run", 2, new SubagentStartEventDto("task-9", "q", "q", "code-editor", "other device (same ids)", null, "sonnet", false, 1), 0);

        Assert.Equal(["task-3", "task-1", "task-2"], state.SubagentsOf(Parent).Select(e => e.Ref.CallId));
        Assert.Single(state.Subagents.Keys, k => k.DeviceId == "dev-2");
        Assert.Equal(3, state.SubagentsOf(Parent).Count);
    }

    // ───────────────────────── subagent-panel-feed:卡片遥测与子会话转录是两份 ─────────────────────────

    [Fact]
    public void The_cards_telemetry_moves_with_the_parents_events_but_the_child_transcript_only_exists_once_the_panel_was_opened()
    {
        var state = Start(WithSession());
        // 子 Run 自己的事件(没订阅它的主题就不会到;这里直接喂,验证「没有子会话镜像就长不出转录」)
        var childRun = new RunRef(Device, Child);
        var childMessage = new MessageDto("c-m1", "assistant", 1, false, "[{\"type\":\"text\",\"text\":\"我先看一眼 config.ts\"}]");
        var notOpened = ConversationReducer.ApplyRunEvent(state, Device, Child, 1, new MessageEndEventDto(childMessage, null, null, "endTurn"), 1);
        Assert.DoesNotContain(new SessionRef(Device, ChildSession), notOpened.Sessions.Keys);

        // 点开面板 = 子会话的快照进 Store + 订阅子 Run 的主题:子 Run 的 message_end 才折进那份转录(和主智能体同一个归约函数)
        var opened = ConversationReducer.ApplySession(state, Device, new SessionTopicState(
            new SessionPageDto(Meta(ChildSession), [], false, [], [], []), new QueueDto(ChildSession, 0, []), Child));
        opened = ConversationReducer.ApplyRunSnapshot(opened, Device,
            new RunSnapshotDto(Child, ChildSession, "w1", "parent-run", 1, "running", null, 1, null, 2, "fresh",
                [new SeqEventDto(1, new RunStartEventDto(Child, ChildSession, "sonnet")), new SeqEventDto(2, new MessageEndEventDto(childMessage, null, null, "endTurn"))], [], [], false, false), 1);
        Assert.Equal("c-m1", Assert.Single(opened.Sessions[new SessionRef(Device, ChildSession)].Messages).Id);
        Assert.Equal(RunPhase.Running, opened.Runs[childRun].Phase);

        // 父卡片的遥测不依赖子面板:只由父 Run 日志里的 subagent_update 推进
        var telemetry = Apply(opened, 3, new SubagentUpdateEventDto(Call, Child, "thinking", ToolCalls: 0, At: 11_000));
        Assert.Equal("thinking", telemetry.Subagents[Card].Telemetry.Phase);
    }

    [Fact]
    public void Late_events_of_a_finished_child_run_change_neither_the_card_nor_the_panel()
    {
        var state = Start(WithSession());
        state = ConversationReducer.ApplySession(state, Device, new SessionTopicState(new SessionPageDto(Meta(ChildSession), [], false, [], [], []), new QueueDto(ChildSession, 0, []), null));
        state = ConversationReducer.ApplyRunEvent(state, Device, Child, 1, new RunStartEventDto(Child, ChildSession, "sonnet"), 1);
        state = ConversationReducer.ApplyRunEvent(state, Device, Child, 2, new RunEndEventDto("done"), 1);
        state = Apply(state, 3, new SubagentEndEventDto(Call, Child, "done", null, null, null, 12_000));
        var finished = state;

        // 迟到的重放(序号不大于已收到的)和更大序号的旧事件:Run 阶段只升不降,卡片的终态不被遥测改动
        Assert.Same(finished, ConversationReducer.ApplyRunEvent(finished, Device, Child, 1, new RunStartEventDto(Child, ChildSession, "sonnet"), 1));
        var late = Apply(finished, 9, new SubagentUpdateEventDto(Call, Child, "tool", new StringPatchDto("Bash"), ToolCalls: 40, At: 99_000));
        Assert.Equal((SubagentStatus.Done, 0), (late.Subagents[Card].Record.Status, late.Subagents[Card].Record.ToolCalls));
        Assert.Equal(RunPhase.Done, ConversationReducer.ApplyRunEvent(finished, Device, Child, 9, new RunStartEventDto(Child, ChildSession, "sonnet"), 1).Runs[new RunRef(Device, Child)].Phase);
    }
}
