/**
 * 源会话转录 → 一段给「提炼 Skill」会话读的 Markdown 摘要。
 *
 * 需求:从一段做完的业务改动里沉淀项目 Skill(`kernel/skill/extraction.ts`)。
 * 提炼 agent 需要知道**用户要了什么、最后怎么做成的、中间哪里走错了、怎么验证的** ——
 * 这些散落在几十上百条消息、工具调用和工具输出里,而整段原样塞进去必然爆窗口。
 *
 * 不变式:
 * - 用**完整转录**,不走 `messagesForModel`:压缩边界之前的原始内容恰恰是经验所在,
 *   只拿边界之后那段,提炼出来的 Skill 会只剩「收尾那几步」。
 * - **最后一轮**始终按满档渲染;超预算时按档位逐级降低**更早轮次**的细节。
 *   user 消息在档位内按 `user` 上限裁剪,但最低档也放不下时**会被整轮丢掉**——
 *   这里原先写着「所有 user 消息永远保留」,是错的:旧兜底从头部切片,最早那几轮
 *   (user 消息正在那里)最先没掉。现在改成从最新一轮往回装,并如实报出丢了几轮。
 * - **输出永不超预算**:省略标记本身也占 token(不满足会怎样:预算极小时标记自己先超预算,
 *   调用方拿到的「≤ 预算」是假的);预算是有限非负数,所有循环都有界。
 * - **失败的工具结果**比成功的多留 —— 踩坑信息是 Skill 里最值钱、也最容易被
 *   「总结」掉的部分(不满足会怎样:写出来的 Skill 只有正确路径,下一次还踩同一个坑)。
 * - 输出是纯文本,永不 throw;降档到最后仍超预算时按轮丢弃并标注。
 *
 * 性能:同一段转录要在 5 个档位下各渲染一遍,而正则 sanitize 与逐码点 token 估算
 * 是长转录上的两处大头。所以**先按最高档把每个片段 sanitize 并限长一次**(`prepareTurn`),
 * 之后每档只做字符串裁剪;明显超预算的档位连估算都不跑(`fitsBudget`)。
 *
 * 故意不做的:
 * - 不下发 `thinking`:体积大、是模型的中间推理,且部分上游是加密的 opaque。
 * - 不下发图片:只留 `[image]` 占位,不外发 base64。
 * - 不调模型做摘要:这里是确定性的裁剪,真正的归纳由提炼 agent 自己做
 *   (它还要回头读代码核对,一次额外的模型调用换不来什么)。
 */
import type { AgentMessage, ContentPart } from '../../../shared/agent/message'
import { isToolResultOnly } from '../../../shared/agent/message'
import { estimateTokens } from '../context-assembler'
import { clampWithEllipsis, stripAnsi, stripControlChars } from '../text'
import { neutralizeReminderTags } from '../untrusted'

/** 预算的绝对上限。见 `digestBudget`。 */
export const DIGEST_MAX_TOKENS = 60_000
/** 预算占模型协议窗口的比例 —— 剩下的留给系统提示词、读代码的工具输出和回答。 */
export const DIGEST_WINDOW_RATIO = 0.35

/**
 * 这次摘要能用多少 token。
 * 需求:小窗口模型(128K)不能被一段 60K 的摘要吃掉一半,它还要读代码核对。
 */
export function digestBudget(contextWindow: number | undefined): number {
  if (contextWindow === undefined || !Number.isFinite(contextWindow) || contextWindow <= 0) return DIGEST_MAX_TOKENS
  return Math.max(4_000, Math.min(DIGEST_MAX_TOKENS, Math.floor(contextWindow * DIGEST_WINDOW_RATIO)))
}

type ToolResultPart = Extract<ContentPart, { type: 'tool_result' }>

/** 一种细节档位。数字全是字符数上限;0 = 整段不出现。 */
interface Detail {
  user: number
  assistant: number
  /** 工具入参里「正文型」字段(写入内容、old/new 片段)的上限 */
  toolBody: number
  /** 其余入参字段的上限 */
  toolArg: number
  okResult: number
  errorResult: number
  /** false = 这一轮只留 user 消息和一行「省略了 N 次工具调用」 */
  tools: boolean
}

const FULL: Detail = { user: 4_000, assistant: 4_000, toolBody: 800, toolArg: 300, okResult: 600, errorResult: 1_500, tools: true }

