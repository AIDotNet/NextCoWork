-- 2.x 迁移 v14(plans)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
CREATE TABLE plans (
  id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  version INTEGER NOT NULL,
  status TEXT NOT NULL,
  json TEXT NOT NULL,
  source_run_id TEXT NOT NULL,
  updated_at INTEGER NOT NULL,
  UNIQUE(session_id, id)
);
CREATE INDEX plans_by_session ON plans(session_id, updated_at DESC);
CREATE TABLE plan_revisions (
  plan_id TEXT NOT NULL REFERENCES plans(id) ON DELETE CASCADE,
  version INTEGER NOT NULL,
  author TEXT NOT NULL,
  source_run_id TEXT,
  patch TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  PRIMARY KEY(plan_id, version)
);
