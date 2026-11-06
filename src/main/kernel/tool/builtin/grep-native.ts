/**
 * `Grep` 的**原生后端** —— 优先交给机器上现成的搜索程序,JS 引擎只做最后的兜底。
 *
 * 优先级:`rg` > `git grep` > `grep` > JS(`grep-regex.ts` 那条隔离池)。
 *
 * 需求:JS 实现要在内核里自己遍历、逐文件读进来、再丢进 worker 匹配,大仓库上
 * 光 IO 就能烧掉整个 5 秒预算;而 ripgrep 一类工具本来就是为这件事写的,快一两个
 * 数量级,并且跑在**独立进程**里 —— 灾难性回溯卡住的是它自己,超时由 spawn 整树杀掉,
 * 主线程永远不碰模型给的正则。
 *
 * ## 契约
 *
 * - **只负责「哪些文件的哪些行命中」**。行正文、上下文、三种输出模式的渲染都还在
 *   `search.ts`,所以四个后端的输出格式逐字节同形。
 * - 原生程序的「没装 / 不支持这个参数 / 方言不认这个正则 / 跑挂了」一律是
 *   `unavailable`,调用方顺延到下一个后端,最后落到 JS。★ 绝不把一次失败当成
 *   「没有命中」—— 那会给出一个很像是真的「仓库里没有」。
 * - 固定忽略表(`ignore.ts`)在原生侧**尽量**下推,`search.ts` 那边还会再按同一张表
 *   过滤一遍 —— 下推只是为了少搜,正确性靠后过滤。rg / git grep 另外遵守 `.gitignore`
 *   (这是它们的原生默认,也是用户要的语义)。
 * - 文件名过滤同理:能下推的只有「与根无关的 basename 模式」,而且下推的永远是
 *   **超集**(大小写不敏感、任意深度),精确判定由调用方拿 `compileGlob` 再做一遍。
 *
 * ★ 命令走 `host.spawn`,也就是**用户的 shell**(本地 zsh / cmd / pwsh,或 SSH 那头的
 * shell)。参数一律经 `quoteShellArg` 按实际方言引号化;cmd 下引不了的字符(`!` `%` `|` …)
 * 会让 `quoteShellArg` 抛错 —— 必需参数引不了就整个后端顺延,可选参数(忽略表下推)
 * 引不了就丢掉,靠后过滤兜住。
 */
import { basename, dirname, join } from 'node:path'
import { abortError } from '../../abort'
import { agentShell, quoteShellArg } from '../../node-spawn'
import type { ToolContext, ToolHost } from '../registry'
import { DEFAULT_SKIP_DIRS, SKIP_FILES } from './ignore'

export type NativeBackend = 'rg' | 'git' | 'grep'

/** 探测结果。grep 的方言:GNU 有 `-P`(PCRE),BSD 只有 `-E` */
interface Probe {
  rg: boolean
  git: boolean
  grep: 'P' | 'E' | null
}

/** 探测一个程序在不在,单次上限。只在每个宿主第一次 Grep 时付一次 */
const PROBE_TIMEOUT_MS = 3000
/** 与 `node-spawn.ts` 的 `MAX_STREAM_CHARS` 同值:stdout 撑到这么长就当被截断了 */
const SPAWN_OUTPUT_CAP = 512 * 1024
/** `node-spawn.ts` 的超时退出码 */
const TIMEOUT_CODE = 124
/** 与 JS 后端同一个单文件 5MB 上限 —— 只有 rg 能原生表达,其余两个靠不住也无妨 */
const MAX_FILESIZE = '5M'
/** 与 `glob-match.ts` 的判据一致 */
const CASE_SENSITIVE_FS = !(process.platform === 'darwin' || process.platform === 'win32')

/**
 * 每个宿主探测一次。★ 键是宿主对象:本地与各个 SSH 工作区装的东西不一样,
 * 不能共用一份答案。用 WeakMap,工作区关掉后跟着宿主一起回收。
 */
const probes = new WeakMap<ToolHost, Promise<Probe>>()

/** 仅供测试:限定可用的原生后端(`[]` = 一律走 JS)。生产路径恒为 `null` */
let backendsOverride: readonly NativeBackend[] | null = null
export function setGrepBackendsForTest(backends: readonly NativeBackend[] | null): void {
  backendsOverride = backends
}

