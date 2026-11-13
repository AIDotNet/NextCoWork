-- 2.x 迁移 v19(plans-v2)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
CREATE TABLE plans_v2 (
  id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  version INTEGER NOT NULL,
  lifecycle TEXT NOT NULL,
  json TEXT NOT NULL,
  source_run_id TEXT NOT NULL,
  execution_run_id TEXT,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL
);
CREATE INDEX plans_v2_by_session ON plans_v2(session_id, updated_at DESC);
CREATE TABLE plan_revisions_v2 (
  plan_id TEXT NOT NULL REFERENCES plans_v2(id) ON DELETE CASCADE,
  version INTEGER NOT NULL,
  author TEXT NOT NULL CHECK(author IN ('agent', 'user', 'system')),
  source_run_id TEXT,
  patch TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  PRIMARY KEY(plan_id, version)
);
CREATE UNIQUE INDEX plans_v2_one_execution ON plans_v2(id, execution_run_id) WHERE execution_run_id IS NOT NULL;
