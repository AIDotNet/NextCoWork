/**
 * 工具时间线的**纯逻辑** —— 分组、窗口、收束判定、摘要。
 *
 * ★ **为什么这些函数必须留在 shared/ 且单独单测。**
 * 它们的 bug 表现是「偶尔少一组」「偶尔多折一层」「失败项被折进去了」——
 * 全都依赖某一次 run 里工具的具体排列顺序才现形,靠盯屏幕根本复现不了。
 * 这与 `transcript.ts` 文件头讲的是同一条理由:能用三行测试锁死的东西,
 * 不要留给肉眼。
 *
 * 这里**不 import 任何 React**。组件只负责把这些函数的输出摆进版式。
 */
import { durationOf } from '../agent/duration'
import type { ThinkingStats } from '../agent/thinking-stats'
import type { SubagentState, ToolCallState } from '../agent/transcript'
import { presenterOf, type ToolShape } from './tool-presenter'

/**
 * 时间线里的一项。三种来源统一成一个形状:
 * 已提交消息的 `parts`、还在流的 `live` 块,以及将来的子代理节点。
 *
 * `key` 由构造方给出且**必须稳定**:tool 用 `callId`,拿不到 callId 时
 * (流式刚开头)退回块 index。它同时是 React key 和「用户折叠意图」的锚 ——
 * 见 `groupKey` 的说明。
 */
export type TimelineItem =
  /** `stats`:思考卡片的用时与 token 读数来源,见 `shared/agent/thinking-stats.ts`。缺席 = 不画读数。 */
  | { key: string; kind: 'thinking'; text: string; streaming: boolean; stats?: ThinkingStats }
  | { key: string; kind: 'tool'; callId: string | undefined; name: string; input: unknown }
  | {
      key: string
      kind: 'subagent'
      callId: string
      summary: string | undefined
      state?: SubagentState
      /**
       * 子代理**还没派出去** —— `Task` 的参数还在流,`subagent_start` 没到。
       *
       * 需求:这段时间里卡片要长成子代理卡的样子(而不是先画一张通用工具卡、
       * 等状态到了再整个换掉),但它不能显示成「已完成」——
       * `state` 缺省时卡片的兜底状态正是 done,见 `parts.tsx` 的 `SubagentNode`。
       * 不能用「state 为空」代替这个标记:**旧转录**里的子代理块同样没有 state,
       * 而它们早就跑完了。
       */
      pending?: boolean
    }

/** 可见的最近工具行数。理由见设计文档 §5.2:约 130px,不把正文挤出视口。 */
export const TOOL_WINDOW_SIZE = 3

/** A completed run with any process item can be summarized above its answer. */
export const WORKSPACE_MIN_ITEMS = 1

export function shapeOfItem(
  item: TimelineItem,
  tools: Readonly<Record<string, ToolCallState>>
): ToolShape {
  switch (item.kind) {
    case 'thinking':
      return 'reasoning'
    case 'subagent':
      return 'orchestration'
    case 'tool': {
      // 优先用 tools 表里的名字:live 块的 name 可能还没到
      const call = item.callId === undefined ? undefined : tools[item.callId]
      return presenterOf(call?.name ?? item.name).shape
    }
  }
}

export function statusOfItem(
  item: TimelineItem,
  tools: Readonly<Record<string, ToolCallState>>
): 'pending' | 'running' | 'ok' | 'error' {
  if (item.kind === 'subagent') {
    switch (item.state?.status) {
      case 'running': return 'running'
      case 'error':
      case 'aborted': return 'error'
      case 'done': return 'ok'
      default: return 'pending'
    }
  }
  if (item.kind !== 'tool') return 'ok'
  if (item.callId === undefined) return 'pending'
  const call = tools[item.callId]
  return call === undefined ? 'pending' : call.status
}

function isError(item: TimelineItem, tools: Readonly<Record<string, ToolCallState>>): boolean {
  return statusOfItem(item, tools) === 'error'
}

/**
 * 按「连续同形态类」切段。
 *
 * ★ **不按固定条数切。**「读了 5 个文件」是一个语义完整的单元,折起来不丢信息;
 * 而机械地「每 3 条一组」会把 Grep + Read + Edit 塞进同一个**标题没法写**的组里。
 * 分组标题能不能写成一句人话,是判断分组规则对不对的直接标准。
 */
