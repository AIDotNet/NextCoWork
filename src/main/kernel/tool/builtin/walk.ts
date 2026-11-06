/**
 * 目录遍历 —— `glob` / `grep` 共用的那一趟走。
 *
 * ★ **不做成 `KernelHost` 的端口**。遍历带着忽略规则,而忽略规则是**策略**;
 * `KernelHost` 是**能力**。一旦 `walk` 端口开始长 `ignore` 参数,端口就变成了
 * 半个业务层,而「换一套忽略规则」这种事就得去改宿主。
 *
 * ★ `root` 是**这一趟的基点**,不一定是工作区根:模型指到工作区外的目录时,
 * 基点就换成那个目录(见 `paths.ts` 的 `walkBaseOf`)。`rel` 一律相对 `root` 算。
 *
 * 这里有三道闸门,每一道都对应一种真实的挂死方式:
 *
 * 1. **软链成环**。`a/link -> ..` 在词法上完全正常,遍历会无限深下去。
 *    按目录的 realpath 去重,进过的目录不再进。
 * 2. **软链出界**。`ws/link -> /` 会让 grep 把用户的整个磁盘搜一遍并塞进上下文。
 *    每个要进的目录都过一次 `resolveInWorkspace`(基点是这一趟的 `root`),
 *    出了这一趟范围的直接跳过 —— 不是报错,因为一条这样的软链不该让整次搜索失败。
 *    注意这**不是**权限边界:模型可以直接把 `root` 指到那个目录去。它拦的是
 *    「你只要了一个目录,却顺着一条链把半个磁盘搜了」。
 * 3. **量与时间**。`maxEntries` / `maxDepth` / `deadlineMs` 三条都必须有,
 *    而且超了要**照实说**(`truncated` / `timedOut`)—— 静默的部分结果
 *    会让模型断言「仓库里没有」。
 */
import { join } from 'node:path'
import { abortError } from '../../abort'
import type { KernelFs, WorkspacePaths } from '../../host'
import { PathEscapeError, resolveInWorkspace } from '../path-guard'

export interface WalkEntry {
  /** 相对这一趟的 `root`、始终用 `/` 分隔 —— 和 `FileEntry.path` 同一种形式 */
  rel: string
  abs: string
  isDir: boolean
}

export interface WalkOptions {
  fs: Pick<KernelFs, 'readDir'>
  path?: WorkspacePaths
  /** 这一趟的基点(工作区根,或模型指定的那个工作区外的目录)。必须是已经 realpath 过的。 */
  root: string
  /** 从基点下的哪个子目录开始。`''` = 基点本身。 */
  start?: string
  signal: AbortSignal
  now(): number
  /** 收集到多少项就停。默认 20000。 */
  maxEntries?: number
  /** 相对 `start` 的最大深度。默认 32 —— 够深,又挡得住软链没查到的怪环。 */
  maxDepth?: number
  /** 墙钟预算,毫秒。默认 5000。 */
  deadlineMs?: number
  /** 返回 true 就跳过。默认用 `ignore.ts` 的表。 */
  skip?(name: string, isDir: boolean): boolean
}

export interface WalkResult {
  entries: WalkEntry[]
  /** 撞上 `maxEntries` 或 `maxDepth` */
  truncated: boolean
  /** 撞上 `deadlineMs` */
  timedOut: boolean
}

const DEFAULT_MAX_ENTRIES = 20_000
const DEFAULT_MAX_DEPTH = 32
const DEFAULT_DEADLINE_MS = 5_000
/** 每处理这么多项查一次中断与时间。太密会拖慢,太疏会让停止按钮迟钝。 */
const CHECK_EVERY = 128
/**
 * 同时在列的目录数,以及同时在做 realpath 的子目录数。
 *
 * 需求:SSH 工作区上每次 `readDir` / `resolveWithin` 都是网络往返(后者还是两次),
 * 原先逐个目录串行等,几百个目录就能吃掉 1 秒以上,而 `Grep` 的 5 秒预算是
 * 遍历和读文件**共用**的 —— 遍历慢了,留给读文件的时间就少,结果就被截断。
 */
const WALK_CONCURRENCY = 8

/** 这些错误码 = 这一个目录没法进(没权限 / 刚被删掉 / 断链),跳过它,不让整次遍历失败 */
const SKIPPABLE_CODES = ['ENOENT', 'ENOTDIR', 'EACCES', 'EPERM']

type Listing = { ok: true; items: Array<{ name: string; isDir: boolean }> } | { ok: false; error: unknown }

/**
 * ★ 把异常装进返回值,而不是让 Promise reject。列表是**预取**的:
 * 遍历中途因为超时 / 截断提前 return 时,还在途的预取没人 await,
 * 一个 reject 的预取就成了 unhandled rejection。
 */
async function listDir(fs: WalkOptions['fs'], abs: string): Promise<Listing> {
  try {
    return { ok: true, items: await fs.readDir(abs) }
  } catch (error) {
    return { ok: false, error }
  }
}

/**
 * 以 `limit` 路并发对 `items` 逐个跑 `fn`,结果**按原顺序**返回。
 * 有一个抛错就让其余 worker 停止领取新项,等在途的收尾后再抛 —— 不留后台请求。
 */
