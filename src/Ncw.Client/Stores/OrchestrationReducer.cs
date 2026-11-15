using System.Collections.Immutable;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Stores;

/// <summary>
/// 子代理 / 目标 / 待办折进 <see cref="ConversationState"/> 的纯函数(和 <see cref="ConversationReducer"/> 一起被设备连接调用)。幂等、单调:
/// 重放的旧事件、乱序的广播、断线重连后的补发都不会让状态倒退(AGENTS.md §3 规则 5、6、7)。
/// <b>归约规则只有 Core 里的一份</b>(<see cref="SubagentView"/>:<c>ApplyStart / ApplyUpdate / ApplyEnd / Merge</c>;<see cref="Todos"/>;<see cref="GoalWire"/>),这里只做「事件 → 参数」的映射和键。
/// </summary>
public static class OrchestrationReducer
{
    // ───────────────────────── 子代理:父 Run 日志里的事件 ─────────────────────────

    /// <summary>
    /// <c>subagent_start / update / end</c>(父 Run 的主题)。<paramref name="sessionId"/> 是这个 Run 的会话(= 子代理的父会话);
    /// <paramref name="tools"/> 是这个 Run 已知的工具调用:update / end 先于 start 到达(日志被裁剪过、续传补发)时,从那次 <c>Task</c> 调用的入参补出类型和描述。
    /// </summary>
    public static ConversationState ApplySubagentEvent(
        ConversationState state, string deviceId, string sessionId, long knownRev, IReadOnlyDictionary<string, ToolCallView> tools, RunEventDto ev)
    {
        if (sessionId.Length == 0)
        {
            return state;
        }

        switch (ev)
        {
            case SubagentStartEventDto e:
            {
                var at = DateTimeOffset.FromUnixTimeMilliseconds(e.At);
                var started = new SubagentRecord(sessionId, e.CallId, e.AgentType, e.Description, SubagentStatus.Running, at, null, 0, null)
                {
                    ChildRunId = e.ChildRunId,
                    ChildSessionId = e.ChildSessionId,
                    Background = e.Background,
                    LastEventAt = at,
                };
                return Upsert(state, new SubagentRef(deviceId, sessionId, e.CallId), knownRev, entry =>
                {
                    var record = SubagentView.ApplyStart(entry?.Record, started);
                    var telemetry = (entry?.Telemetry ?? SubagentTelemetry.Empty) with
                    {
                        Model = e.Model,
                        Color = e.Color ?? entry?.Telemetry.Color,
                        Phase = entry is null ? (e.Background ? "background" : "starting") : entry.Telemetry.Phase,
                    };
                    return (entry ?? New(deviceId, sessionId, e.CallId, record, telemetry, knownRev)) with { Record = record, Telemetry = telemetry, Unknown = false };
                });
            }

            case SubagentUpdateEventDto e:
            {
                var key = new SubagentRef(deviceId, sessionId, e.CallId);
                var at = e.At is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : (DateTimeOffset?)null;
                state = EnsureFromTool(state, key, knownRev, tools, e.ChildRunId, at);
                if (!state.Subagents.TryGetValue(key, out var entry))
                {
                    return state;
                }

                // 对账推断的终态被一条「还活着」的更新推翻:那次推断错了(见 Guessed)
                var live = entry.Guessed ? entry with { Record = entry.Record with { Status = SubagentStatus.Running, EndedAt = null }, Guessed = false } : entry;
                var record = SubagentView.ApplyUpdate(
                    live.Record, e.ToolCalls, ToPatch(e.CurrentTool), ToPatch(e.CurrentTarget), at);
                var telemetry = live.Telemetry with
                {
                    Phase = e.Phase ?? live.Telemetry.Phase,
                    ToolErrors = Math.Max(live.Telemetry.ToolErrors, e.ToolErrors ?? 0),
                    Usage = e.Usage ?? live.Telemetry.Usage,
                    ContextUsage = e.ContextUsage ?? live.Telemetry.ContextUsage,
                    Notice = e.Notice is { } notice ? notice.Value : live.Telemetry.Notice,
                };
                return Set(state, live with { Record = record, Telemetry = telemetry, Unknown = false });
            }

            case SubagentEndEventDto e:
            {
                var key = new SubagentRef(deviceId, sessionId, e.CallId);
                var at = e.At is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : (DateTimeOffset?)null;
                state = EnsureFromTool(state, key, knownRev, tools, e.ChildRunId, at);
                return state.Subagents.TryGetValue(key, out var entry) && SubagentView.ParseStatus(e.Status) is { } status
                    ? Set(state, Settle(entry, status, at, e.Summary, e.Error))
                    : state;
            }

            default:
                return state;
        }
    }

