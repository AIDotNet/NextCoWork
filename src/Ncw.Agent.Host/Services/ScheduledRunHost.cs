using System.Text;
using System.Text.Json;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Llm;
using Ncw.Agent.Services.Schedules;
using Ncw.Agent.Services.Settings;
using Ncw.Agent.Storage.Sessions;
using Ncw.Core.Ids;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Services;

/// <summary>
/// <see cref="IScheduledRunHost"/> 的实现:建一个 <c>origin = scheduled</c> 的会话,经注册表的唯一入口以 <see cref="RunOrigin.Scheduled"/> 发起 Run(档位固定 <c>full</c>:后台运行从不等交互式审批,
/// 2.x 的 <c>SCHEDULED_PERMISSION_MODE</c>;所以定时任务的写入只有 owner),等它收尾,把结局和摘要(助手最后的文本,截到 1000 字符)交回调度器。
/// 目标对定时任务的 Run 不生效(编排层按 <see cref="RunOrigin.Scheduled"/> 排除;目标的 <c>Interactive</c> 回调对 <c>origin = scheduled</c> 的会话也返回 false)。
/// 模型取任务自己的;没写就取项目的默认模型,再取设备的默认模型。
/// </summary>
internal sealed class ScheduledRunHost(ConversationService conversation, ProjectSettingsService projects, SettingsService settings, TimeProvider clock) : IScheduledRunHost
{
    public const string SessionOrigin = "scheduled";

    public const string PermissionMode = "full";

    public async Task<ScheduledOutcome> ExecuteAsync(ScheduledTask task, string sessionId, Action onStarted, CancellationToken ct)
    {        // 云端:配额用尽(或正在收尾休眠)时定时任务不起 Run,记一条 error 记录(key = quota.exceeded)。先只检查(建会话之前,不留空会话),真正发起之前再记账。
        Gate(conversation.RunGate is { } gate ? gate.CheckInput : null);
        var project = conversation.Workspaces.Get(task.WorkspaceId)
            ?? throw new ScheduleException(ScheduleFailure.NotFound, ScheduledErrors.WorkspaceUnavailable, "The project does not exist.");
        try
        {
            // 定时任务没人在场:SSH 环境只用已经能直接连上的(已钉扎的指纹 + 已保存的凭据);要问人就明确失败,而不是挂着等一个不会来的回答
            using (Ncw.Agent.Host.Environments.EnvironmentService.NonInteractiveScope())
            {
                _ = await conversation.EnvironmentOfAsync(project, ct).ConfigureAwait(false);
            }
        }
        catch (ProtocolException)
        {
            throw new ScheduleException(ScheduleFailure.NotFound, ScheduledErrors.WorkspaceUnavailable, "The project's environment is not available.");
        }

        var workspace = projects.Get(project.Id);
        var pick = task.Model.Length > 0
            ? new ModelPick(task.Model, task.ModelProviderId)
            : workspace?.DefaultModel is { IsSet: true } projectModel ? projectModel : settings.Current().DefaultModel;
        if (!pick.IsSet || conversation.Models.Resolve(new ModelRef(pick.ProviderId, pick.Model)) is null)
        {
            throw new ScheduleException(ScheduleFailure.NotFound, ScheduledErrors.ModelUnavailable, "The task's model is not configured on this device.");
        }

        var model = new ModelRef(pick.ProviderId, pick.Model);
        var mode = workspace?.DefaultMode is { Length: > 0 } m ? m : "code";
        var thinking = Enum.TryParse<ThinkingLevel>(workspace?.DefaultThinking, ignoreCase: true, out var level) ? level : ThinkingLevel.Auto;
        conversation.Sessions.Create(new SessionCreate(
            project.Id, Id: sessionId, Origin: SessionOrigin, Title: task.Name, Model: pick.Model, ModelProviderId: pick.ProviderId, Mode: mode,
            Thinking: thinking.ToString().ToLowerInvariant(), RootPathAtCreation: project.RootPath));

        // 定时任务没有输入框:唯一的档位来源就是项目默认值(「最大上下文」也一样,漏了它半夜的任务仍按默认窗口压缩,且无处可查)。
        var options = new SendOptions(model, thinking, workspace?.MaxContext ?? false, mode, PermissionMode);
        var send = new UserSend(
            new RunId(Ulid.New(clock)), sessionId, project.Id, new MessageId(Ulid.New(clock)), [new TextPart(task.Prompt)], options)
        {
            Origin = RunOrigin.Scheduled,
        };
        Ncw.Agent.Cloud.RunTicket? ticket = null;
        Gate(() => ticket = conversation.RunGate?.AdmitRun());
        var sent = await conversation.Registry.SendAsync(send).ConfigureAwait(false);
        if (sent is not SendResult.StartedRun)
        {
            ticket?.Refund();
        }

        switch (sent)
        {
            case SendResult.StartedRun started:
                onStarted();
                return await AwaitAsync(started.Handle, sessionId, ct).ConfigureAwait(false);
            case SendResult.Refused refused:
                return new ScheduledOutcome(ScheduledRunStatuses.Error, Error: refused.Reason == RunRefusal.ShuttingDown ? ScheduledErrors.ExecutionFailed : ScheduledErrors.ExecutionFailed);
            default:
                return new ScheduledOutcome(ScheduledRunStatuses.Error, Error: ScheduledErrors.ExecutionFailed);
        }
    }

