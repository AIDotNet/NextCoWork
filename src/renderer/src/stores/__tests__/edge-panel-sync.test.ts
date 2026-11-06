/**
 * 需求:右侧工作台 / 底部面板的开关,必须和「那一格里还有没有 Tab」是同一个事实。
 *
 * 空掉的那一格会被 `visibleDockNode` 当空组滤掉,画面上什么都没有;开关却还留着
 * true 的话,用户点「展开右侧工作区」要点两下 —— 第一下把这个没有画面的 true
 * 翻成 false(看上去毫无反应),第二下才真的展开。这个文件守住的就是这条同步,
 * 以及它的反面:先掀开面板、再 write 补 Tab 的那几条路径不能被这条同步抹掉。
 */
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { InnerTab } from '../../../../shared/domain/tab'

vi.mock('../../services/app', () => ({
  getInnerTabs: vi.fn(),
  persistInnerTabs: vi.fn(),
  persistOuterTabs: vi.fn()
}))

vi.mock('../../services/browser', () => ({
  closeBrowserTab: vi.fn(async () => undefined)
}))

import { useTabsStore } from '../tabs'
import { useWindowStore } from '../window'

const windowInitial = useWindowStore.getState()
const tabsInitial = useTabsStore.getState()

beforeEach(() => {
  vi.clearAllMocks()
  useWindowStore.setState(windowInitial, true)
  useTabsStore.setState(tabsInitial, true)
})

const chat: InnerTab = { id: 'chat', kind: 'chat', pane: 'main', title: 'Chat', ref: { sessionId: 'session' } }
const files: InnerTab = { id: 'files', kind: 'files', pane: 'right', title: 'Files', ref: { path: '' } }
const note: InnerTab = { id: 'note', kind: 'doc', pane: 'bottom', title: 'note.md', ref: { path: 'note.md' } }

describe('edge panel sync', () => {
  it('collapses the right workbench switch when its last tab is closed', () => {
    useWindowStore.setState({
      activeWorkspaceId: 'workspace-a',
      rightPanelOpen: true,
      rightPanelOpenByWorkspace: { 'workspace-a': true }
    })
    useTabsStore.getState().hydrate('workspace-a', { tabs: [chat, files], activeTabId: chat.id, rightActiveTabId: files.id })

    useTabsStore.getState().close('workspace-a', files.id)

    expect(useWindowStore.getState().rightPanelOpen).toBe(false)
    expect(useWindowStore.getState().rightPanelOpenByWorkspace['workspace-a']).toBe(false)
  })

  it('collapses the bottom panel switch when its last tab is closed', () => {
    useWindowStore.setState({
      activeWorkspaceId: 'workspace-a',
      bottomPanelOpen: true,
      bottomPanelOpenByWorkspace: { 'workspace-a': true }
    })
    useTabsStore.getState().hydrate('workspace-a', { tabs: [chat, note], activeTabId: chat.id, bottomActiveTabId: note.id })

    useTabsStore.getState().close('workspace-a', note.id)

    expect(useWindowStore.getState().bottomPanelOpen).toBe(false)
  })

  it('keeps a background workspace switch untouched when another workspace empties its right pane', () => {
    useWindowStore.setState({
      activeWorkspaceId: 'workspace-a',
      rightPanelOpen: true,
      rightPanelOpenByWorkspace: { 'workspace-a': true, 'workspace-b': true }
    })
    useTabsStore.getState().hydrate('workspace-b', { tabs: [chat, files], activeTabId: chat.id, rightActiveTabId: files.id })

    useTabsStore.getState().close('workspace-b', files.id)

    expect(useWindowStore.getState().rightPanelOpen).toBe(true)
    expect(useWindowStore.getState().rightPanelOpenByWorkspace['workspace-b']).toBe(false)
  })

  it('leaves the switch on when opening a file is what created the right pane', () => {
    useWindowStore.setState({ activeWorkspaceId: 'workspace-a', rightPanelOpen: false, rightPanelOpenByWorkspace: {} })
    useTabsStore.getState().hydrate('workspace-a', { tabs: [chat], activeTabId: chat.id })

    useTabsStore.getState().openPath('workspace-a', 'doc', 'README.md', 'README.md')

    expect(useWindowStore.getState().rightPanelOpen).toBe(true)
  })
})
