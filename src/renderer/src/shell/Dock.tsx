import { useEffect, useLayoutEffect, useRef, useState, type DragEvent, type KeyboardEvent, type PointerEvent, type ReactNode } from 'react'
import { findGroup, type DockNode } from '../../../shared/domain/dock'
import type { InnerTab, InnerTabKind } from '../../../shared/domain/tab'
import type { TabMenuItem } from '../../../shared/plugin/contribution'
import type { Workspace } from '../../../shared/domain/workspace'
import { EmptyState } from '../components/arc/empty-state/empty-state'
import type { FallbackModel } from '../views/chat/Composer'
import { cn } from '../lib/cn'
import { useI18n } from '../i18n'
import type { Presence } from '../lib/usePresence'
import { InnerView } from '../views/registry'
import { AllTabsMenu, InnerTabBar } from './InnerTabBar'
import { useTabsStore } from '../stores/tabs'
import { confirmDocumentChanges, useDocumentsStore } from '../stores/documents'
import { DOCK_TAB_MIME, dockDropZone, edgeHidden, groupPane, groupTabs, visibleDockNode, type DockDropZone } from './dock-layout'
import { useTabMenu } from './tab-menu'
import { submitTabRename } from './tab-rename-actions'
import { usePluginsStore } from '../stores/plugins'

export function DockRoot({ workspace, fallbackModel, maxOutputTokens, runningSessionIds, right, bottom }: { workspace: Workspace; fallbackModel: FallbackModel; maxOutputTokens: number; runningSessionIds: ReadonlySet<string>; right: Presence; bottom: Presence }): ReactNode {
  const dock = useTabsStore((state) => state.dockOf(workspace.id))
  /*
    ★ 结构树一律按「两面全开」投影,开关**不参与结构**。
    原先开关一变就把树摊平,key 跟着换,整块主区跟着重挂载 —— 表现为关一次
    右栏,聊天滚动位置就丢了,且全程零报错。空组 / hidden 组照旧滤掉(那是
    结构性的、和开关无关);开关只在下面的 split 里决定某侧是 ghost 还是
    absent,key 因此在开合之间纹丝不动,开合动画才有稳定的挂载点。
  */
  const structural = visibleDockNode(dock.root, dock.tabs, true, true)
  /*
    根整棵被开关藏起来(主区一个 Tab 都不剩、只剩右栏这类结构)才退回空态。
    ★ 判据用 shown 而不是 mounted:根没有「父级 split」能替它包 ghost 壳,
    留到动画结束才消失反而像坏了。这种结构本来就没有可播动画的邻居,瞬时是对的。
  */
  const root = structural !== null && edgeHidden(structural, dock.tabs, right.shown, bottom.shown) ? null : structural
  const { t } = useI18n()
  return <div data-dock-root className="flex min-h-0 min-w-0 flex-1 overflow-hidden">{root ? <DockNodeView key={root.id} node={root} tabs={dock.tabs} right={right} bottom={bottom} workspace={workspace} fallbackModel={fallbackModel} maxOutputTokens={maxOutputTokens} runningSessionIds={runningSessionIds} /> : <EmptyState title={t('common.empty')} description="" />}</div>
}

function DockNodeView({ node, tabs, right, bottom, workspace, fallbackModel, maxOutputTokens, runningSessionIds }: { node: DockNode; tabs: readonly InnerTab[]; right: Presence; bottom: Presence; workspace: Workspace; fallbackModel: FallbackModel; maxOutputTokens: number; runningSessionIds: ReadonlySet<string> }): ReactNode {
  if (node.type === 'split') {
    return <DockSplitView node={node} tabs={tabs} right={right} bottom={bottom} workspace={workspace} fallbackModel={fallbackModel} maxOutputTokens={maxOutputTokens} runningSessionIds={runningSessionIds} />
  }
  return <DockGroup key={node.id} node={node} workspace={workspace} fallbackModel={fallbackModel} maxOutputTokens={maxOutputTokens} runningSessionIds={runningSessionIds} />
}

