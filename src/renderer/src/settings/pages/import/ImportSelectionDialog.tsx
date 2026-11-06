/**
 * 「选择导入」弹窗 —— 三分组、可折叠、逐项勾选,最后提交一次。
 *
 * ★ **判断逻辑全在 `selection.ts`**,这里只有 DOM 和取数(照
 * `model/ImportModelsDialog.tsx` 的先例)。半选态、提交门禁那几条规则
 * 留在 `.tsx` 里的话,vitest 一行都测不到。
 *
 * ★ 条目**分页取**,不是一次拉全部。一个跑了半年的 Claude Code 目录有几百个
 * 会话,整包过一次结构化克隆就是打开弹窗时肉眼可见的一卡
 * (见 `shared/domain/import.ts` 的 `ImportPreview` 注释)。
 */
import { AlertTriangle, ChevronDown, Search } from 'lucide-react'
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import type {
  ImportCategory,
  ImportPreview,
  ImportPreviewItem,
  ImportProjectCandidate
} from '../../../../../shared/domain/import'
import { IMPORT_LIMITS } from '../../../../../shared/domain/import'
import type { Workspace } from '../../../../../shared/domain/workspace'
import { Button } from '../../../components/arc/button/button'
import { Dialog } from '../../../components/ui/Dialog'
import { EmptyState } from '../../../components/arc/empty-state/empty-state'
import { Select } from '../../../components/ui/Select'
import { TextInput } from '../../../components/ui/TextInput'
import { cn } from '../../../lib/cn'
import { useI18n, type TranslationKey } from '../../../i18n'
import * as importService from '../../../services/import'
import { sourceNameKey } from './source-name'
import { Box } from './ImportSyncDialog'
import { Spinner } from '../../../components/ui/Spinner'
import {
  IMPORT_GROUPS,
  groupState,
  initialSelection,
  isSelectable,
  resolvedProjectKeys,
  selectedCounts,
  submitBlockers,
  toggleGroup,
  toggleItem,
  type ImportGroupId
} from './selection'

/**
 * 搜索防抖窗口。★ 每次按键都发一趟 IPC 的话,主进程正忙着扫描时这些请求会排队,
 * 表现是输入框跟不上手 —— 卡的不是渲染,是回包。200ms 打得完一串连续输入,
 * 又短到停手之后不会被察觉。
 */
const SEARCH_DEBOUNCE_MS = 200

