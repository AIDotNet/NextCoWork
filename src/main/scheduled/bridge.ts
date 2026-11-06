/**
 * 定时任务的**写入收口** + Agent 侧的那道窄缝。
 *
 * ## 为什么写入要收口
 *
 * 每一次写都必须做完整的三件事:落库 → `refreshScheduler()` → 广播
 * `scheduled:changed`。少做任何一件的症状都不像 bug:少了重排是「建了但不跑」
 * (要等到下一次进程重启或下一条任务到点才被顺带修好),少了广播是
 * 「建了但界面上没有」。IPC 那边原来把这三行抄了四遍;现在多了一个调用方
 * (Agent 工具),再抄一遍就是第五遍 —— 而漏抄的那一遍谁也发现不了。
 *
 * ## 为什么 Agent 走桥而不是直接 import store
 *
 * 工具住在内核里(`kernel/tool/builtin/scheduled.ts`),内核对 electron 与
 * `state/store` 零依赖 —— 那是它能在无头 Node 里被完整单测的前提。所以工具
 * 拿到的是 `SchedulingBridge` 这个窄接口(同 `SpawnSubagentFn` 的道理),
 * 实现留在这里。
 *
 * ★ **模型给的入参在这一层还要再核一遍。** 工具那侧的 zod 管的是「形状对不对」,
 * 这里管的是「这件事在当前这台机器上成不成立」:工作区还在不在、任务是不是
 * 这个工作区的、模型别名查不查得到、链深度到没到顶。两侧都有,缺一侧就有一条
 * 绕过去的路。
 */
import {
  SCHEDULED_AGENT_LIMITS,
  nextScheduledOccurrence,
  summarizeScheduledTask,
  systemTimeZone,
  type ScheduledTask,
  type ScheduledTaskInput,
  type SchedulingBridge,
  type SchedulingCreateInput,
  type SchedulingUpdateInput,
  type ScheduledTaskSummary
} from '../../shared/domain/scheduled'
import { store } from '../state/store'
import { refreshScheduledTasks } from './refresh'

/**
 * 任务表变化后的广播口。**由 `ipc/index.ts` 在 `registerIpc()` 里装上**
 * (`windows.emitToAll('scheduled:changed', …)`)—— 同 `scheduled/refresh.ts`
 * 那个重排插槽、以及 `runtime.ts` 里那排 `setXxxChangeListener` 一个套路:
 * `window/registry` 拖着 electron,而 `bridge` 与 `refresh` 都是无头 Node 里
 * 要能被直接 import 的叶子。没装(纯内核测试)时是一次 no-op。
 */
let scheduledChange: ((change: { kind: 'task'; taskId: string }) => void) | null = null

/** 装上真正的广播实现。 */
export function setScheduledChangeListener(listener?: (change: { kind: 'task'; taskId: string }) => void): void {
  scheduledChange = listener ?? null
}

function announce(taskId: string): void {
  refreshScheduledTasks()
  /*
    ★ 广播抛错不能让这次写入变成失败:任务**已经落库**(上面那两行之后才轮到这里),
    把它报成失败会让模型以为没建成,于是再建一遍。异常只留日志,界面靠下一次
    `scheduled:changed`(重排后的 tick、或用户下一次操作)自然对齐。
  */
  try {
    scheduledChange?.({ kind: 'task', taskId })
  } catch (error) {
    console.warn('[scheduled] 任务变化广播失败,已落库但不通知界面', error)
  }
}

/**
 * 定时任务的全部写入路径。**IPC 与 Agent 工具共用这一份**,
 * 于是「写完要重排 + 广播」这件事只有一处需要记得。
 */
export const scheduledWrites = {
  create(input: ScheduledTaskInput): ScheduledTask {
    if (store.getWorkspace(input.workspaceId) === undefined) throw new Error('工作区不存在')
    const task = store.createScheduledTask(input)
    announce(task.id)
    return task
  },
  update(id: string, patch: Partial<ScheduledTaskInput>): ScheduledTask {
    if (patch.workspaceId !== undefined && store.getWorkspace(patch.workspaceId) === undefined) {
      throw new Error('工作区不存在')
    }
    const task = store.updateScheduledTask(id, patch)
    announce(task.id)
    return task
  },
  remove(id: string): void {
    store.deleteScheduledTask(id)
    announce(id)
  },
  setEnabled(id: string, enabled: boolean): ScheduledTask {
    const task = store.setScheduledTaskEnabled(id, enabled)
    announce(task.id)
    return task
  }
}

