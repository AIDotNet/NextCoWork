-- 2.x 迁移 v26(drop-context-checkpoints)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
DROP TABLE IF EXISTS context_checkpoints;
