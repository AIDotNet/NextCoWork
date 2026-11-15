using System.Buffers;
using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Tools;
using Ncw.Agent.Widgets.Actions;
using Ncw.Agent.Widgets.Sanitizing;

namespace Ncw.Agent.Widgets.Cards;

/// <summary>读一个会话当前的转录(<c>visualize_edit_widget</c> / <c>visualize_update_widget</c> 按标题找卡、<c>widgets.runAction</c> 按 callId 找动作)。生产实现在 Host,读 Storage;测试给一个列表。</summary>
public interface IWidgetTranscript
{
    IReadOnlyList<Message> History(string sessionId);
}

public sealed class FixedWidgetTranscript(Func<string, IReadOnlyList<Message>> read) : IWidgetTranscript
{
    public IReadOnlyList<Message> History(string sessionId) => read(sessionId);
}

/// <summary>转录里找回来的一张 widget 卡(<see cref="Code"/> 是消毒后的源码——<c>visualize_edit_widget</c> 的编辑对它做)。</summary>
public sealed record WidgetCardRef(string Title, string Format, string Code, IReadOnlyList<WidgetActionDef> Actions, bool Interactive);

/// <summary>widget 卡片的 Details 形状与读回。卡片只走界面轨(<c>Details</c> 永远不发给模型)。</summary>
public static class WidgetCards
{
    public const string ShowTool = "visualize_show_widget";

    public const string KindWidget = "widget";

    public const string KindData = "widgetData";

