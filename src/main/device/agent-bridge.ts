/**
 * 远程 run 的事件桥:把设备推来的 `agent.events` 帧还原成桌面渲染层认得的 `agent:event` 信封。
 *
 * 渲染层的防漂移逻辑原样适用(信封的 `seq` 是批内最后一条的序号,断档就 `agent:attach` 补齐),
 * 所以这里不改写事件,只转发。
 *
 * ★ 设备推送的游标出现缺口时(`ControllerClient` 会把这条流标成 stale 并停止交付),
 *   桥自己用「上次转发到的 seq」向设备重新 attach,把快照里补上的事件作为**下一个连续信封**发出去,
 *   再从新的快照游标重新跟随 —— 渲染层看到的仍是首尾相接的序号。补不上(日志被截)时,
 *   渲染层会在下一个信封上看到断档并自己 attach,走的还是同一条路由。
 * ★ 切换/移除设备后旧连接上的跟随一律作废(`closeAll`),迟到的事件不会串到新设备的 run 上。
 */
import type { ControllerClient } from '../../controller-client/client'
import type { AgentEvent, RunSnapshot } from '../../shared/agent/event'
import { runTopic } from '../services/agent'

export interface AgentBridgePort {
  /** 推给订阅了这个 run 主题的窗口。 */
  emit(topic: string, envelope: { runId: string; seq: number; events: AgentEvent[] }): void
  /** 在当前所选设备上取客户端;设备不在线会抛 `disconnected`。 */
  run<T>(work: (client: ControllerClient) => Promise<T>): Promise<T>
}

interface Follow { off(): void; lastSeq: number }

export class AgentBridge {
  private readonly follows = new Map<string, Follow>()

  constructor(private readonly port: AgentBridgePort) {}

  /**
   * 订阅并取快照。`sinceSeq` 之后的事件在快照里;`forward` 为真时把它们也作为一个信封发出
   * (用于刚启动的 run:渲染层从 seq 1 开始等,而启动到 attach 之间可能已经产生了事件)。
   */
  async attach(client: ControllerClient, runId: string, sinceSeq: number, forward: boolean): Promise<RunSnapshot> {
    const snapshot = await client.query('agent.attach', { runId, sinceSeq }) as unknown as RunSnapshot
    if (forward && snapshot.events.length > 0) this.port.emit(runTopic(runId), { runId, seq: snapshot.seq, events: snapshot.events })
    this.follow(client, runId, snapshot.seq)
    return snapshot
  }

  /** 不再看这个 run:摘掉本端跟随并通知设备。尽力而为 —— 设备不在线时本端状态照样清理。 */
  async unwatch(client: ControllerClient, runId: string): Promise<void> {
    this.drop(runId)
    try { await client.query('agent.detach', { runId }) } catch { /* 设备已不可达:没有需要摘的了。 */ }
  }

  closeAll(): void {
    for (const runId of [...this.follows.keys()]) this.drop(runId)
  }

  /** 连接层报告某条流出现游标缺口。 */
  async resync(streamId: string): Promise<void> {
    if (!streamId.startsWith('run:')) return
    const runId = streamId.slice(4)
    const entry = this.follows.get(runId)
    if (entry === undefined) return
    entry.off()
    try {
      await this.port.run(async (client) => {
        // 期间被 unwatch/切换作废的话不再补。
        if (this.follows.get(runId) !== entry) return
        const snapshot = await client.query('agent.attach', { runId, sinceSeq: entry.lastSeq }) as unknown as RunSnapshot
        if (this.follows.get(runId) !== entry) return
        if (snapshot.events.length > 0) this.port.emit(runTopic(runId), { runId, seq: snapshot.seq, events: snapshot.events })
        this.follow(client, runId, snapshot.seq)
      })
    } catch {
      // 补不上(设备掉线等):放弃这条跟随;渲染层恢复在线后会自己 attach。
      if (this.follows.get(runId) === entry) this.follows.delete(runId)
    }
  }

  private follow(client: ControllerClient, runId: string, seq: number): void {
    this.drop(runId)
    const entry: Follow = { lastSeq: seq, off: () => {} }
    this.follows.set(runId, entry)
    entry.off = client.follow(`run:${runId}`, String(seq), (event) => {
      if (event.event !== 'agent.events') return
      const data = event.data as { runId?: unknown; seq?: unknown; events?: unknown }
      if (data.runId !== runId || typeof data.seq !== 'number' || !Array.isArray(data.events)) return
      entry.lastSeq = data.seq
      this.port.emit(runTopic(runId), { runId, seq: data.seq, events: data.events as AgentEvent[] })
    })
  }

  private drop(runId: string): void {
    const entry = this.follows.get(runId)
    if (entry === undefined) return
    this.follows.delete(runId)
    try { entry.off() } catch { /* 连接已断开。 */ }
  }
}
