/**
 * 服务端终端管理:终端属于**设备**,不属于某个窗口或某条连接。
 *
 * - 控制端断开/切走/被撤销都不会杀终端;回来用 `attach` 取回缓冲并续订输出。
 * - 输出合批(16ms / 64KiB),每批一个递增的 `seq`;环形缓冲只留最近 256KiB(超出标 `truncated`)。
 *   退出事件也占一个 `seq`,所以每个订阅者看到的游标链是连续的,有缺口就是丢了(控制端据此重新 attach)。
 * - 输入/调整大小/终止由调用方在**租约闸**之后调用本类;本类自己不认识身份。
 * - 终端数有上限(每工作区 8、总共 32),已退出的终端保留一段时间供回看,之后回收。
 *
 * 驱动由 `open` 注入:生产里是环境的 `openTerminal`(node-pty),测试里是假驱动。
 */
import { DeviceProtocolError } from '../../shared/device/errors'
import { TERMINAL_BUFFER_BYTES, TERMINAL_FLUSH_MS, TERMINAL_MAX_CHUNK_BYTES } from '../../shared/domain/terminal'

export interface TerminalProcess {
  write(data: string): void
  resize(cols: number, rows: number): void
  kill(): void
  onData(listener: (data: string) => void): { dispose(): void }
  onExit(listener: (event: { exitCode: number }) => void): { dispose(): void }
}

export interface OpenedTerminal { process: TerminalProcess; cwd: string; shell: string; title: string }

export interface TerminalView {
  id: string
  workspaceId: string
  title: string
  cwd: string
  shell: string
  cols: number
  rows: number
  alive: boolean
  createdAt: number
  exitCode?: number
}

export type TerminalEvent =
  | { kind: 'data'; seq: number; chunk: string }
  | { kind: 'exit'; seq: number; code: number }

export interface TerminalManagerDeps {
  open(workspaceId: string, size: { cols: number; rows: number }): Promise<OpenedTerminal>
  newId(): string
  now?(): number
  /** 已退出的终端保留多久,默认 10 分钟。 */
  exitedTtlMs?: number
  maxPerWorkspace?: number
  maxTotal?: number
}

interface Session {
  info: TerminalView
  process: TerminalProcess
  buffer: string
  truncated: boolean
  pending: string
  timer: ReturnType<typeof setTimeout> | null
  seq: number
  sinks: Set<(event: TerminalEvent) => void>
  exitedAt: number | null
}

export const TERMINAL_INPUT_MAX = 64 * 1024

export class TerminalManager {
  private readonly sessions = new Map<string, Session>()
  private readonly now: () => number
  private readonly ttl: number
  private readonly perWorkspace: number
  private readonly total: number
  private creating = 0

  constructor(private readonly deps: TerminalManagerDeps) {
    this.now = deps.now ?? Date.now
    this.ttl = deps.exitedTtlMs ?? 10 * 60_000
    this.perWorkspace = deps.maxPerWorkspace ?? 8
    this.total = deps.maxTotal ?? 32
  }

  async create(workspaceId: string, cols: number, rows: number): Promise<TerminalView> {
    this.reap()
    const alive = [...this.sessions.values()].filter((s) => s.info.alive)
    if (alive.length + this.creating >= this.total || alive.filter((s) => s.info.workspaceId === workspaceId).length >= this.perWorkspace) {
      throw new DeviceProtocolError('resource_limit', 'Too many open terminals')
    }
    this.creating++
    let opened: OpenedTerminal
    try { opened = await this.deps.open(workspaceId, { cols, rows }) } finally { this.creating-- }
    const id = this.deps.newId()
    const session: Session = {
      info: { id, workspaceId, title: opened.title, cwd: opened.cwd, shell: opened.shell, cols, rows, alive: true, createdAt: this.now() },
      process: opened.process, buffer: '', truncated: false, pending: '', timer: null, seq: 0, sinks: new Set(), exitedAt: null
    }
    this.sessions.set(id, session)
    opened.process.onData((data) => {
      if (this.sessions.get(id) !== session || !session.info.alive) return
      session.pending += data
      if (session.pending.length >= TERMINAL_MAX_CHUNK_BYTES) this.flush(session)
      else if (session.timer === null) session.timer = setTimeout(() => this.flush(session), TERMINAL_FLUSH_MS)
    })
    opened.process.onExit(({ exitCode }) => {
      if (this.sessions.get(id) !== session || !session.info.alive) return
      this.flush(session)
      session.info = { ...session.info, alive: false, exitCode }
      session.exitedAt = this.now()
      this.emit(session, { kind: 'exit', seq: ++session.seq, code: exitCode })
    })
    return { ...session.info }
  }

