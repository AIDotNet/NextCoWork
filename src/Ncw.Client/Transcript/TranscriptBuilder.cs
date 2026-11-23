using System.Collections.Immutable;
using System.Text.Json;
using Ncw.Client.Stores;
using Ncw.Client.Widgets;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Transcript;

/// <summary>
/// 把一个会话在 Store 里的全部镜像(消息、流式增量、工具、子代理、挂起的交互、Run 状态)派生成转录行的纯函数。
/// 没有任何状态:Store 变了就重新算一遍,视图按 <see cref="TranscriptRow.Key"/> 对齐、只更新变了的行。
/// 容错:消息内容是 JSON 文本,坏 JSON、缺字段、未知块都跳过,不抛(设备是真源,界面不能因为一条怪消息整页崩掉)。
/// </summary>
public static class TranscriptBuilder
{
    /// <summary>卡片上摘要最多显示几行。</summary>
    public const int PreviewLineLimit = 6;

    private const int MaxLineLength = 200;

    /// <param name="fallbackEnv">落库的旧调用没带环境标识时,用项目所在的环境兜底(设备 + 环境)。</param>
    /// <param name="deviceUnknown">设备断开:转录末尾加「状态未知」,运行中的东西不再当成还在跑(规则 7)。</param>
    /// <param name="groupFinishedWork">已完成的一轮把最后一段文字以外的操作收成「工作区」分组(<see cref="WorkGroupRow"/>)。主对话打开;子代理转录不分组。</param>
    /// <param name="groupToolRuns">两段文字之间连续的工具调用(至少 2 个)收成一行摘要(<see cref="ToolRunGroupRow"/>),运行中也收。主对话打开;子代理转录不分组。</param>
    /// <param name="sessionRunning">设备的活动 Run 表说这个会话在跑(Core 的 <c>SessionActivityView</c>,和停止按钮同一个真源)。
    /// 队列 / 目标续跑 / 别的控制端发起的 Run,在 Run 镜像到达之前也要有「生成中」(issue #200)。</param>
    public static ImmutableArray<TranscriptRow> Build(
        ConversationState state, SessionRef session, EnvRefDto? fallbackEnv, DateTimeOffset now, bool deviceUnknown = false, bool groupFinishedWork = false,
        bool sessionRunning = false, bool groupToolRuns = false)
    {
        if (!state.Sessions.TryGetValue(session, out var view))
        {
            return [];
        }

        var runs = state.Runs.Values.Where(r => r.Ref.DeviceId == session.DeviceId && r.SessionId == session.SessionId).ToList();
        var liveTools = new Dictionary<string, ToolCallView>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            foreach (var (id, tool) in run.Tools)
            {
                liveTools[id] = tool;
            }
        }

        var subagents = state.SubagentsOf(session);
        var pending = PendingFor(state, session, runs, subagents, view);
        // 等待审批的调用:卡片上的执行环境用审批带来的(设备算好的,「在哪台设备的哪个环境里执行」),不用项目环境兜底
        var approvals = pending.OfType<ToolPermissionInteractionDto>().GroupBy(p => p.CallId).ToDictionary(g => g.Key, g => g.First().Env, StringComparer.Ordinal);

        var parsed = view.Messages.Select(m => (Message: m, Parts: MessageParts.Parse(m.PartsJson))).ToList();
        var results = new Dictionary<string, ParsedPart>(StringComparer.Ordinal);
        foreach (var (_, parts) in parsed)
        {
            foreach (var part in parts.Where(p => p.Type == "tool_result" && p.CallId is not null))
            {
                results[part.CallId!] = part;
            }
        }

        var widgets = new WidgetContext(
            pending.OfType<WidgetSubmitInteractionDto>().Select(w => w.CallId).ToHashSet(StringComparer.Ordinal),
            WidgetData(parsed.SelectMany(p => p.Parts), liveTools.Values),
            deviceUnknown);

