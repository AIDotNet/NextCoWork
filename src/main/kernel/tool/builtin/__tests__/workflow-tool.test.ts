import { describe, expect, it } from 'vitest'
import type { InteractionResponse } from '../../../../../shared/agent/interaction'
import type { ToolProgress } from '../../../../../shared/agent/tool'
import { WORKFLOW_MAP_MAX_ITEMS, type WorkflowState } from '../../../../../shared/agent/workflow'
import { isAbortError } from '../../../abort'
import { nodeHost } from '../../../host'
import type { InteractionDraft } from '../../../interaction-gate'
import type { SubagentOutcome, SubagentRequest, ToolContext } from '../../registry'
import { workflowTool } from '../workflow'

/**
 * `Workflow` 工具的测试 —— 只测调度这一半。子代理全是假的(同 `task-tool.test.ts`):
 * 派发、排队、审批都在 `ctx.spawnSubagent` 背后,由 `subagent-wiring.test.ts` 那一侧负责。
 */

function ctx(over: Partial<ToolContext> = {}): ToolContext {
  return {
    workspaceRoot: '/tmp/does-not-matter',
    signal: new AbortController().signal,
    permissionMode: 'auto',
    depth: 0,
    callId: 'call_1',
    runId: 'run_1',
    host: nodeHost(),
    emit: () => {},
    experimentalEnabled: (f) => f === 'workflow',
    ...over
  }
}

const done = (text: string): SubagentOutcome => ({ kind: 'finished', childRunId: `child:${text}`, status: 'done', text })

/** 每个节点按 description(= 节点 title)查表给结局;可选地让某些节点挂起到手动放行 */
function scripted(outcomes: Record<string, SubagentOutcome>) {
  const seen: SubagentRequest[] = []
  let running = 0
  let peak = 0
  const fn = async (r: SubagentRequest): Promise<SubagentOutcome> => {
    seen.push(r)
    running++
    peak = Math.max(peak, running)
    // 让出一拍,好让同一层的兄弟节点有机会同时进来
    await new Promise((resolve) => setTimeout(resolve, 5))
    running--
    return outcomes[r.description] ?? done(`${r.description} ok`)
  }
  return { fn, seen, peak: () => peak }
}

const fanOutIn = {
  title: '代码审查',
  nodes: [
    { id: 'split', title: '拆分', subagent_type: 'general-purpose', prompt: '拆分改动' },
    { id: 'sec', title: '安全', subagent_type: 'general-purpose', prompt: '查安全', depends_on: ['split'] },
    { id: 'perf', title: '性能', subagent_type: 'general-purpose', prompt: '查性能', depends_on: ['split'] },
    { id: 'merge', title: '汇总', subagent_type: 'general-purpose', prompt: '汇总', depends_on: ['sec', 'perf'] }
  ]
}

