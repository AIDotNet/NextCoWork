import { describe, expect, it } from 'vitest'
import type { ModelAlias, UpstreamProvider } from '../../../../../../shared/domain/provider'
import {
  BUILTIN_PLAN_PROVIDER_ID,
  BUILTIN_PROVIDER_ID,
  findPreset
} from '../../../../../../shared/domain/presets'
import {
  avatarInitial,
  modelOptions,
  providerAliasOptions,
  providerEntries,
  roleModelChoice,
  selectableProviders
} from '../enabled-models'
import { parseModelSelectionKey } from '../../../../../../shared/domain/model-selection'

const prov = (id: string, name = id): UpstreamProvider => ({
  id,
  name,
  protocol: 'openai-chat',
  baseUrl: `https://${id}.example.com/v1`,
  credentialRef: `cred:${id}`,
  priority: 0,
  enabled: true
})

const alias = (a: string, providerId: string): ModelAlias => ({
  alias: a,
  providerId,
  upstreamModel: a,
  capabilities: { tools: true, vision: false, thinking: false, caching: false },
  contextWindow: 200_000,
  maxOutputTokens: 8192
})

describe('providerEntries', () => {
  it('每个供应商一行,顺序照 providers', () => {
    const e = providerEntries([prov('a'), prov('b')], [], '')
    expect(e.map((x) => x.provider.id)).toEqual(['a', 'b'])
  })

  it('别名按 providerId 归到各自那一行', () => {
    const e = providerEntries(
      [prov('a'), prov('b')],
      [alias('m1', 'a'), alias('m2', 'b'), alias('m3', 'a')],
      ''
    )
    expect(e[0]?.aliases.map((m) => m.alias)).toEqual(['m1', 'm3'])
    expect(e[1]?.aliases.map((m) => m.alias)).toEqual(['m2'])
  })

  it('副标题取第一个别名;一个都没有时是 null,不是空串', () => {
    const e = providerEntries([prov('a'), prov('b')], [alias('m1', 'a')], '')
    expect(e[0]?.primaryAlias).toBe('m1')
    expect(e[1]?.primaryAlias).toBeNull()
  })

  it('默认徽章落在提供该别名的那一家', () => {
    const e = providerEntries([prov('a'), prov('b')], [alias('m1', 'a'), alias('m2', 'b')], 'm2')
    expect(e.map((x) => x.isDefault)).toEqual([false, true])
  })

  /**
   * ★★ 这一条是我们和参考图的**结构差异**:故障切换轴就是「同一别名多个供应商」,
   * 所以能提供默认别名的可能不止一家。只给第一家挂徽章等于宣称另一家跟这次请求
   * 无关,而它随时会接手。
   */
  it('同一别名有多家提供、且没锁定供应商时,每一家都算默认', () => {
    const e = providerEntries([prov('a'), prov('b')], [alias('m1', 'a'), alias('m1', 'b')], 'm1')
    expect(e.map((x) => x.isDefault)).toEqual([true, true])
  })

  /** 锁定之后请求只会发给那一家,别家再挂徽章就是假话 */
  it('★ 锁定了供应商时只有那一家挂徽章', () => {
    const e = providerEntries([prov('a'), prov('b')], [alias('m1', 'a'), alias('m1', 'b')], 'm1', 'b')
    expect(e.map((x) => x.isDefault)).toEqual([false, true])
  })

  it('默认模型为空串(跟随对话)时没有任何一家是默认', () => {
    const e = providerEntries([prov('a')], [alias('m1', 'a')], '')
    expect(e.every((x) => !x.isDefault)).toBe(true)
  })

  it('默认模型指向一个已经不存在的别名时,不挂徽章也不报错', () => {
    const e = providerEntries([prov('a')], [alias('m1', 'a')], '已经删掉的模型')
    expect(e.every((x) => !x.isDefault)).toBe(true)
  })

  it('没有供应商时是空数组', () => {
    expect(providerEntries([], [], '')).toEqual([])
  })
})

