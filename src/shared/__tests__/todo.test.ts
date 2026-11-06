/**
 * `shared/agent/todo.ts` 的派生逻辑 —— **增量**是这里唯一的新东西,也是它最难被看出来的地方。
 *
 * 需求:模型每轮发的是完整清单,所以「它悄悄丢掉一项」在界面上完全看不出来 ——
 * 前后两份清单各自都自洽。这里钉的每一条都是「怎样才算一次变化」的判据;
 * 判错的后果是界面上一句**看起来很正常**的假摘要,没有任何报错。
 *
 * 收窄那几条(`narrowTodos` / `latestTodosFrom`)的用例留在
 * `main/kernel/tool/builtin/__tests__/todo-derive.test.ts`(它们是从那里搬过来的),
 * 这里只补「查表按 callId 走」这一条新能力。
 */
import { describe, expect, it } from 'vitest'
import type { AgentMessage } from '../agent/message'
import { assistantMessage, toolResultMessage, userMessage } from '../agent/message'
import { latestTodosFrom, todoHistoryLookup, todoPlan, type TodoItem } from '../agent/todo'

const item = (content: string, status: TodoItem['status']): TodoItem => ({
  content,
  status,
  activeForm: `Doing ${content}`
})

const TOOL = 'TodoWrite'

let n = 0
/** 一对「模型发起调用」+「工具回执」。`isError` 决定这次调用算不算数。 */
function call(input: unknown, isError = false): AgentMessage[] {
  const callId = `c${String(++n)}`
  return [
    assistantMessage(`a${callId}`, [{ type: 'tool_call', callId, name: TOOL, input }], 0),
    toolResultMessage(`r${callId}`, [{ type: 'tool_result', callId, output: { content: 'ok' }, isError }], 0)
  ]
}

describe('todoPlan · 这次更新改了什么', () => {
  it('标出新增、移除、刚完成与刚开始', () => {
    const plan = todoPlan(
      [item('a', 'completed'), item('b', 'in_progress'), item('c', 'pending')],
      [item('a', 'pending'), item('b', 'pending'), item('gone', 'pending')]
    )

    expect(plan.newlyCompleted).toBe(1)
    expect(plan.newlyStarted).toBe(1)
    expect(plan.added.map((t) => t.content)).toEqual(['c'])
    expect(plan.removed.map((t) => t.content)).toEqual(['gone'])
  })

  it('★ 不知道上一份时四个字段全空 —— 不是「整份都是新增」', () => {
    /*
      没有 provider、老转录、历史被压缩都会走到这里。把整份清单标成新增是**错的**,
      而错的增量比没有增量更糟:用户会以为模型刚刚凭空加了五项。
    */
    const plan = todoPlan([item('a', 'completed'), item('b', 'pending')], undefined)

    expect(plan).toEqual({ added: [], removed: [], newlyCompleted: 0, newlyStarted: 0 })
  })

  it('状态没变的项不算任何变化', () => {
    const same = [item('a', 'completed'), item('b', 'pending')]
    const plan = todoPlan(same, [item('a', 'completed'), item('b', 'pending')])

    expect(plan.newlyCompleted).toBe(0)
    expect(plan.newlyStarted).toBe(0)
    expect(plan.added).toEqual([])
    expect(plan.removed).toEqual([])
  })

  it('顺序变化不算变化 —— 它不影响清单说了什么', () => {
    const plan = todoPlan(
      [item('b', 'pending'), item('a', 'pending')],
      [item('a', 'pending'), item('b', 'pending')]
    )

    expect(plan.added).toEqual([])
    expect(plan.removed).toEqual([])
  })

  it('重复内容一对一消耗,不会算成一次新增加一次移除', () => {
    const plan = todoPlan(
      [item('same', 'pending'), item('same', 'pending')],
      [item('same', 'pending')]
    )

    expect(plan.added.map((t) => t.content)).toEqual(['same'])
    expect(plan.removed).toEqual([])
  })

  it('新增且已完成 / 新增且进行中也算「刚完成」「刚开始」', () => {
    const plan = todoPlan([item('new', 'completed'), item('run', 'in_progress')], [])

    expect(plan.newlyCompleted).toBe(1)
    expect(plan.newlyStarted).toBe(1)
    expect(plan.added).toHaveLength(2)
  })

  it('★ 从 completed 退回 pending 不计入任何计数,但要能从 removed/added 之外看出来', () => {
    // 这是一次「模型把做完的项改回未完成」—— 它不该被算成完成,也不该被算成开始
    const plan = todoPlan([item('a', 'pending')], [item('a', 'completed')])

    expect(plan.newlyCompleted).toBe(0)
    expect(plan.newlyStarted).toBe(0)
    expect(plan.added).toEqual([])
    expect(plan.removed).toEqual([])
  })
})

