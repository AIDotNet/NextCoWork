-- 2.x 迁移 v30(device-command-receipts)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
CREATE TABLE device_commands (
  device_id TEXT NOT NULL,
  data_epoch TEXT NOT NULL,
  config_scope TEXT NOT NULL,
  controller_id TEXT NOT NULL,
  idempotency_key TEXT NOT NULL,
  method TEXT NOT NULL,
  params_digest TEXT NOT NULL,
  operation_id TEXT NOT NULL UNIQUE,
  status TEXT NOT NULL CHECK(status IN ('accepting','accepted','running','succeeded','failed','unknown','interrupted')),
  result BLOB,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  PRIMARY KEY (device_id, data_epoch, config_scope, controller_id, idempotency_key)
);
CREATE TABLE device_events (
  seq INTEGER PRIMARY KEY AUTOINCREMENT,
  device_id TEXT NOT NULL,
  data_epoch TEXT NOT NULL,
  config_scope TEXT NOT NULL,
  controller_id TEXT NOT NULL,
  event_type TEXT NOT NULL,
  data BLOB NOT NULL,
  created_at INTEGER NOT NULL
);
CREATE INDEX device_events_by_scope ON device_events(device_id, data_epoch, config_scope, controller_id, seq);
