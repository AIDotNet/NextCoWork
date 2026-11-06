/**
 * `TodoWrite` —— 模型自己的任务清单。
 *
 * ★ **完全无状态,这是它最反直觉也最重要的一点。**
 *
 * 直觉上这个工具该往某个 store 里写一份清单,下一轮再读回来。不需要:
 * 每一轮请求都会把**整条转录**重放给模型,而这次调用的 `tool_use`(连同它的
 * `todos` 入参)就在转录里 —— **转录本身就是那份状态**。
 *
 * 真去存一份的坏处是具体的:那份存下来的清单和转录里的会分叉(中断、重试、
 * 编辑历史消息之后),而模型看见的永远是转录里的那份。于是就有了一份
 * 谁也不看、却会在界面上显示的「幽灵清单」。
 *
 * 所以这里只做两件事:**校验**(把会让清单失去意义的形状挡回去),
 * 和**回显**(让模型确认它写下的东西被收到了)。
 *
 * ★ 清单的**形状**(`TodoItem`)、渲染标记(`MARK`)与从转录反推清单的纯函数
 * (`narrowTodos` / `latestTodosFrom` / `todoPlan`)住在 `shared/agent/todo.ts` ——
 * 渲染层也要用它们(消息里那张卡片要标出这次更新改了什么),而渲染层 import
 * `src/main/**` 是禁止的(AGENTS.md §1)。本文件只剩工具自己的入参 schema 与文案。
 */
import { z } from 'zod'
import { toolFail, toolOk } from '../../../../shared/agent/tool'
import { MARK, MAX_TODOS, latestTodosFrom, narrowTodos, todoPlan } from '../../../../shared/agent/todo'
import { defineTool } from '../define'
import type { ToolRegistration } from '../registry'

const TodoItemSchema = z.object({
  content: z.string().min(1).max(500).describe('The task, in imperative form, e.g. "Run the tests"'),
  status: z.enum(['pending', 'in_progress', 'completed']).describe('The task state'),
  activeForm: z
    .string()
    .min(1)
    .max(500)
    .describe('The present continuous form, e.g. "Running the tests" — this is what the UI shows while it is in progress')
})

const TodoWriteInput = z.object({
  todos: z
    .array(TodoItemSchema)
    .max(MAX_TODOS)
    .describe('The complete task list. ALWAYS send the WHOLE list, never a delta')
})

