import type { TokenUsage } from './stream'

/**
 * `Workflow` 工具的共享形状 —— 主进程的执行器和渲染层的画布读的是同一份。
 *
 * ## 两份状态,一个形状
 *
 * - 运行中:`ToolProgress.workflow`,**易失**,每个节点状态变化推一次整份快照。
 * - 结束后:`ToolOutput.workflow`,跟着转录落盘,**编码器不下发**(同 `output.card`)。
 *
 * 两者都缺(旧转录、或者被中断)时,画布从工具入参 `workflowFromInput` 现推一份
 * 全部 `pending` 的 —— 结构永远画得出来,只是没有状态。
 *
 * ★ 推整份快照而不是增量:最多 20 个节点、每个只有几行字,整份不到几 KB;
 *   增量意味着渲染层要处理乱序和丢包,而进度事件本来就是可丢的。
 */

export type WorkflowNodeStatus = 'pending' | 'running' | 'done' | 'error' | 'skipped'

export interface WorkflowNodeState {
  id: string
  title: string
  /** 派给哪个子代理(`Task` 的 `subagent_type`) */
  agent: string
  dependsOn: readonly string[]
  /** 上游部分失败时也照跑(只要至少一个上游完成)。画布上这类依赖线画成虚线 */
  allowPartial?: boolean
  /** 节点钉的模型别名;缺省 = 跟随子代理的默认 */
  model?: string
  status: WorkflowNodeStatus
  /** 已经派了几次(含重试)。只有 >1 时界面才提 */
  attempts?: number
  /** 逐项节点:对哪个上游产出的数组逐项派子代理。它同时也在 `dependsOn` 里 */
  mapOver?: string
  /** 逐项节点的进度。数组到手之前缺省 */
  items?: WorkflowItemsProgress
  /** 节点声明了 `output_schema`:产出必须是一段符合它的 JSON */
  structured?: boolean
  /** 子代理各轮用量之和(所有尝试累计) */
  usage?: TokenUsage
  startedAt?: number
  endedAt?: number
  /** 子代理产出的前一段,画布上点开节点时看;完整产出只给模型 */
  summary?: string
  /** `error` / `skipped` 的原因 */
  error?: string
}

export interface WorkflowItemsProgress {
  total: number
  done: number
  failed: number
  running: number
  /** 数组超过 `WORKFLOW_MAP_MAX_ITEMS` 时没处理的那几项 */
  dropped?: number
}

export interface WorkflowState {
  title: string
  nodes: readonly WorkflowNodeState[]
}

/** 单个工作流的节点上限。再多画布就成了一张看不清的网,也该拆成两次调用了 */
export const WORKFLOW_MAX_NODES = 20
/** 节点 id:模型要在 depends_on 里逐字引用,所以只给最朴素的字符 */
export const WORKFLOW_NODE_ID_RE = /^[a-zA-Z0-9_-]{1,32}$/
/** `summary` 落盘的上限 —— 完整产出在 `output.content` 里,这里只是给人扫一眼 */
export const WORKFLOW_SUMMARY_MAX = 600
/**
 * 逐项节点一次最多派多少个子代理。★ 数组是**运行时**才知道长度的,入参里的
 * 节点上限管不住它 —— 一个列出了三百个文件的上游会扇出三百个子代理。
 */
export const WORKFLOW_MAP_MAX_ITEMS = 50

export interface WorkflowNodeInput {
  id: string
  title: string
  subagent_type: string
  prompt: string
  depends_on?: readonly string[]
  allow_partial?: boolean
  map_over?: string
  output_schema?: unknown
}

/** 节点真正的依赖:`depends_on` 加上 `map_over`(逐项节点当然要等它的数组) */
export function nodeDependencies(n: { depends_on?: readonly string[]; map_over?: string }): string[] {
  const deps = [...(n.depends_on ?? [])]
  if (n.map_over !== undefined && !deps.includes(n.map_over)) deps.push(n.map_over)
  return deps
}

/** JSON Schema 顶层是不是数组。逐项节点的上游必须声明成这样,运行前就能查 */
function isArraySchema(schema: unknown): boolean {
  return typeof schema === 'object' && schema !== null && (schema as { type?: unknown }).type === 'array'
}

