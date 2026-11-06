import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { ScheduledRun, ScheduledTask, ScheduledTaskInput } from '../../../shared/domain/scheduled'
import { normalizeScheduledTaskInput, SCHEDULED_AGENT_LIMITS } from '../../../shared/domain/scheduled'

/**
 * `schedulingBridgeFor` 的测试 —— 工具那侧只管形状,这一侧管「这件事在这台机器上
 * 成不成立」。四条最重要的:
 *
 * 1. 任务一律落在 **ctx 的工作区**,模型说了不算。
 * 2. 跨工作区的改 / 删当成「不存在」,而不是照改。
 * 3. 链深度到顶就拒 —— 定时任务里再建定时任务是允许的,但不能无限延伸。
 * 4. 每一次写都重排调度器并广播,否则「建了但不跑」/「建了但界面上没有」。
 */

const state = vi.hoisted(() => ({
  tasks: new Map<string, ScheduledTask>(),
  runs: new Map<string, ScheduledRun>(),
  workspaces: new Set<string>(['ws_1']),
  refreshed: 0,
  emitted: [] as unknown[],
  nextId: 0
}))

vi.mock('../../state/store', () => ({
  store: {
    getWorkspace: (id: string) => (state.workspaces.has(id) ? { id } : undefined),
    listScheduledTasks: (workspaceId?: string) =>
      [...state.tasks.values()].filter((task) => workspaceId === undefined || task.workspaceId === workspaceId),
    getScheduledTask: (id: string) => state.tasks.get(id),
    getScheduledRunBySession: (sessionId: string) =>
      [...state.runs.values()].find((run) => run.sessionId === sessionId),
    createScheduledTask: (input: ScheduledTaskInput) => {
      state.nextId += 1
      const task: ScheduledTask = { id: `task_${String(state.nextId)}`, ...normalizeScheduledTaskInput(input) }
      state.tasks.set(task.id, task)
      return task
    },
    updateScheduledTask: (id: string, patch: Partial<ScheduledTaskInput>) => {
      const current = state.tasks.get(id)
      if (current === undefined) throw new Error('定时任务不存在')
      const next: ScheduledTask = { ...current, ...patch, id } as ScheduledTask
      state.tasks.set(id, next)
      return next
    },
    deleteScheduledTask: (id: string) => {
      state.tasks.delete(id)
    }
  }
}))

import { schedulingBridgeFor, setScheduledChangeListener } from '../bridge'
import { resetSchedulerRefreshForTest, setSchedulerRefresh } from '../refresh'

function bridge(overrides: Partial<Parameters<typeof schedulingBridgeFor>[0]> = {}) {
  return schedulingBridgeFor({
    workspaceId: 'ws_1',
    sessionId: 'session_chat',
    model: 'gpt-5.6-sol',
    resolveModel: (model) => model === 'gpt-5.6-sol' || model === 'claude-4.6-opus',
    ...overrides
  })
}

const DAILY = { name: '每日构建', prompt: '跑一遍构建', schedule: { kind: 'daily' as const, time: '09:00' } }

function seedTask(overrides: Partial<ScheduledTask> = {}): ScheduledTask {
  state.nextId += 1
  const task: ScheduledTask = {
    id: `task_${String(state.nextId)}`,
    name: '已有任务',
    prompt: 'x',
    workspaceId: 'ws_1',
    model: 'gpt-5.6-sol',
    schedule: { kind: 'daily', time: '08:00' },
    timezone: 'Asia/Shanghai',
    repeatWindow: { enabled: false },
    enabled: true,
    nextRunAt: Date.now() + 60_000,
    createdAt: 1,
    updatedAt: 1,
    ...overrides
  }
  state.tasks.set(task.id, task)
  return task
}

beforeEach(() => {
  state.tasks.clear()
  state.runs.clear()
  state.workspaces = new Set(['ws_1'])
  state.refreshed = 0
  state.emitted = []
  state.nextId = 0
  setSchedulerRefresh(() => { state.refreshed += 1 })
  // 广播口由 ipc 层装;这里用同一个插槽捕获事件(`bridge` 不再直接认识 windows)
  setScheduledChangeListener((change) => state.emitted.push(change))
})

afterEach(() => {
  resetSchedulerRefreshForTest()
  setScheduledChangeListener()
})

