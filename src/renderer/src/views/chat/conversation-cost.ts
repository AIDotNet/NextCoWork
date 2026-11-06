/*
 * 为会话费用悬停卡归并逐次请求账目。需求：同一轮重试/切换供应商也须按实际模型计入，
 * 不能用 runUsage 的单笔合计反推模型。总额取落盘冻结值；分项没有冻结字段，
 * 仅用请求发生时刻和当前定价规则按比例估算，故意不把估算冒充原始账单。
 */
import type { UsageAttemptRecord } from '../../../../shared/domain/usage'
import { findBuiltinModel } from '../../../../shared/domain/model-catalog-inventory'
import { findPricing, priceOf, type Currency } from '../../../../shared/domain/pricing'
import { getPricingTable } from '../../services/pricing'

type Part = 'input' | 'output' | 'cacheRead' | 'cacheWrite' | 'cacheWrite1h' | 'other'
const PARTS: readonly Part[] = ['input', 'output', 'cacheRead', 'cacheWrite', 'cacheWrite1h', 'other']

export interface ModelCostDetail {
  key: string
  model: string
  provider: string
  currency: Currency | null
  micros: number | null
  tokens: Record<Exclude<Part, 'other'>, number>
  parts: Record<Part, number> | null
  unpriced: boolean
  mixedCurrency: boolean
}

/** 需求：冻结总额必须守恒；当前规则变价时只影响分项比例，不能改写历史总额。 */
function estimateParts(row: UsageAttemptRecord): Record<Part, number> | null {
  const costMicros = row.costMicros
  if (costMicros === null) return null
  const catalog = findBuiltinModel(row.upstreamModel)
  const pricing = findPricing(getPricingTable(), row.providerId, catalog?.pricingModelId ?? catalog?.id ?? row.upstreamModel, row.at)
  if (pricing === null) return null
  // 需求：整次请求共用一个阶梯；逐项独立调用 priceOf 会把输出恒按最低输入档计价。
  const tier = pricing.tiers[row.pricingTier ?? priceOf(pricing, {
    inputTokens: row.inputTokens, outputTokens: row.outputTokens,
    cacheReadInputTokens: row.cacheReadTokens, cacheCreationInputTokens: row.cacheWriteTokens
  }, row.at)?.tier ?? 0]
  if (tier === undefined) return null
  const window = pricing.windows?.find((item) => item.label === row.pricingWindow)
  const rate = window?.rates ?? tier.rate
  const multiplier = window?.rates === undefined ? window?.multiplier ?? 1 : 1
  const values = [
    row.inputTokens * rate.input,
    row.outputTokens * rate.output,
    row.cacheReadTokens === 0 ? 0 : row.cacheReadTokens * (rate.cacheRead ?? NaN),
    row.cacheWriteTokens === row.cacheWrite1hTokens ? 0
      : Math.max(0, row.cacheWriteTokens - row.cacheWrite1hTokens) * (rate.cacheWrite ?? NaN),
    row.cacheWrite1hTokens === 0 ? 0 : row.cacheWrite1hTokens * (rate.cacheWrite1h ?? NaN),
    (rate.perCall ?? 0) * 1_000_000
  ].map((value) => value * multiplier)
  const total = values.reduce((sum, value) => sum + value, 0)
  if (!Number.isFinite(total) || (total === 0 && costMicros !== 0)) return null
  // 需求：舍入尾差归入权重最大的真实分项，不能凭空生成负数的“其他费用”。
  const allocated = values.map((value) => total === 0 ? 0 : Math.floor(costMicros * value / total))
  const largest = values.indexOf(Math.max(...values))
  if (largest >= 0) allocated[largest] = (allocated[largest] ?? 0) + costMicros
    - allocated.reduce((sum, value) => sum + value, 0)
  return Object.fromEntries(PARTS.map((part, index) => [part, allocated[index] ?? 0])) as Record<Part, number>
}

/** 需求：逐模型统计要按实际供应商和上游模型分桶，同名模型不同渠道不可混价。 */
export function summarizeModelCosts(rows: readonly UsageAttemptRecord[]): ModelCostDetail[] {
  const groups = new Map<string, ModelCostDetail>()
  for (const row of rows) {
    const key = JSON.stringify([row.providerId, row.upstreamModel])
    let group = groups.get(key)
    if (group === undefined) {
      group = {
        key, model: row.upstreamModel, provider: row.providerName || row.providerId,
        currency: row.currency, micros: 0,
        tokens: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, cacheWrite1h: 0 },
        parts: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, cacheWrite1h: 0, other: 0 },
        unpriced: false, mixedCurrency: false
      }
      groups.set(key, group)
    }
    group.tokens.input += row.inputTokens
    group.tokens.output += row.outputTokens
    group.tokens.cacheRead += row.cacheReadTokens
    group.tokens.cacheWrite += Math.max(0, row.cacheWriteTokens - row.cacheWrite1hTokens)
    group.tokens.cacheWrite1h += row.cacheWrite1hTokens
    // 需求：一条未计价请求就不能把该模型已知的部分金额当完整费用展示。
    if (row.costMicros === null || row.currency === null) {
      group.unpriced = true
      group.micros = null
      group.parts = null
      continue
    }
    if (group.currency !== null && group.currency !== row.currency) {
      group.mixedCurrency = true
      group.micros = null
      group.parts = null
    }
    if (group.currency === null && !group.mixedCurrency) group.currency = row.currency
    if (group.micros !== null) group.micros += row.costMicros
    const parts = estimateParts(row)
    if (group.parts !== null && parts !== null) {
      for (const part of PARTS) group.parts[part] += parts[part]
    } else group.parts = null
  }
  return [...groups.values()].sort((a, b) => (b.micros ?? -1) - (a.micros ?? -1))
}
