-- 2.x 迁移 v9(context-management)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
CREATE TABLE context_checkpoints (
  id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
  window_index INTEGER NOT NULL,
  note TEXT NOT NULL,
  source TEXT NOT NULL,
  covered_from_message_id TEXT,
  covered_through_message_id TEXT,
  input_tokens_before INTEGER,
  input_tokens_after INTEGER,
  search_hits TEXT,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  revision INTEGER NOT NULL DEFAULT 1,
  UNIQUE (session_id, window_index)
);
CREATE INDEX context_checkpoints_by_session ON context_checkpoints (session_id, window_index);