describe('Workflow 调度', () => {
  it('无依赖关系的节点并行跑,汇总节点等齐上游', async () => {
    const s = scripted({})
    const res = await workflowTool.execute(fanOutIn, ctx({ spawnSubagent: s.fn }))
    expect(res.isError).toBe(false)
    expect(s.seen.map((r) => r.description)).toEqual(['拆分', '安全', '性能', '汇总'])
    expect(s.peak()).toBe(2)
    expect(res.output.workflow?.nodes.map((n) => n.status)).toEqual(['done', 'done', 'done', 'done'])
  })

  it('上游的完整结论附在下游 prompt 后面', async () => {
    const s = scripted({ 安全: done('发现 SQL 注入'), 性能: done('N+1 查询') })
    await workflowTool.execute(fanOutIn, ctx({ spawnSubagent: s.fn }))
    const merge = s.seen.find((r) => r.description === '汇总')!
    expect(merge.prompt.startsWith('汇总')).toBe(true)
    expect(merge.prompt).toContain('发现 SQL 注入')
    expect(merge.prompt).toContain('N+1 查询')
  })

  it('每个节点用自己的 callId 派发 —— 共用的话任务面板里会互相覆盖', async () => {
    const s = scripted({})
    await workflowTool.execute(fanOutIn, ctx({ spawnSubagent: s.fn }))
    expect(new Set(s.seen.map((r) => r.callId)).size).toBe(4)
    expect(s.seen[0]!.callId).toBe('call_1#split')
  })

  it('节点失败只跳过依赖它的那些,不相干的分支照常跑完', async () => {
    const s = scripted({ 安全: { kind: 'finished', childRunId: 'c', status: 'error', text: '', error: '炸了' } })
    const res = await workflowTool.execute(fanOutIn, ctx({ spawnSubagent: s.fn }))
    const status = Object.fromEntries(res.output.workflow!.nodes.map((n) => [n.id, n.status]))
    expect(status).toEqual({ split: 'done', sec: 'error', perf: 'done', merge: 'skipped' })
    // 部分成功仍然是成功 —— 跑完的那几段模型用得上
    expect(res.isError).toBe(false)
    expect(res.output.content).toContain('炸了')
  })

  it('一个节点都没成才算整次失败', async () => {
    const s = scripted({ 拆分: { kind: 'refused', reason: '没有这个子代理' } })
    const res = await workflowTool.execute(fanOutIn, ctx({ spawnSubagent: s.fn }))
    expect(res.isError).toBe(true)
    expect(s.seen).toHaveLength(1)
  })

  it('跑完说了空话的节点按失败算', async () => {
    const s = scripted({ 拆分: done('   ') })
    const res = await workflowTool.execute(fanOutIn, ctx({ spawnSubagent: s.fn }))
    expect(res.output.workflow!.nodes[0]!.status).toBe('error')
  })

  it('子代理被中断时原样抛中断,不包成失败结果', async () => {
    const s = scripted({ 拆分: { kind: 'finished', childRunId: 'c', status: 'aborted', text: '' } })
    await expect(workflowTool.execute(fanOutIn, ctx({ spawnSubagent: s.fn }))).rejects.toSatisfy(isAbortError)
  })

  it('每次状态变化都推一份整图快照', async () => {
    const pushed: WorkflowState[] = []
    const s = scripted({})
    await workflowTool.execute(fanOutIn, ctx({
      spawnSubagent: s.fn,
      emit: (p: ToolProgress) => { if (p.workflow !== undefined) pushed.push(p.workflow) }
    }))
    expect(pushed[0]!.nodes.every((n) => n.status === 'pending')).toBe(true)
    expect(pushed.some((w) => w.nodes.filter((n) => n.status === 'running').length === 2)).toBe(true)
    expect(pushed.at(-1)!.nodes.every((n) => n.status === 'done')).toBe(true)
  })
})

describe('Workflow 重试', () => {
  const fail = (error: string): SubagentOutcome => ({ kind: 'finished', childRunId: 'c', status: 'error', text: '', error })

  it('子代理报错时缺省原地重派一次,第二次成功就算完成', async () => {
    let calls = 0
    const fn = async (r: SubagentRequest): Promise<SubagentOutcome> => {
      if (r.description !== '拆分') return done(`${r.description} ok`)
      calls++
      return calls === 1 ? fail('上游 502') : done('拆好了')
    }
    const res = await workflowTool.execute(fanOutIn, ctx({ spawnSubagent: fn }))
    const split = res.output.workflow!.nodes[0]!
    expect(split.status).toBe('done')
    expect(split.attempts).toBe(2)
  })

  it('重试换 callId,免得第二次的子代理叠在第一次的状态上', async () => {
    const seen: SubagentRequest[] = []
    await workflowTool.execute(fanOutIn, ctx({
      spawnSubagent: async (r) => { seen.push(r); return r.description === '拆分' ? fail('x') : done('ok') }
    }))
    expect(seen.filter((r) => r.description === '拆分').map((r) => r.callId)).toEqual(['call_1#split', 'call_1#split~2'])
  })

  it('retries: 0 不重试;用尽之后报几次', async () => {
    const zero = scripted({ 拆分: fail('x') })
    const input = { ...fanOutIn, nodes: fanOutIn.nodes.map((n) => (n.id === 'split' ? { ...n, retries: 0 } : n)) }
    await workflowTool.execute(input, ctx({ spawnSubagent: zero.fn }))
    expect(zero.seen.filter((r) => r.description === '拆分')).toHaveLength(1)

    const twice = scripted({ 拆分: fail('炸了') })
    const res = await workflowTool.execute(fanOutIn, ctx({ spawnSubagent: twice.fn }))
    expect(res.output.workflow!.nodes[0]!.error).toBe('炸了 (failed 2 attempts)')
  })

  it('用量跨所有尝试累计', async () => {
    let calls = 0
    const fn = async (r: SubagentRequest): Promise<SubagentOutcome> => {
      if (r.description !== '拆分') return done('ok')
      calls++
      const usage = { inputTokens: 100, outputTokens: 10 }
      return calls === 1
        ? { kind: 'finished', childRunId: 'c', status: 'error', text: '', error: 'x', usage }
        : { kind: 'finished', childRunId: 'c', status: 'done', text: '拆好了', usage }
    }
    const res = await workflowTool.execute(fanOutIn, ctx({ spawnSubagent: fn }))
    expect(res.output.workflow!.nodes[0]!.usage).toEqual({ inputTokens: 200, outputTokens: 20 })
  })
})

