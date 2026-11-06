/**
 * Vendor-first model catalogue domain helpers.
 *
 * The catalogue is deliberately separate from `ProviderPreset` and
 * `ModelAlias`.  A provider preset describes a connection (URL, protocol and
 * credentials); a model catalogue entry describes a model that exists even
 * when no connection has been configured.  `ModelAlias` is only a binding of a
 * catalogue model to one provider.
 */
import type {
  ModelAlias,
  ModelCapabilities,
  ModelModality,
  RequestAdapterConfig,
  ThinkingConfig
} from './provider'
import { validateRequestPatches } from './request-patch'

export interface ModelCatalogSource {
  url: string
  fetchedAt: string
  verifiedAt?: string
}

export type ModelCatalogVerificationStatus =
  | 'official-api'
  | 'official-model-card'
  | 'aggregator-reference'
  | 'unverified'

/** The static metadata shipped with the application (or added by a user). */
export interface ModelCatalogDefinition {
  /** Canonical upstream model id. Keep aggregator prefixes case-sensitive. */
  id: string
  manufacturerId: string
  manufacturerLabel: string
  displayName: string
  modality: ModelModality
  capabilities: ModelCapabilities
  contextWindow: number
  maxOutputTokens: number
  thinkingConfig: ThinkingConfig
  requestAdapter?: RequestAdapterConfig
  source?: ModelCatalogSource
  /** How strongly the canonical id and capability metadata were verified. */
  verificationStatus?: ModelCatalogVerificationStatus
  /** ID used to look up a pricing row; defaults to `id`. */
  pricingModelId?: string
  /** Alternate vendor spellings accepted when matching a provider binding. */
  aliases?: readonly string[]
  /** Reasoning strengths exposed by this model/vendor combination. */
  reasoningEfforts?: readonly NonNullable<ThinkingConfig['defaultEffort']>[]
  /**
   * A user-owned metadata overlay for a bundled row.  This is deliberately
   * explicit so an accidental duplicate custom row cannot silently replace
   * the official catalogue metadata.
   */
  overrideBuiltin?: boolean
}

/** A definition supplied by a user rather than shipped in the seed catalogue. */
export type UserModelCatalogDefinition = ModelCatalogDefinition

export interface ModelCatalogEntry extends ModelCatalogDefinition {
  /** True for rows shipped in the application. */
  builtin: boolean
  /** True when the row came from user configuration. */
  custom: boolean
  /** At least one alias is bound to an AI provider. */
  configured: boolean
  /** All provider bindings for this model (a model may have several). */
  bindings: readonly ModelAlias[]
  providerIds: readonly string[]
  /** The effective enabled state for the row, when a binding exists. */
  enabled?: boolean
  /** A bundled row has a persisted user overlay waiting on top of it. */
  overridden?: boolean
}

export interface MergeModelCatalogOptions {
  builtin: readonly ModelCatalogDefinition[]
  /** User-created rows. They are appended after built-in rows. */
  custom?: readonly UserModelCatalogDefinition[]
  /** Provider×model bindings persisted by the provider service. */
  bindings?: readonly ModelAlias[]
}

/** Prefer exact ids/aliases, then an unambiguous, longest gateway-prefixed match. */
export function findCatalogModel<T extends ModelCatalogDefinition>(definitions: readonly T[], modelId: string): T | undefined {
  const wanted = normalizeModelId(modelId)
  const exact = definitions.find((row) => normalizeModelId(row.id) === wanted)
  if (exact !== undefined) return exact
  const aliases = definitions.filter((row) => row.aliases?.some((id) => normalizeModelId(id) === wanted))
  if (aliases.length === 1) return aliases[0]
  if (aliases.length > 1) return undefined
  const matches = definitions.map((row) => ({ row, length: Math.max(0,
    ...[row.id, ...(row.aliases ?? [])].map((id) => wanted.endsWith(`/${normalizeModelId(id)}`) ? id.trim().length : 0)
  ) })).filter((match) => match.length > 0).sort((a, b) => b.length - a.length)
  return matches[0]?.length === matches[1]?.length ? undefined : matches[0]?.row
}