    /// <summary>真实的结局落到一张卡片上。推断出来的终态(<see cref="SubagentEntry.Guessed"/>)先摘掉再应用,所以「已中止」的推断替换得成真实的「已完成」。</summary>
    private static SubagentEntry Settle(SubagentEntry entry, SubagentStatus status, DateTimeOffset? at, string? summary, ErrorDto? error)
    {
        var basis = entry.Guessed ? entry with { Record = entry.Record with { Status = SubagentStatus.Running, EndedAt = null }, Guessed = false } : entry;
        return basis with
        {
            Record = SubagentView.ApplyEnd(basis.Record, status, at),
            Telemetry = basis.Telemetry with { Summary = summary ?? basis.Telemetry.Summary, Error = error ?? basis.Telemetry.Error },
            Unknown = false,
        };
    }

    private static Patch<string> ToPatch(StringPatchDto? patch) => patch is null ? Patch<string>.Keep : patch.Value is { } value ? Patch<string>.Set(value) : Patch<string>.Clear;

    /// <summary>表里没有这张卡片、而这次 <c>Task</c> 调用我们见过入参:补一张「运行中」的(要有时间,否则计时无从算起)。</summary>
    private static ConversationState EnsureFromTool(
        ConversationState state, SubagentRef key, long knownRev, IReadOnlyDictionary<string, ToolCallView> tools, string childRunId, DateTimeOffset? at)
    {
        if (state.Subagents.ContainsKey(key) || at is not { } startedAt || !tools.TryGetValue(key.CallId, out var call)
            || call.Name != SubagentReceipts.TaskToolName || SubagentReceipts.ParseTaskInput(call.InputJson) is not { } input)
        {
            return state;
        }

        var record = new SubagentRecord(key.SessionId, key.CallId, input.Type, input.Description, SubagentStatus.Running, startedAt, null, 0, null) { ChildRunId = childRunId, LastEventAt = startedAt };
        return Set(state, New(key.DeviceId, key.SessionId, key.CallId, record, SubagentTelemetry.Empty, knownRev));
    }

    // ───────────────────────── 子代理:会话主题的事件 ─────────────────────────

    /// <summary>
    /// <c>subagent.stopped</c>(父会话主题):一个子 Run 结束了。后台子代理比父 Run 活得久,父 Run 的日志那时已经收口,<c>subagent_end</c> 进不去——这条补上。
    /// 表里没有这张卡片就从会话消息里的 <c>Task</c> 回执补一张(没有消息也补不出来的忽略)。
    /// </summary>
    public static ConversationState ApplySubagentStopped(ConversationState state, string deviceId, SubagentStoppedDto dto, long knownRev)
    {
        var key = new SubagentRef(deviceId, dto.SessionId, dto.CallId);
        if (!state.Subagents.ContainsKey(key))
        {
            state = ApplyReceipts(state, deviceId, dto.SessionId, knownRev);
        }

        if (!state.Subagents.TryGetValue(key, out var entry) || SubagentView.ParseStatus(dto.Status) is not { } status)
        {
            return state;
        }

        var settled = Settle(entry, status, null, null, null);
        return Set(state, settled with
        {
            Record = settled.Record with
            {
                ChildRunId = settled.Record.ChildRunId ?? dto.ChildRunId,
                ChildSessionId = settled.Record.ChildSessionId ?? dto.ChildSessionId,
                Background = settled.Record.Background || dto.Background,
            },
        });
    }

