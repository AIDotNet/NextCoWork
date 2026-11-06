import { spawn } from 'node:child_process'
import { createRequire } from 'node:module'
import { existsSync } from 'node:fs'
import { chmod, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises'
import { createServer } from 'node:net'
import { tmpdir, userInfo } from 'node:os'
import { join } from 'node:path'
import { expect, it } from 'vitest'
import type { SshConnectionProfile } from '../../../shared/domain/environment'
import { connectionSecretRef } from '../../../shared/domain/environment'
import { nodeHost } from '../../kernel/host'
import { SshAuthBroker } from '../ssh/askpass'
import { POSIX_PROBE, remoteTerminalCommand } from '../ssh/command'
import { OpenSshTransport } from '../ssh/transport'
import { SftpFileSystem } from '../ssh/sftp'
import { execute, integration, isolatedSshd, readyConfig, remoteProcessAlive, until, type Sshd } from './sshd-fixture'

const profile: SshConnectionProfile = { id: 'native-test', name: 'native-test', kind: 'ssh', enabled: true,
  platform: 'auto', revision: 1, createdAt: 0, updatedAt: 0, target: { kind: 'config', host: 'native-test' } }
const electronBinary = createRequire(import.meta.url)('electron') as string

/**
 * 一份**逼 ssh 走密码认证**的客户端 config。
 *
 * `PubkeyAuthentication no` 是关键:夹具里那把 client key 是能用的,不关掉的话 ssh 走公钥
 * 直接连上,一句密码都不会问 —— 用例会以"没收到提示"的形式静默通过。
 */
async function passwordConfig(sshd: Sshd): Promise<string> {
  const knownHosts = join(sshd.directory, 'known_hosts_password')
  await writeFile(knownHosts, `[127.0.0.1]:${sshd.port} ${sshd.hostKeyPublic}\n`, { mode: 0o600 })
  return sshd.config(`Host native-test\n HostName 127.0.0.1\n Port ${sshd.port}\n User ${sshd.username}\n`
    + ` PubkeyAuthentication no\n PreferredAuthentications password\n NumberOfPasswordPrompts 1\n`
    + ` IdentityAgent none\n UserKnownHostsFile ${knownHosts}\n StrictHostKeyChecking yes\n`)
}

it.skipIf(!integration)('runs the built Electron askpass entry without opening application data', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'ncw-helper-test-'))
  const broker = new SshAuthBroker(nodeHost().secrets, (sender, request) => {
    void broker.respond(sender, { id: request.id, value: 'fixture-answer' })
  })
  const auth = await broker.open(profile, 1, { executable: electronBinary, appPath: process.cwd() })
  try {
    const result = await execute(electronBinary, [process.cwd(), `--user-data-dir=${join(directory, 'profile')}`, 'Fixture challenge:'],
      { cwd: directory, env: { ...process.env, ...auth.env, ELECTRON_RUN_AS_NODE: undefined }, timeout: 20_000 })
    expect(result.stdout).toBe('fixture-answer\n')
    expect(existsSync(join(directory, '.next-cowork'))).toBe(false)
  } finally { await auth.close(); await rm(directory, { recursive: true, force: true }) }
}, 30_000)

