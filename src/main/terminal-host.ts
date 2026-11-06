import { randomUUID } from 'node:crypto'
import type { TerminalBuffer, TerminalCreateRequest, TerminalInfo, TerminalIntent, TerminalPreparation } from '../shared/domain/terminal'
import { TERMINAL_BUFFER_BYTES, TERMINAL_FLUSH_MS, TERMINAL_MAX_CHUNK_BYTES } from '../shared/domain/terminal'
import { terminalTopic, windows } from './window/registry'
import type { WebContents } from 'electron'
import { IpcError } from './ipc/errors'
import { EnvironmentError } from './environment/errors'
import type { EnvironmentLease, TerminalDriver, WorkspaceEnvironment } from './environment/contract'
import { getEnvironments } from './runtime'

type Session = {
  info: TerminalInfo
  process: TerminalDriver
  environment: WorkspaceEnvironment
  ownerId: number
  release(): void
  buffer: string
  truncated: boolean
  pending: string
  flushTimer: NodeJS.Timeout | null
  seq: number
}

type PendingIntent = { info: TerminalIntent; ownerId: number; lease: EnvironmentLease; grant?: string; timer: NodeJS.Timeout }
type PendingCreation = { ownerId: number; workspaceId: string; cwd?: string; approval?: string; controller: AbortController; promise: Promise<TerminalInfo> }

/**
 * `tabs.openTerminal` 的**一次性启动 spec**(见 `ipc/plugins.ts` 的
 * `launchTerminal`):主进程先铸 terminalId、备好 env 与 argv,渲染层随后
 * 开 Tab 并带着同一个 id 来 `terminal:create`,在这里消费。
 *
 * ★ env 只活在这张表和 pty 子进程环境里 —— 不过渲染层、不进终端回滚缓冲、
 * 不落盘。★ 一次性:spawn 成功写入启动行后立即删除;没被认领的 spec 由
 * TTL 兜底清理(同 `intents` 的 60s 模式),否则一次点了菜单却没等到 Tab
 * 的 spec 会永远挂在内存里。
 */
type LaunchSpec = { workspaceId: string; env: Record<string, string>; argv: string[]; expiresAt: number; timer: NodeJS.Timeout }

/** spec 从备好到被认领的窗口。同 `intents` 的 60s:一次点击的合理等待。 */
const LAUNCH_SPEC_TTL_MS = 60_000

function safeSize(value: number, fallback: number): number {
  return Number.isFinite(value) && value > 0 ? Math.max(2, Math.floor(value)) : fallback
}

/**
 * 把 argv 拼成一行**写给既有交互 shell** 的启动行。
 *
 * 需求:CLI(claude / codex 这类全屏 TUI)必须跑在交互式终端里,而终端的
 * pty 起的是用户的 shell,`openTerminal` 没有「spawn 后替换进程」的手段 ——
 * 唯一稳妥的办法是把启动行写进去,让 shell 自己启动 CLI。这样 CLI 退出后
 * 终端仍是一个活 shell,与用户手敲命令的体验一致。
 *
 * ★ 空格/引号安全靠单引号包裹;POSIX(`'\''`)与 PowerShell(`''`)的转义
 * 不同,按目标平台选 —— 一期仅本地终端,本地 shell 就是宿主平台的 shell。
 * 安全属性:argv 来自过了 `narrowCommand` 参数门的 spec,这里只负责「写进
 * 去的东西就是 argv 本身」,不新增放行面。
 */
