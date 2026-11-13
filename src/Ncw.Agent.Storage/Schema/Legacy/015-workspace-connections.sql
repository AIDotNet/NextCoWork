-- 2.x 迁移 v15(workspace-connections)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
CREATE TABLE connection_profiles (id TEXT PRIMARY KEY, json TEXT NOT NULL);
