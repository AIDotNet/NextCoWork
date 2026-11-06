/**
 * `visualize_show_widget` / `visualize_read_me` —— 模型直接产出可视化内容的那条路。
 *
 * 需求:模型讲"复利是怎么滚起来的""这个 transformer 的结构"时,一行行文字是
 * 表达力最差的形式。这里给它一次**工具调用**,HTML/SVG 作为参数 payload 传进
 * 来,由渲染层在对话流里内联渲染出来 —— 不是图片,不是代码块,是一个真在跑的
 * HTML 应用(滑块能动、图表能画、脚本会执行)。
 *
 * 两个工具是**配套的一对**,不是两个独立功能:
 * - `visualize_read_me` 按需返回设计规范(7 万字,按模块切);
 * - `visualize_show_widget` 收代码。它的 description 里明确要求先读规范。
 *
 * ── 几条刻意的设计决定 ──
 *
 * 1. **没有 `i_have_seen_read_me` 这个参数。** Claude 早期实现里有它(一个
 *    "先读过文档"的布尔门闩),但泄漏出来的真实 schema 里只有下面这三个字段,
 *    Claude Code 报出来的原始调用记录也没有第四项。要求写在 description 里就够了 ——
 *    硬门闩的代价是模型偶尔会被自己的一个误写参数卡住整整一轮,而收益只是
 *    "它少漏读一次文档"。
 * 2. **`needsNetwork: true`,尽管这个工具自己一个字节都不出网。**
 *    它产出的是**会出网的东西**:widget 里 `<script src="https://cdn...">` 拉
 *    Chart.js 是这套规范推荐的常规做法(见 `visualize-guidelines/charts.ts`)。
 *    申报 false 的后果是用户关掉「联网」之后,仍然有一条从这里出网的路 ——
 *    正是 `permission-gate.ts` 里那句"别用 curl 绕过开关"要堵的同一件事。
 *    **代价要说清:开关关掉时连纯 SVG 图也做不了。** 这是刻意的取舍(宁可整个
 *    功能不可用,也不要一次静默出网)。若产品决定要放开,正确的做法是按
 *    `widget_code` 里有没有外链把它拆成两个工具,而不是把这个字段改回 false。
 * 3. **`readOnly: true`。** 它不碰工作区、不写盘、不改任何状态;产出物只挂在这条
 *    消息上。三档权限都直接放行,用户不会为每次画图点一次窗(network 那条闸
 *    排在只读之前,仍然管得住它)。
 * 4. **没有 `sendPrompt()`,但有几条更窄的通道。** Claude 的 widget 里有一个"像用户
 *    打字一样往对话里发一条消息"的全局函数。本宿主不给它:widget 是模型生成的、
 *    还可能是它从某个网页里读来的内容,一个能自己开新一轮的按钮就是一条注入通道。
 *    代替它的是(见 `WidgetFrame.tsx` 的 `handleCall`):
 *    - `draft()` 只把文字填进输入框,按不按回车由用户决定;
 *    - `submit()` 只在**模型主动发起**的交互模式里可用(`interactive: true`),
 *      值作为**这一次**工具调用的结果回去,开不了新的一轮;
 *    - `runAction()` 只能触发**调用时就声明好**的动作,工具与入参在这里定死、经过
 *      权限闸,widget 只能选 id 和受约束的参数。
 *
 * 5. **TSX 在这里编译,不在 iframe 里。** 理由见 `widget-compile.ts`:编译错误要能
 *    在同一轮回到模型手上。
 */
import { z } from 'zod'
import { MAX_TOOL_OUTPUT_CHARS } from '../../../../shared/agent/message'
import { toolFail, toolOk, type ToolResult } from '../../../../shared/agent/tool'
import type { ToolCard } from '../../../../shared/agent/tool-card'
import {
  WIDGET_LIMITS,
  WIDGET_PARAM_PATTERN,
  type WidgetAction,
  type WidgetFormat
} from '../../../../shared/domain/widget'
import { defineTool } from '../define'
import type { ToolContext } from '../registry'
import { compileWidgetTsx, inferWidgetFormat } from './widget-compile'
import { AVAILABLE_MODULES, getGuidelines } from './visualize-guidelines'

