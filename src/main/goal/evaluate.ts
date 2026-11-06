/**
 * 判定调用 —— 一次**无工具、禁思考、只读对话**的旁路模型请求。
 *
 * 形状照抄压缩那条旁路调用（原先是 `agent-session.ts` 的 `createContextCheckpoint`，
 * 上下文压缩重写后改叫 `kernel/compaction/compact.ts` 的 `summarizeOnce`；那条调用已经验证过），
 * 差别只在输出契约：这里要的是一个 `{ok, reason, impossible}` 的 JSON 结论。
 *
 * ★ 上游端口是**注入**的（`GoalEvaluatorPort`），不 import `getRouter()` ——
 *   这一份因此是可单测的：喂一个十几行的假 stream 就能把四种结论各跑一遍，
 *   不启动 Electron、不联网。
 *
 * ★ 任何失败都收敛成 `skipped`，**不**收敛成 `not_met`：把工具的故障算在用户的
 *   目标头上，会让一次供应商抖动变成一条「你的目标没达成」。
 *
 * ## 三条正确性硬约束（都不是风格问题）
 *
 * 1. **一次判定只有一个墙钟。** 两次尝试（0.5 窗口 → `context_length` → 0.25 窗口）
 *    共用同一个 `AbortController` 和同一个定时器。各给一个的话，最坏要等两倍超时，
 *    而它挂在回合末 —— 用户看到的只是「这一轮就是不结束」。
 * 2. **只有完整的 `end_turn` 才算一次回复。** 手上的那段 JSON 可能是写完的，但流断在
 *    半路、或止于 `max_tokens` / `tool_use` 时，那是一个**被打断的**回答；拿它当结论
 *    与「拿被中断的证据判达成」是同一件事，一律 `skipped`。
 * 3. **中断之后绝不返回 `met`。** 判定器的全部价值在于它独立确认了一遍；中断让这一点
 *    不再成立。宁可 `skipped`，也不让用户那一下「停止」变成一条「目标已达成」。
 */
import type { AgentMessage, ContentPart } from '../../shared/agent/message'
import type { ProviderStreamEvent } from '../../shared/agent/stream'
import type { GoalVerdict } from '../../shared/domain/goal'
import { parseGoalVerdict } from '../../shared/domain/goal'
import type { ModelAlias } from '../../shared/domain/provider'
import { auxiliaryThinkingLevel } from '../../shared/domain/model-runtime'
import type { ThinkingLevel } from '../../shared/agent/run-request'
import { PROMPT_HOOK_DEFAULT_TIMEOUT_MS } from '../../shared/domain/hook'
import { userMessage } from '../../shared/agent/message'
import { ulid } from '../../shared/util/id'
import { abortableStream } from '../kernel/abort'
import { estimateTokens } from '../kernel/context-assembler'
import type { CanonicalRequest, UpstreamRequestContext } from '../kernel/upstream/canonical'
import {
  GOAL_EVALUATOR_SYSTEM,
  renderTranscriptLine,
  truncatedTranscriptPrefix
} from './prompt'

/** 判定器需要上游的全部能力 —— 就这两个方法（同 `SessionUpstream` 的取向）。 */
export interface GoalEvaluatorPort {
  stream(
    req: CanonicalRequest,
    signal: AbortSignal,
    context: UpstreamRequestContext
  ): AsyncIterable<ProviderStreamEvent>
  resolveModel(model: string, modelProviderId?: string): ModelAlias | undefined
}

export interface EvaluateGoalInput {
  upstream: GoalEvaluatorPort
  /** 判定模型。空串 = 回落到 `fallbackModel`。 */
  model: string
  modelProviderId?: string
  /** 本次 run 的模型 —— 判定模型没配时的回落。 */
  fallbackModel: string
  fallbackModelProviderId?: string
  /** 判定的问题。goal 走 `goalEvaluatorQuestion(condition)`；prompt 钩子走用户写的那段。 */
  question: string
  /** 判定器读的转录。 */
  messages: readonly AgentMessage[]
  context: UpstreamRequestContext
  signal: AbortSignal
  timeoutMs?: number
  /**
   * 时钟。只用来给这次请求的 id / createdAt 打时间戳。
   *
   * ★ 超时**不**走它：墙钟由 `setTimeout` 管（见文件头第 1 条）。注入的时钟推进了
   *   而真时间没走，那不算超时；反过来也一样。
   */
  now?: () => number
}

