using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Llm.Wire;
using Ncw.Agent.Storage.Sessions;

namespace Ncw.Agent.Kernel.Session;

/// <summary>
/// Storage 的 2.x 兼容记录(<see cref="MessageRecord"/>:id / role / parts 的 JSON 原文 / 毫秒时间戳 / internal / meta)↔ Llm 的 <see cref="Message"/> / <see cref="Part"/>。
/// Storage 只存取 JSON 文本、不引用 Llm,这一层的转换归会话层(04 §1.1)。全部用 <see cref="Utf8JsonWriter"/> / <see cref="JsonDocument"/> 手写,没有反射序列化。
/// </summary>
/// <remarks>
/// <b>写出的形状是 2.x 的</b>(键名、类型标签、不转义非 ASCII),所以 2.x 读 3.0 写的库、旧库被 3.0 读都成立:
/// <c>text / thinking / tool_call / tool_result / image / file_ref / error / goal_status / compact_boundary</c>。
/// <para>
/// <b>读是宽容的</b>:坏 JSON、未知类型的块、缺字段的块一律跳过(2.x 同),不抛。认不出的块(2.x 的 <c>subagent</c> 块,回执在 <c>tool_result.subagent</c> 里)不进模型上下文。
/// </para>
/// <para>
/// <b>透传数据的协议标签</b>(01-llm §3):2.x 的 Anthropic 透传没有 <c>protocol</c> 字段(<c>{signature}</c> / <c>{redacted}</c>),读出时补 <c>anthropic</c> 标签;
/// OpenAI 两种(<c>{protocol, field}</c> / <c>{protocol, item}</c>)带标签,读出时把标签拆到 <see cref="ProtocolOpaque.Protocol"/>、其余字段留在 <c>Data</c>;
/// 写回时 anthropic 去掉标签(和 2.x 字节一致),其余把标签写回去。认不出的透传数据丢掉(标签对不上的编码器本来就忽略)。
/// </para>
/// <para>
/// 助手消息的来源(<see cref="Provenance"/>)和停止原因 2.x 没有,存在 <c>meta</c> 列(迁移 39):<c>{"source":{"api","provider","model"},"stop":"aborted"}</c>。
/// 2.x 写的旧消息没有它(<c>Source = null</c> = 来源未知:换模型时丢弃思考块,01-llm §4)。
/// </para>
/// </remarks>
public static class MessageCodec
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ── 记录 ↔ 消息 ──

    public static MessageRecord ToRecord(Message message) => new(
        message.Id.Value,
        message.Role == Role.User ? "user" : "assistant",
        PartsToJson(message.Parts),
        message.CreatedAt.ToUnixTimeMilliseconds(),
        SchemaVersion: 1,
        Internal: message.Internal,
        MetaJson: MetaToJson(message));

    public static Message FromRecord(MessageRecord record)
    {
        var meta = MetaFromJson(record.MetaJson);
        return new Message(
            new MessageId(record.Id),
            record.Role == "assistant" ? Role.Assistant : Role.User,
            PartsFromJson(record.PartsJson),
            DateTimeOffset.FromUnixTimeMilliseconds(record.CreatedAt),
            record.Internal,
            meta.Source,
            meta.Stop);
    }

    // ── meta ──

    private static string? MetaToJson(Message message)
    {
        if (message.Role != Role.Assistant || (message.Source is null && message.Stop is null))
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            if (message.Source is { } s)
            {
                w.WriteStartObject("source");
                w.WriteString("api", s.Api);
                w.WriteString("provider", s.Provider);
                w.WriteString("model", s.Model);
                w.WriteEndObject();
            }

            if (message.Stop is { } stop)
            {
                w.WriteString("stop", StopName(stop));
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static (Provenance? Source, StopReason? Stop) MetaFromJson(string? json)
    {
        if (string.IsNullOrEmpty(json) || !Jsn.TryParse(json, out var meta) || !meta.IsObject())
        {
            return (null, null);
        }

        Provenance? source = null;
        if (meta.Obj("source") is { } s && s.Str("api") is { } api && s.Str("provider") is { } provider && s.Str("model") is { } model)
        {
            source = new Provenance(api, provider, model);
        }

        return (source, StopFromName(meta.Str("stop")));
    }

    private static string StopName(StopReason stop) => stop switch
    {
        StopReason.EndTurn => "end_turn",
        StopReason.ToolUse => "tool_use",
        StopReason.MaxTokens => "max_tokens",
        StopReason.StopSequence => "stop_sequence",
        StopReason.Refusal => "refusal",
        StopReason.Aborted => "aborted",
        _ => "end_turn",
    };

    private static StopReason? StopFromName(string? name) => name switch
    {
        "end_turn" => StopReason.EndTurn,
        "tool_use" => StopReason.ToolUse,
        "max_tokens" => StopReason.MaxTokens,
        "stop_sequence" => StopReason.StopSequence,
        "refusal" => StopReason.Refusal,
        "aborted" => StopReason.Aborted,
        _ => null,
    };

    // ── 写:Part → JSON ──

    public static string PartsToJson(IReadOnlyList<Part> parts)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartArray();
            foreach (var part in parts)
            {
                WritePart(w, part);
            }

            w.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WritePart(Utf8JsonWriter w, Part part)
    {
        switch (part)
        {
            case TextPart text:
                w.WriteStartObject();
                w.WriteString("type", "text");
                w.WriteString("text", text.Text);
                w.WriteEndObject();
                break;
            case ThinkingPart thinking:
                w.WriteStartObject();
                w.WriteString("type", "thinking");
                w.WriteString("text", thinking.Text);
                if (thinking.Opaque is { } opaque)
                {
                    w.WritePropertyName("opaque");
                    WriteOpaque(w, opaque);
                }

                if (thinking.DurationMs is { } duration)
                {
                    w.WriteNumber("durationMs", duration);
                }

                if (thinking.Tokens is { } tokens)
                {
                    w.WriteNumber("tokens", tokens);
                }

                w.WriteEndObject();
                break;
            case ToolCallPart call:
                w.WriteStartObject();
                w.WriteString("type", "tool_call");
                w.WriteString("callId", call.CallId);
                w.WriteString("name", call.Name);
                w.WritePropertyName("input");
                WriteElement(w, call.Input);
                w.WriteEndObject();
                break;
            case ToolResultPart result:
                w.WriteStartObject();
                w.WriteString("type", "tool_result");
                w.WriteString("callId", result.CallId);
                w.WritePropertyName("output");
                WriteOutput(w, result.Output);
                w.WriteBoolean("isError", result.IsError);
                if (result.Subagent is { } receipt)
                {
                    w.WriteStartObject("subagent");
                    w.WriteString("childRunId", receipt.ChildRunId);
                    if (receipt.Status is { } status)
                    {
                        w.WriteString("status", status);
                    }

                    if (receipt.Summary is { } summary)
                    {
                        w.WriteString("summary", summary);
                    }

                    if (receipt.Color is { } color)
                    {
                        w.WriteString("color", color);
                    }

                    if (receipt.Error is { } receiptError)
                    {
                        w.WritePropertyName("error");
                        WriteError(w, receiptError);
                    }

                    if (receipt.Background is { } background)
                    {
                        w.WriteBoolean("background", background);
                    }

                    if (receipt.ReportStatus is { } reportStatus)
                    {
                        w.WriteString("reportStatus", reportStatus);
                    }

                    w.WriteEndObject();
                }

                w.WriteEndObject();
                break;
            case ImagePart image:
                w.WriteStartObject();
                w.WriteString("type", "image");
                w.WriteString("mime", image.Mime);
                w.WriteString("dataRef", image.DataRef);
                w.WriteEndObject();
                break;
            case FileRefPart file:
                w.WriteStartObject();
                w.WriteString("type", "file_ref");
                w.WriteString("path", file.Path);
                w.WriteString("name", file.Name);
                w.WriteEndObject();
                break;
            case ErrorPart error:
                w.WriteStartObject();
                w.WriteString("type", "error");
                w.WritePropertyName("error");
                WriteError(w, error.Error);
                w.WriteEndObject();
                break;
            case SubagentReportPart report:
                w.WriteStartObject();
                w.WriteString("type", "subagent");
                w.WriteString("callId", report.CallId);
                w.WriteString("childRunId", report.ChildRunId);
                if (report.Summary is { } reportSummary)
                {
                    w.WriteString("summary", reportSummary);
                }

                w.WriteEndObject();
                break;
            case GoalStatusPart goal:
                w.WriteStartObject();
                w.WriteString("type", "goal_status");
                w.WriteBoolean("met", goal.Met);
                w.WriteString("condition", goal.Condition);
                if (goal.Reason is { } reason)
                {
                    w.WriteString("reason", reason);
                }

                if (goal.Failed is { } failed)
                {
                    w.WriteBoolean("failed", failed);
                }

                if (goal.Cleared is { } cleared)
                {
                    w.WriteBoolean("cleared", cleared);
                }

                if (goal.Set is { } set)
                {
                    w.WriteBoolean("set", set);
                }

                if (goal.Origin is { } origin)
                {
                    w.WriteString("origin", origin);
                }

                if (goal.Iterations is { } iterations)
                {
                    w.WriteNumber("iterations", iterations);
                }

                if (goal.DurationMs is { } durationMs)
                {
                    w.WriteNumber("durationMs", durationMs);
                }

                if (goal.Tokens is { } goalTokens)
                {
                    w.WriteNumber("tokens", goalTokens);
                }

                if (goal.Id is { } goalId)
                {
                    w.WriteString("id", goalId);
                }

                if (goal.CreatedAt is { } createdAt)
                {
                    w.WriteNumber("createdAt", createdAt);
                }

                w.WriteEndObject();
                break;
            case CompactBoundaryPart boundary:
                w.WriteStartObject();
                w.WriteString("type", "compact_boundary");
                w.WriteString("trigger", boundary.Trigger);
                w.WriteNumber("preTokens", boundary.PreTokens);
                w.WriteNumber("postTokens", boundary.PostTokens);
                w.WriteString("summary", boundary.Summary);
                if (boundary.Instructions is { } instructions)
                {
                    w.WriteString("instructions", instructions);
                }

                if (boundary.RestoredFiles is { Count: > 0 } files)
                {
                    w.WriteStartArray("restoredFiles");
                    foreach (var file in files)
                    {
                        w.WriteStringValue(file);
                    }

                    w.WriteEndArray();
                }

                if (boundary.SummaryModel is { } model)
                {
                    w.WriteStartObject("summaryModel");
                    w.WriteString("model", model.Model);
                    if (model.ProviderId is { } providerId)
                    {
                        w.WriteString("modelProviderId", providerId);
                    }

                    w.WriteString("thinking", model.Thinking);
                    if (model.FellBack)
                    {
                        w.WriteBoolean("fellBack", true);
                    }

                    w.WriteEndObject();
                }

                w.WriteEndObject();
                break;
        }
    }

    private static void WriteElement(Utf8JsonWriter w, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Undefined)
        {
            w.WriteStartObject();
            w.WriteEndObject();
        }
        else
        {
            element.WriteTo(w);
        }
    }

    private static void WriteOutput(Utf8JsonWriter w, ToolOutput output)
    {
        w.WriteStartObject();
        w.WriteString("content", output.Content);
        if (output.Images is { Count: > 0 } images)
        {
            w.WriteStartArray("images");
            foreach (var image in images)
            {
                w.WriteStartObject();
                w.WriteString("mime", image.Mime);
                w.WriteString("dataRef", image.DataRef);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        if (output.Truncated)
        {
            w.WriteBoolean("truncated", true);
        }

        if (output.OriginalBytes is { } original)
        {
            w.WriteNumber("originalBytes", original);
        }

        if (output.Details is { } details && details.ValueKind != JsonValueKind.Undefined)
        {
            w.WritePropertyName("details");
            details.WriteTo(w);
        }

        w.WriteEndObject();
    }

    private static void WriteOpaque(Utf8JsonWriter w, ProtocolOpaque opaque)
    {
        w.WriteStartObject();
        if (opaque.Protocol != WireProtocols.AnthropicName)
        {
            w.WriteString("protocol", opaque.Protocol);
        }

        if (opaque.Data.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in opaque.Data.EnumerateObject())
            {
                w.WritePropertyName(property.Name);
                property.Value.WriteTo(w);
            }
        }

        w.WriteEndObject();
    }

    private static void WriteError(Utf8JsonWriter w, AgentError error)
    {
        w.WriteStartObject();
        w.WriteString("code", CodeName(error.Code));
        w.WriteString("message", error.Message);
        w.WriteBoolean("retryable", error.Retryable);
        if (error.Status is { } status)
        {
            w.WriteNumber("status", status);
        }

        if (error.RetryAfterMs is { } retryAfter)
        {
            w.WriteNumber("retryAfterMs", retryAfter);
        }

        if (error.MessageKey is { } key)
        {
            w.WriteString("messageKey", key);
        }

        if (error.MessageParams is { Count: > 0 } parameters)
        {
            w.WriteStartObject("messageParams");
            foreach (var (name, value) in parameters)
            {
                w.WriteString(name, value);
            }

            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    private static string CodeName(AgentErrorCode code) => code switch
    {
        AgentErrorCode.Auth => "auth",
        AgentErrorCode.RateLimit => "rate_limit",
        AgentErrorCode.ContextLength => "context_length",
        AgentErrorCode.Network => "network",
        AgentErrorCode.Aborted => "aborted",
        AgentErrorCode.ToolFailed => "tool_failed",
        AgentErrorCode.Provider => "provider",
        AgentErrorCode.CacheUnsupported => "cache_unsupported",
        AgentErrorCode.NoHealthyProvider => "no_healthy_provider",
        AgentErrorCode.Conflict => "conflict",
        _ => "unknown",
    };

    private static AgentErrorCode CodeFromName(string? name) => name switch
    {
        "auth" => AgentErrorCode.Auth,
        "rate_limit" => AgentErrorCode.RateLimit,
        "context_length" => AgentErrorCode.ContextLength,
        "network" => AgentErrorCode.Network,
        "aborted" => AgentErrorCode.Aborted,
        "tool_failed" => AgentErrorCode.ToolFailed,
        "provider" => AgentErrorCode.Provider,
        "cache_unsupported" => AgentErrorCode.CacheUnsupported,
        "no_healthy_provider" => AgentErrorCode.NoHealthyProvider,
        "conflict" => AgentErrorCode.Conflict,
        _ => AgentErrorCode.Unknown,
    };

    // ── 读:JSON → Part ──

    /// <summary>坏 JSON、非数组当空;块级别容错(未知类型、缺字段的块跳过)。</summary>
    public static IReadOnlyList<Part> PartsFromJson(string? json)
    {
        if (string.IsNullOrEmpty(json) || !Jsn.TryParse(json, out var root) || root.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var parts = new List<Part>();
        foreach (var element in root.EnumerateArray())
        {
            if (ReadPart(element) is { } part)
            {
                parts.Add(part);
            }
        }

        return parts;
    }

    private static Part? ReadPart(JsonElement e)
    {
        if (!e.IsObject())
        {
            return null;
        }

        switch (e.Str("type"))
        {
            case "text":
                return e.Str("text") is { } text ? new TextPart(text) : null;
            case "thinking":
                return new ThinkingPart(
                    e.Str("text") ?? "",
                    ReadOpaque(e.Prop("opaque")),
                    e.Num("durationMs") is { } d ? (int)d : null,
                    e.Num("tokens") is { } t ? (int)t : null);
            case "tool_call":
                return e.Str("callId") is { } callId && e.Str("name") is { } name
                    ? new ToolCallPart(callId, name, e.Prop("input") is { } input ? input.Clone() : Jsn.EmptyObject)
                    : null;
            case "tool_result":
                if (e.Str("callId") is not { } resultId)
                {
                    return null;
                }

                SubagentReceipt? receipt = e.Obj("subagent") is { } sub && sub.Str("childRunId") is { } child
                    ? new SubagentReceipt(
                        child, sub.Str("status"), sub.Str("summary"), sub.Bool("background"), sub.Str("reportStatus"), sub.Str("color"),
                        sub.Obj("error") is { } receiptError ? ReadError(receiptError) : null)
                    : null;
                return new ToolResultPart(resultId, ReadOutput(e.Obj("output")), e.Bool("isError") ?? false, receipt);
            case "image":
                return e.Str("mime") is { } mime && e.Str("dataRef") is { } dataRef ? new ImagePart(mime, dataRef) : null;
            case "file_ref":
                return e.Str("path") is { } path ? new FileRefPart(path, e.Str("name") ?? path) : null;
            case "error":
                return e.Obj("error") is { } error ? new ErrorPart(ReadError(error)) : null;
            case "subagent":
                return e.Str("callId") is { } reportCall && e.Str("childRunId") is { } reportChild ? new SubagentReportPart(reportCall, reportChild, e.Str("summary")) : null;
            case "goal_status":
                return new GoalStatusPart(
                    e.Bool("met") ?? false, e.Str("condition") ?? "", e.Str("reason"), e.Bool("failed"), e.Bool("cleared"),
                    e.Bool("set"), e.Str("origin"), (int?)e.Count("iterations"), e.Count("durationMs"), e.Count("tokens"), e.Str("id"), e.Count("createdAt"));
            case "compact_boundary":
                var model = e.Obj("summaryModel") is { } sm && sm.Str("model") is { } smModel
                    ? new SummaryModelInfo(smModel, sm.Str("modelProviderId"), sm.Str("thinking") ?? "auto", sm.Bool("fellBack") ?? false)
                    : null;
                var restored = e.Arr("restoredFiles").Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
                return new CompactBoundaryPart(
                    e.Str("trigger") ?? "auto",
                    (int)(e.Num("preTokens") ?? 0),
                    (int)(e.Num("postTokens") ?? 0),
                    e.Str("summary") ?? "",
                    e.Str("instructions"),
                    restored.Count == 0 ? null : restored,
                    model);
            default:
                return null; // 2.x 的 subagent 块等:回执在 tool_result.subagent 里,不进上下文
        }
    }

    private static ToolOutput ReadOutput(JsonElement? output)
    {
        if (output is not { } o)
        {
            return new ToolOutput("");
        }

        var images = o.Arr("images")
            .Where(i => i.Str("mime") is not null && i.Str("dataRef") is not null)
            .Select(i => new ToolOutputImage(i.Str("mime")!, i.Str("dataRef")!))
            .ToList();
        // 3.0 写 details;2.x 的 card 是同一个意思(只给界面的结构化数据),读出时归入 Details
        JsonElement? details = o.Prop("details") is { } d ? d.Clone() : o.Prop("card") is { } card ? card.Clone() : null;
        return new ToolOutput(
            o.Str("content") ?? "",
            images.Count == 0 ? null : images,
            o.Bool("truncated") ?? false,
            o.Num("originalBytes") is { } bytes ? (long)bytes : null,
            details);
    }

    private static ProtocolOpaque? ReadOpaque(JsonElement? opaque)
    {
        if (opaque is not { ValueKind: JsonValueKind.Object } o)
        {
            return null;
        }

        if (o.Str("protocol") is { } protocol)
        {
            // 标签拆出来,其余字段留在 Data
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                foreach (var property in o.EnumerateObject())
                {
                    if (property.Name != "protocol")
                    {
                        w.WritePropertyName(property.Name);
                        property.Value.WriteTo(w);
                    }
                }

                w.WriteEndObject();
            }

            return new ProtocolOpaque(protocol, Jsn.Parse(Encoding.UTF8.GetString(stream.ToArray())));
        }

        // 2.x 的 Anthropic 透传没有标签:{signature} 或 {redacted}。补 anthropic,否则标签对不上、签名永远回放不了。
        if (o.Str("signature") is not null || o.Str("redacted") is not null)
        {
            return new ProtocolOpaque(WireProtocols.AnthropicName, o.Clone());
        }

        return null;
    }

    private static AgentError ReadError(JsonElement e)
    {
        var parameters = e.Obj("messageParams") is { } p
            ? p.EnumerateObject().ToDictionary(
                x => x.Name,
                x => x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString() ?? "" : x.Value.GetRawText())
            : null;
        return new AgentError(
            CodeFromName(e.Str("code")),
            e.Str("message") ?? "",
            e.Bool("retryable") ?? false,
            e.Num("status") is { } status ? (int)status : null,
            e.Num("retryAfterMs") is { } retry ? (int)retry : null,
            e.Str("messageKey"),
            parameters);
    }
}