/**
 * 返回给模型的那句话。
 *
 * ★ 最后一句是**必须的**,不是客套:模型天然倾向于把刚画出来的东西再用文字
 * 复述一遍(那是它训练里"解释清楚"的默认动作),而那样用户会在同一屏上看到
 * 同一份信息两遍 —— 一遍是图,一遍是图的文字版。Claude 的实现里也钉着这句。
 */
const RENDERED_NOTE =
  'Content rendered and shown to the user. Do not duplicate the shown content in text — it is already ' +
  'visually represented. Keep any remaining explanation in your normal reply text.'

/**
 * `widget_code` 的长度上限(字符)。128K 约等于一份很大的 SVG 图或一个带
 * 内联数据的仪表盘。
 *
 * ★ 上限存在的理由是**落盘**:这张卡跟着 `output.card` 进转录
 * (`repo.ts` 直接 `JSON.stringify(parts)`),没有上限的话一次失控的生成
 * 会往 SQLite 里写进去多少就得跟着读回来多少。超了就给模型一句明确的
 * 失败(它自己会知道要拆小),而不是静默截断一张画不出来的图。
 */
const MAX_WIDGET_CODE_CHARS = 128 * 1024

/**
 * 读取设计规范的工具。
 *
 * ★ 返回值**原样**是规范正文,不加任何我们自己的前后缀 —— 那段正文里
 * 满是"见上"/"见下"的交叉引用,插一句话进去就可能把一处引用指错。
 *
 * ★★ **这道长度检查是这个工具最容易被删掉的一段,它不是多余的。**
 * 工具结果在产出侧按 `MAX_TOOL_OUTPUT_CHARS`(64K 字符)截断
 * (`agent-session.ts` 调 `truncateToolOutput`),而规范正文按模块算:
 * `diagram` 单模块 ≈ 50K,可 `diagram + chart` 装配出来是 **70.5K** ——
 * 正好越过那条线。被截断的表现是最坏的一种:**规范从中间断掉,末尾补一句
 * "输出已截断"**,而模型不会觉得少了什么,它会照着前半截规范把图按错的
 * 规矩画出来,而且画得很有信心(复杂度预算、颜色规则、SVG 自检表都在后半截)。
 * 所以这里宁可**明确失败**:失败信息里带每个模块的体积,模型下一轮就会
 * 只挑一个。
 */
export const visualizeReadMeTool = defineTool({
  internalId: 'visualize_read_me',
  description:
    'Returns the required context for visualize_show_widget: CSS variables, color ramps, typography, layout ' +
    'rules, streaming-safe patterns and worked examples. Call it once before your first visualize_show_widget ' +
    'call, and again later if you need a different module. Do NOT mention or narrate this call to the user — ' +
    'it is an internal setup step. Call it silently and go straight to building the visualization. ' +
    /*
      ★ 这句必须留在 description 里,而且必须排在规范正文之前被读到 —— 那七万字
      里有一批例子用 `sendPrompt(...)` 做下钻按钮,而本宿主**没有实现它**
      (见文件头第 4 条)。不写这句,模型会照着规范画出一批点了没反应的按钮 ——
      那是"画出来的每个控件都是一次会失败的承诺",而它还会以为自己写对了。
    */
    'NOTE: this host does not provide sendPrompt() — ignore that function where the guidelines mention it. ' +
    'For drill-down buttons in a TSX widget use host.draft(text) from nextcowork/widget (it fills the message box ' +
    'without sending); in HTML/SVG widgets put the follow-up options in your reply text instead.',
  schema: z.object({
    modules: z
      .array(z.enum(AVAILABLE_MODULES))
      .min(1)
      .max(2)
      .describe(
        'Which module(s) to load. Pick every one that fits the visual you are about to build. Load "components" ' +
          'before writing a TSX widget. At most two — the largest combination (diagram + chart) does not fit in one response.'
      )
  }),
  readOnly: true,
  destructive: false,
  needsNetwork: false,
  async run(input) {
    const text = getGuidelines(input.modules)
    if (text.length > MAX_TOOL_OUTPUT_CHARS) {
      const sizes = input.modules.map((m) => `${m}: ${String(getGuidelines([m]).length)} chars`).join(', ')
      return toolFail(
        `These modules together are ${String(text.length)} characters and the result is capped at ` +
          `${String(MAX_TOOL_OUTPUT_CHARS)}, so returning them would cut the guidelines off mid-rule. ` +
          `Call again with a single module. Sizes requested: ${sizes}.`
      )
    }
    return toolOk(text)
  }
})

