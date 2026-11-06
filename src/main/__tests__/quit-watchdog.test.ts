/**
 * 需求：退出兜底必须在主进程 JS 不再执行时仍能结束进程；父进程消失后不能误杀
 * 复用的 PID。既测独立子进程的真实强杀，也测父子关系与定时器的边界。
 */
import { runInNewContext } from 'node:vm'
import { afterEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({ spawn: vi.fn(), unref: vi.fn(), on: vi.fn() }))
vi.mock('node:child_process', async (importOriginal) => {
  const original = await importOriginal<typeof import('node:child_process')>()
  return { ...original, spawn: mocks.spawn }
})
import { armQuitWatchdog } from '../quit-watchdog'

function watchdogSource(): string {
  mocks.spawn.mockReturnValue({ unref: mocks.unref, on: mocks.on })
  armQuitWatchdog()
  const args = mocks.spawn.mock.calls.at(-1)?.[1] as string[]
  return args[1]!
}

afterEach(() => {
  vi.useRealTimers()
  vi.clearAllMocks()
})

describe.skipIf(process.platform !== 'darwin')('quit watchdog', () => {
  it('uses an independent detached runtime without application credentials', () => {
    watchdogSource()
    expect(mocks.spawn).toHaveBeenCalledWith(process.execPath, expect.arrayContaining([String(process.pid), '6000']), {
      env: { ELECTRON_RUN_AS_NODE: '1' }, stdio: 'ignore', detached: true
    })
    expect(mocks.unref).toHaveBeenCalledOnce()
  })

  it('does not kill a reused PID after its parent has exited', () => {
    vi.useFakeTimers()
    const runtime = { argv: ['node', '123', '6000'], ppid: 123, kill: vi.fn(), exit: vi.fn() }
    runInNewContext(watchdogSource(), { process: runtime, setTimeout, clearTimeout, setInterval })
    runtime.ppid = 1
    vi.advanceTimersByTime(6000)
    expect(runtime.kill).not.toHaveBeenCalled()
    expect(runtime.exit).toHaveBeenCalledWith(0)
  })

  it('kills a parent whose JavaScript event loop cannot run', async () => {
    const { spawn } = await vi.importActual<typeof import('node:child_process')>('node:child_process')
    // 需求：父进程死循环使 JS 超时完全无效，只有独立 watchdog 可以完成这条断言。
    const parentSource = `
      const { spawn } = require('node:child_process')
      spawn(process.execPath, ['-e', ${JSON.stringify(watchdogSource())}, String(process.pid), '500'], {
        stdio: 'ignore', detached: true
      }).unref()
      while (true) {}
    `
    const parent = spawn(process.execPath, ['-e', parentSource], { stdio: 'ignore' })
    try {
      const signal = await new Promise<NodeJS.Signals | null>((resolve, reject) => {
        parent.once('error', reject)
        parent.once('exit', (_code, exitSignal) => resolve(exitSignal))
      })
      expect(signal).toBe('SIGKILL')
    } finally {
      parent.kill('SIGKILL')
    }
  }, 5000)
})
