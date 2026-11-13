-- 2.x 迁移 v29(rebuild-message-search)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
DELETE FROM messages_fts;
