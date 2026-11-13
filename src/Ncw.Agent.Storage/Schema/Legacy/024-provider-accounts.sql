-- 2.x 迁移 v24(provider-accounts)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
CREATE TABLE provider_accounts (
  id            TEXT PRIMARY KEY,
  provider_id   TEXT NOT NULL,
  issuer        TEXT NOT NULL,
  label         TEXT,
  sort_order    INTEGER NOT NULL DEFAULT 0,
  enabled       INTEGER NOT NULL DEFAULT 1,
  is_current    INTEGER NOT NULL DEFAULT 0,
  -- ★★ 从密文里**反范式**出来的一个位:刷新被上游明确拒过(invalid_grant)。
  -- 真值住在凭证 JSON 的 needsReauth 里,但「这一刻用哪个账号」发生在请求的
  -- 热路径上、必须同步,而解密是异步的。不反范式的话,每选一次账号都要先解一次
  -- 密文(四个并发子代理就是四次),且解密失败会让「选账号」这件事本身可能抛。
  -- 写入点只有一处:ipc/provider-auth.ts 的 announce —— 凭证一变就跟着写这一列。
  needs_reauth  INTEGER NOT NULL DEFAULT 0,
  -- 限流闸门。四列同进同出:until 为 NULL 即「没被限流」,其余三列一并无意义
  limit_until   INTEGER,
  limit_since   INTEGER,
  limit_source  TEXT,
  limit_reason  TEXT,
  -- ProviderQuotaSnapshot 的 JSON。只有 ChatGPT 那家会有值
  quota_json    TEXT,
  created_at    INTEGER NOT NULL,
  updated_at    INTEGER NOT NULL
);
-- 读路径只有一条:「这家的账号,按轮换顺序」。写多了索引只是给每次写多一棵 B 树
CREATE INDEX provider_accounts_by_provider ON provider_accounts (provider_id, sort_order);
