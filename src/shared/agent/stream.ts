/**
 * 归一化的上游事件流 —— 方案 §4.2。
 * 这是「上游那一层」的事件;往 UI 推的是 AgentEvent(见 event.ts),它是这个的超集。
 */
import type { AgentError } from './error'
import type { RunCost } from '../domain/pricing'

export type StopReason =
  | 'end_turn'
  | 'tool_use'
  | 'max_tokens'
  | 'stop_sequence'
  | 'refusal'
  | 'aborted'

export interface TokenUsage {
  inputTokens: number
  outputTokens: number
  /**
   * ★ 缓存两项不能省:开启提示缓存那天,没有它们成本显示就是错的
   * (而且是**静默**错的 —— 数字看着很合理)。
   */
  cacheCreationInputTokens?: number
  cacheReadInputTokens?: number
  /**
   * ★ 上面那个 `cacheCreationInputTokens` 是**两档写入价的和**,不够计价用:
   * Anthropic 的 1h 缓存写是 5m 的两倍价,上游在
   * `cache_creation.ephemeral_1h_input_tokens` 里单独给这个数。
   * 拿不到就留 undefined —— `priceOf` 那时把写入**全部**按 5m 计,
   * 而不是猜一个比例(猜错的方向是少收用户的钱,且没人看得出来)。
   */
  cacheCreation1hInputTokens?: number
  /**
   * Separate reasoning/thinking count when the upstream reports one. It is a
   * subset of outputTokens and must never be added to the billable total again.
   */
  reasoningTokens?: number
}

/** 两份用量逐项相加。`a` 缺省时原样返回 `b`;可选项两边都没有时保持缺省 */
export function addTokenUsage(a: TokenUsage | undefined, b: TokenUsage): TokenUsage {
  if (a === undefined) return { ...b }
  const sum = (x: number | undefined, y: number | undefined): number | undefined =>
    x === undefined && y === undefined ? undefined : (x ?? 0) + (y ?? 0)
  const out: TokenUsage = { inputTokens: a.inputTokens + b.inputTokens, outputTokens: a.outputTokens + b.outputTokens }
  const optional = ['cacheCreationInputTokens', 'cacheReadInputTokens', 'cacheCreation1hInputTokens', 'reasoningTokens'] as const
  for (const key of optional) {
    const v = sum(a[key], b[key])
    if (v !== undefined) out[key] = v
  }
  return out
}