export function groupItems(
  items: readonly TimelineItem[],
  tools: Readonly<Record<string, ToolCallState>>
): TimelineItem[][] {
  const groups: TimelineItem[][] = []
  let cur: TimelineItem[] = []
  let curShape: ToolShape | null = null

  for (const it of items) {
    const shape = shapeOfItem(it, tools)
    if (curShape !== null && shape === curShape) {
      cur.push(it)
    } else {
      if (cur.length > 0) groups.push(cur)
      cur = [it]
      curShape = shape
    }
  }
  if (cur.length > 0) groups.push(cur)
  return groups
}

/** Group every adjacent tool call, including different tool shapes. Thinking and
 * prose stay boundaries so a run can be summarized as one compact process row. */
export function groupConsecutiveTools(
  items: readonly TimelineItem[],
  tools: Readonly<Record<string, ToolCallState>> = {}
): TimelineItem[][] {
  const groups: TimelineItem[][] = []
  for (const item of items) {
    const previous = groups.at(-1)
    const startsAfterCompleted = item.kind === 'tool'
      && statusOfItem(item, tools) !== 'ok'
      && previous !== undefined
      && isCompletedToolGroup(previous, tools)
    if (item.kind === 'tool' && previous?.[0]?.kind === 'tool' && !startsAfterCompleted) previous.push(item)
    else groups.push([item])
  }
  return groups
}

/** A multi-call group is safe to collapse once every call has a durable result. */
export function isCompletedToolGroup(
  group: readonly TimelineItem[],
  tools: Readonly<Record<string, ToolCallState>>
): boolean {
  return group.length > 1 && group.every((item) => item.kind === 'tool'
    && item.callId !== undefined && statusOfItem(item, tools) === 'ok')
}

/**
 * 组的稳定 key = **首项的 key**。
 *
 * ★ 不能用数组下标。新项到达会让 `groupItems` 重新切分,下标作 key 时
 * 「用户手动展开过第 2 组」这个意图会漂移到另一组身上 ——
 * 表现为「我展开的是读取,结果展开的是命令」。
 *
 * 用首项 key 则:同形态新项追加进已有组 → 首项不变 → key 不变 → 用户意图保住;
 * 新形态开新组 → 新 key → 新组走自动规则。正是期望行为。
 */
export function groupKey(group: readonly TimelineItem[]): string {
  return group[0]?.key ?? 'empty'
}

export interface GroupCollapseInput {
  groups: readonly (readonly TimelineItem[])[]
  tools: Readonly<Record<string, ToolCallState>>
  /** run 是否仍在进行。结束后窗口规则不再适用,交给 L3。 */
  running: boolean
  windowSize?: number
}

/**
 * 算出每个组的**自动**折叠态(不含用户手动覆盖 —— 那是 hook 的事)。
 *
 * 规则:
 * ```
 * tail = items 中最后 N 个「非 error」项
 * 组含 error        → 展开(且不占 tail 名额)
 * 组 ∩ tail ≠ ∅     → 展开
 * 其余              → 坍缩
 * ```
 *
 * ★ **失败项被排除在 tail 之外**,这条不是细节。若失败项占窗口名额,
 * 一次早期失败会把窗口永久钉死在很久以前的位置,用户就再也看不到
 * 此刻正在跑什么 —— 而那恰恰是 run 进行中最需要看到的信息。
 * 正确行为是:失败组在窗口之外**独立常驻展开**,窗口继续跟随最新项。
 */
export function computeAutoCollapsed({
  groups,
  tools,
  running,
  windowSize = TOOL_WINDOW_SIZE
}: GroupCollapseInput): boolean[] {
  // run 结束后 L2 不再自动坍缩(收束交给 L3);全部展开,由 L3 决定整体藏不藏
  if (!running) return groups.map(() => false)

  const nonError: TimelineItem[] = []
  for (const g of groups) {
    for (const it of g) {
      if (!isError(it, tools)) nonError.push(it)
    }
  }
  const tail = new Set(nonError.slice(-windowSize).map((it) => it.key))

  return groups.map((g) => {
    if (g.some((it) => isError(it, tools))) return false
    if (g.some((it) => tail.has(it.key))) return false
    return true
  })
}

