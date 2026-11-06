import { AlertTriangle, Clock3, Play, Plus, Trash2, Pencil, X } from 'lucide-react'
import { useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent, type ReactNode } from 'react'
import type { ScheduledRun, ScheduledTask, ScheduledTaskInput, ScheduleRule } from '../../../../shared/domain/scheduled'
import type { Workspace } from '../../../../shared/domain/workspace'
import { modelSelectionKey, parseModelSelectionKey } from '../../../../shared/domain/model-selection'
import { EmptyState } from '../../components/arc/empty-state/empty-state'
import { Button } from '../../components/arc/button/button'
import { Dialog } from '../../components/ui/Dialog'
import { Select, type SelectOption } from '../../components/ui/Select'
import SegmentedControl from '../../components/arc/segmented-control/segmented-control'
import { TextArea } from '../../components/ui/TextArea'
import { TextInput } from '../../components/ui/TextInput'
import { Switch } from '../../components/arc/switch/switch'
import { useI18n } from '../../i18n'
import { useModelsStore } from '../../stores/models'
import { useWindowStore } from '../../stores/window'
import {
  createScheduledTask,
  deleteScheduledTask,
  deleteScheduledRun,
  listScheduledRuns,
  listScheduledTasks,
  onScheduledChanged,
  onScheduledFocusRun,
  runScheduledTaskNow,
  setScheduledTaskEnabled,
  updateScheduledTask
} from '../../services/scheduled'
import { cn } from '../../lib/cn'
import { ContextMenu, type ContextMenuPosition } from '../../components/ui/ContextMenu'
import { ChatView } from '../chat/ChatView'
import type { FallbackModel } from '../chat/Composer'
import { Spinner } from '../../components/ui/Spinner'
import { SidebarReveal } from '../../shell/SidebarReveal'

type PageMode = 'runs' | 'tasks'
type DialogState = { mode: 'create' | 'edit'; task?: ScheduledTask } | null

const DEFAULT_RULE: ScheduleRule = { kind: 'daily', time: '09:00' }

