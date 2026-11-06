/**
 * 子代理并发队列 —— 撞上上限时**排队**，而不是把一次调度问题报成失败。
 *
 * ★ 为什么不是「返回 refused 让模型自己重试」（改之前的做法）：
 * 模型拿到 `toolFail` 之后的行为是不可控的 —— 它可能放弃这个子任务、可能换一个
 * `subagent_type` 再试（以为是那个代理坏了）、也可能自己硬做。**这次派发的意图直接丢了。**
 * 而用户配「同时最多 4 个」的本意是「同时最多**跑** 4 个」，不是「最多**派** 4 个」。
 *
 * ★ 本文件**零 electron / 零 store import**。名额占用量与上限都是注入进来的
 * （`Occupancy` / `readCapacity`），所以它能脱离 Electron 单测，也不会和 runtime 形成环。
 */

/** 排不进去时的理由。每一种在 runtime 里都会被翻成一句给模型看的人话。 */
export type SlotRefusal = 'disabled' | 'deadlock' | 'cancelled' | 'overflow'

export type SlotResult<T> = { ok: true; value: T } | { ok: false; reason: SlotRefusal }

export interface Capacity {
  /** 一个父 run 名下的并发上限。`>= 1` */
  perSessionLimit: number
  /** 全应用的并发上限。`0` 是合法值，意为「完全禁用子代理」 */
  globalLimit: number
}

/**
 * 「现在有谁在占着名额」。
 *
 * ★ 要的是 **id 列表**而不是计数：死锁判据要拿这些 id 去和「正被队列阻塞的 run」
 * 求交集，只有计数是判不出来的。
 */
export interface Occupancy {
  /** 某个父 run 名下**仍在跑**的子 run id */
  childRunIds(parentRunId: string): readonly string[]
  /** 全局**仍在跑**的子 run id */
  subagentRunIds(): readonly string[]
}

/** 父 run 的中断源。只用这三样，所以测试不必造一个完整的 RunHandle。 */
export interface ParentSignalSource {
  readonly runId: string
  readonly status: string
  readonly signal: AbortSignal
  on(listener: (event: { type: string }) => void): () => void
}

export interface SlotRequest<T> {
  parent: ParentSignalSource
  /**
   * 等待期间**这个父 run 自己是不是停住了**。前台派发 `true`,后台派发 `false`。
   *
   * ★ 死锁判据只认 `blocking` 的等待者。后台派发立刻返回、父 run 继续往下做、
   * 随时会结束并腾出名额 —— 把它也算成「卡住了」的话,一个连发 5 个后台任务的
   * 父代理会被误判成死锁,当场拒掉一次本来排一会儿就能跑的派发。
   */
  blocking: boolean
  /**
   * ★ **必须是同步函数。**拿到名额的瞬间在派发循环里内联执行。
   *
   * 这是整个队列避开 TOCTOU 的唯一手段：如果改成「resolve 等待者的 promise、
   * 让等待者自己去 `runs.create`」，那次 create 会发生在**微任务**里 ——
   * 而派发循环在同一个 tick 内继续往下走，于是同一个空位会被唤醒的 N 个等待者
   * 同时认领。让 `start()` 在循环内同步跑完，下一轮判断读到的占用量就已经是新的了。
   */
  start: () => T
  /**
   * 真的排上队时回调一次，之后每次排位变化再回调。**没排队（直接拿到名额）就不会调**——
   * runtime 靠这个同步信号来区分「已启动」和「已排队」，见 `acquire` 的注释。
   */
  onQueued?: (ahead: number) => void
}

/** 队列非空时的兜底重扫周期。 */
const TICK_MS = 1000

/*
  ★ 排队**没有**墙钟上限。子代理一跑半小时是常事,排在它后面的就得等它跑完 ——
  以前的 5 分钟上限会把这种正常排队报成失败(Workflow 一扇出就撞上)。
  不会永久挂住靠的是别的出口:父 run 中断/结束即取消、死锁当场拒绝、队列溢出直接拒绝。
*/

