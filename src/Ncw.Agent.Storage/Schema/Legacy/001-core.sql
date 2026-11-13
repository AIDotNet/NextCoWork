-- 2.x 迁移 v1(core)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
-- 单例行。用 CHECK 把「只能有一行」写进表里,而不是靠每个写入点都记得用 id=1:
-- 多出来的第二行不会报错,只会让 getSettings 随机读到其中一行。
CREATE TABLE settings (
  id   INTEGER PRIMARY KEY CHECK (id = 1),
  json TEXT NOT NULL
);

-- 窗口/Tab 布局这类易失 UI 状态。键名收在 state/store.ts 的 outerTabKey / innerTabKey。
CREATE TABLE kv (
  key  TEXT PRIMARY KEY,
  json TEXT NOT NULL
);

CREATE TABLE workspaces (
  id             TEXT PRIMARY KEY,
  -- 提成真列:listWorkspaces() 按它倒序,首屏「开哪个工作区」依赖这个顺序
  last_opened_at INTEGER NOT NULL,
  json           TEXT NOT NULL
);
CREATE INDEX workspaces_by_last_opened ON workspaces (last_opened_at DESC);

CREATE TABLE providers (
  id       TEXT PRIMARY KEY,
  -- 提成真列:故障切换按它挑下一个候选(方案 §5.3),顺序是有语义的
  priority INTEGER NOT NULL,
  json     TEXT NOT NULL
);
CREATE INDEX providers_by_priority ON providers (priority);

-- ★ 主键是 (provider_id, alias) 的**复合主键**,不是 alias。
-- 同一个 alias 由多个 provider 提供正是别名表存在的理由(方案 §5.2):
-- 用 alias 当主键,故障切换就只剩一个候选。
--
-- 顺带消掉了一个 Map 实现里必须自己防的坑:拼接字符串当键时
--   "a" + "|b" 和 "a|" + "b" 会撞成同一个键,旧实现为此选了 U+0000 当分隔符。
-- 复合主键下这件事结构上就不可能发生。
CREATE TABLE model_aliases (
  provider_id TEXT NOT NULL REFERENCES providers (id) ON DELETE CASCADE,
  alias       TEXT NOT NULL,
  json        TEXT NOT NULL,
  PRIMARY KEY (provider_id, alias)
);

-- 上游密钥。**存的是程序 AES-256-GCM 密文,不是明文** —— NCK1 格式与
-- 加解密逻辑都在 main/secrets/credential-crypto.ts,主密钥是同目录 0600 文件。
-- 升级期间还可能有旧 safeStorage 行;host 按 magic 双格式读并在启动时迁移。
CREATE TABLE credentials (
  ref  TEXT PRIMARY KEY,
  blob BLOB NOT NULL
);
