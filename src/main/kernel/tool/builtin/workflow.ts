/**
 * `Workflow` —— 把一件事编排成一张依赖图,按依赖**并行**派子代理执行。
 *
 * ## 和连续调几次 `Task` 有什么不同
 *
 * 1. **并行是确定的**:没有依赖关系的节点同时派出,不靠模型「记得在一轮里并发调用」。
 * 2. **上游产出自动送到下游**:节点的 prompt 后面会附上它所有依赖节点的完整结论,
 *    模型不必把 A 的产出抄一遍再写进 B 的 prompt(抄的那一遍既花 token 又会走样)。
 * 3. **结构对用户可见**:进度以整份节点状态推给界面,对话里画成画布
 *    (`renderer/.../WorkflowCanvas.tsx`)。
 *
 * ## 子代理怎么派
 *
 * 每个节点就是一次 `ctx.spawnSubagent` —— 并发名额、排队、权限审批、中断级联
 * 全部照 `Task` 那条路走,这里一行都不重写。
 *
 * ★ 每个节点用**自己的** callId(`<本次调用>#<节点 id>`)去派。渲染层按 callId
 *   索引子代理状态,共用一个的话 N 个子代理会在任务面板里互相覆盖成一个。
 *   这些合成 id 在转录里没有对应的 `tool_result`,`persistSubagentCompletion`
 *   按 callId 找不到就什么都不做 —— 子代理的完整结论由这个工具自己的结果带回。
 *
 * ## 失败怎么传
 *
 * 一个节点失败,**只有依赖它的那些**标成 `skipped`;不相干的分支照常跑完。
 * 一个分支坏了就整张图停下,会把已经能做完的活白白扔掉。
 *
 * 子代理报错或交了空话时,节点先按 `retries`(缺省 1 次)原地重派;被拒(名字不对、
 * 排不上队)和被转去后台不重试 —— 前者重派一模一样会再被拒,后者根本没失败。
 *
 * 标了 `allow_partial` 的节点(典型是汇总)等**所有**上游落定,只要有一个完成就照跑,
 * 没完成的那几个在 prompt 里列成「缺失」。不标的话,一个分析分支挂了汇总就整个跳过,
 * 其余几份做完的结论没人收。
 *
 * ## 结构化产出与逐项节点
 *
 * 节点可以声明 `output_schema`(JSON Schema):子代理必须以一段 ```json 代码块收尾,
 * 解析并校验通过才算完成,不合格按失败重试(重试的 prompt 带上不合格的原因)。
 * 下游拿到的是规整过的 JSON,而不是夹在一段散文里的数据。
 *
 * 声明了 `map_over` 的节点是**逐项节点**:等上游交出一个数组,对每一项各派一个子代理,
 * 画布上折成一个节点、显示「完成几项 / 共几项」。这是一张写死的图唯一表达不了的形状 ——
 * 写图的时候不知道上游会列出几个模块。上游必须声明数组型的 `output_schema`,
 * 运行前就查;项数上限 `WORKFLOW_MAP_MAX_ITEMS`,超出的不处理并在结果里写明。
 * 只要有一项完成,逐项节点就算完成;失败的项列在它的产出里。
 *
 * ## 运行前确认
 *
 * 设置 › 实验 › 运行前确认(出厂开)。校验通过、派出任何节点之前,把全部 `pending`
 * 的图交给用户:运行 / 要求修改(带反馈,原样转给模型)/ 取消。放在校验**之后**:
 * 一张非法的图不该先打扰用户再被拒。
 *
 * ## 开关
 *
 * 设置 › 实验 › Workflow,出厂关闭。只在主 run 下发:子代理里再起一张图,
 * 一次调用就可能扇出几十个子代理,而嵌套深度上限挡不住宽度。
 */
import { z } from 'zod'
import { addTokenUsage } from '../../../../shared/agent/stream'
import { toolFail, type ToolResult } from '../../../../shared/agent/tool'
import {
  validateWorkflowGraph,
  workflowFromInput,
  WORKFLOW_MAX_NODES,
  WORKFLOW_NODE_ID_RE,
  WORKFLOW_SUMMARY_MAX,
  nodeDependencies,
  WORKFLOW_MAP_MAX_ITEMS,
  type WorkflowItemsProgress,
  type WorkflowNodeState,
  type WorkflowState
} from '../../../../shared/agent/workflow'
import { abortError } from '../../abort'
import { defineTool } from '../define'
import type { SubagentRequest, ToolContext } from '../registry'

