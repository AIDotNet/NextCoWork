-- 2.x 迁移 v35(device-trust)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
CREATE TABLE device_controllers (
  controller_id      TEXT PRIMARY KEY,
  fingerprint_sha256 TEXT NOT NULL UNIQUE CHECK (length(fingerprint_sha256) = 64),
  label              TEXT NOT NULL,
  paired_at          INTEGER NOT NULL,
  revoked_at         INTEGER
);

CREATE TABLE device_enrollments (
  token_hash  TEXT PRIMARY KEY CHECK (length(token_hash) = 64),
  created_at  INTEGER NOT NULL,
  expires_at  INTEGER NOT NULL,
  consumed_at INTEGER,
  consumed_by TEXT
);
CREATE INDEX idx_device_enrollments_expiry ON device_enrollments (expires_at);
