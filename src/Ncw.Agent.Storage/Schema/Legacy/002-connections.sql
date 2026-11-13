-- 2.x 迁移 v2(connections)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
CREATE TABLE mcp_servers (
  id   TEXT PRIMARY KEY,
  json TEXT NOT NULL
);

CREATE TABLE search_providers (
  id       TEXT PRIMARY KEY,
  -- 提成真列:web_search 按它升序依次尝试,失败切下一家(见 main/search/service.ts)
  priority INTEGER NOT NULL,
  json     TEXT NOT NULL
);
CREATE INDEX search_providers_by_priority ON search_providers (priority);
