using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Services.Settings;
using Ncw.Agent.Tools;
using Ncw.Agent.Widgets.Actions;
using Ncw.Agent.Widgets.Cards;
using Ncw.Protocol.Methods;

namespace Ncw.Agent.Host.Conversation;

/// <summary>可视化工具读转录的 Host 实现:从库里读会话的完整消息(同一个 Run 里已经提交的消息都在里面:每条消息先落库、再发 <c>message_end</c>)。</summary>
internal sealed class HostWidgetTranscript(Ncw.Agent.Storage.Sessions.SessionRepository sessions) : IWidgetTranscript
{
    public IReadOnlyList<Message> History(string sessionId) => [.. sessions.History(sessionId).Select(MessageCodec.FromRecord)];
}

/// <summary>
/// <c>widgets.runAction</c> 的实现(2.x <c>widget-host.ts</c> 的 <c>runWidgetAction</c>)。三条不变式:
/// <list type="number">
/// <item><b>动作的定义只从转录里取。</b>控制端只给 <c>(sessionId, callId, actionId, params)</c>,工具名与入参由这里从那次工具调用的卡片里读出来、再代参——widget 和控制端都改不了「要跑什么」。</item>
/// <item><b>非只读动作第一次一定问</b>(哪怕档位是 <c>full</c>):确认框由控制端画、显示真实的工具与入参(本方法返回 <c>confirm</c>,<c>Output</c> 是 <c>{toolName,input,environment}</c> 的 JSON);
/// 确认过一次的 (会话, 卡片, 工具, 绑定后的入参) 在进程内记住,入参一变就要重新问。</item>
/// <item><b>输出不进模型上下文。</b>结果只回到那张 widget。</item>
/// </list>
/// 判定走和 Run 里一样的权限链(<see cref="PermissionExtension.PreflightActionAsync"/>:联网开关 → deny 规则 → PreToolUse 钩子 → ask / allow 规则);档位和联网开关取项目设置。
/// 工具经项目的执行环境(<see cref="ToolRuntime"/>)运行,和 Run 里的同名工具是同一份实现;5 分钟上限。稳定 key:<c>widgets.action.*</c>(拒绝)。
/// </summary>
internal sealed class WidgetActionRunner(ConversationService conversation)
{
    public const int OutputLimit = 64 * 1024;

    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, byte> _confirmed = new(StringComparer.Ordinal);
    private readonly ProjectSettingsService _projects = new(conversation.Workspaces);
    private readonly HostWidgetTranscript _transcript = new(conversation.Sessions);

    private static WidgetActionResultDto Deny(string key, string detail) => new("deny", key, detail);

