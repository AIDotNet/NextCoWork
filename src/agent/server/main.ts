/**
 * 独立设备服务的命令行入口(可被测试直接调用,不读 process.argv/exit)。
 *
 *   --data-dir <dir>       数据根(必填;与桌面旧数据同一目录即「原地接管」)
 *   --host <addr>          监听地址,默认 127.0.0.1;对外服务必须显式给出
 *   --port <n>             设备端点端口,默认 0(随机)
 *   --pairing-port <n>     配对端点端口,默认 0(随机)
 *   --advertise <host>     邀请里公布的主机名/IP(可重复);同时进入服务端证书 SAN
 *   --invite               启动后签发一个 5 分钟的配对邀请并打印
 *
 * ★ 没有任何默认的对外监听:不指定 `--host` 就只有本机能连。
 * ★ 邀请是敏感信息:只在显式 `--invite` 时输出,且只输出到 `io.out`。
 */
import { mkdirSync, promises as fs } from 'node:fs'
import { basename, join } from 'node:path'
import { headlessHost } from '../../main/host/headless'
import { runs } from '../../main/kernel/run-registry'
import { installWorkspaceTrash } from '../../main/services/workspace-files'
import { installHost, shutdownEnvironments, shutdownMcp, shutdownSessionTitles } from '../../main/runtime'
import { installAgentStartGuard, shutdownRuns } from '../../main/services/agent'
import { DeviceProtocolError } from '../../shared/device/errors'
import { DeviceEventHub } from './events'
import { registerCoreMethods } from './methods/core'
import { ensurePtyHelperExecutable } from './pty-helper'
import { startDeviceService, type DeviceService } from './service'

export interface CliIo {
  out(line: string): void
  err(line: string): void
}

export interface CliOptions {
  dataDir: string
  host: string
  port: number
  pairingPort: number
  advertise: string[]
  invite: boolean
}

export class UsageError extends Error {}

export function parseArgs(argv: readonly string[]): CliOptions {
  const options: CliOptions = { dataDir: '', host: '127.0.0.1', port: 0, pairingPort: 0, advertise: [], invite: false }
  const value = (index: number, flag: string): string => {
    const next = argv[index + 1]
    if (next === undefined || next.startsWith('--')) throw new UsageError(`${flag} needs a value`)
    return next
  }
  const port = (text: string, flag: string): number => {
    const parsed = Number(text)
    if (!/^[0-9]{1,5}$/.test(text) || parsed > 65535) throw new UsageError(`${flag} must be a port number`)
    return parsed
  }
  for (let i = 0; i < argv.length; i++) {
    const flag = argv[i]!
    switch (flag) {
      case '--data-dir': options.dataDir = value(i, flag); i++; break
      case '--host': options.host = value(i, flag); i++; break
      case '--port': options.port = port(value(i, flag), flag); i++; break
      case '--pairing-port': options.pairingPort = port(value(i, flag), flag); i++; break
      case '--advertise': options.advertise.push(value(i, flag)); i++; break
      case '--invite': options.invite = true; break
      default: throw new UsageError(`Unknown option: ${flag}`)
    }
  }
  if (options.dataDir === '') throw new UsageError('--data-dir is required')
  return options
}

/** 启动并返回运行中的服务;调用方负责在信号到来时 `stop()`。 */
export async function runDeviceService(options: CliOptions, io: CliIo): Promise<DeviceService> {
  /*
    终端是**可选**能力:从 tgz / `npm i -g` 装的 node-pty 缺执行位(见 pty-helper.ts),
    这里补一次。补不上也只警告,不拦服务启动 —— 其余能力照常,`diagnose` 会如实报 pty 失败。
  */
  const ptyProblem = ensurePtyHelperExecutable()
  if (ptyProblem !== null) io.err(`warning: terminals are unavailable: ${ptyProblem}`)
  const hub = new DeviceEventHub()
  let uninstall: (() => void) | null = null
  const advertiseHosts = options.advertise.length > 0 ? options.advertise : [options.host]
  const service = await startDeviceService({
    dataDir: options.dataDir, host: options.host, port: options.port, pairingPort: options.pairingPort,
    advertiseHosts, features: ['tasks', 'interactions'],
    register: (router, _identity, services) => {
      // 数据库此时已打开:凭证与路径都从它所在目录派生。
      mkdirSync(join(options.dataDir, 'tmp'), { recursive: true })
      installHost(headlessHost())
      // 无系统废纸篓:「删除」移进数据目录的 trash/,仍可恢复,绝不降级为永久删除。
      installWorkspaceTrash(async (path) => {
        const bin = join(options.dataDir, 'trash', `${Date.now()}-${Math.random().toString(36).slice(2, 10)}`)
        await fs.mkdir(bin, { recursive: true })
        await fs.rename(path, join(bin, basename(path)))
      })
      uninstall = registerCoreMethods(router, hub, services.leases, services.onRevoke)
    },
    onDrain: () => {
      // 新的 Agent 启动(含排队消息的自动续跑)都被拒绝;已在跑的 run 不受影响。
      installAgentStartGuard(() => { throw new DeviceProtocolError('busy', 'Service is draining for an upgrade') })
    },
    onStop: async () => {
      installAgentStartGuard(null)
      uninstall?.()
      installWorkspaceTrash(null)
      shutdownRuns()
      shutdownSessionTitles()
      await shutdownMcp()
      await shutdownEnvironments()
    }
  })
  const device = service.address()
  io.out(`device ${service.identity.deviceId} listening on ${device.host}:${device.port} (pairing ${service.pairingAddress().port})`)
  if (options.invite) io.out(service.createInvitation().text)
  return service
}

/**
 * 优雅升级:先排空(拒绝新配对与新任务),等所有 run 自然结束,再停止。
 * 控制端在排空期间仍可观看、审批、停止任务;超过 `maxWaitMs` 仍有任务则**不强杀**,
 * 返回 false 让调用方决定(任务由 Agent 自己的 shutdown 收尾,而不是被悄悄丢掉)。
 */
export async function drainAndStop(service: DeviceService, options: { maxWaitMs: number; pollMs?: number; isIdle?: () => boolean } ): Promise<boolean> {
  const isIdle = options.isIdle ?? (() => runs.activeRunIds().length === 0)
  const pollMs = options.pollMs ?? 500
  await service.drain()
  const deadline = Date.now() + options.maxWaitMs
  while (!isIdle()) {
    if (Date.now() >= deadline) return false
    await new Promise((resolve) => setTimeout(resolve, pollMs))
  }
  await service.stop()
  return true
}
