/**
 * 远程终端桥:把桌面渲染层的终端频道转到所选设备上的真实 PTY。
 *
 * 渲染层的终端 id 是它自己生成的标签 id(重新挂载时复用同一个 id 去重连);设备上的终端 id 由设备生成。
 * 桥维护 `渲染层 id → 设备终端 + 输入租约` 的映射,并:
 *  - 创建者自动持有设备上的输入租约;桥每隔一段时间**续租**,否则租约过期后输入会被设备拒绝;
 *  - 订阅设备的 `terminal:<id>` 流,把输出以 `terminal:data` / `terminal:exit` 推给订阅该终端的窗口;
 *  - 输入/改尺寸(桌面是 send 频道,没有回程)失败时只记日志,不抛 —— 和桌面本地行为一致。
 *
 * ★ 不支持自定义 cwd:设备上的终端固定在工作区根。要求别的 cwd 就拒绝,而不是悄悄换目录。
 * ★ 设备推送出现游标缺口时,本端重新 attach 并从新游标继续;缺口期间的输出要等标签重新挂载
 *   (`terminal:getBuffer`)才会补回 —— 这是已知限制,不是悄悄丢弃后假装完整。
 * ★ 切换/移除设备后所有映射与续租作废(`closeAll`);设备上的终端本身不受影响(终端属于设备)。
 */
import type { ControllerClient } from '../../controller-client/client'
import { DeviceProtocolError } from '../../shared/device/errors'
import type { TerminalBuffer, TerminalCreateRequest, TerminalInfo } from '../../shared/domain/terminal'

export interface TerminalBridgePort {
  emitData(localId: string, payload: { id: string; seq: number; chunk: string }): void
  emitExit(localId: string, payload: { id: string; code: number }): void
  run<T>(work: (client: ControllerClient) => Promise<T>): Promise<T>
  warn(message: string, error: unknown): void
}

/** 窗口侧的主题订阅(见 `broker.ts` 的 `WindowTopics`,这里只取需要的那一半避免循环依赖)。 */
export interface TerminalWindow { subscribe(localId: string): void }

interface Entry {
  localId: string
  deviceId: string
  workspaceId: string
  token: number
  off: () => void
  renew: ReturnType<typeof setInterval>
  alive: boolean
}

const LEASE_TTL_MS = 2 * 60_000
/** 创建时设备给的初始租约只有 30 秒,所以第一次续租必须比它早得多。 */
const RENEW_EVERY_MS = 10_000

type DeviceTerminal = Omit<TerminalInfo, 'id'> & { id: string; exitCode?: number }
interface Attached { info: DeviceTerminal; data: string; truncated: boolean; seq: number }

export class TerminalBridge {
  private readonly entries = new Map<string, Entry>()

  constructor(private readonly port: TerminalBridgePort) {}

  async create(client: ControllerClient, req: TerminalCreateRequest, window: TerminalWindow): Promise<TerminalInfo> {
    if (req.cwd !== undefined) throw new DeviceProtocolError('forbidden', 'A custom working directory is not available for device terminals')
    const existing = req.id === undefined ? undefined : this.entries.get(req.id)
    if (existing?.alive === true) {
      if (existing.workspaceId !== req.workspaceId) throw new DeviceProtocolError('conflict', 'Terminal id is already in use')
      window.subscribe(existing.localId)
      return this.refresh(client, existing)
    }
    if (existing !== undefined) this.drop(existing)
    const receipt = await client.command('terminals.create', { workspaceId: req.workspaceId, cols: req.cols, rows: req.rows }, `ncw-term-${cryptoId()}`) as
      { status?: unknown; result?: { terminal?: DeviceTerminal; lease?: { token?: number }; error?: { code?: string; message?: string } } }
    if (receipt.status !== 'succeeded' || receipt.result?.terminal === undefined || typeof receipt.result.lease?.token !== 'number') {
      const error = receipt.result?.error
      throw new DeviceProtocolError((error?.code as never) ?? 'internal', error?.message ?? 'The device could not open a terminal')
    }
    const terminal = receipt.result.terminal
    const localId = req.id ?? terminal.id
    window.subscribe(localId)
    const entry: Entry = {
      localId, deviceId: terminal.id, workspaceId: req.workspaceId, token: receipt.result.lease.token, off: () => {}, alive: true,
      renew: setInterval(() => { void this.renewLease(entry) }, RENEW_EVERY_MS)
    }
    entry.renew.unref()
    this.entries.set(localId, entry)
    // 创建后立刻 attach:首个输出可能在订阅之前就产生了,快照里带着。
    const attached = await this.attach(client, entry)
    if (attached.data.length > 0) this.port.emitData(localId, { id: localId, seq: attached.seq, chunk: attached.data })
    return { ...terminal, id: localId } as TerminalInfo
  }

  async getBuffer(client: ControllerClient, localId: string): Promise<TerminalBuffer> {
    const entry = this.require(localId)
    const attached = await this.attach(client, entry)
    return { id: localId, data: attached.data, truncated: attached.truncated, seq: attached.seq }
  }

