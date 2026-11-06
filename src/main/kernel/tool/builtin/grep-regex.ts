/**
 * `Grep` 的**正则执行隔离** —— 把「编译 + 逐行/跨行匹配」整段搬进一个 worker,
 * 主线程只负责静态拒绝与有界预筛。
 *
 * ## 为什么必须换成一个能被打断的执行位置
 *
 * 原先的三道缓解(行截到 2000、每 N 个文件查 signal、整次搜索一个墙钟预算)
 * 在**主线程**上执行 `re.test` / `re.exec`,而 V8 的匹配是**原子的**:一旦跑进
 * 一次灾难性回溯,主线程在它返回之前发不出任何事件 —— abort 按钮、时钟、进度
 * 全都停摆,用户只能强杀整个应用。`redos.ts` 的静态筛查只能认识**嵌套无界量词**
 * 一个家族,对 `^(a|aa)+$`、`^(a{1,20})+$` 这种「交替 / 有界内层但外层无界」
 * 一律放过(见那个文件的判据说明),于是这些写法照样能把应用挂死。
 *
 * 只要匹配还能被打断,静态筛查就不再是**唯一**承重那道:它退化成一道快速的
 * **误报优先**过滤器,漏网的写法由这里的墙钟 deadline + `terminate()` 兜底。
 *
 * ## 为什么是 `eval` worker,而不是一个新构建入口
 *
 * worker 的源码必须是自包含的字符串:内核零 electron import,也不该为了一个
 * worker 去改 `electron.vite.config.ts` 加入口、或在 `package.json` 里加脚本
 * (本批次明确不动发行/打包)。`new Worker(source, { eval: true })` 让
 * 「worker 代码住哪儿」和「主进程怎么打包」彻底解耦 —— 这段源码随模块一起
 * 进 bundle,启动时按字符串求值。它只 require `node:worker_threads`,没有别的依赖。
 * `execArgv: []` 另外把**父进程的 node 参数**掐掉:debug 参数会让 worker 抢端口,
 * 而 TS loader / `--import` 那类参数在 `eval` worker 里根本没有解析器可用 ——
 * 继承它们的结果是「某些机器上 Grep 一律失败」这种查不出来的故障。
 *
 * ## 保留 JS RegExp 的**完整语义**
 *
 * 隔离的是**执行位置**,不是语法。flags 仍然是 `g`(+ `-i` 时 `i`,+ multiline 时 `s`),
 * 逐行仍然是 `lastIndex = 0` 后 `test`,跨行仍然是 `exec` 循环 + 零宽自增 +
 * 命中行号按「起点之前的换行数」计。所以输出、顺序、截断、`lastIndex` 语义
 * 与未隔离前逐字节一致 —— 变的只是「出事时能不能被掐断」。
 *
 * ## 契约
 *
 * - **真实墙钟**(`Date.now`),不看 `ctx.host.clock`。测试里 clock 常常是假的,
 *   而安全 deadline 恰恰要在那种场景下仍然生效 —— 否则一个 frozen clock 的
 *   单测就能让危险正则跑满整个测试超时。
 * - 到期 **`terminate()` 所有 worker** 并**结算每一个 pending 请求**
 *   (`RegexBudgetError`),调用方据此返回**明确的部分结果 + 超时说明**,
 *   绝不假空结果,也绝不留孤儿线程。
 *
 * ## 资源预算:三层闸门,各管一件事
 *
 * 「搬进 worker」把主线程被卡死换成了「线程与内存要有人管」,于是分成三层:
 *
 * 1. **池内并发**:一个池同时最多 `maxWorkers` 个 worker(调用方给 2);
 * 2. **进程级**:★ 所有池加起来**物理存活**的 worker 至多 `MAX_PROCESS_WORKERS`(4)。
 *    池内上限只管一次 Grep;几次 Grep 同时跑,最坏就是 n × 池上限 × 256MB 堆,
 *    所以名额是**进程级**的。计数以 `exit` 事件为准:`dead` 标志只表示「我们不再用它」,
 *    被 terminate 的线程在真正退出前仍然占着堆 —— 拿 `dead` 当证据的上限是假上限。
 * 3. **排队**:★ 一个池同时「在跑 + 排队」的请求至多 `MAX_QUEUED_REQUESTS`(16),
 *    正文驻留在 JS 堆上有字节上限。排队的正文不封顶,就是拿 OOM 换进度。
 *
 * ★ 拿不到名额的请求是**排队等待**,不是当场报超时:等待同样受这个池自己的真实
 * deadline 与 `signal` 管,到期/中止时才按 `RegexBudgetError` / `AbortError` 结算。
 * **进程级名额满了绝不等于这个请求超时** —— 那样会给出「仓库里没有」这个看起来
 * 很有说服力的错误答案。等待队列是 FIFO 的,新请求不插队;池被 dispose 时,
 * 它挂在全局等待队列里的那份登记会被摘掉,全局队列不会 retain 一个已经死掉的池。
 *
 * ## 不变式:有界 worker 池,跨文件复用
 *
 * worker 的启动(~10ms)远贵于一次扫描(~1ms),而一次 `Grep` 要扫成千上万个文件 ——
 * 逐文件 spawn 会把「省下的线程」原样花回启动开销上。所以池大小固定为
 * `min(GREP_CONCURRENCY, maxWorkers)`,worker 在文件之间**复用**,
 * 调用方也只在 `pool` 上排队,不会为整仓文件预先堆一串字符串。
 */
import { Worker } from 'node:worker_threads'
import { abortError } from '../../abort'
import { redosRisk } from './redos'

