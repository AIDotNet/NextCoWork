/**
 * 任务清单(`TodoWrite`)的**形状、渲染标记与派生逻辑** —— 全仓唯一一处真源。
 *
 * 需求:同一份清单同时出现在三处 —— 内核工具自己的入参与回执、上下文提醒
 * (`context-assembler` / `todo-reconciliation`)、以及界面上那两张清单卡片
 * (输入框上方那条 + 消息里那张)。三处都要回答同样三个问题:
 * 「一份清单长什么样」「最近一次**成功**写下的清单是哪一份」「这次更新改了什么」。
 *
 * 三个答案各写一份的代价是具体的,而且每一种都**静默**:撞名后的 externalName、
 * 被工具拒绝过的那次调用、流式中的半截条目 —— 判据漏掉任何一条,界面上都是一份
 * 看起来完全正常的错清单。所以这里只放纯函数:入参一律 `unknown`(来源是模型),
 * 收窄规则写在各自函数的注释里。
 *
 * 从 `main/kernel/tool/builtin/todo.ts` 搬来这里,是因为渲染层也要用它
 * (`views/chat/todo-history.tsx` 要找「上一次那份」来算增量),而渲染层 import
 * `src/main/**` 是明令禁止的(AGENTS.md §1)。搬运没有改动任何判据。
 *
 * 不碰 zod:zod 只用来生成下发给上游的 schema,住在那份工具定义里。
 */
import type { AgentMessage } from './message'

export type TodoStatus = 'pending' | 'in_progress' | 'completed'

/**
 * 一条待办。
 *
 * ★ 这个形状是**三处共同承诺**的:内核 schema 的产物、转录里的 `tool_use` 入参、
 * 界面上的清单行。所以它既不是 zod 的推导产物,也不是某个组件的私有类型。
 */
export interface TodoItem {
  content: string
  status: TodoStatus
  activeForm: string
}

/** 再多就不是「计划」而是「清单癖」了 —— 而且每一轮都要重发一遍 */
export const MAX_TODOS = 40

/**
 * 清单的渲染标记。
 *
 * ★ 上下文提醒里那份快照和工具自己的回显必须长得一样,否则模型会以为那是两份
 * 不同的清单。
 */
export const MARK: Record<TodoStatus, string> = {
  completed: '[x]',
  in_progress: '[~]',
  pending: '[ ]'
}

const STATUSES = new Set<string>(['pending', 'in_progress', 'completed'])

/**
 * 入参 → 清单,**严格**收窄。三个判据缺一不可,缺了就当这份入参不是一份清单:
 *
 * - 不是对象 / `todos` 不是数组 → `undefined`;
 * - 单个条目缺 `content` / `activeForm`,或 `status` 不是三档之一 → **跳过这一条**
 *   (半份清单好过没有,但补不出一个字段:编出来的 `activeForm` 会变成界面上
 *   一行看似真实的「正在……」);
 * - 一条都不剩 → `undefined`,不是空数组 —— 调用方据此区分「清单是空的」与
 *   「这里根本没有清单」。
 *
 * 与 `views/chat/todo-preview.ts` 的宽松收窄**刻意不同**:那个跑在流式中的半截
 * JSON 上,缺 `status` 按 `pending` 算是为了「边写边画」;这里跑在**已经跑完**的
 * 调用上,缺字段只可能是模型写错了,那就该按没有清单处理。
 */
export function narrowTodos(input: unknown): readonly TodoItem[] | undefined {
  if (typeof input !== 'object' || input === null) return undefined
  const raw: unknown = (input as { todos?: unknown }).todos
  if (!Array.isArray(raw)) return undefined

  const out: TodoItem[] = []
  for (const item of raw.slice(0, MAX_TODOS)) {
    if (typeof item !== 'object' || item === null) continue
    const { content, status, activeForm } = item as Record<string, unknown>
    if (typeof content !== 'string' || typeof activeForm !== 'string') continue
    if (typeof status !== 'string' || !STATUSES.has(status)) continue
    out.push({ content, status: status as TodoStatus, activeForm })
  }
  return out.length > 0 ? out : undefined
}

/**
 * 转录 → 两张查询表。`latestTodosFrom` 与 `todoHistoryLookup` 都是它的投影,
 * 共用一次遍历与同一套「哪次调用算数」的判据。
 *
 * ★ **必须确认那次调用真的成功了。** `agent-session.ts` 是**先** commit
 * `tool_call`、**后**在 `executeAll` → `defineTool` → `safeParse` 里校验的,
 * 所以被工具明确拒绝过的清单(两个 in_progress、空数组、坏 JSON)也原样
 * 躺在转录里。盲取最近一条 = 把工具拒绝过的东西当成当前进度渲染出去。
 * 判据是配对的 `tool_result` 存在**且** `isError === false`。
 */
interface TodoIndex {
  /** 每个工具名最后一次成功写下的清单 */
  latest: Map<string, readonly TodoItem[]>
  /** 每次调用**之前**那一份(同名工具上一次成功的清单) */
  before: Map<string, readonly TodoItem[]>
}

