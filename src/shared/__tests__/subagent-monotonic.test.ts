/**
 * 子代理状态**单调**:running → 终态,不会反过来。
 *
 * 症状(这组用例存在的原因):漏掉一条子 run 的结束事件之后,卡片和右上角胶囊永远「运行中」,
 * 而点开子会话(读库)看到的是「已完成」—— 主进程早把回执改成了 done,渲染层却让内存里那份
 * 过期的 running 盖住了它;或者一次 attach 重放把一张早已完成的卡片改回了 running。
 */
import { describe, expect, it } from 'vitest'
import type { AgentMessage } from '../agent/message'
import { applyEvent, emptyTranscript, mergeSubagentRecord, subagentsFromMessages, type SubagentState } from '../agent/transcript'

const live: SubagentState = { callId: 'task-1', childRunId: 'child-1', status: 'running', toolCalls: 7, toolErrors: 0, currentTool: 'Bash' }

function receipt(status: 'running' | 'done' | 'error'): AgentMessage {
  return {
    id: 'receipt', role: 'tool', createdAt: 1,
    parts: [{ type: 'tool_result', callId: 'task-1', output: { content: 'ok' }, isError: false,
      subagent: { childRunId: 'child-1', status, background: true, summary: 'finished work' } }]
  } as unknown as AgentMessage
}

describe('subagent status is monotonic', () => {
  it('lets a terminal receipt from the database win over a stale live "running"', () => {
    const merged = subagentsFromMessages([receipt('done')], { 'task-1': live })['task-1']
    expect(merged?.status).toBe('done')
    expect(merged?.currentTool).toBeUndefined()
    // 遥测只有实时那份有,照样留着
    expect(merged?.toolCalls).toBe(7)
  })

  it('keeps live telemetry when both sides agree it is running', () => {
    expect(mergeSubagentRecord({ ...live, toolCalls: 0, currentTool: undefined }, live)).toMatchObject({ status: 'running', currentTool: 'Bash', toolCalls: 7 })
  })

  it('lets a live terminal state win over a saved "running" receipt', () => {
    expect(mergeSubagentRecord({ ...live, status: 'running' }, { ...live, status: 'aborted' }).status).toBe('aborted')
  })

  it('does not let a replayed subagent_start revive a finished card', () => {
    const finished = { ...emptyTranscript(), subagents: { 'task-1': { ...live, status: 'done' as const, currentTool: undefined } } }
    const next = applyEvent(finished, { type: 'subagent_start', callId: 'task-1', childRunId: 'child-1', at: 1 })
    expect(next.subagents['task-1']?.status).toBe('done')
  })

  it('ignores replayed telemetry once the card is terminal', () => {
    const finished = { ...emptyTranscript(), subagents: { 'task-1': { ...live, status: 'done' as const, currentTool: undefined } } }
    const next = applyEvent(finished, { type: 'subagent_update', callId: 'task-1', childRunId: 'child-1', currentTool: 'Read' })
    expect(next.subagents['task-1']?.currentTool).toBeUndefined()
  })
})
