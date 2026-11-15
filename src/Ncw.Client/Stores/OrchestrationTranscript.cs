using System.Text.Json;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Stores;

/// <summary>
/// 父会话转录里一张子代理的持久回执(<c>tool_result.subagent</c>)加上它所属的那次 <c>Task</c> 调用的入参。
/// 块的形状只有一份定义(Agent 会话层的 <c>MessageCodec</c>):这里只读,宽容(坏 JSON、缺字段、未知块都跳过,不抛)。
/// </summary>
public sealed record ReceiptCard(
    string CallId,
    string ChildRunId,
    string AgentType,
    string Description,
    SubagentStatus Status,
    bool Background,
    SubagentReportStatus? ReportStatus,
    string? Summary,
    string? Color,
    ErrorDto? Error,
    DateTimeOffset StartedAt,
    DateTimeOffset? ResultAt);

/// <summary>汇报行:内部消息里的 <c>subagent</c> 块(「某个后台子代理的结果回传」)。全文在同一条消息的文字块里,这里只有标记和 240 字预览。</summary>
public sealed record ReportRow(string MessageId, string CallId, string ChildRunId, string? Summary);

public static class SubagentReceipts
{
    public const string TaskToolName = "Task";

    /// <summary>
    /// 从会话消息里重建子代理卡片的持久部分(2.x 的 <c>subagentsFromMessages</c>)。回执里<b>没有子会话 id</b>(那只随 <c>subagent_start</c> 来):
    /// 只靠回执重建的卡片 <see cref="SubagentViewState.CanOpen"/> 为假——旧转录的卡片点开是空的,仅此而已。
    /// </summary>
    public static IReadOnlyList<ReceiptCard> FromMessages(IEnumerable<MessageDto> messages)
    {
        var calls = new Dictionary<string, (string Type, string Description, DateTimeOffset At)>(StringComparer.Ordinal);
        var results = new List<(string CallId, JsonElement Receipt, DateTimeOffset At, bool IsError)>();
        var docs = new List<JsonDocument>();
        try
        {
            foreach (var message in messages)
            {
                // 先按子串筛:绝大多数消息和子代理无关,不值得解析
                if (!message.PartsJson.Contains("\"subagent\"", StringComparison.Ordinal) && !message.PartsJson.Contains("\"Task\"", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!TryParseParts(message.PartsJson, out var doc))
                {
                    continue;
                }

                docs.Add(doc);
                var at = DateTimeOffset.FromUnixTimeMilliseconds(message.CreatedAt);
                foreach (var part in doc.RootElement.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object || Str(part, "type") is not { } type)
                    {
                        continue;
                    }

                    if (type == "tool_call" && Str(part, "name") == TaskToolName && Str(part, "callId") is { } callId)
                    {
                        var input = part.TryGetProperty("input", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
                        calls[callId] = (input.ValueKind == JsonValueKind.Object ? Str(input, "subagent_type") ?? "" : "", input.ValueKind == JsonValueKind.Object ? Str(input, "description") ?? "" : "", at);
                    }
                    else if (type == "tool_result" && Str(part, "callId") is { } resultId
                             && part.TryGetProperty("subagent", out var receipt) && receipt.ValueKind == JsonValueKind.Object)
                    {
                        results.Add((resultId, receipt, at, part.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True));
                    }
                }
            }

            var cards = new List<ReceiptCard>(results.Count);
            foreach (var (callId, receipt, at, _) in results)
            {
                if (Str(receipt, "childRunId") is not { } childRunId)
                {
                    continue;
                }

                var call = calls.GetValueOrDefault(callId);
                var status = SubagentView.ParseStatus(Str(receipt, "status")) ?? SubagentStatus.Running;
                var terminal = SubagentView.IsTerminal(status);
                var background = receipt.TryGetProperty("background", out var b) && b.ValueKind == JsonValueKind.True;
                cards.Add(new ReceiptCard(
                    callId, childRunId, call.Type ?? "", call.Description ?? "", status, background,
                    SubagentView.ParseReportStatus(Str(receipt, "reportStatus")), Str(receipt, "summary"), Str(receipt, "color"), ReadError(receipt),
                    call.At == default ? at : call.At,
                    // 前台的结果落库那一刻就是它结束的时间;后台的回执是「已转后台」那一刻写的,结束时间只有事件知道
                    terminal && !background ? at : null));
            }

            return cards;
        }
        finally
        {
            foreach (var doc in docs)
            {
                doc.Dispose();
            }
        }
    }

    /// <summary>一条消息里的文字(各 <c>text</c> 块依次拼起来;没有文字为空串)。子代理的全文汇报就是子会话最后一条助手消息的文字。</summary>
    public static string TextOf(MessageDto message)
    {
        if (!TryParseParts(message.PartsJson, out var doc))
        {
            return "";
        }

        using (doc)
        {
            return string.Concat(doc.RootElement.EnumerateArray().Where(p => Str(p, "type") == "text").Select(p => Str(p, "text") ?? ""));
        }
    }

    /// <summary>消息里的汇报行(<c>subagent</c> 块)。</summary>
    public static IReadOnlyList<ReportRow> ReportRowsOf(MessageDto message)
    {
        if (!message.PartsJson.Contains("\"subagent\"", StringComparison.Ordinal) || !TryParseParts(message.PartsJson, out var doc))
        {
            return [];
        }

        using (doc)
        {
            var rows = new List<ReportRow>();
            foreach (var part in doc.RootElement.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object && Str(part, "type") == "subagent" && Str(part, "callId") is { } callId && Str(part, "childRunId") is { } childRunId)
                {
                    rows.Add(new ReportRow(message.Id, callId, childRunId, Str(part, "summary")));
                }
            }

            return rows;
        }
    }

