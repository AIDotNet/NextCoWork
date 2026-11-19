using System.Text.Json;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Subagents;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Storage.Sessions;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Conversation;

/// <summary>
/// 领域类型 ↔ 协议 DTO(只有 Host 知道协议,其它 Agent 层只产出领域类型)。纯函数,不走反射;
/// JSON 文本(工具输入、详情、消息块)原样搬运,不在这里重新解释。
/// </summary>
internal static class Mapping
{
    // ───────────────────────── 会话 ─────────────────────────

    public static SessionDto ToDto(SessionRecord s) => new(
        s.Id, s.WorkspaceId, s.ParentSessionId, s.Origin, s.Title, s.TitleSource, s.Model, s.ModelProviderId, s.Mode, s.Thinking,
        s.Archived, s.Favorited, s.CreatedAt, s.UpdatedAt, s.PermissionMode);

    public static SessionListItemDto ToDto(SessionListItem s) => new(s.Id, s.Title, s.UpdatedAt, s.Archived, s.Favorited, s.SkillExtraction);

    public static MessageDto ToDto(MessageRecord m) => new(m.Id, m.Role, m.CreatedAt, m.Internal, m.PartsJson, m.MetaJson);

    public static MessageDto ToDto(Message message) => ToDto(Ncw.Agent.Kernel.Session.MessageCodec.ToRecord(message));

    public static RunUsageDto ToDto(RunUsageRecord u) => new(
        u.InputTokens, u.OutputTokens, u.CacheReadInputTokens, u.CacheCreationInputTokens, u.CacheCreation1hInputTokens, u.ReasoningTokens, u.UpstreamMs,
        u.Cost is { } cost ? new CostDto("known", cost.Micros, cost.Currency.ToLowerInvariant()) : null);

    public static SessionPageDto ToDto(SessionDetailRecord d) => new(
        ToDto(d.Session),
        [.. d.Messages.Select(static m => ToDto(m))],
        d.HasMore,
        new Dictionary<string, string>(d.MessageRuns, StringComparer.Ordinal),
        d.RunUsage.ToDictionary(static kv => kv.Key, static kv => ToDto(kv.Value), StringComparer.Ordinal),
        new Dictionary<string, string>(d.RunModel, StringComparer.Ordinal));

    // ───────────────────────── 输入与排队 ─────────────────────────

    public static IReadOnlyList<Part> ToParts(InputPartDto[] parts)
    {
        var result = new List<Part>(parts.Length);
        foreach (var part in parts)
        {
            switch (part.Kind)
            {
                case InputPartKinds.Text:
                    result.Add(new TextPart(part.Text ?? ""));
                    break;
                case InputPartKinds.Image when !string.IsNullOrEmpty(part.DataRef):
                    result.Add(new ImagePart(part.Mime ?? "image/png", part.DataRef));
                    break;
                case InputPartKinds.File when !string.IsNullOrEmpty(part.Path):
                    result.Add(new FileRefPart(part.Path, part.Name ?? System.IO.Path.GetFileName(part.Path)));
                    break;
                default:
                    throw new ProtocolException(ErrorCodes.InvalidRequest, $"Unsupported input part '{part.Kind}'.");
            }
        }

        return result;
    }

    public static InputPartDto[] ToDto(IReadOnlyList<Part> parts)
    {
        var result = new List<InputPartDto>(parts.Count);
        foreach (var part in parts)
        {
            switch (part)
            {
                case TextPart text:
                    result.Add(new InputPartDto(InputPartKinds.Text, Text: text.Text));
                    break;
                case ImagePart image:
                    result.Add(new InputPartDto(InputPartKinds.Image, Mime: image.Mime, DataRef: image.DataRef));
                    break;
                case FileRefPart file:
                    result.Add(new InputPartDto(InputPartKinds.File, Path: file.Path, Name: file.Name));
                    break;
            }
        }

        return [.. result];
    }

