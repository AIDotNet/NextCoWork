import { PassThrough } from 'node:stream'
import { describe, expect, it, vi } from 'vitest'
import { BACKGROUND_SHELL_LIMITS } from '../../shared/domain/shell'
import { AgentShells } from '../agent-shells'
import type { EnvironmentProcess } from '../environment/contract'

/**
 * 后台 shell 注册表本身的测试 —— 不碰 `openProcess`,进程是假的。
 *
 * 这里钉的全是「坏了也不报错」的那几条:读过即清空(否则模型每轮重读同一段日志
 * 并以为服务又崩了一次)、丢的是最早的一段且要说出来、杀过之后状态不被退出回调
 * 改回 `exited`、还在跑的永远不被淘汰、退出时一个不剩。
 */

function fakeProcess(): EnvironmentProcess & {
  finish: (code: number | null) => void
  /** 假进程自己往管道里写 —— `EnvironmentProcess.stdout` 对外只是 `Readable` */
  emit: (stream: 'stdout' | 'stderr', text: string) => void
} {
  const stdout = new PassThrough()
  const stderr = new PassThrough()
  let settle: (value: { code: number | null }) => void = () => {}
  const exited = new Promise<{ code: number | null }>((resolve) => {
    settle = resolve
  })
  return {
    stdin: new PassThrough(),
    stdout,
    stderr,
    exited,
    kill: vi.fn(),
    finish: (code) => settle({ code }),
    emit: (stream, text) => { (stream === 'stdout' ? stdout : stderr).write(text) }
  }
}

function adopt(shells: AgentShells, over: Partial<{ command: string; runId: string; callId: string }> = {}) {
  const process = fakeProcess()
  const info = shells.adopt({
    command: over.command ?? 'npm run dev',
    cwd: '/w',
    runId: over.runId ?? 'run_1',
    callId: over.callId ?? 'call_1',
    workspaceId: 'ws',
    startedAt: 1,
    process,
    release: vi.fn()
  })
  return { info, process }
}

/** 流是异步的:写进去之后要让事件循环转一圈,监听器才会收到。 */
const flush = (): Promise<void> => new Promise((resolve) => setImmediate(resolve))

describe('AgentShells · 前台停止句柄', () => {
  it('按 runId + callId 停到那一条,并且只停那一条', () => {
    const shells = new AgentShells()
    const first = vi.fn()
    const second = vi.fn()
    shells.hold({ runId: 'run_1', callId: 'call_1', command: 'a' }, first)
    shells.hold({ runId: 'run_1', callId: 'call_2', command: 'b' }, second)

    expect(shells.stopCall('run_1', 'call_1')).toBe(true)
    expect(first).toHaveBeenCalledOnce()
    expect(second).not.toHaveBeenCalled()
  })

  it('★ 注销之后再点停止是 false,不是停了一条别的命令', () => {
    const shells = new AgentShells()
    const stop = vi.fn()
    const release = shells.hold({ runId: 'run_1', callId: 'call_1', command: 'a' }, stop)
    release()

    expect(shells.stopCall('run_1', 'call_1')).toBe(false)
    expect(stop).not.toHaveBeenCalled()
  })

  it('同一个 callId 在另一个 run 下互不相干 —— 键必须带 runId', () => {
    const shells = new AgentShells()
    const stop = vi.fn()
    shells.hold({ runId: 'run_1', callId: 'call_1', command: 'a' }, stop)
    expect(shells.stopCall('run_2', 'call_1')).toBe(false)
    expect(stop).not.toHaveBeenCalled()
  })
})