export function ScheduledFeature({ onClose }: { onClose?: () => void } = {}): ReactNode {
  const { t } = useI18n()
  const workspaceTargets = useWindowStore((state) => state.workspaceTargets)
  const activeWorkspaceId = useWindowStore((state) => state.activeWorkspaceId)
  const clearScheduledUnread = useWindowStore((state) => state.clearScheduledUnread)
  const workspaces = useMemo(() => Object.values(workspaceTargets), [workspaceTargets])
  const models = useModelsStore((state) => state.models)
  const providers = useModelsStore((state) => state.providers)
  const loadModels = useModelsStore((state) => state.load)
  const [mode, setMode] = useState<PageMode>('runs')
  const [tasks, setTasks] = useState<ScheduledTask[]>([])
  const [runs, setRuns] = useState<ScheduledRun[]>([])
  const [selectedTaskId, setSelectedTaskId] = useState<string | null>(null)
  const [selectedRunId, setSelectedRunId] = useState<string | null>(null)
  const [filterWorkspace, setFilterWorkspace] = useState('')
  const [dialog, setDialog] = useState<DialogState>(null)
  const [confirmDelete, setConfirmDelete] = useState<ScheduledTask | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [runMenu, setRunMenu] = useState<{ run: ScheduledRun; position: ContextMenuPosition } | null>(null)
  const [confirmDeleteRun, setConfirmDeleteRun] = useState<ScheduledRun | null>(null)
  const [listWidth, setListWidth] = useState(320)
  const [resizing, setResizing] = useState(false)
  const mainRef = useRef<HTMLDivElement>(null)
  const loadVersion = useRef(0)

  const clampListWidth = (requested: number): number => {
    const total = mainRef.current?.getBoundingClientRect().width ?? 960
    const minList = 260
    const minDetail = 420
    const max = Math.max(minList, Math.min(520, total - minDetail))
    return Math.min(max, Math.max(minList, requested))
  }

  const resizeFromPointer = (event: ReactPointerEvent<HTMLDivElement>): void => {
    if (!event.currentTarget.hasPointerCapture(event.pointerId)) return
    const bounds = mainRef.current?.getBoundingClientRect()
    if (bounds === undefined) return
    setListWidth(clampListWidth(event.clientX - bounds.left))
  }

  const finishResize = (event: ReactPointerEvent<HTMLDivElement>): void => {
    if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId)
    setResizing(false)
  }

  const load = async (): Promise<void> => {
    const version = ++loadVersion.current
    try {
      const [nextTasks, nextRuns] = await Promise.all([
        listScheduledTasks(filterWorkspace || undefined),
        listScheduledRuns(undefined, 100)
      ])
      if (version !== loadVersion.current) return
      setTasks(nextTasks)
      setRuns(nextRuns)
      setSelectedTaskId((current) => current !== null && nextTasks.some((task) => task.id === current) ? current : (nextTasks[0]?.id ?? null))
      setSelectedRunId((current) => current !== null && nextRuns.some((run) => run.id === current) ? current : (nextRuns[0]?.id ?? null))
    } catch {
      setError(t('scheduled.operationFailed'))
    }
  }

  useEffect(() => { clearScheduledUnread(); void load(); void loadModels() }, [filterWorkspace, loadModels, clearScheduledUnread])
  useEffect(() => onScheduledChanged(() => { void load() }), [])
  useEffect(() => onScheduledFocusRun(({ runId }) => {
    setMode('runs')
    setSelectedRunId(runId)
    void load()
  }), [])

  const selectedTask = tasks.find((task) => task.id === selectedTaskId) ?? null
  const selectedRun = runs.find((run) => run.id === selectedRunId) ?? null
  const orderedTasks = [...tasks].sort((a, b) => Number(b.enabled) - Number(a.enabled) || a.name.localeCompare(b.name))
  const workspaceName = (id: string): string => workspaces.find((workspace) => workspace.id === id)?.name ?? t('scheduled.noWorkspace')
  const fallbackModel: FallbackModel = {
    model: models[0]?.alias ?? '',
    modelProviderId: models[0]?.providerId
  }
  const workspaceOptions: SelectOption[] = [
    { value: '', label: t('scheduled.workspaceFilter') },
    ...workspaces.map((workspace) => ({ value: workspace.id, label: workspace.name }))
  ]

  const create = async (input: ScheduledTaskInput): Promise<void> => {
    try {
      const created = await createScheduledTask(input)
      setDialog(null)
      setMode('tasks')
      setSelectedTaskId(created.id)
      await load()
    } catch { setError(t('scheduled.operationFailed')) }
  }

  const edit = async (input: ScheduledTaskInput): Promise<void> => {
    if (dialog?.task === undefined) return
    try {
      const updated = await updateScheduledTask(dialog.task.id, input)
      setDialog(null)
      setSelectedTaskId(updated.id)
      await load()
    } catch { setError(t('scheduled.operationFailed')) }
  }

  const runNow = async (task: ScheduledTask): Promise<void> => {
    try {
      const run = await runScheduledTaskNow(task.id)
      setMode('runs')
      setSelectedRunId(run.id)
      await load()
    } catch { setError(t('scheduled.operationFailed')) }
  }

  const toggle = async (task: ScheduledTask): Promise<void> => {
    try { await setScheduledTaskEnabled(task.id, !task.enabled); await load() }
    catch { setError(t('scheduled.operationFailed')) }
  }

  const remove = async (): Promise<void> => {
    if (confirmDelete === null) return
    try {
      await deleteScheduledTask(confirmDelete.id)
      setConfirmDelete(null)
      await load()
    } catch { setError(t('scheduled.operationFailed')) }
  }

  const removeRun = async (): Promise<void> => {
    if (confirmDeleteRun === null) return
    try {
      await deleteScheduledRun(confirmDeleteRun.id)
      setConfirmDeleteRun(null)
      setRunMenu(null)
      await load()
    } catch { setError(t('scheduled.operationFailed')) }
  }

  return (
    <div ref={mainRef} className={cn('flex min-h-0 flex-1 bg-canvas', resizing && 'select-none')}>
      <section style={{ width: listWidth }} className="flex shrink-0 flex-col border-r border-hairline">
        <div className="px-4 pb-3 pt-4">
          <div className="flex items-center justify-between gap-2">
            {/* 收起态下这里既让出红绿灯也把侧边栏叫得回来,见 shell/SidebarReveal */}
            <div className="flex min-w-0 items-center gap-2">
              <SidebarReveal />
              <div className="text-[14px] font-medium text-fg">{t('scheduled.title')}</div>
            </div>
            {onClose !== undefined && <button type="button" className="rounded-[6px] p-1 text-fg-faint hover:bg-tint-hover hover:text-fg" onClick={onClose} aria-label={t('scheduled.close')}><X size={15} /></button>}
            <Select value={filterWorkspace} options={workspaceOptions} onValueChange={setFilterWorkspace} ariaLabel={t('scheduled.workspaceFilter')} className="w-[138px]" />
          </div>
          <p className="mt-1 text-[12px] leading-relaxed text-fg-faint">{t('scheduled.description')}</p>
          <Button type="button" variant="secondary" className="mt-3 w-full border border-fg" onClick={() => setDialog({ mode: 'create' })}><Plus size={15} />{t('scheduled.newTask')}</Button>
          <SegmentedControl value={mode} onValueChange={(v) => setMode(v as PageMode)} options={[{ value: 'runs', label: t('scheduled.executions') }, { value: 'tasks', label: t('scheduled.tasks') }]} className="mt-3 flex w-full [&>button]:flex-1" />
        </div>
        <div className="min-h-0 flex-1 overflow-y-auto px-2 pb-3">
          {mode === 'tasks' ? (
            tasks.length === 0 ? <EmptyState icon={<Clock3 size={26} />} title={t('scheduled.emptyTasks')} description={t('scheduled.emptyTasksHint')} /> :
              <div className="space-y-1">{orderedTasks.map((task) => (
                <button key={task.id} type="button" onClick={() => setSelectedTaskId(task.id)} className={cn('flex w-full items-center justify-between rounded-[8px] px-2.5 py-2 text-left text-[13px] transition-colors', selectedTaskId === task.id ? 'bg-tint-strong text-fg' : 'text-fg-muted hover:bg-tint-hover hover:text-fg')}>
                  <span className="min-w-0 truncate">{task.name}</span><Switch checked={task.enabled} onCheckedChange={() => { void toggle(task) }} aria-label={t('scheduled.enabled')} />
                </button>
              ))}</div>
          ) : (
            runs.length === 0 ? <EmptyState icon={<Clock3 size={26} />} title={t('scheduled.emptyRuns')} description={t('scheduled.emptyRunsHint')} /> :
              <div className="space-y-3 pt-2">
                {(['today', 'earlier'] as const).map((group) => {
                  const start = new Date(); start.setHours(0, 0, 0, 0)
                  const grouped = runs.filter((run) => group === 'today' ? run.scheduledAt >= start.getTime() : run.scheduledAt < start.getTime())
                  if (grouped.length === 0) return null
                  return <div key={group}><div className="px-2 text-[11px] text-fg-faint">{t(`scheduled.${group}` as 'scheduled.today' | 'scheduled.earlier')}</div>{grouped.map((run) => {
                    const task = tasks.find((candidate) => candidate.id === run.taskId)
                    return <button key={run.id} type="button" onClick={() => setSelectedRunId(run.id)} onContextMenu={(event) => { event.preventDefault(); setSelectedRunId(run.id); setRunMenu({ run, position: { x: event.clientX, y: event.clientY } }) }} className={cn('flex w-full items-center justify-between rounded-[8px] px-2.5 py-2 text-left text-[13px]', selectedRunId === run.id ? 'bg-tint-strong text-fg' : 'text-fg-muted hover:bg-tint-hover hover:text-fg')}><span className="min-w-0 truncate">{task?.name ?? run.taskId}</span><RunStatus status={run.status} /></button>
                  })}</div>
                })}
              </div>
          )}
        </div>
      </section>
      <div
        role="separator"
        tabIndex={0}
        aria-orientation="vertical"
        aria-label={t('scheduled.resizeList')}
        className={cn('group relative z-10 flex w-1.5 shrink-0 cursor-col-resize touch-none items-stretch justify-center bg-transparent outline-none', resizing && 'bg-accent/20')}
        onPointerDown={(event) => { event.currentTarget.setPointerCapture(event.pointerId); setResizing(true); resizeFromPointer(event) }}
        onPointerMove={resizeFromPointer}
        onPointerUp={finishResize}
        onPointerCancel={finishResize}
        onKeyDown={(event) => {
          if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
            event.preventDefault()
            setListWidth((current) => clampListWidth(current + (event.key === 'ArrowRight' ? 16 : -16)))
          }
          if (event.key === 'Home') { event.preventDefault(); setListWidth(clampListWidth(260)) }
          if (event.key === 'End') { event.preventDefault(); setListWidth(clampListWidth(9999)) }
        }}
      >
        <span className={cn('h-full w-px bg-hairline transition-colors group-hover:bg-accent/60', resizing && 'bg-accent')} />
      </div>
      <section className={cn('min-w-0 flex-1', mode === 'runs' ? 'flex min-h-0 flex-col overflow-hidden p-0' : 'overflow-y-auto p-5')}>
        {error !== null && <div className="mb-3 flex items-center justify-between rounded-[8px] bg-danger/10 px-3 py-2 text-[12px] text-danger"><span>{error}</span><button type="button" onClick={() => setError(null)} aria-label={t('common.close')}>×</button></div>}
        {mode === 'tasks' ? <TaskDetail task={selectedTask} runs={runs} workspaceName={workspaceName} onRun={runNow} onEdit={(task) => setDialog({ mode: 'edit', task })} onDelete={setConfirmDelete} onToggle={toggle} /> : <RunDetail run={selectedRun} task={selectedRun ? tasks.find((task) => task.id === selectedRun.taskId) ?? null : null} workspace={selectedRun ? workspaces.find((workspace) => workspace.id === tasks.find((task) => task.id === selectedRun.taskId)?.workspaceId) ?? null : null} workspaceName={workspaceName} fallbackModel={fallbackModel} onRun={runNow} onEdit={(task) => setDialog({ mode: 'edit', task })} />}
      </section>
      {runMenu !== null && <ContextMenu position={runMenu.position} label={t('scheduled.menu')} onClose={() => setRunMenu(null)}>
        {(close) => <ScheduledMenuAction icon={<Trash2 size={15} />} danger label={t('scheduled.deleteRun')} onSelect={() => { close(); setConfirmDeleteRun(runMenu.run) }} />}
      </ContextMenu>}
      <ScheduledDialog open={dialog !== null} state={dialog} workspaces={workspaces} models={models} providers={providers} activeWorkspaceId={activeWorkspaceId} onClose={() => setDialog(null)} onSubmit={(input) => dialog?.mode === 'edit' ? edit(input) : create(input)} />
      <Dialog open={confirmDelete !== null} title={t('scheduled.delete')} onClose={() => setConfirmDelete(null)} footer={<><Button type="button" variant="secondary" onClick={() => setConfirmDelete(null)}>{t('common.cancel')}</Button><Button type="button" variant="danger" onClick={() => { void remove() }}>{t('common.delete')}</Button></>}>
        <p className="text-[13px] text-fg-muted">{t('scheduled.deleteConfirm', { name: confirmDelete?.name ?? '' })}</p>
      </Dialog>
      <Dialog open={confirmDeleteRun !== null} title={t('scheduled.deleteRun')} onClose={() => setConfirmDeleteRun(null)} footer={<><Button type="button" variant="secondary" onClick={() => setConfirmDeleteRun(null)}>{t('common.cancel')}</Button><Button type="button" variant="danger" onClick={() => { void removeRun() }}>{t('common.delete')}</Button></>}>
        <p className="text-[13px] text-fg-muted">{t('scheduled.deleteRunConfirm')}</p>
      </Dialog>
    </div>
  )
}