/**
 * 交互模式与动作只开给主 run。子代理没有人能回答它的挂起(见 `registry.ts` 的
 * `noInteraction`),一个等提交的 widget 在子代理里就是一次死锁。
 */
function interactiveAllowed(ctx: ToolContext): boolean {
  return ctx.depth === 0 && ctx.interact !== undefined
}

/**
 * 不许出现在 `actions` 里的工具。
 *
 * ★ 这几个要么会挂起等人(在 widget 的按钮背后挂起,没有人知道它在等),要么会
 * 派出新的 agent(一颗按钮背后是一整轮看不见的运行)。动作的定位是"一次有界的
 * 工具调用",不是"从 widget 里再起一个 agent"。
 */
const FORBIDDEN_ACTION_TOOLS = new Set([
  'AskUserQuestion',
  'ExitPlanMode',
  'ProposeGoal',
  'Task',
  'Workflow',
  'visualize_read_me',
  'visualize_show_widget',
  'visualize_edit_widget',
  'visualize_update_widget'
])

const actionParamSchema = z.discriminatedUnion('type', [
  z.object({ type: z.literal('enum'), values: z.array(z.string().min(1).max(200)).min(1).max(32) }),
  z.object({
    type: z.literal('number'),
    min: z.number(),
    max: z.number(),
    integer: z.boolean().optional()
  })
])

const actionSchema = z.object({
  id: z.string().regex(/^[a-z][a-z0-9_]{0,39}$/, 'Use a short snake_case id.'),
  label: z.string().trim().min(1).max(60).describe('Shown to the user in the confirmation dialog.'),
  tool: z.string().min(1).max(128).describe('Name of an existing tool, e.g. Bash or Read.'),
  input: z.record(z.string(), z.unknown()).describe('The exact tool input. String values may contain {{param}} placeholders.'),
  params: z.record(z.string().regex(/^[a-zA-Z_][a-zA-Z0-9_]*$/), actionParamSchema).optional()
})

/**
 * 动作声明的一致性:id 不重、工具不在禁用表里、占位符与参数一一对应。
 * 返回错误句子或 null。
 */
function checkActions(actions: readonly WidgetAction[]): string | null {
  const ids = new Set<string>()
  for (const action of actions) {
    if (ids.has(action.id)) return `Duplicate action id: ${action.id}.`
    ids.add(action.id)
    if (FORBIDDEN_ACTION_TOOLS.has(action.tool)) {
      return `Action ${action.id}: the ${action.tool} tool cannot be used as a widget action.`
    }
    const declared = new Set(Object.keys(action.params ?? {}))
    const used = new Set<string>()
    const walk = (node: unknown): void => {
      if (typeof node === 'string') {
        for (const match of node.matchAll(WIDGET_PARAM_PATTERN)) used.add(match[1] as string)
      } else if (Array.isArray(node)) node.forEach(walk)
      else if (node !== null && typeof node === 'object') Object.values(node).forEach(walk)
    }
    walk(action.input)
    for (const name of used) if (!declared.has(name)) return `Action ${action.id}: placeholder {{${name}}} has no matching entry in params.`
    for (const name of declared) if (!used.has(name)) return `Action ${action.id}: param ${name} is never used in input.`
    for (const [name, spec] of Object.entries(action.params ?? {})) {
      if (spec.type === 'number' && !(spec.min <= spec.max)) return `Action ${action.id}: param ${name} has min > max.`
    }
  }
  return null
}

