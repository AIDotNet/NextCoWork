/**
 * 打进应用里的 SSH 客户端。Windows 上用它,不再依赖系统 OpenSSH 的连接复用。
 *
 * 需求:一次连接只认证一次,而且不能要求这台电脑另装 Git、Node、Python,也不能要求
 * 远端装任何东西。系统自带的 OpenSSH 没有 Unix socket,`ControlMaster` 开不了,
 * 于是每条命令都是一次新的 ssh、一次新的密码。复用必须发生在客户端,所以这里用
 * `ssh2`(纯 JS,随应用一起打包)自己握**一条**连接。
 *
 * 密码由 `ask` 提供,它走的是和系统 ssh 同一个询问框、同一个「记住密码」槽位。
 * 问一次(或直接命中已存密码),这条连接就留下来。后面的 exec、SFTP、TCP 转发
 * 都是这条连接上的通道,不再起 ssh,也不再问密码。
 *
 * 只支持手动填写的主机、端口、用户名。走 `~/.ssh/config` 别名的连接仍用系统 ssh,
 * ssh2 读不到那份配置。终端要 PTY,也仍由系统 ssh 起,那一条会单独认证一次。
 *
 * ★ 认证**之前**先验主机密钥。ssh2 不读 known_hosts,所以这里按 `host:port` 把指纹
 * 存进账户隔离的 secrets 槽位,并复用 `SshAuthBroker` 的确认框(见 `askHostKey`)。
 * 旧实现在这里硬编码 `hostVerifier: () => true`,等于对"密钥换了一台机器"也照发密码。
 */
import { createHash } from 'node:crypto'
import { Client, type ConnectConfig } from 'ssh2'
import type { Socket } from 'node:net'
import { PassThrough, type Readable, type Writable } from 'node:stream'
import type { SshConnectionProfile } from '../../../shared/domain/environment'
import type { EnvironmentProcess } from '../contract'
import { EnvironmentError } from '../errors'
import { knownHostsName } from './command'

/**
 * 内置客户端验主机密钥需要的两样东西。
 *
 * ★ `confirm` 的 `true` 是**唯一**放行条件:取消、窗口关闭、连接被 abort 一律 `false`,
 * 没有"没问到就当同意"的分支。`secrets` 是账户隔离的槽位,只在明确的「是」之后写。
 */
export interface HostKeyVerifier {
  secrets: { get(ref: string): Promise<string | null>; set(ref: string, value: string): Promise<void>; available(): boolean }
  confirm(prompt: string, fingerprint: string): Promise<boolean>
}

/**
 * 已确认指纹存哪个槽位:连接账户命名空间下,再按**目标 host:port** 分。
 *
 * ★ 为什么多带一个 host:port 而不是只按连接 id —— 同一条连接的 `target` 是可以改的
 * (改完 `EnvironmentManager` 会断开重连)。只按 id 存的话,旧机器上按过的那次「是」
 * 会被当成新机器的信任,恰好绕开"换了主机必须重新确认"。带上 host:port 之后,
 * 改目标 = 换槽位 = 重新走首次确认。
 */
export function hostKeyRef(connectionId: string, host: string, port: number): string {
  return `connection:${connectionId}:hostkey:${createHash('sha256').update(`${host}\0${String(port)}`).digest('hex')}`
}

/** `ssh-keygen -lf` 用的那种指纹:不带 `=` 填充,也不带 OpenSSH 的 `SHA256:` 前缀。 */
export function hostKeyFingerprint(key: Buffer): string {
  return createHash('sha256').update(key).digest('base64').replace(/=+$/u, '')
}

/** 公钥 blob 的第一个字段就是算法名(string:uint32 长度 + 字节),提示里照着说,不硬编码 ed25519。 */
function hostKeyAlgorithm(key: Buffer): string {
  if (key.length < 4) return 'host'
  const length = key.readUInt32BE(0)
  return length > 0 && 4 + length <= key.length ? key.subarray(4, 4 + length).toString('ascii') : 'host'
}

export class BundledSshClient {
  private client?: Client
  private closed = false
  /** 这条连接上已经确认过的指纹(重协商不必再弹一次窗)。 */
  private confirmed = ''

