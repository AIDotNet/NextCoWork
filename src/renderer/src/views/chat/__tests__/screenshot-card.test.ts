/**
 * 浏览器截图卡片(`browser_screenshot`)。
 *
 * 这一组守的坏全都**不报错**:
 * - 图还在这条调用里、却画了一片空白 —— 用户会以为这次调用什么都没产出;
 * - 失败时照样画一张“成功”的图框 —— 而此刻 `output.images` 里根本没有图;
 * - 截图被过程段折走 —— 一轮收尾折进「用时」之后,那张图正是这次调用唯一的产物,
 *   折起来等于这次调用在界面上只剩一个标题(判据见 `isPinnedShape`)。
 *
 * 为什么和生图卡片分开测:两张卡的生命周期不同(截图没有生成期、没有提示词、
 * 没有多图网格),共用断言只会让两边都不敢改。理由写在 `ScreenshotDetail.tsx` 的文件头。
 */
import { act, createElement } from 'react'
import { createRoot } from 'react-dom/client'
import { JSDOM } from 'jsdom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { assistantMessage, toolResultMessage, userMessage, type ToolOutputImage } from '../../../../../shared/agent/message'
import { emptyTranscript, toolsFromMessages, type ToolCallState } from '../../../../../shared/agent/transcript'
import { I18nProvider } from '../../../i18n'
import { ToolCallCard } from '../parts'
import { Thread } from '../Thread'
import { isPinnedToolBlock, type AssistantBlock } from '../thread-content'

/** 一帧页面截图。★ dataRef 是内联 data URL —— 截图不是磁盘上的文件(见卡片头注释)。 */
const shot = (tag: string): ToolOutputImage => ({ mime: 'image/png', dataRef: `data:image/png;base64,${tag}` })