describe('AgentShells · 前台转后台', () => {
  it('登记了 detach 的才转得了,转的是那一条', () => {
    const shells = new AgentShells()
    const detach = vi.fn()
    shells.hold({ runId: 'run_1', callId: 'call_1', command: 'a' }, vi.fn(), detach)
    shells.hold({ runId: 'run_1', callId: 'call_2', command: 'b' }, vi.fn())

    expect(shells.detachCall('run_1', 'call_2')).toBe(false)
    expect(shells.detachCall('run_1', 'call_1')).toBe(true)
    expect(detach).toHaveBeenCalledOnce()
  })

  it('★ 后台名额满了当场拒绝,不先转走再收编失败', () => {
    const shells = new AgentShells()
    for (let i = 0; i < BACKGROUND_SHELL_LIMITS.MAX_RUNNING; i++) adopt(shells, { callId: `bg_${String(i)}` })
    const detach = vi.fn()
    shells.hold({ runId: 'run_1', callId: 'call_1', command: 'a' }, vi.fn(), detach)

    expect(shells.detachCall('run_1', 'call_1')).toBe(false)
    expect(detach).not.toHaveBeenCalled()
  })

  it('注销之后再点转后台是 false', () => {
    const shells = new AgentShells()
    const detach = vi.fn()
    shells.hold({ runId: 'run_1', callId: 'call_1', command: 'a' }, vi.fn(), detach)()
    expect(shells.detachCall('run_1', 'call_1')).toBe(false)
    expect(detach).not.toHaveBeenCalled()
  })
})

describe('AgentShells · 后台读取', () => {
  it('★ 读过即清空:第二次读只拿到这之后的新输出', async () => {
    const shells = new AgentShells()
    const { info, process } = adopt(shells)
    process.emit('stdout', 'first\n')
    await flush()

    expect(shells.read(info.id).stdout).toBe('first\n')
    expect(shells.read(info.id).stdout).toBe('')

    process.emit('stdout', 'second\n')
    await flush()
    expect(shells.read(info.id).stdout).toBe('second\n')
  })

  it('stdout 与 stderr 分开,状态跟着进程走', async () => {
    const shells = new AgentShells()
    const { info, process } = adopt(shells)
    process.emit('stdout', 'out')
    process.emit('stderr', 'err')
    await flush()

    const first = shells.read(info.id)
    expect(first.stdout).toBe('out')
    expect(first.stderr).toBe('err')
    expect(first.info.status).toBe('running')

    process.finish(3)
    await flush()
    const second = shells.read(info.id)
    expect(second.info.status).toBe('exited')
    expect(second.info.exitCode).toBe(3)
  })

  it('filter 按行筛,不匹配的**丢掉**而不是留到下一次', async () => {
    const shells = new AgentShells()
    const { info, process } = adopt(shells)
    process.emit('stdout', 'keep me\ndrop me\n')
    await flush()

    expect(shells.read(info.id, /keep/).stdout).toBe('keep me')
    expect(shells.read(info.id).stdout).toBe('')
  })

  it('★ 缓冲溢出时丢最早的一段,并把 dropped 报上来(只报一次)', async () => {
    const shells = new AgentShells()
    const { info, process } = adopt(shells)
    process.emit('stdout', 'A'.repeat(BACKGROUND_SHELL_LIMITS.MAX_BUFFER_CHARS))
    process.emit('stdout', 'TAIL')
    await flush()

    const first = shells.read(info.id)
    expect(first.dropped).toBe(true)
    expect(first.stdout.endsWith('TAIL')).toBe(true)
    expect(first.stdout.length).toBe(BACKGROUND_SHELL_LIMITS.MAX_BUFFER_CHARS)
    expect(shells.read(info.id).dropped).toBe(false)
  })

  it('★ 认不出的 id 报错里要列出现有的 —— 否则模型会原样重试', () => {
    const shells = new AgentShells()
    const { info } = adopt(shells, { command: 'npm run dev' })
    expect(() => shells.read('bash_404')).toThrow(info.id)
    expect(() => shells.read('bash_404')).toThrow('Do not retry')
  })
})

