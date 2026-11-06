/**
 * 文件树右键菜单。
 *
 * 钉的都是「不报错、只是看起来不对」的那一类:
 *
 * 1. 第一行跟着**设置里的默认打开方式**走,而不是探测表的第一项。
 * 2. 二级菜单 portal 在 body 下 —— 指针移进去不能把它自己收掉(React 事件沿组件树冒泡),
 *    点里面的项不能先被父菜单判成「点了外面」。
 * 3. 远端工作区不画本机专属的那几行。
 *
 * @vitest-environment jsdom
 */
import { act, createElement } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { FileEntry } from '../../../../../shared/domain/file-tree'
import type { OpenTarget } from '../../../../../shared/domain/open-target'
import { I18nProvider } from '../../../i18n'

vi.mock('../../../services/open-with', () => ({
  listOpenTargets: vi.fn(),
  openWithTarget: vi.fn(async () => undefined),
  copyWorkspacePath: vi.fn(async () => '/tmp/x'),
  saveWorkspaceFileAs: vi.fn(async () => true)
}))
vi.mock('../../../services/app', () => ({
  getSettings: vi.fn(async () => ({ defaultOpenTarget: 'zed' })),
  copyText: vi.fn(async () => undefined)
}))
vi.mock('../add-to-chat', () => ({ addFileToChat: vi.fn(() => true) }))

import { listOpenTargets, openWithTarget } from '../../../services/open-with'
import { copyText } from '../../../services/app'
import { addFileToChat } from '../add-to-chat'
import { FileRowMenu } from '../FileRowMenu'

const TARGETS: OpenTarget[] = [
  { id: 'reveal', label: '', icon: 'file-manager' },
  { id: 'default-app', label: '', icon: 'default-app' },
  { id: 'terminal', label: '', icon: 'terminal' },
  { id: 'vscode', label: 'Visual Studio Code', icon: 'vscode' },
  { id: 'zed', label: 'Zed', icon: 'zed' }
]

const FILE: FileEntry = { name: 'a.ts', path: 'src/a.ts', kind: 'file', hidden: false }
const DIR: FileEntry = { name: 'src', path: 'src', kind: 'dir', hidden: false }

let teardown: (() => Promise<void>) | null = null

beforeEach(() => {
  vi.mocked(listOpenTargets).mockResolvedValue(TARGETS)
  vi.mocked(openWithTarget).mockClear()
  vi.mocked(copyText).mockClear()
  vi.mocked(addFileToChat).mockClear()
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
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

async function render(entry: FileEntry, local = true): Promise<void> {
  const container = document.createElement('div')
  document.body.append(container)
  const root = createRoot(container)
  await act(async () => root.render(createElement(I18nProvider, {
    initialLocale: 'zh-CN',
    children: createElement(FileRowMenu, {
      workspaceId: 'w1',
      entry,
      position: { x: 100, y: 100 },
      local,
      onOperation: () => undefined,
      onDelete: () => undefined,
      onReveal: () => undefined,
      onClose: () => undefined
    })
  })))
  // 探测结果和设置都是异步回来的
  await act(async () => { await Promise.resolve() })
  await act(async () => { await Promise.resolve() })
  teardown = async () => {
    await act(async () => root.unmount())
    container.remove()
  }
}

function item(text: string): HTMLButtonElement {
  const found = [...document.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')]
    .find((node) => node.textContent?.trim() === text)
  if (found === undefined) throw new Error(`菜单项不在:${text}`)
  return found
}

const menus = (): HTMLElement[] => [...document.querySelectorAll<HTMLElement>('[role="menu"]')]

describe('FileRowMenu', () => {
  it('第一行按设置里的默认程序写成「在 Zed 中打开」,点它打开的就是 Zed', async () => {
    await render(FILE)
    await act(async () => item('在 Zed 中打开').click())
    expect(openWithTarget).toHaveBeenCalledWith('w1', 'src/a.ts', 'zed')
  })

  it('参考截图里那几行都在:打开方式 / 另存为 / 复制路径 / 添加到聊天', async () => {
    await render(FILE)
    for (const text of ['打开方式', '另存为…', '复制路径', '添加到聊天']) expect(item(text)).toBeDefined()
  })

  it('★ 悬停「打开方式」展开子菜单,默认那个打头;指针移进子菜单不会把它收掉', async () => {
    await render(FILE)
    await act(async () => { item('打开方式').dispatchEvent(new MouseEvent('pointerover', { bubbles: true })) })
    expect(menus()).toHaveLength(2)
    const submenu = menus()[1]
    const labels = [...(submenu?.querySelectorAll('[role="menuitem"]') ?? [])].map((node) => node.textContent?.trim())
    expect(labels).toEqual(['Zed', '默认应用', '文件管理器', '终端在此文件所在目录打开终端', 'Visual Studio Code'])

    await act(async () => { item('Visual Studio Code').dispatchEvent(new MouseEvent('pointerover', { bubbles: true })) })
    expect(menus()).toHaveLength(2)
    // pointerdown 在子菜单里:父菜单不该把它当成「点了外面」
    await act(async () => { item('Visual Studio Code').dispatchEvent(new MouseEvent('pointerdown', { bubbles: true })) })
    await act(async () => item('Visual Studio Code').click())
    expect(openWithTarget).toHaveBeenCalledWith('w1', 'src/a.ts', 'vscode')
  })

  it('指针移到父菜单的别的行上就收起子菜单', async () => {
    await render(FILE)
    await act(async () => { item('打开方式').dispatchEvent(new MouseEvent('pointerover', { bubbles: true })) })
    expect(menus()).toHaveLength(2)
    await act(async () => { item('复制路径').dispatchEvent(new MouseEvent('pointerover', { bubbles: true })) })
    expect(menus()).toHaveLength(1)
  })

  it('「添加到聊天」把这一项交给当前工作区的对话', async () => {
    await render(FILE)
    await act(async () => item('添加到聊天').click())
    expect(addFileToChat).toHaveBeenCalledWith('w1', { name: 'a.ts', path: 'src/a.ts' })
  })

  it('★ 目录上没有「另存为」,第一行即使设置里选的是编辑器也是文件管理器', async () => {
    await render(DIR)
    expect(item('在 文件管理器 中打开')).toBeDefined()
    expect(() => item('另存为…')).toThrow()
  })

  it('★ 远端工作区不画打开方式 / 另存为 / 复制绝对路径;相对路径改走 app:copyText', async () => {
    await render(FILE, false)
    for (const text of ['打开方式', '另存为…', '复制路径']) expect(() => item(text)).toThrow()
    await act(async () => item('复制相对路径').click())
    expect(copyText).toHaveBeenCalledWith('src/a.ts')
  })
})
