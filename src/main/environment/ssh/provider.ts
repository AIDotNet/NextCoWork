import type { EnvironmentFacts, SshConnectionProfile } from '../../../shared/domain/environment'
import { abortable } from '../../kernel/abort'
import type { EnvironmentConnection, TerminalDriver } from '../contract'
import type { ConnectionContext } from '../manager'
import { EnvironmentError } from '../errors'
import { createWorkspacePaths } from '../paths'
import { POSIX_PROBE, remoteCommand, remoteProcessRequest, remoteTerminalCommand, shellQuote, WINDOWS_PROBE } from './command'
import { BundledSshClient, type HostKeyVerifier } from './bundled-client'
import { OpenSshTransport, type OpenSshOptions } from './transport'
import { SftpFileSystem } from './sftp'

/** 内置客户端这条路要的认证能力。`askHostKey` / `secrets` 强制要求 —— 缺了就不走这条路(见下)。 */
type BundledAuthentication = {
  close(): Promise<void>
  ask(prompt: string, rejected: boolean): Promise<string>
  askHostKey(prompt: string): Promise<boolean>
  secrets: HostKeyVerifier['secrets']
}

export async function connectSshEnvironment(profile: SshConnectionProfile,
  context: ConnectionContext & { generation: number; assertCurrent(): void; onDisconnect(): void },
  authentication: { env: NodeJS.ProcessEnv; close(): Promise<void>; resolve?(values: Map<string, string>): void; ask?(prompt: string, rejected: boolean): Promise<string>; askHostKey?(prompt: string): Promise<boolean>; secrets?: HostKeyVerifier['secrets'] },
  options: Pick<OpenSshOptions, 'executable' | 'openProxyTunnel'> = {}): Promise<EnvironmentConnection> {
  /**
   * Windows 上手动填写的连接走内置客户端:一条连接认证一次,后面的命令不再起 ssh。
   * 配置型别名读不到 ~/.ssh/config,继续走系统 ssh。
   *
   * ★ `askHostKey` 是**进门条件**,不是可选装饰:内置客户端不读 known_hosts,没有主机密钥
   * 确认就没有任何东西能验指纹。缺了它宁可退回系统 ssh(那条自己会验),也绝不建一条
   * "谁都信"的连接 —— 这正是原先 `hostVerifier: () => true` 的病根。
   */
  if (process.platform === 'win32' && authentication.ask && authentication.askHostKey && authentication.secrets
    && BundledSshClient.supports(profile)) {
    return connectBundled(profile, context, authentication as BundledAuthentication)
  }
  let closed = false
  let filesystem: SftpFileSystem | undefined
  const terminals = new Set<TerminalDriver>()
  const transport = new OpenSshTransport(profile, { ...options, env: authentication.env,
    ...(authentication.resolve ? { onResolved: authentication.resolve } : {}),
    onDisconnect: () => { closed = true; context.onDisconnect() } })
  const assertReady = (): void => {
    context.assertCurrent()
    if (closed) throw new EnvironmentError('disconnected')
  }
  const close = async (): Promise<void> => {
    closed = true
    for (const terminal of terminals) terminal.kill()
    terminals.clear()
    filesystem?.close()
    await transport.close()
    await authentication.close()
  }
  try {
    await transport.connect(context.signal)
    let facts: EnvironmentFacts | undefined
    if (profile.platform !== 'win32') {
      const result = await transport.exec(`/bin/sh -c ${shellQuote(POSIX_PROBE)}`, context.signal, 15_000)
      if (result.code === 0) {
        const [system, osVersion, hostname, username, home, shell] = result.stdout.split('\0')
        const os = system === 'Darwin' ? 'darwin' : system === 'Linux' ? 'linux' : undefined
        if (os && osVersion && hostname && username && home && shell) facts = { os, osVersion, hostname, username, home, shell }
      }
    }
    if (!facts && (profile.platform === 'auto' || profile.platform === 'win32')) {
      const result = await transport.exec(WINDOWS_PROBE, context.signal, 15_000)
      if (result.code === 0) {
        try {
          const candidate = JSON.parse(result.stdout.replace(/^\uFEFF/, '').trim()) as EnvironmentFacts
          if (candidate.os === 'win32' && ['osVersion', 'hostname', 'username', 'home', 'shell'].every((key) =>
            typeof candidate[key as keyof EnvironmentFacts] === 'string' && candidate[key as keyof EnvironmentFacts] !== '')) facts = candidate
        } catch { throw new EnvironmentError('unsupported-platform') }
      }
    }
    if (!facts || (profile.platform !== 'auto' && facts.os !== profile.platform)) throw new EnvironmentError('unsupported-platform')
    const platform = facts
    const channel = transport.subsystem()
    channel.stderr.resume()
    filesystem = new SftpFileSystem(channel.stdin, channel.stdout, platform.os, assertReady, context.onDisconnect)
    await abortable(() => filesystem!.ready, context.signal)
    platform.home = await filesystem.realpath(platform.home)
    const path = createWorkspacePaths(filesystem, platform.os)
    const environment: EnvironmentConnection = {
      key: JSON.stringify(['ssh', profile.id, profile.revision, context.generation]), generation: context.generation,
      remote: true, description: `${profile.name} (${platform.username}@${platform.hostname})`, facts: platform, platform, fs: filesystem, path, assertReady, close,
      openTcp: async (hostname, port) => { assertReady(); return transport.openTcp(hostname, port) },
      spawn: async (command, request) => {
        assertReady()
        return transport.exec(remoteCommand(platform.os, platform.shell, request.cwd, command), request.signal, request.timeoutMs)
      },
      openProcess: async (command, args, request) => {
        assertReady()
        const prepared = remoteProcessRequest(platform.os, platform.shell, request.cwd, command, args, request.env)
        return transport.process(prepared.command, prepared.input)
      },
      openTerminal: async (request) => {
        assertReady()
        // request.env 刻意忽略:一期插件终端仅本地(拒绝在 ipc/plugins.ts 的
        // launchTerminal 里做),这里不消费只是兜底 —— 远程 pty 的 env 注入
        // 要动 remoteTerminalCommand 的行内拼接,留到那一期一起做。
        const canonical = await filesystem!.realpath(request.cwd)
        if (!(await filesystem!.stat(canonical)).isDir) throw new EnvironmentError('invalid-path')
        const invocation = transport.terminalArgs(remoteTerminalCommand(platform.os, platform.shell, canonical))
        const pty = await import('node-pty')
        assertReady()
        const terminal = pty.spawn(invocation.executable, invocation.args, { name: 'xterm-256color', cols: request.cols, rows: request.rows,
          env: { ...invocation.env, TERM: 'xterm-256color' } as Record<string, string> })
        terminals.add(terminal)
        terminal.onExit(() => terminals.delete(terminal))
        return terminal
      }
    }
    return environment
  } catch (error) { await close(); throw error }
}