it.skipIf(!integration || process.platform === 'win32')('uses native config, encrypted-key askpass and SFTP against an isolated sshd', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'ncw-sshd-test-'))
  await chmod(directory, 0o700)
  const hostKey = join(directory, 'host-key')
  const clientKey = join(directory, 'client-key')
  const portServer = createServer()
  await new Promise<void>((resolve) => portServer.listen(0, '127.0.0.1', resolve))
  const address = portServer.address()
  if (!address || typeof address === 'string') throw new Error('No loopback port')
  const port = address.port
  await new Promise<void>((resolve) => portServer.close(() => resolve()))
  const sftpServer = ['/usr/libexec/sftp-server', '/usr/lib/openssh/sftp-server'].find(existsSync)
  if (!sftpServer) throw new Error('Native SFTP subsystem missing')
  await execute('/usr/bin/ssh-keygen', ['-q', '-t', 'ed25519', '-N', '', '-f', hostKey])
  await execute('/usr/bin/ssh-keygen', ['-q', '-t', 'ed25519', '-N', 'fixture-passphrase', '-f', clientKey])
  await writeFile(join(directory, 'authorized_keys'), await readFile(`${clientKey}.pub`), { mode: 0o600 })
  await writeFile(join(directory, 'known_hosts'), `[127.0.0.1]:${port} ${(await readFile(`${hostKey}.pub`, 'utf8')).trim()}\n`, { mode: 0o600 })
  const config = join(directory, 'sshd_config')
  await writeFile(config, `Port ${port}\nListenAddress 127.0.0.1\nHostKey ${hostKey}\nPidFile ${join(directory, 'pid')}\n`
    + `AuthorizedKeysFile ${join(directory, 'authorized_keys')}\nStrictModes yes\nPasswordAuthentication no\nKbdInteractiveAuthentication no\n`
    + `UsePAM no\nPermitRootLogin no\nAllowUsers ${userInfo().username}\nSubsystem sftp ${sftpServer}\nLogLevel VERBOSE\n`, { mode: 0o600 })
  const clientConfig = join(directory, 'ssh_config')
  await writeFile(clientConfig, `Host native-test\n HostName 127.0.0.1\n Port ${port}\n User ${userInfo().username}\n`
    + ` IdentityFile ${clientKey}\n IdentitiesOnly yes\n IdentityAgent none\n UserKnownHostsFile ${join(directory, 'known_hosts')}\n StrictHostKeyChecking yes\n`, { mode: 0o600 })
  const daemon = spawn('/usr/sbin/sshd', ['-D', '-e', '-f', config], { stdio: ['ignore', 'ignore', 'pipe'] })
  let diagnostic = ''
  let auth: Awaited<ReturnType<SshAuthBroker['open']>> | undefined
  let transport: OpenSshTransport | undefined
  try {
    await new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(`sshd startup timed out: ${diagnostic}`)), 5000)
      daemon.once('error', (error) => { clearTimeout(timer); reject(error) })
      daemon.once('exit', (code) => { clearTimeout(timer); reject(new Error(`sshd exited ${code}: ${diagnostic}`)) })
      daemon.stderr.on('data', (bytes: Buffer) => {
        diagnostic += bytes.toString('utf8')
        if (diagnostic.includes('Server listening on')) { clearTimeout(timer); resolve() }
      })
    })
    let prompts = 0
    const broker = new SshAuthBroker(nodeHost().secrets, (sender, request) => {
      prompts++
      void broker.respond(sender, { id: request.id, value: request.kind === 'passphrase' ? 'fixture-passphrase' : 'no' })
    })
    auth = await broker.open(profile, 1, { executable: electronBinary, appPath: process.cwd() })
    transport = new OpenSshTransport({ ...profile, target: { kind: 'config', host: 'native-test', configFile: clientConfig } }, { env: auth.env })
    await transport.connect(AbortSignal.timeout(15_000))
    const result = await transport.exec(POSIX_PROBE, AbortSignal.timeout(5000))
    expect(result.code).toBe(0)
    expect(result.stdout.split('\0')[3]).toBe(userInfo().username)
    const subsystem = transport.subsystem()
    subsystem.stderr.resume()
    const fs = new SftpFileSystem(subsystem.stdin, subsystem.stdout, process.platform)
    try {
      await fs.ready
      const path = join(directory, 'remote-roundtrip')
      await fs.writeFile(path, 'via native SSH')
      expect(await fs.readFile(path)).toBe('via native SSH')
    } finally { fs.close(); if ('kill' in subsystem) subsystem.kill() }
    expect(prompts).toBe(1)
  } finally {
    await transport?.close()
    await auth?.close()
    daemon.kill()
    await rm(directory, { recursive: true, force: true })
  }
}, 45_000)
/**
 * ★ host key 首次确认。这条是本组的核心。
 *
 * `askpass.ts` 靠 `payload.hint === 'confirm'`(来自 ssh 设的 `SSH_ASKPASS_PROMPT`)把提示
 * 判成 host-key,而在此之前**没有任何用例让真实 ssh 走过这个分支** —— 既有的集成用例
 * 预置了 known_hosts。判错的后果不是报错而是**降级**:UI 把指纹确认显示成普通密码输入框,
 * 且 `respond()` 里那条 `value !== 'yes' && value !== 'no'` 的强校验不会生效。
 */
