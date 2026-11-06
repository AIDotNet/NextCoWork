import { describe, expect, it } from 'vitest'
import { InteractionGate } from '../interaction-gate'
import { RunHandle } from '../run-registry'
import type { RunRequest } from '../../../shared/agent/run-request'
import type { InteractionResponse } from '../../../shared/agent/interaction'

const req: RunRequest = {
  runId: 'r', sessionId: 's', workspaceId: 'w', depth: 0, input: [], mode: 'normal', thinking: 'off',
  webSearch: false, permissionMode: 'ask', model: 'm', skillIds: []
}

describe('InteractionGate', () => {
  it('records approval in attach state, accepts edited input once and clears pending state', async () => {
    const gate = new InteractionGate()
    const handle = new RunHandle(req)
    const result = gate.request(handle, { kind: 'tool_permission', callId: 'c', toolName: 'Write',
      input: { path: 'a' }, destructive: true, readOnly: false }, 100)
    const pending = gate.list()[0]!
    expect(handle.snapshot(0).pendingInteractions).toEqual([pending])
    const response: InteractionResponse = { id: pending.id, kind: 'tool_permission', decision: { kind: 'allow_edited', input: { path: 'b' } } }
    gate.respond(response)
    expect(await result).toEqual(response)
    expect(handle.snapshot(0).pendingInteractions).toEqual([])
    expect(gate.list()).toEqual([])
    expect(() => gate.respond(response)).toThrow('no longer pending')
  })

  it('rejects mismatched kinds, invalid answers and unsupported persistent grants', async () => {
    const gate = new InteractionGate()
    const handle = new RunHandle(req)
    const result = gate.request(handle, { kind: 'ask_user', questions: [{ header: 'Pick', question: 'Pick one',
      options: [{ label: 'A' }, { label: 'B' }], multiSelect: false, allowFreeform: false }] }, 100)
    const id = gate.list()[0]!.id
    expect(() => gate.respond({ id, kind: 'plan_approval', action: 'approve_current' })).toThrow('Invalid')
    expect(() => gate.respond({ id, kind: 'ask_user', answers: [['C']] })).toThrow('Invalid')
    expect(() => gate.respond({ id, kind: 'ask_user', answers: [['A', 'B']] })).toThrow('Invalid')
    expect(() => gate.respond({ id, kind: 'ask_user', answers: [] })).toThrow('Invalid')
    expect(gate.list()).toHaveLength(1)
    gate.respond({ id, kind: 'ask_user', answers: [['B']] })
    expect(await result).toMatchObject({ answers: [['B']] })
    const approval = gate.request(handle, { kind: 'tool_permission', callId: 'c', toolName: 'Write', input: {}, readOnly: false, destructive: true }, 101)
    const permissionId = gate.list()[0]!.id
    // workspace 作用域有 `.next-cowork/settings.local.json` 兜着;session 还没有存放处。
    expect(() => gate.respond({ id: permissionId, kind: 'tool_permission', decision: { kind: 'allow_always', scope: 'session' } })).toThrow('Invalid')
    gate.respond({ id: permissionId, kind: 'tool_permission', decision: { kind: 'allow_always', scope: 'workspace' } })
    expect(await approval).toMatchObject({ decision: { kind: 'allow_always', scope: 'workspace' } })
  })

  /**
   * ★ 多道题时,答案是**按下标**对到题上的。所以长度不等必须整份退回 ——
   * 窗口重载后拿着旧题面抢答的话,按下标对齐会把「要不要加测试」的回答
   * 安到「改哪个模块」上,而两边都不会报错。
   */
  it('★ 多题多选:逐题校验,长度不等整份退回', async () => {
    const gate = new InteractionGate()
    const handle = new RunHandle(req)
    const result = gate.request(handle, { kind: 'ask_user', questions: [
      { header: '范围', question: '改哪里?', options: [{ label: 'A' }, { label: 'B' }], multiSelect: true, allowFreeform: false },
      { header: '细节', question: '还有什么?', options: [], multiSelect: false, allowFreeform: true }
    ] }, 100)
    const id = gate.list()[0]!.id

    expect(() => gate.respond({ id, kind: 'ask_user', answers: [['A']] })).toThrow('Invalid')
    expect(() => gate.respond({ id, kind: 'ask_user', answers: [['A'], []] })).toThrow('Invalid')
    // 第一题不许自由作答,写的字不在选项里
    expect(() => gate.respond({ id, kind: 'ask_user', answers: [['自己写的'], ['随便']] })).toThrow('Invalid')
    // 同一项勾两次(界面做不到,但 IPC 那侧递得进来)
    expect(() => gate.respond({ id, kind: 'ask_user', answers: [['A', 'A'], ['随便']] })).toThrow('Invalid')
    // 第二题是纯问答,自由作答放行
    gate.respond({ id, kind: 'ask_user', answers: [['A', 'B'], ['随便写的']] })
    expect(await result).toMatchObject({ answers: [['A', 'B'], ['随便写的']] })
  })

  it('单选题不接受多个答案,dismiss 一律放行', async () => {
    const gate = new InteractionGate()
    const handle = new RunHandle(req)
    const result = gate.request(handle, { kind: 'ask_user', questions: [
      { header: '范围', question: '改哪里?', options: [{ label: 'A' }, { label: 'B' }], multiSelect: false, allowFreeform: true }
    ] }, 100)
    const id = gate.list()[0]!.id
    expect(() => gate.respond({ id, kind: 'ask_user', answers: [['A', 'B']] })).toThrow('Invalid')
    gate.respond({ id, kind: 'ask_user', answers: null })
    expect(await result).toMatchObject({ answers: null })
  })

  it('accepts file-backed plan decisions and requires revision feedback', async () => {
    const gate = new InteractionGate()
    const handle = new RunHandle(req)
    const result = gate.request(handle, {
      kind: 'plan_approval',
      planId: 'plan-1',
      path: '.plan/plan-1.md',
      plan: '# Plan'
    }, 100)
    const id = gate.list()[0]!.id
    expect(() => gate.respond({ id, kind: 'plan_approval', action: 'request_revision' })).toThrow('Invalid')
    gate.respond({ id, kind: 'plan_approval', action: 'request_revision', feedback: 'Add tests.' })
    expect(await result).toMatchObject({ action: 'request_revision', feedback: 'Add tests.' })
  })

  it('workflow_approval:只收三种动作,要求修改必须带反馈', async () => {
    const gate = new InteractionGate()
    const handle = new RunHandle(req)
    const result = gate.request(handle, {
      kind: 'workflow_approval',
      callId: 'call_1',
      workflow: { title: 'W', nodes: [] }
    }, 100)
    const id = gate.list()[0]!.id
    expect(() => gate.respond({ id, kind: 'workflow_approval', action: 'go' } as unknown as InteractionResponse)).toThrow('Invalid')
    expect(() => gate.respond({ id, kind: 'workflow_approval', action: 'revise' })).toThrow('Invalid')
    expect(() => gate.respond({ id, kind: 'workflow_approval', action: 'revise', feedback: '  ' })).toThrow('Invalid')
    gate.respond({ id, kind: 'workflow_approval', action: 'revise', feedback: '加一个测试节点' })
    expect(await result).toMatchObject({ action: 'revise', feedback: '加一个测试节点' })
  })

  it('goal_proposal:approved 必须是布尔,不合格的整份退回', async () => {
    const gate = new InteractionGate()
    const handle = new RunHandle(req)
    const result = gate.request(handle, { kind: 'goal_proposal', sessionId: 's', condition: '让 bun test 全绿' }, 100)
    const id = gate.list()[0]!.id
    expect(gate.hasGoalProposal('s')).toBe(true)
    expect(gate.hasGoalProposal('other')).toBe(false)

    expect(() => gate.respond({ id, kind: 'goal_proposal', approved: 'yes' } as unknown as InteractionResponse)).toThrow('Invalid')
    expect(() => gate.respond({ id, kind: 'goal_proposal' } as unknown as InteractionResponse)).toThrow('Invalid')
    expect(() => gate.respond({ id, kind: 'ask_user', answers: null })).toThrow('Invalid')
    expect(gate.list()).toHaveLength(1)

    gate.respond({ id, kind: 'goal_proposal', approved: true })
    expect(await result).toEqual({ id, kind: 'goal_proposal', approved: true })
    expect(gate.list()).toEqual([])
    expect(gate.hasGoalProposal('s')).toBe(false)
  })

  /**
   * ★ 目标提案**不占住 run**:批准要问的是一句「这个目标要不要」,而模型
   *   这时候该继续干活。按其他三种交互那样在 `run_end` 一律 abort 的话,
   *   一个说得很快的模型会让弹窗在用户看清之前自己关掉。
   */
  it('★ goal_proposal 在 run 正常收尾后仍然可以回答', async () => {
    const gate = new InteractionGate()
    const handle = new RunHandle(req)
    const result = gate.request(handle, { kind: 'goal_proposal', sessionId: 's', condition: '让 bun test 全绿' }, 100)
    const id = gate.list()[0]!.id

    handle.finish('done')
    expect(handle.status).toBe('done')
    expect(gate.list()).toHaveLength(1)
    expect(handle.pendingInteractions).toHaveLength(1)

    gate.respond({ id, kind: 'goal_proposal', approved: false })
    expect(await result).toEqual({ id, kind: 'goal_proposal', approved: false })
    expect(gate.list()).toEqual([])
    expect(handle.pendingInteractions).toEqual([])
    expect(() => gate.respond({ id, kind: 'goal_proposal', approved: true })).toThrow('no longer pending')
  })

  it.each(['abort', 'error'] as const)('goal_proposal 在 run %s 时落到 aborted 并结算', async (action) => {
    const gate = new InteractionGate()
    const handle = new RunHandle(req)
    const result = gate.request(handle, { kind: 'goal_proposal', sessionId: 's', condition: '让 bun test 全绿' }, 100)
    const id = gate.list()[0]!.id

    const rejected = expect(result).rejects.toMatchObject({ name: 'AbortError' })
    if (action === 'abort') handle.abort({ by: 'user' })
    else handle.finish('error')
    await rejected
    expect(gate.list()).toEqual([])
    expect(gate.hasGoalProposal('s')).toBe(false)
    expect(handle.pendingInteractions).toEqual([])
    expect(() => gate.respond({ id, kind: 'goal_proposal', approved: true })).toThrow()
  })

  it('cancelGoalProposals 只结算这条会话的目标提案', async () => {
    const gate = new InteractionGate()
    const handle = new RunHandle(req)
    const first = gate.request(handle, { kind: 'goal_proposal', sessionId: 's1', condition: 'a' }, 100)
    const second = gate.request(handle, { kind: 'goal_proposal', sessionId: 's2', condition: 'b' }, 101)
    const asked = gate.request(handle, { kind: 'ask_user', questions: [{ header: '继续', question: '继续?',
      options: [], multiSelect: false, allowFreeform: true }] }, 102)

    const rejected = expect(first).rejects.toMatchObject({ name: 'AbortError' })
    gate.cancelGoalProposals('s1')
    await rejected

    expect(gate.hasGoalProposal('s1')).toBe(false)
    expect(gate.hasGoalProposal('s2')).toBe(true)
    expect(gate.list().map((i) => i.kind).sort()).toEqual(['ask_user', 'goal_proposal'])
    expect(handle.pendingInteractions).toHaveLength(2)

    // 没被取消的那两条照常回答
    gate.respond({ id: gate.list().find((i) => i.kind === 'goal_proposal')!.id, kind: 'goal_proposal', approved: true })
    expect(await second).toMatchObject({ kind: 'goal_proposal', approved: true })
    gate.respond({ id: gate.list()[0]!.id, kind: 'ask_user', answers: [['好']] })
    expect(await asked).toMatchObject({ answers: [['好']] })
    expect(gate.list()).toEqual([])
  })

  it.each(['abort', 'finish'] as const)('settles the blocked call on run %s and rejects late approvals', async (action) => {
    const gate = new InteractionGate()
    const handle = new RunHandle(req)
    const result = gate.request(handle, { kind: 'ask_user', questions: [{ header: 'Continue', question: 'Continue?',
      options: [], multiSelect: false, allowFreeform: true }] }, 100)
    const id = gate.list()[0]!.id
    const rejected = expect(result).rejects.toMatchObject({ name: 'AbortError' })
    if (action === 'abort') handle.abort({ by: 'user' })
    else handle.finish('error')
    await rejected
    expect(gate.list()).toEqual([])
    expect(handle.pendingInteractions).toEqual([])
    expect(() => gate.respond({ id, kind: 'ask_user', answers: [['late']] })).toThrow()
  })
})