function RunStatus({ status }: { status: ScheduledRun['status'] }): ReactNode {
  const { t } = useI18n()
  const key = status === 'success' ? 'scheduled.success' : status === 'error' ? 'scheduled.error' : status === 'running' ? 'scheduled.running' : status === 'queued' ? 'scheduled.queued' : status === 'skipped' ? 'scheduled.skipped' : status === 'aborted' ? 'scheduled.aborted' : 'scheduled.status'
  return <span className={cn('inline-flex shrink-0 items-center gap-1 text-[11px]', status === 'running' || status === 'queued' ? 'text-accent' : status === 'error' ? 'text-danger' : 'text-fg-faint')} aria-live="polite">{(status === 'running' || status === 'queued') && <Spinner size="xs" />}{t(key)}</span>
}

function TaskDetail({ task, runs, workspaceName, onRun, onEdit, onDelete, onToggle }: { task: ScheduledTask | null; runs: ScheduledRun[]; workspaceName: (id: string) => string; onRun: (task: ScheduledTask) => void; onEdit: (task: ScheduledTask) => void; onDelete: (task: ScheduledTask) => void; onToggle: (task: ScheduledTask) => void }): ReactNode {
  const { t } = useI18n()
  if (task === null) return <EmptyState icon={<Clock3 size={28} />} title={t('scheduled.emptyTasks')} description={t('scheduled.emptyTasksHint')} />
  const taskRuns = runs.filter((run) => run.taskId === task.id)
  const lastRun = taskRuns[0]
  return <div className="mx-auto max-w-[720px]">
    <div className="flex items-start justify-between border-b border-border-subtle pb-4"><div><div className="flex items-center gap-2 text-[16px] font-medium text-fg">{task.name}<span className={cn('rounded-pill px-2 py-0.5 text-[11px]', task.enabled ? 'bg-accent/15 text-accent' : 'bg-tint text-fg-faint')}>{task.enabled ? t('scheduled.enabled') : t('scheduled.disabled')}</span></div><div className="mt-1 text-[12px] text-fg-faint">{workspaceName(task.workspaceId)} · {task.model}</div></div><div className="flex items-center gap-1"><Button type="button" size="sm" variant="secondary" onClick={() => onRun(task)}><Play size={13} />{t('scheduled.runNow')}</Button><Button type="button" size="sm" variant="secondary" onClick={() => onEdit(task)}><Pencil size={13} />{t('scheduled.edit')}</Button><Button type="button" size="sm" variant="secondary" onClick={() => onDelete(task)}><Trash2 size={13} />{t('scheduled.delete')}</Button><Switch checked={task.enabled} onCheckedChange={() => { onToggle(task) }} aria-label={t('scheduled.enabled')} /></div></div>
    <div className="mt-5 grid grid-cols-2 gap-3 rounded-card bg-tint p-4 text-[12px]"><Info label={t('scheduled.nextRun')} value={task.nextRunAt === null ? t('scheduled.never') : new Date(task.nextRunAt).toLocaleString()} /><Info label={t('scheduled.workspace')} value={workspaceName(task.workspaceId)} /><Info label={t('scheduled.model')} value={task.model} /><Info label={t('scheduled.status')} value={task.enabled ? t('scheduled.enabled') : t('scheduled.disabled')} /><Info label={t('scheduled.lastRun')} value={lastRun === undefined ? t('scheduled.never') : new Date(lastRun.scheduledAt).toLocaleString()} /><Info label={t('scheduled.runCount')} value={String(taskRuns.length)} /></div>
    <div className="mt-4 rounded-card bg-tint p-4"><div className="text-[13px] font-medium text-fg">{t('scheduled.prompt')}</div><p className="mt-2 whitespace-pre-wrap text-[13px] leading-relaxed text-fg-muted">{task.prompt}</p></div>
  </div>
}

