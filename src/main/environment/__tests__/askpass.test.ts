import { execFile, execFileSync } from 'node:child_process'
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { expect, it } from 'vitest'
import type { SshAuthRequest, SshConnectionProfile } from '../../../shared/domain/environment'
import { nodeHost } from '../../kernel/host'
import { requestAskpass, SshAuthBroker, windowsAskpassClient } from '../ssh/askpass'

const profile: SshConnectionProfile = { id: 'auth-test', kind: 'ssh', name: 'test', enabled: true, platform: 'auto',
  revision: 1, createdAt: 0, updatedAt: 0, target: { kind: 'config', host: 'test' } }

it('routes a challenge to its owner and rejects cross-window replies', async () => {
  let notify!: (request: SshAuthRequest) => void
  const received = new Promise<SshAuthRequest>((resolve) => { notify = resolve })
  const broker = new SshAuthBroker(nodeHost().secrets, (sender, request) => { expect(sender).toBe(42); notify(request) })
  const session = await broker.open(profile, 42, { executable: process.execPath })
  try {
    const answer = requestAskpass(session.env, 'Verification code:')
    const request = await received
    expect(request.canRemember).toBe(false)
    await expect(broker.respond(43, { id: request.id, value: 'wrong' })).rejects.toThrow('approval-expired')
    await broker.respond(42, { id: request.id, value: 'test-answer', remember: true })
    expect(await answer).toBe('test-answer')
    await expect(broker.respond(42, { id: request.id, value: 'replay' })).rejects.toThrow('approval-expired')
  } finally { await session.close() }
})

it('does not trust an unauthenticated helper connection', async () => {
  const broker = new SshAuthBroker(nodeHost().secrets, () => { throw new Error('Untrusted request reached UI') })
  const session = await broker.open(profile, 1, { executable: process.execPath })
  const directory = mkdtempSync(join(tmpdir(), 'ncw-wrong-'))
  const wrong = join(directory, 'token')
  writeFileSync(wrong, '0'.repeat(64), { mode: 0o600 })
  try { await expect(requestAskpass({ ...session.env, NCW_SSH_AUTH_SECRET: wrong }, 'Password:')).rejects.toThrow() }
  finally { rmSync(directory, { recursive: true, force: true }); await session.close() }
})

/**
 * ★ token 绝不能出现在 ssh 子进程的环境里。
 *
 * ssh 把整个环境交给 ProxyCommand / Match exec 跑的 `/bin/sh -c`,而用户 ssh_config 里
 * 一条 `SendEnv *` 会把它直接送到远端服务器。实测 `-o 'SendEnv=-*'` 清不掉配置里的
 * `SendEnv *`,OpenSSH 也没有 `SendEnv none` —— 所以只能不让它进环境。
 */
it('keeps the one-time auth token out of the SSH process environment', async () => {
  const broker = new SshAuthBroker(nodeHost().secrets, () => {})
  const session = await broker.open(profile, 1, { executable: process.execPath })
  try {
    const secret = session.env.NCW_SSH_AUTH_SECRET ?? ''
    const token = readFileSync(secret, 'utf8')
    expect(token.length).toBeGreaterThan(32)
    for (const [name, value] of Object.entries(session.env)) {
      expect(value, `${name} must not carry the token`).not.toContain(token)
    }
  } finally { await session.close() }
})

it('treats host key approval as yes/no and clears pending prompts on window close', async () => {
  let notify!: (request: SshAuthRequest) => void
  const received = new Promise<SshAuthRequest>((resolve) => { notify = resolve })
  const broker = new SshAuthBroker(nodeHost().secrets, (_sender, request) => notify(request))
  const session = await broker.open(profile, 1, { executable: process.execPath })
  try {
    const answer = requestAskpass({ ...session.env, SSH_ASKPASS_PROMPT: 'confirm' }, 'Trust this host?')
    const rejected = expect(answer).rejects.toThrow('cancelled')
    expect((await received).kind).toBe('host-key')
    broker.cancelWindow(1)
    await rejected
  } finally { await session.close() }
})