export interface NativeGrepRequest {
  pattern: string
  ignoreCase: boolean
  multiline: boolean
  /** 搜索起点的绝对路径 */
  target: string
  isDir: boolean
  /**
   * 可下推的文件名模式(basename、已展开花括号),`null` = 不下推。
   * 由调用方保证它是精确过滤的**超集**。
   */
  nameHints: string[] | null
  maxPerFile: number
  timeoutMs: number
}

export type NativeGrepOutcome =
  | {
      kind: 'ok'
      backend: NativeBackend
      /** 绝对路径 → 命中行号(升序、去重) */
      hits: Map<string, number[]>
      /** 原生进程超时被杀,结果是部分的 */
      timedOut: boolean
      /** stdout 撑到了 spawn 的上限,结果是部分的 */
      truncated: boolean
    }
  | {
      kind: 'unavailable'
      /** rg 明确说正则不合法时的原话 —— JS 也编译失败时拿它报给模型 */
      regexError?: string
    }

function shellOf(host: ToolHost): string {
  return host.platform?.shell ?? agentShell()
}

/** 必需参数:引不了就抛,整个后端顺延 */
function command(shell: string, argv: readonly string[]): string {
  return argv.map((a) => quoteShellArg(shell, a)).join(' ')
}

/** 可选参数:引不了的直接丢掉(只是少下推一点,后过滤兜底) */
function optional(shell: string, argv: readonly string[]): string[] {
  return argv.filter((a) => {
    try {
      quoteShellArg(shell, a)
      return true
    } catch {
      return false
    }
  })
}

async function probe(ctx: ToolContext, cwd: string): Promise<Probe> {
  // ★ 探测不挂 ctx.signal:结果要缓存给后面所有调用,一次中断不该把「没装」写进缓存
  const signal = new AbortController().signal
  const run = (cmd: string): Promise<{ code: number; stdout: string } | null> =>
    ctx.host.spawn(cmd, { cwd, signal, timeoutMs: PROBE_TIMEOUT_MS }).catch(() => null)
  const [rg, git, grep] = await Promise.all([run('rg --version'), run('git --version'), run('grep --version')])
  return {
    rg: rg?.code === 0,
    git: git?.code === 0,
    grep: grep?.code === 0 ? (/GNU grep/.test(grep.stdout) ? 'P' : 'E') : null
  }
}

function probeOnce(ctx: ToolContext, cwd: string): Promise<Probe> {
  let p = probes.get(ctx.host)
  if (p === undefined) {
    p = probe(ctx, cwd)
    probes.set(ctx.host, p)
  }
  return p
}

function dirnameOf(ctx: ToolContext, p: string): string {
  return ctx.host.path ? ctx.host.path.dirname(p) : dirname(p)
}

function basenameOf(ctx: ToolContext, p: string): string {
  return ctx.host.path ? ctx.host.path.basename(p) : basename(p)
}

function joinOf(ctx: ToolContext, a: string, b: string): string {
  return ctx.host.path ? ctx.host.path.join(a, b) : join(a, b)
}

/**
 * 解析 `path\0行号<分隔>正文` 一行一条的输出。rg / grep 的分隔是 `:`,
 * git grep 的 `-z` 把它也换成了 `\0` —— 两种都只取行号前的数字。
 */
function parse(ctx: ToolContext, cwd: string, stdout: string): Map<string, number[]> {
  const byFile = new Map<string, Set<number>>()
  for (const rec of stdout.split('\n')) {
    const nul = rec.indexOf('\0')
    if (nul <= 0) continue
    const m = /^(\d+)/.exec(rec.slice(nul + 1))
    if (m === null) continue
    let p = rec.slice(0, nul)
    while (p.startsWith('./') || p.startsWith('.\\')) p = p.slice(2)
    const abs = joinOf(ctx, cwd, p)
    let set = byFile.get(abs)
    if (set === undefined) {
      set = new Set()
      byFile.set(abs, set)
    }
    set.add(Number(m[1]))
  }
  const out = new Map<string, number[]>()
  for (const [abs, set] of byFile) out.set(abs, [...set].sort((a, b) => a - b))
  return out
}

interface Plan {
  backend: NativeBackend
  cmd: string
}