  constructor(readonly profile: SshConnectionProfile, private readonly ask: (prompt: string, rejected: boolean) => Promise<string>,
    /** 没有它就是不信任任何主机密钥 —— fail closed,绝不退回 `hostVerifier: () => true`。 */
    private readonly hostKey?: HostKeyVerifier) {}

  /**
   * 手动型连接才走这里。配置型别名读不到 `~/.ssh/config`,调用方应继续用系统 ssh。
   *
   * ★ 只接受**真的能连**的形状:`manual` + 非空 host/username + 合法端口。`identityFile`
   * 和 `proxyJump` 走系统 OpenSSH —— 这条纯 JS 连接既读不了私钥文件也做不了跳板,
   * 收下它们只会把「用私钥登录」悄悄降级成密码认证。key / interactive 同理。
   */
  static supports(profile: SshConnectionProfile): boolean {
    const target = profile.target
    if (target?.kind !== 'manual' || target.host === '' || target.username === '' || !Number.isInteger(target.port)) return false
    if (target.port < 1 || target.port > 65535) return false
    if (target.identityFile !== undefined || target.proxyJump !== undefined) return false
    return profile.authMethod === undefined || profile.authMethod === 'auto' || profile.authMethod === 'password' || profile.authMethod === 'ask'
  }

  async connect(signal: AbortSignal): Promise<void> {
    signal.throwIfAborted()
    const target = this.profile.target
    if (target?.kind !== 'manual') throw new EnvironmentError('unsupported-config')
    const client = new Client()
    this.client = client
    let rejected = false
    /** 用户把密码框取消了 —— 这不是"连不上",UI 不该弹一条连接错误。 */
    let cancelled = false
    /**
     * ★ 主机密钥没通过验证。**必须单独记一笔**:ssh2 把"用户拒了密钥"和"服务器把密钥换了"
     * 都收成同一条 `KEY_EXCHANGE_FAILED`,光看 ssh2 的错误分不出来 —— 于是这个改动存在的
     * 全部意义(把"密钥变了"显示给用户)会被降级成一句「无法连接服务器,请检查网络」。
     * 系统 ssh 那条路靠 `classifyConnectFailure` 得到 `host-key`,这里靠这个标记对齐。
     */
    let hostKeyDenied = false
    await new Promise<void>((resolve, reject) => {
      let settled = false
      let verify: ((permitted: boolean) => void) | undefined
      /**
       * ★ 收尾只做一次:`ready`/`error`/`close`/`end`/超时/abort 六条分支都汇到这里,
       * 各自把定时器、signal 监听和 `ready` 监听摘掉。`error`/`close`/`end` 三个监听**留着**
       * (后面带 `settled` 短路)—— ssh2 的连接在 ready 之后仍可能报错,一个没人接的
       * `error` 事件会把主进程整个带崩,而 `once` 在第一次报错后就摘掉了自己。
       */
      const done = (error?: Error): void => {
        if (settled) return
        settled = true
        clearTimeout(timer)
        signal.removeEventListener('abort', abort)
        client.removeListener('ready', ready)
        // ★ 连接结束时把还挂着的确认一并判否:迟到的「批准」绝不能落回一次已经结束的握手。
        const pending = verify
        verify = undefined
        try { pending?.(false) } catch { /* 连接已拆掉,写回一个已死的协议没有意义 */ }
        if (error) { this.closed = true; reject(error) }
        else resolve()
      }
      const timer = setTimeout(() => { client.end(); done(new EnvironmentError('timeout')) }, 5 * 60_000)
      /**
       * 失败的分类顺序:取消 > 主机密钥 > 原来的错误。
       *
       * `cancelled` / `hostKeyDenied` 只在导致连接结束的那一次判否里置位,所以键变更不会被
       * 一轮重协商里的其它结局覆盖。分不出来时保持原样(ssh2 的 Error 原样往上抛)。
       */
      const failure = (fallback: Error): Error => hostKeyDenied ? new EnvironmentError('host-key')
        : cancelled ? new EnvironmentError('cancelled') : fallback
      const fail = (error: Error): void => { if (settled) return; client.end(); done(failure(error)) }
      const abort = (): void => { if (settled) return; client.end(); done(new EnvironmentError('cancelled')) }
      const ready = (): void => done()
      const closed = (): void => done(failure(new EnvironmentError('connection-failed')))
      signal.addEventListener('abort', abort, { once: true })
      client.once('ready', ready)
      client.on('error', fail)
      // ssh2 在握手失败时只 emit error;套接字被对端直接关掉时,不接这两个就永远悬着。
      client.on('close', closed)
      client.on('end', closed)
      const config: ConnectConfig = {
        host: target.host, port: target.port, username: target.username,
        readyTimeout: 5 * 60_000, keepaliveInterval: 15_000, keepaliveCountMax: 2,
        /**
         * ★ ssh2 的 `hostVerifier` **只在回调里认答案**:`return` 一个值会被当成同步结果
         * (`client.js` 里 `if (ret !== undefined) verify(ret)`),而 Promise 恒为真值 ——
         * 异步确认返回 Promise 等于无条件放行。所以返回 `undefined` 让 ssh2 停在握手里等,
         * 由下面这个 `accept(permitted)` 收尾。
         */
        hostVerifier: (key: Buffer, accept: (permitted: boolean) => void) => {
          verify = accept
          void this.verifyHostKey(key, signal).then((permitted) => {
            if (verify !== accept) return
            verify = undefined
            if (!permitted) hostKeyDenied = true
            accept(permitted)
          }, () => {
            if (verify !== accept) return
            verify = undefined
            hostKeyDenied = true
            accept(false)
          })
        },
        authHandler: (_methods, _partial, next) => {
          if (settled || signal.aborted || this.closed) return
          void this.ask(`${target.username}@${target.host}'s password: `, rejected).then(
            (password) => {
              if (settled || signal.aborted || this.closed) return
              rejected = true
              next({ type: 'password', username: target.username, password })
            },
            // ★ 取消密码框 = 用户放弃这次连接。`client.end()` 会触发 `close`,而 `closed`
            //   按这里的标记把它报成 `cancelled` —— 否则界面会把"用户点了取消"显示成一条连接错误。
            () => { cancelled = true; client.end() }
          )
        }
      }
      client.connect(config)
    })
  }

