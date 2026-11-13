-- 2.x 迁移 v22(config-profiles)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
CREATE TABLE config_profiles (
  scope TEXT PRIMARY KEY,
  json  TEXT NOT NULL
);

ALTER TABLE workspaces ADD COLUMN owner TEXT NOT NULL DEFAULT 'local';
-- 每个作用域的首屏列表都按 owner + last_opened_at 取,收窄后行数已经很小
CREATE INDEX workspaces_by_owner ON workspaces (owner, last_opened_at DESC);
