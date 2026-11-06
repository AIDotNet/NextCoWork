import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { RunRequest, ThinkingLevel } from '../../../shared/agent/run-request'
import type { ModelAlias, ReasoningEffort, ThinkingConfig } from '../../../shared/domain/provider'
import type { ScheduledRun, ScheduledTask } from '../../../shared/domain/scheduled'
import type { Workspace } from '../../../shared/domain/workspace'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../../shared/domain/workspace'

const state = vi.hoisted(() => ({
  tasks: [] as ScheduledTask[],
  workspace: undefined as Workspace | undefined,
  model: undefined as ModelAlias | undefined,
  sessions: [] as Array<{ thinking: ThinkingLevel }>,
  requests: [] as RunRequest[],
  scheduledRuns: [] as ScheduledRun[],
  resolved: [] as Array<{ model: string; providerId: string | undefined }>
}))

vi.mock('electron', () => ({ Notification: { isSupported: () => false } }))
vi.mock('../../runtime', () => ({
  runAgent: vi.fn(),
  getRouter: () => ({
    resolveModel: (model: string, providerId?: string) => {
      state.resolved.push({ model, providerId })
      return state.model
    }
  })
}))
vi.mock('../../window/registry', () => ({ windows: { emitToAll: vi.fn() } }))
vi.mock('../../state/store', () => ({
  store: {
    listScheduledTasks: () => state.tasks,
    getScheduledTask: (id: string) => state.tasks.find((task) => task.id === id),
    getWorkspace: (id: string) => (state.workspace?.id === id ? state.workspace : undefined),
    createSession: (input: { thinking: ThinkingLevel }) => {
      state.sessions.push({ thinking: input.thinking })
      return input
    },
    putScheduledRun: (run: ScheduledRun) => {
      state.scheduledRuns.push(run)
      return run
    },
    getHistory: () => [],
    putScheduledTask: (task: ScheduledTask) => {
      const index = state.tasks.findIndex((item) => item.id === task.id)
      if (index >= 0) state.tasks[index] = task
      return task
    },
    getScheduledRun: (id: string) => [...state.scheduledRuns].reverse().find((run) => run.id === id),
    getSettings: () => ({ locale: 'zh-CN' })
  }
}))

import { runs } from '../../kernel/run-registry'
import { runAgent } from '../../runtime'
import { runScheduledTaskNow, startScheduler, stopScheduler } from '../scheduler'

beforeEach(() => {
  vi.useFakeTimers()
  vi.setSystemTime(new Date('2026-10-06T00:00:00Z'))
  state.tasks = []
  state.workspace = undefined
  state.model = undefined
  state.sessions = []
  state.requests = []
  state.scheduledRuns = []
  state.resolved = []
  vi.mocked(runAgent).mockImplementation(async (handle, request) => {
    state.requests.push(request)
    handle.finish('done')
  })
})

afterEach(() => {
  stopScheduler()
  runs.clearForTest()
  vi.useRealTimers()
  vi.restoreAllMocks()
})

const task = (delay: number): ScheduledTask => ({
  id: 'task', name: 'Future task', prompt: 'test', workspaceId: 'workspace', model: 'test',
  timezone: 'UTC', schedule: { kind: 'once', at: '2026-11-06T00:00' },
  repeatWindow: { enabled: false }, enabled: true, nextRunAt: Date.now() + delay,
  createdAt: 1, updatedAt: 1
})

describe('scheduler timer bounds', () => {
  it('caps a distant occurrence and rechecks without a 1ms overflow loop', async () => {
    const day = 24 * 60 * 60 * 1000
    state.tasks = [task(31 * day)]
    const timers = vi.spyOn(globalThis, 'setTimeout')
    startScheduler()
    expect(timers.mock.calls.at(-1)?.[1]).toBe(2_147_483_647)
    await vi.advanceTimersByTimeAsync(day)
    expect(timers).toHaveBeenCalledTimes(2)
    expect(vi.getTimerCount()).toBe(1)
    await vi.advanceTimersByTimeAsync(2_147_483_647 - day)
    expect(timers.mock.calls.at(-1)?.[1]).toBeLessThan(2_147_483_647)
    expect(vi.getTimerCount()).toBe(1)
  })

  it('preserves a short delay and removes the timer on shutdown', () => {
    state.tasks = [task(60_000)]
    const timers = vi.spyOn(globalThis, 'setTimeout')
    startScheduler()
    expect(timers.mock.calls.at(-1)?.[1]).toBe(60_000)
    stopScheduler()
    expect(vi.getTimerCount()).toBe(0)
  })
})

