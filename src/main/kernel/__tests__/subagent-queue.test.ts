/**
 * 子代理并发队列。
 *
 * ★ 这里的赌注是**「满了」不等于「失败」**:撞上上限的派发要排队,只有在真的
 * 没有出路时(禁用、死锁、等太久、队列过长)才退回拒绝。所以每条用例都盯着
 * 同一件事:**这次派发最后到底跑没跑**。
 *
 * 占用量与上限都是注进来的,所以整份测试零 electron、零 registry、零真实时钟。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Capacity, Occupancy, ParentSignalSource } from '../subagent-queue'
import { MAX_QUEUED_PER_PARENT, SubagentQueue } from '../subagent-queue'

/** 假注册表:childRunId → parentRunId,只装「还在跑的」 */
class World implements Occupancy {
  readonly capacity: Capacity = { perSessionLimit: 1, globalLimit: 4 }
  private readonly running = new Map<string, string>()
  private seq = 0

  childRunIds(parentRunId: string): readonly string[] {
    return [...this.running].filter(([, parent]) => parent === parentRunId).map(([id]) => id)
  }

  subagentRunIds(): readonly string[] {
    return [...this.running.keys()]
  }

  /** 模拟「一个子 run 起来了」。用作 `SlotRequest.start`,所以必须是同步的 */
  launch(parentRunId: string, id?: string): string {
    const childRunId = id ?? `${parentRunId}:sub:${String(++this.seq)}`
    this.running.set(childRunId, parentRunId)
    return childRunId
  }

  /** 模拟「跑完了」 */
  stop(childRunId: string): void {
    this.running.delete(childRunId)
  }
}

interface FakeParent extends ParentSignalSource {
  abort(): void
  end(): void
}

function fakeParent(runId: string): FakeParent {
  const controller = new AbortController()
  const listeners = new Set<(event: { type: string }) => void>()
  let status = 'running'
  return {
    runId,
    get status() { return status },
    signal: controller.signal,
    on(listener) {
      listeners.add(listener)
      return () => listeners.delete(listener)
    },
    abort() { controller.abort() },
    end() {
      status = 'done'
      for (const listener of [...listeners]) listener({ type: 'run_end' })
    }
  }
}

