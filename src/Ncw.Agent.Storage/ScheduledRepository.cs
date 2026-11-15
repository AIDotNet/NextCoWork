namespace Ncw.Agent.Storage;

/// <summary>
/// <c>scheduled_tasks</c> 的一行(2.x 迁移 18)。真源是 <see cref="Json"/>(任务整份 JSON,3.0 不认识的字段原样保留);
/// <c>enabled</c> / <c>next_run_at</c> / 时间戳是提出来的真列(调度器按 <c>(enabled, next_run_at)</c> 扫)。
/// </summary>
public sealed record ScheduledTaskRow(string Id, string WorkspaceId, bool Enabled, long? NextRunAt, long CreatedAt, long UpdatedAt, string Json);

/// <summary><c>scheduled_runs</c> 的一行。<see cref="Error"/> 是稳定的 <c>scheduled.*</c> key(2.x 同款,控制端翻译)。</summary>
public sealed record ScheduledRunRow(
    string Id,
    string TaskId,
    string SessionId,
    string Trigger,
    string Status,
    long ScheduledAt,
    long? StartedAt = null,
    long? EndedAt = null,
    string? Summary = null,
    string? Error = null);

/// <summary>
/// 定时任务与运行记录的表(2.x 的 <c>scheduled_tasks</c> / <c>scheduled_runs</c>)。只做存取;求值、调度、校验在服务层。
/// 任务按工作区归属(没有账户作用域列:2.x 同样不收窄)。运行记录不随任务删除而删(2.x 同款:历史留着,界面按任务过滤)。
/// </summary>
public sealed class ScheduledRepository(AgentDatabase database)
{
    private const string TaskColumns = "id, workspace_id, enabled, next_run_at, created_at, updated_at, json";

    private const string RunColumns = "id, task_id, session_id, trigger, status, scheduled_at, started_at, ended_at, summary, error";

    private static ScheduledTaskRow MapTask(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetInt64(2) != 0, r.IsDBNull(3) ? null : r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetString(6));

    private static ScheduledRunRow MapRun(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.Text(2) ?? "", r.GetString(3), r.GetString(4), r.GetInt64(5),
            r.IsDBNull(6) ? null : r.GetInt64(6), r.IsDBNull(7) ? null : r.GetInt64(7), r.Text(8), r.Text(9));

    /// <summary>全部任务(或某个工作区的):启用的在前,按下一次触发时间升序(没有下一次的靠后),同值按 JSON 兜底保证顺序确定。</summary>
    public IReadOnlyList<ScheduledTaskRow> ListTasks(string? workspaceId = null) => workspaceId is null
        ? database.Rows($"SELECT {TaskColumns} FROM scheduled_tasks ORDER BY enabled DESC, next_run_at IS NULL, next_run_at ASC, json", MapTask)
        : database.Rows($"SELECT {TaskColumns} FROM scheduled_tasks WHERE workspace_id = $w ORDER BY enabled DESC, next_run_at IS NULL, next_run_at ASC, json", MapTask, ("$w", workspaceId));

    public ScheduledTaskRow? GetTask(string id) => database.Row($"SELECT {TaskColumns} FROM scheduled_tasks WHERE id = $id", MapTask, ("$id", id));

    public void PutTask(ScheduledTaskRow task) => database.Run("""
        INSERT INTO scheduled_tasks (id, workspace_id, enabled, next_run_at, created_at, updated_at, json) VALUES ($id, $w, $e, $n, $c, $u, $j)
        ON CONFLICT (id) DO UPDATE SET workspace_id = excluded.workspace_id, enabled = excluded.enabled, next_run_at = excluded.next_run_at,
          updated_at = excluded.updated_at, json = excluded.json
        """, ("$id", task.Id), ("$w", task.WorkspaceId), ("$e", task.Enabled ? 1 : 0), ("$n", task.NextRunAt), ("$c", task.CreatedAt), ("$u", task.UpdatedAt), ("$j", task.Json));

    public bool DeleteTask(string id) => database.Run("DELETE FROM scheduled_tasks WHERE id = $id", ("$id", id)) > 0;

    /// <summary>运行记录,按 <c>(scheduled_at, id)</c> 降序(全序:同毫秒不重复不漏);游标是上一页最后一条。多取一条用来判断还有没有下一页。</summary>
    public IReadOnlyList<ScheduledRunRow> ListRuns(string? taskId = null, int limit = 100, long? beforeScheduledAt = null, string? beforeId = null)
    {
        var clauses = new List<string>();
        var args = new List<(string, object?)>();
        if (taskId is not null)
        {
            clauses.Add("task_id = $t");
            args.Add(("$t", taskId));
        }

        if (beforeScheduledAt is { } at)
        {
            clauses.Add("(scheduled_at < $at OR (scheduled_at = $at AND id < $bid))");
            args.Add(("$at", at));
            args.Add(("$bid", beforeId ?? "￿"));
        }

        var where = clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses);
        return database.Rows($"SELECT {RunColumns} FROM scheduled_runs {where} ORDER BY scheduled_at DESC, id DESC LIMIT $limit", MapRun,
            [.. args, ("$limit", Math.Max(1, limit))]);
    }

    public ScheduledRunRow? GetRun(string id) => database.Row($"SELECT {RunColumns} FROM scheduled_runs WHERE id = $id", MapRun, ("$id", id));

    /// <summary>某个会话属于哪一次定时执行(算链深度用:每次 Agent 建任务都要查,所以走索引而不是全表扫)。</summary>
    public ScheduledRunRow? GetRunBySession(string sessionId) => sessionId.Length == 0
        ? null
        : database.Row($"SELECT {RunColumns} FROM scheduled_runs WHERE session_id = $s ORDER BY scheduled_at DESC, id DESC LIMIT 1", MapRun, ("$s", sessionId));

    /// <summary>排队 / 运行中的记录(权威广播的「活动运行」)。</summary>
    public IReadOnlyList<ScheduledRunRow> ActiveRuns() =>
        database.Rows($"SELECT {RunColumns} FROM scheduled_runs WHERE status IN ('queued', 'running') ORDER BY scheduled_at DESC, id DESC", MapRun);

    public void PutRun(ScheduledRunRow run) => database.Run("""
        INSERT INTO scheduled_runs (id, task_id, session_id, trigger, status, scheduled_at, started_at, ended_at, summary, error)
        VALUES ($id, $t, $s, $tr, $st, $sa, $started, $ended, $sum, $err)
        ON CONFLICT (id) DO UPDATE SET task_id = excluded.task_id, session_id = excluded.session_id, trigger = excluded.trigger, status = excluded.status,
          scheduled_at = excluded.scheduled_at, started_at = excluded.started_at, ended_at = excluded.ended_at, summary = excluded.summary, error = excluded.error
        """, ("$id", run.Id), ("$t", run.TaskId), ("$s", run.SessionId), ("$tr", run.Trigger), ("$st", run.Status), ("$sa", run.ScheduledAt),
        ("$started", run.StartedAt), ("$ended", run.EndedAt), ("$sum", run.Summary), ("$err", run.Error));

    public bool DeleteRun(string id) => database.Run("DELETE FROM scheduled_runs WHERE id = $id", ("$id", id)) > 0;
}
