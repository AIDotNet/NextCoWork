import { describe, expect, it } from 'vitest'
import { isChatModelAlias, isImageModelAlias, type ModelAlias, type UpstreamProvider } from '../provider'
import {
  chatModelCorrection,
  firstChatModelAlias,
  modelBindingsFor,
  modelSelectionKey,
  parseModelSelectionKey,
  selectModelBinding,
  subagentModelSelection
} from '../model-selection'

function provider(id: string, priority: number, enabled = true): UpstreamProvider {
  return { id, name: id.toUpperCase(), protocol: 'openai-chat', baseUrl: 'https://local.invalid',
    credentialRef: '', priority, enabled }
}

function model(providerId: string, alias: string, enabled?: boolean): ModelAlias {
  return { alias, upstreamModel: alias, providerId, contextWindow: 100000, maxOutputTokens: 8192,
    capabilities: { tools: true, vision: true, thinking: true, caching: true },
    ...(enabled === undefined ? {} : { enabled }) }
}

// 复现用户报的那一幕:RoutinAI 的 priority 更小,Codex 更大,两家都有 gpt-5.6-sol。
const routin = provider('routin', 0)
const codex = provider('codex', 10)
const providers = [routin, codex]
const models = [model('routin', 'gpt-5.6-sol'), model('codex', 'gpt-5.6-sol')]

describe('模型绑定选择', () => {
  it('没指定供应商时按 priority 择优 —— 这是引入 providerId 之前的行为,必须逐字保留', () => {
    expect(selectModelBinding(models, providers, 'gpt-5.6-sol', undefined)?.providerId).toBe('routin')
  })

  it('指定了 priority 更大的那家时选中它,而不是被优先级推翻', () => {
    const picked = modelBindingsFor(models, providers, 'gpt-5.6-sol', 'codex')
    expect(picked.map((m) => m.providerId)).toEqual(['codex'])
  })

  it('指定的那家被停用时返回空,**不回退到同名的另一家**', () => {
    const disabled = [routin, { ...codex, enabled: false }]
    expect(modelBindingsFor(models, disabled, 'gpt-5.6-sol', 'codex')).toEqual([])
  })

  it('指定的那家不提供这个别名时返回空', () => {
    expect(modelBindingsFor(models, providers, 'gpt-5.6-sol', 'nobody')).toEqual([])
  })

  it('别名自身被禁用时不参与候选', () => {
    const off = [model('routin', 'gpt-5.6-sol', false), model('codex', 'gpt-5.6-sol')]
    expect(selectModelBinding(off, providers, 'gpt-5.6-sol', undefined)?.providerId).toBe('codex')
  })

  it('同 priority 时保留输入顺序(sort 稳定),避免和路由器的 tie-break 分家', () => {
    const tied = [provider('a', 5), provider('b', 5)]
    const rows = [model('b', 'x'), model('a', 'x')]
    expect(modelBindingsFor(rows, tied, 'x', undefined).map((m) => m.providerId)).toEqual(['b', 'a'])
  })
})

describe('下拉框复合键', () => {
  it('空别名对应空选项，审核模型和目标判定模型不会显示成空白', () => {
    expect(modelSelectionKey(undefined, '')).toBe('')
    expect(parseModelSelectionKey(modelSelectionKey(undefined, '')))
      .toEqual({ modelProviderId: undefined, alias: '' })
  })

  it('别名里的斜杠原样保留 —— 只切第一个分隔符', () => {
    const key = modelSelectionKey('openrouter', 'openrouter/claude-sonnet-4')
    expect(parseModelSelectionKey(key))
      .toEqual({ modelProviderId: 'openrouter', alias: 'openrouter/claude-sonnet-4' })
  })

  it('没指定供应商时往返一致', () => {
    expect(parseModelSelectionKey(modelSelectionKey(undefined, 'gpt-5.5')))
      .toEqual({ modelProviderId: undefined, alias: 'gpt-5.5' })
  })

  it('裸别名(旧的持久化值)按「没指定」解析,而不是崩掉', () => {
    expect(parseModelSelectionKey('gpt-5.5')).toEqual({ modelProviderId: undefined, alias: 'gpt-5.5' })
  })
})

