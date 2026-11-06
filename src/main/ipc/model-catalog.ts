/** User-owned model catalogue rows. Built-in rows live in shared static data. */
import type { ModelCatalogDefinition } from '../../shared/domain/model-catalog'
import { isModelCatalogDefinition } from '../../shared/domain/model-catalog'
import { validateRequestPatches } from '../../shared/domain/request-patch'
import { store } from '../state/store'
import { windows } from '../window/registry'
import { listResolvedModels, preserveModelBindingOverrides } from '../state/model-bindings'

/** This returns custom rows only; callers merge them with the bundled catalogue. */
export function listUserModelCatalog(): ModelCatalogDefinition[] {
  return store.listUserModelCatalog()
}

function broadcast(): void {
  windows.emitToAll('modelCatalog:changed', { custom: listUserModelCatalog() })
  windows.emitToAll('provider:changed', { providers: store.listProviders(), models: listResolvedModels() })
}

function normalizeCatalogModel(model: ModelCatalogDefinition): ModelCatalogDefinition {
  if (!isModelCatalogDefinition(model)) throw new Error('自定义模型目录记录格式无效。')
  const patchValidation = validateRequestPatches(model.requestAdapter?.patches ?? [])
  if (!patchValidation.ok) throw new Error('自定义模型目录的请求 Patch 格式无效。')
  return {
    ...model,
    id: model.id.trim(),
    displayName: model.displayName.trim(),
    manufacturerId: model.manufacturerId.trim(),
    manufacturerLabel: model.manufacturerLabel.trim(),
    capabilities: { ...model.capabilities },
    thinkingConfig: { ...model.thinkingConfig },
    ...(model.requestAdapter === undefined
      ? {}
      : {
          requestAdapter: {
            ...model.requestAdapter,
            patches: patchValidation.patches
          }
        }),
    ...(model.source === undefined ? {} : { source: { ...model.source } }),
    ...(model.reasoningEfforts === undefined
      ? {}
      : { reasoningEfforts: [...model.reasoningEfforts] }),
    ...(model.aliases === undefined ? {} : { aliases: [...model.aliases] })
  }
}

export function upsertUserModelCatalog(
  model: ModelCatalogDefinition
): ModelCatalogDefinition {
  const normalized = normalizeCatalogModel(model)
  preserveModelBindingOverrides()
  const saved = store.putUserModelCatalog(normalized)
  broadcast()
  return saved
}

/** 批量写入:全部校验通过才落库,且只广播一次(逐条广播会让设置页刷新几十遍)。 */
export function upsertUserModelCatalogBatch(models: readonly ModelCatalogDefinition[]): void {
  const normalized = models.map(normalizeCatalogModel)
  if (normalized.length === 0) return
  preserveModelBindingOverrides()
  for (const m of normalized) store.putUserModelCatalog(m)
  broadcast()
}

export function removeUserModelCatalog(id: string): void {
  const normalized = id.trim()
  if (normalized === '') throw new Error('模型 ID 不能为空。')
  preserveModelBindingOverrides()
  store.removeUserModelCatalog(normalized)
  broadcast()
}
