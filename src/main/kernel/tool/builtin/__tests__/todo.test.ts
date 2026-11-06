import { describe, expect, it } from 'vitest'
import { assistantMessage, toolResultMessage } from '../../../../../shared/agent/message'
import { nodeHost } from '../../../host'
import type { ToolContext } from '../../registry'
import { TODO_LIMITS, todoWriteTool } from '../todo'

/**
 * `TodoWrite` 的测试。
 *
 * ★ 这个文件里最重要的一条是**反向断言**:调用两次之后,第二次的结果里
 * 不含第一次的任何内容。它钉的是文件头那段「完全无状态」——
 * 谁哪天顺手在这里加一个 module 级的 `let current`,这条会立刻红。
 */

function ctx(): ToolContext {
  return {
    workspaceRoot: '/tmp/does-not-matter',
    signal: new AbortController().signal,
    permissionMode: 'auto',
    depth: 0,
    callId: 'call_1',
    runId: 'run_1',
    host: nodeHost(),
    emit: () => {}
  }
}

type Status = 'pending' | 'in_progress' | 'completed'
const todo = (content: string, status: Status): object => ({
  content,
  status,
  activeForm: `正在${content}`
})

describe('TodoWrite · 标记', () => {
  /**
   * ★ 这不是「填对一个字段」,而是一个功能开关:
   * `snapshot({ readOnlyOnly: true })` 只留只读工具,而计划模式恰恰是最需要
   * 列清单的时候。标成非只读的话,plan 模式下模型连计划都写不了。
   */
  it('★ readOnly —— plan 模式的快照必须留住它', () => {
    expect(todoWriteTool.readOnly).toBe(true)
    expect(todoWriteTool.destructive).toBe(false)
    expect(todoWriteTool.needsNetwork).toBe(false)
  })
})

describe('TodoWrite · 校验', () => {
  it('★ 空清单被拒,并说清为什么(它会抹掉界面上已有的计划)', async () => {
    const r = await todoWriteTool.execute({ todos: [] }, ctx())
    expect(r.isError).toBe(true)
    expect(r.output.content).toContain('empty')
    expect(r.output.content).toContain('wipes out')
  })

  it('一项 in_progress 是合法的', async () => {
    const r = await todoWriteTool.execute({ todos: [todo('跑测试', 'in_progress')] }, ctx())
    expect(r.isError).toBeFalsy()
  })

  it('零项 in_progress 也是合法的(全 pending 或全 completed)', async () => {
    const a = await todoWriteTool.execute({ todos: [todo('跑测试', 'pending')] }, ctx())
    expect(a.isError).toBeFalsy()
    const b = await todoWriteTool.execute({ todos: [todo('跑测试', 'completed')] }, ctx())
    expect(b.isError).toBeFalsy()
  })

  it('★ 两项 in_progress 被拒,且拒绝信息里点名是哪几项', async () => {
    const r = await todoWriteTool.execute(
      {
        todos: [
          todo('改代码', 'in_progress'),
          todo('跑测试', 'in_progress'),
          todo('提交', 'pending')
        ]
      },
      ctx()
    )
    expect(r.isError).toBe(true)
    expect(r.output.content).toContain('At most one')
    expect(r.output.content).toContain('you sent 2')
    // ★ 点名 —— 只说「有多个」的话模型得自己回去数,而它多半会重发一份同样的清单
    expect(r.output.content).toContain('改代码')
    expect(r.output.content).toContain('跑测试')
    expect(r.output.content).not.toContain('提交')
    // 给出改法,不是只说不行
    expect(r.output.content).toContain('pending')
  })
})

