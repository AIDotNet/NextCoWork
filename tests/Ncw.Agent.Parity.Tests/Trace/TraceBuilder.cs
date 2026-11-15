using System.Text.Json.Nodes;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Subagents;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Llm;
using Ncw.Agent.Storage.Sessions;

namespace Ncw.Agent.Parity.Tests.Trace;

/// <summary>
/// C# 的 <see cref="AgentEvent"/> 序列 → 规范化条目(形状与录制脚本里的 <c>projectEvents</c> 一一对应,见 docs/parity.md「回放对照的录制方法」)。
/// 投影只做「去掉不确定的东西 + 合并增量」:
/// <list type="bullet">
/// <item>流增量按「同一条消息内、同一种、同一个下标」合并成一个 <c>stream</c> 条目(两边都是,所以合并不是差异);</item>
/// <item><c>message_end</c> → <c>commit</c>(角色 / internal / id / parts);</item>
/// <item>3.0 独有的事件原样产出(<c>run_start</c>、<c>turn_*</c>、<c>message_start</c>、<c>discard</c>、<c>run_settled</c>),由比较器里带文档引用的规则放行,不在这里悄悄丢掉。</item>
/// </list>
/// </summary>
public static class TraceBuilder
{
    public static JsonArray Events(IEnumerable<AgentEvent> events, IdMap ids)
    {
        var output = new JsonArray();
        JsonArray? blocks = null;

        void Flush()
        {
            if (blocks is { Count: > 0 })
            {
                output.Add(new JsonObject { ["e"] = "stream", ["blocks"] = blocks });
            }

            blocks = null;
        }

        JsonObject Block(string kind, int index, string? callId = null, string? name = null)
        {
            blocks ??= [];
            var found = blocks.Select(b => b!.AsObject()).FirstOrDefault(b => b["k"]!.GetValue<string>() == kind && b["i"]!.GetValue<int>() == index
                && (callId is null || b["callId"]?.GetValue<string>() == callId));
            if (found is not null)
            {
                return found;
            }

            var created = new JsonObject { ["k"] = kind, ["i"] = index, ["text"] = string.Empty };
            if (callId is not null)
            {
                created["callId"] = callId;
            }

            if (name is not null)
            {
                created["name"] = name;
            }

            blocks.Add(created);
            return created;
        }

        void Append(JsonObject block, string text) => block["text"] = block["text"]!.GetValue<string>() + text;

        foreach (var ev in events)
        {
            switch (ev)
            {
                case MessageDeltaEvent delta:
                    switch (delta.Kind)
                    {
                        case MessageDeltaKind.Text:
                            Append(Block("text", delta.ContentIndex), delta.Delta);
                            break;
                        case MessageDeltaKind.Thinking:
                            Append(Block("thinking", delta.ContentIndex), delta.Delta);
                            break;
                        default:
                            Append(Block("tool", delta.ContentIndex, delta.CallId, delta.ToolName), delta.Delta);
                            break;
                    }

                    continue;
                case ToolProgressEvent { Progress.Details: { } details } progress when details.ValueKind == System.Text.Json.JsonValueKind.Object
                    && details.TryGetProperty("kind", out var kind) && kind.GetString() == "queued":
                    // 子代理排队的排位(「前面还有 N 个」):2.x 一侧是同一个 tool_progress 的文字,录制脚本把它投影成同样的 queued 条目。
                    Flush();
                    output.Add(new JsonObject { ["e"] = "queued", ["callId"] = progress.CallId, ["ahead"] = details.GetProperty("ahead").GetInt32() });
                    continue;
                case ToolProgressEvent or SubagentUpdateEvent or InteractionRequestEvent or InteractionResolvedEvent:
                    continue;
            }

            Flush();
            switch (ev)
            {
                case RunStartEvent:
                    output.Add(new JsonObject { ["e"] = "run_start" });
                    break;
                case TurnStartEvent:
                    output.Add(new JsonObject { ["e"] = "turn_start" });
                    break;
                case TurnEndEvent:
                    output.Add(new JsonObject { ["e"] = "turn_end" });
                    break;
                case MessageStartEvent start:
                    output.Add(new JsonObject { ["e"] = "message_start", ["role"] = start.Role == Role.User ? "user" : "assistant" });
                    break;
                case MessageEndEvent end:
                    output.Add(Commit(end.Message, ids));
                    break;
                case MessageDiscardEvent:
                    output.Add(new JsonObject { ["e"] = "discard" });
                    break;
                case ProviderRetryEvent retry:
                    output.Add(new JsonObject { ["e"] = "retry", ["attempt"] = retry.Attempt });
                    break;
                case ProviderSwitchEvent:
                    output.Add(new JsonObject { ["e"] = "switch" });
                    break;
                case ToolStartEvent start:
                    output.Add(new JsonObject
                    {
                        ["e"] = "tool_start",
                        ["callId"] = start.CallId,
                        ["name"] = start.ToolName,
                        ["input"] = Normalize.ToNode(start.Input),
                    });
                    break;
                case ToolEndEvent end:
                    var tool = new JsonObject { ["e"] = "tool_end", ["callId"] = end.CallId, ["isError"] = end.IsError, ["content"] = end.Output.Content };
                    if (end.Output.Truncated)
                    {
                        tool["truncated"] = true;
                        tool["originalBytes"] = end.Output.OriginalBytes;
                    }

                    output.Add(tool);
                    break;
                case ContextStatusEvent status:
                    output.Add(new JsonObject { ["e"] = "ctx", ["phase"] = status.Phase.ToString().ToLowerInvariant() });
                    break;
                case ContextUsageEvent usage:
                    output.Add(new JsonObject { ["e"] = "ctx_usage", ["window"] = usage.Window, ["shouldCompact"] = usage.ShouldCompact });
                    break;
                case NotificationEvent notification:
                    output.Add(new JsonObject { ["e"] = "notification", ["key"] = notification.Key });
                    break;
                case RunEndEvent end:
                    var runEnd = new JsonObject { ["e"] = "run_end", ["status"] = end.Status.ToString().ToLowerInvariant() };
                    if (end.Error is { } error)
                    {
                        var shape = new JsonObject { ["code"] = Normalize.SnakeCase(error.Code.ToString()) };
                        if (error.MessageKey is { } key)
                        {
                            shape["key"] = key;
                        }

                        runEnd["error"] = shape;
                    }

                    output.Add(runEnd);
                    break;
                case RunSettledEvent:
                    output.Add(new JsonObject { ["e"] = "run_settled" });
                    break;
                case SubagentStartEvent start:
                    output.Add(new JsonObject
                    {
                        ["e"] = "subagent_start",
                        ["callId"] = start.CallId,
                        ["childRunId"] = start.ChildRunId.Value,
                        ["childSessionId"] = start.ChildSessionId,
                        ["description"] = start.Description,
                        ["subagentType"] = start.AgentType,
                        ["model"] = start.Model,
                        ["background"] = start.Background,
                    });
                    break;
                case SubagentEndEvent end:
                    var subEnd = new JsonObject { ["e"] = "subagent_end", ["callId"] = end.CallId, ["childRunId"] = end.ChildRunId.Value, ["status"] = end.Status.ToString().ToLowerInvariant() };
                    if (end.Summary is { } summary)
                    {
                        subEnd["summary"] = summary;
                    }

                    if (end.Error is { } subError)
                    {
                        subEnd["error"] = new JsonObject { ["code"] = Normalize.SnakeCase(subError.Code.ToString()) };
                    }

                    output.Add(subEnd);
                    break;
                default:
                    output.Add(new JsonObject { ["e"] = ev.GetType().Name });
                    break;
            }
        }

        Flush();
        return output;
    }

    private static JsonObject Commit(Message message, IdMap ids)
    {
        var shaped = Normalize.Message(
            message.Role == Role.User ? "user" : "assistant",
            message.Internal,
            message.Id.Value,
            Normalize.ParseArray(MessageCodec.PartsToJson(message.Parts)),
            ids);
        var commit = new JsonObject { ["e"] = "commit" };
        foreach (var (key, value) in shaped)
        {
            commit[key] = value?.DeepClone();
        }

        return commit;
    }

    /// <summary>落库的消息行(从真 SQLite 读回来):角色 / internal / id / parts,和录制里 2.x 的 <c>rows</c> 同形。</summary>
    public static JsonArray Rows(IEnumerable<MessageRecord> records, IdMap ids) =>
        new([.. records.Select(r => (JsonNode)Normalize.Message(r.Role, r.Internal, r.Id, Normalize.ParseArray(r.PartsJson), ids))]);
}
