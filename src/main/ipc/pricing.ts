/** models.dev 定价同步:拉取 → 预览差异 → 只采用用户勾选的条目。 */
import { diffPricing, parseModelsDev, type PricingSyncPreview } from '../../shared/domain/models-dev'
import type { ModelPricing } from '../../shared/domain/pricing'
import { getEffectivePricing, invalidateEffectivePricing } from '../pricing-sync'
import { fetchModelsDev } from '../models-dev'
import { getHost } from '../runtime'
import { store } from '../state/store'
import { windows } from '../window/registry'

/** 预览缓存的有效期。过期要求重新预览,避免拿很久以前的价格落库 */
const PREVIEW_TTL_MS = 10 * 60 * 1000

let lastPreview: { at: number; preview: PricingSyncPreview } | null = null

export function listSyncedPricing(): ModelPricing[] {
  return store.listSyncedPricing()
}

function broadcast(): void {
  invalidateEffectivePricing()
  windows.emitToAll('pricing:changed', { synced: listSyncedPricing() })
}

export async function previewPricingSync(): Promise<PricingSyncPreview> {
  const json = await fetchModelsDev()
  const now = getHost().clock.now()
  const fetchedAt = new Date(now).toISOString().slice(0, 10)
  const candidates = parseModelsDev(json, fetchedAt)
  if (candidates.length === 0) throw new Error('models.dev 里没有解析出任何可用定价。')
  // 对比基准是**当前生效表**(含已采用的同步价),不是裸种子
  const preview: PricingSyncPreview = { fetchedAt, total: candidates.length, items: diffPricing(getEffectivePricing(), candidates, now) }
  lastPreview = { at: now, preview }
  return preview
}

export function applyPricingSync(modelIds: readonly string[]): { applied: number } {
  const now = getHost().clock.now()
  if (lastPreview === null || now - lastPreview.at > PREVIEW_TTL_MS) throw new Error('同步预览已过期,请重新同步。')
  const wanted = new Set(modelIds)
  const rows = lastPreview.preview.items.filter((i) => wanted.has(i.modelId)).map((i) => i.incoming)
  if (rows.length === 0) return { applied: 0 }
  store.replaceSyncedPricing(rows)
  lastPreview = null
  broadcast()
  return { applied: rows.length }
}

export function clearSyncedPricing(): void {
  store.clearSyncedPricing()
  broadcast()
}