/**
 * 依赖图是否成立。返回 `null` = 成立,否则是一句给模型看的、能据此改入参的话。
 *
 * ★ 放在 shared 而不只在执行器里:渲染层画「入参推出来的结构」时也要先确认它
 *   是一张合法的图 —— 一个带环的入参交给布局函数,会算出一张谁也看不懂的图。
 */
export function validateWorkflowGraph(nodes: readonly WorkflowNodeInput[]): string | null {
  const ids = new Set<string>()
  for (const n of nodes) {
    if (ids.has(n.id)) return `Duplicate node id "${n.id}" — every node id must be unique.`
    ids.add(n.id)
  }
  const byId = new Map(nodes.map((n) => [n.id, n]))
  for (const n of nodes) {
    if (n.map_over !== undefined) {
      const source = byId.get(n.map_over)
      if (source === undefined || n.map_over === n.id) {
        return `Node "${n.id}" has map_over "${n.map_over}", which is not another node in this workflow.`
      }
      // 结构化的逐项节点交出的本来就是「每项一份」的数组,可以再被逐项一次
      const producesArray = source.map_over !== undefined ? source.output_schema !== undefined : isArraySchema(source.output_schema)
      if (!producesArray) {
        return `Node "${n.id}" maps over "${n.map_over}", so "${n.map_over}" must declare an output_schema ` +
          'whose top-level type is "array" (e.g. {"type":"array","items":{"type":"string"}}).'
      }
    }
    for (const dep of n.depends_on ?? []) {
      if (dep === n.id) return `Node "${n.id}" depends on itself.`
      if (!ids.has(dep)) {
        return `Node "${n.id}" depends on "${dep}", which is not a node in this workflow. ` +
          `Valid ids: ${[...ids].join(', ')}.`
      }
    }
  }
  const normalized = nodes.map((n) => ({ id: n.id, depends_on: nodeDependencies(n) }))
  if (workflowLayers(normalized) === null) return 'The dependencies form a cycle. A workflow must be a directed acyclic graph.'
  /*
    ★ 一条边都没有的图,几乎总是模型忘了给汇总节点写 depends_on —— 于是「汇总」和
    被汇总的分析同时开跑,拿不到任何上游结论,交回来的是一份编出来的总结。
    真正互相独立的几件事不需要这张图:一轮里并发调几次 `Task` 就是同一个效果。
  */
  if (normalized.every((n) => n.depends_on.length === 0)) {
    return 'No node depends on any other, so every node would start at once with no upstream results. ' +
      'If a node is meant to combine or summarize the others, list them in its depends_on. ' +
      'If the steps are truly independent, call the Task tool several times in one turn instead.'
  }
  return null
}

/**
 * 最长路径分层:第 0 层是没有依赖的节点,其余节点落在「最深的依赖 + 1」那层。
 * 有环返回 `null`。
 *
 * ★ 层内顺序保持入参顺序 —— 模型写节点的顺序通常就是它心里的阅读顺序,
 *   重排只会让画布和它在回复里的描述对不上。
 */
export function workflowLayers(
  nodes: readonly { id: string; depends_on?: readonly string[] }[]
): string[][] | null {
  const deps = new Map(nodes.map((n) => [n.id, n.depends_on ?? []]))
  const depth = new Map<string, number>()
  const visiting = new Set<string>()

  const visit = (id: string): number | null => {
    const known = depth.get(id)
    if (known !== undefined) return known
    if (visiting.has(id)) return null
    visiting.add(id)
    let d = 0
    for (const dep of deps.get(id) ?? []) {
      if (!deps.has(dep)) continue
      const dd = visit(dep)
      if (dd === null) return null
      d = Math.max(d, dd + 1)
    }
    visiting.delete(id)
    depth.set(id, d)
    return d
  }

  const layers: string[][] = []
  for (const n of nodes) {
    const d = visit(n.id)
    if (d === null) return null
  }
  for (const n of nodes) {
    const d = depth.get(n.id) ?? 0
    ;(layers[d] ??= []).push(n.id)
  }
  return layers
}

