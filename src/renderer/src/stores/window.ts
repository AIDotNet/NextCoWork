/**
 * 窗口级状态:外层 Tab + 当前工作区 + 侧边栏折叠。
 *
 * ★ **`activeWorkspaceId` 是一个值、两个消费者**(方案 §8):内层 Tab 条和侧边栏
 * 下半的会话区都从它派生。各自存一份必然会不同步,而「切了顶部 Tab 但左边没跟着变」
 * 是那种用户一眼看得见、你却要查半天的 bug。
 *
 * 它**不是**从 `activeOuterId` 直接算出来的,虽然看起来可以。因为「定时任务」这类
 * 功能 Tab 激活时,侧边栏仍然显示着原来那个工作区的会话列表(截图 4aa68110) ——
 * 派生式写法在那一刻会算出 null,整个侧边栏下半会闪空。所以它是显式状态,
 * 只在**激活一个工作区 Tab 时**才更新。
 */
import { create } from 'zustand'
import type { Bootstrap } from '../../../shared/domain/bootstrap'
import type { FeatureKind, OuterTab, WindowKind } from '../../../shared/domain/tab'
import { reorder } from '../../../shared/domain/tab'
import type { Workspace } from '../../../shared/domain/workspace'
import { isLocalEnvironment } from '../../../shared/domain/environment'
import { ulid } from '../../../shared/util/id'
import { DEFAULT_SETTINGS_PAGE, type SettingsPageId } from '../settings/nav'
import { persistInnerTabs, persistOuterTabs } from '../services/app'
import { invoke } from '../services/ipc'
import { installShellWindowPort, shellTabs } from './shell-port'
import { cancelConnectionRequest, commitWorkspaceActivation, connectionErrorKey, prepareWorkspace, releaseWorkspaceActivation } from '../services/connections'

interface WindowState {
  workspaceTargets: Record<string, Workspace>
  pendingActivation: { workspaceId: string; requestId: string } | null
  activationApproval: boolean
  activationError: string | null
  updateWorkspaces: (workspaces: Workspace[]) => void
  confirmActivation: (allowed: boolean) => void
  cancelActivation: () => void
  clearActivationError: () => void
  windowKind: WindowKind
  outer: OuterTab[]
  activeOuterId: string | null
  /** 见文件头:显式状态,不从 activeOuterId 派生 */
  activeWorkspaceId: string | null
  /**
   * 左侧全局入口占用主内容区时的瞬时模式。
   *
   * 浏览器管理页不是一个可持久化的外层 Tab：打开它不能改变当前工作区，
   * 后台 Agent 也不能借此改动用户正在看的窗口布局。退出后继续显示原工作区。
   */
  activeStandaloneFeature: 'browser' | 'extensions' | 'scheduled' | 'git' | null
  scheduledUnread: boolean
  clearScheduledUnread: () => void
  sidebarCollapsed: boolean
  /**
   * 外层 Tab 条右端的右侧工作台开关。当前值是 active workspace 的投影；
   * 真正的展开态记录在 `rightPanelOpenByWorkspace`，后台 Agent 不能掀开
   * 用户此刻正在看的另一个工作区。
   */
  rightPanelOpen: boolean
  /** 右侧工作台展开态按工作区隔离；后台 Agent 的动作不会改当前工作区。 */
  rightPanelOpenByWorkspace: Record<string, boolean>
  /** 底部 Dock 展开态按工作区隔离，避免切换工作区时串用上一个状态。 */
  bottomPanelOpenByWorkspace: Record<string, boolean>
  bottomPanelOpen: boolean
  /**
   * 拖出来的面板尺寸。**和上面两个开关不同,这两个是落盘的** ——
   * 理由写在 `WindowTabState` 那两个字段上。
   *
   * 参考实现里右侧面板确实是可拖的:同一版界面的两张截图,一张 ≈297px
   * (工具条收成「搜索 + …」),一张 ≈608px(七颗按钮全展开)。
   * 也就是说宽度不只是个人喜好,它**改变工具条的形态**(见 FilesView 的 ResizeObserver)。
   */
  rightPanelWidth: number
  bottomPanelHeight: number
  /**
   * 设置模态浮层。**一个字段兼表「开没开」和「开在哪一页」** ——
   * 拆成 `open: boolean` + `page` 的话,这两个值必然会有不同步的那一刻。
   *
   * ★ **不落盘。** 和 `sidebarCollapsed` / 两个面板开关同类:它是这个窗口
   * 此刻的呈现状态,不是用户的偏好。启动到一半自己弹出设置面板是 bug 不是恢复。
   * 现在靠「`persist()` 只在 Tab 增删改移里调用」自动成立,而这是**碰巧对**——
   * 哪天有人给整个 store 套上 persist 中间件就会退化。同目录测试守着这条。
   *
   * 放在 store 而不是 AppShell 的 useState,因为调用点不止一个,且有的够不着
   * 组件树:`services/ipc.ts` 那句「UI 据 code 决定**跳设置页**」说的那个跳转
   * 会发生在 session store 的错误分支里,那里只有 `getState()`。
   */
  settingsPage: SettingsPageId | null

