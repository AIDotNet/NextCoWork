/**
 * 按天 × 供应商 × 模型 × 币种的用量汇总(`usage_daily`,迁移 v20)。
 *
 * 设置页的概览区 —— 指标卡、活动热力图、每日趋势、模型环形图、按模型费用 ——
 * 全部读这张表。原始的 `usage_records` 继续服务「请求日志」那半页。
 *
 * ## 为什么是「按日整桶重算」而不是行级增量累加
 *
 * 因为这张表可以**重算**而结果不变:`usage_records` 只增不删(全仓没有一处
 * `DELETE FROM usage_records`,`cleanupByAge` / `clearHistory` 都不碰它),
 * 所以任何一天的桶一旦算出来就是终局。既然重算是幂等的,就没有理由去维护
 * 「上次加到哪了」这种会漂移的状态 —— 累加错一次,错的数字会一直留在表里,
 * 而且没有任何外显。
 *
 * ## 水位与日期是两件事,不能混
 *
 * ★ `usage_records.id` 是 `ulid(endedAt)` ——**写入时刻**,严格单调;
 *   而 `at` 是 `startedAt`,**请求开始时刻**。一个跑了三分钟的请求,`at` 可能
 *   早于水位却晚于水位才落盘。
 *
 *   所以:**找「哪些日子有新数据」用 id(写入序),算「属于哪一天」用 at(业务时刻)**。
 *   反过来用 `at > 水位` 取增量的话,慢请求会被永久漏掉 —— 而漏掉的账
 *   只表现为「这天的钱比记忆中少一点」,没人会去对账。
 *
 * ★ 再并入最近两天兜底。ULID 的单调性只在单个进程内由模块级状态保证,
 *   跨重启遇上时钟回拨就可能倒退;两天的重算成本是一次索引范围扫描,很便宜。
 *
 * ## 时区
 *
 * `day` 是**本地日期**,所以它依赖写库时的时区。带着笔记本飞一趟,历史桶的
 * 边界就全错位了。刷新器因此记下算这张表时用的 IANA 时区名,发现不一致就
 * 整表重建 —— 这比「悄悄按新时区往下加」要好:后者会让同一张图里前半段是
 * 东八区的天、后半段是西五区的天,而图上看不出任何异常。
 */
import { stmt, tx } from './index'
import { getActiveSyncAccountId, getKv, setKv } from './repo'
import type { UsageActivityStats, UsageDailyBucket, UsageWindow } from '../../shared/domain/usage'
import type { Currency } from '../../shared/domain/pricing'
import { computeStreaks, localDayOf } from '../../shared/domain/usage-activity'

const STATE_KEY = 'usage.rollup.state'

/** 相邻消息间隔超过它就算两场聊天。30 分钟 —— 短到能切开「上午聊完下午再聊」,
 *  长到不会把一次思考停顿切成两段。 */
const CHAT_GAP_MS = 30 * 60 * 1000

/** 每次刷新无条件重算的尾部天数(见文件头「水位与日期」)。 */
const TAIL_DAYS = 2

interface RollupState {
  /** 已汇总到的 `usage_records.id`(写入序水位)。空串 = 还没汇总过。 */
  lastId: string
  /** 算这张表时所处的 IANA 时区名。不一致就整表重建。 */
  timeZone: string
}

function currentTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || ''
  } catch {
    return ''
  }
}

/**
 * 本地日期字符串 → `[起, 止)` 毫秒区间。
 *
 * ★ 重算某一天时用的是这个区间而**不是** `WHERE date(at/1000,...) = ?` ——
 * 后者是函数表达式,用不上 `usage_records_by_at` 索引,每天都要全表扫一遍。
 * 首次全量汇总时那就是「天数 × 全表」。
 *
 * `new Date(y, m - 1, d)` 走的正是系统本地时区,和 SQLite 的 `'localtime'`
 * 同一套规则,所以两处切出来的边界一致(夏令时的那两天也一致)。
 */
