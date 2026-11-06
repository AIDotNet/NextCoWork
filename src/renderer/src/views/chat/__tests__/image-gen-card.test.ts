/*
 * @vitest-environment jsdom
 * ★ 必须在模块加载时就有 document:Radix 的 useLayoutEffect 在 import 那一刻判断
 *   `globalThis.document` 是否存在,不存在就换成空函数 —— 之后再 stubGlobal 也晚了,
 *   弹窗的 Portal 永远挂不上;Motion 也要全局的 getComputedStyle。
 */
/**
 * 生图卡片:格子怎么摆、卡片摆在哪。
 *
 * 这一组守的坏全都**不报错**:
 * - 生成期占位数不跟 `n` 走 / 到手的图挤错格 —— 并发完成顺序不定,只在多图时现形;
 * - 跑完后给失败格补一格永远转圈的占位 —— 看起来像「还在画」;
 * - 生图卡片进了过程段 —— 一轮收尾折进「用时」之后,图被藏在折叠底下,
 *   而模型刚说完「画好了」。
 */
import { act, createElement } from 'react'
import { createRoot } from 'react-dom/client'
import { JSDOM } from 'jsdom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { assistantMessage, toolResultMessage, userMessage, type ToolOutputImage } from '../../../../../shared/agent/message'
import { emptyTranscript, toolsFromMessages, type ToolCallState } from '../../../../../shared/agent/transcript'
import { I18nProvider } from '../../../i18n'
import { copyImage, saveImageFile } from '../../../services/app'
import { imageGenView } from '../image-gen-view'
import { ToolCallCard } from '../parts'
import { Thread } from '../Thread'
import { assistantSegments, isPinnedToolBlock, type AssistantBlock } from '../thread-content'

/** 复制 / 下载那两颗按钮的落地通道:断言的是**送给它们的是什么**(见下面那组用例) */
vi.mock('../../../services/app', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../services/app')>()),
  copyImage: vi.fn(async () => {}),
  saveImageFile: vi.fn(async () => ({ path: '/tmp/image-1.png' }))
}))

const img = (tag: string): ToolOutputImage => ({ mime: 'image/png', dataRef: `data:image/png;base64,${tag}` })

describe('imageGenView · 这一刻摆什么', () => {
  it('参数还在流、n 没写到:先摆 1 格占位;写到了长成 n 格', () => {
    expect(imageGenView({ prompt: 'a cat' }, undefined, undefined).slots).toEqual([{ kind: 'loading', index: 0 }])
    expect(imageGenView({ prompt: 'a cat', n: 3 }, undefined, undefined).slots.map((s) => s.kind))
      .toEqual(['loading', 'loading', 'loading'])
    // 流式中途入参可能还是原始 JSON 前缀字符串 —— 不崩,按 1 张算
    expect(imageGenView('{"prompt":"a c', undefined, undefined).requested).toBe(1)
  })

  it('★ 运行中:到手的图按**格子序号**落位,不按到达顺序往前挤', () => {
    const view = imageGenView({ prompt: 'x', n: 3 }, undefined, { 2: img('CC') })
    expect(view.slots).toEqual([
      { kind: 'loading', index: 0 },
      { kind: 'loading', index: 1 },
      { kind: 'image', index: 2, image: img('CC') }
    ])
    expect(view.done).toBe(1)
    expect(view.partial).toBe(false)
  })

  it('跑完以 output.images 为准:失败格不留占位,只标 partial', () => {
    const view = imageGenView({ prompt: 'x', n: 4 }, { content: 'ok', images: [img('AA'), img('BB'), img('CC')] }, { 0: img('AA') })
    expect(view.slots.every((s) => s.kind === 'image')).toBe(true)
    expect(view.slots).toHaveLength(3)
    expect(view.partial).toBe(true)
    expect(view.done).toBe(3)
  })

  it('带了源图就是改图', () => {
    expect(imageGenView({ prompt: 'x', image: 'latest' }, undefined, undefined).mode).toBe('edit')
    expect(imageGenView({ prompt: 'x' }, undefined, undefined).mode).toBe('generate')
  })
})

