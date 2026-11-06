import { Notification } from 'electron'
import type { ContentPart } from '../../shared/agent/message'
import type { RunRequest } from '../../shared/agent/run-request'
import { nextScheduledOccurrence, SCHEDULED_PERMISSION_MODE, type ScheduledRun, type ScheduledTask } from '../../shared/domain/scheduled'
import { ulid } from '../../shared/util/id'
import { getRouter, runAgent } from '../runtime'
import { normalizeModelThinkingLevel } from '../../shared/domain/model-runtime'
import { runs } from '../kernel/run-registry'
import { store } from '../state/store'
import { windows } from '../window/registry'
import { toAgentError } from '../ipc/errors'

let timer: NodeJS.Timeout | null = null
let stopped = true
const runningTasks = new Set<string>()
const MAX_TIMER_DELAY_MS = 2_147_483_647

function emit(kind: 'task' | 'run', taskId?: string, runId?: string, status?: ScheduledRun['status']): void {
  windows.emitToAll('scheduled:changed', { kind, ...(taskId === undefined ? {} : { taskId }), ...(runId === undefined ? {} : { runId }), ...(status === undefined ? {} : { status }) })
}

function reschedule(): void {
  if (timer !== null) clearTimeout(timer)
  timer = null
  if (stopped) return
  const next = store.listScheduledTasks().filter((task) => task.enabled && task.nextRunAt !== null).sort((a, b) => (a.nextRunAt ?? Infinity) - (b.nextRunAt ?? Infinity))[0]
  if (next === undefined || next.nextRunAt === null) return
  // Node 对超过有符号 32 位的延迟会改成 1ms；分段唤醒后仍由 tick 判断是否到期。
  const delay = Math.min(MAX_TIMER_DELAY_MS, Math.max(0, next.nextRunAt - Date.now()))
  timer = setTimeout(() => { void tick(false) }, delay)
}

async function tick(reconcileOnly: boolean): Promise<void> {
  if (stopped) return
  const now = Date.now()
  for (const task of store.listScheduledTasks()) {
    if (!task.enabled || task.nextRunAt === null || task.nextRunAt > now) continue
    const scheduledAt = task.nextRunAt
    const nextRunAt = nextScheduledOccurrence(task.schedule, task.timezone, task.repeatWindow, scheduledAt)
    store.putScheduledTask({ ...task, nextRunAt, updatedAt: now })
    emit('task', task.id)
    if (reconcileOnly) {
      const skipped = store.putScheduledRun({
        id: ulid(now), taskId: task.id, sessionId: '', trigger: 'scheduled', status: 'skipped', scheduledAt, endedAt: now,
        error: 'scheduled.missed'
      })
      emit('run', task.id, skipped.id, skipped.status)
      continue
    }
    if (runningTasks.has(task.id)) continue
    enqueue(task, scheduledAt, 'scheduled')
  }
  reschedule()
}

function summaryFor(sessionId: string): string {
  const messages = store.getHistory(sessionId)
  const text = messages.filter((message) => message.role === 'assistant').flatMap((message) => message.parts.filter((part): part is Extract<ContentPart, { type: 'text' }> => part.type === 'text').map((part) => part.text)).join('\n').trim()
  return text.length > 1000 ? `${text.slice(0, 1000)}…` : text
}

function closeOneShotAfterRun(task: ScheduledTask, trigger: 'scheduled' | 'manual', at: number): void {
  if (trigger !== 'scheduled' || task.schedule.kind !== 'once') return
  const latest = store.getScheduledTask(task.id)
  if (latest !== undefined) store.putScheduledTask({ ...latest, enabled: false, nextRunAt: null, updatedAt: at })
  emit('task', task.id)
}

