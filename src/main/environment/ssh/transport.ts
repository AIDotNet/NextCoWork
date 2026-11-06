import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process'
import { existsSync } from 'node:fs'
import { mkdtemp, rm, chmod } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { createConnection, createServer, type Socket } from 'node:net'
import type { SshConnectionProfile } from '../../../shared/domain/environment'
import type { EnvironmentProcess } from '../contract'
import { EnvironmentError } from '../errors'
import { proxyTunnelArgs, sshTargetArgs } from './command'
import type { SshProxyTunnel } from './proxy'
import { MUX_MISSING, remoteMuxCommand, SessionMux, type MuxByteStream } from './session-mux'

export interface OpenSshOptions {
  env?: NodeJS.ProcessEnv
  executable?: string
  onDisconnect?: () => void
  /**
   * `ssh -G` 解析出的最终配置。认证 broker 用它判断某条密码提示来自链路上的哪一跳
   * (见 `askpass.ts` 的 `shouldAutoAnswer`)。在任何会认证的 ssh 启动**之前**调用。
   */
  onResolved?: (values: Map<string, string>) => void
  /**
   * 需求:SSH 连接默认跟随应用/系统代理。返回 `null` = 这台主机该直连。
   *
   * ★ 注入而不是在这里自己去问:判断走不走代理要用 Electron 的 `session.resolveProxy`,
   * 而这个文件**不能碰 electron** —— `ssh-native.test.ts` 直接 import 它并起真实 sshd,
   * 在 vitest 的 node 环境里 `import { session } from 'electron'` 拿到的是一个路径字符串。
   * 接口留在这里,实现挂在 `runtime.ts`(见 `net/proxy.ts` 的 `resolveProxyForHost`)。
   */
  openProxyTunnel?: (hostname: string, port: number) => Promise<SshProxyTunnel | null>
}

/**
 * 交给 ssh 子进程的客户端环境。
 *
 * 白名单而不是黑名单:ssh 会把整个环境交给 ProxyCommand / Match exec / KnownHostsCommand
 * 跑的 `/bin/sh -c`,用户 ssh_config 里一条 `SendEnv *` 还能把它送到远端服务器。所以默认
 * 只放行**基础设施**变量,应用自己的密钥(API_KEY 之类)一律不进去。
 *
 * ★ 取舍点:这里放行的是**选择器和路径**(AWS_PROFILE、AWS_CONFIG_FILE、KRB5CCNAME…),
 * 不放行原始凭据(AWS_SECRET_ACCESS_KEY、AWS_SESSION_TOKEN)。前者是 ProxyCommand 找到
 * 凭据所必需的,后者即使缺席,aws/gcloud 也会自己去读凭据文件。放行原始凭据会让它们
 * 同时暴露给 ProxyCommand 和(配了 SendEnv 通配时的)远端服务器,不值得。
 */
const INHERITED_NAMES = [
  // POSIX 基础
  'PATH', 'HOME', 'USER', 'LOGNAME', 'SHELL', 'TMPDIR', 'TMP', 'TEMP', 'TZ',
  // Windows 基础。★ Windows 上 ssh.exe 推断默认登录名靠的是 USERNAME —— USER 在那边根本不存在,
  //   缺了它且 config 没写 User 时会直接登错账号。
  'SystemRoot', 'WINDIR', 'SystemDrive', 'ComSpec', 'USERPROFILE', 'HOMEDRIVE', 'HOMEPATH',
  'ProgramFiles', 'ProgramFiles(x86)', 'ProgramW6432', 'ProgramData', 'ALLUSERSPROFILE', 'PUBLIC',
  'USERNAME', 'USERDOMAIN', 'COMPUTERNAME', 'PATHEXT', 'APPDATA', 'LOCALAPPDATA',
  // 认证代理与硬件密钥。SSH_SK_* 是 FIDO(sk-ecdsa / sk-ssh-ed25519)的自定义 middleware,
  // SSH_PKCS11_HELPER 是智能卡。
  'SSH_AUTH_SOCK', 'SSH_AGENT_PID', 'SSH_SK_PROVIDER', 'SSH_SK_HELPER', 'SSH_PKCS11_HELPER',
  // GSSAPI / Kerberos。非默认 ccache(KEYRING: / DIR: / FILE:)全靠 KRB5CCNAME 定位。
  'KRB5CCNAME', 'KRB5_CONFIG', 'KRB5_KTNAME', 'KRB5_CLIENT_KTNAME', 'KRB5RCACHEDIR',
  // 桌面会话:gnome-keyring / systemd 用户代理的 socket 路径,以及 ForwardX11 要 fork 的 xauth
  'XDG_RUNTIME_DIR', 'DISPLAY', 'XAUTHORITY', 'XAUTHLOCALHOSTNAME', 'WAYLAND_DISPLAY',
  // 终端与区域
  'TERM', 'COLORTERM', 'LANG', 'LANGUAGE',
  // 企业代理:ProxyCommand 走 nc -X connect / corkscrew 时必需
  'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY',
  // 云厂商 ProxyCommand 的选择器与路径(不含原始凭据,见上)
  'AWS_PROFILE', 'AWS_REGION', 'AWS_DEFAULT_REGION', 'AWS_CONFIG_FILE',
  'AWS_SHARED_CREDENTIALS_FILE', 'AWS_SDK_LOAD_CONFIG', 'GOOGLE_APPLICATION_CREDENTIALS'
]

