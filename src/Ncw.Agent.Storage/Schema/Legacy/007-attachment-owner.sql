-- 2.x 迁移 v7(attachment-owner)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
-- 第 5 条建立的是不含 owner_id 的旧索引;换成完整的去重键。
DROP INDEX IF EXISTS attachments_by_checksum;
CREATE INDEX attachments_by_checksum ON attachments (checksum, scope, owner_id);
