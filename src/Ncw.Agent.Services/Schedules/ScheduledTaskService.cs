using System.Text.Json.Nodes;
using Ncw.Agent.Storage;
using Ncw.Core.Ids;

namespace Ncw.Agent.Services.Schedules;

public static class ScheduleCreators
{
    public const string User = "user";
    public const string Agent = "agent";
}

/// <summary>一条定时任务(2.x 的 <c>ScheduledTask</c>)。<see cref="NextRunAt"/> 是下一次触发(UTC 毫秒),停用或规则已过期时为 null。</summary>
public sealed record ScheduledTask(
    string Id,
    string Name,
    string Prompt,
    string WorkspaceId,
    string Model,
    string? ModelProviderId,
    ScheduleRule Schedule,
    string Timezone,
    RepeatWindow RepeatWindow,
    bool Enabled,
    long? NextRunAt,
    long CreatedAt,
    long UpdatedAt,
    string CreatedBy,
    int ChainDepth);

public static class ScheduledRunStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Success = "success";
    public const string Error = "error";
    public const string Skipped = "skipped";
    public const string Aborted = "aborted";

    public static bool IsActive(string status) => status is Queued or Running;
}

public static class ScheduledRunTriggers
{
    public const string Scheduled = "scheduled";
    public const string Manual = "manual";
}

/// <summary>稳定的 <c>scheduled.*</c> 错误 key(运行记录的 <c>error</c> 字段,控制端翻译;和 2.x 同款)。</summary>
public static class ScheduledErrors
{
    /// <summary>到点时上一次还没跑完 / 设备当时不在:记一条 skipped,不补跑(错过补跑策略沿用 2.x:启动时只对账,不回头执行)。</summary>
    public const string Missed = "scheduled.missed";

    public const string WorkspaceUnavailable = "scheduled.workspaceUnavailable";
    public const string ExecutionFailed = "scheduled.executionFailed";
    public const string ExecutionIncomplete = "scheduled.executionIncomplete";
    public const string ModelUnavailable = "scheduled.modelUnavailable";
}

/// <summary>定时任务的业务失败。<see cref="Key"/> 是稳定的 <c>schedules.*</c> key(控制端翻译),<see cref="Kind"/> 决定映射成哪种协议错误码。</summary>
public sealed class ScheduleException(ScheduleFailure kind, string key, string message) : Exception(message)
{
    public ScheduleFailure Kind { get; } = kind;

    public string Key { get; } = key;
}

public enum ScheduleFailure
{
    Invalid,
    NotFound,
    Conflict,
}

public sealed record ScheduledTaskInput(
    string Name,
    string Prompt,
    string WorkspaceId,
    string Model,
    ScheduleRule Schedule,
    string? ModelProviderId = null,
    string? Timezone = null,
    RepeatWindow? RepeatWindow = null,
    bool? Enabled = null,
    string CreatedBy = ScheduleCreators.User,
    int ChainDepth = 0);

public sealed record ScheduledTaskPatch
{
    public string? Name { get; init; }
    public string? Prompt { get; init; }
    public string? Model { get; init; }
    public string? ModelProviderId { get; init; }
    public ScheduleRule? Schedule { get; init; }
    public string? Timezone { get; init; }
    public RepeatWindow? RepeatWindow { get; init; }
    public bool? Enabled { get; init; }
}

/// <summary>
/// 定时任务的<b>写入收口</b>(2.x 的 <c>scheduled/bridge.ts</c> 的 <c>scheduledWrites</c> + <c>store</c> 的创建 / 更新):用户(协议方法)和 Agent(<see cref="AgentScheduling"/>)共用这一份,
/// 所以「落库 → <see cref="Changed"/>(调度器重排 + 权威广播)」这两件事只有一处需要记得——少了重排是「建了但不跑」,少了广播是「建了但界面上没有」,症状都不像 bug。
/// 校验在这里一次做完:工作区在不在、规则是不是真的会触发(<c>once</c> 在过去、时间串格式、时区名拼错,共同症状都是一条永远不触发而界面上看起来完全正常的任务)。
/// </summary>
public sealed class ScheduledTaskService(ScheduledRepository repository, TimeProvider clock, Func<string, bool> workspaceExists)
{
    public const int MaxNameLength = 200;
    public const int MaxPromptLength = 100_000;