function cloneCatalogDefinition<T extends ModelCatalogDefinition>(definition: T): T {
  // Catalogue values are IPC-safe JSON data. `structuredClone` also copies
  // nested patch values, toggle values and effort maps that a shallow spread
  // would otherwise leave attached to the persisted/input object.
  return structuredClone(definition)
}

function cloneModelAlias(alias: ModelAlias): ModelAlias {
  return structuredClone(alias)
}

/**
 * Merge the independent catalogue with provider bindings.
 *
 * Important invariants:
 *
 * - The result is never derived from provider presets or a provider's
 *   `/models` response.  A built-in row survives with zero connections.
 * - Built-in metadata is authoritative when a user accidentally creates a
 *   duplicate id. The duplicate is ignored unless it is explicitly marked as
 *   a user overlay; an overlay edits metadata without changing vendor identity.
 * - Every binding is retained.  This matters when OpenAI and an aggregator
 *   expose the same upstream id with different capabilities or pricing.
 * - No input object is mutated; nested arrays are copied for safe UI use.
 */
export function mergeModelCatalog({
  builtin,
  custom = [],
  bindings = []
}: MergeModelCatalogOptions): ModelCatalogEntry[] {
  const definitions = new Map<string, { definition: ModelCatalogDefinition; builtin: boolean }>()

  for (const definition of builtin) {
    if (!validDefinition(definition)) continue
    const key = normalizeModelId(definition.id)
    if (definitions.has(key)) continue
    definitions.set(key, { definition: cloneCatalogDefinition(definition), builtin: true })
  }
  for (const definition of custom) {
    if (!validDefinition(definition)) continue
    const key = normalizeModelId(definition.id)
    const existing = definitions.get(key)
    if (existing === undefined) {
      definitions.set(key, { definition: cloneCatalogDefinition(definition), builtin: false })
      continue
    }
    // An explicit overlay is the only way a user record may edit a bundled
    // row.  Keep the bundled identity/source while merging editable metadata.
    if (existing.builtin && definition.overrideBuiltin === true) {
      definitions.set(key, {
        builtin: true,
        definition: mergeDefinition(existing.definition, definition)
      })
    }
  }

  const byModel = new Map<string, ModelAlias[]>()
  const mergedDefinitions = [...definitions.values()].map(({ definition }) => definition)
  for (const binding of bindings) {
    if (!binding || typeof binding.upstreamModel !== 'string') continue
    const matched = findCatalogModel(mergedDefinitions, binding.upstreamModel)
    const key = normalizeModelId(matched?.id ?? binding.upstreamModel)
    const rows = byModel.get(key) ?? []
    rows.push(cloneModelAlias(binding))
    byModel.set(key, rows)
  }

  return [...definitions.values()].map(({ definition, builtin: isBuiltin }) => {
    const rows = byModel.get(normalizeModelId(definition.id)) ?? []
    const uniqueRows = [...new Map(rows.map((row) => [`${row.providerId}\u0000${row.alias}`, row])).values()]
    const providerIds = [...new Set(uniqueRows.map((row) => row.providerId))]
    // A disabled binding should not hide an enabled binding for the same model.
    // With no binding, leave enabled undefined so the UI can show “unbound”.
    const enabled = uniqueRows.length === 0 ? undefined : uniqueRows.some((row) => row.enabled !== false)
    const customOverlay = custom.find(
      (row) => normalizeModelId(row.id) === normalizeModelId(definition.id) && row.overrideBuiltin === true
    )
    const clonedDefinition = cloneCatalogDefinition(definition)
    return {
      ...clonedDefinition,
      builtin: isBuiltin,
      custom: !isBuiltin,
      configured: uniqueRows.length > 0,
      bindings: uniqueRows.map(cloneModelAlias),
      providerIds,
      ...(enabled === undefined ? {} : { enabled }),
      ...(customOverlay === undefined ? {} : { overridden: true })
    }
  })
}

