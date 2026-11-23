using System.Collections.Immutable;
using Ncw.Client.Transcript;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Stores;

/// <summary>
/// 把设备推来的快照和事件折进 <see cref="ConversationState"/> 的纯函数。幂等、单调:
/// 同一个事件重放、快照晚于事件到达、乱序的广播都不会让状态倒退(AGENTS.md §3 规则 5、6)。
/// </summary>
public static class ConversationReducer
{
    // ───────────────────────── 会话 ─────────────────────────

    /// <param name="knownRev">当前已知的 activeRuns 版本:由消息里的回执新建的子代理卡片记下它,之后只有更新的权威广播才能判它结束。</param>
    public static ConversationState ApplySession(ConversationState state, string deviceId, SessionTopicState snapshot, long knownRev = 0)
    {
        var key = new SessionRef(deviceId, snapshot.Page.Session.Id);
        var next = OrchestrationReducer.WithTodo(new SessionView(key, snapshot.Page.Session, [.. snapshot.Page.Messages], snapshot.Page.HasMore, snapshot.Queue, snapshot.ActiveRunId));
        // 快照是权威的,但队列只收更新的那份(队列版本全局单调)。
        if (state.Sessions.TryGetValue(key, out var existing) && existing.Queue.Rev > next.Queue.Rev)
        {
            next = next with { Queue = existing.Queue };
        }

        return OrchestrationReducer.ApplyReceipts(state with { Sessions = state.Sessions.SetItem(key, next) }, deviceId, key.SessionId, knownRev);
    }

    public static ConversationState ApplySessionUpdated(ConversationState state, string deviceId, SessionDto meta) =>
        UpdateSession(state, new SessionRef(deviceId, meta.Id), v => v.Meta.UpdatedAt > meta.UpdatedAt ? v : v with { Meta = meta });

    public static ConversationState ApplyQueue(ConversationState state, string deviceId, QueueDto queue) =>
        UpdateSession(state, new SessionRef(deviceId, queue.SessionId), v => queue.Rev < v.Queue.Rev ? v : v with { Queue = queue });

    /// <summary>一条消息落库:按 id 追加或替换(<c>message_end</c> 和 <c>message.committed</c> 两个来源折成同一个结果)。</summary>
    /// <remarks>子代理的持久回执(<c>tool_result.subagent</c>)随消息合进卡片表;待办视图随消息重算(两者的规则都在 Core 里)。</remarks>
    public static ConversationState ApplyMessage(ConversationState state, SessionRef session, MessageDto message, long knownRev = 0, IReadOnlyDictionary<string, ToolCallView>? tools = null)
    {
        var next = UpdateSession(state, session, v => OrchestrationReducer.WithTodo(v with { Messages = Upsert(v.Messages, message) }, message));
        return message.PartsJson.Contains("\"subagent\"", StringComparison.Ordinal)
            ? OrchestrationReducer.ApplyReceipts(next, session.DeviceId, session.SessionId, knownRev, tools)
            : next;
    }

    public static ConversationState ApplyHistoryReset(ConversationState state, string deviceId, SessionPageDto page, long knownRev = 0)
    {
        var next = UpdateSession(state, new SessionRef(deviceId, page.Session.Id), v =>
            OrchestrationReducer.WithTodo(v with { Meta = page.Session, Messages = [.. page.Messages], HasMore = page.HasMore }));
        return OrchestrationReducer.ApplyReceipts(next, deviceId, page.Session.Id, knownRev);
    }

    public static ConversationState RemoveSession(ConversationState state, SessionRef session) =>
        state with { Sessions = state.Sessions.Remove(session) };

    private static ConversationState UpdateSession(ConversationState state, SessionRef key, Func<SessionView, SessionView> change)
    {
        if (!state.Sessions.TryGetValue(key, out var view))
        {
            return state;
        }

        var next = change(view);
        return ReferenceEquals(next, view) ? state : state with { Sessions = state.Sessions.SetItem(key, next) };
    }

