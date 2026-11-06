/**
 * 其它设备同步来的使用统计(`usage_daily_remote` / `usage_remote_origin`,迁移 v27)的读写,
 * 以及同步要上传的「本机那一片」的读取。
 *
 * 需求:使用统计概览合并同一账户下多台设备的数据(云同步 `usage` category)。
 * 合并后的查询在 `usage-rollup.ts`;快照形状与合并规则在 `config-sync-usage-snapshot.ts`。
 *
 * 不变式:
 * - 远端表里**永远没有本机那一片**。本机的真源是 `usage_daily`;远端快照里带回来的
 *   本机片(可能是旧的)一律丢弃,否则本机的用量会在图上出现两份。
 * - 一个账户的远端数据整体替换(先删后插,同一事务),不做行级增量:
 *   快照本身就是全量,增量只会引入「某台设备删掉的片在本机永远留着」这种漂移。
 */
import { stmt, tx } from './index'
import { bucketFromRow, longestChatSpanMs, refreshUsageRollup } from './usage-rollup'
import type { UsageOriginSlice, UsageSlices } from '../config-sync-usage-snapshot'

const INSERT_REMOTE = `
INSERT INTO usage_daily_remote (
  account_id, origin_id,
  day, provider_id, provider_name, upstream_model, alias, currency,
  request_count, success_count,
  input_tokens, output_tokens,
  cache_read_tokens, cache_write_tokens, cache_write_1h_tokens, thinking_tokens,
  cost_micros, priced_count,
  latency_sum, ttft_sum, ttft_count
) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
`

/**
 * 本机那一片:先把汇总表刷到最新,再整表读出。
 *
 * ★ 必须先刷:汇总表由五分钟一轮的定时器维护,不刷的话上传的是几分钟前的数字,
 *   而另一台设备看到的「今天」会比本机少一截,且要等下一次上传才补上。
 */
export function readLocalUsageSlice(): UsageOriginSlice {
  refreshUsageRollup()
  const rows = stmt(
    'SELECT * FROM usage_daily ORDER BY day, provider_id, upstream_model, currency'
  ).all()
  return { longestChatMs: longestChatSpanMs(), buckets: rows.map(bucketFromRow) }
}

/** 用一份远端快照整体替换这个账户的远端数据;`selfOrigin` 那片被丢弃(见文件头不变式)。 */
export function replaceRemoteUsage(accountId: string, slices: UsageSlices, selfOrigin: string): void {
  tx(() => {
    clearRemoteUsageRows(accountId)
    const insertOrigin = stmt(
      'INSERT INTO usage_remote_origin (account_id, origin_id, longest_chat_ms) VALUES (?, ?, ?)'
    )
    const insertRow = stmt(INSERT_REMOTE)
    for (const [origin, slice] of slices) {
      if (origin === selfOrigin) continue
      insertOrigin.run(accountId, origin, slice.longestChatMs)
      for (const b of slice.buckets) {
        insertRow.run(
          accountId, origin,
          b.day, b.providerId, b.providerName, b.upstreamModel, b.alias, b.currency,
          b.requestCount, b.successCount,
          b.inputTokens, b.outputTokens,
          b.cacheReadTokens, b.cacheWriteTokens, b.cacheWrite1hTokens, b.thinkingTokens,
          b.costMicros, b.pricedCount,
          b.latencySum, b.ttftSum, b.ttftCount
        )
      }
    }
  })
}

/**
 * 这个账户下其它设备的全部分片 —— 组装上传快照时用来原样保留别人的那几片。
 *
 * ★ 排序与 `readLocalUsageSlice` 一致,且数值原样回读:快照指纹靠逐字节相同的规范 JSON
 *   判断「有没有变化」,这里多一次无关的重排就会让每一轮都推一次全量。
 */
export function listRemoteUsage(accountId: string): UsageSlices {
  const slices: UsageSlices = new Map()
  for (const row of stmt(
    'SELECT origin_id, longest_chat_ms FROM usage_remote_origin WHERE account_id = ? ORDER BY origin_id'
  ).all(accountId)) {
    slices.set(String(row['origin_id'] ?? ''), {
      longestChatMs: Number(row['longest_chat_ms'] ?? 0),
      buckets: []
    })
  }
  for (const row of stmt(
    `SELECT * FROM usage_daily_remote WHERE account_id = ?
      ORDER BY origin_id, day, provider_id, upstream_model, currency`
  ).all(accountId)) {
    slices.get(String(row['origin_id'] ?? ''))?.buckets.push(bucketFromRow(row))
  }
  return slices
}

/** 关掉使用统计同步时调用:这个账户的远端数据不再展示,重新打开时从云端重拉。 */
export function clearRemoteUsage(accountId: string): void {
  tx(() => clearRemoteUsageRows(accountId))
}

function clearRemoteUsageRows(accountId: string): void {
  stmt('DELETE FROM usage_daily_remote WHERE account_id = ?').run(accountId)
  stmt('DELETE FROM usage_remote_origin WHERE account_id = ?').run(accountId)
}