/**
 * 一次隔离子代理扫描的执行位置预算上限(毫秒)。
 *
 * 与 `search.ts` 的 `SEARCH_BUDGET_MS` 同一个量级 —— 但它量的是**真实时间**,
 * 是安全兜底那道闸,不是搜索自己的时间预算(那个走 `ctx.host.clock`)。
 */
const WORKER_WALL_BUDGET_MS = 5000

/**
 * 单个 worker 的 V8 堆上限。输入侧已被封死在**每个文件 ≤5MB**
 * (见下面的 `MAX_FILE_BYTES`),而最坏情况是 5MB 全由单字符行组成 ——
 * `splitLines` 会切出约 250 万个字符串(约 100MB)。256MB 的老生代给这种病态输入
 * 留了 2 倍余量,又仍然是**一个硬上限**:一个失控的 worker 撑爆也只会自己 OOM,
 * 不会吃掉整个进程。
 */
const WORKER_MAX_OLD_GEN_MB = 256
const WORKER_MAX_YOUNG_GEN_MB = 32

/**
 * 单文件正文的**长度**上限,单位是字符串的 code unit。
 *
 * `search.ts` 的 `MAX_GREP_FILE_BYTES` 就是这个值(那边从这里取,只有一份真值);
 * 磁盘上是 5MiB 的文件解码后长度必然 ≤ 5Mi(code unit),所以这条闸对正常读取
 * **不会有**副作用。
 *
 * ★ 量的是长度而不是「重新编码成 UTF-8 的字节数」:坏字节会被解码成 U+FFFD
 * (一个字节变三个),拿再编码的字节数当判据的话,一个刚从磁盘正常读进来的
 * 5MB 文件会在这里莫名被拒 —— 判据必须和上游那道「磁盘字节数」同口径。
 *
 * ★ 池与 worker 各自再拦一道。原先这道闸只在 `search.ts` 里,而池是**公开导出**
 * 的:别的 caller 完全可以绕开它,把任意大的字符串丢进 worker —— 那正是
 * 「一个 caller 决定整个进程的峰值内存」的写法。`scanFile` 拦一次,worker
 * 收消息时再拦一次(eval worker 的 caller 是 `postMessage`,没有类型系统)。
 */
export const MAX_FILE_BYTES = 5 * 1024 * 1024

/**
 * ★ **进程级**同时在跑的 Grep worker 上限。
 *
 * 池内上限挡的是「一次 Grep 开多少线程」,挡不住「几次 Grep 同时跑」:
 * 3 个池各自起满就够把主进程拖垮(最坏 n × 池上限 × 256MB 堆)。所以名额是
 * 进程级的、所有池共享一份,拿不到就**排队等**(见 `#requestProcessWorker`)。
 */
const MAX_PROCESS_WORKERS = 4

/**
 * ★ 一个池同时「在跑 + 排队」的请求上限。
 *
 * 排队 = 正文在 JS 堆上驻留。16 × 5MiB 的 UTF-8 就是 80MiB 原始字节,
 * 而 JS 字符串最坏要到 160MB(字符串内存按 code unit 算,最坏是字节数的两倍)。
 * 再多就拒绝 enqueue 并明确报错 —— 「无限排队」看起来只是慢,
 * 实际是拿一次 OOM 换一点进度,而 OOM 死掉的是整个应用。
 */
const MAX_QUEUED_REQUESTS = 16
/** 排队正文的 code unit 总上限。与上面那条同时生效,挡住「少而极大」的组合。 */
const MAX_QUEUED_CHARS = MAX_QUEUED_REQUESTS * MAX_FILE_BYTES

/** 给测试与诊断核对上限用的唯一出口 —— 不许在别处再抄一遍这些数字。 */
export const GREP_REGEX_LIMITS = {
  WORKER_WALL_BUDGET_MS,
  WORKER_MAX_OLD_GEN_MB,
  WORKER_MAX_YOUNG_GEN_MB,
  MAX_FILE_BYTES,
  MAX_PROCESS_WORKERS,
  MAX_QUEUED_REQUESTS,
  MAX_QUEUED_CHARS
} as const

/**
 * worker 自己那份源码。★ 自包含:只 require `node:worker_threads`,不 import 任何
 * 仓库内模块 —— `eval: true` 下没有模块解析器,`import './x'` 一定失败。
 *
 * ★ **无状态**:每个 `scan` 消息自带 pattern/flags/cfg,worker 当场 `new RegExp`。
 * 不这样做的话,池里第 2 个及以后的 worker 从没收到过 init,拿到的 `cfg` 是 null,
 * 一扫描就崩 —— 而触发条件只是「并发文件数超过 1 个 worker」,横扫一切正常搜索。
 * V8 的 RegExp 编译缓存按 (source, flags) 键控,每次 `new RegExp` 命中的是缓存,
 * 实测 ~0ms,所以按消息编译不会把编译开销乘上文件数。
 *
 * ★ **输入上限在这儿也要有一道**:`cfg.maxFileBytes` 由池传下来,缺省就是
 * `MAX_FILE_BYTES`。公开导出的池挡不住的 caller,worker 自己挡住 ——
 * 超限的正文不进正则、不切行,直接回一条 `tooLarge`。
 *
 * 语义与未隔离前的 `grepFile` 逐字对应(见文件头「保留完整语义」)。
 */