  /**
   * 验一把主机密钥。**在认证之前**跑:判否时 ssh2 以握手失败结束,`authHandler` 不会被调用,
   * 所以密码不会发到一台指纹对不上的机器上。
   *
   * pin 的语义与系统 OpenSSH 的 known_hosts 逐条对齐:
   * - 槽位里没有 → 首次使用,弹确认框;只有明确的「是」才写 pin。
   * - 指纹一致   → 直接放行,不弹窗(所以一条连接只问一次)。
   * - 指纹不同   → fail closed,立刻拒绝**而不是**弹一个"是否继续"的框 —— 和 `ssh-native.test.ts`
   *   里那条「主机密钥不匹配时不该给用户一个确认按钮」是同一条规则,也绝不静默覆盖旧指纹。
   */
  private async verifyHostKey(key: Buffer, signal: AbortSignal): Promise<boolean> {
    const verifier = this.hostKey
    if (!verifier || signal.aborted) return false
    const target = this.profile.target
    if (target.kind !== 'manual') return false
    const fingerprint = hostKeyFingerprint(key)
    if (this.confirmed === fingerprint) return true
    const ref = hostKeyRef(this.profile.id, target.host, target.port)
    let stored: string | null
    try { stored = await verifier.secrets.get(ref) } catch { return false }
    if (signal.aborted || this.closed) return false
    if (stored !== null) {
      if (stored !== fingerprint) return false
      this.confirmed = fingerprint
      return true
    }
    if (signal.aborted) return false
    /**
     * 提示与系统 ssh 首次连接时同形,指纹是**真实**的那个(SHA256,base64 无填充)。
     * 这里只给提示文字:确认框把 `host-key` 渲染成「信任/取消」,只有「信任」才回 `yes`。
     */
    const prompt = `The authenticity of host '${knownHostsName(target.host, target.port)}' can't be established.\n`
      + `${hostKeyAlgorithm(key)} key fingerprint is SHA256:${fingerprint}.\n`
      + `Are you sure you want to continue connecting (yes/no/[fingerprint])? `
    const approved = await verifier.confirm(prompt, fingerprint)
    if (!approved || signal.aborted || this.closed) return false
    // 只有仍有效的确认才能写 pin；存储失败不能无声降级为不记住主机的密码连接。
    if (verifier.secrets.available()) {
      try { await verifier.secrets.set(ref, fingerprint) } catch { return false }
    }
    if (signal.aborted || this.closed) return false
    this.confirmed = fingerprint
    return true
  }

