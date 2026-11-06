import { describe, expect, it } from 'vitest'
import { modelBindingResolver } from '../model-binding'
import { findCatalogModel, type ModelCatalogDefinition } from '../model-catalog'
import { BUILTIN_MODEL_CATALOG, findBuiltinModel, OLLAMA_STANDARD_THINKING } from '../model-catalog-inventory'
import { effectiveModelProtocol, IMPORTED_ALIAS_DEFAULTS, type ModelAlias } from '../provider'

const imported = (overrides: Partial<ModelAlias> = {}): ModelAlias => ({
  ...structuredClone(IMPORTED_ALIAS_DEFAULTS), providerId: 'relay', alias: 'glm',
  upstreamModel: 'z-ai/GLM-5.3-Flash', ...overrides
})
const glm = findBuiltinModel('glm-5.3-flash')!

describe('catalogue-backed provider metadata', () => {
  it('migrates an unmodified Opus 5.5 budget binding to adaptive effort', () => {
    const resolver = modelBindingResolver()
    const legacy = imported({
      alias: 'opus', upstreamModel: 'claude-opus-5-5',
      thinkingConfig: { mode: 'budget', defaultEnabled: true, defaultBudgetTokens: 64_000,
        parameterPath: 'thinking.budget_tokens' }
    })
    const resolved = resolver.resolve(legacy)
    expect(resolved.thinkingConfig).toMatchObject({ mode: 'effort', anthropicAdaptive: true, defaultEffort: 'medium' })
    expect(resolved.catalogOverrides).not.toContain('thinkingConfig')
    expect(resolved.reasoningEfforts).toEqual(['low', 'medium', 'high', 'xhigh', 'max'])
    expect(resolver.resolve({ ...legacy, catalogOverrides: ['thinkingConfig'] }).thinkingConfig?.mode).toBe('budget')
    expect(resolver.resolve(imported({ upstreamModel: 'claude-opus-5' })).thinkingConfig?.mode).toBe('effort')
  })

  /*
    旧目录抄下来的 budget 只有三档:opus 64K、haiku 16K、其余 32K。
    抄得一字不差才跟着新 adaptive 声明走;改过的预算和显式 override 仍是用户的。
  */
  const legacyAdaptive = [
    ['claude-opus-5-5', 64_000, 'medium', ['low', 'medium', 'high', 'xhigh', 'max']],
    ['claude-opus-5', 64_000, 'high', ['none', 'low', 'medium', 'high', 'xhigh', 'max']],
    ['claude-sonnet-5-5', 32_000, 'high', ['low', 'medium', 'high', 'xhigh', 'max']],
    ['claude-sonnet-5', 32_000, 'high', ['none', 'low', 'medium', 'high', 'xhigh', 'max']],
    ['claude-haiku-5-5', 16_000, 'medium', ['none', 'low', 'medium', 'high', 'xhigh', 'max']],
    ['claude-mythos-5-1', 32_000, 'high', ['low', 'medium', 'high', 'xhigh', 'max']],
    ['claude-fable-5-1', 32_000, 'high', ['low', 'medium', 'high', 'xhigh', 'max']],
    ['claude-mythos-5', 32_000, 'high', ['low', 'medium', 'high', 'xhigh', 'max']],
    ['claude-fable-5', 32_000, 'high', ['low', 'medium', 'high', 'xhigh', 'max']],
    ['claude-opus-4-8', 64_000, 'high', ['none', 'low', 'medium', 'high', 'xhigh', 'max']],
    ['claude-opus-4-7', 64_000, 'high', ['none', 'low', 'medium', 'high', 'xhigh', 'max']],
    ['claude-opus-4-6', 64_000, 'high', ['none', 'low', 'medium', 'high', 'max']],
    ['claude-sonnet-4-6', 32_000, 'high', ['none', 'low', 'medium', 'high', 'max']]
  ] as const

  it('names every adaptive Anthropic catalogue row in the legacy budget table', () => {
    const adaptive = BUILTIN_MODEL_CATALOG
      .filter((row) => row.manufacturerId === 'anthropic' && row.thinkingConfig.anthropicAdaptive === true)
      .map((row) => row.id)
      .sort()
    expect(adaptive).toEqual([...legacyAdaptive.map(([id]) => id)].sort())
  })

  it.each(legacyAdaptive)('migrates an unmodified %s budget copy of %i to adaptive effort', (id, budget, defaultEffort, efforts) => {
    const resolved = modelBindingResolver().resolve(imported({
      upstreamModel: id,
      thinkingConfig: {
        mode: 'budget', defaultEnabled: true, defaultBudgetTokens: budget,
        parameterPath: 'thinking.budget_tokens'
      }
    }))
    expect(resolved.thinkingConfig).toMatchObject({
      mode: 'effort', anthropicAdaptive: true, defaultEffort, parameterPath: 'output_config.effort'
    })
    expect(resolved.reasoningEfforts).toEqual([...efforts])
    expect(resolved.catalogOverrides).toEqual([])
  })

  it('keeps an explicit budget override and a budget that is not the old catalogue copy', () => {
    const resolver = modelBindingResolver()
    const copied = {
      mode: 'budget' as const, defaultEnabled: true, defaultBudgetTokens: 64_000,
      parameterPath: 'thinking.budget_tokens'
    }
    const pinned = resolver.resolve(imported({
      upstreamModel: 'claude-opus-5-5', thinkingConfig: copied, catalogOverrides: ['thinkingConfig']
    }))
    expect(pinned.thinkingConfig).toMatchObject(copied)
    expect(pinned.catalogOverrides).toContain('thinkingConfig')
    for (const [upstreamModel, defaultBudgetTokens] of [
      ['claude-opus-5-5', 32_000],
      ['claude-haiku-5-5', 32_000],
      ['claude-sonnet-5-5', 64_000],
      ['claude-fable-5', 4_096]
    ] as const) {
      const model = resolver.resolve(imported({
        upstreamModel,
        thinkingConfig: {
          mode: 'budget', defaultEnabled: true, defaultBudgetTokens,
          parameterPath: 'thinking.budget_tokens'
        }
      }))
      expect(model.thinkingConfig, upstreamModel).toMatchObject({ mode: 'budget', defaultBudgetTokens })
      expect(model.thinkingConfig, upstreamModel).not.toHaveProperty('anthropicAdaptive')
      expect(model.catalogOverrides, upstreamModel).toEqual(['thinkingConfig', 'reasoningEfforts'])
    }
  })

  it.each([
    ['gateway/claude-opus-6-1', 'high', ['low', 'medium', 'high']],
    ['claude-newline-6', 'high', ['low', 'medium', 'high']],
    ['claude-opus-4.6', 'high', ['none', 'low', 'medium', 'high', 'max']],
    ['vendor/claude-sonnet-4-6-20260301', 'high', ['none', 'low', 'medium', 'high', 'max']],
    ['claude-opus-4.7', 'high', ['none', 'low', 'medium', 'high', 'xhigh', 'max']],
    ['claude-sonnet-5.5', 'high', ['low', 'medium', 'high', 'xhigh', 'max']],
    ['claude-opus-5-5-20261008', 'medium', ['low', 'medium', 'high', 'xhigh', 'max']],
    ['claude-opus-5-5.20261008', 'medium', ['low', 'medium', 'high', 'xhigh', 'max']],
    ['claude-sonnet-5-5.20261008', 'high', ['low', 'medium', 'high', 'xhigh', 'max']],
    ['claude-opus-4-6.20260205', 'high', ['none', 'low', 'medium', 'high', 'max']],
    ['claude-opus-4.6.20260205', 'high', ['none', 'low', 'medium', 'high', 'max']]
  ] as const)('infers a conservative adaptive binding for uncatalogued %s', (id, defaultEffort, efforts) => {
    expect(findBuiltinModel(id)).toBeUndefined()
    const resolved = modelBindingResolver().resolve(imported({ upstreamModel: id }))
    expect(resolved.thinkingConfig).toMatchObject({
      mode: 'effort', anthropicAdaptive: true, defaultEffort, parameterPath: 'output_config.effort'
    })
    expect(resolved.reasoningEfforts).toEqual([...efforts])
    expect(resolved.capabilities.thinking).toBe(true)
    expect(resolved.catalogOverrides).not.toContain('thinkingConfig')
  })

  /*
    未知 id 上 catalogOverrides:[] 表示「没人改过」,过期的 budget / adaptive 副本
    跟着当前推断走。显式 thinkingConfig 与 reasoningEfforts 仍钉住。legacy 记录没有
    这份清单:推断结果重读不能把自己记成覆盖,改过的 budget 才是用户的。
  */
  const staleUnknownOpus = [
    {
      thinkingConfig: {
        mode: 'budget' as const, defaultEnabled: true, defaultBudgetTokens: 64_000,
        parameterPath: 'thinking.budget_tokens'
      },
      reasoningEfforts: ['none', 'low', 'medium', 'high', 'max'] as const
    },
    {
      thinkingConfig: {
        mode: 'effort' as const, defaultEnabled: true, defaultEffort: 'medium' as const,
        parameterPath: 'output_config.effort', anthropicAdaptive: true
      },
      reasoningEfforts: ['none', 'low', 'medium', 'high', 'max'] as const
    }
  ]

  it('reinfers adaptive effort for unknown claude-opus-6-1 when an empty override list stored a stale copy', () => {
    expect(findBuiltinModel('claude-opus-6-1')).toBeUndefined()
    const resolver = modelBindingResolver()
    const fresh = resolver.resolve(imported({ upstreamModel: 'claude-opus-6-1', catalogOverrides: [] }))
    for (const raw of [
      imported({ upstreamModel: 'claude-opus-6-1', catalogOverrides: [] }),
      ...staleUnknownOpus.map((stored) => imported({
        upstreamModel: 'claude-opus-6-1', catalogOverrides: [], ...stored
      })),
      imported({
        upstreamModel: 'claude-opus-6-1', catalogOverrides: [],
        thinkingConfig: fresh.thinkingConfig, reasoningEfforts: fresh.reasoningEfforts
      })
    ]) {
      const resolved = resolver.resolve(raw)
      expect(resolved.thinkingConfig).toMatchObject({
        mode: 'effort', anthropicAdaptive: true, defaultEffort: 'high', parameterPath: 'output_config.effort'
      })
      expect(resolved.reasoningEfforts).toEqual(['low', 'medium', 'high'])
      expect(resolved.catalogOverrides).toEqual([])
    }
  })

  it('keeps explicit thinkingConfig and reasoningEfforts overrides on those unknown Claude records', () => {
    const resolver = modelBindingResolver()
    for (const stored of staleUnknownOpus) {
      const resolved = resolver.resolve(imported({
        upstreamModel: 'claude-opus-6-1', ...stored,
        catalogOverrides: ['thinkingConfig', 'reasoningEfforts']
      }))
      expect(resolved.thinkingConfig).toEqual(stored.thinkingConfig)
      expect(resolved.reasoningEfforts).toEqual([...stored.reasoningEfforts])
      expect(resolved.catalogOverrides).toEqual(['thinkingConfig', 'reasoningEfforts'])
    }
  })

  it('does not pin inferred thinking when a resolved unknown Claude id is reread without catalogOverrides', () => {
    const resolver = modelBindingResolver()
    const generated = resolver.resolve(imported({ upstreamModel: 'claude-opus-6-1' }))
    delete generated.catalogOverrides
    const again = resolver.resolve(generated)
    expect(again.catalogOverrides).not.toContain('thinkingConfig')
    expect(again.catalogOverrides).not.toContain('reasoningEfforts')
    expect(again.catalogOverrides).not.toContain('capabilities.thinking')
    expect(again.thinkingConfig).toEqual(generated.thinkingConfig)
    expect(again.reasoningEfforts).toEqual(generated.reasoningEfforts)
  })

  it('keeps a user budget on an unknown Claude id that never recorded catalogOverrides', () => {
    const budget = {
      mode: 'budget' as const, defaultEnabled: true, defaultBudgetTokens: 4_096,
      parameterPath: 'thinking.budget_tokens'
    }
    const model = modelBindingResolver().resolve(imported({
      upstreamModel: 'claude-opus-6-1', thinkingConfig: budget
    }))
    expect(model.thinkingConfig).toMatchObject(budget)
    expect(model.thinkingConfig).not.toHaveProperty('anthropicAdaptive')
    expect(model.catalogOverrides).toEqual(['thinkingConfig', 'reasoningEfforts'])
  })

  it('leaves non-Claude and Claude 4.5 declarations unchanged', () => {
    const resolver = modelBindingResolver()
    const gpt = resolver.resolve(imported({ upstreamModel: 'gpt-5.4' }))
    expect(gpt.thinkingConfig).toMatchObject({ mode: 'effort', parameterPath: 'reasoning_effort' })
    expect(gpt.thinkingConfig).not.toHaveProperty('anthropicAdaptive')
    expect(resolver.resolve(imported({ upstreamModel: 'claude-opus-4-5' })).thinkingConfig)
      .toMatchObject({ mode: 'budget', defaultBudgetTokens: 64_000, parameterPath: 'thinking.budget_tokens' })
    expect(resolver.resolve(imported({ upstreamModel: 'claude-sonnet-4-5' })).thinkingConfig)
      .toMatchObject({ mode: 'budget', defaultBudgetTokens: 32_000 })
    expect(resolver.resolve(imported({ upstreamModel: 'claude-haiku-4-5-20251001' })).thinkingConfig)
      .toMatchObject({ mode: 'budget', defaultBudgetTokens: 16_000 })
    const dotted = resolver.resolve(imported({ upstreamModel: 'claude-opus-4.5' }))
    expect(dotted.thinkingConfig).toBeUndefined()
    expect(dotted.capabilities.thinking).toBe(false)
  })

  it.each(['ollama', 'ollama-cloud'] as const)('does not invent adaptive thinking for an unknown Claude id on %s', (providerId) => {
    const model = modelBindingResolver().resolve(imported({ providerId, upstreamModel: 'claude-opus-6-1' }))
    expect(model.thinkingConfig).toBeUndefined()
    expect(model.reasoningEfforts).toBeUndefined()
  })

  it('keeps a catalogued Claude model on Ollama on the standard wire', () => {
    const model = modelBindingResolver().resolve(imported({
      providerId: 'ollama-cloud', upstreamModel: 'claude-opus-5-5'
    }))
    expect(model.thinkingConfig).toEqual(OLLAMA_STANDARD_THINKING)
    expect(model.reasoningEfforts).toEqual(['none', 'low', 'medium', 'high', 'max'])
  })

  it('lets a custom catalogue row replace the unknown Claude adaptive fallback', () => {
    const custom: ModelCatalogDefinition = {
      ...glm, id: 'claude-opus-6-1', displayName: 'Custom Opus',
      thinkingConfig: {
        mode: 'budget', defaultEnabled: true, defaultBudgetTokens: 4_096,
        parameterPath: 'thinking.budget_tokens'
      }
    }
    const model = modelBindingResolver([custom]).resolve(imported({ upstreamModel: 'claude-opus-6-1' }))
    expect(model.thinkingConfig).toMatchObject({ mode: 'budget', defaultBudgetTokens: 4_096 })
    expect(model.thinkingConfig).not.toHaveProperty('anthropicAdaptive')
  })

  it('treats an explicit thinking-capability override as off, including unknown Claude ids', () => {
    const resolver = modelBindingResolver()
    const capabilities = { ...IMPORTED_ALIAS_DEFAULTS.capabilities, thinking: false }
    const known = resolver.resolve(imported({
      upstreamModel: 'claude-opus-5-5', capabilities, catalogOverrides: ['capabilities.thinking']
    }))
    expect(known.thinkingConfig).toEqual({ mode: 'unsupported', defaultEnabled: false })
    expect(known.capabilities.thinking).toBe(false)
    const unknown = resolver.resolve(imported({
      upstreamModel: 'claude-opus-6-1', capabilities, catalogOverrides: ['capabilities.thinking']
    }))
    expect(unknown.thinkingConfig).toBeUndefined()
    expect(unknown.capabilities.thinking).toBe(false)
    expect(unknown.reasoningEfforts).toBeUndefined()
  })

  it('round-trips resolved adaptive bindings without pinning inferred config or dropping a custom budget', () => {
    const resolver = modelBindingResolver()
    for (const raw of [
      imported({
        upstreamModel: 'claude-opus-5-5',
        thinkingConfig: {
          mode: 'budget', defaultEnabled: true, defaultBudgetTokens: 64_000,
          parameterPath: 'thinking.budget_tokens'
        }
      }),
      imported({ upstreamModel: 'gateway/claude-opus-6-1' }),
      imported({ upstreamModel: 'claude-newline-6' })
    ]) {
      const first = resolver.resolve(raw)
      expect(first.thinkingConfig).toMatchObject({ mode: 'effort', anthropicAdaptive: true })
      const saved = resolver.update(first, { ...first })
      expect(saved.thinkingConfig).toEqual(first.thinkingConfig)
      expect(saved.reasoningEfforts).toEqual(first.reasoningEfforts)
      expect(saved.catalogOverrides).not.toContain('thinkingConfig')
      const again = resolver.resolve(saved)
      expect(again.thinkingConfig).toEqual(first.thinkingConfig)
      expect(again.reasoningEfforts).toEqual(first.reasoningEfforts)
    }
    const custom = resolver.resolve(imported({
      upstreamModel: 'claude-haiku-5-5',
      thinkingConfig: {
        mode: 'budget', defaultEnabled: true, defaultBudgetTokens: 4_096,
        parameterPath: 'thinking.budget_tokens'
      }
    }))
    const customSaved = resolver.update(custom, { ...custom })
    expect(customSaved.thinkingConfig).toMatchObject({ mode: 'budget', defaultBudgetTokens: 4_096 })
    expect(customSaved.catalogOverrides).toContain('thinkingConfig')
    expect(resolver.resolve(customSaved).thinkingConfig).toMatchObject({ mode: 'budget', defaultBudgetTokens: 4_096 })
  })
  it('resolves a binding protocol override while keeping missing values inherited', () => {
    expect(effectiveModelProtocol({ protocol: 'openai-responses' }, imported())).toBe('openai-responses')
    expect(effectiveModelProtocol({ protocol: 'openai-responses' }, imported({ protocolOverride: 'anthropic' }))).toBe('anthropic')
    expect(effectiveModelProtocol({ protocol: 'openai-chat' }, imported({ protocolOverride: undefined }))).toBe('openai-chat')
  })

  it('clearing protocolOverride removes the persisted override during update', () => {
    const resolver = modelBindingResolver()
    const current = resolver.resolve(imported({ protocolOverride: 'anthropic' }))
    const cleared = resolver.update(current, { ...current, protocolOverride: undefined })
    expect(cleared.protocolOverride).toBeUndefined()
  })

  it('keeps the current override when an older metadata editor omits the field', () => {
    const resolver = modelBindingResolver()
    const current = resolver.resolve(imported({ protocolOverride: 'anthropic' }))
    const input = { ...current }
    delete (input as Partial<ModelAlias>).protocolOverride
    expect(resolver.update(current, input).protocolOverride).toBe('anthropic')
  })
  it('enriches old generic imports, matching casing and gateway prefixes without changing binding identity', () => {
    const raw = imported({ priority: 7, enabled: false })
    const model = modelBindingResolver().resolve(raw)
    expect(model).toMatchObject({
      alias: 'glm', upstreamModel: 'z-ai/GLM-5.3-Flash', providerId: 'relay', priority: 7, enabled: false,
      thinkingConfig: { mode: 'effort', defaultEffort: 'max' }, reasoningEfforts: ['low', 'high', 'max'],
      contextWindow: glm.contextWindow, maxOutputTokens: glm.maxOutputTokens, catalogOverrides: []
    })
    expect(model.capabilities).toEqual(glm.capabilities)
    expect(raw.thinkingConfig).toBeUndefined()
    expect(raw.capabilities.thinking).toBe(false)
  })

  /*
    ★★ **「选了图片模型却说没有工具」那次事故的前提就在这。**
    目录收录的图片模型,落库记录可能一行图片信息都没写,甚至带着
    `imageOutput: false` 的导入默认值(登录同步/老导入发生在目录收录之前):
    **原始记录**因此长得像文本模型,**解析后**才有 `modality: 'image'` /
    `imageOutput: true`。消费方是 `kernel/image-gen.ts` 的桥 —— 它判断「设置里
    点名的是不是图片模型」时必须吃 `listResolvedModels()`(runtime.ts 的装配点),
    吃 `store.listAliases()` 的症状是:设置页下拉里能选中(那边是解析后的)、
    保存成功,但对话里 `generate_image` 整体不下发,零报错。
  */
  it('目录才知道是图片的模型:原始记录没写模态(还带着 imageOutput:false 的导入默认值),解析后才是图片模型', () => {
    const raw = imported({ alias: 'gpt-image-2.5-sunburst', upstreamModel: 'gpt-image-2.5-sunburst' })
    expect(raw.modality).toBeUndefined()
    expect(raw.capabilities.imageOutput).toBe(false)
    const model = modelBindingResolver().resolve(raw)
    expect(model.modality).toBe('image')
    expect(model.capabilities.imageOutput).toBe(true)
  })

  it('keeps identifiable legacy provider customizations while filling other metadata', () => {
    // ★ 这里用 caching 而不是 tools 举例:`tools: false` 曾经是目录默认值,
    // 于是旧记录上的它**无法**和「从没设过」区分开(见下一条测试)。caching 没这个历史包袱。
    const model = modelBindingResolver().resolve(imported({
      contextWindow: 64_000, maxOutputTokens: 4_000,
      capabilities: { ...IMPORTED_ALIAS_DEFAULTS.capabilities, caching: false },
      thinkingConfig: { mode: 'toggle', defaultEnabled: false, parameterPath: 'enable_thinking' }
    }))
    expect(model).toMatchObject({ contextWindow: 64_000, maxOutputTokens: 4_000,
      capabilities: { caching: false, vision: true, thinking: true }, thinkingConfig: { mode: 'toggle' } })
    expect(model.reasoningEfforts).toBeUndefined()
    expect(model.catalogOverrides).toEqual(expect.arrayContaining(['contextWindow', 'maxOutputTokens', 'thinkingConfig']))
  })

  /*
    ★★ **升级路径,不是理论问题。**
    `tools` 的目录默认值从 false 翻成了 true。旧库里每一条别名都带着按旧默认抄下的
    `tools: false`,而推断「这是不是用户自定义」的判据是「和任何默认值都不同」——
    少登记一个历史默认值,这些旧记录就会被读成「用户特意关掉了工具」并**永久钉死**,
    症状是升级完 Agent 依然没有工具,且从界面上再也改不回来。
  */
  it('不把旧目录默认值 tools:false 当成用户显式关闭', () => {
    const legacy = imported({ capabilities: { ...IMPORTED_ALIAS_DEFAULTS.capabilities, tools: false } })
    // 旧记录的判据是「catalogOverrides 缺失」
    expect(legacy.catalogOverrides).toBeUndefined()
    const model = modelBindingResolver().resolve(legacy)
    expect(model.capabilities.tools).toBe(true)
    expect(model.catalogOverrides).not.toContain('capabilities.tools')
  })

  it('用户在界面上真的关掉工具时仍然生效', () => {
    const resolver = modelBindingResolver()
    const inherited = resolver.resolve(imported({ catalogOverrides: [] }))
    expect(inherited.capabilities.tools).toBe(true)
    const edited = resolver.update(inherited, {
      ...inherited, capabilities: { ...inherited.capabilities, tools: false }
    })
    expect(edited.capabilities.tools).toBe(false)
    expect(edited.catalogOverrides).toContain('capabilities.tools')
    // 再 resolve 一次不会被目录冲掉 —— 显式设置压过目录,这正是上一条不该误判的原因
    expect(resolver.resolve(edited).capabilities.tools).toBe(false)
  })

  it('pins only edited fields and keeps thinking configuration and accepted efforts together', () => {
    const resolver = modelBindingResolver()
    const inherited = resolver.resolve(imported({ catalogOverrides: [] }))
    const customized = resolver.update(inherited, { ...inherited, maxOutputTokens: 8192,
      thinkingConfig: { ...inherited.thinkingConfig!, defaultEffort: 'low' }, reasoningEfforts: ['low', 'high'] })
    const overlay: ModelCatalogDefinition = { ...glm, overrideBuiltin: true, contextWindow: 900_000,
      displayName: 'Updated model', thinkingConfig: { ...glm.thinkingConfig, defaultEffort: 'max' }, reasoningEfforts: ['max'] }
    const updated = modelBindingResolver([overlay]).resolve(customized)
    expect(updated).toMatchObject({ maxOutputTokens: 8192, contextWindow: 900_000, displayName: 'Updated model',
      thinkingConfig: { defaultEffort: 'low' }, reasoningEfforts: ['low', 'high'] })
    expect(updated.catalogOverrides).not.toContain('contextWindow')
    expect(resolver.update(inherited, { ...inherited, enabled: false }).catalogOverrides).toEqual([])
  })

  it('does not freeze a legacy catalogue copy when a built-in overlay exists', () => {
    const copied = { ...imported(), ...glm, alias: 'glm', upstreamModel: glm.id }
    const overlay = { ...glm, overrideBuiltin: true, thinkingConfig: { ...glm.thinkingConfig, defaultEffort: 'high' as const },
      reasoningEfforts: ['low', 'high'] as const }
    expect(modelBindingResolver([overlay]).resolve(copied)).toMatchObject({
      thinkingConfig: { defaultEffort: 'high' }, reasoningEfforts: ['low', 'high'], catalogOverrides: []
    })
  })

  it('leaves unknown models at fallback values until a definition is added', () => {
    const raw = imported({ upstreamModel: 'my-custom-model', catalogOverrides: [] })
    expect(modelBindingResolver().resolve(raw).thinkingConfig).toBeUndefined()
    expect(modelBindingResolver([{ ...glm, id: raw.upstreamModel }]).resolve(raw).reasoningEfforts)
      .toEqual(['low', 'high', 'max'])
  })

  it('prefers exact ids over gateway suffixes and refuses ambiguous alias matches', () => {
    const definitions = [{ ...glm, id: 'model', displayName: 'Base' },
      { ...glm, id: 'vendor/model', displayName: 'Vendor' }]
    expect(findCatalogModel(definitions, 'vendor/model')?.displayName).toBe('Vendor')
    expect(findCatalogModel(definitions, 'gateway/vendor/model')?.displayName).toBe('Vendor')
    expect(findCatalogModel(definitions.map((d) => ({ ...d, aliases: ['ambiguous'] })), 'ambiguous')).toBeUndefined()
  })
})