describe('isPinnedToolBlock · 截图与生图一样不进过程段', () => {
  it('browser_screenshot 的两种来源(流式块 / 已提交 part)都认得出来', () => {
    const committed: AssistantBlock = {
      key: 'p0',
      part: { type: 'tool_call', callId: 'shot', name: 'browser_screenshot', input: { tabId: 'tab-1' } },
      streaming: false,
      cursor: false
    }
    expect(isPinnedToolBlock(committed)).toBe(true)

    // 流式 tool_use 块走的是另一条投影路径 —— 只认其中一条的话,提交那一刻卡片会挪位置
    const live: AssistantBlock = {
      key: 'u:0',
      liveBlock: { kind: 'tool_use', index: 0, callId: 'shot', name: 'browser_screenshot', text: '{"tabId":"t' },
      streaming: true,
      cursor: false
    }
    expect(isPinnedToolBlock(live)).toBe(true)
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
})

describe('截图卡片 · DOM', () => {
  const input = { tabId: 'tab-1' }

  async function mount(): Promise<{
    container: HTMLElement
    render: (call: ToolCallState) => Promise<void>
  }> {
    const { container, close } = stubDom()
    const root = createRoot(container)
    teardown = async () => {
      await act(async () => root.unmount())
      close()
    }
    const render = async (call: ToolCallState): Promise<void> => {
      await act(async () => root.render(createElement(I18nProvider, {
        initialLocale: 'zh-CN',
        children: createElement(ToolCallCard, { call, name: 'browser_screenshot', input })
      })))
    }
    return { container, render }
  }

  it('图还没到时画等待卡而不是空框;图到了换成真图并显示 tabId', async () => {
    const { container, render } = await mount()

    await render({ callId: 'shot', name: 'browser_screenshot', input, status: 'running' })
    const loading = container.querySelector('[data-testid="screenshot-loading"]')
    expect(loading).not.toBeNull()
    // ★ 等待卡是 role=status:读屏要能念出「正在截取」,而不只是一圈动画
    expect(loading?.getAttribute('role')).toBe('status')
    expect(loading?.getAttribute('aria-label')).toBe('页面截图')
    expect(container.querySelector('[data-testid="screenshot-card"]')).toBeNull()

    await render({
      callId: 'shot', name: 'browser_screenshot', input, status: 'ok',
      output: { content: 'Screenshot captured', images: [shot('AA')] }
    })
    expect(container.querySelector('[data-testid="screenshot-loading"]')).toBeNull()
    const card = container.querySelector('[data-testid="screenshot-card"]')
    expect(card).not.toBeNull()
    expect(container.querySelector('[data-testid="screenshot-image"]')?.getAttribute('src')).toBe(shot('AA').dataRef)
    // 同时开着几个页面时,「这是哪个 tab 的」是唯一能把连着的几张截图对上的东西
    expect(card?.textContent).toContain('tab-1')
  })

  it('跑完但一张图都没有:不画「成功」的图框(那等于说这次成了)', async () => {
    const { container, render } = await mount()
    await render({
      callId: 'shot', name: 'browser_screenshot', input, status: 'ok',
      output: { content: 'Screenshot captured' }
    })
    expect(container.querySelector('[data-testid="screenshot-card"]')).toBeNull()
    expect(container.querySelector('[data-testid="screenshot-image"]')).toBeNull()
    // 退回等待态:这次调用此刻确实没有图可给
    expect(container.querySelector('[data-testid="screenshot-loading"]')).not.toBeNull()
  })

  it('失败时只给原因,不画卡片', async () => {
    const { container, render } = await mount()
    await render({
      callId: 'shot', name: 'browser_screenshot', input, status: 'error',
      output: { content: 'No tab with id tab-1' }
    })
    expect(container.querySelector('[data-testid="screenshot-card"]')).toBeNull()
    expect(container.textContent).toContain('No tab with id tab-1')
  })

  it('点图打开灯箱 —— 键盘也能到(button 而不是给 img 挂 onClick)', async () => {
    const { container, render } = await mount()
    await render({
      callId: 'shot', name: 'browser_screenshot', input, status: 'ok',
      output: { content: 'ok', images: [shot('AA')] }
    })
    const open = container.querySelector<HTMLButtonElement>('button[aria-label="放大查看图片"]')
    expect(open).not.toBeNull()
    await act(async () => open?.click())
    expect(document.querySelector('[data-testid="lightbox-image"]')?.getAttribute('src')).toBe(shot('AA').dataRef)
  })

  it('★ 一轮收尾、过程折进「用时」之后,截图仍留在折叠外可见', async () => {
    const { container, close } = stubDom()
    const root = createRoot(container)
    teardown = async () => {
      await act(async () => root.unmount())
      close()
    }
    const messages = [
      userMessage('u', [{ type: 'text', text: '看看首页' }], 1),
      assistantMessage('a1', [
        { type: 'tool_call', callId: 'read', name: 'Read', input: { file_path: 'a.md' } },
        { type: 'tool_call', callId: 'shot', name: 'browser_screenshot', input }
      ], 2),
      toolResultMessage('r1', [
        { type: 'tool_result', callId: 'read', output: { content: 'ok' }, isError: false },
        { type: 'tool_result', callId: 'shot', output: { content: 'ok', images: [shot('AA')] }, isError: false }
      ], 3),
      assistantMessage('a2', [{ type: 'text', text: '首页有个登录按钮' }], 4)
    ]
    const transcript = { ...emptyTranscript(), messages, tools: toolsFromMessages(messages), status: 'done' as const }
    await act(async () => root.render(createElement(I18nProvider, {
      initialLocale: 'en-US',
      children: createElement(Thread, { transcript, runId: null, lastSeq: 0, queued: 0, model: undefined, providerName: undefined })
    })))
    const process = container.querySelector('[data-testid="run-process-block"]')
    expect(process?.getAttribute('data-open')).toBe('false')
    const image = container.querySelector('[data-testid="screenshot-image"]')
    expect(image).not.toBeNull()
    expect(process?.contains(image) !== true).toBe(true)
  })
})
