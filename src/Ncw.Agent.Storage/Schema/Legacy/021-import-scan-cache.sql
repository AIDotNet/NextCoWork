-- 2.x 迁移 v21(import-scan-cache)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
CREATE TABLE import_scan_cache (
  source_id      TEXT NOT NULL,
  -- 绝对路径。OpenCode 这类库内会话用 `opencode.db#<id>` 这种合成路径。
  source_path    TEXT NOT NULL,
  -- size:mtime。和源文件一比就知道整行能不能复用。
  change_token   TEXT NOT NULL,
  -- 解析出来的会话 id(去重与映射键都要用它)。
  session_id     TEXT NOT NULL DEFAULT '',
  -- '' = 只读了头部,没算过全文哈希。
  content_hash   TEXT NOT NULL DEFAULT '',
  cwd            TEXT NOT NULL DEFAULT '',
  title          TEXT NOT NULL DEFAULT '',
  model          TEXT NOT NULL DEFAULT '',
  model_provider TEXT NOT NULL DEFAULT '',
  -- partial = 1 时这是下界。
  messages       INTEGER NOT NULL DEFAULT 0,
  partial        INTEGER NOT NULL DEFAULT 0,
  source_updated_at INTEGER NOT NULL DEFAULT 0,
  scanned_at     INTEGER NOT NULL,
  PRIMARY KEY (source_id, source_path)
);
-- 扫完一轮要按来源清掉本轮没再见到的行(源侧文件已删)
CREATE INDEX import_scan_cache_by_source ON import_scan_cache (source_id, scanned_at);