describe('AgentShells · 收工', () => {
  it('kill 杀进程并标成 killed;退出回调不能把它改回 exited', async () => {
    const shells = new AgentShells()
    const { info, process } = adopt(shells)

    expect(shells.kill(info.id).status).toBe('killed')
    expect(process.kill).toHaveBeenCalledOnce()

    // 真实进程被杀之后 `exited` 照样会结算 —— 那时状态必须还是 killed
    process.finish(143)
    await flush()
    expect(shells.read(info.id).info.status).toBe('killed')
  })

  it('杀一条已经结束的不是错误,报告它原本的状态', async () => {
    const shells = new AgentShells()
    const { info, process } = adopt(shells)
    process.finish(0)
    await flush()

    const killed = shells.kill(info.id)
    expect(killed.status).toBe('exited')
    expect(process.kill).not.toHaveBeenCalled()
  })

  it('runningCount 只数还在跑的 —— 并发闸门靠它', async () => {
    const shells = new AgentShells()
    const first = adopt(shells)
    adopt(shells)
    expect(shells.runningCount()).toBe(2)

    first.process.finish(0)
    await flush()
    expect(shells.runningCount()).toBe(1)
  })

  it('★ 淘汰只动已结束的,还在跑的一条都不能丢', async () => {
    const shells = new AgentShells()
    const alive = adopt(shells)
    for (let i = 0; i < BACKGROUND_SHELL_LIMITS.MAX_TRACKED + 4; i++) {
      const finished = adopt(shells)
      finished.process.finish(0)
      await flush()
    }

    expect(shells.list().length).toBeLessThanOrEqual(BACKGROUND_SHELL_LIMITS.MAX_TRACKED)
    expect(shells.list().some((entry) => entry.id === alive.info.id)).toBe(true)
  })

  it('★ shutdown 杀光所有后台进程 —— detached 的进程组不随主进程消失', () => {
    const shells = new AgentShells()
    const first = adopt(shells)
    const second = adopt(shells)

    shells.shutdown()
    expect(first.process.kill).toHaveBeenCalledOnce()
    expect(second.process.kill).toHaveBeenCalledOnce()
    expect(shells.list()).toEqual([])
  })

  it('★ id 不复用 —— 淘汰之后旧 id 不能落到另一条 shell 上', async () => {
    const shells = new AgentShells()
    const first = adopt(shells)
    first.process.finish(0)
    await flush()
    const second = adopt(shells)
    expect(second.info.id).not.toBe(first.info.id)
  })
})

describe('AgentShells · 界面旁观', () => {
  it('★ peek 不消费模型的缓冲 —— 界面看过一眼,BashOutput 照样读得到', async () => {
    const shells = new AgentShells()
    const { info, process } = adopt(shells)
    process.emit('stdout', 'ready\n')
    process.emit('stderr', 'warn\n')
    await flush()
    expect(shells.peek(info.id)?.output).toBe('ready\nwarn\n')
    expect(shells.read(info.id).stdout).toBe('ready\n')
    // 模型读走之后,界面的尾巴不跟着清空
    expect(shells.peek(info.id)?.output).toBe('ready\nwarn\n')
  })

  it('peek 认不出的 id 返回 undefined,不 throw —— 界面上那条可能刚被淘汰', () => {
    expect(new AgentShells().peek('bash_404')).toBeUndefined()
  })

  it('开、杀、退出各通知一次;输出增长不通知', async () => {
    const shells = new AgentShells()
    const listener = vi.fn()
    shells.onChange(listener)
    const first = adopt(shells)
    expect(listener).toHaveBeenCalledTimes(1)
    first.process.emit('stdout', 'log line\n')
    await flush()
    expect(listener).toHaveBeenCalledTimes(1)
    shells.kill(first.info.id)
    expect(listener).toHaveBeenCalledTimes(2)
    const second = adopt(shells, { callId: 'call_2' })
    second.process.finish(0)
    await flush()
    expect(listener).toHaveBeenCalledTimes(4)
  })
})
