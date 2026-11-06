/**
 * ★ **重启恢复** —— 方案 §12 第 7 步的完成标志(「双层 Tab 增删改切 + 拖动重排 + 重启恢复」)。
 *
 * 两个 store 放在同一个文件里,因为它们是**同一件事的两半**:外层 Tab 决定
 * 「打开着哪个工作区」,内层 Tab 决定「那个工作区里开着哪几页」。任何一半没恢复,
 * 用户看到的都是「我上次不是这样的」—— 而这两半各自的失败长得完全不一样:
 * 外层丢了是空壳首屏,内层丢了是一条只剩「新对话」的 Tab 条。
 *
 * 这里量的是**恢复**这条路径,不是 Tab 的增删改(那些是纯粹的数组操作)。
 * 恢复路径的特别之处在于它跨了一次异步 IPC 和一次冷启动播种,
 * 而这两处恰好是本轮真正修掉的两个 bug 所在。
 *
 * `services/app` 整个被替掉:它背后是 `window.nextcowork`,在 node 环境里不存在。
 * 替掉之后这些 store 就是纯粹的状态机,正好是该在无头环境里测的东西。
 */
import { createElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { Bootstrap } from '../../../../shared/domain/bootstrap'
import type { SessionListItem } from '../../../../shared/domain/session'
import { DEFAULT_SETTINGS } from '../../../../shared/domain/settings'
import type { InnerTab, InnerTabState, OuterTab } from '../../../../shared/domain/tab'
import type { Workspace } from '../../../../shared/domain/workspace'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../../../shared/domain/workspace'

vi.mock('../../services/app', () => ({
  getInnerTabs: vi.fn(),
  persistInnerTabs: vi.fn(),
  persistOuterTabs: vi.fn()
}))
vi.mock('../../services/connections', () => ({
  prepareWorkspace: vi.fn(), commitWorkspaceActivation: vi.fn(), cancelConnectionRequest: vi.fn(async () => {}),
  releaseWorkspaceActivation: vi.fn(async () => {}),
  connectionErrorKey: () => 'environment.error.connection-failed'
}))

import { getInnerTabs, persistInnerTabs, persistOuterTabs } from '../../services/app'
import { useTabsStore } from '../tabs'
import { useWindowStore } from '../window'
import { prepareWorkspace, commitWorkspaceActivation, releaseWorkspaceActivation } from '../../services/connections'
import { I18nProvider } from '../../i18n'
import { AppSkeleton } from '../../shell/AppSkeleton'
import { OuterTabBar } from '../../shell/OuterTabBar'
import { Sidebar } from '../../shell/Sidebar'

const mockGetInnerTabs = vi.mocked(getInnerTabs)
const mockPersistInner = vi.mocked(persistInnerTabs)
const mockPersistOuter = vi.mocked(persistOuterTabs)

// store 是模块级单例,用例之间必须还原,否则顺序一换就红
const windowInitial = useWindowStore.getState()
const tabsInitial = useTabsStore.getState()

beforeEach(() => {
  vi.clearAllMocks()
  useWindowStore.setState(windowInitial, true)
  useTabsStore.setState(tabsInitial, true)
})

/** 微任务 + 一次宏任务:足够让 `loadOrSeed` 的 then/catch/finally 全部跑完 */
const settle = (): Promise<void> => new Promise((r) => setTimeout(r, 0))

const ws = (over: Partial<Workspace> = {}): Workspace => ({
  id: 'ws-default',
  name: '默认工作区',
  rootPath: '/tmp/ws-default',
  settings: DEFAULT_WORKSPACE_SETTINGS,
  createdAt: 1_000,
  lastOpenedAt: 1_000,
  ...over
})

const boot = (over: Partial<Bootstrap> = {}): Bootstrap => ({
  windowKind: 'main',
  settings: DEFAULT_SETTINGS,
  resolvedTheme: 'dark',
  workspaces: [],
  tabState: { outer: [], activeOuterId: null },
  activeRuns: [],
  versions: { app: '0.0.0', electron: '44.1.1', chrome: '152', node: '24.19.0' },
  ...over
})

const wsTab = (id: string, workspaceId: string): OuterTab => ({
  id,
  kind: 'workspace',
  ref: { workspaceId }
})

const chatTab = (id: string): InnerTab => ({
  id,
  kind: 'chat',
  title: '上次那个对话',
  ref: { sessionId: `s-${id}` }
})

describe('remote workspace activation', () => {
  const remote = ws({ id: 'remote', environment: { kind: 'connection', connectionId: 'server' } })
  it('keeps active ids, tabs and persisted layout unchanged on connection failure', async () => {
    useWindowStore.getState().hydrate(boot({ workspaces: [ws(), remote], tabState: { outer: [wsTab('local', 'ws-default'), wsTab('remote', 'remote')], activeOuterId: 'local' } }))
    vi.mocked(prepareWorkspace).mockRejectedValueOnce(new Error('offline'))
    expect(await useWindowStore.getState().activate('remote')).toBe(false)
    expect(useWindowStore.getState().activeOuterId).toBe('local')
    expect(useWindowStore.getState().activeWorkspaceId).toBe('ws-default')
    expect(mockPersistOuter).not.toHaveBeenCalled()
  })
  it('does not activate a late response after another tab was selected', async () => {
    useWindowStore.getState().hydrate(boot({ workspaces: [ws(), remote], tabState: { outer: [wsTab('local', 'ws-default'), wsTab('remote', 'remote')], activeOuterId: 'local' } }))
    let finish!: (value: Awaited<ReturnType<typeof prepareWorkspace>>) => void
    vi.mocked(prepareWorkspace).mockImplementationOnce(() => new Promise((resolve) => { finish = resolve }))
    const opening = useWindowStore.getState().activate('remote')
    await useWindowStore.getState().activate('local')
    finish({ ticket: 'ticket', workspaceId: 'remote', rootPath: '/remote', environmentKey: 'server', generation: 1 })
    expect(await opening).toBe(false)
    expect(useWindowStore.getState().activeOuterId).toBe('local')
    expect(commitWorkspaceActivation).not.toHaveBeenCalled()
  })
  it('does not mount a restored SSH workspace before validation succeeds', async () => {
    vi.mocked(prepareWorkspace).mockRejectedValueOnce(new Error('offline'))
    useWindowStore.getState().hydrate(boot({ workspaces: [remote], tabState: { outer: [wsTab('remote', 'remote')], activeOuterId: 'remote' } }))
    expect(useWindowStore.getState().activeWorkspaceId).toBeNull()
    await settle()
    expect(useWindowStore.getState().activeWorkspaceId).toBeNull()
  })
  it('releases a late committed ticket without changing the newly selected local view', async () => {
    useWindowStore.getState().hydrate(boot({ workspaces: [ws(), remote], tabState: { outer: [wsTab('local', 'ws-default'), wsTab('remote', 'remote')], activeOuterId: 'local' } }))
    vi.mocked(prepareWorkspace).mockResolvedValueOnce({ ticket: 'late-ticket', workspaceId: 'remote', rootPath: '/remote', environmentKey: 'server', generation: 1 })
    const pending = Promise.withResolvers<void>()
    vi.mocked(commitWorkspaceActivation).mockReturnValueOnce(pending.promise)
    const opening = useWindowStore.getState().activate('remote')
    await settle()
    await useWindowStore.getState().activate('local')
    pending.resolve()
    expect(await opening).toBe(false)
    expect(useWindowStore.getState().activeOuterId).toBe('local')
    expect(releaseWorkspaceActivation).toHaveBeenCalledWith('late-ticket')
  })
  it('does not treat an unknown workspace as local', async () => {
    vi.mocked(prepareWorkspace).mockRejectedValueOnce(new Error('unbound'))
    expect(await useWindowStore.getState().openWorkspace('missing')).toBe(false)
    expect(useWindowStore.getState().activeWorkspaceId).toBeNull()
  })
})

describe('useWindowStore.hydrate · 外层 Tab 的冷启动', () => {
  it('★ 主进程播种的工作区必须被开成外层 Tab —— 否则全新安装的首屏是个空壳', () => {
    /**
     * `main/runtime.ts` 的 `seedDefaultWorkspace` 建的是一条**记录**,不是一个视图。
     * 没人替它开 Tab 的话,`AppShell` 落在「打开一个工作区开始」这一支上,
     * 内层 Tab 条、对话页、输入框**全都不存在** —— 用户必须先自己走一遍
     * 「打开文件夹」才看得见这个应用长什么样,而那正是那次播种想消灭的空态。
     *
     * e2e 探针「首屏直达对话页」那一条断言的就是这里。
     */
    useWindowStore.getState().hydrate(boot({ workspaces: [ws()] }))

    const s = useWindowStore.getState()
    expect(s.outer).toHaveLength(1)
    expect(s.outer[0]).toMatchObject({ kind: 'workspace', ref: { workspaceId: 'ws-default' } })
    expect(s.activeOuterId).toBe(s.outer[0]?.id)
    // 侧边栏和内层 Tab 条都从它派生(§8「一个值,两个消费者」)
    expect(s.activeWorkspaceId).toBe('ws-default')
  })

  it('自动开出来的这一个要落盘,否则它每次启动都换一个新 id', () => {
    /**
     * id 每次都变本身不影响内层 Tab(那是按 workspaceId 索引的),
     * 但外层 Tab 的**顺序**是按 id 存的 —— 不落盘的话用户拖出来的顺序会莫名回退。
     */
    useWindowStore.getState().hydrate(boot({ workspaces: [ws()] }))

    expect(mockPersistOuter).toHaveBeenCalledTimes(1)
    const [kind, state] = mockPersistOuter.mock.calls[0] ?? []
    expect(kind).toBe('main')
    expect(state?.outer).toHaveLength(1)
    expect(state?.activeOuterId).toBe(useWindowStore.getState().activeOuterId)
  })

  it('多个工作区时挑最近打开过的那个', () => {
    useWindowStore.getState().hydrate(
      boot({
        workspaces: [
          ws({ id: 'old', lastOpenedAt: 1_000 }),
          ws({ id: 'recent', lastOpenedAt: 9_000 }),
          ws({ id: 'middle', lastOpenedAt: 5_000 })
        ]
      })
    )

    expect(useWindowStore.getState().activeWorkspaceId).toBe('recent')
    // 只开一个 —— 不是把所有工作区都摊在 Tab 条上
    expect(useWindowStore.getState().outer).toHaveLength(1)
  })

  it('★ 有持久化布局就原样恢复:顺序不动、不加 Tab、也不回写', () => {
    /**
     * 顺序是**用户资产**(§8:拖出来的顺序)。而「不回写」不只是省一次 I/O ——
     * 读回来的和写出去的是同一份,回写等于每次启动都白打一次盘。
     */
    const outer = [wsTab('t1', 'a'), wsTab('t2', 'b'), wsTab('t3', 'c')]
    useWindowStore.getState().hydrate(
      boot({
        workspaces: [ws({ id: 'a' }), ws({ id: 'b' }), ws({ id: 'c' })],
        tabState: { outer, activeOuterId: 't2' }
      })
    )

    const s = useWindowStore.getState()
    expect(s.outer.map((t) => t.id)).toEqual(['t1', 't2', 't3'])
    expect(s.activeOuterId).toBe('t2')
    expect(s.activeWorkspaceId).toBe('b')
    expect(mockPersistOuter).not.toHaveBeenCalled()
  })

  it('一个工作区都没有:保持空态,不崩也不落盘', () => {
    useWindowStore.getState().hydrate(boot())

    const s = useWindowStore.getState()
    expect(s.outer).toEqual([])
    expect(s.activeOuterId).toBeNull()
    expect(s.activeWorkspaceId).toBeNull()
    expect(mockPersistOuter).not.toHaveBeenCalled()
  })

  it('★ 旧版定时任务功能 Tab 会被迁移掉，并恢复到原工作区上下文', () => {
    /**
     * 「定时任务」这类功能 Tab 不属于任何工作区。它激活时侧边栏下半的会话区
     * 仍然显示原来那个工作区的列表(§8,截图 4aa68110)—— 派生式写法
     * 在这一刻会算出 null,整个侧边栏下半会闪空。
     */
    useWindowStore.getState().hydrate(
      boot({
        workspaces: [ws({ id: 'a' })],
        tabState: {
          outer: [wsTab('t1', 'a'), { id: 't2', kind: 'feature', ref: { feature: 'scheduled' } }],
          activeOuterId: 't2'
        }
      })
    )

    const s = useWindowStore.getState()
    expect(s.activeOuterId).toBe('t1')
    expect(s.activeWorkspaceId).toBe('a')
    expect(s.outer.map((tab) => tab.id)).toEqual(['t1'])
  })
})

describe('专注会话窗口 · 布局隔离', () => {
  beforeEach(() => useWindowStore.getState().hydrate(boot({
    windowKind: 'quick',
    workspaces: [ws({ id: 'target' }), ws({ id: 'another', lastOpenedAt: 9_000 })],
    tabState: { outer: [wsTab('old', 'another')], activeOuterId: 'old' }
  })))

  it('不恢复其他项目，也不自动打开最近的项目', () => {
    expect(useWindowStore.getState()).toMatchObject({
      windowKind: 'quick', outer: [], activeOuterId: null, activeWorkspaceId: null
    })
    expect(mockPersistOuter).not.toHaveBeenCalled()
  })

  it('只建立目标会话，ensure 不再读取共享布局或增加草稿', async () => {
    const tabs = useTabsStore.getState()
    tabs.openFocusedSession('target', 'session-target', '目标会话')
    const initial = tabs.stateOf('target')
    expect(await useWindowStore.getState().openWorkspace('target')).toBe(true)
    tabs.ensure('target')
    tabs.openFocusedSession('target', 'session-target')
    await settle()

    expect(tabs.stateOf('target')).toBe(initial)
    expect(initial.tabs).toEqual([expect.objectContaining({
      kind: 'chat', pane: 'main', title: '目标会话', ref: { sessionId: 'session-target' }
    })])
    expect(tabs.dockOf('target').root).toMatchObject({ type: 'group', activeTabId: initial.activeTabId })
    expect(mockGetInnerTabs).not.toHaveBeenCalled()
    expect(mockPersistInner).not.toHaveBeenCalled()
    expect(mockPersistOuter).not.toHaveBeenCalled()
  })

  it('项目内切换会话、打开文件和调整面板不会覆盖主窗口布局', async () => {
    const tabs = useTabsStore.getState()
    tabs.openFocusedSession('target', 'session-target', '目标会话')
    const firstId = tabs.stateOf('target').activeTabId
    await useWindowStore.getState().openWorkspace('target')
    tabs.openSession('target', 'session-next', '同项目另一段会话')
    tabs.openSession('target', 'session-target')
    expect(tabs.stateOf('target').activeTabId).toBe(firstId)
    expect(tabs.tabsOf('target', 'main')).toHaveLength(2)

    tabs.openPath('target', 'doc', 'notes.txt', 'notes.txt')
    useWindowStore.getState().setRightPanelWidth(450)
    useWindowStore.getState().setBottomPanelHeight(300)
    expect(tabs.tabsOf('target', 'right')).toEqual([expect.objectContaining({
      kind: 'doc', ref: { path: 'notes.txt' }
    })])
    expect(mockGetInnerTabs).not.toHaveBeenCalled()
    expect(mockPersistInner).not.toHaveBeenCalled()
    expect(mockPersistOuter).not.toHaveBeenCalled()
  })

  it('没有会话路由时只补本窗口的临时草稿', () => {
    const tabs = useTabsStore.getState()
    tabs.ensure('target')
    tabs.ensure('target')
    expect(tabs.stateOf('target').tabs).toEqual([expect.objectContaining({ kind: 'chat', ref: { sessionId: null } })])
    expect(mockGetInnerTabs).not.toHaveBeenCalled()
    expect(mockPersistInner).not.toHaveBeenCalled()
  })

  it('不允许切到其他项目或打开全局功能页', async () => {
    const win = useWindowStore.getState()
    await win.openWorkspace('target')
    expect(await win.openWorkspace('another')).toBe(false)
    for (const feature of ['browser', 'scheduled', 'git', 'extensions', 'review', 'settings'] as const) win.openFeature(feature)
    expect(useWindowStore.getState().activeWorkspaceId).toBe('target')
    expect(useWindowStore.getState().outer).toHaveLength(1)
    expect(useWindowStore.getState().activeStandaloneFeature).toBeNull()
    expect(useWindowStore.getState().settingsPage).toBeNull()
    expect(mockPersistOuter).not.toHaveBeenCalled()
  })

  it('目标 SSH 项目仍需验证连接，不恢复无关项目作为兜底', async () => {
    const remote = ws({ id: 'remote', environment: { kind: 'connection', connectionId: 'server' } })
    useWindowStore.getState().updateWorkspaces([ws(), remote])
    vi.mocked(prepareWorkspace).mockRejectedValueOnce(new Error('offline'))
    expect(await useWindowStore.getState().openWorkspace('remote')).toBe(false)
    expect(useWindowStore.getState().activeWorkspaceId).toBeNull()
    expect(useWindowStore.getState().outer).toEqual([])
    expect(mockPersistOuter).not.toHaveBeenCalled()
  })
})

const renderShellPart = (children: ReactNode): string => renderToStaticMarkup(
  createElement(I18nProvider, { initialLocale: 'zh-CN', children })
)

const renderSidebar = (focusMode: boolean, sessions: SessionListItem[] = [], activeSessionId: string | null = null): string => renderShellPart(
  createElement(Sidebar, {
    focusMode, workspace: ws(), chatTabs: [], sessions, activeFeature: null, activeSessionId,
    runningSessionIds: new Set<string>(), auth: { mode: 'offline', user: null, expiresAt: null },
    onNewChat: vi.fn(), onSearch: vi.fn(), onOpenFeature: vi.fn(), onOpenSettings: vi.fn(),
    onSelectSession: vi.fn(), onDeleteSession: vi.fn(async () => {}), onCollapse: vi.fn()
  })
)

const recentSession = (archived = false): SessionListItem => ({
  id: 'recent-session', title: '上周的目标会话', updatedAt: new Date().setHours(0, 0, 0, 0) - 86_400_000,
  archived, favorited: false, running: false
})

describe('专注会话窗口 · 精简界面', () => {
  it('只显示当前项目名称，不出现项目标签与切换入口', () => {
    const html = renderShellPart(createElement(OuterTabBar, {
      compact: true, tabs: [wsTab('target', 'ws-default'), wsTab('other', 'another')], activeId: 'target',
      activeWorkspaceId: 'ws-default', workspaces: [ws(), ws({ id: 'another', name: '不相关项目' })],
      runningWorkspaceIds: new Set<string>(), rightPanelOpen: false, bottomPanelOpen: false,
      onActivate: vi.fn(), onClose: vi.fn(), onTogglePin: vi.fn(), onMove: vi.fn(),
      onOpenWorkspace: vi.fn(), onEditWorkspace: vi.fn(), onPickWorkspace: vi.fn(),
      onCreateWorkspace: vi.fn(), onCreateSshWorkspace: vi.fn(), onToggleRightPanel: vi.fn(), onToggleBottomPanel: vi.fn()
    }))
    expect(html).toContain('默认工作区')
    expect(html).not.toContain('不相关项目')
    expect(html).not.toContain('data-outer-tab-id')
    expect(html).not.toContain('aria-label="打开工作区"')
    expect(html).toContain('aria-label="工作区文件"')
    expect(html).toContain('aria-label="底部面板"')
  })

  it('隐藏品牌、账户和全局导航，保留当前项目会话及搜索', () => {
    const html = renderSidebar(true)
    expect(html).toContain('w-[240px]')
    expect(html).toContain('最近对话')
    expect(html).toContain('aria-label="搜索"')
    expect(html).toContain('aria-label="新建对话"')
    for (const label of ['NextCoWork', '定时任务', '浏览器', '扩展', 'aria-label="设置"']) expect(html).not.toContain(label)

    const main = renderSidebar(false)
    expect(main).toContain('w-[297px]')
    expect(main).toContain('NextCoWork')
    expect(main).toContain('定时任务')
    expect(main).toContain('浏览器')
  })

  it('目标位于最近七天或归档时仍能直接看到它', () => {
    const session = recentSession()
    expect(renderSidebar(true, [session], session.id)).toContain(session.title)
    expect(renderSidebar(false, [session], session.id)).not.toContain(session.title)
    const archived = recentSession(true)
    const html = renderSidebar(true, [archived], archived.id)
    expect(html).toContain('aria-current="page"')
    expect(html).not.toContain('inert=""')
  })

  it('专注窗口骨架屏不预留全局导航，侧边栏尺寸保持一致', () => {
    const html = renderShellPart(createElement(AppSkeleton, { focusMode: true }))
    expect(html).toContain('w-[240px]')
    expect(html).not.toContain('size-5 rounded-full')
    expect(html).not.toContain('w-[72%]')
    expect(renderShellPart(createElement(AppSkeleton))).toContain('w-[297px]')
  })
})

describe('useTabsStore.ensure · 内层 Tab 的冷启动', () => {
  it('★ 上次的 Tab 布局要读回来 —— 「重启恢复」的另一半', async () => {
    /**
     * 这个洞的症状很温和,所以特别容易活很久:每次重启,工作区里开着的
     * 三个终端和两个文档都变回一条孤零零的「新对话」。没人会觉得这是 bug,
     * 只会觉得「这应用不记事」。
     */
    const persisted: InnerTabState = {
      tabs: [chatTab('a'), { id: 'b', kind: 'terminal', title: '终端', ref: { terminalId: 'p1' } }],
      activeTabId: 'b'
    }
    mockGetInnerTabs.mockResolvedValue(persisted)

    useTabsStore.getState().ensure('w1')
    await settle()

    const s = useTabsStore.getState().stateOf('w1')
    expect(s.tabs.map((t) => t.id)).toEqual(['a', 'b'])
    expect(s.activeTabId).toBe('b')
    // 读回来的和写出去的是同一份
    expect(mockPersistInner).not.toHaveBeenCalled()
  })

  it('没有持久化布局才开一个新对话 —— 截图里从来没有空 Tab 条', async () => {
    mockGetInnerTabs.mockResolvedValue({ tabs: [], activeTabId: null })

    useTabsStore.getState().ensure('w1')
    await settle()

    const s = useTabsStore.getState().stateOf('w1')
    expect(s.tabs).toHaveLength(1)
    expect(s.tabs[0]?.kind).toBe('chat')
    expect(s.activeTabId).toBe(s.tabs[0]?.id)
    // 这一份是新造的,必须落盘
    expect(mockPersistInner).toHaveBeenCalledTimes(1)
  })

  it('★ 重入只打一次 IPC —— 它挂在 effect 上,会被连着调好几次', async () => {
    /**
     * 没有 `loading` 闸的话,一次渲染里的三次 `ensure` 会并发发三趟 IPC,
     * 而每一趟回来时 `byWorkspace` 都还是空的 —— 于是三个都走到播种分支,
     * 造出三个 Tab,最后一个赢。表现是「打开工作区偶尔会多出几个空对话」。
     */
    mockGetInnerTabs.mockResolvedValue({ tabs: [chatTab('a')], activeTabId: 'a' })

    const { ensure } = useTabsStore.getState()
    ensure('w1')
    ensure('w1')
    ensure('w1')
    await settle()

    expect(mockGetInnerTabs).toHaveBeenCalledTimes(1)
    expect(useTabsStore.getState().stateOf('w1').tabs.map((t) => t.id)).toEqual(['a'])

    // 已经在手里之后再调,连 IPC 都不该发
    useTabsStore.getState().ensure('w1')
    await settle()
    expect(mockGetInnerTabs).toHaveBeenCalledTimes(1)
  })

  it('★ IPC 在途时用户自己开了 Tab:回来的旧布局不能盖掉它', async () => {
    /**
     * 这一趟 IPC 有真实的往返延迟,期间界面已经可以点了。用户按下 ⌘N 之后
     * 又被一份旧布局顶掉,是那种「我明明新建了一个,它自己没了」的 bug。
     */
    let release!: (s: InnerTabState) => void
    mockGetInnerTabs.mockReturnValue(
      new Promise<InnerTabState>((r) => {
        release = r
      })
    )

    useTabsStore.getState().ensure('w1')
    useTabsStore.getState().open('w1', 'terminal')
    const mine = useTabsStore.getState().stateOf('w1')
    expect(mine.tabs).toHaveLength(1)

    release({ tabs: [chatTab('old')], activeTabId: 'old' })
    await settle()

    expect(useTabsStore.getState().stateOf('w1')).toBe(mine)
  })

  it('读不回来就按新工作区处理,而不是留一条空 Tab 条', async () => {
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {})
    mockGetInnerTabs.mockRejectedValue(new Error('IPC 挂了'))

    useTabsStore.getState().ensure('w1')
    await settle()

    const s = useTabsStore.getState().stateOf('w1')
    expect(s.tabs).toHaveLength(1)
    expect(s.tabs[0]?.kind).toBe('chat')
    expect(spy).toHaveBeenCalled()
    spy.mockRestore()
  })

  it('失败过一次之后仍然可以重试 —— 闸不能卡死', async () => {
    // `loading` 挂在 finally 上;挂在 then 上的话,失败一次这个工作区就再也 ensure 不动了
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {})
    mockGetInnerTabs.mockRejectedValue(new Error('IPC 挂了'))
    useTabsStore.getState().ensure('w1')
    await settle()
    spy.mockRestore()

    useTabsStore.getState().forget('w1')
    mockGetInnerTabs.mockResolvedValue({ tabs: [chatTab('a')], activeTabId: 'a' })
    useTabsStore.getState().ensure('w1')
    await settle()

    expect(mockGetInnerTabs).toHaveBeenCalledTimes(2)
    expect(useTabsStore.getState().stateOf('w1').tabs.map((t) => t.id)).toEqual(['a'])
  })
})