        var subagentByCall = subagents.ToDictionary(e => e.Ref.CallId, StringComparer.Ordinal);
        // 快照里的 ActiveRunId 只在拿到快照那一刻对:它指的 Run 在 Store 里已经是终态,就不再算「还在跑」(否则这一轮永远收不成分组、工具永远显示运行中)
        var snapshotRunLive = view.ActiveRunId is { } snapshotRun
            && !runs.Any(r => r.Ref.RunId == snapshotRun && r.Phase is not (RunPhase.Running or RunPhase.Unknown));
        var anyRunActive = snapshotRunLive || runs.Any(r => r.Phase == RunPhase.Running);
        var liveRuns = runs.Where(r => r.Phase is RunPhase.Running or RunPhase.Unknown).ToList();
        var rows = new List<TranscriptRow>();
        var emittedCalls = new HashSet<string>(StringComparer.Ordinal);
        var committed = view.Messages.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var (message, parts) in parsed)
        {
            var texts = 0;
            var thoughts = 0;
            var user = message.Role == "user";
            var userText = new List<string>();
            var files = new List<string>();
            var images = 0;
            foreach (var part in parts)
            {
                switch (part.Type)
                {
                    case "text" when user && !message.Internal:
                        userText.Add(part.Text ?? "");
                        break;
                    case "file_ref" when user && !message.Internal:
                        files.Add(part.Name ?? part.Path ?? "");
                        break;
                    case "image" when user && !message.Internal:
                        images++;
                        break;
                    case "text" when !user && (part.Text ?? "").Length > 0:
                        rows.Add(new AssistantRow($"{message.Id}:t{texts++}", message.Id, part.Text!, false));
                        break;
                    case "thinking" when !user:
                        // 下标照样前进(key 与流式时对齐);只有透传载体、没有文字的思考块(redacted / 加密推理条目)不画:没有任何可看的,只会多出一行「思考了」
                        var thinkingKey = $"{message.Id}:k{thoughts++}";
                        if (!string.IsNullOrWhiteSpace(part.Text))
                        {
                            rows.Add(new ThinkingRow(thinkingKey, part.Text, part.DurationMs, false));
                        }

                        break;
                    case "tool_call" when part.CallId is { } callId:
                        emittedCalls.Add(callId);
                        rows.Add(part.Name == SubagentReceipts.TaskToolName
                            ? SubagentRowOf(session, callId, subagentByCall, results, now, deviceUnknown)
                            : ToolOrWidgetRow(part, results.GetValueOrDefault(callId), liveTools.GetValueOrDefault(callId), fallbackEnv, approvals, anyRunActive, deviceUnknown, widgets));
                        break;
                    case "error":
                        rows.Add(new ErrorRow($"{message.Id}:e{rows.Count}", part.ErrorCode ?? "unknown", part.ErrorMessage ?? "", part.ErrorKey, part.ErrorRetryable, false, part.ErrorStatus));
                        break;
                    case "subagent" when part.CallId is { } reportCall && part.ChildRunId is { } child:
                        rows.Add(new SubagentReportRow($"{message.Id}:r{reportCall}", reportCall, child, part.Text));
                        break;
                    case "goal_status":
                        rows.Add(new GoalStatusRow($"{message.Id}:g{rows.Count}", part.Met, part.Text ?? "", part.Reason, part.Failed, part.Cleared, part.Set));
                        break;
                    case "compact_boundary":
                        rows.Add(new CompactRow($"{message.Id}:c", part.Trigger ?? "auto", part.PreTokens, part.PostTokens, part.Text ?? ""));
                        break;
                }
            }

            if (user && !message.Internal && (userText.Count > 0 || files.Count > 0 || images > 0))
            {
                rows.Add(new UserRow(message.Id, message.Id, string.Concat(userText), string.Join(", ", files), images, DateTimeOffset.FromUnixTimeMilliseconds(message.CreatedAt)));
            }
        }

        // 还没落库的:流式文字 / 思考、已开始的工具。只看还在跑(或状态未知)的 Run:已经收口的 Run,该落库的都在消息里了,
        // 镜像里剩下的(被丢弃的消息、被改写掉的历史、不在当前消息窗口里的旧调用)再画就会堆在转录末尾,而且永远不消失
        foreach (var run in runs.Where(r => r.Phase is RunPhase.Running or RunPhase.Unknown).OrderBy(r => r.Ref.RunId, StringComparer.Ordinal))
        {
            foreach (var (messageId, text) in run.Thinking.Where(kv => !committed.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value)))
            {
                rows.Add(new ThinkingRow($"{messageId}:k0", text, null, true));
            }

            foreach (var (messageId, text) in run.Streaming.Where(kv => !committed.Contains(kv.Key)))
            {
                rows.Add(new AssistantRow($"{messageId}:t0", messageId, text, true));
            }

            foreach (var tool in run.Tools.Values.Where(t => !emittedCalls.Contains(t.CallId)))
            {
                emittedCalls.Add(tool.CallId);
                rows.Add(tool.Name == SubagentReceipts.TaskToolName
                    ? SubagentRowOf(session, tool.CallId, subagentByCall, results, now, deviceUnknown)
                    : ToolOrWidgetRow(new ParsedPart("tool_call") { CallId = tool.CallId, Name = tool.Name, InputJson = tool.InputJson }, null, tool, fallbackEnv, approvals, anyRunActive, deviceUnknown, widgets));
            }
        }

        // 先分连续工具、再分工作区:工作区把内层表头连同它的成员一起算作自己的成员
        if (groupToolRuns)
        {
            rows = GroupToolRuns(rows, deviceUnknown);
        }

        if (groupFinishedWork && !anyRunActive && !deviceUnknown && pending.Count == 0)
        {
            rows = GroupFinishedWork(rows);
        }

        // 失败的 Run:只有这个会话最近的一个 Run 失败了才在末尾画它的卡片(能重试)。之后又跑过 Run(成功也好、还在跑也好),
        // 旧的失败就只剩消息里持久化的 error 块,留在它发生的位置——Store 不清理收口的 Run,不加这个限制,旧错误会一直钉在转录末尾
        var last = runs.OrderByDescending(r => r.Ref.RunId, StringComparer.Ordinal).FirstOrDefault();
        if (last is { Phase: RunPhase.Error, Error: { } error })
        {
            // 同一次失败会落两份:Agent 把错误写进最后一条助手消息(持久化的 error 块),Run 的终态也带着它。
            // 转录末尾紧挨着的那条消息错误就是这一次失败:只留 Run 的这一张(它能重试),不画两张一样的卡片(03 §11.4)
            if (rows.Count > 0 && rows[^1] is ErrorRow { Key: var previous } && !previous.StartsWith("run:", StringComparison.Ordinal))
            {
                rows.RemoveAt(rows.Count - 1);
            }

            rows.Add(new ErrorRow($"run:{last.Ref.RunId}", error.Code, error.Message, error.MessageKey, error.Retryable, true, error.Status));
        }

        if (deviceUnknown && (view.ActiveRunId is not null || runs.Any(r => r.Phase == RunPhase.Unknown)))
        {
            rows.Add(new StatusRow("status", RunStatusKind.Unknown, 0, 0));
        }
        // 重试 / 压缩 / 运行中的工具都只看还在跑的 Run:Store 不清理收口的 Run,收口时残留的 Retry、没等到 tool_end 的工具
        // 会让「生成中」在之后每一轮都出不来,或者让「正在重试」钉在空闲的会话上(issue #200)。
        else if (liveRuns.FirstOrDefault(r => r.Retry is not null) is { Retry: { } retry })
        {
            rows.Add(new StatusRow("status", RunStatusKind.Retrying, retry.Attempt, retry.DelayMs));
        }
        else if (liveRuns.Any(r => r.CompactPhase == "compacting"))
        {
            rows.Add(new StatusRow("status", RunStatusKind.Compacting, 0, 0));
        }
        else if ((anyRunActive || sessionRunning) && pending.Count == 0 && !rows.Any(ShowsOwnActivity) &&
            !liveRuns.Any(static r => r.Tools.Values.Any(static t => t.Status == ToolCallStatus.Running)))
        {
            rows.Add(new StatusRow("status", RunStatusKind.Generating, 0, 0));
        }

        foreach (var interaction in pending)
        {
            rows.Add(new InteractionRow($"i:{interaction.Id}", interaction));
        }

        return [.. rows];
    }

    /// <summary>
    /// 一轮(从一条用户消息到下一条)已经全部完成时,把最后一段回复文字之前的操作挪到一个 <see cref="WorkGroupRow"/> 表头后面。
    /// 只在这一轮「以一段写完的回复文字收尾」时收:最后一个可收的行必须是它,否则(中止了、停在工具调用上、还在流)原样保留,什么都不藏。
    /// 还在跑 / 等审批的工具、还在跑的子代理也不收。成员以外的行(可视化卡片、错误、目标状态、压缩边界)保持原有的相对顺序,排在这一组后面。
    /// </summary>
    private static List<TranscriptRow> GroupFinishedWork(List<TranscriptRow> rows)
    {
        var result = new List<TranscriptRow>(rows.Count);
        var start = 0;
        for (var i = 0; i <= rows.Count; i++)
        {
            if (i < rows.Count && rows[i] is not UserRow)
            {
                continue;
            }

            // [start, i) 是一轮(开头可能带着前一条用户消息),下一轮从 i 开始
            GroupTurn(rows, start, i, result);
            start = i;
        }

        return result;
    }

    private static void GroupTurn(List<TranscriptRow> rows, int from, int to, List<TranscriptRow> output)
    {
        var lastWork = -1;
        for (var i = to - 1; i >= from; i--)
        {
            if (rows[i] is AssistantRow or ThinkingRow or ToolRow or SubagentRow or SubagentReportRow)
            {
                lastWork = i;
                break;
            }
        }

        if (lastWork < 0 || rows[lastWork] is not AssistantRow { IsStreaming: false })
        {
            output.AddRange(rows.Skip(from).Take(to - from));
            return;
        }

        var members = new List<TranscriptRow>();
        var rest = new List<TranscriptRow>();
        var firstMember = -1;
        for (var i = from; i < to; i++)
        {
            var row = rows[i];
            if (i < lastWork && row is ToolRow { MediaKind: not null, Status: ToolRowStatus.Running or ToolRowStatus.WaitingApproval })
            {
                output.AddRange(rows.Skip(from).Take(to - from));
                return;
            }

            var inGroup = i < lastWork && row is ThinkingRow or ToolRow { MediaKind: null } or ToolRunGroupRow or SubagentRow or SubagentReportRow or AssistantRow;
            if (inGroup && row is ThinkingRow { IsStreaming: true } or AssistantRow { IsStreaming: true }
                or ToolRow { Status: ToolRowStatus.Running or ToolRowStatus.WaitingApproval }
                or SubagentRow { Unknown: true } or SubagentRow { View.IsActive: true })
            {
                // 这一轮里还有没收口的东西:整轮不收(收一半比不收更糟)
                output.AddRange(rows.Skip(from).Take(to - from));
                return;
            }

            if (inGroup)
            {
                firstMember = firstMember < 0 ? rest.Count : firstMember;
                members.Add(row);
            }
            else
            {
                rest.Add(row);
            }
        }

        if (members.Count == 0)
        {
            output.AddRange(rows.Skip(from).Take(to - from));
            return;
        }

        var actions = members.Count(static m => m is ToolRow or SubagentRow);
        var failed = members.Count(static m => m is ToolRow { Status: ToolRowStatus.Failed });
        rest.InsertRange(firstMember, members.Prepend(new WorkGroupRow($"work:{members[0].Key}", members.Count, actions, failed)));
        output.AddRange(rest);
    }

    /// <summary>
    /// 连续工具分组(03 §6):连续的工具行(可以夹着思考块)至少有 2 个工具时,在它们前面插一个 <see cref="ToolRunGroupRow"/> 表头。
    /// 一串开头 / 结尾的思考块留在组外(它属于前后的文字);等待审批的工具、生成媒体和其他任何行都把一串断开,不进组(审批卡片、媒体要原位露出)。
    /// 设备断开时不带「当前动作」(规则 7:不说「正在」)。
    /// </summary>
    private static List<TranscriptRow> GroupToolRuns(List<TranscriptRow> rows, bool deviceUnknown)
    {
        var result = new List<TranscriptRow>(rows.Count + 4);
        var i = 0;
        while (i < rows.Count)
        {
            if (!IsToolRunMember(rows[i]))
            {
                result.Add(rows[i++]);
                continue;
            }

            var end = i;
            while (end < rows.Count && IsToolRunMember(rows[end]))
            {
                end++;
            }

            // [i, end) 是一串可收的行;去掉两头的思考块后是 [first, last]
            var first = i;
            var last = end - 1;
            while (first <= last && rows[first] is ThinkingRow)
            {
                first++;
            }

            while (last >= first && rows[last] is ThinkingRow)
            {
                last--;
            }

            for (var k = i; k < first; k++)
            {
                result.Add(rows[k]);
            }

            if (first <= last)
            {
                var members = rows.GetRange(first, last - first + 1);
                var tools = members.OfType<ToolRow>().ToList();
                if (tools.Count >= 2)
                {
                    result.Add(ToolRunHeader(members.Count, tools, deviceUnknown));
                }

                result.AddRange(members);
                for (var k = last + 1; k < end; k++)
                {
                    result.Add(rows[k]);
                }
            }

            i = end;
        }

        return result;
    }

    private static bool IsToolRunMember(TranscriptRow row) =>
        row is ThinkingRow or ToolRow { MediaKind: null, Status: not ToolRowStatus.WaitingApproval };

    private static ToolRunGroupRow ToolRunHeader(int members, List<ToolRow> tools, bool deviceUnknown)
    {
        ToolRunActivity? active = null;
        if (!deviceUnknown && tools.FindLastIndex(static t => t.Status == ToolRowStatus.Running) is var running and >= 0)
        {
            var tool = tools[running];
            active = new ToolRunActivity(tool.Kind, tool.Name, tool.Target, running + 1);
        }

        return new ToolRunGroupRow(
            $"tools:{tools[0].Key}",
            members,
            tools.Count,
            tools.Count(static t => t.Kind == ToolKind.Read),
            tools.Count(static t => t.Kind is ToolKind.Edit or ToolKind.Write),
            tools.Count(static t => t.Kind == ToolKind.Command),
            tools.Count(static t => t.Kind == ToolKind.Search),
            tools.Count(static t => t.Kind == ToolKind.Other),
            tools.Count(static t => t.Status == ToolRowStatus.Failed),
            tools.Sum(static t => t.Added),
            tools.Sum(static t => t.Removed),
            active);
    }

    /// <summary>这一行自己就在表明「还在干活」(流式的字、转着的工具、跑着的子代理、等输入的小组件),不需要再加「生成中」。只有空白的流式文字不算。</summary>
    private static bool ShowsOwnActivity(TranscriptRow row) => row switch
    {
        ThinkingRow { IsStreaming: true } => true,
        AssistantRow { IsStreaming: true } assistant => !string.IsNullOrWhiteSpace(assistant.Text),
        ToolRow { Status: ToolRowStatus.Running } or SubagentRow { View.IsActive: true } or WidgetRow { Phase: WidgetPhase.Loading or WidgetPhase.Ready } => true,
        _ => false,
    };

    /// <summary>
    /// <see cref="Build"/> 读到的全部输入的「指纹」:这个会话的镜像、它的各个 Run、子代理表、这台设备的交互、兜底环境、断线标记——全部按引用比较
    /// (Store 是不可变记录,没变就是同一个实例)。指纹相同,派生结果一定相同,视图模型可以跳过整份重新派生:
    /// 别的会话在流式、会话列表 / 设备 / 模型候选变了,这些都会触发刷新,但和这一份转录无关。
    /// 例外是随时间变化的子代理计时(<c>now</c>):调用方在计时刷新时强制重新派生。
    /// </summary>
    public sealed class Inputs
    {
        private readonly SessionView? _view;
        private readonly RunView[] _runs;
        private readonly ImmutableDictionary<SubagentRef, SubagentEntry> _subagents;
        private readonly DeviceInteractions? _interactions;
        private readonly EnvRefDto? _fallback;
        private readonly bool _unknown;
        private readonly bool _sessionRunning;

        private Inputs(
            SessionView? view, RunView[] runs, ImmutableDictionary<SubagentRef, SubagentEntry> subagents, DeviceInteractions? interactions, EnvRefDto? fallback, bool unknown,
            bool sessionRunning) =>
            (_view, _runs, _subagents, _interactions, _fallback, _unknown, _sessionRunning) = (view, runs, subagents, interactions, fallback, unknown, sessionRunning);

        public static Inputs Of(ConversationState state, SessionRef session, EnvRefDto? fallbackEnv, bool deviceUnknown, bool sessionRunning = false) => new(
            state.Sessions.GetValueOrDefault(session),
            [.. state.Runs.Values.Where(r => r.Ref.DeviceId == session.DeviceId && r.SessionId == session.SessionId)],
            state.Subagents,
            state.Interactions.GetValueOrDefault(session.DeviceId),
            fallbackEnv,
            deviceUnknown,
            sessionRunning);

        public bool SameAs(Inputs? other)
        {
            if (other is null || !ReferenceEquals(_view, other._view) || !ReferenceEquals(_subagents, other._subagents)
                || !ReferenceEquals(_interactions, other._interactions) || _unknown != other._unknown || _sessionRunning != other._sessionRunning
                || !Equals(_fallback, other._fallback)
                || _runs.Length != other._runs.Length)
            {
                return false;
            }

            for (var i = 0; i < _runs.Length; i++)
            {
                if (!ReferenceEquals(_runs[i], other._runs[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>属于这个会话的挂起交互:顶层 Run 的、以及它派出的子代理 Run 的。按登记时间排。</summary>
    public static IReadOnlyList<InteractionDto> PendingFor(ConversationState state, SessionRef session)
    {
        var view = state.Sessions.GetValueOrDefault(session);
        var runs = state.Runs.Values.Where(r => r.Ref.DeviceId == session.DeviceId && r.SessionId == session.SessionId).ToList();
        return PendingFor(state, session, runs, state.SubagentsOf(session), view);
    }

    private static List<InteractionDto> PendingFor(ConversationState state, SessionRef session, List<RunView> runs, IReadOnlyList<SubagentEntry> subagents, SessionView? view)
    {
        var runIds = runs.Select(r => r.Ref.RunId).Concat(subagents.Select(s => s.Record.ChildRunId).OfType<string>()).ToHashSet(StringComparer.Ordinal);
        if (view?.ActiveRunId is { } active)
        {
            runIds.Add(active);
        }

        return [.. state.InteractionsOf(session.DeviceId).Pending.Where(i => runIds.Contains(i.RunId)).OrderBy(i => i.CreatedAt).ThenBy(i => i.Id, StringComparer.Ordinal)];
    }

    // ───────────────────────── 子代理 ─────────────────────────

    private static SubagentRow SubagentRowOf(SessionRef session, string callId, Dictionary<string, SubagentEntry> byCall, Dictionary<string, ParsedPart> results, DateTimeOffset now, bool deviceUnknown)
    {
        var key = new SubagentRef(session.DeviceId, session.SessionId, callId);
        if (byCall.TryGetValue(callId, out var entry))
        {
            return new SubagentRow($"call:{callId}", key, entry.ViewAt(now), entry.Telemetry.Summary, entry.Telemetry.Color, entry.Unknown || deviceUnknown && !entry.IsTerminal);
        }

        // 表里还没有这张卡片(Task 的参数在流 / 旧转录):按 Core 的兜底状态画
        var status = SubagentView.WhenMissing(argumentsStreaming: !results.ContainsKey(callId));
        var record = new SubagentRecord(session.SessionId, callId, "", "", status, now, status == SubagentStatus.Done ? now : null, 0, null);
        return new SubagentRow($"call:{callId}", key, SubagentView.Of(record, now), null, null, false);
    }

    // ───────────────────────── 内联可视化 ─────────────────────────

    /// <summary>可视化卡片的工具名:这两个工具产出卡片(<c>visualize_update_widget</c> 只推数据,<c>visualize_read_me</c> 读规范,走普通工具卡)。</summary>
    public static bool IsWidgetTool(string name) => name is "visualize_show_widget" or "visualize_edit_widget";

    private sealed record WidgetContext(HashSet<string> Awaiting, Dictionary<string, string> Data, bool DeviceUnknown);

    /// <summary>
    /// 各标题最新的一份 <c>visualize_update_widget</c> 数据(小组件按标题认领,后来者覆盖,同 2.x)。落库的结果和还没落库的实时工具都算;先落库的、再实时的(实时的更新)。
    /// </summary>
    private static Dictionary<string, string> WidgetData(IEnumerable<ParsedPart> persisted, IEnumerable<ToolCallView> live)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var json in persisted.Where(p => p.Type == "tool_result").Select(p => p.DetailsJson).Concat(live.Select(t => t.DetailsJson)))
        {
            if (WidgetCardReader.ReadData(json) is { } item)
            {
                data[item.Title] = item.Data;
            }
        }

        return data;
    }

    private static TranscriptRow ToolOrWidgetRow(ParsedPart call, ParsedPart? result, ToolCallView? live, EnvRefDto? fallbackEnv, Dictionary<string, EnvRefDto> approvals, bool anyRunActive, bool deviceUnknown, WidgetContext widgets)
    {
        var name = call.Name ?? live?.Name ?? "";
        if (string.Equals(name, "RunWorkflow", StringComparison.OrdinalIgnoreCase))
        {
            return WorkflowRowOf(call, result, live, fallbackEnv, anyRunActive);
        }

        return IsWidgetTool(name)
            ? WidgetRowOf(call, result, live, fallbackEnv, anyRunActive, widgets)
            : ToolRowOf(call, result, live, fallbackEnv, approvals, anyRunActive, deviceUnknown);
    }

    private static WorkflowRow WorkflowRowOf(ParsedPart call, ParsedPart? result, ToolCallView? live, EnvRefDto? fallbackEnv, bool anyRunActive)
    {
        var callId = call.CallId ?? Guid.NewGuid().ToString("N");
        var input = call.InputJson ?? live?.InputJson ?? "{}";
        var (runId, detailTitle) = ReadWorkflowDetails(result?.DetailsJson) is { RunId: not null } resDetails
            ? resDetails
            : ReadWorkflowDetails(live?.DetailsJson);

        var (fallbackDef, inputTitle) = ParseWorkflowFallback(input, callId);
        var title = !string.IsNullOrWhiteSpace(detailTitle)
            ? detailTitle
            : !string.IsNullOrWhiteSpace(inputTitle) ? inputTitle : "Workflow";

        ToolRowStatus status;
        if (result is not null)
        {
            status = result.IsError ? ToolRowStatus.Failed : ToolRowStatus.Done;
        }
        else if (live is not null && live.Status != ToolCallStatus.Running)
        {
            status = live.Status == ToolCallStatus.Failed ? ToolRowStatus.Failed : ToolRowStatus.Done;
        }
        else
        {
            status = anyRunActive || live is { Status: ToolCallStatus.Running } ? ToolRowStatus.Running : ToolRowStatus.Interrupted;
        }

        return new WorkflowRow($"call:{callId}", callId, runId, title, fallbackDef, status, ResolveEnv(live?.Environment, fallbackEnv));
    }

    private static (string? RunId, string? Title) ReadWorkflowDetails(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var runId = doc.RootElement.TryGetProperty("runId", out var rEl) && rEl.ValueKind == JsonValueKind.String ? rEl.GetString() : null;
            var title = doc.RootElement.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String ? tEl.GetString() : null;
            return (runId, title);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static (WorkflowDefinition? Definition, string? Title) ParseWorkflowFallback(string inputJson, string callId)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var title = root.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String ? tEl.GetString() : null;
            if (!root.TryGetProperty("steps", out var stepsEl) || stepsEl.ValueKind != JsonValueKind.Array || stepsEl.GetArrayLength() == 0)
            {
                return (null, title);
            }

            var nodes = new List<WorkflowNodeDef>();
            var edges = new List<WorkflowEdgeDef>();
            var edgeIndex = 1;
            foreach (var step in stepsEl.EnumerateArray())
            {
                if (step.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = step.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var stepTitle = step.TryGetProperty("title", out var stEl) && stEl.ValueKind == JsonValueKind.String ? stEl.GetString()
                    : step.TryGetProperty("label", out var lbEl) && lbEl.ValueKind == JsonValueKind.String ? lbEl.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(stepTitle))
                {
                    stepTitle = id;
                }

                var kindStr = step.TryGetProperty("kind", out var kEl) && kEl.ValueKind == JsonValueKind.String ? kEl.GetString() : "agent";
                var kind = Enum.TryParse<WorkflowNodeKind>(kindStr, ignoreCase: true, out var parsed) ? parsed : WorkflowNodeKind.Agent;
                var desc = step.TryGetProperty("description", out var dEl) && dEl.ValueKind == JsonValueKind.String ? dEl.GetString() : null;
                nodes.Add(new WorkflowNodeDef(id, kind, stepTitle, desc));

                if (step.TryGetProperty("depends_on", out var depsEl) && depsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var dep in depsEl.EnumerateArray())
                    {
                        if (dep.ValueKind == JsonValueKind.String && dep.GetString() is { Length: > 0 } src)
                        {
                            edges.Add(new WorkflowEdgeDef($"e{edgeIndex++}", src, id));
                        }
                    }
                }
            }

            if (nodes.Count == 0)
            {
                return (null, title);
            }

            return (new WorkflowDefinition($"wf-call-{callId}", title ?? "Workflow", nodes, edges), title);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static WidgetRow WidgetRowOf(ParsedPart call, ParsedPart? result, ToolCallView? live, EnvRefDto? fallbackEnv, bool anyRunActive, WidgetContext widgets)
    {
        var callId = call.CallId!;
        var name = call.Name ?? live?.Name ?? "";
        var input = call.InputJson ?? live?.InputJson ?? "{}";

        // 卡片:落库的结果(终态)压过实时的(规则 6);实时的可能是交互模式的进度卡
        var card = WidgetCardReader.Read(result?.DetailsJson) ?? WidgetCardReader.Read(live?.DetailsJson);

        WidgetCall state;
        if (result is not null)
        {
            state = result.IsError ? WidgetCall.Failed : WidgetCall.Done;
        }
        else if (live is not null && live.Status != ToolCallStatus.Running)
        {
            state = live.Status == ToolCallStatus.Failed ? WidgetCall.Failed : WidgetCall.Done;
        }
        else
        {
            state = anyRunActive || live is { Status: ToolCallStatus.Running } ? WidgetCall.Running : WidgetCall.Interrupted;
        }

        var facts = new WidgetFacts(state, card is not null, card?.Interactive == true, card?.Submitted is not null, widgets.Awaiting.Contains(callId), widgets.DeviceUnknown);
        var (title, loading) = InputHints(input);
        title = card is { Title.Length: > 0 } ? card.Title : title;
        var data = widgets.Data.GetValueOrDefault(title);
        return new WidgetRow($"call:{callId}", callId, name, title, card, WidgetView.Phase(facts), facts, loading, data, ResolveEnv(live?.Environment ?? card?.Environment, fallbackEnv));
    }

    /// <summary>
    /// 补齐执行环境标识。<c>tool_start</c> 只带 id(设备 + 环境),名字和类型是空的:是项目所在的那个环境就用项目环境(名字、类型都全),
    /// 别的环境用 id 当名字、类型保持未知——照样画出来,离开项目环境的执行必须看得见(AGENTS.md §5)。
    /// </summary>
    public static EnvRefDto? ResolveEnv(EnvRefDto? env, EnvRefDto? fallback)
    {
        if (env is null || env is { Name.Length: > 0, Type.Length: > 0 })
        {
            return env ?? fallback;
        }

        if (fallback is not null && fallback.DeviceId == env.DeviceId && (env.Environment.Length == 0 || env.Environment == fallback.Environment))
        {
            return fallback;
        }

        return env with { Name = env.Name.Length > 0 ? env.Name : env.Environment };
    }

    /// <summary>工具入参里的标题和加载提示(<c>loading_messages</c>,1–4 条,模型写的;每条截到 80 字符,用换行拼成一个字符串,让行记录保持值相等)。</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, Tuple<string, string>> Hints = [];

    private static (string Title, string Loading) InputHints(string inputJson)
    {
        var hints = Hints.GetValue(inputJson, static json => ParseHints(json).ToTuple());
        return (hints.Item1, hints.Item2);
    }

    private static (string Title, string Loading) ParseHints(string inputJson)
    {
        using var doc = TryParse(inputJson);
        var root = doc?.RootElement ?? default;
        var title = SubagentReceipts.Str(root, "title") ?? "";
        var messages = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("loading_messages", out var m) && m.ValueKind == JsonValueKind.Array
            ? m.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => Clip(e.GetString()!.Replace('\n', ' ')).Trim()).Where(t => t.Length > 0).Take(4)
            : [];
        return (title, string.Join('\n', messages));
    }

    // ───────────────────────── 工具 ─────────────────────────

    private static ToolRow ToolRowOf(ParsedPart call, ParsedPart? result, ToolCallView? live, EnvRefDto? fallbackEnv, Dictionary<string, EnvRefDto> approvals, bool anyRunActive, bool deviceUnknown)
    {
        var name = call.Name ?? live?.Name ?? "";
        var input = call.InputJson ?? live?.InputJson ?? "{}";
        var output = result?.Content ?? live?.Output;
        var (kind, target, preview, more, added, removed) = Digest(name, input, output);

        var callId = call.CallId!;
        ToolRowStatus status;
        if (result is not null)
        {
            status = result.IsError ? ToolRowStatus.Failed : ToolRowStatus.Done;
        }
        else if (live is not null && live.Status != ToolCallStatus.Running)
        {
            status = live.Status == ToolCallStatus.Failed ? ToolRowStatus.Failed : ToolRowStatus.Done;
        }
        else
        {
            status = anyRunActive || live is { Status: ToolCallStatus.Running } ? ToolRowStatus.Running : ToolRowStatus.Interrupted;
        }

        EnvRefDto? env = live?.Environment;
        if (status == ToolRowStatus.Running && approvals.TryGetValue(callId, out var approvalEnv))
        {
            status = ToolRowStatus.WaitingApproval;
            env ??= approvalEnv;
        }

        var mediaKind = GeneratedMedia.KindOf(name);
        if (mediaKind is not null && deviceUnknown && status == ToolRowStatus.Running)
        {
            status = ToolRowStatus.Unknown;
        }

        var media = status == ToolRowStatus.Done && mediaKind is { } expected
            ? GeneratedMedia.Read(result?.DetailsJson ?? live?.DetailsJson, expected)
            : null;
        return new ToolRow($"call:{callId}", callId, name, kind, target, status, ResolveEnv(env, fallbackEnv), preview, more, added, removed,
            Truncated: false, live?.QueuedAhead, mediaKind, media);
    }

    /// <summary>一次工具调用解析过的结果(按入参 JSON 字符串实例缓存;摘要另按输出字符串缓存,命令在跑时输出会变)。</summary>
    private sealed class ToolDigest
    {
        public string? Name;
        public ToolKind Kind;
        public string Target = "";
        public bool HasSummary;
        public string? Output;
        public (string Preview, int More, int Added, int Removed) Summary;
    }

    // 入参 / 输出都是不可变字符串:同一个实例跨多次派生(流式时每个增量都重新派生一遍)只解析一次——否则每个增量都要把整个会话里每一次工具调用的
    // 入参 JSON 重新解析、命令输出重新切行。键是字符串实例,字符串不再被引用时缓存随之回收。
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, ToolDigest> Digests = [];

    private static (ToolKind Kind, string Target, string Preview, int More, int Added, int Removed) Digest(string name, string input, string? output)
    {
        var digest = Digests.GetValue(input, static _ => new ToolDigest());
        lock (digest)
        {
            if (!string.Equals(digest.Name, name, StringComparison.Ordinal))
            {
                digest.Name = name;
                (digest.Kind, digest.Target) = Classify(name, input);
                digest.HasSummary = false;
            }

            if (!digest.HasSummary || !(ReferenceEquals(digest.Output, output) || string.Equals(digest.Output, output, StringComparison.Ordinal)))
            {
                digest.Summary = Summarize(digest.Kind, input, output);
                digest.Output = output;
                digest.HasSummary = true;
            }

            var (preview, more, added, removed) = digest.Summary;
            return (digest.Kind, digest.Target, preview, more, added, removed);
        }
    }

    /// <summary>工具参数的一行摘要(命令 / 路径 / 模式)。审批卡片和工具卡片共用,不各写一份。</summary>
    public static string TargetOf(string toolName, string inputJson) => Classify(toolName, inputJson).Target;

    private static (ToolKind Kind, string Target) Classify(string name, string inputJson)
    {
        var kind = name switch
        {
            "Bash" or "BashOutput" or "KillShell" => ToolKind.Command,
            "Edit" or "MultiEdit" or "NotebookEdit" => ToolKind.Edit,
            "Write" => ToolKind.Write,
            "Read" or "Ls" or "LS" => ToolKind.Read,
            "Grep" or "Glob" => ToolKind.Search,
            _ => ToolKind.Other,
        };

        using var doc = TryParse(inputJson);
        var root = doc?.RootElement ?? default;
        string? Pick(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (SubagentReceipts.Str(root, key) is { Length: > 0 } value)
                {
                    return value;
                }
            }

            return null;
        }

        var target = kind switch
        {
            ToolKind.Command => FirstLine(Pick("command") ?? Pick("description") ?? ""),
            ToolKind.Edit or ToolKind.Write or ToolKind.Read => Pick("file_path", "path", "notebook_path") ?? "",
            ToolKind.Search => Pick("pattern") is { } pattern ? pattern + (Pick("path") is { } where ? $"  {where}" : "") : "",
            _ => FirstLine(Pick("description", "query", "url", "path", "file_path", "name") ?? ""),
        };
        return (kind, Clip(target));
    }

    /// <summary>审批卡片上「要做什么」的预览:写 / 改是 diff 行(前缀 <c>+</c> / <c>-</c>),命令是完整的命令文本(多行照原样)。规则和工具行的摘要是同一份,只是行数上限更大。</summary>
    public const int ApprovalPreviewLineLimit = 14;

    public static (string Preview, int More, int Added, int Removed) ApprovalPreview(string toolName, string inputJson)
    {
        var (kind, _) = Classify(toolName, inputJson);
        if (kind == ToolKind.Command)
        {
            using var doc = TryParse(inputJson);
            var command = doc is null ? null : SubagentReceipts.Str(doc.RootElement, "command");
            var lines = Split(command).ToList();
            return (string.Join('\n', lines.Take(ApprovalPreviewLineLimit).Select(l => " " + Clip(l))), Math.Max(0, lines.Count - ApprovalPreviewLineLimit), 0, 0);
        }

        return Summarize(kind, inputJson, null, ApprovalPreviewLineLimit);
    }

    private static (string Preview, int More, int Added, int Removed) Summarize(ToolKind kind, string inputJson, string? output, int limit = PreviewLineLimit)
    {
        switch (kind)
        {
            case ToolKind.Edit or ToolKind.Write:
            {
                using var doc = TryParse(inputJson);
                var root = doc?.RootElement ?? default;
                var lines = new List<string>();
                var added = 0;
                var removed = 0;
                void Pair(string? old, string? @new)
                {
                    foreach (var line in Split(old))
                    {
                        removed++;
                        lines.Add("-" + Clip(line));
                    }

                    foreach (var line in Split(@new))
                    {
                        added++;
                        lines.Add("+" + Clip(line));
                    }
                }

                if (kind == ToolKind.Write)
                {
                    Pair(null, SubagentReceipts.Str(root, "content"));
                }
                else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
                {
                    foreach (var edit in edits.EnumerateArray())
                    {
                        Pair(SubagentReceipts.Str(edit, "old_string"), SubagentReceipts.Str(edit, "new_string"));
                    }
                }
                else
                {
                    Pair(SubagentReceipts.Str(root, "old_string"), SubagentReceipts.Str(root, "new_string"));
                }

                return (string.Join('\n', lines.Take(limit)), Math.Max(0, lines.Count - limit), added, removed);
            }

            case ToolKind.Command when !string.IsNullOrWhiteSpace(output):
            {
                var lines = Split(output).Where(l => l.Length > 0).ToList();
                return (string.Join('\n', lines.Take(limit).Select(l => " " + Clip(l))), Math.Max(0, lines.Count - limit), 0, 0);
            }

            default:
                return ("", 0, 0, 0);
        }
    }

    private static IEnumerable<string> Split(string? text) =>
        string.IsNullOrEmpty(text) ? [] : text.TrimEnd('\n').Split('\n').Select(l => l.TrimEnd('\r'));

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    private static string Clip(string text) => text.Length <= MaxLineLength ? text : text[..MaxLineLength] + "…";

    private static JsonDocument? TryParse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>消息内容块里转录用得到的字段(宽容地解析,字段缺了就是 null / 默认值)。</summary>
internal sealed record ParsedPart(string Type)
{
    public string? Text { get; init; }
    public string? CallId { get; init; }
    public string? Name { get; init; }
    public string? InputJson { get; init; }
    public string? Content { get; init; }
    public bool IsError { get; init; }
    public long? DurationMs { get; init; }
    public string? Path { get; init; }
    /// <summary>用户图片块:MIME 与引用(<c>data:</c> URL 或 <c>ncw://</c> 附件引用)。</summary>
    public string? Mime { get; init; }
    public string? DataRef { get; init; }
    public string? ChildRunId { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorKey { get; init; }
    public bool ErrorRetryable { get; init; }
    public int? ErrorStatus { get; init; }
    public bool Met { get; init; }
    public string? Reason { get; init; }
    public bool Failed { get; init; }
    public bool Cleared { get; init; }
    public bool Set { get; init; }
    public string? Trigger { get; init; }
    public int PreTokens { get; init; }
    public int PostTokens { get; init; }

    /// <summary>工具结果里可展示的结构化回执原文。</summary>
    public string? DetailsJson { get; init; }
}

internal static class MessageParts
{
    // 消息内容 JSON 是不可变的字符串,同一个实例跨多次派生(流式时每个增量都重新派生一遍)只解析一次;只读,调用方不改。
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, List<ParsedPart>> Cache = [];

    public static List<ParsedPart> Parse(string json) => Cache.GetValue(json, ParseCore);

    private static List<ParsedPart> ParseCore(string json)
    {
        var parts = new List<ParsedPart>();
        if (!SubagentReceipts.TryParseParts(json, out var doc))
        {
            return parts;
        }

        using (doc)
        {
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || SubagentReceipts.Str(e, "type") is not { } type)
                {
                    continue;
                }

                var part = new ParsedPart(type) { Text = SubagentReceipts.Str(e, "text"), CallId = SubagentReceipts.Str(e, "callId"), Name = SubagentReceipts.Str(e, "name") };
                parts.Add(type switch
                {
                    "thinking" => part with { DurationMs = Long(e, "durationMs") },
                    "tool_call" => part with { InputJson = e.TryGetProperty("input", out var input) ? input.GetRawText() : "{}" },
                    "tool_result" => part with
                    {
                        Content = e.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object ? SubagentReceipts.Str(output, "content") : null,
                        IsError = Bool(e, "isError"),
                        DetailsJson = e.TryGetProperty("output", out var out2) && out2.ValueKind == JsonValueKind.Object ? DisplayDetails(out2) : null,
                    },
                    "file_ref" => part with { Path = SubagentReceipts.Str(e, "path") },
                    "image" => part with { Mime = SubagentReceipts.Str(e, "mime"), DataRef = SubagentReceipts.Str(e, "dataRef") },
                    "subagent" => part with { ChildRunId = SubagentReceipts.Str(e, "childRunId"), Text = SubagentReceipts.Str(e, "summary") },
                    "error" when e.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object => part with
                    {
                        ErrorCode = SubagentReceipts.Str(error, "code"),
                        ErrorMessage = SubagentReceipts.Str(error, "message"),
                        ErrorKey = SubagentReceipts.Str(error, "messageKey"),
                        ErrorRetryable = Bool(error, "retryable"),
                        ErrorStatus = error.TryGetProperty("status", out var status) && status.TryGetInt32(out var code) ? code : null,
                    },
                    "goal_status" => part with
                    {
                        Text = SubagentReceipts.Str(e, "condition"),
                        Met = Bool(e, "met"),
                        Reason = SubagentReceipts.Str(e, "reason"),
                        Failed = Bool(e, "failed"),
                        Cleared = Bool(e, "cleared"),
                        Set = Bool(e, "set"),
                    },
                    "compact_boundary" => part with
                    {
                        Text = SubagentReceipts.Str(e, "summary"),
                        Trigger = SubagentReceipts.Str(e, "trigger"),
                        PreTokens = (int)(Long(e, "preTokens") ?? 0),
                        PostTokens = (int)(Long(e, "postTokens") ?? 0),
                    },
                    _ => part,
                });
            }
        }

        return parts;
    }

    /// <summary>输出里的 <c>details</c>(2.x 叫 <c>card</c>),保留 widget 卡 / 数据卡、工作流运行卡和媒体生成回执的原文。</summary>
    private static string? DisplayDetails(JsonElement output)
    {
        var details = output.TryGetProperty("details", out var d) ? d : output.TryGetProperty("card", out var c) ? c : default;
        if (details.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (details.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() is "widget" or "widgetData" or "media-generation")
        {
            return details.GetRawText();
        }

        if (details.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "workflow_run")
        {
            return details.GetRawText();
        }

        return null;
    }

    private static long? Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>用户消息里的一张图片(<see cref="DataRef"/> 是 <c>data:</c> URL 或 <c>ncw://</c> 附件引用)。</summary>
public sealed record MessageImage(string Mime, string DataRef);

/// <summary>从一条消息里取出用户发的图片引用(纯函数,解析结果有缓存)。内部消息(压缩边界等)和助手消息不算。</summary>
public static class MessageImages
{
    public static IReadOnlyList<MessageImage> Of(MessageDto message) =>
        message.Role != "user" || message.Internal
            ? []
            : [.. MessageParts.Parse(message.PartsJson)
                .Where(p => p is { Type: "image", Mime.Length: > 0, DataRef.Length: > 0 })
                .Select(p => new MessageImage(p.Mime!, p.DataRef!))];
}

/// <summary>
/// 把一条用户消息里的附件还原成「发送时的输入」(图片 → 附件引用,<c>file_ref</c> → 项目文件引用),按原顺序。
/// 编辑后重新运行 / 重新生成要把它们带上:截断历史再用同样的输入重发,否则图片和 @ 文件会被悄悄丢掉。
/// </summary>
public static class MessageInputs
{
    public static IReadOnlyList<Ncw.Client.Actions.InputFile> FilesOf(MessageDto message) =>
        message.Role != "user" || message.Internal
            ? []
            : [.. MessageParts.Parse(message.PartsJson).SelectMany<ParsedPart, Ncw.Client.Actions.InputFile>(p => p switch
            {
                { Type: "image", Mime.Length: > 0, DataRef.Length: > 0 } => [Ncw.Client.Actions.InputFile.Attachment(p.DataRef!, p.Name is { Length: > 0 } ? p.Name : "image", p.Mime!)],
                { Type: "file_ref", Path.Length: > 0 } => [new Ncw.Client.Actions.InputFile(p.Path!, p.Name is { Length: > 0 } ? p.Name : p.Path!)],
                _ => [],
            })];
}