describe('assistantSegments · 生图卡片不进过程段', () => {
  const block = (key: string, part: AssistantBlock['part']): AssistantBlock => ({ key, part, streaming: false, cursor: false })

  it('生图调用单独成块,把前后的普通工具切成两段过程', () => {
    const segments = assistantSegments([
      block('b0', { type: 'tool_call', callId: 'read', name: 'Read', input: {} }),
      block('b1', { type: 'tool_call', callId: 'gen', name: 'generate_image', input: { prompt: 'x' } }),
      block('b2', { type: 'tool_call', callId: 'grep', name: 'Grep', input: {} })
    ], 'tool')
    expect(segments.map((s) => s.kind)).toEqual(['process', 'block', 'process'])
    // ★ key 用 callId —— 流式块与提交后的 part 的 block.key 不保证相同
    expect(segments[1]?.key).toBe('pinned:gen')
  })

  it('还在流的 tool_use 块同样认得出来,提交前后不换位置', () => {
    const live: AssistantBlock = {
      key: 'u:0',
      liveBlock: { kind: 'tool_use', index: 0, callId: 'gen', name: 'generate_image', text: '{"prompt":"x' },
      streaming: true,
      cursor: false
    }
    expect(isPinnedToolBlock(live)).toBe(true)
    expect(assistantSegments([live], 'tool')[0]).toMatchObject({ kind: 'block', key: 'pinned:gen' })
  })
})

function stubDom(): { container: HTMLElement; close: () => void } {
  const dom = new JSDOM('<div id="root"></div>', { url: 'http://localhost' })
  Object.assign(dom.window, { nextcowork: { on: () => () => {} } })
  vi.stubGlobal('window', dom.window)
  vi.stubGlobal('document', dom.window.document)
  vi.stubGlobal('HTMLElement', dom.window.HTMLElement)
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  vi.stubGlobal('requestAnimationFrame', () => 1)
  vi.stubGlobal('cancelAnimationFrame', () => {})
  vi.stubGlobal('ResizeObserver', class { observe(): void {} disconnect(): void {} })
  return { container: dom.window.document.getElementById('root')!, close: () => dom.window.close() }
}

let teardown: (() => Promise<void>) | null = null

afterEach(async () => {
  await teardown?.()
  teardown = null
  vi.unstubAllGlobals()
  vi.clearAllMocks()
})

