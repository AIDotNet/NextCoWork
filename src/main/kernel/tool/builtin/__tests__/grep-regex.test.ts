import { mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { Worker } from 'node:worker_threads'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { nodeHost } from '../../../host'
import type { ToolContext } from '../../registry'
import {
  GREP_REGEX_LIMITS,
  GrepRegexPool,
  RegexBudgetError,
  RegexInputTooLargeError,
  RegexQueueFullError,
  grepRegexPoolDiagnostics
} from '../grep-regex'
import {
  grepTool,
  resetGrepRegexDeadlineForTest,
  SEARCH_LIMITS,
  setGrepRegexDeadlineForTest
} from '../search'
import { setGrepBackendsForTest } from '../grep-native'

/**
 * `Grep` 正则**隔离执行**的测试。
 *
 * 背景:`search.ts` 原先在主线程跑模型给的正则,`^(a|aa)+$` 这种漏过静态筛查的
 * 写法一跑就指数级回溯,V8 的匹配又是原子的 —— abort、时钟、进度全停摆,
 * 只能强杀应用。现在编译与匹配都在 `node:worker_threads` 的 `eval` worker 里
 * (见 `grep-regex.ts`),真实墙钟到期即 terminate。
 *
 * ★ 这一组大多断言**行为**(在多少毫秒内退出、线程有没有漏),不是结果正确。
 * 危险正则只在**可终止的 worker** 里跑,绝不让真实主线程执行它们。
 */

let root = ''
const pools: GrepRegexPool[] = []

const LIMITS = {
  maxLineChars: 500,
  maxMultilineChars: 4096,
  maxLinesPerFile: 100,
  maxWorkers: 2
} as const

/**
 * ★ 进程级上限从代码里读,不在测试里另抄一份 —— 抄一份的话,改上限时测试会
 * 继续用旧数字把新行为挡住(或者更糟:悄悄放过越界)。
 */
const MAX_PROCESS = grepRegexPoolDiagnostics().maxProcessWorkers

/** 等某个条件成立 —— 线程真的退出、名额真的归还都是异步的 */
async function until(cond: () => boolean, ms = 3000): Promise<void> {
  const t0 = Date.now()
  while (!cond()) {
    if (Date.now() - t0 > ms) throw new Error('condition not reached in time')
    await new Promise((r) => setTimeout(r, 10))
  }
}

/**
 * ★ 一个**会卡住**的 worker 源码:收到 scan 就把它挂着不回。
 *
 * 「名额被占满」这件事必须能确定地做出来 —— 靠真正则去慢慢匹配的话,扫描几毫秒
 * 就结束(实测 25 万行 4ms),名额早就还回去了,测出来的只是「恰好没撞上」。
 * 想让名额回来就把池 dispose 掉(terminate → exit → 归还),所以这个过程
 * 全程用的都是**生产路径同样的清理逻辑**。只用于这一组,生产路径永远是
 * `WORKER_SOURCE`。
 */
const HOLD_SOURCE = `
const { parentPort } = require('node:worker_threads')
parentPort.on('message', (m) => {
  if (m.kind === 'compile') { parentPort.postMessage({ id: m.id, ok: true }); return }
  // scan 一律不回 —— 这个 slot 一直占着
})
`

/** 一个专门用来占名额的池 —— 只暴露需要的动作,不动池的私有状态 */
interface Holder {
  pool: GrepRegexPool
  /** 占住一个 slot(线程会一直忙到池被 dispose) */
  take(): void
  /** 放掉名额:terminate → 等 exit 落地 → 归还 */
  drop(): Promise<void>
}

function holder(pattern: string, maxWorkers: number): Holder {
  const p = pool(pattern, {
    budgetMs: 30_000,
    limits: { ...LIMITS, maxWorkers },
    workerSource: HOLD_SOURCE
  })
  return {
    pool: p,
    take: () => {
      // 池被 dispose 时这条会 reject —— 这里就是它的归宿,别让它变成 unhandled
      void p.scanFile('held\n').catch(() => {})
    },
    drop: () => p.dispose()
  }
}

/** 进程级的那些数:每读一次都是当下的**事实**,不是缓存的基线 */
function processDiag(): ReturnType<typeof grepRegexPoolDiagnostics> {
  return grepRegexPoolDiagnostics()
}

function ctx(over: Partial<ToolContext> = {}): ToolContext {
  return {
    workspaceRoot: root,
    signal: new AbortController().signal,
    permissionMode: 'auto',
    depth: 0,
    callId: 'call_1',
    runId: 'run_1',
    host: nodeHost(),
    emit: () => {},
    ...over
  }
}

function put(rel: string, body: string): void {
  const abs = join(root, rel)
  mkdirSync(join(abs, '..'), { recursive: true })
  writeFileSync(abs, body)
}

function pool(pattern: string, over: Partial<ConstructorParameters<typeof GrepRegexPool>[0]> = {}): GrepRegexPool {
  const p = new GrepRegexPool({
    pattern,
    ignoreCase: false,
    multiline: false,
    signal: new AbortController().signal,
    budgetMs: 2000,
    limits: LIMITS,
    ...over
  })
  pools.push(p)
  return p
}

beforeEach(() => {
  // 这份测试测的是 JS 引擎的隔离池本身,原生后端一律关掉
  setGrepBackendsForTest([])
  const base = realpathSync.native(mkdtempSync(join(tmpdir(), 'ncw-grep-regex-')))
  root = join(base, 'ws')
  mkdirSync(root)
  // 全套工具级用例走一个很短的隔离期限 —— 危险正则也必须在这个量级内退出。
  // 250ms 而不是更小:隔离池的墙钟**从 run 起步算**(遍历先扣掉),留够走目录的余量。
  setGrepRegexDeadlineForTest(250)
})

afterEach(async () => {
  setGrepBackendsForTest(null)
  resetGrepRegexDeadlineForTest()
  for (const p of pools.splice(0)) await p.dispose()
  rmSync(join(root, '..'), { recursive: true, force: true })
})

// ─────────────────────────── 正常 JS 语法 / 输出 ───────────────────────────

describe('Grep · 隔离后仍是完整的 JS RegExp 语义', () => {
  it('lookaround / 惰性 / 命名组等 JS 写法照常命中', async () => {
    put('a.ts', 'fooBar\nfooBaz\n')
    for (const [pattern, rel] of [
      ['foo(?=Bar)', 'a.ts'],
      ['foo.*?Bar', 'a.ts'],
      ['(?<name>foo)Baz', 'a.ts'],
      ['\\bfoo\\w+\\b', 'a.ts']
    ] as const) {
      const r = await grepTool.execute({ pattern, output_mode: 'content' }, ctx())
      expect(r.output.content, pattern).toContain(rel)
    }
  })

  it('非法正则仍然报错,并提示这是 JS 语法', async () => {
    put('a.ts', 'x\n')
    const r = await grepTool.execute({ pattern: '([unclosed' }, ctx())
    expect(r.isError).toBe(true)
    expect(r.output.content).toContain('JavaScript')
  })
})

// ─────────────────── 底层隔离池:语义 / 复用 / 清理 ───────────────────

describe('GrepRegexPool · 语义', () => {
  it('逐行匹配每行前重置 lastIndex —— 第 2 行不会被上一行的 g 状态漏掉', async () => {
    const p = pool('b')
    await p.compiled()
    expect((await p.scanFile('b\nb\nb')).lines).toEqual([1, 2, 3])
  })

  it('multiline 的零宽匹配不卡死,行的推进与按换行计数一致', async () => {
    const p = pool('\\b', { multiline: true })
    await p.compiled()
    // 'ab\ncd\nef':\b 落在 0/2/3/5/6/8 六处,分别位于第 1/1/2/2/3/3 行 → 去重后 [1,2,3]
    expect((await p.scanFile('ab\ncd\nef')).lines).toEqual([1, 2, 3])
  })

  it('-i 的大小写折叠走 RegExp 自己那份,而不是 toLowerCase', async () => {
    const p = pool('STRASSE', { ignoreCase: true })
    await p.compiled()
    expect((await p.scanFile('strasse\n')).lines).toEqual([1])
  })

  it('多行模式跨行匹配,单行模式不跨行', async () => {
    const ml = pool('a[\\s\\S]*?c', { multiline: true })
    await ml.compiled()
    expect((await ml.scanFile('a\nb\nc\n')).lines).toEqual([1])

    const single = pool('a.*c')
    await single.compiled()
    expect((await single.scanFile('a\nb\nc\n')).lines).toEqual([])
  })
})

describe('GrepRegexPool · 有界 worker 池与清理', () => {
  it('大量小文件复用同一个线程,不会每个文件 spawn', async () => {
    const p = pool('hit')
    await p.compiled()
    const first = p.workerStats.spawned
    for (let i = 0; i < 60; i++) {
      expect((await p.scanFile(`nope ${String(i)}\n`)).lines).toEqual([])
    }
    // 串行扫描 → 一个 slot 足够;绝不可能是「一个文件一个 worker」
    expect(p.workerStats.spawned).toBeLessThanOrEqual(LIMITS.maxWorkers)
    expect(p.workerStats.spawned).toBe(first)
    expect(p.liveWorkers).toBeGreaterThan(0)
  }, 20_000)

  it('并发提交也只起有上限的 worker,不是一请求一线程', async () => {
    const p = pool('hit', { budgetMs: 30_000 })
    await p.compiled()
    // 排队上限本身是进程级预算的一部分,这里只发到上限以内 —— 超了就该被拒
    const n = SEARCH_LIMITS.REGEX_MAX_QUEUED_REQUESTS
    const results = await Promise.all(
      Array.from({ length: n }, (_, i) => p.scanFile(i === 7 ? 'hit\n' : `nope ${String(i)}\n`))
    )
    expect(p.workerStats.spawned).toBeLessThanOrEqual(LIMITS.maxWorkers)
    expect(results[7]?.lines).toEqual([1])
    expect(results.filter((r) => r.lines.length === 0)).toHaveLength(n - 1)
  }, 20_000)

  it('dispose 后没有活着的 worker,且所有请求都被结算', async () => {
    const p = pool('hit')
    await p.compiled()
    // 直接丢掉返回的 promise:dispose 必须把它结算掉,不能留一个永久 pending
    const pending = p.scanFile('hit\n')
    const settled = pending.then(() => 'resolved', () => 'rejected')
    await p.dispose()
    expect(await settled).toBe('rejected')
    expect(p.liveWorkers).toBe(0)
    expect(p.workerStats.terminated).toBeGreaterThan(0)
  })

  it('★ dispose 等的是真的退出:回来时物理存活数已经归零', async () => {
    const p = pool('hit')
    await p.compiled()
    expect(p.aliveWorkers).toBeGreaterThan(0)
    await p.dispose()
    expect(p.liveWorkers).toBe(0)
    // `dead` 只表示「我们不再用它」;物理退出才减 aliveWorkers —— dispose 必须等到那时
    expect(p.aliveWorkers).toBe(0)
    expect(p.workerStats.exited).toBe(p.workerStats.spawned)
  })

  it('★ dispose 之后再提交一律被拒(compile 与 scan 都是)', async () => {
    const p = pool('hit')
    await p.compiled()
    await p.dispose()
    expect(p.disposed).toBe(true)
    await expect(p.compiled()).rejects.toThrow(/disposed/)
    await expect(p.scanFile('hit\n')).rejects.toThrow(/disposed/)
    expect(p.liveWorkers).toBe(0)
  })

  it('★ 单文件正文超限在进 worker 之前就被拒', async () => {
    const p = pool('hit', { limits: { ...LIMITS, maxFileBytes: 64 }, budgetMs: 30_000 })
    await p.compiled()
    await expect(p.scanFile('x'.repeat(65))).rejects.toBeInstanceOf(RegexInputTooLargeError)
    // 恰好等于上限要放行(闸门量的就是长度,不是「长度-1」)
    await expect(p.scanFile('y'.repeat(64))).resolves.toEqual({ lines: [], truncated: false })
  })

  it('★ 构造时就已经中止:不挂悬着的定时器,也不起线程', async () => {
    const ac = new AbortController()
    ac.abort()
    const p = pool('hit', { signal: ac.signal, budgetMs: 60_000 })
    await expect(p.compiled()).rejects.toHaveProperty('name', 'AbortError')
    await expect(p.scanFile('hit\n')).rejects.toHaveProperty('name', 'AbortError')
    expect(p.workerStats.spawned).toBe(0)
    expect(p.aliveWorkers).toBe(0)
  })

  it('★ worker 自身初始化失败时不残留线程,错误照样往上冒', async () => {
    const p = pool('hit', { workerSource: 'throw new Error("boom-from-worker")' })
    await expect(p.compiled()).rejects.toThrow(/boom-from-worker/)
    // 等一拍让 exit 事件落地
    await new Promise((resolve) => setTimeout(resolve, 20))
    expect(p.liveWorkers).toBe(0)
    expect(p.aliveWorkers).toBe(0)
  })
})

// ─────────────────── 进程级预算:并发多个池 / 排队 / 拒绝 ───────────────────

/**
 * ★ 这一组盯的是**进程级**那一层:一个池的上限挡不住「几次 Grep 同时跑」。
 *
 * 所有断言都只读 `grepRegexPoolDiagnostics()` 与池自己的 getter —— 不假定任何私有
 * 结构。上限本身从 `SEARCH_LIMITS` 取,不在这里另抄一份数字。
 */
describe('★ GrepRegexPool · 进程级 worker 名额与排队', () => {
  it('诊断里的上限就是导出的那些数,不是各写各的', () => {
    expect(MAX_PROCESS).toBe(SEARCH_LIMITS.REGEX_MAX_PROCESS_WORKERS)
    expect(MAX_PROCESS).toBe(GREP_REGEX_LIMITS.MAX_PROCESS_WORKERS)
    expect(processDiag().maxQueuedRequests).toBe(SEARCH_LIMITS.REGEX_MAX_QUEUED_REQUESTS)
    expect(MAX_PROCESS).toBe(4)
  })

  it('★ 多个池加起来物理存活的 worker 不超过进程级上限', async () => {
    const a = pool('aaa', { budgetMs: 30_000 })
    const b = pool('bbb', { budgetMs: 30_000 })
    await a.compiled()
    await b.compiled()
    expect(a.aliveWorkers + b.aliveWorkers, '两个池各自起满就冲破进程级上限了').toBeLessThanOrEqual(
      MAX_PROCESS
    )
    expect(processDiag().grantedWorkers).toBeLessThanOrEqual(MAX_PROCESS)
  })

  it('★ 名额被占满时,后来的池是**等**自己的 deadline,不是假报超时', async () => {
    const a = holder('aaa', MAX_PROCESS)
    // compile 占住 IV1(它随后空闲下来);4 个 take 里第 1 个落在那个空闲槽上,
    // 后 3 个各要一个新线程 —— 于是 A 把 4 个名额全攥在手里
    await a.pool.compiled()
    a.take()
    a.take()
    a.take()
    a.take()
    await until(() => processDiag().grantedWorkers === MAX_PROCESS, 5000)

    // B 拿不到名额 → 它的请求必须**挂着等**,不能立刻拒绝
    const b = pool('bbb', { budgetMs: 30_000 })
    const settled: string[] = []
    const attempt = b.scanFile('bbb\n').then(
      (r) => {
        settled.push('ok')
        return r
      },
      (e: unknown) => {
        settled.push((e as Error).name ?? 'error')
        throw e
      }
    )
    await new Promise((r) => setTimeout(r, 60))
    expect(settled, '进程级名额满被当成了失败 —— 这正是「假报超时」那类 bug').toEqual([])
    expect(b.queued).toBe(1)
    expect(processDiag().grantedWorkers).toBeLessThanOrEqual(MAX_PROCESS)

    // A 退出、名额归还后,B 自然拿到名额并跑完
    await a.drop()
    expect(await attempt).toEqual({ lines: [1], truncated: false })
    expect(settled).toEqual(['ok'])
  }, 20_000)

  it('★ 排队是 FIFO:先来的先拿到放出来的名额,后来的不插队', async () => {
    // 名额全被占住,而且每一份都在**忙**(扫着的线程不会被劝退)
    const a = holder('aaa', MAX_PROCESS - 1)
    const b = holder('bbb', 1)
    await Promise.all([a.pool.compiled(), b.pool.compiled()])
    for (let i = 0; i < MAX_PROCESS - 1; i++) a.take()
    b.take()
    await until(() => processDiag().grantedWorkers === MAX_PROCESS)

    // c 先排队,d 后排队
    const c = pool('ccc', { budgetMs: 30_000 })
    const d = pool('ddd', { budgetMs: 30_000 })
    const pc = c.scanFile('ccc\n')
    await until(() => processDiag().waitingPools >= 1)
    const pd = d.scanFile('ddd\n')
    await until(() => processDiag().waitingPools >= 2)

    // 只放出**一个**名额:d 想插队也只有一个位置,而它排在 c 后面
    await b.drop()
    expect(await pc).toEqual({ lines: [1], truncated: false })
    expect(d.queued, '后来的 d 插到 c 前面去了').toBe(1)
    expect(d.aliveWorkers).toBe(0)

    await a.drop()
    expect(await pd).toEqual({ lines: [1], truncated: false })
  }, 30_000)

  it('★ 拿不到名额时到期:按 RegexBudgetError 结算,不留等待者、不留线程', async () => {
    const a = holder('aaa', MAX_PROCESS)
    await a.pool.compiled()
    for (let i = 0; i < MAX_PROCESS; i++) a.take()
    await until(() => processDiag().grantedWorkers === MAX_PROCESS)

    const b = pool('bbb', { budgetMs: 60 })
    const started = Date.now()
    await expect(b.scanFile('bbb\n')).rejects.toBeInstanceOf(RegexBudgetError)
    // 是被自己的 deadline 结算的,不是被别的什么立刻拒绝
    expect(Date.now() - started).toBeGreaterThanOrEqual(40)
    // 等待登记必须被摘掉 —— 全局队列不许 retain 一个已经停掉的池
    expect(processDiag().waitingPools).toBe(0)
    expect(b.aliveWorkers).toBe(0)
    expect(b.queued).toBe(0)

    await a.drop()
    await until(() => processDiag().grantedWorkers === 0)
  }, 20_000)

  it('★ dispose 会摘掉等待登记:全局队列不 retain 已 dispose 的池', async () => {
    const a = holder('aaa', MAX_PROCESS)
    await a.pool.compiled()
    for (let i = 0; i < MAX_PROCESS; i++) a.take()
    await until(() => processDiag().grantedWorkers === MAX_PROCESS)

    const b = pool('bbb', { budgetMs: 30_000 })
    const pending = b.scanFile('bbb\n')
    const settled = pending.then(() => 'resolved', () => 'rejected')
    await until(() => processDiag().waitingPools >= 1)

    const poolsBefore = processDiag().pools
    await b.dispose()
    expect(await settled).toBe('rejected')
    expect(processDiag().waitingPools).toBe(0)
    expect(processDiag().pools).toBe(poolsBefore - 1)
    expect(b.aliveWorkers).toBe(0)

    await a.drop()
    await until(() => processDiag().grantedWorkers === 0)
  }, 20_000)

  it('★ 排队超过上限就明确拒绝,不静默排队', async () => {
    const p = pool('zzz', { budgetMs: 30_000 })
    await p.compiled()
    const n = SEARCH_LIMITS.REGEX_MAX_QUEUED_REQUESTS
    const settled = await Promise.allSettled(Array.from({ length: n + 8 }, () => p.scanFile('zzz\n')))
    const rejected = settled.filter((r) => r.status === 'rejected')
    expect(rejected.length).toBeGreaterThan(0)
    for (const r of rejected) {
      expect((r as PromiseRejectedResult).reason).toBeInstanceOf(RegexQueueFullError)
      expect((r as PromiseRejectedResult).reason.message).toMatch(/queue is full/)
    }
    expect(settled.filter((r) => r.status === 'fulfilled').length).toBeGreaterThan(0)
    expect(p.inFlightChars).toBeLessThanOrEqual(SEARCH_LIMITS.REGEX_MAX_QUEUED_CHARS)
  }, 20_000)

  it('★ 多个池同时跑、各自 dispose 之后,alive 计数退到零', async () => {
    const pools = Array.from({ length: 4 }, (_, i) => pool(`p${String(i)}`, { budgetMs: 30_000 }))
    await Promise.all(pools.map((p) => p.compiled()))
    const results = await Promise.all(pools.map((p, i) => p.scanFile(`p${String(i)}\n`)))
    expect(results.every((r) => r.lines.length === 1)).toBe(true)
    expect(pools.reduce((n, p) => n + p.aliveWorkers, 0)).toBeLessThanOrEqual(MAX_PROCESS)
    await Promise.all(pools.map((p) => p.dispose()))
    expect(pools.reduce((n, p) => n + p.aliveWorkers, 0)).toBe(0)
    expect(processDiag().grantedWorkers).toBe(0)
  }, 20_000)

  it('★ 从池导出的上限和 search.ts 转出来的那些是同一份', () => {
    expect(SEARCH_LIMITS.REGEX_MAX_PROCESS_WORKERS).toBe(GREP_REGEX_LIMITS.MAX_PROCESS_WORKERS)
    expect(SEARCH_LIMITS.REGEX_MAX_QUEUED_REQUESTS).toBe(GREP_REGEX_LIMITS.MAX_QUEUED_REQUESTS)
    expect(SEARCH_LIMITS.REGEX_MAX_QUEUED_CHARS).toBe(GREP_REGEX_LIMITS.MAX_QUEUED_CHARS)
    expect(SEARCH_LIMITS.REGEX_MAX_WORKERS).toBe(2)
  })
})

// ─────────────────── 基础设施故障:立即失败,不留活线程 ───────────────────

/**
 * ★ 这一组盯的是「worker 那层塌了」的两条路:`postMessage` 同步抛,以及
 * `messageerror`(结构化克隆反序列化失败)。两者都必须**立刻**结算正在等的请求,
 * 而且线程要真的退出 —— 只做一个「不再用它」的死标记的话,线程还活着、还占着
 * 256MB 堆和进程级名额,`dispose()` 也可能在它 exit 之前就返回。
 *
 * ★ `postMessage` 用 `mockImplementationOnce` 只打坏**一次**调用:池里第 2 个及
 * 以后的 worker 仍然正常,清理路径之外的行为不被牵连。原型上的方法,spy 会临时
 * 挂到实例上,`mockRestore()` 会把它摘掉(不是塞一份副本回去)。
 * 用 `restoreAllMocks()` 兜底:断言中途失败也不会漏掉恢复。
 */
describe('★ GrepRegexPool · worker 基础设施故障立刻失败且真退出', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('★ postMessage 同步抛:按原始错误立刻拒绝,dispose 后线程与名额都归零', async () => {
    const p = pool('hit', { budgetMs: 30_000 })
    const boom = new Error('postMessage failed')
    /*
      抛的是**同一个** Error 实例:vitest 的实现是原样 `implementation.apply(this, args)`,
      没有包装,所以 `rejects.toBe(boom)` 才有意义 —— 池确实把原始错误漏了出来。
    */
    const spy = vi
      .spyOn(Worker.prototype, 'postMessage')
      .mockImplementationOnce(function (this: Worker): void {
        throw boom
      } as unknown as Worker['postMessage'])

    await expect(p.compiled()).rejects.toBe(boom)
    expect(spy).toHaveBeenCalled()
    expect(p.liveWorkers).toBe(0)

    // dispose 等的是**真的** exit:回来时物理存活数与全局名额都必须归零
    await p.dispose()
    expect(p.aliveWorkers).toBe(0)
    expect(p.workerStats.exited).toBe(p.workerStats.spawned)
    expect(grepRegexPoolDiagnostics().grantedWorkers).toBe(0)
  })

  it('★ messageerror:按原始错误拒绝,线程不留在「死标记却还活着」的状态', async () => {
    const p = pool('hit', { budgetMs: 30_000 })
    const boom = new Error('worker message deserialization failed')
    /*
      每起一个线程就挂一个 error/messageerror 的发射器,只给**第 1 个**线程放一发
      (`this` 就是那个 Worker 实例 —— 类型的 `this` 正是 Worker)。
      ★ 原始 `on` 必须在 `spyOn` **之前**取:spy 换掉的正是 `Worker.prototype.on` 本身,
      在 mock 里再调 `Worker.prototype.on` 就是无穷递归。
    */
    const originalOn = Worker.prototype.on
    let fired = false
    const spy = vi
      .spyOn(Worker.prototype, 'on')
      .mockImplementation(function (this: Worker, event: string, listener: Parameters<Worker['on']>[1]): Worker {
        if (event === 'messageerror' && !fired) {
          fired = true
          // 用真实的事件派发,而不是直接调 listener —— 走的就是池注册的那条路径
          setTimeout(() => { this.emit('messageerror', boom) }, 0)
        }
        return originalOn.call(this, event, listener)
      } as unknown as Worker['on'])

    await expect(p.compiled()).rejects.toBe(boom)
    expect(spy).toHaveBeenCalled()
    expect(p.liveWorkers).toBe(0)

    await p.dispose()
    expect(p.aliveWorkers).toBe(0)
    expect(p.workerStats.exited).toBe(p.workerStats.spawned)
    expect(grepRegexPoolDiagnostics().grantedWorkers).toBe(0)
  })

  it('★ 线程起不来时按根因立刻失败,不是等 deadline 报一个假超时', async () => {
    const failure = new Error('EMFILE: too many open files')
    // 明确在 Worker 已创建、exit 已接好之后让 message 接线失败，不依赖 Node 内部监听次数。
    const originalOn = Worker.prototype.on
    vi.spyOn(Worker.prototype, 'on').mockImplementation(function (this: Worker, event: string, listener: Parameters<Worker['on']>[1]): Worker {
      if (event === 'message') throw failure
      return originalOn.call(this, event, listener)
    } as unknown as Worker['on'])

    const p = pool('hit', { budgetMs: 30_000 })
    const t0 = Date.now()
    await expect(p.compiled()).rejects.toBe(failure)
    await expect(p.compiled()).rejects.toBe(failure)
    // 立刻,不是等 30s 的 deadline
    expect(Date.now() - t0).toBeLessThan(2000)
    // 根因不被压成「超时」:报的是环境,不是这个正则慢
    await expect(p.scanFile('hit\n')).rejects.not.toBeInstanceOf(RegexBudgetError)
    expect(p.workerStats.spawned).toBe(1)

    await p.dispose()
    expect(p.aliveWorkers).toBe(0)
    expect(p.workerStats.exited).toBe(p.workerStats.spawned)
    expect(grepRegexPoolDiagnostics().grantedWorkers).toBe(0)
  })
})

