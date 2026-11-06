/**
 * 中转进程入口。SIGINT/SIGTERM 优雅停止;SIGHUP 重新读取注册表(新注册/撤销无需重启)。
 * 业务逻辑都在 `main.ts`;这里只碰 process。
 */
import { RelayUsageError, runRelayCli } from './main'

async function main(): Promise<void> {
  const io = { out: (line: string) => process.stdout.write(`${line}\n`), err: (line: string) => process.stderr.write(`${line}\n`) }
  let relay
  try {
    relay = await runRelayCli(process.argv.slice(2), io)
  } catch (error) {
    io.err(error instanceof RelayUsageError ? `usage: ${error.message}` : 'relay failed to start')
    process.exitCode = error instanceof RelayUsageError ? 64 : 1
    return
  }
  if (relay === null) return // register / revoke 已完成
  const stop = (): void => { relay.close().then(() => process.exit(0), () => process.exit(1)) }
  process.once('SIGINT', stop)
  process.once('SIGTERM', stop)
  if (process.platform !== 'win32') {
    process.on('SIGHUP', () => {
      try { relay.reload(); io.out('registrations reloaded') } catch { io.err('registrations reload failed; keeping the previous table') }
    })
  }
}

void main()