    /// <summary>任务(或它的下一次触发)变了。参数是任务 id。回调在写入之后调用,异常被吞掉(任务已经落库,把通知失败报成写入失败会让调用方以为没建成而再建一遍)。</summary>
    public event Action<string>? Changed;

    public IReadOnlyList<ScheduledTask> List(string? workspaceId = null) => [.. repository.ListTasks(workspaceId).Select(Parse).OfType<ScheduledTask>()];

    public ScheduledTask? Get(string id) => repository.GetTask(id) is { } row ? Parse(row) : null;

    private long Now() => clock.GetUtcNow().ToUnixTimeMilliseconds();

    public ScheduledTask Create(ScheduledTaskInput input)
    {
        if (!workspaceExists(input.WorkspaceId))
        {
            throw new ScheduleException(ScheduleFailure.NotFound, "schedules.workspaceNotFound", "The project does not exist.");
        }

        var now = Now();
        var task = Normalize(input, now);
        RequireText(task.Name, task.Prompt);
        RequireValidRule(task);
        if (task.Enabled)
        {
            RequireFires(task, now);
        }

        var created = task with { Id = Ulid.New(clock) };
        repository.PutTask(ToRow(created, null));
        Announce(created.Id);
        return created;
    }

    public ScheduledTask Update(string id, ScheduledTaskPatch patch)
    {
        var raw = repository.GetTask(id) ?? throw NotFound(id);
        var current = Parse(raw) ?? throw NotFound(id);
        var now = Now();
        var timingChanged = patch.Schedule is not null || patch.Timezone is not null || patch.RepeatWindow is not null;
        var merged = Normalize(new ScheduledTaskInput(
            patch.Name ?? current.Name,
            patch.Prompt ?? current.Prompt,
            current.WorkspaceId,
            patch.Model ?? current.Model,
            patch.Schedule ?? current.Schedule,
            patch.Model is not null ? patch.ModelProviderId : patch.ModelProviderId ?? current.ModelProviderId,
            string.IsNullOrWhiteSpace(patch.Timezone) ? current.Timezone : patch.Timezone,
            patch.RepeatWindow ?? current.RepeatWindow,
            patch.Enabled ?? current.Enabled,
            current.CreatedBy,
            current.ChainDepth), now);
        RequireText(merged.Name, merged.Prompt);
        RequireValidRule(merged);
        if (merged.Enabled && (timingChanged || patch.Enabled == true))
        {
            RequireFires(merged, now);
        }

        var next = merged with { Id = id, CreatedAt = current.CreatedAt };
        repository.PutTask(ToRow(next, raw.Json));
        Announce(id);
        return next;
    }

    public bool Delete(string id)
    {
        var deleted = repository.DeleteTask(id);
        if (deleted)
        {
            Announce(id);
        }

        return deleted;
    }

    public ScheduledTask SetEnabled(string id, bool enabled)
    {
        var raw = repository.GetTask(id) ?? throw NotFound(id);
        var task = Parse(raw) ?? throw NotFound(id);
        var now = Now();
        if (enabled)
        {
            RequireFires(task, now);
        }

        var next = task with
        {
            Enabled = enabled,
            NextRunAt = enabled ? ScheduleCalculator.NextOccurrence(task.Schedule, task.Timezone, task.RepeatWindow, now) : null,
            UpdatedAt = now,
        };
        repository.PutTask(ToRow(next, raw.Json));
        Announce(id);
        return next;
    }

    /// <summary>调度器每次到点 / 对账时调用:把任务的下一次触发推到 <paramref name="after"/> 之后(不改「启用」)。</summary>
    internal ScheduledTask Advance(ScheduledTask task, long scheduledAt, long now, bool disable = false)
    {
        var enabled = task.Enabled && !disable;
        var next = task with
        {
            Enabled = enabled,
            NextRunAt = enabled ? ScheduleCalculator.NextOccurrence(task.Schedule, task.Timezone, task.RepeatWindow, scheduledAt) : null,
            UpdatedAt = now,
        };
        var raw = repository.GetTask(task.Id);
        repository.PutTask(ToRow(next, raw?.Json));
        Announce(task.Id);
        return next;
    }

    /// <summary>一次性任务按计划跑完之后关掉(手动运行不关)。</summary>
    internal void CloseOneShot(string taskId, long at)
    {
        if (repository.GetTask(taskId) is not { } raw || Parse(raw) is not { } task)
        {
            return;
        }

        repository.PutTask(ToRow(task with { Enabled = false, NextRunAt = null, UpdatedAt = at }, raw.Json));
        Announce(taskId);
    }

