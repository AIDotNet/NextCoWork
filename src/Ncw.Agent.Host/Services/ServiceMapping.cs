using Ncw.Agent.Services.Attachments;
using Ncw.Agent.Services.Schedules;
using Ncw.Agent.Services.Settings;
using Ncw.Agent.Services.Widgets;
using Ncw.Agent.Storage;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Services;

/// <summary>
/// 杂项服务的领域类型 ↔ 协议 DTO(Host 是唯一把领域层和 <c>Ncw.Protocol</c> 接起来的 Agent 层)和业务失败 → 协议错误码。
/// 业务失败的结构化原因放在 <c>error.details</c>(<see cref="ProviderErrorDto"/>:稳定的 key + 参数,控制端翻译),错误码是粗粒度的。
/// </summary>
internal static class ServiceMapping
{
    // ───────────────────────── 错误 ─────────────────────────

    public static ProtocolException Error(string code, string key, string message, bool retryable = false, params (string Name, string Value)[] parameters)
    {
        var details = new ProviderErrorDto(key, parameters.ToDictionary(static p => p.Name, static p => p.Value, StringComparer.Ordinal));
        return new ProtocolException(code, message, retryable, FrameCodec.EncodePayload(details, PayloadShapes.GetShape<ProviderErrorDto>()));
    }

    public static ProtocolException Invalid(string key, string message, params (string Name, string Value)[] parameters) =>
        Error(ErrorCodes.InvalidRequest, key, message, false, parameters);

    /// <summary>认识的业务 / 存储失败转成协议错误;其它(含 <see cref="ProtocolException"/> 本身、取消)返回 null,原样往上抛。</summary>
    public static ProtocolException? ToProtocolError(Exception e) => e switch
    {
        ProtocolException => null,
        SettingsException s => Error(ErrorCodes.InvalidRequest, s.Key, s.Message, false, ("field", s.Field)),
        ScheduleException s => Error(
            s.Kind switch { ScheduleFailure.NotFound => ErrorCodes.NotFound, ScheduleFailure.Conflict => ErrorCodes.Conflict, _ => ErrorCodes.InvalidRequest }, s.Key, s.Message),
        AttachmentException a => Error(
            a.Kind switch { AttachmentFailure.NotFound => ErrorCodes.NotFound, AttachmentFailure.TooLarge => ErrorCodes.TooLarge, AttachmentFailure.Conflict => ErrorCodes.Conflict, _ => ErrorCodes.InvalidRequest },
            a.Key, a.Message, false, [.. a.Params.Select(static p => (p.Key, p.Value))]),
        WidgetException w => Error(w.Kind == WidgetFailure.TooLarge ? ErrorCodes.TooLarge : ErrorCodes.InvalidRequest, w.Key, w.Message),
        System.Security.Cryptography.CryptographicException => new ProtocolException(ErrorCodes.Unavailable, "The credential store could not be read.", retryable: false),
        StorageException s => new ProtocolException(ErrorCodes.Unavailable, s.Message, retryable: true),
        Microsoft.Data.Sqlite.SqliteException s => new ProtocolException(ErrorCodes.Unavailable, s.Message, retryable: true),
        _ => null,
    };

    public static ValueTask<T> Guard<T>(Func<T> work)
    {
        try
        {
            return ValueTask.FromResult(work());
        }
        catch (Exception e) when (ToProtocolError(e) is { } converted)
        {
            throw converted;
        }
    }

    public static async ValueTask<T> GuardAsync<T>(Func<Task<T>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (Exception e) when (ToProtocolError(e) is { } converted)
        {
            throw converted;
        }
    }

    // ───────────────────────── 设置 ─────────────────────────

    public static ModelPickDto ToDto(ModelPick p) => new(p.Model, p.IsSet ? p.ProviderId : null);

    public static ModelPick FromDto(ModelPickDto p) => new(p.Model, string.IsNullOrEmpty(p.ProviderId) ? null : p.ProviderId);

    public static DeviceSettingsDto ToDto(DeviceSettings s, bool hasProxyPassword) => new(
        ToDto(s.DefaultModel), ToDto(s.PermissionReviewer), ToDto(s.GoalEvaluator), ToDto(s.Compact), s.CompactThinking,
        ToDto(s.Completion), ToDto(s.InlineCompletionModel), ToDto(s.Image),
        s.DefaultPermissionMode, s.ModelProposedGoals, s.AutoCompact, s.MaxOutputTokens,
        s.PromptSuggestions, s.LocalCompletion, s.InlineCompletion, s.ImageGeneration,
        s.Shell, s.GoalCheckinMinutes, s.UpstreamIdleTimeoutSeconds, s.AccountRotation, s.Failover, s.BuiltinSearxngUrl,
        new SubagentSettingsDto(ToDto(s.Subagent.Model), s.Subagent.Thinking, s.Subagent.PerSessionLimit, s.Subagent.GlobalLimit),
        new ProxySettingsDto(s.Proxy.Enabled, s.Proxy.Mode, s.Proxy.Scheme, s.Proxy.Host, s.Proxy.Port, s.Proxy.AuthEnabled, s.Proxy.AuthUser, s.Proxy.Bypass, hasProxyPassword),
        new GatewaySettingsDto(s.Gateway.Enabled, s.Gateway.AllowRemote, s.Gateway.PreferredPort),
        new DataSettingsDto(s.Data.BackupDirectory, s.Data.BackupFrequency),
        new PersonalizationSettingsDto(s.Personalization.Name, s.Personalization.Background, s.Personalization.Instructions));

