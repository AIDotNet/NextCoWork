/**
 * 对话区右上角的活动胶囊 —— 任务清单、子代理、后台命令三样「agent 手上还开着的事」
 * 收进同一个入口。
 *
 * 需求:原先任务清单是输入框上方的一张卡片(收起是一颗球),子代理只能在转录里翻卡片,
 * 后台 shell 则根本没有界面 —— 模型 `run_in_background` 起的 `npm run dev` 用户看不到、
 * 也停不掉,只能等它占着端口。三样东西回答的是同一个问题,所以放进同一个胶囊:
 * 平时只报数(进度环 + 各类在跑的数量),点开是一张分三栏的浮层。
 *
 * ★ 胶囊**浮在转录上方**(`absolute`),不占版式:它的全部意义就是把输入框上方那一块
 *   让出来。数量为 0 的那一类不画;三类都没有时整颗胶囊不画。
 * ★ 任务和子代理来自转录(本会话),后台 shell 来自主进程注册表(本工作区)——
 *   后台 shell 不随 run 结束,也不属于某一条会话,换个会话照样在跑,所以按工作区筛。
 */
import { AnimatePresence, motion } from 'motion/react'
import { Bot, ChevronRight, SquareTerminal } from 'lucide-react'
import { useCallback, useEffect, useId, useMemo, useRef, useState, type ReactNode } from 'react'
import { formatDuration } from '../../../../shared/agent/duration'
import type { SubagentState } from '../../../../shared/agent/transcript'
import { agentColorHex } from '../../../../shared/domain/agent-def'
import type { BackgroundShellInfo } from '../../../../shared/domain/shell'
import SegmentedControl from '../../components/arc/segmented-control/segmented-control'
import { Spinner } from '../../components/ui/Spinner'
import { useI18n } from '../../i18n'
import { cn } from '../../lib/cn'
import { abortRun } from '../../services/agent'
import {
  killBackgroundShell,
  listBackgroundShells,
  onBackgroundShellsChanged,
  peekBackgroundShell
} from '../../services/shell'
import { motionScale, useMotionLevel } from '../../theme/useMotionLevel'
import { ProgressRing, TaskChecklistRows, type TaskChecklistExecution, type TaskChecklistItem } from './TaskChecklist'

type ActivityTab = 'tasks' | 'agents' | 'shells'

/**
 * 本工作区的后台 shell。初值主动拉一次,之后靠全量广播收敛。
 *
 * ★ 先订阅、再拉:反过来的话,拉取与订阅之间那一刻的变化会丢,而下一次广播
 *   可能要等到某条命令退出 —— 那之前胶囊一直报着错的数。
 */
function useBackgroundShells(workspaceId: string): BackgroundShellInfo[] {
  const [shells, setShells] = useState<BackgroundShellInfo[]>([])
  useEffect(() => {
    let live = true
    let pushed = false
    const off = onBackgroundShellsChanged((all) => {
      pushed = true
      setShells(all.filter((shell) => shell.workspaceId === workspaceId))
    })
    void listBackgroundShells(workspaceId).then((initial) => {
      // 广播已经先到过的话它更新,拉取的结果作废
      if (live && !pushed) setShells(initial)
    }).catch(() => {})
    return () => {
      live = false
      off()
    }
  }, [workspaceId])
  return shells
}

/** 每秒走一格的「现在」,只在有东西在跑时才走 —— 否则面板开着也不该每秒重渲。 */
function useNow(active: boolean): number {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    if (!active) return
    setNow(Date.now())
    const timer = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(timer)
  }, [active])
  return now
}

