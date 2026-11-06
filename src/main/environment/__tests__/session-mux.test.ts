/**
 * Windows 那条「一次认证,后面所有命令复用」的多路复用。
 *
 * 分两层测,两层各测各的:
 *
 * 1. **客户端 `SessionMux`**(分帧、channel 生命周期、banner 与噪声、exec、stdin、TCP 回环)
 *    —— 对端用一个 **Node fixture**,说同一套帧协议,走真实的 stdin/stdout 字节流。
 *    不 mock 帧本身:帧边界、banner 前的噪声、并发通道这些错,mock 原理上对得上、真跑对不上。
 *    这一层**不依赖本机装没装 Python**,默认 `npm test` 永远跑得到。
 *
 * 2. **生产那段 `MUX_SCRIPT` 真身** —— 它跑在远端、是 Python,所以只有本机找得到
 *    `python3` / `python` 时才跑;找不到就整组 skip(不算失败)。
 *
 * 需求:这两层原先合成一层、直接 `spawn('python', ...)`。
 * 不满足会怎样:本机只有 `python3` 没有 `python` → `spawn python ENOENT`,
 * 表现为「session-mux 三项失败」+ 一屏未捕获的 child_process 异常堆栈,而默认门禁的红
 * 从此分不清是真回归还是环境差异(CHANGELOG 里这条记了好几版)。
 */
import { spawn, spawnSync } from 'node:child_process'
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { createServer } from 'node:net'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { PassThrough } from 'node:stream'
import { afterAll, describe, expect, it } from 'vitest'
import { SessionMux, muxPythonLaunchCode } from '../ssh/session-mux'

/**
 * Node 版对端。**协议是被测契约,行为是替身** —— 它复刻 `MUX_SCRIPT` 的帧语义,
 * 但不去起 shell:这一层要证的是客户端把帧收发对了,不是 shell 的 echo 怎么工作。
 *
 * 命令形态与真实用例一一对应:
 *   `echo X`        → STDOUT + EXIT 0
 *   `exit N`        → EXIT N
 *   `__stdin_echo__` → 收到 STDIN_EOF 后把收到的字节原样 STDOUT + EXIT 0
 * OPEN_TCP 走**真** `net.connect`,因为 `openTcp` 的语义是「远端真的去连那个地址」。
 */
