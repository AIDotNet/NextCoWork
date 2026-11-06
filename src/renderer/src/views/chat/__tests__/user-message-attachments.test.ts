/**
 * 需求：正文引用在气泡里保留，同时在气泡下与发送的图片/文件一起展示；
 * 草稿里只有上传完的图片可打开预览，不改变发送内容或附件的其他操作。
 * 超长正文的折叠、尺寸变化和编辑返回不得丢失文本或附件。
 *
 * @vitest-environment jsdom
 */
import { act, createElement, type ComponentProps, type ReactNode } from 'react'
import { createRoot } from 'react-dom/client'
import { JSDOM } from 'jsdom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { userMessage, type AgentMessage } from '../../../../../shared/agent/message'
import { emptyTranscript } from '../../../../../shared/agent/transcript'
import { I18nProvider } from '../../../i18n'
import { AttachmentTray, type TrayItem } from '../AttachmentTray'
import { Thread } from '../Thread'
import { userMessageFileRefs } from '../user-message-attachments'
import { copyText } from '../../../services/app'

vi.mock('../../../services/app', async (importOriginal) => ({
  ...await importOriginal<typeof import('../../../services/app')>(),
  copyText: vi.fn(async () => {})
}))

let cleanup: (() => Promise<void>) | null = null
let rerender: ((children: ReactNode) => Promise<void>) | null = null

afterEach(async () => {
  await cleanup?.()
  cleanup = null
  rerender = null
  vi.unstubAllGlobals()
})

async function render(children: ReactNode, prepare?: (dom: JSDOM) => void): Promise<HTMLElement> {
  const dom = new JSDOM('<div id="root"></div>', { url: 'http://localhost' })
  Object.assign(dom.window, { nextcowork: { on: () => () => {} } })
  vi.stubGlobal('window', dom.window)
  vi.stubGlobal('document', dom.window.document)
  vi.stubGlobal('HTMLElement', dom.window.HTMLElement)
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  vi.stubGlobal('requestAnimationFrame', () => 1)
  vi.stubGlobal('cancelAnimationFrame', () => {})
  vi.stubGlobal('ResizeObserver', class { observe(): void {} disconnect(): void {} })
  prepare?.(dom)
  const container = document.getElementById('root')!
  const root = createRoot(container)
  cleanup = async () => {
    await act(async () => root.unmount())
    dom.window.close()
  }
  rerender = async (next) => {
    await act(async () => root.render(createElement(I18nProvider, { initialLocale: 'zh-CN', children: next })))
  }
  await rerender(children)
  return container
}

const image = (name: string): TrayItem => ({
  key: name, name, status: 'done', attachment: {
    id: name, scope: 'session', ownerId: 's', displayName: name,
    mime: 'image/png', size: 1, checksum: name, createdAt: 1,
    url: `ncw://attachments/${name}`
  }
})

describe('用户消息附件', () => {
  it('按路径去重正文引用和 file_ref，排除网址与技能且不修改 parts', () => {
    const text = '先看 [a](src/a.ts) 和 [a](src/a.ts) [web](https://example.com) <skill name="test" /> [b](src/b.ts)'
    const parts = [
      { type: 'text' as const, text },
      { type: 'file_ref' as const, name: 'another a', path: 'src/a.ts' },
      { type: 'file_ref' as const, name: 'c', path: 'src/c.ts' }
    ]
    const original = JSON.stringify(parts)
    expect(userMessageFileRefs(text, parts)).toEqual([
      { name: 'a', path: 'src/a.ts' },
      { name: 'b', path: 'src/b.ts' },
      { name: 'c', path: 'src/c.ts' }
    ])
    expect(JSON.stringify(parts)).toBe(original)
  })

  it('纯图片消息没有空文字气泡，多张图片可预览和翻页', async () => {
    const messages = [userMessage('u', [
      { type: 'image', mime: 'image/png', dataRef: 'ncw://attachments/one' },
      { type: 'image', mime: 'image/png', dataRef: 'ncw://attachments/two' }
    ], 1)]
    const container = await render(createElement(Thread, {
      transcript: { ...emptyTranscript(), messages }, runId: null, lastSeq: 0,
      queued: 0, model: undefined, providerName: undefined
    }))
    expect(container.querySelector('[data-testid="user-message-bubble"]')).toBeNull()
    const attachments = container.querySelector('[data-testid="user-message-attachments"]')!
    expect(attachments.querySelectorAll('[data-testid="message-image"]')).toHaveLength(2)
    expect(attachments.querySelector('img')?.className).toContain('max-h-24')
    await act(async () => (attachments.querySelector('button') as HTMLButtonElement).click())
    expect(document.querySelector('[data-testid="lightbox-image"]')?.getAttribute('src')).toBe('ncw://attachments/one')
    await act(async () => window.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'ArrowRight' })))
    expect(document.querySelector('[data-testid="lightbox-image"]')?.getAttribute('src')).toBe('ncw://attachments/two')
  })

  it('正文保留引用 chip，下方只出现一张独立文件卡片', async () => {
    const messages = [userMessage('u', [
      { type: 'text', text: '看 [a](src/a.ts) [a](src/a.ts)' },
      { type: 'file_ref', path: 'src/a.ts', name: 'a' },
      { type: 'image', mime: 'image/png', dataRef: 'ncw://attachments/one' }
    ], 1)]
    const container = await render(createElement(Thread, {
      transcript: { ...emptyTranscript(), messages }, runId: null, lastSeq: 0,
      queued: 0, model: undefined, providerName: undefined
    }))
    const bubble = container.querySelector('[data-testid="user-message-bubble"]')!
    const attachments = container.querySelector('[data-testid="user-message-attachments"]')!
    expect(bubble.querySelectorAll('[data-testid="mention-chip"]')).toHaveLength(2)
    expect(bubble.querySelector('[data-testid="message-file-ref"]')).toBeNull()
    expect(attachments.querySelectorAll('[data-testid="message-file-ref"]')).toHaveLength(1)
    expect(attachments.querySelector('[data-testid="message-file-ref"]')?.tagName).toBe('DIV')
    expect(attachments.parentElement).toBe(bubble.parentElement)
  })
})

