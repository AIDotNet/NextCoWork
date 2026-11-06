import { describe, expect, it } from 'vitest'
import type { ModelAlias } from '../provider'
import { catalogDefinitionFromAlias, isModelCatalogDefinition, mergeModelCatalog, type ModelCatalogDefinition } from '../model-catalog'
import { BUILTIN_MODEL_CATALOG, findBuiltinModel, manufacturerForModelId, MODEL_MANUFACTURERS } from '../model-catalog-inventory'

const definition = (id: string, manufacturerId = 'openai'): ModelCatalogDefinition => ({
  id,
  manufacturerId,
  manufacturerLabel: manufacturerId,
  displayName: id,
  modality: 'text',
  capabilities: {
    tools: true,
    vision: false,
    thinking: false,
    caching: true,
    textInput: true,
    textOutput: true,
    streaming: true,
  },
  contextWindow: 128_000,
  maxOutputTokens: 16_384,
  thinkingConfig: { mode: 'unsupported', defaultEnabled: false },
})

const binding = (providerId: string, upstreamModel: string, enabled = true): ModelAlias => ({
  alias: `${providerId}-${upstreamModel}`,
  providerId,
  upstreamModel,
  capabilities: {
    tools: false,
    vision: true,
    thinking: true,
    caching: false,
  },
  contextWindow: 64_000,
  maxOutputTokens: 4_096,
  enabled,
})

