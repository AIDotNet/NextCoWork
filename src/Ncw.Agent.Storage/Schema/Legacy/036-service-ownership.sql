-- 2.x 迁移 v36(service-ownership)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
CREATE TABLE service_ownership (
  id             INTEGER PRIMARY KEY CHECK (id = 1),
  device_id      TEXT NOT NULL,
  journal_id     TEXT NOT NULL,
  backup_ref     TEXT NOT NULL,
  taken_over_at  INTEGER NOT NULL
);
