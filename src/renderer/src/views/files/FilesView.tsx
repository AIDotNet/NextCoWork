/**
 * 工作区文件树 —— 右侧「工作台标签」里默认的那一个。
 *
 * 对着参考截图逐条落下来的行为(顺序就是它们在界面上从上到下的顺序):
 *
 * 1. **`对话文件` / `所有文件` 两档 scope**,左上角,`所有文件` 是加粗那个(选中态)。
 * 2. **工具条会随面板宽度收起**。同一套按钮在宽面板(≈608px)里是七颗全展开
 *    (新建 / 搜索 / 排序 / 隐藏文件 / 全部折叠 / 访达 / 刷新),
 *    在窄面板(≈297px)里只剩「搜索 + `…`」。这不是两套 UI,是**一套按溢出**。
 * 3. **懒加载**。目录默认收起(`>`),展开一层拉一层 —— `node_modules` 不会被整棵拉下来。
 * 4. **目录在前、文件在后**(见 shared 的 `sortEntries`),文件图标按类型上色。
 * 5. **行有三态**:普通 / 悬停(浅底 + 右端冒出 `…`)/ 选中(挖亮底,即当前打开的那个文件)。
 * 6. **离开再回来,树还是原样**:展开的目录、列表、滚动位置存在 `stores/file-tree.ts`
 *    (右侧工作台只挂激活的 Tab,点开文件时这棵树会整棵卸载 —— 理由全文在那个文件头)。
 * 7. **动效与键盘照 beUI File Tree**:缩进参考线、悬停底色在行间滑动(`TreeHoverGlide`)、
 *    展开时子项依次落下、↑↓←→ / Home End 导航(纯逻辑在 `tree-rows.ts`)。
 * 8. **右键 = 行尾 `…`**:两者打开同一份 `FileRowMenu`(在 X 中打开 / 打开方式 › / 另存为 /
 *    复制路径 / 添加到聊天 / 文件管理动作),菜单开着的那一行描一圈强调色边。
 *
 * 点一个文件 → 在**右侧工作台**新增一个 doc Tab,所以这个组件不自己渲染文件内容,
 * 它只发 `onOpenFile`。
 */
import {
  ArrowUpDown,
  ChevronRight,
  Eye,
  EyeOff,
  FilePlus2,
  FolderOpen,
  FolderPlus,
  ListCollapse,
  MoreHorizontal,
  Plus,
  RefreshCw,
  Search,
  Undo2,
  X
} from 'lucide-react'
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent as ReactKeyboardEvent, type ReactNode } from 'react'
import type { DirListing, FileEntry, SortBy } from '../../../../shared/domain/file-tree'
import { isLocalEnvironment } from '../../../../shared/domain/environment'
import { paneOf, type InnerTab } from '../../../../shared/domain/tab'
import type { Workspace } from '../../../../shared/domain/workspace'
import type { WorkspaceFileMutationRequest, WorkspaceRecoveryEntry } from '../../../../shared/domain/workspace-file'
import type { DockNode } from '../../../../shared/domain/dock'
import { Button } from '../../components/arc/button/button'
import type { ContextMenuPosition } from '../../components/ui/ContextMenu'
import { EmptyState } from '../../components/arc/empty-state/empty-state'
import { IconButton } from '../../components/ui/IconButton'
import { Menu, MenuItem, MenuSeparator } from '../../components/ui/Menu'
import { cn } from '../../lib/cn'
import { iconFor } from '../../lib/file-icon'
import { listDir } from '../../services/app'
import { listWorkspaceRecovery, isResultUnknown, workspaceFileErrorKey, type WorkspaceFilesChanged } from '../../services/workspace-files'
import { awaitDocumentSaves, mutateWorkspaceFile, revealWorkspaceFile } from '../../actions/workspace-files'
import { confirmDocumentChanges } from '../../stores/documents'
import { fileTreeViewKey, pruneListings, useFileTreeStore } from '../../stores/file-tree'
import { useTabsStore } from '../../stores/tabs'
import { flatten, type Row } from './flatten'
import { enterDelays, treeKeyAction } from './tree-rows'
import { TreeHoverGlide } from './TreeHoverGlide'
import { useI18n, type TranslationKey } from '../../i18n'
import { FileOperationDialog } from './FileOperationDialog'
import { FILE_ROW_MENU_WIDTH, FileRowMenu } from './FileRowMenu'
import { type FileOperationTarget } from './file-operations'
import { Spinner } from '../../components/ui/Spinner'

type Scope = 'conversation' | 'all'

/**
 * 工具条全展开需要的宽度。低于它就折进 `…`。
 *
 * 量参考截图定的:窄面板(面板宽 ≈297,工具条可用宽 ≈270)只放得下「搜索 + `…`」,
 * 宽面板(≈608)七颗全在。400 落在两者中间,且留出了 scope 那两个 chip 的位置。
 */
const TOOLBAR_FULL_WIDTH = 400

/** 一层缩进。12 是让第 3 层还看得出层级、又不至于把长文件名挤没的那个值。 */
const INDENT = 12
/** 参考线相对那一层起点的偏移:箭头槽 `size-3.5`(14px)的正中,于是线从箭头正下方垂下来 */
const GUIDE_OFFSET = 7

