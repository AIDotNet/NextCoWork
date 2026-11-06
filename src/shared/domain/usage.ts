import type { AgentErrorCode } from '../agent/error'
import type { StopReason } from '../agent/stream'
import type { UpstreamProtocol } from './provider'
import type { Currency } from './pricing'

/** Inclusive lower bound and exclusive upper bound for every usage query. */
export interface UsageWindow {
  from?: number
  to: number
}

export type UsageStatusFilter = 'all' | 'success' | 'failed'

/**
 * One row per upstream attempt. The row intentionally contains measurements
 * and routing metadata only: prompts, responses, credentials, and headers are
 * never part of the usage log.
 */
export interface UsageAttemptRecord {
  id: string
  at: number
  runId: string
  workspaceId: string
  sessionId: string
  attempt: number
  providerId: string
  providerName: string
  protocol: UpstreamProtocol | 'unknown'
  endpoint: string
  alias: string
  upstreamModel: string
  responseModel: string | null
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheWriteTokens: number
  cacheWrite1hTokens: number
  thinkingTokens: number | null
  thinkingTokensEstimated: boolean
  latencyMs: number
  timeToFirstTokenMs: number | null
  ok: boolean
  httpStatus: number | null
  errorKind: AgentErrorCode | null
  errorMessage: string | null
  stopReason: StopReason | null
  costMicros: number | null
  currency: Currency | null
  pricingTier: number | null
  pricingWindow: string | null
  toolCalls: number
  toolErrors: number
}

/** Raw attempt emitted by the router before the pricing snapshot is attached. */
export type UnpricedUsageAttempt = Omit<
  UsageAttemptRecord,
  'costMicros' | 'currency' | 'pricingTier' | 'pricingWindow'
>

export interface UsageCostTotal {
  currency: Currency
  micros: number
}

export interface UsageSummary {
  requestCount: number
  successCount: number
  failedCount: number
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheWriteTokens: number
  cacheWrite1hTokens: number
  thinkingTokens: number
  estimatedThinkingRequestCount: number
  totalTokens: number
  cacheHitRate: number | null
  averageLatencyMs: number | null
  averageTimeToFirstTokenMs: number | null
  toolCalls: number
  toolErrors: number
  costs: UsageCostTotal[]
}

export interface UsageRequestLogsQuery extends UsageWindow {
  query?: string
  status?: UsageStatusFilter
  offset?: number
  limit?: number
}

export interface UsageRequestLogsPage {
  items: UsageAttemptRecord[]
  total: number
  offset: number
  limit: number
}

export interface UsageDimensionStat {
  id: string
  label: string
  requestCount: number
  successCount: number
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheWriteTokens: number
  thinkingTokens: number
  averageLatencyMs: number | null
  averageTimeToFirstTokenMs: number | null
  toolCalls: number
  toolErrors: number
  costs: UsageCostTotal[]
}

/**
 * 一天里某个「供应商 × 模型 × 币种」的汇总。趋势图、热力图、模型环形图和
 * 费用表都由这一种行推出来 —— 同一个事实源,所以四张图不会互相打架。
 *
 * ★ `costMicros` 为 null 表示这一桶里**没有一条请求能定价**,和 0(真的免费)
 * 不是一回事。`pricedCount < requestCount` 则表示只有一部分算得出钱,
 * 界面必须把这件事说出来,否则少算的钱看起来和省下的钱一模一样。
 */
export interface UsageDailyBucket {
  /** 本地日期 `YYYY-MM-DD`。 */
  day: string
  providerId: string
  providerName: string
  upstreamModel: string
  /** 展示用别名;为空时界面回退到 `upstreamModel`。 */
  alias: string
  /** 空串 = 这一桶未计价。有值时只会是 `Currency`。 */
  currency: Currency | ''
  requestCount: number
  successCount: number
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheWriteTokens: number
  cacheWrite1hTokens: number
  thinkingTokens: number
  costMicros: number | null
  pricedCount: number
  /** 延迟存 sum + count,跨桶再聚合才不会变成「平均的平均」。 */
  latencySum: number
  ttftSum: number
  ttftCount: number
}

/**
 * 不带时间窗的全历史活跃度指标(参考图顶部那排卡里的后三个)。
 *
 * ★ 活跃日口径取 `usage_daily` 而不是 `sessions`/`messages`:后两者会被
 * 「按时间清理」和「清空对话历史」删掉,用户清一次历史,连续天数就凭空断档;
 * 而 usage 表不被清理,并且和同一页其它图表同源 —— 不会出现「这天图上有费用,
 * 却不算活跃日」这种自相矛盾。
 */
export interface UsageActivityStats {
  /** 有过上游请求的本地日期,升序。热力图直接用它着色。 */
  activeDays: string[]
  /** 截至今天(或昨天)的连续活跃天数。 */
  currentStreak: number
  longestStreak: number
  /** 单日 token 总量的历史峰值。 */
  peakDayTokens: number
  peakDay: string | null
  /**
   * 最长的一场连续聊天,毫秒。按 `messages.created_at` 在会话内分段,
   * 相邻消息间隔超过阈值就断开。
   *
   * ★ 这个指标依赖 `messages`,而它**会**被清理删除 —— 清过历史之后这个数字
   * 会变小。没有更好的源:`runs` 只有单次 run 的时长(不是一场对话),
   * `sessions.updated_at` 会被重命名/归档推进而虚高。
   *
   * 开了使用统计同步时取各设备各自算出的值的最大者(远端设备的 messages 本机看不到)。
   */
  longestChatMs: number
  /**
   * 概览里合并了几台**其它**设备同步来的数据;0 = 纯本机。
   * 需求:概览含其它设备、请求日志只含本机,界面必须把这个口径差说出来,
   * 否则两处数字对不上时用户只会以为统计出错了。
   */
  syncedDevices: number
}