export function dayBoundsLocal(day: string): { from: number; to: number } {
  const [y, m, d] = day.split('-').map((part) => Number(part))
  const from = new Date(y ?? 1970, (m ?? 1) - 1, d ?? 1, 0, 0, 0, 0).getTime()
  const to = new Date(y ?? 1970, (m ?? 1) - 1, (d ?? 1) + 1, 0, 0, 0, 0).getTime()
  return { from, to }
}

const INSERT_DAY = `
INSERT INTO usage_daily (
  day, provider_id, provider_name, upstream_model, alias, currency,
  request_count, success_count,
  input_tokens, output_tokens,
  cache_read_tokens, cache_write_tokens, cache_write_1h_tokens, thinking_tokens,
  cost_micros, priced_count,
  latency_sum, ttft_sum, ttft_count
)
SELECT
  ?,
  provider_id,
  COALESCE(NULLIF(MAX(provider_name), ''), provider_id),
  upstream_model,
  COALESCE(MAX(alias), ''),
  COALESCE(currency, ''),
  COUNT(*),
  SUM(ok),
  SUM(input_tokens),
  SUM(output_tokens),
  SUM(cache_read_tokens),
  SUM(cache_write_tokens),
  SUM(cache_write_1h_tokens),
  SUM(COALESCE(thinking_tokens, 0)),
  -- ★ SUM 跳过 NULL,全是 NULL 时返回 NULL —— 正好就是「这一桶一条也算不出钱」。
  --   千万别 COALESCE 成 0,那等于把「不知道多少钱」说成「免费」。
  SUM(cost_micros),
  -- COUNT(列) 不数 NULL,于是它就是「这一桶里有几条真算出了钱」
  COUNT(cost_micros),
  SUM(latency_ms),
  SUM(COALESCE(time_to_first_token_ms, 0)),
  COUNT(time_to_first_token_ms)
FROM usage_records
WHERE at >= ? AND at < ?
GROUP BY provider_id, upstream_model, COALESCE(currency, '')
`

/**
 * 把 `usage_records` 的新增部分汇总进 `usage_daily`。幂等 —— 重复调用不改变结果。
 *
 * 由后台定时器周期调用,两条 usage 概览 IPC 在查询前也各兜一次
 * (定时器负责摊薄,兜底负责「刚聊完就点开统计」能看到新数字)。
 */
export function refreshUsageRollup(now: number = Date.now()): { days: number; rows: number } {
  const timeZone = currentTimeZone()
  const state = getKv<RollupState>(STATE_KEY, { lastId: '', timeZone })

  return tx(() => {
    let since = state.lastId
    if (state.timeZone !== timeZone) {
      // 时区变了 —— 所有 day 的边界都可能移位,没有「只修一部分」的说法
      stmt('DELETE FROM usage_daily').run()
      since = ''
    }

    // ★ 先按 id 收窄(主键索引),再在少数几行上算 date() —— 反过来是全表扫
    const changed = stmt(
      `SELECT DISTINCT date(at / 1000, 'unixepoch', 'localtime') AS day
         FROM usage_records
        WHERE id > ?`
    ).all(since)

    const days = new Set<string>()
    for (const row of changed) {
      const day = String(row['day'] ?? '')
      if (day !== '') days.add(day)
    }
    for (let i = 0; i < TAIL_DAYS; i++) {
      days.add(localDayOf(now - i * 24 * 60 * 60 * 1000))
    }

    let rows = 0
    for (const day of days) {
      const { from, to } = dayBoundsLocal(day)
      stmt('DELETE FROM usage_daily WHERE day = ?').run(day)
      const result = stmt(INSERT_DAY).run(day, from, to)
      rows += Number(result.changes ?? 0)
    }

    const head = stmt('SELECT MAX(id) AS last FROM usage_records').get()
    const lastId = head === undefined ? '' : String(head['last'] ?? '')
    setKv(STATE_KEY, { lastId: lastId === '' ? since : lastId, timeZone } satisfies RollupState)

    return { days: days.size, rows }
  })
}

