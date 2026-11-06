/**
 * 内层 Tab —— **按 workspaceId 索引,不是按窗口**(方案 §8)。
 *
 * 这个归属关系是「切回某个工作区,它的 Tab 全都还在」的全部实现。
 * 反过来,如果内层 Tab 挂在窗口上,切工作区就得把 Tab 条清空再重建,
 * 用户开着的三个终端和两个文档就没了。
 *
 * **会话不是 Tab**,Tab 只**引用**一个 sessionId。所以:关掉聊天 Tab 不杀死
 * 正在跑的 run(run 在主进程的 RunRegistry 里活着)、可以在新 Tab 里重开历史会话、
 * 两个 Tab 能看同一个会话。
 */
import { create } from 'zustand'
import { translate } from '../i18n'
import type { InnerTab, InnerTabKind, InnerTabState, TabPane } from '../../../shared/domain/tab'
import { chatKey, paneOf, reorderInPane, tabsInPane } from '../../../shared/domain/tab'
import { ulid } from '../../../shared/util/id'
import { getInnerTabs } from '../services/app'
import { killTerminal } from '../services/terminal'
import { closeBrowserTab } from '../services/browser'
import { adoptDraftSession, isSessionUntouched, releaseSession } from './session'
import type { WorkspaceFileMutationRequest } from '../../../shared/domain/workspace-file'
import { isWithinPath } from './documents'
import { findGroup, migrateLegacyInnerTabs, normalizeDockState, splitGroup, moveTab as moveDockTab, reorderTab as reorderDockTab, resizeSplit, closeTab as closeDockTab, closeGroup as closeDockGroupState, addTabToGroup, type DockDirection, type DockNode } from '../../../shared/domain/dock'
import { installShellTabsPort, shellWindow } from './shell-port'
import { usePluginsStore } from './plugins'
import { pickCustomEditor } from '../../../shared/plugin/custom-editor'
import type { SessionChange } from '../../../shared/domain/session'

const EMPTY: InnerTabState = {
  tabs: [],
  activeTabId: null,
  bottomActiveTabId: null,
  rightActiveTabId: null
}

interface BrowserTabSyncItem {
  id: string
  clientTabId?: string
  url: string
  title: string
  source: 'user' | 'agent'
  profileId?: string
}

/**
 * 打开一个 Tab 时可以覆盖的初值。
 *
 * 存在的理由只有一个,但很硬:**在文件树里点一个文件要开的是「那个文件」**,
 * 不是又一个「未命名文档」。没有它,`open()` 就只能开空白 Tab,
 * 调用方只好开完再 `rename` + 改 ref —— 两次写盘、中间一帧标题是错的。
 */
export interface TabInit {
  title?: string
  /** 给 files(文件树里选中的那一项)/ changes(先看哪个文件的 diff)用 */
  selectedPath?: string
  /** 给 doc / draw / preview / files 用;其余 kind 忽略 */
  path?: string
  /** 给 browser 用 */
  url?: string
  /** Agent/browser manager tab identity. */
  browserId?: string
  profileId?: string
  /** 自定义编辑器的身份 —— 哪个插件的哪一个 viewType */
  viewType?: string
  pluginId?: string
  /** 网页应用的身份 —— 哪个插件的哪一个 webApp(`contributes.webApps[].id`) */
  webAppId?: string
  /** 给 changes(改动审查)用:定位到哪一轮 run 的改动集 */
  runId?: string
  sessionId?: string
  /**
   * 插件终端(`tabs.openTerminal`):主进程按这个 id 备好了启动 spec(env + argv),
   * Tab 必须原样引用。★ 不传(普通新建终端)就本地铸一个 —— 换 id 的症状是
   * create 起了一个裸 shell,插件注入的配置**静默丢失**,零报错。
   */
  terminalId?: string
}

/**
 * `+` 菜单里各种 kind 的初始 Tab。ref 的形状由 kind 决定,所以只能在这里分支。
 *
 * ★ 默认标题走 `translate()` 而不是裸中文。这里不在组件里,调不了 `useI18n()` ——
 * 原来的写法是七处硬编码中文,切到 en-US 之后新建的 Tab 仍然叫「新对话」,
 * 而且**它会跟着布局落盘**:以后每次启动都还是那个中文标题。
 */
function makeTab(kind: InnerTabKind, pane: TabPane, init: TabInit = {}): InnerTab {
  const id = ulid()
  const path = init.path ?? ''
  switch (kind) {
    case 'chat':
      /*
        ★ **`sessionId` 是 null,而且这里绝不发 `sessions:create`。**

        以前这行铸一个 ULID、紧接着一次 IPC 把它插进库,于是「新建对话」「关掉主区
        最后一个 Tab」「工作区第一次露面」各自都会在库里留下一条零消息的「新对话」。
        用户什么也没做,侧边栏却在涨 —— 而那条记录连一句话都没有,谁也认不出它是什么。

        真正需要它的时刻在别处:主进程 `runAgent` 发第一条消息时就会
        `store.ensureSession(...)`(`main/runtime.ts`),而且带的信息比这里全
        (model / mode / thinking / rootPathAtCreation)。这里那次抢跑只是让一条
        空记录提前几分钟出生。id 由 `bindChatSession` 在需要归属时才铸。
      */
      return { id, kind, pane, title: init.title ?? translate('tab.newChat'), ref: { sessionId: null } }
    case 'terminal':
      return { id, kind, pane, title: init.title ?? translate('tab.terminal'), ref: { terminalId: init.terminalId ?? ulid() } }
    case 'doc':
      return { id, kind, pane, title: init.title ?? translate('tab.untitledDoc'), ref: { path } }
    case 'draw':
      return { id, kind, pane, title: init.title ?? translate('tab.untitledDraw'), ref: { path } }
    case 'browser':
      return {
        id,
        kind,
        pane,
        title: init.title ?? translate('tab.newPage'),
        ref: {
          url: init.url ?? '',
          ...(init.browserId === undefined ? {} : { browserId: init.browserId }),
          ...(init.profileId === undefined ? {} : { profileId: init.profileId })
        }
      }
    case 'preview':
      return { id, kind, pane, title: init.title ?? translate('tab.preview'), ref: { path } }
    case 'files':
      return { id, kind, pane, title: init.title ?? translate('tab.files'), ref: { path, ...(init.selectedPath === undefined ? {} : { selectedPath: init.selectedPath }) } }
    case 'custom':
      /*
        ★ 自定义编辑器的 Tab **只能由调用方带着身份建**(哪个插件、哪个
        viewType、哪个文件)。`makeTab('custom')` 拿不到那三样,所以这里
        给的是一个**明确无效**的占位:`views/registry.tsx` 认出它并降级成
        只读预览,而不是渲染一个空白格子让人以为编辑器坏了。
      */
      return {
        id,
        kind,
        pane,
        title: init.title ?? translate('tab.preview'),
        ref: { viewType: init.viewType ?? '', pluginId: init.pluginId ?? '', path }
      }
    case 'changes':
      return {
        id,
        kind,
        pane,
        title: init.title ?? translate('tab.changes'),
        ref: {
          runId: init.runId ?? '',
          sessionId: init.sessionId ?? '',
          ...(init.selectedPath === undefined ? {} : { selectedPath: init.selectedPath })
        }
      }
    case 'webapp':
      /*
        ★ 同 `custom`:网页应用 Tab **只能由调用方带着身份建**(哪个插件、
        哪个 webApp、什么地址)。`makeTab('webapp')` 三样都拿不到,所以给的是
        一个明确无效的占位 —— `views/registry.tsx` 认出它并画降级说明,
        而不是渲染一个空 webview 让人以为网站崩了。

        这也是为什么 `+` 菜单里**没有**「新建网页应用」:那是插件带进来的东西,
        不是用户能凭空新建的一种 Tab。
      */
      return {
        id,
        kind,
        pane,
        title: init.title ?? translate('tab.newPage'),
        ref: { pluginId: init.pluginId ?? '', webAppId: init.webAppId ?? '', url: init.url ?? '' }
      }
  }
}

interface TabsState {
  byWorkspace: Record<string, InnerTabState>

