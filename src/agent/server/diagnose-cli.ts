/**
 * `ncw-agent diagnose [--json] [--data-dir <d>]` —— 诊断 CLI:这台机器能不能跑设备服务,哪些能力可用。
 *
 * 只报告**真实探测**的结果(不是读版本号推断):node:sqlite 真的开一个内存库、node-pty 真的拉起一个 shell
 * 并读到输出、git 真的执行 `--version`。浏览器与 Office 组件尚未随服务提供,如实报告为 `absent`,
 * 不假装「已安装」。
 *
 * 退出码:所有 `required` 检查通过 = 0;任何一项失败 = 1。`optional` 失败只影响对应能力(如终端),不影响退出码。
 * 输出不含密钥、路径以外的数据目录内容。
 */
import { execFile } from 'node:child_process'
import { accessSync, constants, mkdirSync, rmSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { setTimeout as sleep } from 'node:timers/promises'
import { ensurePtyHelperExecutable } from './pty-helper'

export class DiagnoseUsageError extends Error {}
export interface DiagnoseIo { out(line: string): void; err(line: string): void }

export type CheckStatus = 'ok' | 'fail' | 'absent'
export interface CheckResult {
  id: string
  /** required 失败 = 服务不可用;optional 失败 = 只是缺少某项能力。 */
  level: 'required' | 'optional'
  status: CheckStatus
  detail: string
}

export interface Probes {
  nodeVersion(): string
  sqlite(): Promise<string>
  pty(): Promise<string>
  git(): Promise<string>
  dataDir(dir: string): Promise<string>
}

const MIN_NODE_MAJOR = 24

async function attempt(id: string, level: CheckResult['level'], work: () => Promise<string>): Promise<CheckResult> {
  try { return { id, level, status: 'ok', detail: await work() } } catch (error) {
    return { id, level, status: 'fail', detail: error instanceof Error ? error.message : 'check failed' }
  }
}

export const realProbes: Probes = {
  nodeVersion: () => process.versions.node,
  async sqlite() {
    const { DatabaseSync } = await import('node:sqlite')
    const database = new DatabaseSync(':memory:')
    try {
      database.exec('PRAGMA synchronous = FULL')
      const row = database.prepare('SELECT sqlite_version() AS v').get() as { v?: string } | undefined
      if (row?.v === undefined) throw new Error('node:sqlite returned no version')
      return `sqlite ${row.v}`
    } finally { database.close() }
  },
  async pty() {
    /*
      先补 helper 的执行位(npm ≥ 12 不跑依赖的 install script,装出来的 spawn-helper 是 644,
      真正的失败会是原生层一句 `posix_spawnp failed`)。补不上时把原因带进详情:那才是要改的东西。
    */
    const helperProblem = ensurePtyHelperExecutable()
    const pty = await import('node-pty')
    const shell = process.platform === 'win32' ? 'cmd.exe' : '/bin/sh'
    const args = process.platform === 'win32' ? ['/c', 'echo ncw-diagnose'] : ['-c', 'echo ncw-diagnose']
    let child: import('node-pty').IPty
    try {
      child = pty.spawn(shell, args, { name: 'xterm-256color', cols: 80, rows: 24, cwd: process.cwd(), env: process.env as Record<string, string> })
    } catch (error) {
      // 可操作的那一句放前面:原生层的 `posix_spawnp failed` 本身指不出要改什么。
      const failure = error instanceof Error ? error.message : 'the pseudo-terminal could not be started'
      throw new Error(helperProblem === null ? failure : `${helperProblem}; ${failure}`, { cause: error })
    }
    let output = ''
    child.onData((chunk) => { output += chunk })
    const exited = new Promise<void>((resolve) => child.onExit(() => resolve()))
    const outcome = await Promise.race([exited.then(() => 'exit' as const), sleep(5000).then(() => 'timeout' as const)])
    if (outcome === 'timeout') { try { child.kill() } catch { /* 已退出 */ } throw new Error('the pseudo-terminal did not finish within 5 s') }
    if (!output.includes('ncw-diagnose')) throw new Error('the pseudo-terminal produced no output')
    return `node-pty can run ${shell}`
  },
  git: () => new Promise<string>((resolve, reject) => {
    execFile('git', ['--version'], { timeout: 5000 }, (error, stdout) => error === null ? resolve(stdout.trim()) : reject(new Error('git is not available on PATH')))
  }),
  async dataDir(dir) {
    mkdirSync(dir, { recursive: true })
    accessSync(dir, constants.R_OK | constants.W_OK)
    const probe = join(dir, `.diagnose-${process.pid}.tmp`)
    writeFileSync(probe, 'ok')
    rmSync(probe, { force: true })
    return 'the data directory is readable and writable'
  }
}

export async function runChecks(dataDir: string | undefined, probes: Probes = realProbes): Promise<CheckResult[]> {
  const major = Number(probes.nodeVersion().split('.')[0])
  const results: CheckResult[] = [
    Number.isFinite(major) && major >= MIN_NODE_MAJOR
      ? { id: 'node', level: 'required', status: 'ok', detail: `Node ${probes.nodeVersion()}` }
      : { id: 'node', level: 'required', status: 'fail', detail: `Node ${probes.nodeVersion()} is too old; ${MIN_NODE_MAJOR} or newer is required` },
    await attempt('sqlite', 'required', probes.sqlite),
    await attempt('pty', 'optional', probes.pty),
    await attempt('git', 'optional', probes.git)
  ]
  if (dataDir !== undefined) results.push(await attempt('data-dir', 'required', () => probes.dataDir(dataDir)))
  // 这些组件还没有随服务提供(见计划 M6):如实报告缺席,不算失败。
  results.push({ id: 'browser', level: 'optional', status: 'absent', detail: 'the managed browser component is not installed' })
  results.push({ id: 'office', level: 'optional', status: 'absent', detail: 'the Office engine component is not installed' })
  return results
}

/** 返回退出码。 */
export async function runDiagnoseCli(argv: readonly string[], io: DiagnoseIo, probes: Probes = realProbes): Promise<number> {
  let json = false
  let dataDir: string | undefined
  for (let i = 0; i < argv.length; i++) {
    const flag = argv[i]!
    if (flag === '--json') { json = true; continue }
    if (flag !== '--data-dir') throw new DiagnoseUsageError(`Unknown option: ${flag}`)
    dataDir = argv[++i]
    if (dataDir === undefined || dataDir.startsWith('--')) throw new DiagnoseUsageError('--data-dir needs a value')
  }
  const results = await runChecks(dataDir, probes)
  const failed = results.some((result) => result.level === 'required' && result.status === 'fail')
  if (json) io.out(JSON.stringify({ ok: !failed, checks: results }, null, 2))
  else {
    for (const result of results) io.out(`${result.status.padEnd(6)} ${result.id.padEnd(9)} ${result.level === 'optional' ? '(optional) ' : ''}${result.detail}`)
    io.out(failed ? 'The device service cannot run on this machine.' : 'The device service can run on this machine.')
  }
  return failed ? 1 : 0
}