    /// <summary><c>subagent.report</c>(父会话主题):汇报进度变了。<c>reported</c> 是终点,不会被迟到的 <c>pending</c> 退回(Core 的 <see cref="SubagentView.Merge"/>)。</summary>
    public static ConversationState ApplySubagentReport(ConversationState state, string deviceId, SubagentReportDto dto)
    {
        var key = new SubagentRef(deviceId, dto.SessionId, dto.CallId);
        if (!state.Subagents.TryGetValue(key, out var entry) || SubagentView.ParseReportStatus(dto.Status) is not { } report)
        {
            return state;
        }

        return Set(state, entry with { Record = SubagentView.Merge(entry.Record, entry.Record with { ReportStatus = report }), Unknown = false });
    }

    // ───────────────────────── 子代理:库里的回执 ─────────────────────────

    /// <summary>
    /// 把一个会话消息里的回执合进卡片表(消息落库、会话快照、历史被改写时调)。合并走 Core 的 <see cref="SubagentView.Merge"/>:终态压过运行中,不论哪一边更晚到;
    /// 推断出来的终态被真实的回执替换。只靠回执重建的卡片没有子会话 id(回执里没有)。
    /// </summary>
    /// <param name="tools">
    /// 这条消息所属 Run 已知的工具调用(可选;没给就看这个会话上所有 Run 镜像):后台派发在排队时 <c>Task</c> 调用就已经返回、回执写的是「运行中」——
    /// 回执分不清排队和已开始,而排位(<see cref="ToolCallView.QueuedAhead"/>)分得清:新建的卡片标为排队中,<c>subagent_start</c> 到来时按单调规则升为运行中。
    /// </param>
    public static ConversationState ApplyReceipts(ConversationState state, string deviceId, string sessionId, long knownRev, IReadOnlyDictionary<string, ToolCallView>? tools = null)
    {
        if (!state.Sessions.TryGetValue(new SessionRef(deviceId, sessionId), out var view))
        {
            return state;
        }

        var cards = SubagentReceipts.FromMessages(view.Messages);
        foreach (var card in cards)
        {
            var key = new SubagentRef(deviceId, sessionId, card.CallId);
            var saved = SubagentReceipts.ToRecord(sessionId, card);
            state.Subagents.TryGetValue(key, out var entry);
            // 回执写的是「运行中」,但这次派发还在排队:别让回执把「排队中」顶成「运行中」(合并取更靠后的状态)
            if (saved.Status == SubagentStatus.Running && IsQueued(state, deviceId, sessionId, card.CallId, tools))
            {
                saved = saved with { Status = SubagentStatus.Queued };
            }

            if (entry is null)
            {
                state = Set(state, New(deviceId, sessionId, card.CallId, saved, new SubagentTelemetry { Color = card.Color, Summary = card.Summary, Error = card.Error }, knownRev));
                continue;
            }

            var basis = entry.Guessed && SubagentView.IsTerminal(saved.Status) ? entry with { Record = entry.Record with { Status = SubagentStatus.Running, EndedAt = null }, Guessed = false } : entry;
            var merged = SubagentView.Merge(saved, basis.Record);
            state = Set(state, basis with
            {
                Record = merged,
                Telemetry = basis.Telemetry with
                {
                    Color = basis.Telemetry.Color ?? card.Color,
                    Summary = basis.Telemetry.Summary ?? card.Summary,
                    Error = basis.Telemetry.Error ?? card.Error,
                },
            });
        }

        return state;
    }

    /// <summary>
    /// 这次派发在排队(<c>tool_progress</c> 的排位到了):如果它的回执已经先一步建出了卡片(会话主题和 Run 主题是两条独立的推送,谁先到不保证)而还没见过
    /// <c>subagent_start</c>,卡片就是「排队中」,不是「运行中」。已经开始的(见过 start)不动——状态只升不降。
    /// </summary>
    public static ConversationState MarkQueued(ConversationState state, string deviceId, string sessionId, string callId)
    {
        var key = new SubagentRef(deviceId, sessionId, callId);
        return state.Subagents.TryGetValue(key, out var entry)
               && entry.Record is { Status: SubagentStatus.Running, ChildSessionId: null } && entry.Telemetry.Model is null
            ? Set(state, entry with { Record = entry.Record with { Status = SubagentStatus.Queued } })
            : state;
    }

    private static bool IsQueued(ConversationState state, string deviceId, string sessionId, string callId, IReadOnlyDictionary<string, ToolCallView>? tools) =>
        tools is not null
            ? tools.GetValueOrDefault(callId)?.QueuedAhead is not null
            : state.Runs.Values.Any(r => r.Ref.DeviceId == deviceId && r.SessionId == sessionId && r.Tools.GetValueOrDefault(callId)?.QueuedAhead is not null);