const BUDGET: ThinkingConfig = {
  mode: 'budget', defaultEnabled: true, defaultBudgetTokens: 60_000,
  parameterPath: 'thinking.budget_tokens'
}

const OPUS_46_EFFORT: ThinkingConfig = { mode: 'effort', defaultEnabled: true, defaultEffort: 'high' }
const OPUS_46_EFFORTS = ['none', 'low', 'medium', 'high', 'max'] as const satisfies readonly ReasoningEffort[]

/** Saved budget is what resolve returns; normalization is read-only and must not rewrite the workspace default. */
const THINKING_CASES: ReadonlyArray<{
  label: string
  upstreamModel: string
  defaultThinking: ThinkingLevel
  expected: ThinkingLevel
  thinkingConfig: ThinkingConfig
  reasoningEfforts?: readonly ReasoningEffort[]
}> = [
  { label: 'saved budget opus 5.5 off normalizes to auto', upstreamModel: 'claude-opus-5-5', defaultThinking: 'off', expected: 'auto', thinkingConfig: BUDGET },
  { label: 'saved budget opus 5.5 minimal normalizes to auto', upstreamModel: 'claude-opus-5-5', defaultThinking: 'minimal', expected: 'auto', thinkingConfig: BUDGET },
  { label: 'saved budget opus 5.5 higher stays higher', upstreamModel: 'claude-opus-5-5', defaultThinking: 'higher', expected: 'higher', thinkingConfig: BUDGET },
  { label: 'opus 4.6 effort higher normalizes to auto', upstreamModel: 'claude-opus-4-6', defaultThinking: 'higher', expected: 'auto', thinkingConfig: OPUS_46_EFFORT, reasoningEfforts: OPUS_46_EFFORTS },
  { label: 'manual budget opus 4.6 minimal stays minimal', upstreamModel: 'claude-opus-4-6', defaultThinking: 'minimal', expected: 'minimal', thinkingConfig: BUDGET },
  { label: 'opus 5 off stays off', upstreamModel: 'claude-opus-5', defaultThinking: 'off', expected: 'off', thinkingConfig: BUDGET }
]

function resolvedAlias(upstreamModel: string, thinkingConfig: ThinkingConfig, reasoningEfforts?: readonly ReasoningEffort[]): ModelAlias {
  return {
    alias: 'claude-opus', providerId: 'provider-anthropic', upstreamModel,
    runtimeProtocol: 'anthropic',
    capabilities: { tools: true, vision: true, thinking: true, caching: false },
    contextWindow: 1_000_000, maxOutputTokens: 64_000,
    thinkingConfig: { ...thinkingConfig },
    ...(reasoningEfforts === undefined ? {} : { reasoningEfforts: [...reasoningEfforts] })
  }
}

describe('scheduler thinking normalization', () => {
  it.each(THINKING_CASES)('$label', async (row) => {
    const providerId = 'provider-anthropic'
    const settings = { ...DEFAULT_WORKSPACE_SETTINGS, defaultThinking: row.defaultThinking, activeSkillIds: [] as string[] }
    const savedSettings = structuredClone(settings)
    const model = resolvedAlias(row.upstreamModel, row.thinkingConfig, row.reasoningEfforts)
    const savedModel = structuredClone(model)
    state.model = model
    state.workspace = {
      id: 'workspace', name: 'Workspace', rootPath: 'F:/tmp/workspace',
      settings, createdAt: 1, lastOpenedAt: 1
    }
    const scheduled: ScheduledTask = {
      id: `task-${row.upstreamModel}-${row.defaultThinking}`,
      name: 'Nightly', prompt: 'summarize', workspaceId: 'workspace',
      model: 'claude-opus', modelProviderId: providerId,
      timezone: 'UTC', schedule: { kind: 'daily', time: '09:00' },
      repeatWindow: { enabled: false }, enabled: true,
      nextRunAt: Date.now() + 86_400_000, createdAt: 1, updatedAt: 1
    }
    state.tasks = [scheduled]
    await runScheduledTaskNow(scheduled.id)
    await vi.advanceTimersByTimeAsync(0)
    expect(state.resolved).toEqual([{ model: 'claude-opus', providerId }])
    expect(state.sessions.map((session) => session.thinking)).toEqual([row.expected])
    expect(state.requests.map((request) => request.thinking)).toEqual([row.expected])
    expect(state.sessions[0]?.thinking).toBe(state.requests[0]?.thinking)
    expect(state.workspace.settings).toEqual(savedSettings)
    expect(state.workspace.settings.defaultThinking).toBe(row.defaultThinking)
    expect(state.model).toEqual(savedModel)
    expect(state.scheduledRuns.at(-1)?.status).toBe('success')
  })
})