/** 判定器读多少上下文：模型窗口 × 这个比例。窗口未知时按 1e6 当大窗口算。 */
const TRANSCRIPT_BUDGET_RATIO = 0.5
/** 第一次裁完仍然 `context_length` 时的重试比例。 */
const TRANSCRIPT_RETRY_RATIO = 0.25
const ASSUMED_LARGE_WINDOW = 1_000_000
/** 判定器只要一个 JSON 对象，1K 绰绰有余；给多了只会让它写小作文。 */
const MAX_OUTPUT_TOKENS = 1024

export async function evaluateGoal(input: EvaluateGoalInput): Promise<GoalVerdict> {
  const model = input.model.trim() === '' ? input.fallbackModel.trim() : input.model.trim()
  // ★ 模型和供应商是**成对**的：回落了模型就必须同时回落 providerId，
  //   否则请求会带着「本次 run 的模型 + 判定模型那家的 providerId」出去。
  const providerId = input.model.trim() === '' ? input.fallbackModelProviderId : input.modelProviderId
  if (model === '') return { kind: 'skipped', reason: 'no_model' }
  const alias = input.upstream.resolveModel(model, providerId)
  if (alias === undefined) return { kind: 'skipped', reason: 'no_model' }
  if (input.messages.length === 0) return { kind: 'skipped', reason: 'transcript_empty' }
  /*
    ★ 已经中断的调用**一次请求都不发**。

    这一条必须显式判断：`addEventListener('abort')` 在一个**已经** abort 的 signal 上
    永远不会触发，所以下面那个转发监听器兜不住这种情形 —— 漏掉它，被中断的回合末
    照样会打一次上游。
  */
  if (input.signal.aborted) return { kind: 'skipped', reason: 'error' }

  const window = Math.min(alias.contextWindow ?? ASSUMED_LARGE_WINDOW, ASSUMED_LARGE_WINDOW)
  /*
    需求:判定器要的是一个 JSON 结论,能不思考就不思考(快、便宜)。但**不能硬发 `'off'`**:
    `gpt-6-*` 这类 effort 模型的 `reasoningEfforts` 不含 `'none'`,`thinking-adapter` 会
    直接抛「该模型不支持关闭推理」,router 把它包成不可重试的错 —— 表现是每个回合末的
    判定全部 `skipped`,而用户看到的只是「目标永远判不出来」。
    `auxiliaryThinkingLevel` 在关不掉的模型上降到最低可用档,绝不抛。
    压缩、权限审核、会话标题四处共用这一个规则。
  */
  const wire = {
    thinking: auxiliaryThinkingLevel('off', alias),
    // 输出额度夹一次窗口:一个声明了极小窗口的条目上,1024 的输出本身就可能越界。
    maxOutputTokens: Math.max(1, Math.min(MAX_OUTPUT_TOKENS, window))
  }

  // ── 整个判定只有一个墙钟（含两次尝试）───────────────────────────────
  const controller = new AbortController()
  const timeoutMs = Math.max(0, input.timeoutMs ?? PROMPT_HOOK_DEFAULT_TIMEOUT_MS)
  let timedOut = false
  const timer = setTimeout(() => {
    timedOut = true
    controller.abort()
  }, timeoutMs)
  const onAbort = (): void => controller.abort()
  input.signal.addEventListener('abort', onAbort, { once: true })
  const control: AttemptControl = { controller, timedOut: () => timedOut }

  try {
    const first = await runOnce(
      input,
      model,
      providerId,
      transcriptBudget(window, TRANSCRIPT_BUDGET_RATIO, input.question, wire.maxOutputTokens),
      control,
      wire
    )
    if (first.kind !== 'retry_smaller') return first.verdict
    /*
      ★ 裁到一半仍然 `context_length`：不是「历史太长」，是我们的估算偏低（长 CJK、
        大段 JSON 工具输出都会把字符/词元比拉低）。按 0.25 再来一次，仍然不行就认输 ——
        第三次只会是同一个结果，而每一次都是一次真实的上游请求。

      ★ 第二次之前先看墙钟：定时器可能就在这两次之间到点了，而那时 `stream()`
        是一次注定被丢弃的请求，一个字节都不该发出去。
    */
    if (controller.signal.aborted) return abortOrErrorVerdict(input, control)
    const second = await runOnce(
      input,
      model,
      providerId,
      transcriptBudget(window, TRANSCRIPT_RETRY_RATIO, input.question, wire.maxOutputTokens),
      control,
      wire
    )
    return second.kind === 'retry_smaller' ? { kind: 'skipped', reason: 'error' } : second.verdict
  } finally {
    clearTimeout(timer)
    input.signal.removeEventListener('abort', onAbort)
  }
}