describe('vendor-first model catalogue', () => {
  it('accepts adaptive effort only for effort-based declarations', () => {
    const row = findBuiltinModel('claude-opus-5-5')!
    expect(isModelCatalogDefinition(row)).toBe(true)
    expect(isModelCatalogDefinition({ ...row, thinkingConfig: {
      mode: 'budget', defaultEnabled: true, anthropicAdaptive: true
    } })).toBe(false)
    expect(isModelCatalogDefinition({ ...row, thinkingConfig: {
      ...row.thinkingConfig, anthropicAdaptive: 'true'
    } })).toBe(false)
  })
  it('rejects a default adaptive Off without confirmed Off support', () => {
    const row = findBuiltinModel('claude-opus-5-5')!
    const thinkingConfig = { ...row.thinkingConfig, defaultEnabled: false }
    expect(isModelCatalogDefinition({ ...row, thinkingConfig })).toBe(false)
    expect(isModelCatalogDefinition({ ...row, thinkingConfig, reasoningEfforts: undefined })).toBe(false)
    expect(isModelCatalogDefinition({ ...row, thinkingConfig,
      reasoningEfforts: ['none', 'low', 'medium', 'high', 'xhigh', 'max'] })).toBe(true)
    const offSupported = findBuiltinModel('claude-opus-5')!
    expect(isModelCatalogDefinition({ ...offSupported,
      thinkingConfig: { ...offSupported.thinkingConfig, defaultEnabled: false } })).toBe(true)
  })

  it('validates request adapters with the shared Patch security rules', () => {
    const row = definition('adapter-boundary')
    expect(
      isModelCatalogDefinition({
        ...row,
        requestAdapter: {
          preset: 'openai-responses',
          patches: [{ op: 'add', path: '/reasoning/effort', value: 'high' }],
        },
      }),
    ).toBe(true)

    for (const requestAdapter of [
      { preset: 'unknown', patches: [] },
      { preset: 'custom', patches: null },
      { preset: 'custom', patches: [], headers: { authorization: 'secret' } },
      {
        preset: 'custom',
        patches: [{ op: 'add', path: '/model', value: 'other' }],
      },
      { preset: 'custom', patches: [{ op: 'replace', path: '/temperature' }] },
      {
        preset: 'custom',
        patches: [{ op: 'add', path: '/metadata', value: { authorization: 'secret' } }],
      },
    ]) {
      expect(isModelCatalogDefinition({ ...row, requestAdapter }), JSON.stringify(requestAdapter)).toBe(false)
    }
  })

  it('validates optional catalogue metadata read directly by the renderer', () => {
    const row = definition('optional-boundary')
    expect(
      isModelCatalogDefinition({
        ...row,
        source: {
          url: 'https://official.example/models',
          fetchedAt: '2026-09-05',
          verifiedAt: '2026-09-06',
        },
        pricingModelId: 'official-price-id',
        overrideBuiltin: true,
      }),
    ).toBe(true)

    for (const invalid of [
      { source: null },
      { source: { url: 42, fetchedAt: '2026-09-05' } },
      { source: { url: 'https://official.example/models', fetchedAt: 42 } },
      {
        source: {
          url: 'https://official.example/models',
          fetchedAt: '2026-09-05',
          verifiedAt: 42,
        },
      },
      { pricingModelId: '' },
      { pricingModelId: 42 },
      { overrideBuiltin: 'true' },
    ]) {
      expect(isModelCatalogDefinition({ ...row, ...invalid }), JSON.stringify(invalid)).toBe(false)
    }
  })

  it('returns false instead of throwing for damaged enum-shaped IPC values', () => {
    const row = definition('damaged-enums')
    const damaged = [
      { thinkingConfig: { mode: Object.create(null), defaultEnabled: false } },
      { reasoningEfforts: [Object.create(null)] },
      { verificationStatus: Object.create(null) },
      {
        requestAdapter: {
          preset: Object.create(null),
          patches: [],
        },
      },
    ]

    for (const fields of damaged) {
      expect(() => isModelCatalogDefinition({ ...row, ...fields })).not.toThrow()
      expect(isModelCatalogDefinition({ ...row, ...fields })).toBe(false)
    }
  })

  it('rejects Think mode/path combinations that confuse toggles with token budgets', () => {
    const row = definition('invalid-thinking-path')
    for (const parameterPath of ['enable_thinking', 'thinking_mode', 'thinking.enabled', 'reasoning_split', 'reasoning_effort', 'reasoning.effort']) {
      expect(
        isModelCatalogDefinition({
          ...row,
          capabilities: { ...row.capabilities, thinking: true },
          thinkingConfig: {
            mode: 'budget',
            defaultEnabled: true,
            defaultBudgetTokens: 4_096,
            parameterPath,
          },
        }),
        parameterPath,
      ).toBe(false)
    }
    expect(
      isModelCatalogDefinition({
        ...row,
        capabilities: { ...row.capabilities, thinking: true },
        thinkingConfig: {
          mode: 'toggle',
          defaultEnabled: true,
          parameterPath: 'thinking_budget',
        },
      }),
    ).toBe(false)
    expect(
      isModelCatalogDefinition({
        ...row,
        capabilities: { ...row.capabilities, thinking: true },
        thinkingConfig: {
          mode: 'budget',
          defaultEnabled: true,
          defaultBudgetTokens: 4_096,
          parameterPath: 'thinking_budget',
        },
      }),
    ).toBe(true)
  })

  it('keeps every bundled canonical id unique and structurally complete', () => {
    const normalizedIds = BUILTIN_MODEL_CATALOG.map((row) => row.id.trim().toLowerCase())
    expect(new Set(normalizedIds).size).toBe(normalizedIds.length)
    expect(BUILTIN_MODEL_CATALOG.length).toBeGreaterThan(300)
    expect(BUILTIN_MODEL_CATALOG.every((row) => row.id.trim() !== '' && row.displayName.trim() !== '' && row.contextWindow > 0 && row.maxOutputTokens > 0 && row.source?.url.startsWith('https://') === true)).toBe(true)
  })

  it('keeps every manufacturer reference and canonical/alias ownership valid', () => {
    const manufacturerIds = new Set(MODEL_MANUFACTURERS.map((row) => row.id))
    expect(BUILTIN_MODEL_CATALOG.filter((row) => !manufacturerIds.has(row.manufacturerId)).map((row) => `${row.id}:${row.manufacturerId}`)).toEqual([])

    const ownerByName = new Map<string, string>()
    const conflicts: string[] = []
    for (const row of BUILTIN_MODEL_CATALOG) {
      for (const rawName of [row.id, ...(row.aliases ?? [])]) {
        const name = rawName.trim().toLowerCase()
        const previous = ownerByName.get(name)
        if (previous !== undefined && previous !== row.id) {
          conflicts.push(`${rawName}:${previous}:${row.id}`)
        } else {
          ownerByName.set(name, row.id)
        }
      }
    }
    expect(conflicts).toEqual([])
  })

  it('keeps token limits and Think declarations internally consistent', () => {
    expect(BUILTIN_MODEL_CATALOG.filter((row) => !Number.isInteger(row.contextWindow) || row.contextWindow <= 0 || !Number.isInteger(row.maxOutputTokens) || row.maxOutputTokens <= 0 || row.maxOutputTokens > row.contextWindow).map((row) => row.id)).toEqual([])

    expect(BUILTIN_MODEL_CATALOG.filter((row) => row.capabilities.thinking !== (row.thinkingConfig.mode !== 'unsupported')).map((row) => row.id)).toEqual([])

    expect(
      BUILTIN_MODEL_CATALOG.filter((row) => {
        if (row.thinkingConfig.mode !== 'effort') return false
        const values = row.reasoningEfforts ?? []
        return row.thinkingConfig.defaultEffort === undefined || values.length === 0 || new Set(values).size !== values.length || !values.includes(row.thinkingConfig.defaultEffort)
      }).map((row) => row.id),
    ).toEqual([])
  })

  it('never treats a switch or effort field as a token-budget path', () => {
    const forbidden = new Set(['enable_thinking', 'thinking_mode', 'thinking.enabled', 'reasoning_split', 'reasoning_effort', 'reasoning.effort'])
    expect(BUILTIN_MODEL_CATALOG.filter((row) => row.thinkingConfig.mode === 'budget' && forbidden.has(row.thinkingConfig.parameterPath ?? '')).map((row) => row.id)).toEqual([])
  })

  it('keeps conservative defaults and models Vision independently from File', () => {
    const conservative = BUILTIN_MODEL_CATALOG.find((row) => row.id === 'gpt-3.5-turbo')
    expect(conservative?.verificationStatus).toBe('unverified')
    // `tools` is deliberately absent here: it is the one capability that does
    // not follow the conservative default, because a false negative silently
    // strips the agent's whole tool schema rather than hiding a control.
    // See __tests__/model-catalog-tools.test.ts for that rule and its evidence.
    expect(conservative?.capabilities).toMatchObject({
      caching: false,
      structuredOutput: false,
      streaming: false,
    })

    const minimax = BUILTIN_MODEL_CATALOG.find((row) => row.id === 'MiniMax-M3')
    expect(minimax?.capabilities).toMatchObject({
      vision: true,
      visionInput: true,
      fileInput: false,
      videoInput: true,
    })

    const muse = BUILTIN_MODEL_CATALOG.find((row) => row.id === 'muse-spark-1.3')
    expect(muse?.capabilities).toMatchObject({
      vision: true,
      visionInput: true,
      fileInput: true,
      videoInput: true,
    })
  })

  it('ships the specifically requested MiMo, Muse/HY-Muse, MiniMax and Qwen families', () => {
    const ids = new Set(BUILTIN_MODEL_CATALOG.map((row) => row.id.toLowerCase()))
    expect(ids.has('mimo-v2.5-pro')).toBe(true)
    expect(ids.has('mimo-v2-pro')).toBe(true)
    expect(ids.has('mimo-v2-omni')).toBe(true)
    expect(ids.has('muse-spark-1.3')).toBe(true)
    expect(ids.has('muse-spark-1.2')).toBe(true)
    expect(ids.has('hunyuan-muse')).toBe(true)
    expect(ids.has('minimax-m3')).toBe(true)
    expect(ids.has('qwen3.8-max')).toBe(true)
    expect(ids.has('qwen3.5-plus')).toBe(true)
    const manufacturers = new Set(MODEL_MANUFACTURERS.map((row) => row.id))
    expect(['xiaomi', 'meta', 'hunyuan', 'minimax', 'qwen'].every((id) => manufacturers.has(id))).toBe(true)
    expect(BUILTIN_MODEL_CATALOG.filter((row) => row.id.startsWith('muse-')).every((row) => row.manufacturerId === 'meta')).toBe(true)
    expect(manufacturerForModelId('muse-spark-1.3').id).toBe('meta')
  })

  /*
   * 需求:2026-09-20 用户点名收录的微软 MAI-Code 1.1 Flash。钉住三点:
   * 目录行存在且未宣称已核实;`mai-` 前缀经启发式归入 microsoft 家族
   * (否则侧边栏落到「其他厂商」,表现是模型管理页分组错家,且零报错);
   * 定价走自身 id 的独立价目行(用户要求不依赖 gpt-5.6-luna,见 pricing-seed)。
   */
  it('ships the user-requested Microsoft MAI-Code 1.1 Flash as an unverified row', () => {
    expect(findBuiltinModel('mai-code-1.1-flash')).toMatchObject({
      manufacturerId: 'microsoft',
      displayName: 'MAI-Code 1.1 Flash',
      verificationStatus: 'unverified',
      pricingModelId: 'mai-code-1.1-flash',
    })
    expect(manufacturerForModelId('mai-code-1.1-flash').id).toBe('microsoft')
  })

  it('keeps the requested vendor Think adapters and evidence explicit', () => {
    const byId = new Map(BUILTIN_MODEL_CATALOG.map((row) => [row.id, row]))

    for (const id of ['qwen3-coder-next', 'qwen3-coder-plus', 'qwen3-coder-flash']) {
      expect(byId.get(id)).toMatchObject({
        capabilities: { thinking: true, tools: true },
        thinkingConfig: { mode: 'toggle', parameterPath: 'enable_thinking' },
        verificationStatus: 'official-api',
      })
    }
    expect(byId.get('qwen-max')?.thinkingConfig.mode).toBe('unsupported')
    expect(byId.get('qwq-32b')?.thinkingConfig).toEqual({
      mode: 'always',
      defaultEnabled: true,
    })
    expect(byId.get('qwen-math-plus')?.thinkingConfig.mode).toBe('unsupported')

    for (const id of ['hy4-preview', 'hy3']) {
      expect(byId.get(id)).toMatchObject({
        capabilities: {
          thinking: true,
          tools: true,
          structuredOutput: true,
          caching: true,
          visionInput: false,
          fileInput: false,
          videoInput: false,
        },
        reasoningEfforts: ['none', 'low', 'high'],
        thinkingConfig: {
          mode: 'effort',
          defaultEnabled: true,
          defaultEffort: 'high',
          parameterPath: 'reasoning_effort',
        },
        source: {
          url: 'https://cloud.tencent.com/document/product/1823/130051',
        },
        verificationStatus: 'official-api',
      })
    }
    expect(byId.get('hy4-preview')).toMatchObject({
      contextWindow: 1_048_576,
      maxOutputTokens: 65_536,
    })
    expect(byId.get('hy3')).toMatchObject({
      contextWindow: 262_144,
      maxOutputTokens: 131_072,
    })
    for (const id of ['hy-mt2-pro', 'hy-mt2-plus', 'hy-mt2-lite', 'hunyuan-role-latest', 'hy-role']) {
      expect(byId.get(id), id).toMatchObject({
        manufacturerId: 'hunyuan',
        source: {
          url: 'https://cloud.tencent.com/document/product/1823/130051',
        },
        verificationStatus: 'official-api',
      })
    }
    expect(byId.get('hunyuan-muse')?.verificationStatus).toBe('unverified')

    expect(byId.get('mimo-v2.5-pro')).toMatchObject({
      contextWindow: 1_000_000,
      thinkingConfig: {
        mode: 'toggle',
        parameterPath: 'thinking.type',
        enabledValue: 'enabled',
        disabledValue: 'disabled',
      },
      verificationStatus: 'official-api',
    })

    expect(byId.get('MiniMax-M3')).toMatchObject({
      capabilities: { visionInput: true, fileInput: false, videoInput: true },
      thinkingConfig: {
        mode: 'toggle',
        parameterPath: 'thinking',
        enabledValue: { type: 'adaptive' },
        disabledValue: { type: 'disabled' },
      },
      verificationStatus: 'official-api',
    })
    for (const id of ['MiniMax-M2.7', 'MiniMax-M2.5', 'MiniMax-M2.1', 'MiniMax-M2']) {
      expect(byId.get(id)?.thinkingConfig).toEqual({
        mode: 'always',
        defaultEnabled: true,
      })
    }

    expect(byId.get('muse-spark-1.3')?.source?.url).toBe('https://ai.developer.meta.com/docs/models/muse-spark-1.3')
    expect(byId.get('muse-spark-1.1')?.source?.url).toBe('https://developer.meta.com/ai/models/muse-spark-1-1/')
    expect(byId.get('muse-spark-1.2')?.source?.url).toBe('https://ai.developer.meta.com/docs/models/muse-spark-1.2')
    expect(byId.get('muse-spark-1.2')?.verificationStatus).toBe('official-model-card')
    expect(byId.get('muse-glimmer-30b')?.verificationStatus).toBe('unverified')
  })

  it('ships current StepFun models with verified multimodal and reasoning metadata', () => {
    const byId = new Map(BUILTIN_MODEL_CATALOG.map((row) => [row.id, row]))

    expect(byId.get('step-3.7-flash')).toMatchObject({
      manufacturerId: 'stepfun',
      contextWindow: 262_144,
      capabilities: {
        vision: true,
        visionInput: true,
        videoInput: true,
        tools: true,
        thinking: true,
      },
      thinkingConfig: {
        mode: 'effort',
        defaultEnabled: true,
        defaultEffort: 'medium',
        parameterPath: 'reasoning_effort',
      },
      reasoningEfforts: ['low', 'medium', 'high'],
      verificationStatus: 'official-api',
    })
    expect(byId.get('step-3.7-flash')?.source?.url).toBe('https://platform.stepfun.com/docs/zh/guides/models/step-3.7-flash.md')

    expect(byId.get('step-3.5-flash')).toMatchObject({
      contextWindow: 262_144,
      capabilities: { visionInput: false, tools: true, thinking: true },
      thinkingConfig: { mode: 'always', defaultEnabled: true },
      verificationStatus: 'official-api',
    })
    expect(byId.get('step-3.5-flash-2603')).toMatchObject({
      contextWindow: 262_144,
      capabilities: { visionInput: false, tools: true, thinking: true },
      thinkingConfig: {
        mode: 'effort',
        defaultEnabled: true,
        defaultEffort: 'high',
        parameterPath: 'reasoning_effort',
      },
      reasoningEfforts: ['low', 'high'],
      verificationStatus: 'official-api',
    })
    expect(byId.get('step-1o-turbo-vision')).toMatchObject({
      contextWindow: 32_768,
      capabilities: {
        vision: true,
        visionInput: true,
        videoInput: true,
        thinking: false,
      },
      thinkingConfig: { mode: 'unsupported', defaultEnabled: false },
      verificationStatus: 'official-api',
    })
  })

  it('ships every current Baichuan text Token SKU listed on the official price page', () => {
    const byId = new Map(BUILTIN_MODEL_CATALOG.map((row) => [row.id, row]))
    const current = ['Baichuan4', 'Baichuan4-Turbo', 'Baichuan4-Air', 'Baichuan3-Turbo', 'Baichuan3-Turbo-128k', 'Baichuan2-Turbo', 'Baichuan-M3-Plus', 'Baichuan-M3', 'Baichuan-M2-Plus', 'Baichuan-M2']

    for (const id of current) {
      expect(byId.get(id), id).toMatchObject({
        manufacturerId: 'baichuan',
        source: { url: 'https://platform.baichuan-ai.com/prices' },
        verificationStatus: 'official-api',
      })
    }
    expect(byId.get('Baichuan3-Turbo-128k')?.contextWindow).toBe(131_072)
    for (const id of current.filter((id) => id !== 'Baichuan3-Turbo-128k')) {
      expect(byId.get(id)?.contextWindow, id).toBe(32_768)
    }
  })

  it('ships current SenseNova Token models with conservative per-model capabilities', () => {
    const byId = new Map(BUILTIN_MODEL_CATALOG.map((row) => [row.id, row]))
    const priced = [
      'SenseNova-V6-5-Pro',
      'SenseNova-V6-5-Turbo',
      'SenseNova-V6-Pro',
      'SenseNova-V6-Turbo',
      'SenseNova-V6-Reasoner',
      'SenseChat-Vision',
      'SenseChat-Character-Pro',
      'SenseChat-Character',
    ]

    for (const id of priced) {
      expect(byId.get(id), id).toMatchObject({
        manufacturerId: 'sensenova',
        pricingModelId: id,
        verificationStatus: 'official-api',
      })
    }

    for (const id of ['SenseNova-V6-5-Pro', 'SenseNova-V6-5-Turbo']) {
      expect(byId.get(id), id).toMatchObject({
        contextWindow: 131_072,
        maxOutputTokens: 16_384,
        capabilities: {
          visionInput: true,
          fileInput: false,
          videoInput: true,
          thinking: true,
          streaming: true,
          tools: false,
          caching: false,
        },
        thinkingConfig: {
          mode: 'toggle',
          defaultEnabled: false,
          parameterPath: 'thinking.enabled',
        },
      })
    }
    expect(findBuiltinModel('SenseNova-V6.5-Pro')?.id).toBe('SenseNova-V6-5-Pro')
    expect(findBuiltinModel('SenseNova-V6.5-Turbo')?.id).toBe('SenseNova-V6-5-Turbo')

    expect(byId.get('SenseNova-V6-Pro')).toMatchObject({
      contextWindow: 32_768,
      capabilities: { visionInput: true, videoInput: false, thinking: false, streaming: true },
    })
    expect(byId.get('SenseNova-V6-Turbo')).toMatchObject({
      contextWindow: 32_768,
      capabilities: { visionInput: true, videoInput: true, thinking: false, streaming: true },
    })
    expect(byId.get('SenseNova-V6-Reasoner')).toMatchObject({
      contextWindow: 32_768,
      capabilities: { visionInput: true, videoInput: false, thinking: true, tools: false },
      thinkingConfig: { mode: 'always', defaultEnabled: true, parameterPath: 'thinking.enabled' },
    })
    expect(byId.get('SenseChat-Vision')).toMatchObject({
      contextWindow: 16_384,
      maxOutputTokens: 16_384,
      capabilities: { visionInput: true, videoInput: false, thinking: false, streaming: true },
    })
    expect(byId.get('SenseChat-Character-Pro')).toMatchObject({
      contextWindow: 32_768,
      maxOutputTokens: 4_096,
      capabilities: { visionInput: false, thinking: false, streaming: false },
    })
    expect(byId.get('SenseChat-Character')).toMatchObject({
      contextWindow: 8_192,
      maxOutputTokens: 1_024,
      capabilities: { visionInput: false, thinking: false, streaming: false },
    })
  })

  it('keeps secondary SenseChat catalogue entries separate from current main-page prices', () => {
    const byId = new Map(BUILTIN_MODEL_CATALOG.map((row) => [row.id, row]))

    for (const id of ['SenseChat-5', 'SenseChat', 'SenseChat-Turbo', 'SenseChat-5-Cantonese']) {
      expect(byId.get(id), id).toMatchObject({
        manufacturerId: 'sensenova',
        pricingModelId: id,
        source: { url: 'https://www.sensecore.cn/help/docs/model-as-a-service/nova/overview/compatible-mode' },
      })
    }
    expect(byId.get('SenseChat-FunctionCall')).toMatchObject({
      pricingModelId: 'SenseChat-FunctionCall',
      capabilities: { tools: true, streaming: true },
      source: { url: 'https://www.sensecore.cn/help/docs/model-as-a-service/nova/chat/ChatCompletions/FunctionCalling' },
    })
  })

  it('ships the current official InternLM hosted catalogue and aliases without inventing output limits', () => {
    const rows = BUILTIN_MODEL_CATALOG.filter(
      (row) =>
        row.manufacturerId === 'internlm' &&
        row.source?.url.includes('internlm.intern-ai.org.cn/doc/docs/'),
    )
    const byId = new Map(rows.map((row) => [row.id, row]))

    expect(rows.map((row) => row.id)).toEqual([
      'intern-s2-preview-397b',
      'intern-s2-preview-35b',
      'intern-s1-pro',
      'intern-s1',
      'intern-s1-mini',
      'internvl3.5-241b-a28b',
    ])
    for (const id of ['intern-s2-preview-397b', 'intern-s2-preview-35b', 'intern-s1-pro']) {
      expect(byId.get(id), id).toMatchObject({
        contextWindow: 262_144,
        capabilities: { visionInput: true, tools: true, thinking: true, streaming: true },
        thinkingConfig: {
          mode: 'toggle',
          defaultEnabled: true,
          parameterPath: 'thinking_mode',
          enabledValue: true,
          disabledValue: false,
        },
        verificationStatus: 'official-api',
      })
    }
    for (const id of ['intern-s1', 'intern-s1-mini']) {
      expect(byId.get(id), id).toMatchObject({
        contextWindow: 32_768,
        capabilities: { visionInput: true, tools: true, thinking: true, streaming: true },
        thinkingConfig: { mode: 'toggle', defaultEnabled: true, parameterPath: 'thinking_mode' },
      })
    }
    expect(byId.get('intern-s1-pro')?.capabilities.webSearch).toBe(true)
    expect(byId.get('internvl3.5-241b-a28b')).toMatchObject({
      contextWindow: 32_768,
      capabilities: { visionInput: true, tools: false, thinking: false, streaming: true, webSearch: false },
      thinkingConfig: { mode: 'unsupported', defaultEnabled: false },
    })
    expect(findBuiltinModel('intern-latest')?.id).toBe('intern-s2-preview-397b')
    expect(findBuiltinModel('intern-s2-preview')?.id).toBe('intern-s2-preview-35b')
    expect(findBuiltinModel('internvl3.5-latest')?.id).toBe('internvl3.5-241b-a28b')
    expect(findBuiltinModel('internvl-latest')?.id).toBe('internvl3.5-241b-a28b')
  })

  it('ships current Doubao endpoint IDs with official price aliases and Think metadata', () => {
    const byId = new Map(BUILTIN_MODEL_CATALOG.map((row) => [row.id, row]))
    const priced = [
      ['doubao-seed-evolving', 'doubao-seed-evolving'],
      ['doubao-seed-2-1-pro-260915', 'doubao-seed-2.1-pro'],
      ['doubao-seed-2-1-lite-260915', 'doubao-seed-2.1-lite'],
      ['doubao-seed-2-0-pro-260215', 'doubao-seed-2.0-pro'],
      ['doubao-seed-2-0-lite-260428', 'doubao-seed-2.0-lite'],
      ['doubao-seed-2-0-mini-260428', 'doubao-seed-2.0-mini'],
      ['doubao-seed-2-0-code-preview-260215', 'doubao-seed-2.0-code'],
      ['doubao-seed-character-260628', 'doubao-seed-character'],
      ['doubao-seed-code-preview-251028', 'doubao-seed-code'],
      ['doubao-seed-1-6-flash-250828', 'doubao-seed-1.6-flash'],
      ['doubao-seed-1-6-vision-250815', 'doubao-seed-1.6-vision'],
      ['doubao-1-5-pro-32k-250115', 'doubao-1.5-pro-32k'],
      ['doubao-1-5-lite-32k-250115', 'doubao-1.5-lite-32k'],
      ['doubao-1.5-vision-pro', 'doubao-1.5-vision-pro'],
      ['doubao-seed-translation-250915', 'doubao-seed-translation'],
    ] as const

    for (const [id, pricingModelId] of priced) {
      expect(byId.get(id), id).toMatchObject({
        manufacturerId: 'doubao',
        pricingModelId,
        verificationStatus: 'official-api',
      })
    }
    expect(byId.get('doubao-seed-2-1-pro-260915')).toMatchObject({
      contextWindow: 1_048_576,
      maxOutputTokens: 262_144,
      capabilities: {
        visionInput: true,
        tools: true,
        structuredOutput: true,
        thinking: true,
      },
      thinkingConfig: { mode: 'toggle', parameterPath: 'thinking.type' },
    })
    expect(findBuiltinModel('doubao-seed-2.1-pro')?.id).toBe('doubao-seed-2-1-pro-260915')
    expect(findBuiltinModel('doubao-seed-2.1-lite')?.id).toBe('doubao-seed-2-1-lite-260915')
    expect(findBuiltinModel('doubao-seed-2-0-lite-260215')?.id).toBe('doubao-seed-2-0-lite-260428')
    // ★ 260628 的两行按「退役旧行」删掉,但它们的**产品别名**必须留在新行上 ——
    // 别名是用户在药丸/默认模型里看见的字符串,漏登记等于老绑定错过目录元数据。
    for (const retired of ['doubao-seed-2-1-pro-260628', 'doubao-seed-2-1-turbo-260628']) {
      expect(byId.has(retired), retired).toBe(false)
    }
    // ★ Turbo 没有后继行,它的别名也就**不许**被顺手挂到 Lite 上 ——
    // 挂上去等于用 Lite 的窗口/价格替一款已退役的模型作答。
    expect(findBuiltinModel('doubao-seed-2.1-turbo')).toBeUndefined()
  })

  it('ships current ERNIE endpoints and never models ERNIE thinking as a Token budget', () => {
    const byId = new Map(BUILTIN_MODEL_CATALOG.map((row) => [row.id, row]))

    expect(byId.get('ernie-5.1')).toMatchObject({
      manufacturerId: 'baidu',
      contextWindow: 131_072,
      maxOutputTokens: 65_536,
      pricingModelId: 'ernie-5.1',
      verificationStatus: 'official-api',
    })
    for (const id of ['ernie-5.0', 'ernie-5.0-thinking-preview', 'ernie-5.0-thinking-latest', 'ernie-5.0-thinking-exp']) {
      expect(byId.get(id), id).toMatchObject({
        contextWindow: 131_072,
        maxOutputTokens: 65_536,
        capabilities: { visionInput: true, thinking: true },
        pricingModelId: 'ernie-5.0',
        verificationStatus: 'official-api',
      })
      expect(byId.get(id)?.thinkingConfig.mode, id).not.toBe('budget')
      expect(byId.get(id)?.thinkingConfig.parameterPath, id).toBe('enable_thinking')
    }
    expect(byId.get('ernie-4.5-turbo-128k')?.pricingModelId).toBe('ernie-4.5-turbo')
    expect(byId.get('ernie-4.5-turbo-vl')?.pricingModelId).toBe('ernie-4.5-turbo-vl')
    expect(byId.get('ernie-x1.1-preview')).toMatchObject({
      contextWindow: 65_536,
      maxOutputTokens: 65_536,
      thinkingConfig: { mode: 'always', parameterPath: 'enable_thinking' },
    })
    expect(findBuiltinModel('ernie-4.5-turbo')?.id).toBe('ernie-4.5-turbo-128k')
  })

  it('ships the current official Gemini text catalogue with per-model Think metadata', () => {
    const byId = new Map(BUILTIN_MODEL_CATALOG.map((row) => [row.id, row]))
    const current = [
      {
        id: 'gemini-3.8-flash',
        defaultEnabled: true,
        defaultEffort: 'medium',
        efforts: ['low', 'medium', 'high'],
      },
      {
        id: 'gemini-3.7-flash',
        defaultEnabled: true,
        defaultEffort: 'medium',
        efforts: ['low', 'medium', 'high'],
      },
      {
        id: 'gemini-3.6-flash',
        defaultEnabled: true,
        defaultEffort: 'medium',
        efforts: ['minimal', 'low', 'medium', 'high'],
      },
      {
        id: 'gemini-3.5-flash',
        defaultEnabled: true,
        defaultEffort: 'medium',
        efforts: ['minimal', 'low', 'medium', 'high'],
      },
      {
        id: 'gemini-3.5-flash-lite',
        defaultEnabled: true,
        defaultEffort: 'minimal',
        efforts: ['minimal', 'low', 'medium', 'high'],
      },
      {
        id: 'gemini-3.1-pro-preview',
        defaultEnabled: true,
        defaultEffort: 'high',
        efforts: ['low', 'medium', 'high'],
      },
      {
        id: 'gemini-3.1-flash-lite',
        defaultEnabled: true,
        defaultEffort: 'minimal',
        efforts: ['minimal', 'low', 'medium', 'high'],
      },
      {
        id: 'gemini-3-flash-preview',
        defaultEnabled: true,
        defaultEffort: 'high',
        efforts: ['minimal', 'low', 'medium', 'high'],
      },
      {
        id: 'gemini-2.5-pro',
        defaultEnabled: true,
        defaultEffort: 'medium',
        efforts: ['low', 'medium', 'high'],
      },
      {
        id: 'gemini-2.5-flash',
        defaultEnabled: true,
        defaultEffort: 'medium',
        efforts: ['none', 'low', 'medium', 'high'],
      },
      {
        id: 'gemini-2.5-flash-lite',
        defaultEnabled: false,
        defaultEffort: 'medium',
        efforts: ['none', 'low', 'medium', 'high'],
      },
    ] as const

    for (const expected of current) {
      expect(byId.get(expected.id), expected.id).toMatchObject({
        contextWindow: 1_048_576,
        maxOutputTokens: 65_536,
        capabilities: {
          visionInput: true,
          fileInput: true,
          videoInput: true,
          audioInput: true,
          tools: true,
          caching: true,
          webSearch: true,
          structuredOutput: true,
          streaming: true,
          batch: true,
        },
        thinkingConfig: {
          mode: 'effort',
          defaultEnabled: expected.defaultEnabled,
          defaultEffort: expected.defaultEffort,
          parameterPath: 'reasoning_effort',
        },
        reasoningEfforts: expected.efforts,
        source: {
          url: `https://ai.google.dev/gemini-api/docs/models/${expected.id}`,
          fetchedAt: '2026-09-05',
        },
        verificationStatus: 'official-api',
      })
    }

    for (const retiredOrInvalid of ['gemini-3.8-pro', 'gemini-2.0-flash', 'gemini-1.5-pro', 'gemini-1.5-flash']) {
      expect(byId.has(retiredOrInvalid), retiredOrInvalid).toBe(false)
    }
  })

  it('uses the official Gemini 3.1 Preview id while retaining the old shorthand as an alias', () => {
    const canonical = findBuiltinModel('gemini-3.1-pro-preview')
    expect(canonical).toMatchObject({
      id: 'gemini-3.1-pro-preview',
      pricingModelId: 'gemini-3.1-pro-preview',
      aliases: ['gemini-3.1-pro'],
      verificationStatus: 'official-api',
    })
    expect(findBuiltinModel('gemini-3.1-pro')).toBe(canonical)
  })

  it('uses the dated official Gemini Native Audio id and keeps the former shorthand as an alias', () => {
    const canonical = findBuiltinModel('gemini-2.5-flash-native-audio-preview-12-2025')
    expect(canonical).toMatchObject({
      id: 'gemini-2.5-flash-native-audio-preview-12-2025',
      modality: 'speech',
      contextWindow: 131_072,
      maxOutputTokens: 8_192,
      aliases: ['gemini-2.5-flash-native-audio'],
      capabilities: {
        audioInput: true,
        videoInput: true,
        audioOutput: true,
        textInput: true,
        textOutput: true,
        visionInput: false,
        tools: true,
        webSearch: true,
        streaming: true,
      },
      source: {
        url: 'https://ai.google.dev/gemini-api/docs/models/gemini-2.5-flash-native-audio-preview-12-2025',
        fetchedAt: '2026-09-05',
      },
      verificationStatus: 'official-api',
    })
    expect(findBuiltinModel('gemini-2.5-flash-native-audio')).toBe(canonical)
    expect(BUILTIN_MODEL_CATALOG.some((row) => row.id === 'gemini-2.5-flash-native-audio')).toBe(false)
  })

  it('uses the Google Gemma 3 model card as evidence for every bundled Gemma 3 size', () => {
    for (const id of ['gemma-3-27b-it', 'gemma-3-12b-it', 'gemma-3-4b-it']) {
      expect(findBuiltinModel(id), id).toMatchObject({
        source: {
          url: 'https://ai.google.dev/gemma/docs/core/model_card_3',
          fetchedAt: '2026-09-05',
        },
        verificationStatus: 'official-model-card',
      })
    }
  })

  it('includes GLM-5.3-Flash with its official multimodal and effort metadata', () => {
    expect(findBuiltinModel('glm-5.3-flash')).toMatchObject({
      manufacturerId: 'zhipu',
      contextWindow: 1_000_000,
      maxOutputTokens: 128_000,
      capabilities: {
        visionInput: true,
        fileInput: true,
        videoInput: true,
        tools: true,
        caching: true,
        structuredOutput: true,
        streaming: true,
      },
      thinkingConfig: {
        mode: 'effort',
        defaultEffort: 'max',
        parameterPath: 'reasoning_effort',
      },
      reasoningEfforts: ['low', 'high', 'max'],
      verificationStatus: 'official-api',
    })
  })

  /*
   * 2026-09-18 修正的三件事钉在这里:GLM-5.3 的 1M 窗口、与 flash 同表的
   * effort 档位(以前是 toggle,订阅端点选 5.3 没有强度滑杆),以及全部
   * Claude 行的 1M 窗口(以前 16 行全吃 200_000 兜底)。
   */
  it('exposes GLM-5.3 with a 1M window and the same effort ladder as Flash', () => {
    expect(findBuiltinModel('glm-5.3')).toMatchObject({
      manufacturerId: 'zhipu',
      contextWindow: 1_000_000,
      thinkingConfig: {
        mode: 'effort',
        defaultEffort: 'max',
        parameterPath: 'reasoning_effort',
      },
      reasoningEfforts: ['low', 'high', 'max'],
    })
  })

  it('gives every bundled Claude row an explicit 1M context window', () => {
    const claudeRows = BUILTIN_MODEL_CATALOG.filter((row) => row.manufacturerId === 'anthropic')
    expect(claudeRows.length).toBeGreaterThan(10)
    expect(claudeRows.filter((row) => row.contextWindow !== 1_000_000).map((row) => row.id)).toEqual([])
  })

  /*
   * Kimi Coding Plan(2026-09-18 修正):订阅端点的四个 Model ID 必须命中目录行 ——
   * 命不中就落 IMPORTED_ALIAS_DEFAULTS,表现是 200K 窗口 + 思考只剩「自动/关」;
   * 而那张端点表 supportsModelList 拉不到别名元数据,用户没有任何自救路径。
   */
  it('binds every Kimi Coding Plan model id to a catalogue row with a real window', () => {
    const expectWindow = (id: string, contextWindow: number): void => {
      const row = findBuiltinModel(id)
      expect(row, id).toBeDefined()
      expect(row?.contextWindow, id).toBe(contextWindow)
    }
    expectWindow('k3', 1_000_000)
    expectWindow('k3-256k', 256_000)
    expectWindow('kimi-for-coding', 1_000_000)
    expectWindow('kimi-for-coding-highspeed', 1_000_000)
  })

  it('exposes Kimi K3 with an effort ladder while the K2.7 Code rows stay toggles', () => {
    expect(findBuiltinModel('kimi-k3')).toMatchObject({
      contextWindow: 1_000_000,
      thinkingConfig: { mode: 'effort', defaultEffort: 'max', parameterPath: 'reasoning_effort' },
      reasoningEfforts: ['low', 'high', 'max'],
    })
    for (const id of ['kimi-k2.7-code', 'kimi-k2.7-code-highspeed']) {
      expect(findBuiltinModel(id), id).toMatchObject({
        contextWindow: 1_000_000,
        thinkingConfig: { mode: 'toggle' },
      })
    }
  })

  it('uses the current DeepSeek V4 API ids, limits, capabilities and Think controls', () => {
    const pro = findBuiltinModel('deepseek-v4-pro')
    const flash = findBuiltinModel('deepseek-v4-flash')
    const vision = findBuiltinModel('deepseek-v4-flash-vision-exp')

    for (const row of [pro, flash, vision]) {
      expect(row).toMatchObject({
        manufacturerId: 'deepseek',
        contextWindow: 1_000_000,
        maxOutputTokens: 384_000,
        capabilities: {
          thinking: true,
          tools: true,
          caching: true,
          structuredOutput: true,
          streaming: true,
        },
        thinkingConfig: {
          mode: 'effort',
          defaultEnabled: true,
          defaultEffort: 'high',
          parameterPath: 'reasoning_effort',
          // 这家的思考模式要求历史 reasoning_text 全文回传(见 reasoningReplayFor)
          reasoningReplay: 'text-required',
        },
        reasoningEfforts: ['none', 'low', 'high', 'max'],
        verificationStatus: 'official-api',
      })
      expect(row?.source?.url).toBe('https://api-docs.deepseek.com/quick_start/pricing/')
    }

    expect(pro?.capabilities.visionInput).toBe(false)
    expect(flash?.capabilities.visionInput).toBe(false)
    expect(vision?.capabilities).toMatchObject({
      visionInput: true,
      fileInput: true,
    })
    expect(findBuiltinModel('DeepSeek-V4-Pro-0813')).toBe(pro)
    expect(findBuiltinModel('DeepSeek-V4-Flash-0731')).toBe(flash)
  })

  it('includes the temporary DeepSeek V4.1 Flash preview with its V4 Flash runtime limits', () => {
    const preview = findBuiltinModel('deepseek-v4.1-flash-expires-on-0910')

    expect(preview).toMatchObject({
      id: 'deepseek-v4.1-flash-expires-on-0910',
      displayName: 'DeepSeek V4.1 Flash',
      manufacturerId: 'deepseek',
      contextWindow: 1_000_000,
      maxOutputTokens: 384_000,
      capabilities: {
        vision: true,
        visionInput: true,
        thinking: true,
        tools: true,
        caching: true,
        structuredOutput: true,
        streaming: true,
      },
      thinkingConfig: {
        mode: 'effort',
        defaultEnabled: true,
        defaultEffort: 'high',
        parameterPath: 'reasoning_effort',
      },
      reasoningEfforts: ['none', 'low', 'high', 'max'],
      pricingModelId: 'deepseek-v4-flash',
      aliases: ['deepseek-v4.1-flash', 'deepseek-v4.1-flash-beta'],
    })
    expect(findBuiltinModel('deepseek-v4.1-flash')).toBe(preview)
    expect(findBuiltinModel('deepseek-v4.1-flash-beta')).toBe(preview)
  })

  it('includes the current Spark X2 family and keeps legacy direct API ids separate', () => {
    const x2 = findBuiltinModel('spark-x2')
    const flash = findBuiltinModel('spark-x2-flash')

    expect(x2).toMatchObject({
      manufacturerId: 'spark',
      contextWindow: 192_000,
      maxOutputTokens: 131_072,
      capabilities: {
        thinking: true,
        tools: true,
        webSearch: true,
        streaming: true,
        visionInput: false,
      },
      thinkingConfig: {
        mode: 'toggle',
        defaultEnabled: true,
        parameterPath: 'thinking.type',
        enabledValue: 'enabled',
        disabledValue: 'disabled',
      },
      verificationStatus: 'official-api',
    })
    expect(flash).toMatchObject({
      manufacturerId: 'spark',
      contextWindow: 262_144,
      maxOutputTokens: 262_144,
      thinkingConfig: {
        mode: 'toggle',
        defaultEnabled: true,
        parameterPath: 'thinking.type',
      },
      verificationStatus: 'official-api',
    })

    expect(findBuiltinModel('spark-x2.5-4b')).toMatchObject({
      contextWindow: 1_000_000,
      capabilities: { thinking: false, tools: true },
      verificationStatus: 'official-model-card',
    })
    expect(findBuiltinModel('spark-x2.5-1.7b')).toMatchObject({
      contextWindow: 1_000_000,
      capabilities: { thinking: false, tools: true },
      verificationStatus: 'official-model-card',
    })

    expect(findBuiltinModel('4.0Ultra')).toMatchObject({
      id: 'spark-4.0-ultra',
      capabilities: { visionInput: false, tools: true, webSearch: true },
    })
    expect(findBuiltinModel('generalv3.5')?.displayName).toBe('讯飞星火 Max')
    expect(findBuiltinModel('lite')?.id).toBe('spark-lite')
    expect(findBuiltinModel('spark-x')).toBeUndefined()
  })

  it('includes Huawei openPangu 2.0 as its own model manufacturer', () => {
    for (const id of ['openpangu-2.0-pro', 'openpangu-2.0-flash']) {
      expect(findBuiltinModel(id), id).toMatchObject({
        id,
        manufacturerId: 'pangu',
        manufacturerLabel: '华为盘古 / openPangu',
        pricingModelId: id,
        contextWindow: 512_000,
        maxOutputTokens: 128_000,
        capabilities: {
          thinking: true,
          tools: true,
          caching: true,
          streaming: true,
          visionInput: false,
        },
        thinkingConfig: {
          mode: 'toggle',
          defaultEnabled: true,
          parameterPath: 'thinking.type',
          enabledValue: 'enabled',
          disabledValue: 'disabled',
        },
        source: {
          url: 'https://support.huaweicloud.com/model-list-maas/model_list_0001.html',
          fetchedAt: '2026-09-05',
        },
        verificationStatus: 'official-api',
      })
    }
    expect(manufacturerForModelId('huaweicloud/openpangu-2.0-pro').id).toBe('pangu')
  })

  it('includes the current OpenCode Go catalogue under each model manufacturer', () => {
    const expected = ['kimi-k2.5', 'qwen3.5-plus', 'mimo-v2-pro', 'mimo-v2-omni', 'omen-alpha', 'muse-spark-1.3-contributor', 'muse-spark-1.2-contributor']
    for (const id of expected) {
      expect(findBuiltinModel(id), id).toMatchObject({
        id,
        verificationStatus: 'aggregator-reference',
      })
    }

    expect(findBuiltinModel('omen-alpha')?.manufacturerId).toBe('other')
    expect(findBuiltinModel('muse-spark-1.3-contributor')?.manufacturerId).toBe('meta')
    expect(findBuiltinModel('muse-spark-1.2-contributor')?.manufacturerId).toBe('meta')

    expect(findBuiltinModel('mimo-v2-omni')?.capabilities).toMatchObject({
      visionInput: true,
      fileInput: true,
      audioInput: true,
      videoInput: false,
      tools: true,
    })
    expect(findBuiltinModel('omen-alpha')).toMatchObject({
      reasoningEfforts: ['low', 'high'],
      thinkingConfig: { mode: 'effort', defaultEffort: 'high' },
    })
  })

  it('uses the official LongCat id and request metadata while retaining the old lowercase alias', () => {
    const canonical = findBuiltinModel('LongCat-2.0')

    expect(canonical).toMatchObject({
      id: 'LongCat-2.0',
      manufacturerId: 'meituan',
      pricingModelId: 'LongCat-2.0',
      aliases: ['longcat-2.0'],
      contextWindow: 1_048_576,
      maxOutputTokens: 131_072,
      capabilities: {
        textInput: true,
        textOutput: true,
        visionInput: false,
        fileInput: false,
        videoInput: false,
        audioInput: false,
        tools: true,
        thinking: true,
        caching: true,
        streaming: true,
        structuredOutput: false,
        webSearch: false,
        batch: false,
      },
      thinkingConfig: {
        mode: 'toggle',
        defaultEnabled: false,
        parameterPath: 'thinking.type',
      },
      source: {
        url: 'https://longcat.chat/platform/docs/zh/api/model',
      },
      verificationStatus: 'official-api',
    })
    expect(findBuiltinModel('longcat-2.0')).toBe(canonical)
    expect(manufacturerForModelId('LongCat-2.0').id).toBe('meituan')
    expect(manufacturerForModelId('longcat-2.0').id).toBe('meituan')
  })

  it('uses current official OpenAI canonical ids and includes media families', () => {
    const ids = new Set(BUILTIN_MODEL_CATALOG.map((row) => row.id.toLowerCase()))
    expect(ids.has('gpt-6-astra')).toBe(true)
    expect(ids.has('gpt-5.6-astra')).toBe(false)
    expect(ids.has('gpt-5.6-cyber')).toBe(true)
    expect(ids.has('gpt-image-2')).toBe(true)
    expect(ids.has('sora-2')).toBe(true)
    expect(ids.has('gpt-realtime-2.1')).toBe(true)
    expect(ids.has('gpt-transcribe')).toBe(true)
  })

  it('keeps built-in rows when there are no providers configured', () => {
    const rows = mergeModelCatalog({
      builtin: [definition('mimo-v2.5-pro', 'xiaomi'), definition('muse-spark-1.3', 'meta')],
    })

    expect(rows.map((row) => row.id)).toEqual(['mimo-v2.5-pro', 'muse-spark-1.3'])
    expect(rows.every((row) => row.configured === false)).toBe(true)
    expect(rows.every((row) => row.providerIds.length === 0)).toBe(true)
  })

  it('does not make provider preset/discovered models appear as catalogue rows', () => {
    const rows = mergeModelCatalog({
      builtin: [definition('qwen3.8-max', 'qwen')],
      // A binding for a model that is not in the catalogue is intentionally
      // ignored. The user must explicitly add it as a custom catalogue row.
      bindings: [binding('openrouter', 'provider-only-model')],
    })

    expect(rows).toHaveLength(1)
    expect(rows[0]?.id).toBe('qwen3.8-max')
  })

  it('overlays all bindings without losing vendor metadata', () => {
    const rows = mergeModelCatalog({
      builtin: [definition('minimax-m3', 'minimax')],
      bindings: [binding('minimax', 'minimax-m3'), binding('openrouter', 'minimax-m3', false)],
    })
    const row = rows[0]

    expect(row?.manufacturerId).toBe('minimax')
    expect(row?.configured).toBe(true)
    expect(row?.providerIds).toEqual(['minimax', 'openrouter'])
    expect(row?.bindings).toHaveLength(2)
    expect(row?.enabled).toBe(true)
    expect(row?.capabilities.vision).toBe(false)
  })

  it('appends explicit user-created rows and lets built-in metadata win on duplicate ids', () => {
    const rows = mergeModelCatalog({
      builtin: [definition('qwen3.8-flash', 'qwen')],
      custom: [definition('custom-local-model', 'other'), definition('qwen3.8-flash', 'other')],
    })

    expect(rows.map((row) => row.id)).toEqual(['qwen3.8-flash', 'custom-local-model'])
    expect(rows[0]?.manufacturerId).toBe('qwen')
    expect(rows[1]?.custom).toBe(true)
  })

  it('applies only an explicitly marked user overlay to a bundled row', () => {
    const base = definition('MiniMax-M3', 'minimax')
    const rows = mergeModelCatalog({
      builtin: [base],
      custom: [
        {
          ...base,
          id: 'minimax-m3',
          displayName: '我的 MiniMax M3',
          overrideBuiltin: true,
          capabilities: { ...base.capabilities, thinking: true },
          thinkingConfig: {
            mode: 'effort',
            defaultEnabled: true,
            defaultEffort: 'high',
          },
        },
      ],
    })

    expect(rows).toHaveLength(1)
    expect(rows[0]?.builtin).toBe(true)
    expect(rows[0]?.overridden).toBe(true)
    expect(rows[0]?.displayName).toBe('我的 MiniMax M3')
    expect(rows[0]?.manufacturerId).toBe('minimax')
  })

  it('deep-clones definitions, overlays and bindings before exposing UI rows', () => {
    const base: ModelCatalogDefinition = {
      ...definition('safe-clone-model'),
      capabilities: {
        ...definition('safe-clone-model').capabilities,
        thinking: true,
      },
      thinkingConfig: {
        mode: 'effort',
        defaultEnabled: true,
        defaultEffort: 'high',
        parameterPath: 'reasoning_effort',
        effortMap: { high: { wire: ['high'] } },
      },
      reasoningEfforts: ['high'],
      aliases: ['safe-clone-alias'],
      requestAdapter: {
        preset: 'custom',
        patches: [{ op: 'add', path: '/metadata', value: { nested: ['builtin'] } }],
      },
      source: {
        url: 'https://official.example/models',
        fetchedAt: '2026-09-05',
      },
      verificationStatus: 'official-api',
    }
    const overlay: ModelCatalogDefinition = {
      ...base,
      displayName: 'User overlay',
      aliases: ['user-overlay-alias'],
      requestAdapter: {
        preset: 'custom',
        patches: [{ op: 'add', path: '/metadata', value: { nested: ['overlay'] } }],
      },
      source: {
        url: 'https://user.example/not-official',
        fetchedAt: '2026-09-06',
      },
      overrideBuiltin: true,
    }
    const providerBinding: ModelAlias = {
      ...binding('provider-a', base.id),
      thinkingConfig: overlay.thinkingConfig,
      reasoningEfforts: ['high'],
      requestAdapter: {
        preset: 'custom',
        patches: [{ op: 'add', path: '/metadata', value: { nested: ['binding'] } }],
      },
    }

    const row = mergeModelCatalog({
      builtin: [base],
      custom: [overlay],
      bindings: [providerBinding],
    })[0]!

    expect(row.source?.url).toBe('https://official.example/models')
    ;(row.aliases as string[]).push('mutated')
    ;(row.reasoningEfforts as string[])[0] = 'low'
    ;(row.thinkingConfig.effortMap?.high as { wire: string[] }).wire[0] = 'mutated'
    ;(row.requestAdapter?.patches[0]?.value as { nested: string[] }).nested[0] = 'mutated'
    row.source!.url = 'https://mutated.example'
    row.bindings[0]!.capabilities.tools = true
    ;(row.bindings[0]!.requestAdapter?.patches[0]?.value as { nested: string[] }).nested[0] = 'mutated'

    expect(base.aliases).toEqual(['safe-clone-alias'])
    expect(overlay.aliases).toEqual(['user-overlay-alias'])
    expect((overlay.thinkingConfig.effortMap?.high as { wire: string[] }).wire).toEqual(['high'])
    expect((overlay.requestAdapter?.patches[0]?.value as { nested: string[] }).nested).toEqual(['overlay'])
    expect(base.source?.url).toBe('https://official.example/models')
    expect(providerBinding.capabilities.tools).toBe(false)
    expect(
      (
        providerBinding.requestAdapter?.patches[0]?.value as {
          nested: string[]
        }
      ).nested,
    ).toEqual(['binding'])
  })

  it('matches a provider binding through a built-in alias case-insensitively', () => {
    const base = {
      ...definition('MiniMax-M3', 'minimax'),
      aliases: ['minimax-m3'],
    }
    const rows = mergeModelCatalog({
      builtin: [base],
      bindings: [binding('minimax', 'minimax-m3')],
    })

    expect(rows[0]?.configured).toBe(true)
    expect(rows[0]?.providerIds).toEqual(['minimax'])
  })

  it('converts an alias to a custom row only on explicit user action', () => {
    const row = catalogDefinitionFromAlias(binding('ollama', 'muse-spark-1.3'), {
      manufacturerId: 'meta',
      manufacturerLabel: 'Meta Muse',
    })

    expect(row.id).toBe('muse-spark-1.3')
    expect(row.manufacturerId).toBe('meta')
    expect(row.thinkingConfig.mode).toBe('toggle')
    expect(row.capabilities.vision).toBe(true)
  })
})