it.skipIf(!integration || process.platform === 'win32')('classifies a real host key prompt as host-key and records the accepted key', async () => {
  const sshd = await isolatedSshd()
  const knownHosts = join(sshd.directory, 'known_hosts_empty')
  await writeFile(knownHosts, '', { mode: 0o600 })
  const clientConfig = await sshd.config(`Host native-test\n HostName 127.0.0.1\n Port ${sshd.port}\n User ${sshd.username}\n`
    + ` IdentityFile ${sshd.clientKey}\n IdentitiesOnly yes\n IdentityAgent none\n UserKnownHostsFile ${knownHosts}\n StrictHostKeyChecking ask\n`)
  const kinds: string[] = []
  const prompts: string[] = []
  const broker = new SshAuthBroker(nodeHost().secrets, (sender, request) => {
    kinds.push(request.kind)
    prompts.push(request.prompt)
    void broker.respond(sender, { id: request.id, value: request.kind === 'host-key' ? 'yes' : 'fixture' })
  })
  const auth = await broker.open(profile, 1, { executable: electronBinary, appPath: process.cwd() })
  const transport = new OpenSshTransport({ ...profile, target: { kind: 'config', host: 'native-test', configFile: clientConfig } }, { env: auth.env })
  try {
    const outcome = await transport.connect(AbortSignal.timeout(20_000)).then(() => 'connected', (error: Error) => error.message)
    expect(kinds, `真实 ssh 的 host key 提示必须判成 host-key。连接结果:${outcome};收到的提示:${JSON.stringify(prompts)}`).toContain('host-key')
    expect(outcome).toBe('connected')
    // 接受之后 ssh 必须把它写进我们指定的 known_hosts，而不是用户的 ~/.ssh/known_hosts
    expect(await readFile(knownHosts, 'utf8')).toContain(sshd.hostKeyPublic.split(' ')[1]!)
  } finally { await transport.close(); await auth.close(); await sshd.close() }
}, 60_000)

/**
 * ★ 真实 ssh 的**密码**提示长什么样,在此之前没有任何用例走过。
 *
 * 既有的集成夹具一律 `PasswordAuthentication no` —— 也就是说真实 ssh 走过的认证提示只有
 * `passphrase` 和 `host-key` 两种,`password` 的真实覆盖是零。而 `host-key` 的分类曾经就是
 * 错的(OpenSSH 10.3 做 host key 确认时并不设 `SSH_ASKPASS_PROMPT`),那个 bug 是开了真实
 * 开关之后第一次运行抓到的,mock 原理上抓不到。这里给 `password` 补同一根哨兵。
 *
 * 分类错的后果很具体:掉进 `challenge` 分支之后标签显示成"认证响应"、`canRemember` 关闭,
 * 于是**存下的密码永远不会被用上**,自动作答整条路径静默失效。
 */
it.skipIf(!integration || process.platform === 'win32')('classifies a real password prompt as password', async () => {
  const sshd = await isolatedSshd({ passwordAuth: true })
  const clientConfig = await passwordConfig(sshd)
  const kinds: string[] = []
  const prompts: string[] = []
  const broker = new SshAuthBroker(nodeHost().secrets, (sender, request) => {
    kinds.push(request.kind)
    prompts.push(request.prompt)
    void broker.respond(sender, { id: request.id, value: 'wrong-on-purpose' })
  })
  const auth = await broker.open(profile, 1, { executable: electronBinary, appPath: process.cwd() })
  const transport = new OpenSshTransport({ ...profile, target: { kind: 'config', host: 'native-test', configFile: clientConfig } },
    { env: auth.env, onResolved: auth.resolve })
  try {
    const outcome = await transport.connect(AbortSignal.timeout(20_000)).then(() => 'connected', (error: Error) => error.message)
    expect(kinds, `真实 ssh 的密码提示必须判成 password。连接结果:${outcome};收到的提示:${JSON.stringify(prompts)}`).toEqual(['password'])
    // `UsePAM no` 下 sshd 校验不了密码 —— 认证注定失败,这里断言的是**分类**,不是登录成功
    expect(outcome).not.toBe('connected')
  } finally { await transport.close(); await auth.close(); await sshd.close() }
}, 60_000)

