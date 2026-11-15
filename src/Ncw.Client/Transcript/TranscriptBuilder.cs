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
    public static ImmutableArray<TranscriptRow> Build(ConversationState state, SessionRef session, EnvRefDto? fallbackEnv, DateTimeOffset now, bool deviceUnknown = false)
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
        var anyRunActive = view.ActiveRunId is not null || runs.Any(r => r.Phase == RunPhase.Running);
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
                        rows.Add(new ThinkingRow($"{message.Id}:k{thoughts++}", part.Text ?? "", part.DurationMs, false));
                        break;
                    case "tool_call" when part.CallId is { } callId:
                        emittedCalls.Add(callId);
                        rows.Add(part.Name == SubagentReceipts.TaskToolName
                            ? SubagentRowOf(session, callId, subagentByCall, results, now, deviceUnknown)
                            : ToolOrWidgetRow(part, results.GetValueOrDefault(callId), liveTools.GetValueOrDefault(callId), fallbackEnv, approvals, anyRunActive, widgets));
                        break;
                    case "error":
                        rows.Add(new ErrorRow($"{message.Id}:e{rows.Count}", part.ErrorCode ?? "unknown", part.ErrorMessage ?? "", part.ErrorKey, part.ErrorRetryable, false));
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

        // 还没落库的:流式文字 / 思考、已开始的工具
        foreach (var run in runs.OrderBy(r => r.Ref.RunId, StringComparer.Ordinal))
        {
            foreach (var (messageId, text) in run.Thinking.Where(kv => !committed.Contains(kv.Key)))
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
                    : ToolOrWidgetRow(new ParsedPart("tool_call") { CallId = tool.CallId, Name = tool.Name, InputJson = tool.InputJson }, null, tool, fallbackEnv, approvals, anyRunActive, widgets));
            }
        }

        // 运行状态与失败的 Run
        var last = runs.OrderByDescending(r => r.Ref.RunId, StringComparer.Ordinal).FirstOrDefault();
        foreach (var run in runs.Where(r => r.Phase == RunPhase.Error && r.Error is not null))
        {
            var error = run.Error!;
            rows.Add(new ErrorRow($"run:{run.Ref.RunId}", error.Code, error.Message, error.MessageKey, error.Retryable, run.Ref == last?.Ref));
        }

        if (deviceUnknown && (view.ActiveRunId is not null || runs.Any(r => r.Phase == RunPhase.Unknown)))
        {
            rows.Add(new StatusRow("status", RunStatusKind.Unknown, 0, 0));
        }
        else if (runs.FirstOrDefault(r => r.Retry is not null) is { Retry: { } retry })
        {
            rows.Add(new StatusRow("status", RunStatusKind.Retrying, retry.Attempt, retry.DelayMs));
        }
        else if (runs.Any(r => r.CompactPhase == "compacting"))
        {
            rows.Add(new StatusRow("status", RunStatusKind.Compacting, 0, 0));
        }

        foreach (var interaction in pending)
        {
            rows.Add(new InteractionRow($"i:{interaction.Id}", interaction));
        }

        return [.. rows];
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

    private static TranscriptRow ToolOrWidgetRow(ParsedPart call, ParsedPart? result, ToolCallView? live, EnvRefDto? fallbackEnv, Dictionary<string, EnvRefDto> approvals, bool anyRunActive, WidgetContext widgets) =>
        IsWidgetTool(call.Name ?? live?.Name ?? "")
            ? WidgetRowOf(call, result, live, fallbackEnv, anyRunActive, widgets)
            : ToolRowOf(call, result, live, fallbackEnv, approvals, anyRunActive);

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
        return new WidgetRow($"call:{callId}", callId, name, title, card, WidgetView.Phase(facts), facts, loading, data, live?.Environment ?? card?.Environment ?? fallbackEnv);
    }

    /// <summary>工具入参里的标题和加载提示(<c>loading_messages</c>,1–4 条,模型写的;每条截到 80 字符,用换行拼成一个字符串,让行记录保持值相等)。</summary>
    private static (string Title, string Loading) InputHints(string inputJson)
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

    private static ToolRow ToolRowOf(ParsedPart call, ParsedPart? result, ToolCallView? live, EnvRefDto? fallbackEnv, Dictionary<string, EnvRefDto> approvals, bool anyRunActive)
    {
        var name = call.Name ?? live?.Name ?? "";
        var input = call.InputJson ?? live?.InputJson ?? "{}";
        var output = result?.Content ?? live?.Output;
        var (kind, target) = Classify(name, input);
        var (preview, more, added, removed) = Summarize(kind, input, output);

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

        return new ToolRow($"call:{callId}", callId, name, kind, target, status, env ?? fallbackEnv, preview, more, added, removed,
            Truncated: false, live?.QueuedAhead);
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

    private static (string Preview, int More, int Added, int Removed) Summarize(ToolKind kind, string inputJson, string? output)
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

                return (string.Join('\n', lines.Take(PreviewLineLimit)), Math.Max(0, lines.Count - PreviewLineLimit), added, removed);
            }

            case ToolKind.Command when !string.IsNullOrWhiteSpace(output):
            {
                var lines = Split(output).Where(l => l.Length > 0).ToList();
                return (string.Join('\n', lines.Take(PreviewLineLimit).Select(l => " " + Clip(l))), Math.Max(0, lines.Count - PreviewLineLimit), 0, 0);
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
    public string? ChildRunId { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorKey { get; init; }
    public bool ErrorRetryable { get; init; }
    public bool Met { get; init; }
    public string? Reason { get; init; }
    public bool Failed { get; init; }
    public bool Cleared { get; init; }
    public bool Set { get; init; }
    public string? Trigger { get; init; }
    public int PreTokens { get; init; }
    public int PostTokens { get; init; }

    /// <summary>工具结果里 widget 卡片 / 数据卡的 Details 原文(别的工具的 Details 不进转录镜像)。</summary>
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
                        DetailsJson = e.TryGetProperty("output", out var out2) && out2.ValueKind == JsonValueKind.Object ? WidgetDetails(out2) : null,
                    },
                    "file_ref" => part with { Path = SubagentReceipts.Str(e, "path") },
                    "subagent" => part with { ChildRunId = SubagentReceipts.Str(e, "childRunId"), Text = SubagentReceipts.Str(e, "summary") },
                    "error" when e.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object => part with
                    {
                        ErrorCode = SubagentReceipts.Str(error, "code"),
                        ErrorMessage = SubagentReceipts.Str(error, "message"),
                        ErrorKey = SubagentReceipts.Str(error, "messageKey"),
                        ErrorRetryable = Bool(error, "retryable"),
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

    /// <summary>输出里的 <c>details</c>(2.x 叫 <c>card</c>),只在它是 widget 卡 / 数据卡时取原文。</summary>
    private static string? WidgetDetails(JsonElement output)
    {
        var details = output.TryGetProperty("details", out var d) ? d : output.TryGetProperty("card", out var c) ? c : default;
        return details.ValueKind == JsonValueKind.Object && details.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() is "widget" or "widgetData"
            ? details.GetRawText()
            : null;
    }

    private static long? Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
