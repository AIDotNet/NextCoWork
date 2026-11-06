/**
 * 工具卡片的流式参数回归。
 *
 * 上游参数在 `tool_call_end` 前只是 JSON 前缀；这条用例钉住卡片会随前缀增长
 * 立即更新目标，而不是一直显示无参数兜底、等工具真正开始后才突然跳变。
 *
 * ★ 断言读的是行里那一格(`row-target`)而不是整行文本:行改成
 * 「标签 / 目标 / 目录」三段之后,相邻 span 之间的空格是 flex 的 gap,
 * 不在 `textContent` 里 —— 拿整行文本对比会把一次纯版式改动误报成回归。
 */
import { act, createElement } from 'react'
import { createRoot } from 'react-dom/client'
import { JSDOM } from 'jsdom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { ToolCallState } from '../../../../../shared/agent/transcript'
import { I18nProvider } from '../../../i18n'
import { MAX_PARTIAL_JSON_CHARS } from '../partial-json'
import { ToolCallCard } from '../parts'

let teardown: (() => Promise<void>) | null = null

afterEach(async () => {
  await teardown?.()
  teardown = null
  vi.unstubAllGlobals()
})

describe('ToolCallCard · 流式参数', () => {
  it('JSON 字符串尚未闭合时就用已到达字段实时更新目标', async () => {
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
    const render = async (input: string, call?: ToolCallState): Promise<void> => {
      await act(async () => root.render(createElement(I18nProvider, {
        initialLocale: 'zh-CN',
        children: createElement(ToolCallCard, { call, name: 'Read', input })
      })))
    }
    const cardText = (): string => {
      const card = container.querySelector('[data-testid="tool-call"]')
      if (card === null) throw new Error('工具卡片没有渲染')
      return card.textContent ?? ''
    }
    /** 行里「目标」那一格 —— 文件名就画在这里,和标签、目录各占一格 */
    const targetText = (): string =>
      container.querySelector('[data-testid="row-target"]')?.textContent ?? ''
    /**
     * 行里「目录」那一格。
     *
     * ★ 路径的另一半现在在**行上**,不在展开区里:展开后原先还有一行完整路径,
     * 那一行已经删掉(见 `ToolDetail` 的 ReadDetail)——
     * 所以这条用例改成在行上验证「前缀在增长」,而不是点开去找。
     */
    const contextText = (): string =>
      container.querySelector('[data-testid="row-context"]')?.textContent ?? ''
    teardown = async () => {
      await act(async () => root.unmount())
      dom.window.close()
    }

    await render('{"file_path":"src/ren')
    expect(targetText()).toBe('ren')
    expect(contextText()).toBe('src/')
    expect(cardText()).toContain('读取')
    const trigger = container.querySelector<HTMLButtonElement>('button')
    if (trigger === null) throw new Error('工具卡片按钮没有渲染')
    await act(async () => trigger.click())

    await render('{"file_path":"src/renderer/src/App.ts')
    expect(targetText()).toBe('App.ts')
    expect(contextText()).toBe('src/renderer/src/')

    await render('{"file_path":"src/orphan.ts', {
      callId: 'call-orphan',
      name: 'Read',
      input: undefined,
      status: 'running'
    })
    expect(targetText()).toBe('orphan.ts')

    await render(`{"padding":"${'x'.repeat(MAX_PARTIAL_JSON_CHARS)}","file_path":"late.ts`)
    expect(cardText()).not.toContain('late.ts')

    await render('{"file_path":"src/other.ts', {
      callId: 'call-1',
      name: 'Read',
      input: { file_path: 'src/real.ts' },
      status: 'running'
    })
    expect(targetText()).toBe('real.ts')
    expect(cardText()).not.toContain('other.ts')
  })
})