    /// <summary>云端闸门的协议错误 → 定时任务的 error 记录(key 是稳定的 <c>quota.exceeded</c> / <c>cloud.preparing</c>)。</summary>
    private static void Gate(Action? check)
    {
        try
        {
            check?.Invoke();
        }
        catch (Ncw.Protocol.Wire.ProtocolException e)
        {
            throw new ScheduleException(
                ScheduleFailure.Conflict,
                e.Code == Ncw.Protocol.Wire.ErrorCodes.RateLimited ? Ncw.Protocol.Methods.CloudErrorKeys.QuotaExceeded : Ncw.Protocol.Methods.CloudErrorKeys.Preparing, e.Message);
        }
    }

    private async Task<ScheduledOutcome> AwaitAsync(RunHandle handle, string sessionId, CancellationToken ct)
    {
        using var abortOnCancel = ct.Register(() => conversation.Registry.Abort(handle.RunId));
        if (handle.Completion is { } completion)
        {
            await completion.ConfigureAwait(false);
        }

        // 驱动本应在会话收尾之后才返回;万一违约,别让调度器留一条永远 running 的记录。
        if (handle.State == RunState.Running)
        {
            return new ScheduledOutcome(ScheduledRunStatuses.Error, Error: ScheduledErrors.ExecutionIncomplete);
        }

        return handle.State switch
        {
            RunState.Done => new ScheduledOutcome(ScheduledRunStatuses.Success, Summary: Summary(sessionId)),
            RunState.Aborted => new ScheduledOutcome(ScheduledRunStatuses.Aborted),
            _ => new ScheduledOutcome(ScheduledRunStatuses.Error, Error: ScheduledErrors.ExecutionFailed),
        };
    }

    /// <summary>助手所有文本块拼起来,截到 1000 字符(2.x 的 <c>summaryFor</c>)。</summary>
    internal string Summary(string sessionId)
    {
        var text = new StringBuilder();
        foreach (var message in conversation.Sessions.History(sessionId).Where(static m => m.Role == "assistant" && !m.Internal))
        {
            try
            {
                using var doc = JsonDocument.Parse(message.PartsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var part in doc.RootElement.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("type", out var type) && type.GetString() == "text"
                        && part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    {
                        if (text.Length > 0)
                        {
                            text.Append('\n');
                        }

                        text.Append(t.GetString());
                    }
                }
            }
            catch (JsonException)
            {
                // 坏 JSON 的消息不参与摘要。
            }
        }

        var joined = text.ToString().Trim();
        return joined.Length > 1000 ? joined[..1000] + "…" : joined;
    }
}