const longMessage = Array.from({ length: 24 }, (_, i) => `第 ${i + 1} 行内容`).join('\n')

function foldFixture(messages: AgentMessage[], props: Partial<ComponentProps<typeof Thread>> = {}): ReactNode {
  return createElement(Thread, {
    transcript: { ...emptyTranscript(), messages }, runId: null, lastSeq: 0,
    queued: 0, model: undefined, providerName: undefined, ...props
  })
}

function mockTextHeight(dom: JSDOM, height: () => number): void {
  Object.defineProperty(dom.window.HTMLElement.prototype, 'scrollHeight', {
    configurable: true,
    get() { return this.tagName === 'P' ? height() : 0 }
  })
}

describe('用户消息折叠', () => {
  it('短消息无需折叠，保留可读、可选择的正文', async () => {
    const container = await render(foldFixture([userMessage('u', [{ type: 'text', text: '短消息' }], 1)]),
      (dom) => mockTextHeight(dom, () => 44))
    const text = container.querySelector<HTMLElement>('[data-testid="user-message-text"]')!
    expect(text.textContent).toBe('短消息')
    expect(text.hasAttribute('inert')).toBe(false)
    expect(text.getAttribute('aria-hidden')).toBeNull()
    expect(container.querySelector('[data-testid="user-message-expand"]')).toBeNull()
    expect(container.querySelector('[data-testid="user-message-fade"]')).toBeNull()
  })

  it('超长正文默认折叠，展开及收起不卸载或截断原文和附件', async () => {
    const message = userMessage('u', [
      { type: 'text', text: longMessage },
      { type: 'file_ref', name: 'a.ts', path: 'src/a.ts' },
      { type: 'image', mime: 'image/png', dataRef: 'ncw://attachments/one' }
    ], 1)
    const container = await render(foldFixture([message], { readOnly: true }),
      (dom) => mockTextHeight(dom, () => 560))
    const text = container.querySelector<HTMLElement>('[data-testid="user-message-text"]')!
    const paragraph = text.querySelector('p')!
    const toggle = container.querySelector<HTMLButtonElement>('[data-testid="user-message-expand"]')!
    const fade = container.querySelector('[data-testid="user-message-fade"]')!
    const attachments = container.querySelector('[data-testid="user-message-attachments"]')!
    expect(toggle.getAttribute('aria-controls')).toBe(text.id)
    expect(toggle.getAttribute('aria-expanded')).toBe('false')
    expect(toggle.textContent).toBe('展开消息全文')
    expect(text.style.maxHeight).toBe('220px')
    expect(text.hasAttribute('inert')).toBe(false)
    expect(text.getAttribute('aria-hidden')).toBeNull()
    expect(paragraph.textContent).toBe(longMessage)
    expect(fade.className).toContain('opacity-100')
    expect(container.querySelector('[data-testid="user-message-edit"]')).toBeNull()

    await act(async () => toggle.click())
    expect(toggle.getAttribute('aria-expanded')).toBe('true')
    expect(toggle.textContent).toBe('收起消息')
    expect(text.style.maxHeight).toBe('560px')
    expect(fade.className).toContain('opacity-0')
    expect(text.querySelector('p')).toBe(paragraph)
    expect(container.querySelector('[data-testid="user-message-attachments"]')).toBe(attachments)

    await act(async () => toggle.click())
    expect(toggle.getAttribute('aria-expanded')).toBe('false')
    expect(text.style.maxHeight).toBe('220px')
    expect(fade.className).toContain('opacity-100')
    expect(paragraph.textContent).toBe(longMessage)
    expect(attachments.querySelectorAll('[data-testid="message-file-ref"]')).toHaveLength(1)
    expect(attachments.querySelectorAll('[data-testid="message-image"]')).toHaveLength(1)
  })

  it('重新测量窗口缩放后的正文高度，保留手动展开状态', async () => {
    let height = 90
    const resizeCallbacks: Array<() => void> = []
    const container = await render(foldFixture([userMessage('u', [{ type: 'text', text: longMessage }], 1)]), (dom) => {
      mockTextHeight(dom, () => height)
      vi.stubGlobal('ResizeObserver', class {
        constructor(callback: ResizeObserverCallback) {
          resizeCallbacks.push(() => callback([], this as unknown as ResizeObserver))
        }
        observe(): void {}
        disconnect(): void {}
      })
    })
    const text = container.querySelector<HTMLElement>('[data-testid="user-message-text"]')!
    expect(container.querySelector('[data-testid="user-message-expand"]')).toBeNull()
    height = 640
    await act(async () => resizeCallbacks.forEach((callback) => callback()))
    const toggle = container.querySelector<HTMLButtonElement>('[data-testid="user-message-expand"]')!
    expect(toggle.getAttribute('aria-expanded')).toBe('false')
    await act(async () => toggle.click())
    expect(text.style.maxHeight).toBe('640px')
    height = 960
    await act(async () => resizeCallbacks.forEach((callback) => callback()))
    expect(text.style.maxHeight).toBe('960px')
    expect(toggle.getAttribute('aria-expanded')).toBe('true')
    height = 150
    await act(async () => resizeCallbacks.forEach((callback) => callback()))
    expect(container.querySelector('[data-testid="user-message-expand"]')).toBeNull()
    expect(text.style.maxHeight).toBe('150px')
  })

  it('手动展开时暂停贴底，用户滚回底部后恢复跟随', async () => {
    let threadHeight = 430
    const resizeCallbacks: Array<() => void> = []
    const container = await render(foldFixture([userMessage('u', [{ type: 'text', text: longMessage }], 1)]), (dom) => {
      Object.defineProperty(dom.window.HTMLElement.prototype, 'scrollHeight', {
        configurable: true,
        get() { return this.tagName === 'P' ? 560 : this.dataset.testid === 'thread' ? threadHeight : 0 }
      })
      Object.defineProperty(dom.window.HTMLElement.prototype, 'clientHeight', {
        configurable: true,
        get() { return this.dataset.testid === 'thread' ? 480 : 0 }
      })
      vi.stubGlobal('ResizeObserver', class {
        constructor(callback: ResizeObserverCallback) {
          resizeCallbacks.push(() => callback([], this as unknown as ResizeObserver))
        }
        observe(): void {}
        disconnect(): void {}
      })
    })
    const viewport = container.querySelector<HTMLElement>('[data-testid="thread"]')!
    await act(async () => container.querySelector<HTMLButtonElement>('[data-testid="user-message-expand"]')!.click())
    threadHeight = 900
    await act(async () => resizeCallbacks.forEach((callback) => callback()))
    expect(viewport.scrollTop).toBe(0)
    await act(async () => {
      viewport.scrollTop = threadHeight - viewport.clientHeight
      viewport.dispatchEvent(new window.Event('scroll'))
    })
    threadHeight = 960
    await act(async () => resizeCallbacks.forEach((callback) => callback()))
    expect(viewport.scrollTop).toBe(480)
  })

  it('每条消息独立展开，控制目标不重复', async () => {
    const container = await render(foldFixture([
      userMessage('u1', [{ type: 'text', text: longMessage }], 1),
      userMessage('u2', [{ type: 'text', text: longMessage }], 2)
    ]), (dom) => mockTextHeight(dom, () => 560))
    const toggles = container.querySelectorAll<HTMLButtonElement>('[data-testid="user-message-expand"]')
    expect(toggles).toHaveLength(2)
    expect(toggles[0]!.getAttribute('aria-controls')).not.toBe(toggles[1]!.getAttribute('aria-controls'))
    await act(async () => toggles[0]!.click())
    expect(toggles[0]!.getAttribute('aria-expanded')).toBe('true')
    expect(toggles[1]!.getAttribute('aria-expanded')).toBe('false')
  })

  it('键盘进入正文文件引用时展开，避免聚焦到不可见内容', async () => {
    const container = await render(foldFixture([
      userMessage('u', [{ type: 'text', text: `${longMessage}\n看 [a](src/a.ts)` }], 1)
    ], { workspaceId: 'w' }), (dom) => mockTextHeight(dom, () => 560))
    const toggle = container.querySelector<HTMLButtonElement>('[data-testid="user-message-expand"]')!
    const chip = container.querySelector<HTMLButtonElement>('[data-testid="user-message-text"] [data-testid="mention-chip"]')!
    await act(async () => chip.focus())
    expect(document.activeElement).toBe(chip)
    expect(toggle.getAttribute('aria-expanded')).toBe('true')
    expect(container.querySelector<HTMLElement>('[data-testid="user-message-text"]')!.style.maxHeight).toBe('560px')
  })

  it('编辑时使用全文，取消后重新测量，内容更新后恢复默认折叠', async () => {
    const onEditMessage = vi.fn(async () => {})
    const message = userMessage('u', [{ type: 'text', text: longMessage }], 1)
    let height = 560
    const container = await render(foldFixture([message], { onEditMessage }),
      (dom) => mockTextHeight(dom, () => height))
    await act(async () => container.querySelector<HTMLButtonElement>('[data-testid="user-message-expand"]')!.click())
    await act(async () => container.querySelector<HTMLButtonElement>('[data-testid="user-message-edit"]')!.click())
    expect(container.querySelector<HTMLTextAreaElement>('[data-testid="user-message-editor"]')!.value).toBe(longMessage)
    height = 740
    await act(async () => Array.from(container.querySelectorAll<HTMLButtonElement>('button')).find((button) => button.textContent === '取消')!.click())
    expect(container.querySelector<HTMLElement>('[data-testid="user-message-text"]')!.style.maxHeight).toBe('740px')
    expect(container.querySelector('[data-testid="user-message-expand"]')!.getAttribute('aria-expanded')).toBe('true')
    await rerender!(foldFixture([{ ...message, parts: [{ type: 'text', text: `${longMessage}\n新增内容` }] }], { onEditMessage }))
    expect(container.querySelector('[data-testid="user-message-expand"]')!.getAttribute('aria-expanded')).toBe('false')
    expect(container.querySelector<HTMLElement>('[data-testid="user-message-text"]')!.style.maxHeight).toBe('220px')
    expect(container.querySelector('[data-testid="user-message-text"]')!.textContent).toBe(`${longMessage}\n新增内容`)
  })
})