    private static ImmutableArray<MessageDto> Upsert(ImmutableArray<MessageDto> messages, MessageDto message)
    {
        for (var i = messages.Length - 1; i >= 0; i--)
        {
            if (messages[i].Id == message.Id)
            {
                return messages.SetItem(i, message);
            }
        }

        return messages.Add(message);
    }

    // ───────────────────────── 交互 ─────────────────────────

    /// <summary><c>interactions</c> 主题是全量:收到修订号不更小的就整体替换(规则 5)。</summary>
    public static ConversationState ApplyInteractions(ConversationState state, string deviceId, InteractionsDto dto) =>
        state.Interactions.TryGetValue(deviceId, out var existing) && existing.Revision > dto.Revision
            ? state
            : state with { Interactions = state.Interactions.SetItem(deviceId, new DeviceInteractions(dto.Revision, [.. dto.Pending])) };

    // ───────────────────────── Run ─────────────────────────

    public static RunPhase PhaseOf(string status) => status switch
    {
        RunStatusNames.Running => RunPhase.Running,
        RunStatusNames.Done => RunPhase.Done,
        RunStatusNames.Aborted => RunPhase.Aborted,
        RunStatusNames.Error => RunPhase.Error,
        _ => RunPhase.Unknown,
    };

    /// <summary>
    /// 控制端自己发起了一个 Run(<c>runs.start</c> 返回 started):在第一个事件到达之前就有一份镜像,界面立刻能显示「运行中」。
    /// 已经有镜像(事件比返回值先到)就不动。
    /// </summary>
    public static ConversationState ApplyRunStarted(ConversationState state, RunRef run, string sessionId, long knownRev)
    {
        if (state.Runs.ContainsKey(run))
        {
            return state;
        }

        var view = RunView.New(run, sessionId, knownRev) with { Phase = RunPhase.Running };
        return state with { Runs = state.Runs.SetItem(run, view) };
    }

    /// <summary>
    /// <c>run/&lt;id&gt;</c> 的快照:把日志里的全部事件从头折一遍,再和已有的镜像合并(阶段取更靠后的、序号取更大的、已收口不回退)。
    /// </summary>
    public static ConversationState ApplyRunSnapshot(ConversationState state, string deviceId, RunSnapshotDto snapshot, long knownRev)
    {
        var run = new RunRef(deviceId, snapshot.RunId);
        state.Runs.TryGetValue(run, out var existing);
        var rebuilt = RunView.New(run, snapshot.SessionId, existing?.StartedAtRev ?? knownRev);
        var next = state;
        var seen = existing?.Seq ?? 0;
        foreach (var e in snapshot.Events)
        {
            // Run 的镜像总是从头折一遍;但落到会话上的副作用(消息、回执)只认镜像还没收过的事件——序号不大于已收到的就是重放,
            // 和 ApplyRunEvent 同一条规则。否则一份迟到的快照会把这之后已经被截断(编辑重发 / 重新生成)的消息重新塞回会话。
            var (folded, view) = Fold(next, deviceId, rebuilt, e.Seq, e.Event, knownRev);
            rebuilt = view;
            if (e.Seq > seen)
            {
                next = folded;
            }
        }

        // ★ 迟到的旧快照:快照的返回值和推送的事件走两条路,镜像可能已经收到了比它更靠后的事件。这时「当前状态」类的字段(流式文本、思考、重试、
        //   上下文占用、工具卡片)以镜像为准,快照只补镜像里没有的——否则已经被 message_end 清掉的流式文本会被带回来,而能再次清掉它的事件
        //   序号都不大于已收到的、会被当作重放丢弃,这段文本就永远留在镜像里;已经结束的工具也会变回运行中(规则 6)。
        var stale = existing is not null && existing.Seq > snapshot.Seq;

        // 推断出来的收口(PhaseGuessed)不算「已知的终态」:真实的状态来了就替换它。
        var known = existing is { PhaseGuessed: true } ? RunPhase.Unknown : existing?.Phase ?? RunPhase.Unknown;
        var phase = RunLifecycle.Merge(known, RunLifecycle.Merge(rebuilt.Phase, PhaseOf(snapshot.State)));
        var merged = rebuilt with
        {
            Phase = phase,
            PhaseGuessed = existing?.PhaseGuessed == true && !RunLifecycle.IsTerminal(PhaseOf(snapshot.State)) && !RunLifecycle.IsTerminal(rebuilt.Phase),
            Seq = Math.Max(existing?.Seq ?? 0, snapshot.Seq),
            Settled = snapshot.Settled || existing?.Settled == true,
            Error = rebuilt.Error ?? snapshot.Error ?? existing?.Error,
            Tools = existing is null ? rebuilt.Tools : stale ? rebuilt.Tools.SetItems(existing.Tools) : existing.Tools.SetItems(rebuilt.Tools),
            Streaming = stale ? existing!.Streaming : rebuilt.Streaming,
            Thinking = stale ? existing!.Thinking : rebuilt.Thinking,
            Retry = stale ? existing!.Retry : rebuilt.Retry,
            Context = stale ? existing!.Context ?? rebuilt.Context : rebuilt.Context,
            CompactPhase = stale ? existing!.CompactPhase ?? rebuilt.CompactPhase : rebuilt.CompactPhase,
        };
        return next with { Runs = next.Runs.SetItem(run, merged) };
    }