async function mapOrdered<T, R>(items: readonly T[], limit: number, fn: (item: T) => Promise<R>): Promise<R[]> {
  const out = new Array<R>(items.length)
  let next = 0
  let failure: { error: unknown } | null = null
  const worker = async (): Promise<void> => {
    while (next < items.length && failure === null) {
      const i = next++
      try {
        out[i] = await fn(items[i] as T)
      } catch (error) {
        failure ??= { error }
        return
      }
    }
  }
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, worker))
  if (failure !== null) throw (failure as { error: unknown }).error
  return out
}

/**
 * 广度优先。
 *
 * ★ BFS 而不是 DFS,是因为**截断的语义不一样**:撞上 `maxEntries` 时,
 * BFS 留下的是靠近根的那些文件(通常正是模型想要的),DFS 留下的是
 * 第一棵子树钻到底的那一串 —— 用户搜 `*.ts` 拿回来的可能全是
 * `packages/a/src/...` 下的,而根目录的 `index.ts` 一个都没有。
 *
 * 目录列表与子目录的 realpath 是**并发取、按顺序用**的(见 `WALK_CONCURRENCY`):
 * `entries` 的顺序、截断落点、软链去重谁先谁后,都和串行时逐项一致。
 */
export async function walk(opts: WalkOptions): Promise<WalkResult> {
  const {
    fs,
    root,
    start = '',
    signal,
    now,
    maxEntries = DEFAULT_MAX_ENTRIES,
    maxDepth = DEFAULT_MAX_DEPTH,
    deadlineMs = DEFAULT_DEADLINE_MS,
    skip
  } = opts

  const deadline = now() + deadlineMs
  const entries: WalkEntry[] = []
  let truncated = false
  let timedOut = false
  let seen = 0

  /** 进过的目录(按 realpath)。软链成环时,第二次遇到同一个真实目录就停。 */
  const visited = new Set<string>()
  let frontier: Array<{ rel: string; abs: string; depth: number }> = []

  const joinPath = opts.path?.join ?? join
  const startAbs = start === '' ? root : joinPath(root, start)
  frontier.push({ rel: start, abs: startAbs, depth: 0 })
  visited.add(startAbs)

  /**
   * 子目录的 realpath;`null` = 这个子目录不进(越界软链 / 进不去)。
   *
   * ★ 只有**要往里走**的目录才付这次 realpath 的钱。
   * 放到每一项上做的话,一个 5 万文件的仓库要多 5 万次系统调用。
   */
  const realOf = async (rel: string): Promise<string | null> => {
    try {
      return opts.path ? await opts.path.resolveWithin(root, rel) : resolveInWorkspace(root, rel)
    } catch (err) {
      // 软链指到工作区外面。跳过它,但**不中止遍历** —— 一个越界的软链
      // 不该让「搜一下这个仓库」整体失败。
      if (err instanceof PathEscapeError) return null
      if (opts.path && !SKIPPABLE_CODES.includes(String((err as { code?: unknown })?.code))) throw err
      return null
    }
  }

  while (frontier.length > 0) {
    const level = frontier
    const next: typeof frontier = []

    // 滑动窗口预取:始终有 WALK_CONCURRENCY 个目录的列表在途,消费仍按顺序
    const listings: Array<Promise<Listing>> = []
    const prefetch = (i: number): void => {
      const d = level[i]
      if (d !== undefined) listings[i] = listDir(fs, d.abs)
    }
    for (let i = 0; i < WALK_CONCURRENCY; i++) prefetch(i)

    for (let di = 0; di < level.length; di++) {
      const dir = level[di] as (typeof level)[number]
      if (signal.aborted) throw abortError()
      if (now() > deadline) return { entries, truncated, timedOut: true }

      const listed = await (listings[di] as Promise<Listing>)
      prefetch(di + WALK_CONCURRENCY)
      if (!listed.ok) {
        const error = listed.error
        if (opts.path && !SKIPPABLE_CODES.includes(String((error as { code?: unknown })?.code))) throw error
        // 没权限 / 刚被删掉 / 是个断链。跳过这一个目录,不让整次遍历失败。
        continue
      }

      const subdirs: Array<{ rel: string; abs: string }> = []
      for (const item of listed.items) {
        if (++seen % CHECK_EVERY === 0) {
          if (signal.aborted) throw abortError()
          if (now() > deadline) {
            timedOut = true
            return { entries, truncated, timedOut }
          }
        }

        if (skip?.(item.name, item.isDir) === true) continue

        const rel = dir.rel === '' ? item.name : `${dir.rel}/${item.name}`
        const abs = joinPath(dir.abs, item.name)

        if (entries.length >= maxEntries) {
          truncated = true
          return { entries, truncated, timedOut }
        }
        entries.push({ rel, abs, isDir: item.isDir })

        if (!item.isDir) continue
        if (dir.depth + 1 >= maxDepth) {
          truncated = true
          continue
        }
        subdirs.push({ rel, abs })
      }

      // realpath 并发取,去重按列表顺序做 —— 两条软链指向同一目录时,留下的仍是排在前面那条
      const reals = await mapOrdered(subdirs, WALK_CONCURRENCY, (s) => realOf(s.rel))
      for (let si = 0; si < subdirs.length; si++) {
        const real = reals[si]
        const sub = subdirs[si] as (typeof subdirs)[number]
        if (real === null || real === undefined) continue
        if (visited.has(real)) continue
        visited.add(real)
        next.push({ rel: sub.rel, abs: sub.abs, depth: dir.depth + 1 })
      }
    }

    frontier = next
  }

  return { entries, truncated, timedOut }
}