/**
 * 右/底部面板开合动画的落点(设计见 AppShell 的 PANEL_MS 与 usePresence)。
 *
 * 需求:开合是一次 280ms 的过渡,而不是开关一翻轨道就瞬时重排、主区瞬间变宽。
 *
 * ★ 三轨结构恒定(两头 minmax + 中间 4px),开关只改轨里的数 ——
 * `grid-template-columns/rows` 只在**结构相同**的两串之间插值,少一段就动画不出来,
 * 所以收尾帧的几何必须和静止态逐帧对上,交接时才看不出跳变。
 *
 * ★ ghost 侧的内层钉死为目标像素宽、由轨道格裁 —— 和侧边栏「裁切不挤扁」
 * 同一条理由:否则文件面板收的过程里工具条可用宽跌破 400px,会在动画中途
 * 自己折成 `…`,UI 换了一套(那套溢出逻辑在 FilesView 的 TOOLBAR_FULL_WIDTH)。
 */
function DockSplitView({ node, tabs, right, bottom, workspace, fallbackModel, maxOutputTokens, runningSessionIds }: { node: Extract<DockNode, { type: 'split' }>; tabs: readonly InnerTab[]; right: Presence; bottom: Presence; workspace: Workspace; fallbackModel: FallbackModel; maxOutputTokens: number; runningSessionIds: ReadonlySet<string> }): ReactNode {
  const horizontal = node.direction === 'horizontal'
  const stateOf = (child: DockNode): 'live' | 'ghost' | 'absent' => {
    if (edgeHidden(child, tabs, right.mounted, bottom.mounted)) return 'absent'
    if (edgeHidden(child, tabs, right.shown, bottom.shown)) return 'ghost'
    return 'live'
  }
  const first = stateOf(node.first)
  const second = stateOf(node.second)
  const ghostSlot = first === 'ghost' ? 1 : second === 'ghost' ? 3 : null

  const gridRef = useRef<HTMLDivElement>(null)
  const [frozen, setFrozen] = useState<number | null>(null)
  /*
    ghost 侧的目标宽 = 本侧占比 × (整格 − 分隔条),整格在开合期间不变
    (主区总宽不因 dock 内部的此消彼长而变),所以开播当帧量一次就够。
    不挂依赖:ghost 期间每次 commit 都重量,顺带跟上罕见的中途变化;
    收尾 setFrozen(null) 只是把状态归位,多余的一次渲染没有可见后果。
  */
  useLayoutEffect(() => {
    if (ghostSlot === null) {
      if (frozen !== null) setFrozen(null)
      return
    }
    const rect = gridRef.current?.getBoundingClientRect()
    if (rect === undefined) return
    const fraction = ghostSlot === 1 ? node.ratio : 1 - node.ratio
    const size = (horizontal ? rect.width : rect.height) - 4
    const target = size * fraction
    setFrozen((prev) => (prev === target ? prev : target))
  })

  /*
    ★ 只在 usePresence 的 animating 窗口里挂 transition:拖分隔条时 ratio 每帧
    都在写,常开过渡等于每拖一帧排一次插值,手感像拉皮筋(usePresence 文件头
    记的正是这条)。280 必须和 AppShell 的 PANEL_MS 同一个数 —— 四条曲线只要
    有一条不同步,看着就是「分好几批到位」。
  */
  const animating = right.animating || bottom.animating
  const filling = second === 'live' ? 'minmax(0, 0fr) 0px minmax(0, 1fr)' : 'minmax(0, 1fr) 0px minmax(0, 0fr)'
  const tracks = first !== 'live' || second !== 'live'
    ? (horizontal ? { gridTemplateColumns: filling } : { gridTemplateRows: filling })
    : horizontal
      ? { gridTemplateColumns: `minmax(0, ${node.ratio}fr) 4px minmax(0, ${1 - node.ratio}fr)` }
      : { gridTemplateRows: `minmax(0, ${node.ratio}fr) 4px minmax(0, ${1 - node.ratio}fr)` }

  const pane = (child: DockNode, slot: 1 | 3, state: 'live' | 'ghost', childNode: ReactNode): ReactNode => (
    <div
      key={child.id}
      className={cn('flex min-h-0 min-w-0', !horizontal && 'flex-col', state === 'ghost' && 'overflow-hidden')}
      style={horizontal ? { gridColumn: slot } : { gridRow: slot }}
    >
      <div
        className={cn('flex min-h-0 min-w-0 flex-col', state === 'live' ? 'flex-1' : 'shrink-0')}
        style={state === 'ghost' ? (horizontal ? { width: frozen ?? 0 } : { height: frozen ?? 0 }) : undefined}
      >
        {childNode}
      </div>
    </div>
  )
  const pass = (child: DockNode): ReactNode => <DockNodeView node={child} tabs={tabs} right={right} bottom={bottom} workspace={workspace} fallbackModel={fallbackModel} maxOutputTokens={maxOutputTokens} runningSessionIds={runningSessionIds} />
  return (
    <div
      ref={gridRef}
      data-dock-split-id={node.id}
      className={cn(
        'grid min-h-0 min-w-0 flex-1 overflow-hidden',
        animating && (horizontal ? 'transition-[grid-template-columns] duration-280 ease-panel' : 'transition-[grid-template-rows] duration-280 ease-panel')
      )}
      style={tracks}
    >
      {first !== 'absent' && pane(node.first, 1, first, pass(node.first))}
      {/*
        分隔条只在两侧都还在时画。★ 宽高不写死 w-1/h-1,交给网格拉伸填轨道 ——
        轨道在开合动画里要收到 0px,写死 4px 的话它会孤零零地多留一截。
        ghost 期间置 inert:轨道收成 0 后它没有可点面积,留着 tabIndex 只会
        让 Tab 键跳进一个看不见的分隔条。
      */}
      {first !== 'absent' && second !== 'absent' && (
        <DockSplitter
          key="splitter"
          workspaceId={workspace.id}
          splitId={node.id}
          direction={node.direction}
          ratio={node.ratio}
          style={horizontal ? { gridColumn: 2 } : { gridRow: 2 }}
          inert={ghostSlot !== null}
        />
      )}
      {second !== 'absent' && pane(node.second, 3, second, pass(node.second))}
    </div>
  )
}

