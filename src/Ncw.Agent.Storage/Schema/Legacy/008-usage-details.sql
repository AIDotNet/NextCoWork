-- 2.x 迁移 v8(usage-details)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
ALTER TABLE usage_records ADD COLUMN workspace_id TEXT NOT NULL DEFAULT '';
ALTER TABLE usage_records ADD COLUMN session_id TEXT NOT NULL DEFAULT '';
ALTER TABLE usage_records ADD COLUMN attempt INTEGER NOT NULL DEFAULT 1;
ALTER TABLE usage_records ADD COLUMN provider_name TEXT NOT NULL DEFAULT '';
ALTER TABLE usage_records ADD COLUMN protocol TEXT NOT NULL DEFAULT 'unknown';
ALTER TABLE usage_records ADD COLUMN endpoint TEXT NOT NULL DEFAULT '';
ALTER TABLE usage_records ADD COLUMN response_model TEXT;
ALTER TABLE usage_records ADD COLUMN thinking_tokens INTEGER;
ALTER TABLE usage_records ADD COLUMN thinking_tokens_estimated INTEGER NOT NULL DEFAULT 0;
ALTER TABLE usage_records ADD COLUMN time_to_first_token_ms INTEGER;
ALTER TABLE usage_records ADD COLUMN stop_reason TEXT;
ALTER TABLE usage_records ADD COLUMN error_message TEXT;

CREATE INDEX usage_records_by_run ON usage_records (run_id, at DESC, id DESC);
CREATE INDEX usage_records_by_session ON usage_records (session_id, at DESC, id DESC);