describe('生图卡片 · DOM', () => {
  it('运行中默认展开:n 格里到手的换成图、其余是加载格;跑完点图打开灯箱', async () => {
    const { container, close } = stubDom()
    const root = createRoot(container)
    teardown = async () => {
      await act(async () => root.unmount())
      close()
    }
    const input = { prompt: 'three cats', n: 3 }
    const render = async (call: ToolCallState): Promise<void> => {
      await act(async () => root.render(createElement(I18nProvider, {
        initialLocale: 'zh-CN',
        children: createElement(ToolCallCard, { call, name: 'generate_image', input })
      })))
    }
    await render({ callId: 'gen', name: 'generate_image', input, status: 'running', progress: '1/3', partialImages: { 1: img('BB') } })
    const card = container.querySelector('[data-testid="image-gen-card"]')
    // ★ 默认展开:折叠态下 SurfaceReveal 直接卸载子树,图片卡根本不存在
    expect(card).not.toBeNull()
    expect(container.querySelectorAll('[data-testid="image-gen-loading"]')).toHaveLength(2)
    expect(container.querySelectorAll('[data-testid="image-gen-image"]')).toHaveLength(1)
    expect(container.querySelector('[data-testid="image-gen-loading"]')?.getAttribute('aria-label')).toBe('第 1/3 张')

    await render({
      callId: 'gen', name: 'generate_image', input, status: 'ok',
      output: { content: 'Generated 2 images', images: [img('AA'), img('BB')] }
    })
    expect(container.querySelectorAll('[data-testid="image-gen-loading"]')).toHaveLength(0)
    expect(container.querySelectorAll('[data-testid="image-gen-image"]')).toHaveLength(2)
    expect(container.querySelector('[data-testid="image-gen-partial"]')?.textContent).toBe('成功生成 2/3 张')

    const open = container.querySelectorAll<HTMLButtonElement>('button[aria-label="放大查看第 2 张图片"]')[0]
    await act(async () => open?.click())
    expect(document.querySelector('[data-testid="lightbox-image"]')?.getAttribute('src')).toBe(img('BB').dataRef)
  })

  it('提示词区:两行装得下时不画「展开」,装不下才画,点了去掉裁切', async () => {
    const { container, close } = stubDom()
    const root = createRoot(container)
    teardown = async () => {
      await act(async () => root.unmount())
      close()
    }
    const input = { prompt: 'a very long prompt' }
    const call: ToolCallState = { callId: 'gen', name: 'generate_image', input, status: 'ok', output: { content: 'ok', images: [img('AA')] } }
    const render = async (): Promise<void> => {
      await act(async () => root.render(createElement(I18nProvider, {
        initialLocale: 'zh-CN',
        children: createElement(ToolCallCard, { call, name: 'generate_image', input })
      })))
    }
    // jsdom 不排版:scrollHeight/clientHeight 恒为 0,即「装得下」
    await render()
    expect(container.querySelector('[data-testid="image-gen-prompt"]')?.textContent).toBe('a very long prompt')
    expect(container.querySelector('[data-testid="image-gen-prompt-toggle"]')).toBeNull()
    expect(container.querySelector('[data-testid="image-gen-prompt-copy"]')).not.toBeNull()

    await act(async () => root.unmount())
    const proto = window.HTMLElement.prototype
    Object.defineProperty(proto, 'scrollHeight', { configurable: true, get: () => 80 })
    Object.defineProperty(proto, 'clientHeight', { configurable: true, get: () => 40 })
    const next = createRoot(container)
    teardown = async () => {
      await act(async () => next.unmount())
      close()
    }
    await act(async () => next.render(createElement(I18nProvider, {
      initialLocale: 'zh-CN',
      children: createElement(ToolCallCard, { call, name: 'generate_image', input })
    })))
    const toggle = container.querySelector<HTMLButtonElement>('[data-testid="image-gen-prompt-toggle"]')
    expect(toggle?.getAttribute('aria-expanded')).toBe('false')
    expect(container.querySelector('[data-testid="image-gen-prompt"]')?.className).toContain('line-clamp-2')
    await act(async () => toggle?.click())
    expect(toggle?.getAttribute('aria-expanded')).toBe('true')
    expect(toggle?.textContent).toBe('收起')
    expect(container.querySelector('[data-testid="image-gen-prompt"]')?.className).not.toContain('line-clamp-2')
  })

  it('★ 一轮收尾、过程折进「用时」之后,生成的图仍在折叠外可见', async () => {
    const { container, close } = stubDom()
    const root = createRoot(container)
    teardown = async () => {
      await act(async () => root.unmount())
      close()
    }
    const messages = [
      userMessage('u', [{ type: 'text', text: 'Draw two cats' }], 1),
      assistantMessage('a1', [
        { type: 'tool_call', callId: 'read', name: 'Read', input: { file_path: 'a.md' } },
        { type: 'tool_call', callId: 'gen', name: 'generate_image', input: { prompt: 'cats', n: 2 } }
      ], 2),
      toolResultMessage('r1', [
        { type: 'tool_result', callId: 'read', output: { content: 'ok' }, isError: false },
        { type: 'tool_result', callId: 'gen', output: { content: 'Generated 2 images', images: [img('AA'), img('BB')] }, isError: false }
      ], 3),
      assistantMessage('a2', [{ type: 'text', text: 'Here are your cats' }], 4)
    ]
    const transcript = { ...emptyTranscript(), messages, tools: toolsFromMessages(messages), status: 'done' as const }
    await act(async () => root.render(createElement(I18nProvider, {
      initialLocale: 'en-US',
      children: createElement(Thread, { transcript, runId: null, lastSeq: 0, queued: 0, model: undefined, providerName: undefined })
    })))
    const process = container.querySelector('[data-testid="run-process-block"]')
    expect(process?.getAttribute('data-open')).toBe('false')
    // 图不在被折起来的过程块里,而是在它外面
    const images = [...container.querySelectorAll('[data-testid="image-gen-image"]')]
    expect(images).toHaveLength(2)
    expect(images.every((image) => process?.contains(image) !== true)).toBe(true)
  })
})