export function ImportSelectionDialog({
  open,
  preview,
  workspaces,
  busy,
  onClose,
  onApply
}: {
  open: boolean
  preview: ImportPreview | null
  workspaces: readonly Workspace[]
  busy: boolean
  onClose: () => void
  onApply: (itemIds: string[], targets: Array<{ projectKey: string; workspaceId: string }>) => void
}): ReactNode {
  const { t } = useI18n()
  const [items, setItems] = useState<ImportPreviewItem[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [expanded, setExpanded] = useState<Set<ImportGroupId>>(new Set(['providers', 'projects', 'skills', 'hooks', 'tools']))
  const [query, setQuery] = useState('')
  /* 输入框读 `query`(必须跟手),发请求读这个(慢一拍)。 */
  const [debouncedQuery, setDebouncedQuery] = useState('')
  const [projectFilter, setProjectFilter] = useState('')
  const [targets, setTargets] = useState<Map<string, string>>(new Map())

  const previewId = preview?.previewId

  /*
    ★ 一次把当前筛选下的条目拉进来,上限是分页大小的若干倍;超出的靠筛选收窄。
    不做无限滚动是因为这个弹窗的用法是「看一眼、勾几个、提交」,而不是浏览 ——
    真要找某一条,搜索比滚动快得多。
  */
  /** 最后一次发出的请求序号。用来把先发后回的旧结果认出来并丢掉。 */
  const requestSeq = useRef(0)

  const load = useCallback(async (): Promise<void> => {
    if (previewId === undefined) return
    const seq = (requestSeq.current += 1)
    setLoading(true)
    setError(null)
    try {
      const page = await importService.previewItems({
        previewId,
        ...(debouncedQuery.trim() === '' ? {} : { q: debouncedQuery.trim() }),
        ...(projectFilter === '' ? {} : { projectKey: projectFilter }),
        offset: 0,
        limit: IMPORT_LIMITS.pageSize
      })
      /*
        ★ 防抖挡不住乱序。两次请求在途时,先发的那次完全可能后回 ——
        真这样的话列表里显示的是**上一个搜索词**的结果,而且看不出哪里不对。
        只认最后一次发出去的那趟,其余的结果整个丢掉。
      */
      if (seq !== requestSeq.current) return
      setItems(page.items)
      setSelected((prev) => (prev.size === 0 ? initialSelection(page.items, projectsRef.current) : prev))
    } catch (err) {
      if (seq !== requestSeq.current) return
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      // 作废的那趟不许碰 loading —— 否则它一收尾就把仍在飞的新请求标成「已完成」。
      if (seq === requestSeq.current) setLoading(false)
    }
  }, [previewId, debouncedQuery, projectFilter])

  useEffect(() => {
    if (!open) return
    void load()
  }, [open, load])

  useEffect(() => {
    const timer = setTimeout(() => setDebouncedQuery(query), SEARCH_DEBOUNCE_MS)
    return () => clearTimeout(timer)
  }, [query])

  // 弹窗关掉再打开 = 一次新的选择。不清的话上次的勾会留在下一次的预览上,
  // 而那批 id 属于一个已经过期的快照。
  useEffect(() => {
    if (open) return
    setSelected(new Set())
    setItems([])
    setQuery('')
    setDebouncedQuery('')
    setProjectFilter('')
    setTargets(new Map())
  }, [open])

  const projects = useMemo(() => preview?.projects ?? [], [preview])
  /*
    ★ `load` 的依赖里不能放 `projects` —— preview 换一次它就是一个新数组,
    而 `load` 变化会触发 effect 重新拉一次,于是无限循环。用 ref 取当前值。
  */
  const projectsRef = useRef(projects)
  projectsRef.current = projects

  /**
   * 这一轮结束后会有工作区的项目。★ 它决定 `needs-target` 的聊天能不能勾 ——
   * 详见 `selection.ts` 的 `resolvedProjectKeys`(那是隔离 Electron 探针
   * 抓出来的一个真 bug 的答案)。
   */
  const resolved = useMemo(
    () => resolvedProjectKeys(items, selected, projects, targets),
    [items, selected, projects, targets]
  )
  const blockers = useMemo(
    () => submitBlockers(items, selected, projects, targets),
    [items, selected, projects, targets]
  )
  const counts = useMemo(() => selectedCounts(items, selected), [items, selected])

  const byGroup = useMemo(() => {
    const map = new Map<ImportGroupId, ImportPreviewItem[]>()
    for (const group of IMPORT_GROUPS) map.set(group.id, [])
    for (const item of items) {
      const group = IMPORT_GROUPS.find((g) => g.categories.includes(item.category))
      if (group !== undefined) map.get(group.id)?.push(item)
    }
    return map
  }, [items])

  const localWorkspaces = workspaces.filter((w) => w.rootPath !== '')

  return (
    <Dialog
      open={open}
      title={t('import.selectDialogTitle')}
      /*
        ★ 来源名读 `preview.sourceKind`(这份快照实际扫的那家),**不是**页面上
        来源切换按钮的当前值。扫完之后用户还能接着切按钮,读按钮就会出现
        「扫的是 Codex、提示说 Claude Code」。preview 为空时弹窗本就是关着的,
        兜底值只是为了不让类型上出现 undefined。
      */
      description={t('import.selectDialogHint', {
        source: t(sourceNameKey(preview?.sourceKind ?? 'claude-code'))
      })}
      onClose={onClose}
      width={640}
      footer={
        <>
          <Button size="sm" type="button" variant="secondary" onClick={onClose}>{t('import.cancel')}</Button>
          <Button size="sm"
            type="button"
            variant="primary"
            disabled={busy || loading || !blockers.ok}
            onClick={() => onApply([...selected], [...targets].map(([projectKey, workspaceId]) => ({ projectKey, workspaceId })))}
          >
            {busy ? <Spinner size="sm" /> : null}
            {`${t('import.applyButton')}${counts.total > 0 ? ` (${String(counts.total)})` : ''}`}
          </Button>
        </>
      }
    >
      <div className="mb-3 flex items-center gap-2">
        <TextInput
          value={query}
          onChange={setQuery}
          placeholder={t('import.searchPlaceholder')}
          ariaLabel={t('import.searchPlaceholder')}
          icon={<Search size={13} />}
          className="flex-1"
        />
        <Select
          value={projectFilter}
          onValueChange={setProjectFilter}
          ariaLabel={t('import.filterAllProjects')}
          inModal
          /* 同上:不限宽的话它会把左边那个 flex-1 的搜索框挤成 0 宽。 */
          className="w-[220px] shrink-0"
          options={[
            { value: '', label: t('import.filterAllProjects') },
            ...projects.map((p) => ({ value: p.key, label: shortPath(p.sourcePath) }))
          ]}
        />
      </div>

      {error !== null && (
        <p role="alert" className="mb-3 flex items-center gap-1.5 text-[12px] text-danger">
          <AlertTriangle size={13} />
          {error}
        </p>
      )}

      {loading && items.length === 0 && (
        <p role="status" className="flex items-center gap-1.5 py-6 text-[12px] text-fg-muted">
          <Spinner size="sm" />
          {t('import.scanning')}
        </p>
      )}

      {!loading && items.length === 0 && error === null && <EmptyState title={t('import.noItems')} description="" />}

      {IMPORT_GROUPS.map((group) => {
        const groupItems = byGroup.get(group.id) ?? []
        if (groupItems.length === 0) return null
        const state = groupState(groupItems, selected, resolved)
        const isOpen = expanded.has(group.id)
        return (
          <section key={group.id} className="mb-2.5 overflow-hidden rounded-[10px] border border-border">
            <div className="flex items-center gap-2 bg-surface-field px-2.5 py-2">
              <button
                type="button"
                role="checkbox"
                aria-checked={state === 'all'}
                aria-label={t(`import.group.${group.id}` as TranslationKey)}
                onClick={() => setSelected(toggleGroup(groupItems, selected, resolved))}
                className="app-no-drag"
              >
                <Box checked={state === 'all'} partial={state === 'some'} />
              </button>
              <button
                type="button"
                onClick={() => {
                  const next = new Set(expanded)
                  if (next.has(group.id)) next.delete(group.id)
                  else next.add(group.id)
                  setExpanded(next)
                }}
                className="app-no-drag flex min-w-0 flex-1 items-center gap-1.5 text-left"
              >
                <span className="truncate text-[12.5px] text-fg">
                  {t(`import.group.${group.id}` as TranslationKey)}
                </span>
                <span className="text-[11.5px] text-fg-faint">{groupItems.length}</span>
                <ChevronDown size={13} className={cn('ml-auto shrink-0 transition-transform', !isOpen && '-rotate-90')} />
              </button>
            </div>

            {isOpen && (
              <ul>
                {groupItems.map((item) => (
                  <ItemRow
                    key={item.id}
                    item={item}
                    checked={selected.has(item.id)}
                    selectable={isSelectable(item, resolved)}
                    workspaces={localWorkspaces}
                    target={item.projectKey === undefined ? undefined : targets.get(item.projectKey)}
                    onToggle={() => setSelected(toggleItem(item, selected, resolved))}
                    onTarget={(workspaceId) => {
                      if (item.projectKey === undefined) return
                      const next = new Map(targets)
                      if (workspaceId === '') next.delete(item.projectKey)
                      else next.set(item.projectKey, workspaceId)
                      setTargets(next)
                    }}
                  />
                ))}
              </ul>
            )}
          </section>
        )
      })}

      {!blockers.ok && counts.total > 0 && (
        <p role="status" className="mt-1 text-[11.5px] text-fg-muted">
          {t('import.needsTarget')}
        </p>
      )}
    </Dialog>
  )
}

function ItemRow({
  item,
  checked,
  selectable,
  workspaces,
  target,
  onToggle,
  onTarget
}: {
  item: ImportPreviewItem
  checked: boolean
  /** ★ 由调用方算好传进来 —— 它依赖「这一轮还勾了哪些项目」,单看这一项算不出。 */
  selectable: boolean
  workspaces: readonly Workspace[]
  target: string | undefined
  onToggle: () => void
  onTarget: (workspaceId: string) => void
}): ReactNode {
  const { t } = useI18n()
  /*
    ★ 只有「聊天缺目标」和「项目本身」要显示工作区选择器。
    给每一行都配一个的话,一个技能包旁边会出现一个跟它毫无关系的下拉框。
  */
  const needsTarget = item.status === 'needs-target' || item.category === 'project'

  return (
    <li className="border-b border-hairline last:border-b-0">
      <div className="flex items-center gap-2.5 px-2.5 py-2">
        <button
          type="button"
          role="checkbox"
          aria-checked={checked}
          aria-label={item.title}
          disabled={!selectable}
          onClick={onToggle}
          className={cn('app-no-drag', !selectable && 'cursor-not-allowed opacity-40')}
        >
          <Box checked={checked} />
        </button>
        <div className="min-w-0 flex-1">
          <p className="truncate text-[12.5px] text-fg">{item.title}</p>
          <p className="truncate text-[11px] text-fg-faint">
            {shortPath(item.sourcePath)}
            {item.provider !== undefined ? ` · ${item.provider.protocol} · ${item.provider.baseUrl}` : ''}
            {item.hook !== undefined ? ` · ${item.hook.event}${item.hook.command ? ` · ${item.hook.command}` : ''}` : ''}
            {item.count !== undefined && item.category === 'chat'
              ? ` · ${t('import.sessionsCount', { count: item.count })}`
              : ''}
          </p>
          {item.diagnostics.length > 0 && (
            <p className="mt-0.5 truncate text-[11px] text-fg-muted">
              {item.diagnostics.map((d) => t(`import.diag.${d.code}` as TranslationKey)).join(' · ')}
            </p>
          )}
        </div>

        {needsTarget && item.projectKey !== undefined && (
          <Select
            value={target ?? item.targetWorkspaceId ?? ''}
            onValueChange={onTarget}
            ariaLabel={t('import.needsTarget')}
            inModal
            /*
              ★★ **必须限宽 + shrink-0。**

              `Select` 的触发器自带 `w-full`(它本来是给整行独占的表单行用的)。
              放进这个 flex 行之后,它要走 100% 宽度,而左边的标题块是
              `min-w-0 flex-1` —— 于是标题块被挤成 **0 像素宽**,配上 `truncate`
              的 overflow-hidden,标题、路径、诊断三行全部被裁成看不见。

              症状极具欺骗性:DOM 里那几行文字**一个字都不少**,只是宽度为 0。
              所以"会话没有标题"看起来像数据没解析出来,实际是布局吃掉了它。
            */
            className="w-[168px] shrink-0"
            options={[
              { value: '', label: item.category === 'project' ? t('import.createWorkspace') : t('import.noTarget') },
              ...workspaces.map((w) => ({ value: w.id, label: w.name }))
            ]}
          />
        )}

        <span className="shrink-0 text-[11px] text-fg-faint">
          {t(`import.status.${item.status}` as TranslationKey)}
        </span>
      </div>
    </li>
  )
}

/** 长路径只留后三段。整条绝对路径会把行撑出横向滚动条,而用户认的是尾巴。 */
function shortPath(path: string): string {
  const parts = path.split(/[\\/]/).filter((p) => p !== '')
  if (parts.length <= 3) return path
  return `…/${parts.slice(-3).join('/')}`
}

export type { ImportCategory, ImportProjectCandidate }