/** 按前缀放行:LC_ 有 14 个,CLOUDSDK_ / TELEPORT_ 数量不定,逐个列会漏。 */
const INHERITED_PREFIXES = ['lc_', 'cloudsdk_', 'teleport_', 'tsh_']

/** 永不从继承来的环境里透传,即使调用方在 overrides 里塞进来:它们会改变子进程里 Node/Electron 的行为。 */
const DENIED_NAMES = ['ELECTRON_RUN_AS_NODE', 'NODE_OPTIONS']

/**
 * overrides 这一路的 deny —— 比上面少一个 `ELECTRON_RUN_AS_NODE`。
 *
 * 原先它对 overrides 也封死,理由是「别让它顺着 ssh 扩散到 ProxyCommand 那层」;
 * 那条理由仍然成立,所以**只**对这一个名字放开,而且只能由我们自己的代码显式传进来:
 * Windows 的 askpass helper 靠它让同一个 exe 以 node 形态启动(Windows 上 SSH_ASKPASS
 * 只能是 exe,没有 `sh` 可以包一层,见 `ssh/askpass.ts`)。缺了它,那边的密码认证在
 * 打包版上必然失败。`NODE_OPTIONS` 依旧全封:那是真正的注入面。
 */
const DENIED_OVERRIDE_NAMES = ['NODE_OPTIONS']

export function sshProcessEnvironment(overrides: NodeJS.ProcessEnv = {}, inherited: NodeJS.ProcessEnv = process.env): NodeJS.ProcessEnv {
  /**
   * ★ 按小写比对但**保留系统原始拼写**。Windows 上 `process.env` 查找本就大小写不敏感,
   * 逐个列出 ProgramFiles/PROGRAMFILES 这类拼写变体只会在子进程环境块里造出重复键,
   * 而且永远列不全(SystemRoot/SYSTEMROOT/systemroot…)。
   */
  const allowed = new Set(INHERITED_NAMES.map((name) => name.toLowerCase()))
  const denied = new Set(DENIED_NAMES.map((name) => name.toLowerCase()))
  const deniedOverrides = new Set(DENIED_OVERRIDE_NAMES.map((name) => name.toLowerCase()))
  const env: NodeJS.ProcessEnv = {}
  for (const [name, value] of Object.entries(inherited)) {
    if (value === undefined) continue
    const lower = name.toLowerCase()
    if (denied.has(lower)) continue
    if (allowed.has(lower) || INHERITED_PREFIXES.some((prefix) => lower.startsWith(prefix))) env[name] = value
  }
  for (const [name, value] of Object.entries(overrides)) {
    if (value === undefined || deniedOverrides.has(name.toLowerCase())) continue
    env[name] = value
  }
  return env
}

/**
 * ssh 连接失败时,`result.code !== 0` 本身分不出「主机密钥被拒」「认证失败」「网络/超时」——
 * 这三种原因过去全部落进同一个 `connection-failed`,UI 只能显示一句「无法连接服务器」,
 * 真正的原因(ssh 自己的 stderr)从未展示给用户。OpenSSH 不本地化这些字符串,匹配英文原文是安全的。
 */