function RunDetail({ run, task, workspace, workspaceName, fallbackModel, onRun, onEdit }: { run: ScheduledRun | null; task: ScheduledTask | null; workspace: Workspace | null; workspaceName: (id: string) => string; fallbackModel: FallbackModel; onRun: (task: ScheduledTask) => void; onEdit: (task: ScheduledTask) => void }): ReactNode {
  const { t } = useI18n()
  if (run === null) return <EmptyState icon={<Clock3 size={28} />} title={t('scheduled.emptyRuns')} description={t('scheduled.emptyRunsHint')} />
  const errorText = run.error === undefined ? null : ['scheduled.missed', 'scheduled.workspaceUnavailable', 'scheduled.executionFailed'].includes(run.error) ? t(run.error as 'scheduled.missed' | 'scheduled.workspaceUnavailable' | 'scheduled.executionFailed') : run.error
  if (workspace !== null && run.sessionId !== '') {
    const taskModel: FallbackModel = task === null
      ? fallbackModel
      : { model: task.model, modelProviderId: task.modelProviderId }
    // A scheduled conversation must continue with the model frozen on the
    // task. ChatView's composer normally initializes from workspace defaults;
    // passing this view-local snapshot prevents the global default model from
    // appearing in the composer or being used for the next message.
    const chatWorkspace = task === null ? workspace : {
      ...workspace,
      settings: {
        ...workspace.settings,
        defaultModel: task.model,
        defaultModelProviderId: task.modelProviderId
      }
    }
    return <div className="flex min-h-0 h-full flex-col">
      <div className="flex shrink-0 items-center justify-between border-b border-hairline px-6 py-3">
        <div className="min-w-0"><div className="flex items-center gap-2 text-[16px] font-medium text-fg"><span className="truncate">{task?.name ?? run.taskId}</span><RunStatus status={run.status} /></div><div className="mt-1 text-[12px] text-fg-faint">{workspaceName(workspace.id)} · {task?.model ?? t('scheduled.noModel')}</div></div>
        <div className="flex shrink-0 items-center gap-2"><Button type="button" size="sm" variant="secondary" onClick={() => { if (task !== null) onRun(task) }}><Play size={13} />{t('scheduled.runNow')}</Button>{task !== null && <Button type="button" size="sm" variant="secondary" onClick={() => onEdit(task)}><Pencil size={13} />{t('scheduled.edit')}</Button>}</div>
      </div>
      <div className="flex min-h-0 flex-1 flex-col"><ChatView key={run.sessionId} sessionId={run.sessionId} tabId={`scheduled-run:${run.id}`} workspace={chatWorkspace} fallbackModel={taskModel} runningOverride={run.status === 'queued' || run.status === 'running'} /></div>
    </div>
  }
  return <div className="mx-auto max-w-[720px]"><div className="flex items-center gap-2 border-b border-hairline pb-4 text-[16px] font-medium text-fg">{task?.name ?? run.taskId}<RunStatus status={run.status} /></div><div className="mt-5 grid grid-cols-2 gap-3 rounded-card bg-tint p-4 text-[12px]"><Info label={t('scheduled.status')} value={t(run.status === 'success' ? 'scheduled.success' : run.status === 'error' ? 'scheduled.error' : run.status === 'running' ? 'scheduled.running' : 'scheduled.skipped')} /><Info label={t('scheduled.nextRun')} value={new Date(run.scheduledAt).toLocaleString()} /><Info label={t('scheduled.workspace')} value={task === null ? t('scheduled.noWorkspace') : workspaceName(task.workspaceId)} /><Info label={t('scheduled.model')} value={task?.model ?? t('scheduled.noModel')} /></div>{errorText !== null && <div className="mt-4 rounded-card bg-danger/10 p-4 text-[13px] text-danger">{errorText}</div>}<div className="mt-4 rounded-card bg-tint p-4 text-[13px] text-fg-muted">{t('scheduled.sessionUnavailable')}</div></div>
}

