import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { InnerTab, OuterTab } from '../../../../shared/domain/tab'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../../../shared/domain/workspace'

vi.mock('../../services/app', () => ({
  getInnerTabs: vi.fn(),
  persistInnerTabs: vi.fn(),
  persistOuterTabs: vi.fn()
}))

vi.mock('../../services/browser', () => ({
  closeBrowserTab: vi.fn(async () => undefined)
}))

import { getInnerTabs, persistInnerTabs } from '../../services/app'
import { useTabsStore } from '../tabs'
import { useWindowStore } from '../window'

const windowInitial = useWindowStore.getState()
const tabsInitial = useTabsStore.getState()

beforeEach(() => {
  vi.clearAllMocks()
  useWindowStore.setState(windowInitial, true)
  useWindowStore.getState().updateWorkspaces(['workspace-a', 'workspace-b'].map((id) => ({ id, name: id, rootPath: `/tmp/${id}`, settings: DEFAULT_WORKSPACE_SETTINGS, createdAt: 1, lastOpenedAt: 1 })))
  useTabsStore.setState(tabsInitial, true)
})

const workspaceTab = (id: string, workspaceId: string): OuterTab => ({
  id,
  kind: 'workspace',
  ref: { workspaceId }
})

const chatTab = (id: string): InnerTab => ({
  id,
  kind: 'chat',
  pane: 'main',
  title: 'Chat',
  ref: { sessionId: `session-${id}` }
})

describe('browser workspace isolation', () => {
  it('后台工作区请求展开右侧面板时，不改变当前工作区；切过去后恢复展开态', () => {
    useWindowStore.setState({
      outer: [workspaceTab('outer-a', 'workspace-a'), workspaceTab('outer-b', 'workspace-b')],
      activeOuterId: 'outer-a',
      activeWorkspaceId: 'workspace-a',
      rightPanelOpen: false,
      rightPanelOpenByWorkspace: {}
    })

    useWindowStore.getState().setRightPanelForWorkspace('workspace-b', true)

    expect(useWindowStore.getState().activeWorkspaceId).toBe('workspace-a')
    expect(useWindowStore.getState().rightPanelOpen).toBe(false)
    expect(useWindowStore.getState().rightPanelOpenByWorkspace['workspace-b']).toBe(true)

    useWindowStore.getState().activate('outer-b')

    expect(useWindowStore.getState().activeWorkspaceId).toBe('workspace-b')
    expect(useWindowStore.getState().rightPanelOpen).toBe(true)
  })

  it('Agent 标签只写入目标工作区，并成为该工作区右侧面板的活动标签', () => {
    const a = chatTab('chat-a')
    const b = chatTab('chat-b')
    const files: InnerTab = {
      id: 'files-b',
      kind: 'files',
      pane: 'right',
      title: 'Files',
      ref: { path: '' }
    }
    useTabsStore.getState().hydrate('workspace-a', { tabs: [a], activeTabId: a.id })
    useTabsStore.getState().hydrate('workspace-b', {
      tabs: [b, files],
      activeTabId: b.id,
      rightActiveTabId: files.id
    })
    // 落一份 Dock 快照 —— 有快照以后 legacy 的 rightActiveTabId 就不再是权威，
    // 这正是真实运行时的状态，也是这条用例要守住的分支。
    useTabsStore.getState().activate('workspace-b', b.id)

    useTabsStore.getState().syncBrowserTabs('workspace-b', [{
      id: 'remote-agent-tab',
      url: 'https://example.com/',
      title: 'Example',
      source: 'agent'
    }])

    expect(useTabsStore.getState().stateOf('workspace-a').tabs).toEqual([a])
    const target = useTabsStore.getState().stateOf('workspace-b')
    const browser = target.tabs.find((tab) => tab.kind === 'browser')
    expect(browser).toMatchObject({
      kind: 'browser',
      pane: 'right',
      ref: { browserId: 'remote-agent-tab' }
    })
    expect(target.rightActiveTabId).toBe(browser?.id)
    // 右侧那一组默认停在「工作区文件」上；Agent 打开的页面必须顶到前面，
    // 否则右侧面板掀开后看到的还是文件树。
    const dock = useTabsStore.getState().dockOf('workspace-b')
    const group = dock.root.type === 'split' ? dock.root.second : dock.root
    expect(group.type === 'group' && group.activeTabId).toBe(browser?.id)
  })
})