function DockGroup({ node, workspace, fallbackModel, maxOutputTokens, runningSessionIds }: { node: Extract<DockNode, { type: 'group' }>; workspace: Workspace; fallbackModel: FallbackModel; maxOutputTokens: number; runningSessionIds: ReadonlySet<string> }): ReactNode {
  const { t } = useI18n()
  const dock = useTabsStore((state) => state.dockOf(workspace.id))
  const tabs = groupTabs(node, dock.tabs)
  const edgePane = node.pinned === 'right' ? 'right' : groupPane(tabs)
  const activateTab = useTabsStore((state) => state.activateDockTab)
  const closeTab = useTabsStore((state) => state.closeDockTab)
  const openDock = useTabsStore((state) => state.openDock)
  const splitAndMoveDockTab = useTabsStore((state) => state.splitAndMoveDockTab)
  const moveDock = useTabsStore((state) => state.moveDockTab)
  const reorderDock = useTabsStore((state) => state.reorderDockTab)
  const [dragZone, setDragZone] = useState<DockDropZone | null>(null)
  useEffect(() => {
    if (dragZone === null) return
    const clear = (): void => setDragZone(null)
    document.addEventListener('dragend', clear)
    document.addEventListener('drop', clear)
    return () => {
      document.removeEventListener('dragend', clear)
      document.removeEventListener('drop', clear)
    }
  }, [dragZone])
  const active = tabs.find((tab) => tab.id === node.activeTabId)
  /*
    ★ 三条常量的三元选择换成一个 hook。内置项与插件贡献项在 `useTabMenu` 里
    合并,插件项被 clamp 到内置项之后(见 `shared/plugin/contribution.ts`)。
  */
  const menu = useTabMenu(edgePane)
  /*
    ★ 按 `item.action` 分发,不再按 `kind`。内置项开一个 Tab;插件项执行
    它自己的命令 —— 能力门在命令实现里,所以贡献菜单本身不需要新权限。
  */
  const open = (item: TabMenuItem): void => {
    if (item.action.kind === 'openTab') {
      openDock(workspace.id, node.id, item.action.tabKind as InnerTabKind, undefined, edgePane)
      return
    }
    if (item.pluginId === undefined) return
    // args 带上菜单所在的工作区:插件终端这类能力要知道「在哪个工作区里起」。
    void usePluginsStore.getState().runCommand(item.pluginId, item.action.commandId, { workspaceId: workspace.id })
  }
  const onClose = async (id: string): Promise<void> => {
    const target = tabs.find((tab) => tab.id === id)
    if (target?.kind === 'doc' || target?.kind === 'preview') {
      if (!(await confirmDocumentChanges(workspace.id, target.ref.path))) return
      closeTab(workspace.id, node.id, id)
      const stillOpen = useTabsStore.getState().dockOf(workspace.id).tabs.some((tab) => (tab.kind === 'doc' || tab.kind === 'preview') && tab.ref.path === target.ref.path)
      if (!stillOpen) useDocumentsStore.getState().release(workspace.id, target.ref.path)
      return
    }
    closeTab(workspace.id, node.id, id)
  }
  const zoneAt = (event: DragEvent<HTMLElement>): DockDropZone => {
    const rect = event.currentTarget.getBoundingClientRect()
    const barHeight = event.currentTarget.querySelector('[data-dock-tabbar]')?.getBoundingClientRect().height ?? 40
    return dockDropZone(event.clientX - rect.left, event.clientY - rect.top, rect.width, rect.height, barHeight)
  }
  const onDrop = (event: DragEvent<HTMLElement>): void => {
    if (!event.dataTransfer.types.includes(DOCK_TAB_MIME)) return
    event.preventDefault()
    event.stopPropagation()
    setDragZone(null)
    const raw = event.dataTransfer.getData(DOCK_TAB_MIME)
    if (!raw) return
    try {
      const payload = JSON.parse(raw) as { tabId?: string; groupId?: string; workspaceId?: string }
      if (!payload.tabId || !payload.groupId || payload.workspaceId !== workspace.id) return
      const sourceGroup = findGroup(dock.root, payload.groupId)
      if (!sourceGroup?.tabIds.includes(payload.tabId)) return
      // The workspace-files tab is the only immovable tab. The rightmost
      // group itself can still receive previews, documents, terminals, and
      // browser tabs; keeping the group pinned only controls its position.
      if (dock.tabs.find((tab) => tab.id === payload.tabId)?.kind === 'files') return
      const zone = zoneAt(event)
      if (zone === 'tabs') {
        const elements = [...event.currentTarget.querySelectorAll<HTMLElement>('[data-dock-tab-id]')].filter((tab) => tab.dataset.dockTabId !== payload.tabId)
        const before = elements.findIndex((tab) => { const rect = tab.getBoundingClientRect(); return event.clientX < rect.left + rect.width / 2 })
        const index = before < 0 ? elements.length : before
        if (payload.groupId === node.id) reorderDock(workspace.id, node.id, node.tabIds.indexOf(payload.tabId), index)
        else moveDock(workspace.id, payload.tabId, payload.groupId, node.id, index)
      } else if (zone === 'center') {
        moveDock(workspace.id, payload.tabId, payload.groupId, node.id)
      } else {
        splitAndMoveDockTab(workspace.id, payload.tabId, payload.groupId, node.id, zone)
      }
    } catch { /* ignore malformed drag payloads */ }
  }
  /*
    ★ 这一格**不能**挂 `app-no-drag`。`-webkit-app-region` 是**继承**属性:挂上以后
    聊天列表里每个元素都带上 no-drag,而 Chromium 收集拖动区用的是元素的绝对包围盒
    ——**滚上去的长内容包围盒是负 y、高好几千**,横跨顶部那条 34px 的 Tab 条,
    在 Electron 那边把它的 drag 区整块减掉,表现是「顶栏空白处拖不动窗口」。
    这一格本来就不在拖动区里,默认的 `none` 才是对的。
  */
  return (
    <section data-dock-group-id={node.id} className={cn('relative flex min-h-0 min-w-0 flex-1 flex-col overflow-hidden border border-hairline', node.id === dock.activeGroupId && 'outline outline-1 outline-accent/30')} onMouseDown={() => useTabsStore.getState().activateDockGroup(workspace.id, node.id)} onDragOver={(event) => { if (!event.dataTransfer.types.includes(DOCK_TAB_MIME)) return; event.preventDefault(); event.dataTransfer.dropEffect = 'move'; setDragZone(zoneAt(event)) }} onDragLeave={(event) => { if (!(event.relatedTarget instanceof Node) || !event.currentTarget.contains(event.relatedTarget)) setDragZone(null) }} onDrop={onDrop}>
      <InnerTabBar
        // ★ 实色,不能让图片主题的底图透上来 —— 这条 Tab 条和内容区是两层
        //   (参考实现里图是从这条下面那道 hairline 开始铺的)。写在这里而不是
        //   InnerTabBar 内部:左右/底部面板里的同一条坐在 `bg-surface` 上,
        //   钉死 canvas 会让那三格的 Tab 条比面板亮一格。
        className="bg-canvas"
        tabs={tabs}
        groupId={node.id}
        workspaceId={workspace.id}
        activeId={node.activeTabId}
        runningSessionIds={runningSessionIds}
        menu={menu}
        trailing={
          <AllTabsMenu
            tabs={tabs}
            activeId={node.activeTabId}
            onActivate={(id) => activateTab(workspace.id, node.id, id)}
          />
        }
        canDragTab={(tab) => tab.kind !== 'files'}
        onActivate={(id) => activateTab(workspace.id, node.id, id)}
        onClose={(id) => { void onClose(id) }}
        onMove={(from, to) => useTabsStore.getState().reorderDockTab(workspace.id, node.id, from, to)}
        onOpen={open}
        onRename={(tab, value) => { void submitTabRename(workspace.id, tab, value) }}
      />
      {dragZone !== null && <DropOverlay zone={dragZone} />}
      <DockContent tabs={tabs} active={active} workspace={workspace} fallbackModel={fallbackModel} maxOutputTokens={maxOutputTokens} emptyTitle={t('common.empty')} />
    </section>
  )
}

