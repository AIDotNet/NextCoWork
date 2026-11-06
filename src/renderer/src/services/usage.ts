import type {
  UsageActivityStats,
  UsageAttemptRecord,
  UsageDailyBucket,
  UsageDimensionStat,
  UsageRequestLogsPage,
  UsageRequestLogsQuery,
  UsageSummary,
  UsageWindow
} from '../../../shared/domain/usage'
import { invoke } from './ipc'

export function getUsageSummary(window: UsageWindow): Promise<UsageSummary> {
  return invoke('usage:getSummary', window)
}

export function getUsageRequestLogs(
  query: UsageRequestLogsQuery
): Promise<UsageRequestLogsPage> {
  return invoke('usage:getRequestLogs', query)
}

// 需求：会话费用明细必须按 session_id 取全量账目，不能按日志搜索词模糊匹配。
export function getSessionUsageAttempts(sessionId: string): Promise<UsageAttemptRecord[]> {
  return invoke('usage:getSessionAttempts', { sessionId })
}

export function getUsageProviderStats(window: UsageWindow): Promise<UsageDimensionStat[]> {
  return invoke('usage:getProviderStats', window)
}

export function getUsageModelStats(window: UsageWindow): Promise<UsageDimensionStat[]> {
  return invoke('usage:getModelStats', window)
}

/** 概览区的每日汇总。★ 主进程会先刷一次汇总表再查,比上面几条慢,别放进轮询。 */
export function getUsageDailySeries(window: UsageWindow): Promise<UsageDailyBucket[]> {
  return invoke('usage:getDailySeries', window)
}

/** 全历史活跃度。不接受时间窗 —— 连续天数按定义就是问全部历史。 */
export function getUsageActivityStats(): Promise<UsageActivityStats> {
  return invoke('usage:getActivityStats', undefined)
}