/**
 * Convert a provider-discovered alias into a user catalogue row.  This is
 * useful for an explicit “Add custom model” action, but is never called while
 * merely loading a provider's model list.
 */
export function catalogDefinitionFromAlias(
  alias: ModelAlias,
  manufacturer: Pick<ModelCatalogDefinition, 'manufacturerId' | 'manufacturerLabel'>
): UserModelCatalogDefinition {
  return cloneCatalogDefinition({
    id: alias.upstreamModel,
    manufacturerId: manufacturer.manufacturerId,
    manufacturerLabel: manufacturer.manufacturerLabel,
    displayName: alias.displayName ?? alias.alias,
    modality: alias.modality ?? 'text',
    capabilities: alias.capabilities,
    contextWindow: alias.contextWindow,
    maxOutputTokens: alias.maxOutputTokens,
    thinkingConfig: alias.thinkingConfig ?? {
      mode: alias.capabilities.thinking ? 'toggle' : 'unsupported',
      defaultEnabled: false
    },
    ...(alias.reasoningEfforts === undefined
      ? {}
      : { reasoningEfforts: alias.reasoningEfforts }),
    ...(alias.requestAdapter === undefined ? {} : { requestAdapter: alias.requestAdapter }),
    ...(alias.source === undefined ? {} : { source: alias.source })
  })
}

