/**
 * 建表脚本。**只增不改** —— 每条迁移一旦发布过就是历史,改它等于让已经升过级的
 * 库和新装的库长得不一样,而这种分叉没有任何机制会报警。要改结构就追加下一条。
 *
 * ## 为什么大部分表是「JSON 一列 + 少数几列真字段」
 *
 * 这不是偷懒,是按**查询形状**分的:
 *
 * - `settings` / `kv` / `workspaces` / `providers` / `model_aliases` 全都是
 *   **整行读、整行写**,行数在几十这个量级,没有一处按字段过滤或聚合。
 *   拆成真列换不到任何查询能力,却要为领域类型的每一次增删字段写一条迁移 ——
 *   而 `ModelAlias` 接下来就要加 `modality` / `enabled` / `order` 三个字段
 *   (方案 §1.2 / §1.4)。存 JSON 的话那三个字段一条迁移都不用写。
 * - 例外是**排序键**:`providers.priority` 和 `workspaces.last_opened_at`
 *   提到真列上,因为 `listProviders()` / `listWorkspaces()` 的顺序是有语义的
 *   (前者决定故障切换先切到谁),交给 SQLite 的 ORDER BY 比读回来再排稳。
 *
 * ★ 反过来说:`usage_records` 与 `model_pricing`(第 3 条)**必须是真列**,
 * 因为它们唯一的用法就是按时间窗聚合和按 `(provider_id, model_id)` 查找。
 * 判据是查询形状,不是「新表一律怎样」。
 *
 * ## 级联删除是结构性的,不再是记在心里的
 *
 * `model_aliases.provider_id` 上有真 FK + `ON DELETE CASCADE`。之前这条级联
 * 是 `store.removeProvider` 里手写的一个循环 —— 对的,但要靠人记得。
 * 悬空别名的症状离删除点很远(「模型还在下拉框里,选了却报『没有已启用的供应商』」),
 * 所以让数据库来保证它。**FK 的前提是连接级的 `PRAGMA foreign_keys=ON`**,
 * 见 `index.ts` 的 `openAt()`。
 */

export interface Migration {
  version: number
  name: string
  sql: string
}

/**
 * 第 1 条:最初的核心配置状态。会话转录在后续第 4 条迁移中追加，历史迁移
 * 本身保持不变；完整的当前结构以 `MIGRATIONS` 数组全部执行后的结果为准。
 */
const V1_CORE = `
-- 单例行。用 CHECK 把「只能有一行」写进表里,而不是靠每个写入点都记得用 id=1:
-- 多出来的第二行不会报错,只会让 getSettings 随机读到其中一行。
CREATE TABLE settings (
  id   INTEGER PRIMARY KEY CHECK (id = 1),
  json TEXT NOT NULL
);

-- 窗口/Tab 布局这类易失 UI 状态。键名收在 state/store.ts 的 outerTabKey / innerTabKey。
CREATE TABLE kv (
  key  TEXT PRIMARY KEY,
  json TEXT NOT NULL
);

CREATE TABLE workspaces (
  id             TEXT PRIMARY KEY,
  -- 提成真列:listWorkspaces() 按它倒序,首屏「开哪个工作区」依赖这个顺序
  last_opened_at INTEGER NOT NULL,
  json           TEXT NOT NULL
);
CREATE INDEX workspaces_by_last_opened ON workspaces (last_opened_at DESC);

CREATE TABLE providers (
  id       TEXT PRIMARY KEY,
  -- 提成真列:故障切换按它挑下一个候选(方案 §5.3),顺序是有语义的
  priority INTEGER NOT NULL,
  json     TEXT NOT NULL
);
CREATE INDEX providers_by_priority ON providers (priority);

-- ★ 主键是 (provider_id, alias) 的**复合主键**,不是 alias。
-- 同一个 alias 由多个 provider 提供正是别名表存在的理由(方案 §5.2):
-- 用 alias 当主键,故障切换就只剩一个候选。
--
-- 顺带消掉了一个 Map 实现里必须自己防的坑:拼接字符串当键时
--   "a" + "|b" 和 "a|" + "b" 会撞成同一个键,旧实现为此选了 U+0000 当分隔符。
-- 复合主键下这件事结构上就不可能发生。
CREATE TABLE model_aliases (
  provider_id TEXT NOT NULL REFERENCES providers (id) ON DELETE CASCADE,
  alias       TEXT NOT NULL,
  json        TEXT NOT NULL,
  PRIMARY KEY (provider_id, alias)
);

-- 上游密钥。**存的是程序 AES-256-GCM 密文,不是明文** —— NCK1 格式与
-- 加解密逻辑都在 main/secrets/credential-crypto.ts,主密钥是同目录 0600 文件。
-- 升级期间还可能有旧 safeStorage 行;host 按 magic 双格式读并在启动时迁移。
CREATE TABLE credentials (
  ref  TEXT PRIMARY KEY,
  blob BLOB NOT NULL
);
`

/**
 * 第 2 条:MCP 服务器与搜索服务。「设置 › 连接」那一页要存的两样东西。
 *
 * 两张表的形状**刻意不一样**,判据就是文件头那条「按查询形状分」:
 *
 * - `mcp_servers` 整行读、整行写,行数是个位数,顺序没有语义 → JSON 一列。
 *   而 `McpServerConfig` 是个三传输方式的可辨识联合(stdio 有 command/args/envNames,
 *   http/sse 有 url/headerNames),拆成真列就得为并集里每个字段留一列可空,
 *   然后靠代码记住「stdio 那行的 url 列没意义」—— 那正是 JSON 列要避免的事。
 * - `search_providers` 的 `priority` **提成真列**,理由和 `providers.priority`
 *   一模一样:「按优先级依次调用、失败切下一家」是它唯一的语义,
 *   交给 ORDER BY 比读回来再排稳。
 *
 * ★ **两张表里都没有一个明文密钥。** MCP 的 env / headers 值、搜索服务的 API Key
 * 全部在 `credentials` 表里(ref 分别是 `mcpSecretRef()` 和 `searchSecretRef()`),
 * 经程序主密钥加密。这也意味着删一行配置要顺手删对应的 credentials 行 ——
 * 那件事在 `repo.ts` 的 `removeMcpServer` 里做,不在这里,因为这张表不认识 ref 的构造规则。
 */
const V2_CONNECTIONS = `
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
`

/**
 * 第 3 条:定价表与用量记录(方案 §4 / §5)。
 *
 * 文件头那条「按查询形状分」在这两张表上给出的答案和前两条相反 ——
 * **查找键与聚合键必须是真列**,只有「一整套费率」这种从不按字段查的嵌套结构留 JSON。
 *
 * ─────────────────────────────────────────────────────────────
 * ★★ `model_pricing.provider_id` **不是** `providers.id`,所以这里没有外键
 * ─────────────────────────────────────────────────────────────
 *
 * 它是**厂商**维度的键(`presets.ts` 里的预设 id,如 `deepseek` / `zai`),
 * 不是「用户配的某一条连接」。理由是种子表 `pricing-seed.ts` 里的国内条目
 * **在用户配置任何供应商之前就存在** —— DeepSeek 的人民币价是这张表的初始内容,
 * 而那时 `providers` 表可能一行都没有。
 *
 * 所以加外键会**让整张种子表插不进去**。这条注释就是写给下一个看见
 * `provider_id` 就想补 `REFERENCES providers (id)` 的人的 —— 那不是加固,是把
 * 「重装后没有任何国内定价」变成开箱即有的行为。
 *
 * (跟着的推论:运行时查价传的是该 provider 的**厂商归属**,不是它的行 id。
 * 这条映射在步骤 3 落地 `provider:upsert` 时定形;DDL 对两种选择都成立,
 * 所以这里不替那个决定表态。)
 *
 * ★ NULL 在 UNIQUE 约束里彼此不相等,所以 `(provider_id, model_id, effective_from)`
 * 直接做主键**挡不住重复**:两条 `(NULL, 'gpt-5', NULL)` 会双双插入成功,
 * 然后 `findPricing` 随机读到其中一条。用 `COALESCE(...,'')` 的表达式唯一索引
 * 把这个洞补上,同时让列本身保持诚实的可空 —— 空值的含义(通用价 / 永远生效)
 * 是领域语义的一部分,不该为了绕开 SQLite 的 NULL 规则而编码成空串。
 */
const V3_PRICING_USAGE = `
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
`

/**
 * 第 4 条：会话转录与本机数据管理。
 *
 * 会话/消息的正文仍以 JSON 保存，避免把 ContentPart[] 压扁成字符串后丢掉
 * 工具调用、思考签名和图片引用。可查询字段单独提列，FTS 索引由 repository
 * 在 message_commit 边界维护。
 */