/* ================================================================
 * Ollama 家族 —— 条目只带 `:tag` 形态的名字,守的是「Think 开关在这家
 * 真的生效」:目录按名字匹配,`gpt-oss:120b`(冒号)匹配不上 openai 的
 * `gpt-oss-120b`(连字符),落空后用户切 Off 也什么都不发。
 * ================================================================ */
describe('Ollama 目录条目 · Think 的启用方式', () => {
  it('★★★ `gpt-oss:120b` 命中 Ollama 条目(effort 档),而不再落空', () => {
    const row = findBuiltinModel('gpt-oss:120b')
    expect(row).toBeDefined()
    expect(row?.manufacturerId).toBe('ollama')
    expect(row?.thinkingConfig.mode).toBe('effort')
    expect(row?.thinkingConfig.standardWire).toBe(true)
    /*
     * ★ 没有 `none`:官方文档明说 GPT-OSS「布尔被忽略、trace 关不干净」——
     * 给它一个关不掉的「关」是在骗用户。也没有 `max`(它只认 low/medium/high)。
     */
    expect(row?.reasoningEfforts).toEqual(['low', 'medium', 'high'])
  })

  it('★★ 冒号/连字符两个名字各归各家,互不抢占', () => {
    // openai 那条(连字符)不能被 ollama 条目顶掉 —— 两边语义确实不同
    expect(findBuiltinModel('gpt-oss-120b')?.manufacturerId).toBe('openai')
    expect(findBuiltinModel('gpt-oss:20b')?.manufacturerId).toBe('ollama')
  })

  it('★★★ 非 gpt-oss 的带 tag 模型给全套档位(官方文档:多数模型接受布尔或档位)', () => {
    for (const id of ['deepseek-v4-pro:0813', 'qwen3.5:397b']) {
      const row = findBuiltinModel(id)
      expect(row?.thinkingConfig.mode, id).toBe('effort')
      expect(row?.thinkingConfig.standardWire, id).toBe(true)
      expect(row?.reasoningEfforts, id).toEqual(['none', 'low', 'medium', 'high', 'max'])
    }
  })

  it('★ 不带 tag 的名字仍归各自厂商 —— ollama 不重复收录(同 id 双条目会让 exact 匹配静默拿到先注册的那条)', () => {
    expect(findBuiltinModel('kimi-k3')?.manufacturerId).toBe('moonshot')
    expect(findBuiltinModel('glm-5.3')?.manufacturerId).toBe('zhipu')
    // ollama 家族里没有这两个 id
    const ollamaIds = BUILTIN_MODEL_CATALOG.filter((row) => row.manufacturerId === 'ollama').map((row) => row.id)
    expect(ollamaIds).not.toContain('kimi-k3')
    expect(ollamaIds).not.toContain('glm-5.3')
  })

  it('★ ollama 家族已注册(否则侧边栏不显示,且条目校验会退到 other)', () => {
    expect(MODEL_MANUFACTURERS.some((m) => m.id === 'ollama')).toBe(true)
    /*
     * manufacturerForModelId 是按 id 前缀的启发式,`gpt-oss:120b` 以 `gpt-` 开头
     * 归 openai —— 这是**对的行为**(gpt-oss 本就是 OpenAI 的开源模型,Ollama 只是
     * 托管方)。权威的家族归属是目录条目自己的 manufacturerId(上面第一条已断言)。
     */
    expect(manufacturerForModelId('gpt-oss:120b').id).toBe('openai')
  })
})