describe('schedulingBridgeFor · 创建', () => {
  it('★ 任务落在 ctx 的工作区,并标成 agent 建的、链深度 1', async () => {
    const created = await bridge().create({ ...DAILY, timezone: 'Asia/Shanghai' })
    const stored = state.tasks.get(created.id)
    expect(stored?.workspaceId).toBe('ws_1')
    expect(stored?.createdBy).toBe('agent')
    expect(stored?.chainDepth).toBe(1)
    // 模型没指定就继承当前 run 的模型
    expect(stored?.model).toBe('gpt-5.6-sol')
    expect(created.schedule).toBe('every day at 09:00 (Asia/Shanghai)')
    expect(created.nextRunAt).not.toBeNull()
  })

  it('★ 每次写都重排调度器并广播 —— 少一件就是「建了但不跑」或「界面上没有」', async () => {
    await bridge().create(DAILY)
    expect(state.refreshed).toBe(1)
    expect(state.emitted).toEqual([{ kind: 'task', taskId: 'task_1' }])
  })

  it('★ 广播抛错不让创建变成失败 —— 任务已经落库,报失败会让模型再建一遍', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {})
    setScheduledChangeListener(() => { throw new Error('窗口已销毁') })
    const created = await bridge().create(DAILY)
    expect(state.tasks.has(created.id)).toBe(true)
    expect(warn).toHaveBeenCalled()
    warn.mockRestore()
  })

  it('模型别名查不到就拒,并指回「省略它就继承当前模型」', async () => {
    await expect(bridge().create({ ...DAILY, model: 'gpt-9-imaginary' })).rejects.toThrow(/gpt-5.6-sol/)
    expect(state.tasks.size).toBe(0)
  })

  it('★ 永不触发的规则当场拒 —— 过去的一次性时间在界面上看起来完全正常', async () => {
    await expect(
      bridge().create({ ...DAILY, schedule: { kind: 'once', at: '2000-01-01T09:00' } })
    ).rejects.toThrow(/never fires/)
  })

  it('重复窗口:间隔过短、缺 end_time 都拒', async () => {
    await expect(
      bridge().create({ ...DAILY, repeatWindow: { enabled: true, intervalMinutes: 30 } })
    ).rejects.toThrow(/end_time/)
    await expect(
      bridge().create({ ...DAILY, repeatWindow: { enabled: true, endTime: '18:00', intervalMinutes: 1 } })
    ).rejects.toThrow(new RegExp(String(SCHEDULED_AGENT_LIMITS.minIntervalMinutes)))
  })

  it(`到 ${String(SCHEDULED_AGENT_LIMITS.maxTasksPerWorkspace)} 条上限就拒,并让模型先删一条`, async () => {
    for (let i = 0; i < SCHEDULED_AGENT_LIMITS.maxTasksPerWorkspace; i += 1) seedTask()
    await expect(bridge().create(DAILY)).rejects.toThrow(/Delete one/)
  })

  it('工作区没了就拒,不留下一条无主任务', async () => {
    state.workspaces.clear()
    await expect(bridge().create(DAILY)).rejects.toThrow(/no longer exists/)
  })
})

describe('schedulingBridgeFor · 链深度', () => {
  /** 定时任务跑出来的会话:父任务 chainDepth=1 → 这次建的是 2,允许 */
  it('定时任务运行中还能再排一层', async () => {
    const parent = seedTask({ chainDepth: 1 })
    state.runs.set('run_1', {
      id: 'run_1', taskId: parent.id, sessionId: 'session_scheduled', trigger: 'scheduled',
      status: 'running', scheduledAt: 1
    })
    const created = await bridge({ sessionId: 'session_scheduled' }).create(DAILY)
    expect(state.tasks.get(created.id)?.chainDepth).toBe(2)
  })

  /** ★ 到顶那一层拒掉,否则每一层都会在下一次运行时再生出一层 */
  it(`★ 第 ${String(SCHEDULED_AGENT_LIMITS.maxChainDepth)} 层之后不再允许`, async () => {
    const parent = seedTask({ chainDepth: SCHEDULED_AGENT_LIMITS.maxChainDepth })
    state.runs.set('run_1', {
      id: 'run_1', taskId: parent.id, sessionId: 'session_scheduled', trigger: 'scheduled',
      status: 'running', scheduledAt: 1
    })
    await expect(bridge({ sessionId: 'session_scheduled' }).create(DAILY)).rejects.toThrow(/chain depth/)
  })
})

describe('schedulingBridgeFor · 改与删', () => {
  it('能改用户手建的任务,且只动传进来的字段', async () => {
    const task = seedTask({ createdBy: 'user', name: '用户建的' })
    const updated = await bridge().update(task.id, { enabled: false })
    expect(updated.enabled).toBe(false)
    expect(state.tasks.get(task.id)?.name).toBe('用户建的')
    expect(state.refreshed).toBe(1)
  })

  it('★ 别的工作区的任务当成「不存在」,改不了也删不了', async () => {
    const other = seedTask({ workspaceId: 'ws_2' })
    await expect(bridge().update(other.id, { enabled: false })).rejects.toThrow(/ListScheduledTasks/)
    await expect(bridge().remove(other.id)).rejects.toThrow(/ListScheduledTasks/)
    expect(state.tasks.has(other.id)).toBe(true)
  })

  it('改时间时同样校验规则是否还会触发', async () => {
    const task = seedTask()
    await expect(
      bridge().update(task.id, { schedule: { kind: 'once', at: '2001-02-03T04:05' } })
    ).rejects.toThrow(/never fires/)
  })

  it('删:回话带名字,并且广播出去', async () => {
    const task = seedTask({ name: '要删的' })
    expect(await bridge().remove(task.id)).toEqual({ id: task.id, name: '要删的' })
    expect(state.tasks.has(task.id)).toBe(false)
    expect(state.emitted).toEqual([{ kind: 'task', taskId: task.id }])
  })

  it('查:只看当前工作区,且时间已经是人话', async () => {
    seedTask()
    seedTask({ workspaceId: 'ws_2' })
    const listed = await bridge().list()
    expect(listed.length).toBe(1)
    expect(listed[0]?.schedule).toContain('every day at 08:00')
    expect(listed[0]?.createdBy).toBe('user')
  })
})