  stateOf: (workspaceId: string) => InnerTabState
  /** 某一格里的 Tab,按显示顺序 */
  tabsOf: (workspaceId: string, pane: TabPane) => InnerTab[]
  /** 某一格当前激活的 Tab id —— 两格各存各的 */
  activeIdOf: (workspaceId: string, pane: TabPane) => string | null
  dockOf: (workspaceId: string) => ReturnType<typeof migrateLegacyInnerTabs>
  activateDockGroup: (workspaceId: string, groupId: string) => void
  activateDockTab: (workspaceId: string, groupId: string, tabId: string) => void
  splitAndOpenDock: (workspaceId: string, groupId: string, direction: DockDirection, kind: InnerTabKind, pane?: TabPane) => void
  splitAndMoveDockTab: (workspaceId: string, tabId: string, fromGroupId: string, targetGroupId: string, direction: DockDirection) => void
  openDock: (workspaceId: string, groupId: string, kind: InnerTabKind, init?: TabInit, pane?: TabPane) => void
  moveDockTab: (workspaceId: string, tabId: string, fromGroupId: string, toGroupId: string, index?: number) => void
  reorderDockTab: (workspaceId: string, groupId: string, from: number, to: number) => void
  resizeDock: (workspaceId: string, splitId: string, ratio: number) => void
  closeDockTab: (workspaceId: string, groupId: string, tabId: string) => void
  closeDockGroup: (workspaceId: string, groupId: string) => void
  hydrate: (workspaceId: string, s: InnerTabState) => void
  /**
   * 工作区第一次被看到时:先取回上次的 Tab 布局,没有才开一个新对话
   * ——截图里从来没有空 Tab 条。异步但签名是 void,它挂在 effect 上。
   */
  ensure: (workspaceId: string) => void
  /** 专注会话窗口的临时布局:只放入目标会话,不读取或写回主窗口布局。 */
  openFocusedSession: (workspaceId: string, sessionId: string, title?: string) => void
  open: (workspaceId: string, kind: InnerTabKind, pane?: TabPane, init?: TabInit) => void
  /** 在已有会话上打开一个新 Tab（侧边栏历史会话使用）。 */
  openSession: (workspaceId: string, sessionId: string, title?: string) => void
  removeSessions: (change: Extract<SessionChange, { kind: 'deleted' }>) => void
  /**
   * 在文件树或 Markdown 链接里点一个文件时走这条,**不是 `open`**:文件会进入
   * 当前工作区的右侧工作台；同一个文件已经开着就切过去,不再开第二个。
   * `open` 反过来必须每次都新建(连点两次 `+ 新建对话`要得到两个对话),
   * 所以去重不能塞进它里面。
   */
  openPath: (workspaceId: string, kind: InnerTabKind, path: string, title: string, init?: TabInit) => void
  /**
   * 按路径打开一个文件,**由已装插件决定用谁来打开**。
   *
   * ★ 这是「查插件目录 → 决定 kind」唯一的入口。文件树、Markdown 预览里的
   * 链接、对话里的文件引用,三处以前各自硬编码 `'doc'` —— 于是插件清单里的
   * `contributes.customEditors[].selector[].filenamePattern` 全仓**没有任何一处
   * 读过**,双击 `.excalidraw` 打开的是一屏原始 JSON。分派逻辑只放这一份,
   * 是为了不让三处再分叉。
   *
   * 没有插件认领就退回内置 `doc`;插件被禁用或卸载之后同理 —— `pickCustomEditor`
   * 只看 `isRunnable` 的插件,所以禁用后重开同一个文件会自动退回文本。
   */
  openFile: (workspaceId: string, path: string, title?: string) => void
  /**
   * 点一张子代理卡片 → 在右侧工作区开一个**只读**会话。
   *
   * ★ 为什么不复用 `openPath`:那条路的去重谓词要求 `'path' in t.ref`,而 chat
   * 的 ref 里没有 path —— 直接拿来用的话每点一次都会再开一个 Tab。右侧面板那套
   * 机制(无条件展开、没有 right 分栏就现切一个、已开着但在别的 pane 就搬过来)
   * 是一样的,所以下面是照着它写的,只换了去重的判据和建 Tab 的方式。
   *
   * 也不复用 `openSession`:那条路把会话开在**主区**,而且是可写的。
   */
  /**
   * 子代理卡片点一下走这条:在**右侧工作区**开一个只读会话。
   *
   * `parent` 是那张卡片在父转录里的坐标(父会话 id + `Task` 那次调用的 callId)。
   * 它会跟着 Tab 一起落盘 —— 身份栏要显示的格子一个都不在子会话自己的转录里,
   * 见 `shared/domain/tab.ts` 的 `subagentOf`。
   */
  openSubagentSession: (
    workspaceId: string,
    sessionId: string,
    title: string,
    parent: { sessionId: string; callId: string }
  ) => void
  /**
   * 回合底部那张改动审查卡点一下走这条:在**右侧工作区**开这一轮的改动审查。
   *
   * ★ 不能用 `open(ws, 'changes', 'right')`:`open` 找不到 right 分组时会退回
   * `activeGroupId` —— 右侧工作台还没切出来的时候,审查 tab 会落在**主区**、
   * 盖在对话上面。这里跟 `openPath` / `openSubagentSession` 用同一套右侧机制。
   *
   * ★ 一轮只留一张:点第二个文件是**换这张 tab 看哪个 diff**(改 `selectedPath`),
   * 不是再开一张一模一样的 —— 否则展开一个 11 个文件的改动集挨个点,
   * 右边会攒出 11 个同名「改动」。
   */
  openChangeReview: (workspaceId: string, runId: string, sessionId?: string, selectedPath?: string) => void
  /**
   * 侧边栏那颗「新建对话」走这条,**也不是 `open`**。
   *
   * 区别在于这两颗按钮问的是**不同的问题**:Tab 条上的 `+ 新建对话` 说的是
   * 「再给我一个」(连点两次要得到两个,所以它必须走 `open`);侧边栏那颗说的是
   * 「我要开始一段新对话」—— 手边已经摊着一张没写过字的白纸时,再抽一张出来
   * 只会攒下一排一模一样的「新对话」,而用户以为自己什么也没做成。
   *
   * 所以:主区里已经有一个**没用过**的对话就切过去,没有才新建。
   * 「没用过」的判据是 `isSessionUntouched`,和 `ChatView` 用来决定画不画
   * 问候语那一屏的是同一个 —— 屏幕上是白纸的,这里就当白纸。
   */
  newChat: (workspaceId: string) => void
  /**
   * 给一个草稿聊天 Tab 铸出真正的 sessionId。**这是「白纸」变成「一段会话」的唯一入口。**
   *
   * 调它的只有两处,而且都在 `ChatView`:发出第一条消息、贴上第一个附件。
   * 前者显然;后者是被逼的 —— `main/db/repo.ts` 的 `recordMessageAttachments` 里
   * 有一句 `if (loc.scope !== 'session' || loc.ownerId !== session.id) return`,
   * 那是一道安全边界(渲染层不能把别的会话的图 re-home 到这条消息上)。附件在
   * 草稿键下上传的话,它的 `ncw://` owner 就永远不等于后来的会话 id,于是这张图
   * **被静默丢弃**:发送时看着好好的,重启后历史里那张图凭空消失,且没有任何报错。
   * 要么绑定时把文件和表行整体搬家(一套会部分失败的机器),要么贴图那一刻就铸 id。
   *
   * ★ **必须幂等。** 上面两条路径可能先后发生(先贴图再发送),第二次调用要拿到
   * 同一个 id,否则第二次会把第一次的附件甩掉。
   *
   * ★ 铸了 id **不等于**库里有了记录。那一行仍然只由主进程 `runAgent` 的
   * `ensureSession` 建 —— 所以「贴了图但没发送」的 Tab 有 id、有 URL、
   * 有磁盘上的草稿附件,而侧边栏里没有它。这正是我们要的。
   *
   * 返回 null = 这个 tabId 不存在或者不是 chat。
   */
  bindChatSession: (workspaceId: string, tabId: string) => string | null
  activate: (workspaceId: string, tabId: string) => void
  close: (workspaceId: string, tabId: string) => void
  applyFileMutation: (req: WorkspaceFileMutationRequest) => void
  /** `from`/`to` 是**该格内**的下标,见 shared/domain/tab.ts 的 reorderInPane */
  move: (workspaceId: string, from: number, to: number, pane?: TabPane) => void
  rename: (workspaceId: string, tabId: string, title: string) => void
  /** Session metadata is authoritative across windows; updating it never changes focus/layout. */
  syncSessionTitle: (workspaceId: string, sessionId: string, title: string) => void
  setBrowser: (workspaceId: string, tabId: string, patch: { url?: string; title?: string; browserId?: string; profileId?: string }) => void
  syncBrowserTabs: (
    workspaceId: string,
    tabs: ReadonlyArray<{
      id: string
      clientTabId?: string
      ownerSessionId?: string
      url: string
      title: string
      source: 'user' | 'agent'
      profileId?: string
    }>
  ) => void
  /** 把已经存在的 Tab 顶到它所在 Dock 组的最前面(不改 activeGroupId) */
  revealTab: (workspaceId: string, tabId: string) => void
  /** 工作区关闭时销毁:Tab 表和它那些会话的转录一起放掉(正在跑的除外) */
  forget: (workspaceId: string) => void
}

