/**
 * 文件树的**视图状态**:展开了哪些目录、上次列回来的内容、滚到了哪儿。
 *
 * 需求:在右侧文件树里展开几层、点开一个文件,再切回文件树时,树必须还是刚才的样子。
 * 原先这些都是 `FilesView` 里的 `useState`,而右侧工作台同一格**只挂激活的那个 Tab**
 * (`shell/Dock.tsx` 的 `DockContent`,`key={active.id}`)—— 点开文件 = 切到 doc Tab =
 * 文件树整棵卸载,回来时展开状态、列表、滚动位置全部从零开始,表现是「展开的目录又收起了」。
 *
 * 不变式:
 * - key 是「工作区 + 子树根」(`fileTreeViewKey`),与 `FilesView` 的组件 key 同一粒度 ——
 *   换工作区 / 换子树根仍然是一棵新树,不会把上一个项目的展开状态带过去。
 * - `listings` 只是**先画出来**用的快照:挂载时照样重读每一个展开的目录(静默,不闪转圈),
 *   所以离开期间磁盘上的增删改会在回来后的第一轮读盘里补上。
 *
 * 故意不做:
 * - **不落盘**。重启后回到全部收起 —— 展开状态跟着 Tab 布局一起落盘的话,每点一次目录就是
 *   一次 `persistInnerTabs` 的 IPC 写,而它换来的只是「重启后还展开着」。真要做,
 *   正确的位置是给这里加一层持久化,不是塞进 Tab 的 `ref`。
 * - **不订阅**。只有 `FilesView` 在挂载时读一次、变化时写回,没有任何组件需要跟着它重渲染。
 */
import { create } from 'zustand'
import type { DirListing } from '../../../shared/domain/file-tree'

export interface FileTreeView {
  /** 展开着的目录(工作区相对路径) */
  expanded: readonly string[]
  /** 根 + 展开目录的最近一次列表;收起的目录不留,下次展开时本来就要重读 */
  listings: Readonly<Record<string, DirListing>>
  scrollTop: number
}

interface FileTreeViewState {
  views: Readonly<Record<string, FileTreeView>>
  save: (key: string, patch: Partial<FileTreeView>) => void
}

/** ★ 分隔符用 NUL:路径里不会有它,`a:b` + `c` 与 `a` + `b:c` 不会撞成同一个 key。 */
export function fileTreeViewKey(workspaceId: string, rootPath: string): string {
  return `${workspaceId}\u0000${rootPath}`
}

const EMPTY: FileTreeView = { expanded: [], listings: {}, scrollTop: 0 }

export const useFileTreeStore = create<FileTreeViewState>((set) => ({
  views: {},
  save(key, patch) {
    set((state) => ({ views: { ...state.views, [key]: { ...(state.views[key] ?? EMPTY), ...patch } } }))
  }
}))

/**
 * 只留根与展开目录的列表。
 *
 * 需求:收起的目录在 `FilesView` 的 state 里仍然留着上次的列表(方便马上再展开),
 * 但带进快照没有意义 —— `refresh` 对收起的目录本来就是「下次展开时重读」,
 * 留着只会让快照随着用户点过的目录越长越大。
 */
export function pruneListings(
  listings: Readonly<Record<string, DirListing>>,
  rootPath: string,
  expanded: ReadonlySet<string>
): Record<string, DirListing> {
  return Object.fromEntries(Object.entries(listings).filter(([path]) => path === rootPath || expanded.has(path)))
}
