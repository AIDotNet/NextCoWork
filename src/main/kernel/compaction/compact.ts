/**
 * 上下文压缩 —— Claude Code 的 compactConversation。
 *
 * 需求:对话长到阈值(或上游直接报「prompt 太长」,或用户 /compact)时,
 * 把「最后一个边界之后的全部对话」交给同一个模型写一份九节摘要,产出一条
 * 边界消息追加到转录末尾。从此发给模型的只有这条消息及其之后的内容
 * (`shared/agent/compaction.ts` 的 `messagesForModel`)。
 *
 * 这是**唯一**的压缩手段。原先的机械压缩(清空旧工具输出、整条丢弃留骨架)
 * 已删除:它压不动以正文为主的历史,压不动之后又判「榨干」整个 run 不再重试 ——
 * 真实会话因此从 330K 一路涨到 624K,每一轮都按长上下文计费。
 *
 * 不变式:
 * - 摘要请求发**原始对话** + 末尾一条压缩指令,不下发工具 schema。
 * - 摘要用哪个模型、哪个思考档位、多大输出额度,**由调用方决定并传进来**
 *   (三档来源在 `shared/domain/compaction-model.ts`,解析与回落在 `./binding.ts`)。
 *   本模块只负责把它们原样带进请求、并记进边界消息。
 * - 上游报 prompt 太长时从最早一侧按轮丢弃重试,最多 `MAX_PTL_RETRIES` 次(CC 同款)。
 *   丢的只是**这次摘要请求**的输入,转录本身一条不动。
 * - 失败返回 `{ ok: false }`,不抛(取消除外):失败计数、熔断、界面提示由调用方决定。
 *
 * 故意不做的:历史里的 `tool_call` / `tool_result` **不拍平成文本**,哪怕压缩模型声明
 * `capabilities.tools === false` —— 目前 OpenAI / Anthropic 两条线都接受「历史里有工具块、
 * 请求不带 tools」。拆除条件:真的遇到某家上游因此拒收,再在 `prepareForSummary` 里拍平。
 *
 * 这个模块不碰数据库、不发事件、不知道 run —— 发请求和读文件都由调用方注入,
 * 所以 session 的自动压缩和 IPC 的手动 /compact 走的是同一份代码,且能在纯单测里跑。
 */
import type { AgentError } from '../../../shared/agent/error'
import { agentError } from '../../../shared/agent/error'
import type { AgentMessage, ContentPart } from '../../../shared/agent/message'
import { userMessage } from '../../../shared/agent/message'
import type { ProviderStreamEvent } from '../../../shared/agent/stream'
import type { ThinkingLevel } from '../../../shared/agent/run-request'
import { messagesForModel, type CompactBoundary } from '../../../shared/agent/compaction'
import { parseNcwUrl } from '../../../shared/domain/attachment'
import { isAbortError } from '../abort'
import { imageUrlNote } from '../upstream/images'
import { estimateMessages, estimateTokens } from '../context-assembler'
import { buildPostCompactAttachments, type AttachmentToolNames } from './attachments'
import { compactPrompt, continuationText, formatCompactSummary } from './prompt'

/** 上游报 prompt 太长时最多重试几次。同 CC 的 `MAX_PTL_RETRIES`。 */
export const MAX_PTL_RETRIES = 3
/** 每次重试从最早一侧丢掉的比例(按消息条数)。同 CC 在拿不到 token 差值时的回退。 */
export const PTL_DROP_RATIO = 0.2
/** 同一个 run 里连续失败几次就熔断。同 CC 的 `MAX_CONSECUTIVE_AUTOCOMPACT_FAILURES`。 */
export const MAX_CONSECUTIVE_COMPACT_FAILURES = 3

export const COMPACT_SYSTEM = 'You are a helpful AI assistant tasked with summarizing conversations.'

