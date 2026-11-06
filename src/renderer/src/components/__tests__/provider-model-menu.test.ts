/**
 * 模型选择弹层(`ProviderModelMenu`):供应商 → 模型两级菜单,Radix DropdownMenu 实现。
 *
 * 钉四件事,都是「不报错、只是选错 / 选不到」那一类:
 *
 * 1. **同一个别名在两家都有时,回调带的是被点的那一家。** 只回传别名的话,
 *    两家里的同名模型会被当成同一个。
 * 2. **当前供应商、当前模型都打勾**,别家的同名模型不打勾。
 * 3. **键盘能走完全程**:↓ 到供应商、→ 进子菜单、Enter 选中,不用碰鼠标。
 * 4. **`topItem`(「跟随对话」)在第一级就能直接选**,不进子菜单。
 *
 * @vitest-environment jsdom
 */
import { act, createElement } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ProviderModelMenu, type ProviderModelMenuRow } from '../ProviderModelMenu'

const ROWS: ProviderModelMenuRow[] = [
  {
    id: 'anthropic',
    label: 'Anthropic',
    description: '2 available models',
    selected: true,
    models: [
      { value: 'claude-sonnet', label: 'claude-sonnet', selected: true },
      { value: 'claude-haiku', label: 'claude-haiku', selected: false }
    ]
  },
  {
    id: 'relay',
    label: 'Internal relay',
    description: '2 available models',
    selected: false,
    models: [
      { value: 'claude-sonnet', label: 'claude-sonnet', selected: false },
      { value: 'gpt-5', label: 'gpt-5', selected: false }
    ]
  }
]

let root: Root | null = null
let container: HTMLElement

beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class {
    observe(): void {}
    unobserve(): void {}
    disconnect(): void {}
  })
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  container = document.createElement('div')
  document.body.append(container)
})

afterEach(() => {
  act(() => root?.unmount())
  root = null
  container.remove()
  vi.unstubAllGlobals()
})

interface Mounted {
  onSelectModel: ReturnType<typeof vi.fn>
  onTop: ReturnType<typeof vi.fn>
}

async function mount(withTop = false): Promise<Mounted> {
  const onSelectModel = vi.fn()
  const onTop = vi.fn()
  root = createRoot(container)
  await act(async () => root!.render(createElement(ProviderModelMenu, {
    trigger: 'claude-sonnet',
    menuLabel: 'Select a model provider',
    rows: ROWS,
    onSelectModel,
    ...(withTop ? { topItem: { label: 'Follow chat', selected: false, onSelect: onTop } } : {})
  })))
  return { onSelectModel, onTop }
}

const trigger = (): HTMLButtonElement => container.querySelector<HTMLButtonElement>('button[aria-label="Select a model provider"]')!
const items = (): HTMLElement[] => [...document.querySelectorAll<HTMLElement>('[role="menuitem"]')]
const item = (text: string, nth = 0): HTMLElement =>
  items().filter((el) => el.textContent?.trim().startsWith(text))[nth]!
/** 勾是 lucide 的 svg,没勾的那一格带 `invisible` 占位 */
const ticked = (el: HTMLElement): boolean => el.querySelector('svg.lucide-check:not(.invisible)') !== null

/** Radix 的触发器在 pointerdown 上开,不是 click */
async function open(): Promise<void> {
  await act(async () => {
    trigger().dispatchEvent(new MouseEvent('pointerdown', { bubbles: true, button: 0 }))
  })
}

async function press(key: string): Promise<void> {
  await act(async () => {
    document.activeElement?.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }))
  })
  // Radix 的方向键移焦点放在 setTimeout 里做,等它落地
  await act(async () => { await new Promise((resolve) => setTimeout(resolve, 0)) })
}

describe('ProviderModelMenu', () => {
  it('lists providers first and opens a provider’s models beside it', async () => {
    await mount()
    await open()
    expect(items().map((el) => el.textContent?.trim())).toEqual([
      'Anthropic2 available models',
      'Internal relay2 available models'
    ])
    expect(ticked(item('Anthropic'))).toBe(true)
    expect(ticked(item('Internal relay'))).toBe(false)

    await act(async () => item('Internal relay').click())
    const models = [...document.querySelectorAll<HTMLElement>('[role="menu"][data-side] [role="menuitem"]')]
      .filter((el) => el.closest('[role="menu"]')?.textContent?.startsWith('Internal relay') === true)
    expect(models.map((el) => el.title)).toEqual(['claude-sonnet', 'gpt-5'])
    // 别家的同名模型不打勾
    expect(ticked(models[0]!)).toBe(false)
  })

  it('reports the provider of the submenu the alias was picked from', async () => {
    const { onSelectModel } = await mount()
    await open()
    await act(async () => item('Internal relay').click())
    const relaySonnet = items().find((el) => el.title === 'claude-sonnet' && el.closest('[role="menu"]')?.textContent?.startsWith('Internal relay'))!
    await act(async () => relaySonnet.click())
    expect(onSelectModel).toHaveBeenCalledWith('relay', 'claude-sonnet')
    expect(trigger().getAttribute('aria-expanded')).toBe('false')
  })

  it('walks the whole way with the keyboard', async () => {
    const { onSelectModel } = await mount()
    await act(async () => {
      trigger().focus()
      trigger().dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
    })
    expect(trigger().getAttribute('aria-expanded')).toBe('true')
    await press('ArrowDown') // → Anthropic
    await press('ArrowDown') // → Internal relay
    expect(document.activeElement?.textContent).toContain('Internal relay')
    await press('ArrowRight') // 进子菜单,焦点落在第一项
    expect((document.activeElement as HTMLElement | null)?.title).toBe('claude-sonnet')
    await press('ArrowDown')
    await press('Enter')
    expect(onSelectModel).toHaveBeenCalledWith('relay', 'gpt-5')
  })

  it('selects the top item straight from the first level', async () => {
    const { onTop, onSelectModel } = await mount(true)
    await open()
    await act(async () => item('Follow chat').click())
    expect(onTop).toHaveBeenCalledOnce()
    expect(onSelectModel).not.toHaveBeenCalled()
  })
})