/* ================================================================
 * Ollama 系供应商的思考线形覆盖 —— `glm-5.3` 这类名字命中的是智谱官方条目,
 * 而它的方言字段(`thinking.type`)会被 Ollama 的兼容层静默丢弃,用户的
 * 「关」发不出去。覆盖成 Ollama 的线形(`reasoning_effort` + standardWire),
 * 并保证**别的供应商一个字节都不变**。
 * ================================================================ */
describe('Ollama 系绑定 · 思考线形的 provider 感知覆盖', () => {
  const ollamaStandard = OLLAMA_STANDARD_THINKING

  it('★★★ ollama-cloud 上的 glm-5.3 → 智谱方言被改写成 reasoning_effort 线形', () => {
    const model = modelBindingResolver().resolve(
      imported({ providerId: 'ollama-cloud', upstreamModel: 'glm-5.3' })
    )
    expect(model.thinkingConfig).toEqual(ollamaStandard)
    expect(model.reasoningEfforts).toEqual(['none', 'low', 'medium', 'high', 'max'])
    expect(model.capabilities.thinking).toBe(true)
  })

  it('★★ 本地 ollama 那条线同样覆盖(同一个 daemon 的同一套 API)', () => {
    const model = modelBindingResolver().resolve(
      imported({ providerId: 'ollama', upstreamModel: 'glm-5.3' })
    )
    expect(model.thinkingConfig).toEqual(ollamaStandard)
  })

  it('★★★ 智谱自己的供应商上不碰 —— 目录的 effort 档位线形原样保留(改写只属于 Ollama)', () => {
    const model = modelBindingResolver().resolve(
      imported({ providerId: 'zhipu', upstreamModel: 'glm-5.3' })
    )
    expect(model.thinkingConfig).toMatchObject({ mode: 'effort', parameterPath: 'reasoning_effort' })
    expect(model.thinkingConfig?.standardWire).toBeUndefined()
    expect(model.reasoningEfforts).toEqual(['low', 'high', 'max'])
  })

  it('★★ ollama 自己的条目不覆盖 —— gpt-oss 的档位表短一截(无 none/无 max)', () => {
    const model = modelBindingResolver().resolve(
      imported({ providerId: 'ollama-cloud', upstreamModel: 'gpt-oss:120b' })
    )
    expect(model.thinkingConfig).toEqual(ollamaStandard)
    expect(model.reasoningEfforts).toEqual(['low', 'medium', 'high'])
  })

  it('★★★ 非思考模型不长出思考开关(llama-3.3 在 Ollama 上也没有)', () => {
    const model = modelBindingResolver().resolve(
      imported({ providerId: 'ollama-cloud', upstreamModel: 'meta-llama/llama-3.3-70b-instruct' })
    )
    expect(model.thinkingConfig?.mode).toBe('unsupported')
    expect(model.capabilities.thinking).toBe(false)
  })

  it('★★★ 用户显式改过的思考配置不被覆盖', () => {
    const custom = { mode: 'budget' as const, defaultEnabled: true, defaultBudgetTokens: 4096 }
    const model = modelBindingResolver().resolve(
      imported({
        providerId: 'ollama-cloud',
        upstreamModel: 'glm-5.3',
        thinkingConfig: custom,
        catalogOverrides: ['thinkingConfig', 'reasoningEfforts']
      })
    )
    expect(model.thinkingConfig).toMatchObject({ mode: 'budget', defaultBudgetTokens: 4096 })
  })

  it('★★★ 往返稳定:把解析结果存回去再解析,覆盖仍在且没被记成用户自定义', () => {
    /*
     * 这条守的是「覆盖会不会被自己写的值反噬」:resolve 的输出若被存回
     * (update 的常见路径),下一次 resolve 必须得到同一个结果,且
     * catalogOverrides 里不能因此多出 thinkingConfig —— 多出来的话覆盖
     * 会被当成用户自定义而永久失效。
     */
    const resolver = modelBindingResolver()
    const raw = imported({ providerId: 'ollama-cloud', upstreamModel: 'glm-5.3' })
    const first = resolver.resolve(raw)
    const second = resolver.update(first, { ...first })
    expect(second.thinkingConfig).toEqual(ollamaStandard)
    expect(second.catalogOverrides).not.toContain('thinkingConfig')
    expect(second.reasoningEfforts).toEqual(['none', 'low', 'medium', 'high', 'max'])
  })
})