describe('子代理选模型', () => {
  const parent = { model: 'gpt-5.6-sol', modelProviderId: 'codex' }
  const none = { model: '' }

  it('三档都空不下来时连供应商一起继承父亲', () => {
    expect(subagentModelSelection({}, none, parent))
      .toEqual({ model: 'gpt-5.6-sol', modelProviderId: 'codex' })
  })

  it('★ 只声明了别名时供应商必须是「没指定」,不能沿用父亲那家', () => {
    // 从 CC 粘过来的文件就是这个形状(只有 `model:`)。沿用父亲的锁会拼出
    // 「A 家的别名 + B 家的锁」,候选集为空,报错还指着一个跟这次调用无关的供应商。
    expect(subagentModelSelection({ model: 'claude-fable-5' }, none, parent))
      .toEqual({ model: 'claude-fable-5', modelProviderId: undefined })
  })

  it('子代理自己钉了供应商时那一对原样生效', () => {
    expect(subagentModelSelection({ model: 'claude-fable-5', modelProviderId: 'routin' }, none, parent))
      .toEqual({ model: 'claude-fable-5', modelProviderId: 'routin' })
  })

  it('父亲没锁时不凭空多出一个锁', () => {
    expect(subagentModelSelection({}, none, { model: 'gpt-5.5' }))
      .toEqual({ model: 'gpt-5.5', modelProviderId: undefined })
  })

  it('★★ 设置里配了「默认子代理」时它盖过父亲 —— 这一栏就是为了别跟着主力模型走', () => {
    expect(subagentModelSelection({}, { model: 'deepseek-v4-flash', modelProviderId: 'routin' }, parent))
      .toEqual({ model: 'deepseek-v4-flash', modelProviderId: 'routin' })
  })

  it('设置里的别名和供应商成对生效,不会拼出「配置的别名 + 父亲的锁」', () => {
    expect(subagentModelSelection({}, { model: 'deepseek-v4-flash' }, parent))
      .toEqual({ model: 'deepseek-v4-flash', modelProviderId: undefined })
  })

  it('子代理自己声明的别名盖过设置 —— 越具体的越优先', () => {
    expect(
      subagentModelSelection({ model: 'claude-fable-5' }, { model: 'deepseek-v4-flash', modelProviderId: 'routin' }, parent)
    ).toEqual({ model: 'claude-fable-5', modelProviderId: undefined })
  })

  it('设置里是空白串时当作没配,而不是拿一个空别名去发请求', () => {
    expect(subagentModelSelection({}, { model: '  ' }, parent))
      .toEqual({ model: 'gpt-5.6-sol', modelProviderId: 'codex' })
  })
})

// ── 对话模型 vs 图片模型:选择器过滤与「打开时校正」的判据 ──

const imageByModality: ModelAlias = { ...model('routin', 'gpt-image-2'), modality: 'image' }
const imageByCapability: ModelAlias = {
  ...model('routin', 'seedream-4'),
  capabilities: { tools: false, vision: true, thinking: false, caching: false, imageOutput: true }
}
const nonTextOutput: ModelAlias = {
  ...model('routin', 'wanx-video'),
  capabilities: { tools: false, vision: true, thinking: false, caching: false, textOutput: false }
}

describe('模态判据', () => {
  it('isImageModelAlias:modality 或 imageOutput 任一命中即图片模型(缺一边都会漏一族)', () => {
    expect(isImageModelAlias(imageByModality)).toBe(true)
    expect(isImageModelAlias(imageByCapability)).toBe(true)
    expect(isImageModelAlias(model('routin', 'gpt-5.6-sol'))).toBe(false)
  })

  it('isChatModelAlias:图片模型和显式不产文本的都不是对话模型', () => {
    expect(isChatModelAlias(model('routin', 'gpt-5.6-sol'))).toBe(true)
    expect(isChatModelAlias(imageByModality)).toBe(false)
    expect(isChatModelAlias(imageByCapability)).toBe(false)
    expect(isChatModelAlias(nonTextOutput)).toBe(false)
  })
})

describe('firstChatModelAlias 校正兜底', () => {
  it('沿输入顺序取第一个文本模型 —— 那正是选择器列表的显示顺序', () => {
    const rows = [imageByModality, model('codex', 'claude-fable-5'), model('routin', 'gpt-5.6-sol')]
    expect(firstChatModelAlias(rows, providers)?.alias).toBe('claude-fable-5')
  })

  it('停用的别名/供应商都不算 —— 落到一个发不出请求的模型比不校正更糟', () => {
    expect(firstChatModelAlias([model('routin', 'off', false), model('codex', 'ok')], providers)?.alias).toBe('ok')
    expect(firstChatModelAlias([model('gone', 'x'), model('codex', 'ok')], [routin, { ...codex, enabled: false }])).toBeNull()
  })

  it('全是图片模型时答 null —— 没有可落点,不瞎换', () => {
    expect(firstChatModelAlias([imageByModality, imageByCapability], providers)).toBeNull()
  })
})

describe('chatModelCorrection 打开选择器时的校正', () => {
  const rows = [imageByModality, model('codex', 'claude-fable-5'), model('routin', 'gpt-5.6-sol')]

  it('选中的是图片模型时换成第一个文本模型,别名与供应商成对给', () => {
    expect(chatModelCorrection(rows, providers, 'gpt-image-2', 'routin'))
      .toEqual({ alias: 'claude-fable-5', modelProviderId: 'codex' })
  })

  it('本来就是文本模型时不动', () => {
    expect(chatModelCorrection(rows, providers, 'gpt-5.6-sol', 'routin')).toBeNull()
  })

  it('空选中(跟随对话这类空档)与悬空别名都不动 —— 悬空是 repairModelSelection 的活', () => {
    expect(chatModelCorrection(rows, providers, '', undefined)).toBeNull()
    expect(chatModelCorrection(rows, providers, 'deleted-alias', 'routin')).toBeNull()
  })

  it('一个文本模型都没有时不动配置', () => {
    expect(chatModelCorrection([imageByModality], providers, 'gpt-image-2', 'routin')).toBeNull()
  })
})