    internal void Announce(string taskId)
    {
        try
        {
            Changed?.Invoke(taskId);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 任务已经落库:通知失败不能让这次写入变成失败。界面靠下一次广播自然对齐。
        }
    }

    // ───────────────────────── 规范化与校验 ─────────────────────────

    private ScheduledTask Normalize(ScheduledTaskInput input, long now)
    {
        var timezone = string.IsNullOrWhiteSpace(input.Timezone) ? ScheduleCalculator.SystemTimeZone() : input.Timezone.Trim();
        var repeat = input.RepeatWindow is { Enabled: true } r
            ? new RepeatWindow(true, r.EndTime, Math.Max(1, r.IntervalMinutes ?? 60))
            : RepeatWindow.Off;
        var enabled = input.Enabled ?? true;
        return new ScheduledTask(
            "", input.Name.Trim(), input.Prompt.Trim(), input.WorkspaceId, input.Model, string.IsNullOrEmpty(input.ModelProviderId) ? null : input.ModelProviderId,
            input.Schedule, timezone, repeat, enabled, enabled ? ScheduleCalculator.NextOccurrence(input.Schedule, timezone, repeat, now) : null,
            now, now, input.CreatedBy, input.ChainDepth);
    }

    private static void RequireText(string name, string prompt)
    {
        if (name.Length is 0 or > MaxNameLength)
        {
            throw new ScheduleException(ScheduleFailure.Invalid, "schedules.invalid.name", $"A task name must be 1-{MaxNameLength} characters.");
        }

        if (prompt.Length is 0 or > MaxPromptLength)
        {
            throw new ScheduleException(ScheduleFailure.Invalid, "schedules.invalid.prompt", $"A task prompt must be 1-{MaxPromptLength} characters.");
        }
    }

    /// <summary>规则本身要合法(种类、时间串、星期、时区名、窗口结束时间):畸形的规则永远不会触发,界面上却看起来完全正常。</summary>
    private static void RequireValidRule(ScheduledTask task)
    {
        var rule = task.Schedule;
        var ok = rule.Kind switch
        {
            ScheduleKinds.Once => ScheduleCalculator.ParseLocalDateTime(rule.At) is not null,
            ScheduleKinds.Daily => ScheduleCalculator.ParseTime(rule.Time) is not null,
            ScheduleKinds.Weekly => ScheduleCalculator.ParseTime(rule.Time) is not null && rule.Weekdays is { Count: > 0 } days && days.All(d => d is >= 0 and <= 6),
            _ => false,
        };
        if (!ok)
        {
            throw new ScheduleException(ScheduleFailure.Invalid, "schedules.invalid.rule", "The schedule rule is malformed: times are \"HH:MM\", one-off dates \"YYYY-MM-DDTHH:MM\", weekdays 0-6.");
        }

        if (ScheduleCalculator.FindZone(task.Timezone) is null)
        {
            throw new ScheduleException(ScheduleFailure.Invalid, "schedules.invalid.timezone", $"'{task.Timezone}' is not a known IANA timezone.");
        }

        if (task.RepeatWindow.Enabled && ScheduleCalculator.ParseTime(task.RepeatWindow.EndTime) is null)
        {
            throw new ScheduleException(ScheduleFailure.Invalid, "schedules.invalid.repeat", "The repeat window needs an end time (HH:MM).");
        }
    }

    /// <summary>算一次下一次触发,拿不到就拒(过去的一次性时间在界面上看起来完全正常)。</summary>
    private static void RequireFires(ScheduledTask task, long now)
    {
        if (ScheduleCalculator.NextOccurrence(task.Schedule, task.Timezone, task.RepeatWindow, now) is null)
        {
            throw new ScheduleException(ScheduleFailure.Invalid, "schedules.invalid.neverFires",
                "This schedule never fires: a one-off time must be in the future, times must be \"HH:MM\", dates \"YYYY-MM-DDTHH:MM\", and the timezone a valid IANA name such as \"Asia/Shanghai\".");
        }
    }

    private static ScheduleException NotFound(string id) => new(ScheduleFailure.NotFound, "schedules.notFound", $"No scheduled task with id \"{id}\".");

    // ───────────────────────── 存储形态 ─────────────────────────

