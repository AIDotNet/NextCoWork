-- 2.x 迁移 v18(scheduled-tasks)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
CREATE TABLE scheduled_tasks (
  id TEXT PRIMARY KEY,
  workspace_id TEXT NOT NULL,
  enabled INTEGER NOT NULL DEFAULT 1,
  next_run_at INTEGER,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  json TEXT NOT NULL
);
CREATE INDEX scheduled_tasks_by_next_run ON scheduled_tasks (enabled, next_run_at);
CREATE INDEX scheduled_tasks_by_workspace ON scheduled_tasks (workspace_id, updated_at DESC);

CREATE TABLE scheduled_runs (
  id TEXT PRIMARY KEY,
  task_id TEXT NOT NULL,
  session_id TEXT NOT NULL DEFAULT '',
  trigger TEXT NOT NULL,
  status TEXT NOT NULL,
  scheduled_at INTEGER NOT NULL,
  started_at INTEGER,
  ended_at INTEGER,
  summary TEXT,
  error TEXT
);
CREATE INDEX scheduled_runs_by_task ON scheduled_runs (task_id, scheduled_at DESC, id DESC);
CREATE INDEX scheduled_runs_by_scheduled_at ON scheduled_runs (scheduled_at DESC, id DESC);
