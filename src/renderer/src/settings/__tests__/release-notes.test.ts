/*
 * @vitest-environment jsdom
 * ★ 必须在模块加载时就有 document:Radix 的 useLayoutEffect 在 import 那一刻判断
 *   `globalThis.document` 是否存在,不存在就换成空函数 —— 之后再 stubGlobal 也晚了,
 *   弹窗的 Portal 永远挂不上;Motion 也要全局的 getComputedStyle。
 */
/**
 * 「更新说明」弹窗:Markdown 那一侧真的渲染出来了,以及关着的时候什么都不挂。
 *
 * 测的不是外观,是一件**错了也不报错**的事:弹窗里的文字是从 `CHANGELOG.md` 截出来
 * 的 Markdown(`### 新增` / `**加粗**` / `- 列表`)。接成纯文本的话界面上会原样出现
 * `###` 和 `**`,没有红字、没有异常,只有用户看得见 —— 2026-09-21 的 v2.2.3 打包版
 * 就是这样。
 *
 * 「渲染器还没到」那条边界在 `release-notes-fallback.test.ts`:它要求 markdown 那条
 * 依赖链在本进程里还没被加载过,和这个文件放一起会互相干扰(vitest 每个测试文件才
 * 有独立的模块注册表)。
 */
import { act, createElement } from 'react'
import { createRoot } from 'react-dom/client'
import { JSDOM } from 'jsdom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { I18nProvider } from '../../i18n'
import { ReleaseNotesDialog } from '../pages/ReleaseNotesDialog'

const NOTES = '### 改动\n\n- **macOS 发布包启用签名与公证**:不签名的包过不了 Gatekeeper'

let dom: JSDOM
let container: HTMLElement

beforeEach(() => {
  dom = new JSDOM('<div id="root"></div>', { url: 'http://localhost' })
  vi.stubGlobal('window', dom.window)
  vi.stubGlobal('document', dom.window.document)
  vi.stubGlobal('HTMLElement', dom.window.HTMLElement)
  vi.stubGlobal('Node', dom.window.Node)
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  vi.stubGlobal('requestAnimationFrame', () => 1)
  vi.stubGlobal('cancelAnimationFrame', () => {})
  vi.stubGlobal('ResizeObserver', class { observe(): void {} disconnect(): void {} })
  container = document.getElementById('root')!
})

afterEach(() => {
  vi.unstubAllGlobals()
})

/** 让 lazy 的 chunk 落地。用真实定时器 —— 动态 import 至少要跨几个宏任务。 */
async function settle(): Promise<void> {
  for (let i = 0; i < 5; i += 1) await act(async () => { await new Promise((resolve) => setTimeout(resolve, 0)) })
}

/** 等到 Markdown 那一侧真的提交(`h3` 出现)。首次解析整条 markdown 依赖链要久一些。 */
async function untilRendered(): Promise<void> {
  for (let i = 0; i < 200; i += 1) {
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 10)) })
    if (document.querySelector('[role="dialog"] h3') !== null) return
  }
}

describe('release notes dialog', () => {
  it('renders the changelog as markdown instead of showing its source syntax', async () => {
    const root = createRoot(container)
    await act(async () => root.render(createElement(I18nProvider, { initialLocale: 'zh-CN', children:
      createElement(ReleaseNotesDialog, { notes: NOTES, open: true, onClose: () => {} }) })))
    await untilRendered()
    const panel = document.querySelector('[role="dialog"]')!
    expect(panel.querySelector('h3')?.textContent).toBe('改动')
    expect(panel.querySelector('strong')?.textContent).toBe('macOS 发布包启用签名与公证')
    expect(panel.querySelectorAll('li')).toHaveLength(1)
    expect(panel.textContent).not.toContain('###')
    expect(panel.textContent).not.toContain('**')
  })

  /*
    「关着的时候不挂载 markdown 那一侧」—— `lazy` 的加载在它首次渲染时就发起,
    常挂的话 About 页一打开就会替所有人拉那 1.0MB 的 chunk。
  */
  it('renders nothing while closed, so the markdown chunk is never fetched early', async () => {
    const root = createRoot(container)
    await act(async () => root.render(createElement(I18nProvider, { initialLocale: 'zh-CN', children:
      createElement(ReleaseNotesDialog, { notes: NOTES, open: false, onClose: () => {} }) })))
    await settle()
    expect(document.querySelector('[role="dialog"]')).toBeNull()
  })
})
