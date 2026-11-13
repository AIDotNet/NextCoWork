-- 2.x 迁移 v31(device-interactions)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
CREATE TABLE device_interactions (
  device_id TEXT NOT NULL,
  data_epoch TEXT NOT NULL,
  config_scope TEXT NOT NULL,
  id TEXT NOT NULL,
  scope_epoch TEXT NOT NULL,
  run_id TEXT NOT NULL,
  kind TEXT NOT NULL CHECK(kind IN ('permission','question','plan','plugin','goal')),
  payload BLOB NOT NULL,
  payload_digest TEXT NOT NULL,
  status TEXT NOT NULL CHECK(status IN ('pending','resolved','cancelled')),
  answer BLOB,
  resolved_by TEXT,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  expires_at INTEGER,
  PRIMARY KEY (device_id, data_epoch, config_scope, id)
);
-- 待决列表按状态扫描,再按时间排;账号代际和 run 各留一条,便于恢复时局部收敛。
CREATE INDEX device_interactions_by_status ON device_interactions(device_id, data_epoch, config_scope, status, created_at);
CREATE INDEX device_interactions_by_account ON device_interactions(device_id, data_epoch, config_scope, scope_epoch, created_at);
CREATE INDEX device_interactions_by_run ON device_interactions(device_id, data_epoch, config_scope, run_id, created_at);