const V4_SESSIONS = `
CREATE TABLE sessions (
  id                   TEXT PRIMARY KEY,
  workspace_id         TEXT NOT NULL,
  title                TEXT NOT NULL,
  model                TEXT NOT NULL,
  mode                 TEXT NOT NULL,
  thinking             TEXT NOT NULL,
  root_path_at_creation TEXT NOT NULL,
  status               TEXT NOT NULL DEFAULT 'idle',
  archived             INTEGER NOT NULL DEFAULT 0,
  favorited            INTEGER NOT NULL DEFAULT 0,
  created_at           INTEGER NOT NULL,
  updated_at           INTEGER NOT NULL,
  json                 TEXT NOT NULL
);
CREATE INDEX sessions_by_workspace ON sessions (workspace_id, archived, updated_at DESC);
CREATE INDEX sessions_by_updated ON sessions (updated_at DESC);

CREATE TABLE messages (
  id             TEXT PRIMARY KEY,
  session_id     TEXT NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
  ordinal        INTEGER NOT NULL,
  role           TEXT NOT NULL,
  parts          TEXT NOT NULL,
  schema_version INTEGER NOT NULL DEFAULT 1,
  created_at     INTEGER NOT NULL,
  UNIQUE (session_id, ordinal)
);
CREATE INDEX messages_by_session ON messages (session_id, ordinal);

CREATE TABLE runs (
  id         TEXT PRIMARY KEY,
  session_id TEXT NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
  status     TEXT NOT NULL,
  started_at INTEGER NOT NULL,
  ended_at   INTEGER,
  json       TEXT
);
CREATE INDEX runs_by_session ON runs (session_id, started_at DESC);

CREATE TABLE attachments (
  id          TEXT PRIMARY KEY,
  session_id  TEXT REFERENCES sessions (id) ON DELETE CASCADE,
  message_id  TEXT REFERENCES messages (id) ON DELETE CASCADE,
  path        TEXT NOT NULL,
  size        INTEGER NOT NULL DEFAULT 0,
  checksum    TEXT,
  created_at  INTEGER NOT NULL
);
CREATE INDEX attachments_by_path ON attachments (path);
CREATE INDEX attachments_by_message ON attachments (message_id);

CREATE VIRTUAL TABLE messages_fts USING fts5(
  message_id UNINDEXED,
  session_id UNINDEXED,
  title,
  content,
  tokenize = 'unicode61'
);
`

/**
 * 第 5 条:附件的 scope 与生命周期。
 *
 * ## 为什么必须加这两列
 *
 * `attachments` 原本只记录**已经提交进消息**的会话附件,而清理规则是
 * 「扫附件根目录,不在表里的一律删」。那条规则默认「表覆盖全集」。
 *
 * 一旦附件根下开始放别的东西,这个默认就不成立了,而它的失效方式是**静默删文件**:
 *
 * - 主题图不在表里 → 被当孤儿删掉
 * - **用户刚上传、还没点发送的附件不在表里** → 挑好图去倒杯水,
 *   期间清理跑过一次,回来图没了
 *
 * `scope` 让清理能按子树分治,`status` 把孤儿的定义从「表里没有」
 * 收紧为「表里标记为可回收且已过宽限期」。
 *
 * ## 为什么是 ALTER 而不是重建表
 *
 * `attachments` 上有两条指向 `sessions`/`messages` 的外键。重建表要先关外键、
 * 拷数据、改名、再开 —— 而 SQLite 的 `PRAGMA foreign_keys` 是**连接级**的,
 * 在事务里改它不生效。两列都有默认值,ALTER 是安全且足够的。
 */
const V5_ATTACHMENT_SCOPE = `
-- 既有行全部是「会话附件且已提交」——这正是加这两列之前唯一可能存在的形态,
-- 所以默认值就是对历史数据的正确回填,不需要额外的 UPDATE。
ALTER TABLE attachments ADD COLUMN scope TEXT NOT NULL DEFAULT 'session';
ALTER TABLE attachments ADD COLUMN status TEXT NOT NULL DEFAULT 'committed';

-- ★ owner_id 与 session_id 是**两回事**,不是冗余:
--
-- session_id 上有指向 sessions 的外键,而附件是在**发送之前**上传的 ——
-- 用户新建对话、还没发第一条消息时,sessions 表里没有那一行。
-- 上传时往 session_id 里填就会直接违反外键。
--
-- 所以:draft 行填 owner_id(无外键),消息提交时由 recordMessageAttachments
-- 填上 session_id —— 那一刻会话行必然已经存在,CASCADE 从此生效。
ALTER TABLE attachments ADD COLUMN owner_id TEXT;
UPDATE attachments SET owner_id = session_id WHERE owner_id IS NULL;

-- 清理按 (status, created_at) 扫:找「draft 且超期」的那批
CREATE INDEX attachments_by_status ON attachments (status, created_at);
-- 上传去重按 (checksum, scope, owner_id) 查
CREATE INDEX attachments_by_checksum ON attachments (checksum, scope, owner_id);
`

/**
 * 第 6 条:附件的显示名。
 *
 * 磁盘文件名是 ULID(理由见 `shared/domain/attachment.ts`:路径注入、重名覆盖、
 * 跨平台非法字符),所以**用户看到的名字必须单独存一列**。
 *
 * 不加这一列的表现很具体:传了图 → 关掉应用 → 重开,草稿附件区里的 chip
 * 从「季度报表.png」变成「01J8XQZ4M7.png」。用户认不出哪张是哪张 ——
 * 而 ULID 恰恰是为了不让人认路径才选的。
 *
 * 可空:迁移之前的行没有这个信息,读的时候退回 `basename(path)`。
 * 那正是不加这列时的表现,所以旧数据不会更糟。
 */
const V6_ATTACHMENT_DISPLAY_NAME = `
ALTER TABLE attachments ADD COLUMN display_name TEXT;
`

/**
 * 第 7 条:附件 owner_id。
 *
 * owner_id 是草稿附件的归属键,不能复用带外键的 session_id:新会话在发送
 * 第一条消息前可能还没有 sessions 行。它原本误放进第 5 条迁移,导致已经
 * 执行过旧版第 5 条的用户永远拿不到这列(SQLite 不会重跑已记录的迁移)。
 * 因此这里必须追加新迁移,而不是修改历史迁移。
 */
const V7_ATTACHMENT_OWNER = `
-- 第 5 条建立的是不含 owner_id 的旧索引;换成完整的去重键。
DROP INDEX IF EXISTS attachments_by_checksum;
CREATE INDEX attachments_by_checksum ON attachments (checksum, scope, owner_id);
`

/**
 * 第 8 条：让用量记录足以回答一次请求为什么慢、为什么失败、以及统计值是否精确。
 *
 * 仍然只存路由和计量元数据，不保存提示词、回复正文、请求头或密钥。思考 Token
 * 可空是刻意的：不是每家供应商都会回传独立计数；从可见 thinking 文本推算时，
 * `thinking_tokens_estimated` 会明确标出来，不能把估算冒充账单真值。
 */
const V8_USAGE_DETAILS = `
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
`

/** 第 9 条：上下文窗口检查点。完整消息仍在 messages 表中，检查点只保存可编辑的派生笔记。 */
const V9_CONTEXT_MANAGEMENT = `
CREATE TABLE context_checkpoints (
  id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
  window_index INTEGER NOT NULL,
  note TEXT NOT NULL,
  source TEXT NOT NULL,
  covered_from_message_id TEXT,
  covered_through_message_id TEXT,
  input_tokens_before INTEGER,
  input_tokens_after INTEGER,
  search_hits TEXT,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  revision INTEGER NOT NULL DEFAULT 1,
  UNIQUE (session_id, window_index)
);
CREATE INDEX context_checkpoints_by_session ON context_checkpoints (session_id, window_index);
`