    // ───────────────────────── 子代理:权威广播对账与断线 ─────────────────────────

    /// <summary>
    /// 用权威的 activeRuns 广播(它的 <c>subagents</c> 半边)给这台设备上的卡片对账(规则 5):还在运行 / 排队的卡片,如果它的子 Run 不在更新的索引里,就是已经结束了——
    /// 哪怕结束事件丢了。先看库里的回执(终态就用它),回执也说不清就推断为已中止(2.x 同)并标 <see cref="SubagentEntry.Guessed"/>:随后到的真实结局会替换它。
    /// 在索引里的未知卡片恢复。只有版本比这份镜像创建时更新的索引才能据此判定(刚起的子 Run 不在旧广播里是正常的)。
    /// </summary>
    public static ConversationState ReconcileSubagents(ConversationState state, ActiveRunIndex index)
    {
        var live = index.Subagents.Select(s => s.RunId).ToHashSet(StringComparer.Ordinal);
        var next = state;
        foreach (var (key, entry) in state.Subagents)
        {
            if (key.DeviceId != index.DeviceId || entry.IsTerminal)
            {
                continue;
            }

            if (entry.Record.ChildRunId is { } child && live.Contains(child))
            {
                if (entry.Unknown)
                {
                    next = Set(next, entry with { Unknown = false });
                }

                continue;
            }

            if (index.Rev <= entry.StartedAtRev)
            {
                continue;
            }

            // 先问库里的回执:它是持久的,说得清就别猜
            var fromReceipt = ApplyReceipts(next, key.DeviceId, key.SessionId, entry.StartedAtRev);
            if (fromReceipt.Subagents.TryGetValue(key, out var settled) && settled.IsTerminal)
            {
                next = fromReceipt;
                continue;
            }

            next = Set(fromReceipt, entry with
            {
                Record = SubagentView.ApplyEnd(entry.Record, SubagentStatus.Aborted, null),
                Guessed = true,
                Unknown = false,
            });
        }

        return next;
    }

    /// <summary>设备断开:没结束的卡片和目标都是「未知」(规则 7):不判结束,也不继续计时。已收口的不动。</summary>
    public static ConversationState MarkUnknown(ConversationState state, string deviceId)
    {
        var next = state;
        foreach (var (key, entry) in state.Subagents)
        {
            if (key.DeviceId == deviceId && !entry.IsTerminal && !entry.Unknown)
            {
                next = Set(next, entry with { Unknown = true });
            }
        }

        foreach (var (key, goal) in state.Goals)
        {
            if (key.DeviceId == deviceId && goal is { Goal: not null, Unknown: false })
            {
                next = next with { Goals = next.Goals.SetItem(key, goal with { Unknown = true }) };
            }
        }

        return next;
    }

    // ───────────────────────── 目标 ─────────────────────────

    public static ActiveGoal ToGoal(GoalDto dto) => new(
        dto.Id, dto.Condition, GoalWire.ParseOrigin(dto.Origin) ?? GoalOrigin.Restored, DateTimeOffset.FromUnixTimeMilliseconds(dto.SetAt), dto.TokensAtStart)
    {
        Iterations = dto.Iterations,
        LastReason = dto.LastReason,
        DeferredSince = dto.DeferredSince is { } deferred ? DateTimeOffset.FromUnixTimeMilliseconds(deferred) : null,
        CheckinCount = dto.CheckinCount,
        LastDeferralPassAt = dto.LastDeferralPassAt is { } pass ? DateTimeOffset.FromUnixTimeMilliseconds(pass) : null,
        IdleCheckinCount = dto.IdleCheckinCount,
        Tokens = dto.Tokens,
    };

