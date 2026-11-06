import type { AgentMessage } from '../../../../shared/agent/message'
import { fileRefMarkdown } from '../../../../shared/agent/message'
import { REQUEST_PATH } from '../../../../shared/domain/baseurl'
import type { ReasoningReplay } from '../../../../shared/domain/provider'
import type { CanonicalRequest } from '../canonical'
import { record } from '../decode/openai-common'
import type { EncodedRequest, UpstreamEncodeOptions } from './anthropic'

/**
 * 转录 → Responses 的 `input` 数组。
 *
 * `replay` 是**回传方言**(官方 OpenAI 剥掉推理正文 / DeepSeek 要求正文全文回传),
 * 由路由器判定后传进来(见 `reasoningReplayFor`)。默认 `opaque-only`:它既是官方那侧
 * 的约束,也是「没人声明过」时唯一不会让整轮 400 的形状。
 */
export function toOpenAIResponsesInput(
  messages: readonly AgentMessage[],
  replay: ReasoningReplay = 'opaque-only'
): unknown[] {
  const input: unknown[] = []
  for (const message of messages) {
    let content: unknown[] = []
    const flush = (): void => {
      if (content.length === 0) return
      input.push({ role: message.role, content: message.role === 'assistant'
        ? content.map((part) => stringText(part)).join('') : content })
      content = []
    }
    for (const part of message.parts) {
      switch (part.type) {
        case 'goal_status':
        case 'compact_boundary':
          break // UI-only: do not flush or create an input item.
        case 'text':
          if (part.text !== '') content.push({ type: message.role === 'user' ? 'input_text' : 'output_text', text: part.text })
          break
        case 'image':
          content.push({ type: 'input_image', image_url: part.dataRef, detail: 'auto' })
          break
        case 'file_ref':
          // ★ 不读文件、不传字节 —— 只把路径当 markdown 链接告诉模型,它自己用工具去读。
          content.push({ type: message.role === 'user' ? 'input_text' : 'output_text', text: fileRefMarkdown(part) })
          break
        case 'thinking': {
          const opaque = record(part.opaque)
          const item = record(opaque?.item)
          const carrier = opaque?.protocol === 'openai-responses' && item !== undefined && item.type === 'reasoning'
            ? reasoningInputItem(item, replay)
            : undefined
          /*
            ★★ **历史思考怎么回传,按上游方言分流**(判据见 `reasoningReplayFor`)。

            `text-required`(DeepSeek 思考模式)把它写成了硬校验:带 tools 的请求,
            历史每一轮的 reasoning_text 必须完整回传,缺失即 400 ——
            `The \`reasoning_text\` in the thinking mode must be passed back to the API`。

            而 opaque 只是回传的载体,不是思考正文的唯一来源。三种情况下旧逻辑
            (按协议键门控、只认 opaque)会一个字节都不上行,表现是**界面上思考好好的、
            转录也完整,请求却 400,且本地全程零报错**:
            - 流在 `output_item.done` 之前被打断,或网关不发 reasoning 的 done 事件;
            - 会话里混进另一条协议产生的思考轮次(中途换过模型/供应商),协议键对不上;
            - 上游 item 经上面的字段白名单过滤后没剩下任何正文(只剩 id 之类)。
            这三种情况全文都还在 `part.text` 里 —— 这条分支的需求就是把它补回上行链路。
            合成/补全出来的只带 content:不编造 id、不搬 encrypted_content
            (那是上游签发给它自己的密文,换个服务商毫无意义)。

            `opaque-only`(官方 OpenAI)是**反过来的硬校验**,而且更硬:输入侧对
            reasoning item 的 `content` 上限是 **0**,带非空正文会让整轮 400 ——
            `Invalid 'input[N].content': array too long. Expected an array with maximum
            length 0`(`array_above_max_length`)。所以这一支要**剥掉 content**
            (见 `reasoningInputItem`),也不再拿 `part.text` 合成或补全正文 ——
            合成出来的 item 一样带正文,一样 400。原先那句「有正文的 item 一个字节
            都不改,官方那侧验证过的」只对 `summary` / `encrypted_content` 成立、
            对 `content` 恰好相反;现在的代价是少一段推理上下文(官方本来也靠
            `encrypted_content` 续链),比整个会话发不出去小得多。

            ★ 出事的 item 会**留在转录里被每一轮重放**:只要有一个,这个会话之后
            每轮都发不出去(`input[N]` 的 N 还随历史长度漂移)。所以清洁只能放在编码期、
            不能只在解码期 —— 这样存量转录下一轮自动不再触发,不需要数据迁移。
          */
          if (replay === 'text-required') {
            if (carrier !== undefined) {
              flush()
              input.push(!reasoningCarriesText(carrier) && part.text !== ''
                ? { ...carrier, content: [{ type: 'reasoning_text', text: part.text }] }
                : carrier)
            } else if (part.text !== '') {
              flush()
              input.push({ type: 'reasoning', content: [{ type: 'reasoning_text', text: part.text }] })
            }
          } else if (carrier !== undefined && hasReplayableReasoningId(carrier)) {
            flush()
            input.push(carrier)
          }
          break
        }
        case 'tool_call':
          flush()
          input.push({ type: 'function_call', call_id: part.callId, name: part.name, arguments: JSON.stringify(part.input ?? {}) })
          break
        case 'tool_result': {
          flush()
          const images = part.output.images ?? []
          input.push({
            type: 'function_call_output',
            call_id: part.callId,
            output: images.length === 0
              ? part.output.content
              : [
                  { type: 'input_text', text: part.output.content },
                  ...images.map((image) => ({ type: 'input_image', image_url: image.dataRef, detail: 'auto' }))
                ]
          })
          break
        }
        // ★ `error` / `goal_status` / `subagent` 只属于 UI 那一轨 —— 落到 default,
        //   一个字节都不上行(理由见 `encode/anthropic.ts` 里对应的那两条 return null)。
        default:
          break
      }
    }
    flush()
  }
  return input
}