  /**
   * 奖励中心（邀请好友）那个全屏浮层开没开。
   *
   * ★ **不落盘**，和 `settingsPage` 同类：它是这个窗口此刻的呈现状态，
   * 不是用户的偏好 —— 启动就自己弹出一个邀请页是 bug 不是恢复。
   *
   * 放在 store 而不是 `AppShell` 的 useState：发起它的是侧边栏最下面那颗账户菜单里
   * 的一项（`AccountMenu`），而浮层挂在 AppShell 上。经 store 走省掉
   * 「Sidebar 再加一个只为透传的 prop」这一层 —— 同 `settingsPage` 的先例。
   */
  rewardsOpen: boolean

  /**
   * 窗口是不是最大化的 —— 自绘的中间那颗窗口按钮据此在「□」和「双叠框」之间换字形
   * (`shell/WindowControls.tsx`,仅 Windows/Linux)。
   *
   * ★ **权威值在主进程**(`win.isMaximized()`),这里只是它经 `window:maximized`
   * 推过来的投影。渲染层不能按自己发出去的 `toggleMaximize` 记账:用户还能拖窗口
   * 边缘、双击拖动区、按 Win+↑,那样迟早对不上。
   *
   * ★ **不落盘。** 和 `settingsPage`、`sidebarCollapsed` 同类,是此刻的呈现状态。
   * macOS 上永远是 false(那边没有这条推送,也没有那三颗按钮)。
   */
  maximized: boolean

  /**
   * 应用版本号（`Bootstrap.versions.app`），给侧边栏品牌行下面那行小字用。
   *
   * ★ **放在这里是因为它是窗口外壳级的常量，不是因为要在 store 里算。**
   * 权威值在主进程（`app.getVersion()`，经 bootstrap 的 `versions` 过来），
   * 这里只是它在渲染层的投影，`hydrate` 时写入一次。
   *
   * 不给 `Sidebar` 加一个只为透传的 prop，也不让品牌行自己去读 store 的
   * `versions` —— 理由同 `rewardsOpen`：视图从 store 取比「AppShell 加 prop、
   * 再往主题预览那一路透传」少一层。主题预览里的 `Sidebar` 因此也自动带上
   * 真版本号，不用为它编一个假值。
   */
  appVersion: string

  hydrate: (b: Bootstrap) => void
  activate: (outerId: string) => Promise<boolean>
  openWorkspace: (workspaceId: string) => Promise<boolean>
  openFeature: (feature: FeatureKind) => void
  closeStandaloneFeature: () => void
  close: (outerId: string) => Promise<void>
  move: (from: number, to: number) => void
  togglePin: (outerId: string) => void
  toggleSidebar: () => void
  toggleRightPanel: () => void
  setRightPanelForWorkspace: (workspaceId: string, open: boolean) => void
  toggleBottomPanel: () => void
  setBottomPanelForWorkspace: (workspaceId: string, open: boolean) => void
  /** 拖动分隔条时每帧都在调 —— 落盘那侧防抖 500ms,这里不用自己攒 */
  setRightPanelWidth: (px: number) => void
  setBottomPanelHeight: (px: number) => void
  openSettings: (page?: SettingsPageId) => void
  closeSettings: () => void
  openRewards: () => void
  closeRewards: () => void
  setMaximized: (maximized: boolean) => void
}

const firstWorkspaceId = (tabs: readonly OuterTab[], activeId: string | null): string | null => {
  const active = tabs.find((t) => t.id === activeId)
  if (active?.kind === 'workspace') return active.ref.workspaceId
  for (const t of tabs) if (t.kind === 'workspace') return t.ref.workspaceId
  return null
}

