-- 2.x 迁移 v37(message-fts-rowid)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
CREATE TABLE messages_fts_rowid (
  fts_rowid   INTEGER PRIMARY KEY,
  message_id  TEXT NOT NULL UNIQUE,
  session_id  TEXT NOT NULL
);
INSERT OR IGNORE INTO messages_fts_rowid (fts_rowid, message_id, session_id)
  SELECT rowid, message_id, session_id FROM messages_fts ORDER BY rowid DESC;
DELETE FROM messages_fts_rowid WHERE message_id NOT IN (SELECT id FROM messages);
DELETE FROM messages_fts WHERE rowid NOT IN (SELECT fts_rowid FROM messages_fts_rowid);
CREATE INDEX messages_fts_rowid_by_session ON messages_fts_rowid (session_id);