function rgCommand(shell: string, req: NativeGrepRequest, operand: string): string {
  const skips = req.isDir
    ? optional(shell, [...DEFAULT_SKIP_DIRS, ...SKIP_FILES].map((n) => `!${n}`)).flatMap((g) => ['--glob', g])
    : []
  const hints = (req.nameHints ?? []).flatMap((h) => ['--iglob', h])
  return command(shell, [
    'rg',
    '--no-config',
    '--null',
    '--line-number',
    '--with-filename',
    '--no-heading',
    '--color', 'never',
    '--no-messages',
    // 与 JS 后端一致:隐藏文件也搜(`.git` 等由忽略表挡掉)
    '--hidden',
    // 只要行号,正文由 search.ts 自己读 —— 压住 stdout,别让一行压缩产物吃掉 512KB 上限
    '--max-columns', '1',
    '--max-filesize', MAX_FILESIZE,
    '--max-count', String(req.maxPerFile),
    // ★ 有序输出:结果可复现,而且被截断时拿到的是一个**前缀**,不是随机子集
    '--sort', 'path',
    // 默认 Rust 引擎不认的写法(环视、反向引用)自动切 PCRE2
    '--engine', 'auto',
    ...(req.ignoreCase ? ['-i'] : []),
    ...(req.multiline ? ['-U', '--multiline-dotall'] : []),
    ...skips,
    ...hints,
    '-e', req.pattern,
    '--',
    operand
  ])
}

function gitCommand(shell: string, req: NativeGrepRequest, operand: string): string {
  const scope = req.isDir && req.nameHints !== null
    ? req.nameHints.map((h) => `:(glob,icase)**/${h}`)
    : [operand]
  const skips = req.isDir
    ? optional(shell, [...DEFAULT_SKIP_DIRS].map((d) => `:(exclude,glob)**/${d}/**`))
    : []
  return command(shell, [
    'git',
    '-c', 'core.quotepath=off',
    'grep',
    '--no-color',
    '-n',
    '-z',
    '-I',
    // 未跟踪但没被忽略的文件也搜 —— 新建还没 add 的文件不该「搜不到」
    '--untracked',
    '-P',
    ...(req.ignoreCase ? ['-i'] : []),
    '-e', req.pattern,
    '--',
    ...scope,
    ...skips
  ])
}

function grepCommand(shell: string, req: NativeGrepRequest, operand: string, flavor: 'P' | 'E'): string {
  const skips = req.isDir
    ? [...[...DEFAULT_SKIP_DIRS].map((d) => `--exclude-dir=${d}`), ...[...SKIP_FILES].map((f) => `--exclude=${f}`)]
    : []
  // ★ `--include` 区分大小写,而 `compileGlob` 在 macOS / Windows 上不区分 —— 那里下推就不再是超集
  const hints = req.isDir && CASE_SENSITIVE_FS ? (req.nameHints ?? []).map((h) => `--include=${h}`) : []
  return command(shell, [
    'grep',
    '-r',
    '-n',
    '-H',
    '-I',
    '-s',
    '--null',
    '--color=never',
    `-${flavor}`,
    ...(req.ignoreCase ? ['-i'] : []),
    '-m', String(req.maxPerFile),
    ...optional(shell, skips),
    ...hints,
    '-e', req.pattern,
    '--',
    operand
  ])
}

/** 起点在 git 工作树里、且自己没被 .gitignore 掉 —— 否则 git grep 会静默地什么都不搜 */
async function gitEligible(ctx: ToolContext, cwd: string, operand: string, shell: string): Promise<boolean> {
  const inside = await ctx.host
    .spawn('git rev-parse --is-inside-work-tree', { cwd, signal: ctx.signal, timeoutMs: PROBE_TIMEOUT_MS })
    .catch(() => null)
  if (inside?.code !== 0 || inside.stdout.trim() !== 'true') return false
  let check: string
  try {
    check = command(shell, ['git', 'check-ignore', '-q', '--', operand])
  } catch {
    return false
  }
  const ignored = await ctx.host.spawn(check, { cwd, signal: ctx.signal, timeoutMs: PROBE_TIMEOUT_MS }).catch(() => null)
  // 0 = 被忽略,1 = 没被忽略,其余 = 说不清,一律不用
  return ignored?.code === 1
}

/** rg 的正则编译错误。只认 rg 的,grep 家族的报错格式五花八门,不值得猜 */
function rgRegexError(stderr: string): string | undefined {
  return /regex parse error|error parsing regex|PCRE2: error compiling pattern/i.test(stderr)
    ? stderr.trim()
    : undefined
}

/**
 * 按优先级挨个试原生后端。全都用不了时返回 `unavailable`,调用方走 JS。
 *
 * ★ 中断(`ctx.signal`)原样往上抛,不顺延 —— 用户按了停止,不该再换个后端重跑一遍。
 */