describe('用户消息复制', () => {
  it('只读面板也能复制,复制的是原文而不是折叠预览', async () => {
    const container = await render(foldFixture([userMessage('u', [{ type: 'text', text: longMessage }], 1)], { readOnly: true }),
      (dom) => mockTextHeight(dom, () => 560))
    const button = container.querySelector<HTMLButtonElement>('[data-testid="user-message-copy"]')!
    expect(button.getAttribute('aria-label')).toBe('复制消息')
    await act(async () => button.click())
    expect(copyText).toHaveBeenCalledWith(longMessage)
    expect(button.getAttribute('aria-label')).toBe('已复制')
  })
})

describe('草稿图片预览', () => {
  it('上传完成图片可打开灯箱，删除按钮仍独立生效', async () => {
    const onRemove = vi.fn()
    const container = await render(createElement(AttachmentTray, {
      items: [image('one.png')], onRemove, onRetry: vi.fn()
    }))
    const preview = container.querySelector<HTMLButtonElement>('[aria-label="放大查看图片"]')
    expect(preview).not.toBeNull()
    await act(async () => preview!.click())
    expect(document.querySelector('[data-testid="lightbox-image"]')?.getAttribute('src')).toBe('ncw://attachments/one.png')
    await act(async () => (document.querySelector('[data-testid="lightbox-close"]') as HTMLButtonElement).click())
    expect(document.querySelector('[data-testid="image-lightbox"]')).toBeNull()
    await act(async () => (container.querySelector('[data-testid="attachment-remove"]') as HTMLButtonElement).click())
    expect(onRemove).toHaveBeenCalledWith('one.png')
  })

  it('未上传、上传失败及非图片不提供预览入口', async () => {
    const container = await render(createElement(AttachmentTray, {
      items: [
        { ...image('pending.png'), status: 'uploading' },
        { ...image('failed.png'), status: 'error', error: 'error' },
        { key: 'file', name: 'file.txt', status: 'done', path: '/file.txt' }
      ], onRemove: vi.fn(), onRetry: vi.fn()
    }))
    expect(container.querySelector('[data-status="uploading"] [aria-label="放大查看图片"]')).toBeNull()
    expect(container.querySelector('[data-status="error"] [aria-label="放大查看图片"]')).toBeNull()
    expect(container.querySelector('[data-status="done"] [aria-label="放大查看图片"]')).toBeNull()
  })
})