// ─────────────────────────── Agent 侧 ───────────────────────────

export interface SchedulingBridgeInput {
  workspaceId: string
  /** 当前会话。定时任务跑出来的会话由它反查父任务,算链深度。 */
  sessionId: string
  /** 当前 run 的模型 —— 新任务默认继承它,而不是让模型自己编一个别名。 */
  model: string
  modelProviderId?: string
  /** 别名查得到吗。由 `runtime.ts` 递进来,避免内核/桥反向依赖路由器。 */
  resolveModel: (model: string, modelProviderId?: string) => boolean
}

/**
 * 当前会话所处的链深度。
 *
 * 用户手建的任务 = 0;它运行时 Agent 建出来的 = 1;再下一层 = 2……
 * 普通聊天会话查不到对应的 run,于是从 0 起算。
 */
function chainDepthOf(sessionId: string): number {
  const run = store.getScheduledRunBySession(sessionId)
  if (run === undefined) return 0
  const parent = store.getScheduledTask(run.taskId)
  return parent?.chainDepth ?? 0
}

/** 这条任务是不是**这个工作区**的。跨工作区改删一律当成「不存在」。 */
function ownedTask(id: string, workspaceId: string): ScheduledTask {
  const task = store.getScheduledTask(id)
  if (task === undefined || task.workspaceId !== workspaceId) {
    throw new Error(
      `No scheduled task with id "${id}" exists in this workspace. Call ListScheduledTasks to get the current ids.`
    )
  }
  return task
}

/**
 * 时间相关的入参校验。
 *
 * ★ **算一次 `nextScheduledOccurrence`,拿不到就拒。** 这一条同时挡掉了
 * 「once 的时间在过去」「时间串格式对但语义不成立」「时区名拼错」三种情况 ——
 * 它们的共同症状都是一条永远不会触发的任务,而那种任务在界面上看起来完全正常。
 */
function validateTiming(input: SchedulingCreateInput | (SchedulingUpdateInput & { schedule: ScheduledTask['schedule'] }), timezone: string): void {
  const repeat = input.repeatWindow
  if (repeat?.enabled === true) {
    if (repeat.endTime === undefined || !/^\d{2}:\d{2}$/.test(repeat.endTime)) {
      throw new Error('repeat_window.end_time is required (HH:MM) when the repeat window is enabled.')
    }
    const interval = repeat.intervalMinutes ?? 60
    if (interval < SCHEDULED_AGENT_LIMITS.minIntervalMinutes) {
      throw new Error(
        `repeat_window.interval_minutes must be at least ${String(SCHEDULED_AGENT_LIMITS.minIntervalMinutes)} — ` +
          `a shorter interval starts a full agent run that often, which burns the user's tokens.`
      )
    }
  }
  const next = nextScheduledOccurrence(input.schedule, timezone, repeat ?? { enabled: false })
  if (next === null) {
    throw new Error(
      'This schedule never fires: a one-off time must be in the future, times must be "HH:MM", ' +
        'dates "YYYY-MM-DDTHH:MM", and the timezone must be a valid IANA name such as "Asia/Shanghai".'
    )
  }
}

function validateText(name: string | undefined, prompt: string | undefined): void {
  if (name !== undefined && (name.trim() === '' || name.length > SCHEDULED_AGENT_LIMITS.maxNameLength)) {
    throw new Error(`name must be 1-${String(SCHEDULED_AGENT_LIMITS.maxNameLength)} characters.`)
  }
  if (prompt !== undefined && (prompt.trim() === '' || prompt.length > SCHEDULED_AGENT_LIMITS.maxPromptLength)) {
    throw new Error(`prompt must be 1-${String(SCHEDULED_AGENT_LIMITS.maxPromptLength)} characters.`)
  }
}