/**
 * ★ **关掉工作区 Tab 要真的释放内存。**
 *
 * 两个释放函数(`useTabsStore.forget` 和会话 store 的 `releaseSession`)一直
 * 写在那里、也各自能用,但**没有任何人调用它们** —— 开过的每个工作区、每段转录
 * 都留在内存里直到退出应用。这类 bug 不会让任何测试变红,也不会在界面上留下痕迹:
 * 它只是让内存曲线一路向上,而那要跑够久才看得出来。
 *
 * 所以这里量的不是 `forget` 干得对不对(上面那些用例已经量过),
 * 而是**关 Tab 这条路径上到底有没有人去调它**。
 */
describe('useWindowStore.close · 工作区退场时的释放', () => {
  beforeEach(() => useWindowStore.getState().updateWorkspaces([ws({ id: 'w1' }), ws({ id: 'w2' })]))
  it('★ 关掉工作区 Tab,它的内层 Tab 表跟着放掉', () => {
    useTabsStore.getState().hydrate('w1', { tabs: [chatTab('a')], activeTabId: 'a' })
    useWindowStore.getState().openWorkspace('w1')
    const outerId = useWindowStore.getState().activeOuterId ?? ''

    useWindowStore.getState().close(outerId)

    expect(useTabsStore.getState().byWorkspace['w1']).toBeUndefined()
  })

  it('关闭定时任务独立模式不碰任何工作区', () => {
    useTabsStore.getState().hydrate('w1', { tabs: [chatTab('a')], activeTabId: 'a' })
    useWindowStore.getState().openWorkspace('w1')
    useWindowStore.getState().openFeature('scheduled')
    useWindowStore.getState().closeStandaloneFeature()

    expect(useTabsStore.getState().stateOf('w1').tabs).toHaveLength(1)
  })

  it('关掉工作区 A 不影响还开着的工作区 B', () => {
    useTabsStore.getState().hydrate('w1', { tabs: [chatTab('a')], activeTabId: 'a' })
    useTabsStore.getState().hydrate('w2', { tabs: [chatTab('b')], activeTabId: 'b' })
    useWindowStore.getState().openWorkspace('w1')
    const first = useWindowStore.getState().activeOuterId ?? ''
    useWindowStore.getState().openWorkspace('w2')

    useWindowStore.getState().close(first)

    expect(useTabsStore.getState().byWorkspace['w1']).toBeUndefined()
    expect(useTabsStore.getState().stateOf('w2').tabs).toHaveLength(1)
  })
})