/**
 * 全新安装的第一屏。
 *
 * 主进程已经播种了「默认工作区」这条**记录**(`main/runtime.ts` 的 `seedDefaultWorkspace`),
 * 但记录不是视图 —— 没人替它开一个外层 Tab 的话,首屏停在「打开一个工作区开始」,
 * 用户必须先走一遍「打开文件夹」才看得见这个应用长什么样。而那恰恰是那次播种
 * 想消灭的空态。挑最近打开过的一个开出来。
 *
 * 只在**持久化的 Tab 表为空**时发生 —— 包括用户上次自己关光了所有 Tab 的情况。
 * 编辑器类应用都是这么做的(重开上次那个目录),而空壳首屏除了 Tab 条上那个 `+`
 * 之外什么都没有,不值得让用户每次启动都走一遍。
 *
 * 注意这不等于「Tab 关不掉」:补 Tab 只发生在 `hydrate`,`close` 里没有这一步 ——
 * 当前这次会话里关光就是空的,不会有一个 Tab 自己长回来。
 */
const initialTabs = (workspaces: readonly Workspace[]): OuterTab[] => {
  const latest = [...workspaces].sort((a, b) => b.lastOpenedAt - a.lastOpenedAt)[0]
  if (latest === undefined) return []
  return [{ id: ulid(), kind: 'workspace', ref: { workspaceId: latest.id } }]
}

/**
 * 面板尺寸的默认值与夹取范围。
 *
 * 下界不是随手定的:右侧那格窄于 ≈240 时,树里第三层的文件名就只剩几个字,
 * 那时候该做的是把面板关掉而不是继续缩。上界留出主区仍能读一段对话的宽度。
 * **读回持久化值时也走这个夹取** —— 上一版的默认宽度、或者换了台小屏机器,
 * 都可能让存下来的数字在当前屏幕上是荒谬的。
 */
const RIGHT_PANEL = { def: 300, min: 240, max: 720 } as const
const BOTTOM_PANEL = { def: 220, min: 140, max: 640 } as const

const clamp = (px: number, r: { min: number; max: number }): number => Math.round(Math.min(r.max, Math.max(r.min, px)))

/** Keep pinned tabs in a stable block at the beginning of the outer tab strip. */
const orderOuterTabs = (tabs: readonly OuterTab[]): OuterTab[] => [
  ...tabs.filter((tab) => tab.pinned === true),
  ...tabs.filter((tab) => tab.pinned !== true)
]

const outerTabOrderChanged = (before: readonly OuterTab[], after: readonly OuterTab[]): boolean =>
  before.length !== after.length || after.some((tab, index) => {
    const previous = before[index]
    return previous?.id !== tab.id || previous?.pinned !== tab.pinned
  })