export const WORKER_SOURCE = `
const { parentPort } = require('node:worker_threads')

const DEFAULT_MAX_FILE_BYTES = 5242880

function cap(s, n) { return s.length > n ? s.slice(0, n) : s }
function splitLines(raw, cfg) {
  return raw.split('\\n').map((l) => (l.length > cfg.maxLineChars ? l.slice(0, cfg.maxLineChars) : l))
}

function scan(text, re, cfg) {
  const lines = []
  let truncated = false
  if (cfg.multiline) {
    const joined = cap(splitLines(text, cfg).join('\\n'), cfg.maxMultilineChars)
    re.lastIndex = 0
    let m
    while ((m = re.exec(joined)) !== null) {
      let line = 1
      for (let i = 0; i < m.index; i++) if (joined.charCodeAt(i) === 10) line++
      if (lines[lines.length - 1] !== line) lines.push(line)
      // 零宽匹配会让 lastIndex 不前进,死循环
      if (m.index === re.lastIndex) re.lastIndex++
      if (lines.length >= cfg.maxLinesPerFile) { truncated = true; break }
      if (Date.now() > cfg.deadline) return { lines, truncated, timedOut: true }
    }
  } else {
    let start = 0
    // 单文件也受真实墙钟约束:一个 5MB 的文件本身就够慢,不能等它扫完才发现超时
    for (let n = 1; start <= text.length; n++) {
      let end = text.indexOf('\\n', start)
      if (end === -1) end = text.length
      const line = text.slice(start, end - start > cfg.maxLineChars ? start + cfg.maxLineChars : end)
      re.lastIndex = 0
      if (re.test(line)) {
        lines.push(n)
        if (lines.length >= cfg.maxLinesPerFile) { truncated = true; break }
      }
      if ((n & 0x3ff) === 1 && Date.now() > cfg.deadline) return { lines, truncated, timedOut: true }
      start = end + 1
    }
  }
  return { lines, truncated, timedOut: false }
}

parentPort.on('message', (m) => {
  try {
    if (m.kind === 'compile') {
      new RegExp(m.pattern, m.flags)
      parentPort.postMessage({ id: m.id, ok: true })
      return
    }
    if (m.kind === 'scan') {
      const limit = (m.cfg && m.cfg.maxFileBytes) || DEFAULT_MAX_FILE_BYTES
      // 长度就是上游「磁盘字节数」那道闸的同口径判据(见池里 MAX_FILE_BYTES 的说明)
      if (m.text.length > limit) {
        parentPort.postMessage({
          id: m.id,
          ok: false,
          tooLarge: true,
          error: 'input is ' + m.text.length + ' chars which exceeds the per-file limit of ' + limit
        })
        return
      }
      const re = new RegExp(m.pattern, m.flags)
      const r = scan(m.text, re, m.cfg)
      parentPort.postMessage({ id: m.id, ok: true, lines: r.lines, truncated: r.truncated, timedOut: r.timedOut })
      return
    }
  } catch (e) {
    parentPort.postMessage({ id: m.id, ok: false, error: e && e.message ? e.message : String(e) })
  }
})
`

/** worker 的静态上限配置。workers 侧真拿这份数组切行、截断。 */
export interface GrepRegexLimits {
  /** 单行截断,匹配之前生效 */
  maxLineChars: number
  /** multiline 时整篇进入正则的上限 */
  maxMultilineChars: number
  /** 单个文件的命中行数上限 */
  maxLinesPerFile: number
  /** 池内并发 worker 上限(实际还会被进程级名额 `MAX_PROCESS_WORKERS` 压住) */
  maxWorkers: number
  /** 单文件正文的 code unit 长度上限,缺省 `MAX_FILE_BYTES`。★ 只给测试调小用 */
  maxFileBytes?: number
}

export interface GrepRegexOptions {
  pattern: string
  ignoreCase: boolean
  multiline: boolean
  signal: AbortSignal
  limits: GrepRegexLimits
  /** 真实墙钟预算(毫秒),从池创建时起算 */
  budgetMs?: number
  /** 纯测量/诊断用的事件(启动、异常、退出),不给就不发 */
  onEvent?(message: string): void
  /**
   * worker 源码。**仅供测试**注入一个会在初始化时抛错的 worker,用来验证
   * 「错误 worker 不泄漏」那条清理路径。生产路径永远用上面的自包含源码。
   */
  workerSource?: string
}

export interface FileScan {
  /** 命中的行号,升序,1 起 */
  lines: number[]
  /** 撞上单文件命中上限(结果可能不完整) */
  truncated: boolean
}

/** 到期/中止时**结算每一个 pending 请求**用它 —— 调用方据此给部分结果 + 说明。 */
export class RegexBudgetError extends Error {
  constructor(message = 'regex scan exceeded its wall-clock budget') {
    super(message)
    this.name = 'RegexBudgetError'
  }
}

/**
 * 排队位置不够(见 `MAX_QUEUED_REQUESTS`)。★ 这是一个**调用方要改请求**的错误,
 * 不是「没搜完」:`search.ts` 只把 `RegexBudgetError` 当部分结果,这条会原样冒上去。
 */
export class RegexQueueFullError extends Error {
  constructor(
    message = `regex pool queue is full (${String(MAX_QUEUED_REQUESTS)} requests / ` +
      `${String(MAX_QUEUED_CHARS >> 20)}MiB in flight) — narrow the pattern, glob, or path and search again`
  ) {
    super(message)
    this.name = 'RegexQueueFullError'
  }
}

/** 单文件正文超限。★ 池是公开导出的,这道闸不能只靠调用方自觉。 */
export class RegexInputTooLargeError extends Error {
  constructor(length: number, limit: number) {
    super(
      `regex input is ${String(length)} chars which exceeds the per-file limit of ${String(limit)}`
    )
    this.name = 'RegexInputTooLargeError'
  }
}