/**
 * ★★ 内置那两条上游的「主模型」—— 副标题显示谁,由**预设表的 `suggestedModels` 顺序**
 * 决定(`main/runtime.ts` 的 seed 按下标算 `priority`,这里取 `aliases[0]`)。
 *
 * 单独钉一条,是因为这层耦合隔了两个模块、跨了主/渲染进程,而它断掉不会报错:
 * 有人「顺手」把订阅线的模型列表按字母排一下,左列副标题就从 `gpt-5.6-sol`
 * 变成 `gpt-5.3-codex-spark` —— 界面照常渲染,只是显示的不再是那条线该用的模型。
 */
describe('内置上游的主模型', () => {
  const primaryOf = (presetId: string): string | null => {
    const models = findPreset(presetId)?.suggestedModels ?? []
    const aliases = models.map((m) => alias(m, presetId))
    return providerEntries([prov(presetId)], aliases, '')[0]?.primaryAlias ?? null
  }

  it('按量线显示 deepseek-v4-pro,订阅线显示 gpt-5.6-sol', () => {
    expect(primaryOf(BUILTIN_PROVIDER_ID)).toBe('deepseek-v4-pro')
    expect(primaryOf(BUILTIN_PLAN_PROVIDER_ID)).toBe('gpt-5.6-sol')
  })

  /** 两条线各显示各的 —— 混在一起就说明 providerId 归属错了 */
  it('两条线的主模型不是同一个', () => {
    expect(primaryOf(BUILTIN_PROVIDER_ID)).not.toBe(primaryOf(BUILTIN_PLAN_PROVIDER_ID))
  })
})

/**
 * ★★ 下拉框的选项 —— **一条绑定一个,`value` 是 `providerId/alias`。**
 *
 * 别名的主键是 `(provider_id, alias)`,同一个别名可以挂在多家上(故障切换轴),
 * 而这几个设置项现在存的是**别名 + 供应商**一对 —— 「哪一家」正是用户要选的。
 *
 * ★ 曾经必须按别名去重,是因为那时 `value` 就是裸别名:两个 `value` 相同的
 * `Select.Item` 都认为自己被选中,各自把文本 portal 进触发器的 value 节点,
 * 那一格显示成 `gpt-6-astragpt-6-astra`(实测)。现在 `value` 天然唯一,
 * 重复项不存在了。下面第一条用例就是钉住这一点:**别把去重加回来**。
 */
describe('modelOptions', () => {
  it('★ 同一别名挂在两家上时,两条都出现 —— 「选哪一家」是用户的选项', () => {
    const options = modelOptions([alias('gpt-6-astra', 'a'), alias('gpt-6-astra', 'b')],
      [prov('a', 'Acme'), prov('b', 'Beta')])
    expect(options).toEqual([
      { value: 'a/gpt-6-astra', label: 'gpt-6-astra · Acme' },
      { value: 'b/gpt-6-astra', label: 'gpt-6-astra · Beta' }
    ])
  })

  it('value 唯一 —— 这正是去重曾经要解决的那个问题', () => {
    const options = modelOptions([alias('gpt-6-astra', 'a'), alias('gpt-6-astra', 'b')])
    expect(new Set(options.map((o) => o.value)).size).toBe(options.length)
  })

  it('只有一家提供时标签保持裸别名,不加供应商后缀制造噪音', () => {
    expect(modelOptions([alias('solo', 'a')], [prov('a', 'Acme')]))
      .toEqual([{ value: 'a/solo', label: 'solo' }])
  })

  it('不同别名一个都不少', () => {
    const options = modelOptions([alias('x', 'a'), alias('y', 'a'), alias('z', 'b')])
    expect(options.map((o) => o.value)).toEqual(['a/x', 'a/y', 'b/z'])
  })

  /** 上游已按「供应商顺序 → priority」排好,这里不重排 */
  it('保序', () => {
    const options = modelOptions([alias('b', 'p1'), alias('a', 'p1'), alias('b', 'p2')])
    expect(options.map((o) => o.value)).toEqual(['p1/b', 'p1/a', 'p2/b'])
  })

  it('别名里的斜杠不会把 value 切错', () => {
    const [option] = modelOptions([alias('openrouter/claude-sonnet-4', 'p1')])
    expect(parseModelSelectionKey(option!.value))
      .toEqual({ modelProviderId: 'p1', alias: 'openrouter/claude-sonnet-4' })
  })

  it('空列表返回空数组,不返回 undefined', () => {
    expect(modelOptions([])).toEqual([])
  })

  /** ★ 对话模型下拉只列文本模型 —— 图片模型选中了也发不出对话请求(见 modelOptions 里的注释) */
  it('图片模型不进对话模型下拉(modality 与 imageOutput 两种标法都滤掉)', () => {
    const imageByModality = { ...alias('gpt-image-2', 'a'), modality: 'image' as const }
    const imageByCapability = {
      ...alias('seedream-4', 'a'),
      capabilities: { ...alias('seedream-4', 'a').capabilities, imageOutput: true }
    }
    expect(modelOptions([alias('gpt-6-astra', 'a'), imageByModality, imageByCapability]).map((o) => o.value))
      .toEqual(['a/gpt-6-astra'])
  })
})