const MUX_PEER_FIXTURE = String.raw`
const net = require('node:net')

const OPEN_EXEC = 1, OPEN_SFTP = 2, STDIN = 3, STDIN_EOF = 4, CLOSE = 5, OPEN_TCP = 6
const STDOUT = 1, STDERR = 2, EXIT = 3, ERROR = 4
const MAX = 8 * 1024 * 1024

const out = process.stdout
const chans = new Map()
let buf = Buffer.alloc(0)

function int32(n) { const b = Buffer.alloc(4); b.writeInt32BE(n, 0); return b }

function send(sid, kind, payload) {
  payload = payload || Buffer.alloc(0)
  const body = Buffer.alloc(5 + payload.length)
  body.writeUInt32BE(sid, 0)
  body.writeUInt8(kind, 4)
  payload.copy(body, 5)
  if (body.length > MAX) return
  const frame = Buffer.alloc(4 + body.length)
  frame.writeUInt32BE(body.length, 0)
  body.copy(frame, 4)
  out.write(frame)
}

function handleExec(sid, cmd) {
  const echo = /^echo(?: (.*))?$/s.exec(cmd)
  if (echo) {
    send(sid, STDOUT, Buffer.from((echo[1] || '') + '\n', 'utf8'))
    send(sid, EXIT, int32(0))
    return
  }
  const exitCode = /^exit\s+(\d+)$/.exec(cmd)
  if (exitCode) { send(sid, EXIT, int32(Number(exitCode[1]))); return }
  if (cmd === '__stdin_echo__') { chans.set(sid, { echo: true, buf: Buffer.alloc(0) }); return }
  send(sid, STDERR, Buffer.from('unknown command: ' + cmd, 'utf8'))
  send(sid, EXIT, int32(127))
}

function handleTcp(sid, text) {
  const sep = text.indexOf('\0')
  if (sep < 1) { send(sid, ERROR, Buffer.from('bad address')); return }
  const host = text.slice(0, sep)
  const port = Number(text.slice(sep + 1))
  const socket = net.connect(port, host)
  chans.set(sid, { socket })
  socket.on('data', (chunk) => { send(sid, STDOUT, chunk) })
  socket.on('end', () => { if (chans.has(sid)) { chans.delete(sid); send(sid, EXIT, int32(0)) } })
  socket.on('close', () => { if (chans.has(sid)) { chans.delete(sid); send(sid, EXIT, int32(0)) } })
  socket.on('error', (err) => {
    if (chans.has(sid)) { chans.delete(sid); send(sid, ERROR, Buffer.from(String((err && err.message) || err))) }
  })
}

function handle(sid, kind, payload) {
  if (kind === OPEN_EXEC) { handleExec(sid, payload.toString('utf8')); return }
  if (kind === OPEN_SFTP) { send(sid, ERROR, Buffer.from('sftp-server not found')); return }
  if (kind === OPEN_TCP) { handleTcp(sid, payload.toString('utf8')); return }

  const ch = chans.get(sid)
  if (!ch) return
  if (kind === STDIN) {
    if (ch.socket) { ch.socket.write(payload); return }
    if (ch.echo) ch.buf = Buffer.concat([ch.buf, payload])
    return
  }
  if (kind === STDIN_EOF) {
    if (ch.socket) { ch.socket.end(); return }
    if (ch.echo) {
      const data = ch.buf
      chans.delete(sid)
      send(sid, STDOUT, data)
      send(sid, EXIT, int32(0))
    }
    return
  }
  if (kind === CLOSE) {
    chans.delete(sid)
    if (ch.socket) { ch.socket.destroy() }
    send(sid, EXIT, int32(-1))
  }
}

out.write(Buffer.from('\0NCW-MUX-1\n'))

process.stdin.on('data', (chunk) => {
  buf = Buffer.concat([buf, chunk])
  while (buf.length >= 4) {
    const len = buf.readUInt32BE(0)
    if (len < 5 || len > MAX) process.exit(1)
    if (buf.length < 4 + len) break
    const body = buf.subarray(4, 4 + len)
    buf = buf.subarray(4 + len)
    const sid = body.readUInt32BE(0)
    const kind = body.readUInt8(4)
    try { handle(sid, kind, body.subarray(5)) } catch (err) {
      send(sid, ERROR, Buffer.from(String((err && err.message) || err)))
    }
  }
})
process.stdin.on('end', () => { process.exit(0) })
`

const fixtureRoot = mkdtempSync(join(tmpdir(), 'ncw-mux-peer-'))
const fixturePath = join(fixtureRoot, 'mux-peer.cjs')
writeFileSync(fixturePath, MUX_PEER_FIXTURE)
afterAll(() => { rmSync(fixtureRoot, { recursive: true, force: true }) })

/** 起 Node 对端并接上客户端。★ 不在这里起 shell —— 那是下面 Python 集成组的事。 */
function startMux(): { mux: SessionMux; kill: () => void } {
  const child = spawn(process.execPath, [fixturePath], { windowsHide: true, stdio: 'pipe' })
  const mux = new SessionMux(child.stdin, child.stdout, () => {})
  child.stderr.on('data', () => {})
  return { mux, kill: () => { child.kill() } }
}