export const useWindowStore = create<WindowState>((set, get) => {
  let approval: ((allowed: boolean) => void) | undefined
  let activeTicket: string | undefined
  const releaseActive = (): void => {
    if (activeTicket) void releaseWorkspaceActivation(activeTicket).catch(() => {})
    activeTicket = undefined
  }
  const cancelPending = (): void => {
    const pending = get().pendingActivation
    approval?.(false)
    approval = undefined
    if (pending) void cancelConnectionRequest(pending.requestId).catch(() => {})
    set({ pendingActivation: null, activationApproval: false })
  }
  const needsPrepare = (workspaceId: string): boolean => {
    const workspace = get().workspaceTargets[workspaceId]
    return workspace === undefined || !isLocalEnvironment(workspace.environment)
  }
  const verify = async (workspaceId: string, commit: () => boolean): Promise<boolean> => {
    cancelPending()
    const requestId = ulid()
    set({ pendingActivation: { workspaceId, requestId }, activationError: null })
    try {
      let ready
      try { ready = await prepareWorkspace({ workspaceId, requestId }) } catch (error) {
        if (get().pendingActivation?.requestId !== requestId) return false
        if (connectionErrorKey(error) !== 'environment.error.approval-required') throw error
        set({ activationApproval: true })
        const allowed = await new Promise<boolean>((resolve) => { approval = resolve })
        if (!allowed || get().pendingActivation?.requestId !== requestId) return false
        set({ activationApproval: false })
        ready = await prepareWorkspace({ workspaceId, requestId, allowLocalCommands: true })
      }
      if (get().pendingActivation?.requestId !== requestId) return false
      await commitWorkspaceActivation(ready.ticket, requestId)
      if (get().pendingActivation?.requestId !== requestId || !commit()) {
        void releaseWorkspaceActivation(ready.ticket).catch(() => {})
        return false
      }
      releaseActive()
      activeTicket = ready.ticket
      return true
    } catch (error) {
      if (get().pendingActivation?.requestId === requestId) set({ activationError: connectionErrorKey(error) })
      return false
    } finally {
      if (get().pendingActivation?.requestId === requestId) {
        set({ pendingActivation: null, activationApproval: false })
        approval = undefined
      }
    }
  }
  /** 每次结构性变更都落盘。主进程侧防抖 500ms,这里不用自己攒。 */
  const persist = (outer: OuterTab[], activeOuterId: string | null): void => {
    const { windowKind, rightPanelWidth, bottomPanelHeight } = get()
    // 专注会话窗口只承载一次打开的工作区，不把临时布局写回窗口级状态。
    if (windowKind === 'quick') return
    persistOuterTabs(windowKind, {
      outer,
      activeOuterId,
      rightPanelWidth,
      bottomPanelHeight
    })
  }

  return {
    workspaceTargets: {},
    pendingActivation: null,
    activationApproval: false,
    activationError: null,
    updateWorkspaces: (workspaces) => set({ workspaceTargets: Object.fromEntries(workspaces.map((workspace) => [workspace.id, workspace])) }),
    confirmActivation: (allowed) => { if (allowed) approval?.(true); else cancelPending() },
    cancelActivation: cancelPending,
    clearActivationError: () => set({ activationError: null }),
    windowKind: 'main',
    outer: [],
    activeOuterId: null,
    activeWorkspaceId: null,
    activeStandaloneFeature: null,
    sidebarCollapsed: false,
    rightPanelOpen: false,
    rightPanelOpenByWorkspace: {},
    bottomPanelOpenByWorkspace: {},
    bottomPanelOpen: false,
    rightPanelWidth: RIGHT_PANEL.def,
    bottomPanelHeight: BOTTOM_PANEL.def,
    settingsPage: null,
    rewardsOpen: false,
    maximized: false,
    appVersion: '',
    scheduledUnread: false,
    clearScheduledUnread: () => set({ scheduledUnread: false }),

    hydrate(b) {
      cancelPending()
      releaseActive()
      get().updateWorkspaces(b.workspaces)
      /*
        ★ 过滤不再属于外层 Tab 的功能。设置是模态浮层；浏览器和扩展是左侧入口切换出的
        主内容模式。旧版本可能已经把它们写进 kv，升级后必须在恢复边界清掉，
        否则用户仍会看见一个无法由当前交互创建、却会一直恢复出来的幽灵 Tab。

        ★ `'skills'` 是 `'extensions'` 的**旧名**（扩展面板把 Skill 管理并了进去）。
        它必须继续留在这张表里：老库里那条记录写的还是 `skills`，而这个值已经不在
        `FeatureKind` 里了 —— 不过滤的话它既取不到图标也匹配不到视图，正好变成上面
        说的那种幽灵 Tab。表的类型故意写 `string[]` 而不是 `FeatureKind[]`，
        好让这个已经退役的值还能写在这儿。
      */
      const NON_TAB_FEATURES: readonly string[] = ['settings', 'browser', 'extensions', 'scheduled', 'git', 'skills']
      const quickWindow = b.windowKind === 'quick'
      const persisted = quickWindow ? [] : b.tabState.outer.filter(
        (t) => !(t.kind === 'feature' && NON_TAB_FEATURES.includes(t.ref.feature))
      )
      const orderedPersisted = orderOuterTabs(persisted)
      const outer = quickWindow
        ? []
        : (orderedPersisted.length > 0 ? orderedPersisted : initialTabs(b.workspaces))
      const requestedActiveId = b.tabState.activeOuterId
      const desiredActiveId = outer.some((tab) => tab.id === requestedActiveId)
        ? requestedActiveId
        : (outer[0]?.id ?? null)
      const desiredTab = outer.find((tab) => tab.id === desiredActiveId)
      const waitForRemote = desiredTab?.kind === 'workspace' && needsPrepare(desiredTab.ref.workspaceId)
      const activeOuterId = waitForRemote
        ? outer.find((tab) => tab.kind === 'workspace' && !needsPrepare(tab.ref.workspaceId))?.id ?? null : desiredActiveId
      const workspaceId = quickWindow ? null : firstWorkspaceId(outer, activeOuterId)
      set({
        windowKind: b.windowKind,
        appVersion: b.versions.app,
        outer,
        activeOuterId,
        activeWorkspaceId: workspaceId && !needsPrepare(workspaceId) ? workspaceId : null,
        activeStandaloneFeature: null,
        rightPanelWidth: clamp(b.tabState.rightPanelWidth ?? RIGHT_PANEL.def, RIGHT_PANEL),
        bottomPanelHeight: clamp(b.tabState.bottomPanelHeight ?? BOTTOM_PANEL.def, BOTTOM_PANEL),
        bottomPanelOpen: false,
        bottomPanelOpenByWorkspace: {}
      })
      // 自动开出来的这一个也要落盘,否则它每次启动都换一个新 id。
      // 迁移掉旧功能 Tab 时也立即回写，避免每次启动都重复修复同一份旧布局。
      if (!waitForRemote && ((persisted.length === 0 && outer.length > 0) || persisted.length !== b.tabState.outer.length || outerTabOrderChanged(persisted, outer)))
        persist(outer, activeOuterId)
      if (waitForRemote && desiredActiveId) void get().activate(desiredActiveId)
    },

    async activate(outerId) {
      const tab = get().outer.find((t) => t.id === outerId)
      if (tab === undefined) return false
      const commit = (): boolean => {
      if (!get().outer.some((current) => current.id === outerId)) return false
      set({
        activeOuterId: outerId,
        activeStandaloneFeature: null,
        // 功能 Tab 不改工作区上下文 —— 侧边栏保持原样
        ...(tab.kind === 'workspace'
          ? {
              activeWorkspaceId: tab.ref.workspaceId,
              rightPanelOpen: get().rightPanelOpenByWorkspace[tab.ref.workspaceId] ?? false,
              bottomPanelOpen: get().bottomPanelOpenByWorkspace[tab.ref.workspaceId] ?? false
            }
          : {})
      })
      persist(get().outer, outerId)
      return true
      }
      if (tab.kind === 'workspace' && needsPrepare(tab.ref.workspaceId)) return verify(tab.ref.workspaceId, commit)
      cancelPending()
      if (tab.kind === 'workspace') releaseActive()
      return commit()
    },

    async openWorkspace(workspaceId) {
      const { windowKind, activeWorkspaceId } = get()
      if (windowKind === 'quick' && activeWorkspaceId !== null && workspaceId !== activeWorkspaceId) return false
      const existing = get().outer.find((t) => t.kind === 'workspace' && t.ref.workspaceId === workspaceId)
      if (existing !== undefined) {
        return get().activate(existing.id)
      }
      const commit = (): boolean => {
      const tab: OuterTab = {
        id: ulid(),
        kind: 'workspace',
        ref: { workspaceId }
      }
      const outer = [...get().outer, tab]
      set({
        outer,
        activeOuterId: tab.id,
        activeWorkspaceId: workspaceId,
        activeStandaloneFeature: null,
        rightPanelOpen: get().rightPanelOpenByWorkspace[workspaceId] ?? false,
        bottomPanelOpen: get().bottomPanelOpenByWorkspace[workspaceId] ?? false
      })
      persist(outer, tab.id)
      return true
      }
      if (needsPrepare(workspaceId)) return verify(workspaceId, commit)
      cancelPending()
      releaseActive()
      return commit()
    },

    openFeature(feature) {
      if (get().windowKind === 'quick') return
      cancelPending()
      // 设置是模态浮层,不是 Tab —— 改道,不建 Tab、不落盘(见 hydrate 里的过滤)
      if (feature === 'settings') {
        get().openSettings()
        return
      }
      // 浏览器和扩展入口切换整块主内容区。它们不是文档式工作内容，因此既不创建外层 Tab，
      // 也不改变/持久化用户原本所在的工作区；关闭后自然回到原处。
      if (feature === 'browser' || feature === 'extensions' || feature === 'scheduled' || feature === 'git') {
        set({ activeStandaloneFeature: feature, ...(feature === 'scheduled' ? { scheduledUnread: false } : {}) })
        return
      }
      const existing = get().outer.find((t) => t.kind === 'feature' && t.ref.feature === feature)
      if (existing !== undefined) {
        get().activate(existing.id)
        return
      }
      const tab: OuterTab = { id: ulid(), kind: 'feature', ref: { feature } }
      const outer = [...get().outer, tab]
      set({ outer, activeOuterId: tab.id, activeStandaloneFeature: null })
      persist(outer, tab.id)
    },

    closeStandaloneFeature() {
      set({ activeStandaloneFeature: null })
    },

    async close(outerId) {
      const { outer, activeOuterId } = get()
      const idx = outer.findIndex((t) => t.id === outerId)
      if (idx < 0) return
      const closed = outer[idx]
      const next = outer.filter((t) => t.id !== outerId)
      // 关掉的是当前 Tab 时,焦点给右邻;没有右邻给左邻 —— 浏览器的习惯
      const nextActive = activeOuterId === outerId ? (next[idx]?.id ?? next[idx - 1]?.id ?? null) : activeOuterId
      const nextWorkspaceId = firstWorkspaceId(next, nextActive)
      const commit = (): boolean => {
      if (get().outer !== outer || get().activeOuterId !== activeOuterId) return false
      set({
        outer: next,
        activeOuterId: nextActive,
        activeWorkspaceId: nextWorkspaceId,
        rightPanelOpen: nextWorkspaceId === null ? false : (get().rightPanelOpenByWorkspace[nextWorkspaceId] ?? false),
        bottomPanelOpen: nextWorkspaceId === null ? false : (get().bottomPanelOpenByWorkspace[nextWorkspaceId] ?? false)
      })
      persist(next, nextActive)

      /*
        ★ **关掉工作区 Tab = 这个工作区退出内存。** 内层 Tab 表和它那些会话的
        转录都该放掉 —— 方案 §8 写的「per-session store 懒创建、工作区关闭时销毁」
        就是这一行。曾经这两个释放函数都写好了却没有任何人调用,于是开过的每个
        工作区、每段转录都留在内存里直到退出应用。

        `openWorkspace` 保证一个工作区最多只有一个外层 Tab,所以不必再查
        还有没有别的 Tab 引用它。**正在跑的 run 不受影响** —— 它活在主进程,
        `releaseSession` 会拒绝放掉那些会话。
      */
      if (closed?.kind === 'workspace') shellTabs()?.forgetTabsForWorkspace(closed.ref.workspaceId)
      return true
      }
      if (nextWorkspaceId && needsPrepare(nextWorkspaceId) && (activeOuterId === outerId || nextWorkspaceId !== get().activeWorkspaceId)) {
        await verify(nextWorkspaceId, commit)
      } else {
        cancelPending()
        if (!nextWorkspaceId || !needsPrepare(nextWorkspaceId)) releaseActive()
        commit()
      }
    },

    move(from, to) {
      cancelPending()
      const outer = orderOuterTabs(reorder(get().outer, from, to))
      set({ outer })
      persist(outer, get().activeOuterId)
    },

    togglePin(outerId) {
      cancelPending()
      const current = get().outer
      if (!current.some((tab) => tab.id === outerId)) return
      const outer = orderOuterTabs(current.map((tab) => tab.id === outerId ? { ...tab, pinned: tab.pinned !== true } : tab))
      set({ outer })
      persist(outer, get().activeOuterId)
    },

    toggleSidebar() {
      set({ sidebarCollapsed: !get().sidebarCollapsed })
    },

    toggleRightPanel() {
      const next = !get().rightPanelOpen
      const workspaceId = get().activeWorkspaceId
      set({
        rightPanelOpen: next,
        ...(workspaceId === null
          ? {}
          : {
              rightPanelOpenByWorkspace: {
                ...get().rightPanelOpenByWorkspace,
                [workspaceId]: next
              }
            })
      })
    },

    setRightPanelForWorkspace(workspaceId, open) {
      const byWorkspace = {
        ...get().rightPanelOpenByWorkspace,
        [workspaceId]: open
      }
      set({
        rightPanelOpenByWorkspace: byWorkspace,
        ...(get().activeWorkspaceId === workspaceId ? { rightPanelOpen: open } : {})
      })
    },

    toggleBottomPanel() {
      const next = !get().bottomPanelOpen
      const workspaceId = get().activeWorkspaceId
      set({
        bottomPanelOpen: next,
        ...(workspaceId === null
          ? {}
          : {
              bottomPanelOpenByWorkspace: {
                ...get().bottomPanelOpenByWorkspace,
                [workspaceId]: next
              }
            })
      })
    },

    setBottomPanelForWorkspace(workspaceId, open) {
      const byWorkspace = {
        ...get().bottomPanelOpenByWorkspace,
        [workspaceId]: open
      }
      set({
        bottomPanelOpenByWorkspace: byWorkspace,
        ...(get().activeWorkspaceId === workspaceId ? { bottomPanelOpen: open } : {})
      })
    },

    setRightPanelWidth(px) {
      const rightPanelWidth = clamp(px, RIGHT_PANEL)
      if (rightPanelWidth === get().rightPanelWidth) return
      set({ rightPanelWidth })
      persist(get().outer, get().activeOuterId)
    },

    setBottomPanelHeight(px) {
      const bottomPanelHeight = clamp(px, BOTTOM_PANEL)
      if (bottomPanelHeight === get().bottomPanelHeight) return
      set({ bottomPanelHeight })
      persist(get().outer, get().activeOuterId)
    },

    openSettings(page) {
      // 已经开着时不给页码就停在原地 —— 再按一次 ⌘, 不该把用户翻回首页
      set({
        settingsPage: page ?? get().settingsPage ?? DEFAULT_SETTINGS_PAGE
      })
    },

    closeSettings() {
      set({ settingsPage: null })
    },

    openRewards() {
      set({ rewardsOpen: true })
    },

    closeRewards() {
      set({ rewardsOpen: false })
    },

    setMaximized(maximized) {
      set({ maximized })
    }
  }
})