function Info({ label, value }: { label: string; value: string }): ReactNode { return <div><div className="text-fg-faint">{label}</div><div className="mt-1 text-fg">{value}</div></div> }

function ScheduledDialog({ open, state, workspaces, models, providers, activeWorkspaceId, onClose, onSubmit }: { open: boolean; state: DialogState; workspaces: Workspace[]; models: ReturnType<typeof useModelsStore.getState>['models']; providers: ReturnType<typeof useModelsStore.getState>['providers']; activeWorkspaceId: string | null; onClose: () => void; onSubmit: (input: ScheduledTaskInput) => void }): ReactNode {
  const { t } = useI18n()
  const [name, setName] = useState('')
  const [prompt, setPrompt] = useState('')
  const [workspaceId, setWorkspaceId] = useState('')
  const [model, setModel] = useState('')
  const [modelProviderId, setModelProviderId] = useState<string | undefined>(undefined)
  const [ruleKind, setRuleKind] = useState<ScheduleRule['kind']>('daily')
  const [date, setDate] = useState('')
  const [time, setTime] = useState('09:00')
  const [weekdays, setWeekdays] = useState<number[]>([1, 2, 3, 4, 5])
  const [repeatWindow, setRepeatWindow] = useState(false)
  const [endTime, setEndTime] = useState('18:00')
  const [intervalMinutes, setIntervalMinutes] = useState('60')
  const [enabled, setEnabled] = useState(true)

  useEffect(() => {
    if (!open) return
    const existing = state?.task
    setName(existing?.name ?? '')
    setPrompt(existing?.prompt ?? '')
    const defaultWorkspace = workspaces.find((workspace) => workspace.id === (existing?.workspaceId ?? activeWorkspaceId))
    setWorkspaceId(existing?.workspaceId ?? activeWorkspaceId ?? workspaces[0]?.id ?? '')
    setModel(existing?.model ?? defaultWorkspace?.settings.defaultModel ?? models[0]?.alias ?? '')
    setModelProviderId(existing?.modelProviderId ?? defaultWorkspace?.settings.defaultModelProviderId ?? models.find((item) => item.alias === (existing?.model ?? defaultWorkspace?.settings.defaultModel ?? models[0]?.alias))?.providerId)
    setRuleKind(existing?.schedule.kind ?? DEFAULT_RULE.kind)
    setDate(existing?.schedule.kind === 'once' ? existing.schedule.at.slice(0, 10) : new Date().toISOString().slice(0, 10))
    setTime(existing?.schedule.kind === 'once' ? existing.schedule.at.slice(11, 16) : existing?.schedule.time ?? '09:00')
    setWeekdays(existing?.schedule.kind === 'weekly' ? existing.schedule.weekdays : [1, 2, 3, 4, 5])
    setRepeatWindow(existing?.repeatWindow.enabled ?? false)
    setEndTime(existing?.repeatWindow.endTime ?? '18:00')
    setIntervalMinutes(String(existing?.repeatWindow.intervalMinutes ?? 60))
    setEnabled(existing?.enabled ?? true)
  }, [open, state, activeWorkspaceId, workspaces, models])

  const submit = (): void => {
    if (name.trim() === '' || prompt.trim() === '' || workspaceId === '') return
    const schedule: ScheduleRule = ruleKind === 'once' ? { kind: 'once', at: `${date}T${time}` } : ruleKind === 'weekly' ? { kind: 'weekly', weekdays, time } : { kind: 'daily', time }
    onSubmit({ name, prompt, workspaceId, model, modelProviderId, schedule, repeatWindow: { enabled: repeatWindow, endTime, intervalMinutes: Number(intervalMinutes) || 60 }, enabled })
  }

  const workspaceOptions: SelectOption[] = workspaces.map((workspace) => ({ value: workspace.id, label: workspace.name }))
  const modelOptions: SelectOption[] = models.filter((item) => item.enabled !== false).map((item) => ({ value: modelSelectionKey(item.providerId, item.alias), label: `${providers.find((provider) => provider.id === item.providerId)?.name ?? item.providerId} / ${item.displayName ?? item.alias}` }))
  const modelValue = modelSelectionKey(modelProviderId, model)
  const days = [0, 1, 2, 3, 4, 5, 6]
  return <Dialog open={open} title={state?.mode === 'edit' ? t('scheduled.editTitle') : t('scheduled.createTitle')} description={t('scheduled.description')} onClose={onClose} width={640} footer={<><Button type="button" variant="secondary" onClick={onClose}>{t('common.cancel')}</Button><Button type="button" variant="primary" onClick={submit} disabled={name.trim() === '' || prompt.trim() === '' || workspaceId === ''}>{t('scheduled.save')}</Button></>}>
    <div className="space-y-3"><Field label={t('scheduled.name')}><TextInput value={name} onChange={setName} ariaLabel={t('scheduled.name')} placeholder={t('scheduled.namePlaceholder')} /></Field><Field label={t('scheduled.prompt')}><TextArea value={prompt} onCommit={setPrompt} rows={5} ariaLabel={t('scheduled.prompt')} placeholder={t('scheduled.promptPlaceholder')} /></Field><Field label={t('scheduled.workspace')}><Select value={workspaceId} options={workspaceOptions} onValueChange={setWorkspaceId} ariaLabel={t('scheduled.workspace')} inModal /></Field><Field label={t('scheduled.model')}><Select value={modelValue} options={modelOptions} onValueChange={(value) => { const selected = parseModelSelectionKey(value); setModel(selected.alias); setModelProviderId(selected.modelProviderId) }} ariaLabel={t('scheduled.model')} inModal /></Field><div className="flex items-end gap-3"><Field label={t('scheduled.repeat')} className="min-w-0 flex-1"><SegmentedControl value={ruleKind} onValueChange={(v) => setRuleKind(v as ScheduleRule['kind'])} options={[{ value: 'once', label: t('scheduled.once') }, { value: 'daily', label: t('scheduled.daily') }, { value: 'weekly', label: t('scheduled.weekly') }]} className="w-full [&>button]:flex-1" /></Field><Field label={t('scheduled.time')} className="w-[132px]"><TextInput value={time} onChange={setTime} ariaLabel={t('scheduled.time')} inputMode="numeric" /></Field></div>{ruleKind === 'once' && <Field label={t('scheduled.date')}><TextInput value={date} onChange={setDate} ariaLabel={t('scheduled.date')} placeholder={t('scheduled.datePlaceholder')} /></Field>}{ruleKind === 'weekly' && <Field label={t('scheduled.weekdays')}><div className="flex gap-1">{days.map((day) => <button key={day} type="button" onClick={() => setWeekdays((current) => current.includes(day) ? current.filter((item) => item !== day) : [...current, day].sort())} className={cn('flex-1 rounded-[8px] px-1 py-2 text-[12px]', weekdays.includes(day) ? 'bg-accent text-accent-fg' : 'bg-tint text-fg-muted')}>{t(`scheduled.day.${day}` as Parameters<typeof t>[0])}</button>)}</div></Field>}<div className="flex items-center justify-between rounded-[8px] bg-tint px-3 py-2"><div><div className="text-[13px] text-fg">{t('scheduled.repeatWindow')}</div><div className="mt-0.5 text-[11px] text-fg-faint">{t('scheduled.repeatWindowHint')}</div></div><Switch checked={repeatWindow} onCheckedChange={setRepeatWindow} aria-label={t('scheduled.repeatWindow')} /></div>{repeatWindow && <div className="flex gap-3"><Field label={t('scheduled.endTime')} className="flex-1"><TextInput value={endTime} onChange={setEndTime} ariaLabel={t('scheduled.endTime')} inputMode="numeric" /></Field><Field label={t('scheduled.interval')} className="flex-1"><TextInput value={intervalMinutes} onChange={setIntervalMinutes} ariaLabel={t('scheduled.interval')} inputMode="numeric" /></Field></div>}<div className="flex items-center justify-between rounded-[8px] bg-danger/10 px-3 py-2"><div className="flex items-start gap-2 text-[12px] leading-relaxed text-danger"><AlertTriangle size={15} className="mt-0.5 shrink-0" />{t('scheduled.riskHint')}</div><Switch checked={enabled} onCheckedChange={setEnabled} aria-label={t('scheduled.enabled')} /></div></div>
  </Dialog>
}

function Field({ label, children, className }: { label: string; children: ReactNode; className?: string }): ReactNode { return <label className={cn('block', className)}><div className="mb-1 text-[12px] text-fg-muted">{label}</div>{children}</label> }

function ScheduledMenuAction({ icon, label, danger = false, onSelect }: { icon: ReactNode; label: string; danger?: boolean; onSelect: () => void }): ReactNode {
  return <button type="button" role="menuitem" onClick={onSelect} className={cn('flex w-full items-center gap-2.5 rounded-[8px] px-2.5 py-[7px] text-left text-[13px] transition-colors hover:bg-tint-strong', danger ? 'text-danger' : 'text-fg')}><span className={danger ? 'text-danger' : 'text-accent-soft'}>{icon}</span><span className="truncate">{label}</span></button>
}
