/**
 * NextCoWork 渠道的模型协议钉定。
 *
 * ★★ 登录这条路曾经**漏掉 anthropic 系**:`syncClientModels` 只按一张自己写的
 * 四家厂商表判(deepseek / 智谱 / 小米 / Qwen),claude 落进 `undefined`,别名因此
 * 不带 `protocolOverride`,在路由器里继承供应商的 Responses
 * (`effectiveModelProtocol`)—— 界面写着「跟随供应商」,Claude 的思考档位与 prompt
 * 缓存语义被兼容层丢掉,且登录全程零报错。
 *
 * 这个文件钉住三件事:新同步要钉、老库要补、用户自己选过的不许被顶掉。
 */
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { ModelAlias, UpstreamProvider } from '../../../shared/domain/provider'
import { IMPORTED_ALIAS_DEFAULTS } from '../../../shared/domain/provider'

const mocks = vi.hoisted(() => ({
  kv: new Map<string, unknown>(),
  aliases: [] as ModelAlias[],
  providers: [] as UpstreamProvider[],
  secretsGet: vi.fn<(ref: string) => Promise<string | null>>(),
  fetch: vi.fn(),
  emit: vi.fn(),
  /** 主进程那条「切账户作用域」的收尾,这里只关心它没抛。 */
  prepareAccountSwitch: vi.fn<(accountId: string | null) => Promise<void>>()
}))

vi.mock('electron', () => ({ shell: { openExternal: vi.fn() } }))
vi.mock('../../window/registry', () => ({ windows: { emitToAll: mocks.emit } }))
vi.mock('../config-sync', () => ({
  startConfigSync: vi.fn(),
  stopConfigSync: vi.fn(),
  shutdownConfigSync: vi.fn()
}))
vi.mock('../../account-switch', () => ({
  prepareAccountSwitch: mocks.prepareAccountSwitch,
  startSyncForAccount: vi.fn()
}))
vi.mock('../../runtime', () => ({
  getHost: () => ({
    secrets: {
      get: mocks.secretsGet,
      set: () => Promise.resolve(),
      remove: () => Promise.resolve(),
      available: () => true
    },
    fetch: mocks.fetch
  })
}))
vi.mock('../../state/store', () => ({ store: {
  getKv: (key: string, fallback: unknown) => mocks.kv.get(key) ?? fallback,
  setKv: (key: string, value: unknown) => { mocks.kv.set(key, value) },
  listAliases: () => mocks.aliases,
  putAlias: (alias: ModelAlias) => { mocks.aliases = [...mocks.aliases.filter(
    (a) => !(a.providerId === alias.providerId && a.alias === alias.alias)), alias]; return alias },
  removeAlias: (providerId: string, alias: string) => {
    mocks.aliases = mocks.aliases.filter((a) => !(a.providerId === providerId && a.alias === alias))
  },
  listProviders: () => mocks.providers,
  putProvider: (provider: UpstreamProvider) => { mocks.providers = [...mocks.providers, provider] },
  removeProvider: (id: string) => { mocks.providers = mocks.providers.filter((p) => p.id !== id) },
  listUserModelCatalog: () => []
} }))

import { getClientAuthState } from '../client-auth'

const META_KEY = 'client-auth.meta'
const CLIENT = 'nextcowork'

/** 平台 `/v1/models` 的响应形状 —— 只有 id 和能力位,元数据靠内置目录补。 */
function modelList(ids: readonly string[]) {
  return {
    ok: true,
    json: () => Promise.resolve({ data: ids.map((id) => ({ id, capabilities: ['tools', 'vision', 'thinking'] })) })
  }
}

function aliasOf(id: string): ModelAlias | undefined {
  return mocks.aliases.find((a) => a.providerId === CLIENT && a.upstreamModel === id)
}

/**
 * 读一次登录态,并等 `syncClientModels` 那条**故意不 await** 的支线跑完
 * (`getClientAuthState` 里它挂在 `prepareAccountSwitch().then` 之后,fire-and-forget)。
 * 不去等 `windows.emitToAll`:回填分支在没有目标时**不广播**,拿它当信号会漏掉
 * 「本来就该什么都不改」那条用例。
 */