/**
 * 回传 reasoning item 时,只带**输入侧**认得的那几个字段。
 *
 * ★★ `block_opaque` 里存的是上游原样的 **output** item,除了要搬运的密文,
 * 还带着 `status: 'completed'` 这类只属于输出侧的字段。官方接口对它宽容,
 * 第三方网关会直接 400(`Unknown parameter: 'input[246].status'`)——
 * 而且是**整段历史都带着它**,一旦出现,这个会话之后每一轮都发不出去。
 *
 * ★ 白名单而不是把 `status` 单独删掉:网关以后再挑剔别的输出字段,
 * 不会把我们打回同一个坑。反过来,`encrypted_content` 少传一个字节,
 * 无状态续轮就丢掉整条推理链,所以这几个字段一个都不能漏。
 *
 * ★★ `content`(推理正文)**只对 `text-required` 放行**:
 * 官方 OpenAI 的输入侧给它的上限是 0(症状见 `case 'thinking'` 那段注释),
 * 原样搬运等于替上游造一个每轮必 400 的 item。原先「一个都不能漏」那句是照
 * DeepSeek 的硬校验写的,对官方恰好反了 —— 现在按方言分。
 */
const REASONING_INPUT_FIELDS = ['type', 'id', 'summary', 'content', 'encrypted_content'] as const

function reasoningInputItem(item: Record<string, unknown>, replay: ReasoningReplay): Record<string, unknown> {
  const out: Record<string, unknown> = {}
  for (const key of REASONING_INPUT_FIELDS) {
    if (key === 'id' && !hasReplayableReasoningId(item)) continue
    if (key === 'content' && replay !== 'text-required') continue
    if (item[key] !== undefined) out[key] = structuredClone(item[key])
  }
  return out
}

/**
 * Responses 输入侧的推理 id 必须是非空且不超过 64 字符的字符串。
 *
 * 部分上游返回过 428 字符的 id,原样回传会让后续每轮都被 400 拒绝。
 * 不截断或哈希上游签发的标识,避免伪造引用:opaque-only 跳过无法回传的 item;
 * text-required 允许无 id 的正文载体,只去掉无效 id,正文仍按原有规则保留或补全。
 * 只在编码期过滤,不改转录,这样存量会话重试时也能恢复。
 */
function hasReplayableReasoningId(item: Record<string, unknown>): boolean {
  const id = item['id']
  return typeof id === 'string' && id.length > 0 && id.length <= 64
}

/**
 * `text-required` 回传物里还有没有**上游读得动的**推理正文。
 *
 * 需求:没有的话,调用方必须用转录里的 `part.text` 补 `content`,否则思考模式的
 * 上游会以「reasoning_text 必须回传」400 拒掉整轮(见 `case 'thinking'` 那段注释)。
 *
 * ★ 判成「有」的三种载体各有各的读者:`content` 是纯文本、`summary` 和
 * `encrypted_content` 只对签发它们的那家有意义 —— 但共同点是**回传物里确实带着
 * 上游给过的推理信息**,原样发出去就是零改动路径。补 content 的判断只需要回答
 * 「这条 item 是不是空的」,不需要分辨对面是谁:编码器看不到供应商,那是 router
 * 那一层的事实(它把结论经 `replay` 传进来)。
 */
function reasoningCarriesText(item: Record<string, unknown>): boolean {
  if (typeof item['encrypted_content'] === 'string' && item['encrypted_content'] !== '') return true
  for (const key of ['content', 'summary']) {
    const parts = item[key]
    if (!Array.isArray(parts)) continue
    for (const part of parts) {
      const text = record(part)?.text
      if (typeof text === 'string' && text !== '') return true
    }
  }
  return false
}

function stringText(value: unknown): string {
  const text = record(value)?.text
  return typeof text === 'string' ? text : ''
}

export function encodeOpenAIResponses(
  req: CanonicalRequest,
  model: string,
  apiKey: string,
  options?: UpstreamEncodeOptions
): EncodedRequest {
  return {
    path: REQUEST_PATH['openai-responses'],
    headers: { 'content-type': 'application/json', authorization: `Bearer ${apiKey}` },
    body: {
      model,
      // 需求:回传方言由路由器判定后经 options 传入 —— 编码器看不到供应商,
      // 见 `reasoningReplayFor` 与 `case 'thinking'` 两处注释。
      input: toOpenAIResponsesInput(req.messages, options?.reasoningReplay),
      ...(req.system === '' ? {} : { instructions: req.system }),
      max_output_tokens: req.maxOutputTokens,
      stream: true,
      store: false,
      // Retain encrypted reasoning for stateless tool continuation, including ZDR accounts.
      include: ['reasoning.encrypted_content'],
      ...(req.temperature === undefined ? {} : { temperature: req.temperature }),
      ...(req.tools.length === 0 ? {} : {
        tools: req.tools.map((tool) => ({
          type: 'function', name: tool.externalName, description: tool.description,
          parameters: tool.inputSchema, strict: false
        })),
        tool_choice: 'auto'
      })
    }
  }
}
