-- 2.x 迁移 v16(imports)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
CREATE TABLE import_sources (
  source_id      TEXT PRIMARY KEY,
  kind           TEXT NOT NULL,
  -- 已授权的配置目录绝对路径。空串 = 曾经登记过但现在找不到了。
  config_dir     TEXT NOT NULL DEFAULT '',
  origin         TEXT NOT NULL DEFAULT 'auto',
  sync_enabled   INTEGER NOT NULL DEFAULT 0,
  -- 已授权类别与项目。JSON 数组:整读整写,从不按元素查询(见文件头的判据)。
  categories     TEXT NOT NULL DEFAULT '[]',
  project_keys   TEXT NOT NULL DEFAULT '[]',
  last_check_at  INTEGER,
  last_sync_at   INTEGER,
  status         TEXT NOT NULL DEFAULT 'off',
  diagnostics    TEXT NOT NULL DEFAULT '[]',
  created_at     INTEGER NOT NULL,
  updated_at     INTEGER NOT NULL
);

CREATE TABLE import_mappings (
  source_id          TEXT NOT NULL,
  -- '' = 全局作用域;否则是 workspace id。见文件头。
  scope_key          TEXT NOT NULL DEFAULT '',
  entity_kind        TEXT NOT NULL,
  source_item_id     TEXT NOT NULL,
  target_id          TEXT NOT NULL DEFAULT '',
  target_path        TEXT NOT NULL DEFAULT '',
  target_workspace_id TEXT NOT NULL DEFAULT '',
  source_fingerprint TEXT NOT NULL DEFAULT '',
  target_fingerprint TEXT NOT NULL DEFAULT '',
  -- 转换器版本。解析规则改了之后,旧映射要能被认出来重做。
  transformer_version INTEGER NOT NULL DEFAULT 1,
  -- linked | detached | conflict | source-missing | suppressed
  sync_state         TEXT NOT NULL DEFAULT 'linked',
  -- 来源模型名/时间/标题这类只用于显示的元数据。不按字段查,留 JSON。
  meta               TEXT NOT NULL DEFAULT '{}',
  created_at         INTEGER NOT NULL,
  updated_at         INTEGER NOT NULL,
  PRIMARY KEY (source_id, scope_key, entity_kind, source_item_id)
);
-- 反查:本地实体被删/被改时要找到它对应的那条映射
CREATE INDEX import_mappings_by_target ON import_mappings (target_id, entity_kind);
-- 同步一轮要按 (source, kind, state) 收窄
CREATE INDEX import_mappings_by_state ON import_mappings (source_id, entity_kind, sync_state);

CREATE TABLE import_batches (
  id         TEXT PRIMARY KEY,
  source_id  TEXT NOT NULL,
  source_kind TEXT NOT NULL,
  trigger    TEXT NOT NULL,
  phase      TEXT NOT NULL,
  started_at INTEGER NOT NULL,
  ended_at   INTEGER,
  -- ImportCounts 六个数。整读整写。
  counts     TEXT NOT NULL DEFAULT '{}'
);
CREATE INDEX import_batches_by_started ON import_batches (started_at DESC, id DESC);

-- ★ 明细只记「哪一项、去了哪、结果如何」,**不复制转录正文或原始配置**。
--   复制正文等于在审计表里留第二份用户数据,而它既不参与任何查询,
--   又要跟着「清空对话历史」一起被想起来清掉。
CREATE TABLE import_batch_items (
  batch_id            TEXT NOT NULL REFERENCES import_batches (id) ON DELETE CASCADE,
  seq                 INTEGER NOT NULL,
  category            TEXT NOT NULL,
  title               TEXT NOT NULL DEFAULT '',
  source_path         TEXT NOT NULL DEFAULT '',
  result              TEXT NOT NULL,
  target_kind         TEXT,
  target_id           TEXT,
  target_workspace_id TEXT,
  diagnostics         TEXT NOT NULL DEFAULT '[]',
  PRIMARY KEY (batch_id, seq)
);
