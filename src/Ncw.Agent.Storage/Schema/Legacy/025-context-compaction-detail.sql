-- 2.x 迁移 v25(context-compaction-detail)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
ALTER TABLE context_checkpoints ADD COLUMN detail TEXT;