describe('todoHistoryLookup · 按 callId 查「这次之前那份」', () => {
  const START = userMessage('u0', [{ type: 'text', text: '开始干活' }], 0)

  it('★ 每次调用查到的都是**它自己之前**那份,不是全局最新那份', () => {
    const messages = [
      START,
      ...call({ todos: [item('a', 'pending')] }),
      ...call({ todos: [item('a', 'completed'), item('b', 'in_progress')] })
    ]
    const lookup = todoHistoryLookup(messages)
    const calls = messages.flatMap((m) => m.parts).filter((p) => p.type === 'tool_call')

    // 第一次调用之前什么都没有;第二次之前是第一次那份
    expect(lookup(calls[0]?.callId ?? '')).toBeUndefined()
    expect(lookup(calls[1]?.callId ?? '')).toEqual([item('a', 'pending')])
  })

  it('被工具拒绝的那次不更新「上一份」—— 它从来就不是当前清单', () => {
    const messages = [
      START,
      ...call({ todos: [item('a', 'pending')] }),
      ...call({ todos: [item('bad', 'in_progress'), item('bad2', 'in_progress')] }, true),
      ...call({ todos: [item('a', 'completed')] })
    ]
    const lookup = todoHistoryLookup(messages)
    const calls = messages.flatMap((m) => m.parts).filter((p) => p.type === 'tool_call')

    expect(lookup(calls[2]?.callId ?? '')).toEqual([item('a', 'pending')])
  })

  it('还没有回执的那次不更新「上一份」—— 中断时最后一条 tool_call 就是孤儿', () => {
    const messages = [
      START,
      ...call({ todos: [item('a', 'pending')] }),
      assistantMessage('a-orphan', [{ type: 'tool_call', callId: 'orphan', name: TOOL, input: { todos: [item('new', 'pending')] } }], 0)
    ]
    const lookup = todoHistoryLookup(messages)

    expect(lookup('orphan')).toEqual([item('a', 'pending')])
  })

  it('callId 未知 → undefined,不抛', () => {
    expect(todoHistoryLookup([START])('nope')).toBeUndefined()
  })

  it('★ 撞名后的外部名同样按名字分组 —— 这一侧根本不需要知道工具叫什么', () => {
    /*
      渲染层拿不到 `ToolNamer`,所以它不可能按 internalId 查。按转录里每条调用
      **自己带的那个名字**分组,于是 `TodoWrite_a1b2c3d4` 与 `TodoWrite` 各自成组,
      两个工具名互不干扰 —— 这正是主进程那侧必须从注册表查名字的原因的反面。
    */
    const hashed = assistantMessage('a1', [{ type: 'tool_call', callId: 'h1', name: 'TodoWrite_a1b2c3d4', input: { todos: [item('x', 'pending')] } }], 0)
    const hashedResult = toolResultMessage('r1', [{ type: 'tool_result', callId: 'h1', output: { content: 'ok' }, isError: false }], 0)
    const lookup = todoHistoryLookup([START, hashed, hashedResult, ...call({ todos: [item('y', 'pending')] })])
    const calls = [...hashed.parts, ...hashedResult.parts].filter((p) => p.type === 'tool_call')

    expect(lookup(calls[0]?.callId ?? '')).toBeUndefined()
    // 字面名那一路照旧能取到自己的历史,不受另一个名字影响
    expect(latestTodosFrom([START, hashed, hashedResult], 'TodoWrite_a1b2c3d4')).toEqual([item('x', 'pending')])
  })
})