/** 导出给 `usage-remote.ts`:远端表与本表同列名,读回同一种桶。 */
export function bucketFromRow(row: Record<string, unknown>): UsageDailyBucket {
  const cost = row['cost_micros']
  return {
    day: String(row['day'] ?? ''),
    providerId: String(row['provider_id'] ?? ''),
    providerName: String(row['provider_name'] ?? ''),
    upstreamModel: String(row['upstream_model'] ?? ''),
    alias: String(row['alias'] ?? ''),
    currency: String(row['currency'] ?? '') as Currency | '',
    requestCount: Number(row['request_count'] ?? 0),
    successCount: Number(row['success_count'] ?? 0),
    inputTokens: Number(row['input_tokens'] ?? 0),
    outputTokens: Number(row['output_tokens'] ?? 0),
    cacheReadTokens: Number(row['cache_read_tokens'] ?? 0),
    cacheWriteTokens: Number(row['cache_write_tokens'] ?? 0),
    cacheWrite1hTokens: Number(row['cache_write_1h_tokens'] ?? 0),
    thinkingTokens: Number(row['thinking_tokens'] ?? 0),
    costMicros: cost === null || cost === undefined ? null : Number(cost),
    pricedCount: Number(row['priced_count'] ?? 0),
    latencySum: Number(row['latency_sum'] ?? 0),
    ttftSum: Number(row['ttft_sum'] ?? 0),
    ttftCount: Number(row['ttft_count'] ?? 0)
  }
}

/**
 * 本机汇总 ∪ 当前同步账户下其它设备同步来的汇总(`usage_daily_remote`,见 `usage-remote.ts`)。
 *
 * 需求:使用统计概览合并多台设备的数据。唯一的参数是账户 id;未启用同步时传 null,
 * `account_id = NULL` 一行也不匹配,于是退回纯本机 —— 不需要为「没同步」另写一套查询。
 */
const MERGED_DAILY = `
  SELECT day, provider_id, provider_name, upstream_model, alias, currency,
         request_count, success_count, input_tokens, output_tokens,
         cache_read_tokens, cache_write_tokens, cache_write_1h_tokens, thinking_tokens,
         cost_micros, priced_count, latency_sum, ttft_sum, ttft_count
    FROM usage_daily
  UNION ALL
  SELECT day, provider_id, provider_name, upstream_model, alias, currency,
         request_count, success_count, input_tokens, output_tokens,
         cache_read_tokens, cache_write_tokens, cache_write_1h_tokens, thinking_tokens,
         cost_micros, priced_count, latency_sum, ttft_sum, ttft_count
    FROM usage_daily_remote
   WHERE account_id = ?`

/**
 * 多台设备的同一「天 × 供应商 × 模型 × 币种」合成一桶,形状与 `usage_daily` 一行相同。
 *
 * ★ 必须在这里合,不能把多行同键的桶交给前端:前端按键建表,同键第二行会覆盖第一行,
 *   表现为「另一台设备的用量时有时无」。
 * ★ `SUM(cost_micros)` 跳过 NULL、全 NULL 时仍是 NULL —— 与汇总表「NULL = 算不出钱」同义,
 *   千万别 COALESCE 成 0。
 */
const MERGED_SERIES = `
  SELECT day, provider_id, MAX(provider_name) AS provider_name, upstream_model, MAX(alias) AS alias, currency,
         SUM(request_count) AS request_count, SUM(success_count) AS success_count,
         SUM(input_tokens) AS input_tokens, SUM(output_tokens) AS output_tokens,
         SUM(cache_read_tokens) AS cache_read_tokens, SUM(cache_write_tokens) AS cache_write_tokens,
         SUM(cache_write_1h_tokens) AS cache_write_1h_tokens, SUM(thinking_tokens) AS thinking_tokens,
         SUM(cost_micros) AS cost_micros, SUM(priced_count) AS priced_count,
         SUM(latency_sum) AS latency_sum, SUM(ttft_sum) AS ttft_sum, SUM(ttft_count) AS ttft_count
    FROM (${MERGED_DAILY})
   WHERE day >= ? AND day <= ?
   GROUP BY day, provider_id, upstream_model, currency
   ORDER BY day ASC`

/**
 * 时间窗内的所有日桶,按日期升序。含同一同步账户下其它设备的数据(见 `MERGED_SERIES`)。
 *
 * ★ 窗口是毫秒,而 `day` 是日期字符串 —— 用 `localDayOf` 把两端换算成日期边界比
 * 较,而不是把 day 转回毫秒:后者要对每一行做一次日期解析,并且在夏令时那天会
 * 与写入时的口径分叉。
 */
