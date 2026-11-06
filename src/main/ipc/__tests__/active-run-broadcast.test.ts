/**
 * `agent:activeRuns` 广播的验收 —— 「谁在跑」这件事必须送到**每一个**窗口。
 *
 * 为什么需要这份用例:渲染层的运行中角标(外层工作区 Tab、内层对话 Tab、
 * 侧边栏会话行)读的是它自己那份 run 索引,而那份索引原先只能靠 `run_end` 事件收敛。
 * 事件流是按 run 主题定向推送的:没有窗口订阅过的 run —— 定时任务起的那些、
 * 以及 ⌘R 重载后还没被打开的会话 —— 在 `RunPump.flush()` 里整批丢弃,
 * 于是它们的结束永远送不到渲染层,角标一直转到应用重启,而且全程零报错。
 *
 * 所以这里断言的是**与订阅无关**的那条路:一个从没订阅过任何 run 的窗口,
 * 在 run 起止时都能收到这份权威集合。
 */
import { afterEach, describe, expect, it } from 'vitest'
import type { WebContents } from 'electron'
import type { ActiveRunEntry } from '../../../shared/agent/event'
import type { RunRequest } from '../../../shared/agent/run-request'
import { runs } from '../../kernel/run-registry'
import { windows } from '../../window/registry'
import { broadcastActiveRuns } from '../agent'

class FakeWebContents {
  readonly sent: Array<{ channel: string; payload: unknown }> = []
  constructor(readonly id: number) {}
  once(): void {}
  isDestroyed(): boolean {
    return false
  }
  send(channel: string, payload: unknown): void {
    this.sent.push({ channel, payload })
  }

  /**
   * WindowRegistry 判「帧还活不活」靠的是这里,不是 send 抛不抛 ——
   * 真实的 `webFrameMain.send` 自己把异常吞了(见 window/registry.ts)。
   */
  get mainFrame(): { isDestroyed: () => boolean; detached: boolean; send: (channel: string, payload: unknown) => void } {
    return {
      isDestroyed: () => this.isDestroyed(),
      detached: false,
      send: (channel, payload) => this.send(channel, payload)
    }
  }
  /** 只取权威集合广播,按到达顺序 */
  broadcasts(): ActiveRunEntry[][] {
    return this.sent
      .filter((message) => message.channel === 'agent:activeRuns')
      .map((message) => (message.payload as { runs: ActiveRunEntry[] }).runs)
  }
}

let nextId = 900
let runSeq = 0
const req = (over: Partial<RunRequest> = {}): RunRequest => ({
  runId: `broadcast-${++runSeq}`,
  sessionId: 's1',
  workspaceId: 'w1',
  depth: 0,
  input: [{ type: 'text', text: 'hi' }],
  mode: 'normal',
  thinking: 'auto',
  webSearch: false,
  permissionMode: 'ask',
  model: 'fake-model',
  skillIds: [],
  ...over
})

/** `registerIpc` 里的那一行接线,原样照搬 —— 测的是它,不是它的副本。 */
function wire(): () => void {
  return runs.onActiveChange(broadcastActiveRuns)
}

/** 一个**从不订阅任何 run** 的窗口:定时任务跑起来时,每个窗口都是这个样子。 */
function bystander(): FakeWebContents {
  const wc = new FakeWebContents(nextId++)
  windows.register(wc as unknown as WebContents, 'main')
  return wc
}

afterEach(() => {
  runs.abortAll()
  runs.clearForTest()
})

describe('agent:activeRuns', () => {
  it('reaches a window that never subscribed to the run, both at start and at end', () => {
    const off = wire()
    const wc = bystander()

    const request = req()
    const handle = runs.create(request)
    expect(wc.broadcasts()).toEqual([[{
      runId: request.runId, sessionId: 's1', workspaceId: 'w1', status: 'running'
    }]])

    handle.finish('done')
    expect(wc.broadcasts().at(-1)).toEqual([])
    off()
  })

  it('leaves subagents out of the set so their end never has to be observed', () => {
    const off = wire()
    const wc = bystander()
    const parent = runs.create(req())
    const child = runs.create(req({ runId: 'child-run', sessionId: 'child-session', parentRunId: parent.runId, parentSessionId: parent.sessionId, depth: 1 }))

    // 子 run 既不进集合,也不该单独触发一次内容相同的广播。
    expect(wc.broadcasts()).toHaveLength(1)
    child.finish('done')
    expect(wc.broadcasts()).toHaveLength(1)

    parent.finish('done')
    expect(wc.broadcasts().at(-1)).toEqual([])
    off()
  })

  it('keeps an ended run reapable — the listener must not hold the run alive', () => {
    const off = wire()
    bystander()
    const handle = runs.create(req())
    handle.finish('done')

    // `reap()` 拿 listenerCount 当「还有没有人在看」的判据;常驻监听器会让
    // 已结束的 run 永远回收不掉,连带内存里那份事件日志。
    expect(runs.reap()).toBe(1)
    off()
  })
})