    /// <summary>一个增量事件。序号不大于已收到的最后一个就是重放,直接丢弃。</summary>
    public static ConversationState ApplyRunEvent(ConversationState state, string deviceId, string runId, long seq, RunEventDto ev, long knownRev = 0)
    {
        var run = new RunRef(deviceId, runId);
        if (!state.Runs.TryGetValue(run, out var view))
        {
            // 续传的补发先于任何快照到达(镜像是上次连接留下的,不会缺);真缺了就是从事件里建一份。
            view = RunView.New(run, ev is RunStartEventDto start ? start.SessionId : "", 0);
        }

        if (seq <= view.Seq && state.Runs.ContainsKey(run))
        {
            return state;
        }

        var (next, folded) = Fold(state, deviceId, view, seq, ev, knownRev);
        return next with { Runs = next.Runs.SetItem(run, folded) };
    }

    private static (ConversationState State, RunView View) Fold(ConversationState state, string deviceId, RunView view, long seq, RunEventDto ev, long knownRev)
    {
        view = view with { Seq = Math.Max(view.Seq, seq) };
        switch (ev)
        {
            case RunStartEventDto e:
                return (state, view with { SessionId = e.SessionId, Phase = RunLifecycle.Merge(view.Phase, RunPhase.Running) });

            case MessageDeltaEventDto { Kind: "text" } e:
                return (state, view with
                {
                    Streaming = view.Streaming.SetItem(e.MessageId, view.Streaming.GetValueOrDefault(e.MessageId, "") + e.Delta),
                    Retry = null,
                });

            case MessageDeltaEventDto { Kind: "thinking" } e:
                return (state, view with
                {
                    Thinking = view.Thinking.SetItem(e.MessageId, view.Thinking.GetValueOrDefault(e.MessageId, "") + e.Delta),
                    Retry = null,
                });

            case ProviderRetryEventDto e:
                return (state, view with { Retry = e });

            case ContextUsageEventDto e:
                return (state, view with { Context = e });

            case ContextStatusEventDto e:
                return (state, view with { CompactPhase = e.Phase });

            case MessageEndEventDto e:
            {
                var next = view.SessionId.Length == 0 ? state : ApplyMessage(state, new SessionRef(deviceId, view.SessionId), e.Message, knownRev, view.Tools);
                return (next, view with { Streaming = view.Streaming.Remove(e.Message.Id), Thinking = view.Thinking.Remove(e.Message.Id), Retry = null });
            }

            case MessageDiscardEventDto e:
                return (state, view with { Streaming = view.Streaming.Remove(e.MessageId), Thinking = view.Thinking.Remove(e.MessageId) });

            case ToolStartEventDto e:
                return (state, view with { Tools = view.Tools.SetItem(e.CallId, new ToolCallView(e.CallId, e.ToolName, e.InputJson, ToolCallStatus.Running, null, e.Environment)) });

            case ToolProgressEventDto e:
                // 排队中的 Task 调用(等子代理名额):结构化的排位;别的进度不在这里画
                if (SubagentReceipts.QueuedAhead(e.DetailsJson) is { } ahead && view.Tools.TryGetValue(e.CallId, out var queued))
                {
                    return (view.SessionId.Length == 0 ? state : OrchestrationReducer.MarkQueued(state, deviceId, view.SessionId, e.CallId), view with { Tools = view.Tools.SetItem(e.CallId, queued with { QueuedAhead = ahead }) });
                }

                // 交互模式的 widget 卡片:先经进度推出来、工具再挂起等提交。终态的详情(tool_end)压过进度的,重放的旧进度不能盖掉它(规则 6)
                return Ncw.Client.Widgets.WidgetCardReader.IsWidgetCard(e.DetailsJson) && view.Tools.TryGetValue(e.CallId, out var live) && live.Status == ToolCallStatus.Running
                    ? (state, view with { Tools = view.Tools.SetItem(e.CallId, live with { DetailsJson = e.DetailsJson }) })
                    : (state, view);

            case ToolEndEventDto e:
            {
                var existing = view.Tools.GetValueOrDefault(e.CallId) ?? new ToolCallView(e.CallId, "", "{}", ToolCallStatus.Running, null, null);
                // 前台的 Task 调用要等子代理跑完才返回,结束就不再排队;后台的立刻返回而派发可能还在排队(直到 subagent_start,或父 Run 结束把排队的取消):排位留着
                var stillQueued = existing.QueuedAhead is not null && !e.IsError && SubagentReceipts.IsBackgroundCall(existing.InputJson);
                var showsDetails = Ncw.Client.Widgets.WidgetCardReader.IsDetails(e.DetailsJson)
                    || !e.IsError && GeneratedMedia.KindOf(existing.Name) is { } kind && GeneratedMedia.Read(e.DetailsJson, kind) is not null;
                return (state, view with { Tools = view.Tools.SetItem(e.CallId, existing with { Status = e.IsError ? ToolCallStatus.Failed : ToolCallStatus.Done, Output = e.Content, QueuedAhead = stillQueued ? existing.QueuedAhead : null, DetailsJson = showsDetails ? e.DetailsJson : existing.DetailsJson }) });
            }

            case SubagentStartEventDto or SubagentUpdateEventDto or SubagentEndEventDto:
            {
                // 子代理的状态镜像:并进父 Run 日志里的事件;排队的排位随 start(拿到名额)清掉
                var folded = OrchestrationReducer.ApplySubagentEvent(state, deviceId, view.SessionId, knownRev, view.Tools, ev);
                var callId = ev switch { SubagentStartEventDto s => s.CallId, SubagentEndEventDto d => d.CallId, SubagentUpdateEventDto u => u.CallId, _ => "" };
                return (folded, ev is SubagentStartEventDto && view.Tools.TryGetValue(callId, out var call)
                    ? view with { Tools = view.Tools.SetItem(callId, call with { QueuedAhead = null }) }
                    : view);
            }

            case RunEndEventDto e:
                return (state, view with { Phase = EndPhase(view, e.Status), PhaseGuessed = false, Error = e.Error ?? view.Error, Tools = WithoutQueue(view.Tools) });

            case RunSettledEventDto e:
                return (state, view with { Settled = true, Phase = EndPhase(view, e.Status), PhaseGuessed = false });

            default:
                return (state, view);
        }
    }