  async list(client: ControllerClient, workspaceId: string): Promise<TerminalInfo[]> {
    const listed = await client.query('terminals.list', { workspaceId }) as unknown as DeviceTerminal[]
    const byDevice = new Map([...this.entries.values()].map((entry) => [entry.deviceId, entry.localId]))
    return listed.map((terminal) => ({ ...terminal, id: byDevice.get(terminal.id) ?? terminal.id })) as TerminalInfo[]
  }

  async kill(client: ControllerClient, localId: string): Promise<void> {
    const entry = this.require(localId)
    const receipt = await client.command('terminals.kill', { id: entry.deviceId, token: entry.token }, `ncw-term-${cryptoId()}`) as { status?: unknown; result?: { error?: { code?: string; message?: string } } }
    if (receipt.status !== 'succeeded') {
      throw new DeviceProtocolError((receipt.result?.error?.code as never) ?? 'internal', receipt.result?.error?.message ?? 'The device could not stop the terminal')
    }
  }

  /** 输入/改尺寸:没有回程,失败只记日志。 */
  async write(localId: string, data: string): Promise<void> {
    const entry = this.entries.get(localId)
    if (entry === undefined) return
    try { await this.port.run((client) => client.query('terminals.write', { id: entry.deviceId, token: entry.token, data })) } catch (error) { this.port.warn(`terminal write ${localId}`, error) }
  }

  async resize(localId: string, cols: number, rows: number): Promise<void> {
    const entry = this.entries.get(localId)
    if (entry === undefined) return
    try { await this.port.run((client) => client.query('terminals.resize', { id: entry.deviceId, token: entry.token, cols, rows })) } catch (error) { this.port.warn(`terminal resize ${localId}`, error) }
  }

  closeAll(): void {
    for (const entry of [...this.entries.values()]) this.drop(entry)
  }

  /** 连接层报告某条终端流出现游标缺口。 */
  async resync(streamId: string): Promise<void> {
    if (!streamId.startsWith('terminal:')) return
    const deviceId = streamId.slice('terminal:'.length)
    const entry = [...this.entries.values()].find((item) => item.deviceId === deviceId)
    if (entry === undefined || !entry.alive) return
    entry.off()
    try { await this.port.run((client) => this.attach(client, entry)) } catch (error) {
      this.port.warn(`terminal resync ${entry.localId}`, error)
      this.drop(entry)
    }
  }

  private require(localId: string): Entry {
    const entry = this.entries.get(localId)
    if (entry === undefined) throw new DeviceProtocolError('not_found', 'Terminal does not exist on this device connection')
    return entry
  }

  private async refresh(client: ControllerClient, entry: Entry): Promise<TerminalInfo> {
    const attached = await this.attach(client, entry)
    return { ...attached.info, id: entry.localId } as TerminalInfo
  }

  /** attach(设备端重复 attach = 替换订阅)并从快照游标开始跟随输出。 */
  private async attach(client: ControllerClient, entry: Entry): Promise<Attached> {
    const attached = await client.query('terminals.attach', { id: entry.deviceId }) as unknown as Attached
    entry.off()
    entry.off = client.follow(`terminal:${entry.deviceId}`, String(attached.seq), (frame) => {
      const data = frame.data as { seq?: unknown; chunk?: unknown; code?: unknown }
      if (frame.event === 'terminals.data' && typeof data.seq === 'number' && typeof data.chunk === 'string') {
        this.port.emitData(entry.localId, { id: entry.localId, seq: data.seq, chunk: data.chunk })
      } else if (frame.event === 'terminals.exit' && typeof data.code === 'number') {
        this.port.emitExit(entry.localId, { id: entry.localId, code: data.code })
        entry.alive = false
        this.stop(entry)
      }
    })
    if (!attached.info.alive) { entry.alive = false; this.stop(entry) }
    return attached
  }

  private async renewLease(entry: Entry): Promise<void> {
    if (!entry.alive) return
    try {
      // 续租是设备上的幂等命令(带 key 的 execution),不是查询。
      const receipt = await this.port.run((client) => client.command('leases.renew',
        { kind: 'terminal', resourceId: entry.deviceId, token: entry.token, ttlMs: LEASE_TTL_MS }, `ncw-term-${cryptoId()}`)) as
        { status?: unknown; result?: { token?: number; error?: { message?: string } } }
      if (receipt.status !== 'succeeded') throw new DeviceProtocolError('conflict', receipt.result?.error?.message ?? 'The device did not renew the input lease')
      if (typeof receipt.result?.token === 'number') entry.token = receipt.result.token
    } catch (error) {
      // 租约丢了(被别的控制端接管/设备重启):输入会被设备拒绝,日志里能看到原因。
      this.port.warn(`terminal lease renew ${entry.localId}`, error)
    }
  }

  private stop(entry: Entry): void {
    clearInterval(entry.renew)
    try { entry.off() } catch { /* 连接已断开。 */ }
  }

  private drop(entry: Entry): void {
    this.entries.delete(entry.localId)
    entry.alive = false
    this.stop(entry)
  }
}

function cryptoId(): string {
  return globalThis.crypto.randomUUID()
}