    public async ValueTask<WidgetActionResultDto> RunAsync(WidgetActionParams request, CancellationToken ct)
    {
        var session = conversation.Sessions.Get(request.SessionId);
        if (session is null)
        {
            return Deny("widgets.action.sessionGone", "The conversation no longer exists.");
        }

        var card = WidgetCards.FindByCall(_transcript.History(request.SessionId), request.CallId);
        var action = card?.Actions.FirstOrDefault(a => a.Id == request.ActionId);
        if (action is null)
        {
            return Deny("widgets.action.undeclared", $"No action \"{request.ActionId}\" was declared for this widget.");
        }

        JsonElement input;
        try
        {
            using var parameters = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.ParamsJson) ? "{}" : request.ParamsJson);
            input = WidgetActions.Bind(action, parameters.RootElement);
        }
        catch (Exception e) when (e is WidgetActionException or JsonException)
        {
            return Deny("widgets.action.invalidParams", e.Message);
        }

        var registered = conversation.ToolRegistry.Find(action.Tool) ?? conversation.ToolRegistry.List().FirstOrDefault(t => t.ExternalName == action.Tool);
        if (registered is null || registered.Disabled || WidgetActions.ForbiddenTools.Contains(registered.InternalId))
        {
            return Deny("widgets.action.toolUnavailable", $"The tool {action.Tool} is not available.");
        }

        var project = conversation.Workspaces.Get(session.WorkspaceId);
        if (project is null)
        {
            return Deny("widgets.action.projectGone", "The project no longer exists.");
        }

        var settings = _projects.Get(project.Id);
        var mode = settings is not null && PermissionModes.TryParse(settings.PermissionMode, out var parsed) ? parsed : PermissionMode.Ask;
        var environment = await conversation.EnvironmentOfAsync(project, ct).ConfigureAwait(false);
        var runtime = new ToolRuntime
        {
            Environment = environment,
            Root = Ncw.Agent.Environments.EnvPath.Parse(project.RootPath, environment.Facts.PathStyle),
            Shells = conversation.Shells,
        };

        // 判定:一个临时的、顶层的权限状态(不属于任何 Run),判完即关。
        var runId = new RunId("widget_" + Guid.NewGuid().ToString("N"));
        var callId = "widget_" + Guid.NewGuid().ToString("N");
        var interactions = conversation.Gate.OpenRun(runId);
        try
        {
            var state = PermissionRunState.FromRuntime(runtime, runId, session.Id, interactions, mode);
            state.WebSearch = settings?.WebSearch ?? false;
            var invocation = new ToolInvocation(callId, registered.ExternalName, registered.Tool, input);
            var verdict = await conversation.Permissions.PreflightActionAsync(state, invocation, conversation.Log, ct).ConfigureAwait(false);
            if (verdict.Kind == ActionPreflightKind.Deny)
            {
                return Deny("widgets.action.denied", verdict.Reason ?? "The action was blocked by the permission rules.");
            }

            if (verdict.Kind == ActionPreflightKind.Ask)
            {
                var key = $"{request.SessionId}\u0000{request.CallId}\u0000{registered.InternalId}\u0000{input.GetRawText()}";
                if (!_confirmed.ContainsKey(key))
                {
                    if (!request.Confirmed)
                    {
                        return new WidgetActionResultDto("confirm", null, ConfirmJson(registered.InternalId, input, runtime));
                    }

                    _confirmed[key] = 0;
                }
            }

            return await ExecuteAsync(registered, invocation, runtime, runId, session.Id, ct).ConfigureAwait(false);
        }
        finally
        {
            interactions.End(RunStatus.Done);
        }
    }

    private static string ConfirmJson(string toolName, JsonElement input, ToolRuntime runtime)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("toolName", toolName);
            w.WritePropertyName("input");
            input.WriteTo(w);
            w.WriteStartObject("environment");
            w.WriteString("deviceId", runtime.EnvironmentId.DeviceId);
            w.WriteString("environment", runtime.EnvironmentId.Environment);
            w.WriteString("name", runtime.Environment.DisplayName);
            w.WriteString("type", runtime.Environment.Kind == Ncw.Agent.Environments.EnvironmentKind.Local ? "local" : "ssh");
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static async ValueTask<WidgetActionResultDto> ExecuteAsync(RegisteredTool registered, ToolInvocation invocation, ToolRuntime runtime, RunId runId, string sessionId, CancellationToken ct)
    {
        const string Failed = "widgets.action.failed";
        var check = registered.Tool.PrepareArguments(invocation.Input);
        if (!check.IsValid)
        {
            return new WidgetActionResultDto("result", Failed, check.Error);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var baseContext = new ToolContext(
            runId, invocation.CallId, sessionId, 0, new EnvironmentTag(runtime.EnvironmentId.DeviceId, runtime.EnvironmentId.Environment), new Progress<ToolProgress>(static _ => { }), new FileMutationQueue());
        try
        {
            var result = await registered.Tool.ExecuteAsync(invocation with { Input = check.Input }, new ExecutionToolContext(baseContext, runtime), timeout.Token).ConfigureAwait(false);
            var text = string.Concat(result.Content.OfType<TextPart>().Select(static p => p.Text));
            return new WidgetActionResultDto("result", result.IsError ? Failed : null, text.Length > OutputLimit ? text[..OutputLimit] : text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new WidgetActionResultDto("result", Failed, "The action timed out.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new WidgetActionResultDto("result", Failed, e.Message);
        }
    }
}
