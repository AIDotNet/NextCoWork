namespace Ncw.Agent.Storage;

/// <summary><c>workflow_runs</c> 的一行。</summary>
public sealed record WorkflowRunRow(
    string Id,
    string WorkspaceId,
    string? SessionId,
    string? WorkflowId,
    string Title,
    string DefinitionJson,
    string InputsJson,
    string Status,
    string? OutputText,
    string? OutputJson,
    string? Error,
    string? EnvironmentId,
    string PermissionMode,
    long TotalTokens,
    long CreatedAt,
    long? StartedAt = null,
    long? FinishedAt = null);

/// <summary><c>workflow_node_runs</c> 的一行。</summary>
public sealed record WorkflowNodeRunRow(
    string RunId,
    string NodeId,
    int Attempt,
    string Kind,
    string Status,
    string? ChildRunId = null,
    string? ChildSessionId = null,
    string? InputJson = null,
    string? OutputText = null,
    string? OutputJson = null,
    string? Error = null,
    long Tokens = 0,
    long? StartedAt = null,
    long? FinishedAt = null);

/// <summary>Workflow 运行与节点状态的持久化仓库(只做存取,不承担调度与校验)。</summary>
public sealed class WorkflowRunRepository(AgentDatabase database)
{
    private const string RunColumns =
        "id, workspace_id, session_id, workflow_id, title, definition_json, inputs_json, status, output_text, output_json, error, environment_id, permission_mode, total_tokens, created_at, started_at, finished_at";

    private const string NodeColumns =
        "run_id, node_id, attempt, kind, status, child_run_id, child_session_id, input_json, output_text, output_json, error, tokens, started_at, finished_at";

