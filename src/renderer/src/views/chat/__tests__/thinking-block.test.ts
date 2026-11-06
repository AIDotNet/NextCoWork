/**
 * 思考卡片标题行右端的「用时 · token」读数。
 *
 * 钉住三件肉眼容易放过的事:上游真值与估算分得开、旧转录没有计时时
 * 用时那一格根本不画(而不是写 0)、流式中那一格会走表。
 * (原先靠读数带不带 ≈ 区分真值与估算;按需求读数本身已不加 ≈,
 * 两者现在只靠 `data-estimated` 和悬停提示区分,见 parts.tsx / i18n/thinking-stats.ts。)
 */
import { act, createElement } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { JSDOM } from 'jsdom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { ThinkingStats } from '../../../../../shared/agent/thinking-stats'
import { I18nProvider } from '../../../i18n'
import { ThinkingBlock } from '../parts'

let teardown: (() => Promise<void>) | null = null

afterEach(async () => {
  await teardown?.()
  teardown = null
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

function mount(): { container: HTMLElement; root: Root } {
  const dom = new JSDOM('<div id="root"></div>', { url: 'http://localhost' })
  Object.assign(dom.window, { nextcowork: { on: () => () => {} } })
  vi.stubGlobal('window', dom.window)
  vi.stubGlobal('document', dom.window.document)
  vi.stubGlobal('HTMLElement', dom.window.HTMLElement)
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  vi.stubGlobal('requestAnimationFrame', () => 1)
  vi.stubGlobal('cancelAnimationFrame', () => {})
  vi.stubGlobal('ResizeObserver', class { observe(): void {} disconnect(): void {} })
  const container = document.getElementById('root')!
  const root = createRoot(container)
  teardown = async () => {
    await act(async () => root.unmount())
    dom.window.close()
  }
  return { container, root }
}

async function render(root: Root, text: string, streaming: boolean, stats?: ThinkingStats): Promise<void> {
  await act(async () => root.render(createElement(I18nProvider, {
    initialLocale: 'zh-CN',
    children: createElement(ThinkingBlock, { text, streaming, ...(stats === undefined ? {} : { stats }) })
  })))
}

const read = (container: HTMLElement, id: string): Element | null =>
  container.querySelector(`[data-testid="${id}"]`)

describe('ThinkingBlock · 用时与 token 读数', () => {
  it('已提交且上游报了真值:显示落盘用时和两位小数的 token 数', async () => {
    const { container, root } = mount()
    await render(root, '先看配置', false, { durationMs: 12_300, tokens: 1_234 })
    expect(read(container, 'thinking-duration')?.textContent).toBe('12s')
    expect(read(container, 'thinking-tokens')?.textContent).toBe('1.23K tokens')
    expect(read(container, 'thinking-tokens')?.getAttribute('data-estimated')).toBe('false')
  })

  it('旧转录没有计时:用时不画,token 按正文估约数并标 data-estimated', async () => {
    const { container, root } = mount()
    await render(root, 'a'.repeat(400), false)
    expect(read(container, 'thinking-duration')).toBeNull()
    expect(read(container, 'thinking-tokens')?.textContent).toBe('100 tokens')
    expect(read(container, 'thinking-tokens')?.getAttribute('data-estimated')).toBe('true')
  })

  it('流式中用时按本地时钟走表', async () => {
    vi.useFakeTimers({ now: 10_000 })
    const { container, root } = mount()
    await render(root, '想', true, { startedAt: 8_000, endedAt: 9_000 })
    expect(read(container, 'thinking-duration')?.textContent).toBe('2.0s')
    await act(async () => { vi.advanceTimersByTime(1_500) })
    expect(read(container, 'thinking-duration')?.textContent).toBe('3.5s')
  })
})
