/**
 * 「打开方式」下拉。
 *
 * 钉两件事 —— 它们都不会以「报错」的形式坏掉:
 *
 * 1. **目录上不列编辑器。** 顺手接上的表现是「点了一下目录,VS Code 的工作区根
 *    被换掉了」,而用户当时只是想看看那个目录里有什么。
 * 2. **两个通用目标的名字走 i18n。** 主进程下发的是空 label(「文件管理器」在
 *    各平台叫法不同,而该跟着应用语言走的是这个概念,不是产品名);渲染层要是
 *    忘了映射,菜单上就是两行空白 —— 看得见,但很容易被当成「还没探测完」。
 *
 * 真 jsdom 环境(菜单要量高度、要监听 document 上的 pointerdown),与
 * `views/chat/__tests__/goal-panel.test.ts` 同一套做法。
 *
 * @vitest-environment jsdom
 */
import { act, createElement, type ReactNode } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { OpenTarget } from '../../../../shared/domain/open-target'
import { I18nProvider, type Locale } from '../../i18n'

vi.mock('../../services/open-with', () => ({
  listOpenTargets: vi.fn(),
  openWithTarget: vi.fn(async () => undefined),
  copyWorkspacePath: vi.fn(async () => '/tmp/x')
}))

import { listOpenTargets, openWithTarget } from '../../services/open-with'
import { Menu } from '../ui/Menu'
import { OpenWithItems, OpenWithMenu } from '../OpenWithMenu'

const probe = vi.mocked(listOpenTargets)
const open = vi.mocked(openWithTarget)

const TARGETS: OpenTarget[] = [
  { id: 'reveal', label: '', icon: 'file-manager' },
  { id: 'terminal', label: '', icon: 'terminal' },
  { id: 'vscode', label: 'Visual Studio Code', icon: 'vscode' },
  { id: 'rider', label: 'Rider', icon: 'rider' }
]

let teardown: (() => Promise<void>) | null = null

beforeEach(() => {
  probe.mockReset()
  probe.mockResolvedValue(TARGETS)
  open.mockClear()
  // 菜单用 ResizeObserver 跟着面板尺寸重算落点;jsdom 没有它
  vi.stubGlobal('ResizeObserver', class {
    observe(): void {}
    unobserve(): void {}
    disconnect(): void {}
  })
})

afterEach(async () => {
  await teardown?.()
  teardown = null
  vi.unstubAllGlobals()
})

/** 打开菜单并把它整份展开后的文本交出来。 */
async function openMenu(node: ReactNode, locale: Locale = 'zh-CN'): Promise<string> {
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  const container = document.createElement('div')
  document.body.append(container)
  const root = createRoot(container)
  await act(async () => root.render(createElement(I18nProvider, { initialLocale: locale, children: node })))
  const trigger = container.querySelector('button')
  if (trigger === null) throw new Error('触发器不在')
  await act(async () => trigger.click())
  await act(async () => { await Promise.resolve() })
  teardown = async () => {
    await act(async () => root.unmount())
    container.remove()
  }
  return document.body.textContent ?? ''
}

describe('OpenWithMenu', () => {
  it('★ 两个通用目标的名字由渲染层出 —— 主进程给的是空 label', async () => {
    const text = await openMenu(createElement(OpenWithMenu, { workspaceId: 'w1', path: 'src/a.ts', trigger: createElement('span', null, '▾') }))
    expect(text).toContain('文件管理器')
    expect(text).toContain('终端')
    expect(text).toContain('Visual Studio Code')
  })

  it('★ 目录上不列编辑器,只留文件管理器与终端', async () => {
    const text = await openMenu(createElement(OpenWithMenu, {
      workspaceId: 'w1',
      path: 'src',
      directory: true,
      trigger: createElement('span', null, '▾')
    }))
    expect(text).toContain('文件管理器')
    expect(text).not.toContain('Visual Studio Code')
    expect(text).not.toContain('Rider')
  })

  it('点一项之后调的是那条具名目标,而不是让渲染层自己拼命令', async () => {
    await openMenu(createElement(OpenWithMenu, { workspaceId: 'w1', path: 'src/a.ts', trigger: createElement('span', null, '▾') }))
    // MenuItem 的内容包在 `<span className="block truncate">` 里,textContent 带一层空白
    const item = [...document.querySelectorAll('button')].find((node) => node.textContent?.trim() === 'Visual Studio Code')
    if (item === undefined) throw new Error('菜单项不在')
    await act(async () => item.click())
    expect(open).toHaveBeenCalledWith('w1', 'src/a.ts', 'vscode')
  })

  it('★ 品牌字形真的画出来了 —— 「id 有、图没有」在界面上是一排空白', async () => {
    await openMenu(createElement(OpenWithMenu, { workspaceId: 'w1', path: 'src/a.ts', trigger: createElement('span', null, '▾') }))
    const item = [...document.querySelectorAll('button')].find((node) => node.textContent?.trim() === 'Visual Studio Code')
    // 内联 svg 缺 width/height 时按 100% 解析,外层又想被内容撑开 —— 两者会一起塌成 0
    expect(item?.querySelector('svg')?.getAttribute('viewBox')).toBe('0 0 24 24')
    expect(item?.querySelector('span[aria-hidden]')?.getAttribute('style')).toContain('width: 14px')
  })

  it('★ 文件树那一行的平铺形态:不再重复「文件管理器」,但终端与编辑器都在', async () => {
    const text = await openMenu(createElement(Menu, {
      label: 'actions',
      trigger: createElement('span', null, '…'),
      children: (close: () => void) => createElement(OpenWithItems, {
        workspaceId: 'w1',
        path: 'src/a.ts',
        omitReveal: true,
        close
      })
    }))
    expect(text).not.toContain('文件管理器')
    expect(text).toContain('终端')
    expect(text).toContain('Visual Studio Code')
    expect(text).toContain('复制绝对路径')
  })
})