export async function runNativeGrep(ctx: ToolContext, req: NativeGrepRequest): Promise<NativeGrepOutcome> {
  if (backendsOverride !== null && backendsOverride.length === 0) return { kind: 'unavailable' }
  const cwd = req.isDir ? req.target : dirnameOf(ctx, req.target)
  const operand = req.isDir ? '.' : basenameOf(ctx, req.target)
  const shell = shellOf(ctx.host)
  const allowed = (b: NativeBackend): boolean => backendsOverride === null || backendsOverride.includes(b)

  const found = await probeOnce(ctx, cwd)
  const plans: Array<() => Promise<Plan | null>> = []
  // ★ git grep / grep 都没有跨行匹配,multiline 只有 rg 能接
  if (found.rg && allowed('rg')) plans.push(async () => ({ backend: 'rg', cmd: rgCommand(shell, req, operand) }))
  if (!req.multiline && found.git && allowed('git')) {
    plans.push(async () =>
      (await gitEligible(ctx, cwd, operand, shell)) ? { backend: 'git', cmd: gitCommand(shell, req, operand) } : null
    )
  }
  const grepFlavor = found.grep
  if (!req.multiline && grepFlavor !== null && allowed('grep')) {
    plans.push(async () => ({ backend: 'grep', cmd: grepCommand(shell, req, operand, grepFlavor) }))
  }

  let regexError: string | undefined
  for (const next of plans) {
    let plan: Plan | null
    try {
      plan = await next()
    } catch (error) {
      if (ctx.signal.aborted) throw abortError()
      // quoteShellArg 引不了(cmd 下的特殊字符)→ 这个后端用不了
      ctx.host.logger.debug(`grep: ${String(error)}`)
      continue
    }
    if (plan === null) continue
    let r: { code: number; stdout: string; stderr: string }
    try {
      r = await ctx.host.spawn(plan.cmd, { cwd, signal: ctx.signal, timeoutMs: req.timeoutMs })
    } catch (error) {
      if (ctx.signal.aborted) throw abortError()
      ctx.host.logger.debug(`grep: ${plan.backend} failed to start: ${String(error)}`)
      continue
    }
    if (ctx.signal.aborted) throw abortError()

    const timedOut = r.code === TIMEOUT_CODE
    const hits = parse(ctx, cwd, r.stdout)
    /*
      ★ 有输出就算数,不论退出码:rg / grep 遇到读不了的文件会以 2 退出,但已经输出的
      命中是真的。没有输出时只认 0/1 是「确实没命中」,其余都是这个后端出了事 —— 顺延。
    */
    if (hits.size > 0 || timedOut || r.code === 1 || r.code === 0) {
      if (hits.size === 0 && r.code === 0 && !timedOut) {
        // 退出码说有命中却解析不出一条 —— 输出格式对不上(比如被别名/包装改过),不信它
        ctx.host.logger.debug(`grep: ${plan.backend} exited 0 with unparsable output`)
        continue
      }
      return {
        kind: 'ok',
        backend: plan.backend,
        hits,
        timedOut,
        truncated: r.stdout.length >= SPAWN_OUTPUT_CAP
      }
    }
    if (plan.backend === 'rg') regexError = rgRegexError(r.stderr)
    ctx.host.logger.debug(`grep: ${plan.backend} exited ${String(r.code)}: ${r.stderr.slice(0, 500)}`)
  }
  return regexError === undefined ? { kind: 'unavailable' } : { kind: 'unavailable', regexError }
}

/**
 * 从 `glob` 参数里抽出能安全下推给原生程序的 basename 模式。
 *
 * 只认两种形状:不含 `/` 的(如 `*.ts`),和 `**\/` 开头、后面不再含 `/` 的。
 * 原生侧按「任意深度、大小写不敏感」匹配它们,于是下推结果必然是 `compileGlob`
 * 精确语义的**超集**;其余形状(`src/**\/*.ts` 这种和搜索根有关的)一律不下推。
 * 花括号展开一层(`*.{ts,tsx}` → `*.ts` `*.tsx`),因为 grep / git 的 pathspec 不认它。
 */
export function nativeNameHints(glob: string): string[] | null {
  const tail = glob.startsWith('**/') ? glob.slice(3) : glob
  if (tail === '' || tail.includes('/') || tail.includes('\\')) return null
  const m = /^([^{}]*)\{([^{}]*)\}([^{}]*)$/.exec(tail)
  if (m !== null) return (m[2] ?? '').split(',').map((alt) => `${m[1] ?? ''}${alt}${m[3] ?? ''}`)
  return /[{}]/.test(tail) ? null : [tail]
}