/** 组标题:「读取了 5 个文件」这类人话。单项时不加数量。 */
export function groupTitle(
  group: readonly TimelineItem[],
  tools: Readonly<Record<string, ToolCallState>>
): string {
  const first = group[0]
  if (first === undefined) return ''
  const shape = shapeOfItem(first, tools)
  const n = group.length

  const LABEL: Record<ToolShape, (count: number) => string> = {
    reasoning: (c) => (c === 1 ? '深度思考' : `${String(c)} 段思考`),
    read: (c) => (c === 1 ? '读取了 1 个文件' : `读取了 ${String(c)} 个文件`),
    mutate: (c) => (c === 1 ? '修改了 1 个文件' : `修改了 ${String(c)} 个文件`),
    search: (c) => (c === 1 ? '检索了 1 次' : `检索了 ${String(c)} 次`),
    command: (c) => (c === 1 ? '执行了 1 条命令' : `执行了 ${String(c)} 条命令`),
    network: (c) => (c === 1 ? '访问了 1 个网络资源' : `访问了 ${String(c)} 个网络资源`),
    orchestration: (c) => (c === 1 ? '调度了 1 项' : `调度了 ${String(c)} 项`),
    interaction: (c) => (c === 1 ? '等你表态 1 次' : `等你表态 ${String(c)} 次`),
    widget: (c) => (c === 1 ? '画了 1 张图' : `画了 ${String(c)} 张图`),
    image: (c) => (c === 1 ? '生成了 1 次图片' : `生成了 ${String(c)} 次图片`),
    video: (c) => (c === 1 ? '生成了 1 段视频' : `生成了 ${String(c)} 段视频`),
    screenshot: (c) => (c === 1 ? '截取了 1 个页面' : `截取了 ${String(c)} 个页面`),
    workflow: (c) => (c === 1 ? '运行了 1 个工作流' : `运行了 ${String(c)} 个工作流`),
    external: (c) => (c === 1 ? '调用了 1 个外部工具' : `调用了 ${String(c)} 个外部工具`)
  }
  return LABEL[shape](n)
}

/** 一组的累计耗时(毫秒)。没有可算的返回 0。 */
export function groupDuration(
  group: readonly TimelineItem[],
  tools: Readonly<Record<string, ToolCallState>>
): number {
  let total = 0
  for (const it of group) {
    if (it.kind !== 'tool' || it.callId === undefined) continue
    const call = tools[it.callId]
    if (call !== undefined) total += durationOf(call) ?? 0
  }
  return total
}

// ─────────────────────────── L3:工作区 ───────────────────────────

export interface WorkspaceSummary {
  toolCount: number
  /**
   * 各工具耗时之**和**。
   *
   * ★ 文案必须写「累计」不能写「总耗时」:工具将来会并行调度
   * (`tool.ts` 里 readOnly 字段正是为此),并行时这个和会明显大于墙钟时长。
   * 「累计 12.4s」在串行和并行下都是对的。
   */
  totalMs: number
  errorCount: number
  fileChangeCount: number
  /** 出现过的形态类,按首次出现顺序 —— 标题行画一排小图标 */
  shapes: ToolShape[]
}

export function summarize(
  items: readonly TimelineItem[],
  tools: Readonly<Record<string, ToolCallState>>,
  fileChangeCount = 0
): WorkspaceSummary {
  let totalMs = 0
  let errorCount = 0
  let toolCount = 0
  const shapes: ToolShape[] = []

  for (const it of items) {
    const shape = shapeOfItem(it, tools)
    if (!shapes.includes(shape)) shapes.push(shape)
    if (it.kind !== 'tool') continue
    toolCount += 1
    if (it.callId === undefined) continue
    const call = tools[it.callId]
    if (call === undefined) continue
    totalMs += durationOf(call) ?? 0
    if (call.status === 'error') errorCount += 1
  }

  return { toolCount, totalMs, errorCount, fileChangeCount, shapes }
}

/** run 的终态。`aborted` / `error` 与 `done` 的收束行为不同。 */
export type RunOutcome = 'running' | 'ok' | 'error' | 'aborted'

export interface WorkspaceDecision {
  /** 是否把过程段收进「工作区」外壳 */
  collapse: boolean
  /**
   * 收进去之后是否默认展开。
   *
   * 需求：本轮正常结束就应该收起，让用户一眼看到结尾正文；曾经有过失败工具调用
   * 不再是例外——之前「有失败就默认展开」会被后台任务（如更新检查抓取失败）
   * 永久触发，表现为每次打开会话「用时」面板都是展开的，且看不出是渲染问题还是
   * 真的出了状况。用户要看失败细节，点开工作区一次就行。
   */
  defaultOpen: boolean
}