    /// <summary>
    /// 卡片 Details:<c>kind: "widget"</c>、<c>environment</c>、<c>title</c>、<c>format</c>(<c>html</c> / <c>svg</c>)、<c>code</c>(消毒后,桌面渲染用)、
    /// <c>snapshot</c>(静态快照:无脚本、无外链,手机端降级用)、<c>size</c>(根 svg 的尺寸提示)、<c>interactive</c>、<c>actions</c>、<c>submitted</c>(交互模式提交后的值 JSON 文本)、
    /// <c>findings</c>(消毒拿掉了什么:规则 id + 例子 + 次数,界面按 id 翻译)。
    /// </summary>
    public static JsonElement Details(
        ToolRuntime? runtime, string title, string format, SanitizeResult code, string snapshot, bool interactive, IReadOnlyList<WidgetActionDef> actions, string? submitted = null)
    {
        var buffer = new ArrayBufferWriter<byte>(code.Code.Length + snapshot.Length + 512);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("kind", KindWidget);
            if (runtime is not null)
            {
                WriteEnvironment(w, runtime);
            }

            w.WriteString("title", title);
            w.WriteString("format", format);
            w.WriteString("code", code.Code);
            w.WriteString("snapshot", snapshot);
            if (code.Size is { } size)
            {
                w.WriteStartObject("size");
                if (size.Width is { } width)
                {
                    w.WriteNumber("width", width);
                }

                if (size.Height is { } height)
                {
                    w.WriteNumber("height", height);
                }

                w.WriteEndObject();
            }

            if (interactive)
            {
                w.WriteBoolean("interactive", true);
            }

            if (submitted is not null)
            {
                w.WriteString("submitted", submitted);
            }

            if (actions.Count > 0)
            {
                w.WritePropertyName("actions");
                WidgetActions.Write(w, actions);
            }

            if (code.Findings.Count > 0)
            {
                w.WriteStartArray("findings");
                foreach (var finding in code.Findings)
                {
                    w.WriteStartObject();
                    w.WriteString("rule", finding.Rule);
                    w.WriteString("detail", finding.Detail);
                    w.WriteNumber("count", finding.Count);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }

            w.WriteEndObject();
        }

        return Jsn.Parse(System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    public static JsonElement DataDetails(ToolRuntime? runtime, string title, JsonElement data)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("kind", KindData);
            if (runtime is not null)
            {
                WriteEnvironment(w, runtime);
            }

            w.WriteString("title", title);
            w.WritePropertyName("data");
            data.WriteTo(w);
            w.WriteEndObject();
        }

        return Jsn.Parse(System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    /// <summary>只带环境标识和一小段结构化数据的 Details(读规范这类没有卡片的工具)。</summary>
    public static JsonElement Simple(ToolRuntime runtime, string kind, Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("kind", kind);
            WriteEnvironment(w, runtime);
            body(w);
            w.WriteEndObject();
        }

        return Jsn.Parse(System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private static void WriteEnvironment(Utf8JsonWriter w, ToolRuntime runtime)
    {
        w.WriteStartObject("environment");
        w.WriteString("deviceId", runtime.EnvironmentId.DeviceId);
        w.WriteString("environment", runtime.EnvironmentId.Environment);
        w.WriteString("name", runtime.Environment.DisplayName);
        w.WriteString("type", runtime.Environment.Kind == EnvironmentKind.Local ? "local" : "ssh");
        w.WriteEndObject();
    }

    // ───────────────────────── 读回 ─────────────────────────

    /// <summary>转录里最近一张叫这个标题的 widget 卡(<c>edit</c> / <c>update</c> 按标题认领)。</summary>
    public static WidgetCardRef? FindByTitle(IReadOnlyList<Message> messages, string title)
    {
        for (var m = messages.Count - 1; m >= 0; m--)
        {
            var parts = messages[m].Parts;
            for (var p = parts.Count - 1; p >= 0; p--)
            {
                if (parts[p] is ToolResultPart { IsError: false, Output.Details: { } details } && ReadCard(details) is { } card && card.Title == title)
                {
                    return card;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 某次工具调用产出的那张卡。先找结果;找不到(交互模式还挂着,工具没返回)再看调用入参——★ 入参来自模型,但它已经过了工具 schema 的校验才走到「挂起」这一步,
    /// 动作声明在这里再解析 / 校验一遍;只认 <c>visualize_show_widget</c> 这一个名字(编辑工具不能开交互)。
    /// </summary>
    public static WidgetCardRef? FindByCall(IReadOnlyList<Message> messages, string callId)
    {
        for (var m = messages.Count - 1; m >= 0; m--)
        {
            foreach (var part in messages[m].Parts)
            {
                if (part is ToolResultPart { IsError: false, Output.Details: { } details } result && result.CallId == callId)
                {
                    return ReadCard(details);
                }
            }
        }

        for (var m = messages.Count - 1; m >= 0; m--)
        {
            foreach (var part in messages[m].Parts)
            {
                if (part is not ToolCallPart call || call.CallId != callId)
                {
                    continue;
                }

                if (call.Name != ShowTool)
                {
                    return null;
                }

                var interactive = call.Input.Bool("interactive") == true;
                var title = call.Input.Str("title") ?? "";
                var format = call.Input.Str("format") ?? "html";
                try
                {
                    return new WidgetCardRef(title, format, "", ParseActions(call.Input), interactive);
                }
                catch (WidgetActionException)
                {
                    return new WidgetCardRef(title, format, "", [], interactive);
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<WidgetActionDef> ParseActions(JsonElement owner) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty("actions", out var raw) && raw.ValueKind == JsonValueKind.Array && raw.GetArrayLength() > 0
            ? WidgetActions.Parse(raw)
            : [];

    public static WidgetCardRef? ReadCard(JsonElement details)
    {
        if (details.ValueKind != JsonValueKind.Object || details.Str("kind") != KindWidget)
        {
            return null;
        }

        IReadOnlyList<WidgetActionDef> actions;
        try
        {
            actions = ParseActions(details);
        }
        catch (WidgetActionException)
        {
            actions = []; // 卡片里的声明坏了(不该发生):没有动作,而不是半套动作
        }

        return new WidgetCardRef(details.Str("title") ?? "", details.Str("format") ?? "html", details.Str("code") ?? "", actions, details.Bool("interactive") == true);
    }
}