/**
 * 降档顺序。★ 只作用于**非最后一轮**:最后一轮通常是最终方案与验证,永远按 FULL 渲染。
 * 顺序的理由:成功输出最不值钱(代码可以回头再读),其次是 agent 的叙述,失败输出最后才砍。
 * ★ 最低档把 user 上限收到 1_500:它保的是「一轮问题别整段消失」,不是「永不裁剪」——
 *   真正一点都放不下时,整轮由 `renderOverflow` 丢弃并计入省略标记。
 */
const LEVELS: readonly Detail[] = [
  FULL,
  { ...FULL, okResult: 0 },
  { ...FULL, okResult: 0, assistant: 1_200, toolBody: 300, toolArg: 160, errorResult: 800 },
  { ...FULL, okResult: 0, assistant: 400, toolBody: 0, toolArg: 120, errorResult: 400 },
  { ...FULL, okResult: 0, assistant: 200, toolBody: 0, toolArg: 0, errorResult: 0, tools: false, user: 1_500 }
]

/** 入参里这些字段是「正文」:写进文件的内容、编辑前后的片段。 */
const BODY_FIELDS = new Set(['content', 'old_string', 'new_string', 'old_str', 'new_str', 'patch', 'diff', 'text'])

/**
 * 最低限度的密钥打码。
 * ★ 这是兜底,不是保证:提示词里同样要求 agent 不把密钥写进 Skill。
 * 规则宁可漏也不误伤正文 —— 误伤的代价是摘要里的代码片段莫名其妙缺了一截。
 */