// ─────────────────── 危险正则:短 deadline / abort 必须退出 ───────────────────

/**
 * ★ 这一组是「静态筛查放过的那一族」的报警器。
 *
 * `^(a|aa)+$`(交替)与 `^(a{1,20})+$`(有界内层但外层无界)**都漏过 `redos.ts`** ——
 * 它们正是缺陷里点名的那两个。它们必须在隔离池的短墙钟内被 terminate,
 * 而不是把调用方拖到天荒地老。
 */
describe('★ GrepRegexPool · 危险正则被墙钟终止', () => {
  // 100 个 'a' 后面跟一个 '!':匹配失败 → 指数级回溯。实测这两个模式在
  // 主线程上都会跑成「分钟级」,这里必须几十毫秒内退出。
  const BOMBS = ['^(a|aa)+$', '^(a{1,20})+$']
  const TEXT = `${'a'.repeat(100)}!`

  it.each(BOMBS)('%s 在短 deadline 内被 terminate', async (pattern) => {
    const p = pool(pattern, { budgetMs: 80 })
    await p.compiled()
    const t0 = Date.now()
    await expect(p.scanFile(TEXT)).rejects.toBeInstanceOf(RegexBudgetError)
    const dt = Date.now() - t0
    expect(dt, `${pattern} 耗时 ${String(dt)}ms —— worker 隔离 / terminate 是不是坏了?`).toBeLessThan(1500)
    expect(p.timedOut).toBe(true)
    expect(p.liveWorkers).toBe(0)
    expect(p.workerStats.terminated).toBeGreaterThan(0)
  })

  it('abort 同样立刻终止 worker 并让 pending 以 AbortError 结算', async () => {
    const ac = new AbortController()
    const p = pool('^(a|aa)+$', { signal: ac.signal, budgetMs: 10_000 })
    await p.compiled()
    const pending = p.scanFile(TEXT)
    const settled = pending.then(() => 'resolved', (e: unknown) => e)
    setTimeout(() => ac.abort(), 30)
    const err = await settled
    expect((err as { name?: string }).name).toBe('AbortError')
    expect(p.liveWorkers).toBe(0)
  })
})