    private static WorkflowRunRow MapRun(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(
            r.GetString(0),
            r.GetString(1),
            r.Text(2),
            r.Text(3),
            r.GetString(4),
            r.GetString(5),
            r.GetString(6),
            r.GetString(7),
            r.Text(8),
            r.Text(9),
            r.Text(10),
            r.Text(11),
            r.GetString(12),
            r.GetInt64(13),
            r.GetInt64(14),
            r.IsDBNull(15) ? null : r.GetInt64(15),
            r.IsDBNull(16) ? null : r.GetInt64(16));

    private static WorkflowNodeRunRow MapNode(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(
            r.GetString(0),
            r.GetString(1),
            r.GetInt32(2),
            r.GetString(3),
            r.GetString(4),
            r.Text(5),
            r.Text(6),
            r.Text(7),
            r.Text(8),
            r.Text(9),
            r.Text(10),
            r.GetInt64(11),
            r.IsDBNull(12) ? null : r.GetInt64(12),
            r.IsDBNull(13) ? null : r.GetInt64(13));

    public void PutRun(WorkflowRunRow run) => database.Run("""
        INSERT INTO workflow_runs (
            id, workspace_id, session_id, workflow_id, title, definition_json, inputs_json,
            status, output_text, output_json, error, environment_id, permission_mode,
            total_tokens, created_at, started_at, finished_at
        ) VALUES (
            $id, $ws, $sid, $wid, $title, $def, $in,
            $status, $outText, $outJson, $err, $env, $perm,
            $tokens, $created, $started, $finished
        )
        ON CONFLICT (id) DO UPDATE SET
            status = excluded.status,
            output_text = excluded.output_text,
            output_json = excluded.output_json,
            error = excluded.error,
            total_tokens = excluded.total_tokens,
            started_at = COALESCE(workflow_runs.started_at, excluded.started_at),
            finished_at = excluded.finished_at
        """,
        ("$id", run.Id), ("$ws", run.WorkspaceId), ("$sid", run.SessionId), ("$wid", run.WorkflowId),
        ("$title", run.Title), ("$def", run.DefinitionJson), ("$in", run.InputsJson),
        ("$status", run.Status), ("$outText", run.OutputText), ("$outJson", run.OutputJson),
        ("$err", run.Error), ("$env", run.EnvironmentId), ("$perm", run.PermissionMode),
        ("$tokens", run.TotalTokens), ("$created", run.CreatedAt), ("$started", run.StartedAt), ("$finished", run.FinishedAt));

    public WorkflowRunRow? GetRun(string runId) =>
        database.Row($"SELECT {RunColumns} FROM workflow_runs WHERE id = $id", MapRun, ("$id", runId));

    public IReadOnlyList<WorkflowRunRow> ListRuns(string? workspaceId = null, string? sessionId = null, int limit = 50)
    {
        var clauses = new List<string>();
        var args = new List<(string, object?)>();
        if (workspaceId is not null)
        {
            clauses.Add("workspace_id = $ws");
            args.Add(("$ws", workspaceId));
        }

        if (sessionId is not null)
        {
            clauses.Add("session_id = $sid");
            args.Add(("$sid", sessionId));
        }

        var where = clauses.Count > 0 ? "WHERE " + string.Join(" AND ", clauses) : "";
        args.Add(("$limit", Math.Clamp(limit, 1, 200)));
        return database.Rows($"SELECT {RunColumns} FROM workflow_runs {where} ORDER BY created_at DESC, id DESC LIMIT $limit", MapRun, [.. args]);
    }

    public void PutNodeRun(WorkflowNodeRunRow node) => database.Run("""
        INSERT INTO workflow_node_runs (
            run_id, node_id, attempt, kind, status, child_run_id, child_session_id,
            input_json, output_text, output_json, error, tokens, started_at, finished_at
        ) VALUES (
            $rid, $nid, $att, $kind, $status, $crid, $csid,
            $in, $outText, $outJson, $err, $tokens, $started, $finished
        )
        ON CONFLICT (run_id, node_id, attempt) DO UPDATE SET
            status = excluded.status,
            child_run_id = COALESCE(excluded.child_run_id, workflow_node_runs.child_run_id),
            child_session_id = COALESCE(excluded.child_session_id, workflow_node_runs.child_session_id),
            input_json = COALESCE(excluded.input_json, workflow_node_runs.input_json),
            output_text = excluded.output_text,
            output_json = excluded.output_json,
            error = excluded.error,
            tokens = excluded.tokens,
            started_at = COALESCE(workflow_node_runs.started_at, excluded.started_at),
            finished_at = excluded.finished_at
        """,
        ("$rid", node.RunId), ("$nid", node.NodeId), ("$att", node.Attempt), ("$kind", node.Kind),
        ("$status", node.Status), ("$crid", node.ChildRunId), ("$csid", node.ChildSessionId),
        ("$in", node.InputJson), ("$outText", node.OutputText), ("$outJson", node.OutputJson),
        ("$err", node.Error), ("$tokens", node.Tokens), ("$started", node.StartedAt), ("$finished", node.FinishedAt));

    /// <summary>返回某次运行每个节点的最新 Attempt 行。</summary>
    public IReadOnlyList<WorkflowNodeRunRow> GetLatestNodes(string runId) =>
        database.Rows($"""
            SELECT {NodeColumns}
            FROM workflow_node_runs n
            WHERE n.run_id = $rid
              AND n.attempt = (SELECT MAX(n2.attempt) FROM workflow_node_runs n2 WHERE n2.run_id = n.run_id AND n2.node_id = n.node_id)
            """, MapNode, ("$rid", runId));

    /// <summary>启动对账:异常重启残留的运行中记录统一收口为 failed(保留已完成节点以便断点续跑)。</summary>
    public int FailInterruptedOnStartup(long nowMs, string errorKey = "workflows.error.agentRestarted") =>
        database.InTransaction(() =>
        {
            database.Run("""
                UPDATE workflow_node_runs
                SET status = 'failed', error = $err, finished_at = COALESCE(finished_at, $now)
                WHERE status IN ('pending', 'running', 'waiting_approval')
                  AND run_id IN (SELECT id FROM workflow_runs WHERE status IN ('queued', 'running', 'waiting_approval'))
                """, ("$err", errorKey), ("$now", nowMs));

            return database.Run("""
                UPDATE workflow_runs
                SET status = 'failed', error = $err, finished_at = COALESCE(finished_at, $now)
                WHERE status IN ('queued', 'running', 'waiting_approval')
                """, ("$err", errorKey), ("$now", nowMs));
        });
}
