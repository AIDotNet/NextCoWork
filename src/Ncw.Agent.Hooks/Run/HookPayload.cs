using System.Text;
using System.Text.Json;

namespace Ncw.Agent.Hooks;

/// <summary>Notification 事件的补充:什么通知(目前只有 <c>tool_permission</c>:有个审批弹窗在等人)。</summary>
public sealed record HookNotification(string Kind, string? ToolName = null);

/// <summary>
/// 喂给钩子 stdin 的那一行 JSON(2.x <c>HookPayload</c> 的形状:键名一字不改,脚本可以原样沿用)。
/// 工具入参 / 输出是<b>模型产物</b>,经 <c>Utf8JsonWriter</c> 写出,一律按 JSON 转义,不拼字符串。
/// </summary>
public sealed record HookPayload
{
    public required string Event { get; init; }

    public required string SessionId { get; init; }

    public required string RunId { get; init; }

    public required string WorkspaceRoot { get; init; }

    /// <summary>这条钩子自己所在的层,便于一份脚本兼顾两种安装位置。</summary>
    public required HookScope Scope { get; init; }

    public string? ToolName { get; init; }

    public string? ToolInternalId { get; init; }

    public JsonElement? ToolInput { get; init; }

    /// <summary>PostToolUse 才有。</summary>
    public string? ToolOutput { get; init; }

    public bool? ToolIsError { get; init; }

    /// <summary>UserPromptSubmit 才有。</summary>
    public string? Prompt { get; init; }

    /// <summary>Stop / SubagentStop 才有。</summary>
    public string? Status { get; init; }

    /// <summary>
    /// 这一轮的 <c>Stop</c> 钩子已经被叫过至少一次了——<b>防死循环的唯一线索</b>:一条阻断了收尾的 Stop 钩子会让这一轮继续跑,
    /// 下一次收尾时它又会被叫一遍,脚本据此可以决定「第二次就放行」。没有这一项的话,一条无条件 <c>exit 2</c> 的 Stop 钩子就是一个无限循环。
    /// </summary>
    public bool? StopHookActive { get; init; }

    public HookNotification? Notification { get; init; }

    /// <summary><c>PreCompact</c> 触发来源(<c>manual</c> / <c>auto</c>)。</summary>
    public string? Trigger { get; init; }

    /// <summary><c>PreCompact</c> 的用户自定义压缩指令。</summary>
    public string? CustomInstructions { get; init; }

    /// <summary><c>SubagentStart</c> 的子代理定义名。</summary>
    public string? AgentType { get; init; }

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("event", Event);
            w.WriteString("sessionId", SessionId);
            w.WriteString("runId", RunId);
            w.WriteString("workspaceRoot", WorkspaceRoot);
            w.WriteString("scope", Scope == HookScope.Global ? "global" : "project");
            if (ToolName is not null)
            {
                w.WriteString("toolName", ToolName);
            }

            if (ToolInternalId is not null)
            {
                w.WriteString("toolInternalId", ToolInternalId);
            }

            if (ToolInput is { } input)
            {
                w.WritePropertyName("toolInput");
                input.WriteTo(w);
            }

            if (ToolOutput is not null)
            {
                w.WriteString("toolOutput", ToolOutput);
            }

            if (ToolIsError is { } isError)
            {
                w.WriteBoolean("toolIsError", isError);
            }

            if (Prompt is not null)
            {
                w.WriteString("prompt", Prompt);
            }

            if (Status is not null)
            {
                w.WriteString("status", Status);
            }

            if (StopHookActive is { } active)
            {
                w.WriteBoolean("stop_hook_active", active);
            }

            if (Notification is { } notification)
            {
                w.WriteStartObject("notification");
                w.WriteString("kind", notification.Kind);
                if (notification.ToolName is not null)
                {
                    w.WriteString("toolName", notification.ToolName);
                }

                w.WriteEndObject();
            }

            if (Trigger is not null)
            {
                w.WriteString("trigger", Trigger);
            }

            if (CustomInstructions is not null)
            {
                w.WriteString("custom_instructions", CustomInstructions);
            }

            if (AgentType is not null)
            {
                w.WriteString("agent_type", AgentType);
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>试运行喂给钩子的那份示例载荷。预览与真跑共用一份,两边不会漂。</summary>
    public static HookPayload Sample(string hookEvent, HookScope scope, string workspaceRoot) => new()
    {
        Event = hookEvent,
        SessionId = "test",
        RunId = "test",
        WorkspaceRoot = workspaceRoot,
        Scope = scope,
        ToolName = HookEvents.ToolScoped.Contains(hookEvent, StringComparer.Ordinal) ? "Bash" : null,
        ToolInternalId = HookEvents.ToolScoped.Contains(hookEvent, StringComparer.Ordinal) ? "Bash" : null,
        ToolInput = HookEvents.ToolScoped.Contains(hookEvent, StringComparer.Ordinal) ? SampleToolInput : null,
        Prompt = hookEvent == HookEvents.UserPromptSubmit ? "This is a trial run" : null,
        Status = hookEvent is HookEvents.Stop or HookEvents.SubagentStop ? "done" : null,
        StopHookActive = hookEvent is HookEvents.Stop or HookEvents.SubagentStop ? false : null,
    };

    private static readonly JsonElement SampleToolInput = JsonDocument.Parse("""{"command":"echo hello"}""").RootElement.Clone();
}