    internal static ScheduledTask? Parse(ScheduledTaskRow row)
    {
        try
        {
            if (JsonNode.Parse(row.Json) is not JsonObject o)
            {
                return null;
            }

            var schedule = o["schedule"] as JsonObject;
            var repeat = o["repeatWindow"] as JsonObject;
            var weekdays = schedule?["weekdays"] as JsonArray;
            return new ScheduledTask(
                row.Id,
                Str(o, "name") ?? "",
                Str(o, "prompt") ?? "",
                row.WorkspaceId,
                Str(o, "model") ?? "",
                Str(o, "modelProviderId") is { Length: > 0 } provider ? provider : null,
                new ScheduleRule(
                    Str(schedule, "kind") ?? "",
                    Str(schedule, "at"),
                    Str(schedule, "time"),
                    weekdays is null ? null : [.. weekdays.Select(static d => d is JsonValue v && v.TryGetValue<int>(out var i) ? i : -1)]),
                Str(o, "timezone") ?? "UTC",
                new RepeatWindow(
                    repeat?["enabled"] is JsonValue e && e.TryGetValue<bool>(out var enabled) && enabled,
                    Str(repeat, "endTime"),
                    repeat?["intervalMinutes"] is JsonValue iv && iv.TryGetValue<int>(out var minutes) ? minutes : null),
                row.Enabled,
                row.NextRunAt,
                row.CreatedAt,
                row.UpdatedAt,
                Str(o, "createdBy") == ScheduleCreators.Agent ? ScheduleCreators.Agent : ScheduleCreators.User,
                o["chainDepth"] is JsonValue d2 && d2.TryGetValue<int>(out var depth) ? Math.Max(0, depth) : 0);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            // 一行坏数据不能让整个列表失败。
            return null;
        }
    }

    /// <summary>写回:在原 JSON 上改已知字段,3.0 不认识的字段(2.x 以后新增的)原样保留。</summary>
    private static ScheduledTaskRow ToRow(ScheduledTask t, string? existingJson)
    {
        JsonObject o;
        try
        {
            o = existingJson is not null && JsonNode.Parse(existingJson) is JsonObject existing ? existing : new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            o = new JsonObject();
        }

        o["id"] = t.Id;
        o["name"] = t.Name;
        o["prompt"] = t.Prompt;
        o["workspaceId"] = t.WorkspaceId;
        o["model"] = t.Model;
        if (t.ModelProviderId is null)
        {
            o.Remove("modelProviderId");
        }
        else
        {
            o["modelProviderId"] = t.ModelProviderId;
        }

        var schedule = new JsonObject { ["kind"] = t.Schedule.Kind };
        if (t.Schedule.Kind == ScheduleKinds.Once)
        {
            schedule["at"] = t.Schedule.At;
        }
        else
        {
            schedule["time"] = t.Schedule.Time;
            if (t.Schedule.Kind == ScheduleKinds.Weekly)
            {
                var days = new JsonArray();
                foreach (var d in t.Schedule.Weekdays ?? [])
                {
                    days.Add((JsonNode?)JsonValue.Create(d));
                }

                schedule["weekdays"] = days;
            }
        }

        o["schedule"] = schedule;
        o["timezone"] = t.Timezone;
        var repeat = new JsonObject { ["enabled"] = t.RepeatWindow.Enabled };
        if (t.RepeatWindow.Enabled)
        {
            repeat["endTime"] = t.RepeatWindow.EndTime;
            repeat["intervalMinutes"] = t.RepeatWindow.IntervalMinutes ?? 60;
        }

        o["repeatWindow"] = repeat;
        o["enabled"] = t.Enabled;
        o["nextRunAt"] = t.NextRunAt;
        o["createdAt"] = t.CreatedAt;
        o["updatedAt"] = t.UpdatedAt;
        if (t.CreatedBy == ScheduleCreators.Agent)
        {
            o["createdBy"] = t.CreatedBy;
        }
        else
        {
            o.Remove("createdBy");
        }

        if (t.ChainDepth > 0)
        {
            o["chainDepth"] = t.ChainDepth;
        }
        else
        {
            o.Remove("chainDepth");
        }

        return new ScheduledTaskRow(t.Id, t.WorkspaceId, t.Enabled, t.NextRunAt, t.CreatedAt, t.UpdatedAt, o.ToJsonString());
    }

    private static string? Str(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