/** 每个上游结论送给下游时的上限。几个上游加起来仍要装得进一个子代理的首条消息 */
const UPSTREAM_CHARS_MAX = 12_000

const WorkflowInput = z.object({
  title: z.string().min(1).max(80).describe('A short title for the whole workflow, shown on the canvas'),
  nodes: z
    .array(
      z.object({
        id: z
          .string()
          .regex(WORKFLOW_NODE_ID_RE)
          .describe('Unique node id (letters, digits, _ or -), referenced by depends_on'),
        title: z.string().min(1).max(40).describe('2-6 word label shown on the canvas node'),
        subagent_type: z
          .string()
          .min(1)
          .max(64)
          .describe('The subagent that runs this node — same list as the Task tool'),
        prompt: z
          .string()
          .min(1)
          .max(50_000)
          .describe(
            'The full task for this node. The subagent CANNOT see this conversation; write every bit of ' +
              'context it needs. Outputs of the nodes in depends_on are appended automatically'
          ),
        depends_on: z
          .array(z.string())
          .max(WORKFLOW_MAX_NODES)
          .optional()
          .describe('Ids of nodes that must finish first. Omit or leave empty to start immediately'),
        allow_partial: z
          .boolean()
          .optional()
          .describe(
            'Run this node even if some of its dependencies failed, as long as at least one finished. ' +
              'Set it on summary/merge nodes so one broken branch does not throw away the others. ' +
              'Dependencies that did not finish are listed as missing in the prompt'
          ),
        model: z
          .string()
          .min(1)
          .max(128)
          .optional()
          .describe(
            'Model alias for this node only. Omit to use the subagent\'s default. Only set it when the user ' +
              'asked for specific models or you know the available aliases; unknown names are rejected before anything runs'
          ),
        effort: z
          .enum(['low', 'medium', 'high', 'max'])
          .optional()
          .describe('Thinking effort for this node only. Omit to use the subagent\'s default'),
        retries: z
          .number()
          .int()
          .min(0)
          .max(3)
          .optional()
          .describe('How many times to re-run this node if its subagent errors or returns no text. Default 1'),
        output_schema: z
          .record(z.string(), z.unknown())
          .optional()
          .describe(
            'JSON Schema the node\'s output must match. The subagent is told to end its reply with a ```json block; ' +
              'invalid output counts as a failure and is retried. Downstream nodes receive the JSON. ' +
              'Required (with top-level "type": "array") on any node that another node maps over'
          ),
        map_over: z
          .string()
          .regex(WORKFLOW_NODE_ID_RE)
          .optional()
          .describe(
            'Id of a node whose output is a JSON array. This node then runs once PER ITEM, in parallel ' +
              `(at most ${String(WORKFLOW_MAP_MAX_ITEMS)} items). Put {{item}} in the prompt where the item goes, ` +
              'otherwise it is appended. Implies a dependency on that node. If this node also has an output_schema, ' +
              'it describes ONE item\'s result and the combined output is an array of them'
          )
      })
    )
    .min(2)
    .max(WORKFLOW_MAX_NODES)
})

type Input = z.output<typeof WorkflowInput>
type Node = Input['nodes'][number]

/** 节点没写 `retries` 时的重试次数:上游抖一下就丢掉整条分支不值得 */
const DEFAULT_RETRIES = 1

