import { PassThrough } from 'node:stream'
import { describe, expect, it, vi } from 'vitest'
import type { BackgroundShellInfo, ShellBridge } from '../../../../../shared/domain/shell'
import { abortError } from '../../../abort'
import { nodeHost, type DetachedProcess, type KernelHost } from '../../../host'
import type { ToolContext } from '../../registry'
import { bashTool } from '../bash'
import { bashOutputTool, killShellTool } from '../bash-background'

/**
 * 「一条命令的两种活法」——后台执行与单条停止的**工具层**测试。
 *
 * 进程、缓冲、淘汰已经由 `main/__tests__/agent-shells.test.ts` 在注册表那一层钉死;
 * 这里只测工具自己负责的那几件事:没有注册表时说不说清楚、后台回包里有没有
 * 下一步该调什么、以及**单条停止到底是 toolFail 还是 throw**——最后这条一旦写反,
 * 用户点一条命令的停止会把整段回复停在半截,而界面上没有任何报错。
 */

function fakeBridge(over: Partial<ShellBridge> = {}): ShellBridge & { held: Array<{ runId: string; callId: string }> } {
  const held: Array<{ runId: string; callId: string }> = []
  return {
    held,
    hold: (call, stop) => {
      held.push({ runId: call.runId, callId: call.callId })
      // 用例靠它模拟「用户点了这张卡片上的停止」
      stops.set(`${call.runId}:${call.callId}`, stop)
      return () => {
        stops.delete(`${call.runId}:${call.callId}`)
      }
    },
    start: vi.fn(async () => info()),
    read: vi.fn(() => ({ info: info(), stdout: '', stderr: '', dropped: false })),
    kill: vi.fn(() => info({ status: 'killed' })),
    list: vi.fn(() => [info()]),
    ...over
  }
}

const stops = new Map<string, () => void>()

function info(over: Partial<BackgroundShellInfo> = {}): BackgroundShellInfo {
  return {
    id: 'bash_1',
    command: 'npm run dev',
    cwd: '/w',
    status: 'running',
    startedAt: 1,
    runId: 'run_1',
    callId: 'call_1',
    workspaceId: 'ws',
    ...over
  }
}

function ctx(over: Partial<ToolContext> = {}): ToolContext {
  return {
    workspaceRoot: '/w',
    signal: new AbortController().signal,
    permissionMode: 'full',
    depth: 0,
    callId: 'call_1',
    runId: 'run_1',
    host: nodeHost(),
    emit: () => {},
    ...over
  }
}

describe('Bash · 后台执行', () => {
  it('★ 没有注册表时明说,而不是悄悄降级成前台跑', async () => {
    const spawn = vi.fn()
    const r = await bashTool.execute(
      { command: 'npm run dev', run_in_background: true, description: '测试命令' },
      ctx({ host: nodeHost({ spawn }) })
    )
    expect(r.isError).toBe(true)
    expect(r.output.content).toContain('unavailable')
    // 降级的话模型会以为服务已经起在后台,然后去调一个永远读不到东西的 BashOutput
    expect(spawn).not.toHaveBeenCalled()
  })

  it('返回 shell id,并写明下一步用 BashOutput / KillShell', async () => {
    const shells = fakeBridge()
    const spawn = vi.fn()
    const r = await bashTool.execute(
      { command: 'npm run dev', run_in_background: true, description: '起开发服务' },
      ctx({ shells, host: nodeHost({ spawn }) })
    )
    expect(r.isError).toBeFalsy()
    expect(r.output.content).toContain('bash_1')
    expect(r.output.content).toContain('BashOutput')
    expect(r.output.content).toContain('KillShell')
    // 后台走的是注册表,不是 SpawnFn —— 走错的话这条命令会把整轮卡到超时
    expect(spawn).not.toHaveBeenCalled()
    // description 是必填,且必须原样转给注册表 —— 后台 shell 列表显示的就是它
    expect(shells.start).toHaveBeenCalledWith(expect.objectContaining({ command: 'npm run dev', cwd: '/w', description: '起开发服务' }))
  })

  it('起不来时把原因原样转给模型', async () => {
    const shells = fakeBridge({
      start: vi.fn(async () => { throw new Error('Too many background shells are already running (8).') })
    })
    const r = await bashTool.execute({ command: 'npm run dev', run_in_background: true, description: '测试命令' }, ctx({ shells }))
    expect(r.isError).toBe(true)
    expect(r.output.content).toContain('Too many background shells')
  })
})

