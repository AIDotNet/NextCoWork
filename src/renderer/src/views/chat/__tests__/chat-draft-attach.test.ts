import { act, createElement, useEffect, type ComponentProps, type ReactNode } from 'react'
import { createRoot } from 'react-dom/client'
import { JSDOM } from 'jsdom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { DEFAULT_WORKSPACE_SETTINGS, type Workspace } from '../../../../../shared/domain/workspace'
import { chatKey } from '../../../../../shared/domain/tab'
import { I18nProvider } from '../../../i18n'
import { pickAttachments, uploadFile } from '../../../services/attachment'
import { useModelsStore } from '../../../stores/models'
import { releaseSession } from '../../../stores/session'
import { useTabsStore } from '../../../stores/tabs'
import { ChatView } from '../ChatView'
import type { Composer } from '../Composer'

const hoisted = vi.hoisted(() => ({ composerMounts: { count: 0 } }))

vi.mock('../../../services/app', async (importOriginal) => ({
  ...await importOriginal<typeof import('../../../services/app')>(),
  getInnerTabs: vi.fn(async () => ({ tabs: [], activeTabId: null })),
  persistInnerTabs: vi.fn(),
  persistOuterTabs: vi.fn(),
  getSessionInput: vi.fn(async () => null),
  persistSessionDraft: vi.fn(),
  updateWorkspace: vi.fn()
}))
vi.mock('../../../services/attachment', () => ({
  listSessionAttachments: vi.fn(async () => []),
  pickAttachments: vi.fn(async () => [{ kind: 'path', path: '/workspace/picked.txt', name: 'picked.txt' }]),
  removeAttachment: vi.fn(),
  uploadFile: vi.fn(async () => ({ id: 'shot', scope: 'session', ownerId: 'owner', displayName: 'shot.png',
    mime: 'image/png', size: 6, checksum: 'fixture', createdAt: 1, url: 'ncw://attachments/sessions/owner/shot.png' }))
}))
vi.mock('../Composer', () => ({
  Composer: (props: ComponentProps<typeof Composer>) => {
    // 重挂计数:输入框被重建 = 用户正在打的字、光标、托盘全部作废
    useEffect(() => { hoisted.composerMounts.count++ }, [])
    return createElement('div', null,
      createElement('button', { 'data-testid': 'paste-image', onClick: () => props.onAttachFiles?.([
        new File(['pixels'], 'shot.png', { type: 'image/png' })
      ]) }),
      createElement('button', { 'data-testid': 'pick', onClick: props.onPickAttachment }),
      createElement('output', null, props.attachments?.length ?? 0),
      createElement('span', { 'data-testid': 'statuses' }, props.attachments?.map((item) => item.status).join('|')))
  }
}))

const workspace: Workspace = { id: 'workspace', name: 'Workspace', rootPath: '/workspace',
  createdAt: 1, lastOpenedAt: 1, settings: { ...DEFAULT_WORKSPACE_SETTINGS } }

/**
 * `views/registry.tsx` 的那一层。**key 的取法就是被测的东西**:挂 `tab.id` 时
 * 草稿铸出 sessionId 不会换树,挂 `chatKey(tab)` 时整棵 ChatView 被卸载重挂。
 */
function harnessWith(keyOf: (tab: Extract<ReturnType<typeof draftTab>, object> & { id: string }) => string): () => ReactNode {
  return function Harness(): ReactNode {
    const tabs = useTabsStore((state) => state.stateOf(workspace.id).tabs)
    const tab = tabs.find((item) => item.kind === 'chat')
    if (tab === undefined || tab.kind !== 'chat') return null
    return createElement(ChatView, {
      key: keyOf({ id: tab.id, sessionId: tab.ref.sessionId }),
      sessionId: tab.ref.sessionId,
      tabId: tab.id,
      workspace,
      fallbackModel: { model: '' }
    })
  }
}

function mount(): { container: HTMLElement; root: ReturnType<typeof createRoot>; dom: JSDOM } {
  const dom = new JSDOM('<div id="root"></div>', { url: 'http://localhost' })
  Object.assign(dom.window, { nextcowork: {
    getPathForFile: () => '/workspace/dropped.txt',
    on: () => () => {},
    invoke: async (channel: string) => ({ ok: true, data: channel === 'agent:listInteractions' ? []
      : channel === 'goal:get' ? undefined
        : { messages: [], session: { id: 'draft', workspaceId: 'workspace', model: '', mode: 'code' } } })
  } })
  vi.stubGlobal('window', dom.window)
  vi.stubGlobal('document', dom.window.document)
  vi.stubGlobal('HTMLElement', dom.window.HTMLElement)
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  vi.stubGlobal('requestAnimationFrame', () => 1)
  vi.stubGlobal('cancelAnimationFrame', () => {})
  vi.stubGlobal('ResizeObserver', class { observe(): void {} disconnect(): void {} })
  hoisted.composerMounts.count = 0
  const container = dom.window.document.getElementById('root') as unknown as HTMLElement
  return { container, root: createRoot(container), dom }
}