async function readAuthState(): Promise<void> {
  getClientAuthState()
  await vi.waitFor(() => expect(mocks.prepareAccountSwitch).toHaveBeenCalled())
  await new Promise((resolve) => setTimeout(resolve, 0))
}

beforeEach(() => {
  mocks.kv.clear()
  mocks.kv.set(META_KEY, { mode: 'authenticated', user: { id: 'u1' }, expiresAt: Date.now() + 600_000 })
  mocks.aliases = []
  mocks.providers = [{ id: CLIENT, name: 'NextCoWork', protocol: 'openai-responses',
    baseUrl: 'https://nextco.work/v1', credentialRef: 'nextcowork:client-access-token',
    priority: 1, enabled: true }]
  mocks.secretsGet.mockResolvedValue('access')
  mocks.fetch.mockResolvedValue(modelList([]))
  mocks.emit.mockClear()
  mocks.prepareAccountSwitch.mockReset()
  mocks.prepareAccountSwitch.mockResolvedValue(undefined)
})

describe('NextCoWork 渠道的协议钉定', () => {
  it('1017 模型只有上游 id 时也能补齐视觉、工具能力、窗口与 Anthropic 协议', async () => {
    mocks.fetch.mockResolvedValue({
      ok: true,
      json: () => Promise.resolve({ data: [{ id: 'deepseek-v4.1-flash-1017' }] })
    })

    await readAuthState()

    expect(aliasOf('deepseek-v4.1-flash-1017')).toMatchObject({
      displayName: 'DeepSeek V4.1 Flash 1017',
      contextWindow: 1_000_000,
      maxOutputTokens: 384_000,
      capabilities: { visionInput: true, tools: true, thinking: true, caching: true },
      thinkingConfig: { mode: 'effort', reasoningReplay: 'text-required' },
      protocolOverride: 'anthropic',
    })
  })

  it('首次同步:claude 钉 anthropic,未登记的厂商跟随供应商', async () => {
    mocks.fetch.mockResolvedValue(modelList(['claude-fable-5-1', 'gpt-5.6-sol']))

    await readAuthState()

    expect(aliasOf('claude-fable-5-1')?.protocolOverride).toBe('anthropic')
    // ★ 反面:目录没登记厂商的模型不许被顺手钉死,「跟随供应商」是合理默认
    expect(aliasOf('gpt-5.6-sol')).not.toHaveProperty('protocolOverride')
  })

  it('老库回填:已经同步过的 claude 别名补上钉定,平台侧要求的四家仍在', async () => {
    mocks.aliases = [
      { ...IMPORTED_ALIAS_DEFAULTS, alias: 'claude-opus-5', upstreamModel: 'claude-opus-5', providerId: CLIENT, catalogOverrides: [] },
      { ...IMPORTED_ALIAS_DEFAULTS, alias: 'glm-5.3', upstreamModel: 'glm-5.3', providerId: CLIENT, catalogOverrides: [] }
    ]

    await readAuthState()

    expect(aliasOf('claude-opus-5')?.protocolOverride).toBe('anthropic')
    expect(aliasOf('glm-5.3')?.protocolOverride).toBe('anthropic')
    // 已经同步过的库不该再打一次 /v1/models —— 那是整表覆盖,用户删掉的模型会自己回来
    expect(mocks.fetch).not.toHaveBeenCalled()
  })

  it('用户在「协议」下拉里显式选过的不被回填顶掉', async () => {
    mocks.aliases = [
      { ...IMPORTED_ALIAS_DEFAULTS, alias: 'claude-opus-5', upstreamModel: 'claude-opus-5', providerId: CLIENT,
        catalogOverrides: [], protocolOverride: 'openai-chat' }
    ]

    await readAuthState()

    expect(aliasOf('claude-opus-5')?.protocolOverride).toBe('openai-chat')
  })
})
