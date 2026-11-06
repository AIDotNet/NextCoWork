/**
 * `ncw-agent service ...` —— 常驻服务的安装辅助(可被测试直接调用)。
 *
 *   service plan  --user <u> --data-dir <d> [--bin <bin.mjs>] [--node <node>] [--platform linux|darwin|win32]
 *                 [--host <h>] [--port <n>] [--pairing-port <n>] [--advertise <h>]... [--name <n>]
 *       打印将要写的文件与随后要执行的命令。什么都不写、不执行。
 *   service apply 同上,另加 [--root <prefix>]
 *       只**写文件**(到 `<prefix>` 之下;默认真实系统路径,需要管理员权限,没有就报错并提示)。
 *       之后的 enable/start 命令仍由操作者自己执行 —— 这里从不 sudo、不启动、不启用服务。
 *   service uninstall-plan 同 plan 的参数,打印卸载命令。
 */
import { chmodSync, mkdirSync, writeFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { DeviceProtocolError } from '../../shared/device/errors'
import { planServiceInstall, type InstallPlan, type ServiceInstallOptions, type ServicePlatform } from './service-install'

export interface ServiceCliIo { out(line: string): void; err(line: string): void }
export class ServiceUsageError extends Error {}

const VALUE_FLAGS = new Set(['--user', '--data-dir', '--bin', '--node', '--platform', '--host', '--port', '--pairing-port', '--advertise', '--name', '--root'])

interface Parsed { command: 'plan' | 'apply' | 'uninstall-plan'; options: ServiceInstallOptions; root: string | null }

export function parseServiceArgs(argv: readonly string[], defaults: { platform: ServicePlatform; nodePath: string; binPath: string }): Parsed {
  const command = argv[0]
  if (command !== 'plan' && command !== 'apply' && command !== 'uninstall-plan') throw new ServiceUsageError('command must be plan, apply or uninstall-plan')
  const flags = new Map<string, string[]>()
  for (let i = 1; i < argv.length; i++) {
    const flag = argv[i]!
    if (!VALUE_FLAGS.has(flag)) throw new ServiceUsageError(`Unknown option: ${flag}`)
    const value = argv[i + 1]
    if (value === undefined || value.startsWith('--')) throw new ServiceUsageError(`${flag} needs a value`)
    flags.set(flag, [...(flags.get(flag) ?? []), value])
    i++
  }
  const one = (flag: string): string | undefined => flags.get(flag)?.at(-1)
  const user = one('--user')
  const dataDir = one('--data-dir')
  if (user === undefined) throw new ServiceUsageError('--user is required (the service never runs as root)')
  if (dataDir === undefined) throw new ServiceUsageError('--data-dir is required')
  const platform = (one('--platform') ?? defaults.platform) as ServicePlatform
  if (!['linux', 'darwin', 'win32'].includes(platform)) throw new ServiceUsageError('--platform must be linux, darwin or win32')
  const port = (flag: string): number | undefined => {
    const text = one(flag)
    if (text === undefined) return undefined
    if (!/^[0-9]{1,5}$/.test(text)) throw new ServiceUsageError(`${flag} must be a port number`)
    return Number(text)
  }
  const options: ServiceInstallOptions = {
    platform, user, dataDir, nodePath: one('--node') ?? defaults.nodePath, binPath: one('--bin') ?? defaults.binPath,
    ...(one('--host') !== undefined && { host: one('--host')! }),
    ...(port('--port') !== undefined && { port: port('--port')! }),
    ...(port('--pairing-port') !== undefined && { pairingPort: port('--pairing-port')! }),
    ...(flags.get('--advertise') !== undefined && { advertise: flags.get('--advertise')! }),
    ...(one('--name') !== undefined && { name: one('--name')! })
  }
  return { command, options, root: one('--root') ?? null }
}

const commandLine = (argv: string[]): string => argv.map((part) => (/[^\w@%+=:,./-]/.test(part) ? `'${part.replace(/'/g, `'\\''`)}'` : part)).join(' ')

function describe(plan: InstallPlan, io: ServiceCliIo): void {
  for (const file of plan.files) {
    io.out(`# file: ${file.path} (mode ${file.mode.toString(8)})`)
    io.out(file.content.trimEnd())
  }
  io.out(`# then, as an administrator${plan.requiresRoot ? '' : ' (not required)'}:`)
  for (const step of [...plan.install, ...plan.start]) io.out(commandLine(step))
  for (const note of plan.notes) io.out(`# note: ${note}`)
}

/** 把计划里的文件写到 `<root>` 之下(root 为空则写真实路径)。返回写入的路径。 */
export function applyPlanFiles(plan: InstallPlan, root: string | null, isAdmin: boolean): string[] {
  if (root === null && plan.requiresRoot && !isAdmin) {
    throw new DeviceProtocolError('forbidden', 'Writing the service files needs administrator privileges; re-run with elevated rights or use `service plan` and install them yourself')
  }
  const written: string[] = []
  for (const file of plan.files) {
    // Windows 盘符路径在 root 之下要去掉 `C:`;POSIX 路径直接拼接。
    const relative = file.path.replace(/^[A-Za-z]:/, '').replace(/^[\\/]+/, '')
    const target = root === null ? file.path : join(resolve(root), relative)
    mkdirSync(dirname(target), { recursive: true })
    writeFileSync(target, file.content, { mode: file.mode })
    chmodSync(target, file.mode)
    written.push(target)
  }
  return written
}

export function runServiceCli(argv: readonly string[], io: ServiceCliIo,
  env: { platform: ServicePlatform; nodePath: string; binPath: string; isAdmin: boolean }): void {
  const parsed = parseServiceArgs(argv, env)
  const plan = planServiceInstall(parsed.options)
  if (parsed.command === 'plan') { describe(plan, io); return }
  if (parsed.command === 'uninstall-plan') {
    for (const step of plan.uninstall) io.out(commandLine(step))
    return
  }
  const written = applyPlanFiles(plan, parsed.root, env.isAdmin)
  for (const path of written) io.out(`wrote ${path}`)
  io.out('Service files written. Nothing was enabled or started. Run, as an administrator:')
  for (const step of [...plan.install, ...plan.start]) io.out(commandLine(step))
}