    /// <summary>空的模型 / 模式落回会话上记住的;权限档位空 = <c>ask</c>。</summary>
    public static SendOptions ToOptions(SendOptionsDto dto, SessionRecord session)
    {
        var alias = dto.Model.Length > 0 ? dto.Model : session.Model;
        if (alias.Length == 0)
        {
            throw new ProtocolException(ErrorCodes.InvalidRequest, "A model is required: pass one in the options or set it on the session first.");
        }

        var providerId = dto.Model.Length > 0 ? dto.ModelProviderId : session.ModelProviderId;
        var thinking = Enum.TryParse<ThinkingLevel>(dto.Thinking, ignoreCase: true, out var level) ? level : ThinkingLevel.Auto;
        var permission = dto.PermissionMode.Length > 0 ? dto.PermissionMode : session.PermissionMode;
        if (!Ncw.Agent.Extensions.Permissions.PermissionModes.TryParse(permission, out _))
        {
            throw new ProtocolException(ErrorCodes.InvalidRequest, $"Unknown permission mode '{permission}'.");
        }

        return new SendOptions(new ModelRef(providerId, alias), thinking, dto.MaxContext, dto.Mode.Length > 0 ? dto.Mode : session.Mode, permission);
    }

    public static SendOptionsDto ToDto(SendOptions o) => new(
        o.Model.Alias, o.Model.ProviderId, o.Thinking.ToString().ToLowerInvariant(), o.MaxContext, o.Mode, o.PermissionMode);

    public static QueueItemDto ToDto(QueueItem item) => new(
        item.Id.Value, ToDto(item.Parts), item.State == QueueState.Promoted ? QueueStates.Promoted : QueueStates.Pending,
        item.PromotedOrder, item.EnqueuedAt, item.PromotedAt, ToDto(item.Options));

    public static QueueResultDto ToResult(QueueResult result) => new(result.Accepted, ToDto(result.Update));

    public static QueueDto ToDto(QueueUpdate update) => new(update.SessionId, update.Rev, [.. update.Items.Select(static i => ToDto(i))]);

    // ───────────────────────── 设备广播 ─────────────────────────

    /// <summary><c>device</c> 主题的状态:权威索引(Core 的 <see cref="ActiveRunIndexBuilder"/> 生成)+ 项目版本号。</summary>
    public static DeviceTopicState ToDeviceState(ActiveRunIndex index, long projectsRevision) =>
        new([.. index.Runs.Select(static r => new ActiveRunDto(r.RunId, r.SessionId, r.WorkspaceId, r.PendingInteractions))], projectsRevision)
        {
            Subagents = [.. index.Subagents.Select(static s => new ActiveSubagentDto(s.RunId, s.ParentRunId, s.SessionId, s.WorkspaceId, s.StartedAt.ToUnixTimeMilliseconds()))],
            Rev = index.Rev,
        };

    // ───────────────────────── Run 事件 ─────────────────────────

    public static ErrorDto ToDto(AgentError e) => new(
        e.Code.ToString().ToLowerInvariant(), e.Message, e.Retryable, e.Status, e.RetryAfterMs, e.MessageKey,
        e.MessageParams is null ? null : new Dictionary<string, string>(e.MessageParams, StringComparer.Ordinal));

    public static UsageDto ToDto(TokenUsage u) => new(
        u.InputTokens, u.OutputTokens, u.CacheCreationInputTokens, u.CacheReadInputTokens, u.CacheCreation1hInputTokens, u.ReasoningTokens);

    public static CostDto ToDto(CostInfo c) => c.State switch
    {
        CostState.Known => new CostDto("known", c.Micros, c.Currency == Currency.Cny ? "cny" : "usd"),
        CostState.Unknown => new CostDto("unknown"),
        _ => new CostDto("notMetered"),
    };

    private static string StopName(StopReason stop) => stop switch
    {
        StopReason.EndTurn => "endTurn",
        StopReason.ToolUse => "toolUse",
        StopReason.MaxTokens => "maxTokens",
        StopReason.StopSequence => "stopSequence",
        StopReason.Refusal => "refusal",
        _ => "aborted",
    };