function classifyConnectFailure(stderr: string): 'host-key' | 'authentication' | 'connection-failed' {
  if (/host key verification failed/i.test(stderr) || /REMOTE HOST IDENTIFICATION HAS CHANGED/.test(stderr)
    || /no matching host key type found/i.test(stderr)) return 'host-key'
  if (/permission denied/i.test(stderr)) return 'authentication'
  return 'connection-failed'
}

/** `ssh -G` 里「这项没设」有两种写法:键根本不出现,或者字面量 `none`(`askpass.ts` 同样这么判)。 */
function isConfigured(value: string | undefined): boolean {
  return value !== undefined && value !== '' && value !== 'none'
}

/**
 * 这台机器上能用的 ssh,按优先级排。
 *
 * 需求:Windows 上一次连接只认证一次,靠的是 ControlMaster,而**不是**每个 ssh 都支持。
 * 系统自带的 `OpenSSH_for_Windows` 没有 Unix socket,`ControlMaster` 一开就是
 * `getsockname failed: Not a socket`。Git for Windows 自带的是 MSYS 构建,支持,
 * 所以它排在系统自带之前。一个都没有时才退到系统自带 —— 那时连得上,但每条命令都要重新认证,
 * 由 `connect()` 里的远端多路复用兜底。
 *
 * 返回数组而不是一个路径:调用方要先试支持复用的,试不通再退。只返回第一个存在的,
 * 会把「装了 Git 但那份 ssh 恰好起不来」变成「整条连接退回逐条认证」。
 */
export function sshExecutableCandidates(): string[] {
  if (process.platform !== 'win32') {
    const unix = ['/usr/bin/ssh', '/bin/ssh', '/usr/local/bin/ssh'].filter(existsSync)
    if (unix.length === 0) throw new EnvironmentError('ssh-unavailable')
    return unix
  }
  const programFiles = process.env.ProgramFiles ?? 'C:\\Program Files'
  const programFilesX86 = process.env['ProgramFiles(x86)'] ?? 'C:\\Program Files (x86)'
  const localAppData = process.env.LOCALAPPDATA ?? ''
  const multiplexing = [
    join(programFiles, 'Git', 'usr', 'bin', 'ssh.exe'),
    join(programFilesX86, 'Git', 'usr', 'bin', 'ssh.exe'),
    ...(localAppData === '' ? [] : [join(localAppData, 'Programs', 'Git', 'usr', 'bin', 'ssh.exe')])
  ]
  const builtin = [
    join(process.env.SystemRoot ?? 'C:\\Windows', 'System32', 'OpenSSH', 'ssh.exe'),
    join(programFiles, 'OpenSSH', 'ssh.exe')
  ]
  const found = [...multiplexing, ...builtin].filter(existsSync)
  if (found.length === 0) throw new EnvironmentError('ssh-unavailable')
  return found
}

export function sshExecutable(): string {
  return sshExecutableCandidates()[0] ?? (() => { throw new EnvironmentError('ssh-unavailable') })()
}

export class OpenSshTransport {
  private directory = ''
  private control = ''
  private closed = false
  private closing?: Promise<void>
  private tunnel?: SshProxyTunnel
  /** 代理隧道的改道参数,没走代理时是空数组。见 `proxyTunnelArgs` 对顺序的要求。 */
  private tunnelArgs: string[] = []
  private readonly children = new Set<ChildProcessWithoutNullStreams>()
  /**
   * 这次连接实际用的 ssh。`connect()` 里定下来,后面的命令、SFTP、终端都用同一个,
   * 否则主连接走 Git 的 ssh、后续命令走系统自带的,ControlPath 对不上,等于没复用。
   */
  private executable = ''
  /**
   * 只有「这台机器上的 ssh 都不支持 ControlMaster」时才有值,见 `startWindowsMux`。
   * 远端没有 Python 时保持空,后面的命令退回「每条一次 ssh」。
   */
  private mux?: SessionMux

  constructor(readonly profile: SshConnectionProfile, private readonly options: OpenSshOptions = {}) {}

  private baseArgs(): string[] {
    return ['-o', 'ServerAliveInterval=15', '-o', 'ServerAliveCountMax=2', '-o', 'ConnectTimeout=15',
      ...(this.control ? ['-S', this.control] : []), ...this.tunnelArgs, ...sshTargetArgs(this.profile)]
  }

