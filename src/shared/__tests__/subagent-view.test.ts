/**
 * 子代理展示投影 —— 卡片、活动胶囊、只读子会话**共用的那一个函数**。
 * 这几条守的都是「两处曾经各算各的、算出了不同答案」的地方。
 */
import { describe, expect, it } from 'vitest'
import type { SubagentState } from '../agent/transcript'
import { compareSubagentsByRecency, subagentViewOf } from '../domain/subagent-view'

const base: SubagentState = { callId: 'c1', childRunId: 'r1', status: 'running', toolCalls: 0, toolErrors: 0 }

describe('subagentViewOf', () => {
  it('counts a running agent up to now', () => {
    expect(subagentViewOf({ ...base, startedAt: 1_000 }, 4_000).elapsedMs).toBe(3_000)
  })

  it('never lets a finished agent keep ticking when endedAt is missing', () => {
    // 胶囊以前用 `endedAt ?? now`:从库里回执重建的终态卡片没有 endedAt,计时一直在走
    const view = subagentViewOf({ ...base, status: 'done', startedAt: 1_000, lastEventAt: 2_500 }, 99_000)
    expect(view.elapsedMs).toBe(1_500)
    expect(subagentViewOf({ ...base, status: 'done', startedAt: 1_000 }, 99_000).elapsedMs).toBeUndefined()
  })

  it('separates "still streaming" from "old transcript without state"', () => {
    expect(subagentViewOf(undefined, 0, true).status).toBe('pending')
    expect(subagentViewOf(undefined, 0).status).toBe('done')
  })

  it('offers stop and background only while running, background only for foreground agents', () => {
    expect(subagentViewOf(base, 0)).toMatchObject({ canStop: true, canBackground: true })
    expect(subagentViewOf({ ...base, background: true }, 0)).toMatchObject({ canStop: true, canBackground: false })
    expect(subagentViewOf({ ...base, status: 'aborted' }, 0)).toMatchObject({ canStop: false, canBackground: false })
  })

  it('reports only for background agents', () => {
    expect(subagentViewOf({ ...base, status: 'done' }, 0).report).toBe('none')
    expect(subagentViewOf({ ...base, status: 'done', background: true }, 0).report).toBe('pending')
    expect(subagentViewOf({ ...base, status: 'done', background: true, reportStatus: 'reported' }, 0).report).toBe('reported')
  })

  it('shows the current tool while running and the agent type otherwise', () => {
    expect(subagentViewOf({ ...base, currentTool: 'Read', currentTarget: 'a.ts', subagentType: 'explore' }, 0).detail).toBe('Read · a.ts')
    expect(subagentViewOf({ ...base, status: 'done', currentTool: 'Read', subagentType: 'explore' }, 0).detail).toBe('explore')
  })

  it('sorts running agents first, then newest', () => {
    const list = [
      { ...base, callId: 'old-done', status: 'done' as const, startedAt: 1 },
      { ...base, callId: 'new-done', status: 'done' as const, startedAt: 5 },
      { ...base, callId: 'old-run', startedAt: 2 }
    ].sort(compareSubagentsByRecency)
    expect(list.map((item) => item.callId)).toEqual(['old-run', 'new-done', 'old-done'])
  })
})