function launchLine(argv: readonly string[], windows: boolean): string {
  const quote = (value: string): string => {
    if (value !== '' && /^[A-Za-z0-9_@%+=:,./-]+$/.test(value)) return value
    return windows ? `'${value.replaceAll("'", "''")}'` : `'${value.replaceAll("'", `'\\''`)}'`
  }
  return argv.map(quote).join(' ')
}

/** Owns long-lived interactive shells. It intentionally lives in main, never in the renderer. */
export class TerminalHost {
  private readonly sessions = new Map<string, Session>()
  private readonly intents = new Map<string, PendingIntent>()
  private readonly launchSpecs = new Map<string, LaunchSpec>()
  private readonly creating = new Map<string, PendingCreation>()
  private readonly preparing = new Map<string, { ownerId: number; controller: AbortController }>()
  private readonly owners = new Set<number>()

  constructor(private readonly acquire: (workspaceId: string) => EnvironmentLease = (id) => getEnvironments().acquire(id),
    private readonly now: () => number = Date.now) {}

  async prepare(req: TerminalCreateRequest, sender: WebContents): Promise<TerminalPreparation> {
    this.watchOwner(sender)
    const lease = this.acquire(req.workspaceId)
    try {
      const environment = lease.environment
      environment.assertReady()
      if (!environment.remote) return { kind: 'ready', terminal: await this.create(req, sender) }
      const existing = req.id ? this.sessions.get(req.id) : undefined
      if (existing?.info.alive) {
        this.checkSession(existing, req.workspaceId, sender, environment)
        windows.subscribe(terminalTopic(existing.info.id), sender)
        return { kind: 'ready', terminal: existing.info }
      }
      if (existing) this.checkOwner(existing, sender)
      const id = req.id ?? randomUUID()
      const previous = this.preparing.get(id)
      if (previous && previous.ownerId !== sender.id) throw new EnvironmentError('approval-required')
      previous?.controller.abort()
      const pendingPreparation = { ownerId: sender.id, controller: new AbortController() }
      this.preparing.set(id, pendingPreparation)
      let cwd: string
      try {
        cwd = await this.cwd(environment, req.cwd)
        if (pendingPreparation.controller.signal.aborted || sender.isDestroyed()) throw new EnvironmentError('cancelled')
      } finally { if (this.preparing.get(id) === pendingPreparation) this.preparing.delete(id) }
      for (const [intentId, pending] of this.intents) {
        if (pending.info.expiresAt <= this.now()) this.dropIntent(intentId)
        else if (pending.info.terminalId === id) {
          if (pending.ownerId !== sender.id) throw new EnvironmentError('approval-required')
          this.dropIntent(intentId)
        }
      }
      if (this.intents.size >= 64) throw new EnvironmentError('conflict')
      const info: TerminalIntent = { id: randomUUID(), terminalId: id, workspaceId: req.workspaceId,
        connection: environment.description, cwd, shell: environment.platform.shell, expiresAt: this.now() + 60_000 }
      const held = this.acquire(req.workspaceId)
      if (held.environment.key !== environment.key) { held.release(); throw new EnvironmentError('disconnected') }
      const timer = setTimeout(() => this.dropIntent(info.id), 60_000)
      timer.unref()
      this.intents.set(info.id, { info, ownerId: sender.id, lease: held, timer })
      return { kind: 'approval', intent: info }
    } finally { lease.release() }
  }

  approve(id: string, approved: boolean, sender: WebContents): string | null {
    const pending = this.intents.get(id)
    if (!pending || pending.ownerId !== sender.id) throw new EnvironmentError('approval-expired')
    if (!approved) { this.dropIntent(id); return null }
    if (pending.info.expiresAt <= this.now()) { this.dropIntent(id); throw new EnvironmentError('approval-expired') }
    pending.lease.environment.assertReady()
    pending.grant ??= randomUUID()
    return pending.grant
  }

  create(req: TerminalCreateRequest, sender: WebContents): Promise<TerminalInfo> {
    this.watchOwner(sender)
    const id = req.id ?? randomUUID()
    const pending = this.creating.get(id)
    if (pending) {
      if (pending.ownerId !== sender.id || pending.workspaceId !== req.workspaceId || pending.approval !== req.approval || pending.cwd !== req.cwd) {
        return Promise.reject(new EnvironmentError('approval-required'))
      }
      return pending.promise
    }
    const controller = new AbortController()
    const promise = this.createOne({ ...req, id }, sender, controller.signal).finally(() => this.creating.delete(id))
    this.creating.set(id, { ownerId: sender.id, workspaceId: req.workspaceId, cwd: req.cwd, approval: req.approval, controller, promise })
    return promise
  }

  private async createOne(req: TerminalCreateRequest & { id: string }, sender: WebContents, signal: AbortSignal): Promise<TerminalInfo> {
    const existing = req.id ? this.sessions.get(req.id) : undefined
    let lease = this.acquire(req.workspaceId)
    let retained = false
    try {
    const environment = lease.environment
    environment.assertReady()
    if (existing) {
      this.checkOwner(existing, sender)
      if (existing.info.alive) {
        this.checkSession(existing, req.workspaceId, sender, environment)
        windows.subscribe(terminalTopic(existing.info.id), sender)
        return existing.info
      }
    }
    let cwd: string
    if (environment.remote) {
      const pending = [...this.intents.values()].find((intent) => intent.grant !== undefined && intent.grant === req.approval)
      if (!pending) throw new EnvironmentError('approval-required')
      if (pending.ownerId !== sender.id || pending.info.terminalId !== req.id || pending.info.workspaceId !== req.workspaceId
        || pending.lease.environment.key !== environment.key || (req.cwd !== undefined && req.cwd !== pending.info.cwd)) {
        throw new EnvironmentError('approval-required')
      }
      if (pending.info.expiresAt <= this.now()) { this.dropIntent(pending.info.id); throw new EnvironmentError('approval-expired') }
      pending.lease.environment.assertReady()
      this.intents.delete(pending.info.id)
      clearTimeout(pending.timer)
      lease.release()
      lease = pending.lease
      cwd = await this.cwd(lease.environment, pending.info.cwd)
      if (cwd !== pending.info.cwd) throw new EnvironmentError('approval-expired')
    } else cwd = await this.cwd(environment, req.cwd)
    if (sender.isDestroyed() || signal.aborted) throw new EnvironmentError('cancelled')
    const id = req.id
    const cols = safeSize(req.cols, 100)
    const rows = safeSize(req.rows, 28)
    const shell = environment.terminalShell ?? environment.platform.shell
    /*
      spec 只在本地分支消费:远程路径上 env 根本注入不进 pty,消耗它只会造成
      「spec 没了、启动也没发生」的双重丢失。一期插件终端仅本地,远程拒绝在
      `ipc/plugins.ts` 的 launchTerminal,这里的 `environment.remote` 判空是兜底。
    */
    const spec = environment.remote ? undefined : this.launchSpecs.get(req.id)
    const child = await lease.environment.openTerminal({ cols, rows, cwd, ...(spec === undefined ? {} : { env: spec.env }) })
    if (sender.isDestroyed() || signal.aborted) { child.kill(); throw new EnvironmentError('cancelled') }
    try { lease.environment.assertReady() } catch (error) { child.kill(); throw error }
    const info: TerminalInfo = {
      id,
      workspaceId: req.workspaceId,
      title: environment.path.basename(cwd) || shell,
      cwd,
      shell,
      cols,
      rows,
      alive: true,
      createdAt: this.now()
    }
    const session: Session = {
      info,
      process: child,
      environment: lease.environment,
      ownerId: sender.id,
      release: lease.release,
      buffer: existing?.buffer ?? '',
      truncated: existing?.truncated ?? false,
      pending: '',
      flushTimer: null,
      seq: existing?.seq ?? 0
    }
    this.sessions.set(id, session)
    retained = true
    windows.subscribe(terminalTopic(id), sender)

    if (spec !== undefined) {
      /*
        ★ 一次性,而且**必须在这里才消费**:spawn 中断/失败的路径(上面那道
        isDestroyed/aborted 检查)不该弄丢 spec;反过来,写入启动行之后再留着,
        复用同 id 的新 create 也不会到这里(活会话在上面 `existing.alive` 早返回),
        但 TTL 之外留着它没有任何好处,还可能被一次 id 撞车重放。
      */
      this.dropLaunchSpec(id)
      child.write(`${launchLine(spec.argv, process.platform === 'win32')}\n`)
    }

    child.onData((data) => {
      if (this.sessions.get(id) !== session || !session.info.alive) return
      session.pending += data
      if (session.pending.length >= TERMINAL_MAX_CHUNK_BYTES) this.flush(id)
      else if (session.flushTimer === null) {
        session.flushTimer = setTimeout(() => { if (this.sessions.get(id) === session) this.flush(id) }, TERMINAL_FLUSH_MS)
      }
    })
    child.onExit(({ exitCode }) => {
      if (this.sessions.get(id) !== session || !session.info.alive) return
      if (session.flushTimer !== null) clearTimeout(session.flushTimer)
      session.flushTimer = null
      this.flush(id)
      session.info = { ...session.info, alive: false }
      session.release()
      windows.emitToTopic(terminalTopic(id), 'terminal:exit', { id, code: exitCode })
    })
    return info
    } finally { if (!retained) lease.release() }
  }

  /**
   * 备好一次性启动 spec(`tabs.openTerminal`,见类型注释)。仅本地工作区会走到这里。
   * 主进程铸 id 的原因同 `intents`:渲染层随后带着这个 id 来 create,spec 才对得上。
   */
  setLaunchSpec(id: string, spec: { workspaceId: string; env: Record<string, string>; argv: string[] }): void {
    this.dropLaunchSpec(id)
    const timer = setTimeout(() => this.dropLaunchSpec(id), LAUNCH_SPEC_TTL_MS)
    timer.unref()
    this.launchSpecs.set(id, { ...spec, expiresAt: this.now() + LAUNCH_SPEC_TTL_MS, timer })
  }

  attach(id: string, sender: WebContents): TerminalBuffer {
    const session = this.sessions.get(id)
    if (!session) throw new IpcError('unknown', `终端不存在: ${id}`)
    this.checkOwner(session, sender)
    windows.subscribe(terminalTopic(id), sender)
    return { id, data: session.buffer, truncated: session.truncated, seq: session.seq }
  }

  list(workspaceId: string, sender?: WebContents): TerminalInfo[] {
    return [...this.sessions.values()]
      .filter((session) => session.info.workspaceId === workspaceId && (!session.environment.remote || session.ownerId === sender?.id))
      .map((session) => session.info)
  }

  write(id: string, data: string, sender: WebContents): void {
    const session = this.sessions.get(id)
    if (!session || !session.info.alive || typeof data !== 'string') return
    this.checkOwner(session, sender)
    session.environment.assertReady()
    session.process.write(data)
  }

  resize(id: string, cols: number, rows: number, sender: WebContents): void {
    const session = this.sessions.get(id)
    if (!session || !session.info.alive) return
    this.checkOwner(session, sender)
    session.environment.assertReady()
    const nextCols = safeSize(cols, session.info.cols)
    const nextRows = safeSize(rows, session.info.rows)
    session.process.resize(nextCols, nextRows)
    session.info = { ...session.info, cols: nextCols, rows: nextRows }
  }

  kill(id: string, sender?: WebContents): void {
    const pending = this.creating.get(id)
    const preparation = this.preparing.get(id)
    if (sender && ((pending && pending.ownerId !== sender.id) || (preparation && preparation.ownerId !== sender.id))) throw new EnvironmentError('approval-required')
    pending?.controller.abort()
    preparation?.controller.abort()
    for (const [intentId, intent] of this.intents) {
      if (intent.info.terminalId === id && (!sender || intent.ownerId === sender.id)) this.dropIntent(intentId)
    }
    const session = this.sessions.get(id)
    if (!session) return
    if (sender) this.checkOwner(session, sender)
    if (session.flushTimer !== null) clearTimeout(session.flushTimer)
    if (session.info.alive) session.process.kill()
    session.release()
    this.sessions.delete(id)
  }

  /** 工作区被删除时,收掉它名下所有终端(含还在创建/等待审批的)。 */
  killWorkspace(workspaceId: string): void {
    for (const [id, pending] of this.creating) if (pending.workspaceId === workspaceId) this.kill(id)
    for (const [id, session] of [...this.sessions]) if (session.info.workspaceId === workspaceId) this.kill(id)
  }

  shutdown(): void {
    for (const pending of this.creating.values()) pending.controller.abort()
    for (const pending of this.preparing.values()) pending.controller.abort()
    for (const id of this.intents.keys()) this.dropIntent(id)
    for (const id of [...this.launchSpecs.keys()]) this.dropLaunchSpec(id)
    for (const id of [...this.sessions.keys()]) this.kill(id)
  }

  private async cwd(environment: WorkspaceEnvironment, requested?: string): Promise<string> {
    const cwd = await environment.path.resolveWithin(environment.rootPath, requested || environment.rootPath)
    if (!(await environment.fs.stat(cwd)).isDir) throw new EnvironmentError('invalid-path')
    environment.assertReady()
    return cwd
  }

  private checkOwner(session: Session, sender: WebContents): void {
    if (session.environment.remote && session.ownerId !== sender.id) throw new EnvironmentError('approval-required')
  }

  private checkSession(session: Session, workspaceId: string, sender: WebContents, environment: WorkspaceEnvironment): void {
    this.checkOwner(session, sender)
    if (session.info.workspaceId !== workspaceId || session.environment.key !== environment.key) throw new EnvironmentError('approval-required')
    session.environment.assertReady()
  }

  private dropIntent(id: string): void {
    const intent = this.intents.get(id)
    if (!intent) return
    this.intents.delete(id)
    clearTimeout(intent.timer)
    intent.lease.release()
  }

  private dropLaunchSpec(id: string): void {
    const spec = this.launchSpecs.get(id)
    if (spec === undefined) return
    this.launchSpecs.delete(id)
    clearTimeout(spec.timer)
  }

  private watchOwner(sender: WebContents): void {
    if (sender.isDestroyed()) throw new EnvironmentError('cancelled')
    if (this.owners.has(sender.id)) return
    this.owners.add(sender.id)
    sender.once('destroyed', () => {
      this.owners.delete(sender.id)
      for (const pending of this.creating.values()) if (pending.ownerId === sender.id) pending.controller.abort()
      for (const pending of this.preparing.values()) if (pending.ownerId === sender.id) pending.controller.abort()
      for (const [id, intent] of this.intents) if (intent.ownerId === sender.id) this.dropIntent(id)
      for (const [id, session] of this.sessions) if (session.ownerId === sender.id && session.environment.remote) this.kill(id)
    })
  }

  private flush(id: string): void {
    const session = this.sessions.get(id)
    if (!session || session.pending.length === 0) return
    const chunk = session.pending
    session.pending = ''
    session.flushTimer = null
    session.seq += 1
    session.buffer += chunk
    if (Buffer.byteLength(session.buffer, 'utf8') > TERMINAL_BUFFER_BYTES) {
      const bytes = Buffer.from(session.buffer, 'utf8')
      session.buffer = bytes.subarray(bytes.length - TERMINAL_BUFFER_BYTES).toString('utf8')
      session.truncated = true
    }
    windows.emitToTopic(terminalTopic(id), 'terminal:data', { id, seq: session.seq, chunk })
  }
}

export const terminalHost = new TerminalHost()
export const shutdownTerminals = (): void => terminalHost.shutdown()