/**
 * ★ 池的**基础设施**起不来(worker 线程创建失败)。
 *
 * 与 `RegexBudgetError` 是两件事:那不是「没搜完」,是**这个环境根本跑不了**,
 * 所以不能按部分结果结算 —— 那会给出一个看起来很有说服力的「仓库里没有」。
 * 这条原样冒给调用方,连同 `cause` 里的根因(EMFILE / ENOMEM …)。
 */
export class RegexWorkerError extends Error {
  constructor(message: string, options?: { cause?: unknown }) {
    super(message, options)
    this.name = 'RegexWorkerError'
  }
}

interface Pending {
  kind: 'compile' | 'scan'
  /** scan 的正文;compile 为空串 */
  text: string
  settle(reply: WorkerReply): void
  fail(error: unknown): void
}

interface WorkerReply {
  id: number
  ok: boolean
  error?: string
  tooLarge?: boolean
  lines?: number[]
  truncated?: boolean
  timedOut?: boolean
}

class Slot {
  readonly worker: Worker
  /** 我们不再用它了(正在退出或已经死了) */
  dead = false
  /** `exit` 事件落地 —— 「这个线程物理上没了」的唯一凭据 */
  readonly exit: Promise<void>
  private readonly resolveExit: () => void
  /** 这个 slot 占着一个**进程级**名额,在 `exit` 时归还(只归还一次) */
  holdsToken = true
  /** 这个 slot 正在处理的请求;`null` = 空闲 */
  current: { id: number; resolve(reply: WorkerReply): void; reject(error: unknown): void } | null = null
  constructor(worker: Worker) {
    this.worker = worker
    let release: () => void = () => {}
    this.exit = new Promise<void>((resolve) => {
      release = resolve
    })
    this.resolveExit = release
  }

  /** 由池的 `exit` 处理调用。内部用,别从外面碰 */
  markExited(): void {
    this.resolveExit()
  }
}

// ───────────────────── 进程级名额(所有池共享的一份) ─────────────────────

/**
 * 等名额的登记。★ 用 FIFO 数组而不是 Map:新来的请求只能排在队尾,
 * 否则「谁先等谁先跑」不成立,饥饿的是先来的那批。
 */
interface PoolWaiter {
  /** 拿到名额返回 true;已经被放弃/池已停则返回 false,名额转给下一个 */
  grant(): boolean
  /** 放弃等待(池已 dispose / 到期 / 中止) */
  abandon(): void
}

const grantQueue: PoolWaiter[] = []
/** 已发出的名额数 —— 只有 worker 真正 `exit` 时才减少 */
let grantedWorkers = 0
/** 还活着(未 dispose)的池,给诊断和「不留 registry」测试用 */
const livePools = new Set<GrepRegexPool>()
/** 正在「劝空闲线程让位」,别递归地再来一轮 */
let nudging = false

/**
 * 名额刚腾出来。★ 先给排在最前面的池;若还有池在等,就去劝**手里攥着空闲线程**
 * 的池退掉几个 —— 否则先来的池会把名额一直占着,后来的池只能干等到自己的
 * deadline,那不是「排队」是「饿死」。
 *
 * 只劝 `idleWorkers > 0` 的池(正在扫的线程退掉就是白扔一次扫描),而且每个池
 * 至多让出自己空闲的那几个 —— 让位是有限度的。
 */
function handOverProcessWorker(): void {
  grantedWorkers -= 1
  while (grantQueue.length > 0) {
    const waiter = grantQueue.shift() as PoolWaiter
    // ★ 交给真正还需要它的那个池;已经死掉的登记直接跳过,名额继续往下传
    if (waiter.grant()) {
      grantedWorkers += 1
      if (grantQueue.length > 0) nudgeIdlePools(grantQueue.length)
      return
    }
  }
}

/** 请空闲的池让出至多 `limit` 个线程给正在排队的池。 */
function nudgeIdlePools(limit: number): void {
  if (nudging) return
  nudging = true
  try {
    let left = limit
    // 空闲多的先等,空闲少的先让 —— 让位的池重开线程的代价最小
    const candidates = [...livePools]
      .filter((p) => p.idleWorkers > 0)
      .sort((a, b) => a.idleWorkers - b.idleWorkers)
    for (const pool of candidates) {
      if (left <= 0) return
      left -= pool.shedIdleWorkers(Math.min(left, pool.idleWorkers))
    }
  } finally {
    nudging = false
  }
}

export interface GrepRegexPoolDiagnostics {
  /** 还在注册表里的池。dispose 过的池立即摘掉 —— 全局队列不 retain 死池 */
  pools: number
  /** 正在等进程级名额的池数 */
  waitingPools: number
  /** 已发出、尚未随 `exit` 归还的进程级名额(≈ 物理存活的 worker 数) */
  grantedWorkers: number
  maxProcessWorkers: number
  maxQueuedRequests: number
}

/** 进程级诊断。★ 这些数必须是**事实**:`grantedWorkers` 只在 exit 落地时才减。 */
export function grepRegexPoolDiagnostics(): GrepRegexPoolDiagnostics {
  return {
    pools: livePools.size,
    waitingPools: grantQueue.length,
    grantedWorkers,
    maxProcessWorkers: MAX_PROCESS_WORKERS,
    maxQueuedRequests: MAX_QUEUED_REQUESTS
  }
}

/**
 * 有界、可复用、可强杀的 JS-regex 执行池。生命周期由调用方拥有
 * (`Grep.run` 的 `try/finally`),`dispose()` 后不留任何线程。
 */
