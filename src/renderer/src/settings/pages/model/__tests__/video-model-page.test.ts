/** @vitest-environment jsdom */
import { act, createElement } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { IMPORTED_ALIAS_DEFAULTS, type ModelAlias, type UpstreamProvider } from '../../../../../../shared/domain/provider'
import { DEFAULT_SETTINGS, type AppSettings } from '../../../../../../shared/domain/settings'
import { I18nProvider, type Locale } from '../../../../i18n'
import { getCredentialInfo, removeProvider, setCredential, setProviderAliases, updateModel, upsertProvider } from '../../../../services/provider'
import { useModelsStore } from '../../../../stores/models'
import { VideoModelPage } from '../VideoModelPage'

vi.mock('../../../../services/provider', () => ({
  listModels: vi.fn(),
  listProviders: vi.fn(),
  getCredentialInfo: vi.fn(),
  removeProvider: vi.fn(),
  setCredential: vi.fn(),
  setProviderAliases: vi.fn(),
  updateModel: vi.fn(),
  upsertProvider: vi.fn()
}))

let root: Root | null = null
let container: HTMLDivElement
const credentials = new Map<string, string>()
const initialStore = useModelsStore.getState()
const scrollDescriptor = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'scrollIntoView')

const provider = (over: Partial<UpstreamProvider> = {}): UpstreamProvider => ({
  id: 'video-custom-relay', name: 'Relay', protocol: 'openai-chat',
  baseUrl: 'https://relay.example/chat/v1', credentialRef: 'provider:video-custom-relay',
  priority: 60, enabled: true,
  videoGeneration: { adapter: 'xai-video', baseUrl: 'https://relay.example/video/v1' },
  ...over
})

const model = (over: Partial<ModelAlias> = {}): ModelAlias => ({
  ...IMPORTED_ALIAS_DEFAULTS,
  alias: 'private-video', upstreamModel: 'private-video', providerId: 'video-custom-relay',
  modality: 'video', capabilities: { ...IMPORTED_ALIAS_DEFAULTS.capabilities, textOutput: false, videoOutput: true },
  video: { profileId: 'xai-video-1.5' },
  ...over
})

beforeEach(() => {
  vi.resetAllMocks()
  credentials.clear()
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  vi.stubGlobal('ResizeObserver', class {
    observe(): void {}
    unobserve(): void {}
    disconnect(): void {}
  })
  Object.defineProperty(HTMLElement.prototype, 'scrollIntoView', { configurable: true, value: vi.fn() })
  useModelsStore.setState({ providers: [], models: [], loaded: true, load: vi.fn(async () => {}) })
  vi.mocked(getCredentialInfo).mockImplementation(async (id) => ({
    hasKey: credentials.has(id), last4: credentials.get(id)?.slice(-4) ?? null, encryptionAvailable: true
  }))
  vi.mocked(setCredential).mockImplementation(async (id, key) => {
    credentials.set(id, key)
    return { hasKey: true, last4: key.slice(-4), encryptionAvailable: true }
  })
  vi.mocked(upsertProvider).mockImplementation(async (input) => {
    const current = useModelsStore.getState().providers
    useModelsStore.setState({ providers: [...current.filter((item) => item.id !== input.id), input] })
    return input
  })
  vi.mocked(setProviderAliases).mockImplementation(async (id, ids) => {
    const current = useModelsStore.getState().models
    const aliases = ids.map((upstreamModel) => current.find((item) => item.providerId === id && item.upstreamModel === upstreamModel) ?? {
      ...IMPORTED_ALIAS_DEFAULTS, alias: upstreamModel, upstreamModel, providerId: id
    })
    useModelsStore.setState({ models: [...current.filter((item) => item.providerId !== id), ...aliases] })
    return aliases
  })
  vi.mocked(updateModel).mockImplementation(async (input) => {
    const current = useModelsStore.getState().models
    useModelsStore.setState({ models: current.map((item) => item.providerId === input.providerId && item.alias === input.alias ? input : item) })
    return input
  })
  vi.mocked(removeProvider).mockImplementation(async (id) => {
    const state = useModelsStore.getState()
    useModelsStore.setState({ providers: state.providers.filter((item) => item.id !== id), models: state.models.filter((item) => item.providerId !== id) })
  })
})