    /// <summary>
    /// <c>goal.changed</c>(<c>goal</c> 主题):这个会话的目标(全量,null = 没有)。只收版本不更旧的(清除留墓碑,所以迟到的旧设定不能让它复活);
    /// 带外写了标记的那条助手消息按 id 替换进会话镜像(和版本无关:替换是幂等的)。
    /// </summary>
    public static ConversationState ApplyGoalChange(ConversationState state, string deviceId, GoalChangeDto dto)
    {
        var session = new SessionRef(deviceId, dto.SessionId);
        var next = SetGoal(state, session, dto.Goal, dto.Rev, GoalWire.ParseClearReason(dto.Cleared));
        return dto.Message is { } message ? ConversationReducer.ApplyMessage(next, session, message) : next;
    }

    /// <summary>
    /// <c>goal.get</c> 的结果,和 <see cref="ApplyGoalChange"/> 同一个类型(首帧和后续变更同一个类型),但<b>优先级更低</b>:只收版本<b>更新</b>的。
    /// 设备给的版本号是读目标之后读的,可能比这份目标本身更新;同一个版本的变更事件一定会随后(或已经)经 <c>goal</c> 主题到达,事件才是权威,所以同版本时以事件为准。
    /// </summary>
    public static ConversationState ApplyGoalState(ConversationState state, string deviceId, GoalStateDto dto)
    {
        var session = new SessionRef(deviceId, dto.SessionId);
        return state.Goals.TryGetValue(session, out var existing) && existing.Rev >= dto.Rev
            ? state
            : SetGoal(state, session, dto.Goal, dto.Rev, null);
    }

    /// <summary>
    /// <c>goal</c> 主题的快照:内存里所有会话的目标。快照里没有、而镜像里还有的(这台设备的),说明已经清掉了(只在快照不比镜像旧时才下这个结论)。
    /// </summary>
    public static ConversationState ApplyGoalsSnapshot(ConversationState state, string deviceId, GoalsTopicState snapshot)
    {
        var next = state;
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var goal in snapshot.Goals)
        {
            present.Add(goal.SessionId);
            next = SetGoal(next, new SessionRef(deviceId, goal.SessionId), goal, snapshot.Rev, null);
        }

        foreach (var (key, entry) in state.Goals)
        {
            if (key.DeviceId == deviceId && entry.Goal is not null && !present.Contains(key.SessionId))
            {
                next = SetGoal(next, key, null, snapshot.Rev, null);
            }
        }

        return next;
    }

    private static ConversationState SetGoal(ConversationState state, SessionRef session, GoalDto? goal, long rev, GoalClearReason? cleared)
    {
        if (state.Goals.TryGetValue(session, out var existing) && existing.Rev > rev)
        {
            return state;
        }

        var entry = new GoalEntry(goal is null ? null : ToGoal(goal), rev, goal is null ? cleared ?? existing?.Cleared : null);
        return entry == existing ? state : state with { Goals = state.Goals.SetItem(session, entry) };
    }

    // ───────────────────────── 待办 ─────────────────────────

    /// <summary>会话消息变了:重算待办视图(Core 的派生函数)。和待办无关的消息(没有 <c>TodoWrite</c> 也没有工具结果)不重算。</summary>
    public static SessionView WithTodo(SessionView view, MessageDto? changed = null)
    {
        if (changed is not null
            && !changed.PartsJson.Contains(TodoTranscript.ToolName, StringComparison.Ordinal)
            && !changed.PartsJson.Contains("\"tool_result\"", StringComparison.Ordinal))
        {
            return view;
        }

        var todo = TodoTranscript.ViewOf(view.Messages);
        return SameTodo(todo, view.Todo) ? view : view with { Todo = todo };
    }

    // ───────────────────────── 小工具 ─────────────────────────

    private static bool SameTodo(TodoView? a, TodoView? b) =>
        a is null ? b is null : b is not null && a.Items.SequenceEqual(b.Items);

    private static SubagentEntry New(string deviceId, string sessionId, string callId, SubagentRecord record, SubagentTelemetry telemetry, long rev) =>
        new(new SubagentRef(deviceId, sessionId, callId), record, telemetry, rev);

    private static ConversationState Set(ConversationState state, SubagentEntry entry) =>
        state with { Subagents = state.Subagents.SetItem(entry.Ref, entry) };

    private static ConversationState Upsert(ConversationState state, SubagentRef key, long knownRev, Func<SubagentEntry?, SubagentEntry> change)
    {
        state.Subagents.TryGetValue(key, out var existing);
        var next = change(existing);
        return Set(state, next);
    }
}