describe('Workflow allow_partial', () => {
  const partial = {
    ...fanOutIn,
    nodes: fanOutIn.nodes.map((n) => (n.id === 'merge' ? { ...n, allow_partial: true } : n))
  }
  const broken: SubagentOutcome = { kind: 'refused', reason: '排不上' }

  it('部分上游失败时照跑,缺的那块在 prompt 里标成缺失', async () => {
    const s = scripted({ 安全: broken, 性能: done('N+1 查询') })
    const res = await workflowTool.execute(partial, ctx({ spawnSubagent: s.fn }))
    expect(res.output.workflow!.nodes.find((n) => n.id === 'merge')!.status).toBe('done')
    const merge = s.seen.find((r) => r.description === '汇总')!
    expect(merge.prompt).toContain('N+1 查询')
    expect(merge.prompt).toContain('安全 (sec) — MISSING')
  })

  it('上游全部没成就跳过', async () => {
    const s = scripted({ 安全: broken, 性能: broken })
    const res = await workflowTool.execute(partial, ctx({ spawnSubagent: s.fn }))
    expect(res.output.workflow!.nodes.find((n) => n.id === 'merge')!.status).toBe('skipped')
  })

  it('跳过沿依赖链传下去,入参顺序不是拓扑序也一样', async () => {
    const s = scripted({ A: broken })
    const res = await workflowTool.execute({
      title: 't',
      nodes: [
        { id: 'c', title: 'C', subagent_type: 'x', prompt: 'p', depends_on: ['b'] },
        { id: 'b', title: 'B', subagent_type: 'x', prompt: 'p', depends_on: ['a'] },
        { id: 'a', title: 'A', subagent_type: 'x', prompt: 'p' }
      ]
    }, ctx({ spawnSubagent: s.fn }))
    expect(res.output.workflow!.nodes.map((n) => n.status)).toEqual(['skipped', 'skipped', 'error'])
  })
})

describe('Workflow 节点级模型', () => {
  const pinned = {
    ...fanOutIn,
    nodes: fanOutIn.nodes.map((n) => (n.id === 'merge' ? { ...n, model: 'opus', effort: 'high' as const } : n))
  }

  it('钉的模型和档位原样交给派发', async () => {
    const s = scripted({})
    await workflowTool.execute(pinned, ctx({ spawnSubagent: s.fn, subagentModels: () => ['opus', 'haiku'] }))
    const merge = s.seen.find((r) => r.description === '汇总')!
    expect(merge.model).toBe('opus')
    expect(merge.thinking).toBe('high')
    expect(s.seen.find((r) => r.description === '拆分')!.model).toBeUndefined()
  })

  it('认不出的模型在派出任何节点之前就拒,并列出可用的', async () => {
    const s = scripted({})
    const res = await workflowTool.execute(pinned, ctx({ spawnSubagent: s.fn, subagentModels: () => ['haiku'] }))
    expect(res.isError).toBe(true)
    expect(res.output.content).toContain('"opus"')
    expect(res.output.content).toContain('haiku')
    expect(s.seen).toHaveLength(0)
  })
})