/** Runtime guard for IPC/import boundaries. */
export function isModelCatalogDefinition(value: unknown): value is ModelCatalogDefinition {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return false
  const row = value as Record<string, unknown>
  const capabilities = row['capabilities']
  const thinking = row['thinkingConfig']
  if (typeof capabilities !== 'object' || capabilities === null || Array.isArray(capabilities)) {
    return false
  }
  const cap = capabilities as Record<string, unknown>
  if (!['tools', 'vision', 'thinking', 'caching'].every((key) => typeof cap[key] === 'boolean')) {
    return false
  }
  for (const key of [
    'textInput',
    'visionInput',
    'fileInput',
    'videoInput',
    'audioInput',
    'textOutput',
    'imageOutput',
    'videoOutput',
    'audioOutput',
    'webSearch',
    'structuredOutput',
    'streaming',
    'batch'
  ]) {
    if (cap[key] !== undefined && typeof cap[key] !== 'boolean') return false
  }
  if (typeof thinking !== 'object' || thinking === null || Array.isArray(thinking)) return false
  const tc = thinking as Record<string, unknown>
  if (
    typeof tc['mode'] !== 'string' ||
    !['unsupported', 'always', 'toggle', 'effort', 'budget'].includes(tc['mode']) ||
    typeof tc['defaultEnabled'] !== 'boolean'
  ) {
    return false
  }
  if (
    Object.hasOwn(tc, 'parameterPath') &&
    (typeof tc['parameterPath'] !== 'string' || tc['parameterPath'].trim() === '')
  ) {
    return false
  }
  if (
    (Object.hasOwn(tc, 'enabledValue') && !isCatalogJsonValue(tc['enabledValue'])) ||
    (Object.hasOwn(tc, 'disabledValue') && !isCatalogJsonValue(tc['disabledValue']))
  ) return false
  const parameterPath = typeof tc['parameterPath'] === 'string' ? tc['parameterPath'].trim() : ''
  const parameterLeaf = parameterPath.split('.').at(-1)
  if (
    tc['mode'] === 'budget' &&
    (
      parameterLeaf === 'enable_thinking' ||
      parameterLeaf === 'thinking_mode' ||
      parameterPath === 'thinking.enabled' ||
      parameterPath === 'reasoning_split' ||
      parameterPath === 'reasoning_effort' ||
      parameterPath === 'reasoning.effort'
    )
  ) return false
  if (tc['mode'] === 'toggle' && (parameterPath === 'thinking_budget' || parameterPath.endsWith('.budget_tokens'))) return false
  if (Object.hasOwn(tc, 'effortMap')) {
    const effortMap = tc['effortMap']
    if (typeof effortMap !== 'object' || effortMap === null || Array.isArray(effortMap)) return false
    for (const [effort, mapped] of Object.entries(effortMap)) {
      if (!['none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'].includes(effort) || !isCatalogJsonValue(mapped)) return false
    }
  }
  if (
    tc['defaultEffort'] !== undefined &&
    (typeof tc['defaultEffort'] !== 'string' ||
      !['none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'].includes(
        tc['defaultEffort']
      ))
  ) {
    return false
  }
  if (
    tc['defaultBudgetTokens'] !== undefined &&
    (!Number.isInteger(tc['defaultBudgetTokens']) || Number(tc['defaultBudgetTokens']) < 0)
  ) {
    return false
  }
  if (tc['anthropicAdaptive'] !== undefined && typeof tc['anthropicAdaptive'] !== 'boolean') return false
  if (tc['anthropicAdaptive'] === true && tc['mode'] !== 'effort') return false
  if (tc['mode'] === 'effort' && tc['defaultEffort'] === undefined) return false
  if (tc['mode'] === 'unsupported' && tc['defaultEnabled'] !== false) return false
  if (tc['mode'] === 'always' && tc['defaultEnabled'] !== true) return false
  const reasoningEfforts = row['reasoningEfforts']
  if (
    reasoningEfforts !== undefined &&
    (!Array.isArray(reasoningEfforts) ||
      reasoningEfforts.some(
        (effort) =>
          typeof effort !== 'string' ||
          !['none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'].includes(effort)
      ))
  ) {
    return false
  }
  if (tc['anthropicAdaptive'] === true && tc['defaultEnabled'] === false &&
    (!Array.isArray(reasoningEfforts) || !reasoningEfforts.includes('none'))) return false
  if (Array.isArray(reasoningEfforts)) {
    const normalizedEfforts = reasoningEfforts as string[]
    if (new Set(normalizedEfforts).size !== normalizedEfforts.length) return false
    if (
      tc['mode'] === 'effort' &&
      tc['defaultEffort'] !== undefined &&
      !normalizedEfforts.includes(tc['defaultEffort'] as string)
    ) {
      return false
    }
  }
  const aliases = row['aliases']
  if (
    aliases !== undefined &&
    (
      !Array.isArray(aliases) ||
      aliases.some((alias) => typeof alias !== 'string' || alias.trim() === '') ||
      new Set(aliases.map((alias) => alias.trim())).size !== aliases.length
    )
  ) {
    return false
  }
  if (
    row['verificationStatus'] !== undefined &&
    (typeof row['verificationStatus'] !== 'string' ||
      !['official-api', 'official-model-card', 'aggregator-reference', 'unverified'].includes(
        row['verificationStatus']
      ))
  ) {
    return false
  }
  const requestAdapter = row['requestAdapter']
  if (requestAdapter !== undefined) {
    if (typeof requestAdapter !== 'object' || requestAdapter === null || Array.isArray(requestAdapter)) {
      return false
    }
    const adapter = requestAdapter as Record<string, unknown>
    if (
      Object.keys(adapter).some((key) => key !== 'preset' && key !== 'patches') ||
      typeof adapter['preset'] !== 'string' ||
      !['auto', 'anthropic', 'openai-chat', 'openai-responses', 'custom'].includes(
        adapter['preset']
      ) ||
      !Array.isArray(adapter['patches']) ||
      !validateRequestPatches(adapter['patches']).ok
    ) {
      return false
    }
  }
  const source = row['source']
  if (source !== undefined) {
    if (typeof source !== 'object' || source === null || Array.isArray(source)) return false
    const sourceRecord = source as Record<string, unknown>
    if (
      typeof sourceRecord['url'] !== 'string' ||
      sourceRecord['url'].trim() === '' ||
      typeof sourceRecord['fetchedAt'] !== 'string' ||
      sourceRecord['fetchedAt'].trim() === '' ||
      (sourceRecord['verifiedAt'] !== undefined &&
        typeof sourceRecord['verifiedAt'] !== 'string')
    ) {
      return false
    }
  }
  if (
    row['pricingModelId'] !== undefined &&
    (typeof row['pricingModelId'] !== 'string' || row['pricingModelId'].trim() === '')
  ) {
    return false
  }
  if (row['overrideBuiltin'] !== undefined && typeof row['overrideBuiltin'] !== 'boolean') {
    return false
  }
  return validDefinition(value as ModelCatalogDefinition)
}