  private launch(args: string[], closing = false): ChildProcessWithoutNullStreams {
    if (this.closed && !closing) throw new EnvironmentError('disconnected')
    const env = sshProcessEnvironment(this.options.env)
    const child = spawn(this.options.executable ?? (this.executable || sshExecutable()), args, { shell: false, windowsHide: true, env, stdio: 'pipe' })
    this.children.add(child)
    child.once('close', () => this.children.delete(child))
    child.on('error', () => {})
    child.stdin.on('error', () => {})
    return child
  }

  async connect(signal: AbortSignal): Promise<void> {
    signal.throwIfAborted()
    const config = await this.capture(['-G', ...sshTargetArgs(this.profile)], signal, 15_000)
    if (config.code !== 0) throw new EnvironmentError('unsupported-config', config.stderr.slice(-2000))
    const values = new Map(config.stdout.split('\n').map((line) => { const split = line.indexOf(' '); return [line.slice(0, split), line.slice(split + 1)] }))
    // 交给认证 broker —— 必须在下面那个会认证的 ssh 之前,否则密码提示来时它还不知道要连哪儿
    this.options.onResolved?.(values)
    if (values.get('remotecommand') && values.get('remotecommand') !== 'none') {
      throw new EnvironmentError('unsupported-config', 'RemoteCommand conflicts with workspace command and SFTP sessions. Use a dedicated Host alias.')
    }
    await this.prepareProxy(values)
    if (process.platform !== 'win32') {
      this.directory = await mkdtemp(join(tmpdir(), 'ncw-ssh-'))
      await chmod(this.directory, 0o700)
      this.control = join(this.directory, 'control')
      const result = await this.capture(['-T', '-M', '-o', 'ControlPersist=60', ...this.baseArgs(), 'echo NextCoWork-SSH-Ready'], signal, 5 * 60_000)
      if (result.code !== 0 || !result.stdout.includes('NextCoWork-SSH-Ready')) {
        await this.close()
        throw new EnvironmentError(classifyConnectFailure(result.stderr), result.stderr.slice(-2000))
      }
      return
    }
    /**
     * 需求:Windows 上一次连接只认证一次,而且不要求远端装任何东西。
     *
     * 先试支持 ControlMaster 的 ssh(Git for Windows 那份)。它和 macOS / Linux 走的是
     * 同一条路:主连接认证一次,后面的命令用 `-S` 接上去。系统自带的 OpenSSH 不支持,
     * 报 `getsockname failed: Not a socket` —— 那不是网络故障,换下一份 ssh 再试。
     * 每一份都这样,才退到 `startWindowsMux`:在这一次 ssh 上跑远端多路复用,那条路要求远端有 Python。
     */
    const candidates = this.options.executable ? [this.options.executable] : sshExecutableCandidates()
    let muxFallback: { stderr: string } | undefined
    for (const candidate of candidates) {
      this.executable = candidate
      this.directory = await mkdtemp(join(tmpdir(), 'ncw-ssh-'))
      this.control = join(this.directory, 'control')
      const result = await this.capture(['-T', '-M', '-o', 'ControlPersist=60', ...this.baseArgs(), 'echo NextCoWork-SSH-Ready'], signal, 5 * 60_000)
      if (result.code === 0 && result.stdout.includes('NextCoWork-SSH-Ready')) return
      const unsupported = /not a socket|getsockname failed|unix domain sockets are not supported/i.test(result.stderr)
      await this.discardControlAttempt()
      if (!unsupported) {
        await this.close()
        throw new EnvironmentError(classifyConnectFailure(result.stderr), result.stderr.slice(-2000))
      }
      muxFallback = { stderr: result.stderr }
    }
    if (muxFallback === undefined) throw new EnvironmentError('ssh-unavailable')
    await this.startWindowsMux(signal)
  }

  /** 一次没建成的 ControlMaster 尝试留下的临时目录。下一次尝试要一个新的,不能复用这个路径。 */
  private async discardControlAttempt(): Promise<void> {
    this.control = ''
    if (this.directory === '') return
    const directory = this.directory
    this.directory = ''
    await rm(directory, { recursive: true, force: true }).catch(() => {})
  }