    public static string StatusName(RunStatus status) => status switch
    {
        RunStatus.Done => RunStatusNames.Done,
        RunStatus.Aborted => RunStatusNames.Aborted,
        _ => RunStatusNames.Error,
    };

    public static string StateName(RunState state) => state switch
    {
        RunState.Running => RunStatusNames.Running,
        RunState.Done => RunStatusNames.Done,
        RunState.Aborted => RunStatusNames.Aborted,
        _ => RunStatusNames.Error,
    };

    private static string? Raw(JsonElement? element) =>
        element is { ValueKind: not JsonValueKind.Undefined } e ? e.GetRawText() : null;

    private static string Raw(JsonElement element) => element.ValueKind == JsonValueKind.Undefined ? "null" : element.GetRawText();

    /// <summary>Agent 事件 → 线上事件。没有对应线上类型的事件(目前没有)返回 null,调用方跳过它。</summary>
    public static RunEventDto? ToDto(AgentEvent ev) => ev switch
    {
        RunStartEvent e => new RunStartEventDto(e.RunId.Value, e.SessionId, e.Model.Alias, e.Model.ProviderId),
        TurnStartEvent e => new TurnStartEventDto(e.TurnIndex),
        MessageStartEvent e => new MessageStartEventDto(e.MessageId.Value, e.Role == Ncw.Agent.Llm.Role.User ? "user" : "assistant", e.Model, e.ProviderId),
        MessageDeltaEvent e => new MessageDeltaEventDto(
            e.MessageId.Value, e.ContentIndex, e.Kind switch { MessageDeltaKind.Thinking => "thinking", MessageDeltaKind.ToolArgs => "toolArgs", _ => "text" },
            e.Delta, e.CallId, e.ToolName),
        MessageEndEvent e => new MessageEndEventDto(ToDto(e.Message), e.Usage is { } usage ? ToDto(usage) : null, e.Cost.State == CostState.NotMetered ? null : ToDto(e.Cost), e.Stop is { } stop ? StopName(stop) : null),
        MessageDiscardEvent e => new MessageDiscardEventDto(e.MessageId.Value),
        ProviderRetryEvent e => new ProviderRetryEventDto(e.Attempt, e.DelayMs, e.Reason),
        ProviderSwitchEvent e => new ProviderSwitchEventDto(e.From, e.To, e.Reason),
        ToolStartEvent e => new ToolStartEventDto(e.CallId, e.ToolName, Raw(e.Input), e.Environment is { } tag ? new EnvRefDto(tag.DeviceId, tag.EnvironmentId, "", "") : null),
        ToolProgressEvent e => new ToolProgressEventDto(e.CallId, e.Progress.Message, e.Progress.Fraction, Raw(e.Progress.Details)),
        ToolEndEvent e => new ToolEndEventDto(
            e.CallId, e.Output.Content, e.Output.Images is { Count: > 0 } images ? [.. images.Select(static i => new ToolImageDto(i.Mime, i.DataRef))] : null,
            e.Output.Truncated, e.Output.OriginalBytes, Raw(e.Output.Details), e.IsError, e.DurationMs),
        NotificationEvent e => new NotificationEventDto(e.Key, e.Detail),
        TurnEndEvent e => new TurnEndEventDto(e.TurnIndex),
        RunEndEvent e => new RunEndEventDto(StatusName(e.Status), e.Error is { } error ? ToDto(error) : null),
        ContextUsageEvent e => new ContextUsageEventDto(e.Used, e.Window, e.ShouldCompact, [.. e.Segments.Select(static s => new ContextSegmentDto(s.Kind, s.Tokens))]),
        ContextStatusEvent e => new ContextStatusEventDto(e.Phase.ToString().ToLowerInvariant(), e.Trigger),
        InteractionRequestEvent e => new InteractionRequestEventDto(ToDto(e.Interaction)),
        InteractionResolvedEvent e => new InteractionResolvedEventDto(ToDto(e.Resolution), e.Response is { } response ? ToDto(response) : null),
        RunSettledEvent e => new RunSettledEventDto(StatusName(e.Status)),
        SubagentStartEvent e => OrchestrationMapping.ToDto(e),
        SubagentUpdateEvent e => OrchestrationMapping.ToDto(e),
        SubagentEndEvent e => OrchestrationMapping.ToDto(e),
        _ => null,
    };

