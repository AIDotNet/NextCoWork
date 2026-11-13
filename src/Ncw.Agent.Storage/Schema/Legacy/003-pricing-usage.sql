-- 2.x 迁移 v3(pricing-usage)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
CREATE TABLE model_pricing (
  -- NULL = 通用基础定价;非 NULL = 该厂商的价(见上面那段,**不要**加外键)
  provider_id     TEXT,
  model_id        TEXT NOT NULL,
  display_name    TEXT NOT NULL,
  currency        TEXT NOT NULL,
  modality        TEXT NOT NULL,
  -- 一整套费率(含阶梯)。从不按字段查询、结构还会长(perCall 之类),所以留 JSON
  tiers           TEXT NOT NULL,
  windows         TEXT,
  -- YYYY-MM-DD,NULL = 该侧无界。厂商会预告调价,同一模型可以有多行
  effective_from  TEXT,
  effective_until TEXT,
  source          TEXT NOT NULL,
  fetched_at      TEXT NOT NULL,
  -- ★ 这一行是不是来自仓库种子表。pricing:resetToSeed 靠它区分「重置」的边界:
  -- 没有这一列,重置要么连用户手工录的覆盖价一起抹掉,要么根本没法只换种子那部分。
  is_seed         INTEGER NOT NULL DEFAULT 0
);

-- 见上面:NULL 不参与 UNIQUE 比较,所以折成空串再比
CREATE UNIQUE INDEX model_pricing_key
  ON model_pricing (model_id, COALESCE(provider_id, ''), COALESCE(effective_from, ''));

-- findPricing 先按 model_id 收窄,再在少数几行里挑 provider 与日期区间
CREATE INDEX model_pricing_by_model ON model_pricing (model_id);

-- 一次 HTTP 尝试一条(方案 §5.2)。首字节后禁止切换意味着一次逻辑请求可能有多条,
-- 合并成一条就算不出参考图要的「请求成功率」。
CREATE TABLE usage_records (
  id             TEXT PRIMARY KEY,
  at             INTEGER NOT NULL,
  -- 同一次逻辑请求的多条尝试共用它。日志视图按它分组
  run_id         TEXT NOT NULL,
  provider_id    TEXT NOT NULL,
  alias          TEXT NOT NULL,
  -- ★ 上游真实模型名,不是别名 —— 定价按它查(方案 §4.1)
  upstream_model TEXT NOT NULL,

  -- ★ token 数用 NOT NULL DEFAULT 0,和「价格绝不填 0」是**两件事**:
  -- 缺一个价意味着「我们不知道多少钱」,填 0 会静默变成「免费」;
  -- 而 TokenUsage 里缺一项缓存字段就是真的没有那类 token,0 是它准确的值。
  input_tokens            INTEGER NOT NULL DEFAULT 0,
  output_tokens           INTEGER NOT NULL DEFAULT 0,
  cache_read_tokens       INTEGER NOT NULL DEFAULT 0,
  cache_write_tokens      INTEGER NOT NULL DEFAULT 0,
  cache_write_1h_tokens   INTEGER NOT NULL DEFAULT 0,

  latency_ms     INTEGER NOT NULL,
  ok             INTEGER NOT NULL,
  http_status    INTEGER,
  error_kind     TEXT,

  -- ★★ **可空,而且 NULL 与 0 必须分得开。** NULL = 查不到定价,
  -- 0 = 真的不要钱。PricingTable 顶部那张「用过但查不到定价」的表就是
  --   SELECT DISTINCT upstream_model ... WHERE cost_micros IS NULL
  -- 而「种子表把 modelId 抄错了」这一类错,唯一的外显方式就是费用列永远是「—」。
  --
  -- 记账时算好并**冻结**(方案 §5.2):事后改定价表不改历史账单。
  cost_micros    INTEGER,
  currency       TEXT,
  pricing_tier   INTEGER,
  pricing_window TEXT,

  tool_calls     INTEGER NOT NULL DEFAULT 0,
  tool_errors    INTEGER NOT NULL DEFAULT 0
);

-- 五条 usage:* 查询**每一条**都带时间窗(方案 §5.4)。带上 id 是为了让
-- getRequestLogs 的游标是全序的:同毫秒的两条记录不加 id 就会在翻页时重复或漏掉。
CREATE INDEX usage_records_by_at ON usage_records (at DESC, id DESC);

-- getModelStats / getProviderStats:先按维度收窄再按时间窗过滤
CREATE INDEX usage_records_by_model ON usage_records (upstream_model, at DESC);
CREATE INDEX usage_records_by_provider ON usage_records (provider_id, at DESC);