/**
 * 第 10 条：子代理转录不是一条对话。
 *
 * ## 这一列在挡什么
 *
 * 子代理 run 有自己的 sessionId(`runtime.childRequestFor`),于是每派一个子代理
 * 就在 `sessions` 表里留下一条真行 —— workspace 和父一样,标题永远是「新对话」
 * (标题生成只对 `depth === 0` 触发)。表现是:用户在侧边栏里数不清哪条是自己的对话。
 *
 * 它必须**留在表里**:`messages` / `runs` / `attachments` / `context_checkpoints`
 * 都对 `sessions` 有外键,而子代理的转录要落盘。所以「落盘」和「出现在列表里」
 * 得用一列分开,而不是靠不建行。
 *
 * ## 为什么判据是这一列,而不是 id 里的 `:sub:`
 *
 * 子会话 id 是**递归拼**出来的:`父.id || ':sub:' || 子runId`,而子 runId 自己
 * 又是 `父runId || ':sub:' || 序号`。于是 depth-1 的 id 里有 2 个 `:sub:`、
 * depth-2 有 5 个 —— 想从 id 反推父亲,第一个 `:sub:` 切出的是**爷爷**,
 * 最后一个切出的是一个**根本不存在的 id**。两种都错,而且错在不同方向。
 *
 * 所以运行期一律读这一列(`RunRequest.parentSessionId` 一路传下来),
 * 下面那几条 DELETE 是**唯一**一处解析 id 形状的地方,只用来认出存量脏行。
 *
 * ## 为什么不加真外键
 *
 * `REFERENCES sessions(id) ON DELETE CASCADE` 在 ADD COLUMN 上是合法的,也能白拿
 * 一层级联。但 `mergeDataExport` 是按导出顺序逐条 `putSession` 的,子会话完全可能
 * 排在父之前 —— 一次外键违反会让**整个导入事务回滚**。级联因此写在
 * `repo.deleteSession` 里(它还要顺带收 `messages_fts` 和草稿附件,那两处本来
 * 就没有外键管得着)。
 */
const V10_SUBAGENT_SESSIONS = `
ALTER TABLE sessions ADD COLUMN parent_session_id TEXT;
-- 两种形状共用:过滤按 IS NULL 扫,级联按 = ? 查子。
CREATE INDEX sessions_by_parent ON sessions (parent_session_id, updated_at DESC);

-- 存量脏行连根删掉。顶层 id 是 ULID(不含冒号),「:sub:」 只可能来自
-- childRequestFor,所以这条判据不会误伤用户自己的对话。
--
-- ★ 顺序不能换。messages_fts 是虚表、attachments 的草稿行挂的是 owner_id,
--   这两处都没有外键管得着,必须赶在 DELETE FROM sessions 之前;
--   messages / runs / context_checkpoints / 已提交附件由外键 CASCADE 带走
--   (migrate() 全程开着 enableForeignKeyConstraints)。
DELETE FROM messages_fts WHERE session_id IN (SELECT id FROM sessions WHERE instr(id, ':sub:') > 0);
DELETE FROM attachments WHERE scope = 'session' AND owner_id IN (SELECT id FROM sessions WHERE instr(id, ':sub:') > 0);
DELETE FROM kv WHERE key IN (SELECT 'session.input.' || id FROM sessions WHERE instr(id, ':sub:') > 0);
DELETE FROM sessions WHERE instr(id, ':sub:') > 0;
`

/**
 * 第 11 条：智能上下文管理改为默认关闭,存量库一起翻过来。
 *
 * ## 为什么非得动库,而不是只改 `DEFAULT_SETTINGS`
 *
 * `repo.updateSettings` 每次都把**整份合并后的 AppSettings** 序列化回这一行,
 * 而 `runtime.ts` 首次落默认模型时就会触发一次写入 —— 于是几乎每个老用户
 * 库里都存着 `experimentalMode: true`,哪怕他从没碰过那个开关。
 * `mergeSettings` 只在字段**缺席**时才铺新默认值,对这些行是彻底的空操作。
 *
 * ## 为什么不能学 `migrateLegacyProxy` 那套「只在缺失时才动」
 *
 * 学不了:字段从来不缺席,所以库里的 `true` **区分不出**「用户主动打开的」
 * 和「当年默认值被顺手写进来的」。这条迁移因此是无条件的,主动开过的人会被
 * 一起关掉 —— 这是明知的代价,换的是绝大多数从没做过选择的人拿到新默认值。
 *
 * ★ 一次性**由迁移表保证**,不是靠这条 SQL 自己幂等:版本号记进 `migrations`
 * 之后就不再执行。这一点是必须的 —— 每次启动都跑的话,用户在设置里重新打开,
 * 下次启动又被关上,正是 `mergeSettings` 那段注释里骂过的「我明明打开了,
 * 它自己关了」。
 *
 * `json('false')` 而不是 `0`:后者写进去是**数字**。读路径上它恰好也是假值,
 * 所以本地看不出任何异样 —— 但 `isContextManagementSettings` 要的是 `isBoolean`,
 * 而它一路挂在 `isAppSettings` → `isDataExport` 下面(`shared/domain/data.ts`)。
 * 于是这台机器导出的备份**整份**过不了校验,导入端拒绝的是整个文件,不是这一个
 * 字段。症状离这条 SQL 有十万八千里,所以这里必须一次写对。
 */
const V11_CONTEXT_EXPERIMENTAL_OFF = `
UPDATE settings
   SET json = json_set(json, '$.contextManagement.experimentalMode', json('false'))
 WHERE id = 1
   AND json_extract(json, '$.contextManagement.experimentalMode') = 1;
`

/**
 * 第 12 条：把每条消息归属到产出它的那一次 run。
 *
 * ★ 用量一直是落盘的(`usage_records` 有 `run_id`),缺的只是「哪一轮对应哪个 run」——
 * 少了这一跳,重启之后界面就只能显示本进程内存里攒出来的那个数,于是每一轮的
 * Token 读数在重启后集体消失,看着像是从来没记过账。
 *
 * ★ 可空,且**不回填**。历史消息无从知道自己属于哪个 run(usage_records 只有
 * session_id 和时间戳,按时间窗猜会把重试、并发子代理的账算到别人头上 ——
 * 错的数字比没有数字更糟)。旧对话继续不显示用量,新对话从此有。
 *
 * ★ 不建索引:读路径只有「按 session 拉全部消息」这一条,run_id 是随行读出的
 * 一个字段,从不作为查询条件。为它建索引只是给每次写多一棵 B 树要维护。
 */
const V12_MESSAGE_RUN = `
ALTER TABLE messages ADD COLUMN run_id TEXT;
`

/** 第 13 条：基础配置云同步的本地 outbox。会话/消息刻意不进入此表。 */
const V13_CONFIG_SYNC = `
CREATE TABLE sync_account (
  account_id TEXT PRIMARY KEY,
  device_id TEXT NOT NULL,
  enabled INTEGER NOT NULL DEFAULT 1,
  initial_sync_completed INTEGER NOT NULL DEFAULT 0,
  last_pull_cursor INTEGER NOT NULL DEFAULT 0,
  last_server_cursor INTEGER NOT NULL DEFAULT 0,
  last_success_at INTEGER,
  last_error TEXT,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL
);
CREATE TABLE sync_outbox (
  mutation_id TEXT PRIMARY KEY,
  account_id TEXT NOT NULL,
  device_id TEXT NOT NULL,
  client_seq INTEGER NOT NULL,
  kind TEXT NOT NULL,
  entity_id TEXT NOT NULL,
  operation TEXT NOT NULL,
  payload TEXT NOT NULL,
  base_revision INTEGER NOT NULL DEFAULT 0,
  workspace_id TEXT,
  created_at INTEGER NOT NULL,
  attempt_count INTEGER NOT NULL DEFAULT 0,
  next_attempt_at INTEGER NOT NULL,
  acked_at INTEGER,
  last_error TEXT
);
CREATE UNIQUE INDEX sync_outbox_sequence ON sync_outbox(account_id, client_seq);
CREATE INDEX sync_outbox_pending ON sync_outbox(account_id, acked_at, next_attempt_at, client_seq);
CREATE TABLE sync_conflict (
  id TEXT PRIMARY KEY,
  account_id TEXT NOT NULL,
  kind TEXT NOT NULL,
  entity_id TEXT NOT NULL,
  local_revision INTEGER NOT NULL,
  remote_revision INTEGER NOT NULL,
  local_payload TEXT NOT NULL,
  remote_payload TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'pending',
  created_at INTEGER NOT NULL,
  resolved_at INTEGER
);
CREATE INDEX sync_conflict_pending ON sync_conflict(account_id, status, created_at);
CREATE TABLE sync_revisions (
  account_id TEXT NOT NULL,
  kind TEXT NOT NULL,
  entity_id TEXT NOT NULL,
  revision INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (account_id, kind, entity_id)
);
`

/** 第 14 条：版本化计划文档与修订记录。计划内容不写入稳定提示词前缀。 */
const V14_PLANS = `
CREATE TABLE plans (
  id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  version INTEGER NOT NULL,
  status TEXT NOT NULL,
  json TEXT NOT NULL,
  source_run_id TEXT NOT NULL,
  updated_at INTEGER NOT NULL,
  UNIQUE(session_id, id)
);
CREATE INDEX plans_by_session ON plans(session_id, updated_at DESC);
CREATE TABLE plan_revisions (
  plan_id TEXT NOT NULL REFERENCES plans(id) ON DELETE CASCADE,
  version INTEGER NOT NULL,
  author TEXT NOT NULL,
  source_run_id TEXT,
  patch TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  PRIMARY KEY(plan_id, version)
);
`

