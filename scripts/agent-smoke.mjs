#!/usr/bin/env node
/**
 * 冒烟:把构建产物当**真实进程**跑一遍,不是只看单元测试。
 *
 *  agent:启动 → 打印监听行与邀请 → 数据根出现身份/库/PKI/锁 → SIGUSR2 排空后自行退出 → 锁被清理
 *  relay:register 打印凭据且注册表只存摘要、权限 0600 → 明文 serve 被拒 → behind-tls 可启动 →
 *         SIGHUP 重载 → SIGTERM 退出
 *
 * 前置:`npm run build:agent && npm run build:relay`。任一断言失败即非零退出。
 * 仅 POSIX(依赖信号);Windows 上直接跳过并说明。
 */
import { spawn, spawnSync } from 'node:child_process'
import { existsSync, mkdtempSync, readFileSync, rmSync, statSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { setTimeout } from 'node:timers'
import { fileURLToPath } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const agent = join(root, 'out/agent/bin.mjs')
const relay = join(root, 'out/relay/bin.mjs')

function fail(message) { console.error(`smoke failed: ${message}`); process.exit(1) }
function check(condition, message) { if (!condition) fail(message) }

function start(entry, args) {
  const child = spawn(process.execPath, [entry, ...args], { stdio: ['ignore', 'pipe', 'pipe'] })
  const state = { out: '', err: '', exited: null }
  child.stdout.on('data', (chunk) => { state.out += chunk })
  child.stderr.on('data', (chunk) => { state.err += chunk })
  state.done = new Promise((resolveExit) => child.on('exit', (code, signal) => { state.exited = { code, signal }; resolveExit(state.exited) }))
  state.child = child
  return state
}
async function until(predicate, label, ms = 15_000) {
  const end = Date.now() + ms
  while (!predicate()) {
    if (Date.now() > end) fail(`timed out waiting for ${label}`)
    await new Promise((r) => setTimeout(r, 50))
  }
}
const exit = (state, ms = 15_000) => Promise.race([state.done, new Promise((r) => setTimeout(() => r(null), ms))])

if (process.platform === 'win32') { console.log('smoke skipped on Windows (signal based)'); process.exit(0) }
check(existsSync(agent), 'out/agent/bin.mjs is missing; run `npm run build:agent`')
check(existsSync(relay), 'out/relay/bin.mjs is missing; run `npm run build:relay`')
const dir = mkdtempSync(join(tmpdir(), 'ncw-smoke-'))

try {
  // ── agent ──
  const data = join(dir, 'data')
  const service = start(agent, ['--data-dir', data, '--invite'])
  await until(() => /listening on 127\.0\.0\.1:\d+/.test(service.out), 'the agent to listen')
  /*
    邀请是监听行之后的**下一次** write(main.ts 先报监听、再签发邀请),两行不一定在同一个
    chunk 里到 —— 监听行一到就断言邀请,等于赌这两次 write 被合成一次到达。macOS runner 上
    就是这么红过一次(「agent did not print an invitation」)。等它到,而不是赌它到。
  */
  await until(() => service.out.includes('ncwpair1.'), 'the agent to print an invitation')
  for (const entry of ['device-identity.json', 'nextcowork.db', 'pki', 'service.lock']) check(existsSync(join(data, entry)), `agent data root lacks ${entry}`)
  const second = spawnSync(process.execPath, [agent, '--data-dir', data], { encoding: 'utf8', timeout: 20_000 })
  check(second.status !== 0, 'a second agent on the same data root must be refused')
  service.child.kill('SIGUSR2') // 排空后停止;没有任务,应当很快自行退出
  const drained = await exit(service)
  check(drained !== null && drained.code === 0, `agent did not exit cleanly after SIGUSR2 (${JSON.stringify(drained)})`)
  check(!existsSync(join(data, 'service.lock')), 'agent left its lock file behind')
  const plan = spawnSync(process.execPath, [agent, 'service', 'plan', '--user', 'ncw', '--data-dir', '/var/lib/ncw', '--platform', 'linux'], { encoding: 'utf8' })
  check(plan.status === 0 && plan.stdout.includes('User=ncw'), 'service plan failed')
  check(spawnSync(process.execPath, [agent, 'service', 'plan', '--user', 'root', '--data-dir', '/d'], { encoding: 'utf8' }).status !== 0, 'service plan accepted root')
  // 诊断 CLI:真实探测 sqlite / 数据目录,JSON 可解析;node-pty 与 git 是可选项,不在冒烟里强求。
  const diagnosed = spawnSync(process.execPath, [agent, 'diagnose', '--json', '--data-dir', join(dir, 'diagnose-data')], { encoding: 'utf8' })
  check(diagnosed.status === 0, `diagnose failed: ${diagnosed.stdout}${diagnosed.stderr}`)
  const report = JSON.parse(diagnosed.stdout)
  check(report.ok === true && report.checks.some((c) => c.id === 'sqlite' && c.status === 'ok'), 'diagnose did not report a working sqlite')
  check(report.checks.filter((c) => c.status === 'absent').map((c) => c.id).join() === 'browser,office', 'diagnose must report the browser and Office components as absent')
  check(spawnSync(process.execPath, [agent, 'diagnose', '--bogus'], { encoding: 'utf8' }).status === 64, 'diagnose accepted an unknown option')

  // ── relay ──
  const registrations = join(dir, 'relay.json')
  const registered = spawnSync(process.execPath, [relay, 'register', '--registrations', registrations, '--device', 'dev-smoke'], { encoding: 'utf8' })
  const token = registered.stdout.trim()
  check(registered.status === 0 && token.length >= 43, 'relay register failed')
  check(!readFileSync(registrations, 'utf8').includes(token), 'relay stored the credential in clear text')
  check((statSync(registrations).mode & 0o077) === 0, 'relay registrations file is not private')
  check(spawnSync(process.execPath, [relay, 'serve', '--registrations', registrations], { encoding: 'utf8' }).status !== 0, 'relay must refuse plaintext serving')
  const server = start(relay, ['serve', '--registrations', registrations, '--behind-tls', '--port', '0'])
  await until(() => /relay listening on/.test(server.out), 'the relay to listen')
  server.child.kill('SIGHUP')
  await until(() => server.out.includes('registrations reloaded'), 'the relay to reload')
  server.child.kill('SIGTERM')
  const stopped = await exit(server)
  check(stopped !== null && stopped.code === 0, 'relay did not exit cleanly after SIGTERM')
  console.log('smoke ok')
} finally {
  rmSync(dir, { recursive: true, force: true })
}