describe('专注窗口的浏览器事件隔离', () => {
  beforeEach(() => {
    useWindowStore.setState({ windowKind: 'quick', activeWorkspaceId: 'workspace-a' })
    useTabsStore.getState().openFocusedSession('workspace-a', 'focused-session', '目标会话')
  })

  it('早到或其他项目的广播不会读取主窗口布局', () => {
    const before = useTabsStore.getState().stateOf('workspace-a')
    useTabsStore.getState().syncBrowserTabs('workspace-b', [{
      id: 'other-project-browser', ownerSessionId: 'focused-session',
      source: 'agent', url: 'https://example.com/', title: 'Other project'
    }])
    expect(useTabsStore.getState().byWorkspace['workspace-b']).toBeUndefined()
    expect(useTabsStore.getState().stateOf('workspace-a')).toBe(before)
    expect(getInnerTabs).not.toHaveBeenCalled()
    expect(persistInnerTabs).not.toHaveBeenCalled()
  })

  it('同项目其他会话的页面也不进入当前窗口', () => {
    const before = useTabsStore.getState().stateOf('workspace-a')
    useTabsStore.getState().syncBrowserTabs('workspace-a', [{
      id: 'other-session-browser', ownerSessionId: 'other-session',
      source: 'agent', url: 'https://example.com/', title: 'Other session'
    }, {
      id: 'main-window-browser', source: 'user', clientTabId: 'another-window-tab',
      url: 'https://example.com/', title: 'Main window'
    }])
    expect(useTabsStore.getState().stateOf('workspace-a')).toBe(before)
    expect(getInnerTabs).not.toHaveBeenCalled()
    expect(persistInnerTabs).not.toHaveBeenCalled()
  })

  it('目标会话自己的页面仍能显示在右侧，不抢走对话焦点', () => {
    const before = useTabsStore.getState().stateOf('workspace-a')
    useTabsStore.getState().syncBrowserTabs('workspace-a', [{
      id: 'own-browser', ownerSessionId: 'focused-session', source: 'agent',
      url: 'https://example.com/', title: 'Own page'
    }])
    const current = useTabsStore.getState().stateOf('workspace-a')
    expect(current.tabs.filter((tab) => tab.kind === 'browser')).toEqual([expect.objectContaining({
      pane: 'right', ref: { browserId: 'own-browser', url: 'https://example.com/' }
    })])
    expect(current.activeTabId).toBe(before.activeTabId)
    expect(persistInnerTabs).not.toHaveBeenCalled()

    useTabsStore.getState().syncBrowserTabs('workspace-a', [])
    expect(useTabsStore.getState().stateOf('workspace-a').tabs).toEqual(before.tabs)
  })

  it('本窗口手动打开的页面按 clientTabId 合并，不引入主窗口页面', () => {
    const tabs = useTabsStore.getState()
    const chat = tabs.stateOf('workspace-a').tabs[0]!
    const browser: InnerTab = { id: 'own-client-tab', kind: 'browser', pane: 'bottom', title: 'New page', ref: { url: '' } }
    tabs.hydrate('workspace-a', { tabs: [chat, browser], activeTabId: chat.id, bottomActiveTabId: browser.id })
    tabs.syncBrowserTabs('workspace-a', [{
      id: 'own-user-browser', clientTabId: browser.id, source: 'user',
      url: 'https://example.com/', title: 'Own user page'
    }, {
      id: 'other-user-browser', clientTabId: 'other-client-tab', source: 'user',
      url: 'https://example.com/other', title: 'Other user page'
    }])
    expect(tabs.stateOf('workspace-a').tabs.filter((tab) => tab.kind === 'browser')).toEqual([expect.objectContaining({
      id: browser.id, pane: 'bottom', ref: { browserId: 'own-user-browser', url: 'https://example.com/' }
    })])
    expect(persistInnerTabs).not.toHaveBeenCalled()
  })
})

describe('browser tab reconciliation', () => {
  it('用 clientTabId 将主进程事件合并回原标签，不按 URL 猜测或创建副本', () => {
    const chat = chatTab('chat')
    const optimistic: InnerTab = {
      id: 'client-browser-tab',
      kind: 'browser',
      pane: 'bottom',
      title: 'New tab',
      ref: { url: '' }
    }
    useTabsStore.getState().hydrate('workspace-a', {
      tabs: [chat, optimistic],
      activeTabId: chat.id,
      bottomActiveTabId: optimistic.id
    })

    useTabsStore.getState().syncBrowserTabs('workspace-a', [{
      id: 'remote-user-tab',
      clientTabId: optimistic.id,
      url: 'https://example.com/',
      title: 'Example',
      source: 'user'
    }])

    const browserTabs = useTabsStore.getState().stateOf('workspace-a').tabs.filter(
      (tab) => tab.kind === 'browser'
    )
    expect(browserTabs).toHaveLength(1)
    expect(browserTabs[0]).toMatchObject({
      id: optimistic.id,
      pane: 'bottom',
      ref: { browserId: 'remote-user-tab', url: 'https://example.com/' }
    })
  })

  it('远端关闭活动浏览器后修复活动项，并保证主区仍有可用标签', () => {
    const browser: InnerTab = {
      id: 'browser-only',
      kind: 'browser',
      pane: 'main',
      title: 'Example',
      ref: { url: 'https://example.com/', browserId: 'remote-old' }
    }
    useTabsStore.getState().hydrate('workspace-a', {
      tabs: [browser],
      activeTabId: browser.id
    })

    useTabsStore.getState().syncBrowserTabs('workspace-a', [])

    const state = useTabsStore.getState().stateOf('workspace-a')
    expect(state.tabs.some((tab) => tab.id === browser.id)).toBe(false)
    expect(state.tabs).toHaveLength(1)
    expect(state.tabs[0]?.kind).toBe('chat')
    expect(state.activeTabId).toBe(state.tabs[0]?.id)
  })
})