/** 摘要请求里调用方要补的那几样之外的部分。model / provider / 路由上下文由调用方填。 */
export interface SummaryRequest {
  system: string
  messages: AgentMessage[]
  maxOutputTokens: number
  /**
   * 这次摘要下发的思考档位。
   *
   * ★ 原先这里没有这一项,`send` 的两个实现各自硬写 `'off'` —— 而 effort 模型
   * (`gpt-6-*`)的 `reasoningEfforts` 不含 `'none'`,`thinking-adapter` 会直接抛
   * 「该模型不支持关闭推理」。表现是压缩每次必败、三次熔断、上下文再也压不下去,
   * 全程零报错。档位现在由调用方按压缩模型归一化后给出(`auxiliaryThinkingLevel`)。
   */
  thinkingLevel: ThinkingLevel
}

/** 写这份摘要的是谁 —— 原样记进边界消息,给界面看。 */
export interface SummaryModel {
  model: string
  modelProviderId?: string
  /** 实际下发的档位(已按压缩模型归一化)。 */
  thinking: ThinkingLevel
  /** true = 配置的压缩模型当时解析不到,回落成了会话模型。 */
  fellBack: boolean
}

export interface CompactInput {
  /** 完整转录。本函数自己取 `messagesForModel`,调用方不要预先切。 */
  messages: readonly AgentMessage[]
  trigger: 'auto' | 'manual'
  instructions?: string
  /** 压缩前的占用(上游真值优先)。只用于记录和界面展示。 */
  preTokens: number
  /** 自动压缩 = true:续接语里要求模型接着干,别停下来问。见 `continuationText`。 */
  autoContinue: boolean
  /**
   * **压缩模型**的协议窗口(不是会话模型的)。摘要请求的输入先按它预裁一次:
   * 再往上发必然被拒,白白花一次往返才进 PTL 重试。
   *
   * ★ 压缩模型可以和会话模型不是同一个,窗口也就可能更小 —— 继续拿会话模型的窗口
   * 当分母,等于拿 1M 的预算去喂一个 128K 的压缩模型,预裁形同虚设。
   */
  protocolWindow: number
  /** 摘要这次的输出额度(调用方已按压缩模型的窗口夹过)。 */
  summaryMaxOutputTokens: number
  /** 摘要这次的思考档位(调用方已按压缩模型归一化过)。 */
  summaryThinking: ThinkingLevel
  /** 记进边界、给界面看的那一组事实。 */
  summaryModel: SummaryModel
  send: (request: SummaryRequest) => AsyncIterable<ProviderStreamEvent>
  attachments: {
    tools: AttachmentToolNames
    readFile: (path: string) => Promise<string | undefined>
  }
  newId: () => string
  now: number
  signal: AbortSignal
}

export type CompactResult =
  | { ok: true; message: AgentMessage; boundary: CompactBoundary }
  | { ok: false; error: AgentError }