/** 队列长度上限，防模型一口气派上百个。 */
export const MAX_QUEUED_PER_PARENT = 16
export const MAX_QUEUED_TOTAL = 64

interface Waiter {
  parentRunId: string
  /** 见 `SlotRequest.blocking`。只有 `true` 的才进 `blocked`,才参与死锁判据 */
  blocking: boolean
  start: () => unknown
  resolve: (result: SlotResult<unknown>) => void
  reject: (error: unknown) => void
  onQueued: ((ahead: number) => void) | undefined
  /** 上一次报给调用方的排位，用来抑制重复的进度事件 */
  reported: number
  /** 摘掉 abort / run_end 监听 */
  cleanup: () => void
}

function decrement(counts: Map<string, number>, key: string): void {
  const left = (counts.get(key) ?? 1) - 1
  if (left <= 0) counts.delete(key)
  else counts.set(key, left)
}

export class SubagentQueue {
  private readonly waiters: Waiter[] = []
  /** parentRunId → 该 run 名下**卡住它自己**的等待者数(只数前台)。死锁判据要用 */
  private readonly blocked = new Map<string, number>()
  /** parentRunId → 该 run 名下排队中的等待者总数(前台 + 后台)。公平性与溢出上限要用 */
  private readonly queuedPerParent = new Map<string, number>()
  private tick: ReturnType<typeof setInterval> | undefined

  constructor(
    private readonly readCapacity: () => Capacity,
    private readonly occupancy: Occupancy
  ) {}

  /**
   * 要一个名额。
   *
   * ★ **同步不变式**：本函数在第一次 `await` 之前就把「直接启动 / 入队 / 立刻拒绝」
   * 这个决定做完了。所以调用方可以在 `acquire(...)` 返回之后**立即**读取
   * `onQueued` 有没有被调过，据此判断这次派发是「已启动」还是「已排队」——
   * 后台派发要立刻回话给模型，靠的就是这条。别在决定之前插入任何 await。
   */
  acquire<T>(req: SlotRequest<T>): Promise<SlotResult<T>> {
    const parentRunId = req.parent.runId

    // 父 run 已经没了就别排了 —— 排上也没人会去消费结果。
    if (req.parent.signal.aborted || req.parent.status !== 'running') {
      return Promise.resolve({ ok: false, reason: 'cancelled' })
    }

    const capacity = this.readCapacity()

    /*
      ★ `globalLimit: 0` 是合法配置，意为「完全禁用子代理」（见 domain/data.ts 的校验）。
      它**永远**不会有空位，入队就是永久挂起 —— 必须在这里短路掉。
    */
    if (capacity.globalLimit <= 0) {
      return Promise.resolve({ ok: false, reason: 'disabled' })
    }

    /*
      快路径：有位置就地启动。

      只在「本 run 没有别的等待者」时才走 —— 否则同一个父 run 的后来者会插队，
      把先到的那个饿死。别的父 run 的等待者不必让：`pump()` 刚刚跑过，
      它们要是能放下，早就已经被启动了。
    */
    if (this.hasRoom(parentRunId, capacity) && !this.queuedPerParent.has(parentRunId)) {
      return this.startNow(req)
    }

    /*
      ★ 判死锁之前**先把自己算进去**(`self`)。

      场景:`globalLimit = 2`,子代理 A、B 都在跑,B 已经卡在队列里等孙代理,
      现在 A 也要派孙代理。先判后入队的话,救兵集合里还剩一个 A —— 判成「再等等」,
      可 A 正是提问的那个人,它一挂就真的没人能动了,永远等下去。
      只有前台派发算数,理由见 `SlotRequest.blocking`。
    */
    const deadlock = this.deadlockReason(parentRunId, capacity, req.blocking)
    if (deadlock !== undefined) return Promise.resolve({ ok: false, reason: deadlock })

    const mine = this.queuedPerParent.get(parentRunId) ?? 0
    if (mine >= MAX_QUEUED_PER_PARENT || this.waiters.length >= MAX_QUEUED_TOTAL) {
      return Promise.resolve({ ok: false, reason: 'overflow' })
    }

    return this.enqueue(req)
  }

