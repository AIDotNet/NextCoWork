using PolyType;

namespace Ncw.Protocol.Methods;

// 定时任务(docs/design/08-agent-runtime/05 §8、10 的 `scheduled` 模块 → `schedules.*`)。真源在 Agent 的 `ScheduledTaskService` / `Scheduler`(2.x 的 `scheduled_tasks` / `scheduled_runs` 表原样沿用)。
// ★ 定时任务无人值守地以 `full` 权限档位运行(2.x 的 `SCHEDULED_PERMISSION_MODE`;member 不能开 full,09 §4),所以**所有写入(创建 / 修改 / 启停 / 立即运行 / 删除)只有 owner**;读取 viewer 起。
// 规则不是 cron:沿用 2.x 的三种(一次 / 每天 / 每周几)加可选的「当天窗口内重复」,按任务自己的 IANA 时区求值。时间一律是 Unix 毫秒(UTC);本地日期时间串(`once.at`)按任务时区解释。
// `schedules` 主题是全量权威(快照与 `schedules.changed` 同一个类型,带单调的 `Rev`):含全部任务 + 活动的运行(排队 / 运行中)+ 最近的已结束运行;终态压过运行中,旧版本不会把状态改回去。

/// <summary>调度规则。<see cref="Kind"/> = <c>once</c>(<see cref="At"/> = <c>YYYY-MM-DDTHH:MM</c>)/ <c>daily</c>(<see cref="Time"/> = <c>HH:MM</c>)/ <c>weekly</c>(<see cref="Weekdays"/> 0 = 周日 … 6 = 周六,加 <see cref="Time"/>)。</summary>
public sealed record ScheduleRuleDto(string Kind, string? At = null, string? Time = null, int[]? Weekdays = null);

/// <summary>当天窗口内重复:从规则的开始时刻起,每 <see cref="IntervalMinutes"/> 分钟一次,直到 <see cref="EndTime"/>(<c>HH:MM</c>,含)。间隔最少 1 分钟(模型建的任务另有 5 分钟下限)。</summary>
public sealed record RepeatWindowDto(bool Enabled, string? EndTime = null, int? IntervalMinutes = null);

/// <param name="CreatedBy"><c>user</c> / <c>agent</c>(模型在运行中建的)。</param>
/// <param name="ChainDepth">自动排程的层数:用户手建 = 0,定时任务运行当中由 Agent 建的 = 父任务 + 1(上限 2,防止指数增长)。</param>
/// <param name="NextRunAt">下一次触发(UTC 毫秒);停用或规则已过期时为 null。</param>
public sealed record ScheduledTaskDto(
    string Id,
    string Name,
    string Prompt,
    string WorkspaceId,
    string Model,
    string? ModelProviderId,
    ScheduleRuleDto Schedule,
    string Timezone,
    RepeatWindowDto RepeatWindow,
    bool Enabled,
    long? NextRunAt,
    long CreatedAt,
    long UpdatedAt,
    string CreatedBy,
    int ChainDepth);

/// <param name="Trigger"><c>scheduled</c> / <c>manual</c>。</param>
/// <param name="Status"><c>queued</c> / <c>running</c> / <c>success</c> / <c>error</c> / <c>skipped</c>(错过 / 上一次还没跑完)/ <c>aborted</c>。</param>
/// <param name="Error">稳定的 <c>scheduled.*</c> key(控制端翻译);没有为 null。</param>
public sealed record ScheduledRunDto(
    string Id,
    string TaskId,
    string SessionId,
    string Trigger,
    string Status,
    long ScheduledAt,
    long? StartedAt,
    long? EndedAt,
    string? Summary,
    string? Error);

/// <summary>创建任务。<see cref="Timezone"/> 缺省 = 设备本地时区;<see cref="Enabled"/> 缺省 = 启用。<see cref="Model"/> 为空 = 用项目的默认模型。</summary>
public sealed record ScheduledTaskInputDto(
    string Name,
    string Prompt,
    string WorkspaceId,
    string Model,
    ScheduleRuleDto Schedule,
    string? ModelProviderId = null,
    string? Timezone = null,
    RepeatWindowDto? RepeatWindow = null,
    bool? Enabled = null);

/// <summary>任务补丁:字段缺省 = 不改。工作区不能改(任务属于创建它的项目)。</summary>
public sealed record ScheduledTaskPatchDto(
    string? Name = null,
    string? Prompt = null,
    string? Model = null,
    string? ModelProviderId = null,
    ScheduleRuleDto? Schedule = null,
    string? Timezone = null,
    RepeatWindowDto? RepeatWindow = null,
    bool? Enabled = null);

public sealed record ScheduleListParams(string? WorkspaceId = null);

public sealed record ScheduledTasksResult(ScheduledTaskDto[] Tasks);

public sealed record ScheduleRefParams(string Id);

public sealed record ScheduleUpdateParams(string Id, ScheduledTaskPatchDto Patch);

public sealed record ScheduleSetEnabledParams(string Id, bool Enabled);