/**
 * 这次判定能给转录多少 token。
 *
 * ★ system / question / 输出**都要从窗口里先扣掉**：它们和转录抢的是同一个窗口，
 *   漏扣的那部分会以「上游报 context_length」的形式回来 —— 而那正好触发一次
 *   白白多打的上游请求。
 */
function transcriptBudget(window: number, ratio: number, question: string, maxOutputTokens: number): number {
  const reserved =
    estimateTokens(GOAL_EVALUATOR_SYSTEM) + estimateTokens(question) + maxOutputTokens
  return Math.max(0, Math.floor(window * ratio) - reserved)
}

/** 两次尝试共用的那点状态：同一个 controller，同一个「到点了没有」。 */
interface AttemptControl {
  controller: AbortController
  /** 墙钟到点时它也已经被 abort；两者合成一个结论需要分开看（超时 vs 调用方中断）。 */
  timedOut: () => boolean
}

type Attempt = { kind: 'done'; verdict: GoalVerdict } | { kind: 'retry_smaller' }

async function runOnce(
  input: EvaluateGoalInput,
  model: string,
  providerId: string | undefined,
  budgetTokens: number,
  control: AttemptControl,
  /** 这个判定模型能接受的档位与输出额度 —— 在 `evaluateGoal` 里算一次,两次尝试共用。 */
  wire: { thinking: ThinkingLevel; maxOutputTokens: number }
): Promise<Attempt> {
  const now = input.now ?? ((): number => Date.now())
  const selection = selectTranscript(input.messages, budgetTokens)
  /*
    ★ 预算里一条证据都留不下时**不发请求**。

    发出去的那份会是一条**只有问题、没有转录**的判定 —— 判定器对它的回答必然是
    「证据不足」，而那在 Stop 上表现为「这一轮永远停不下来」。不如直接跳过，
    让调用方知道这次没判成。
  */
  if (selection.keptLines === 0) {
    return { kind: 'done', verdict: { kind: 'skipped', reason: 'transcript_empty' } }
  }

  const prompt = `${selection.text}\n\n${input.question}`
  const request: CanonicalRequest = {
    model,
    ...(providerId === undefined ? {} : { modelProviderId: providerId }),
    system: GOAL_EVALUATOR_SYSTEM,
    messages: [userMessage(ulid(now()), [{ type: 'text', text: prompt }], now())],
    tools: [],
    maxOutputTokens: wire.maxOutputTokens,
    thinkingLevel: wire.thinking
  }

  let text = ''
  let ended = false
  try {
    // ★ 上游端口未必认 signal（假流、某些 SDK）。`abortableStream` 补上这一层：
    //   即便生成器完全不看 signal，超时/中断也能立刻从这里脱身。
    const stream = abortableStream(
      input.upstream.stream(request, control.controller.signal, input.context),
      control.controller.signal
    )
    for await (const event of stream) {
      switch (event.type) {
        case 'text_delta':
          text += event.text
          break
        case 'message_end':
          // 只有完整的 `end_turn` 才算一次可判的回复，见文件头第 2 条。
          ended = event.stopReason === 'end_turn'
          break
        case 'error':
          return event.error.code === 'context_length'
            ? { kind: 'retry_smaller' }
            : { kind: 'done', verdict: { kind: 'skipped', reason: 'error' } }
        default:
          break
      }
    }
  } catch {
    // 中断、超时、上游抛错 —— 三种都收敛成 skipped，绝无可能是 met。
    return { kind: 'done', verdict: abortOrErrorVerdict(input, control) }
  }

  /*
    ★ 收官前最后一道闸：流跑完了、JSON 也拿到了，但**调用方在这中间中断了**（或墙钟
      到点了）。这时候那份材料是完整的、结论却已经不该有了 —— 谁先发生都不能算数，
      因为判定结果最终会变成「目标已达成」这类不可撤销的陈述。
  */
  if (control.controller.signal.aborted) {
    return { kind: 'done', verdict: abortOrErrorVerdict(input, control) }
  }
  if (!ended) {
    /*
      ★ 没有 `message_end`，或止于 `max_tokens` / `tool_use` / `refusal`：
        手上那段 JSON 可能恰好是完整的，但那次回答**没有结束**。判定器没有工具，
        出现 `tool_use` 只说明上游没照契约走；被截断的回答同样不能当证据。
    */
    return { kind: 'done', verdict: { kind: 'skipped', reason: 'error' } }
  }
  return { kind: 'done', verdict: parseGoalVerdict(text) }
}

