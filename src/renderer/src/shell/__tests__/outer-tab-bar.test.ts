/**
 * 外层 Tab 条的键盘操作与尾部槽位。
 *
 * 钉的都是「肉眼看不出、但用起来就不对」的东西:
 *
 * 1. **只有一张 Tab 在 Tab 键序里**(roving tabindex),方向键在 Tab 之间挪焦点;
 *    **挪焦点不切换工作区**,Enter 才切 —— 自动激活的话方向键扫过一排就全打开一遍。
 * 2. **Delete 关闭、F2 改名**,而且只在焦点就在 Tab 本身上时生效:改名输入框里按
 *    Backspace 删字,不能把整张 Tab 关掉。
 * 3. **尾部只有一个槽**:未激活、没在运行的 Tab 不再给关闭按钮留一格空白;
 *    × 不进 Tab 键序(键盘上关闭走 Delete)。
 *
 * @vitest-environment jsdom
 */
import { act, createElement } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { OuterTab } from '../../../../shared/domain/tab'
import type { Workspace } from '../../../../shared/domain/workspace'
import { I18nProvider } from '../../i18n'
import { OuterTabBar } from '../OuterTabBar'

const ws = (id: string, name: string): Workspace => ({ id, name, rootPath: `/tmp/${id}` }) as Workspace
const tab = (id: string): OuterTab => ({ id, kind: 'workspace', ref: { workspaceId: id } })

let root: Root | null = null
let container: HTMLElement

beforeEach(() => {
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  vi.stubGlobal('ResizeObserver', class {
    observe(): void {}
    unobserve(): void {}
    disconnect(): void {}
  })
  container = document.createElement('div')
  document.body.append(container)
})

afterEach(() => {
  act(() => root?.unmount())
  root = null
  container.remove()
  vi.unstubAllGlobals()
})

interface Handlers {
  onActivate: ReturnType<typeof vi.fn>
  onClose: ReturnType<typeof vi.fn>
  onRenameWorkspace: ReturnType<typeof vi.fn>
}

async function mount(running: string[] = []): Promise<Handlers> {
  const handlers = { onActivate: vi.fn(), onClose: vi.fn(), onRenameWorkspace: vi.fn() }
  root = createRoot(container)
  await act(async () => root!.render(createElement(I18nProvider, {
    initialLocale: 'en-US',
    children: createElement(OuterTabBar, {
      tabs: [tab('a'), tab('b'), tab('c')],
      activeId: 'a',
      activeWorkspaceId: 'a',
      workspaces: [ws('a', 'Alpha'), ws('b', 'Beta'), ws('c', 'Gamma')],
      runningWorkspaceIds: new Set(running),
      onTogglePin: vi.fn(),
      onMove: vi.fn(),
      onOpenWorkspace: vi.fn(),
      onEditWorkspace: vi.fn(),
      onPickWorkspace: vi.fn(),
      onCreateWorkspace: vi.fn(),
      onCreateSshWorkspace: vi.fn(),
      rightPanelOpen: false,
      bottomPanelOpen: false,
      onToggleRightPanel: vi.fn(),
      onToggleBottomPanel: vi.fn(),
      ...handlers
    })
  })))
  return handlers
}

const tabEl = (id: string): HTMLElement => container.querySelector<HTMLElement>(`[data-outer-tab-id="${id}"]`)!

async function press(key: string): Promise<void> {
  await act(async () => {
    document.activeElement?.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }))
  })
  // Radix 的方向键移焦点放在 setTimeout 里做
  await act(async () => { await new Promise((resolve) => setTimeout(resolve, 0)) })
}

describe('OuterTabBar · keyboard', () => {
  it('exposes a tablist with exactly one tab in the tab order', async () => {
    await mount()
    expect(container.querySelector('[role="tablist"]')).not.toBeNull()
    expect(tabEl('a').getAttribute('role')).toBe('tab')
    expect(tabEl('a').getAttribute('aria-selected')).toBe('true')
    // Radix 的做法:Tab 键停在 tablist 上(tabIndex 0),它再把焦点转给当前那张;每张 Tab 都是 -1。
    // 不管停在哪,整条只占键序里的一格。
    const list = container.querySelector<HTMLElement>('[role="tablist"]')!
    const stops = [list, ...['a', 'b', 'c'].map(tabEl)].filter((el) => el.tabIndex === 0)
    expect(stops).toHaveLength(1)
    // 关闭按钮不进键序
    expect([...container.querySelectorAll<HTMLButtonElement>('button[aria-label^="Close"]')].every((b) => b.tabIndex === -1)).toBe(true)
  })

  it('moves focus with arrows without switching, and switches on Enter', async () => {
    const { onActivate } = await mount()
    await act(async () => tabEl('a').focus())
    await press('ArrowRight')
    expect(document.activeElement).toBe(tabEl('b'))
    expect(onActivate).not.toHaveBeenCalled()
    await press('Enter')
    expect(onActivate).toHaveBeenCalledWith('b')
  })

  it('closes with Delete and renames with F2 only when the tab itself has focus', async () => {
    const { onClose } = await mount()
    await act(async () => tabEl('b').focus())
    await press('Delete')
    expect(onClose).toHaveBeenCalledWith('b')
    // 关闭后焦点在下一帧落到相邻那张,等它落完再接着测
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 50)) })

    await act(async () => tabEl('a').focus())
    await press('F2')
    const input = tabEl('a').querySelector('input')
    expect(input).not.toBeNull()
    await act(async () => input!.focus())
    await press('Backspace')
    expect(onClose).toHaveBeenCalledTimes(1)
  })
})

describe('OuterTabBar · trailing slot', () => {
  it('keeps one slot on the active tab and lets the spinner give way to the close button', async () => {
    await mount(['a'])
    const active = tabEl('a')
    // 运行中:转圈和 × 叠在同一格里,而不是并排两格
    const slot = active.querySelector('button[aria-label="Close Alpha"]')!.parentElement!
    expect(slot.querySelector('[role="status"], svg')).not.toBeNull()
    expect(slot.className).toContain('size-[18px]')
  })

  it('reserves no slot on idle inactive tabs: the close button is an overlay', async () => {
    await mount()
    const overlay = tabEl('b').querySelector('button[aria-label="Close Beta"]')!.parentElement!
    expect(overlay.className).toContain('absolute')
    expect(overlay.className).toContain('opacity-0')
  })
})
