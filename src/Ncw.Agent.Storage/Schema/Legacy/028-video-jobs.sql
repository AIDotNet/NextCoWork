-- 2.x 迁移 v28(video-jobs)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
CREATE TABLE video_generation_jobs (
  id                     TEXT PRIMARY KEY,
  session_id             TEXT REFERENCES sessions (id) ON DELETE CASCADE,
  workspace_id           TEXT NOT NULL,
  config_profile         TEXT NOT NULL,
  cloud                  TEXT NOT NULL,
  retrieval              TEXT NOT NULL,
  credential_fingerprint TEXT,
  upstream_id            TEXT,
  created_at             INTEGER NOT NULL,
  updated_at             INTEGER NOT NULL,
  revision               INTEGER NOT NULL,
  json                   TEXT NOT NULL
);
CREATE INDEX video_jobs_by_session ON video_generation_jobs (session_id, created_at DESC);
CREATE INDEX video_jobs_by_recovery ON video_generation_jobs (cloud, retrieval, updated_at);