    /// <summary>事件帧的 <c>type</c>:多态鉴别名(和 <see cref="RunEventDto"/> 的 <c>DerivedTypeShape</c> 名字一致,控制端不解码就能按类型过滤)。</summary>
    public static string TypeName(RunEventDto ev) => ev switch
    {
        RunStartEventDto => "run_start",
        TurnStartEventDto => "turn_start",
        MessageStartEventDto => "message_start",
        MessageDeltaEventDto => "message_delta",
        MessageEndEventDto => "message_end",
        MessageDiscardEventDto => "message_discard",
        ProviderRetryEventDto => "provider_retry",
        ProviderSwitchEventDto => "provider_switch",
        ToolStartEventDto => "tool_start",
        ToolProgressEventDto => "tool_progress",
        ToolEndEventDto => "tool_end",
        NotificationEventDto => "notification",
        TurnEndEventDto => "turn_end",
        RunEndEventDto => "run_end",
        ContextUsageEventDto => "context_usage",
        ContextStatusEventDto => "context_status",
        InteractionRequestEventDto => "interaction_request",
        InteractionResolvedEventDto => "interaction_resolved",
        RunSettledEventDto => "run_settled",
        SubagentStartEventDto => "subagent_start",
        SubagentUpdateEventDto => "subagent_update",
        SubagentEndEventDto => "subagent_end",
        _ => "unknown",
    };

    // ───────────────────────── 交互 ─────────────────────────

    public static InteractionDto ToDto(InteractionRequest request)
    {
        var id = request.Id.Value;
        var run = request.RunId.Value;
        var created = request.CreatedAt.ToUnixTimeMilliseconds();
        switch (request)
        {
            case ToolPermission p:
                return new ToolPermissionInteractionDto
                {
                    Id = id, RunId = run, CreatedAt = created, Version = request.Version,
                    CallId = p.CallId, ToolName = p.ToolName, InternalId = p.InternalId, InputJson = Raw(p.Input),
                    ReadOnly = p.Traits.ReadOnly, Destructive = p.Traits.Destructive, NeedsNetwork = p.Traits.NeedsNetwork,
                    Env = new EnvRefDto(p.Env.DeviceId, p.Env.Environment, p.Env.Name, p.Env.Type),
                    SuggestedRule = p.SuggestedRule,
                    AllowedScopes = Scopes(p.AllowedScopes),
                    Risk = p.Risk switch { CommandRisk.ReadOnly => "readOnly", CommandRisk.Dangerous => "dangerous", CommandRisk.Unknown => "unknown", _ => null },
                    OutsideWorkspace = p.OutsideWorkspace,
                };
            case AskUser a:
                return new AskUserInteractionDto
                {
                    Id = id, RunId = run, CreatedAt = created, Version = request.Version,
                    Questions = [.. a.Questions.Select(static q => new AskUserQuestionDto(
                        q.Header, q.Question, [.. q.Options.Select(static o => new AskUserOptionDto(o.Label, o.Description))], q.MultiSelect, q.AllowFreeform))],
                };
            case PlanApproval p:
                return new PlanApprovalInteractionDto { Id = id, RunId = run, CreatedAt = created, Version = request.Version, Plan = p.Plan, PlanId = p.PlanId, Path = p.Path };
            case GoalProposal g:
                return new GoalProposalInteractionDto { Id = id, RunId = run, CreatedAt = created, Version = request.Version, SessionId = g.SessionId, Condition = g.Condition };
            case WorkflowApproval w:
                return new WorkflowApprovalInteractionDto { Id = id, RunId = run, CreatedAt = created, Version = request.Version, CallId = w.CallId, WorkflowJson = Raw(w.Workflow) };
            case WidgetSubmit w:
                return new WidgetSubmitInteractionDto { Id = id, RunId = run, CreatedAt = created, Version = request.Version, CallId = w.CallId, Title = w.Title };
            case SshHostKey h:
                return new SshHostKeyInteractionDto
                {
                    Id = id, RunId = run, CreatedAt = created, Version = request.Version,
                    Env = new EnvRefDto(h.Env.DeviceId, h.Env.Environment, h.Env.Name, h.Env.Type),
                    HostName = h.HostName, Algorithm = h.Algorithm, Fingerprint = h.Fingerprint,
                };
            case SshCredential c:
                return new SshCredentialInteractionDto
                {
                    Id = id, RunId = run, CreatedAt = created, Version = request.Version,
                    Env = new EnvRefDto(c.Env.DeviceId, c.Env.Environment, c.Env.Name, c.Env.Type),
                    PromptKind = c.PromptKind switch
                    {
                        SshCredentialPromptKind.Password => "password",
                        SshCredentialPromptKind.KeyPassphrase => "keyPassphrase",
                        _ => "keyboardInteractive",
                    },
                    HostName = c.HostName, UserName = c.UserName, KeyName = c.KeyName, Instruction = c.Instruction,
                    Prompts = [.. (c.Prompts ?? []).Select(static p => new SshPromptDto(p.Text, p.Echo))],
                    PreviousRejected = c.PreviousRejected, CanRemember = c.CanRemember, HasSaved = c.HasSaved,
                };
            default:
                throw new InvalidOperationException($"Unknown interaction kind '{request.Kind}'.");
        }
    }