    /// <summary>回执 → 卡片的持久记录。<paramref name="sessionId"/> 是父会话。</summary>
    public static SubagentRecord ToRecord(string sessionId, ReceiptCard card) =>
        new(sessionId, card.CallId, card.AgentType, card.Description, card.Status, card.StartedAt, card.ResultAt, 0, null)
        {
            ChildRunId = card.ChildRunId,
            Background = card.Background,
            ReportStatus = card.ReportStatus,
            LastEventAt = card.ResultAt,
        };

    internal static bool TryParseParts(string json, out JsonDocument doc)
    {
        try
        {
            doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return true;
            }

            doc.Dispose();
        }
        catch (JsonException)
        {
        }

        doc = null!;
        return false;
    }

    internal static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static ErrorDto? ReadError(JsonElement receipt)
    {
        if (!receipt.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new ErrorDto(
            Str(error, "code") ?? "unknown",
            Str(error, "message") ?? "",
            error.TryGetProperty("retryable", out var r) && r.ValueKind == JsonValueKind.True,
            error.TryGetProperty("status", out var s) && s.TryGetInt32(out var status) ? status : null,
            error.TryGetProperty("retryAfterMs", out var a) && a.TryGetInt32(out var after) ? after : null,
            Str(error, "messageKey"));
    }

    /// <summary>一次 <c>Task</c> 调用的入参(<c>tool_start</c> 的 <c>InputJson</c>):类型和描述。认不出返回 null。</summary>
    public static (string Type, string Description)? ParseTaskInput(string inputJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? (Str(doc.RootElement, "subagent_type") ?? "", Str(doc.RootElement, "description") ?? "")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>这次 <c>Task</c> 调用是后台派发(入参 <c>run_in_background: true</c>)?认不出按前台。</summary>
    public static bool IsBackgroundCall(string inputJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("run_in_background", out var flag) && flag.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary><c>tool_progress</c> 的 <c>DetailsJson</c> 里「排队中」那一种(<see cref="ToolQueueDetails"/>)的排位;不是排队返回 null。</summary>
    public static int? QueuedAhead(string? detailsJson)
    {
        if (detailsJson is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            return Str(doc.RootElement, "kind") == ToolQueueDetails.Kind
                ? doc.RootElement.TryGetProperty("ahead", out var ahead) && ahead.TryGetInt32(out var n) ? n : 0
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// 把会话消息压成 Core 的 <see cref="TodoEvent"/> 序列(待办的派生判据只在 <see cref="Todos"/> 里有一份,Agent 和客户端各写这几行映射)。
/// 待办没有表:当前清单 = 转录里最近一次<b>成功</b>的 <c>TodoWrite</c> 调用的入参。
/// </summary>
public static class TodoTranscript
{
    /// <summary>
    /// 工具在转录里的名字。3.0 里外部名 = 内部 id(一方工具撞名时后来者被丢弃,没有哈希后缀),所以可以写字面量;
    /// Core 的派生函数仍然按「调用自己带的名字」分组,这里只决定问哪一个。
    /// </summary>
    public const string ToolName = "TodoWrite";

    public static IReadOnlyList<TodoEvent> EventsOf(IEnumerable<MessageDto> messages)
    {
        var all = messages as IReadOnlyList<MessageDto> ?? [.. messages];
        var events = new List<TodoEvent>();
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in all)
        {
            if (!message.PartsJson.Contains(ToolName, StringComparison.Ordinal) && !message.PartsJson.Contains("\"tool_result\"", StringComparison.Ordinal))
            {
                continue;
            }

            if (!SubagentReceipts.TryParseParts(message.PartsJson, out var doc))
            {
                continue;
            }

            using (doc)
            {
                foreach (var part in doc.RootElement.EnumerateArray())
                {
                    switch (SubagentReceipts.Str(part, "type"))
                    {
                        case "tool_call" when SubagentReceipts.Str(part, "name") == ToolName && SubagentReceipts.Str(part, "callId") is { } callId:
                            callIds.Add(callId);
                            events.Add(new TodoEvent.Call(callId, ToolName, part.TryGetProperty("input", out var input) ? input.Clone() : default));
                            break;
                        case "tool_result" when SubagentReceipts.Str(part, "callId") is { } resultId && callIds.Contains(resultId):
                            events.Add(new TodoEvent.Result(resultId, part.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True));
                            break;
                    }
                }
            }
        }

        return events;
    }

    /// <summary>当前的待办视图(输入框上方那条、消息里那张、手机详情页共用);没有成功写过清单为 null。</summary>
    public static TodoView? ViewOf(IEnumerable<MessageDto> messages) =>
        Todos.Latest(EventsOf(messages), ToolName) is { } items ? TodoView.Of(items) : null;
}