/**
 * 一次失败该记成什么。
 *
 * ★ 超时与「调用方中断」必须分开：前者是「对面没反应」，要进诊断（用户据此去查
 *   那一家的连通性）；后者是用户按了停止，不是故障，记进诊断只会制造噪音。
 */
function abortOrErrorVerdict(input: EvaluateGoalInput, control: AttemptControl): GoalVerdict {
  if (input.signal.aborted) return { kind: 'skipped', reason: 'error' }
  return control.timedOut()
    ? { kind: 'skipped', reason: 'timeout' }
    : { kind: 'skipped', reason: 'error' }
}

/**
 * 转录 → 判定器读的那段文本，按预算从后往前裁。
 *
 * ★ 裁掉的部分**替换成一条显式前缀**，不能静默丢：见 `truncatedTranscriptPrefix`
 *   上那段 —— 不写的话判定器会把「我看不到」读成「没发生过」。
 *
 * ★ 按**回合**边界裁，不按消息、更不按字符：从一条 tool_result 的中间切一刀，
 *   留下的半截 JSON 比不留更糟（判定器会把它当成一次失败的工具调用）。
 */
export function renderTranscript(messages: readonly AgentMessage[], budgetTokens: number): string {
  return selectTranscript(messages, budgetTokens).text
}

interface TranscriptSelection {
  /** 拼好的正文（含截断前缀）。预算里一条证据都留不下时只剩那条前缀。 */
  text: string
  /** 留下来的证据**行**数。0 = 这次判定没有可读的证据，调用方据此直接 skipped。 */
  keptLines: number
}

/** 一个回合：从一条「带非 tool_result 语义块的 user 消息」开始，到下一个这样的消息为止。 */
interface TranscriptTurn {
  /** 这个回合渲染出来的行。空数组 = 这个回合没有可读内容（只有 thinking / goal_status）。 */
  lines: string[]
  /** 这个回合覆盖的原始消息条数 —— 前缀里那个 N 用的就是它。 */
  messages: number
}

function selectTranscript(
  messages: readonly AgentMessage[],
  budgetTokens: number
): TranscriptSelection {
  const candidates = transcriptTurns(messages).filter((turn) => turn.lines.length > 0)
  // 整条转录里没有一个可读的块（UI-only 的 thinking / goal_status 不算）
  if (candidates.length === 0) return { text: '', keptLines: 0 }

  const budget = Math.max(0, budgetTokens)
  const total = messages.length
  let kept: TranscriptTurn[] = []
  for (let i = candidates.length - 1; i >= 0; i--) {
    const turn = candidates[i]
    if (turn === undefined) break
    const next = [turn, ...kept]
    /*
      ★ 前缀本身也占预算（`estimateTokens` 算的是最终发出去的那整段）：不算它，
        一条刚好卡在预算上的转录会多出近百个 token —— 而超发的代价是一次
        `context_length` 重试。

      ★ 从后往前**贪心**，一遇到装不下就停：留下的因此永远是「最近的若干个完整回合」。

      ★★ 最新那一个回合**自己**就装不下时，`kept` 停在空 —— 整份转录都不发。
        这一点是刻意的：只发更旧的回合，等于让判定器拿着一份**缺了最新一轮**的
        证据去判「达成」，而那条前缀说的是「更早的那些被裁掉了」，方向正好相反。
        宁可这次不判（调用方看到 `transcript_empty`），也不给一份误导性的材料。
    */
    const keptAll = next.length === candidates.length
    const omitted = keptAll ? 0 : total - sumMessages(next)
    if (estimateTokens(composeTranscript(next, omitted)) > budget) break
    kept = next
  }

  const keptAll = kept.length === candidates.length
  const omitted = keptAll ? 0 : total - sumMessages(kept)
  return {
    text: composeTranscript(kept, omitted),
    keptLines: kept.reduce((n, turn) => n + turn.lines.length, 0)
  }
}