function draftTab(): { id: string; sessionId: string | null } {
  useTabsStore.getState().open(workspace.id, 'chat')
  const tab = useTabsStore.getState().stateOf(workspace.id).tabs.find((item) => item.kind === 'chat')!
  return { id: tab.id, sessionId: tab.kind === 'chat' ? tab.ref.sessionId : null }
}

function boundSessionId(tabId: string): string | null {
  const tab = useTabsStore.getState().stateOf(workspace.id).tabs.find((item) => item.id === tabId)
  return tab?.kind === 'chat' ? tab.ref.sessionId : null
}

afterEach(() => {
  for (const tab of useTabsStore.getState().stateOf(workspace.id).tabs) {
    releaseSession(chatKey(tab as Extract<typeof tab, { kind: 'chat' }>))
  }
  useTabsStore.setState({ byWorkspace: {} })
  vi.clearAllMocks()
})

describe('pasting into a fresh conversation', () => {
  it('binds the session without rebuilding the view, and keeps the pasted image', async () => {
    const { container, root, dom } = mount()
    const models = useModelsStore.getState()
    useModelsStore.setState({ loaded: true })
    const tab = draftTab()
    expect(tab.sessionId).toBeNull()
    try {
      // registry 现在的写法:key 挂 tab.id
      await act(async () => root.render(createElement(I18nProvider, {
        initialLocale: 'en-US', children: createElement(harnessWith((t) => t.id))
      })))
      expect(hoisted.composerMounts.count).toBe(1)
      await act(async () => (container.querySelector('[data-testid="paste-image"]') as HTMLButtonElement).click())

      expect(boundSessionId(tab.id)).not.toBeNull()
      // ★ 输入框没有被重建 —— 用户正在打的字、光标和焦点都还在
      expect(hoisted.composerMounts.count).toBe(1)
      expect(container.querySelector('output')?.textContent).toBe('1')
      expect(container.querySelector('[data-testid="statuses"]')?.textContent).toBe('done')
      expect(uploadFile).toHaveBeenCalledTimes(1)
      expect(uploadFile).toHaveBeenCalledWith(
        expect.objectContaining({ name: 'shot.png' }), 'session', boundSessionId(tab.id)
      )
    } finally {
      await act(async () => root.unmount())
      useModelsStore.setState(models, true)
      dom.window.close()
      vi.unstubAllGlobals()
    }
  })

  it('still lands the image if the view is rebuilt on binding', async () => {
    const { container, root, dom } = mount()
    const models = useModelsStore.getState()
    useModelsStore.setState({ loaded: true })
    const tab = draftTab()
    try {
      // 旧 key 的取法,留作回归:重建发生时 `draft-handoff` 必须把这批文件接住
      await act(async () => root.render(createElement(I18nProvider, {
        initialLocale: 'en-US', children: createElement(harnessWith((t) => t.sessionId ?? t.id))
      })))
      await act(async () => (container.querySelector('[data-testid="paste-image"]') as HTMLButtonElement).click())

      expect(hoisted.composerMounts.count).toBe(2) // 确实重建了
      expect(container.querySelector('output')?.textContent).toBe('1')
      expect(container.querySelector('[data-testid="statuses"]')?.textContent).toBe('done')
      expect(uploadFile).toHaveBeenCalledTimes(1)
      expect(uploadFile).toHaveBeenCalledWith(
        expect.objectContaining({ name: 'shot.png' }), 'session', boundSessionId(tab.id)
      )
    } finally {
      await act(async () => root.unmount())
      useModelsStore.setState(models, true)
      dom.window.close()
      vi.unstubAllGlobals()
    }
  })

  it('opens the file dialog once, after the draft has an id to upload into', async () => {
    const { container, root, dom } = mount()
    const models = useModelsStore.getState()
    useModelsStore.setState({ loaded: true })
    draftTab()
    try {
      await act(async () => root.render(createElement(I18nProvider, {
        initialLocale: 'en-US', children: createElement(harnessWith((t) => t.id))
      })))
      await act(async () => (container.querySelector('[data-testid="pick"]') as HTMLButtonElement).click())
      expect(pickAttachments).toHaveBeenCalledTimes(1)
      expect(container.querySelector('output')?.textContent).toBe('1')
    } finally {
      await act(async () => root.unmount())
      useModelsStore.setState(models, true)
      dom.window.close()
      vi.unstubAllGlobals()
    }
  })
})