// ★ 远端 sshd 完全控制 askpass 的 argv[1]。打包形态下 SSH_ASKPASS 指向 Electron 本体,
//   Chromium 会先把它当开关解析,所以提示语绝不能直接落在 argv 的开关位置上。
it.skipIf(process.platform === 'win32')('never hands a server-controlled prompt to the client as a switch', async () => {
  const broker = new SshAuthBroker(nodeHost().secrets, () => {})
  const session = await broker.open(profile, 1, { executable: '/bin/echo' })
  try {
    const helper = session.env.SSH_ASKPASS ?? ''
    expect(helper, 'the helper must be a wrapper, not the client binary itself').not.toBe('/bin/echo')
    const hostile = '--remote-debugging-port=9222'
    const forwarded = execFileSync(helper, [hostile], { encoding: 'utf8' }).trim()
    expect(forwarded).toBe(`-- ${hostile}`)
  } finally { await session.close() }
})

/**
 * ★ Windows 上 ssh 跑的不是上面那条 TS 实现,而是 `windowsAskpassClient()` 生成的
 * 独立脚本(SSH_ASKPASS 只能是 exe,于是 helper 是「同一个 exe 的 node 形态」+ 这个脚本)。
 * 两份实现说同一套协议,而只有脚本那份是真正会被执行的 —— 所以这里按 ssh 的调法
 * (提示在 argv 末尾、答案从 stdout 读)把它真的跑起来。
 *
 * 断言里 `toBe('script-answer\n')` 的「整串相等」是重点:OpenSSH 只取 stdout 的第一行
 * (`buf[strcspn(buf, "\r\n")] = '\0'`),helper 多写一个字节,用户看到的就是
 * 「密码明明是对的却认证失败」。
 */
it('answers through the standalone helper script, writing nothing else to stdout', async () => {
  const broker = new SshAuthBroker(nodeHost().secrets, (sender, request) => {
    expect(request.prompt).toBe('Fixture password:')
    void broker.respond(sender, { id: request.id, value: 'script-answer' })
  })
  const session = await broker.open(profile, 7, { executable: process.execPath })
  const directory = mkdtempSync(join(tmpdir(), 'ncw-helper-'))
  const script = join(directory, 'askpass.js')
  writeFileSync(script, windowsAskpassClient(), { mode: 0o600 })
  try {
    const stdout = await new Promise<string>((resolve, reject) => {
      // `--` 与末尾的提示,正是 Windows 上 ssh 会拼出来的那条命令行
      execFile(process.execPath, [script, '--', 'Fixture password:'],
        { env: { ...process.env, ...session.env }, encoding: 'utf8', timeout: 20_000 },
        (error, out) => { if (error) reject(error); else resolve(out) })
    })
    expect(stdout).toBe('script-answer\n')
  } finally { rmSync(directory, { recursive: true, force: true }); await session.close() }
}, 30_000)

/** 取消要让 helper 以非 0 退出 —— 写一行空的会被 ssh 当成「用户输了个空密码」送上去。 */
it('fails the helper with a non-zero exit instead of sending an empty password', async () => {
  const broker = new SshAuthBroker(nodeHost().secrets, (sender, request) => {
    void broker.respond(sender, { id: request.id, cancelled: true })
  })
  const session = await broker.open(profile, 8, { executable: process.execPath })
  const directory = mkdtempSync(join(tmpdir(), 'ncw-helper-'))
  const script = join(directory, 'askpass.js')
  writeFileSync(script, windowsAskpassClient(), { mode: 0o600 })
  try {
    const result = await new Promise<{ code: number | null; stdout: string }>((resolve) => {
      const child = execFile(process.execPath, [script, '--', 'Fixture password:'],
        { env: { ...process.env, ...session.env }, encoding: 'utf8', timeout: 20_000 }, () => {})
      let stdout = ''
      child.stdout?.on('data', (chunk: string) => { stdout += chunk })
      child.once('close', (code) => resolve({ code, stdout }))
    })
    expect(result.code, '非 0 退出码 = ssh 放弃这次询问').not.toBe(0)
    expect(result.stdout).toBe('')
  } finally { rmSync(directory, { recursive: true, force: true }); await session.close() }
}, 30_000)