export class GrepRegexPool {
  private readonly opts: GrepRegexOptions
  private readonly limits: GrepRegexLimits
  private readonly source: string
  private readonly budgetMs: number
  private readonly deadline: number
  /** 还在用的 slot(被标记退出时立即摘掉 —— `liveWorkers` 看的就是这个) */
  private readonly slots: Slot[] = []
  private readonly idle: Slot[] = []
  /** FIFO:只从头部取。新请求永远排到队尾,不插队 */
  private readonly queue: Pending[] = []
  /** 所有未结算请求的集合(排队中 + 在 worker 里)。结算失败时按它兜底 */
  private readonly pending = new Set<Pending>()
  /** 正在退出的线程们的 termination promise */
  private readonly terminations = new Set<Promise<void>>()
  private waiter: PoolWaiter | null = null
  /** 本池占着的进程级名额数(每个 spawn 成功的 worker 一个) */
  private heldWorkers = 0
  /** 排队中的正文总 code unit 数 */
  private outstandingChars = 0
  /** 已经有一个在等进程级名额的申请在飞 —— 别对同一个池重复排队 */
  private acquiring = false
  /** 正在把空闲线程让出去(防止重入的 #pump 里再让一轮) */
  private shedding = false
  /** worker 起不来:不再自动重试,免得变成热循环(deadline 仍会结算所有请求) */
  private spawnFailed = false
  /**
   * ★ 起 worker 时那个**基础设施错误**。存下来是为了让之后的 submit **立刻**以根因
   * 失败 —— 只记一个 `spawnFailed` 标记的话,后来的请求只能干等到自己的 deadline,
   * 报出来的是一个跟根因无关的「超时」,而调用方会把它当成「这个正则太慢」。
   */
  private spawnError: Error | null = null
  private nextId = 1
  private stopped: 'abort' | 'timeout' | 'dispose' | null = null
  private timer: NodeJS.Timeout | null = null

  /** 启动 / 被 terminate / 真正 exit 的计数 —— 给测试与诊断核对「不泄漏」的确定性凭据。 */
  private readonly stats = { spawned: 0, terminated: 0, exited: 0 }

  private readonly onAbort = (): void => {
    if (this.stopped !== null) return
    this.stopped = 'abort'
    this.waiter?.abandon()
    this.#failPending(abortError())
    this.#terminateAll()
  }