const DESCRIPTION =
  'Runs a multi-step task as a dependency graph of subagents. Nodes with no unfinished dependencies run ' +
  'IN PARALLEL; each node is one subagent run (same subagent types as the `Task` tool). The final ' +
  'output of every dependency is appended to a node\'s prompt automatically, so do NOT copy results ' +
  'between nodes yourself. The graph is shown to the user as a live canvas.\n\n' +
  'When to use:\n' +
  '- The task splits into 3+ subtasks and some of them are independent (fan-out), or later steps ' +
  'need to combine earlier results (fan-in), e.g. review a change from several angles then merge\n' +
  '- You would otherwise launch several `Task` calls and pass results between them by hand\n\n' +
  'When NOT to use:\n' +
  '- One or two steps — use `Task` or do it yourself\n' +
  '- The steps need back-and-forth with the user: subagents cannot ask the user anything\n\n' +
  'Rules:\n' +
  `- 2 to ${String(WORKFLOW_MAX_NODES)} nodes; ids unique; depends_on must reference existing ids; no cycles\n` +
  '- At least one node must depend on another. A node that combines or summarizes other nodes MUST list them ' +
  'in depends_on, otherwise it starts at the same time and sees none of their results\n' +
  '- A failed node is retried (`retries`, default 1). If it still fails, the nodes that depend on it are skipped, ' +
  'unless they set `allow_partial`; independent branches still finish\n' +
  '- Use output_schema + map_over when the number of subtasks is only known after an earlier step, e.g. ' +
  '"list the modules" (output_schema: array of strings) then "analyse {{item}}" (map_over that node)\n' +
  '- The result contains every node\'s final output. CHECK it before acting on it'

function clip(text: string, max: number): string {
  return text.length <= max ? text : `${text.slice(0, max)}\n…(truncated, ${String(text.length - max)} more chars)`
}

/**
 * 下游节点实际收到的 prompt:自己那段 + 所有上游的结论。
 * `allow_partial` 节点的上游可能没做完 —— 那几段写成「缺失」,不能悄悄略过,
 * 否则汇总节点会把缺了一块的结论当成全貌。
 */
function composePrompt(
  node: Node,
  outputs: ReadonlyMap<string, string>,
  states: ReadonlyMap<string, WorkflowNodeState>,
  item?: { value: unknown }
): string {
  let prompt = node.prompt
  if (item !== undefined) {
    const text = clip(typeof item.value === 'string' ? item.value : JSON.stringify(item.value, null, 2), UPSTREAM_CHARS_MAX)
    prompt = prompt.includes('{{item}}')
      ? prompt.split('{{item}}').join(text)
      : `${prompt}\n\n## The item to work on\n\n${text}`
  }
  if (node.output_schema !== undefined) {
    prompt += '\n\n## Output format\n\nEnd your reply with exactly one ```json fenced code block containing valid JSON ' +
      'that matches the JSON Schema below. The next steps only read that block.\n\n' +
      `\`\`\`json\n${JSON.stringify(node.output_schema, null, 2)}\n\`\`\``
  }
  // 逐项节点的那个数组不整份附上:每一项已经单独给了,再附一遍全表只是噪音
  const deps = node.depends_on ?? []
  if (deps.length === 0) return prompt
  const sections = deps.map((id) => {
    const st = states.get(id)
    const head = `### ${st?.title ?? id} (${id})`
    const out = outputs.get(id)
    return out !== undefined
      ? `${head}\n${clip(out, UPSTREAM_CHARS_MAX)}`
      : `${head} — MISSING: this step did not finish (${st?.error ?? st?.status ?? 'unknown'}). Do not invent its result.`
  })
  return `${prompt}\n\n## Results from the steps this one depends on\n\n${sections.join('\n\n')}`
}

/**
 * 从子代理的回复里取出那段 JSON:最后一个 ``` 代码块,没有代码块就试整段回复。
 * ★ 取**最后一个**:子代理常在前面引用一段示例,真正交差的是收尾那块。
 */
function extractJson(text: string): { ok: true; value: unknown } | { ok: false; error: string } {
  const blocks = [...text.matchAll(/```(?:json)?[^\n]*\n([\s\S]*?)```/g)]
  const candidate = blocks.length > 0 ? blocks.at(-1)![1]! : text.trim()
  try {
    return { ok: true, value: JSON.parse(candidate) as unknown }
  } catch (error) {
    return {
      ok: false,
      error: `The reply did not end with a valid JSON block (${error instanceof Error ? error.message : String(error)}).`
    }
  }
}