    /// <summary>Run 结束了:排着队的派发随父 Run 一起被取消(不会再开始),排位不再有意义。</summary>
    private static ImmutableDictionary<string, ToolCallView> WithoutQueue(ImmutableDictionary<string, ToolCallView> tools) =>
        tools.Values.Any(t => t.QueuedAhead is not null)
            ? tools.SetItems(tools.Values.Where(t => t.QueuedAhead is not null).Select(t => KeyValuePair.Create(t.CallId, t with { QueuedAhead = null })))
            : tools;

    /// <summary>真实的结局:一般按单调规则合并(终态不再改);对账推断出来的收口(<see cref="RunView.PhaseGuessed"/>)直接被真实结局替换。</summary>
    private static RunPhase EndPhase(RunView view, string status) =>
        view.PhaseGuessed ? PhaseOf(status) : RunLifecycle.Merge(view.Phase, PhaseOf(status));

    // ───────────────────────── 连接 / 对账 ─────────────────────────

    /// <summary>设备断开:运行中的 Run 变成未知,不判结束、不继续算运行(规则 7);已收口的不动。</summary>
    public static ConversationState MarkDeviceUnknown(ConversationState state, string deviceId)
    {
        var changed = false;
        var runs = state.Runs.ToBuilder();
        foreach (var (key, view) in state.Runs)
        {
            if (key.DeviceId == deviceId && view.Phase == RunPhase.Running)
            {
                runs[key] = view with { Phase = RunPhase.Unknown };
                changed = true;
            }
        }

        // 子代理卡片和目标同样是未知:不判结束、不继续计时
        return OrchestrationReducer.MarkUnknown(changed ? state with { Runs = runs.ToImmutable() } : state, deviceId);
    }