export function getUsageDailySeries(window: UsageWindow): UsageDailyBucket[] {
  const toDay = localDayOf(window.to - 1)
  // 无下界时用空串:任何 `YYYY-MM-DD` 都 >= ''
  const fromDay = window.from === undefined ? '' : localDayOf(window.from)
  return stmt(MERGED_SERIES).all(getActiveSyncAccountId(), fromDay, toDay).map(bucketFromRow)
}

/**
 * 全历史活跃度。**不带时间窗** —— 「最长连续天数」这种指标按定义就是问全部历史,
 * 跟着上面的范围切换走的话,选「近 24 小时」会得到一个恒等于 1 的数字。
 * 与概览图同源,同样合并其它设备同步来的数据。
 */
export function getUsageActivityStats(now: number = Date.now()): UsageActivityStats {
  const account = getActiveSyncAccountId()
  const dayRows = stmt(`SELECT DISTINCT day FROM (${MERGED_DAILY}) ORDER BY day ASC`).all(account)
  const activeDays = dayRows.map((row) => String(row['day'] ?? '')).filter((day) => day !== '')

  const peak = stmt(
    `SELECT day,
            SUM(input_tokens + output_tokens + cache_read_tokens + cache_write_tokens) AS total
       FROM (${MERGED_DAILY})
      GROUP BY day
      ORDER BY total DESC, day DESC
      LIMIT 1`
  ).get(account)

  // 「最长一场聊天」依赖 messages,只有写入设备自己算得出;远端设备随快照带来各自的值,取最大
  const remote = stmt(
    `SELECT COUNT(*) AS devices, MAX(longest_chat_ms) AS longest
       FROM usage_remote_origin
      WHERE account_id = ?`
  ).get(account)

  const { current, longest } = computeStreaks(activeDays, localDayOf(now))

  return {
    activeDays,
    currentStreak: current,
    longestStreak: longest,
    peakDayTokens: peak === undefined ? 0 : Number(peak['total'] ?? 0),
    peakDay: peak === undefined ? null : String(peak['day'] ?? '') || null,
    longestChatMs: Math.max(longestChatSpanMs(), Number(remote?.['longest'] ?? 0)),
    syncedDevices: Number(remote?.['devices'] ?? 0)
  }
}

/**
 * 最长的一场连续聊天,毫秒。经典的 gaps-and-islands:先用 `LAG` 求相邻消息间隔,
 * 间隔超阈值处 +1 形成段号,再取各段跨度的最大值。
 *
 * ★ 只看 `internal = 0`。内部协调消息(发给模型但不在气泡里显示的那些)会把
 * 两场相隔很久的聊天连成一场。
 *
 * ★ 用 `messages` 而不是 `runs`:`runs` 一行是一次 run 的时长,不是一场对话;
 * `sessions.updated_at - created_at` 则会被重命名、归档、收藏推进而虚高。
 * 代价是 `messages` **会**被「按时间清理」和「清空对话历史」删掉,清理之后
 * 这个数字会变小 —— 三个源里这是失真最小的一个。
 */
export function longestChatSpanMs(): number {
  const row = stmt(
    `WITH ordered AS (
       SELECT session_id,
              created_at,
              created_at - LAG(created_at) OVER (
                PARTITION BY session_id ORDER BY created_at
              ) AS gap
         FROM messages
        WHERE internal = 0
     ),
     marked AS (
       SELECT session_id,
              created_at,
              SUM(CASE WHEN gap IS NULL OR gap > ? THEN 1 ELSE 0 END) OVER (
                PARTITION BY session_id ORDER BY created_at
                ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
              ) AS segment
         FROM ordered
     )
     SELECT MAX(span) AS longest
       FROM (
         SELECT MAX(created_at) - MIN(created_at) AS span
           FROM marked
          GROUP BY session_id, segment
       )`
  ).get(CHAT_GAP_MS)
  return row === undefined ? 0 : Number(row['longest'] ?? 0)
}