export async function compactConversation(input: CompactInput): Promise<CompactResult> {
  const history = messagesForModel(input.messages)
  // 边界之后还没有任何真实对话 = 刚压过,再压一次只会拿摘要去摘要摘要。
  if (!history.some((message) => message.internal !== true)) {
    return { ok: false, error: agentError('conflict', 'Nothing to compact yet.', { retryable: false, messageKey: 'chat.compaction.nothingToCompact' }) }
  }

  let request = prepareForSummary(history)
  const inputBudget = summaryInputBudget(input)
  while (request.length > 1 && estimateMessages(request) > inputBudget) {
    const next = truncateHead(request)
    if (next === undefined) break
    request = next
  }

  let raw: string | undefined
  let lastError: AgentError | undefined
  for (let attempt = 0; attempt <= MAX_PTL_RETRIES; attempt++) {
    const result = await summarizeOnce(input, request)
    if (result.ok) {
      raw = result.text
      break
    }
    lastError = result.error
    if (result.error.code !== 'context_length') break
    const next = truncateHead(request)
    if (next === undefined) break
    request = next
  }
  if (raw === undefined) {
    return { ok: false, error: lastError ?? agentError('provider', 'Compaction returned no summary.', { retryable: true }) }
  }

  const summary = formatCompactSummary(raw)
  if (summary === '') {
    return { ok: false, error: agentError('provider', 'Compaction returned an empty summary.', { retryable: true, messageKey: 'chat.compaction.emptySummary' }) }
  }

  const attachments = await buildPostCompactAttachments({
    messages: history,
    tools: input.attachments.tools,
    readFile: input.attachments.readFile,
    signal: input.signal
  })
  if (input.signal.aborted) throw new DOMException('Aborted', 'AbortError')

  const texts: ContentPart[] = [
    { type: 'text', text: continuationText(summary, input.autoContinue) },
    ...attachments.texts.map((text): ContentPart => ({ type: 'text', text }))
  ]
  const draft = userMessage(input.newId(), texts, input.now)
  const boundary: CompactBoundary = {
    type: 'compact_boundary',
    trigger: input.trigger,
    preTokens: Math.max(0, Math.round(input.preTokens)),
    postTokens: estimateMessages([draft]),
    summary,
    ...(input.instructions === undefined || input.instructions.trim() === '' ? {} : { instructions: input.instructions.trim() }),
    ...(attachments.restoredFiles.length === 0 ? {} : { restoredFiles: attachments.restoredFiles }),
    summaryModel: {
      model: input.summaryModel.model,
      ...(input.summaryModel.modelProviderId === undefined ? {} : { modelProviderId: input.summaryModel.modelProviderId }),
      thinking: input.summaryModel.thinking,
      // 没有回落就不写这个键 —— 边界是落盘的历史记录,一个恒为 false 的字段会在
      // 每一条边界上占位,而它要表达的是一件**例外**发生过。
      ...(input.summaryModel.fellBack ? { fellBack: true } : {})
    }
  }
  /*
    ★ `internal: true`:这条是我们替用户写的,聊天界面只画分隔线,不当成用户气泡;
    它仍然是 user 角色,因为摘要要以「用户给的上下文」的身份出现在模型面前 —— 放成
    assistant 的话,下一轮模型会以为这些话是自己说过的,复述而不是接着干。
  */
  const message: AgentMessage = { ...draft, internal: true, parts: [boundary, ...texts] }
  return { ok: true, message, boundary }
}

/**
 * 摘要请求的输入还能占多少 token:压缩模型的窗口 − 这次的输出额度 − 压缩指令本身。
 *
 * ★ 必须封底。输出额度现在跟随全局设置(默认 32K),而压缩模型可能是个 8K / 32K 窗口的
 * 小模型 —— 那时上面这个减法是**负数**,`while` 会一路 `truncateHead` 到只剩一条消息,
 * 然后拿这一条去写「整段对话的摘要」。表现是压缩成功、摘要却只提到最后一句话,
 * 而且一个报错都没有。封底到窗口的一半:宁可发出去被上游拒(那是一条能看懂的错),
 * 也不要静默地摘要一段空气。
 */
function summaryInputBudget(input: CompactInput): number {
  const budget = input.protocolWindow - input.summaryMaxOutputTokens - estimateTokens(compactPrompt(input.instructions))
  return budget > 0 ? budget : Math.floor(input.protocolWindow / 2)
}

async function summarizeOnce(
  input: CompactInput,
  history: readonly AgentMessage[]
): Promise<{ ok: true; text: string } | { ok: false; error: AgentError }> {
  const request: SummaryRequest = {
    system: COMPACT_SYSTEM,
    messages: withCompactInstruction(history, compactPrompt(input.instructions), input.now),
    maxOutputTokens: input.summaryMaxOutputTokens,
    thinkingLevel: input.summaryThinking
  }
  let text = ''
  try {
    for await (const ev of input.send(request)) {
      if (ev.type === 'text_delta') text += ev.text
      if (ev.type === 'error') return { ok: false, error: ev.error }
    }
  } catch (error) {
    if (isAbortError(error) || input.signal.aborted) throw error
    return { ok: false, error: agentError('provider', error instanceof Error ? error.message : String(error), { retryable: true }) }
  }
  return { ok: true, text }
}

