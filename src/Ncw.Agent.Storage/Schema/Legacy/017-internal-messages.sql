-- 2.x 迁移 v17(internal-messages)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
ALTER TABLE messages ADD COLUMN internal INTEGER NOT NULL DEFAULT 0;