describe('Bash · 单条停止', () => {
  /**
   * 一个「除非被中断、否则永不返回」的 spawn。
   *
   * ★ 它的中断行为必须和 `nodeSpawn` 一字不差(抛 abortError,不 resolve) ——
   * 那条契约由 `kernel/__tests__/host-spawn.test.ts` 钉住,而这里测的正是
   * 工具怎么**翻译**它:整轮中断往上抛,单条停止转成 toolFail。
   */
  const hangingSpawn: KernelHost['spawn'] = (_cmd, opts) =>
    new Promise((_resolve, reject) => {
      opts.signal.addEventListener('abort', () => reject(abortError()), { once: true })
    })

  it('★ 用户停这一条 = toolFail,不是抛出 —— 抛出会把整轮回复也停掉', async () => {
    const shells = fakeBridge()
    const pending = bashTool.execute(
      { command: 'sleep 30', description: '测试命令' },
      ctx({ shells, host: nodeHost({ spawn: hangingSpawn }) })
    )
    // hold 在 spawn 之前同步调用,但 execute 本身要先过一遍 schema 校验
    await Promise.resolve()
    stops.get('run_1:call_1')?.()

    const r = await pending
    expect(r.isError).toBe(true)
    expect(r.output.content).toContain('user stopped')
    expect(r.output.content).toContain('do not run it again')
  })

  it('★ 整轮中断仍然原样抛出 —— 两条路径必须分得开', async () => {
    const shells = fakeBridge()
    const run = new AbortController()
    const pending = bashTool.execute(
      { command: 'sleep 30', description: '测试命令' },
      ctx({ shells, signal: run.signal, host: nodeHost({ spawn: hangingSpawn }) })
    )
    await Promise.resolve()
    run.abort()
    await expect(pending).rejects.toThrow()
  })

  it('停止句柄在命令收尾时注销 —— 留着的话下一次点停止会停到一条不存在的命令', async () => {
    const shells = fakeBridge()
    await bashTool.execute(
      { command: 'true', description: '测试命令' },
      ctx({ shells, host: nodeHost({ spawn: async () => ({ code: 0, stdout: '', stderr: '' }) }) })
    )
    expect(shells.held).toEqual([{ runId: 'run_1', callId: 'call_1' }])
    expect(stops.has('run_1:call_1')).toBe(false)
  })
})

describe('Bash · 前台转后台', () => {
  function detachedProcess(): DetachedProcess {
    return { stdout: new PassThrough(), stderr: new PassThrough(), exited: new Promise(() => {}), kill: vi.fn() }
  }

  /** 一个只会被「转后台」放出来的 spawn —— 和 nodeSpawn 一样,detach 响了就交出进程 */
  const detachableSpawn = (process: DetachedProcess): KernelHost['spawn'] => (_cmd, opts) =>
    new Promise((resolve) => {
      opts.detach?.addEventListener('abort', () => resolve({ code: 0, stdout: 'building…', stderr: '', detached: process }), { once: true })
    })

  it('没有 adopt(SSH)时不告诉界面能转,也不给 spawn 传 detach', async () => {
    const shells = fakeBridge()
    const emit = vi.fn()
    const spawn = vi.fn<KernelHost['spawn']>(async () => ({ code: 0, stdout: '', stderr: '' }))
    await bashTool.execute({ command: 'true', description: '测试命令' }, ctx({ shells, emit, host: nodeHost({ spawn }) }))
    expect(emit.mock.calls[0]?.[0]).not.toHaveProperty('detachable')
    expect(spawn.mock.calls[0]?.[1]).not.toHaveProperty('detach')
  })

  it('★ 用户转后台:收编进程、回包里有 shell id 与已有输出,而且是成功不是失败', async () => {
    const process = detachedProcess()
    const adoptFn = vi.fn(() => info({ id: 'bash_7' }))
    const detaches = new Map<string, () => void>()
    const shells = fakeBridge({
      adopt: adoptFn,
      hold: (call, _stop, detach) => {
        if (detach !== undefined) detaches.set(call.callId, detach)
        return () => detaches.delete(call.callId)
      }
    })
    const emit = vi.fn()
    const pending = bashTool.execute(
      { command: 'npm run build', description: '构建' },
      ctx({ shells, emit, host: nodeHost({ spawn: detachableSpawn(process) }) })
    )
    await Promise.resolve()
    expect(emit.mock.calls[0]?.[0]).toMatchObject({ detachable: true })
    detaches.get('call_1')?.()

    const r = await pending
    expect(r.isError).toBeFalsy()
    expect(r.output.content).toContain('bash_7')
    expect(r.output.content).toContain('building…')
    expect(r.output.content).toContain('BashOutput')
    expect(adoptFn).toHaveBeenCalledWith(expect.objectContaining({ command: 'npm run build', description: '构建', process }))
    expect(process.kill).not.toHaveBeenCalled()
    // 句柄在收尾时注销
    expect(detaches.size).toBe(0)
  })

  it('★ 收编失败时杀掉它 —— 否则就是一个谁也看不见的孤儿', async () => {
    const process = detachedProcess()
    const detaches = new Map<string, () => void>()
    const shells = fakeBridge({
      adopt: vi.fn(() => { throw new Error('Too many background shells are already running (8).') }),
      hold: (call, _stop, detach) => {
        if (detach !== undefined) detaches.set(call.callId, detach)
        return () => detaches.delete(call.callId)
      }
    })
    const pending = bashTool.execute(
      { command: 'npm run build', description: '构建' },
      ctx({ shells, host: nodeHost({ spawn: detachableSpawn(process) }) })
    )
    await Promise.resolve()
    detaches.get('call_1')?.()

    const r = await pending
    expect(r.isError).toBe(true)
    expect(r.output.content).toContain('Too many background shells')
    expect(process.kill).toHaveBeenCalledOnce()
  })
})