interface FilesViewProps {
  workspace: Workspace
  /** Workspace-relative subtree root; empty for the workspace root. */
  rootPath: string
  selectedPath: string | null
  onOpenFile: (path: string, name: string) => void
}

export function FilesView(props: FilesViewProps): ReactNode {
  // Scope the full state to the workspace and subtree, including pending dialogs.
  return <WorkspaceFilesView key={`${props.workspace.id}:${props.rootPath}`} {...props} />
}

function WorkspaceFilesView({
  workspace,
  rootPath,
  selectedPath,
  onOpenFile
}: FilesViewProps): ReactNode {
  const { t } = useI18n()
  const scopes: readonly { id: Scope; label: string }[] = [
    { id: 'conversation', label: t('files.conversation') },
    { id: 'all', label: t('files.all') }
  ]
  const [scope, setScope] = useState<Scope>('all')
  const [sortBy, setSortBy] = useState<SortBy>('name')
  const [showHidden, setShowHidden] = useState(false)
  const [query, setQuery] = useState<string | null>(null)
  const viewKey = fileTreeViewKey(workspace.id, rootPath)
  /*
    需求:切走再切回来时展开状态还在(见 `stores/file-tree.ts` 文件头)。
    ★ 只在**挂载那一刻**读一次(lazy initial state),不订阅:之后这棵树自己是权威,
      store 只是它卸载时留下的快照。订阅的话,自己写回去的那一下又会触发自己重渲。
  */
  const [restored] = useState(() => useFileTreeStore.getState().views[viewKey])
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(() => new Set(restored?.expanded ?? []))
  const [listings, setListings] = useState<Readonly<Record<string, DirListing>>>(() => restored?.listings ?? {})
  const [failed, setFailed] = useState<ReadonlySet<string>>(new Set())
  const [loading, setLoading] = useState<ReadonlySet<string>>(new Set())
  const [operation, setOperation] = useState<FileOperationTarget | null>(null)
  const [busy, setBusy] = useState(false)
  const [confirmingChanges, setConfirmingChanges] = useState(false)
  const [operationError, setOperationError] = useState<TranslationKey | null>(null)
  const [notice, setNotice] = useState<{ key: TranslationKey; path?: string; error?: boolean } | null>(null)
  /**
   * ★ 可恢复项从**服务器索引**派生,不再是组件 state。
   *
   * 原先是单槽 `useState`:删第二个就把第一个覆盖掉(而它在服务器回收站里还在,UI 再也
   * 找不到);组件 key 是 `${workspace.id}:${rootPath}`,切子树根就重挂载、恢复项蒸发;
   * 刷新、重连、重启更不用说。现在列表来自 `listRecovery()`,上述四种情况天然都还在。
   */
  const [recoveryEntries, setRecoveryEntries] = useState<WorkspaceRecoveryEntry[]>([])
  const [recoveryKey, setRecoveryKey] = useState('')
  /** 正开着菜单的那一行。整棵树只有一份菜单(见 `FileRowMenu` 文件头 ★) */
  const [rowMenu, setRowMenu] = useState<{ entry: FileEntry; position: ContextMenuPosition } | null>(null)
  const generation = useRef(0)
  const requests = useRef(new Map<string, number>())
  const sequence = useRef(0)
  const alive = useRef(true)
  const operationRunning = useRef(false)

  const toolbar = useRef<HTMLDivElement>(null)
  const compact = useToolbarCompact(toolbar)
  /*
    本机工作区才出「打开方式」—— SSH 工作区里那些文件不在本机磁盘上,本机的
    VS Code / 访达打开它们只会打开一个不存在的路径(与浏览器视图同一条判据)。
  */
  const local = isLocalEnvironment(workspace.environment)

  const refreshRecovery = useCallback(async (): Promise<void> => {
    try {
      const listing = await listWorkspaceRecovery(workspace.id)
      if (!alive.current) return
      setRecoveryEntries(listing.entries)
      setRecoveryKey(listing.environmentKey)
    } catch {
      // 列不出来不该打断文件操作本身,静默退成空列表
      if (alive.current) setRecoveryEntries([])
    }
  }, [workspace.id])

  useEffect(() => { void refreshRecovery() }, [refreshRecovery])

  const load = useCallback(
    /**
     * @param quiet 不进 `loading`(不转圈、不出「正在刷新」那一行)。只给「从快照恢复后的
     *   后台重读」用:那时画面上已经是上次的内容,每切回来一次就让每个展开的目录闪一下转圈、
     *   顶部再冒出一行提示把整棵树往下推 20px,恰恰是在提醒用户「树被重置过」。
     */
    (path: string, quiet = false): void => {
      const epoch = generation.current
      const request = ++sequence.current
      requests.current.set(path, request)
      const isCurrent = (): boolean =>
        alive.current && generation.current === epoch && requests.current.get(path) === request
      if (!quiet) setLoading((prev) => new Set(prev).add(path))
      void listDir(workspace.id, path)
        .then((l) => {
          if (!isCurrent()) return
          setListings((prev) => ({ ...prev, [path]: l }))
          setFailed((prev) => {
            if (!prev.has(path)) return prev
            const next = new Set(prev)
            next.delete(path)
            return next
          })
        })
        .catch((err: unknown) => {
          if (!isCurrent()) return
          // 目录读不到(权限 / 刚被删)只该让**那一行**显示读取失败,
          // 不该把整棵树打空 —— 树是一层一层来的,一层坏不等于整棵坏。
          console.error('[files] 列目录失败:', path, err)
          setFailed((prev) => new Set(prev).add(path))
        })
        .finally(() => {
          if (!isCurrent() || quiet) return
          setLoading((prev) => {
            const next = new Set(prev)
            next.delete(path)
            return next
          })
        })
    },
    [workspace.id]
  )

  /*
    挂载 = 读盘。换工作区 / 换子树根仍然是整棵重来:旧工作区的路径在新根下毫无意义,
    留着会让第一帧画出上一个项目的文件名。
    原先这里显式把 listings / expanded 清空;现在这件事由组件 key(`FilesView` 里的
    `${workspace.id}:${rootPath}`)与快照 key(`fileTreeViewKey`)同粒度来保证 ——
    换根就是一个新实例、读的是另一份快照(或没有快照),所以不再需要手动清。
    有快照时:展开过的每个目录都静默重读一遍,离开期间磁盘上的变化在这一轮补上。
  */
  useEffect(() => {
    alive.current = true
    generation.current += 1
    // 快照里真有根列表才静默:否则画面上什么都没有,「加载中」本来就该显示
    const quiet = restored?.listings[rootPath] !== undefined
    load(rootPath, quiet)
    for (const path of restored?.expanded ?? []) if (path !== rootPath) load(path, quiet)
    return () => {
      alive.current = false
      generation.current += 1
    }
  }, [load, rootPath, restored])

  /*
    写回快照。展开状态与列表随变化写(一次 set,没有订阅者,很便宜);
    滚动位置只在卸载时写 —— 每个 scroll 事件都 set 一次没有必要。
    ★ 滚动位置从 onScroll 记进 ref,而不是卸载时去读 DOM:被动 effect 的清理函数跑的时候
      节点已经摘下来了,那时 `scrollRef.current` 是 null,读到的永远是 0。
  */
  const scrollTop = useRef(restored?.scrollTop ?? 0)
  const scroller = useRef<HTMLDivElement>(null)
  useEffect(() => {
    useFileTreeStore.getState().save(viewKey, {
      expanded: [...expanded],
      listings: pruneListings(listings, rootPath, expanded)
    })
  }, [expanded, listings, rootPath, viewKey])
  useEffect(() => () => {
    useFileTreeStore.getState().save(viewKey, { scrollTop: scrollTop.current })
  }, [viewKey])
  // 快照里有列表 → 第一帧就画得出行 → 在绘制前把滚动条放回原处,否则先闪一帧顶部
  useLayoutEffect(() => {
    if (restored !== undefined && restored.scrollTop > 0 && scroller.current !== null) {
      scroller.current.scrollTop = restored.scrollTop
    }
  }, [restored])

  const toggleDir = (path: string): void => {
    const wasExpanded = expanded.has(path)
    setExpanded((prev) => {
      const next = new Set(prev)
      if (wasExpanded && !failed.has(path)) next.delete(path)
      else next.add(path)
      return next
    })
    if (failed.has(path) || (!wasExpanded && listings[path] === undefined)) load(path)
  }

  const refresh = useCallback((paths: ReadonlySet<string> = expanded): void => {
    generation.current += 1
    // Only preserve visible listings while they reload; collapsed folders must
    // read fresh data the next time they are expanded.
    setListings((prev) => Object.fromEntries(
      Object.entries(prev).filter(([path]) => path === rootPath || paths.has(path)),
    ))
    setLoading(new Set())
    setFailed(new Set())
    load(rootPath)
    // 展开状态**留着**:刷新是"重新读盘",不是"把我打开的目录都收起来"
    for (const p of paths) if (p !== rootPath) load(p)
  }, [expanded, load, rootPath])

  useEffect(() => {
    const onFilesChanged = (event: Event): void => {
      const change = (event as CustomEvent<WorkspaceFilesChanged>).detail
      if (change.workspaceId !== workspace.id) return
      const paths = new Set<string>()
      for (const path of expanded) {
        const affected = path === change.path || path.startsWith(`${change.path}/`)
        if (affected && change.operation === 'delete') continue
        paths.add(
          affected && (change.operation === 'move' || change.operation === 'rename') && change.destination
            ? `${change.destination}${path.slice(change.path.length)}`
            : path,
        )
      }
      setExpanded(paths)
      refresh(paths)
    }
    window.addEventListener('workspace-files-changed', onFilesChanged)
    return () => window.removeEventListener('workspace-files-changed', onFilesChanged)
  }, [expanded, refresh, workspace.id])

  const beginOperation = (operation: FileOperationTarget): void => {
    if (operationRunning.current) return
    setOperationError(null)
    setNotice(null)
    setOperation(operation)
  }

  const submitOperation = async (request: WorkspaceFileMutationRequest): Promise<void> => {
    if (operationRunning.current) return
    operationRunning.current = true
    setBusy(true)
    setOperationError(null)
    try {
      if (request.operation === 'rename' || request.operation === 'move' || request.operation === 'delete') {
        setConfirmingChanges(true)
        // 破坏性操作前先等源/目标路径上还在飞的保存(闸门在 `mutateWorkspaceFile` 里,
        // 这里提前一次是为了让挽留对话框在保存落定之后才弹)。
        await awaitDocumentSaves(workspace.id, [
          request.path,
          ...(request.destination === undefined ? [] : [request.destination])
        ])
        const confirmed = await confirmDocumentChanges(workspace.id, request.path)
        if (!alive.current) return
        setConfirmingChanges(false)
        if (!confirmed) return
      }
      const result = await mutateWorkspaceFile(request)
      if (!alive.current) return
      const noticeKeys: Record<FileOperationTarget['operation'], TranslationKey> = {
        'create-file': 'files.manage.created',
        'create-directory': 'files.manage.created',
        rename: 'files.manage.renamed',
        copy: 'files.manage.copied',
        move: 'files.manage.moved',
        delete: 'files.manage.deleted',
      }
      setNotice({ key: noticeKeys[request.operation], path: result.destination ?? result.path })
      if (result.recoveryPath) void refreshRecovery()
      setOperation(null)
      setScope('all')
      if (request.operation === 'create-file') {
        onOpenFile(result.path, result.path.split('/').at(-1) ?? result.path)
      }
    } catch (error) {
      if (alive.current) {
        const errorKey = workspaceFileErrorKey(error)
        setOperationError(errorKey)
        if (request.operation === 'delete') setNotice({ key: errorKey, error: true })
        // 「结果未知」之后面板上这份列表可能已经是假的 —— 重新去读服务器,别拿本地状态当结论
        if (isResultUnknown(error)) { refresh(); void refreshRecovery() }
      }
    } finally {
      operationRunning.current = false
      if (alive.current) {
        setBusy(false)
        setConfirmingChanges(false)
      }
    }
  }

  const reveal = async (path: string): Promise<void> => {
    try {
      await revealWorkspaceFile(workspace.id, path)
    } catch (error) {
      if (alive.current) setNotice({ key: workspaceFileErrorKey(error), error: true })
    }
  }

  const restoreDeleted = async (entry: WorkspaceRecoveryEntry): Promise<void> => {
    if (operationRunning.current) return
    operationRunning.current = true; setBusy(true)
    try {
      // ★ 带上列举时的 environmentKey:重连换了环境后这条记录属于上一个连接,主进程会拒绝
      await mutateWorkspaceFile({ workspaceId: workspace.id, operation: 'move', path: entry.recoveryPath, destination: entry.originalPath, environmentKey: recoveryKey })
      if (alive.current) setNotice({ key: 'ssh.fileRestored', path: entry.originalPath })
    } catch (error) {
      if (alive.current) {
        setNotice({ key: workspaceFileErrorKey(error), error: true })
        // 恢复也可能是「发出去了但不知道成没成」—— 树里那一行到底回来没有,只能问服务器
        if (isResultUnknown(error)) refresh()
      }
    }
    finally {
      operationRunning.current = false
      if (alive.current) { setBusy(false); void refreshRecovery() }
    }
  }

  const rows = useMemo(
    () => flatten(listings, expanded, rootPath, sortBy, showHidden, query, selectedPath),
    [listings, expanded, rootPath, sortBy, showHidden, query, selectedPath]
  )

  /*
    哪些行是这一次**新露出来的**(播入场动画)。用「渲染期对比上一次的 rows」这个 React 认可的
    写法,而不是 effect:effect 晚一帧,新行会先以终态画出来再跳回起点重播。
    初值就是首帧的 rows —— 从快照恢复的那棵树一行都不算新(见 `enterDelays`)。
  */
  const [shownRows, setShownRows] = useState(rows)
  const [entering, setEntering] = useState<ReadonlyMap<string, number>>(() => new Map())
  if (shownRows !== rows) {
    setShownRows(rows)
    setEntering(enterDelays(shownRows, rows))
  }

  /*
    键盘焦点所在的那一行(roving tabindex:整棵树只有一行 tabIndex=0)。
    原先每一行都是 tabIndex=0,于是 Tab 键要穿过树里的每一行才出得去。
    焦点行不在当前行里(被收起 / 被过滤掉)时,退到选中行,再退到第一行。
  */
  const [focusedPath, setFocusedPath] = useState<string | null>(null)
  const [tree, setTree] = useState<HTMLDivElement | null>(null)
  const tabbablePath =
    rows.find((row) => row.entry.path === focusedPath)?.entry.path ??
    rows.find((row) => row.entry.path === selectedPath)?.entry.path ??
    rows[0]?.entry.path ?? null

  const activate = (row: Row): void => {
    if (row.entry.kind === 'dir') toggleDir(row.entry.path)
    else onOpenFile(row.entry.path, row.entry.name)
  }

  const onRowKeyDown = (event: ReactKeyboardEvent<HTMLDivElement>, index: number): void => {
    // 行尾 `…` 按钮上的 Enter/空格归按钮自己
    if (event.target !== event.currentTarget) return
    const action = treeKeyAction(rows, index, event.key, expanded)
    if (action === null) return
    event.preventDefault()
    const row = rows[index]
    switch (action.kind) {
      case 'focus': {
        const target = rows[action.index]
        if (target === undefined) return
        setFocusedPath(target.entry.path)
        tree?.querySelector<HTMLElement>(`[data-tree-index="${action.index}"]`)?.focus()
        return
      }
      case 'expand':
      case 'collapse':
        toggleDir(action.path)
        return
      case 'activate':
        if (row !== undefined) activate(row)
        return
    }
  }

  const root = listings[rootPath]

  return (
    <div className="flex min-h-0 flex-1 flex-col bg-canvas">
      <div ref={toolbar} className="flex shrink-0 items-center gap-2 px-2 pb-1.5">
        <div className="flex shrink-0 items-center gap-2">
          {scopes.map((s) => (
            <button
              key={s.id}
              type="button"
              aria-pressed={scope === s.id}
              onClick={() => setScope(s.id)}
              className={cn(
                'rounded-[6px] px-1 py-0.5 text-[12.5px] transition-colors',
                // 参考里未选中的那个不是灰底按钮,就是一段浅色文字 ——
                // 选中态靠**字重 + 字色**,不靠底色。底色留给下面的树行用。
                scope === s.id ? 'font-medium text-fg' : 'text-fg-muted hover:text-fg'
              )}
            >
              {s.label}
            </button>
          ))}
        </div>

        <div className="flex min-w-0 flex-1 items-center justify-end gap-0.5">
          {query === null ? (
            <IconButton label={t('files.search')} size={24} onClick={() => setQuery('')}>
              <Search size={14} />
            </IconButton>
          ) : (
            <div className="flex min-w-0 flex-1 items-center gap-1 rounded-[8px] bg-surface-input px-1.5">
              <Search size={12} className="shrink-0 text-fg-faint" />
              <input
                autoFocus
                value={query}
                aria-label={t('files.search')}
                placeholder={t('files.filterPlaceholder')}
                onChange={(e) => setQuery(e.target.value)}
                onKeyDown={(e) => e.key === 'Escape' && setQuery(null)}
                className="min-w-0 flex-1 bg-transparent py-1 text-[12.5px] text-fg outline-none placeholder:text-fg-faint"
              />
              <IconButton label={t('files.closeSearch')} size={18} onClick={() => setQuery(null)}>
                <X size={11} />
              </IconButton>
            </div>
          )}

          {query === null &&
            (compact ? (
              /*
                窄面板:七颗折成一颗 `…`。**同一批动作、同一批 handler** ——
                折叠只是换个呈现,不是另写一套精简版工具条(那必然会慢慢长歪)。
              */
              <Menu
                label={t('files.moreActions')}
                width={190}
                trigger={<MoreHorizontal size={14} />}
                disabled={busy}
                triggerClassName="flex size-6 items-center justify-center rounded-[8px] text-icon transition-colors hover:bg-tint-hover hover:text-fg"
              >
                {(close) => (
                  <>
                    <MenuItem icon={<FilePlus2 size={14} />} onSelect={() => {
                      beginOperation({ operation: 'create-file', path: rootPath, name: '' })
                      close()
                    }}>
                      {t('files.manage.newFile')}
                    </MenuItem>
                    <MenuItem icon={<FolderPlus size={14} />} onSelect={() => {
                      beginOperation({ operation: 'create-directory', path: rootPath, name: '' })
                      close()
                    }}>
                      {t('files.manage.newDirectory')}
                    </MenuItem>
                    <MenuItem
                      icon={<ArrowUpDown size={14} />}
                      onSelect={() => {
                        cycleSort(setSortBy)
                        close()
                      }}
                    >
                      {sortLabel(t, sortBy)}
                    </MenuItem>
                    <MenuItem
                      icon={showHidden ? <EyeOff size={14} /> : <Eye size={14} />}
                      onSelect={() => {
                        setShowHidden((v) => !v)
                        close()
                      }}
                    >
                      {showHidden ? t('files.hideHidden') : t('files.showHidden')}
                    </MenuItem>
                    <MenuSeparator />
                    <MenuItem
                      icon={<ListCollapse size={14} />}
                      onSelect={() => {
                        setExpanded(new Set())
                        close()
                      }}
                    >
                      {t('files.collapseAll')}
                    </MenuItem>
                    <MenuItem icon={<FolderOpen size={14} />} onSelect={() => {
                      void reveal(rootPath)
                      close()
                    }}>
                      {t('files.manage.reveal')}
                    </MenuItem>
                    <MenuItem
                      icon={<RefreshCw size={14} />}
                      onSelect={() => {
                        refresh()
                        close()
                      }}
                    >
                      {t('common.refresh')}
                    </MenuItem>
                  </>
                )}
              </Menu>
            ) : (
              <>
                <Menu
                  label={t('files.new')}
                  trigger={<Plus size={14} />}
                  width={190}
                  disabled={busy}
                  triggerClassName="flex size-6 items-center justify-center rounded-[8px] text-icon transition-colors hover:bg-tint-hover hover:text-fg"
                >
                  {(close) => <>
                    <MenuItem icon={<FilePlus2 size={14} />} onSelect={() => {
                      beginOperation({ operation: 'create-file', path: rootPath, name: '' })
                      close()
                    }}>{t('files.manage.newFile')}</MenuItem>
                    <MenuItem icon={<FolderPlus size={14} />} onSelect={() => {
                      beginOperation({ operation: 'create-directory', path: rootPath, name: '' })
                      close()
                    }}>{t('files.manage.newDirectory')}</MenuItem>
                  </>}
                </Menu>
                <IconButton
                  label={sortLabel(t, sortBy)}
                  size={24}
                  active={sortBy !== 'name'}
                  onClick={() => cycleSort(setSortBy)}
                >
                  <ArrowUpDown size={14} />
                </IconButton>
                <IconButton
                  label={showHidden ? t('files.hideHidden') : t('files.showHidden')}
                  size={24}
                  active={showHidden}
                  onClick={() => setShowHidden((v) => !v)}
                >
                  {showHidden ? <Eye size={14} /> : <EyeOff size={14} />}
                </IconButton>
                <IconButton label={t('files.collapseAll')} size={24} onClick={() => setExpanded(new Set())}>
                  <ListCollapse size={14} />
                </IconButton>
                <IconButton label={t('files.manage.reveal')} size={24} onClick={() => void reveal(rootPath)}>
                  <FolderOpen size={14} />
                </IconButton>
                <IconButton label={t('common.refresh')} size={24} onClick={() => refresh()} disabled={loading.size > 0}>
                  <RefreshCw size={14} className={cn(loading.size > 0 && 'animate-spin motion-reduce:animate-none')} />
                </IconButton>
              </>
            ))}
        </div>
      </div>

      {recoveryEntries.length > 0 && (
        <div role="status" className="flex shrink-0 flex-col gap-1 border-y border-border px-3 py-2 text-[12px] text-fg-muted">
          <span className="shrink-0">{t('ssh.recoveryTitle', { count: String(recoveryEntries.length) })}</span>
          {/* 只列最近若干条,其余用一行计数带过 —— 窄面板下每条都要能换行,所以 break-all */}
          {recoveryEntries.slice(0, 5).map((entry) => (
            <div key={entry.token} className="flex items-start gap-2">
              <span className="min-w-0 flex-1 break-all">
                {t('ssh.fileRecovery', { path: entry.originalPath })}
                {entry.occupied && <span className="ml-1 text-fg-subtle">({t('ssh.recoveryOccupied')})</span>}
              </span>
              <IconButton disabled={busy || entry.occupied} label={t('ssh.restoreFile')} onClick={() => { void restoreDeleted(entry) }}>
                <Undo2 size={14} />
              </IconButton>
            </div>
          ))}
          {recoveryEntries.length > 5 && <span className="shrink-0">{t('ssh.recoveryMore', { count: String(recoveryEntries.length - 5) })}</span>}
        </div>
      )}
      {notice !== null && (
        <div role={notice.error ? 'alert' : 'status'} className={cn(
          'mx-2 mb-1.5 flex items-start gap-1 rounded-[8px] bg-tint px-2 py-1.5 text-[12px]',
          notice.error ? 'text-danger' : 'text-fg-muted',
        )}>
          <span className="min-w-0 flex-1 break-words">{t(notice.key, { path: notice.path ?? '' })}</span>
          <IconButton label={t('files.manage.dismissStatus')} size={18} onClick={() => setNotice(null)}>
            <X size={11} />
          </IconButton>
        </div>
      )}
      {loading.size > 0 && listings[rootPath] !== undefined && (
        <div role="status" className="flex items-center gap-1.5 px-3 py-1 text-[11.5px] text-fg-faint">
          <Spinner size="xs" />
          {t('files.manage.refreshing')}
        </div>
      )}
      <div
        ref={scroller}
        onScroll={(event) => { scrollTop.current = event.currentTarget.scrollTop }}
        className="scroll-thin min-h-0 flex-1 overflow-y-auto px-1.5 pb-2"
      >
        {scope === 'conversation' ? (
          <EmptyState title={t('files.noConversation')} description={t('files.noConversationHint')} className="py-10" />
        ) : failed.has(rootPath) ? (
          <div className="flex flex-col items-center py-8">
            <EmptyState title={t('files.unreadable')} description={t('files.unreadableHint')} className="pb-4" />
            <Button type="button" variant="secondary" size="sm" onClick={() => load(rootPath)} disabled={loading.has(rootPath)}>
              {t(loading.has(rootPath) ? 'common.loading' : 'common.retry')}
            </Button>
          </div>
        ) : root === undefined ? (
          <EmptyState title={t('common.loading')} description="" className="py-10" />
        ) : rows.length === 0 ? (
          <EmptyState
            title={query !== null && query !== '' ? t('files.noMatch') : t('files.empty')}
            description=""
            className="py-10"
          />
        ) : (
          <div ref={setTree} role="tree" aria-label={t('files.manage.tree')} aria-busy={loading.size > 0} className="relative">
            {/* ★ 必须是第一个子元素,行按文档顺序画在它上面(见 `TreeHoverGlide` 文件头) */}
            <TreeHoverGlide container={tree} />
            {rows.map(({ entry, depth }, index) => (
              <TreeRow
                key={entry.path}
                index={index}
                entry={entry}
                depth={depth}
                tabbable={entry.path === tabbablePath}
                enterDelay={entering.get(entry.path)}
                expanded={expanded.has(entry.path)}
                failed={failed.has(entry.path)}
                loading={loading.has(entry.path)}
                selected={entry.path === selectedPath}
                busy={busy}
                menuOpen={rowMenu?.entry.path === entry.path}
                onMenu={(position, toggle) => {
                  // 文件操作进行中不开菜单(与改版前 `Menu disabled={busy}` 一致)
                  if (busy) return
                  // `…` 再点一下是收起;右键永远是「在这里打开」
                  setRowMenu((current) =>
                    toggle && current?.entry.path === entry.path ? null : { entry, position })
                }}
                onFocus={() => setFocusedPath(entry.path)}
                onKeyDown={(event) => onRowKeyDown(event, index)}
                onClick={() => activate({ entry, depth })}
              />
            ))}
            {root.truncated && (
              <p className="px-2 py-2 text-[11.5px] text-fg-faint">
                {t('files.manage.truncated', { count: root.entries.length })}
              </p>
            )}
          </div>
        )}
      </div>
      {rowMenu !== null && (
        <FileRowMenu
          // 换一行 = 换一份菜单:二次确认删除、子菜单这些临时状态不能串到另一行上
          key={rowMenu.entry.path}
          workspaceId={workspace.id}
          entry={rowMenu.entry}
          position={rowMenu.position}
          local={local}
          onOperation={(operation) => beginOperation({ operation, path: rowMenu.entry.path, name: rowMenu.entry.name })}
          onDelete={() => {
            setNotice(null)
            void submitOperation({ workspaceId: workspace.id, operation: 'delete', path: rowMenu.entry.path })
          }}
          onReveal={() => void reveal(rowMenu.entry.path)}
          onClose={() => setRowMenu(null)}
        />
      )}
      {operation !== null && operation.operation !== 'delete' && (
        <FileOperationDialog
          key={`${operation.operation}:${operation.path}`}
          workspaceId={workspace.id}
          target={operation}
          busy={busy}
          hidden={confirmingChanges}
          failure={operationError}
          onSubmit={(request) => void submitOperation(request)}
          onClose={() => !busy && setOperation(null)}
        />
      )}
    </div>
  )
}