    private static string[] Scopes(RememberScopes scopes)
    {
        var result = new List<string>(3);
        if (scopes.HasFlag(RememberScopes.Session))
        {
            result.Add("session");
        }

        if (scopes.HasFlag(RememberScopes.Project))
        {
            result.Add("project");
        }

        if (scopes.HasFlag(RememberScopes.Global))
        {
            result.Add("global");
        }

        return [.. result];
    }

    public static InteractionsDto ToDto(InteractionsSnapshot snapshot) => new(snapshot.Revision, [.. snapshot.Pending.Select(static p => ToDto(p))]);

    public static ResolutionDto ToDto(InteractionResolution r) => new(
        r.Id.Value, r.RunId.Value, r.Kind, r.Version, r.Status == ResolutionStatus.Answered ? "answered" : "aborted", r.ResolvedBy, r.At.ToUnixTimeMilliseconds(), r.Revision);

    /// <summary>回答 → DTO(<c>interaction_resolved</c> 事件带着它,别的控制端据此看到答案)。</summary>
    public static InteractionResponseDto ToDto(InteractionResponse response) => response switch
    {
        ToolPermissionResponse r => r.Choice switch
        {
            PermissionChoice.AllowAlwaysChoice always => new ToolPermissionResponseDto(PermissionChoices.AllowAlways, always.Scope.ToString().ToLowerInvariant()),
            PermissionChoice.AllowEditedChoice edited => new ToolPermissionResponseDto(PermissionChoices.AllowEdited, null, Raw(edited.Input)),
            PermissionChoice.DenyChoice deny => new ToolPermissionResponseDto(PermissionChoices.Deny, null, null, deny.Reason),
            _ => new ToolPermissionResponseDto(PermissionChoices.AllowOnce),
        },
        AskUserResponse r => new AskUserResponseDto(r.Answers is null ? null : [.. r.Answers.Select(static a => new AskUserAnswerDto([.. a]))]),
        PlanApprovalResponse r => new PlanApprovalResponseDto(r.Action switch
        {
            PlanApprovalAction.ApproveNewSession => "approveNewSession",
            PlanApprovalAction.RequestRevision => "requestRevision",
            PlanApprovalAction.Reject => "reject",
            _ => "approveCurrent",
        }, r.Feedback),
        GoalProposalResponse r => new GoalProposalResponseDto(r.Approved),
        WorkflowApprovalResponse r => new WorkflowApprovalResponseDto(r.Action switch
        {
            WorkflowApprovalAction.Revise => "revise",
            WorkflowApprovalAction.Cancel => "cancel",
            _ => "run",
        }, r.Feedback),
        WidgetSubmitResponse r => new WidgetSubmitResponseDto(r.Value),
        SshHostKeyResponse r => new SshHostKeyResponseDto(r.Trust),
        // ★ 凭据的回答有秘密:交互闸门对 RedactsResponse 的请求从不把回答放进事件,所以这里不该被调用到——调用到就是 bug,宁可抛也不把口令映射出去
        SshCredentialResponse => throw new InvalidOperationException("An SSH credential response must never be mapped to a DTO."),
        _ => throw new InvalidOperationException($"Unknown interaction response kind '{response.Kind}'."),
    };