describe('useWindowStore · 外层 Tab 置顶', () => {
  const reviewTab = (id: string, pinned = false): OuterTab => ({
    id,
    kind: 'feature',
    ref: { feature: 'review' },
    pinned
  })

  it('恢复时把置顶 Tab 放到最前，并保留置顶状态', () => {
    useWindowStore.getState().hydrate(boot({
      tabState: { outer: [reviewTab('regular'), reviewTab('pinned', true)], activeOuterId: 'regular' }
    }))

    expect(useWindowStore.getState().outer.map((tab) => tab.id)).toEqual(['pinned', 'regular'])
    expect(useWindowStore.getState().outer[0]?.pinned).toBe(true)
    expect(useWindowStore.getState().activeOuterId).toBe('regular')
  })

  it('右键操作对应的 store action 可置顶、取消置顶并持久化', () => {
    useWindowStore.getState().hydrate(boot({ tabState: { outer: [reviewTab('a'), reviewTab('b')], activeOuterId: 'a' } }))
    vi.clearAllMocks()

    useWindowStore.getState().togglePin('b')
    expect(useWindowStore.getState().outer.map((tab) => tab.id)).toEqual(['b', 'a'])
    expect(useWindowStore.getState().outer[0]?.pinned).toBe(true)
    expect(mockPersistOuter).toHaveBeenCalledTimes(1)

    useWindowStore.getState().togglePin('b')
    expect(useWindowStore.getState().outer.map((tab) => tab.id)).toEqual(['b', 'a'])
    expect(useWindowStore.getState().outer[0]?.pinned).toBe(false)
  })
})