function TreeRow({
  index,
  entry,
  depth,
  tabbable,
  enterDelay,
  expanded,
  failed,
  loading,
  selected,
  busy,
  menuOpen,
  onMenu,
  onFocus,
  onKeyDown,
  onClick
}: {
  /** 在平铺行里的位置;键盘导航按它找下一行(`data-tree-index`) */
  index: number
  entry: FileEntry
  depth: number
  /** roving tabindex:整棵树只有这一行可以 Tab 进来 */
  tabbable: boolean
  /** 这一行是刚露出来的:入场动画的错开延迟(ms);undefined = 不播 */
  enterDelay: number | undefined
  expanded: boolean
  failed: boolean
  loading: boolean
  selected: boolean
  busy: boolean
  /** 这一行的菜单正开着:描边,并让行尾 `…` 保持可见 */
  menuOpen: boolean
  /**
   * 请求在某个视口坐标打开这一行的菜单。菜单本身由 `FilesView` 持有(整棵树一份),
   * 行只负责报坐标。`toggle`:从 `…` 来的再点一下是收起,右键不是。
   */
  onMenu: (position: ContextMenuPosition, toggle: boolean) => void
  onFocus: () => void
  /** 方向键 / Home End / Enter 空格 —— 解释在 `tree-rows.ts`,这里只转发 */
  onKeyDown: (event: ReactKeyboardEvent<HTMLDivElement>) => void
  onClick: () => void
}): ReactNode {
  const { t } = useI18n()
  const { Icon, className } = iconFor(entry.name, entry.kind, expanded)

  return (
    <div
      role="treeitem"
      data-tree-index={index}
      tabIndex={tabbable ? 0 : -1}
      aria-level={depth + 1}
      aria-label={failed ? t('files.manage.readFailed') : entry.name}
      aria-busy={loading}
      aria-expanded={entry.kind === 'dir' ? expanded : undefined}
      aria-selected={selected}
      aria-haspopup="menu"
      title={entry.path}
      onClick={onClick}
      onContextMenu={(event) => {
        event.preventDefault()
        /*
          键盘唤起(Shift+F10 / 菜单键)的 contextmenu 没有指针坐标(两个都是 0),
          按原样用会把菜单甩到窗口左上角 —— 落到这一行的左下方。
        */
        const fromKeyboard = event.clientX === 0 && event.clientY === 0
        const rect = event.currentTarget.getBoundingClientRect()
        onMenu(fromKeyboard ? { x: rect.left + 24, y: rect.bottom } : { x: event.clientX, y: event.clientY }, false)
      }}
      onFocus={(event) => { if (event.target === event.currentTarget) onFocus() }}
      onKeyDown={onKeyDown}
      // 缩进走 padding 而不是嵌套 div:树是**平铺**渲染的(见 flatten),
      // 这样虚拟化和键盘上下移动将来都只面对一维数组
      style={{
        paddingLeft: 4 + depth * INDENT,
        ...(enterDelay === undefined ? {} : { animationDelay: `${enterDelay}ms` })
      }}
      className={cn(
        'group relative flex h-[26px] cursor-default items-center gap-1.5 rounded-[8px] pr-1 text-[12.5px]',
        'transition-colors select-none outline-none focus-visible:ring-1 focus-visible:ring-accent',
        // 悬停底色不在行上:由 `TreeHoverGlide` 那一块在行之间滑动,这里只换字色
        selected ? 'bg-surface-raised text-fg' : 'text-fg-muted hover:text-fg',
        enterDelay !== undefined && 'file-tree-row-enter',
        // 参考截图:右键的那一行描一圈强调色,让人知道菜单是对谁的
        menuOpen && 'text-fg ring-1 ring-inset ring-accent/70'
      )}
    >
      {/*
        缩进参考线(beUI File Tree 的 branch line):每一层祖先一条竖线,落在那一层箭头的正中。
        行与行之间没有间隙,所以一列 1px 接起来就是一条连续的线,不需要额外的树形结构。
      */}
      {Array.from({ length: depth }, (_, level) => (
        <span
          key={level}
          aria-hidden
          className="pointer-events-none absolute inset-y-0 w-px bg-border"
          style={{ left: 4 + level * INDENT + GUIDE_OFFSET }}
        />
      ))}
      {/* 占位一律画:没有它,文件名会比同级目录名左移 14px,一列名字就对不齐了 */}
      <span className="flex size-3.5 shrink-0 items-center justify-center text-fg-faint">
        {loading ? <Spinner size="xs" /> : entry.kind === 'dir' && (
          <ChevronRight
            size={12}
            className={cn('transition-transform duration-200 ease-panel motion-reduce:transition-none', expanded && 'rotate-90')}
          />
        )}
      </span>
      <Icon size={14} className={cn('shrink-0', className)} />
      <span className={cn('min-w-0 flex-1 truncate', failed && 'text-danger')}>{entry.name}</span>
      <button
        type="button"
        aria-label={t('files.manage.actions', { name: entry.name })}
        aria-haspopup="menu"
        aria-expanded={menuOpen}
        disabled={busy}
        onClick={(event) => {
          // 不冒泡到行:那会顺手把文件打开 / 把目录折起来
          event.stopPropagation()
          const rect = event.currentTarget.getBoundingClientRect()
          // 右缘对齐按钮右缘(改版前 `Menu align="end"` 的落点);越界由 ContextMenu 夹回视口
          onMenu({ x: rect.right - FILE_ROW_MENU_WIDTH, y: rect.bottom + 4 }, true)
        }}
        className={cn(
          'app-no-drag flex size-[18px] shrink-0 items-center justify-center rounded-[5px] disabled:opacity-40',
          'text-fg-faint opacity-0 transition-opacity group-hover:opacity-100 group-focus-within:opacity-100',
          'hover:bg-tint-strong hover:text-fg focus-visible:opacity-100 aria-expanded:opacity-100',
        )}
      >
        <MoreHorizontal size={12} />
      </button>
    </div>
  )
}

