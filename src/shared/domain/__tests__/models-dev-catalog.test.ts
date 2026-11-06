import { describe, expect, it } from 'vitest'
import { findBuiltinModel } from '../model-catalog-inventory'
import { isModelCatalogDefinition } from '../model-catalog'
import { parseModelsDevCatalog } from '../models-dev-catalog'

const model = (over: Record<string, unknown>): Record<string, unknown> => ({
  id: 'x',
  name: 'X',
  reasoning: false,
  tool_call: true,
  modalities: { input: ['text', 'image', 'pdf'], output: ['text'] },
  limit: { context: 123_456, output: 7_890 },
  ...over
})

const api = {
  google: {
    models: {
      'gemini-3.1-pro-preview': model({ id: 'gemini-3.1-pro-preview' }), // 内置已有
      'gemini-9-new': model({ id: 'gemini-9-new', name: 'Gemini 9 New', reasoning: true, reasoning_options: [{ type: 'effort', values: ['low', 'high', 'bogus'] }] }),
      'gemini-img': model({ id: 'gemini-img', modalities: { input: ['text'], output: ['image'] } }),
      'plain-new': model({ id: 'plain-new' })
    }
  },
  unknownvendor: { models: { 'u-1': model({ id: 'u-1' }) } }
}

describe('parseModelsDevCatalog', () => {
  const rows = parseModelsDevCatalog(api, '2026-10-14', [{ ...findBuiltinModel('gemini-3.1-pro-preview')!, id: 'PLAIN-NEW' }]).added

  it('跳过内置已有、非文本输出、用户目录已有、不在映射里的厂商', () => {
    expect(rows.map((r) => r.id)).toEqual(['gemini-9-new'])
  })

  it('字段映射与来源标记', () => {
    const r = rows[0]!
    expect(r).toMatchObject({
      manufacturerId: 'google',
      displayName: 'Gemini 9 New',
      modality: 'text',
      contextWindow: 123_456,
      maxOutputTokens: 7_890,
      verificationStatus: 'aggregator-reference',
      source: { url: 'https://models.dev/api.json#google/gemini-9-new', fetchedAt: '2026-10-14' }
    })
    expect(r.capabilities).toMatchObject({ tools: true, vision: true, fileInput: true, videoInput: false })
  })

  it('思考配置只从同厂商已核实内置行借,effort 只留已知档位', () => {
    const r = rows[0]!
    expect(r.thinkingConfig.mode).not.toBe('unsupported')
    expect(r.capabilities.thinking).toBe(true)
    expect(r.reasoningEfforts).toEqual(['low', 'high'])
  })

  it('不支持 reasoning 的模型按不支持思考处理', () => {
    const [r] = parseModelsDevCatalog({ google: { models: { a: model({ id: 'a-new' }) } } }, '2026-10-14').added
    expect(r?.thinkingConfig).toEqual({ mode: 'unsupported', defaultEnabled: false })
    expect(r?.capabilities.thinking).toBe(false)
  })
})

describe('parseModelsDevCatalog · 更新', () => {
  const builtin = findBuiltinModel('gemini-3.1-pro-preview')!

  it('已有内置模型数值不同 → 出现在 updates,incoming 只改差异字段并标覆盖层', () => {
    const j = { google: { models: { g: model({ id: 'gemini-3.1-pro-preview', limit: { context: 2_000_000, output: builtin.maxOutputTokens } }) } } }
    const { added, updates } = parseModelsDevCatalog(j, '2026-10-14')
    expect(added).toEqual([])
    const u = updates.find((x) => x.id === 'gemini-3.1-pro-preview')!
    expect(u.changes.find((c) => c.field === 'contextWindow')).toEqual({ field: 'contextWindow', from: builtin.contextWindow, to: 2_000_000 })
    expect(u.incoming).toMatchObject({
      contextWindow: 2_000_000,
      maxOutputTokens: builtin.maxOutputTokens,
      overrideBuiltin: true,
      verificationStatus: 'aggregator-reference',
      thinkingConfig: builtin.thinkingConfig
    })
  })

  it('完全一致不出现', () => {
    const j = { google: { models: { g: model({ id: 'x-same', limit: { context: 1, output: 1 } }) } } }
    const user = { ...builtin, id: 'x-same', contextWindow: 1, maxOutputTokens: 1, capabilities: { ...builtin.capabilities, visionInput: true, vision: true, fileInput: true, videoInput: false, audioInput: false } }
    expect(parseModelsDevCatalog(j, '2026-10-14', [user]).updates).toEqual([])
  })

  it('tools 不会被聚合源从 true 改成 false', () => {
    const j = { google: { models: { g: model({ id: builtin.id, tool_call: false, limit: { context: builtin.contextWindow, output: builtin.maxOutputTokens } }) } } }
    const u = parseModelsDevCatalog(j, '2026-10-14').updates.find((x) => x.id === builtin.id)
    expect(u?.changes.some((c) => c.field === 'tools') ?? false).toBe(false)
  })

  it('用户目录里的覆盖层优先作为当前值', () => {
    const overlay = { ...builtin, contextWindow: 5, overrideBuiltin: true }
    const j = { google: { models: { g: model({ id: builtin.id, limit: { context: 5, output: builtin.maxOutputTokens } }) } } }
    const u = parseModelsDevCatalog(j, '2026-10-14', [overlay]).updates.find((x) => x.id === builtin.id)
    expect(u?.changes.some((c) => c.field === 'contextWindow') ?? false).toBe(false)
  })
})

describe('parseModelsDevCatalog · 产出必须过目录校验', () => {
  it('档位与借来的默认档不一致时,默认档跟着落进列表(否则整批写入失败)', () => {
    const j = { google: { models: { a: model({ id: 'g-eff', reasoning: true, reasoning_options: [{ type: 'effort', values: ['minimal', 'high'] }] }) } } }
    const [r] = parseModelsDevCatalog(j, '2026-10-14').added
    expect(r && isModelCatalogDefinition(r)).toBe(true)
    expect(r?.reasoningEfforts).toContain(r?.thinkingConfig.defaultEffort)
  })

  it('能输出图像等非文本的模型不进文本目录', () => {
    const j = { google: { models: { a: model({ id: 'g-img', modalities: { input: ['text'], output: ['text', 'image'] } }) } } }
    expect(parseModelsDevCatalog(j, '2026-10-14').added).toEqual([])
  })

  it('输出上限大于上下文时取较小值', () => {
    const j = { google: { models: { a: model({ id: 'g-lim', limit: { context: 1000, output: 5000 } }) } } }
    const [r] = parseModelsDevCatalog(j, '2026-10-14').added
    expect(r?.maxOutputTokens).toBe(1000)
  })
})
