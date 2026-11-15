using Ncw.Agent.Storage;

namespace Ncw.Agent.Services.Schedules;

/// <summary>只对<b>模型</b>生效的几条上限(用户在界面里不受它们约束):站在界面上每一次创建背后的是一个人,而站在工具那一侧的是一个会把「每分钟检查一次」当成合理安排的模型。</summary>
public static class ScheduledAgentLimits
{
    /// <summary>每个项目模型能建到的上限。到顶之后创建直接拒,并让模型去删旧的。</summary>
    public const int MaxTasksPerWorkspace = 20;

    /// <summary>链式排程的最大层数(用户手建 = 0)。没有这个数,一个每天跑的任务可以在每次运行时再建一个任务,新任务又接着建:指数增长,在用户打开面板之前没有任何症状。</summary>
    public const int MaxChainDepth = 2;

    /// <summary>窗口内重复的最小间隔(领域层允许 1 分钟,那是给界面上的人用的)。</summary>
    public const int MinIntervalMinutes = 5;

    public const int MaxNameLength = 60;
    public const int MaxPromptLength = 4000;
}

/// <summary>
/// 交给模型看的一条任务。★ 时间一律是<b>已经格式化好的本地时间字符串</b>:模型拿到 epoch 毫秒只会按 UTC 复述给用户,而任务是按 <see cref="Timezone"/> 跑的——
/// 那种错说出来一字不差地像真的(「已设为明早 9 点」),用户要到第二天才发现不对。
/// </summary>
public sealed record ScheduledTaskSummary(
    string Id,
    string Name,
    string Prompt,
    string Schedule,
    string Timezone,
    bool Enabled,
    string Model,
    string? ModelProviderId,
    string? NextRunAt,
    string CreatedBy);

public sealed record AgentScheduleCreate(
    string Name,
    string Prompt,
    ScheduleRule Schedule,
    string? Timezone = null,
    RepeatWindow? RepeatWindow = null,
    string? Model = null,
    string? ModelProviderId = null,
    bool? Enabled = null);

/// <summary>人话描述:模型复述给用户时逐字用它。</summary>
public static class ScheduleDescriptions
{
    private static readonly string[] Weekdays = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    /// <summary><c>1714032000000</c> → <c>2024-04-25 09:00</c>(按给定时区;时区非法时回退到 ISO)。</summary>
    public static string FormatInstant(long at, string timezone)
    {
        if (ScheduleCalculator.FindZone(timezone) is not { } zone)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(at).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        }

        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(at), zone);
        return $"{local.Year:D4}-{local.Month:D2}-{local.Day:D2} {local.Hour:D2}:{local.Minute:D2}";
    }

    public static string Describe(ScheduleRule rule, string timezone, RepeatWindow? repeat = null)
    {
        var baseText = rule.Kind switch
        {
            ScheduleKinds.Once => $"once at {rule.At?.Replace('T', ' ')}",
            ScheduleKinds.Daily => $"every day at {rule.Time}",
            _ => $"every {string.Join(", ", (rule.Weekdays ?? []).Select(d => d is >= 0 and <= 6 ? Weekdays[d] : d.ToString(System.Globalization.CultureInfo.InvariantCulture)))} at {rule.Time}",
        };
        var repeatText = repeat is { Enabled: true, EndTime: not null }
            ? $", then every {repeat.IntervalMinutes ?? 60} minutes until {repeat.EndTime}"
            : "";
        return $"{baseText}{repeatText} ({timezone})";
    }

    public static ScheduledTaskSummary Summarize(ScheduledTask t) => new(
        t.Id, t.Name, t.Prompt, Describe(t.Schedule, t.Timezone, t.RepeatWindow), t.Timezone, t.Enabled, t.Model, t.ModelProviderId,
        t.NextRunAt is { } next ? FormatInstant(next, t.Timezone) : null, t.CreatedBy);
}

