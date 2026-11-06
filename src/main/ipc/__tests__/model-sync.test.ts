import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { findBuiltinModel } from '../../../shared/domain/model-catalog-inventory'
import { IMPORTED_ALIAS_DEFAULTS } from '../../../shared/domain/provider'
import { closeDatabase, openDatabase } from '../../db/index'
import { getRouter, resetRuntimeForTest } from '../../runtime'
import { store } from '../../state/store'
import { windows } from '../../window/registry'
import { removeUserModelCatalog, upsertUserModelCatalog } from '../model-catalog'
import { listModels, renameModel, setAliases, updateModel, upsertProvider } from '../provider'

let dir = ''
const glm = findBuiltinModel('glm-5.3-flash')!
beforeEach(() => {
  closeDatabase()
  dir = mkdtempSync(join(tmpdir(), 'nextcowork-model-sync-'))
  openDatabase(dir)
  resetRuntimeForTest()
  upsertProvider({ id: 'relay', name: 'Relay', protocol: 'openai-chat', baseUrl: 'https://relay.invalid',
    credentialRef: '', priority: 0, enabled: true })
})
afterEach(() => {
  vi.restoreAllMocks()
  closeDatabase()
  rmSync(dir, { recursive: true, force: true })
})

describe('model metadata synchronization through IPC and runtime', () => {
  it('recomputes read-only protocol metadata and strips renderer copies before saving', () => {
    const [imported] = setAliases('relay', [glm.id])
    const emit = vi.spyOn(windows, 'emitToAll')
    expect(imported?.runtimeProtocol).toBe('openai-chat')
    expect(getRouter().resolveModel(glm.id, 'relay')?.runtimeProtocol).toBe('openai-chat')
    updateModel({ ...imported!, runtimeProtocol: 'anthropic', displayName: 'Renamed' })
    expect(store.listAliases().find((row) => row.providerId === 'relay')).not.toHaveProperty('runtimeProtocol')
    const provider = store.listProviders().find((row) => row.id === 'relay')!
    upsertProvider({ ...provider, protocol: 'anthropic' })
    const projected = listModels('relay')[0]!
    expect(projected.runtimeProtocol).toBe('anthropic')
    expect(getRouter().resolveModel(glm.id, 'relay')?.runtimeProtocol).toBe('anthropic')
    expect(emit).toHaveBeenLastCalledWith('provider:changed', expect.objectContaining({
      models: expect.arrayContaining([expect.objectContaining({ providerId: 'relay', runtimeProtocol: 'anthropic' })])
    }))
    updateModel({ ...projected, protocolOverride: 'openai-responses' })
    expect(listModels('relay')[0]?.runtimeProtocol).toBe('openai-responses')
    expect(getRouter().resolveModel(glm.id, 'relay')?.runtimeProtocol).toBe('openai-responses')
    expect(store.listAliases().find((row) => row.providerId === 'relay')).not.toHaveProperty('runtimeProtocol')
  })

  it('imports capabilities, limits, thinking and adapters, using the same resolved row for runtime', () => {
    const [model] = setAliases('relay', ['z-ai/GLM-5.3-Flash'])
    expect(model).toMatchObject({ thinkingConfig: glm.thinkingConfig, reasoningEfforts: ['low', 'high', 'max'],
      capabilities: glm.capabilities, contextWindow: glm.contextWindow, maxOutputTokens: glm.maxOutputTokens })
    expect(getRouter().listModels().find((row) => row.providerId === 'relay')).toEqual(model)
  })

  it('updates existing generic imports immediately and broadcasts the resolved model after catalogue changes', () => {
    store.putAlias({ ...IMPORTED_ALIAS_DEFAULTS, alias: glm.id, upstreamModel: glm.id, providerId: 'relay' })
    const emit = vi.spyOn(windows, 'emitToAll')
    upsertUserModelCatalog({ ...glm, overrideBuiltin: true,
      thinkingConfig: { ...glm.thinkingConfig, defaultEffort: 'high' }, reasoningEfforts: ['low', 'high'],
      requestAdapter: { preset: 'auto', patches: [{ op: 'add', path: '/temperature', value: 0.3 }] } })
    const model = listModels('relay')[0]!
    expect(model.reasoningEfforts).toEqual(['low', 'high'])
    expect(model.thinkingConfig?.defaultEffort).toBe('high')
    expect(model.requestAdapter?.patches).toHaveLength(1)
    expect(emit).toHaveBeenCalledWith('provider:changed', expect.objectContaining({ models: expect.arrayContaining([model]) }))
    expect(getRouter().listModels().find((row) => row.providerId === 'relay')).toEqual(model)
    removeUserModelCatalog(glm.id)
    expect(listModels('relay')[0]?.reasoningEfforts).toEqual(glm.reasoningEfforts)
    expect(listModels('relay')[0]?.requestAdapter).toEqual(glm.requestAdapter)
  })

  it('preserves provider overrides, renamed aliases, ordering and disabled state across resync and catalogue edits', () => {
    const [model] = setAliases('relay', [glm.id])
    updateModel({ ...model!, maxOutputTokens: 8192, enabled: false,
      thinkingConfig: { mode: 'toggle', defaultEnabled: false, parameterPath: 'enable_thinking' } })
    renameModel('relay', glm.id, 'my-glm')
    upsertUserModelCatalog({ ...glm, overrideBuiltin: true, contextWindow: 800_000,
      thinkingConfig: { ...glm.thinkingConfig, defaultEffort: 'low' }, reasoningEfforts: ['low'] })
    const rows = setAliases('relay', ['unknown', glm.id])
    expect(rows[1]).toMatchObject({ alias: 'my-glm', priority: 1, enabled: false, maxOutputTokens: 8192,
      contextWindow: 800_000, thinkingConfig: { mode: 'toggle', parameterPath: 'enable_thinking' } })
    expect(rows[0]?.thinkingConfig).toBeUndefined()
  })

  it('preserves a model protocol override across provider model-list synchronization', () => {
    const [model] = setAliases('relay', [glm.id])
    updateModel({ ...model!, protocolOverride: 'anthropic' })
    const rows = setAliases('relay', ['new-model', glm.id])
    expect(rows.find((row) => row.upstreamModel === glm.id)?.protocolOverride).toBe('anthropic')
    expect(rows.find((row) => row.upstreamModel === 'new-model')?.protocolOverride).toBeUndefined()
  })

  it('pins anthropic-family models to the anthropic protocol on openai-family providers', () => {
    const rows = setAliases('relay', ['anthropic/claude-opus-5', 'z-ai/GLM-5.3-Flash'])
    expect(rows.find((row) => row.upstreamModel === 'anthropic/claude-opus-5')?.protocolOverride).toBe('anthropic')
    expect(rows.find((row) => row.upstreamModel === 'z-ai/GLM-5.3-Flash')?.protocolOverride).toBeUndefined()
  })

  it('lets anthropic-family models inherit when the provider already speaks anthropic', () => {
    upsertProvider({ id: 'anthropic-relay', name: 'Anthropic Relay', protocol: 'anthropic',
      baseUrl: 'https://relay.invalid', credentialRef: '', priority: 0, enabled: true })
    const [row] = setAliases('anthropic-relay', ['claude-fable-5.1'])
    expect(row?.protocolOverride).toBeUndefined()
  })

  it('rejects an adaptive default Off without Off support and keeps the saved declaration', () => {
    const [model] = setAliases('relay', ['claude-opus-5-5'])
    expect(() => updateModel({ ...model!,
      thinkingConfig: { ...model!.thinkingConfig!, defaultEnabled: false } })).toThrow(/配置无效/u)
    expect(listModels('relay')[0]?.thinkingConfig?.defaultEnabled).toBe(true)
  })

  it('accepts default Off on an adaptive binding with confirmed Off support', () => {
    const [model] = setAliases('relay', ['claude-opus-5'])
    expect(updateModel({ ...model!,
      thinkingConfig: { ...model!.thinkingConfig!, defaultEnabled: false } }).thinkingConfig)
      .toMatchObject({ defaultEnabled: false, anthropicAdaptive: true })
  })

  it('clears the adaptive marker on save when the reasoning editor changes its mode or path', () => {
    const [model] = setAliases('relay', ['claude-opus-5-5'])
    for (const thinkingConfig of [
      { ...model!.thinkingConfig!, anthropicAdaptive: undefined, parameterPath: 'reasoning_effort' },
      { mode: 'budget' as const, defaultEnabled: true, defaultBudgetTokens: 4096,
        parameterPath: 'thinking.budget_tokens', anthropicAdaptive: undefined }
    ]) {
      const saved = updateModel({ ...model!, thinkingConfig })
      expect(saved.thinkingConfig?.anthropicAdaptive).toBeUndefined()
      expect(saved.thinkingConfig?.parameterPath).toBe(thinkingConfig.parameterPath)
    }
  })

  it('keeps effort parameter paths on save and rejects inconsistent supported/default efforts', () => {
    const [model] = setAliases('relay', [glm.id])
    expect(updateModel({ ...model!, thinkingConfig: { ...model!.thinkingConfig!, defaultBudgetTokens: undefined } })
      .thinkingConfig?.parameterPath).toBe('reasoning_effort')
    expect(() => updateModel({ ...model!, reasoningEfforts: ['low'] })).toThrow(/配置无效/u)
    expect(listModels('relay')[0]?.reasoningEfforts).toEqual(glm.reasoningEfforts)
  })
})