describe('TodoWrite · schema', () => {
  it(`超过 ${String(TODO_LIMITS.MAX_TODOS)} 项被 schema 挡回`, async () => {
    const todos = Array.from({ length: TODO_LIMITS.MAX_TODOS + 1 }, (_, i) =>
      todo(`第 ${String(i)} 步`, 'pending')
    )
    const r = await todoWriteTool.execute({ todos }, ctx())
    expect(r.isError).toBe(true)
    expect(r.output.content).toContain('Invalid arguments')
  })

  it(`正好 ${String(TODO_LIMITS.MAX_TODOS)} 项可以过`, async () => {
    const todos = Array.from({ length: TODO_LIMITS.MAX_TODOS }, (_, i) =>
      todo(`第 ${String(i)} 步`, 'pending')
    )
    const r = await todoWriteTool.execute({ todos }, ctx())
    expect(r.isError).toBeFalsy()
  })

  it('认不出的 status 被挡回', async () => {
    const r = await todoWriteTool.execute(
      { todos: [{ content: 'x', status: 'doing', activeForm: 'y' }] },
      ctx()
    )
    expect(r.isError).toBe(true)
    expect(r.output.content).toContain('Invalid arguments')
  })

  it('★ activeForm 是必填 —— 缺了界面上那行「正在……」就没东西可显示', async () => {
    const r = await todoWriteTool.execute(
      { todos: [{ content: 'x', status: 'pending' }] },
      ctx()
    )
    expect(r.isError).toBe(true)
    expect(r.output.content).toContain('Invalid arguments')
  })

  it('空的 content 被挡回', async () => {
    const r = await todoWriteTool.execute(
      { todos: [{ content: '', status: 'pending', activeForm: 'x' }] },
      ctx()
    )
    expect(r.isError).toBe(true)
  })

  it('todos 完全缺失时被挡回,不是当成空清单', async () => {
    const r = await todoWriteTool.execute({}, ctx())
    expect(r.isError).toBe(true)
    expect(r.output.content).toContain('Invalid arguments')
  })
})

describe('TodoWrite · 回显', () => {
  it('三种状态各有各的记号,顺序按模型给的来', async () => {
    const r = await todoWriteTool.execute(
      {
        todos: [
          todo('读代码', 'completed'),
          todo('改代码', 'in_progress'),
          todo('跑测试', 'pending')
        ]
      },
      ctx()
    )
    const lines = r.output.content.split('\n')
    expect(lines).toContain('[x] 读代码')
    expect(lines).toContain('[~] 改代码')
    expect(lines).toContain('[ ] 跑测试')
    expect(lines.indexOf('[x] 读代码')).toBeLessThan(lines.indexOf('[~] 改代码'))
  })

  it('完成计数是 完成数/总数', async () => {
    const r = await todoWriteTool.execute(
      {
        todos: [
          todo('a', 'completed'),
          todo('b', 'completed'),
          todo('c', 'in_progress'),
          todo('d', 'pending')
        ]
      },
      ctx()
    )
    expect(r.output.content).toContain('2/4 completed')
  })

  /** ★ 不催一句的话,模型会把一整轮的事做完再一次性全标 completed —— 界面上就是长时间没动静 */
  it('★ 成功回显里催一句「做完一项就立刻标掉」', async () => {
    const r = await todoWriteTool.execute({ todos: [todo('a', 'pending')] }, ctx())
    expect(r.output.content).toContain('As soon as an item is done')
  })

  it('activeForm 不进回显 —— 它是给界面用的,不是给模型看的', async () => {
    const r = await todoWriteTool.execute({ todos: [todo('跑测试', 'pending')] }, ctx())
    expect(r.output.content).toContain('跑测试')
    expect(r.output.content).not.toContain('正在跑测试')
  })

  /**
   * 需求:全绿与「还有没做完的」必须是**两条**回执。
   *
   * 一份已经全绿的清单上再催「做完一项就标掉」没有对象可指,只会把模型推回循环 ——
   * 它反复重发同一份清单,用户看着界面一直亮着「正在……」,而每次重发都在烧一遍上下文。
   * 所以全绿这条必须**明确说完成**,并且只把「用户请求已完全满足」当作可以收尾的条件。
   */
  it('全完成时明确说「清单上的事都做完了」,并且不再催继续', async () => {
    const r = await todoWriteTool.execute(
      { todos: [todo('a', 'completed'), todo('b', 'completed')] },
      ctx()
    )
    expect(r.isError).toBeFalsy()
    expect(r.output.content).toContain('All listed tasks are complete')
    expect(r.output.content).toContain('fully addressed')
    expect(r.output.content).not.toContain('As soon as an item is done')
  })

  /**
   * 需求:没做完时不能默认放行,也**不能**逼着全绿 —— 受阻/未验证的项本来就该留在
   * 清单上。所以这条回执要同时做到两件相反的事:保留逐项提醒,并要求收尾前核对。
   *
   * 不满足会怎样:要么模型把受阻项勾成 completed 来「清空清单」(界面上的进度变成
   * 谎话),要么它在还没做完时就直接交最终答复(用户以为干完了)。
   */
  it('未完成时保留逐项提醒,并要求收尾前核对,但不强迫全绿', async () => {
    const r = await todoWriteTool.execute(
      { todos: [todo('a', 'completed'), todo('b', 'pending')] },
      ctx()
    )
    expect(r.output.content).toContain('As soon as an item is done')
    expect(r.output.content).toContain('check it against what actually happened')
    expect(r.output.content).toContain('stay unfinished')
    expect(r.output.content).not.toContain('All listed tasks are complete')
  })
})