type WidgetCard = Extract<ToolCard, { kind: 'widget' }>

/**
 * 把一份源码变成一张卡。TSX 在这里编译;html/svg 原样。
 * 失败返回给模型看的那句话。
 */
function buildCard(
  title: string,
  code: string,
  format: WidgetFormat | undefined,
  extras: Pick<WidgetCard, 'interactive' | 'actions'>
): WidgetCard | string {
  const resolved = format ?? inferWidgetFormat(code)
  const card: WidgetCard = { kind: 'widget', title, code, format: resolved }
  if (resolved === 'tsx') {
    const result = compileWidgetTsx(code)
    if (!result.ok) return result.error
    card.compiled = result.compiled
  }
  if (extras.interactive === true) card.interactive = true
  if (extras.actions !== undefined && extras.actions.length > 0) card.actions = extras.actions
  return card
}

/**
 * 交互模式的收尾:先把卡片作为**实时卡片**推出去(工具还没返回,`output.card` 还不存在),
 * 再挂起等 widget 里的 `submit()`。
 */
async function presentCard(card: WidgetCard, ctx: ToolContext): Promise<ToolResult> {
  if (card.interactive !== true) return toolOk(RENDERED_NOTE, { card })
  if (!interactiveAllowed(ctx) || ctx.interact === undefined) {
    return toolFail('interactive widgets are unavailable here (subagents and headless runs cannot wait for the user). Call again without interactive.')
  }
  ctx.emit({ callId: ctx.callId, message: '', card })
  const response = await ctx.interact({ kind: 'widget_submit', callId: ctx.callId, title: card.title })
  if (response.kind !== 'widget_submit') return toolFail('Unexpected interaction response.')
  if (response.value === null) {
    return toolFail('The user closed the widget without submitting. Do not assume an answer.')
  }
  return toolOk(
    `The user submitted the widget. Submitted value (JSON): ${response.value}\n` +
      'The widget is still shown to the user — do not redraw it; continue based on the submitted value.',
    { card: { ...card, submitted: response.value } }
  )
}

/** 转录里最近一张叫这个名字的 widget 卡片。`edit` / `update` 都按名字认领。 */
function findWidget(ctx: ToolContext, title: string): WidgetCard | undefined {
  const messages = ctx.messages ?? []
  for (let m = messages.length - 1; m >= 0; m--) {
    const parts = messages[m]?.parts ?? []
    for (let p = parts.length - 1; p >= 0; p--) {
      const part = parts[p]
      if (part?.type !== 'tool_result' || part.isError) continue
      const card = part.output.card
      if (card?.kind === 'widget' && card.title === title) return card
    }
  }
  return undefined
}

const titleSchema = z
  .string()
  .trim()
  .min(1)
  .max(64)
  .regex(/^[a-z0-9_]+$/, 'Use a short snake_case identifier, e.g. q4_revenue_by_product_line.')

/**
 * 收代码、产出 widget 卡片的工具。
 */
