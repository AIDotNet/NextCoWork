/*
 * @vitest-environment jsdom
 * ★ 必须在模块加载时就有 document:Radix 的 useLayoutEffect 在 import 那一刻判断
 *   `globalThis.document` 是否存在,不存在就换成空函数 —— 之后再 stubGlobal 也晚了,
 *   弹窗的 Portal 永远挂不上;Motion 也要全局的 getComputedStyle。
 */
import { act, createElement } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { JSDOM } from 'jsdom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { I18nProvider } from '../../../i18n'
import { useToastStore } from '../../../stores/toast'
import { TurnActions } from '../TurnActions'

/**
 * 「分支」按钮的忙碌态。原先请求在路上时按钮毫无变化,连点几下就建出几条一样的分支;
 * 失败只改 tooltip,不悬停就看不见。这两条都是肉眼验收容易漏的。
 */

let root: Root
let container: HTMLElement

beforeEach(() => {
  const dom = new JSDOM('<div id="root"></div>', { url: 'http://localhost' })
  vi.stubGlobal('window', dom.window)
  vi.stubGlobal('document', dom.window.document)
  vi.stubGlobal('HTMLElement', dom.window.HTMLElement)
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  vi.stubGlobal('requestAnimationFrame', () => 1)
  vi.stubGlobal('cancelAnimationFrame', () => {})
  container = dom.window.document.getElementById('root')!
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  useToastStore.getState().clear()
  vi.unstubAllGlobals()
})

function render(onBranch: (id: string) => Promise<void>): void {
  act(() => root.render(createElement(I18nProvider, { initialLocale: 'en-US', children:
    createElement(TurnActions, { text: 'answer', prompt: { id: 'u1', text: 'question' }, alwaysVisible: true, disabled: false, onBranch }) })))
}

const branchButton = (): HTMLButtonElement => container.querySelector<HTMLButtonElement>('[data-testid="turn-branch"]')!

describe('turn branch button', () => {
  it('disables itself while the branch is in flight so repeated clicks send one request', async () => {
    let finish: () => void = () => {}
    const onBranch = vi.fn(() => new Promise<void>((resolve) => { finish = resolve }))
    render(onBranch)
    act(() => branchButton().click())
    act(() => branchButton().click())
    expect(onBranch).toHaveBeenCalledTimes(1)
    expect(branchButton().disabled).toBe(true)
    await act(async () => { finish() })
    expect(branchButton().disabled).toBe(false)
    expect(branchButton().getAttribute('aria-label')).toBe('Branched')
  })

  it('reports a failure through a toast instead of only the hover tooltip', async () => {
    render(vi.fn(() => Promise.reject(new Error('boom'))))
    await act(async () => { branchButton().click() })
    expect(useToastStore.getState().toasts).toMatchObject([{ tone: 'error', message: 'Branch failed. Try again' }])
    expect(branchButton().disabled).toBe(false)
  })
})