export function schedulingBridgeFor(input: SchedulingBridgeInput): SchedulingBridge {
  const { workspaceId, sessionId } = input

  /** 新任务用哪个模型:模型显式指定的(必须查得到)→ 当前 run 的。 */
  const modelFor = (requested?: string, requestedProvider?: string): Pick<ScheduledTaskInput, 'model' | 'modelProviderId'> => {
    if (requested === undefined || requested.trim() === '') {
      return { model: input.model, ...(input.modelProviderId === undefined ? {} : { modelProviderId: input.modelProviderId }) }
    }
    if (!input.resolveModel(requested, requestedProvider)) {
      throw new Error(
        `No model alias "${requested}" is configured. Omit the model field to reuse this session's model (${input.model}).`
      )
    }
    return { model: requested, ...(requestedProvider === undefined ? {} : { modelProviderId: requestedProvider }) }
  }

  return {
    list(): Promise<ScheduledTaskSummary[]> {
      return Promise.resolve(store.listScheduledTasks(workspaceId).map(summarizeScheduledTask))
    },

    create(task: SchedulingCreateInput): Promise<ScheduledTaskSummary> {
      if (store.getWorkspace(workspaceId) === undefined) {
        return Promise.reject(new Error('This workspace no longer exists, so scheduled tasks cannot be created in it.'))
      }
      const existing = store.listScheduledTasks(workspaceId)
      if (existing.length >= SCHEDULED_AGENT_LIMITS.maxTasksPerWorkspace) {
        return Promise.reject(new Error(
          `This workspace already has ${String(existing.length)} scheduled tasks, which is the limit ` +
            `(${String(SCHEDULED_AGENT_LIMITS.maxTasksPerWorkspace)}). Delete one before creating another.`
        ))
      }
      /*
        ★ 链深度。一个每天跑的任务在运行中再建一个任务是合理的(「跑完安排一次复查」),
        但它不能无限延续下去 —— 每一层都会在下一次运行时再生出一层。
      */
      const chainDepth = chainDepthOf(sessionId) + 1
      if (chainDepth > SCHEDULED_AGENT_LIMITS.maxChainDepth) {
        return Promise.reject(new Error(
          `This run is itself a scheduled task at chain depth ${String(chainDepth - 1)}, which is the limit ` +
            `(${String(SCHEDULED_AGENT_LIMITS.maxChainDepth)}). Do not schedule further follow-ups from here — ` +
            `report what you found and let the user schedule anything else.`
        ))
      }
      try {
        validateText(task.name, task.prompt)
        const timezone = task.timezone?.trim() ?? ''
        validateTiming(task, timezone === '' ? systemTimeZone() : timezone)
        const created = scheduledWrites.create({
          name: task.name,
          prompt: task.prompt,
          workspaceId,
          ...modelFor(task.model, task.modelProviderId),
          schedule: task.schedule,
          ...(timezone === '' ? {} : { timezone }),
          ...(task.repeatWindow === undefined ? {} : { repeatWindow: task.repeatWindow }),
          ...(task.enabled === undefined ? {} : { enabled: task.enabled }),
          createdBy: 'agent',
          chainDepth
        })
        return Promise.resolve(summarizeScheduledTask(created))
      } catch (error) {
        return Promise.reject(error instanceof Error ? error : new Error(String(error)))
      }
    },

    update(id: string, patch: SchedulingUpdateInput): Promise<ScheduledTaskSummary> {
      try {
        const current = ownedTask(id, workspaceId)
        validateText(patch.name, patch.prompt)
        const timezone = patch.timezone?.trim() ?? ''
        validateTiming(
          {
            ...patch,
            schedule: patch.schedule ?? current.schedule,
            repeatWindow: patch.repeatWindow ?? current.repeatWindow
          },
          timezone === '' ? current.timezone : timezone
        )
        const model = patch.model === undefined ? {} : modelFor(patch.model, patch.modelProviderId)
        const updated = scheduledWrites.update(id, {
          ...(patch.name === undefined ? {} : { name: patch.name }),
          ...(patch.prompt === undefined ? {} : { prompt: patch.prompt }),
          ...(patch.schedule === undefined ? {} : { schedule: patch.schedule }),
          ...(timezone === '' ? {} : { timezone }),
          ...(patch.repeatWindow === undefined ? {} : { repeatWindow: patch.repeatWindow }),
          ...(patch.enabled === undefined ? {} : { enabled: patch.enabled }),
          ...model
        })
        return Promise.resolve(summarizeScheduledTask(updated))
      } catch (error) {
        return Promise.reject(error instanceof Error ? error : new Error(String(error)))
      }
    },

    remove(id: string): Promise<{ id: string; name: string }> {
      try {
        const task = ownedTask(id, workspaceId)
        scheduledWrites.remove(id)
        return Promise.resolve({ id: task.id, name: task.name })
      } catch (error) {
        return Promise.reject(error instanceof Error ? error : new Error(String(error)))
      }
    }
  }
}