  private requireClient(): Client {
    if (this.closed || !this.client) throw new EnvironmentError('disconnected')
    return this.client
  }

  exec(command: string, signal: AbortSignal, timeoutMs: number): Promise<{ code: number; stdout: string; stderr: string }> {
    signal.throwIfAborted()
    const client = this.requireClient()
    return new Promise((resolve, reject) => {
      client.exec(command, (error, channel) => {
        if (error || !channel) { reject(error ?? new EnvironmentError('disconnected')); return }
        const stdout: Buffer[] = []
        const stderr: Buffer[] = []
        let length = 0
        let settled = false
        const stop = (failure?: Error): void => {
          if (settled) return
          settled = true
          clearTimeout(timer)
          signal.removeEventListener('abort', abort)
          if (failure) { channel.close(); reject(failure) }
        }
        const abort = (): void => stop(new EnvironmentError('cancelled'))
        const timer = setTimeout(() => stop(new EnvironmentError('timeout')), timeoutMs)
        signal.addEventListener('abort', abort, { once: true })
        const take = (target: Buffer[], bytes: Buffer): void => {
          length += bytes.byteLength
          if (length > 8 * 1024 * 1024) stop(new EnvironmentError('unsupported', 'SSH output limit exceeded'))
          else target.push(bytes)
        }
        channel.on('data', (bytes: Buffer) => take(stdout, bytes))
        channel.stderr.on('data', (bytes: Buffer) => take(stderr, bytes))
        channel.on('close', (code: number) => {
          if (settled) return
          settled = true
          clearTimeout(timer)
          signal.removeEventListener('abort', abort)
          resolve({ code: code ?? 0, stdout: Buffer.concat(stdout).toString('utf8'), stderr: Buffer.concat(stderr).toString('utf8') })
        })
        channel.on('error', (failure: Error) => stop(failure))
      })
    })
  }

  process(command: string, input?: string): EnvironmentProcess {
    const client = this.requireClient()
    const stdin = new PassThrough()
    const stdout = new PassThrough()
    const stderr = new PassThrough()
    const exited = new Promise<{ code: number | null; signal?: string | null }>((resolve) => {
      client.exec(command, (error, channel) => {
        if (error || !channel) { stdout.end(); stderr.end(); resolve({ code: null, signal: null }); return }
        if (input !== undefined) channel.write(input)
        stdin.on('data', (chunk: Buffer) => channel.write(chunk))
        stdin.on('end', () => channel.end())
        channel.on('data', (bytes: Buffer) => stdout.write(bytes))
        channel.stderr.on('data', (bytes: Buffer) => stderr.write(bytes))
        channel.on('close', (code: number | null) => { stdout.end(); stderr.end(); resolve({ code, signal: null }) })
      })
    })
    return { stdin: stdin as unknown as Writable, stdout: stdout as unknown as Readable, stderr: stderr as unknown as Readable, exited, kill: () => stdin.destroy() }
  }

  /** 同一条已认证连接上的 SFTP 子系统。上层要的是原始字节流,所以这里开的是通道而不是 ssh2 的高层 API。 */
  subsystem(): { stdin: Writable; stdout: Readable; stderr: Readable } {
    const client = this.requireClient()
    const stdin = new PassThrough()
    const stdout = new PassThrough()
    const stderr = new PassThrough()
    client.subsys('sftp', (error, channel) => {
      if (error || !channel) { stdout.end(); stderr.end(); return }
      stdin.pipe(channel)
      channel.pipe(stdout)
      channel.stderr.pipe(stderr)
      channel.on('close', () => { stdout.end(); stderr.end() })
    })
    return { stdin, stdout, stderr }
  }

  async openTcp(hostname: string, port: number): Promise<Socket> {
    const client = this.requireClient()
    return new Promise((resolve, reject) => {
      client.forwardOut('127.0.0.1', 0, hostname, port, (error, channel) => {
        if (error || !channel) reject(error ?? new EnvironmentError('disconnected'))
        else resolve(channel as unknown as Socket)
      })
    })
  }

  close(): Promise<void> {
    this.closed = true
    this.client?.end()
    this.client = undefined
    return Promise.resolve()
  }
}