  /** 有子 run 结束了 —— 立刻重扫一遍，把延迟从「最多一个 tick」降到 0。 */
  notify(): void {
    this.pump()
  }

  /** 这个 run 是不是正卡在队列里等名额。死锁判据要用。 */
  isBlocked(runId: string): boolean {
    return (this.blocked.get(runId) ?? 0) > 0
  }

  /** 当前排队人数，仅供日志与测试。 */
  get size(): number {
    return this.waiters.length
  }

  /**
   * 清空。★ `resetRuntimeForTest` 必须调 —— 否则等待者和那个 interval 会跨用例泄漏，
   * 下一个用例会被上一个用例的定时器唤醒。
   */
  reset(): void {
    const pending = this.waiters.splice(0, this.waiters.length)
    for (const waiter of pending) {
      waiter.cleanup()
      waiter.resolve({ ok: false, reason: 'cancelled' })
    }
    this.blocked.clear()
    this.queuedPerParent.clear()
    this.stopTick()
  }

  // ─── 内部 ───

  private hasRoom(parentRunId: string, capacity: Capacity): boolean {
    /*
      ★ 两个名额**一起判、一起拿**，中间没有任何 await。

      这就是为什么这里不会出现「双资源获取」那类互锁：没有人会先攥住一半再去等另一半，
      不存在部分持有的状态，也就不存在环。
    */
    return (
      this.occupancy.childRunIds(parentRunId).length < capacity.perSessionLimit &&
      this.occupancy.subagentRunIds().length < capacity.globalLimit
    )
  }

  /**
   * 死锁判据：**关着的那道闸门背后，是不是每一个占位者自己也卡在这个队列里。**
   *
   * `MAX_DEPTH = 2`，子代理还能再派孙代理。全局池满、而池子里那 N 个子代理
   * 恰好全都在等自己的孙代理时，没有任何人能跑完 —— 谁也腾不出空位。
   * 这时候入队就是永久挂起，必须当场拒绝。
   */
  private deadlockReason(parentRunId: string, capacity: Capacity, self: boolean): SlotRefusal | undefined {
    const stuck = (id: string): boolean => (self && id === parentRunId) || this.isBlocked(id)
    const global = this.occupancy.subagentRunIds()
    if (global.length >= capacity.globalLimit && global.length > 0) {
      if (global.every(stuck)) return 'deadlock'
    }
    const mine = this.occupancy.childRunIds(parentRunId)
    if (mine.length >= capacity.perSessionLimit && mine.length > 0) {
      if (mine.every(stuck)) return 'deadlock'
    }
    return undefined
  }

  private startNow<T>(req: SlotRequest<T>): Promise<SlotResult<T>> {
    try {
      return Promise.resolve({ ok: true, value: req.start() })
    } catch (error) {
      return Promise.reject(error)
    }
  }

  private enqueue<T>(req: SlotRequest<T>): Promise<SlotResult<T>> {
    const parentRunId = req.parent.runId
    return new Promise<SlotResult<T>>((resolve, reject) => {
      let settled = false
      const waiter: Waiter = {
        parentRunId,
        blocking: req.blocking,
        start: req.start,
        onQueued: req.onQueued,
        reported: -1,
        resolve: (result) => {
          if (settled) return
          settled = true
          resolve(result as SlotResult<T>)
        },
        reject: (error) => {
          if (settled) return
          settled = true
          reject(error)
        },
        cleanup: () => {
          req.parent.signal.removeEventListener('abort', cancel)
          off()
        }
      }

      const finishWith = (reason: SlotRefusal): void => {
        if (!this.remove(waiter)) return
        waiter.cleanup()
        waiter.resolve({ ok: false, reason })
      }
      const cancel = (): void => {
        finishWith('cancelled')
      }

      /*
        ★ abort 和 run_end **两个都要挂**，缺一不可。

        `RunHandle.abort()` 只 abort 那个 controller，**不**把 status 置为非 running、
        也**不**发 run_end；而 run 正常跑完时走的是 `finish()`，那条路不碰 signal。
        只挂一个的话，另一条路上的等待者就永久留在队列里。
        （范式抄自 `kernel/interaction-gate.ts` 的 `request`。）
      */
      req.parent.signal.addEventListener('abort', cancel, { once: true })
      const off = req.parent.on((event) => {
        if (event.type === 'run_end') cancel()
      })

      this.waiters.push(waiter)
      this.queuedPerParent.set(parentRunId, (this.queuedPerParent.get(parentRunId) ?? 0) + 1)
      if (waiter.blocking) this.blocked.set(parentRunId, (this.blocked.get(parentRunId) ?? 0) + 1)
      this.startTick()
      this.reportPositions()
    })
  }