/**
 * ★ 存下的密码被**真实 ssh 的真实提示**触发、自动作答,界面上不弹窗。
 *
 * "自动作答了"这件事必须有正面证据:只断言"没弹窗"是空的 —— askpass 压根没被调用时
 * 也没弹窗。所以同时断言 broker 确实去取过那把密码。
 */
it.skipIf(!integration || process.platform === 'win32')('answers a real password prompt from storage without prompting the window', async () => {
  const sshd = await isolatedSshd({ passwordAuth: true })
  const clientConfig = await passwordConfig(sshd)
  const ref = connectionSecretRef(profile.id, 'password')
  const reads: string[] = []
  const secrets = {
    get: (key: string) => { reads.push(key); return Promise.resolve(key === ref ? 'stored-but-wrong' : null) },
    set: () => Promise.resolve(), remove: () => Promise.resolve(), available: () => true
  }
  const notified: string[] = []
  const broker = new SshAuthBroker(secrets, (sender, request) => {
    notified.push(request.kind)
    void broker.respond(sender, { id: request.id, cancelled: true })
  })
  const auth = await broker.open(profile, 1, { executable: electronBinary, appPath: process.cwd() })
  const transport = new OpenSshTransport({ ...profile, target: { kind: 'config', host: 'native-test', configFile: clientConfig } },
    { env: auth.env, onResolved: auth.resolve })
  try {
    const outcome = await transport.connect(AbortSignal.timeout(20_000)).then(() => 'connected', (error: Error) => error.message)
    expect(reads, `broker 必须去取存下的密码。连接结果:${outcome}`).toContain(ref)
    expect(notified, '存了密码就不该弹窗').toEqual([])
    // 同上:密码是错的,而且 sshd 也校验不了 —— 认证失败是预期结果
    expect(outcome).not.toBe('connected')
  } finally { await transport.close(); await auth.close(); await sshd.close() }
}, 60_000)

/** host key 变更必须被拒绝,且**不能**退化成一次可点"是"的确认。 */it.skipIf(!integration || process.platform === 'win32')('refuses to connect when the recorded host key no longer matches', async () => {
  const sshd = await isolatedSshd()
  const decoy = join(sshd.directory, 'decoy-key')
  await execute('/usr/bin/ssh-keygen', ['-q', '-t', 'ed25519', '-N', '', '-f', decoy])
  const knownHosts = join(sshd.directory, 'known_hosts_stale')
  await writeFile(knownHosts, `[127.0.0.1]:${sshd.port} ${(await readFile(`${decoy}.pub`, 'utf8')).trim()}\n`, { mode: 0o600 })
  const clientConfig = await sshd.config(`Host native-test\n HostName 127.0.0.1\n Port ${sshd.port}\n User ${sshd.username}\n`
    + ` IdentityFile ${sshd.clientKey}\n IdentitiesOnly yes\n IdentityAgent none\n UserKnownHostsFile ${knownHosts}\n StrictHostKeyChecking yes\n`)
  let accepted = 0
  const broker = new SshAuthBroker(nodeHost().secrets, (sender, request) => {
    accepted++
    void broker.respond(sender, { id: request.id, value: 'yes' })
  })
  const auth = await broker.open(profile, 1, { executable: electronBinary, appPath: process.cwd() })
  const transport = new OpenSshTransport({ ...profile, target: { kind: 'config', host: 'native-test', configFile: clientConfig } }, { env: auth.env })
  try {
    await expect(transport.connect(AbortSignal.timeout(20_000))).rejects.toThrow()
    expect(accepted, '主机密钥不匹配时不该给用户一个"确认"按钮').toBe(0)
  } finally { await transport.close(); await auth.close(); await sshd.close() }
}, 60_000)