  constructor(opts: GrepRegexOptions) {
    this.opts = opts
    this.limits = opts.limits
    this.source = opts.workerSource ?? WORKER_SOURCE
    this.budgetMs = opts.budgetMs ?? WORKER_WALL_BUDGET_MS
    this.deadline = Date.now() + this.budgetMs
    if (opts.signal.aborted) {
      // ★ 已经中止就别挂定时器:没人会 clearTimeout 它,它会把进程按住不退
      this.stopped = 'abort'
    } else {
      opts.signal.addEventListener('abort', this.onAbort, { once: true })
      const t = setTimeout(() => this.#expire(), this.budgetMs)
      // 正常路径下别让这个定时器把进程按住不退
      if (typeof t.unref === 'function') t.unref()
      this.timer = t
    }
    livePools.add(this)
  }

  /** 池里还留着、还在用的 worker 数(terminate 一发出就减)。 */
  get liveWorkers(): number {
    return this.slots.length
  }

  /**
   * ★ **物理上**还活着的 worker 数(`spawned - exited`)。诊断和容量都该看它:
   * 被 terminate 的线程从 `slots` 里走了,但在 `exit` 落地前它仍然占着 256MB 的堆 ——
   * 而这正是要防的那件事。`dispose()` 返回时它必然为 0(dispose 等 exit)。
   */
  get aliveWorkers(): number {
    return this.stats.spawned - this.stats.exited
  }

  get workerStats(): { spawned: number; terminated: number; exited: number } {
    return { ...this.stats }
  }

  get timedOut(): boolean {
    return this.stopped === 'timeout'
  }

  get disposed(): boolean {
    return this.stopped === 'dispose'
  }

  /** 排队中的请求数(不含已经在 worker 里跑的那个) */
  get queued(): number {
    return this.queue.length
  }

  /** 空转着、随时可以让给别的池的 worker 数 */
  get idleWorkers(): number {
    return this.idle.length
  }

  /** 排队 + 在跑的正文总 code unit 数 */
  get inFlightChars(): number {
    return this.outstandingChars
  }

  /**
   * 在一个 worker 里编译正则。返回 V8 的原话(`null` = 编译通过)——
   * 语法错误的成句留给 `search.ts`,那里有给模型的改写提示。
   */
  async compiled(): Promise<string | null> {
    const reply = await this.#submit('compile', '')
    return reply.ok ? null : (reply.error ?? 'invalid regular expression')
  }

  /**
   * 扫一个文件的正文。`Grep` 的文件读取已经在外面做完(二进制嗅探、大小上限、
   * 字面量预筛都在主线程)—— 到这里只剩「交给 worker 匹配」这一件事。
   */
  scanFile(text: string): Promise<FileScan> {
    const limit = this.limits.maxFileBytes ?? MAX_FILE_BYTES
    // ★ 池是公开导出的:输入上限不能只指望调用方。worker 侧还有一道(见 WORKER_SOURCE)
    if (text.length > limit) {
      return Promise.reject(new RegexInputTooLargeError(text.length, limit))
    }
    // 静态筛查在这里再跑一遍:调用方已经跑过,这是纵深防御,代价是一次线性扫描
    const risk = redosRisk(this.opts.pattern)
    if (risk !== null) return Promise.reject(new Error(risk))
    return this.#submit('scan', text).then((reply) => {
      if (reply.timedOut === true) {
        // worker 自己撞上了 cfg.deadline:这一次扫描没扫完,和一个池级超时等价
        this.#expire()
        throw new RegexBudgetError()
      }
      if (reply.ok !== true) throw new Error(reply.error ?? 'regex worker scan failed')
      return { lines: reply.lines ?? [], truncated: reply.truncated === true }
    })
  }

  /**
   * 结束这一次搜索:停掉定时器与 abort 监听,**terminate 全部 worker**,
   * 结算任何还挂着的请求。★ 等的是每个线程的 `terminate()` promise 与 `exit` 事件,
   * **不是 `dead` 标志** —— 线程真正退出前它还在占内存,`dispose()` 返回就该意味着
   * 「份额已经还回去了」。幂等。
   */
  async dispose(): Promise<void> {
    if (this.timer !== null) {
      clearTimeout(this.timer)
      this.timer = null
    }
    this.opts.signal.removeEventListener('abort', this.onAbort)
    if (this.stopped === null) this.stopped = 'dispose'
    // 先摘掉全局等待队列里的自己,免得全局队列 retain 一个已经死掉的池
    this.waiter?.abandon()
    this.#failPending(new Error('regex pool disposed'))
    for (const slot of [...this.slots]) this.#beginTerminate(slot)
    // 每轮都有 promise 落地;terminate 之后不会再产生新的(stopped 已定),这里只兜个底
    let guard = 0
    while (this.terminations.size > 0 && guard++ < 64) {
      await Promise.all([...this.terminations])
    }
    livePools.delete(this)
  }

  #flags(): string {
    return `g${this.opts.ignoreCase ? 'i' : ''}${this.opts.multiline ? 's' : ''}`
  }

  #workerCap(): number {
    return Math.max(1, Math.min(this.limits.maxWorkers, MAX_PROCESS_WORKERS))
  }

  #workerLimits(): GrepRegexLimits & { deadline: number; multiline: boolean } {
    return {
      maxLineChars: this.limits.maxLineChars,
      maxMultilineChars: this.limits.maxMultilineChars,
      maxLinesPerFile: this.limits.maxLinesPerFile,
      maxWorkers: this.limits.maxWorkers,
      maxFileBytes: this.limits.maxFileBytes ?? MAX_FILE_BYTES,
      deadline: this.deadline,
      multiline: this.opts.multiline
    }
  }

  /**
   * 提交一个请求。★ 两道闸在这里:池已停(abort/timeout/dispose)和排队位置不够。
   * 后者是**明确拒绝**,不是静默排队 —— 无限排队就是拿 OOM 换进度。
   */
  #submit(kind: 'compile' | 'scan', text: string): Promise<WorkerReply> {
    const stop = this.#stopError(0, true)
    if (stop !== null) return Promise.reject(stop)
    if (this.pending.size >= MAX_QUEUED_REQUESTS || this.outstandingChars + text.length > MAX_QUEUED_CHARS) {
      return Promise.reject(new RegexQueueFullError())
    }
    this.outstandingChars += text.length
    return new Promise<WorkerReply>((resolve, reject) => {
      const job: Pending = {
        kind,
        text,
        settle: (reply) => {
          if (!this.pending.delete(job)) return
          this.outstandingChars -= text.length
          resolve(reply)
        },
        fail: (error) => {
          if (!this.pending.delete(job)) return
          this.outstandingChars -= text.length
          reject(error)
        }
      }
      this.pending.add(job)
      this.queue.push(job)
      this.#pump()
    })
  }

  /** 真的开始扫。`#pump` 里唯一发消息的地方。 */
  #dispatch(slot: Slot, job: Pending): void {
    const id = this.nextId++
    const payload =
      job.kind === 'compile'
        ? { id, kind: 'compile', pattern: this.opts.pattern, flags: this.#flags() }
        : {
            id,
            kind: 'scan',
            text: job.text,
            pattern: this.opts.pattern,
            flags: this.#flags(),
            cfg: this.#workerLimits()
          }
    slot.current = { id, resolve: (reply) => job.settle(reply), reject: (error) => job.fail(error) }
    try {
      slot.worker.postMessage(payload)
    } catch (error) {
      const p = slot.current
      slot.current = null
      /*
        ★ postMessage 都失败了,这个线程不能再信。这里必须**真的 terminate**,
        不能只 `#bury` —— `dead` 只表示「我们不再用它」,而物理退出的凭据是
        `exit` 事件/`terminate()` 的 promise。只标记的话,进程级名额要等到
        线程自己不知何时退出才归还,而 `dispose()` 也可能在它 exit 之前就返回。
        名额仍由 `#onExit` 归还(那才是物理退出的凭据),这里只负责让它真的退。
      */
      this.#beginTerminate(slot)
      if (p !== null) p.reject(error)
      this.#pump()
    }
  }

  /**
   * 抽一次队列。★ 这个函数**从不 await**:能直接派发的立刻派发,要新线程才去
   * 申请名额(申请是异步的,但它在后台跑,不拦住「把活派给已经空闲的 slot」)。
   * 早先把它写成「drain 里 await 名额」会死锁:池在等全局名额时,自己的 worker
   * 干完活了、队列里还有活,却因为 drain 停在 await 上而没人去派。
   */
  #pump(): void {
    if (this.stopped !== null || this.spawnFailed) return
    while (this.queue.length > 0) {
      const idle = this.idle.pop()
      if (idle === undefined) break
      this.#dispatch(idle, this.queue.shift() as Pending)
    }
    if (this.stopped !== null) return
    if (this.queue.length === 0) {
      // ★ 我这边暂时没活了,而别的池正在等名额:把空闲线程让出去。不准的话,
      // 先来的池会攥着闲置线程,把后来的池一直饿到它自己的 deadline。
      if (!this.shedding && grantQueue.length > 0 && this.idle.length > 0) {
        this.shedding = true
        this.shedIdleWorkers(grantQueue.length)
        this.shedding = false
      }
      return
    }
    // 需要新线程。池内还有空间吗?(名额是进程级的,见 #requestProcessWorker)
    if (this.acquiring || this.slots.length >= this.#workerCap()) return
    this.acquiring = true
    void this.#acquireAndDispatch()
  }

  /**
   * 退掉本池最多 `limit` 个**空闲** worker,把进程级名额让给正在等的池,
   * 返回真正退掉的个数。
   * ★ 只退空闲的:正在扫的线程退掉就是白扔一次扫描。线程重开只要 ~10ms,
   * 而让名额在池之间流动,比攥着闲置线程划算得多。
   */
  shedIdleWorkers(limit: number): number {
    // 自己还有活要干(或已经起不动线程了)就不让 —— 让出去只会拖慢自己
    if (this.stopped !== null || this.spawnFailed || this.queue.length > 0 || limit <= 0) return 0
    const victims = this.idle.slice(0, limit)
    for (const slot of victims) this.#beginTerminate(slot)
    if (victims.length > 0) this.#pump()
    return victims.length
  }

  /** 申请一个进程级名额,拿到就起一个 worker 并派活。 */
  async #acquireAndDispatch(): Promise<void> {
    let granted: boolean
    try {
      granted = await this.#requestProcessWorker()
    } finally {
      this.acquiring = false
    }
    if (!granted) return
    if (this.stopped !== null) {
      this.#releaseProcessWorker()
      return
    }
    const spawnedBefore = this.stats.spawned
    let slot: Slot
    try {
      slot = this.#spawn()
    } catch (error) {
      /*
        ★ 起不来就是**起不来**:不再自动重试,但也绝不把它压成一次「超时」——
        那样 pending 只能干等到自己的 deadline,调用方拿到的错误还指向正则慢,
        而根因是环境(EMFILE / ENOMEM …)。所以:记下根因、当场结算所有 pending、
        真的 terminate 掉已经起来的线程、把**这次没 spawn** 的那份名额还回去。
        之后 `#submit` 由 `#stopError` 的 probe 直接以这个根因失败。
      */
      this.spawnFailed = true
      this.spawnError =
        error instanceof Error
          ? error
          : new RegexWorkerError(`Grep regex worker failed to start: ${String(error)}`)
      this.opts.onEvent?.(`Grep regex worker failed to start: ${this.spawnError.message}`)
      this.#failPending(this.spawnError)
      this.#terminateAll()
      // 线程已创建但接线失败时，名额仍须等 exit 归还，不能提前突破物理上限。
      if (this.stats.spawned === spawnedBefore) this.#releaseProcessWorker()
      return
    }
    const job = this.queue.shift()
    if (job === undefined) this.idle.push(slot)
    else this.#dispatch(slot, job)
    // 池内可能还有空间(上限 2 个),或者刚起空了别的活
    this.#pump()
  }

  /**
   * 申请一个进程级名额。★ FIFO:有人已经排队时,新来的也只能排到队尾。
   * 池在等的时候被 abort/到期/dispose,`abandon()` 会把它从全局队列里摘掉。
   */
  #requestProcessWorker(): Promise<boolean> {
    if (this.stopped !== null) return Promise.resolve(false)
    return new Promise<boolean>((resolve) => {
      const waiter: PoolWaiter = {
        grant: () => {
          if (this.waiter !== waiter) return false
          this.waiter = null
          this.heldWorkers += 1
          resolve(true)
          return true
        },
        abandon: () => {
          if (this.waiter !== waiter) return
          this.waiter = null
          const i = grantQueue.indexOf(waiter)
          if (i !== -1) grantQueue.splice(i, 1)
          resolve(false)
        }
      }
      if (grantedWorkers < MAX_PROCESS_WORKERS && grantQueue.length === 0) {
        this.heldWorkers += 1
        grantedWorkers += 1
        resolve(true)
        return
      }
      this.waiter = waiter
      grantQueue.push(waiter)
      // ★ 我一排队,就该有人让位:不这样做的话,4 个**空闲**的池能把名额攥到
      // 后来的池自己的 deadline 到期 —— 那不是排队,是拿闲置线程换一次假超时
      nudgeIdlePools(1)
    })
  }

  /** 归还一个名额。★ 只在「worker 真的 exit」时调用,所以上限是真的。 */
  #releaseProcessWorker(): void {
    if (this.heldWorkers === 0) return
    this.heldWorkers -= 1
    handOverProcessWorker()
  }

  #spawn(): Slot {
    const worker = new Worker(this.source, {
      eval: true,
      // ★ 不继承父进程的 node 参数:debug 参数会抢端口,TS loader / --import 在
      // eval worker 里没有解析器可用 —— 继承它们的结果是「某些机器上 Grep 一律失败」
      execArgv: [],
      resourceLimits: {
        maxOldGenerationSizeMb: WORKER_MAX_OLD_GEN_MB,
        maxYoungGenerationSizeMb: WORKER_MAX_YOUNG_GEN_MB
      }
    })
    const slot = new Slot(worker)
    this.slots.push(slot)
    this.stats.spawned++
    // 先装物理退出监听：后续接线即使失败，dispose 也能等到退出并归还名额。
    worker.on('exit', (code: number) => {
      this.#onExit(slot, code)
    })
    worker.on('error', (error: Error) => {
      this.#onError(slot, error)
    })
    worker.on('message', (m: unknown) => {
      this.#onMessage(slot, m as WorkerReply)
    })
    // ★ 反序列化失败走的是 `messageerror`,不是 `error`:两个都要按「这个线程不可信」
    // 处理,否则一条坏消息会让 slot 停在一个死标记却还活着的状态上
    worker.on('messageerror', (error: Error) => {
      this.#onError(slot, error)
    })
    this.opts.onEvent?.(`Grep regex worker ${String(this.stats.spawned)} started`)
    return slot
  }

  #release(slot: Slot): void {
    if (slot.dead) {
      this.#pump()
      return
    }
    slot.current = null
    // 防重复:slot 只该在 idle 里出现一次,否则并发上限失效
    if (!this.idle.includes(slot)) this.idle.push(slot)
    this.#pump()
  }

  /*
    只负责结算这一个请求。把 slot 放回空闲、继续消化队列统一由 `#release` 做 ——
    两处都放回就会让同一个 slot 在 idle 里出现两次,并发上限当场失效。
  */
  #onMessage(slot: Slot, m: WorkerReply): void {
    const p = slot.current
    if (p === null || p.id !== m.id) return
    slot.current = null
    // 先放回再结算:结算里可能是 `#expire()`(worker 自己报 timedOut),那条路会
    // terminate 全部 worker,包括刚刚放回的这个
    this.#release(slot)
    p.resolve(m)
  }

  #onError(slot: Slot, error: Error): void {
    const p = slot.current
    slot.current = null
    /*
      ★ `error` / `messageerror` 之后**不保证**还有 `exit`:只 `#bury` 的话,这个
      线程会停在一个「死标记但物理还活着」的状态上,占着 256MB 堆也占着名额。
      所以走和到期同一条路 —— 真的 terminate,名额仍由 `#onExit` 归还(物理退出的凭据)。
    */
    this.#beginTerminate(slot)
    if (p !== null) p.reject(error)
    this.#pump()
  }

  #onExit(slot: Slot, code: number): void {
    this.stats.exited++
    // ★ 物理退出,到这里才归还进程级名额(且只归还一次)
    if (slot.holdsToken) {
      slot.holdsToken = false
      this.#releaseProcessWorker()
    }
    const p = slot.current
    slot.current = null
    this.#bury(slot)
    slot.markExited()
    if (p !== null) p.reject(this.#stopError(code, false))
    this.#pump()
  }

  /** 从池的使用列表里摘掉。★ 不在这里还名额 —— 那要等 `exit`。 */
  #bury(slot: Slot): void {
    slot.dead = true
    const i = this.slots.indexOf(slot)
    if (i !== -1) this.slots.splice(i, 1)
    const j = this.idle.indexOf(slot)
    if (j !== -1) this.idle.splice(j, 1)
  }

  /**
   * 一个 worker 意外退出 / 被本池 terminate 时,给它当前那个请求的结算值。
   * `probe` 为真时只是问「池停了吗」,返回 `null` 表示还能收活。
   */
  #stopError(code: number, probe: boolean): unknown | null {
    if (this.stopped === 'abort') return abortError()
    if (this.stopped === 'timeout') return new RegexBudgetError()
    if (this.stopped === 'dispose') return new Error('regex pool disposed')
    /*
      ★ 起不来之后池里没有可用的执行位置:后来的 submit 必须**立刻**以根因失败,
      而不是排进一个永远不会被派发的队列、等到自己的 deadline 才报一个假超时。
    */
    if (this.spawnFailed) {
      return this.spawnError ?? new RegexWorkerError('Grep regex worker failed to start')
    }
    if (probe) return null
    return new Error(`regex worker exited unexpectedly (code ${String(code)})`)
  }

  /** 发出 terminate 并登记「真的退了」的 promise。同步返回,退出在后台完成。 */
  #beginTerminate(slot: Slot): void {
    if (slot.dead) return
    slot.dead = true
    const i = this.slots.indexOf(slot)
    if (i !== -1) this.slots.splice(i, 1)
    const j = this.idle.indexOf(slot)
    if (j !== -1) this.idle.splice(j, 1)
    this.stats.terminated++
    this.opts.onEvent?.('Grep regex worker terminated')
    // terminate 本身也可能同步抛(线程已经没了之类):那也要算「它已经不在了」,
    // 绝不能让它冒出去变成一个没人在等的 rejection
    let terminated: Promise<void>
    try {
      terminated = slot.worker.terminate().then(
        () => undefined,
        () => undefined
      )
    } catch {
      terminated = Promise.resolve()
    }
    const settled = Promise.all([slot.exit, terminated]).then(() => undefined)
    this.terminations.add(settled)
    void settled.then(() => {
      this.terminations.delete(settled)
    })
  }

  #terminateAll(): void {
    for (const slot of [...this.slots]) this.#beginTerminate(slot)
  }

  /** 到期:结算所有 pending,强杀所有 worker —— 调用方据此返回部分结果 + 说明。 */
  #expire(): void {
    if (this.stopped !== null) return
    this.stopped = 'timeout'
    if (this.timer !== null) {
      clearTimeout(this.timer)
      this.timer = null
    }
    this.waiter?.abandon()
    this.#failPending(new RegexBudgetError())
    this.#terminateAll()
  }

  #failPending(error: unknown): void {
    for (const job of this.queue.splice(0)) job.fail(error)
    // ★ 兜底:手里还攥着的(已经发给 worker、还在等的)也要结算,不能留永久 pending
    for (const job of [...this.pending]) job.fail(error)
    // slot.current 的结算就是上面那一遍(它的 job 一定在 pending 里);这里只把
    // 槽位标记成「不欠结果了」,免得下面的 terminate 路径再纠结一次
    for (const slot of this.slots) slot.current = null
  }
}