export const todoWriteTool: ToolRegistration = defineTool({
  internalId: 'TodoWrite',
  /*
    需求:承诺给模型的是「至多一项 in_progress」,不是「恰好一项」。

    两者在**受阻**时不是一回事:剩下的项全都停着不动(等用户答话、等一个失败的外部
    调用),这时逼出「恰好一项」只会让模型把一项其实没在动的勾成进行中 ——
    界面上的「正在……」就变成谎话,而清单也就不再是进度的真值。
    这条与 `run` 里那道 `active.length > 1` 的校验是同一个口径,改一处必须改另一处。

    需求:更新要独立等到成功回执再继续。工具结果按整批提交,与长任务并行发出的话,
    即使 TodoWrite 自己已经结束,输入框上方的清单也得等那项长任务结束后才会刷新。
  */
  description:
    'Use this tool to manage a task list for the work in front of you. It helps you break the work up, ' +
    'keeps track of where you are, and shows the user what you are doing.\n\n' +
    'When to use it:\n' +
    '- The task takes three or more steps, or touches several files\n' +
    '- The user gave you several things at once (numbered, or comma-separated)\n' +
    '- The task is non-trivial and the order matters\n' +
    '- As soon as you receive the request: write the list first, then start\n' +
    '- Before starting an item: mark it in_progress\n' +
    '- After each item finishes: read the result you just got, then update the list IMMEDIATELY, ' +
    'before starting the next item. Do NOT batch completions up\n\n' +
    'When NOT to use it:\n' +
    '- A single, straightforward step\n' +
    '- Purely answering a question or explaining some code\n' +
    '- Anything you can finish in a couple of actions — here the list is just noise\n\n' +
    'How to use it:\n' +
    '- ALWAYS send the complete list, not just the entries that changed. What you send IS the list\n' +
    '- Send progress updates separately from work or verification calls, and wait for a successful ' +
    'receipt before continuing. A rejected update leaves the previous list visible\n' +
    '- There are exactly three states: pending (not started or waiting on something), in_progress ' +
    '(actively working on it now), completed (done and verified)\n' +
    '- IMPORTANT: AT MOST ONE item may be in_progress at a time, and while you are actually working ' +
    'there should be exactly one. Zero items in_progress is correct when nothing is left to do or when ' +
    'every remaining item is blocked. Marking everything in progress is the same as having no plan, ' +
    'and the user sees "it is doing five things at once"\n' +
    '- Before your final answer: check it against what actually happened and send the complete list ' +
    'if any status or blocker needs updating. An unchanged, accurate list needs no duplicate call. ' +
    'When every listed task is complete, no item may be left in_progress\n' +
    '- A text reply is not a substitute for the tool call: never say an item is done without sending ' +
    'the update, and never mark an item completed while the work or the verification for it is still ' +
    'in flight\n' +
    '- content is imperative ("Run the tests"), activeForm is present continuous ("Running the tests"). ' +
    'Both are required\n' +
    '- Mark completed ONLY when the work is actually done AND verified. If tests are still failing or the ' +
    'implementation is half-finished, leave the affected item incomplete and describe what is left. ' +
    'Use in_progress only while actively working on it; otherwise use pending\n' +
    '- NEVER mark something completed because you got stuck: leave it pending or in_progress and put the ' +
    'reason — blocked, failed, or not verified yet — in that entry',
  schema: TodoWriteInput,
  /*
    ★ readOnly。它不碰磁盘、不碰网络,而 plan 模式(`readOnlyOnly` 快照)
    恰恰是最需要列清单的时候 —— 标成非只读,计划模式下模型就没法写计划了。
  */
  readOnly: true,
  destructive: false,
  needsNetwork: false,
  /*
    契约要求返回 Promise —— `async` 只是为了满足它,这个工具本身没有任何异步的事要做。
    ★ 本 run 的转录从 `ctx.messages` 来,给不出时(纯内核测试、还没有转录)
    `latestTodosFrom` 返回 undefined,回执就只说计数、不编造差异。
  */
  async run(input, ctx) {
    const { todos } = input

    if (todos.length === 0) {
      return toolFail(
        'The task list is empty. Either send at least one task, or do not call this tool at all — ' +
          'an empty list wipes out the plan the user can already see.'
      )
    }

    /*
      ★ 这条校验是有实际作用的,不是洁癖:多个 in_progress 时,界面上那行
      「正在……」不知道该显示哪一条,而模型也就失去了「我现在在做哪一步」这个锚点。
      拒绝并说清楚,比默默接受一份没有意义的清单要好。
      (上界是 1 而不是「必须等于 1」:整份清单都不活跃是合法的,见 description 里那段。)
    */
    const active = todos.filter((t) => t.status === 'in_progress')
    if (active.length > 1) {
      return toolFail(
        `At most one item may be in_progress at a time, and you sent ${String(active.length)}: ` +
          `${active.map((t) => `"${t.content}"`).join(', ')}. ` +
          `Keep only the one you are ACTUALLY working on right now, and set the rest back to pending.`
      )
    }

    const done = todos.filter((t) => t.status === 'completed').length
    const body = todos.map((t) => `${MARK[t.status]} ${t.content}`).join('\n')

    /*
      需求:回执要**顺便告诉模型这次更新动了什么**。

      模型每轮发的是完整清单,它看不见自己改了什么 —— 尤其是**丢掉一项**:
      前后两份各自都自洽,用户看到的却是任务凭空消失。这里把它自己造成的差异
      回显一次,是它在收尾前核对清单的依据。
      ★ 上一份从**本 run 的转录**里取(`ctx.messages`),不新存一份状态:
      `todo.ts` 文件头那条「完全无状态」不动。取不到(第一次写、历史被压缩)
      就只说计数,不编造差异。
    */
    const previous = latestTodosFrom(ctx.messages ?? [], ctx.todoToolName ?? '')
    const plan = todoPlan(narrowTodos({ todos }) ?? [], previous)
    const changes: string[] = []
    if (plan.newlyCompleted > 0) changes.push(`${String(plan.newlyCompleted)} completed`)
    if (plan.newlyStarted > 0) changes.push(`${String(plan.newlyStarted)} started`)
    if (plan.added.length > 0) changes.push(`${String(plan.added.length)} added`)
    if (plan.removed.length > 0) changes.push(`${String(plan.removed.length)} dropped`)
    const header =
      `Todo list updated (${String(done)}/${String(todos.length)} completed` +
      `${changes.length === 0 ? '' : ` · ${changes.join(' · ')}`}):`

    /*
      需求:清单全绿和「还有没做完的」必须给出两条不同的下一步 —— 一份已经全绿的清单上
      再催「做完一项就标掉」没有对象可指,只会把模型推回循环:它反复重发同一份清单,
      而用户看着界面一直亮着「正在……」。
      反过来,没做完时**不能**默认放行 —— 所以下面那条照旧逐项提醒,只额外要求它
      收尾前核对一遍,并不强迫全绿(受阻项本来就该留在清单上)。
      ★ 这三句逐项提醒**不能**因为「description 里已经写过」就删掉:它们钉在
      `todo.test.ts` 里,而回执是模型真正读到最后一次的地方 —— 描述可能在长对话里
      被压缩掉,回执不会。
    */
    const dropped = plan.removed.length === 0
      ? ''
      : `\nYou dropped ${String(plan.removed.length)} item(s) that were on the previous list: ` +
        `${plan.removed.map((t) => `"${t.content}"`).join(', ')}. ` +
        `If that was not deliberate, put them back or say why they are gone.`

    if (done === todos.length) {
      return toolOk(
        `${header}\n${body}${dropped}\n\n` +
          `All listed tasks are complete. If the user's request is now fully addressed, give the final ` +
          `answer and stop — do not keep calling this tool on a finished list. If it is not, keep the ` +
          `list honest by adding what is left.`
      )
    }

    return toolOk(
      `${header}\n${body}${dropped}\n\n` +
        `Keep working through this list. As soon as an item is done, call this tool again to mark it — ` +
        `do NOT wait and mark them all at the end. Before your final answer, check it against what ` +
        `actually happened and send a complete update if needed; an unchanged, accurate list needs no ` +
        `duplicate call. Items that are blocked, failed, or not verified yet stay unfinished, with the ` +
        `reason in their own entry.`
    )
  }
})

export const TODO_LIMITS = { MAX_TODOS } as const