function checkStructured(text: string, schema: z.ZodType): { ok: true; value: unknown } | { ok: false; error: string } {
  const parsed = extractJson(text)
  if (!parsed.ok) return parsed
  const result = schema.safeParse(parsed.value)
  if (result.success) return { ok: true, value: result.data }
  const issues = result.error.issues.slice(0, 5)
    .map((issue) => `${issue.path.length === 0 ? '(root)' : issue.path.join('.')}: ${issue.message}`)
  return { ok: false, error: `The JSON did not match output_schema — ${issues.join('; ')}.` }
}

/** 画布和子代理卡片上那一项叫什么:字符串取前段,对象取常见的名字字段,否则用序号 */
function itemLabel(item: unknown, index: number): string {
  if (typeof item === 'string' && item.trim() !== '') return item.trim().slice(0, 40)
  if (typeof item === 'object' && item !== null) {
    for (const key of ['name', 'title', 'id', 'path', 'file']) {
      const value = (item as Record<string, unknown>)[key]
      if (typeof value === 'string' && value.trim() !== '') return value.trim().slice(0, 40)
    }
  }
  return `#${String(index + 1)}`
}

type Attempt =
  | { ok: true; text: string; value?: unknown; attempts: number }
  | { ok: false; error: string; attempts: number }

/** 给模型的最终报告:每个节点一段,按入参顺序 */
function report(state: WorkflowState, outputs: ReadonlyMap<string, string>): string {
  const lines = [`Workflow "${state.title}" finished.`]
  for (const n of state.nodes) {
    lines.push('', `## ${n.title} (${n.id}) — ${n.status}`)
    if (n.status === 'done') lines.push(outputs.get(n.id) ?? '')
    else if (n.error !== undefined) lines.push(n.error)
  }
  return lines.join('\n')
}