  /**
   * 这台机器上没有任何一份 ssh 支持 ControlMaster 时的退路。
   *
   * 在**这一次**已认证的 ssh 上跑远端多路复用(见 `session-mux.ts`)。远端没有 Python 时
   * stderr 里会有 `NCW-MUX-MISSING`:那条 ssh 已经认证过了,关掉它,后面退回每条命令一次 ssh。
   * 密码还会被问,但至少连得上。
   */
  private async startWindowsMux(signal: AbortSignal): Promise<void> {
    const child = this.launch(['-T', ...this.baseArgs(), remoteMuxCommand()])
    const mux = new SessionMux(child.stdin, child.stdout, () => { if (!this.closed) this.options.onDisconnect?.() })
    const stderr: Buffer[] = []
    child.stderr.on('data', (bytes: Buffer) => stderr.push(bytes))
    const outcome = await Promise.race([
      mux.ready.then(() => 'ready' as const),
      new Promise<{ code: number; stderr: string }>((resolve) => {
        const settle = (code: number): void => resolve({ code, stderr: Buffer.concat(stderr).toString('utf8') })
        child.once('close', (code) => settle(code ?? 255))
        child.once('error', () => settle(255))
      })
    ])
    signal.throwIfAborted()
    if (outcome !== 'ready') {
      child.kill()
      await this.close()
      if (outcome.stderr.includes(MUX_MISSING)) return
      throw new EnvironmentError(classifyConnectFailure(outcome.stderr), outcome.stderr.slice(-2000))
    }
    this.mux = mux
    child.once('close', () => { if (!this.closed) this.options.onDisconnect?.() })
  }

  /**
   * 需求:SSH 连接默认跟随系统代理。隧道必须在**会认证的那次 ssh 之前**搭好。
   *
   * ★ 用户自己配了 ProxyJump / ProxyCommand 就完全不插手:那是他写明的线路,
   *   再套一层等于悄悄改掉他的拓扑(而且第一跳早就不是 `hostname` 那台机器了)。
   * ★ `-G` 给不出 hostname/port 时也不插手 —— 退回直连比把 ssh 指到一条
   *   注定连不上的隧道好:后者表现为「装了代理之后所有 SSH 都连不上」。
   */
  private async prepareProxy(values: Map<string, string>): Promise<void> {
    const open = this.options.openProxyTunnel
    if (!open) return
    const hostname = values.get('hostname')
    const port = Number(values.get('port'))
    if (hostname === undefined || hostname === '' || !Number.isInteger(port) || port < 1 || port > 65535) return
    if (isConfigured(values.get('proxyjump')) || isConfigured(values.get('proxycommand'))) return
    const tunnel = await open(hostname, port)
    if (tunnel === null) return
    this.tunnel = tunnel
    this.tunnelArgs = proxyTunnelArgs(hostname, port, tunnel.port, isConfigured(values.get('hostkeyalias')))
  }

  async capture(args: string[], signal: AbortSignal, timeoutMs: number, closing = false): Promise<{ code: number; stdout: string; stderr: string }> {
    signal.throwIfAborted()
    const child = this.launch(args, closing)
    child.stdin.end()
    return new Promise((resolve, reject) => {
      const stdout: Buffer[] = []
      const stderr: Buffer[] = []
      let length = 0
      let failure: Error | undefined
      const stop = (error: Error): void => { failure ??= error; child.kill(); reject(error) }
      const abort = (): void => stop(new EnvironmentError('cancelled'))
      const timer = setTimeout(() => stop(new EnvironmentError('timeout')), timeoutMs)
      signal.addEventListener('abort', abort, { once: true })
      const collect = (target: Buffer[], bytes: Buffer): void => {
        length += bytes.byteLength
        if (length > 8 * 1024 * 1024) stop(new EnvironmentError('unsupported', 'SSH output limit exceeded'))
        else target.push(bytes)
      }
      child.stdout.on('data', (bytes: Buffer) => collect(stdout, bytes))
      child.stderr.on('data', (bytes: Buffer) => collect(stderr, bytes))
      child.once('error', (error) => { failure = error; reject(error) })
      child.once('close', (code) => {
        clearTimeout(timer)
        signal.removeEventListener('abort', abort)
        if (failure) return
        resolve({ code: code ?? 255, stdout: Buffer.concat(stdout).toString('utf8'), stderr: Buffer.concat(stderr).toString('utf8') })
      })
    })
  }