describe('Workflow 入参校验', () => {
  it('依赖一个不存在的节点', async () => {
    const res = await workflowTool.execute({
      title: 't',
      nodes: [
        { id: 'a', title: 'A', subagent_type: 'x', prompt: 'p' },
        { id: 'b', title: 'B', subagent_type: 'x', prompt: 'p', depends_on: ['nope'] }
      ]
    }, ctx({ spawnSubagent: scripted({}).fn }))
    expect(res.isError).toBe(true)
    expect(res.output.content).toContain('"nope"')
  })

  it('有环', async () => {
    const res = await workflowTool.execute({
      title: 't',
      nodes: [
        { id: 'a', title: 'A', subagent_type: 'x', prompt: 'p', depends_on: ['b'] },
        { id: 'b', title: 'B', subagent_type: 'x', prompt: 'p', depends_on: ['a'] }
      ]
    }, ctx({ spawnSubagent: scripted({}).fn }))
    expect(res.isError).toBe(true)
    expect(res.output.content).toContain('cycle')
  })
})

describe('Workflow 图形状', () => {
  it('一条依赖都没有就拒 —— 汇总节点忘写 depends_on 时会和被汇总的同时开跑', async () => {
    const s = scripted({})
    const res = await workflowTool.execute({
      title: 't',
      nodes: [
        { id: 'a', title: 'A', subagent_type: 'x', prompt: 'p' },
        { id: 'sum', title: '汇总', subagent_type: 'x', prompt: 'p' }
      ]
    }, ctx({ spawnSubagent: s.fn }))
    expect(res.isError).toBe(true)
    expect(res.output.content).toContain('depends_on')
    expect(s.seen).toHaveLength(0)
  })
})

describe('Workflow 结构化产出', () => {
  const listSchema = { type: 'array', items: { type: 'string' } }
  const graph = (schema: unknown = listSchema) => ({
    title: 't',
    nodes: [
      { id: 'list', title: '列模块', subagent_type: 'x', prompt: '列出模块', output_schema: schema },
      { id: 'sum', title: '汇总', subagent_type: 'x', prompt: '汇总', depends_on: ['list'] }
    ]
  })

  it('取回复里最后一个 json 代码块,校验通过后下游拿到规整的 JSON', async () => {
    const s = scripted({ 列模块: done('先举个例子 ```json\n["示例"]\n```\n结论:\n```json\n["main", "renderer"]\n```') })
    const res = await workflowTool.execute(graph(), ctx({ spawnSubagent: s.fn }))
    expect(res.output.workflow!.nodes[0]!.status).toBe('done')
    expect(s.seen[0]!.prompt).toContain('## Output format')
    const sum = s.seen.find((r) => r.description === '汇总')!
    expect(sum.prompt).toContain('"renderer"')
    expect(sum.prompt).not.toContain('示例')
  })

  it('不合格的 JSON 按失败重试,重试时带上原因', async () => {
    let calls = 0
    const fn = async (r: SubagentRequest): Promise<SubagentOutcome> => {
      if (r.description !== '列模块') return done('ok')
      calls++
      return calls === 1 ? done('```json\n[1, 2]\n```') : done('```json\n["a"]\n```')
    }
    const seen: SubagentRequest[] = []
    const res = await workflowTool.execute(graph(), ctx({ spawnSubagent: async (r) => { seen.push(r); return fn(r) } }))
    expect(res.output.workflow!.nodes[0]!.status).toBe('done')
    const retry = seen.filter((r) => r.description === '列模块')[1]!
    expect(retry.prompt).toContain('did not match output_schema')
  })

  it('写坏的 schema 在开跑前就拒', async () => {
    const s = scripted({})
    const res = await workflowTool.execute(graph({ type: 'nonsense-type' }), ctx({ spawnSubagent: s.fn }))
    expect(res.isError).toBe(true)
    expect(s.seen).toHaveLength(0)
  })
})