// ─────────────────── 端到端:短 deadline 下的部分结果 + 说明 ───────────────────

describe('★ Grep · 墙钟到期给部分结果 + 说明,不是假空结果', () => {
  it('编译排队等待资源时到期，也返回明确的不完整结果而不误报正则错误', async () => {
    put('a.txt', 'hit\n')
    const held = holder('held', MAX_PROCESS)
    await held.pool.compiled()
    for (let i = 0; i < MAX_PROCESS; i++) held.take()
    await until(() => processDiag().grantedWorkers === MAX_PROCESS)
    setGrepRegexDeadlineForTest(100)
    const result = await grepTool.execute({ pattern: 'hit' }, ctx())
    expect(result.isError).toBeFalsy()
    expect(result.output.content).toContain('安全期限')
    expect(result.output.content).toContain('结果不完整')
    expect(result.output.content).toContain('0/1')
    expect(result.output.content).not.toContain('灾难性回溯')
    await held.drop()
  })

  it.each(['^(a|aa)+$', '^(a{1,20})+$'])(
    '%s 在短 deadline 内返回,并明确说结果不完整',
    async (pattern) => {
      put('bomb.txt', `${'a'.repeat(100)}!\n`)
      const t0 = Date.now()
      const r = await grepTool.execute({ pattern, output_mode: 'content' }, ctx())
      const dt = Date.now() - t0
      expect(dt, `耗时 ${String(dt)}ms`).toBeLessThan(2000)
      // 不是错误:它是一个「没扫完」的部分结果
      expect(r.isError).toBeFalsy()
      expect(r.output.content).toContain('安全期限')
      expect(r.output.content).toContain('结果不完整')
    }
  )

  it('正常大仓库仍走隔离池,结果与顺序不变', async () => {
    for (let i = 0; i < 30; i++) put(`f${String(i).padStart(2, '0')}.txt`, 'needle\n')
    const r = await grepTool.execute({ pattern: 'needle', output_mode: 'count' }, ctx())
    const rows = r.output.content.split('\n\n')[0]?.split('\n') ?? []
    expect(rows).toEqual(Array.from({ length: 30 }, (_, i) => `f${String(i).padStart(2, '0')}.txt:1`))
  })

  it('隔离池不会把并发上限撑破 —— 命中上限的截断点与串行时一致', async () => {
    for (let i = 0; i < 40; i++) put(`g${String(i).padStart(2, '0')}.txt`, 'hit\n'.repeat(10))
    const r = await grepTool.execute({ pattern: 'hit', output_mode: 'count' }, ctx())
    const rows = r.output.content.split('\n\n')[0]?.split('\n') ?? []
    expect(rows).toHaveLength(20)
    expect(r.output.content).toContain('match cap')
  })

  it('这套常量真的导出给了测试,不是各写各的', () => {
    expect(SEARCH_LIMITS.REGEX_WALL_BUDGET_MS).toBe(5000)
    expect(SEARCH_LIMITS.REGEX_MAX_WORKERS).toBeGreaterThan(0)
  })
})

