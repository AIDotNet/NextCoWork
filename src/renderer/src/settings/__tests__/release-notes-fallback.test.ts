/*
 * @vitest-environment jsdom
 * ★ 必须在模块加载时就有 document:Radix 的 useLayoutEffect 在 import 那一刻判断
 *   `globalThis.document` 是否存在,不存在就换成空函数 —— 之后再 stubGlobal 也晚了,
 *   弹窗的 Portal 永远挂不上;Motion 也要全局的 getComputedStyle。
 */
/**
 * 「更新说明」弹窗:**Markdown 渲染器到达之前**那一帧。
 *
 * 单独成一个文件,是因为这条用例要把 markdown 那一侧换成「一直不解析」的 mock ——
 * 在 `release-notes.test.ts` 里它已经被真实加载过,换不成。
 *
 * ★ 用闸门而不是等定时器:赌时机的版本单独跑能过、全套跑时因为模块已经热掉而
 * 当场提交,是一条会自己变红的测试(第一版就是这么写的)。
 *
 * 它挡的是:慢盘/冷启动上先出现一个空弹窗,而用户会以为「更新说明是空的」。
 */
import { act, createElement } from 'react'
import { createRoot } from 'react-dom/client'
import { JSDOM } from 'jsdom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { I18nProvider } from '../../i18n'
import { ReleaseNotesDialog } from '../pages/ReleaseNotesDialog'

const NOTES = '### 改动\n\n- **macOS 发布包启用签名与公证**:不签名的包过不了 Gatekeeper'

/** 手动放行的 markdown 模块 —— 工厂体在测试跑起来之后才执行,所以这里不是 TDZ */
let releaseMarkdown: () => void = () => {}

vi.mock('../../components/markdown', () => new Promise((resolve) => {
  releaseMarkdown = () => resolve({ AgentMarkdown: () => createElement('div', { 'data-testid': 'markdown' }) })
}))

beforeEach(() => {
  const dom = new JSDOM('<div id="root"></div>', { url: 'http://localhost' })
  vi.stubGlobal('window', dom.window)
  vi.stubGlobal('document', dom.window.document)
  vi.stubGlobal('HTMLElement', dom.window.HTMLElement)
  vi.stubGlobal('Node', dom.window.Node)
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  vi.stubGlobal('requestAnimationFrame', () => 1)
  vi.stubGlobal('cancelAnimationFrame', () => {})
  vi.stubGlobal('ResizeObserver', class { observe(): void {} disconnect(): void {} })
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('release notes before the markdown chunk arrives', () => {
  it('shows the notes as plain text instead of an empty dialog', async () => {
    const root = createRoot(document.getElementById('root')!)
    await act(async () => root.render(createElement(I18nProvider, { initialLocale: 'zh-CN', children:
      createElement(ReleaseNotesDialog, { notes: NOTES, open: true, onClose: () => {} }) })))
    const panel = document.querySelector('[role="dialog"]')!
    // 原文照旧可读,只是还没变成标题和列表 —— 绝不能是空白
    expect(panel.textContent).toContain('### 改动')
    expect(panel.textContent).toContain('macOS 发布包启用签名与公证')
    expect(panel.querySelector('h3')).toBeNull()

    await act(async () => { releaseMarkdown() })
    expect(panel.querySelector('[data-testid="markdown"]')).not.toBeNull()
  })
})
