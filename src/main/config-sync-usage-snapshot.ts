/**
 * 加密配置同步 v2 的 `usage` 快照:数据形状与合并规则(纯函数,不碰库、不碰网络)。
 *
 * ## 需求
 *
 * 使用统计页的概览(热力图、趋势、模型环形图、费用表、连续天数)要合并同一账户下
 * 多台设备的数据;**请求日志不跨设备**(`usage_records` 整张表留在本机,不进快照)。
 *
 * ## 形状
 *
 * `{ devices: { [originId]: { longestChatMs, rows } } }` —— 每台设备一片。
 *
 * - 键是 originId = 本机 `ensureSyncDeviceId()`(存在本机库里,重新登记不会变)。
 *   ★ 不能用同步会话的 deviceId:那个值每次在 vault 上重新登记都会换成
 *   `${base}:${random}`,用它做键,同一台机器重登一次就多出一片,历史用量被算两遍,
 *   而图上看不出任何异常。
 * - 一片**只由它自己的设备改写**。合并 = 远端其它片原样保留 + 本机那片整体替换,
 *   所以冲突总能自动解,不存在「保留本地 / 使用远端」二选一 —— 那会丢掉某台设备的数据。
 * - 行是 `usage_daily` 的一行,用定长数组而不是对象:键名重复几万次会让快照膨胀两倍多,
 *   而整份快照的上限是 8MiB(`SYNC_MAX_CIPHERTEXT_BYTES`)。列序见 `USAGE_ROW_COLUMNS`,
 *   ★ 只能在末尾追加列;改顺序等于让新旧客户端互相把 token 数读成费用。
 *
 * ## 故意不做
 *
 * - 不做跨时区对齐:`day` 是写入设备的本地日期,两台设备时区不同时同一天会错开。
 * - 不去重:从备份恢复 / 整库拷到另一台机器后,两台机器的 `usage_daily` 含同一段历史,
 *   会被算两遍。按日汇总里没有能去重的键,这是「同步日汇总」方案明知的代价。
 */
import { z } from 'zod'
import type { UsageDailyBucket } from '../shared/domain/usage'

/** 一台设备的全部日汇总 + 它本机算出的「最长一场聊天」。 */
export interface UsageOriginSlice {
  longestChatMs: number
  buckets: UsageDailyBucket[]
}

/** originId → 那台设备的一片。 */
export type UsageSlices = Map<string, UsageOriginSlice>

/** 快照里一行的列序(文档用;真正的约束是下面的 tuple schema)。 */
export const USAGE_ROW_COLUMNS = [
  'day', 'providerId', 'providerName', 'upstreamModel', 'alias', 'currency',
  'requestCount', 'successCount',
  'inputTokens', 'outputTokens', 'cacheReadTokens', 'cacheWriteTokens', 'cacheWrite1hTokens', 'thinkingTokens',
  'costMicros', 'pricedCount', 'latencySum', 'ttftSum', 'ttftCount'
] as const

const counter = z.number().int().nonnegative().max(Number.MAX_SAFE_INTEGER)
const text = z.string().max(512)
const rowSchema = z.tuple([
  z.string().regex(/^\d{4}-\d{2}-\d{2}$/), text, text, text, text,
  // ★ 空串 = 这一桶未计价,与 `UsageDailyBucket.currency` 同义
  z.enum(['', 'USD', 'CNY']),
  counter, counter,
  counter, counter, counter, counter, counter, counter,
  // ★ null = 一条也算不出钱,和 0(真的免费)不是一回事,见 `UsageDailyBucket.costMicros`
  counter.nullable(), counter, counter, counter, counter
])
type UsageRow = z.infer<typeof rowSchema>

const sliceSchema = z.object({
  longestChatMs: counter,
  // 20 万行 ≈ 远超 8MiB 快照上限,只是给恶意/损坏输入一个硬边界
  rows: z.array(rowSchema).max(200_000)
}).strict()

const dataSchema = z.object({
  // 与服务端 SyncLimits.MaxDevicesPerVault 同数
  devices: z.record(z.string().min(1).max(128), sliceSchema)
}).strict().refine((data) => Object.keys(data.devices).length <= 100)

export type UsageSyncData = z.infer<typeof dataSchema>

function toRow(bucket: UsageDailyBucket): UsageRow {
  return [
    bucket.day, bucket.providerId, bucket.providerName, bucket.upstreamModel, bucket.alias, bucket.currency,
    bucket.requestCount, bucket.successCount,
    bucket.inputTokens, bucket.outputTokens, bucket.cacheReadTokens, bucket.cacheWriteTokens,
    bucket.cacheWrite1hTokens, bucket.thinkingTokens,
    bucket.costMicros, bucket.pricedCount, bucket.latencySum, bucket.ttftSum, bucket.ttftCount
  ]
}

function fromRow(row: UsageRow): UsageDailyBucket {
  return {
    day: row[0], providerId: row[1], providerName: row[2], upstreamModel: row[3], alias: row[4], currency: row[5],
    requestCount: row[6], successCount: row[7],
    inputTokens: row[8], outputTokens: row[9], cacheReadTokens: row[10], cacheWriteTokens: row[11],
    cacheWrite1hTokens: row[12], thinkingTokens: row[13],
    costMicros: row[14], pricedCount: row[15], latencySum: row[16], ttftSum: row[17], ttftCount: row[18]
  }
}

/** 解密后的明文 → 各设备分片。形状不对抛错,调用方在动本地库之前调用。 */
export function parseUsageSyncData(input: unknown): UsageSlices {
  const data = dataSchema.parse(input)
  const slices: UsageSlices = new Map()
  for (const [origin, slice] of Object.entries(data.devices)) {
    slices.set(origin, { longestChatMs: slice.longestChatMs, buckets: slice.rows.map(fromRow) })
  }
  return slices
}

/**
 * 各设备分片 → 快照明文。
 *
 * ★ 输出必须是确定的:设备按 originId 排序,行按汇总表主键排序。快照的指纹
 * (`syncDigest`)拿它和上一次的比,顺序一变就会在「什么都没变」时也推一次全量。
 */
export function buildUsageSyncData(slices: UsageSlices): UsageSyncData {
  const devices: UsageSyncData['devices'] = {}
  for (const origin of [...slices.keys()].sort()) {
    const slice = slices.get(origin)
    if (slice === undefined) continue
    devices[origin] = {
      longestChatMs: slice.longestChatMs,
      rows: [...slice.buckets].sort(compareBuckets).map(toRow)
    }
  }
  return { devices }
}

function compareBuckets(a: UsageDailyBucket, b: UsageDailyBucket): number {
  const left = [a.day, a.providerId, a.upstreamModel, a.currency]
  const right = [b.day, b.providerId, b.upstreamModel, b.currency]
  for (let i = 0; i < left.length; i++) {
    const x = left[i] ?? ''
    const y = right[i] ?? ''
    if (x !== y) return x < y ? -1 : 1
  }
  return 0
}

/**
 * 合并:远端其它设备的片原样保留,本机那片整体换成 `local`。
 *
 * 需求:这是冲突自动解的全部规则 —— 每片只有一个写者,所以「拿远端最新 + 换掉自己」
 * 永远不丢任何一台设备的数据。
 */
export function mergeUsageSlices(remote: UsageSlices, origin: string, local: UsageOriginSlice): UsageSlices {
  const merged: UsageSlices = new Map(remote)
  merged.set(origin, local)
  return merged
}