    public static DeviceSettingsPatch FromDto(DeviceSettingsPatchDto p) => new()
    {
        DefaultModel = p.DefaultModel is null ? null : FromDto(p.DefaultModel),
        PermissionReviewer = p.PermissionReviewer is null ? null : FromDto(p.PermissionReviewer),
        GoalEvaluator = p.GoalEvaluator is null ? null : FromDto(p.GoalEvaluator),
        Compact = p.Compact is null ? null : FromDto(p.Compact),
        CompactThinking = p.CompactThinking,
        Completion = p.Completion is null ? null : FromDto(p.Completion),
        InlineCompletionModel = p.InlineCompletionModel is null ? null : FromDto(p.InlineCompletionModel),
        Image = p.Image is null ? null : FromDto(p.Image),
        DefaultPermissionMode = p.DefaultPermissionMode,
        ModelProposedGoals = p.ModelProposedGoals,
        AutoCompact = p.AutoCompact,
        MaxOutputTokens = p.MaxOutputTokens,
        PromptSuggestions = p.PromptSuggestions,
        LocalCompletion = p.LocalCompletion,
        InlineCompletion = p.InlineCompletion,
        ImageGeneration = p.ImageGeneration,
        Shell = p.Shell,
        GoalCheckinMinutes = p.GoalCheckinMinutes,
        UpstreamIdleTimeoutSeconds = p.UpstreamIdleTimeoutSeconds,
        AccountRotation = p.AccountRotation,
        Failover = p.Failover,
        BuiltinSearxngUrl = p.BuiltinSearxngUrl,
        Subagent = p.Subagent is null ? null : new SubagentSettingsPatch(p.Subagent.Model is null ? null : FromDto(p.Subagent.Model), p.Subagent.Thinking, p.Subagent.PerSessionLimit, p.Subagent.GlobalLimit),
        Proxy = p.Proxy is null ? null : new ProxySettingsPatch(p.Proxy.Enabled, p.Proxy.Mode, p.Proxy.Scheme, p.Proxy.Host, p.Proxy.Port, p.Proxy.AuthEnabled, p.Proxy.AuthUser, p.Proxy.Bypass),
        Gateway = p.Gateway is null ? null : new GatewaySettingsPatch(p.Gateway.Enabled, p.Gateway.AllowRemote, p.Gateway.PreferredPort),
        Data = p.Data is null ? null : new DataSettingsPatch(p.Data.BackupDirectory, p.Data.BackupFrequency),
        Personalization = p.Personalization is null ? null : new PersonalizationPatch(p.Personalization.Name, p.Personalization.Background, p.Personalization.Instructions),
    };

    public static ProjectSettingsDto ToDto(ProjectSettings s) => new(
        s.WorkspaceId, s.PermissionMode, ToDto(s.DefaultModel), s.DefaultMode, s.DefaultThinking, s.WebSearch, s.MaxContext,
        s.CompactModel is null ? null : ToDto(s.CompactModel), s.CompactThinking);

    public static ProjectSettingsPatch FromDto(ProjectSettingsPatchDto p) => new()
    {
        PermissionMode = p.PermissionMode,
        DefaultModel = p.DefaultModel is null ? null : FromDto(p.DefaultModel),
        DefaultMode = p.DefaultMode,
        DefaultThinking = p.DefaultThinking,
        WebSearch = p.WebSearch,
        MaxContext = p.MaxContext,
        CompactModel = p.CompactModel is null ? null : FromDto(p.CompactModel),
        CompactThinking = p.CompactThinking,
        ClearCompactModel = p.ClearCompactModel,
        ClearCompactThinking = p.ClearCompactThinking,
    };

    // ───────────────────────── 用量 ─────────────────────────

    public static UsageWindow FromDto(UsageWindowDto w) => new(w.From, w.To);

    public static UsageCostTotalDto ToDto(UsageCostTotal c) => new(c.Currency, c.Micros);

