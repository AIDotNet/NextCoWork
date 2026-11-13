-- 2.x 迁移 v10(subagent-sessions)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
ALTER TABLE sessions ADD COLUMN parent_session_id TEXT;
-- 两种形状共用:过滤按 IS NULL 扫,级联按 = ? 查子。
CREATE INDEX sessions_by_parent ON sessions (parent_session_id, updated_at DESC);

-- 存量脏行连根删掉。顶层 id 是 ULID(不含冒号),「:sub:」 只可能来自
-- childRequestFor,所以这条判据不会误伤用户自己的对话。
--
-- ★ 顺序不能换。messages_fts 是虚表、attachments 的草稿行挂的是 owner_id,
--   这两处都没有外键管得着,必须赶在 DELETE FROM sessions 之前;
--   messages / runs / context_checkpoints / 已提交附件由外键 CASCADE 带走
--   (migrate() 全程开着 enableForeignKeyConstraints)。
DELETE FROM messages_fts WHERE session_id IN (SELECT id FROM sessions WHERE instr(id, ':sub:') > 0);
DELETE FROM attachments WHERE scope = 'session' AND owner_id IN (SELECT id FROM sessions WHERE instr(id, ':sub:') > 0);
DELETE FROM kv WHERE key IN (SELECT 'session.input.' || id FROM sessions WHERE instr(id, ':sub:') > 0);
DELETE FROM sessions WHERE instr(id, ':sub:') > 0;