/**
 * 第 16 条：从其他 AI 应用导入(首版:本机 Claude Code)。
 *
 * ## 为什么这四张表不进 `AppSettings`
 *
 * 来源绑定是**设备本地状态**:授权的目录、已确认的项目范围、同步开关,
 * 说的全都是「这台机器上的那份 Claude Code 数据」。而 `AppSettings` 会被
 * `config-sync.ts` 推上云再拉到另一台机器 —— 那台机器上 `/Users/a/.claude`
 * 根本不存在,同步器却会照着它去扫,最好的情况是报错,最坏的情况是扫到
 * 另一个人的目录。所以它必须留在只属于本机的表里。
 *
 * ## `import_mappings` 是整个功能的支点
 *
 * 「导入两次仍一份」不是靠 `replaceHistory` 的增量对账实现的 —— 那个函数按
 * **本地消息 id** 对账,而同一份源转录第二次扫描会 mint 一批全新的 ULID,
 * 于是对账的结果是「全都是新消息」。唯一能挡住的是一张
 * 源 UUID → 本地 id 的持久映射,也就是这张表。
 *
 * 唯一键是 `(source_id, scope_key, entity_kind, source_item_id)` 四元组:
 * - `scope_key`:`''` = 全局,否则是工作区 id。同一个源技能可以按全局装一份、
 *   按项目再装一份,两者是不同的目标,不能互相覆盖。
 * - `entity_kind`:workspace / session / message / skill / agent / command /
 *   instructions / mcp。消息也在里面 —— 重试不重复全靠它。
 *
 * ## 两份指纹各挡一件事
 *
 * - `source_fingerprint`:上次成功导入时**源**长什么样。源没变就不用重做。
 * - `target_fingerprint`:上次写完时**目标**长什么样。目标和它不一样 =
 *   用户改过,于是源更新只能记 conflict,不能覆盖。
 *
 * 少任何一份都会退化成「按时间戳猜」,而那正是会把用户工作覆盖掉的做法。
 *
 * ★ 没有指向 `sessions` / `workspaces` 的外键。目标被用户删掉之后这一行要
 * **留下来**当 tombstone(`sync_state = 'suppressed'`),否则下一轮自动同步
 * 会把它原地复活 —— 那是用户明确删过的东西。
 */
const V16_IMPORTS = `
CREATE TABLE import_sources (
  source_id      TEXT PRIMARY KEY,
  kind           TEXT NOT NULL,
  -- 已授权的配置目录绝对路径。空串 = 曾经登记过但现在找不到了。
  config_dir     TEXT NOT NULL DEFAULT '',
  origin         TEXT NOT NULL DEFAULT 'auto',
  sync_enabled   INTEGER NOT NULL DEFAULT 0,
  -- 已授权类别与项目。JSON 数组:整读整写,从不按元素查询(见文件头的判据)。
  categories     TEXT NOT NULL DEFAULT '[]',
  project_keys   TEXT NOT NULL DEFAULT '[]',
  last_check_at  INTEGER,
  last_sync_at   INTEGER,
  status         TEXT NOT NULL DEFAULT 'off',
  diagnostics    TEXT NOT NULL DEFAULT '[]',
  created_at     INTEGER NOT NULL,
  updated_at     INTEGER NOT NULL
);

CREATE TABLE import_mappings (
  source_id          TEXT NOT NULL,
  -- '' = 全局作用域;否则是 workspace id。见文件头。
  scope_key          TEXT NOT NULL DEFAULT '',
  entity_kind        TEXT NOT NULL,
  source_item_id     TEXT NOT NULL,
  target_id          TEXT NOT NULL DEFAULT '',
  target_path        TEXT NOT NULL DEFAULT '',
  target_workspace_id TEXT NOT NULL DEFAULT '',
  source_fingerprint TEXT NOT NULL DEFAULT '',
  target_fingerprint TEXT NOT NULL DEFAULT '',
  -- 转换器版本。解析规则改了之后,旧映射要能被认出来重做。
  transformer_version INTEGER NOT NULL DEFAULT 1,
  -- linked | detached | conflict | source-missing | suppressed
  sync_state         TEXT NOT NULL DEFAULT 'linked',
  -- 来源模型名/时间/标题这类只用于显示的元数据。不按字段查,留 JSON。
  meta               TEXT NOT NULL DEFAULT '{}',
  created_at         INTEGER NOT NULL,
  updated_at         INTEGER NOT NULL,
  PRIMARY KEY (source_id, scope_key, entity_kind, source_item_id)
);
-- 反查:本地实体被删/被改时要找到它对应的那条映射
CREATE INDEX import_mappings_by_target ON import_mappings (target_id, entity_kind);
-- 同步一轮要按 (source, kind, state) 收窄
CREATE INDEX import_mappings_by_state ON import_mappings (source_id, entity_kind, sync_state);

CREATE TABLE import_batches (
  id         TEXT PRIMARY KEY,
  source_id  TEXT NOT NULL,
  source_kind TEXT NOT NULL,
  trigger    TEXT NOT NULL,
  phase      TEXT NOT NULL,
  started_at INTEGER NOT NULL,
  ended_at   INTEGER,
  -- ImportCounts 六个数。整读整写。
  counts     TEXT NOT NULL DEFAULT '{}'
);
CREATE INDEX import_batches_by_started ON import_batches (started_at DESC, id DESC);

-- ★ 明细只记「哪一项、去了哪、结果如何」,**不复制转录正文或原始配置**。
--   复制正文等于在审计表里留第二份用户数据,而它既不参与任何查询,
--   又要跟着「清空对话历史」一起被想起来清掉。
CREATE TABLE import_batch_items (
  batch_id            TEXT NOT NULL REFERENCES import_batches (id) ON DELETE CASCADE,
  seq                 INTEGER NOT NULL,
  category            TEXT NOT NULL,
  title               TEXT NOT NULL DEFAULT '',
  source_path         TEXT NOT NULL DEFAULT '',
  result              TEXT NOT NULL,
  target_kind         TEXT,
  target_id           TEXT,
  target_workspace_id TEXT,
  diagnostics         TEXT NOT NULL DEFAULT '[]',
  PRIMARY KEY (batch_id, seq)
);
`

/** 第 17 条：标记发给模型的内部协调消息，渲染层可将其从用户气泡中隐藏。 */
const V17_INTERNAL_MESSAGES = `
ALTER TABLE messages ADD COLUMN internal INTEGER NOT NULL DEFAULT 0;
`

/** 第 18 条：定时任务计划与执行记录。计划的查询/排序字段提列，领域配置保留 JSON。 */
const V18_SCHEDULED_TASKS = `
CREATE TABLE scheduled_tasks (
  id TEXT PRIMARY KEY,
  workspace_id TEXT NOT NULL,
  enabled INTEGER NOT NULL DEFAULT 1,
  next_run_at INTEGER,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  json TEXT NOT NULL
);
CREATE INDEX scheduled_tasks_by_next_run ON scheduled_tasks (enabled, next_run_at);
CREATE INDEX scheduled_tasks_by_workspace ON scheduled_tasks (workspace_id, updated_at DESC);

CREATE TABLE scheduled_runs (
  id TEXT PRIMARY KEY,
  task_id TEXT NOT NULL,
  session_id TEXT NOT NULL DEFAULT '',
  trigger TEXT NOT NULL,
  status TEXT NOT NULL,
  scheduled_at INTEGER NOT NULL,
  started_at INTEGER,
  ended_at INTEGER,
  summary TEXT,
  error TEXT
);
CREATE INDEX scheduled_runs_by_task ON scheduled_runs (task_id, scheduled_at DESC, id DESC);
CREATE INDEX scheduled_runs_by_scheduled_at ON scheduled_runs (scheduled_at DESC, id DESC);
`

/** 第 19 条：Codex-shaped v2 plans. Legacy plans remain readable through the old table. */
const V19_PLANS_V2 = `
CREATE TABLE plans_v2 (
  id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  version INTEGER NOT NULL,
  lifecycle TEXT NOT NULL,
  json TEXT NOT NULL,
  source_run_id TEXT NOT NULL,
  execution_run_id TEXT,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL
);
CREATE INDEX plans_v2_by_session ON plans_v2(session_id, updated_at DESC);
CREATE TABLE plan_revisions_v2 (
  plan_id TEXT NOT NULL REFERENCES plans_v2(id) ON DELETE CASCADE,
  version INTEGER NOT NULL,
  author TEXT NOT NULL CHECK(author IN ('agent', 'user', 'system')),
  source_run_id TEXT,
  patch TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  PRIMARY KEY(plan_id, version)
);
CREATE UNIQUE INDEX plans_v2_one_execution ON plans_v2(id, execution_run_id) WHERE execution_run_id IS NOT NULL;
`