/**
 * L3 是否收束。三个条件缺一不可(设计文档 §6.1):
 *
 * 1. run **正常**结束 —— 中断/报错时用户大概率要看「跑到哪一步停的」,
 *    收起来等于多要求一次点击才能看到最需要的信息。
 * 2. 存在**位于所有工具块之后的非空正文** —— 否则收束后界面上只剩一个
 *    孤零零的「工作区」块,用户看不到任何结论。那不是折叠,是把内容藏没了。
 * 3. 至少存在一个过程项 —— 完成后的思考或工具内容都应收束到摘要行。
 */
export function decideWorkspace({
  outcome,
  itemCount,
  hasTrailingText
}: {
  outcome: RunOutcome
  itemCount: number
  hasTrailingText: boolean
}): WorkspaceDecision {
  const collapse =
    outcome === 'ok' && hasTrailingText && itemCount >= WORKSPACE_MIN_ITEMS
  return { collapse, defaultOpen: false }
}

/**
 * 标题文案。指标顺序固定为 **数量 → 时间 → 产出 → 异常**,
 * 异常永远在最后 —— 固定顺序让眼睛形成肌肉记忆,每次都在同一个位置找同一个指标。
 *
 * 返回分段而不是拼好的字符串,因为「失败」那一段要单独标红。
 */
export function workspaceTitleParts(
  s: WorkspaceSummary,
  formatMs: (ms: number) => string
): { normal: string[]; danger: string | undefined } {
  const normal = [`${String(s.toolCount)} 个工具`]
  if (s.totalMs > 0) normal.push(`累计 ${formatMs(s.totalMs)}`)
  if (s.fileChangeCount > 0) normal.push(`${String(s.fileChangeCount)} 个文件变更`)
  const danger = s.errorCount > 0 ? `${String(s.errorCount)} 个失败` : undefined
  return { normal, danger }
}

// ─────────────────────────── 正文段 / 过程段的切分 ───────────────────────────

/** 切分的输入:一条一条的原始条目,由调用方从 `parts` 或 `live` 适配而来。 */
export type TimelineEntry =
  | { kind: 'text'; key: string; text: string }
  | { kind: 'item'; item: TimelineItem }

/**
 * 切分结果。`process` 段是**连续**的非文本块 —— 也就是要交给 `ToolTimeline`
 * 去做 L2/L3 折叠的那一段。
 */
export type TimelineSegment =
  | { kind: 'text'; key: string; text: string }
  | { kind: 'process'; key: string; items: TimelineItem[] }

/**
 * 把「文本 / 过程」交替的条目流切成段。
 *
 * ★ **为什么必须先切段,不能把整条消息当一个时间线。**
 * 一轮回答的典型形状是「思考 → 工具×N → 正文 → 工具×M → 收尾正文」。
 * 若不切段,L3 收束时会把中间那段正文一起藏起来 —— 而那段正文往往是
 * 「我先看了下,发现 X,接下来改 Y」这类**解释性内容**,恰恰是用户要读的。
 *
 * 空文本条目会被丢掉(`part.text.trim() === ''` 的块在渲染层本来就返回 null),
 * 但**丢弃发生在切分之前** —— 否则一个空文本块会把本该连续的两段过程劈成两半,
 * 于是界面上出现两个「工作区」外壳,中间夹着一段什么都没有的空白。
 */
export function segmentize(entries: readonly TimelineEntry[]): TimelineSegment[] {
  const segments: TimelineSegment[] = []
  let buffer: TimelineItem[] = []

  const flush = (): void => {
    if (buffer.length === 0) return
    segments.push({ kind: 'process', key: `p:${buffer[0]?.key ?? ''}`, items: buffer })
    buffer = []
  }

  for (const e of entries) {
    if (e.kind === 'item') {
      buffer.push(e.item)
      continue
    }
    // ★ 空正文不产生段,也不打断过程段
    if (e.text.trim() === '') continue
    flush()
    segments.push({ kind: 'text', key: e.key, text: e.text })
  }
  flush()
  return segments
}

/**
 * 是否存在「位于所有过程块之后的非空正文」—— L3 收束的必要条件之一。
 *
 * 判据是**最后一段是不是 text**,而不是「有没有 text」:
 * 中间夹着的解释性正文不算数,收束后它仍然被藏在工作区里。
 */
export function hasTrailingText(segments: readonly TimelineSegment[]): boolean {
  return segments[segments.length - 1]?.kind === 'text'
}
