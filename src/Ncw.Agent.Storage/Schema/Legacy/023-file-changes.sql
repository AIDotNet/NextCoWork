-- 2.x 迁移 v23(file-changes)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
CREATE TABLE file_change_sets (
  run_id       TEXT PRIMARY KEY,
  session_id   TEXT NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
  workspace_id TEXT NOT NULL,
  -- 'applied' | 'reverted' —— 整轮原子撤销态
  state        TEXT NOT NULL DEFAULT 'applied',
  file_count   INTEGER NOT NULL DEFAULT 0,
  additions    INTEGER NOT NULL DEFAULT 0,
  deletions    INTEGER NOT NULL DEFAULT 0,
  created_at   INTEGER NOT NULL,
  updated_at   INTEGER NOT NULL
);
-- 会话侧「这条会话有哪些轮改过文件」的列表查询
CREATE INDEX file_change_sets_by_session ON file_change_sets (session_id);

CREATE TABLE file_snapshots (
  run_id         TEXT NOT NULL REFERENCES file_change_sets (run_id) ON DELETE CASCADE,
  file_path      TEXT NOT NULL,
  -- 真正写它的 run(可能是子代理的 child run);run_id 存的是顶层 root
  source_run_id  TEXT NOT NULL,
  -- 解析后的绝对路径,内核 host.fs 读写用
  abs_path       TEXT NOT NULL,
  -- 'created' | 'modified' | (预留 'deleted')
  change_kind    TEXT NOT NULL,
  -- created 或 oversize 时为 NULL
  before_content TEXT,
  after_content  TEXT,
  before_hash    TEXT,
  after_hash     TEXT NOT NULL,
  additions      INTEGER NOT NULL DEFAULT 0,
  deletions      INTEGER NOT NULL DEFAULT 0,
  -- 1 = 单文件超 1 MiB,内容存 NULL,禁 diff/undo
  oversize       INTEGER NOT NULL DEFAULT 0,
  -- 0 = 改到了工作区外的文件,禁 undo
  in_workspace   INTEGER NOT NULL DEFAULT 1,
  created_at     INTEGER NOT NULL,
  PRIMARY KEY (run_id, file_path)
);
