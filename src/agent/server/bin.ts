/**
 * 进程入口:解析参数、启动服务、响应信号。
 *
 *   SIGINT / SIGTERM  立即优雅停止(Agent 任务由各自的 shutdown 收尾)。
 *   SIGUSR2           排空后停止:拒绝新配对与新任务,等在跑的任务自然结束再退出(升级用)。
 *                     最长等 `NCW_DRAIN_MAX_WAIT_MS`(默认 1 小时);超时不强杀,继续运行并报告。
 *
 * 业务逻辑都在 `main.ts`;这里只碰 process。
 */
import { fileURLToPath } from 'node:url'
import { DiagnoseUsageError, runDiagnoseCli } from './diagnose-cli'
import { drainAndStop, parseArgs, runDeviceService, UsageError } from './main'
import { runServiceCli, ServiceUsageError } from './service-cli'
import { runTakeoverCli, TakeoverUsageError } from './takeover-cli'

async function main(): Promise<void> {
  const io = { out: (line: string) => process.stdout.write(`${line}\n`), err: (line: string) => process.stderr.write(`${line}\n`) }
  // `diagnose [--json] [--data-dir d]`:真实探测这台机器能跑什么,不启动服务。
  if (process.argv[2] === 'diagnose') {
    try {
      process.exitCode = await runDiagnoseCli(process.argv.slice(3), io)
    } catch (error) {
      io.err(error instanceof DiagnoseUsageError ? `usage: ${error.message}` : error instanceof Error ? error.message : 'diagnose failed')
      process.exitCode = error instanceof DiagnoseUsageError ? 64 : 1
    }
    return
  }
  // `service plan|apply|uninstall-plan`:安装辅助,不启动服务。
  if (process.argv[2] === 'service') {
    try {
      runServiceCli(process.argv.slice(3), io, {
        platform: process.platform as 'linux' | 'darwin' | 'win32', nodePath: process.execPath,
        binPath: fileURLToPath(import.meta.url), isAdmin: typeof process.getuid === 'function' && process.getuid() === 0
      })
    } catch (error) {
      io.err(error instanceof ServiceUsageError ? `usage: ${error.message}` : error instanceof Error ? error.message : 'service command failed')
      process.exitCode = error instanceof ServiceUsageError ? 64 : 1
    }
    return
  }
  // `takeover begin|commit|rollback|status`:把桌面数据根原地交给服务,不启动服务。
  if (process.argv[2] === 'takeover') {
    try {
      runTakeoverCli(process.argv.slice(3), io)
    } catch (error) {
      io.err(error instanceof TakeoverUsageError ? `usage: ${error.message}` : error instanceof Error ? error.message : 'takeover command failed')
      process.exitCode = error instanceof TakeoverUsageError ? 64 : 1
    }
    return
  }
  let service
  try {
    service = await runDeviceService(parseArgs(process.argv.slice(2)), io)
  } catch (error) {
    io.err(error instanceof UsageError ? `usage: ${error.message}` : `device service failed to start${error instanceof Error ? `: ${error.message}` : ''}`)
    process.exitCode = error instanceof UsageError ? 64 : 1
    return
  }
  let stopping = false
  const stop = (): void => {
    if (stopping) return
    stopping = true
    service.stop().then(() => process.exit(0), () => process.exit(1))
  }
  let draining = false
  const drain = (): void => {
    if (draining || stopping) return
    draining = true
    const maxWaitMs = Number(process.env.NCW_DRAIN_MAX_WAIT_MS ?? 3_600_000)
    drainAndStop(service, { maxWaitMs: Number.isFinite(maxWaitMs) && maxWaitMs > 0 ? maxWaitMs : 3_600_000 }).then((stopped) => {
      if (stopped) process.exit(0)
      io.err('drain timed out: tasks are still running; staying up (send SIGTERM to stop now)')
      draining = false
    }, () => process.exit(1))
  }
  process.on('SIGINT', stop)
  process.on('SIGTERM', stop)
  if (process.platform !== 'win32') process.on('SIGUSR2', drain)
}

void main()
