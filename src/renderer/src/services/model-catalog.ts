import type { ModelCatalogSyncPreview } from '../../../shared/domain/models-dev-catalog'
/** Renderer service for explicit user model-catalogue records. */
import type { ModelCatalogDefinition } from '../../../shared/domain/model-catalog'
import { invoke, on } from './ipc'

/** Returns user-created rows; bundled rows are imported from shared catalogue data. */
export function listUserModelCatalog(): Promise<ModelCatalogDefinition[]> {
  return invoke('modelCatalog:list', undefined)
}

export function upsertUserModelCatalog(
  model: ModelCatalogDefinition
): Promise<ModelCatalogDefinition> {
  return invoke('modelCatalog:upsert', model)
}

export function removeUserModelCatalog(id: string): Promise<void> {
  return invoke('modelCatalog:remove', { id })
}

export function onUserModelCatalogChanged(
  callback: (custom: ModelCatalogDefinition[]) => void
): () => void {
  return on('modelCatalog:changed', ({ custom }) => callback(custom))
}

export function previewModelCatalogSync(): Promise<ModelCatalogSyncPreview> {
  return invoke('modelCatalog:syncPreview', undefined)
}

export function applyModelCatalogSync(ids: string[]): Promise<{ applied: number }> {
  return invoke('modelCatalog:syncApply', { ids })
}