/// <summary>
/// Agent 侧(模型的定时任务工具)的那道窄缝(2.x 的 <c>schedulingBridgeFor</c>)。写入和用户共用 <see cref="ScheduledTaskService"/>;这里管的是「这件事在当前这台机器上成不成立」:
/// <list type="number">
/// <item>任务一律落在<b>当前会话的项目</b>,模型说了不算;跨项目的改 / 删当成「不存在」。</item>
/// <item>链深度到顶就拒(定时任务运行中再建任务是允许的,但不能无限延伸)。</item>
/// <item>每个项目的任务数、窗口内重复的最小间隔、名字 / 提示词长度另有只对模型生效的上限。</item>
/// <item>失败一律抛一个<b>模型读得懂的英文</b> <see cref="ScheduleException"/>(由工具转成工具失败),不返回 <c>{ ok: false }</c>:那会让每个调用点都写一遍分支,漏写的那处把失败当成成功报给模型。</item>
/// </list>
/// </summary>
public sealed class AgentScheduling(
    ScheduledTaskService tasks,
    ScheduledRepository repository,
    string workspaceId,
    string sessionId,
    string model,
    string? modelProviderId,
    Func<string, string?, bool> resolveModel)
{
    public IReadOnlyList<ScheduledTaskSummary> List() => [.. tasks.List(workspaceId).Select(ScheduleDescriptions.Summarize)];

    public ScheduledTaskSummary Create(AgentScheduleCreate request)
    {
        var existing = tasks.List(workspaceId);
        if (existing.Count >= ScheduledAgentLimits.MaxTasksPerWorkspace)
        {
            throw Invalid("schedules.agent.limit",
                $"This workspace already has {existing.Count} scheduled tasks, which is the limit ({ScheduledAgentLimits.MaxTasksPerWorkspace}). Delete one before creating another.");
        }

        var chainDepth = ChainDepth() + 1;
        if (chainDepth > ScheduledAgentLimits.MaxChainDepth)
        {
            throw Invalid("schedules.agent.chainDepth",
                $"This run is itself a scheduled task at chain depth {chainDepth - 1}, which is the limit ({ScheduledAgentLimits.MaxChainDepth}). " +
                "Do not schedule further follow-ups from here — report what you found and let the user schedule anything else.");
        }

        ValidateText(request.Name, request.Prompt);
        ValidateRepeat(request.RepeatWindow);
        var (m, p) = ModelFor(request.Model, request.ModelProviderId);
        try
        {
            var created = tasks.Create(new ScheduledTaskInput(
                request.Name, request.Prompt, workspaceId, m, request.Schedule, p, request.Timezone, request.RepeatWindow, request.Enabled, ScheduleCreators.Agent, chainDepth));
            return ScheduleDescriptions.Summarize(created);
        }
        catch (ScheduleException e) when (e.Kind == ScheduleFailure.NotFound)
        {
            throw Invalid(e.Key, "This workspace no longer exists, so scheduled tasks cannot be created in it.");
        }
    }

    public ScheduledTaskSummary Update(string id, ScheduledTaskPatch patch)
    {
        var current = Owned(id);
        ValidateText(patch.Name, patch.Prompt);
        ValidateRepeat(patch.RepeatWindow ?? (patch.Schedule is null ? null : current.RepeatWindow));
        (string Model, string? ProviderId)? model = patch.Model is null ? null : ModelFor(patch.Model, patch.ModelProviderId);
        return ScheduleDescriptions.Summarize(tasks.Update(id, patch with { Model = model?.Model, ModelProviderId = model?.ProviderId }));
    }

    public (string Id, string Name) Remove(string id)
    {
        var task = Owned(id);
        tasks.Delete(id);
        return (task.Id, task.Name);
    }

    /// <summary>当前会话所处的链深度:用户手建的任务 = 0,它运行时 Agent 建出来的 = 1,再下一层 = 2……普通聊天会话查不到对应的运行记录,从 0 起算。</summary>
    private int ChainDepth() =>
        repository.GetRunBySession(sessionId) is { } run && tasks.Get(run.TaskId) is { } parent ? parent.ChainDepth : 0;

    /// <summary>这条任务是不是<b>这个项目</b>的。跨项目改删一律当成「不存在」。</summary>
    private ScheduledTask Owned(string id) =>
        tasks.Get(id) is { } task && task.WorkspaceId == workspaceId
            ? task
            : throw new ScheduleException(ScheduleFailure.NotFound, "schedules.notFound",
                $"No scheduled task with id \"{id}\" exists in this workspace. Call ListScheduledTasks to get the current ids.");

    private (string Model, string? ProviderId) ModelFor(string? requested, string? requestedProvider)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return (model, modelProviderId);
        }

        if (!resolveModel(requested, requestedProvider))
        {
            throw Invalid("schedules.agent.model", $"No model alias \"{requested}\" is configured. Omit the model field to reuse this session's model ({model}).");
        }

        return (requested, requestedProvider);
    }

    private static void ValidateText(string? name, string? prompt)
    {
        if (name is not null && (name.Trim().Length == 0 || name.Length > ScheduledAgentLimits.MaxNameLength))
        {
            throw Invalid("schedules.invalid.name", $"name must be 1-{ScheduledAgentLimits.MaxNameLength} characters.");
        }

        if (prompt is not null && (prompt.Trim().Length == 0 || prompt.Length > ScheduledAgentLimits.MaxPromptLength))
        {
            throw Invalid("schedules.invalid.prompt", $"prompt must be 1-{ScheduledAgentLimits.MaxPromptLength} characters.");
        }
    }

    private static void ValidateRepeat(RepeatWindow? repeat)
    {
        if (repeat is not { Enabled: true })
        {
            return;
        }

        if (ScheduleCalculator.ParseTime(repeat.EndTime) is null)
        {
            throw Invalid("schedules.invalid.repeat", "repeat_window.end_time is required (HH:MM) when the repeat window is enabled.");
        }

        if ((repeat.IntervalMinutes ?? 60) < ScheduledAgentLimits.MinIntervalMinutes)
        {
            throw Invalid("schedules.agent.interval",
                $"repeat_window.interval_minutes must be at least {ScheduledAgentLimits.MinIntervalMinutes} — a shorter interval starts a full agent run that often, which burns the user's tokens.");
        }
    }

    private static ScheduleException Invalid(string key, string message) => new(ScheduleFailure.Invalid, key, message);
}