async function execute(task: ScheduledTask, scheduledAt: number, trigger: 'scheduled' | 'manual', queuedRun?: ScheduledRun): Promise<ScheduledRun> {
  const now = Date.now()
  const runId = ulid(now)
  const sessionId = queuedRun?.sessionId ?? ulid(now + 1)
  const run = queuedRun ?? store.putScheduledRun({ id: ulid(now + 2), taskId: task.id, sessionId, trigger, status: 'queued', scheduledAt })
  if (queuedRun === undefined) {
    runningTasks.add(task.id)
    emit('run', task.id, run.id, run.status)
  }
  try {
    const workspace = store.getWorkspace(task.workspaceId)
    if (workspace === undefined) throw new Error('scheduled.workspaceUnavailable')
    const model = getRouter().resolveModel(task.model, task.modelProviderId)
    const thinking = normalizeModelThinkingLevel(workspace.settings.defaultThinking, model)
    const session = store.createSession({ id: sessionId, workspaceId: task.workspaceId, title: task.name, origin: 'scheduled', model: task.model, modelProviderId: task.modelProviderId, mode: workspace.settings.defaultMode, thinking, rootPathAtCreation: workspace.rootPath })
    void session
    const startedAt = Date.now()
    const runningRun = store.putScheduledRun({ ...run, status: 'running', startedAt })
    emit('run', task.id, run.id, runningRun.status)
    const request: RunRequest = {
      runId, sessionId, workspaceId: task.workspaceId, depth: 0,
      input: [{ type: 'text', text: task.prompt }], mode: workspace.settings.defaultMode,
      thinking, webSearch: workspace.settings.webSearch,
      // 定时任务没有输入框,唯一档位来源就是工作区默认值 —— 漏了这行,用户开了
      // 「最大上下文」但半夜的任务仍按 272K 压缩,且无处可查。
      maxContext: workspace.settings.maxContext === true,
      permissionMode: SCHEDULED_PERMISSION_MODE, model: task.model, modelProviderId: task.modelProviderId,
      skillIds: workspace.settings.activeSkillIds, skillSelectionMode: workspace.settings.skillSelectionMode
    }
    const handle = runs.create(request)
    const release = runs.retainSessionForRun(handle)
    try { await runAgent(handle, request) } catch (error) { handle.finish('error', toAgentError(error)) }
    finally { release() }
    // `runAgent` is expected to resolve only after AgentSession has finished.
    // Keep the scheduler from leaving a durable row stuck in `running` if a
    // future driver violates that contract.
    if (handle.status === 'running') handle.finish('error', toAgentError(new Error('scheduled.executionIncomplete')))
    const status = handle.status === 'done' ? 'success' : handle.status === 'aborted' ? 'aborted' : 'error'
    const finishedAt = Date.now()
    const result = store.putScheduledRun({ ...run, status, startedAt, endedAt: finishedAt, summary: status === 'success' ? summaryFor(sessionId) : undefined, ...(status === 'error' ? { error: 'scheduled.executionFailed' } : {}) })
    closeOneShotAfterRun(task, trigger, finishedAt)
    notify(task, result)
    emit('run', task.id, run.id, result.status)
    return result
  } catch (error) {
    const endedAt = Date.now()
    const result = store.putScheduledRun({ ...run, status: 'error', endedAt, error: error instanceof Error ? error.message : String(error) })
    closeOneShotAfterRun(task, trigger, endedAt)
    notify(task, result)
    emit('run', task.id, run.id, result.status)
    return result
  } finally {
    runningTasks.delete(task.id)
    reschedule()
  }
}

/** Create the durable queued row before doing any asynchronous work. */
function enqueue(task: ScheduledTask, scheduledAt: number, trigger: 'scheduled' | 'manual'): ScheduledRun {
  const now = Date.now()
  const sessionId = ulid(now + 1)
  const run = store.putScheduledRun({
    id: ulid(now + 2),
    taskId: task.id,
    sessionId,
    trigger,
    status: 'queued',
    scheduledAt
  })
  runningTasks.add(task.id)
  emit('run', task.id, run.id, run.status)
  void executeQueued(task, run)
  return run
}

async function executeQueued(task: ScheduledTask, run: ScheduledRun): Promise<ScheduledRun> {
  try {
    // execute() owns the actual session/agent lifecycle. The queued record is
    // passed through so runNow can return it immediately to the renderer.
    return await execute(task, run.scheduledAt, run.trigger, run)
  } catch {
    // execute() handles its own errors; this guard prevents a detached promise
    // from becoming an unhandled rejection if a future change violates that.
    return store.getScheduledRun(run.id) ?? run
  }
}

function notify(task: ScheduledTask, run: ScheduledRun): void {
  try {
    if (!Notification.isSupported()) return
    const locale = store.getSettings().locale
    const body = run.status === 'success'
      ? (locale === 'en-US' ? 'Task completed' : '任务已完成')
      : (locale === 'en-US' ? 'Task failed' : '任务执行失败')
    const notification = new Notification({ title: task.name, body })
    notification.on('click', () => { windows.showMainWindow(); windows.emitToAll('scheduled:focusRun', { runId: run.id }) })
    notification.show()
  } catch { /* Notifications are optional on headless Linux and in tests. */ }
}

export function startScheduler(): void {
  stopped = false
  void tick(true)
  reschedule()
}

export function stopScheduler(): void {
  stopped = true
  if (timer !== null) clearTimeout(timer)
  timer = null
}

/** Re-arm the single scheduler timer after a task is created or edited. */
export function refreshScheduler(): void {
  reschedule()
}

export function reconcileScheduler(): void { void tick(true) }

export function runScheduledTaskNow(taskId: string): Promise<ScheduledRun> {
  const task = store.getScheduledTask(taskId)
  if (task === undefined) return Promise.reject(new Error('定时任务不存在'))
  if (runningTasks.has(task.id)) return Promise.reject(new Error('任务正在执行'))
  return Promise.resolve(enqueue(task, Date.now(), 'manual'))
}
