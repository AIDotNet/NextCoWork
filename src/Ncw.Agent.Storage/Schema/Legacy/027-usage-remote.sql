-- 2.x 迁移 v27(usage-remote)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
CREATE TABLE usage_daily_remote (
  account_id            TEXT NOT NULL,
  -- 写入设备的 originId（那台机器的 ensureSyncDeviceId），一台设备一片
  origin_id             TEXT NOT NULL,
  day                   TEXT NOT NULL,
  provider_id           TEXT NOT NULL,
  provider_name         TEXT NOT NULL DEFAULT '',
  upstream_model        TEXT NOT NULL,
  alias                 TEXT NOT NULL DEFAULT '',
  currency              TEXT NOT NULL DEFAULT '',
  request_count         INTEGER NOT NULL DEFAULT 0,
  success_count         INTEGER NOT NULL DEFAULT 0,
  input_tokens          INTEGER NOT NULL DEFAULT 0,
  output_tokens         INTEGER NOT NULL DEFAULT 0,
  cache_read_tokens     INTEGER NOT NULL DEFAULT 0,
  cache_write_tokens    INTEGER NOT NULL DEFAULT 0,
  cache_write_1h_tokens INTEGER NOT NULL DEFAULT 0,
  thinking_tokens       INTEGER NOT NULL DEFAULT 0,
  -- ★ 与 usage_daily 同义：NULL = 这一桶一条也算不出钱，不是 0
  cost_micros           INTEGER,
  priced_count          INTEGER NOT NULL DEFAULT 0,
  latency_sum           INTEGER NOT NULL DEFAULT 0,
  ttft_sum              INTEGER NOT NULL DEFAULT 0,
  ttft_count            INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (account_id, origin_id, day, provider_id, upstream_model, currency)
);
CREATE INDEX usage_daily_remote_by_day ON usage_daily_remote (account_id, day);

-- 每台远端设备一行：它自己算出的「最长一场聊天」（依赖 messages，本机替它算不了）。
-- 行数本身也是「这个账号下有几台别的设备贡献了数据」的来源。
CREATE TABLE usage_remote_origin (
  account_id      TEXT NOT NULL,
  origin_id       TEXT NOT NULL,
  longest_chat_ms INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (account_id, origin_id)
);