/**
 * 第 20 条：按天 × 供应商 × 模型 × 币种的用量汇总。
 *
 * `usage_records` 只增不删(全仓没有一处 `DELETE FROM usage_records`,`cleanupByAge`
 * 与 `clearHistory` 都不碰它),所以历史桶不会被回溯削减 —— 这是「汇总一次就算数」
 * 成立的前提,也是这张表敢做物化的理由。
 *
 * ★ **`currency` 用空串表示「未计价」,不是 NULL。** SQLite 主键里的 NULL 不参与
 *   唯一性判定,同一天同一模型的未计价行会一次次插进来而不是合并成一行 ——
 *   而它的表现是「费用图里某个模型莫名其妙出现很多次」,没人会去 diff 的静默错。
 *
 * ★ **刻意不存 `tool_calls` / `tool_errors`。** 那两列是 run 结束后由
 *   `updateUsageToolsForRun` **回写历史行**的,任何汇总都会停在回写之前。
 *   工具口径继续走 `usage_records` 直查(设置页的「工具」分区),不进这张表。
 *
 * ★ `cost_micros` 可空,并单独记 `priced_count`。沿用 `usage_records` 的约定:
 *   NULL = 不知道多少钱,0 = 真的免费。`priced_count < request_count` 就是界面
 *   该说「这个数字不含未定价的请求」的依据 —— 缺了它,少算的钱看起来和省下的钱一样。
 *
 * ★ 平均延迟存的是 **sum + count 而不是 avg**。存 avg 的话跨桶再平均就是「平均的
 *   平均」,权重全错;而这张表的每一行都还要被按天、按模型二次聚合。
 */
const V20_USAGE_DAILY = `
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
`

/**
 * 扫描缓存 —— 让「扫过但**没导入**」的转录也能跳过重读。
 *
 * ★ 存在的理由:changeToken 原先挂在 `import_mappings.meta` 上,而映射只有
 * 导入过的会话才有。实测一台机器上 1063 个转录只有 47 条映射 —— 96% 的文件
 * 每次扫描都要完整重读(Codex 那 1.6GB 读一遍约 5 秒,全程堵在主进程里)。
 * 换句话说那个缓存对绝大多数人是不生效的。
 *
 * ★ 主键用**文件路径**不用 sessionId:sessionId 要解析完文件才知道
 * (Codex 的文件名是 `rollout-*`,与会话 id 无关),而缓存的全部意义
 * 就是在读文件之前决定「要不要读」。
 *
 * ★ `content_hash = ''` 表示这一行是**只读头部**得来的 —— 那种情况下
 * `messages` 是下界而不是精确值。判据见 `service.ts` 的 `readChatMeta`:
 * 没有映射的条目状态必然是 new,而 new 不需要哈希,所以那一趟全文读是白读的。
 */
const V21_IMPORT_SCAN_CACHE = `
CREATE TABLE import_scan_cache (
  source_id      TEXT NOT NULL,
  -- 绝对路径。OpenCode 这类库内会话用 \`opencode.db#<id>\` 这种合成路径。
  source_path    TEXT NOT NULL,
  -- size:mtime。和源文件一比就知道整行能不能复用。
  change_token   TEXT NOT NULL,
  -- 解析出来的会话 id(去重与映射键都要用它)。
  session_id     TEXT NOT NULL DEFAULT '',
  -- '' = 只读了头部,没算过全文哈希。
  content_hash   TEXT NOT NULL DEFAULT '',
  cwd            TEXT NOT NULL DEFAULT '',
  title          TEXT NOT NULL DEFAULT '',
  model          TEXT NOT NULL DEFAULT '',
  model_provider TEXT NOT NULL DEFAULT '',
  -- partial = 1 时这是下界。
  messages       INTEGER NOT NULL DEFAULT 0,
  partial        INTEGER NOT NULL DEFAULT 0,
  source_updated_at INTEGER NOT NULL DEFAULT 0,
  scanned_at     INTEGER NOT NULL,
  PRIMARY KEY (source_id, source_path)
);
-- 扫完一轮要按来源清掉本轮没再见到的行(源侧文件已删)
CREATE INDEX import_scan_cache_by_source ON import_scan_cache (source_id, scanned_at);
`

/**
 * 第 22 条:本机账户配置隔离。
 *
 * `config_profiles` 存**每个作用域那一份配置的归档**:scope 是 `'local'`
 * (未登录)或账户的**原始不可变 id**;`json` 是 `config-profile.ts` 定义的
 * 快照(settings / providers+aliases / mcp / search / connections /
 * scheduled tasks / kv 白名单)。切账户 = 归档当前、恢复目标,**在同一个事务里**。
 *
 * ★ 只有**配置**进这张表。会话、消息、运行记录、附件、用量一律留在原表 ——
 *   它们是「这台机器上的数据」,不随账户切换搬家,也绝不能因为一次切换被删掉。
 *   连 `sessions.workspace_id` 上的行都不动:切账户改的是**看得见哪些行**。
 *
 * ★ 为什么不复用 `sync_outbox` / kv:这份归档必须**在任何作用域下都能读**,
 *   而 kv 里那些键本身就会随作用域被换掉 —— 用它存快照等于把梯子架在自己脚下。
 *
 * `workspaces.owner` 是第 2 件事:工作区**归属**。默认 `'local'` 同时是
 * 这一列存在之前**所有已有行**的正确归属(那时的库里只有未登录这一种用法),
 * 所以这条迁移不需要任何数据回填。列而不是新表,因为判据是「要不要被
 * WHERE 读到」—— `listWorkspaces` 的每一条查询都要按它收窄。
 */
const V22_CONFIG_PROFILES = `
CREATE TABLE config_profiles (
  scope TEXT PRIMARY KEY,
  json  TEXT NOT NULL
);

ALTER TABLE workspaces ADD COLUMN owner TEXT NOT NULL DEFAULT 'local';
-- 每个作用域的首屏列表都按 owner + last_opened_at 取,收窄后行数已经很小
CREATE INDEX workspaces_by_owner ON workspaces (owner, last_opened_at DESC);
`

/**
 * 第 23 条:Agent 每一轮改动的文件快照 —— 支撑回复底部的「改动审查」卡、
 * 右侧 diff 预览、以及**跨重启**的撤销/恢复。
 *
 * 两张表,一个是「轮」一个是「文件」:
 *
 * - `file_change_sets`:一轮(顶层 run)一行。`state` 是**整轮原子**的撤销态
 *   (applied / reverted)—— 模型里不存在「三个文件撤了两个」的中间态,把它放
 *   set 级一行,卡片的撤销/恢复按钮读它写它都在一处,杜绝 per-file 列漂移。
 *   `run_id` 存的是**顶层** runId:子代理在自己的 child run 下改的文件也 roll-up
 *   到发起它的父 run,好让 renderer 用 `messageRuns[messageId]` 一次查全。
 *
 * - `file_snapshots`:一轮里每个被改文件一行,主键 `(run_id, file_path)` ——
 *   同一轮多次改同一文件时**首个 before 与最后一个 after** 合并成一行(采集器
 *   保证,见 `change-recorder.ts`)。`source_run_id` 留真正写它的 run(可能是子 run)。
 *   `abs_path` 给内核 `host.fs` 用,`file_path`(工作区相对)给撤销回写的
 *   `workspace:*` IPC 用 —— 两条路径在采集时一次算好,撤销时不重算。
 *   `before_hash`/`after_hash` 让「撤销前比对磁盘现状」不必先把全文读回内存。
 *   `oversize=1`(单文件 > 1 MiB)时内容存 NULL、只留 hash+行计数,禁 diff/undo;
 *   `in_workspace=0`(改到工作区外的文件)同样禁 undo —— IPC 的 checkedPath 会拒。
 *
 * 级联:`file_change_sets.session_id` → sessions ON DELETE CASCADE,
 * `file_snapshots.run_id` → file_change_sets ON DELETE CASCADE。删会话 / 清历史
 * 时两张表随之清空(FK 前提是 `PRAGMA foreign_keys=ON`,见本文件头与 index.ts)。
 */
