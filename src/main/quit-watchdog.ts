/**
 * 需求：macOS 云端占位文件的 pread 可以长期阻塞，Electron 在 Node CleanupHandles
 * 等它时已经无法执行主进程的 JS 定时器。最终退出必须由独立进程提供时限。
 * 只在 will-quit（窗口确认与应用收尾之后）启动，不参与文档确认或取消退出。
 * 不继承应用凭据；通过父子关系识别目标，父进程已退出时绝不杀复用同一 PID 的进程。
 */
import { spawn } from 'node:child_process'

const EXIT_DEADLINE_MS = 6000

// 需求：兜底不能依赖正在退出的 Node 环境；父进程消失后也必须自行结束，避免残留 helper。
const WATCHDOG_SOURCE = `
const parent = Number(process.argv[1])
const deadline = Number(process.argv[2])
const timer = setTimeout(() => {
  if (process.ppid === parent) {
    try { process.kill(parent, 'SIGKILL') } catch {}
  }
  process.exit(0)
}, deadline)
setInterval(() => {
  if (process.ppid !== parent) {
    clearTimeout(timer)
    process.exit(0)
  }
}, 250)
`

export function armQuitWatchdog(): void {
  // 需求：本次证据是 macOS 的 APFS 清理阻塞，其他平台的退出行为保持原样。
  if (process.platform !== 'darwin') return
  try {
    const child = spawn(process.execPath, ['-e', WATCHDOG_SOURCE, String(process.pid), String(EXIT_DEADLINE_MS)], {
      env: { ELECTRON_RUN_AS_NODE: '1' },
      stdio: 'ignore',
      detached: true
    })
    child.on('error', (error: Error) => {
      console.error('[quit] 无法启动退出兜底进程:', error)
    })
    child.unref()
  } catch (error) {
    console.error('[quit] 无法启动退出兜底进程:', error)
  }
}
