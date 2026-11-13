-- 2.x 迁移 v34(session-input-receipts)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
CREATE TABLE session_input_receipts (
  -- 有外键:会话整体被删时这些回执也只是垃圾,跟着走
  session_id   TEXT NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
  -- ★ 裸 id,**没有**外键 —— 见上面那段:转录压缩/删除不能抹掉消费事实
  message_id   TEXT NOT NULL,
  committed_at INTEGER NOT NULL,
  PRIMARY KEY (session_id, message_id)
);

-- 回填:第 34 条之前提交过的 user 消息全都是已消费的输入。
-- 这里能安全地 SELECT messages —— 迁移跑的这一刻它们还在。
INSERT OR IGNORE INTO session_input_receipts (session_id, message_id, committed_at)
  SELECT session_id, id, created_at FROM messages WHERE role = 'user';