/** 入参 → 一份全部 `pending` 的状态。画布在拿不到任何进度时用它画结构。 */
export function workflowFromInput(input: unknown): WorkflowState | null {
  if (typeof input !== 'object' || input === null) return null
  const raw = input as { title?: unknown; nodes?: unknown }
  if (!Array.isArray(raw.nodes)) return null
  const nodes: WorkflowNodeState[] = []
  for (const n of raw.nodes as unknown[]) {
    if (typeof n !== 'object' || n === null) return null
    const node = n as Record<string, unknown>
    if (typeof node.id !== 'string') return null
    const mapOver = typeof node.map_over === 'string' ? node.map_over : undefined
    const dependsOn = nodeDependencies({
      depends_on: Array.isArray(node.depends_on) ? node.depends_on.filter((d): d is string => typeof d === 'string') : [],
      ...(mapOver === undefined ? {} : { map_over: mapOver })
    })
    nodes.push({
      id: node.id,
      title: typeof node.title === 'string' ? node.title : node.id,
      agent: typeof node.subagent_type === 'string' ? node.subagent_type : '',
      dependsOn,
      ...(node.allow_partial === true ? { allowPartial: true } : {}),
      ...(typeof node.model === 'string' ? { model: node.model } : {}),
      ...(mapOver === undefined ? {} : { mapOver }),
      ...(node.output_schema !== undefined ? { structured: true } : {}),
      status: 'pending'
    })
  }
  if (nodes.length === 0) return null
  if (workflowLayers(nodes.map((n) => ({ id: n.id, depends_on: n.dependsOn }))) === null) return null
  return { title: typeof raw.title === 'string' ? raw.title : 'Workflow', nodes }
}

/**
 * 一份用量折成一个「花了多少 token」的数,给画布看。缓存读写也算进去 ——
 * 它们是这个节点真实吞下的上下文,只算 `inputTokens` 会让读了大量缓存的节点显得很便宜。
 */
export function workflowTokens(usage: TokenUsage | undefined): number {
  if (usage === undefined) return 0
  return usage.inputTokens + usage.outputTokens + (usage.cacheReadInputTokens ?? 0) + (usage.cacheCreationInputTokens ?? 0)
}

export interface WorkflowProgressCount {
  done: number
  total: number
  running: number
  failed: number
}

export function countWorkflow(state: WorkflowState): WorkflowProgressCount {
  let done = 0
  let running = 0
  let failed = 0
  for (const n of state.nodes) {
    if (n.status === 'done') done++
    else if (n.status === 'running') running++
    else if (n.status === 'error' || n.status === 'skipped') failed++
  }
  return { done, total: state.nodes.length, running, failed }
}

// ─────────────────────────── 画布布局 ───────────────────────────

/** 节点尺寸与间距(px)。画布和这里必须同一份,否则连线端点会落在节点外面 */
export const WORKFLOW_NODE_W = 172
export const WORKFLOW_NODE_H = 54
export const WORKFLOW_GAP_X = 44
export const WORKFLOW_GAP_Y = 14

export interface WorkflowLayout {
  positions: Readonly<Record<string, { x: number; y: number }>>
  /** 整张图的外接尺寸(不含画布内边距) */
  width: number
  height: number
}

/**
 * 从左到右分层:列 = `workflowLayers` 的层,列内节点竖向居中对齐。
 *
 * ★ 选横向而不是纵向:对话列只有 ~720px 宽,而工作流的典型形状是
 *   「一个拆分 → 三四个并行 → 一个汇总」—— 横排是三列、高度取最高那一列,
 *   竖排则是四五层叠起来,一张图能把半屏对话顶出视野。
 */
export function layoutWorkflow(state: WorkflowState): WorkflowLayout {
  const layers =
    workflowLayers(state.nodes.map((n) => ({ id: n.id, depends_on: n.dependsOn }))) ??
    // 有环(只可能来自坏数据)时退化成一列,至少每个节点都画得出来
    [state.nodes.map((n) => n.id)]
  const tallest = Math.max(...layers.map((l) => l.length))
  const height = tallest * WORKFLOW_NODE_H + (tallest - 1) * WORKFLOW_GAP_Y
  const positions: Record<string, { x: number; y: number }> = {}
  layers.forEach((layer, col) => {
    const colHeight = layer.length * WORKFLOW_NODE_H + (layer.length - 1) * WORKFLOW_GAP_Y
    const top = (height - colHeight) / 2
    layer.forEach((id, row) => {
      positions[id] = {
        x: col * (WORKFLOW_NODE_W + WORKFLOW_GAP_X),
        y: top + row * (WORKFLOW_NODE_H + WORKFLOW_GAP_Y)
      }
    })
  })
  return {
    positions,
    width: layers.length * WORKFLOW_NODE_W + (layers.length - 1) * WORKFLOW_GAP_X,
    height
  }
}