/**
 * 「哪一格的激活项」这件事只在这里分支一次。
 *
 * 三格的激活项存在**不同字段**上(`activeTabId` / `bottomActiveTabId` /
 * `rightActiveTabId`),因为它们是同时可见的三条 Tab 条 —— 合成一个字段的话,
 * 点一下底部的终端就会把主区正在看的对话取消激活。
 */
function withActive(s: InnerTabState, pane: TabPane, id: string | null): InnerTabState {
  if (pane === 'bottom') return { ...s, bottomActiveTabId: id }
  if (pane === 'right') return { ...s, rightActiveTabId: id }
  return { ...s, activeTabId: id }
}

function activeIn(s: InnerTabState, pane: TabPane): string | null {
  if (pane === 'bottom') return s.bottomActiveTabId ?? null
  if (pane === 'right') return s.rightActiveTabId ?? null
  return s.activeTabId
}

function groupForPane(dock: ReturnType<typeof migrateLegacyInnerTabs>, pane: TabPane): string | null {
  const visit = (node: DockNode): string | null => {
    if (node.type === 'group') {
      const members = dock.tabs.filter((tab) => node.tabIds.includes(tab.id))
      if (members.length > 0 && members.every((tab) => paneOf(tab) === pane)) return node.id
      return null
    }
    return visit(node.first) ?? visit(node.second)
  }
  return visit(dock.root)
}

function groupContainingTab(dock: ReturnType<typeof migrateLegacyInnerTabs>, tabId: string): string | null {
  const visit = (node: DockNode): string | null => {
    if (node.type === 'group') return node.tabIds.includes(tabId) ? node.id : null
    return visit(node.first) ?? visit(node.second)
  }
  return visit(dock.root)
}

/**
 * 右侧工作台的落点分组;右边还没被切出来过就**当场切一个**。
 *
 * ★ 新代码走这条,不要用 `open(ws, kind, 'right')`:`open` 找不到 right 分组时
 *   会退回 `activeGroupId` —— 右侧工作台从没开过的时候,tab 会落在**主区**、
 *   盖在对话上面。`openPath`(:742)和 `openSubagentSession`(:813)各手抄了一份
 *   同样的逻辑,这里没顺手改它们,只是不再抄第三遍。
 *
 * 返回的 `dock` 可能是切分之后的新 dock,调用方**必须接着用返回的这个**。
 */
function rightGroup(
  dock: ReturnType<typeof migrateLegacyInnerTabs>
): { dock: ReturnType<typeof migrateLegacyInnerTabs>; groupId: string | null } {
  const existing = groupForPane(dock, 'right')
  if (existing !== null) return { dock, groupId: existing }
  const base = dock.activeGroupId ?? (dock.root.type === 'group' ? dock.root.id : null)
  if (base === null) return { dock, groupId: null }
  const split = splitGroup(dock, base, 'right')
  return { dock: split, groupId: split.activeGroupId }
}

/** A split inherits the pane its anchor group sits in, never the drag direction. */
function paneOfGroup(dock: ReturnType<typeof migrateLegacyInnerTabs>, groupId: string): TabPane {
  const node = findGroup(dock.root, groupId)
  if (node === null) return 'main'
  if (node.pinned === 'right') return 'right'
  const members = dock.tabs.filter((tab) => node.tabIds.includes(tab.id))
  const head = members[0]
  return head !== undefined && members.every((tab) => paneOf(tab) === paneOf(head)) ? paneOf(head) : 'main'
}

