-- 2.x 迁移 v4(sessions-data)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
CREATE TABLE sessions (
  id                   TEXT PRIMARY KEY,
  workspace_id         TEXT NOT NULL,
  title                TEXT NOT NULL,
  model                TEXT NOT NULL,
  mode                 TEXT NOT NULL,
  thinking             TEXT NOT NULL,
  root_path_at_creation TEXT NOT NULL,
  status               TEXT NOT NULL DEFAULT 'idle',
  archived             INTEGER NOT NULL DEFAULT 0,
  favorited            INTEGER NOT NULL DEFAULT 0,
  created_at           INTEGER NOT NULL,
  updated_at           INTEGER NOT NULL,
  json                 TEXT NOT NULL
);
CREATE INDEX sessions_by_workspace ON sessions (workspace_id, archived, updated_at DESC);
CREATE INDEX sessions_by_updated ON sessions (updated_at DESC);

CREATE TABLE messages (
  id             TEXT PRIMARY KEY,
  session_id     TEXT NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
  ordinal        INTEGER NOT NULL,
  role           TEXT NOT NULL,
  parts          TEXT NOT NULL,
  schema_version INTEGER NOT NULL DEFAULT 1,
  created_at     INTEGER NOT NULL,
  UNIQUE (session_id, ordinal)
);
CREATE INDEX messages_by_session ON messages (session_id, ordinal);

CREATE TABLE runs (
  id         TEXT PRIMARY KEY,
  session_id TEXT NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
  status     TEXT NOT NULL,
  started_at INTEGER NOT NULL,
  ended_at   INTEGER,
  json       TEXT
);
CREATE INDEX runs_by_session ON runs (session_id, started_at DESC);

CREATE TABLE attachments (
  id          TEXT PRIMARY KEY,
  session_id  TEXT REFERENCES sessions (id) ON DELETE CASCADE,
  message_id  TEXT REFERENCES messages (id) ON DELETE CASCADE,
  path        TEXT NOT NULL,
  size        INTEGER NOT NULL DEFAULT 0,
  checksum    TEXT,
  created_at  INTEGER NOT NULL
);
CREATE INDEX attachments_by_path ON attachments (path);
CREATE INDEX attachments_by_message ON attachments (message_id);

CREATE VIRTUAL TABLE messages_fts USING fts5(
  message_id UNINDEXED,
  session_id UNINDEXED,
  title,
  content,
  tokenize = 'unicode61'
);