function indexTodos(messages: readonly AgentMessage[]): TodoIndex {
  // 先把 callId → 成功与否收成一张表,省得对每个候选再正向扫一遍
  const ok = new Map<string, boolean>()
  for (const m of messages) {
    for (const p of m.parts) {
      if (p.type === 'tool_result') ok.set(p.callId, !p.isError)
    }
  }

  const latest = new Map<string, readonly TodoItem[]>()
  const before = new Map<string, readonly TodoItem[]>()
  for (const m of messages) {
    for (const p of m.parts) {
      if (p.type !== 'tool_call') continue
      // ★ 先记「这次之前是什么」再决定要不要采纳本次:本次的结果此刻可能还没提交,
      //   就算已提交,它也不该出现在自己的「之前」里。
      const known = latest.get(p.name)
      if (known !== undefined) before.set(p.callId, known)
      // 还没跑完的那次不算(没有配对结果),被拒绝的那次也不算
      if (ok.get(p.callId) !== true) continue
      const todos = narrowTodos(p.input)
      if (todos !== undefined) latest.set(p.name, todos)
    }
  }
  return { latest, before }
}

/**
 * 把「当前的待办清单」从转录里读回来。
 *
 * ★ 这**不违反** `todo.ts` 文件头那条无状态设计,反而是它的直接推论:当前清单
 * 已经在转录里了 —— 就是最近一次成功的 `TodoWrite` 调用的入参。所以这仍然是一个
 * **对转录的纯函数**,唯一真相源没有变,分叉不可能发生。
 *
 * 调用方(`context-assembler.ts`)拿它做的事,只是把已经存在的事实
 * **搬到离生成点更近的地方** —— 二十轮之前的那条 `tool_call`,模型翻不动了。
 *
 * ## 两处会踩空的地方
 *
 * 1. ★ **`toolName` 是 `externalName`,要从注册表查**(`byInternalId('TodoWrite')`),
 *    不能写字面量、也不能从本轮的 `advertised` 快照里取。转录里存的是
 *    `ToolNamer` 分配的外部名,撞名时会带 8 位哈希后缀;而 `advertised`
 *    被 `readOnlyOnly` / `allowList` / `network` 过滤过,一个 `tools:` 写得窄的
 *    子代理会因此看不见**它自己刚写的**清单。两种错都是**静默**的。
 *    (渲染层没有 namer,所以它走 `todoHistoryLookup` —— 那个不需要知道名字。)
 * 2. ★ **入参是 `unknown`,来源是模型** —— 收窄在 `narrowTodos` 里做,不 throw。
 *
 * 压缩吃不掉它:`compactPart()` 只动 `tool_result` / `thinking` / `image`,
 * `tool_call` 走 `default: return p` 原样保留。
 */
export function latestTodosFrom(
  messages: readonly AgentMessage[],
  toolName: string
): readonly TodoItem[] | undefined {
  return indexTodos(messages).latest.get(toolName)
}

/**
 * 「这次调用**之前**那份清单」的查询表 —— 用来算增量。
 *
 * 需求:模型每轮发的是**完整清单**(工具契约如此),所以「它悄悄丢掉了一项」
 * 在界面上完全看不出来:前后两份清单各自都是自洽的。要标出变化,就必须知道
 * 上一份是什么。
 *
 * ★ 与 `latestTodosFrom` 的区别是**它不需要工具名**。渲染层拿不到 `ToolNamer`
 * (那是主进程的),但转录里每次调用自己带着名字 —— 按名字分组就够了,
 * 于是撞名后缀这件事在这一侧根本不存在。
 *
 * ★ 返回 `undefined` 的含义是「**不知道**」,不是「空的」:老转录里第一次调用
 * 之前也可能有过清单(被压缩掉了),所以调用方在 `undefined` 时不做增量标注,
 * 而不是把整份清单标成「新增」。
 */
export function todoHistoryLookup(
  messages: readonly AgentMessage[]
): (callId: string) => readonly TodoItem[] | undefined {
  const { before } = indexTodos(messages)
  return (callId) => before.get(callId)
}

export interface TodoPlan {
  /** 本次清单里有、上一份里没有的条目 */
  added: readonly TodoItem[]
  /** 上一份里有、本次清单里没有的条目 */
  removed: readonly TodoItem[]
  /** 本次**变成** completed 的条数(新增且已完成也算) */
  newlyCompleted: number
  /** 本次**变成** in_progress 的条数(新增且进行中也算) */
  newlyStarted: number
}

/**
 * 这次更新改了什么。
 *
 * 配对按 `content`(清单项没有 id):同名的项按出现顺序一对一消耗,所以重复内容
 * 不会被算成一次新增加一次移除。顺序变化、`activeForm` 的措辞变化都不算变化 ——
 * 它们不影响清单说了什么。
 *
 * ★ `previous === undefined` 时四个字段全空,即「不标增量」而不是「整份都是新增」。
 *   后者是错的,而错的增量比没有增量更糟:用户会以为模型刚刚凭空加了五项。
 */
export function todoPlan(
  todos: readonly TodoItem[],
  previous: readonly TodoItem[] | undefined
): TodoPlan {
  if (previous === undefined) return { added: [], removed: [], newlyCompleted: 0, newlyStarted: 0 }

  const remaining = [...previous]
  const added: TodoItem[] = []
  let newlyCompleted = 0
  let newlyStarted = 0
  for (const item of todos) {
    const at = remaining.findIndex((p) => p.content === item.content)
    const before = at < 0 ? undefined : remaining.splice(at, 1)[0]
    if (before === undefined) added.push(item)
    if ((before === undefined || before.status !== item.status) && item.status === 'completed') {
      newlyCompleted += 1
    }
    if ((before === undefined || before.status !== item.status) && item.status === 'in_progress') {
      newlyStarted += 1
    }
  }
  return { added, removed: remaining, newlyCompleted, newlyStarted }
}