    public static UsageSummaryDto ToDto(UsageSummary s) => new(
        s.RequestCount, s.SuccessCount, s.FailedCount, s.InputTokens, s.OutputTokens, s.CacheReadTokens, s.CacheWriteTokens, s.CacheWrite1hTokens, s.ThinkingTokens,
        s.EstimatedThinkingRequestCount, s.TotalTokens, s.CacheHitRate, s.AverageLatencyMs, s.AverageTimeToFirstTokenMs, s.ToolCalls, s.ToolErrors, [.. s.Costs.Select(ToDto)]);

    public static UsageAttemptDto ToDto(UsageRow r) => new(
        r.Id, r.At, r.RunId, r.WorkspaceId, r.SessionId, r.Attempt, r.ProviderId, r.ProviderName, r.Protocol, r.Endpoint, r.Alias, r.UpstreamModel, r.ResponseModel,
        r.InputTokens, r.OutputTokens, r.CacheReadTokens, r.CacheWriteTokens, r.CacheWrite1hTokens, r.ThinkingTokens, r.ThinkingTokensEstimated, r.LatencyMs, r.TimeToFirstTokenMs,
        r.Ok, r.HttpStatus, r.ErrorKind, r.ErrorMessage, r.StopReason, r.CostMicros, r.Currency, r.PricingTier, r.PricingWindow, r.ToolCalls, r.ToolErrors);

    public static UsageDimensionStatDto ToDto(UsageDimensionStat s) => new(
        s.Id, s.Label, s.RequestCount, s.SuccessCount, s.InputTokens, s.OutputTokens, s.CacheReadTokens, s.CacheWriteTokens, s.ThinkingTokens,
        s.AverageLatencyMs, s.AverageTimeToFirstTokenMs, s.ToolCalls, s.ToolErrors, [.. s.Costs.Select(ToDto)]);

    public static UsageDailyBucketDto ToDto(UsageDailyBucket b) => new(
        b.Day, b.ProviderId, b.ProviderName, b.UpstreamModel, b.Alias, b.Currency, b.RequestCount, b.SuccessCount, b.InputTokens, b.OutputTokens, b.CacheReadTokens,
        b.CacheWriteTokens, b.CacheWrite1hTokens, b.ThinkingTokens, b.CostMicros, b.PricedCount, b.LatencySum, b.TtftSum, b.TtftCount);

    public static UsageActivityStatsDto ToDto(UsageActivityStats s) => new(
        [.. s.ActiveDays], s.CurrentStreak, s.LongestStreak, s.PeakDayTokens, s.PeakDay, s.LongestChatMs, s.SyncedDevices);

    // ───────────────────────── 定时任务 ─────────────────────────

    public static ScheduleRuleDto ToDto(ScheduleRule r) => new(r.Kind, r.At, r.Time, r.Weekdays is null ? null : [.. r.Weekdays]);

    public static ScheduleRule FromDto(ScheduleRuleDto r) => new(r.Kind, r.At, r.Time, r.Weekdays is null ? null : [.. r.Weekdays]);

    public static RepeatWindowDto ToDto(RepeatWindow r) => new(r.Enabled, r.EndTime, r.IntervalMinutes);

    public static RepeatWindow FromDto(RepeatWindowDto r) => new(r.Enabled, r.EndTime, r.IntervalMinutes);

    public static ScheduledTaskDto ToDto(ScheduledTask t) => new(
        t.Id, t.Name, t.Prompt, t.WorkspaceId, t.Model, t.ModelProviderId, ToDto(t.Schedule), t.Timezone, ToDto(t.RepeatWindow), t.Enabled, t.NextRunAt,
        t.CreatedAt, t.UpdatedAt, t.CreatedBy, t.ChainDepth);

    public static ScheduledRunDto ToDto(ScheduledRunRow r) => new(r.Id, r.TaskId, r.SessionId, r.Trigger, r.Status, r.ScheduledAt, r.StartedAt, r.EndedAt, r.Summary, r.Error);

    public static ScheduledTaskInput FromDto(ScheduledTaskInputDto d) => new(
        d.Name, d.Prompt, d.WorkspaceId, d.Model, FromDto(d.Schedule), d.ModelProviderId, d.Timezone, d.RepeatWindow is null ? null : FromDto(d.RepeatWindow), d.Enabled);

    public static ScheduledTaskPatch FromDto(ScheduledTaskPatchDto d) => new()
    {
        Name = d.Name,
        Prompt = d.Prompt,
        Model = d.Model,
        ModelProviderId = d.ModelProviderId,
        Schedule = d.Schedule is null ? null : FromDto(d.Schedule),
        Timezone = d.Timezone,
        RepeatWindow = d.RepeatWindow is null ? null : FromDto(d.RepeatWindow),
        Enabled = d.Enabled,
    };
}