/**
 * 内置客户端的连接。和上面那个系统 ssh 的版本对外是同一个 `EnvironmentConnection`,
 * 差别只在传输:这里所有 exec / SFTP / TCP 都是同一条已认证连接上的通道。
 */
async function connectBundled(profile: SshConnectionProfile,
  context: ConnectionContext & { generation: number; assertCurrent(): void; onDisconnect(): void },
  authentication: BundledAuthentication): Promise<EnvironmentConnection> {
  let closed = false
  let filesystem: SftpFileSystem | undefined
  const client = new BundledSshClient(profile, authentication.ask,
    { secrets: authentication.secrets, confirm: (prompt) => authentication.askHostKey(prompt) })
  const assertReady = (): void => {
    context.assertCurrent()
    if (closed) throw new EnvironmentError('disconnected')
  }
  const close = async (): Promise<void> => {
    closed = true
    filesystem?.close()
    await client.close()
    await authentication.close()
  }
  try {
    await client.connect(context.signal)
    let facts: EnvironmentFacts | undefined
    if (profile.platform !== 'win32') {
      const result = await client.exec(`/bin/sh -c ${shellQuote(POSIX_PROBE)}`, context.signal, 15_000)
      if (result.code === 0) {
        const [system, osVersion, hostname, username, home, shell] = result.stdout.split('\0')
        const os = system === 'Darwin' ? 'darwin' : system === 'Linux' ? 'linux' : undefined
        if (os && osVersion && hostname && username && home && shell) facts = { os, osVersion, hostname, username, home, shell }
      }
    }
    if (!facts && (profile.platform === 'auto' || profile.platform === 'win32')) {
      const result = await client.exec(WINDOWS_PROBE, context.signal, 15_000)
      if (result.code === 0) {
        const candidate = JSON.parse(result.stdout.replace(/^\uFEFF/, '').trim()) as EnvironmentFacts
        if (candidate.os === 'win32' && ['osVersion', 'hostname', 'username', 'home', 'shell'].every((key) =>
          typeof candidate[key as keyof EnvironmentFacts] === 'string' && candidate[key as keyof EnvironmentFacts] !== '')) facts = candidate
      }
    }
    if (!facts || (profile.platform !== 'auto' && facts.os !== profile.platform)) throw new EnvironmentError('unsupported-platform')
    const platform = facts
    const channel = client.subsystem()
    filesystem = new SftpFileSystem(channel.stdin, channel.stdout, platform.os, assertReady, context.onDisconnect)
    await abortable(() => filesystem!.ready, context.signal)
    platform.home = await filesystem.realpath(platform.home)
    const path = createWorkspacePaths(filesystem, platform.os)
    const environment: EnvironmentConnection = {
      key: JSON.stringify(['ssh', profile.id, profile.revision, context.generation]), generation: context.generation,
      remote: true, description: `${profile.name} (${platform.username}@${platform.hostname})`, facts: platform, platform, fs: filesystem, path, assertReady, close,
      openTcp: async (hostname, port) => { assertReady(); return client.openTcp(hostname, port ?? 0) },
      spawn: async (command, request) => {
        assertReady()
        return client.exec(remoteCommand(platform.os, platform.shell, request.cwd, command), request.signal, request.timeoutMs ?? 120_000)
      },
      openProcess: async (command, args, request) => {
        assertReady()
        const prepared = remoteProcessRequest(platform.os, platform.shell, request.cwd, command, args, request.env)
        return client.process(prepared.command, prepared.input)
      },
      // 终端要 PTY,这条纯 JS 连接给不了。打开终端会走到「没有终端驱动」而不是再起一个要密码的 ssh。
      openTerminal: () => Promise.reject(new EnvironmentError('unsupported'))
    }
    return environment
  } catch (error) { await close(); throw error }
}