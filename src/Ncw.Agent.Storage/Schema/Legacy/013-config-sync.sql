-- 2.x 迁移 v13(config-sync)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
CREATE TABLE sync_account (
  account_id TEXT PRIMARY KEY,
  device_id TEXT NOT NULL,
  enabled INTEGER NOT NULL DEFAULT 1,
  initial_sync_completed INTEGER NOT NULL DEFAULT 0,
  last_pull_cursor INTEGER NOT NULL DEFAULT 0,
  last_server_cursor INTEGER NOT NULL DEFAULT 0,
  last_success_at INTEGER,
  last_error TEXT,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL
);
CREATE TABLE sync_outbox (
  mutation_id TEXT PRIMARY KEY,
  account_id TEXT NOT NULL,
  device_id TEXT NOT NULL,
  client_seq INTEGER NOT NULL,
  kind TEXT NOT NULL,
  entity_id TEXT NOT NULL,
  operation TEXT NOT NULL,
  payload TEXT NOT NULL,
  base_revision INTEGER NOT NULL DEFAULT 0,
  workspace_id TEXT,
  created_at INTEGER NOT NULL,
  attempt_count INTEGER NOT NULL DEFAULT 0,
  next_attempt_at INTEGER NOT NULL,
  acked_at INTEGER,
  last_error TEXT
);
CREATE UNIQUE INDEX sync_outbox_sequence ON sync_outbox(account_id, client_seq);
CREATE INDEX sync_outbox_pending ON sync_outbox(account_id, acked_at, next_attempt_at, client_seq);
CREATE TABLE sync_conflict (
  id TEXT PRIMARY KEY,
  account_id TEXT NOT NULL,
  kind TEXT NOT NULL,
  entity_id TEXT NOT NULL,
  local_revision INTEGER NOT NULL,
  remote_revision INTEGER NOT NULL,
  local_payload TEXT NOT NULL,
  remote_payload TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'pending',
  created_at INTEGER NOT NULL,
  resolved_at INTEGER
);
CREATE INDEX sync_conflict_pending ON sync_conflict(account_id, status, created_at);
CREATE TABLE sync_revisions (
  account_id TEXT NOT NULL,
  kind TEXT NOT NULL,
  entity_id TEXT NOT NULL,
  revision INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (account_id, kind, entity_id)
);