function sortLabel(t: ReturnType<typeof useI18n>['t'], sort: SortBy): string {
  return t({ name: 'files.sortName', mtime: 'files.sortMtime', size: 'files.sortSize' }[sort])
}

function cycleSort(set: (f: (cur: SortBy) => SortBy) => void): void {
  set((cur) => (cur === 'name' ? 'mtime' : cur === 'mtime' ? 'size' : 'name'))
}

/**
 * 工具条是否该折起来。用 `ResizeObserver` 而不是窗口宽度:
 * **右侧面板自己是可拖宽的**,窗口没变而面板变了才是常态。
 */
function useToolbarCompact(ref: React.RefObject<HTMLElement | null>): boolean {
  const [compact, setCompact] = useState(false)

  useEffect(() => {
    const el = ref.current
    if (el === null) return
    const ro = new ResizeObserver(([e]) => {
      if (e !== undefined) setCompact(e.contentRect.width < TOOLBAR_FULL_WIDTH)
    })
    ro.observe(el)
    return () => ro.disconnect()
  }, [ref])

  return compact
}

/**
 * 接到 Tab 系统上的那一层。`FilesView` 自己不碰 store —— 它只认
 * 「根在哪 / 选中哪个 / 打开时叫谁」,这样它在测试里是纯的。
 */
