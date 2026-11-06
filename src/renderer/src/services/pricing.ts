import { useSyncExternalStore } from 'react'
import type { PricingSyncPreview } from '../../../shared/domain/models-dev'
import { mergePricing } from '../../../shared/domain/models-dev'
import type { ModelPricing } from '../../../shared/domain/pricing'
import { PRICING_SEED } from '../../../shared/domain/pricing-seed'
import { invoke, on } from './ipc'

/**
 * 生效价目表(种子 + 已采用的 models.dev 同步行)的渲染层副本。
 * 主进程按同一规则合并后计费;这里只是让界面显示的价和实际计价对得上。
 * 同步行读不到时退回纯种子 —— 界面不该因为定价库读失败而没有价。
 */
let synced: readonly ModelPricing[] = []
let table: readonly ModelPricing[] = PRICING_SEED
let started = false
const listeners = new Set<() => void>()

function set(next: readonly ModelPricing[]): void {
  synced = next
  table = mergePricing(PRICING_SEED, next)
  for (const l of listeners) l()
}

function start(): void {
  if (started) return
  started = true
  try {
    void invoke('pricing:listSynced', undefined).then(set, () => undefined)
    on('pricing:changed', ({ synced: next }) => set(next))
  } catch {
    // preload 桥不在(测试 / 预览):保持纯种子
  }
}

export function getPricingTable(): readonly ModelPricing[] {
  start()
  return table
}

export function getSyncedPricing(): readonly ModelPricing[] {
  start()
  return synced
}

function subscribe(listener: () => void): () => void {
  start()
  listeners.add(listener)
  return () => listeners.delete(listener)
}

export function usePricingTable(): readonly ModelPricing[] {
  return useSyncExternalStore(subscribe, getPricingTable)
}

export function useSyncedPricing(): readonly ModelPricing[] {
  return useSyncExternalStore(subscribe, getSyncedPricing)
}

export function previewPricingSync(): Promise<PricingSyncPreview> {
  return invoke('pricing:syncPreview', undefined)
}

export function applyPricingSync(modelIds: string[]): Promise<{ applied: number }> {
  return invoke('pricing:syncApply', { modelIds })
}

export function clearSyncedPricing(): Promise<void> {
  return invoke('pricing:clearSynced', undefined)
}