describe('Workflow 逐项节点', () => {
  const mapGraph = (extra: Record<string, unknown> = {}) => ({
    title: 't',
    nodes: [
      { id: 'list', title: '列模块', subagent_type: 'x', prompt: '列出模块', output_schema: { type: 'array', items: { type: 'string' } } },
      { id: 'each', title: '分析', subagent_type: 'x', prompt: '分析模块 {{item}} 的职责', map_over: 'list', ...extra },
      { id: 'sum', title: '汇总', subagent_type: 'x', prompt: '汇总', depends_on: ['each'] }
    ]
  })

  it('上游数组的每一项各派一个子代理,{{item}} 换成那一项', async () => {
    const s = scripted({ 列模块: done('```json\n["main", "renderer", "shared"]\n```') })
    const res = await workflowTool.execute(mapGraph(), ctx({ spawnSubagent: s.fn }))
    const each = s.seen.filter((r) => r.description.startsWith('分析'))
    expect(each.map((r) => r.description)).toEqual(['分析 · main', '分析 · renderer', '分析 · shared'])
    expect(each[1]!.prompt.startsWith('分析模块 renderer 的职责')).toBe(true)
    expect(each.map((r) => r.callId)).toEqual(['call_1#each[0]', 'call_1#each[1]', 'call_1#each[2]'])
    // 并行派出,不是一项一项排着跑
    expect(s.peak()).toBe(3)
    const node = res.output.workflow!.nodes.find((n) => n.id === 'each')!
    expect(node.status).toBe('done')
    expect(node.items).toEqual({ total: 3, done: 3, failed: 0, running: 0 })
    // 逐项节点不把整个数组再附一遍
    expect(each[0]!.prompt).not.toContain('Results from the steps')
  })

  it('部分项失败:节点仍算完成,失败的项写进产出', async () => {
    const s = scripted({
      列模块: done('```json\n["a", "b"]\n```'),
      '分析 · b': { kind: 'refused', reason: '排不上' }
    })
    const res = await workflowTool.execute(mapGraph(), ctx({ spawnSubagent: s.fn }))
    const node = res.output.workflow!.nodes.find((n) => n.id === 'each')!
    expect(node.status).toBe('done')
    expect(node.items).toMatchObject({ done: 1, failed: 1 })
    const sum = s.seen.find((r) => r.description === '汇总')!
    expect(sum.prompt).toContain('b — FAILED: 排不上')
  })

  it('每一项都失败就算节点失败,下游跳过', async () => {
    const s = scripted({ 列模块: done('```json\n["a"]\n```'), '分析 · a': { kind: 'refused', reason: 'x' } })
    const res = await workflowTool.execute(mapGraph(), ctx({ spawnSubagent: s.fn }))
    expect(res.output.workflow!.nodes.map((n) => n.status)).toEqual(['done', 'error', 'skipped'])
  })

  it('空数组:节点完成,不派子代理', async () => {
    const s = scripted({ 列模块: done('```json\n[]\n```') })
    const res = await workflowTool.execute(mapGraph(), ctx({ spawnSubagent: s.fn }))
    expect(res.output.workflow!.nodes.find((n) => n.id === 'each')!.status).toBe('done')
    expect(s.seen.some((r) => r.description.startsWith('分析'))).toBe(false)
  })

  it('超过上限的项不处理,并在产出里写明', async () => {
    const many = Array.from({ length: WORKFLOW_MAP_MAX_ITEMS + 5 }, (_, i) => `m${String(i)}`)
    const s = scripted({ 列模块: done('```json\n' + JSON.stringify(many) + '\n```') })
    const res = await workflowTool.execute(mapGraph(), ctx({ spawnSubagent: s.fn }))
    const node = res.output.workflow!.nodes.find((n) => n.id === 'each')!
    expect(node.items).toMatchObject({ total: WORKFLOW_MAP_MAX_ITEMS, dropped: 5 })
    expect(res.output.content).toContain(`only the first ${String(WORKFLOW_MAP_MAX_ITEMS)} were processed`)
  })

  it('结构化逐项节点交出每项一份的数组', async () => {
    const s = scripted({
      列模块: done('```json\n["a", "b"]\n```'),
      '分析 · a': done('```json\n{"risk": 1}\n```'),
      '分析 · b': done('```json\n{"risk": 2}\n```')
    })
    const schema = { type: 'object', properties: { risk: { type: 'number' } }, required: ['risk'] }
    await workflowTool.execute(mapGraph({ output_schema: schema }), ctx({ spawnSubagent: s.fn }))
    const sum = s.seen.find((r) => r.description === '汇总')!
    expect(sum.prompt.replace(/\s/g, '')).toContain('[{"risk":1},{"risk":2}]')
  })

  it('上游没声明数组型 output_schema 就在开跑前拒', async () => {
    const s = scripted({})
    const g = mapGraph()
    const res = await workflowTool.execute({ ...g, nodes: [{ ...g.nodes[0]!, output_schema: undefined }, ...g.nodes.slice(1)] }, ctx({ spawnSubagent: s.fn }))
    expect(res.isError).toBe(true)
    expect(res.output.content).toContain('top-level type is "array"')
    expect(s.seen).toHaveLength(0)
  })
})