  /** 从队列里摘掉，并把 blocked 计数减回去。返回它是否还在队列里。 */
  private remove(waiter: Waiter): boolean {
    const index = this.waiters.indexOf(waiter)
    if (index < 0) return false
    this.waiters.splice(index, 1)
    decrement(this.queuedPerParent, waiter.parentRunId)
    if (waiter.blocking) decrement(this.blocked, waiter.parentRunId)
    if (this.waiters.length === 0) this.stopTick()
    return true
  }

  /**
   * 派发一轮。
   *
   * 扫的是整条队列而不是只看队头 —— 队头因为**它自己那个会话**满了而排着的时候，
   * 不该顺带挡住另一个会话里明明放得下的请求。
   */
  private pump(): void {
    if (this.waiters.length === 0) {
      this.stopTick()
      return
    }
    // 每一轮现读一次上限：用户在运行期间调高并发，下一个 tick 就生效。
    const capacity = this.readCapacity()
    let started = false

    for (let i = 0; i < this.waiters.length; ) {
      const waiter = this.waiters[i]
      if (waiter === undefined) {
        i++
        continue
      }
      if (!this.hasRoom(waiter.parentRunId, capacity)) {
        /*
          ★ 死锁**每一轮都要重判**,不能只在入队时判一次:它会后来才形成 ——
          入队的那一刻还有一个空闲的救兵,三秒后那个救兵自己也去排队了。
          (`self: false`:前台等待者自己早已在 `blocked` 里。)
        */
        const deadlock = this.deadlockReason(waiter.parentRunId, capacity, false)
        if (deadlock === undefined) {
          i++
          continue
        }
        this.remove(waiter)
        waiter.cleanup()
        waiter.resolve({ ok: false, reason: deadlock })
        continue
      }
      this.remove(waiter)
      waiter.cleanup()
      started = true
      try {
        // ★ 同步执行，见 SlotRequest.start 上的注释。
        waiter.resolve({ ok: true, value: waiter.start() })
      } catch (error) {
        waiter.reject(error)
      }
      // 不推进 i：刚摘掉一个，当前下标已经是下一个等待者了。
    }

    if (started) this.reportPositions()
  }

  /** 把「前面还有几个」报给每个还在排的等待者，排位没变的不重复报。 */
  private reportPositions(): void {
    this.waiters.forEach((waiter, index) => {
      if (waiter.reported === index) return
      waiter.reported = index
      waiter.onQueued?.(index)
    })
  }

  private startTick(): void {
    if (this.tick !== undefined) return
    /*
      ★ 兜底重扫。有了它，正确性就**不依赖穷举每一条名额释放路径** ——
      run 被 abort 但从未 finish、launch 抛了异常、某个 run 被整个遗弃，
      全都由下一次重扫收敛，而不会让队列永远卡着。
    */
    const timer = setInterval(() => {
      this.pump()
    }, TICK_MS)
    if (typeof timer.unref === 'function') timer.unref()
    this.tick = timer
  }

  private stopTick(): void {
    if (this.tick === undefined) return
    clearInterval(this.tick)
    this.tick = undefined
  }
}