function DockContent({ tabs, active, workspace, fallbackModel, maxOutputTokens, emptyTitle }: {
  tabs: ReturnType<typeof groupTabs>
  active: ReturnType<typeof groupTabs>[number] | undefined
  workspace: Workspace
  fallbackModel: FallbackModel
  /** 设置 › 通用 › Agent 的输出额度,聊天视图用它算压力条分母,见 InnerViewProps */
  maxOutputTokens: number
  emptyTitle: string
}): ReactNode {
  const browsers = tabs.filter((tab) => tab.kind === 'browser')
  if (active === undefined) return <EmptyState title={emptyTitle} description="" className="py-6" />
  return (
    <div className="relative flex min-h-0 min-w-0 flex-1">
      {active.kind !== 'browser' && <InnerView key={active.id} tab={active} workspace={workspace} fallbackModel={fallbackModel} maxOutputTokens={maxOutputTokens} />}
      {browsers.map((tab) => {
        const visible = tab.id === active.id
        return (
          <div
            key={tab.id}
            aria-hidden={visible ? undefined : true}
            className={cn(
              'absolute inset-0 flex min-h-0 min-w-0',
              visible ? 'z-10 opacity-100' : 'pointer-events-none z-0 opacity-0'
            )}
          >
            {/*
              ★ Browser webviews stay mounted while their tab is inactive. Destroying the guest
              here detaches its CDP target, so an Agent switching between two browser tabs would
              make the first tab impossible to inspect or click until the user selected it again.
            */}
            <InnerView tab={tab} workspace={workspace} fallbackModel={fallbackModel} maxOutputTokens={maxOutputTokens} />
          </div>
        )
      })}
    </div>
  )
}

