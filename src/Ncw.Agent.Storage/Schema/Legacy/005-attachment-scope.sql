-- 2.x 迁移 v5(attachment-scope)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
-- 既有行全部是「会话附件且已提交」——这正是加这两列之前唯一可能存在的形态,
-- 所以默认值就是对历史数据的正确回填,不需要额外的 UPDATE。
ALTER TABLE attachments ADD COLUMN scope TEXT NOT NULL DEFAULT 'session';
ALTER TABLE attachments ADD COLUMN status TEXT NOT NULL DEFAULT 'committed';

-- ★ owner_id 与 session_id 是**两回事**,不是冗余:
--
-- session_id 上有指向 sessions 的外键,而附件是在**发送之前**上传的 ——
-- 用户新建对话、还没发第一条消息时,sessions 表里没有那一行。
-- 上传时往 session_id 里填就会直接违反外键。
--
-- 所以:draft 行填 owner_id(无外键),消息提交时由 recordMessageAttachments
-- 填上 session_id —— 那一刻会话行必然已经存在,CASCADE 从此生效。
ALTER TABLE attachments ADD COLUMN owner_id TEXT;
UPDATE attachments SET owner_id = session_id WHERE owner_id IS NULL;

-- 清理按 (status, created_at) 扫:找「draft 且超期」的那批
CREATE INDEX attachments_by_status ON attachments (status, created_at);
-- 上传去重按 (checksum, scope, owner_id) 查
CREATE INDEX attachments_by_checksum ON attachments (checksum, scope, owner_id);