async function runWorkflow(input: Input, ctx: ToolContext): Promise<ToolResult> {
  const spawn = ctx.spawnSubagent
  if (spawn === undefined) return toolFail('Subagents cannot be launched in this environment. Do the steps yourself.')

  const invalid = validateWorkflowGraph(input.nodes)
  if (invalid !== null) return toolFail(`Invalid workflow — ${invalid}`)

  // 节点钉的模型在派出任何节点之前查 —— 派到第三层才发现名字敲错,前两层就白跑了
  const models = ctx.subagentModels?.()
  if (models !== undefined) {
    const unknown = [...new Set(input.nodes.flatMap((n) => (n.model === undefined || models.includes(n.model) ? [] : [n.model])))]
    if (unknown.length > 0) {
      return toolFail(
        `Invalid workflow — unknown model ${unknown.map((m) => `"${m}"`).join(', ')}. ` +
          `Available: ${models.join(', ')}. Or omit \`model\` to use each subagent's default.`
      )
    }
  }

  // output_schema 在开跑前转成校验器 —— 一份写坏的 schema 要到节点跑完才暴露,那个节点就白跑了
  const validators = new Map<string, z.ZodType>()
  for (const n of input.nodes) {
    if (n.output_schema === undefined) continue
    try {
      validators.set(n.id, z.fromJSONSchema(n.output_schema as Parameters<typeof z.fromJSONSchema>[0]))
    } catch (error) {
      return toolFail(
        `Invalid workflow — node "${n.id}" has an output_schema that is not a usable JSON Schema: ` +
          `${error instanceof Error ? error.message : String(error)}`
      )
    }
  }

  if (ctx.interact !== undefined && ctx.experimentalEnabled?.('workflowConfirm') === true) {
    const planned = workflowFromInput(input)
    if (planned !== null) {
      const response = await ctx.interact({ kind: 'workflow_approval', callId: ctx.callId, workflow: planned })
      if (response.kind !== 'workflow_approval') return toolFail('Workflow review returned an invalid response.')
      if (response.action === 'revise') {
        return toolFail(
          `The user reviewed the workflow and asked for changes before it runs:\n\n${response.feedback ?? ''}\n\n` +
            'Revise the graph accordingly and call Workflow again.'
        )
      }
      if (response.action === 'cancel') {
        return toolFail(
          'The user cancelled this workflow before it started. Nothing was run. ' +
            'Do not call Workflow again unless the user asks; ask them how they want to proceed.'
        )
      }
    }
  }

  const now = (): number => ctx.host.clock.now()
  const outputs = new Map<string, string>()
  /** 声明了 output_schema 的节点校验过的值。逐项节点从这里取数组,不重新解析文字 */
  const values = new Map<string, unknown>()
  const states = new Map<string, WorkflowNodeState>(
    input.nodes.map((n) => [
      n.id,
      {
        id: n.id,
        title: n.title,
        agent: n.subagent_type,
        dependsOn: nodeDependencies(n),
        ...(n.allow_partial === true ? { allowPartial: true } : {}),
        ...(n.model === undefined ? {} : { model: n.model }),
        ...(n.map_over === undefined ? {} : { mapOver: n.map_over }),
        ...(n.output_schema === undefined ? {} : { structured: true }),
        status: 'pending'
      }
    ])
  )

  const snapshot = (): WorkflowState => ({ title: input.title, nodes: input.nodes.map((n) => states.get(n.id)!) })
  const set = (id: string, patch: Partial<WorkflowNodeState>): void => {
    states.set(id, { ...states.get(id)!, ...patch })
  }
  const publish = (): void => {
    const s = snapshot()
    const done = s.nodes.filter((n) => n.status === 'done').length
    const running = s.nodes.filter((n) => n.status === 'running').map((n) => n.title)
    ctx.emit({
      callId: ctx.callId,
      message: running.length > 0 ? `${String(done)}/${String(s.nodes.length)} · ${running.join(', ')}` : `${String(done)}/${String(s.nodes.length)}`,
      fraction: done / s.nodes.length,
      workflow: s
    })
  }

  /**
   * 一个还在 pending 的节点此刻该怎么办:`run` 可以派、`wait` 上游还没落定、
   * 否则是跳过的原因。
   *
   * ★ 严格节点一见上游失败就跳过,不等兄弟上游跑完 —— 结局已经定了,早点在画布上
   *   标出来。`allow_partial` 节点必须等**全部**上游落定:还在跑的那个可能正是唯一会成功的。
   */
  const readiness = (node: Node): 'run' | 'wait' | { skip: string } => {
    const deps = nodeDependencies(node).map((d) => states.get(d)!)
    // 逐项节点的数组没有就是没有,`allow_partial` 也救不了
    if (node.map_over !== undefined) {
      const source = states.get(node.map_over)!
      if (source.status === 'error' || source.status === 'skipped') {
        return { skip: `Skipped because "${source.title}", whose list this step works through, did not finish.` }
      }
    }
    if (node.allow_partial === true) {
      if (deps.some((d) => d.status === 'pending' || d.status === 'running')) return 'wait'
      return deps.length === 0 || deps.some((d) => d.status === 'done')
        ? 'run'
        : { skip: 'Skipped because none of the steps it depends on finished.' }
    }
    const broken = deps.find((d) => d.status === 'error' || d.status === 'skipped')
    if (broken !== undefined) return { skip: `Skipped because "${broken.title}" did not finish.` }
    return deps.every((d) => d.status === 'done') ? 'run' : 'wait'
  }

  /**
   * 一次带重试的派发。被拒和被转去后台不重试:前者重派一模一样会再被拒,后者根本没失败。
   * 结构化节点交了不合格的 JSON 也算失败,重试时把不合格的原因附在 prompt 后面 ——
   * 原样再派一次,子代理多半会交回同一份。
   */
  const dispatch = async (
    node: Node,
    prompt: string,
    callId: string,
    description: string,
    hooks: { onRetry?: (attempt: number) => void; onUsage: (usage: NonNullable<WorkflowNodeState['usage']>) => void }
  ): Promise<Attempt> => {
    const retries = node.retries ?? DEFAULT_RETRIES
    const schema = validators.get(node.id)
    let note = ''
    for (let attempt = 1; ; attempt++) {
      if (attempt > 1) hooks.onRetry?.(attempt)
      const req: SubagentRequest = {
        subagentType: node.subagent_type,
        prompt: note === '' ? prompt : `${prompt}\n\n## Note\n\nA previous attempt at this step was rejected: ${note} Fix that this time.`,
        description,
        // 重试换一个 callId:同一个 id 再派一次,渲染层会把第二次的子代理叠在第一次的状态上
        callId: attempt === 1 ? callId : `${callId}~${String(attempt)}`,
        ...(node.model === undefined ? {} : { model: node.model }),
        ...(node.effort === undefined ? {} : { thinking: node.effort })
      }
      const outcome = await spawn(req)
      // 中断原样抛:父 run 正在中断收尾,包成节点失败会让模型以为图还能接着用
      if (outcome.kind === 'finished' && outcome.status === 'aborted') throw abortError('The workflow was aborted')
      if (outcome.kind === 'finished' && outcome.usage !== undefined) hooks.onUsage(outcome.usage)
      if (outcome.kind === 'refused') return { ok: false, error: outcome.reason, attempts: attempt }
      if (outcome.kind === 'background') {
        // 只可能是用户把它转去了后台:结论会单独送回,这张图里不再等它
        return {
          ok: false,
          error: `Moved to the background by the user (run ${outcome.childRunId}); its report will arrive separately.`,
          attempts: attempt
        }
      }
      let why: string
      if (outcome.status === 'done' && outcome.text.trim() !== '') {
        if (schema === undefined) return { ok: true, text: outcome.text, attempts: attempt }
        const checked = checkStructured(outcome.text, schema)
        if (checked.ok) return { ok: true, text: JSON.stringify(checked.value, null, 2), value: checked.value, attempts: attempt }
        why = checked.error
        note = checked.error
      } else {
        why = outcome.status === 'done'
          ? 'The subagent finished without producing any text.'
          : typeof outcome.error === 'string' ? outcome.error : (outcome.error?.message ?? 'unknown error')
      }
      if (attempt <= retries && !ctx.signal.aborted) continue
      return { ok: false, error: attempt > 1 ? `${why} (failed ${String(attempt)} attempts)` : why, attempts: attempt }
    }
  }

  const runNode = async (node: Node): Promise<void> => {
    let usage: WorkflowNodeState['usage']
    const onUsage = (u: NonNullable<WorkflowNodeState['usage']>): void => {
      usage = addTokenUsage(usage, u)
      set(node.id, { usage })
    }
    set(node.id, { status: 'running', startedAt: now() })
    publish()
    if (node.map_over !== undefined) {
      await runMapNode(node, onUsage)
      publish()
      return
    }
    const result = await dispatch(node, composePrompt(node, outputs, states), `${ctx.callId}#${node.id}`, node.title, {
      onUsage,
      onRetry: (attempt) => {
        set(node.id, { attempts: attempt })
        publish()
      }
    })
    if (result.ok) {
      outputs.set(node.id, result.text)
      if (result.value !== undefined) values.set(node.id, result.value)
      set(node.id, { status: 'done', endedAt: now(), summary: clip(result.text.trim(), WORKFLOW_SUMMARY_MAX) })
    } else {
      set(node.id, { status: 'error', endedAt: now(), error: result.error })
    }
    publish()
  }

  /** 逐项节点:对上游数组的每一项各派一个子代理,并发交给子代理队列去限 */
  const runMapNode = async (node: Node, onUsage: (u: NonNullable<WorkflowNodeState['usage']>) => void): Promise<void> => {
    const sourceId = node.map_over!
    const source = values.get(sourceId)
    if (!Array.isArray(source)) {
      set(node.id, { status: 'error', endedAt: now(), error: `"${states.get(sourceId)!.title}" did not produce a JSON array.` })
      return
    }
    const list = source.slice(0, WORKFLOW_MAP_MAX_ITEMS)
    const dropped = source.length - list.length
    const progress: WorkflowItemsProgress = { total: list.length, done: 0, failed: 0, running: 0, ...(dropped > 0 ? { dropped } : {}) }
    const sync = (): void => {
      set(node.id, { items: { ...progress } })
      publish()
    }
    sync()
    const results = await Promise.all(list.map(async (item, index) => {
      progress.running++
      sync()
      const label = itemLabel(item, index)
      const result = await dispatch(
        node,
        composePrompt(node, outputs, states, { value: item }),
        `${ctx.callId}#${node.id}[${String(index)}]`,
        `${node.title} · ${label}`,
        { onUsage }
      )
      progress.running--
      if (result.ok) progress.done++
      else progress.failed++
      sync()
      return { label, result }
    }))

    const tail = dropped > 0
      ? `\n\nNote: the list had ${String(source.length)} items; only the first ${String(WORKFLOW_MAP_MAX_ITEMS)} were processed.`
      : ''
    if (list.length === 0) {
      outputs.set(node.id, 'The list was empty, so there was nothing to process.')
      if (validators.has(node.id)) values.set(node.id, [])
      set(node.id, { status: 'done', endedAt: now(), summary: 'The list was empty.' })
      return
    }
    const succeeded = results.filter((r) => r.result.ok)
    if (succeeded.length === 0) {
      const first = results[0]!.result
      set(node.id, {
        status: 'error',
        endedAt: now(),
        error: `All ${String(list.length)} items failed. First error: ${first.ok ? '' : first.error}`
      })
      return
    }
    let text: string
    if (validators.has(node.id)) {
      // 结构化逐项节点的产出是「每项一份」的数组,下游还可以再逐项一次
      const combined = succeeded.map((r) => (r.result.ok ? r.result.value : undefined))
      values.set(node.id, combined)
      const failed = results.filter((r) => !r.result.ok).map((r) => r.label)
      text = JSON.stringify(combined, null, 2) +
        (failed.length > 0 ? `\n\nItems that failed and are not in the array: ${failed.join(', ')}` : '') + tail
    } else {
      text = results.map((r, index) => r.result.ok
        ? `### Item ${String(index + 1)}: ${r.label}\n${r.result.text}`
        : `### Item ${String(index + 1)}: ${r.label} — FAILED: ${r.result.error}`).join('\n\n') + tail
    }
    outputs.set(node.id, text)
    set(node.id, { status: 'done', endedAt: now(), summary: clip(text.trim(), WORKFLOW_SUMMARY_MAX) })
  }

  publish()
  const inFlight = new Map<string, Promise<void>>()
  for (;;) {
    if (ctx.signal.aborted) throw abortError('The workflow was aborted')
    // 跳过沿依赖链往下传,而入参顺序不一定是拓扑序 —— 扫到不再有变化为止
    let skipped = false
    for (let changed = true; changed;) {
      changed = false
      for (const n of input.nodes) {
        if (states.get(n.id)!.status !== 'pending') continue
        const r = readiness(n)
        if (typeof r === 'object') {
          set(n.id, { status: 'skipped', error: r.skip })
          changed = true
          skipped = true
        }
      }
    }
    if (skipped) publish()
    for (const n of input.nodes) {
      if (states.get(n.id)!.status !== 'pending' || inFlight.has(n.id)) continue
      if (readiness(n) === 'run') {
        const p = runNode(n).finally(() => inFlight.delete(n.id))
        // 中断时只有第一个拒绝会被 race 接住,其余的在这里吞掉,免得报未处理的拒绝
        p.catch(() => undefined)
        inFlight.set(n.id, p)
      }
    }
    if (inFlight.size === 0) break
    await Promise.race(inFlight.values())
  }

  const final = snapshot()
  const anyDone = final.nodes.some((n) => n.status === 'done')
  return {
    output: { content: report(final, outputs), workflow: final },
    // 一个节点都没成才算整次调用失败 —— 部分成功的产出模型仍然用得上
    isError: !anyDone
  }
}

export const workflowTool = defineTool({
  internalId: 'Workflow',
  description: DESCRIPTION,
  schema: WorkflowInput,
  /* 理由同 `Task`:跑的是完整的子代理循环,能做什么由子代理定义决定,不是这个字段 */
  readOnly: false,
  destructive: false,
  needsNetwork: false,
  isEnabled: (ctx) =>
    ctx.experimentalEnabled?.('workflow') === true &&
    ctx.spawnSubagent !== undefined &&
    // 只在主 run 下发 —— 见文件头「开关」
    ctx.depth === 0,
  run: runWorkflow
})