function DropOverlay({ zone }: { zone: DockDropZone }): ReactNode {
  const cls = 'pointer-events-none absolute z-20 rounded-lg border border-accent/70 bg-accent/20 shadow-lg shadow-black/10'
  if (zone === 'tabs') return <div className={cn(cls, 'inset-x-2 top-1 h-8')} />
  if (zone === 'left') return <div className={cn(cls, 'inset-y-2 left-2 w-1/2')} />
  if (zone === 'right') return <div className={cn(cls, 'inset-y-2 right-2 w-1/2')} />
  if (zone === 'up') return <div className={cn(cls, 'inset-x-2 top-2 h-1/2')} />
  if (zone === 'down') return <div className={cn(cls, 'inset-x-2 bottom-2 h-1/2')} />
  return <div className={cn(cls, 'inset-2')} />
}

function DockSplitter({ workspaceId, splitId, direction, ratio, style, inert = false }: { workspaceId: string; splitId: string; direction: 'horizontal' | 'vertical'; ratio: number; /** 钉在网格第 2 轨 —— 两侧 children 用了显式 slot,它不靠自动摆放。 */ style?: React.CSSProperties; /** 开合动画期间轨道收到 0px:没有可点面积,别留一个可 Tab 的隐形分隔条。 */ inert?: boolean }): ReactNode {
  const [dragging, setDragging] = useState(false)
  const ref = useRef<HTMLDivElement>(null)
  const resize = useTabsStore((state) => state.resizeDock)
  const onPointerDown = (event: PointerEvent<HTMLDivElement>): void => {
    event.preventDefault()
    ref.current?.setPointerCapture(event.pointerId)
    setDragging(true)
  }
  const onPointerMove = (event: PointerEvent<HTMLDivElement>): void => {
    if (!dragging || ref.current?.parentElement === null || ref.current?.parentElement === undefined) return
    const rect = ref.current.parentElement.getBoundingClientRect()
    const ratio = direction === 'horizontal' ? (event.clientX - rect.left) / rect.width : (event.clientY - rect.top) / rect.height
    resize(workspaceId, splitId, ratio)
  }
  const onPointerUp = (event: PointerEvent<HTMLDivElement>): void => {
    if (ref.current?.hasPointerCapture(event.pointerId)) ref.current.releasePointerCapture(event.pointerId)
    setDragging(false)
  }
  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>): void => {
    const delta = direction === 'horizontal' ? (event.key === 'ArrowLeft' ? -0.03 : event.key === 'ArrowRight' ? 0.03 : 0) : (event.key === 'ArrowUp' ? -0.03 : event.key === 'ArrowDown' ? 0.03 : 0)
    if (event.key === 'Home') { resize(workspaceId, splitId, 0.5); event.preventDefault(); return }
    if (delta !== 0) { resize(workspaceId, splitId, ratio + delta); event.preventDefault() }
  }
  // 宽高不写死 w-1/h-1:由网格拉伸填满 4px 的轨道。轨道在开合动画里要收到
  // 0px,写死的话它会比轨道宽、在收尾帧溢到邻居格上。
  return <div ref={ref} role="separator" tabIndex={inert ? -1 : 0} aria-orientation={direction === 'horizontal' ? 'vertical' : 'horizontal'} inert={inert} style={style} className={cn('relative z-10 shrink-0 bg-border/60', direction === 'horizontal' ? 'cursor-col-resize' : 'cursor-row-resize', dragging && 'bg-accent')} onPointerDown={onPointerDown} onPointerMove={onPointerMove} onPointerUp={onPointerUp} onKeyDown={onKeyDown} onDoubleClick={() => resize(workspaceId, splitId, 0.5)} />
}
