/**
 * 把 Agent 的事件泵接到已认证连接上。
 *
 * 实现 `AgentDeliveryPort`:`RunPump` 按 run 主题合批后调用 `emitToTopic`,这里按主题
 * 转给订阅它的连接。没有订阅者时 `hasSubscribers` 为 false,泵整批丢弃 —— run 照跑、
 * 转录照写,控制端回来用 `agent.attach` 重放(与桌面同一套语义)。
 *
 * ★ 一个连接的推送失败(已关闭、撤销、帧过大)只影响它自己,不影响 run,也不影响
 *   同主题的其他订阅者。
 */
import type { AgentDeliveryPort, AgentEventEnvelopePort, GlobalAgentChannel, GlobalAgentEventMap } from '../../main/services/agent'
import type { DeviceConnectionPort } from '../../main/services/request-context'
import type { DeviceValue } from '../../shared/device/value'

type Sink = (payload: AgentEventEnvelopePort) => void
type GlobalSink = (channel: GlobalAgentChannel, payload: GlobalAgentEventMap[GlobalAgentChannel]) => void

export class DeviceEventHub implements AgentDeliveryPort {
  private readonly topics = new Map<string, Set<Sink>>()
  private readonly globals = new Set<GlobalSink>()

  hasSubscribers(topic: string): boolean {
    return (this.topics.get(topic)?.size ?? 0) > 0
  }

  emitToTopic(topic: string, _channel: 'agent:event', payload: AgentEventEnvelopePort): void {
    for (const sink of [...(this.topics.get(topic) ?? [])]) {
      try { sink(payload) } catch { /* 单个连接失败不影响其他订阅者。 */ }
    }
  }

  emitGlobal<K extends GlobalAgentChannel>(channel: K, payload: GlobalAgentEventMap[K]): void {
    for (const sink of [...this.globals]) {
      try { sink(channel, payload) } catch { /* 同上。 */ }
    }
  }

  dropTopic(topic: string): void {
    this.topics.delete(topic)
  }

  subscribe(topic: string, sink: Sink): () => void {
    const set = this.topics.get(topic) ?? new Set<Sink>()
    set.add(sink)
    this.topics.set(topic, set)
    return () => {
      set.delete(sink)
      if (set.size === 0 && this.topics.get(topic) === set) this.topics.delete(topic)
    }
  }

  subscribeGlobal(sink: GlobalSink): () => void {
    this.globals.add(sink)
    return () => { this.globals.delete(sink) }
  }
}

/**
 * 订阅一个 run 并把批次推成事件帧。`initialCursor` 是控制端已经拥有的 seq(快照的 `seq`)。
 *
 * 一批一帧;一帧超过协商上限时拆成逐条帧(每条的 cursor 是它自己的 seq)。
 * 单条仍然过大的事件会被网关跳过,控制端在下一帧会发现缺口并 resync。
 */
export function followRunEvents(hub: DeviceEventHub, connection: DeviceConnectionPort, topic: string,
  streamId: string, initialCursor: number): void {
  let last = initialCursor
  const unsubscribe = hub.subscribe(topic, (payload) => {
    const first = payload.seq - payload.events.length + 1
    // 已经在快照里的批次不重发;部分重叠的批次只发新的那一段。
    if (payload.seq <= last) return
    const events = first <= last ? payload.events.slice(last - first + 1) : payload.events
    const lastSeq = payload.seq
    // 事件里可能有 undefined/类实例;经 JSON 规范化后才是合法的设备值。
    const plain = JSON.parse(JSON.stringify(events)) as DeviceValue[]
    const frame = (cursor: number, previous: number, batch: DeviceValue[]): 'sent' | 'too_large' | 'closed' =>
      connection.push({ streamId, cursor: String(cursor), previousCursor: String(previous), event: 'agent.events',
        data: { runId: payload.runId, seq: cursor, events: batch } })
    const outcome = frame(lastSeq, last, plain)
    if (outcome === 'too_large' && plain.length > 1) {
      // 拆成逐条帧:每条的 cursor 就是它自己的 seq。单条仍过大会被跳过,控制端据此 resync。
      let previous = last
      const base = lastSeq - plain.length + 1
      plain.forEach((item, index) => { frame(base + index, previous, [item]); previous = base + index })
    }
    last = lastSeq
  })
  connection.subscribe(streamId, unsubscribe)
}