describe('SubagentQueue', () => {
  let world: World
  let queue: SubagentQueue

  beforeEach(() => {
    vi.useFakeTimers()
    world = new World()
    queue = new SubagentQueue(() => world.capacity, world)
  })

  afterEach(() => {
    queue.reset()
    vi.useRealTimers()
  })

  /** 派一次。返回 promise 与「排到第几位」的回调记录 */
  function dispatch(parent: FakeParent, options: { blocking?: boolean; id?: string } = {}) {
    const positions: number[] = []
    const slot = queue.acquire<string>({
      parent,
      blocking: options.blocking ?? true,
      start: () => world.launch(parent.runId, options.id),
      onQueued: (ahead) => positions.push(ahead)
    })
    return { slot, positions }
  }

  it('满了就排队,有空位按先来后到依次启动', async () => {
    const parent = fakeParent('p')
    const first = dispatch(parent, { id: 'c1' })
    expect(first.positions).toEqual([]) // 直接拿到名额的不回调,runtime 靠这条区分「已启动 / 已排队」
    expect(await first.slot).toEqual({ ok: true, value: 'c1' })

    const second = dispatch(parent, { id: 'c2' })
    const third = dispatch(parent, { id: 'c3' })
    expect(queue.size).toBe(2)
    expect(second.positions).toEqual([0])
    expect(third.positions).toEqual([1])

    // ★ 关键断言:第二、三次派发**没有**被拒,它们只是还没轮到。
    world.stop('c1')
    queue.notify()
    expect(await second.slot).toEqual({ ok: true, value: 'c2' })
    expect(queue.size).toBe(1)

    world.stop('c2')
    queue.notify()
    expect(await third.slot).toEqual({ ok: true, value: 'c3' })
    expect(queue.size).toBe(0)
  })

  it('队头卡住时,不挡住另一段对话里放得下的那个', async () => {
    const p = fakeParent('p')
    const q = fakeParent('q')
    await dispatch(p, { id: 'p1' }).slot
    await dispatch(q, { id: 'q1' }).slot

    const pWaiter = dispatch(p) // 队头,p 的名额满着
    const qWaiter = dispatch(q, { id: 'q2' })
    expect(queue.size).toBe(2)

    world.stop('q1')
    queue.notify()

    expect(await qWaiter.slot).toEqual({ ok: true, value: 'q2' })
    expect(queue.size).toBe(1) // 队头还在等,而后面那个已经跑了
    void pWaiter
  })

  it('全局上限为 0 时立刻拒绝,绝不入队', async () => {
    world.capacity.globalLimit = 0
    const { slot } = dispatch(fakeParent('p'))
    expect(await slot).toEqual({ ok: false, reason: 'disabled' })
    expect(queue.size).toBe(0)
    expect(vi.getTimerCount()).toBe(0) // 一个定时器都没建:它不是「等一等」,是「不准」
  })

  it('占位者全都在等自己的子代理时,判死锁并当场拒绝', async () => {
    world.capacity.globalLimit = 2
    world.capacity.perSessionLimit = 2
    const p = fakeParent('p')
    await dispatch(p, { id: 'a' }).slot
    await dispatch(p, { id: 'b' }).slot

    // a 去派孙代理 —— 全局满了,它排进队列,于是 a 自己也卡住了
    const a = fakeParent('a')
    const grandchildOfA = dispatch(a)
    expect(queue.size).toBe(1)

    // 现在 b 也去派孙代理:占着两个名额的 a、b 都在等,谁也腾不出位子。
    // ★ 判定时**自己也算卡住的**,否则 b 会以为 a 能救它,白等一个超时。
    const b = fakeParent('b')
    expect(await dispatch(b).slot).toEqual({ ok: false, reason: 'deadlock' })
    expect(queue.size).toBe(1)
    void grandchildOfA
  })

  it('死锁是后来才形成的:排着队的那个在下一轮重扫时被拒', async () => {
    /*
      两段对话:p 名下跑着 a、b(会话名额 2 用满),q 名下跑着 c(全局名额 3 用满)。
      W 入队的那一刻 a、b 都在好好跑着 —— 这不是死锁,是拥塞,所以它该等。
    */
    world.capacity.perSessionLimit = 2
    world.capacity.globalLimit = 3
    const p = fakeParent('p')
    const q = fakeParent('q')
    await dispatch(p, { id: 'a' }).slot
    await dispatch(p, { id: 'b' }).slot
    await dispatch(q, { id: 'c' }).slot

    const waiting = dispatch(p)
    expect(queue.size).toBe(1)

    // 随后 a、b 各自也去派孙代理,双双卡在全局闸门上 —— p 的会话名额于是没人能腾。
    dispatch(fakeParent('a'))
    dispatch(fakeParent('b'))

    await vi.advanceTimersByTimeAsync(1000) // 兜底重扫,不必有人来 notify
    expect(await waiting.slot).toEqual({ ok: false, reason: 'deadlock' })
  })

  it('后台等待者不算「卡住」——它的父 run 还在正常干活', async () => {
    world.capacity.globalLimit = 1
    const p = fakeParent('p')
    await dispatch(p, { id: 'a' }).slot

    // a 派了一个后台子代理,排着队;但 a 本身没停,随时会跑完腾出名额
    const child = fakeParent('a')
    dispatch(child, { blocking: false })

    // 所以 p 的下一次派发应当**排队**,而不是被误判成死锁
    const next = dispatch(p)
    expect(queue.size).toBe(2)
    expect(next.positions).toHaveLength(1)
  })

  it('排队没有时间上限:前面的跑半小时,后面的就等半小时', async () => {
    const p = fakeParent('p')
    await dispatch(p, { id: 'a' }).slot
    dispatch(p)

    await vi.advanceTimersByTimeAsync(60 * 60_000)
    expect(queue.size).toBe(1)
  })

  it('排队的太多就拒,不让模型一口气堆上百个', async () => {
    const p = fakeParent('p')
    await dispatch(p, { id: 'a' }).slot
    for (let i = 0; i < MAX_QUEUED_PER_PARENT; i++) dispatch(p)
    expect(queue.size).toBe(MAX_QUEUED_PER_PARENT)
    expect(await dispatch(p).slot).toEqual({ ok: false, reason: 'overflow' })
  })

  /*
    ★ 中断和正常结束**分两条用例**,因为它们在 RunHandle 上是两条完全不同的路:
    `abort()` 只 abort signal、不发 run_end;`finish()` 发 run_end、不碰 signal。
    只挂一个监听的话,漏掉的那一条上的等待者会永久留在队列里。
  */
  it('父 run 被中断时唤醒等待者', async () => {
    const p = fakeParent('p')
    await dispatch(p, { id: 'a' }).slot
    const { slot } = dispatch(p)
    p.abort()
    expect(await slot).toEqual({ ok: false, reason: 'cancelled' })
    expect(queue.size).toBe(0)
  })

  it('父 run 正常结束时唤醒等待者', async () => {
    const p = fakeParent('p')
    await dispatch(p, { id: 'a' }).slot
    const { slot } = dispatch(p)
    p.end()
    expect(await slot).toEqual({ ok: false, reason: 'cancelled' })
    expect(queue.size).toBe(0)
  })

  it('父 run 早就没了的派发直接拒,不进队列', async () => {
    const p = fakeParent('p')
    p.end()
    expect(await dispatch(p).slot).toEqual({ ok: false, reason: 'cancelled' })
    expect(queue.size).toBe(0)
  })

  it('运行期把上限调高,下一次重扫就生效', async () => {
    const p = fakeParent('p')
    await dispatch(p, { id: 'a' }).slot
    const { slot } = dispatch(p, { id: 'b' })

    world.capacity.perSessionLimit = 2 // 用户在设置页改了并发,没有人会来 notify
    await vi.advanceTimersByTimeAsync(1000)
    expect(await slot).toEqual({ ok: true, value: 'b' })
  })

  it('reset 结清所有等待者并停掉定时器', async () => {
    const p = fakeParent('p')
    await dispatch(p, { id: 'a' }).slot
    const { slot } = dispatch(p)
    expect(vi.getTimerCount()).toBeGreaterThan(0)

    queue.reset()
    expect(await slot).toEqual({ ok: false, reason: 'cancelled' })
    expect(queue.size).toBe(0)
    expect(vi.getTimerCount()).toBe(0) // 留着的话,下一个用例会被上一个的定时器唤醒
  })
})