/**
 * 摘要请求的输入:原始对话去掉模型用不上、又可能让请求非法的东西。
 *
 * - 图片(含工具截图)换成一句占位:摘要不需要看图,而图片是最贵的输入;
 *   CC 同样在压缩前剥图。
 * - 思考块去掉。原先的理由是「摘要请求不开思考,带着签名过的旧思考块发过去部分上游会拒」;
 *   现在摘要**可能**开思考(档位跟随会话,见文件头),但这一条照样成立且更强了:
 *   那些签名是**会话模型**在别的轮次里生成的,而写摘要的可能根本是另一个模型 ——
 *   带着别人的签名块过去,上游拒得更干脆。
 * - 剥完变成空消息的整条去掉(只含图片的 user 消息)。
 */
export function prepareForSummary(history: readonly AgentMessage[]): AgentMessage[] {
  const out: AgentMessage[] = []
  for (const message of history) {
    const parts: ContentPart[] = []
    for (const part of message.parts) {
      if (part.type === 'thinking') continue
      if (part.type === 'image') {
        /*
          托管图(`ncw://`)占位里带上地址:摘要是压缩后模型唯一的记忆,
          地址丢了,它就再也点不了名(改图、`SaveImage`)。内联图没有地址可带。
        */
        parts.push({ type: 'text', text: parseNcwUrl(part.dataRef) === null ? '[image]' : `[image] ${imageUrlNote(part.dataRef)}` })
        continue
      }
      if (part.type === 'tool_result' && part.output.images !== undefined) {
        const { images: _images, ...output } = part.output
        parts.push({ ...part, output })
        continue
      }
      parts.push(part)
    }
    if (parts.length > 0) out.push({ ...message, parts })
  }
  return out
}

/**
 * 从最早一侧丢掉约 `PTL_DROP_RATIO` 的消息,切在「一轮的开头」。
 *
 * ★ 切点必须是一条**不含 tool_result 的 user 消息**:切在 tool_result 前面,
 * 剩下的开头就是一个没有配对 tool_call 的回执,上游直接判请求非法 —— 于是 PTL
 * 重试换来的是另一种失败。
 *
 * ★ 开头若是上一次的边界消息(上一份摘要),**保留它**,只丢它后面的:
 * 丢了它等于把更早那一段的全部记忆一起扔掉。
 *
 * 返回 undefined = 已经丢不动了。
 */
export function truncateHead(messages: readonly AgentMessage[]): AgentMessage[] | undefined {
  const keepHead = messages[0]?.parts.some((part) => part.type === 'compact_boundary') === true ? 1 : 0
  const body = messages.length - keepHead
  if (body <= 1) return undefined
  const target = keepHead + Math.max(1, Math.floor(body * PTL_DROP_RATIO))
  for (let i = target; i < messages.length; i++) {
    const message = messages[i]
    if (message?.role === 'user' && !message.parts.some((part) => part.type === 'tool_result')) {
      return [...messages.slice(0, keepHead), ...messages.slice(i)]
    }
  }
  return undefined
}

/**
 * 在对话末尾加上压缩指令。
 *
 * ★ 末尾已经是 user 消息(通常是一条 tool_result 回执)时**并进去**而不是另起一条:
 * 连续两条 user 消息有的上游会拒,有的会悄悄合并,行为不一致。
 */
function withCompactInstruction(history: readonly AgentMessage[], prompt: string, now: number): AgentMessage[] {
  const last = history.at(-1)
  const instruction: ContentPart = { type: 'text', text: prompt }
  if (last?.role === 'user') {
    return [...history.slice(0, -1), { ...last, parts: [...last.parts, instruction] }]
  }
  return [...history, userMessage('compact:instruction', [instruction], now)]
}