/// <summary>运行记录分页:按 <c>ScheduledAt</c> 降序;游标是上一页最后一条的 <c>(ScheduledAt, Id)</c>(同毫秒不会重复或漏)。<see cref="Limit"/> 1–200,缺省 50。</summary>
public sealed record ScheduledRunsQuery(string? TaskId = null, int Limit = 50, long? BeforeScheduledAt = null, string? BeforeId = null);

public sealed record ScheduledRunsResult(ScheduledRunDto[] Runs, bool HasMore);

/// <summary><c>schedules</c> 主题的状态,同时是 <c>schedules.changed</c> 的数据(首帧与事件同一个类型,规则 5)。<see cref="Runs"/> = 活动的运行 + 最近 50 条已结束的。</summary>
public sealed record SchedulesTopicState(long Rev, ScheduledTaskDto[] Tasks, ScheduledRunDto[] Runs);

public static class SchedulesEvents
{
    /// <summary>任务或运行变了(数据是新的 <see cref="SchedulesTopicState"/>,全量)。</summary>
    public const string Changed = "schedules.changed";
}

/// <summary>定时任务方法表(能力 <see cref="Capabilities.Schedules"/>,承载 本机 + 远程):读取 viewer 起,写入 owner(无人值守的 full 档位)。</summary>
public static class SchedulesMethods
{
    public static readonly MethodDef<ScheduleListParams, ScheduledTasksResult> ListTasks =
        MethodTable.Query<ScheduleListParams, ScheduledTasksResult>("schedules.listTasks", Role.Viewer, Capabilities.Schedules);

    public static readonly MethodDef<ScheduleRefParams, ScheduledTaskDto> GetTask =
        MethodTable.Query<ScheduleRefParams, ScheduledTaskDto>("schedules.getTask", Role.Viewer, Capabilities.Schedules);

    public static readonly MethodDef<ScheduledTaskInputDto, ScheduledTaskDto> Create =
        MethodTable.Command<ScheduledTaskInputDto, ScheduledTaskDto>("schedules.create", Role.Owner, Capabilities.Schedules);

    public static readonly MethodDef<ScheduleUpdateParams, ScheduledTaskDto> Update =
        MethodTable.Command<ScheduleUpdateParams, ScheduledTaskDto>("schedules.update", Role.Owner, Capabilities.Schedules);

    public static readonly MethodDef<ScheduleRefParams, Unit> Delete =
        MethodTable.Command<ScheduleRefParams, Unit>("schedules.delete", Role.Owner, Capabilities.Schedules);

    public static readonly MethodDef<ScheduleSetEnabledParams, ScheduledTaskDto> SetEnabled =
        MethodTable.Command<ScheduleSetEnabledParams, ScheduledTaskDto>("schedules.setEnabled", Role.Owner, Capabilities.Schedules);

    /// <summary>立即运行一次:先落一条 <c>queued</c> 记录并返回它,运行在后台进行(进度看主题)。上一次还没跑完 → <c>conflict</c>。</summary>
    public static readonly MethodDef<ScheduleRefParams, ScheduledRunDto> RunNow =
        MethodTable.Command<ScheduleRefParams, ScheduledRunDto>("schedules.runNow", Role.Owner, Capabilities.Schedules);

    public static readonly MethodDef<ScheduledRunsQuery, ScheduledRunsResult> ListRuns =
        MethodTable.Query<ScheduledRunsQuery, ScheduledRunsResult>("schedules.listRuns", Role.Viewer, Capabilities.Schedules);

    public static readonly MethodDef<ScheduleRefParams, ScheduledRunDto> GetRun =
        MethodTable.Query<ScheduleRefParams, ScheduledRunDto>("schedules.getRun", Role.Viewer, Capabilities.Schedules);

    /// <summary>删一条已结束的运行记录(排队 / 运行中的 → <c>conflict</c>)。</summary>
    public static readonly MethodDef<ScheduleRefParams, Unit> DeleteRun =
        MethodTable.Command<ScheduleRefParams, Unit>("schedules.deleteRun", Role.Owner, Capabilities.Schedules);

    public static readonly TopicDef<SchedulesTopicState> Topic = new("schedules", PayloadShapes.GetShape<SchedulesTopicState>());

    public static readonly IReadOnlyList<MethodDef> All =
        [ListTasks, GetTask, Create, Update, Delete, SetEnabled, RunNow, ListRuns, GetRun, DeleteRun];
}

[GenerateShapeFor<ScheduleRuleDto>]
[GenerateShapeFor<RepeatWindowDto>]
[GenerateShapeFor<ScheduledTaskDto>]
[GenerateShapeFor<ScheduledRunDto>]
[GenerateShapeFor<ScheduledTaskInputDto>]
[GenerateShapeFor<ScheduledTaskPatchDto>]
[GenerateShapeFor<ScheduleListParams>]
[GenerateShapeFor<ScheduledTasksResult>]
[GenerateShapeFor<ScheduleRefParams>]
[GenerateShapeFor<ScheduleUpdateParams>]
[GenerateShapeFor<ScheduleSetEnabledParams>]
[GenerateShapeFor<ScheduledRunsQuery>]
[GenerateShapeFor<ScheduledRunsResult>]
[GenerateShapeFor<SchedulesTopicState>]
public sealed partial class PayloadShapes;
