import { beforeEach, describe, expect, it, vi } from 'vitest'

const persisted = vi.hoisted(() => new Map<string, unknown>())

vi.mock('../../state/store', () => ({
  store: {
    getWorkspace: vi.fn(),
    getKv: vi.fn((key: string, fallback: unknown) => persisted.get(key) ?? fallback),
    setKv: vi.fn((key: string, value: unknown) => persisted.set(key, value))
  }
}))

import { assertLocalBrowserWorkspace, BrowserManager } from '../manager'
import { store } from '../../state/store'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../../shared/domain/workspace'

beforeEach(() => persisted.clear())

describe('BrowserManager · workspace and run isolation', () => {
  it('rejects remote and unknown workspace browser requests before opening local resources', () => {
    vi.mocked(store.getWorkspace).mockReturnValue(undefined)
    expect(() => assertLocalBrowserWorkspace('missing')).toThrow('unbound')
    const workspace = { id: 'workspace', name: 'workspace', rootPath: '/project', settings: DEFAULT_WORKSPACE_SETTINGS, createdAt: 1, lastOpenedAt: 1 }
    vi.mocked(store.getWorkspace).mockReturnValue({ ...workspace, environment: { kind: 'connection', connectionId: 'server' } })
    expect(() => assertLocalBrowserWorkspace('workspace')).toThrow('unsupported')
    const manager = new BrowserManager(assertLocalBrowserWorkspace)
    expect(() => manager.open({ workspaceId: 'workspace', url: 'https://example.com', source: 'user' })).toThrow('unsupported')
    expect(manager.list('workspace')).toEqual([])
    vi.mocked(store.getWorkspace).mockReturnValue(workspace)
    expect(() => assertLocalBrowserWorkspace('workspace')).not.toThrow()
  })
  it('标签只出现在所属工作区的目录里', () => {
    const manager = new BrowserManager()
    const a = manager.open({ workspaceId: 'workspace-a', source: 'user', url: 'https://a.example.com' })
    const b = manager.open({ workspaceId: 'workspace-b', source: 'user', url: 'https://b.example.com' })

    expect(manager.list('workspace-a').map((tab) => tab.id)).toEqual([a.id])
    expect(manager.list('workspace-b').map((tab) => tab.id)).toEqual([b.id])
  })

  it('Agent 必须同时匹配工作区和 run 才能导航或关闭标签', () => {
    const manager = new BrowserManager()
    const tab = manager.open({
      workspaceId: 'workspace-a',
      source: 'agent',
      ownerRunId: 'run-a',
      url: 'https://example.com'
    })

    expect(() => manager.navigate(tab.id, 'https://example.org', {
      workspaceId: 'workspace-b',
      runId: 'run-a'
    })).toThrow('当前 Agent')
    expect(() => manager.close(tab.id, {
      workspaceId: 'workspace-a',
      runId: 'run-b'
    })).toThrow('当前 Agent')

    expect(manager.navigate(tab.id, 'https://example.org', {
      workspaceId: 'workspace-a',
      runId: 'run-a'
    }).url).toBe('https://example.org/')
    manager.close(tab.id, { workspaceId: 'workspace-a', runId: 'run-a' })
    expect(manager.list('workspace-a')).toEqual([])
  })

  it('允许一个 run 认领用户标签且拒绝另一个 run 抢占', () => {
    const manager = new BrowserManager()
    const tab = manager.open({ workspaceId: 'workspace-a', source: 'user', url: 'https://example.com' })

    expect(manager.claim(tab.id, { workspaceId: 'workspace-a', runId: 'run-a' }).ownerRunId).toBe('run-a')
    expect(manager.claim(tab.id, { workspaceId: 'workspace-a', runId: 'run-a' }).ownerRunId).toBe('run-a')
    expect(() => manager.claim(tab.id, { workspaceId: 'workspace-a', runId: 'run-b' })).toThrow('另一个 Agent 会话')
    expect(manager.navigate(tab.id, 'https://example.org', {
      workspaceId: 'workspace-a',
      runId: 'run-a'
    }).url).toBe('https://example.org/')
  })

  it('同一会话的新 run 可以继续操作先前打开的标签', () => {
    const manager = new BrowserManager()
    const tab = manager.open({
      workspaceId: 'workspace-a',
      source: 'agent',
      ownerRunId: 'run-a',
      ownerSessionId: 'session-a',
      url: 'https://example.com'
    })

    expect(manager.navigate(tab.id, 'https://example.org', {
      workspaceId: 'workspace-a',
      runId: 'run-b',
      sessionId: 'session-a'
    }).url).toBe('https://example.org/')
    expect(() => manager.navigate(tab.id, 'https://example.net', {
      workspaceId: 'workspace-a',
      runId: 'run-c',
      sessionId: 'session-b'
    })).toThrow('只能操作')
  })

  it('拒绝认领 Agent 创建的无头标签', () => {
    const manager = new BrowserManager()
    const tab = manager.open({
      workspaceId: 'workspace-a',
      source: 'agent',
      ownerRunId: 'run-a',
      backend: 'headless',
      url: 'https://example.com'
    })

    expect(tab.backend).toBe('headless')
    expect(() => manager.claim(tab.id, { workspaceId: 'workspace-a', runId: 'run-a' })).toThrow('只能认领用户')
  })

  it('用户标签用 clientTabId 幂等注册，避免事件与 invoke 竞态产生副本', () => {
    const manager = new BrowserManager()
    const first = manager.open({
      workspaceId: 'workspace-a',
      source: 'user',
      clientTabId: 'renderer-tab-1',
      url: 'https://example.com'
    })
    const second = manager.open({
      workspaceId: 'workspace-a',
      source: 'user',
      clientTabId: 'renderer-tab-1',
      url: 'https://example.com'
    })

    expect(second.id).toBe(first.id)
    expect(manager.list('workspace-a')).toHaveLength(1)
  })

  /*
    需求：浏览器要能打开 file:// 本地页面（2026-09-22，见 normalizeUrl 头注）。
    标题这条是配套的：file:// 的 hostname 是空串，不兜底就是一个没有文字的标签。
  */
  it('允许 file:// 打开与导航，标题取路径末段而不是空串', () => {
    const manager = new BrowserManager()
    const tab = manager.open({
      workspaceId: 'workspace-a',
      source: 'agent',
      ownerRunId: 'run-a',
      url: 'file:///tmp/reports/q3.html'
    })

    expect(tab.url).toBe('file:///tmp/reports/q3.html')
    expect(tab.title).toBe('q3.html')
    expect(
      manager.navigate(tab.id, 'file:///tmp/next.html', { workspaceId: 'workspace-a', runId: 'run-a' }).url
    ).toBe('file:///tmp/next.html')
  })

  it('file:// 之外的非 http(s) 协议仍然被拒', () => {
    const manager = new BrowserManager()
    expect(() => manager.open({ workspaceId: 'workspace-a', source: 'user', url: 'javascript:alert(1)' })).toThrow('http')
  })

  /*
    需求：Agent 一打开浏览器，右侧工作台就要展开（2026-09-22）。整条链是
    open({openRightPanel}) → emit(rightPanelOpen) → 渲染层 setRightPanelForWorkspace，
    这里钉住第一步：flag 丢了的话上游全部照常工作、只有面板永远不展开，
    而那种「工具跑成功了但界面没反应」最难查。
  */
  it('openRightPanel 会随变更事件发给监听方，不传时不带这个标记', () => {
    const manager = new BrowserManager()
    const seen: Array<{ rightPanelOpen?: boolean }> = []
    manager.setListener((change) => seen.push(change))

    manager.open({
      workspaceId: 'workspace-a',
      source: 'agent',
      ownerRunId: 'run-a',
      url: 'https://example.com',
      openRightPanel: true
    })
    manager.open({ workspaceId: 'workspace-a', source: 'user', url: 'https://example.org' })

    expect(seen[0]?.rightPanelOpen).toBe(true)
    expect(seen[1]?.rightPanelOpen).toBeUndefined()
    manager.setListener(null)
  })
})

describe('BrowserManager · Profiles', () => {
  it('总会提供不可删除的默认浏览器', () => {
    const manager = new BrowserManager()
    const profile = manager.listProfiles()[0]

    expect(profile).toMatchObject({ id: 'default', isDefault: true })
    expect(() => manager.deleteProfile('default')).toThrow('默认浏览器不能删除')
  })

  it('自定义 Profile 可删除，并关闭所有工作区中使用它的标签', () => {
    const manager = new BrowserManager()
    const profile = manager.createProfile('Store A', ['EXAMPLE.COM', 'example.com'])
    manager.open({
      workspaceId: 'workspace-a',
      source: 'user',
      profileId: profile.id,
      url: 'https://example.com'
    })
    manager.open({
      workspaceId: 'workspace-b',
      source: 'user',
      profileId: profile.id,
      url: 'https://example.org'
    })

    expect(profile.domains).toEqual(['example.com'])
    manager.deleteProfile(profile.id)

    expect(manager.listProfiles().map((item) => item.id)).toEqual(['default'])
    expect(manager.list('workspace-a')).toEqual([])
    expect(manager.list('workspace-b')).toEqual([])
  })
})