const V23_FILE_CHANGES = `
CREATE TABLE file_change_sets (
  run_id       TEXT PRIMARY KEY,
  session_id   TEXT NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
  workspace_id TEXT NOT NULL,
  -- 'applied' | 'reverted' —— 整轮原子撤销态
  state        TEXT NOT NULL DEFAULT 'applied',
  file_count   INTEGER NOT NULL DEFAULT 0,
  additions    INTEGER NOT NULL DEFAULT 0,
  deletions    INTEGER NOT NULL DEFAULT 0,
  created_at   INTEGER NOT NULL,
  updated_at   INTEGER NOT NULL
);
-- 会话侧「这条会话有哪些轮改过文件」的列表查询
CREATE INDEX file_change_sets_by_session ON file_change_sets (session_id);

CREATE TABLE file_snapshots (
  run_id         TEXT NOT NULL REFERENCES file_change_sets (run_id) ON DELETE CASCADE,
  file_path      TEXT NOT NULL,
  -- 真正写它的 run(可能是子代理的 child run);run_id 存的是顶层 root
  source_run_id  TEXT NOT NULL,
  -- 解析后的绝对路径,内核 host.fs 读写用
  abs_path       TEXT NOT NULL,
  -- 'created' | 'modified' | (预留 'deleted')
  change_kind    TEXT NOT NULL,
  -- created 或 oversize 时为 NULL
  before_content TEXT,
  after_content  TEXT,
  before_hash    TEXT,
  after_hash     TEXT NOT NULL,
  additions      INTEGER NOT NULL DEFAULT 0,
  deletions      INTEGER NOT NULL DEFAULT 0,
  -- 1 = 单文件超 1 MiB,内容存 NULL,禁 diff/undo
  oversize       INTEGER NOT NULL DEFAULT 0,
  -- 0 = 改到了工作区外的文件,禁 undo
  in_workspace   INTEGER NOT NULL DEFAULT 1,
  created_at     INTEGER NOT NULL,
  PRIMARY KEY (run_id, file_path)
);
`

/**
 * 第 24 条：OAuth 供应商的**多账号**。
 *
 * ## 为了什么需求建的
 *
 * 订阅制的那几家(Codex / Kimi / GLM …)额度是按账号算的,一个账号被限流时
 * 另一个账号的额度还在。在这张表之前,一家 provider 只有一个凭证槽,
 * 「限流了自动换个号」在数据模型上表达不出来。
 *
 * ## ★★ 表里一个字节的密文都没有
 *
 * token 仍然只住 `credentials` 表(经 `host.secrets` 加解密、经
 * `physicalCredentialRef` 做账户隔离)。把 access/refresh token 塞进这张业务表
 * 会绕过整条加密边界,而症状是**没有症状**:功能照常跑,直到有人打开数据库文件。
 * 这里存的是「哪个账号、排第几、停用没、限流到什么时候、额度快照」这些元数据,
 * 密文由 `provider:<id>#<account_id>` 这个 ref 指过去。
 *
 * ## `is_current` 为什么和排序分开
 *
 * `sort_order` 决定**轮换时谁先上**,`is_current` 只决定「镜像回旧槽 `provider:<id>`
 * 的是谁」和界面上那个「当前」标。合成一个字段的话,限流轮换到下一个账号时会把
 * 用户显式设的「当前账号」悄悄改掉 —— 他会发现自己的选择莫名其妙变了,而他从没点过。
 *
 * ## 存量迁移为什么不在这条 SQL 里
 *
 * SQL 读不到密文,判不出 `provider:<id>` 那一行装的是 OAuth 凭证还是一把 API Key,
 * 而把 API Key 供应商也迁成「账号」会让它的界面整个换成账号列表。迁移因此放在
 * 主进程启动路径上的一次性 `ensureProviderAccountsSeeded()`(`db/provider-accounts.ts`),
 * 幂等靠「这家已经有账号行就跳过」,不靠迁移版本号 —— 版本号管不到
 * 「用户是升级之后才第一次登录的」这种情况。
 */
const V24_PROVIDER_ACCOUNTS = `
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
`

/**
 * 第 25 条：压缩检查点记下「这一刀实际做了什么」。
 *
 * ## 需求
 *
 * 在此之前检查点只有 `note` 和一对 token 读数,于是界面上能说的只有「压缩了」三个字。
 * 用户(和下一个接手的 agent)问的两个问题一个都答不上来:**哪些消息已经不再发给
 * 模型**、**摘要到底读没读过它们**。后者尤其要命 —— digest 有 token 预算,超了会从
 * 最早的一侧整条丢,而检查点原先一律把覆盖锚点写成转录的最后一条,于是那些没进
 * 摘要的消息以「已覆盖」的身份被投影裁掉:既不在摘要里,也不在上下文里。
 *
 * ★ 一列 JSON 而不是七个平行列,同 `search_hits` 的先例:这些字段是**一起有、
 * 一起没有**的一份事实,而且还会长(将来要加「哪几条被降级了」之类)。拆成列的话,
 * 每加一个字段就是一次 ALTER TABLE,而读路径上它们从不作为查询条件。
 *
 * ★ 可空且**不回填**。老检查点无从知道自己丢过什么 —— 猜一个 0 会把「不知道」
 * 谎报成「一条都没丢」,而界面对这两种必须有不同反应(见 `ContextCompactionDetail`)。
 */
const V25_CONTEXT_COMPACTION_DETAIL = `
ALTER TABLE context_checkpoints ADD COLUMN detail TEXT;
`

/**
 * 第 26 条：检查点表随上下文压缩重写一并退役。
 *
 * ## 需求
 *
 * 压缩按 Claude Code 的模型重写之后,「这段对话从哪里开始发给模型」由转录里的
 * 一条边界消息回答(`compact_boundary` 块,见 `shared/agent/compaction.ts`),
 * 不再由另一张表回答。两处并存就会出现「界面按检查点画线、请求却按边界切」
 * 这类**零报错**的错位 —— 那正是上一版最难查的那一类 bug。
 *
 * ## 为什么是 DROP 而不是留着不读
 *
 * 留着的代价不是零:它有 `ON DELETE CASCADE` 外键、唯一约束和一条索引,
 * 每一次 `deleteSession` / 导入合并都还在为它付账;更要紧的是,下一个人读到
 * 建表语句会以为压缩状态住在这里。**没有读路径的表不是零成本,是一份会骗人的文档。**
 *
 * ★ 老检查点**不迁移**成边界消息。检查点记的是「窗口序号 + 一段笔记」,
 * 而边界消息要的是摘要正文和压缩前后的真实读数 —— 前者推不出后者,硬造出来的
 * 是一段编的摘要,而模型会当真。老会话因此退回「整段历史照发」,到阈值时
 * 自动压缩会立刻给它建一条真的边界。这是明知的代价,换的是不撒谎。
 *
 * ★ `IF EXISTS`:V9 之前建库、又从没跑过 V9 的库不存在(迁移是顺序执行的),
 * 但导入 / 合并路径上可能拿到一份缺表的库,而一次建表失败会让整个迁移事务回滚。
 */
const V26_DROP_CONTEXT_CHECKPOINTS = `
DROP TABLE IF EXISTS context_checkpoints;
`

/**
 * 第 27 条：其它设备同步来的使用统计。
 *
 * ## 需求
 *
 * 使用统计概览要合并同一账户下多台设备的数据（云同步 `usage` category，
 * 见 `config-sync-usage-snapshot.ts`）。这两张表存的是**别的设备**的日汇总，
 * 本机自己的仍在 `usage_daily`；请求日志（`usage_records`）不跨设备。
 *
 * ★ 不能并进 `usage_daily`：刷新器发现时区变了会 `DELETE FROM usage_daily` 整表重建
 *   （`usage-rollup.ts`），远端数据混在里面会被一起清掉，而本机算不回来 ——
 *   表现为换个时区之后其它设备的用量从图上消失，零报错。
 * ★ 带 `account_id`：换账号登录后，上一个账号同步来的用量不能出现在这个账号的图上；
 *   按账号隔离而不是直接删，是为了切回来时不用重新拉。
 */
const V27_USAGE_REMOTE = `
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
`

