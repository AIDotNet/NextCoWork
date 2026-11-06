/** models.dev 模型目录同步:拉取 → 预览新增与更新 → 只写入用户勾选的。 */
import { parseModelsDevCatalog, type ModelCatalogSyncPreview } from '../../shared/domain/models-dev-catalog'
import { fetchModelsDev } from '../models-dev'
import { getHost } from '../runtime'
import { listUserModelCatalog, upsertUserModelCatalogBatch } from './model-catalog'

/** 预览缓存有效期。过期要求重新预览,避免拿很久以前的数据落库 */
const PREVIEW_TTL_MS = 10 * 60 * 1000

let lastPreview: { at: number; preview: ModelCatalogSyncPreview } | null = null

export async function previewModelCatalogSync(): Promise<ModelCatalogSyncPreview> {
  const json = await fetchModelsDev()
  const now = getHost().clock.now()
  const fetchedAt = new Date(now).toISOString().slice(0, 10)
  // 用户目录里已有的 id(含「覆盖内置行」的那种)同样跳过 —— 内置的由 findBuiltinModel 挡
  const { added, updates } = parseModelsDevCatalog(json, fetchedAt, listUserModelCatalog())
  const preview: ModelCatalogSyncPreview = { fetchedAt, items: added, updates }
  lastPreview = { at: now, preview }
  return preview
}

export function applyModelCatalogSync(ids: readonly string[]): { applied: number } {
  const now = getHost().clock.now()
  if (lastPreview === null || now - lastPreview.at > PREVIEW_TTL_MS) throw new Error('同步预览已过期,请重新同步。')
  const wanted = new Set(ids)
  const { items, updates } = lastPreview.preview
  const rows = [...items.filter((m) => wanted.has(m.id)), ...updates.filter((u) => wanted.has(u.id)).map((u) => u.incoming)]
  if (rows.length === 0) return { applied: 0 }
  upsertUserModelCatalogBatch(rows)
  lastPreview = null
  return { applied: rows.length }
}