describe('BashOutput', () => {
  it('没有注册表时整体不下发,调到了也说清楚', async () => {
    expect(bashOutputTool.isEnabled?.(ctx())).toBe(false)
    expect(bashOutputTool.isEnabled?.(ctx({ shells: fakeBridge() }))).toBe(true)
    const r = await bashOutputTool.execute({ bash_id: 'bash_1' }, ctx())
    expect(r.isError).toBe(true)
    expect(r.output.content).toContain('unavailable')
  })

  it('把状态说成人话,并标出 stdout / stderr 两段', async () => {
    const shells = fakeBridge({
      read: vi.fn(() => ({ info: info(), stdout: 'ready on :5173', stderr: 'warn', dropped: false }))
    })
    const r = await bashOutputTool.execute({ bash_id: 'bash_1' }, ctx({ shells }))
    expect(r.output.content).toContain('is still running')
    expect(r.output.content).toContain('<stdout>')
    expect(r.output.content).toContain('<stderr>')
  })

  it('退出码要出现在回包里 —— 它决定模型下一步做什么', async () => {
    const shells = fakeBridge({
      read: vi.fn(() => ({ info: info({ status: 'exited', exitCode: 1 }), stdout: '', stderr: '', dropped: false }))
    })
    const r = await bashOutputTool.execute({ bash_id: 'bash_1' }, ctx({ shells }))
    expect(r.output.content).toContain('exited with code 1')
    expect(r.output.content).toContain('no new output')
  })

  it('★ 丢过输出必须说 —— 不说的话模型会拿一段缺了头的日志去归因', async () => {
    const shells = fakeBridge({
      read: vi.fn(() => ({ info: info(), stdout: 'tail', stderr: '', dropped: true }))
    })
    const r = await bashOutputTool.execute({ bash_id: 'bash_1' }, ctx({ shells }))
    expect(r.output.content).toContain('OLDEST part was dropped')
  })

  it('★ 危险正则当场拒掉,不进 RegExp —— 跑进去 V8 就再也出不来', async () => {
    const shells = fakeBridge()
    const r = await bashOutputTool.execute({ bash_id: 'bash_1', filter: '(a+)+$' }, ctx({ shells }))
    expect(r.isError).toBe(true)
    expect(r.output.content).toContain('backtracking')
    expect(shells.read).not.toHaveBeenCalled()
  })

  it('认不出的 id:桥抛出的那句话原样转给模型', async () => {
    const shells = fakeBridge({
      read: vi.fn(() => { throw new Error('There is no background shell with id "bash_9". Currently tracked: bash_1 (running).') })
    })
    const r = await bashOutputTool.execute({ bash_id: 'bash_9' }, ctx({ shells }))
    expect(r.isError).toBe(true)
    expect(r.output.content).toContain('bash_1 (running)')
  })
})

describe('KillShell', () => {
  it('★ 不是 destructive —— 每次收工都弹审批的话,模型会学会不收工', () => {
    expect(killShellTool.destructive).toBe(false)
    expect(killShellTool.readOnly).toBe(false)
    expect(bashOutputTool.readOnly).toBe(true)
  })

  it('杀掉之后说清杀的是哪一条', async () => {
    const shells = fakeBridge()
    const r = await killShellTool.execute({ shell_id: 'bash_1' }, ctx({ shells }))
    expect(r.isError).toBeFalsy()
    expect(r.output.content).toContain('Killed shell bash_1')
    expect(shells.kill).toHaveBeenCalledWith('bash_1')
  })

  it('杀一条早就结束的不是失败', async () => {
    const shells = fakeBridge({ kill: vi.fn(() => info({ status: 'exited', exitCode: 0 })) })
    const r = await killShellTool.execute({ shell_id: 'bash_1' }, ctx({ shells }))
    expect(r.isError).toBeFalsy()
    expect(r.output.content).toContain('already finished')
  })
})