describe('Workflow 运行前确认', () => {
  const confirmOn = (f: string): boolean => f === 'workflow' || f === 'workflowConfirm'
  const answer = (action: 'run' | 'revise' | 'cancel', feedback?: string) => {
    const asked: InteractionDraft[] = []
    const interact = async (draft: InteractionDraft): Promise<InteractionResponse> => {
      asked.push(draft)
      return { id: 'i1', kind: 'workflow_approval', action, ...(feedback === undefined ? {} : { feedback }) }
    }
    return { interact, asked }
  }

  it('点「运行」之前一个节点都不派;给用户看的是全 pending 的图', async () => {
    const s = scripted({})
    const a = answer('run')
    const res = await workflowTool.execute(fanOutIn, ctx({ spawnSubagent: s.fn, interact: a.interact, experimentalEnabled: confirmOn }))
    expect(res.isError).toBe(false)
    expect(a.asked).toHaveLength(1)
    const draft = a.asked[0]!
    expect(draft.kind).toBe('workflow_approval')
    if (draft.kind === 'workflow_approval') {
      expect(draft.callId).toBe('call_1')
      expect(draft.workflow.nodes.every((n) => n.status === 'pending')).toBe(true)
    }
    expect(s.seen).toHaveLength(4)
  })

  it('要求修改:不派节点,反馈原样转给模型', async () => {
    const s = scripted({})
    const res = await workflowTool.execute(fanOutIn, ctx({
      spawnSubagent: s.fn, interact: answer('revise', '汇总前加一个测试覆盖分析').interact, experimentalEnabled: confirmOn
    }))
    expect(res.isError).toBe(true)
    expect(res.output.content).toContain('汇总前加一个测试覆盖分析')
    expect(s.seen).toHaveLength(0)
  })

  it('取消:不派节点', async () => {
    const s = scripted({})
    const res = await workflowTool.execute(fanOutIn, ctx({ spawnSubagent: s.fn, interact: answer('cancel').interact, experimentalEnabled: confirmOn }))
    expect(res.isError).toBe(true)
    expect(res.output.content).toContain('cancelled')
    expect(s.seen).toHaveLength(0)
  })

  it('设置关掉就不问', async () => {
    const a = answer('cancel')
    const res = await workflowTool.execute(fanOutIn, ctx({ spawnSubagent: scripted({}).fn, interact: a.interact }))
    expect(res.isError).toBe(false)
    expect(a.asked).toHaveLength(0)
  })

  it('非法的图先拒,不打扰用户', async () => {
    const a = answer('run')
    await workflowTool.execute({
      title: 't',
      nodes: [
        { id: 'a', title: 'A', subagent_type: 'x', prompt: 'p', depends_on: ['b'] },
        { id: 'b', title: 'B', subagent_type: 'x', prompt: 'p', depends_on: ['a'] }
      ]
    }, ctx({ spawnSubagent: scripted({}).fn, interact: a.interact, experimentalEnabled: confirmOn }))
    expect(a.asked).toHaveLength(0)
  })
})

describe('Workflow 开关', () => {
  const spawn = scripted({}).fn
  it('实验开关关着就不下发', () => {
    expect(workflowTool.isEnabled?.(ctx({ spawnSubagent: spawn, experimentalEnabled: () => false }))).toBe(false)
  })
  it('没有设置可读(纯内核环境)时当作关', () => {
    expect(workflowTool.isEnabled?.(ctx({ spawnSubagent: spawn, experimentalEnabled: undefined }))).toBe(false)
  })
  it('子代理里不下发', () => {
    expect(workflowTool.isEnabled?.(ctx({ spawnSubagent: spawn, depth: 1 }))).toBe(false)
  })
  it('开着、主 run、能派子代理 → 下发', () => {
    expect(workflowTool.isEnabled?.(ctx({ spawnSubagent: spawn }))).toBe(true)
  })
})
