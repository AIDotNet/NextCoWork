-- Workflow 运行与节点执行记录(docs/design/12-workflows.md §6)。
-- 增量迁移,不触碰 2.x 既有表;definition_json 冻结运行时快照,支持历史回放与断点续跑。
CREATE TABLE IF NOT EXISTS workflow_runs (
    id                TEXT PRIMARY KEY,
    workspace_id      TEXT NOT NULL,
    session_id        TEXT,
    workflow_id       TEXT,
    title             TEXT NOT NULL,
    definition_json   TEXT NOT NULL,
    inputs_json       TEXT NOT NULL,
    status            TEXT NOT NULL,
    output_text       TEXT,
    output_json       TEXT,
    error             TEXT,
    environment_id    TEXT,
    permission_mode   TEXT NOT NULL,
    total_tokens      INTEGER NOT NULL DEFAULT 0,
    created_at        INTEGER NOT NULL,
    started_at        INTEGER,
    finished_at       INTEGER
);

CREATE INDEX IF NOT EXISTS idx_workflow_runs_workspace ON workflow_runs(workspace_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_workflow_runs_session   ON workflow_runs(session_id, created_at DESC);

CREATE TABLE IF NOT EXISTS workflow_node_runs (
    run_id            TEXT NOT NULL,
    node_id           TEXT NOT NULL,
    attempt           INTEGER NOT NULL DEFAULT 1,
    kind              TEXT NOT NULL,
    status            TEXT NOT NULL,
    child_run_id      TEXT,
    child_session_id  TEXT,
    input_json        TEXT,
    output_text       TEXT,
    output_json       TEXT,
    error             TEXT,
    tokens            INTEGER NOT NULL DEFAULT 0,
    started_at        INTEGER,
    finished_at       INTEGER,
    PRIMARY KEY (run_id, node_id, attempt),
    FOREIGN KEY (run_id) REFERENCES workflow_runs(id) ON DELETE CASCADE
);