/** Include 与 Match:原生 config 是权威,我们只是把 -F 传下去,不自己解析。 */
it.skipIf(!integration || process.platform === 'win32')('honours Include and Match directives in the native client config', async () => {
  const sshd = await isolatedSshd()
  const knownHosts = join(sshd.directory, 'known_hosts_include')
  await writeFile(knownHosts, `[127.0.0.1]:${sshd.port} ${sshd.hostKeyPublic}\n`, { mode: 0o600 })
  const included = join(sshd.directory, 'included_config')
  // 身份与端口只出现在被 Include 的文件里；用户名只出现在 Match 块里
  await writeFile(included, `Host native-test\n HostName 127.0.0.1\n Port ${sshd.port}\n`
    + ` IdentityFile ${sshd.clientKey}\n IdentitiesOnly yes\n IdentityAgent none\n`
    + ` UserKnownHostsFile ${knownHosts}\n StrictHostKeyChecking yes\n`, { mode: 0o600 })
  const clientConfig = await sshd.config(`Include ${included}\nMatch host native-test\n User ${sshd.username}\n`)
  const broker = new SshAuthBroker(nodeHost().secrets, (sender, request) => {
    void broker.respond(sender, { id: request.id, cancelled: true })
  })
  const auth = await broker.open(profile, 1, { executable: electronBinary, appPath: process.cwd() })
  const transport = new OpenSshTransport({ ...profile, target: { kind: 'config', host: 'native-test', configFile: clientConfig } }, { env: auth.env })
  try {
    await transport.connect(AbortSignal.timeout(20_000))
    const result = await transport.exec(POSIX_PROBE, AbortSignal.timeout(10_000))
    expect(result.code).toBe(0)
    expect(result.stdout.split('\0')[3], 'Match 块里的 User 必须生效').toBe(sshd.username)
  } finally { await transport.close(); await auth.close(); await sshd.close() }
}, 60_000)

/** ProxyJump:同一个 sshd 既当跳板也当目标,链路仍然是真实的 `ssh -W`。 */
it.skipIf(!integration || process.platform === 'win32')('connects through a ProxyJump hop', async () => {
  const sshd = await isolatedSshd()
  const knownHosts = join(sshd.directory, 'known_hosts_jump')
  await writeFile(knownHosts, `[127.0.0.1]:${sshd.port} ${sshd.hostKeyPublic}\n`, { mode: 0o600 })
  const common = ` HostName 127.0.0.1\n Port ${sshd.port}\n User ${sshd.username}\n IdentityFile ${sshd.clientKey}\n`
    + ` IdentitiesOnly yes\n IdentityAgent none\n UserKnownHostsFile ${knownHosts}\n StrictHostKeyChecking yes\n`
  const clientConfig = await sshd.config(`Host jump-host\n${common}\nHost native-test\n${common} ProxyJump jump-host\n`)
  const broker = new SshAuthBroker(nodeHost().secrets, (sender, request) => {
    void broker.respond(sender, { id: request.id, cancelled: true })
  })
  const auth = await broker.open(profile, 1, { executable: electronBinary, appPath: process.cwd() })
  const transport = new OpenSshTransport({ ...profile, target: { kind: 'config', host: 'native-test', configFile: clientConfig } }, { env: auth.env })
  try {
    await transport.connect(AbortSignal.timeout(25_000))
    const result = await transport.exec(POSIX_PROBE, AbortSignal.timeout(10_000))
    expect(result.code).toBe(0)
    expect(result.stdout.split('\0')[3]).toBe(sshd.username)
  } finally { await transport.close(); await auth.close(); await sshd.close() }
}, 75_000)




/**
 * ★ 真实 PTY:输入透传、SIGWINCH 随 resize 传到远端、Ctrl+C 的信号语义。
 *
 * 这三件事 mock 全都证明不了 —— mock 里 write/resize/kill 是我们自己写的 spy,
 * 而真正要验的是 OpenSSH 的 `-tt` 有没有把它们真的送到远端那个 pty 上。
 */