describe('TodoWrite · 回执里的增量', () => {
  /**
   * 需求:模型每轮发的是**完整清单**,它看不见自己改了什么 —— 尤其是**丢掉一项**:
   * 前后两份各自都自洽,而用户看到的是任务凭空消失。回执里那次「上次→这次」的
   * 回显是它收尾前核对清单的依据,所以这里钉的是「上一份从哪儿来」与「丢项怎么点名」。
   */
  it('★ 上一次那份来自本 run 的转录(ctx.messages),据此报出改了什么', async () => {
    const before = assistantMessage('a-before', [
      { type: 'tool_call', callId: 'before', name: 'TodoWrite', input: { todos: [todo('读代码', 'pending')] } }
    ], 0)
    const beforeResult = toolResultMessage('r-before', [
      { type: 'tool_result', callId: 'before', output: { content: 'ok' }, isError: false }
    ], 0)

    const r = await todoWriteTool.execute(
      { todos: [todo('读代码', 'completed'), todo('跑测试', 'pending')] },
      { ...ctx(), messages: [before, beforeResult], todoToolName: 'TodoWrite' }
    )

    expect(r.output.content).toContain('1 completed · 1 added')
  })

  it('★ 丢掉的项点名回显 —— 否则「任务凭空消失」只能靠用户自己发现', async () => {
    const before = assistantMessage('a-before', [
      { type: 'tool_call', callId: 'before', name: 'TodoWrite', input: { todos: [todo('读代码', 'pending'), todo('跑测试', 'pending')] } }
    ], 0)
    const beforeResult = toolResultMessage('r-before', [
      { type: 'tool_result', callId: 'before', output: { content: 'ok' }, isError: false }
    ], 0)

    const r = await todoWriteTool.execute(
      { todos: [todo('读代码', 'pending')] },
      { ...ctx(), messages: [before, beforeResult], todoToolName: 'TodoWrite' }
    )

    expect(r.output.content).toContain('1 dropped')
    expect(r.output.content).toContain('You dropped 1 item(s)')
    expect(r.output.content).toContain('跑测试')
  })

  it('没有转录 / 没有工具名时只说计数,不编造增量', async () => {
    const r = await todoWriteTool.execute({ todos: [todo('a', 'pending')] }, ctx())

    expect(r.output.content).toContain('0/1 completed')
    expect(r.output.content).not.toContain('dropped')
    expect(r.output.content).not.toContain('added')
  })

  it('★ 工具名对不上时不误判成「全丢了」—— 撞名后缀会让它一条都取不到', async () => {
    const before = assistantMessage('a-before', [
      { type: 'tool_call', callId: 'before', name: 'TodoWrite_a1b2c3d4', input: { todos: [todo('读代码', 'pending')] } }
    ], 0)
    const beforeResult = toolResultMessage('r-before', [
      { type: 'tool_result', callId: 'before', output: { content: 'ok' }, isError: false }
    ], 0)

    const r = await todoWriteTool.execute(
      { todos: [todo('读代码', 'pending')] },
      { ...ctx(), messages: [before, beforeResult], todoToolName: 'TodoWrite' }
    )

    expect(r.output.content).not.toContain('dropped')
  })
})