export const visualizeShowWidgetTool = defineTool({
  internalId: 'visualize_show_widget',
  description:
    'Show visual content — SVG graphics, diagrams, charts, or interactive widgets — that renders inline in the ' +
    'conversation, with no frame around it. Three formats: "svg" (raw <svg>), "html" (an HTML fragment), and ' +
    '"tsx" (a React component using the host\'s own UI kit — preferred for anything interactive: dashboards, ' +
    'calculators, forms, tables, charts). IMPORTANT: call visualize_read_me before your first call (load the ' +
    '"components" module before writing TSX), and never narrate that call — call it silently and then respond ' +
    'as if you went straight to building the visualization. ' +
    'Set interactive: true when you need the user to pick or configure something in the widget: the call then ' +
    'waits until the widget calls submit(value), and you receive that value as the result. ' +
    'Declare actions when the widget needs buttons that run a fixed tool call (e.g. re-run tests, re-read a ' +
    'file); the user approves them through the normal permission flow.',
  schema: z.object({
    title: titleSchema.describe(
      'Short snake_case identifier for this visual. Must disambiguate — if the conversation has several ' +
        'visuals, this title alone should say which one is meant (q4_revenue_by_product_line, not chart). ' +
        'visualize_edit_widget and visualize_update_widget refer to the widget by this title.'
    ),
    format: z
      .enum(['tsx', 'html', 'svg'])
      .optional()
      .describe('Format of widget_code. If omitted it is inferred: <svg → svg, import/export default → tsx, otherwise html.'),
    widget_code: z
      .string()
      .min(1)
      .max(MAX_WIDGET_CODE_CHARS)
      .describe(
        'The code to render. svg: raw SVG starting with <svg>. html: a fragment — no <!DOCTYPE>, <html>, <head> ' +
          'or <body>; use the CSS variables from visualize_read_me, keep the background transparent, put ' +
          '<script> last. tsx: a module with `export default function Widget()`, importing only from react, ' +
          'nextcowork/widget, nextcowork/charts and lucide-react. Never wrap the outermost element in a card, ' +
          'border or background — the widget sits directly in the conversation.'
      ),
    loading_messages: z
      .array(z.string().trim().min(1).max(120))
      .min(1)
      .max(4)
      .describe(
        '1–4 short messages shown while the visual streams in, each roughly 5 words, in the language the user ' +
          'is writing. For serious topics (illness, death, war, disaster, trauma) keep them deliberately flat ' +
          'and procedural — no evocative wording.'
      ),
    interactive: z
      .boolean()
      .optional()
      .describe('Wait for the user to submit a value from the widget (tsx: useHost().submit(value)) and return it.'),
    actions: z
      .array(actionSchema)
      .max(16)
      .optional()
      .describe('Fixed tool calls the widget may trigger by id (tsx: useAction(id)). Results go back to the widget, not to you.')
  }),
  readOnly: true,
  destructive: false,
  // 见文件头第 2 条:它自己不出网,但它产出的东西会。
  needsNetwork: true,
  async run(input, ctx) {
    if (input.actions !== undefined) {
      const problem = checkActions(input.actions)
      if (problem !== null) return toolFail(problem)
    }
    const card = buildCard(input.title, input.widget_code, input.format, {
      ...(input.interactive === undefined ? {} : { interactive: input.interactive }),
      ...(input.actions === undefined ? {} : { actions: input.actions })
    })
    if (typeof card === 'string') return toolFail(card)
    /*
      `content` 是模型唯一看得见的东西;`card` 只走 UI 轨,编码器一律 strip
      (`shared/agent/tool-card.ts` 的文件头写着这条)。所以这里不把代码回灌给模型 ——
      它自己刚写完,回灌既浪费窗口又会让它想再改一遍。
    */
    return presentCard(card, ctx)
  }
})

/**
 * 在上一版的基础上改几处,而不是整份重写。
 *
 * 需求:"把 X 轴改成月份"这种改动,整份重写要模型再吐一遍几 K 的代码,而且每次重写
 * 都有机会顺手改坏别处。这里照 `Edit` 的规矩做精确替换:旧串必须**恰好出现一次**
 * (或显式 `replace_all`),否则失败并说清楚 —— 不猜。
 *
 * ★ 产出的是一张**新卡**,上一版留在原处。改写历史里的那张卡会让"上一轮看到的图"
 * 在用户眼前悄悄变掉,而对话的其余部分还在讨论旧的那一版。
 */