afterEach(async () => {
  if (root !== null) await act(async () => root?.unmount())
  root = null
  document.body.replaceChildren()
  useModelsStore.setState(initialStore)
  if (scrollDescriptor === undefined) Reflect.deleteProperty(HTMLElement.prototype, 'scrollIntoView')
  else Object.defineProperty(HTMLElement.prototype, 'scrollIntoView', scrollDescriptor)
  vi.unstubAllGlobals()
})

async function mount(options: { providers?: UpstreamProvider[]; models?: ModelAlias[]; settings?: Partial<AppSettings>; locale?: Locale } = {}): Promise<ReturnType<typeof vi.fn>> {
  useModelsStore.setState({ providers: options.providers ?? [], models: options.models ?? [] })
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  const patch = vi.fn()
  await act(async () => root?.render(createElement(I18nProvider, {
    initialLocale: options.locale ?? 'en-US',
    children: createElement(VideoModelPage, { settings: { ...DEFAULT_SETTINGS, ...options.settings }, patch })
  })))
  return patch
}

/**
 * 按钮上**当前**的文字。Arc 的 Button 换文案时,旧文案标成 aria-hidden 播完退场才卸载,
 * jsdom 里动画不会结束,所以 `textContent` 会是新旧两段拼在一起。
 */
function visibleText(node: Node): string {
  if (node.nodeType === node.TEXT_NODE) return node.textContent ?? ''
  if (node instanceof Element && node.getAttribute('aria-hidden') === 'true') return ''
  return [...node.childNodes].map(visibleText).join('')
}

function button(label: string): HTMLButtonElement {
  const found = [...document.querySelectorAll<HTMLButtonElement>('button')].find((item) => item.getAttribute('aria-label') === label || visibleText(item).trim() === label)
  expect(found, `button: ${label}`).toBeDefined()
  return found!
}

async function click(label: string): Promise<void> {
  await act(async () => button(label).click())
}

async function input(label: string, value: string): Promise<void> {
  const field = [...container.querySelectorAll<HTMLInputElement>('input')].find((item) => item.getAttribute('aria-label') === label)
  expect(field, `input: ${label}`).toBeDefined()
  await act(async () => {
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(field, value)
    field!.dispatchEvent(new Event('input', { bubbles: true }))
  })
}

async function select(label: string, optionLabel: string): Promise<void> {
  const trigger = [...container.querySelectorAll<HTMLElement>('[role="combobox"]')].find((item) => item.getAttribute('aria-label') === label)
  expect(trigger, `select: ${label}`).toBeDefined()
  await act(async () => trigger!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true })))
  const option = [...document.querySelectorAll<HTMLElement>('[role="option"]')].find((item) => item.textContent?.trim() === optionLabel)
  expect(option, `option: ${optionLabel}`).toBeDefined()
  await act(async () => {
    option!.focus()
    option!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
  })
}

async function openCustom(): Promise<void> {
  await click('Add a video provider')
  await click('Custom video provider')
}

async function fill(modelId = 'my-private-model', name = 'Internal relay'): Promise<void> {
  await input('Provider name', name)
  await input('API address (custom service)', ' https://relay.example/video/v1/ ')
  await input('Model ID 1', modelId)
}

async function selectProvider(name: string): Promise<void> {
  const row = [...container.querySelectorAll<HTMLButtonElement>('ul button')].find((item) => item.textContent?.includes(name))
  expect(row, `provider: ${name}`).toBeDefined()
  await act(async () => row!.click())
}