function isCatalogJsonValue(value: unknown, ancestors = new Set<object>()): boolean {
  if (value === null || typeof value === 'string' || typeof value === 'boolean') return true
  if (typeof value === 'number') return Number.isFinite(value)
  if (typeof value !== 'object' || value === null) return false
  if (
    !Array.isArray(value) &&
    Object.getPrototypeOf(value) !== Object.prototype &&
    Object.getPrototypeOf(value) !== null
  ) {
    return false
  }
  if (ancestors.has(value)) return false

  ancestors.add(value)
  const valid = Array.isArray(value)
    ? value.every((item) => isCatalogJsonValue(item, ancestors))
    : Object.entries(value).every(
        ([key, item]) =>
          !['__proto__', 'prototype', 'constructor'].includes(key.toLowerCase()) &&
          isCatalogJsonValue(item, ancestors)
      )
  ancestors.delete(value)
  return valid
}

function mergeDefinition(
  builtin: ModelCatalogDefinition,
  overlay: ModelCatalogDefinition
): ModelCatalogDefinition {
  const base = cloneCatalogDefinition(builtin)
  const custom = cloneCatalogDefinition(overlay)
  return {
    ...base,
    ...custom,
    // The canonical id and vendor identity come from the bundled record.
    id: base.id,
    manufacturerId: base.manufacturerId,
    manufacturerLabel: base.manufacturerLabel,
    capabilities: { ...base.capabilities, ...custom.capabilities },
    thinkingConfig: { ...base.thinkingConfig, ...custom.thinkingConfig },
    ...(custom.reasoningEfforts === undefined
      ? base.reasoningEfforts === undefined
        ? {}
        : { reasoningEfforts: [...base.reasoningEfforts] }
      : { reasoningEfforts: [...custom.reasoningEfforts] }),
    ...(custom.requestAdapter === undefined
      ? base.requestAdapter === undefined
        ? {}
        : { requestAdapter: base.requestAdapter }
      : { requestAdapter: custom.requestAdapter }),
    // A user metadata overlay cannot rewrite the evidence shipped with a
    // bundled record. User-created rows still own their own source normally.
    ...(base.source === undefined ? {} : { source: base.source }),
    ...(base.verificationStatus === undefined
      ? {}
      : { verificationStatus: base.verificationStatus }),
    overrideBuiltin: true
  }
}

function normalizeModelId(value: string): string {
  return value.trim().toLowerCase()
}

function validDefinition(value: ModelCatalogDefinition): boolean {
  const modalities: readonly ModelCatalogDefinition['modality'][] = [
    'text',
    'image',
    'video',
    'speech',
    'transcription'
  ]
  return (
    typeof value.id === 'string' &&
    value.id.trim().length > 0 &&
    typeof value.manufacturerId === 'string' &&
    value.manufacturerId.trim().length > 0 &&
    typeof value.manufacturerLabel === 'string' &&
    value.manufacturerLabel.trim().length > 0 &&
    typeof value.displayName === 'string' &&
    value.displayName.trim().length > 0 &&
    modalities.includes(value.modality) &&
    Number.isInteger(value.contextWindow) &&
    value.contextWindow > 0 &&
    Number.isInteger(value.maxOutputTokens) &&
    value.maxOutputTokens > 0 &&
    value.maxOutputTokens <= value.contextWindow &&
    value.capabilities.thinking === (value.thinkingConfig.mode !== 'unsupported')
  )
}