/*
  ★ 这一半由本模块自己装到 port 上(见 `shell-port.ts`)。`tabs` store 那一半
  在它自己的文件里装。两边都**不 import 对方**,`write` 的落盘与面板同步却仍然
  照原样走到这里。

  `persistInnerTabs` 在这里丢掉专注窗口那一路 —— 那个判断原先写死在
  `tabs.ts` 的 `write` 里,而它读的正是本 store 的 `windowKind`,放到安装处
  只有一份,不会再出现「两处各判一次、判据不一样」。
*/
installShellWindowPort({
  windowKind: () => useWindowStore.getState().windowKind,
  setRightPanelForWorkspace: (workspaceId, open) => useWindowStore.getState().setRightPanelForWorkspace(workspaceId, open),
  setBottomPanelForWorkspace: (workspaceId, open) => useWindowStore.getState().setBottomPanelForWorkspace(workspaceId, open),
  persistInnerTabs: (workspaceId, state) => {
    if (useWindowStore.getState().windowKind === 'quick') return
    persistInnerTabs(workspaceId, state)
  }
})
/**
 * 把 `activeWorkspaceId` 上报给主进程。
 *
 * ★ **用 subscribe 而不是在每个赋值点插一行 `invoke`。** 这个字段现在有五处
 * 写入(hydrate / activate / openWorkspace / close / 关掉最后一个工作区),
 * 而且它是显式状态 —— 以后还会有新的写入点。逐点插调用的话,漏掉一处的表现是
 * 「大部分时候对,偶尔插件把文件写进上一个工作区」,一种没人能稳定复现的故障。
 * 订阅式写法让「谁改的」这件事不再重要。
 *
 * ★ 首次也发:启动时 `hydrate()` 恢复出来的那个值,主进程同样需要 —— 否则
 * 用户没切过 Tab 的整个会话里,主进程一无所知。
 *
 * 由 `App.tsx` 调用一次;返回退订函数(HMR 下不退订会叠加订阅)。
 */
export function startActiveWorkspaceReporting(): () => void {
  let last: string | null | undefined
  const report = (id: string | null): void => {
    if (id === last) return
    last = id
    // 即发即忘:主进程那边只是往一个 Map 里写一笔,失败了也没有值得打断用户的事。
    void invoke('workspace:setActive', { workspaceId: id }).catch(() => undefined)
  }
  report(useWindowStore.getState().activeWorkspaceId)
  return useWindowStore.subscribe((state) => { report(state.activeWorkspaceId) })
}