/**
 * 复制 / 下载。★ 这一组盯的是**送出去的载荷**,不是「按钮在不在」:
 * 两颗按钮的文案与图标在两种状态下都长得像,而送错的那张图(比如把第 1 张的字节
 * 送给第 2 张那颗按钮)在界面上**看不出任何异常** —— 用户粘出来才发现是另一张。
 * 所以断言用 `toBe`/`toEqual` 精确比,不用 `toContain`。
 */
describe('生图卡片 · 每张图的复制 / 下载', () => {
  async function mountCard(images: ToolOutputImage[]): Promise<HTMLElement> {
    const { container, close } = stubDom()
    const root = createRoot(container)
    teardown = async () => {
      await act(async () => root.unmount())
      close()
    }
    const input = { prompt: 'two cats', n: images.length }
    await act(async () => root.render(createElement(I18nProvider, {
      initialLocale: 'zh-CN',
      children: createElement(ToolCallCard, {
        call: { callId: 'gen', name: 'generate_image', input, status: 'ok', output: { content: 'ok', images } },
        name: 'generate_image',
        input
      })
    })))
    return container
  }

  it('复制送的是**这张图**的字节,复制完按钮自己换成「图片已复制」', async () => {
    const container = await mountCard([img('AA'), img('BB')])
    const buttons = [...container.querySelectorAll<HTMLButtonElement>('[data-testid="image-action-copy"]')]
    expect(buttons).toHaveLength(2)

    await act(async () => buttons[1]?.click())
    expect(copyImage).toHaveBeenCalledWith('BB')
    expect(buttons[1]?.getAttribute('aria-label')).toBe('图片已复制')
    // 另一张那颗不许跟着变 —— 「已复制」标在错的那一张上等于告诉用户复制了它
    expect(buttons[0]?.getAttribute('aria-label')).toBe('复制图片')
  })

  it('下载送的是建议名 + 这张图的字节,取消保存不闪任何状态', async () => {
    const container = await mountCard([img('AA'), img('BB')])
    const buttons = [...container.querySelectorAll<HTMLButtonElement>('[data-testid="image-action-download"]')]

    await act(async () => buttons[1]?.click())
    // 序号是本次调用里的位置:第 2 张就是 image-2(扩展名由主进程按字节补)
    expect(saveImageFile).toHaveBeenCalledWith('image-2', 'BB')
    expect(buttons[1]?.getAttribute('aria-label')).toBe('图片已保存')

    // 用户在系统对话框里按取消(主进程回 null)—— 那不是失败,不许标「失败」也不许标「已保存」
    vi.mocked(saveImageFile).mockResolvedValueOnce(null)
    await act(async () => buttons[0]?.click())
    expect(buttons[0]?.getAttribute('aria-label')).toBe('下载图片')
  })

  it('读不到字节(附件没了)时闪失败态,而不是把空内容送出去', async () => {
    const container = await mountCard([{ mime: 'image/png', dataRef: 'ncw://attachments/sessions/s1/gone.png' }])
    const button = container.querySelector<HTMLButtonElement>('[data-testid="image-action-copy"]')
    // 只给 imageBase64 真正读的那两个字段 —— jsdom 里不必凑一个真 Response
    vi.stubGlobal('fetch', async () => ({ ok: false, status: 404 }))

    await act(async () => button?.click())
    expect(copyImage).not.toHaveBeenCalled()
    expect(button?.getAttribute('aria-label')).toBe('复制图片失败')
  })
})