// ─────────────────── 行正文只在 content 模式下取 ───────────────────

/**
 * ★ `grepFile` 只在 `output_mode: 'content'` 时才在主线程整文件切行(见那里
 * `needText` 的说明):替一个只要路径/计数的模式切 5MB 的单字符行文件,
 * 会白切出约 250 万个字符串。这一组钉的是**可观察的那一面** —— content 仍然
 * 拿得到行正文(管道真的接上了),非 content 只给路径/计数(不取正文也不乱)。
 */
describe('★ Grep · 行正文只在 content 模式下取', () => {
  it('content 仍给行正文与上下文,其它模式只给路径/计数', async () => {
    put('a.txt', 'L1\nalpha\nL3\n')
    put('b.txt', 'nothing\n')

    const content = await grepTool.execute({ pattern: 'alpha', output_mode: 'content', '-C': 1, '-n': true }, ctx())
    expect(content.output.content.split('\n')).toEqual(['a.txt-1-L1', 'a.txt:2:alpha', 'a.txt-3-L3'])

    const count = await grepTool.execute({ pattern: 'alpha', output_mode: 'count' }, ctx())
    expect(count.output.content.split('\n')).toEqual(['a.txt:1'])

    const files = await grepTool.execute({ pattern: 'alpha' }, ctx())
    expect(files.output.content.split('\n')).toEqual(['a.txt'])
  })
})