export const visualizeEditWidgetTool = defineTool({
  internalId: 'visualize_edit_widget',
  description:
    'Make targeted edits to a widget shown earlier in this conversation (found by its title) and show the ' +
    'updated version. Each edit replaces old_string with new_string in the widget source; old_string must ' +
    'match exactly once unless replace_all is set. Prefer this over rewriting the whole widget for small changes.',
  schema: z.object({
    title: titleSchema.describe('Title of the existing widget to edit.'),
    new_title: titleSchema.optional().describe('Title for the new version. Defaults to the same title.'),
    edits: z
      .array(
        z.object({
          old_string: z.string().min(1),
          new_string: z.string(),
          replace_all: z.boolean().optional()
        })
      )
      .min(1)
      .max(32)
  }),
  readOnly: true,
  destructive: false,
  needsNetwork: true,
  async run(input, ctx) {
    if (ctx.messages === undefined) return toolFail('Conversation history is unavailable here, so the widget cannot be found.')
    const base = findWidget(ctx, input.title)
    if (base === undefined) {
      return toolFail(`No widget titled "${input.title}" in this conversation. Use visualize_show_widget to create it.`)
    }
    let code = base.code
    for (const [index, edit] of input.edits.entries()) {
      const count = code.split(edit.old_string).length - 1
      if (count === 0) return toolFail(`Edit ${String(index + 1)}: old_string was not found in the widget source.`)
      if (count > 1 && edit.replace_all !== true) {
        return toolFail(
          `Edit ${String(index + 1)}: old_string matches ${String(count)} places. Add more context to make it unique, or set replace_all.`
        )
      }
      code = edit.replace_all === true ? code.split(edit.old_string).join(edit.new_string) : code.replace(edit.old_string, () => edit.new_string)
    }
    if (code.length > MAX_WIDGET_CODE_CHARS) return toolFail('The edited widget_code exceeds the size limit.')
    /*
      交互模式不继承:上一版的提交已经交过卷,新版本要不要再等一次提交,
      得由模型重新用 `visualize_show_widget` 明说。动作照旧继承 —— 它们是这张图的按钮。
    */
    const card = buildCard(input.new_title ?? input.title, code, base.format, {
      ...(base.actions === undefined ? {} : { actions: base.actions })
    })
    if (typeof card === 'string') return toolFail(card)
    return toolOk(RENDERED_NOTE, { card })
  }
})

/**
 * 给一张已经画出来的 widget 推一份新数据,不重画。
 *
 * 需求:长任务的进度、分析做到一半的中间结果 —— 图是同一张,变的只有数。
 * widget 里用 `useWidgetData(initial)` 读它;宿主按 `title` 把最新一份送到那张卡上。
 *
 * ★ 数据**只走卡片**,不回灌给模型(同 show_widget 的理由):它刚写完。
 */
export const visualizeUpdateWidgetTool = defineTool({
  internalId: 'visualize_update_widget',
  description:
    'Push new data into a TSX widget already shown in this conversation (found by its title) without ' +
    'redrawing it. The widget reads the latest value with useWidgetData(initial) from nextcowork/widget. Use ' +
    'for progress, live results, or updated numbers.',
  schema: z.object({
    title: titleSchema.describe('Title of the existing widget.'),
    data: z.unknown().describe('Any JSON value. Replaces the previous data.')
  }),
  readOnly: true,
  destructive: false,
  needsNetwork: false,
  async run(input, ctx) {
    if (ctx.messages !== undefined && findWidget(ctx, input.title) === undefined) {
      return toolFail(`No widget titled "${input.title}" in this conversation.`)
    }
    const json = JSON.stringify(input.data ?? null)
    if (json.length > WIDGET_LIMITS.data) {
      return toolFail(`data is ${String(json.length)} characters; the limit is ${String(WIDGET_LIMITS.data)}. Send a smaller summary.`)
    }
    return toolOk(`Data pushed to widget ${input.title}. Do not describe the new values again in text.`, {
      card: { kind: 'widget_data', title: input.title, data: JSON.parse(json) as unknown }
    })
  }
})