/**
 * 第 28 条:视频生成任务。
 *
 * ★★ **这张表是后台视频生命周期的唯一真源**(见 shared/domain/video-generation.ts
 * 的 `VideoJob`)。它必须落盘而不是只留在内存里,因为一个视频任务要跑几分钟,
 * 而应用随时可能被退出、重启、切账户 —— 恢复查询全靠这里的上游任务身份。
 *
 * ★ 三处刻意的列选择,都按"查询形状"来(schema.ts 文件头那条规矩):
 *   - `credential_fingerprint` **提成真列**:恢复前要按它筛"哪些任务还能用当前
 *     凭据继续查",而那是恢复路径每一次启动都要跑的一次过滤;
 *   - `cloud` / `retrieval` 也是真列:前者决定"还要不要轮询",后者决定
 *     "云端成功了但文件没取回来"那条最容易被漏掉的分支;
 *   - 其余整块塞进 `json`,因为它们永远是整行读写,没有按字段查的需求。
 *
 * ★★ **一个字节的视频都不进这张表**:只存上游 URL 与本地附件引用。
 *   把 base64 视频写进 SQLite 会让库膨胀到几百兆,而那条库是每次启动都要打开的。
 *
 * ★ 外键指向 sessions:会话删了,它的任务行也跟着走(与 attachments 同规)。
 *   但**云端任务不会被这行删除自动取消** —— 那件事由 manager 尽力而为,
 *   且不能宣称"删了就等于退款"。
 */
const V28_VIDEO_JOBS = `
CREATE TABLE video_generation_jobs (
  id                     TEXT PRIMARY KEY,
  session_id             TEXT REFERENCES sessions (id) ON DELETE CASCADE,
  workspace_id           TEXT NOT NULL,
  config_profile         TEXT NOT NULL,
  cloud                  TEXT NOT NULL,
  retrieval              TEXT NOT NULL,
  credential_fingerprint TEXT,
  upstream_id            TEXT,
  created_at             INTEGER NOT NULL,
  updated_at             INTEGER NOT NULL,
  revision               INTEGER NOT NULL,
  json                   TEXT NOT NULL
);
CREATE INDEX video_jobs_by_session ON video_generation_jobs (session_id, created_at DESC);
CREATE INDEX video_jobs_by_recovery ON video_generation_jobs (cloud, retrieval, updated_at);
`


/**
 * 第 32 条:控制端持久化基础 —— 控制端 UI 状态与推送**订阅登记**。
 *
 * ## 隔离键:为什么有 `controller_id`,没有 `client_session_id` / `binding_epoch`
 *
 * 两张表都用 `(device_id, data_epoch, config_scope, controller_id)`,这是刻意的:
 *
 * - ★ **有 `controller_id`**:同设备上多个控制端不能互相覆盖对方的草稿,
 *   也不能互相改对方的订阅。网络入参里**没有** controllerId 这个字段 ——
 *   它只由已认证连接的绑定得出(`DeviceRequestContext.binding`)。
 * - ★ **没有 `client_session_id` / `binding_epoch`**:两者都是"这一次连接"的
 *   控制端本地概念,重连就会变。放进键里的话,同一个控制端每重连一次就读不回
 *   自己刚写的草稿、也要重新登记一次订阅 —— 而这两个功能的存在理由正是"重连后还在"。
 * - `data_epoch` / `config_scope` 仍然在键里:换账号或数据恢复之后,旧作用域的行
 *   不能被新作用域读到(`scopeEpoch` 已由 `data_epoch` 覆盖,见 `controller-state.ts` 头)。
 *
 * ## `device_notification_subscriptions.registration_ref` 是不透明引用
 *
 * ★ 它**不是** APNs / FCM 的 device token,也**不允许**是 URL 或任意收件地址:
 *   写入口用 `registrationRefSchema` 约束形状(1..128 个 `[A-Za-z0-9_.-]`,首字符字母数字),
 *   所以 `https://…` / `mailto:…` / `data:abc` 这类值进不了库。它只是将来安全存储 /
 *   推送网关按引用取真 token 的句柄 —— 那张表因此没法被当成"往任意地址发消息"的地址簿。
 *   形状合规只是"进得来",不是授权,更不是凭证。
 *
 * ★ 删除是**软删除**:`deleted` 列(第 33 条迁移追加)为 1 的行是墓碑,
 *   读路径当它不存在,但 `revision` 继续 +1 —— 见 `V33_DEVICE_NOTIFICATION_TOMBSTONES`。
 *
 * ## 列的类型不通约
 *
 * - `device_controller_state.value` 是 `BLOB NOT NULL`,存的是 msgpack 编码的
 *   `DeviceValue`(`shared/device/codec.ts`)—— 与 `device_events.data` 同一个约定。
 *   空值存成 1 字节的 `nil`,**不会**退化成 SQL NULL,所以"清空"与"没有这一行"
 *   在结构上就分得开。
 * - `enabled` 是 `INTEGER`:0/1 的来自控制端的布尔位,不是三态。
 * - 两张表的 `revision` 都是"控制端 CAS 用的版本",从 1 起,每次成功写入 +1
 *   (`expectedRevision` 首次要求 0)—— 版本管理在访问器里,不在 SQL 里。
 *
 * ## 为什么没有外键
 *
 * 这两张表属于**设备账号作用域**,不是某条会话/某个 provider 的从属行;
 * 仓里也没有一张"设备"表可以引用(设备身份来自传输层认证,不是本地行)。
 * 级联删除因此不存在 —— 清账号作用域要靠 `data_epoch` / `config_scope` 换键,
 * 这正是把它们放进主键的原因。
 */
const V32_DEVICE_CONTROLLER_STATE = `
CREATE TABLE device_controller_state (
  device_id     TEXT NOT NULL,
  data_epoch    TEXT NOT NULL,
  config_scope  TEXT NOT NULL,
  controller_id TEXT NOT NULL,
  -- 控制端自己编的稳定视图键(renderer 草稿布局等),名字复用 deviceIdSchema 的形状约束
  view_key      TEXT NOT NULL,
  revision      INTEGER NOT NULL,
  -- msgpack 编码的 DeviceValue;空值是一个字节的 nil,不是 NULL
  value         BLOB NOT NULL,
  updated_at    INTEGER,
  PRIMARY KEY (device_id, data_epoch, config_scope, controller_id, view_key)
);

-- 一个控制端只有一条推送订阅登记,所以 controller_id 之后没有第二个维度。
-- registration_ref 是不透明引用(见文件头),platform 是投递侧的稳定枚举。
CREATE TABLE device_notification_subscriptions (
  device_id        TEXT NOT NULL,
  data_epoch       TEXT NOT NULL,
  config_scope     TEXT NOT NULL,
  controller_id    TEXT NOT NULL,
  registration_ref TEXT NOT NULL,
  platform         TEXT NOT NULL CHECK(platform IN ('ios','android')),
  enabled          INTEGER,
  revision         INTEGER,
  updated_at       INTEGER,
  PRIMARY KEY (device_id, data_epoch, config_scope, controller_id)
);
`

/**
 * 第 33 条:推送订阅的**墓碑**。
 *
 * ## 需求:删除再创建不能把版本打回 1
 *
 * 第 32 条里删除是真删行。于是同一个控制端"建 → 删 → 再建"会拿到一条
 * `revision` 又从 1 开始的新行 —— 这正是 ABA:控制端以为自己在建版本 1,
 * 而**旧的版本 1 指令可能还在路上**,迟到之后它照样能改这条新行。
 *
 * ## 做法:软删除,版本只增不减
 *
 * `deleted=1` 的行是墓碑,读路径一律当它不存在(`getNotificationSubscription` → `null`),
 * 但**行还在**,`revision` 跟着墓碑一起继续 +1。重新登记时必须 `expectedRevision === 0`,
 * 却沿用墓碑的 `revision + 1`(不回到 1)—— 于是"旧 active 版本"的迟到更新 / 删除
 * 在新行上必然是 `conflict`。
 *
 * ★ 软删除时 `registration_ref` 置空串:墓碑不是"往哪台设备推"的句柄,留着等于把
 *   已注销的登记引用继续留在库里。`enabled` 也归 0。
 *
 * ★ `deleted` 加 `CHECK(deleted IN (0,1))`:0 / 1 两位,不是三态。
 *
 * ★ **不重写第 32 条**:那条迁移已经发布过,改它会让升过级的库和新装出来的库长得
 *   不一样。默认值 0 也是既有行的正确回填 —— 加这列之前不可能有墓碑。
 */
const V33_DEVICE_NOTIFICATION_TOMBSTONES = `
ALTER TABLE device_notification_subscriptions ADD COLUMN deleted INTEGER NOT NULL DEFAULT 0 CHECK(deleted IN (0,1));
`