it.skipIf(!integration || process.platform === 'win32')('carries input, window size and Ctrl+C through a real remote PTY', async () => {
  const sshd = await isolatedSshd()
  const clientConfig = await readyConfig(sshd)
  const transport = new OpenSshTransport({ ...profile, target: { kind: 'config', host: 'native-test', configFile: clientConfig } })
  const marker = 'ncw-pty-31337'
  let terminal: import('node-pty').IPty | undefined
  try {
    await transport.connect(AbortSignal.timeout(20_000))
    const invocation = transport.terminalArgs(remoteTerminalCommand('linux', '/bin/sh', sshd.directory))
    const pty = await import('node-pty')
    terminal = pty.spawn(invocation.executable, invocation.args, { name: 'xterm-256color', cols: 80, rows: 24,
      env: { ...invocation.env, TERM: 'xterm-256color' } as Record<string, string> })
    let output = ''
    terminal.onData((data) => { output += data })

    // 1. 输入透传:写进本机 pty 的字节要在远端被 shell 执行
    terminal.write('echo NCW-INPUT-OK\n')
    await until(() => output.includes('NCW-INPUT-OK'), 15_000, '远端 shell 执行输入')

    // 2. resize:SIGWINCH 要穿过 ssh 到达远端 pty。stty 读的是**远端**的窗口大小
    output = ''
    terminal.resize(100, 40)
    terminal.write('stty size\n')
    await until(() => /\b40 100\b/.test(output), 15_000, `远端窗口大小变成 40x100(实际输出:${output.slice(-200)})`)

    // 3. Ctrl+C:前台进程收到 SIGINT 而死，shell 拿回控制权
    output = ''
    terminal.write(`sleep 31337 # ${marker}\n`)
    await until(() => remoteProcessAlive('sleep 31337'), 15_000, '远端 sleep 启动')
    terminal.write('\x03')
    await until(async () => !(await remoteProcessAlive('sleep 31337')), 15_000, 'Ctrl+C 杀掉远端前台进程')
    terminal.write('echo NCW-AFTER-INT\n')
    await until(() => output.includes('NCW-AFTER-INT'), 15_000, 'shell 在 Ctrl+C 后拿回控制权')
  } finally {
    terminal?.kill()
    await transport.close()
    await sshd.close()
    await execute('/usr/bin/pkill', ['-f', 'sleep 31337']).catch(() => undefined)
  }
}, 120_000)

/**
 * ★ 关掉本机这一侧之后，远端进程到底死不死 —— 实测结论，不是推测。
 *
 * 两条路径语义**相反**：
 *   `-tt`(终端)：远端有 pty，连接断开时 sshd 向会话发 SIGHUP → 远端进程随之退出。
 *   `-T` (远端 MCP stdio)：只 kill 本机 ssh **不会**让远端退出。实测本机 ssh 已消失、
 *        远端 `sleep` 30 秒后仍在，是真孤儿 —— 每断开一次就在服务器上留一个。
 *
 * 下面把两种行为都钉住。非 PTY 那条断言的是"仍然存活"，写的是现状而不是期望：
 * 它一旦变绿失败，说明有人改好了(或改坏了)这个语义，应该同步更新 ssh-support-matrix.md。
 */
it.skipIf(!integration || process.platform === 'win32')('kills the PTY session but orphans the non-PTY remote process when the local side goes away', async () => {
  const sshd = await isolatedSshd()
  const clientConfig = await readyConfig(sshd)
  const transport = new OpenSshTransport({ ...profile, target: { kind: 'config', host: 'native-test', configFile: clientConfig } })
  try {
    await transport.connect(AbortSignal.timeout(20_000))

    // -tt：远端有 pty，SIGHUP 会送达
    const invocation = transport.terminalArgs(remoteTerminalCommand('linux', '/bin/sh', sshd.directory))
    const pty = await import('node-pty')
    const terminal = pty.spawn(invocation.executable, invocation.args, { name: 'xterm-256color', cols: 80, rows: 24,
      env: { ...invocation.env, TERM: 'xterm-256color' } as Record<string, string> })
    terminal.write('sleep 31338\n')
    await until(() => remoteProcessAlive('sleep 31338'), 15_000, 'PTY 路径的远端进程启动')
    terminal.kill()
    await until(async () => !(await remoteProcessAlive('sleep 31338')), 20_000, 'PTY 路径:关掉本机后远端进程随之退出')

    // -T：没有 pty，没有 SIGHUP。一个不读 stdin 的远端进程会活下来
    const remote = transport.process('sleep 31339')
    remote.stderr.resume()
    remote.stdout.resume()
    await until(() => remoteProcessAlive('sleep 31339'), 15_000, '非 PTY 路径的远端进程启动')
    remote.kill()
    await new Promise((resolve) => setTimeout(resolve, 5000))
    expect(await remoteProcessAlive('sleep 31339'),
      '非 PTY 路径目前会留下孤儿。这条一旦失败说明语义变了，请同步 docs/ssh-support-matrix.md').toBe(true)
  } finally {
    await transport.close()
    await sshd.close()
    await execute('/usr/bin/pkill', ['-f', 'sleep 3133']).catch(() => undefined)
  }
}, 120_000)