export function ActivityCapsule({
  workspaceId,
  todos,
  execution,
  subagents,
  onOpenSubagent
}: {
  workspaceId: string
  todos: readonly TaskChecklistItem[] | undefined
  execution: TaskChecklistExecution
  subagents: readonly SubagentState[]
  onOpenSubagent: (state: SubagentState) => void
}): ReactNode {
  const { t } = useI18n()
  const shells = useBackgroundShells(workspaceId)
  const [open, setOpen] = useState(false)
  const [tab, setTab] = useState<ActivityTab>('tasks')
  const rootRef = useRef<HTMLDivElement>(null)
  const panelId = useId()
  const scale = motionScale(useMotionLevel())

  const done = todos?.filter((item) => item.status === 'completed').length ?? 0
  const total = todos?.length ?? 0
  const runningAgents = subagents.filter((state) => state.status === 'running').length
  const runningShells = shells.filter((shell) => shell.status === 'running').length
  const tasksRunning = execution === 'running' && done < total

  /*
    需求:点开时直接落在「此刻有东西在动」的那一栏 —— 用户点胶囊多半是因为看到某个
    数在跳。三栏都静止时落在第一个有内容的栏,都没有就留在用户上次看的那栏。
  */
  const pickTab = useCallback((): ActivityTab => {
    if (tasksRunning) return 'tasks'
    if (runningAgents > 0) return 'agents'
    if (runningShells > 0) return 'shells'
    if (total > 0) return 'tasks'
    if (subagents.length > 0) return 'agents'
    if (shells.length > 0) return 'shells'
    return tab
  }, [tasksRunning, runningAgents, runningShells, total, subagents.length, shells.length, tab])

  const toggle = (): void => {
    if (!open) setTab(pickTab())
    setOpen(!open)
  }

  // 点外面 / Esc 关闭。★ 挂在 pointerdown 上:click 要等抬起,拖选正文时会误关
  useEffect(() => {
    if (!open) return
    const onPointerDown = (event: PointerEvent): void => {
      if (rootRef.current?.contains(event.target as Node) !== true) setOpen(false)
    }
    const onKeyDown = (event: KeyboardEvent): void => {
      if (event.key !== 'Escape') return
      setOpen(false)
      rootRef.current?.querySelector<HTMLElement>('[data-activity-capsule]')?.focus()
    }
    document.addEventListener('pointerdown', onPointerDown, true)
    document.addEventListener('keydown', onKeyDown)
    return () => {
      document.removeEventListener('pointerdown', onPointerDown, true)
      document.removeEventListener('keydown', onKeyDown)
    }
  }, [open])

  if (total === 0 && subagents.length === 0 && shells.length === 0) return null

  const summary = [
    ...(total > 0 ? [t('activity.summary.tasks', { done, total })] : []),
    ...(runningAgents > 0 ? [t('activity.summary.agents', { count: runningAgents })] : []),
    ...(runningShells > 0 ? [t('activity.summary.shells', { count: runningShells })] : [])
  ].join(' · ')
  const progress = total === 0 ? 0 : done / total
  const unfinishedAfterStop = execution === 'stopped' && done < total

  return (
    <div ref={rootRef} className="pointer-events-none absolute top-3 right-4 z-20 flex flex-col items-end" data-testid="activity-capsule">
      <button
        type="button"
        data-activity-capsule
        aria-label={t('activity.open', { summary: summary === '' ? t('activity.title') : summary })}
        aria-expanded={open}
        aria-controls={panelId}
        title={summary === '' ? t('activity.title') : summary}
        onClick={toggle}
        className={cn(
          'pointer-events-auto relative flex h-8 items-center gap-2 rounded-pill border border-stroke bg-surface-raised/85 px-1.5 pr-2.5',
          'shadow-menu backdrop-blur-sm transition-colors duration-150 ease-out hover:bg-tint-hover motion-reduce:transition-none',
          open && 'bg-tint-hover'
        )}
      >
        {total > 0 ? (
          <span className="flex items-center gap-1.5">
            <ProgressRing progress={progress} done={done} />
            <span className="text-[11.5px] tabular-nums text-fg-muted">{done}/{total}</span>
          </span>
        ) : (
          <span aria-hidden className="ml-1 size-2 rounded-pill bg-fg-faint" />
        )}
        {runningAgents > 0 && (
          <span className="flex items-center gap-1 text-[11.5px] tabular-nums text-fg-muted">
            <Bot aria-hidden size={13} className="text-accent" />
            {runningAgents}
          </span>
        )}
        {runningShells > 0 && (
          <span className="flex items-center gap-1 text-[11.5px] tabular-nums text-fg-muted">
            <SquareTerminal aria-hidden size={13} className="text-accent" />
            {runningShells}
          </span>
        )}
        {unfinishedAfterStop && <span aria-hidden className="absolute top-0.5 right-0.5 size-1.5 rounded-pill bg-warning" />}
      </button>

      <AnimatePresence>
        {open && (
          <motion.div
            id={panelId}
            role="dialog"
            aria-label={t('activity.title')}
            initial={{ opacity: 0, scale: 0.96, y: -4 }}
            animate={{ opacity: 1, scale: 1, y: 0 }}
            exit={{ opacity: 0, scale: 0.96, y: -4 }}
            transition={{ duration: 0.16 * scale, ease: 'easeOut' }}
            className="pointer-events-auto mt-2 w-[min(400px,calc(100vw-48px))] origin-top-right overflow-hidden rounded-card border border-stroke bg-surface-menu shadow-menu"
          >
            <div className="border-b border-hairline p-2">
              <SegmentedControl
                label={t('activity.title')}
                value={tab}
                onValueChange={(next) => setTab(next as ActivityTab)}
                options={[
                  { value: 'tasks', label: t('activity.tab.tasks'), accessory: <TabCount value={total === 0 ? undefined : `${String(done)}/${String(total)}`} /> },
                  { value: 'agents', label: t('activity.tab.agents'), accessory: <TabCount value={runningAgents > 0 ? String(runningAgents) : undefined} live /> },
                  { value: 'shells', label: t('activity.tab.shells'), accessory: <TabCount value={runningShells > 0 ? String(runningShells) : undefined} live /> }
                ]}
              />
            </div>
            {tab === 'tasks' && <TasksPane todos={todos ?? []} execution={execution} />}
            {tab === 'agents' && (
              <AgentsPane
                subagents={subagents}
                onOpen={(state) => {
                  setOpen(false)
                  onOpenSubagent(state)
                }}
              />
            )}
            {tab === 'shells' && <ShellsPane shells={shells} />}
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  )
}

function TabCount({ value, live = false }: { value: string | undefined; live?: boolean }): ReactNode {
  if (value === undefined) return null
  return (
    <span className={cn('ml-1.5 text-[10.5px] tabular-nums', live ? 'text-accent' : 'text-fg-faint')}>{value}</span>
  )
}

function EmptyPane({ children }: { children: ReactNode }): ReactNode {
  return <p className="px-4 py-8 text-center text-[12px] text-fg-faint">{children}</p>
}

// ─────────────────────────── 任务 ───────────────────────────

function TasksPane({ todos, execution }: { todos: readonly TaskChecklistItem[]; execution: TaskChecklistExecution }): ReactNode {
  const { t } = useI18n()
  if (todos.length === 0) return <EmptyPane>{t('activity.empty.tasks')}</EmptyPane>
  const done = todos.filter((item) => item.status === 'completed').length
  const active = todos.find((item) => item.status === 'in_progress')
  // 判据与 `TaskChecklist` 标题行同一条:没有人在推进时不说「正在做」
  const isActive = execution === 'running'
  return (
    <div data-testid="activity-tasks" data-execution-state={execution}>
      <div className="flex items-center gap-2.5 px-3 py-2.5">
        <span className="min-w-0 flex-1">
          <span className="block truncate text-[12.5px] font-medium text-fg">
            {t('chat.taskChecklist', { done, total: todos.length })}
          </span>
          {isActive && active !== undefined && (
            <span className="mt-0.5 block truncate text-[11px] text-fg-muted">{active.activeForm}</span>
          )}
          {execution === 'stopped' && todos.length - done > 0 && (
            <span className="mt-0.5 block truncate text-[11px] text-warning">
              {t('chat.taskChecklist.stoppedIncomplete', { count: todos.length - done })}
            </span>
          )}
          {execution === 'snapshot' && (
            <span className="mt-0.5 block truncate text-[11px] text-fg-faint">{t('chat.taskChecklist.snapshot')}</span>
          )}
        </span>
        <span aria-hidden className="shrink-0 text-[10.5px] tabular-nums text-fg-faint">
          {Math.round((done / todos.length) * 100)}%
        </span>
      </div>
      <div className="[&>ul]:max-h-[min(360px,55vh)]">
        <TaskChecklistRows todos={todos} isActive={isActive} />
      </div>
    </div>
  )
}

// ─────────────────────────── 子代理 ───────────────────────────

/** 在跑的排前面;同一档里新开的在上 —— 用户关心的是「现在」。 */
function byRecency<T extends { startedAt?: number }>(running: (item: T) => boolean) {
  return (left: T, right: T): number => {
    const live = Number(running(right)) - Number(running(left))
    return live !== 0 ? live : (right.startedAt ?? 0) - (left.startedAt ?? 0)
  }
}

function AgentsPane({ subagents, onOpen }: { subagents: readonly SubagentState[]; onOpen: (state: SubagentState) => void }): ReactNode {
  const { t } = useI18n()
  const sorted = useMemo(
    () => [...subagents].sort(byRecency<SubagentState>((state) => state.status === 'running')),
    [subagents]
  )
  const now = useNow(subagents.some((state) => state.status === 'running'))
  if (sorted.length === 0) return <EmptyPane>{t('activity.empty.agents')}</EmptyPane>
  return (
    <ul className="scroll-thin max-h-[min(400px,60vh)] overflow-y-auto p-1">
      {sorted.map((state) => {
        const running = state.status === 'running'
        const elapsed = state.startedAt === undefined ? undefined : (state.endedAt ?? now) - state.startedAt
        const title = state.description ?? state.subagentType ?? t('chat.subagent.default')
        const detail = running && state.currentTool !== undefined
          ? [state.currentTool, state.currentTarget].filter(Boolean).join(' · ')
          : state.subagentType
        const statusText = running
          ? t('activity.status.running')
          : state.status === 'done'
            ? t('activity.status.done')
            : state.status === 'error'
              ? t('activity.status.error')
              : t('activity.status.stopped')
        const canOpen = state.childSessionId !== undefined
        return (
          <li key={state.callId} className="group flex items-center gap-2.5 rounded-[8px] px-2 py-1.5 hover:bg-tint-hover/60">
            <span className="flex size-5 shrink-0 items-center justify-center" aria-hidden>
              {running ? (
                <Spinner size="sm" className="text-accent" />
              ) : (
                <span className="size-2 rounded-pill" style={{ background: agentColorHex(state.color) ?? 'var(--color-fg-faint)' }} />
              )}
            </span>
            <span className="min-w-0 flex-1">
              <span className={cn('block truncate text-[12.5px]', running ? 'text-fg' : 'text-fg-muted')}>{title}</span>
              <span className="mt-0.5 flex items-center gap-1.5 truncate text-[11px] text-fg-faint">
                <span className={cn(state.status === 'error' && 'text-danger')}>{statusText}</span>
                {elapsed !== undefined && <span className="tabular-nums">· {formatDuration(elapsed)}</span>}
                {state.background === true && <span>· {t('activity.background')}</span>}
                {detail !== undefined && <span className="min-w-0 truncate">· {detail}</span>}
              </span>
            </span>
            <span className="flex shrink-0 items-center gap-1">
              {running && (
                <RowButton danger onClick={() => { void abortRun(state.childRunId, true) }}>{t('activity.stop')}</RowButton>
              )}
              {canOpen && (
                <RowButton onClick={() => onOpen(state)}>
                  {t('activity.view')}
                  <ChevronRight aria-hidden size={12} />
                </RowButton>
              )}
            </span>
          </li>
        )
      })}
    </ul>
  )
}

// ─────────────────────────── 后台命令 ───────────────────────────

function ShellsPane({ shells }: { shells: readonly BackgroundShellInfo[] }): ReactNode {
  const { t } = useI18n()
  const [expanded, setExpanded] = useState<string | null>(null)
  const sorted = useMemo(
    () => [...shells].sort(byRecency<BackgroundShellInfo>((shell) => shell.status === 'running')),
    [shells]
  )
  const now = useNow(shells.some((shell) => shell.status === 'running'))
  if (sorted.length === 0) return <EmptyPane>{t('activity.empty.shells')}</EmptyPane>
  return (
    <ul className="scroll-thin max-h-[min(440px,65vh)] overflow-y-auto p-1">
      {sorted.map((shell) => {
        const running = shell.status === 'running'
        const statusText = running
          ? t('activity.status.running')
          : shell.status === 'killed'
            ? t('activity.status.stopped')
            : shell.status === 'failed'
              ? t('activity.status.failed')
              : shell.exitCode === undefined
                ? t('activity.status.exitedNoCode')
                : t('activity.status.exited', { code: shell.exitCode })
        const failed = shell.status === 'failed' || (shell.status === 'exited' && shell.exitCode !== undefined && shell.exitCode !== 0)
        const isOpen = expanded === shell.id
        return (
          <li key={shell.id} className="rounded-[8px] hover:bg-tint-hover/40">
            <div className="flex items-center gap-2.5 px-2 py-1.5">
              <span className="flex size-5 shrink-0 items-center justify-center" aria-hidden>
                {running ? (
                  <Spinner size="sm" className="text-accent" />
                ) : (
                  <SquareTerminal size={13} className="text-fg-faint" />
                )}
              </span>
              <span className="min-w-0 flex-1">
                <span className={cn('block truncate font-mono text-[12px]', running ? 'text-fg' : 'text-fg-muted')} title={shell.command}>
                  {shell.command}
                </span>
                <span className="mt-0.5 flex items-center gap-1.5 truncate text-[11px] text-fg-faint">
                  <span className={cn(failed && 'text-danger')}>{statusText}</span>
                  <span className="tabular-nums">· {formatDuration((shell.endedAt ?? now) - shell.startedAt)}</span>
                  {shell.description !== undefined && <span className="min-w-0 truncate">· {shell.description}</span>}
                </span>
              </span>
              <span className="flex shrink-0 items-center gap-1">
                <RowButton onClick={() => setExpanded(isOpen ? null : shell.id)} pressed={isOpen}>
                  {isOpen ? t('activity.hideOutput') : t('activity.output')}
                </RowButton>
                {running && (
                  <RowButton danger onClick={() => { void killBackgroundShell(shell.id).catch(() => {}) }}>
                    {t('activity.stop')}
                  </RowButton>
                )}
              </span>
            </div>
            {isOpen && <ShellOutput id={shell.id} running={running} />}
          </li>
        )
      })}
    </ul>
  )
}

/**
 * 展开的那条命令的输出尾巴。在跑时每 1.5 秒重看一次 —— 输出增长不走广播
 * (见 `shell:backgroundChanged`),所以只有展开着的这一条才去拉。
 */
function ShellOutput({ id, running }: { id: string; running: boolean }): ReactNode {
  const { t } = useI18n()
  const [output, setOutput] = useState<string | null>(null)
  const preRef = useRef<HTMLPreElement>(null)
  // 贴着底部时跟随新输出;用户往上翻了就别把他拽回去
  const stickRef = useRef(true)
  useEffect(() => {
    let live = true
    const load = (): void => {
      void peekBackgroundShell(id).then((result) => {
        if (live) setOutput(result?.output ?? '')
      }).catch(() => {})
    }
    load()
    if (!running) return () => { live = false }
    const timer = window.setInterval(load, 1500)
    return () => {
      live = false
      window.clearInterval(timer)
    }
  }, [id, running])
  useEffect(() => {
    const node = preRef.current
    if (node !== null && stickRef.current) node.scrollTop = node.scrollHeight
  }, [output])
  return (
    <pre
      ref={preRef}
      onScroll={(event) => {
        const node = event.currentTarget
        stickRef.current = node.scrollHeight - node.scrollTop - node.clientHeight < 8
      }}
      className="scroll-thin mx-2 mb-2 max-h-48 overflow-auto rounded-[8px] border border-hairline bg-surface-raised/60 px-2.5 py-2 font-mono text-[11px] leading-relaxed whitespace-pre-wrap break-all text-fg-muted"
    >
      {output === null ? '…' : output === '' ? t('activity.noOutput') : output}
    </pre>
  )
}

function RowButton({
  children,
  onClick,
  danger = false,
  pressed
}: {
  children: ReactNode
  onClick: () => void
  danger?: boolean
  pressed?: boolean
}): ReactNode {
  return (
    <button
      type="button"
      onClick={onClick}
      {...(pressed === undefined ? {} : { 'aria-pressed': pressed })}
      className={cn(
        'flex h-6 items-center gap-0.5 rounded-[6px] px-2 text-[11.5px] transition-colors duration-150 motion-reduce:transition-none',
        danger ? 'text-danger hover:bg-danger/10' : 'text-fg-muted hover:bg-tint-hover hover:text-fg',
        pressed === true && 'bg-tint-hover text-fg'
      )}
    >
      {children}
    </button>
  )
}