/**
 * 第 34 条:已提交的用户输入**回执**。
 *
 * ## 记的是什么
 *
 * 一条 user 消息一落进 `messages`,它就已经被**消费**过了 —— 定时的任务队列、
 * 插话队列、重试路径都可能再看见同一份输入,而它们该看的判据只有这一个:
 * 「这条消息有没有已经提交过」。所以这里一行 = 一次已提交的输入,
 * 主键 `(session_id, message_id)` 让重复提交天然幂等。
 *
 * ## 为什么**不**引用 `messages(id)`
 *
 * ★★ 这是这条迁移唯一需要想清楚的地方。加 `REFERENCES messages(id) ON DELETE CASCADE`
 * 看起来更严,而它**恰好会把这条回执的意义消掉**:转录压缩、编辑历史、删除某几条
 * 消息都会把 `messages` 行带走,级联一到,回执跟着消失 —— 于是那条输入重新变成
 * 「没提交过」,在重启或下一轮队列清理之后被**再执行一次**。
 * 用户看到的是「同一句话发了两遍,模型答了两遍」,而库里没有任何痕迹指向这里。
 *
 * 所以 `message_id` 是一个**裸 id**,不是外键。孤儿行是这个设计的一部分:
 * 消费这件事发生过,就不因为正文后来被删掉而没发生过。
 *
 * 真正需要级联的只有**会话整体被删**(用户删对话、按时长清理)—— 那时这行回执
 * 也只是垃圾,所以 `session_id` 保留对 `sessions(id)` 的外键 + `ON DELETE CASCADE`。
 *
 * ## 为什么回填旧消息
 *
 * 升级之前已经提交过的 user 消息同样是已消费的输入。不回填的话,升级后第一次
 * 重启就会把它们当成「还没提交」再执行一遍 —— 而这个 bug 只在升级路径上出现,
 * 新装的库一辈子都测不出来。
 */
const V34_SESSION_INPUT_RECEIPTS = `
CREATE TABLE session_input_receipts (
  -- 有外键:会话整体被删时这些回执也只是垃圾,跟着走
  session_id   TEXT NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
  -- ★ 裸 id,**没有**外键 —— 见上面那段:转录压缩/删除不能抹掉消费事实
  message_id   TEXT NOT NULL,
  committed_at INTEGER NOT NULL,
  PRIMARY KEY (session_id, message_id)
);

-- 回填:第 34 条之前提交过的 user 消息全都是已消费的输入。
-- 这里能安全地 SELECT messages —— 迁移跑的这一刻它们还在。
INSERT OR IGNORE INTO session_input_receipts (session_id, message_id, committed_at)
  SELECT session_id, id, created_at FROM messages WHERE role = 'user';
`

/**
 * 设备信任记录:已配对的控制端与一次性邀请。
 *
 * ★ 邀请只存 sha256(token),**不存明文** —— 库文件被拷走也换不出可用邀请。
 * ★ 控制端被撤销后保留行(`revoked_at`),不是删除:指纹列 UNIQUE,所以同一把
 *   密钥不能悄悄重新配对,撤销之后必须换新密钥。
 */
const V35_DEVICE_TRUST = `
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
`

/**
 * 服务接管标记:这份数据根已经被独立设备服务接管(原地,不复制)。
 *
 * 单行表(`id = 1`)。只**追加**:不改动任何旧表。它同时是「旧桌面共用迁移后数据不受支持」
 * 的依据 —— 新版本据此拒绝在没有明确确认的情况下回退到旧布局。
 */
const V36_SERVICE_OWNERSHIP = `
CREATE TABLE service_ownership (
  id             INTEGER PRIMARY KEY CHECK (id = 1),
  device_id      TEXT NOT NULL,
  journal_id     TEXT NOT NULL,
  backup_ref     TEXT NOT NULL,
  taken_over_at  INTEGER NOT NULL
);
`

/**
 * FTS 行的 rowid 映射。为什么需要它,见 `message-text.ts` 里「FTS 行一律按 rowid 增删改」。
 *
 * 从现有 FTS 行补齐映射,不重新分词:重建 21 万条消息要把近 1 GB 正文重新解析一遍,
 * 而这里只需读一遍 FTS 内容表。
 *
 * - 同一 message_id 有多行时(旧写法下理论上不会,但不信任它)保留 rowid 最大的那行,
 *   也就是最近写入的那行;其余行没有映射,由最后一条 DELETE 收掉。
 * - 消息已经不在 `messages` 里的幽灵行一并清掉。
 */
const V37_MESSAGE_FTS_ROWID = `
CREATE TABLE messages_fts_rowid (
  fts_rowid   INTEGER PRIMARY KEY,
  message_id  TEXT NOT NULL UNIQUE,
  session_id  TEXT NOT NULL
);
INSERT OR IGNORE INTO messages_fts_rowid (fts_rowid, message_id, session_id)
  SELECT rowid, message_id, session_id FROM messages_fts ORDER BY rowid DESC;
DELETE FROM messages_fts_rowid WHERE message_id NOT IN (SELECT id FROM messages);
DELETE FROM messages_fts WHERE rowid NOT IN (SELECT fts_rowid FROM messages_fts_rowid);
CREATE INDEX messages_fts_rowid_by_session ON messages_fts_rowid (session_id);
`

export const MIGRATIONS: readonly Migration[] = [
  { version: 1, name: 'core', sql: V1_CORE },
  { version: 2, name: 'connections', sql: V2_CONNECTIONS },
  { version: 3, name: 'pricing-usage', sql: V3_PRICING_USAGE },
  { version: 4, name: 'sessions-data', sql: V4_SESSIONS },
  { version: 5, name: 'attachment-scope', sql: V5_ATTACHMENT_SCOPE },
  { version: 6, name: 'attachment-display-name', sql: V6_ATTACHMENT_DISPLAY_NAME },
  { version: 7, name: 'attachment-owner', sql: V7_ATTACHMENT_OWNER },
  { version: 8, name: 'usage-details', sql: V8_USAGE_DETAILS },
  { version: 9, name: 'context-management', sql: V9_CONTEXT_MANAGEMENT },
  { version: 10, name: 'subagent-sessions', sql: V10_SUBAGENT_SESSIONS },
  { version: 11, name: 'context-experimental-off', sql: V11_CONTEXT_EXPERIMENTAL_OFF },
  { version: 12, name: 'message-run', sql: V12_MESSAGE_RUN }
  ,{ version: 13, name: 'config-sync', sql: V13_CONFIG_SYNC },
  { version: 14, name: 'plans', sql: V14_PLANS },
  { version: 15, name: 'workspace-connections', sql: `
    CREATE TABLE connection_profiles (id TEXT PRIMARY KEY, json TEXT NOT NULL);
  ` },
  { version: 16, name: 'imports', sql: V16_IMPORTS },
  { version: 17, name: 'internal-messages', sql: V17_INTERNAL_MESSAGES },
  { version: 18, name: 'scheduled-tasks', sql: V18_SCHEDULED_TASKS }
  ,{ version: 19, name: 'plans-v2', sql: V19_PLANS_V2 }
  ,{ version: 20, name: 'usage-daily', sql: V20_USAGE_DAILY }
  ,{ version: 21, name: 'import-scan-cache', sql: V21_IMPORT_SCAN_CACHE }
  ,{ version: 22, name: 'config-profiles', sql: V22_CONFIG_PROFILES }
  ,{ version: 23, name: 'file-changes', sql: V23_FILE_CHANGES }
  ,{ version: 24, name: 'provider-accounts', sql: V24_PROVIDER_ACCOUNTS }
  ,{ version: 25, name: 'context-compaction-detail', sql: V25_CONTEXT_COMPACTION_DETAIL }
  ,{ version: 26, name: 'drop-context-checkpoints', sql: V26_DROP_CONTEXT_CHECKPOINTS }
  ,{ version: 27, name: 'usage-remote', sql: V27_USAGE_REMOTE }
  ,{ version: 28, name: 'video-jobs', sql: V28_VIDEO_JOBS }
  // 正文提取在 index.ts 中与正常写入共用，迁移与重建在同一事务内。
  ,{ version: 29, name: 'rebuild-message-search', sql: 'DELETE FROM messages_fts;' }
  ,{ version: 30, name: 'device-command-receipts', sql: `
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
` }
  ,{ version: 31, name: 'device-interactions', sql: `
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
` }
  ,{ version: 32, name: 'device-controller-state', sql: V32_DEVICE_CONTROLLER_STATE }
  ,{ version: 33, name: 'device-notification-tombstones', sql: V33_DEVICE_NOTIFICATION_TOMBSTONES }
  ,{ version: 34, name: 'session-input-receipts', sql: V34_SESSION_INPUT_RECEIPTS }
  ,{ version: 35, name: 'device-trust', sql: V35_DEVICE_TRUST }
  ,{ version: 36, name: 'service-ownership', sql: V36_SERVICE_OWNERSHIP }
  ,{ version: 37, name: 'message-fts-rowid', sql: V37_MESSAGE_FTS_ROWID }
]