describe('TodoWrite · 无状态', () => {
  /**
   * ★ 这一条钉的是文件头那段设计说明。
   *
   * 转录本身就是那份状态:每一轮都会把这次调用的 `tool_use`(连同 `todos` 入参)
   * 重放给模型。这里再存一份的话,那一份会在中断/重试/编辑历史之后和转录分叉,
   * 于是就有了一份谁也不看、却会显示在界面上的「幽灵清单」。
   */
  it('★ 第二次调用完全不知道第一次发生过', async () => {
    await todoWriteTool.execute({ todos: [todo('第一批', 'completed')] }, ctx())
    const r = await todoWriteTool.execute({ todos: [todo('第二批', 'pending')] }, ctx())
    expect(r.output.content).toContain('第二批')
    expect(r.output.content).not.toContain('第一批')
    expect(r.output.content).toContain('0/1 completed')
  })
})

describe('TodoWrite · 描述里那三条硬规则', () => {
  const d = todoWriteTool.description

  it('★ 说清了要发全量 —— 不说的话模型会发增量,清单每轮都被截短', () => {
    expect(d).toContain('send the complete list')
  })

  it('★ 说清了同一时刻至多一项 in_progress —— 和上面那条校验是一对', () => {
    expect(d).toContain('AT MOST ONE item may be in_progress')
  })

  // 需求：进度更新发生在阶段切换处，不能先做下一项再把完成状态一次性补上。
  it('要求读完结果后立即更新，且先于下一项开始', () => {
    expect(d).toContain('read the result you just got')
    expect(d).toContain('before starting the next item')
    expect(d).toContain('Send progress updates separately from work or verification calls')
    expect(d).toContain('wait for a successful receipt')
  })

  /**
   * 需求:这条是上面 `AT MOST ONE` 的另一半 —— 只写上界的话,模型会读成「必须
   * 一直有一项在动」,于是受阻那一轮把一项其实没在动的勾成进行中,界面上的
   * 「正在……」就成了谎话。零项必须是明说合法的一档。
   */
  it('★ 说清了零项 in_progress 合法(做完了,或剩下的都在等)', () => {
    expect(d).toContain('Zero items in_progress is correct')
    expect(d).toContain('not started or waiting')
  })

  it('★ 说清了全部完成时不许留 in_progress —— 收尾前要再核对一遍全量清单', () => {
    expect(d).toContain('no item may be left in_progress')
    expect(d).toContain('check it against what actually happened')
    // 需求：已经准确同步的清单可以直接收尾，不能为了“最后一次”不停重发。
    expect(d).toContain('An unchanged, accurate list needs no duplicate call')
  })

  /**
   * 需求:模型最容易绕开清单的一招是**在正文里宣布完成** —— 它说「都做完了」,
   * 而用户看着的那份清单还停在半路,回执与界面长期对不上,且全程零报错。
   * 所以描述里必须明说:文字不是一次工具调用的替代品。
   */
  it('★ 说清了文字完成声明不能代替工具调用,也不能与在跑的验证并行标完成', () => {
    expect(d).toContain('is not a substitute for the tool call')
    expect(d).toContain('still in flight')
  })

  /** ★ 受阻/失败/未验证的项保持未完成,而且原因要写进它自己那一条里 */
  it('★ 说清了受阻/失败/未验证的项保持未完成,并注明原因', () => {
    expect(d).toContain('blocked, failed, or not verified yet')
  })

  it('★ 说清了没验证过不许标 completed', () => {
    expect(d).toContain('done AND verified')
    expect(d).toContain('completed')
  })

  it('也说清了什么时候不该用它 —— 否则回答一个问题也要先列清单', () => {
    expect(d).toContain('When NOT to use it')
  })
})
