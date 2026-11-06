/**
 * 右键菜单的「滚动即关」只关**它指着的那块表面**的滚动。
 *
 * 钉的是 run 执行期间的一条回归:流式内容每长一段,`Thread` 的 follow() 就往
 * 聊天区写一次 scrollTop、派发一个 scroll 事件(见 views/chat/Thread.tsx 的
 * selfScrolled 注释)。那阵滚动和刚打开的标签右键菜单毫无关系,全当成「视口变了」
 * 的话,表现为「Agent 跑着的时候右键标签,菜单一闪就没」,且全程零报错。
 *
 * ★ 要真 jsdom 环境,**不能手搓 JSDOM** —— 理由见同目录 text-input-ime.test.ts
 *   的文件头。jsdom 里每个盒子都是 0×0,几何靠 stub `getBoundingClientRect`;
 *   rAF 一并 fake 掉,好让出场动画那条 `setShown` 也落在 act 里。
 *
 * @vitest-environment jsdom
 */
import { act, createElement } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ContextMenu } from '../ContextMenu'

/** 右键点下的那一点 —— 菜单就是指着它,判据也以它为准 */
const ANCHOR = { x: 100, y: 50 }

let root: Root | null = null
const onClose = vi.fn()

function stubBox(element: Element, box: { left: number; top: number; right: number; bottom: number }): void {
  vi.spyOn(element, 'getBoundingClientRect').mockReturnValue({
    ...box,
    x: box.left,
    y: box.top,
    width: box.right - box.left,
    height: box.bottom - box.top,
    toJSON: () => ({})
  })
}

/** 派发一次滚动并走完 150ms 的出场/退场计时 */
async function scroll(element: EventTarget): Promise<void> {
  await act(async () => {
    element.dispatchEvent(new Event('scroll'))
    vi.advanceTimersByTime(300)
  })
}

beforeEach(async () => {
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'requestAnimationFrame', 'cancelAnimationFrame'] })
  onClose.mockClear()
  const container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  await act(async () => {
    root?.render(createElement(ContextMenu, {
      position: ANCHOR,
      label: 'menu',
      onClose,
      children: (close: () => void) => createElement('button', {
        type: 'button',
        role: 'menuitem',
        onClick: close
      }, 'item')
    }))
  })
})

afterEach(async () => {
  await act(async () => root?.unmount())
  root = null
  document.body.replaceChildren()
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

describe('ContextMenu scroll dismissal', () => {
  it('keeps the menu open when a panel that does not contain the right-click point scrolls', async () => {
    // 聊天区那种「在别处自动滚动」的表面:盒子和右键点没有交集
    const unrelated = document.createElement('div')
    document.body.append(unrelated)
    stubBox(unrelated, { left: 400, top: 300, right: 900, bottom: 700 })

    await scroll(unrelated)

    expect(onClose).not.toHaveBeenCalled()
  })

  it('closes the menu when the surface under the right-click point scrolls', async () => {
    // Tab 条自己的横向滚动、文件树的滚动 —— 菜单指着的内容动了
    const surface = document.createElement('div')
    document.body.append(surface)
    stubBox(surface, { left: 0, top: 0, right: 300, bottom: 100 })

    await scroll(surface)

    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it('closes the menu when the page itself scrolls', async () => {
    // document / window 没有盒子,整页滚动维持原本的关闭行为
    await scroll(document)

    expect(onClose).toHaveBeenCalledTimes(1)
  })
})
