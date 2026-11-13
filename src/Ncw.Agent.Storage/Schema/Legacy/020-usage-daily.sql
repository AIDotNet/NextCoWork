-- 2.x 迁移 v20(usage-daily)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
CREATE TABLE usage_daily (
  -- 本地日期 YYYY-MM-DD。跟着写库时的时区走,所以刷新器发现时区变了会整表重建
  day                   TEXT NOT NULL,
  provider_id           TEXT NOT NULL,
  provider_name         TEXT NOT NULL DEFAULT '',
  upstream_model        TEXT NOT NULL,
  -- 展示用:界面优先显示别名(参考图里的「GLM-5.3」),定价与分组仍按 upstream_model
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

  cost_micros           INTEGER,
  priced_count          INTEGER NOT NULL DEFAULT 0,

  latency_sum           INTEGER NOT NULL DEFAULT 0,
  ttft_sum              INTEGER NOT NULL DEFAULT 0,
  ttft_count            INTEGER NOT NULL DEFAULT 0,

  PRIMARY KEY (day, provider_id, upstream_model, currency)
);

-- 趋势图与热力图都是「给我这段日期的所有行」,按 day 收窄后行数已经很小
CREATE INDEX usage_daily_by_day ON usage_daily (day);