describe('avatarInitial', () => {
  it('取首字并大写', () => {
    expect(avatarInitial('routinAI')).toBe('R')
    expect(avatarInitial('智谱')).toBe('智')
  })

  it('忽略前导空格', () => {
    expect(avatarInitial('  NewMax')).toBe('N')
  })

  /** ★ `name[0]` 会在这里切出半个代理对,渲染成 ▯ */
  it('emoji 开头时取完整的那一个字符,不是半个代理对', () => {
    expect(avatarInitial('🚀 Gateway')).toBe('🚀')
  })

  it('空名字有兜底,不返回空串', () => {
    expect(avatarInitial('')).toBe('?')
    expect(avatarInitial('   ')).toBe('?')
  })
})

// ── 「默认模型」/「默认子代理」那两栏的两级下拉 ──

describe('两级下拉:先供应商,再它的模型', () => {
  const a = prov('a', 'A 家')
  const b = prov('b', 'B 家')
  const models = [alias('m1', 'a'), alias('m2', 'a'), alias('m1', 'b')]

  it('只列出挑得出模型的供应商 —— 空的那家点进去是死路', () => {
    expect(selectableProviders(models, [a, b, prov('c')], '').map((p) => p.id)).toEqual(['a', 'b'])
  })

  it('停用的供应商不进第一级', () => {
    expect(selectableProviders(models, [a, { ...b, enabled: false }], '').map((p) => p.id))
      .toEqual(['a'])
  })

  it('★ 已经存着的那一家即使不可选也留着 —— 抹掉它那一格会显示成「没配过」', () => {
    const gone = { ...b, enabled: false }
    expect(selectableProviders(models, [a, gone], 'b').map((p) => p.id)).toEqual(['a', 'b'])
  })

  it('第二级只给这一家的别名,且是裸别名', () => {
    expect(providerAliasOptions(models, 'a')).toEqual([
      { value: 'm1', label: 'm1' },
      { value: 'm2', label: 'm2' }
    ])
  })

  it('没选供应商时第二级为空', () => {
    expect(providerAliasOptions(models, '')).toEqual([])
  })

  it('停用的别名不进第二级 —— 和 modelBindingsFor 的过滤保持一致', () => {
    expect(providerAliasOptions([alias('m1', 'a'), { ...alias('m2', 'a'), enabled: false }], 'a'))
      .toEqual([{ value: 'm1', label: 'm1' }])
  })

  it('钉住了供应商时两级各显示各的', () => {
    expect(roleModelChoice(models, [a, b], 'm1', 'b')).toEqual({ providerId: 'b', alias: 'm1' })
  })

  it('空别名 = 跟随对话,两级都空', () => {
    expect(roleModelChoice(models, [a, b], '', undefined)).toEqual({ providerId: '', alias: '' })
  })

  it('★ 没钉供应商时显示解析出来的那一家 —— 那就是此刻真会收到请求的那家', () => {
    // a.priority 与 b 同分,靠 sort 的稳定性保留输入顺序 → 落在 a
    expect(roleModelChoice(models, [a, b], 'm1', undefined)).toEqual({ providerId: 'a', alias: 'm1' })
  })

  it('悬空的一对原样显示,不装作没配过', () => {
    expect(roleModelChoice(models, [a, b], 'ghost', 'b')).toEqual({ providerId: 'b', alias: 'ghost' })
  })
})