  list(workspaceId: string): TerminalView[] {
    this.reap()
    return [...this.sessions.values()].filter((s) => s.info.workspaceId === workspaceId).map((s) => ({ ...s.info }))
  }

  /** 取回缓冲并订阅之后的事件。订阅与快照在同一个同步块里,中间插不进事件。 */
  attach(id: string, sink: (event: TerminalEvent) => void): { info: TerminalView; data: string; truncated: boolean; seq: number; dispose(): void } {
    const session = this.require(id)
    // 先把还在合批里的输出冲进缓冲,快照才是完整的。
    this.flush(session)
    session.sinks.add(sink)
    return { info: { ...session.info }, data: session.buffer, truncated: session.truncated, seq: session.seq,
      dispose: () => { session.sinks.delete(sink) } }
  }

  write(id: string, data: string): void {
    const session = this.requireAlive(id)
    if (data.length === 0 || data.length > TERMINAL_INPUT_MAX) throw new DeviceProtocolError('resource_limit', 'Terminal input is too large')
    session.process.write(data)
  }

  resize(id: string, cols: number, rows: number): void {
    const session = this.requireAlive(id)
    session.process.resize(cols, rows)
    session.info = { ...session.info, cols, rows }
  }

  kill(id: string): void {
    this.requireAlive(id).process.kill()
  }

  /** 停止服务:杀掉全部仍在跑的终端、清掉定时器。 */
  shutdown(): void {
    for (const session of this.sessions.values()) {
      if (session.timer !== null) clearTimeout(session.timer)
      session.timer = null
      session.sinks.clear()
      if (session.info.alive) { try { session.process.kill() } catch { /* 已退出。 */ } }
    }
    this.sessions.clear()
  }

  private require(id: string): Session {
    const session = this.sessions.get(id)
    if (session === undefined) throw new DeviceProtocolError('not_found', 'Terminal does not exist')
    return session
  }

  private requireAlive(id: string): Session {
    const session = this.require(id)
    if (!session.info.alive) throw new DeviceProtocolError('conflict', 'Terminal has exited')
    return session
  }

  private flush(session: Session): void {
    if (session.timer !== null) { clearTimeout(session.timer); session.timer = null }
    if (session.pending === '') return
    const chunk = session.pending
    session.pending = ''
    session.buffer += chunk
    if (session.buffer.length > TERMINAL_BUFFER_BYTES) {
      session.buffer = session.buffer.slice(session.buffer.length - TERMINAL_BUFFER_BYTES)
      session.truncated = true
    }
    this.emit(session, { kind: 'data', seq: ++session.seq, chunk })
  }

  private emit(session: Session, event: TerminalEvent): void {
    for (const sink of [...session.sinks]) {
      try { sink(event) } catch { /* 单个订阅者失败不影响其他订阅者,也不影响终端。 */ }
    }
  }

  /** 回收过期的已退出终端。 */
  private reap(): void {
    const t = this.now()
    for (const [id, session] of this.sessions) {
      if (!session.info.alive && session.exitedAt !== null && t - session.exitedAt >= this.ttl) this.sessions.delete(id)
    }
  }
}
