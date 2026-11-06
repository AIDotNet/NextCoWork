/**
 * 生效价目表 = 内置种子 + 用户确认采用的 models.dev 同步行。
 *
 * ★ 计费(`runtime.ts`)和同步 IPC 都从这里取表,所以**采用一次同步价,
 * 之后的用量计价立刻按它走**。不依赖 runtime —— runtime 反过来 import 这里。
 * 已落盘的 `usage_records.cost_micros` 不回算:历史花费在写入那一刻就冻结了。
 */
import { mergePricing } from '../shared/domain/models-dev'
import type { ModelPricing } from '../shared/domain/pricing'
import { PRICING_SEED } from '../shared/domain/pricing-seed'
import { store } from './state/store'

let cache: readonly ModelPricing[] | null = null

export function getEffectivePricing(): readonly ModelPricing[] {
  cache ??= mergePricing(PRICING_SEED, store.listSyncedPricing())
  return cache
}

export function invalidateEffectivePricing(): void {
  cache = null
}