describe('VideoModelPage · 自定义视频供应商', () => {
  it('创建多个自由模型，密钥单独保存，模型可在页脚选中', async () => {
    const patch = await mount()
    await openCustom()
    await fill()
    await input('API key', ' sk-test-video-key ')
    expect(container.querySelector<HTMLInputElement>('input[aria-label="API key"]')?.type).toBe('password')
    await click('Add model')
    await input('Model ID 2', 'classic-private-model')
    await select('Model parameter template 2', 'xAI Grok Imagine(经典型)')
    await click('Save')

    const saved = vi.mocked(upsertProvider).mock.calls[0]![0]
    expect(saved).toMatchObject({
      id: 'video-custom-internal-relay', name: 'Internal relay',
      videoGeneration: { adapter: 'xai-video', baseUrl: 'https://relay.example/video/v1' }
    })
    expect(JSON.stringify(saved)).not.toContain('sk-test-video-key')
    expect(setCredential).toHaveBeenCalledWith(saved.id, 'sk-test-video-key')
    expect(setProviderAliases).toHaveBeenCalledWith(saved.id, ['my-private-model', 'classic-private-model'])
    const aliases = useModelsStore.getState().models
    expect(aliases).toHaveLength(2)
    expect(aliases[0]).toMatchObject({ modality: 'video', video: { profileId: 'xai-video-1.5' }, capabilities: { videoOutput: true, textOutput: false, imageOutput: false } })
    expect(aliases[1]?.video?.profileId).toBe('xai-video-classic')
    expect(container.querySelector('input[type="password"]')).toBeNull()

    // 模型选择器是 Radix 的下拉菜单:触发器在 pointerdown 上开,先点供应商、再在子菜单里点模型
    await act(async () => {
      button('Model for video generation in chat').dispatchEvent(new MouseEvent('pointerdown', { bubbles: true, button: 0 }))
    })
    const providerItem = [...document.querySelectorAll<HTMLElement>('[role="menuitem"]')].find((item) => item.textContent?.includes('Internal relay'))!
    await act(async () => providerItem.click())
    const modelItem = [...document.querySelectorAll<HTMLElement>('[role="menuitem"]')].find((item) => item.title === 'my-private-model')!
    await act(async () => modelItem.click())
    expect(patch).toHaveBeenCalledWith({ videoModel: 'my-private-model', videoModelProviderId: saved.id })
  })

  it('中文重名创建不会覆盖已有供应商或凭据', async () => {
    const existing = provider({ id: 'video-custom-provider', name: '旧中转', credentialRef: 'provider:video-custom-provider' })
    await mount({ providers: [existing] })
    await openCustom()
    await fill('private-video', '公司中转')
    await click('Save')
    expect(vi.mocked(upsertProvider).mock.calls[0]?.[0].id).toBe('video-custom-provider-2')
    expect(useModelsStore.getState().providers.find((item) => item.id === existing.id)).toEqual(existing)
    expect(setCredential).not.toHaveBeenCalled()
  })

  it('编辑兼容接口不改自定义地址，保留其它模态、模型别名和未填写的密钥', async () => {
    const video = model({ alias: 'Friendly video' })
    const text = model({ alias: 'chat', upstreamModel: 'chat', modality: 'text', video: undefined, capabilities: { ...IMPORTED_ALIAS_DEFAULTS.capabilities } })
    const image = model({ alias: 'image', upstreamModel: 'image', modality: 'image', video: undefined, capabilities: { ...IMPORTED_ALIAS_DEFAULTS.capabilities, imageOutput: true, videoOutput: false } })
    credentials.set('video-custom-relay', 'existing-key')
    await mount({ providers: [provider()], models: [video, text, image] })
    await selectProvider('Relay')
    await click('Configure provider')
    await select('Compatible API', 'Google Veo / Gemini(原生)')
    expect(container.querySelector<HTMLInputElement>('input[aria-label="API address (custom service)"]')?.value).toBe('https://relay.example/video/v1')
    await input('API address (custom service)', 'https://relay.example/gemini/v1beta')
    await click('Add model')
    await input('Model ID 2', 'my-veo-fast')
    await select('Model parameter template 2', 'Google Veo 3.1 Fast')
    await click('Save')

    expect(vi.mocked(upsertProvider).mock.calls[0]?.[0]).toMatchObject({
      id: 'video-custom-relay', baseUrl: 'https://relay.example/chat/v1', credentialRef: 'provider:video-custom-relay',
      videoGeneration: { adapter: 'google-veo', baseUrl: 'https://relay.example/gemini/v1beta' }
    })
    expect(setCredential).not.toHaveBeenCalled()
    expect(setProviderAliases).toHaveBeenCalledWith('video-custom-relay', ['chat', 'image', 'private-video', 'my-veo-fast'])
    expect(useModelsStore.getState().models.find((item) => item.alias === 'chat')).toEqual(text)
    expect(useModelsStore.getState().models.find((item) => item.alias === 'image')).toEqual(image)
    expect(useModelsStore.getState().models.find((item) => item.alias === 'Friendly video')?.video?.profileId).toBe('google-veo-3.1')
    await click('Configure provider')
    expect(container.querySelector<HTMLInputElement>('input[aria-label="API address (custom service)"]')?.value).toBe('https://relay.example/gemini/v1beta')
    expect(container.querySelector<HTMLInputElement>('input[aria-label="Model ID 2"]')?.value).toBe('my-veo-fast')
    expect(container.textContent).toContain('Leave blank to keep the saved key')
  })

  it('fal 兼容服务可给模型配置独立 endpoint', async () => {
    await mount()
    await openCustom()
    await fill('kling-on-relay')
    await select('Compatible API', 'fal.ai')
    await input('Upstream endpoint (optional) 1', 'fal-ai/kling-video/v2/master/text-to-video')
    await click('Save')
    expect(useModelsStore.getState().models[0]?.video).toEqual({ profileId: 'fal-queue-endpoint', endpointId: 'fal-ai/kling-video/v2/master/text-to-video' })
  })

  it('坏地址、空模型和重复模型在提交前拦下', async () => {
    await mount()
    await openCustom()
    await input('Provider name', 'Relay')
    await input('API address (custom service)', 'file:///etc/passwd')
    await click('Save')
    expect(container.querySelector('[role="alert"]')?.textContent).toContain('valid http or https URL')
    await input('API address (custom service)', 'https://relay.example/v1')
    await click('Save')
    expect(container.querySelector('[role="alert"]')?.textContent).toContain('at least one video model ID')
    await input('Model ID 1', 'same-model')
    await click('Add model')
    await input('Model ID 2', ' same-model ')
    await click('Save')
    expect(container.querySelector('[role="alert"]')?.textContent).toContain('already been added')
    expect(upsertProvider).not.toHaveBeenCalled()
  })

  it('不匹配的导入档案不可选，修正后才保存', async () => {
    await mount({ providers: [provider()], models: [model({ video: { profileId: 'google-veo-3.1' } })], settings: { videoModel: 'private-video', videoModelProviderId: 'video-custom-relay' } })
    expect(container.textContent).toContain('The selected model is unavailable')
    await selectProvider('Relay')
    await click('Configure provider')
    await click('Save')
    expect(container.querySelector('[role="alert"]')?.textContent).toContain('matching the compatible API')
    expect(upsertProvider).not.toHaveBeenCalled()
    await select('Model parameter template 1', 'xAI Grok Imagine 1.5')
    await click('Save')
    expect(useModelsStore.getState().models[0]?.video?.profileId).toBe('xai-video-1.5')
  })

  it('部分模型写入失败时，重试沿用同一供应商且不会把临时别名误认成其它模态', async () => {
    await mount()
    await openCustom()
    await fill()
    await input('API key', 'sk-test-video-key')
    vi.mocked(updateModel).mockRejectedValueOnce(new Error('model write rejected'))
    await click('Save')
    expect(container.querySelector('[role="alert"]')?.textContent).toBe('model write rejected')
    expect(container.querySelector<HTMLInputElement>('input[aria-label="API key"]')?.value).toBe('')
    const id = vi.mocked(upsertProvider).mock.calls[0]![0].id
    await click('Save')
    expect(vi.mocked(upsertProvider).mock.calls[1]?.[0].id).toBe(id)
    expect(useModelsStore.getState().providers).toHaveLength(1)
    expect(useModelsStore.getState().models[0]?.video?.profileId).toBe('xai-video-1.5')
    expect(setCredential).toHaveBeenCalledTimes(1)
  })

  it('快速重复保存只提交一次，写入期间不能切换供应商', async () => {
    await mount()
    await openCustom()
    await fill()
    const original = vi.mocked(upsertProvider).getMockImplementation()!
    let release!: () => void
    const gate = new Promise<void>((resolve) => { release = resolve })
    vi.mocked(upsertProvider).mockImplementationOnce(async (value) => { await gate; return original(value) })
    await act(async () => { button('Save').click(); button('Save').click() })
    expect(upsertProvider).toHaveBeenCalledTimes(1)
    expect(button('Add a video provider').disabled).toBe(true)
    expect(button('Close').disabled).toBe(true)
    await act(async () => release())
    expect(updateModel).toHaveBeenCalledTimes(1)
  })

  it('删除自定义供应商需要两次有意点击', async () => {
    await mount({ providers: [provider()], models: [model()] })
    await selectProvider('Relay')
    await click('Delete')
    expect(removeProvider).not.toHaveBeenCalled()
    await click('Confirm delete')
    expect(removeProvider).toHaveBeenCalledWith('video-custom-relay')
    expect(useModelsStore.getState().providers).toEqual([])
  })

  it('中文界面提供自定义入口与接口选择', async () => {
    await mount({ locale: 'zh-CN' })
    await click('添加视频供应商')
    await click('自定义视频供应商')
    expect(container.textContent).toContain('兼容接口')
    expect(container.textContent).toContain('模型参数模板')
  })
})