export const useTabsStore = create<TabsState>((set, get) => {
  const dockCache = new Map<string, { source: InnerTabState; dock: ReturnType<typeof migrateLegacyInnerTabs> }>()
  const withDock = (state: InnerTabState): InnerTabState => {
    let dock = normalizeDockState(state.dock ?? migrateLegacyInnerTabs(state), state.tabs)
    const present = new Set<string>()
    const collect = (node: DockNode): void => {
      if (node.type === 'group') { node.tabIds.forEach((id) => present.add(id)); return }
      collect(node.first); collect(node.second)
    }
    collect(dock.root)
    // Legacy callers and browser events can add a tab to `tabs` before the
    // Dock tree knows about it. Attach those orphans to the matching pane
    // group so the next render never leaves a tab unmounted.
    for (const tab of state.tabs) {
      if (present.has(tab.id)) continue
      const wanted = paneOf(tab)
      const groups: Array<{ id: string; pane: TabPane | null }> = []
      const visit = (node: DockNode): void => {
        if (node.type === 'group') {
          const members = state.tabs.filter((item) => node.tabIds.includes(item.id))
          groups.push({ id: node.id, pane: members[0] && members.every((item) => paneOf(item) === paneOf(members[0]!)) ? paneOf(members[0]!) : null })
          return
        }
        visit(node.first); visit(node.second)
      }
      visit(dock.root)
      const target = groups.find((group) => group.pane === wanted)?.id ?? groups.find((group) => group.pane === 'main')?.id
      if (target === undefined) continue
      const add = (node: DockNode): DockNode => {
        if (node.type === 'group') return node.id === target ? { ...node, tabIds: [...node.tabIds, tab.id], activeTabId: node.activeTabId ?? tab.id, hidden: false } : node
        return { ...node, first: add(node.first), second: add(node.second) }
      }
      dock = { ...dock, root: add(dock.root) }
      present.add(tab.id)
    }
    // Old activation fields are still accepted during migration. Once a
    // Dock snapshot exists, the Dock group's active id is authoritative.
    if (state.dock === undefined) {
      const activeIds = [state.activeTabId, state.bottomActiveTabId ?? null, state.rightActiveTabId ?? null].filter((id): id is string => id !== null)
      const activate = (node: DockNode): DockNode => {
        if (node.type === 'group') {
          const id = activeIds.find((candidate) => node.tabIds.includes(candidate))
          return id === undefined ? node : { ...node, activeTabId: id }
        }
        return { ...node, first: activate(node.first), second: activate(node.second) }
      }
      dock = { ...dock, root: activate(dock.root) }
    }
    // Recover older layouts where every chat was moved into a collapsible
    // panel. Reuse one of those sessions in the main pane so hiding the right
    // workbench cannot hide the entire conversation area.
    const chats = dock.tabs.filter((tab) => tab.kind === 'chat')
    if (chats.length > 0 && !chats.some((tab) => paneOf(tab) === 'main')) {
      const chat = chats[0]!
      const source = groupContainingTab(dock, chat.id)
      let destination = groupForPane(dock, 'main')
      if (source !== null && destination === null) {
        dock = splitGroup(dock, source, 'left')
        destination = dock.activeGroupId
      }
      if (source !== null && destination !== null) {
        dock = moveDockTab(dock, chat.id, source, destination)
        return { ...state, activeTabId: chat.id, tabs: dock.tabs, dock }
      }
    }
    return { ...state, tabs: dock.tabs, dock }
  }
  /**
   * 需求:右侧工作台 / 底部面板里最后一个 Tab 没了之后,开关必须跟着落回收起态。
   *
   * 那一格空掉以后 `visibleDockNode` 就把它当空组滤掉了(见 shell/dock-layout.ts),
   * 屏幕上什么都没有,而 `rightPanelOpen` 还留着 true。不满足会怎样:表现为
   * 「展开右侧工作区要点两下」—— 第一下只是把这个已经没有画面的 true 翻成 false
   * (看上去毫无反应),第二下才真的展开(`AppShell.toggleDockEdge` 只在 opening
   * 那一支才补 Tab),期间顶栏那颗开关一直亮着「已展开」。
   *
   * ★ 只同步 **有 → 没有** 这个跳变,不能无条件写 false:`openPath` /
   * `openSubagentSession` / `openChangeReview` 都是**先**掀开面板、**再** write
   * 补 Tab 的,无条件写会把它们刚打开的开关当场抹掉。
   */
  const syncEmptyEdgePanels = (workspaceId: string, before: readonly InnerTab[], after: readonly InnerTab[]): void => {
    const win = shellWindow()
    if (win === null) return
    for (const pane of ['right', 'bottom'] as const) {
      if (tabsInPane(before, pane).length === 0 || tabsInPane(after, pane).length > 0) continue
      if (pane === 'right') win.setRightPanelForWorkspace(workspaceId, false)
      else win.setBottomPanelForWorkspace(workspaceId, false)
    }
  }
  const write = (workspaceId: string, next: InnerTabState): void => {
    const before = get().byWorkspace[workspaceId]?.tabs ?? []
    const normalized = withDock(next)
    set({ byWorkspace: { ...get().byWorkspace, [workspaceId]: normalized } })
    // 专注会话窗口的工作台只在本窗口有效，不能覆盖主窗口的项目布局。
    shellWindow()?.persistInnerTabs(workspaceId, normalized)
    syncEmptyEdgePanels(workspaceId, before, normalized.tabs)
  }

  /** 正在取持久化布局的工作区。`ensure` 挂在 effect 上,会被重入。 */
  const loading = new Set<string>()
  const pendingTitles = new Map<string, Map<string, string>>()
  // Agent events can arrive for a workspace that is not currently visible.
  // Keep them pending until that workspace's persisted inner tabs are hydrated,
  // otherwise a background browser event could overwrite its chat tabs.
  const pendingBrowser = new Map<string, readonly BrowserTabSyncItem[]>()

  function applyPendingBrowser(workspaceId: string): void {
    const pending = pendingBrowser.get(workspaceId)
    if (pending === undefined) return
    pendingBrowser.delete(workspaceId)
    get().syncBrowserTabs(workspaceId, pending)
  }

  async function loadOrSeed(workspaceId: string): Promise<void> {
    const snapshot = await getInnerTabs(workspaceId)
    const renamed = pendingTitles.get(workspaceId)
    const persisted = withDock({ ...snapshot, tabs: snapshot.tabs.map((tab) => {
      const title = tab.kind === 'chat' && tab.ref.sessionId !== null ? renamed?.get(tab.ref.sessionId) : undefined
      return title === undefined ? tab : { ...tab, title }
    }) })
    // 这一趟 IPC 期间用户可能已经自己开了一个 Tab —— 那份是新的,别覆盖它
    if (get().byWorkspace[workspaceId] !== undefined) {
      applyPendingBrowser(workspaceId)
      return
    }
    if (persisted.tabs.length > 0) {
      set({ byWorkspace: { ...get().byWorkspace, [workspaceId]: persisted } })
      applyPendingBrowser(workspaceId)
      return
    }
    const tab = makeTab('chat', 'main')
    write(workspaceId, {
      tabs: [tab],
      activeTabId: tab.id,
      bottomActiveTabId: null,
      rightActiveTabId: null
    })
    applyPendingBrowser(workspaceId)
  }

  return {
    byWorkspace: {},

    stateOf(workspaceId) {
      return get().byWorkspace[workspaceId] ?? EMPTY
    },

    tabsOf(workspaceId, pane) {
      return tabsInPane(get().stateOf(workspaceId).tabs, pane)
    },

    activeIdOf(workspaceId, pane) {
      return activeIn(get().stateOf(workspaceId), pane)
    },

    dockOf(workspaceId) {
      const state = get().stateOf(workspaceId)
      const cached = dockCache.get(workspaceId)
      if (cached?.source === state) return cached.dock
      const dock = normalizeDockState(state.dock ?? migrateLegacyInnerTabs(state), state.tabs)
      dockCache.set(workspaceId, { source: state, dock })
      return dock
    },

    activateDockGroup(workspaceId, groupId) {
      const cur = get().stateOf(workspaceId)
      const dock = get().dockOf(workspaceId)
      const visit = (node: import('../../../shared/domain/dock').DockNode): boolean =>
        node.type === 'group' ? node.id === groupId : visit(node.first) || visit(node.second)
      if (!visit(dock.root)) return
      if (dock.activeGroupId === groupId) return
      write(workspaceId, { ...cur, dock: { ...dock, activeGroupId: groupId } })
    },

    activateDockTab(workspaceId, groupId, tabId) {
      const cur = get().stateOf(workspaceId)
      const dock = get().dockOf(workspaceId)
      const target = dock.root && (() => {
        const visit = (node: import('../../../shared/domain/dock').DockNode): boolean => {
          if (node.type === 'group') {
            if (node.id !== groupId) return false
            return node.tabIds.includes(tabId)
          }
          return visit(node.first) || visit(node.second)
        }
        return visit(dock.root)
      })()
      if (!target) return
      const update = (node: import('../../../shared/domain/dock').DockNode): import('../../../shared/domain/dock').DockNode => {
        if (node.type === 'group') return node.id === groupId ? { ...node, activeTabId: tabId } : node
        return { ...node, first: update(node.first), second: update(node.second) }
      }
      const tab = cur.tabs.find((item) => item.id === tabId)
      write(workspaceId, withActive({ ...cur, dock: { ...dock, root: update(dock.root), activeGroupId: groupId } }, tab === undefined ? 'main' : paneOf(tab), tabId))
    },

    splitAndOpenDock(workspaceId, groupId, direction, kind, pane = 'main') {
      const cur = get().stateOf(workspaceId)
      const dock = get().dockOf(workspaceId)
      if (!findGroup(dock.root, groupId)) return
      const split = splitGroup(dock, groupId, direction)
      if (split.activeGroupId === null) return
      const tab = makeTab(kind, pane)
      const next = addTabToGroup(split, split.activeGroupId, tab)
      write(workspaceId, { ...cur, tabs: next.tabs, dock: next })
    },

    splitAndMoveDockTab(workspaceId, tabId, fromGroupId, targetGroupId, direction) {
      const cur = get().stateOf(workspaceId)
      const dock = get().dockOf(workspaceId)
      if (!findGroup(dock.root, fromGroupId)?.tabIds.includes(tabId) || !findGroup(dock.root, targetGroupId)) return
      const moving = dock.tabs.find((tab) => tab.id === tabId)
      /*
        ★ 新格属于**目标格所在的那一面板**,不是拖动方向。按方向推 pane 的话,
        在主区往下拖会把这个 Tab 标成 `bottom`,而底部面板默认是收起的 ——
        `visibleDockNode` 会把整格滤掉,表现是「会话拖完就没了,侧栏点它也没反应」。
      */
      const destinationPane: TabPane = paneOfGroup(dock, targetGroupId)
      if (moving?.kind === 'chat' && paneOf(moving) === 'main' && destinationPane !== 'main') {
        const mainChats = dock.tabs.filter((tab) => tab.kind === 'chat' && paneOf(tab) === 'main')
        if (mainChats.length <= 1) return
      }
      const split = splitGroup(dock, targetGroupId, direction)
      const destination = split.activeGroupId
      if (destination === null) return
      const moved = moveDockTab(split, tabId, fromGroupId, destination)
      const next = {
        ...moved,
        tabs: moved.tabs.map((tab) => tab.id === tabId ? { ...tab, pane: destinationPane } : tab)
      }
      write(workspaceId, { ...cur, tabs: next.tabs, dock: next })
    },

    openDock(workspaceId, groupId, kind, init, pane = 'main') {
      const cur = get().stateOf(workspaceId)
      const tab = makeTab(kind, pane, init)
      write(workspaceId, { ...cur, dock: addTabToGroup(get().dockOf(workspaceId), groupId, tab), tabs: [...cur.tabs, tab] })
    },

    moveDockTab(workspaceId, tabId, fromGroupId, toGroupId, index) {
      const cur = get().stateOf(workspaceId)
      const dock = moveDockTab(get().dockOf(workspaceId), tabId, fromGroupId, toGroupId, index)
      if (dock === get().dockOf(workspaceId)) return
      write(workspaceId, { ...cur, tabs: dock.tabs, dock })
    },

    reorderDockTab(workspaceId, groupId, from, to) {
      const cur = get().stateOf(workspaceId)
      write(workspaceId, { ...cur, dock: reorderDockTab(get().dockOf(workspaceId), groupId, from, to) })
    },

    resizeDock(workspaceId, splitId, ratio) {
      const cur = get().stateOf(workspaceId)
      write(workspaceId, { ...cur, dock: resizeSplit(get().dockOf(workspaceId), splitId, ratio) })
    },

    closeDockTab(workspaceId, groupId, tabId) {
      const cur = get().stateOf(workspaceId)
      if (!findGroup(get().dockOf(workspaceId).root, groupId)?.tabIds.includes(tabId)) return
      const target = cur.tabs.find((tab) => tab.id === tabId)
      if (target?.kind === 'chat' && paneOf(target) === 'main' && cur.tabs.filter((tab) => tab.kind === 'chat' && paneOf(tab) === 'main').length <= 1) return
      if (target?.kind === 'terminal') void killTerminal(target.ref.terminalId).catch(() => undefined)
      if (target?.kind === 'browser' && target.ref.browserId !== undefined) void closeBrowserTab(workspaceId, target.ref.browserId).catch(() => undefined)
      const dock = closeDockTab(get().dockOf(workspaceId), groupId, tabId)
      if (dock.tabs.length === 0) {
        const chat = makeTab('chat', 'main')
        write(workspaceId, { ...cur, tabs: [chat], activeTabId: chat.id, dock: addTabToGroup({ ...dock, tabs: [] }, dock.activeGroupId ?? groupId, chat) })
        return
      }
      write(workspaceId, { ...cur, tabs: dock.tabs, dock })
    },

    closeDockGroup(workspaceId, groupId) {
      const cur = get().stateOf(workspaceId)
      const target = get().dockOf(workspaceId).root
      const ids: string[] = []
      const visit = (node: import('../../../shared/domain/dock').DockNode): void => {
        if (node.type === 'group') { if (node.id === groupId) ids.push(...node.tabIds); return }
        visit(node.first); visit(node.second)
      }
      visit(target)
      const remainingMainChats = cur.tabs.filter((tab) => tab.kind === 'chat' && paneOf(tab) === 'main' && !ids.includes(tab.id))
      if (remainingMainChats.length === 0) return
      for (const id of ids) {
        const tab = cur.tabs.find((item) => item.id === id)
        if (tab?.kind === 'terminal') void killTerminal(tab.ref.terminalId).catch(() => undefined)
        if (tab?.kind === 'browser' && tab.ref.browserId !== undefined) void closeBrowserTab(workspaceId, tab.ref.browserId).catch(() => undefined)
      }
      const dock = closeDockGroupState(get().dockOf(workspaceId), groupId)
      if (dock.tabs.length === 0) {
        const chat = makeTab('chat', 'main')
        write(workspaceId, { ...cur, tabs: [chat], activeTabId: chat.id, dock: addTabToGroup({ ...dock, tabs: [] }, dock.activeGroupId ?? groupId, chat) })
      } else write(workspaceId, { ...cur, tabs: dock.tabs, dock })
    },

    hydrate(workspaceId, s) {
      // Keep hydrate's object identity for callers that use it as an in-memory
      // snapshot (and let dockOf cache/normalize the derived Dock view).
      const chats = s.tabs.filter((tab) => tab.kind === 'chat')
      const state = chats.length > 0 && !chats.some((tab) => paneOf(tab) === 'main') ? withDock(s) : s
      set({ byWorkspace: { ...get().byWorkspace, [workspaceId]: state } })
      applyPendingBrowser(workspaceId)
    },

    ensure(workspaceId) {
      if (get().byWorkspace[workspaceId] !== undefined || loading.has(workspaceId)) return
      if (shellWindow()?.windowKind() === 'quick') {
        const chat = makeTab('chat', 'main')
        write(workspaceId, { tabs: [chat], activeTabId: chat.id, bottomActiveTabId: null, rightActiveTabId: null })
        return
      }
      loading.add(workspaceId)
      void loadOrSeed(workspaceId)
        .catch((err: unknown) => {
          // 读不回来就当全新工作区处理 —— 空 Tab 条比一个错误提示更没得做
          console.error('[tabs] 内层 Tab 布局读取失败,按新工作区处理:', err)
          if (get().byWorkspace[workspaceId] === undefined) {
            const tab = makeTab('chat', 'main')
            write(workspaceId, {
              tabs: [tab],
              activeTabId: tab.id,
              bottomActiveTabId: null,
              rightActiveTabId: null
            })
          }
        })
        .finally(() => {
          loading.delete(workspaceId)
          pendingTitles.delete(workspaceId)
          applyPendingBrowser(workspaceId)
        })
    },

    openFocusedSession(workspaceId, sessionId, title) {
      const current = get().byWorkspace[workspaceId]
      const first = current?.tabs.length === 1 ? current.tabs[0] : undefined
      if (current !== undefined && first?.kind === 'chat' && first.ref.sessionId === sessionId) {
        if (title !== undefined && first.title !== title) {
          set({ byWorkspace: { ...get().byWorkspace, [workspaceId]: { ...current, tabs: [{ ...first, title }] } } })
        }
        return
      }
      const chat: InnerTab = {
        id: ulid(),
        kind: 'chat',
        pane: 'main',
        title: title?.trim() ?? '',
        ref: { sessionId }
      }
      const state = withDock({
        tabs: [chat],
        activeTabId: chat.id,
        bottomActiveTabId: null,
        rightActiveTabId: null
      })
      dockCache.delete(workspaceId)
      set({ byWorkspace: { ...get().byWorkspace, [workspaceId]: state } })
      applyPendingBrowser(workspaceId)
    },

    open(workspaceId, kind, pane = 'main', init) {
      const cur = get().stateOf(workspaceId)
      const tab = makeTab(kind, pane, init)
      const dock = get().dockOf(workspaceId)
      const groupId = groupForPane(dock, pane) ?? dock.activeGroupId ?? (dock.root.type === 'group' ? dock.root.id : null)
      if (groupId === null) return
      const nextDock = addTabToGroup(dock, groupId, tab)
      write(workspaceId, withActive({ ...cur, tabs: nextDock.tabs, dock: nextDock }, pane, tab.id))
    },

    openSession(workspaceId, sessionId, title) {
      const cur = get().stateOf(workspaceId)
      const existing = cur.tabs.find((t) => t.kind === 'chat' && t.ref.sessionId === sessionId)
      if (existing !== undefined) {
        // The conversation may live in a collapsed panel; activating it alone
        // would look like the sidebar click did nothing.
        if (paneOf(existing) === 'right') shellWindow()?.setRightPanelForWorkspace(workspaceId, true)
        if (paneOf(existing) === 'bottom') shellWindow()?.setBottomPanelForWorkspace(workspaceId, true)
        const groupId = groupContainingTab(get().dockOf(workspaceId), existing.id)
        if (groupId) get().activateDockTab(workspaceId, groupId, existing.id)
        return
      }
      // Build the tab directly from the existing session id. Calling makeTab
      // first creates a second, throw-away id (and used to make opening a
      // history item look like it created a new conversation).
      const chat: InnerTab = {
        id: ulid(),
        kind: 'chat',
        pane: 'main',
        title: title?.trim() ?? '',
        ref: { sessionId }
      }
      const dock = get().dockOf(workspaceId)
      const groupId = groupForPane(dock, 'main') ?? dock.activeGroupId ?? (dock.root.type === 'group' ? dock.root.id : null)
      if (groupId === null) return
      const nextDock = addTabToGroup(dock, groupId, chat)
      write(workspaceId, withActive({ ...cur, tabs: nextDock.tabs, dock: nextDock }, 'main', chat.id))
    },

    removeSessions(change) {
      const deleted = new Set(change.sessionIds)
      for (const [workspaceId, state] of Object.entries(get().byWorkspace)) {
        const affected = state.tabs.filter((tab) => tab.kind === 'chat' && tab.ref.sessionId !== null && deleted.has(tab.ref.sessionId))
        if (affected.length === 0) continue
        const replacement = change.replacement
        if (affected.some((tab) => tab.id === state.activeTabId)
          && replacement?.workspaceId === workspaceId && !deleted.has(replacement.id)) {
          get().openSession(workspaceId, replacement.id, replacement.title)
        }
        for (const tab of affected) get().close(workspaceId, tab.id)
      }
    },

    openPath(workspaceId, kind, path, title, init) {
      const cur = get().stateOf(workspaceId)
      // 文件打开属于当前工作区的右侧工作台。后台工作区也只展开自己的面板，
      // 不会改变窗口当前正在看的工作区。
      shellWindow()?.setRightPanelForWorkspace(workspaceId, true)
      let dock = get().dockOf(workspaceId)
      /*
        ★ **只按 path 去重,不比 kind。** 同一个文件可能被不同的 kind 打开
        (插件的 `custom` 画布 / 内置的 `doc` 文本),比上 kind 的话,禁用插件之后
        重开同一个文件会在旁边再开一个标签,两个标签指着同一个文件各editing各的。

        `files` 是唯一的例外:它的 `ref.path` 是**目录根**,不是被打开的文件,
        撞上同名会把文件树标签认成那个文件。
      */
      const existing = cur.tabs.find(
        (t) => t.kind !== 'files' && 'path' in t.ref && t.ref.path === path
      )
      if (existing !== undefined) {
        const sourceGroupId = groupContainingTab(dock, existing.id)
        if (sourceGroupId === null) return
        if (paneOf(existing) === 'right') {
          get().activateDockTab(workspaceId, sourceGroupId, existing.id)
          return
        }
        // Layouts persisted before file tabs moved to the right may still
        // contain this document in the main pane. Move that existing tab so
        // opening it again does not leave a duplicate behind.
        let targetGroupId = groupForPane(dock, 'right')
        if (targetGroupId === null) {
          const base = dock.activeGroupId ?? (dock.root.type === 'group' ? dock.root.id : null)
          if (base === null) return
          dock = splitGroup(dock, base, 'right')
          targetGroupId = dock.activeGroupId
        }
        if (targetGroupId === null) return
        const movedDock = moveDockTab(dock, existing.id, sourceGroupId, targetGroupId)
        // An empty group has no pane hint for the shared move helper to infer;
        // this destination is explicitly the right workbench.
        const nextDock = {
          ...movedDock,
          tabs: movedDock.tabs.map((tab) => tab.id === existing.id ? { ...tab, pane: 'right' as const } : tab)
        }
        write(workspaceId, withActive({ ...cur, tabs: nextDock.tabs, dock: nextDock }, 'right', existing.id))
        return
      }
      const tab = makeTab(kind, 'right', { ...init, path, title })
      let groupId = groupForPane(dock, 'right')
      // If the right side has never been created, split it now so a file link
      // opened from a chat is immediately visible in the right workbench.
      if (groupId === null) {
        const base = dock.activeGroupId ?? (dock.root.type === 'group' ? dock.root.id : null)
        if (base === null) return
        dock = splitGroup(dock, base, 'right')
        groupId = dock.activeGroupId
      }
      if (groupId === null) return
      const nextDock = addTabToGroup(dock, groupId, tab)
      write(workspaceId, withActive({ ...cur, tabs: nextDock.tabs, dock: nextDock }, 'right', tab.id))
    },

    openFile(workspaceId, path, title) {
      const label = title ?? path.split('/').pop() ?? path
      /*
        ★ 目录现取现用,不缓存:插件可以在任意时刻被启用 / 禁用 / 卸载,
        而这三件事都只换掉 store 里的整份 catalog(见 `stores/plugins.ts`)。
        缓存一份的话,刚装好的插件要等到下一次刷新才接管得了文件。
      */
      const editor = pickCustomEditor(usePluginsStore.getState().catalog.plugins, path)
      if (editor === null) {
        get().openPath(workspaceId, 'doc', path, label)
        return
      }
      get().openPath(workspaceId, 'custom', path, label, {
        pluginId: editor.pluginId,
        viewType: editor.viewType
      })
    },

    openSubagentSession(workspaceId, sessionId, title, parent) {
      const cur = get().stateOf(workspaceId)
      // 和 `openPath` 一样:后台工作区也只展开它自己的面板,不改当前在看的工作区
      shellWindow()?.setRightPanelForWorkspace(workspaceId, true)
      let dock = get().dockOf(workspaceId)
      const existing = cur.tabs.find((t) => t.kind === 'chat' && t.ref.sessionId === sessionId)
      if (existing !== undefined) {
        const sourceGroupId = groupContainingTab(dock, existing.id)
        if (sourceGroupId === null) return
        if (paneOf(existing) === 'right') {
          get().activateDockTab(workspaceId, sourceGroupId, existing.id)
          return
        }
        let targetGroupId = groupForPane(dock, 'right')
        if (targetGroupId === null) {
          const base = dock.activeGroupId ?? (dock.root.type === 'group' ? dock.root.id : null)
          if (base === null) return
          dock = splitGroup(dock, base, 'right')
          targetGroupId = dock.activeGroupId
        }
        if (targetGroupId === null) return
        const movedDock = moveDockTab(dock, existing.id, sourceGroupId, targetGroupId)
        // 空分组没有 pane 提示可供推断,这里的目的地明确就是右侧工作台
        const nextDock = {
          ...movedDock,
          tabs: movedDock.tabs.map((tab) => tab.id === existing.id ? { ...tab, pane: 'right' as const } : tab)
        }
        write(workspaceId, withActive({ ...cur, tabs: nextDock.tabs, dock: nextDock }, 'right', existing.id))
        return
      }
      // 手搓字面量而不是 `makeTab`:那个函数会多铸一个随即丢掉的 id,
      // 而且它不认识 `readOnly`。和 `openSession` 同一个理由。
      const chat: InnerTab = {
        id: ulid(),
        kind: 'chat',
        pane: 'right',
        title: title.trim(),
        ref: { sessionId, readOnly: true, subagentOf: parent }
      }
      let groupId = groupForPane(dock, 'right')
      if (groupId === null) {
        const base = dock.activeGroupId ?? (dock.root.type === 'group' ? dock.root.id : null)
        if (base === null) return
        dock = splitGroup(dock, base, 'right')
        groupId = dock.activeGroupId
      }
      if (groupId === null) return
      const nextDock = addTabToGroup(dock, groupId, chat)
      write(workspaceId, withActive({ ...cur, tabs: nextDock.tabs, dock: nextDock }, 'right', chat.id))
    },

    openChangeReview(workspaceId, runId, sessionId, selectedPath) {
      const cur = get().stateOf(workspaceId)
      // 和 `openPath` 一样:后台工作区也只展开它自己的面板
      shellWindow()?.setRightPanelForWorkspace(workspaceId, true)
      const dock = get().dockOf(workspaceId)
      const existing = cur.tabs.find(
        (t): t is Extract<InnerTab, { kind: 'changes' }> => t.kind === 'changes' && t.ref.runId === runId
      )
      // 换一个文件看 = 改这张 tab 的 selectedPath(`ChangeReviewTab` 的 effect 认它)
      const retarget = (list: readonly InnerTab[]): InnerTab[] =>
        list.map((t) =>
          t.id === existing?.id && t.kind === 'changes' && selectedPath !== undefined
            ? { ...t, ref: { ...t.ref, selectedPath } }
            : t
        )
      if (existing !== undefined) {
        const sourceGroupId = groupContainingTab(dock, existing.id)
        if (sourceGroupId === null) return
        if (paneOf(existing) === 'right') {
          if (selectedPath !== undefined) write(workspaceId, { ...cur, tabs: retarget(cur.tabs) })
          get().activateDockTab(workspaceId, sourceGroupId, existing.id)
          return
        }
        // 老布局(或用户自己拖过去的)可能把它留在主区,重开时搬回右侧,不留重复
        const placed = rightGroup(dock)
        if (placed.groupId === null) return
        const movedDock = moveDockTab(placed.dock, existing.id, sourceGroupId, placed.groupId)
        // 空分组没有 pane 提示可供推断,这里的目的地明确就是右侧工作台
        const tabs = retarget(movedDock.tabs).map((tab) =>
          tab.id === existing.id ? { ...tab, pane: 'right' as const } : tab
        )
        write(workspaceId, withActive({ ...cur, tabs, dock: { ...movedDock, tabs } }, 'right', existing.id))
        return
      }
      const tab = makeTab('changes', 'right', {
        runId,
        ...(sessionId === undefined ? {} : { sessionId }),
        ...(selectedPath === undefined ? {} : { selectedPath })
      })
      const placed = rightGroup(dock)
      if (placed.groupId === null) return
      const nextDock = addTabToGroup(placed.dock, placed.groupId, tab)
      write(workspaceId, withActive({ ...cur, tabs: nextDock.tabs, dock: nextDock }, 'right', tab.id))
    },

    newChat(workspaceId) {
      const cur = get().stateOf(workspaceId)
      const chats = tabsInPane(cur.tabs, 'main').filter(
        (t): t is Extract<InnerTab, { kind: 'chat' }> => t.kind === 'chat'
      )
      /*
        ★ **只有还没绑定会话的草稿 Tab 才可能是白纸。**
        `isSessionUntouched` 查的是渲染层的 store 注册表,而那些 store 是懒创建的
        —— 重启后布局从盘里读回来,除了当前那一个,其余 chat Tab 都还没挂载过,
        注册表里查不到,于是「查不到就是没碰过」把**跑过一整段对话的会话**也算成了
        白纸。症状就是点「新建对话」跳进一个早就执行过的会话。
        绑过 sessionId 就说明它至少发过一条消息或贴过一个附件(见 `bindChatSession`),
        无论 store 在不在,它都不是白纸。

        先看当前这一个 —— 已经站在一张白纸前时,这一下应该什么也不发生,
        而不是切到另一张同样空白的纸上(那看着像点错了)。
        否则取**最靠后**的那一个:`open` 是往后追加的,所以上一次「新建对话」
        给出来的就是最后那一个。
      */
      const active = activeIn(cur, 'main')
      const blank = (t: Extract<InnerTab, { kind: 'chat' }>): boolean =>
        t.ref.sessionId === null && isSessionUntouched(chatKey(t))
      const reusable =
        chats.find((t) => t.id === active && blank(t)) ??
        [...chats].reverse().find(blank)

      if (reusable === undefined) {
        get().open(workspaceId, 'chat')
        return
      }
      get().activate(workspaceId, reusable.id)
    },

    bindChatSession(workspaceId, tabId) {
      const cur = get().stateOf(workspaceId)
      const target = cur.tabs.find((t) => t.id === tabId)
      if (target === undefined || target.kind !== 'chat') return null
      if (target.ref.sessionId !== null) return target.ref.sessionId

      const sessionId = ulid()
      // 搬家在写 ref **之前**:写完 ref 会触发一轮渲染,而 `views/registry.tsx`
      // 的 key 挂在 chatKey 上 —— ChatView 重挂时新键的 store 里必须已经有草稿了,
      // 否则输入框会先闪一帧空白。
      adoptDraftSession(tabId, sessionId)
      const tabs = cur.tabs.map((t) =>
        t.id === tabId && t.kind === 'chat' ? { ...t, ref: { sessionId } } : t
      )
      write(workspaceId, { ...cur, tabs })
      return sessionId
    },

    activate(workspaceId, tabId) {
      const cur = get().stateOf(workspaceId)
      const tab = cur.tabs.find((t) => t.id === tabId)
      if (tab === undefined) return
      const groupId = groupContainingTab(get().dockOf(workspaceId), tabId)
      if (groupId === null) return
      const dock = get().dockOf(workspaceId)
      const activate = (node: DockNode): DockNode => {
        if (node.type === 'group') return node.id === groupId ? { ...node, activeTabId: tabId } : node
        return { ...node, first: activate(node.first), second: activate(node.second) }
      }
      write(workspaceId, withActive({ ...cur, dock: { ...dock, root: activate(dock.root), activeGroupId: groupId } }, paneOf(tab), tabId))
    },

    close(workspaceId, tabId) {
      const cur = get().stateOf(workspaceId)
      const target = cur.tabs.find((t) => t.id === tabId)
      if (target === undefined) return
      const pane = paneOf(target)

      // A terminal is a real PTY owned by the main process. Closing its tab is
      // the explicit lifecycle boundary; switching tabs only unmounts xterm
      // and intentionally keeps the shell (and its scrollback) alive.
      if (target.kind === 'terminal') void killTerminal(target.ref.terminalId).catch(() => undefined)
      if (target.kind === 'browser' && target.ref.browserId !== undefined) {
        void closeBrowserTab(workspaceId, target.ref.browserId).catch(() => undefined)
      }

      // 下一个激活项在**本格内**顺位递补 —— 关掉底部的终端不该跳到主区的对话上
      const siblings = tabsInPane(cur.tabs, pane)
      const idx = siblings.findIndex((t) => t.id === tabId)
      const rest = siblings.filter((t) => t.id !== tabId)
      const nextActive =
        activeIn(cur, pane) === tabId
          ? (rest[idx]?.id ?? rest[idx - 1]?.id ?? null)
          : activeIn(cur, pane)

      const tabs = cur.tabs.filter((t) => t.id !== tabId)

      /*
        「关掉最后一个就补一个空对话」**只对主区成立**。底部那条空掉是合法状态 ——
        它对应「把底部面板关掉」这个动作(收起开关由上面的 `syncEmptyEdgePanels`
        在这一格空掉时同步;原注释说的是 AppShell,那条逻辑已经不在 AppShell 里了)。
        照搬到底部的话,面板就永远关不掉:关一个补一个。
      */
      if (pane === 'main' && !tabs.some((tab) => tab.kind === 'chat' && paneOf(tab) === 'main')) {
        const tab = makeTab('chat', 'main')
        // Session cleanup can close a chat while document tabs remain. Keep
        // the main pane usable by replacing the removed session with a draft.
        write(workspaceId, { ...cur, tabs: [...tabs, tab], activeTabId: tab.id })
        return
      }
      write(workspaceId, withActive({ ...cur, tabs }, pane, nextActive))
    },

    move(workspaceId, from, to, pane = 'main') {
      const cur = get().stateOf(workspaceId)
      write(workspaceId, { ...cur, tabs: reorderInPane(cur.tabs, pane, from, to) })
    },

    rename(workspaceId, tabId, title) {
      const cur = get().stateOf(workspaceId)
      const tabs = cur.tabs.map((t) => (t.id === tabId ? { ...t, title } : t))
      write(workspaceId, { ...cur, tabs })
    },

    syncSessionTitle(workspaceId, sessionId, title) {
      if (loading.has(workspaceId)) {
        const pending = pendingTitles.get(workspaceId) ?? new Map<string, string>()
        pending.set(sessionId, title)
        pendingTitles.set(workspaceId, pending)
      }
      const current = get().byWorkspace[workspaceId]
      if (current === undefined) return
      let changed = false
      const tabs = current.tabs.map((tab) => {
        if (tab.kind !== 'chat' || tab.ref.sessionId !== sessionId || tab.title === title) return tab
        changed = true
        return { ...tab, title }
      })
      // No layout write: another window may have a newer tab order. Reloads
      // resolve titles from sessions through tabs:getInner instead.
      if (changed) set({ byWorkspace: { ...get().byWorkspace, [workspaceId]: { ...current, tabs } } })
    },

    setBrowser(workspaceId, tabId, patch) {
      const cur = get().stateOf(workspaceId)
      const tabs = cur.tabs.map((tab) => {
        if (tab.id !== tabId || tab.kind !== 'browser') return tab
        return {
          ...tab,
          ...(patch.title === undefined ? {} : { title: patch.title }),
          ref: {
            ...tab.ref,
            ...(patch.url === undefined ? {} : { url: patch.url }),
            ...(patch.browserId === undefined ? {} : { browserId: patch.browserId }),
            ...(patch.profileId === undefined ? {} : { profileId: patch.profileId })
          }
        }
      })
      write(workspaceId, { ...cur, tabs })
    },

    syncBrowserTabs(workspaceId, remoteTabs) {
      if (shellWindow()?.windowKind() === 'quick') {
        const current = get().byWorkspace[workspaceId]
        // 全局浏览器广播不能让专注窗口恢复其他项目或其他会话的工作台。
        if (current === undefined) return
        const sessions = new Set(current.tabs.flatMap((tab) => tab.kind === 'chat' && tab.ref.sessionId !== null ? [tab.ref.sessionId] : []))
        remoteTabs = remoteTabs.filter((remote) =>
          (remote.ownerSessionId !== undefined && sessions.has(remote.ownerSessionId))
          || current.tabs.some((tab) => tab.kind === 'browser' && (tab.ref.browserId === remote.id || tab.id === remote.clientTabId))
        )
      }
      if (get().byWorkspace[workspaceId] === undefined) {
        pendingBrowser.set(workspaceId, [...remoteTabs])
        get().ensure(workspaceId)
        return
      }
      const cur = get().stateOf(workspaceId)
      const remoteIds = new Set(remoteTabs.map((tab) => tab.id))
      let tabs = [...cur.tabs]
      let changed = false
      let activatedAgentTabId: string | null = null
      for (const remote of remoteTabs) {
        const existing = tabs.find(
          (tab): tab is Extract<InnerTab, { kind: 'browser' }> =>
            tab.kind === 'browser' &&
            (
              tab.ref.browserId === remote.id ||
              (
                remote.source === 'user' &&
                remote.clientTabId !== undefined &&
                tab.id === remote.clientTabId
              )
            )
        )
        if (existing !== undefined) {
          if (
            existing.ref.url !== remote.url ||
            existing.title !== remote.title ||
            existing.ref.browserId !== remote.id ||
            existing.ref.profileId !== remote.profileId
          ) {
            tabs = tabs.map((tab) =>
              tab.id === existing.id && tab.kind === 'browser'
                ? {
                    ...tab,
                    title: remote.title,
                    ref: {
                      ...tab.ref,
                      url: remote.url,
                      browserId: remote.id,
                      ...(remote.profileId === undefined ? {} : { profileId: remote.profileId })
                    }
                  }
                : tab
            )
            changed = true
          }
          continue
        }
        // Agent tabs live in the right workbench so they never replace the
        // conversation currently being edited in the main pane.
        const id = remote.source === 'user' && remote.clientTabId !== undefined
          ? remote.clientTabId
          : ulid()
        tabs.push({
          id,
          kind: 'browser',
          pane: remote.source === 'agent' ? 'right' : 'main',
          title: remote.title,
          ref: {
            url: remote.url,
            browserId: remote.id,
            ...(remote.profileId === undefined ? {} : { profileId: remote.profileId })
          }
        })
        if (remote.source === 'agent') activatedAgentTabId = id
        changed = true
      }
      const beforePrune = tabs.length
      tabs = tabs.filter(
        (tab) =>
          tab.kind !== 'browser' ||
          tab.ref.browserId === undefined ||
          remoteIds.has(tab.ref.browserId)
      )
      if (tabs.length !== beforePrune) changed = true
      if (changed) {
        if (tabsInPane(tabs, 'main').length === 0) {
          const chat = makeTab('chat', 'main')
          tabs = [...tabs, chat]
        }
        const main = tabsInPane(tabs, 'main')
        const bottom = tabsInPane(tabs, 'bottom')
        const right = tabsInPane(tabs, 'right')
        const nextMain = main.some((tab) => tab.id === cur.activeTabId) ? cur.activeTabId : main[0]?.id ?? null
        const nextBottom = bottom.some((tab) => tab.id === cur.bottomActiveTabId)
          ? cur.bottomActiveTabId ?? null
          : bottom[0]?.id ?? null
        const nextRight = right.some((tab) => tab.id === cur.rightActiveTabId)
          ? cur.rightActiveTabId ?? null
          : right[0]?.id ?? null
        write(workspaceId, {
          ...cur,
          tabs,
          activeTabId: nextMain,
          bottomActiveTabId: nextBottom,
          rightActiveTabId: activatedAgentTabId ?? nextRight
        })
        if (activatedAgentTabId !== null) get().revealTab(workspaceId, activatedAgentTabId)
      }
    },

    /*
      `write` 只会把新 Tab 挂进 Dock 组,不会抢走那一组已经激活的 Tab
      (右侧那组默认停在「工作区文件」上)。Agent 打开的浏览器必须自己浮到
      前面来,否则它只是多了一个看不见的标签。这里**不动 activeGroupId** ——
      后台开一个页面不该把用户正在打字的那一格的焦点抢过去。
    */
    revealTab(workspaceId, tabId) {
      const cur = get().stateOf(workspaceId)
      const tab = cur.tabs.find((item) => item.id === tabId)
      if (tab === undefined) return
      const dock = get().dockOf(workspaceId)
      const groupId = groupContainingTab(dock, tabId)
      if (groupId === null) return
      const group = findGroup(dock.root, groupId)
      if (group === null || group.activeTabId === tabId) return
      const update = (node: DockNode): DockNode =>
        node.type === 'group'
          ? (node.id === groupId ? { ...node, activeTabId: tabId, hidden: false } : node)
          : { ...node, first: update(node.first), second: update(node.second) }
      write(workspaceId, withActive({ ...cur, dock: { ...dock, root: update(dock.root) } }, paneOf(tab), tabId))
    },

    applyFileMutation(req) {
      if (!['rename', 'move', 'delete'].includes(req.operation)) return
      const cur = get().stateOf(req.workspaceId)
      if (req.operation === 'delete') {
        for (const tab of cur.tabs) {
          if ('path' in tab.ref && isWithinPath(tab.ref.path, req.path)) get().close(req.workspaceId, tab.id)
        }
        return
      }
      if (!req.destination) return
      const destination = req.destination
      const tabs = cur.tabs.map((tab): InnerTab => {
        if (!('path' in tab.ref) || !isWithinPath(tab.ref.path, req.path)) return tab
        const path = destination + tab.ref.path.slice(req.path.length)
        /*
          ★ **`ref` 必须展开,不能整个换成 `{ path }`。** 带 `path` 的 ref 里有两种
          还挂着别的字段:`custom` 的 `{ viewType, pluginId }`、`files` 的
          `selectedPath`。整个替换掉的表现是**改一次名,插件编辑器就变成
          「提供它的插件已被禁用、卸载或装载失败」** —— `pluginId` 没了,
          `CustomEditorView` 拿 `undefined` 去 catalog 里找,当然找不到。
          (那句提示里的 `{plugin}` 原样显示没被替换,就是这个 bug 的现场证据。)

          ★★ 末尾那个 `as InnerTab` 是这一行当初能编译过去的**唯一**原因:
          它把「`custom` 的 ref 缺了两个必填字段」这个类型错误直接压掉了。
          断言留着是因为 TS 不会对映射里的联合成员做分配式收窄,
          但现在它压住的只剩这一条,不再掩盖字段丢失。
        */
        return { ...tab, ref: { ...tab.ref, path }, title: path.split('/').pop() ?? path } as InnerTab
      })
      if (tabs.some((tab, i) => tab !== cur.tabs[i])) write(req.workspaceId, { ...cur, tabs })
    },

    forget(workspaceId) {
      /*
        会话 store 一起放掉。**这里是唯一放得掉的地方** —— 内层 Tab 表是全应用
        唯一知道「这个工作区有哪些 sessionId」的东西,别处想放也没有名单。

        正在跑的那几个放不掉(`releaseSession` 自己会拒绝),这是故意的:
        run 活在主进程,关掉工作区不该把它从渲染层的账上抹掉。
      */
      for (const t of get().stateOf(workspaceId).tabs) {
        if (t.kind === 'chat') releaseSession(chatKey(t))
      }
      const next = { ...get().byWorkspace }
      delete next[workspaceId]
      dockCache.delete(workspaceId)
      set({ byWorkspace: next })
    }
  }
})

/*
  ★ 这一半由本模块自己装到 port 上(见 `shell-port.ts`)。`window` store 那一半
  在它自己的文件里装。两边都**不 import 对方**,`forget` 却仍然能调到。
*/
installShellTabsPort({
  forgetTabsForWorkspace: (workspaceId) => useTabsStore.getState().forget(workspaceId)
})