    /// <summary>DTO → 回答。格式不对(未知选项、坏 JSON)抛 <c>invalid_request</c>;内容是否合法由交互闸门的校验再判一次(失败同样 <c>invalid_request</c>,且不消耗这次交互)。</summary>
    public static InteractionResponse ToResponse(InteractionResponseDto dto)
    {
        static ProtocolException Bad(string message) => new(ErrorCodes.InvalidRequest, message);

        switch (dto)
        {
            case ToolPermissionResponseDto r:
                return new ToolPermissionResponse(r.Choice switch
                {
                    PermissionChoices.AllowOnce => PermissionChoice.AllowOnce,
                    PermissionChoices.AllowAlways => PermissionChoice.AllowAlways(r.Scope switch
                    {
                        "session" => RememberScope.Session,
                        "project" => RememberScope.Project,
                        "global" => RememberScope.Global,
                        _ => throw Bad("allowAlways needs a scope: session, project or global."),
                    }),
                    PermissionChoices.AllowEdited => PermissionChoice.AllowEdited(ParseObject(r.EditedInputJson)),
                    PermissionChoices.Deny => PermissionChoice.Deny(r.Reason),
                    _ => throw Bad($"Unknown permission choice '{r.Choice}'."),
                });
            case AskUserResponseDto r:
                return new AskUserResponse(r.Answers is null ? null : [.. r.Answers.Select(static a => (IReadOnlyList<string>)[.. a.Values])]);
            case PlanApprovalResponseDto r:
                return new PlanApprovalResponse(r.Action switch
                {
                    "approveCurrent" => PlanApprovalAction.ApproveCurrent,
                    "approveNewSession" => PlanApprovalAction.ApproveNewSession,
                    "requestRevision" => PlanApprovalAction.RequestRevision,
                    "reject" => PlanApprovalAction.Reject,
                    _ => throw Bad($"Unknown plan action '{r.Action}'."),
                }, r.Feedback);
            case GoalProposalResponseDto r:
                return new GoalProposalResponse(r.Approved);
            case WorkflowApprovalResponseDto r:
                return new WorkflowApprovalResponse(r.Action switch
                {
                    "run" => WorkflowApprovalAction.Run,
                    "revise" => WorkflowApprovalAction.Revise,
                    "cancel" => WorkflowApprovalAction.Cancel,
                    _ => throw Bad($"Unknown workflow action '{r.Action}'."),
                }, r.Feedback);
            case WidgetSubmitResponseDto r:
                return new WidgetSubmitResponse(r.Value);
            case SshHostKeyResponseDto r:
                return new SshHostKeyResponse(r.Trust);
            case SshCredentialResponseDto r:
                return new SshCredentialResponse(r.Answers is null ? null : [.. r.Answers], r.Remember);
            default:
                throw Bad("Unknown interaction response.");
        }
    }

    private static JsonElement ParseObject(string? json)
    {
        if (json is null)
        {
            throw new ProtocolException(ErrorCodes.InvalidRequest, "allowEdited needs the edited input as JSON.");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ProtocolException(ErrorCodes.InvalidRequest, "The edited input is not valid JSON.");
        }
    }

    // ───────────────────────── 工具 ─────────────────────────

    public static ToolInfoDto ToDto(IAgentTool tool) => new(
        tool.Info.InternalId, tool.Info.Description, tool.Info.Traits.ReadOnly, tool.Info.Traits.Destructive, tool.Info.Traits.NeedsNetwork, Raw(tool.Info.InputSchema));
}