  async exec(command: string, signal: AbortSignal, timeoutMs = 120_000): Promise<{ code: number; stdout: string; stderr: string }> {
    signal.throwIfAborted()
    if (this.mux) {
      try { return await this.mux.exec(command, signal, timeoutMs) } catch (error) {
        if (error instanceof EnvironmentError && (error.code === 'cancelled' || error.code === 'timeout')) throw new EnvironmentError('result-unknown')
        throw error
      }
    }
    let result: Awaited<ReturnType<OpenSshTransport['capture']>>
    try { result = await this.capture(['-T', ...this.baseArgs(), command], signal, timeoutMs) } catch (error) {
      if (error instanceof EnvironmentError && (error.code === 'cancelled' || error.code === 'timeout')) throw new EnvironmentError('result-unknown')
      throw error
    }
    if (result.code === 255) throw new EnvironmentError('result-unknown', result.stderr.slice(-2000))
    return result
  }

  process(command: string, input?: string): EnvironmentProcess {
    if (this.mux) return this.mux.openProcess(command, input)
    const child = this.launch(['-T', ...this.baseArgs(), command])
    if (input !== undefined) child.stdin.write(input)
    const exited = new Promise<{ code: number | null; signal?: string | null }>((resolve) => {
      child.once('close', (code, signal) => resolve({ code, signal }))
      child.once('error', () => resolve({ code: null }))
    })
    return { stdin: child.stdin, stdout: child.stdout, stderr: child.stderr, exited, kill: () => { child.kill() } }
  }

  subsystem(): ChildProcessWithoutNullStreams | MuxByteStream {
    if (this.mux) return this.mux.openSubsystem()
    const child = this.launch(['-T', '-s', ...this.baseArgs(), 'sftp'])
    child.once('close', () => { if (!this.closed) this.options.onDisconnect?.() })
    return child
  }

  async openTcp(hostname: string, port: number): Promise<Socket> {
    if (!/^[a-zA-Z0-9_.:%-]+$/.test(hostname) || !Number.isInteger(port) || port < 1 || port > 65535) throw new EnvironmentError('invalid-profile')
    if (this.mux) return this.mux.openTcp(hostname, port)
    const target = `${hostname.includes(':') ? `[${hostname}]` : hostname}:${String(port)}`
    const server = createServer()
    await new Promise<void>((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve) })
    const address = server.address()
    if (!address || typeof address === 'string') { server.close(); throw new EnvironmentError('connection-failed') }
    const client = createConnection({ host: '127.0.0.1', port: address.port })
    return new Promise<Socket>((resolve, reject) => {
      const timer = setTimeout(() => { server.close(); client.destroy(); reject(new EnvironmentError('timeout')) }, 5000)
      client.once('error', (error) => { clearTimeout(timer); server.close(); reject(error) })
      server.on('connection', (socket) => {
        if (socket.remotePort !== client.localPort) { socket.destroy(); return }
        clearTimeout(timer)
        server.close()
        let child: ChildProcessWithoutNullStreams
        try { child = this.launch(['-T', '-W', target, ...this.baseArgs()]) } catch (error) {
          socket.destroy(); client.destroy(); reject(error); return
        }
        child.stderr.resume()
        child.stdout.pipe(socket)
        socket.pipe(child.stdin)
        socket.on('error', () => { child.kill() })
        socket.once('close', () => { child.kill() })
        child.once('error', () => { socket.destroy() })
        child.once('close', () => { socket.destroy() })
        resolve(client)
      })
    })
  }

  terminalArgs(command: string): { executable: string; args: string[]; env: NodeJS.ProcessEnv } {
    if (this.closed) throw new EnvironmentError('disconnected')
    return { executable: this.options.executable ?? (this.executable || sshExecutable()), args: ['-tt', ...this.baseArgs(), command], env: sshProcessEnvironment(this.options.env) }
  }

  close(): Promise<void> {
    if (this.closing) return this.closing
    this.closed = true
    this.mux?.close()
    for (const child of this.children) child.kill()
    this.closing = (async () => {
      if (this.control) await this.capture(['-O', 'exit', ...this.baseArgs()], AbortSignal.timeout(2000), 2000, true).catch(() => {})
      // 隧道最后关:`-O exit` 那一发虽然走的是 control socket,但提前拆掉隧道会让
      // 还没退干净的 ssh 子进程在重连时打到一个已经消失的端口上。
      await this.tunnel?.close()
      if (this.directory) await rm(this.directory, { recursive: true, force: true })
    })()
    return this.closing
  }
}