    /// <summary>
    /// 用权威的 activeRuns 广播给这台设备的 Run 对账(规则 5):还在运行 / 未知的 Run 如果不在更新的索引里,说明它已经结束了——哪怕结束事件丢了。
    /// 索引里有的未知 Run 恢复成运行中。只有版本比这份镜像创建时更新的索引才能据此判定(旧广播里没有刚起的 Run 是正常的)。
    /// 结局不明时先记为 <see cref="RunPhase.Done"/> 并标 <see cref="RunView.PhaseGuessed"/>:界面只需要「不再运行」;随后(或同时)到达的真实 <c>run_end</c> / 快照会替换这个推断
    /// (广播和事件走不同的主题,谁先到不保证——如果把推断当终态锁死,「已中止」就会永远显示成「已完成」)。
    /// </summary>
    public static ConversationState Reconcile(ConversationState state, ActiveRunIndex index)
    {
        var live = index.Runs.Select(r => r.RunId).Concat(index.Subagents.Select(s => s.RunId)).ToHashSet(StringComparer.Ordinal);
        var changed = false;
        var runs = state.Runs.ToBuilder();
        foreach (var (key, view) in state.Runs)
        {
            if (key.DeviceId != index.DeviceId)
            {
                continue;
            }

            if (live.Contains(key.RunId))
            {
                if (view.Phase == RunPhase.Unknown)
                {
                    runs[key] = view with { Phase = RunPhase.Running };
                    changed = true;
                }
            }
            else if (!RunLifecycle.IsTerminal(view.Phase) && index.Rev > view.StartedAtRev)
            {
                runs[key] = view with { Phase = RunPhase.Done, Settled = true, PhaseGuessed = true };
                changed = true;
            }
        }

        // 子代理卡片同样靠这份权威广播收口(它的 subagents 半边)
        return OrchestrationReducer.ReconcileSubagents(changed ? state with { Runs = runs.ToImmutable() } : state, index);
    }
}