export function FilesTab({ tab, workspace }: { tab: InnerTab; workspace: Workspace }): ReactNode {
  const openFile = useTabsStore((s) => s.openFile)

  /*
    选中行 = **右侧工作台**正在看的那个文件。选择器只回一个字符串,
    不回对象 —— 回对象的话每次 render 都是新引用,zustand 会认为状态变了。
  */
  const selectedPath = useTabsStore((s) => {
    const st = s.stateOf(workspace.id)
    const dock = s.dockOf(workspace.id)
    const rightActiveId = (node: DockNode): string | null => {
      if (node.type === 'group') {
        const rightTabs = st.tabs.filter((item) => node.tabIds.includes(item.id) && paneOf(item) === 'right')
        return rightTabs.length > 0 ? node.activeTabId : null
      }
      return rightActiveId(node.first) ?? rightActiveId(node.second)
    }
    const active = st.tabs.find((t) => t.id === rightActiveId(dock.root))
    return active !== undefined && 'path' in active.ref ? active.ref.path : null
  })

  return (
    <FilesView
      workspace={workspace}
      rootPath={tab.kind === 'files' ? tab.ref.path : ''}
      selectedPath={tab.kind === 'files' ? tab.ref.selectedPath ?? selectedPath : selectedPath}
      onOpenFile={(path, name) => openFile(workspace.id, path, name)}
    />
  )
}