export type ProviderStreamEvent =
  /**
   * `model` 是上游回包里的**真实模型名**(不是别名);`providerId` 由
   * `upstream/router.ts` 在转发这个事件时补上,decoder 自己不知道也不该知道。
   * 两者合起来才说得清「这段回复到底是谁给的」。
   */
  | { type: 'message_start'; model: string; providerId?: string }
  | { type: 'text_delta'; index: number; text: string }
  | { type: 'thinking_delta'; index: number; text: string }
  | { type: 'tool_call_start'; index: number; callId: string; name: string }
  | { type: 'tool_call_delta'; index: number; callId: string; argsDelta: string }
  | { type: 'tool_call_end'; index: number; callId: string }
  /**
   * ★ 厂商透传的载体 —— 没有它,message.ts 里的 `ContentPart.opaque` 是**死的**。
   *
   * Anthropic 的扩展思考把 signature 放在 `signature_delta` 里,而下一轮必须
   * 原样回传,丢了就是 400。signature 不是「内容」,塞进 thinking_delta 会污染正文;
   * 它属于**整个块**,所以按 index 单独发一条。redacted_thinking 走同一条路
   * (整块都不透明)。
   *
   * 内核不解释 opaque,只负责搬:decode → 这个事件 → ContentPart.opaque → encode。
   */
  | { type: 'block_opaque'; index: number; opaque: unknown }
  /**
   * ★ `reason` 是给用户看的那句话(上游的原文,例如「Our servers are currently
   * overloaded」)。没有它,状态行只能说「正在重试」—— 而用户真正想知道的是
   * **为什么**:上游繁忙可以等,配置错了等一万年也没用。
   */
  | { type: 'provider_retry'; attempt: number; delayMs: number; reason: string }
  | { type: 'provider_switch'; from: string; to: string; reason: string }
  /**
   * ★ `latencyMs` 是**这一次上游请求**从发出到本条消息收完的耗时,由
   * `upstream/router.ts` 在转发时补上 —— 和 `message_start.providerId` 同一个
   * 约定:decoder 不知道自己等了多久,也不该知道。
   *
   * 它是「平均 TPS」的分母:工具执行、等待授权都不在里面,所以一轮里
   * 把每次请求的它累加起来,除出来的才是模型本身的输出速度。
   *
   * ★ `cost` 是**这一次请求**的钱,和 `latencyMs` 同一处补上(`router.ts`)。
   * 三个状态各有各的含义,**不能合并**:
   *
   * - 字段不存在 = 这条链路没接计价(老转录、fake-emitter、大部分测试),
   *   累加方保持已有的累计值不动;
   * - `null` = 接了计价但**查不到价**(模型不在 `pricing-seed.ts` 里),
   *   累加方据此把整轮锁成「算不出」;
   * - 有值 = 算出来了,累加。
   *
   * ★★ 计价**必须按单次请求**做,不能等一轮结束拿累计值算:档位由该次请求的
   * 输入总量决定(越过阈值是整条请求重算,见 `priceOf` 上面那段),时段价由
   * 该次请求的发起时刻决定。拿累计值算,这两类模型都会系统性偏低。
   */
  | {
      type: 'message_end'
      stopReason: StopReason
      usage: TokenUsage
      latencyMs?: number
      cost?: RunCost | null
    }
  | { type: 'error'; error: AgentError }

/**
 * ★ index 是内容块序号,不能省(方案 §4.2)。
 *
 * 一条消息里出现「文本 → 工具调用 → 文本」或两个并行工具调用时,没有 index 就无法归位。
 * Anthropic 的 content_block_start/delta/stop 带 index 正是为此。
 * 现在加是免费的,以后加是全量重构。
 *
 * ★ adapter 契约:**必须**发 tool_call_start … tool_call_end;
 *   中间的 delta 数量**可以为 0** —— 部分上游是一次性给全整个工具调用对象的。
 *
 * ★ argsDelta 按字符串累积,**只在 tool_call_end 时 JSON.parse 一次**;
 *   失败则产出一个工具错误,而不是把异常抛出循环。流式中途的 JSON 一定是非法的。
 */

/**
 * 工具调用参数的流式累积器。放在 shared 是因为解码侧(main)和
 * 测试侧都要用同一份语义,而它是纯函数。
 */
export class ToolCallAccumulator {
  private readonly buf = new Map<string, { name: string; args: string }>()

  start(callId: string, name: string): void {
    this.buf.set(callId, { name, args: '' })
  }

  delta(callId: string, argsDelta: string): void {
    const e = this.buf.get(callId)
    if (e) e.args += argsDelta
  }

  /**
   * 只在 tool_call_end 调用。解析失败**不抛异常** —— 返回 ok:false,
   * 调用方把它变成一个工具错误继续循环。
   */
  end(callId: string): { ok: true; name: string; input: unknown } | { ok: false; name: string; raw: string; reason: string } {
    const e = this.buf.get(callId)
    if (!e) return { ok: false, name: '<unknown>', raw: '', reason: `未见 tool_call_start: ${callId}` }
    this.buf.delete(callId)
    // 空参数是合法的:无参工具的上游可能一个 delta 都不发
    const raw = e.args.trim() === '' ? '{}' : e.args
    try {
      return { ok: true, name: e.name, input: JSON.parse(raw) }
    } catch (err) {
      return { ok: false, name: e.name, raw, reason: (err as Error).message }
    }
  }

  /** 中断收尾时要知道还有哪些没闭合 */
  pending(): Array<{ callId: string; name: string }> {
    return [...this.buf.entries()].map(([callId, v]) => ({ callId, name: v.name }))
  }
}
