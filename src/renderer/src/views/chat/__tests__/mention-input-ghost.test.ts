/**
 * 输入框灰字落在 DOM 上的那两件事:`data-ghost` 属性、以及 Tab 接受时怎么写回草稿。
 *
 * ★ 灰字是**根节点上的一个属性**、由 CSS `::after` 画出来 —— 不是子节点。这里钉住的就是
 *   「它不进 `readDraft`」:一旦哪天有人把它改成插一个 `<span>`,草稿就会把灰字当成用户
 *   打的字发出去。
 *
 * @vitest-environment jsdom
 */
import { act, createElement, createRef } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { MentionInput, type MentionInputHandle } from '../MentionInput'
import { readDraft } from '../rich-draft'

interface Harness {
  el: HTMLElement
  handle: MentionInputHandle
  changes: string[]
  render: (value: string, ghost?: string) => Promise<void>
}

let root: Root | null = null

afterEach(() => {
  act(() => root?.unmount())
  root = null
  document.body.replaceChildren()
  vi.unstubAllGlobals()
})

async function mount(initial: string, ghost?: string): Promise<Harness> {
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  const container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  const handle = createRef<MentionInputHandle | null>() as { current: MentionInputHandle | null }
  const changes: string[] = []
  const render = async (value: string, g?: string): Promise<void> => {
    await act(async () => {
      root?.render(createElement(MentionInput, {
        value,
        ghost: g,
        onChange: (text: string) => { changes.push(text) },
        onCaret: () => {},
        onKeyDown: () => {},
        onPaste: () => {},
        onBlur: () => {},
        onComposing: () => {},
        placeholder: 'p',
        metrics: '',
        handle,
        'aria-expanded': false,
        'aria-controls': undefined,
        'aria-activedescendant': undefined
      }))
    })
  }
  await render(initial, ghost)
  const el = container.querySelector<HTMLElement>('[data-testid="composer-input"]')!
  return { el, handle: handle.current!, changes, render }
}

describe('data-ghost', () => {
  it('非空草稿时挂在根节点上,不进 readDraft', async () => {
    const h = await mount('给 update-service 补', '上单元测试')
    expect(h.el.getAttribute('data-ghost')).toBe('上单元测试')
    expect(h.el.hasAttribute('data-composer-input')).toBe(true)
    expect(readDraft(h.el)).toBe('给 update-service 补')
    await h.render('给 update-service 补')
    expect(h.el.hasAttribute('data-ghost')).toBe(false)
  })

  it('空草稿时不挂(空草稿的灰字画在占位符层)', async () => {
    const h = await mount('', '跑一下测试')
    expect(h.el.hasAttribute('data-ghost')).toBe(false)
  })

  it('关掉系统自带的输入预测,免得两层灰字叠在一起', async () => {
    const h = await mount('')
    expect(h.el.getAttribute('writingsuggestions')).toBe('false')
  })
})

describe('acceptGhost', () => {
  it('没有 execCommand(jsdom)时退回整段重写,草稿与光标一次摆对', async () => {
    const h = await mount('给 update-service 补')
    await act(async () => h.handle.acceptGhost('上单元测试'))
    expect(readDraft(h.el)).toBe('给 update-service 补上单元测试')
    expect(h.changes.at(-1)).toBe('给 update-service 补上单元测试')
  })

  it('★ 纯文本走原生插入(进撤销栈);空草稿先换掉撑行高的 <br>', async () => {
    const h = await mount('')
    expect(h.el.querySelector('br')).not.toBeNull()
    const exec = vi.fn((_cmd: string, _ui: boolean, text: string) => {
      // 模拟浏览器:把字插进光标所在的文本节点,然后发 input
      const node = h.el.firstChild
      if (!(node instanceof Text)) return false
      node.nodeValue = (node.nodeValue ?? '') + text
      h.el.dispatchEvent(new Event('input', { bubbles: true }))
      return true
    })
    Object.defineProperty(document, 'execCommand', { value: exec, configurable: true })
    try {
      await act(async () => h.handle.acceptGhost('跑一下测试'))
      expect(exec).toHaveBeenCalledWith('insertText', false, '跑一下测试')
      expect(h.el.querySelector('br')).toBeNull()
      expect(readDraft(h.el)).toBe('跑一下测试')
    } finally {
      Reflect.deleteProperty(document, 'execCommand')
    }
  })

  it('会长出 chip 的文字不走原生插入(反正要重画)', async () => {
    const h = await mount('看看 ')
    const exec = vi.fn(() => true)
    Object.defineProperty(document, 'execCommand', { value: exec, configurable: true })
    try {
      await act(async () => h.handle.acceptGhost('[a.ts](src/a.ts)'))
      expect(exec).not.toHaveBeenCalled()
      expect(h.el.querySelector('[data-mention]')).not.toBeNull()
    } finally {
      Reflect.deleteProperty(document, 'execCommand')
    }
  })
})