describe('SessionMux 客户端 · Node 对端', () => {
  it('runs several commands over one session without starting another process', async () => {
    const { mux, kill } = startMux()
    try {
      await mux.ready
      const first = await mux.exec('echo one', AbortSignal.timeout(10_000), 10_000)
      const second = await mux.exec('echo two', AbortSignal.timeout(10_000), 10_000)
      expect(first).toMatchObject({ code: 0, stdout: expect.stringContaining('one') })
      expect(second).toMatchObject({ code: 0, stdout: expect.stringContaining('two') })
      const failed = await mux.exec('exit 7', AbortSignal.timeout(10_000), 10_000)
      expect(failed.code).toBe(7)
    } finally { mux.close(); kill() }
  })

  it('keeps a long-lived process open and writes to its stdin', async () => {
    const { mux, kill } = startMux()
    try {
      await mux.ready
      const proc = mux.openProcess('__stdin_echo__')
      proc.stdin.write('hello-mux\n')
      proc.stdin.end()
      const chunks: Buffer[] = []
      proc.stdout.on('data', (chunk: Buffer) => { chunks.push(chunk) })
      const exit = await proc.exited
      expect(exit.code).toBe(0)
      expect(Buffer.concat(chunks).toString('utf8')).toContain('hello-mux')
    } finally { mux.close(); kill() }
  })

  it('forwards a tcp connection through the same session', async () => {
    const server = createServer((socket) => { socket.end('pong') })
    await new Promise<void>((resolve) => { server.listen(0, '127.0.0.1', resolve) })
    const address = server.address()
    if (address === null || typeof address === 'string') throw new Error('no port')
    const { mux, kill } = startMux()
    try {
      await mux.ready
      const socket = await mux.openTcp('127.0.0.1', address.port)
      const received = await new Promise<string>((resolve, reject) => {
        const chunks: Buffer[] = []
        socket.on('data', (chunk: Buffer) => { chunks.push(chunk) })
        socket.on('end', () => { resolve(Buffer.concat(chunks).toString('utf8')) })
        socket.on('error', reject)
      })
      expect(received).toBe('pong')
    } finally { mux.close(); kill(); await new Promise<void>((resolve) => { server.close(() => { resolve() }) }) }
  })

  it('fails ready instead of treating pre-banner noise as a live session', async () => {
    const input = new PassThrough()
    const output = new PassThrough()
    let died = false
    const mux = new SessionMux(input, output, () => { died = true })
    output.end(Buffer.alloc(5000, 0x61))
    await expect(mux.ready).rejects.toThrow(/banner/)
    expect(died, 'banner 之前断掉是没建起来,不是一次掉线').toBe(false)
  })
})

/**
 * 生产 `MUX_SCRIPT` 的真身。远端跑的就是它,值得在本机跑一遍 —— 但它要求 Python,
 * 而**装没装 Python 不该决定默认门禁红不红**,所以按可用性 gate。
 *
 * ★ 只试 `python3` / `python` 两个名字,和 `remoteMuxCommand()` 挑远端解释器的顺序一致:
 *   测试本机与远端用同一份选择逻辑,才谈得上「测过 = 远端也这样」。
 */
function resolvePython(): string | null {
  for (const candidate of ['python3', 'python']) {
    try {
      const probe = spawnSync(candidate, ['-c', 'print(1)'], { stdio: 'ignore', windowsHide: true })
      if (probe.status === 0) return candidate
    } catch { /* 没装就换下一个 */ }
  }
  return null
}
const PYTHON = resolvePython()

describe.skipIf(PYTHON === null)('MUX_SCRIPT · 真 Python 对端', () => {
  function startRealMux(): { mux: SessionMux; kill: () => void } {
    const child = spawn(PYTHON as string, ['-S', '-u', '-c', muxPythonLaunchCode()], { windowsHide: true, stdio: 'pipe' })
    const mux = new SessionMux(child.stdin, child.stdout, () => {})
    child.stderr.on('data', () => {})
    return { mux, kill: () => { child.kill() } }
  }

  it('runs several commands over one session without starting another process', async () => {
    const { mux, kill } = startRealMux()
    try {
      await mux.ready
      const first = await mux.exec('echo one', AbortSignal.timeout(10_000), 10_000)
      expect(first).toMatchObject({ code: 0, stdout: expect.stringContaining('one') })
      const failed = await mux.exec('exit 7', AbortSignal.timeout(10_000), 10_000)
      expect(failed.code).toBe(7)
    } finally { mux.close(); kill() }
  })

  it('keeps a long-lived process open and writes to its stdin', async () => {
    const { mux, kill } = startRealMux()
    try {
      await mux.ready
      const proc = mux.openProcess(`${PYTHON as string} -c "import sys; print(sys.stdin.readline().strip())"`)
      proc.stdin.write('hello-mux\n')
      proc.stdin.end()
      const chunks: Buffer[] = []
      proc.stdout.on('data', (chunk: Buffer) => { chunks.push(chunk) })
      const exit = await proc.exited
      expect(exit.code).toBe(0)
      expect(Buffer.concat(chunks).toString('utf8')).toContain('hello-mux')
    } finally { mux.close(); kill() }
  })
})
