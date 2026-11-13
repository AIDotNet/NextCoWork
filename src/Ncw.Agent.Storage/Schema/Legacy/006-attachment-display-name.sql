-- 2.x 迁移 v6(attachment-display-name)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
ALTER TABLE attachments ADD COLUMN display_name TEXT;