const SECRET_RULES: ReadonlyArray<[RegExp, string]> = [
  [/\bsk-[A-Za-z0-9_-]{16,}/g, 'sk-[REDACTED]'],
  [/\bgh[pousr]_[A-Za-z0-9]{20,}/g, 'gh_[REDACTED]'],
  [/\bAKIA[0-9A-Z]{16}\b/g, 'AKIA[REDACTED]'],
  [/\b(Bearer\s+)[A-Za-z0-9._~+/=-]{16,}/gi, '$1[REDACTED]'],
  [/\b((?:password|passwd|secret|api[_-]?key|access[_-]?token)\s*[=:]\s*)(["']?)[^\s"'&,;]{4,}\2/gi, '$1[REDACTED]']
]

export function redactSecrets(text: string): string {
  let out = text
  for (const [re, replacement] of SECRET_RULES) out = out.replace(re, replacement)
  return out
}

/** 裁剪标记。`[truncated]` 是输出里「这里被砍过」的唯一信号,别改成不可见的写法。 */
const TRUNCATION_MARK = ' …[truncated]'

function clip(text: string, max: number): string {
  return clampWithEllipsis(text.trim(), max, TRUNCATION_MARK)
}

/**
 * 与 `clip` 同源,但顺带回答「这一刀真的砍掉东西了吗」——`truncated` 标志靠它变诚实。
 *
 * ★ 满档限长之后再按低档裁,结果与一开始就按低档裁**逐字相同**:两边都是
 *   「trim → 取前 max-marker.length 个字符 → 补同一个 marker」。所以片段可以只准备一次
 *   (见 `Piece`),而各档的上限都必须 ≤ 用来准备的那一档,见 `LEVELS`。
 */
function bounded(text: string, max: number): { text: string; clipped: boolean } {
  const trimmed = text.trim()
  return { text: clampWithEllipsis(trimmed, max, TRUNCATION_MARK), clipped: trimmed.length > max }
}

function oneLine(text: string): string {
  return text.replace(/\s+/g, ' ').trim()
}

/** 工具入参来自 JSON.parse,理论上没有环 —— 但类型是 unknown,不赌(同 context-assembler 的 safeJson) */
function safeJson(value: unknown): string {
  try {
    return JSON.stringify(value) ?? ''
  } catch {
    return ''
  }
}

/**
 * ★ 一律**先 sanitize 再裁剪**,顺序不能反。反过来时正则只看得到被裁掉一半的密钥
 * (`sk-` + 15 个字符就匹配不上 `sk-[A-Za-z0-9_-]{16,}`),摘要里会留下真密钥的前半截。
 */
function sanitize(text: string): string {
  return neutralizeReminderTags(redactSecrets(stripControlChars(text)))
}

// ─────────────────── 预渲染:每个片段只 sanitize / 限长一次 ───────────────────

/**
 * 一个准备好的片段:文本**已 sanitize**,并已按**最高档(FULL)**限长。
 * 需求:同一段转录要在多个档位下反复渲染;sanitize 的几轮正则和 `Object.entries`
 * 都是按全文做的,放在档位循环里就是重复劳动。
 */
type Piece =
  | { readonly kind: 'user'; readonly text: string }
  | { readonly kind: 'assistant'; readonly text: string }
  | { readonly kind: 'compaction'; readonly text: string }
  | { readonly kind: 'subagent'; readonly text: string }
  /** 定长片段:`[image]`、`[file: x]`、`Run error` —— 长度不随档位变 */
  | { readonly kind: 'fixed'; readonly text: string }
  | { readonly kind: 'tool'; readonly name: string; readonly args: PreparedArgs; readonly result: PreparedResult | undefined }

/** 工具入参:非对象入参整段是一个值;对象入参拆成「正文型字段」与「其余字段」。 */
type PreparedArgs =
  | { readonly kind: 'value'; readonly text: string }
  | { readonly kind: 'object'; readonly body: readonly Field[]; readonly inline: readonly Field[] }

/** 一个入参字段。已 sanitize 并限长到最高档,渲染时只按当前档位再裁一次。 */
interface Field {
  readonly key: string
  readonly value: string
}

interface PreparedResult {
  readonly label: 'ok' | 'ERROR'
  /** 已 sanitize 的正文,限长到最高档 */
  readonly body: string
  /**
   * sanitize 之后、**未**裁剪的字符数。
   * 最低档只留 `→ ok (N chars)` 这一行,这是「省掉了多少」的唯一线索 ——
   * 用 sanitize 后的长度,因为那才是这份摘要本来会包含的正文。
   */
  readonly chars: number
}

interface PreparedTurn {
  readonly pieces: readonly Piece[]
  /** 满档渲染时就已经裁掉过内容 —— 摘要并不完整,`truncated` 必须为 true */
  readonly clipped: boolean
}

function prepareArgs(input: unknown): { args: PreparedArgs; clipped: boolean } {
  if (input === null || typeof input !== 'object' || Array.isArray(input)) {
    const json = safeJson(input)
    if (json === '') return { args: { kind: 'value', text: '' }, clipped: false }
    const prepared = bounded(sanitize(json), FULL.toolArg)
    return { args: { kind: 'value', text: prepared.text }, clipped: prepared.clipped }
  }
  const body: Field[] = []
  const inline: Field[] = []
  let clipped = false
  for (const [key, value] of Object.entries(input as Record<string, unknown>)) {
    const safeKey = sanitize(key)
    if (typeof value === 'string' && BODY_FIELDS.has(key)) {
      const prepared = bounded(sanitize(value), FULL.toolBody)
      clipped = clipped || prepared.clipped
      if (prepared.text !== '') body.push({ key: safeKey, value: prepared.text })
      continue
    }
    const rendered = typeof value === 'string' ? value : safeJson(value)
    // 需求:分开准备键和值时不能丢失 password=... 的语义,否则独立的密码值匹配不到正则。
    const sanitized = /^(?:password|passwd|secret|api[_-]?key|access[_-]?token)$/iu.test(safeKey.trim())
      ? '[REDACTED]'
      : sanitize(rendered)
    const prepared = bounded(oneLine(sanitized), FULL.toolArg)
    clipped = clipped || prepared.clipped
    if (prepared.text !== '') inline.push({ key: safeKey, value: prepared.text })
  }
  return { args: { kind: 'object', body, inline }, clipped }
}

function prepareResult(result: ToolResultPart | undefined): { result: PreparedResult | undefined; clipped: boolean } {
  if (result === undefined) return { result: undefined, clipped: false }
  const summary = result.subagent?.summary
  const body = summary !== undefined && summary.trim() !== '' ? summary : stripAnsi(result.output.content)
  const sanitized = sanitize(body)
  const prepared = bounded(sanitized, result.isError ? FULL.errorResult : FULL.okResult)
  return {
    result: { label: result.isError ? 'ERROR' : 'ok', body: prepared.text, chars: sanitized.length },
    clipped: prepared.clipped
  }
}

/** 按一个档位拼工具入参。★ 这里不再 parse / stringify 入参,片段是准备好的。 */
function renderArgs(args: PreparedArgs, detail: Detail): string {
  if (args.kind === 'value') {
    if (detail.toolArg === 0 || args.text === '') return ''
    return ` ${clip(args.text, detail.toolArg)}`
  }
  const lines: string[] = []
  const inline: string[] = []
  if (detail.toolBody > 0) {
    for (const field of args.body) lines.push(`  ${field.key}:\n  \`\`\`\n${clip(field.value, detail.toolBody)}\n  \`\`\``)
  }
  if (detail.toolArg > 0) {
    for (const field of args.inline) inline.push(`${field.key}=${clip(field.value, detail.toolArg)}`)
  }
  return (inline.length === 0 ? '' : ` ${inline.join(', ')}`) + (lines.length === 0 ? '' : `\n${lines.join('\n')}`)
}

function renderResult(result: PreparedResult | undefined, detail: Detail): string {
  if (result === undefined) return '  → (no result recorded)'
  const limit = result.label === 'ERROR' ? detail.errorResult : detail.okResult
  if (limit === 0) return `  → ${result.label} (${result.chars} chars)`
  return `  → ${result.label}: ${clip(result.body, limit).replace(/\n/g, '\n    ')}`
}

interface Turn {
  messages: AgentMessage[]
}

/** 从一条非纯工具结果的 user 消息起算一轮。内部协调消息不开新轮(压缩摘要除外,见下)。 */
function splitTurns(messages: readonly AgentMessage[]): Turn[] {
  const turns: Turn[] = []
  for (const message of messages) {
    const opens = message.role === 'user' && !isToolResultOnly(message)
    const current = turns.at(-1)
    if (opens || current === undefined) turns.push({ messages: [message] })
    else current.messages.push(message)
  }
  return turns
}

function isCompactionMessage(message: AgentMessage): boolean {
  return message.parts.some((part) => part.type === 'compact_boundary')
}

/**
 * ★ `internal` 消息只保留压缩摘要那一种。
 * 其余 internal(后台子代理回传、目标唤醒)是协调噪音,混进来会让提炼 agent
 * 把「系统在协调」误当成「用户在要求」。而压缩摘要可能是边界之前**唯一**剩下的
 * 信息(老会话导入时原始转录已不全),丢掉它就丢掉了前半段经验。
 */
function keep(message: AgentMessage): boolean {
  return message.internal !== true || isCompactionMessage(message)
}

/** 定长片段的通用上限:只有运行错误这一种,不随档位变。 */
const RUN_ERROR_CHARS = 600

/**
 * 准备一轮:sanitize 与最高档限长在这里做完,后面几个档位只做裁剪。
 * 需求:同一段转录要在 `LEVELS` 的每一档各渲染一遍,不先准备就是同一份正则跑 5 遍。
 */
function prepareTurn(turn: Turn, results: ReadonlyMap<string, ToolResultPart>): PreparedTurn {
  const pieces: Piece[] = []
  let clipped = false
  for (const message of turn.messages) {
    for (const part of message.parts) {
      switch (part.type) {
        case 'text': {
          const isUser = message.role === 'user'
          const prepared = bounded(sanitize(part.text), isUser ? FULL.user : FULL.assistant)
          clipped = clipped || prepared.clipped
          if (prepared.text === '') break
          pieces.push(isUser ? { kind: 'user', text: prepared.text } : { kind: 'assistant', text: prepared.text })
          break
        }
        case 'compact_boundary': {
          // 用 summary 而不是同条消息里那段 text:后者还拼着续接语和重附的文件正文
          const prepared = bounded(sanitize(part.summary), FULL.user)
          clipped = clipped || prepared.clipped
          pieces.push({ kind: 'compaction', text: prepared.text })
          break
        }
        case 'tool_call': {
          // 工具名、入参的键都在这里 sanitize:它们是源会话里的数据,不是我们拼的字面量
          const args = prepareArgs(part.input)
          const result = prepareResult(results.get(part.callId))
          clipped = clipped || args.clipped || result.clipped
          pieces.push({ kind: 'tool', name: sanitize(part.name), args: args.args, result: result.result })
          break
        }
        case 'subagent': {
          if (part.summary === undefined) break
          const prepared = bounded(sanitize(part.summary), FULL.errorResult)
          clipped = clipped || prepared.clipped
          pieces.push({ kind: 'subagent', text: prepared.text })
          break
        }
        case 'image':
          pieces.push({ kind: 'fixed', text: '[image]' })
          break
        case 'file_ref':
          pieces.push({ kind: 'fixed', text: `[file: ${sanitize(part.name)}]` })
          break
        case 'error': {
          const prepared = bounded(sanitize(part.error.message), RUN_ERROR_CHARS)
          clipped = clipped || prepared.clipped
          pieces.push({ kind: 'fixed', text: `**[Run error]:** ${prepared.text}` })
          break
        }
        // thinking / tool_result(随调用渲染)/ goal_status:不单独出现
        default:
          break
      }
    }
  }
  return { pieces, clipped }
}

/** 按一个档位渲染一轮。★ 这里不 sanitize、不 parse:片段都是准备好的,只做裁剪。 */
function renderTurn(turn: PreparedTurn, index: number, detail: Detail): string {
  const out: string[] = [`### Turn ${index + 1}`]
  let omittedCalls = 0
  for (const piece of turn.pieces) {
    switch (piece.kind) {
      case 'user':
        out.push(`**User:** ${clip(piece.text, detail.user)}`)
        break
      case 'assistant':
        if (detail.assistant > 0) out.push(`**Assistant:** ${clip(piece.text, detail.assistant)}`)
        break
      case 'compaction':
        out.push(`**[Earlier history, compacted summary]:** ${clip(piece.text, detail.user)}`)
        break
      case 'subagent':
        if (detail.tools) out.push(`- subagent: ${clip(piece.text, detail.errorResult > 0 ? detail.errorResult : 200)}`)
        break
      case 'fixed':
        out.push(piece.text)
        break
      case 'tool':
        if (!detail.tools) { omittedCalls++; break }
        out.push(`- tool \`${piece.name}\`${renderArgs(piece.args, detail)}`)
        out.push(renderResult(piece.result, detail))
        break
    }
  }
  if (omittedCalls > 0) out.push(`- (${omittedCalls} tool calls omitted)`)
  return out.join('\n')
}

function stripCompactionText(message: AgentMessage): AgentMessage {
  if (!isCompactionMessage(message)) return message
  return { ...message, parts: message.parts.filter((part) => part.type === 'compact_boundary') }
}

export interface SessionDigest {
  text: string
  /**
   * 摘要不完整:有细节被降过档、有片段被裁过(**含满档下的单片段裁剪**)、或有整轮被丢弃。
   * ★ 满档裁片段也算 —— 早先只看「有没有降档」,于是满档里被裁掉一半的工具输出
   *   会让调用方以为拿到的是完整摘要。
   */
  truncated: boolean
}

/** 轮次之间的分隔。预算记账时它也算 token,别在这里改成变长写法而不改记账。 */
const SEPARATOR = '\n\n'

/**
 * 一个字符最少值多少 token,用来在跑逐码点估算之前先否掉明显超预算的档位。
 * 不写死 1/4:「4 字符 = 1 token」是 `context-assembler` 估算器的内部事实,
 * 这里用估算器自己量一次(拉丁字符最便宜),常量改了这里跟着改。
 * ★ 这是**下界**,只用来拒绝:拒错了只是多降一档,接受错了就是真超预算。
 */
const MIN_TOKENS_PER_CHAR = estimateTokens('x'.repeat(64)) / 64

/**
 * 预算规范化。这是导出函数的入参,NaN / 负数 / Infinity 都得有确定行为:
 * 负数与 NaN 收成 0(输出为空),+Infinity 视为不设上限 ——
 * 旧实现里负预算会一路切到空串,最后把省略标记原样返回,标记本身都没进预算。
 */
function normalizeBudget(budgetTokens: number): number {
  if (Number.isNaN(budgetTokens) || budgetTokens <= 0) return 0
  if (!Number.isFinite(budgetTokens)) return Number.MAX_SAFE_INTEGER
  return Math.floor(budgetTokens)
}

/** 是否放得下。★ 先算字符数下界,明显超了就不必逐码点扫全文。 */
function fitsBudget(text: string, budget: number): boolean {
  // 代理对让 text.length 偏大,于是这个下界对 emoji 密集的文本偏保守:
  // 极端情况下会多降一档,不会超预算。
  if (text.length * MIN_TOKENS_PER_CHAR > budget) return false
  return estimateTokens(text) <= budget
}

/**
 * 把文本裁进 token 预算,保留 `keep` 指定的一端。
 * ★ 二分而不是固定比例裁剪:原先按 10% 逐步裁剪会粗略丢掉仍能装入的内容。
 * 估算对前缀/后缀长度单调(少几个字符只会少算 token),所以二分成立;定位次数 O(log n)。
 */
function fitToBudget(text: string, budget: number, keep: 'head' | 'tail'): string {
  if (budget <= 0) return ''
  if (estimateTokens(text) <= budget) return text
  const slice = (length: number): string => (keep === 'head' ? text.slice(0, length) : text.slice(text.length - length))
  let fits = 0
  let tooLong = text.length
  while (tooLong - fits > 1) {
    const mid = fits + Math.floor((tooLong - fits) / 2)
    if (estimateTokens(slice(mid)) <= budget) fits = mid
    else tooLong = mid
  }
  return slice(fits)
}

/**
 * 丢了几轮就写几轮。
 * ★ 旧实现固定写「earlier turns omitted」:预算小到只剩最后一轮、或一轮都不剩时,
 *   这句话是假的 —— 读摘要的 agent 会把「确实没保留」误当成「保留在别处」。
 */
function omissionMarker(omitted: number, total: number): string {
  return `…[${omitted} of ${total} turns omitted]`
}

/**
 * 兜底:最低档也放不下(极长会话,或最后一轮本身就是巨块)。
 * ★ 从**最新**一轮往回装:收尾的方案与验证比最早那几轮值钱。
 * 旧实现是从头部按 10% 反复切片、每刀都重估剩余全文 token,且会切断轮次边界。
 */
function renderOverflow(prepared: readonly PreparedTurn[], last: string, floorEarlier: readonly string[], budget: number): SessionDigest {
  const total = prepared.length
  const lastIndex = total - 1
  // 省略标记自己也要占 token,先从预算里预留最长的那种写法(位数不会超过总轮数)
  const reserve = estimateTokens(omissionMarker(total, total)) + estimateTokens(SEPARATOR)
  const contentBudget = budget - reserve
  const picked: string[] = []
  if (contentBudget > 0) {
    let used = 0
    for (let index = lastIndex; index >= 0; index--) {
      // ★ 最后一轮与档位循环里一样按 FULL 渲染
      const rendered = index === lastIndex ? last : (floorEarlier[index] as string)
      const cost = estimateTokens(rendered) + (picked.length === 0 ? 0 : estimateTokens(SEPARATOR))
      if (used + cost > contentBudget) break
      picked.unshift(rendered)
      used += cost
    }
    if (picked.length === 0) {
      // 最后一轮整轮都放不下:留它的尾部(最终方案与验证在结尾),它仍算「收下了一轮」
      const tail = fitToBudget(last, contentBudget, 'tail')
      if (tail !== '') picked.push(tail)
    }
  }
  if (picked.length === 0) return { text: fitToBudget(omissionMarker(total, total), budget, 'head'), truncated: true }
  // 这里只统计整轮省略;单轮会话也可能只保留末尾,此时 omitted 为 0,但仍然标记摘要不完整。
  const omitted = total - picked.length
  // 记账用「各段估算之和 ≥ 拼接后的估算」,所以这个拼接一定还在预算内
  return { text: `${omissionMarker(omitted, total)}${SEPARATOR}${picked.join(SEPARATOR)}`, truncated: true }
}

export function renderSessionDigest(messages: readonly AgentMessage[], budgetTokens: number): SessionDigest {
  const budget = normalizeBudget(budgetTokens)
  // 需求:空预算不能连「空会话」占位都超额输出,也不应再扫描整段历史。
  if (budget === 0) return { text: '', truncated: messages.length > 0 }
  const kept = messages.filter(keep).map(stripCompactionText)
  const results = new Map<string, ToolResultPart>()
  for (const message of kept) {
    for (const part of message.parts) if (part.type === 'tool_result') results.set(part.callId, part)
  }
  const turns = splitTurns(kept)
  if (turns.length === 0) {
    const empty = '(the source conversation is empty)'
    return { text: fitToBudget(empty, budget, 'head'), truncated: estimateTokens(empty) > budget }
  }

  // 每个片段只 sanitize / 限长一次,档位循环里不再碰正则与入参 JSON
  const prepared = turns.map((turn) => prepareTurn(turn, results))
  const clippedAtFull = prepared.some((turn) => turn.clipped)
  const lastIndex = prepared.length - 1
  const last = renderTurn(prepared[lastIndex] as PreparedTurn, lastIndex, FULL)

  let floorEarlier: readonly string[] = []
  for (let level = 0; level < LEVELS.length; level++) {
    const detail = LEVELS[level] as Detail
    const earlier = prepared.slice(0, lastIndex).map((turn, index) => renderTurn(turn, index, detail))
    if (level === LEVELS.length - 1) floorEarlier = earlier
    const text = [...earlier, last].join(SEPARATOR)
    if (fitsBudget(text, budget)) return { text, truncated: level > 0 || clippedAtFull }
  }
  return renderOverflow(prepared, last, floorEarlier, budget)
}
