import { modelBindingResolver } from '../../shared/domain/model-binding'
import type { ModelAlias } from '../../shared/domain/provider'
import { effectiveModelProtocol } from '../../shared/domain/provider'
import { store } from './store'

export function listResolvedModels(): ModelAlias[] {
  const resolver = modelBindingResolver(store.listUserModelCatalog())
  const providers = store.listProviders()
  const rank = new Map(providers.map((p, i) => [p.id, i]))
  const byId = new Map(providers.map((p) => [p.id, p]))
  return store.listAliases().map((raw) => {
    const alias = resolver.resolve(raw)
    const provider = byId.get(alias.providerId)
    return provider === undefined
      ? alias
      : { ...alias, runtimeProtocol: effectiveModelProtocol(provider, alias) }
  }).sort((a, b) =>
    (rank.get(a.providerId) ?? 1e9) - (rank.get(b.providerId) ?? 1e9) ||
    (a.priority ?? Number.MAX_SAFE_INTEGER) - (b.priority ?? Number.MAX_SAFE_INTEGER) ||
    a.alias.localeCompare(b.alias)
  )
}

/** Freeze legacy ownership against the old catalogue before editing/removing it. */
export function preserveModelBindingOverrides(): void {
  const resolver = modelBindingResolver(store.listUserModelCatalog())
  for (const alias of store.listAliases()) {
    store.putAlias(resolver.resolve(alias))
  }
}