function composeTranscript(turns: readonly TranscriptTurn[], omitted: number): string {
  const body = turns.flatMap((turn) => turn.lines).join('\n')
  if (omitted <= 0) return body
  const prefix = truncatedTranscriptPrefix(omitted)
  return body === '' ? prefix : `${prefix}\n${body}`
}

function sumMessages(turns: readonly TranscriptTurn[]): number {
  return turns.reduce((n, turn) => n + turn.messages, 0)
}

function transcriptTurns(messages: readonly AgentMessage[]): TranscriptTurn[] {
  const starts: number[] = []
  messages.forEach((message, i) => {
    if (startsTurn(message)) starts.push(i)
  })

  const turns: TranscriptTurn[] = []
  /*
    ★ 第一条回合起点之前的内容（通常是些没配对上的 tool_result）自成一组，但它
       **不是**一个回合边界 —— 保留后缀永远不从它开始，否则截出来的第一行就是一条
       「没人调用的工具输出」，判定器会把它读成一次失败。
  */
  if (starts[0] !== 0) turns.push(collectTurn(messages, 0, starts[0] ?? messages.length))
  starts.forEach((from, k) => {
    turns.push(collectTurn(messages, from, starts[k + 1] ?? messages.length))
  })
  return turns
}

function collectTurn(messages: readonly AgentMessage[], from: number, to: number): TranscriptTurn {
  const lines: string[] = []
  for (let i = from; i < to; i++) {
    const message = messages[i]
    if (message === undefined) continue
    const body = bodyOf(message)
    // 只有 UI-only 的块（thinking / goal_status）的消息不占行，也不占预算
    if (body !== '') lines.push(renderTranscriptLine(message.role, body))
  }
  return { lines, messages: to - from }
}

/**
 * 一条消息是不是一个**回合的起点**。
 *
 * ★ 判据不能只是 `role === 'user'`：工具回执也是 user 消息（见 `message.ts` 里
 *   `toolResultMessage` 上那段）。把它们当成回合起点，裁剪就会从一条孤立的
 *   tool_result 切起 —— 而那正是上面 `transcriptTurns` 要避免的那件事。
 */
function startsTurn(message: AgentMessage): boolean {
  return (
    message.role === 'user' &&
    message.parts.some((part) => part.type !== 'tool_result' && partBody(part) !== '')
  )
}

/**
 * 一条消息里判定用得上的内容。
 *
 * ★ `thinking` **不进**：它是模型的草稿。一句「我觉得应该已经好了」会被判定器
 *   当成证据，而那恰恰是最不该采信的证据 —— 判定器的全部价值就在于它独立于
 *   模型的自我评价。`goal_status` 同理（那是我们自己盖的章），`image` / `error`
 *   也一样（一个我们送不上去，一个是本地故障）。
 */
function bodyOf(message: AgentMessage): string {
  return message.parts
    .map(partBody)
    .filter((s) => s !== '')
    .join('\n')
}

/**
 * 一个内容块的正文。
 *
 * ★ tool_result 带上 **callId 与错误状态**：判定器要能把这行输出归因到那一次
 *   调用上。只给一段裸输出的话，一次失败的工具调用和一次成功的看起来一模一样，
 *   而「`bun test` 报了错」和「`bun test` 全绿」是两个相反的结论。
 */
function partBody(part: ContentPart): string {
  switch (part.type) {
    case 'text':
      return part.text
    case 'tool_call':
      return `${part.name} ${safeJson(part.input)}`
    case 'tool_result':
      return `${part.callId} ${part.isError ? '[error]' : '[ok]'} ${part.output.content}`
    case 'subagent':
      return part.summary ?? ''
    case 'file_ref':
      return part.path
    default:
      return ''
  }
}

function safeJson(value: unknown): string {
  try {
    return JSON.stringify(value) ?? ''
  } catch {
    return ''
  }
}
