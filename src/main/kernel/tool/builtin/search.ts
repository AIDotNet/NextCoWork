/**
 * 搜索两件套:`Glob`(按文件名找)与 `Grep`(按内容找)。
 *
 * 名字、参数、描述结构全部对齐 Claude Code(`output_mode` / `-i` / `-n` /
 * `-A` / `-B` / `-C` / `head_limit` / `multiline` 都是 CC 的原参数名)。
 *
 * ## Grep 的后端:原生优先,JS 兜底
 *
 * 优先级 `rg` > `git grep` > `grep` > JS 引擎(见 `grep-native.ts`)。原生程序在宿主上
 * 经 `host.spawn` 跑,只交回「哪些文件的哪些行」,行正文、上下文与三种输出模式的渲染
 * 都在这里,所以换后端不换输出格式。任何一个原生后端用不了(没装、方言不认、多行匹配)
 * 都顺延到下一个,最后落到下面这条 JS 实现。
 *
 * ★ 描述按 **ripgrep 语法**写:它是首选后端,而 PCRE(`grep -P` / `git grep -P`)与
 * JS 在常用子集上和它一致。描述同时如实说出「可能落到 JS」,并给出可移植的子集 ——
 * 让模型按某一种引擎的独有写法去写,换到别的后端上就是「正则不合法」或静默的空结果。
 *
 * Glob 与 Grep 的 JS 路径都走 `walk.ts` 在内核侧自己遍历,而不是加一个 `KernelHost.walk`
 * 端口 —— 理由写在 `walk.ts` 的文件头:遍历带着忽略规则,忽略规则是策略,端口是能力。
 *
 * ## ★ Grep 的 ReDoS(JS 路径):正则**不在主线程跑**
 *
 * 正则是**模型给的**。原先直接 `new RegExp` 之后在主线程上匹配,一个嵌套无界量词
 * 就能让主进程彻底无响应 —— 单线程,V8 的匹配又是原子的:跑进一次灾难性回溯后
 * 连 abort 事件都发不出去,用户只能强杀。现在匹配整个搬进一个 `node:worker_threads`
 * 的 `eval` worker(见 `grep-regex.ts`),主线程只做两件便宜事,外加一堵墙:
 *
 * 0. **编译前静态筛查**(`redos.ts`)—— 嵌套无界量词直接拒绝,并给模型一句
 *    「改成什么」。★ 它只认识一个家族;交替式(如 ^(a|aa)+$ )和有界内层但外层
 *    无界的写法会漏过去,而漏过去的**不会再挂死**,因为——
 * 1. 所有编译与执行都在 worker 内,主线程从不跑模型给的正则;
 * 2. 每行先截到 `MAX_LINE_CHARS`、整次搜索一个**真实墙钟** deadline;到期就
 *    `terminate()` 所有 worker 并结算全部 pending 请求,返回**部分结果 + 超时说明**;
 * 3. 仍然每 N 个文件查一次 `ctx.signal`;
 * 4. 整次搜索另有一个墙钟预算,超了同样**返回部分结果并说明**。
 *
 * ★ 硬 deadline **必须走真实时钟**(`Date.now`),不看 `ctx.host.clock` —— 测试里
 * 的 clock 常常是假的,而安全兜底要在那种场景下照样生效。搜索自己的时间预算
 * 仍然走 `ctx.host.clock`(它要能被测试拨快拨慢)。
 *
 * ★ 线程与内存是**进程级**预算,不是一个池的事:池内 2 个 worker,所有池加起来
 * 物理存活的至多 4 个,排队请求至多 16 个(见 `grep-regex.ts` 的「资源预算」)。
 * 拿不到线程名额是**等着**,等自己这个池的真实 deadline —— 进程级满员绝不
 * 伪装成「这次搜索超时」,那会给出一个很像是真的「仓库里没有」。
 *
 * 对应的测试断言的是「**在 X 毫秒内返回**」,不是结果正确 —— 那是行为测试,
 * 改这个文件的时候别把它当成一条可以放宽的断言。
 */
import { relative } from 'node:path'
import { z } from 'zod'
import { toolFail, toolOk, type ToolResult } from '../../../../shared/agent/tool'
import { EnvironmentError, missingPath } from '../../../environment/errors'
import { defineTool } from '../define'
import type { ToolContext, ToolRegistration } from '../registry'
import { compileGlob, normalizeGlobPath } from './glob-match'
import { GREP_REGEX_LIMITS, GrepRegexPool, MAX_FILE_BYTES, RegexBudgetError } from './grep-regex'
import { nativeNameHints, runNativeGrep, type NativeGrepOutcome } from './grep-native'
import { literalPrefilter } from './grep-prefilter'
import { defaultSkip } from './ignore'
import { looksBinary, relOf, resolvePath, walkBaseOf, type WalkBase } from './paths'
import { redosRisk } from './redos'
import { walk } from './walk'

/** 结果条数上限。再多模型也读不完,只会挤掉上下文。 */
const MAX_GLOB_RESULTS = 200
const MAX_GREP_MATCHES = 200
/** 单行截断。压住良性但慢的模式和内存;**指数级回溯靠的是 `redos.ts`,不是它**。 */
const MAX_LINE_CHARS = 2000
/**
 * ★ `multiline: true` 时整份文件文本一次性进正则,单行截断这道防线就没了。
 * 所以另给一条更小的上限 —— 256KB 已经能覆盖几乎所有源码文件,
 * 而它把最坏情况的回溯规模也压回了可以接受的量级。
 */
const MAX_MULTILINE_CHARS = 256 * 1024
/**
 * 整次搜索的时间预算(毫秒),读 `ctx.host.clock`。
 *
 * ★ 这是**搜索自己的进度预算**,不是安全兜底 —— 安全兜底是 `grep-regex.ts` 里
 * 那个真实墙钟 deadline,它不管 clock 真假。两者分开,是因为测试会冻住 clock,
 * 而冻住的 clock 不该让危险正则跑满整个测试超时。
 */
const SEARCH_BUDGET_MS = 5000
/**
 * 隔离子代理的**安全墙钟**(真实时间,毫秒)。到期即 terminate 所有 worker、
 * 结算全部 pending 请求,返回部分结果 + 超时说明。
 *
 * ★ 与 `SEARCH_BUDGET_MS` 分开是因为两者的时钟来源不同:安全兜底量的是
 * `Date.now`,而搜索自己的时间预算量的是 `ctx.host.clock` —— 后者在测试里
 * 常常是冻住的,冻住的时钟不该让一个危险正则跑满整个测试超时。
 */
const REGEX_WALL_BUDGET_MS = 5000
/**
 * 同时在跑正则的 worker 上限 —— **一个池**的上限。
 *
 * ★ 一次 Grep 至多这么多线程,且**跨文件复用**(见 `grep-regex.ts` 的池)。
 * 单文件的匹配有它的正则与 `lastIndex` 状态,复用没问题;上限与
 * `GREP_CONCURRENCY` 相互独立 —— 前者是有界线程,后者只是有界并发读取。
 *
 * ★ 2 而不是 8:8 个 worker × 256MB 堆意味着**一次 Grep 就能吃掉 2GB**,
 * 而线程数对「一次仓库级 Grep」的收益远小于它对峰值内存的代价(worker 是复用的,
 * 起满之后瓶颈本来就在 IO 与正则本身)。真正的关系是**进程级**的:
 * 几个 Grep 同时跑时,所有池加起来物理存活的 worker 由 `grep-regex.ts` 的
 * `MAX_PROCESS_WORKERS`(4)统一发名额,拿不到名额的请求排队等待,
 * 而不是当场报超时 —— 见那个文件头的「资源预算」。
 */
const REGEX_MAX_WORKERS = 2
/**
 * 仅供测试注入一个更短的墙钟 —— 「危险正则必须在短 deadline 内退出」那条用例
 * 靠它把默认的 5 秒压到几十毫秒,否则只能靠拉大整套 timeout 来掩盖。
 * 生产路径永远不调用 setter,取值恒为 `REGEX_WALL_BUDGET_MS`。
 */
let regexWallBudgetMs = REGEX_WALL_BUDGET_MS
export function setGrepRegexDeadlineForTest(ms: number): void {
  regexWallBudgetMs = ms
}
export function resetGrepRegexDeadlineForTest(): void {
  regexWallBudgetMs = REGEX_WALL_BUDGET_MS
}
/**
 * 比这(磁盘字节)大的文件不进 Grep —— 多半是数据或产物。
 *
 * ★ 数值从池那边取,别在这里再抄一遍:同一份预算写在两个地方,早晚会分叉,
 * 而分叉的症状是「读取侧放行、池侧拒绝」这种对不上号的错误。池拿同一个数
 * 封它的入参长度(见 `grep-regex.ts` 的 `MAX_FILE_BYTES`:长度与磁盘字节数同口径)。
 */
const MAX_GREP_FILE_BYTES = MAX_FILE_BYTES
/**
 * 原生后端(`grep-native.ts`)单次进程的超时(真实时间,由 spawn 计)。
 *
 * ★ 比 JS 的 5 秒宽:rg 在大仓库上为了有序输出(`--sort path`)放弃了并行,
 * 而它即使这样也比 JS 那条路快得多 —— 宁可多等几秒拿全结果。超时同样返回部分结果 + 说明。
 */
const NATIVE_TIMEOUT_MS = 10_000
const SNIFF_BYTES = 4096
/**
 * 不大于这个的文件**一次读完**再嗅探;更大的先读 `SNIFF_BYTES` 嗅探、是文本才全量读。
 *
 * 需求:源码文件绝大多数在几十 KB 以内,原先「stat → 嗅探读 → 全量读」每个文件要付
 * 两次 open/close,是串行 IO 里最大的一块。而大文件里二进制(图片、包、数据库)
 * 占比高,为它们全量读几 MB 再丢掉不划算 —— 所以只对小文件合并成一次读。
 */
const SINGLE_READ_MAX_BYTES = 256 * 1024
/**
 * 同时在搜的文件数。
 *
 * 需求:原先逐个文件 `await`,IO 完全串行 —— 本地是 libuv 线程池空转,
 * SSH 工作区是每个文件 5~8 个网络往返首尾相接,几千个文件就把 5 秒预算烧完。
 * 上限同时也是内存上限:最坏这么多份文件内容同时在手里。
 *
 * ★ 它同时是**池的排队上限**:每一路并发都在池里挂着一个请求(正文驻留 JS 堆),
 * 所以读取路数不能超过 `MAX_QUEUED_REQUESTS` —— 超了的话,多出来的那几路会白读
 * 一个文件,然后拿一个「队列满」的错误。这里用 `min` 把这条耦合写成结构,
 * 而不是靠注释提醒(见 `grep-regex.ts` 的「资源预算」)。
 */
const GREP_CONCURRENCY = Math.min(16, GREP_REGEX_LIMITS.MAX_QUEUED_REQUESTS)
/** 每这么多个文件查一次中断 */
const SIGNAL_EVERY = 32

function rethrowRemoteFailure(error: unknown, ctx: ToolContext): void {
  if (error instanceof EnvironmentError || (ctx.host.remote && !missingPath(error))) throw error
}

/** 忽略表在两个工具的描述里都要提一句 —— 模型据此判断「没搜到」是不是可信 */
const IGNORE_NOTE =
  'Dependency and build directories (node_modules, .git, dist, out, build, target, coverage, …) are ' +
  'skipped automatically. This is a fixed list; .gitignore is NOT read. To search inside those ' +
  'directories, use Bash instead.'

/**
 * Grep 的忽略说明。★ 和 Glob 不同:rg / git grep 原生遵守 `.gitignore`,
 * 这一点必须告诉模型,否则它会把「被忽略的文件里没搜到」当成「仓库里没有」。
 */
const GREP_IGNORE_NOTE =
  'Dependency and build directories (node_modules, .git, dist, out, build, target, coverage, …) are ' +
  'always skipped. When ripgrep or git grep does the search, files ignored by .gitignore are skipped too. ' +
  'To search inside those, use Bash instead.'

/**
 * `type` 参数认识的语言别名。
 *
 * 和 ripgrep 的 `--type` 对齐了常用的那一批。★ 认不出的 type **要报错**,
 * 不能忽略后当成「不过滤」—— 那会让模型拿到一份范围完全不对的结果,
 * 而它没有任何办法察觉。
 */
const TYPE_GLOBS: Record<string, string> = {
  js: '**/*.{js,jsx,mjs,cjs}',
  ts: '**/*.{ts,tsx,mts,cts}',
  tsx: '**/*.tsx',
  jsx: '**/*.jsx',
  py: '**/*.{py,pyi}',
  go: '**/*.go',
  rust: '**/*.rs',
  java: '**/*.java',
  kotlin: '**/*.{kt,kts}',
  swift: '**/*.swift',
  c: '**/*.{c,h}',
  cpp: '**/*.{cc,cpp,cxx,hh,hpp,hxx}',
  cs: '**/*.cs',
  rb: '**/*.rb',
  php: '**/*.php',
  sh: '**/*.{sh,bash,zsh}',
  html: '**/*.{html,htm}',
  css: '**/*.{css,scss,sass,less}',
  vue: '**/*.vue',
  svelte: '**/*.svelte',
  json: '**/*.json',
  yaml: '**/*.{yaml,yml}',
  toml: '**/*.toml',
  xml: '**/*.xml',
  md: '**/*.{md,markdown}',
  sql: '**/*.sql'
}

// ────────────────────────────── Glob ──────────────────────────────

const GlobInput = z.object({
  pattern: z.string().min(1).describe('The glob pattern to match file paths against'),
  path: z
    .string()
    .optional()
    .describe(
      'Directory to search in (absolute path; it may be outside the workspace). Omit it to search the whole workspace. ' +
        'IMPORTANT: to use the default, OMIT this field entirely — do not pass "undefined" or "null"'
    )
})

export const globTool: ToolRegistration = defineTool({
  internalId: 'Glob',
  description:
    '- Fast file-pattern matching that works on any codebase size\n' +
    '- Supports glob patterns like "**/*.js" or "src/**/*.ts"\n' +
    '- Returns matching paths SORTED BY MODIFICATION TIME, most recent first\n' +
    '- Use this when you are looking for files by name; use Grep when you are looking for them by CONTENT\n' +
    '- For an open-ended search that may take several rounds of globbing and grepping, use Task to launch a subagent instead\n' +
    '- You can call multiple tools in one reply. Speculatively firing several searches at once beats trying one at a time\n' +
    '- Note that * does not cross directory boundaries: to find every .ts file in every subdirectory write "**/*.ts", not "*.ts"\n' +
    `- ${IGNORE_NOTE}`,
  schema: GlobInput,
  readOnly: true,
  destructive: false,
  needsNetwork: false,
  async run(input, ctx) {
    const r = await resolvePath(ctx, input.path ?? '')
    if (!r.ok) return r.result
    const walkBase = walkBaseOf(ctx, r)
    const base = walkBase.label

    const re = compileGlob(input.pattern)
    const res = await walk({
      fs: ctx.host.fs,
      path: ctx.host.path,
      root: walkBase.root,
      start: walkBase.start,
      signal: ctx.signal,
      now: () => ctx.host.clock.now(),
      deadlineMs: SEARCH_BUDGET_MS,
      skip: defaultSkip
    })

    const hits = res.entries.filter((e) => !e.isDir && re.test(normalizeGlobPath(e.rel)))
    if (hits.length === 0) {
      return toolOk(
        `No files match "${input.pattern}" under ${base}. ` +
          `Note that * does not cross directory boundaries — use **/ to descend.` +
          `${res.timedOut ? ' The traversal also timed out, so this result may be incomplete.' : ''}`
      )
    }

    // ★ 只对命中的文件 stat。对全部遍历结果 stat 的话,一个大仓库要多几万次系统调用
    const withTime = await Promise.all(
      hits.slice(0, MAX_GLOB_RESULTS * 4).map(async (e) => {
        const rel = walkBase.display(e.rel)
        try {
          return { rel, mtime: (await ctx.host.fs.stat(e.abs)).mtimeMs }
        } catch (error) {
          rethrowRemoteFailure(error, ctx)
          return { rel, mtime: 0 }
        }
      })
    )
    withTime.sort((a, b) => b.mtime - a.mtime || a.rel.localeCompare(b.rel))

    const shown = withTime.slice(0, MAX_GLOB_RESULTS)
    const notes: string[] = []
    if (hits.length > shown.length) {
      notes.push(
        `${String(hits.length)} files matched; listing the ${String(shown.length)} most recently modified`
      )
    }
    if (res.truncated) notes.push('traversal limit reached; some directories were not scanned')
    if (res.timedOut) notes.push('traversal timed out; the result may be incomplete')

    return toolOk(
      shown.map((h) => h.rel).join('\n') + (notes.length > 0 ? `\n\n[${notes.join('; ')}]` : '')
    )
  }
})

// ────────────────────────────── Grep ──────────────────────────────

const GrepInput = z.object({
  pattern: z.string().min(1).describe('The regular expression to search file contents for (JavaScript syntax)'),
  path: z
    .string()
    .optional()
    .describe('File or directory to search (absolute path; it may be outside the workspace). Omit it to search the whole workspace'),
  glob: z
    .string()
    .optional()
    .describe('Filter files by glob pattern, e.g. "*.js" or "**/*.{ts,tsx}". Mutually exclusive with type'),
  type: z
    .string()
    .optional()
    .describe(
      'Filter files by language type, e.g. "js", "py", "rust". Easier than glob and covers the common ' +
        'languages. Use glob when you need finer control'
    ),
  output_mode: z
    .enum(['content', 'files_with_matches', 'count'])
    .optional()
    .describe(
      'Output shape: "content" returns the matching lines (supports -A/-B/-C/-n/head_limit); ' +
        '"files_with_matches" returns just the file paths (default); "count" returns a match count per file'
    ),
  '-i': z.boolean().optional().describe('Case-insensitive matching'),
  '-n': z.boolean().optional().describe('Include line numbers in the output. Only used when output_mode is "content"'),
  '-A': z
    .number()
    .int()
    .min(0)
    .max(50)
    .optional()
    .describe('Lines of context to show after each match. Only used when output_mode is "content"'),
  '-B': z
    .number()
    .int()
    .min(0)
    .max(50)
    .optional()
    .describe('Lines of context to show before each match. Only used when output_mode is "content"'),
  '-C': z
    .number()
    .int()
    .min(0)
    .max(50)
    .optional()
    .describe('Lines of context to show on each side of a match. Only used when output_mode is "content"'),
  multiline: z
    .boolean()
    .optional()
    .describe(
      'Let the pattern match across line boundaries (. then matches newlines too). Defaults to false — ' +
        'by default matching happens within a single line'
    ),
  head_limit: z
    .number()
    .int()
    .min(1)
    .max(1000)
    .optional()
    .describe(
      'Keep only the first N results (lines, files, or count rows, depending on output_mode). Omit for the built-in cap'
    )
})

interface FileHits {
  rel: string
  /** 命中的行号,从 1 开始,升序 */
  lines: number[]
  /**
   * 该文件所有行(已按 MAX_LINE_CHARS 截断),content 模式取上下文要用。
   * ★ 只有调用方真的要行正文(`needText`)时才切;其它模式一律 `[]` ——
   * 命中路径 / 计数用不到行正文,替模式切一遍整文件是纯浪费。
   */
  text: string[]
}

/** 搜一个文件。`null` = 这个文件跳过(二进制/太大/读不了),不是「没命中」。 */
async function grepFile(
  ctx: ToolContext,
  abs: string,
  rel: string,
  /** 隔离执行池。★ 正则的编译与匹配全在里面,主线程从不碰模型给的 `RegExp` */
  pool: GrepRegexPool,
  /** 整文件字面量预筛,`null` = 正则里抽不出必经字面量,只能逐行扫 */
  prefilter: ((text: string) => boolean) | null,
  /**
   * 调用方是否要用行正文(`output_mode: 'content'`)。★ 传 false 时**绝不切行**:
   * 5MB 的单字符行文件能切出约 250 万个字符串,替一个只要行号的模式付这份代价
   * 是最坏那种浪费(宁可让 `text` 为空,也不在主线程上做一次没人读的整文件切分)。
   */
  needText: boolean
): Promise<FileHits | null> {
  const { fs } = ctx.host
  let size: number
  try {
    size = (await fs.stat(abs)).size
  } catch (error) {
    rethrowRemoteFailure(error, ctx)
    return null
  }
  if (size === 0 || size > MAX_GREP_FILE_BYTES) return null

  /*
    ★ 先嗅探再解码:探测在**字节**上做完才解码成字符串,二进制文件永远不会被
    解成一堆 U+FFFD。原先对每个文件都是「嗅探读 4KB → 再全量读」两趟;现在小文件
    (≤ SINGLE_READ_MAX_BYTES)合并成一趟读完、在内存里嗅探前 4KB —— 判据不变,
    省掉一次 open/close。大文件仍走两趟,理由见 `SINGLE_READ_MAX_BYTES`。
    ★ 读的长度是 `size` 而不是 `size + 1`:SFTP 的实现会为多出来的那 1 字节
    再发一次读请求等 EOF,每个文件白付一个网络往返。
  */
  let bytes: Uint8Array
  try {
    if (size <= SINGLE_READ_MAX_BYTES) {
      bytes = await fs.readFileBytes(abs, size)
      if (looksBinary(bytes.subarray(0, SNIFF_BYTES))) return null
    } else {
      if (looksBinary(await fs.readFileBytes(abs, SNIFF_BYTES))) return null
      bytes = await fs.readFileBytes(abs, size)
    }
  } catch (error) {
    rethrowRemoteFailure(error, ctx)
    return null
  }
  // 与 `fs.readFile(abs, 'utf8')` 同一种解码(不剥 BOM、坏字节换成 U+FFFD);零拷贝包一层
  const raw = Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength).toString('utf8')

  // 需求:整文件字面量预筛。绝大多数文件不含要找的标识符,直接跳过切行和逐行正则。
  // 预筛只许误报不许漏报,见 `grep-prefilter.ts` 的文件头。
  if (prefilter !== null && !prefilter(raw)) return { rel, lines: [], text: [] }

  /** 切行 + 截断。★ 截断在**匹配之前**。worker 里也是这个顺序,行号因此对得上 */
  const splitLines = (): string[] =>
    raw.split('\n').map((l) => (l.length > MAX_LINE_CHARS ? l.slice(0, MAX_LINE_CHARS) : l))

  /*
    ★ 匹配整个搬到隔离池里做。这里不再有 `re.lastIndex` 那段共享状态 ——
    worker 里每个文件是「重置 lastIndex → 用完它」中间没有 await 的一段,
    并发文件不再互相踩。超时 / 中止由池 reject:`RegexBudgetError` 走
    「部分结果 + 说明」,`AbortError` 原样往上抛(用户按了停止)。
  */
  const scan = await pool.scanFile(raw)
  return { rel, lines: scan.lines, text: needText && scan.lines.length > 0 ? splitLines() : [] }
}

interface ScanResult {
  /** 有命中的文件,**按遍历顺序**,累计命中条数到 MAX_GREP_MATCHES 为止 */
  hits: FileHits[]
  totalMatches: number
  /** 实际开搜过的文件数 —— 永远是 `files` 的一个前缀的长度 */
  scanned: number
  budgetHit: boolean
  /** 隔离池的真实墙钟 deadline 到期 —— 结果不完整,必须如实说 */
  timedOut: boolean
}

/**
 * 以 `GREP_CONCURRENCY` 路并发搜一批文件,结果按**原顺序**汇总。
 *
 * 需求:并发是为了速度(见 `GREP_CONCURRENCY`),但输出必须和串行时**逐字节一致**:
 * - 结果顺序 = 遍历顺序(BFS,靠近根的在前),不能是「谁先读完谁在前」——
 *   否则同一次搜索跑两遍结果顺序不同,模型会以为仓库变了;
 * - 撞上命中上限时截在哪个文件,也要和串行时一样。
 *
 * 做法:文件按下标**依次领取**,所以开搜过的永远是一个前缀 `[0, next)`;
 * 另外维护「已连续完成的前缀」上的累计命中数,它够了就不再领新文件。
 * 最后按下标顺序拼结果、在上限处截断 —— 多搜的最多 `GREP_CONCURRENCY - 1` 个文件被丢弃。
 *
 * ★ 某个文件抛错(远程断线,见 `rethrowRemoteFailure`)时让所有 worker 停手再抛,
 * 而不是 `Promise.all` 的「第一个错就返回」—— 后者会让其余 worker 在后台
 * 继续对一条已经断掉的连接发请求。
 */
async function scanFiles<F>(
  files: readonly F[],
  budget: number,
  ctx: ToolContext,
  searchOne: (file: F) => Promise<FileHits | null>
): Promise<ScanResult> {
  /** `undefined` = 还没搜完;`null` = 跳过或没命中 */
  const results: Array<FileHits | null | undefined> = new Array<FileHits | null | undefined>(files.length)
  let next = 0
  let completed = 0
  let prefixEnd = 0
  let prefixMatches = 0
  let budgetHit = false
  /** 隔离池的真实墙钟到期:不是错误,是「没扫完」 */
  let timedOut = false
  let failure: { error: unknown } | null = null

  const worker = async (): Promise<void> => {
    while (next < files.length && failure === null && !timedOut) {
      if (prefixMatches >= MAX_GREP_MATCHES) return
      // ★ 中断要能在搜索途中生效。不查的话,停止按钮要等整个仓库搜完才有反应
      if (ctx.signal.aborted) return
      if (ctx.host.clock.now() > budget) {
        budgetHit = true
        return
      }
      const index = next++
      let fh: FileHits | null
      try {
        fh = await searchOne(files[index] as F)
      } catch (error) {
        /*
          ★ 隔离池到期(`RegexBudgetError`)是**结果不完整**,不是工具失败:
          已有结果照常返回,再在说明里写清「没扫完」。把它当 failure 抛出去的话,
          模型拿到的是一个错误、一个命中都没有 —— 那比「部分结果 + 说明」更糟。
        */
        if (error instanceof RegexBudgetError) {
          timedOut = true
          return
        }
        failure ??= { error }
        return
      }
      results[index] = fh !== null && fh.lines.length > 0 ? fh : null
      if (++completed % SIGNAL_EVERY === 0) {
        ctx.emit({ callId: ctx.callId, message: `Searched ${String(completed)} files` })
      }
      while (prefixEnd < files.length && results[prefixEnd] !== undefined) {
        prefixMatches += results[prefixEnd]?.lines.length ?? 0
        prefixEnd++
      }
    }
  }

  await Promise.all(Array.from({ length: Math.min(GREP_CONCURRENCY, files.length) }, worker))
  if (failure !== null) throw (failure as { error: unknown }).error

  const hits: FileHits[] = []
  let totalMatches = 0
  for (let i = 0; i < next && totalMatches < MAX_GREP_MATCHES; i++) {
    const fh = results[i]
    if (fh === undefined || fh === null) continue
    hits.push(fh)
    totalMatches += fh.lines.length
  }
  return { hits, totalMatches, scanned: next, budgetHit, timedOut }
}

/** content 模式的渲染。格式对齐 ripgrep:命中行用 `:`,上下文行用 `-`。 */
function renderContent(
  hits: FileHits[],
  before: number,
  after: number,
  showLineNo: boolean
): string[] {
  const out: string[] = []
  for (const f of hits) {
    const wanted = new Set<number>()
    for (const ln of f.lines) {
      for (let i = ln - before; i <= ln + after; i++) {
        if (i >= 1 && i <= f.text.length) wanted.add(i)
      }
    }
    const isHit = new Set(f.lines)
    const ordered = [...wanted].sort((a, b) => a - b)
    let prev = 0
    for (const ln of ordered) {
      // 两段上下文之间断开时插一条 `--`,和 ripgrep 一样
      if (prev !== 0 && ln > prev + 1) out.push('--')
      const sep = isHit.has(ln) ? ':' : '-'
      const body = f.text[ln - 1] ?? ''
      out.push(showLineNo ? `${f.rel}${sep}${String(ln)}${sep}${body}` : `${f.rel}${sep}${body}`)
      prev = ln
    }
  }
  return out
}

export const grepTool: ToolRegistration = defineTool({
  internalId: 'Grep',
  description:
    'A powerful search tool for finding text inside files.\n\n' +
    'Usage:\n' +
    '- ALWAYS use Grep to search file contents. NEVER shell out to grep or rg through Bash — this tool ' +
    'applies the ignore list, skips binaries, and has a timeout budget that the shell versions do not\n' +
    '- Supports full regex syntax (e.g. "log.*Error", "function\\s+\\w+"). Pattern syntax is ripgrep style: ' +
    'escape literal braces (use "interface\\{\\}" to find "interface{}" in Go code)\n' +
    '- Runs on ripgrep when it is installed, otherwise git grep / grep, otherwise a built-in JavaScript regex engine. ' +
    'For portable results stick to the common subset: \\d \\w \\s \\b, character classes, groups, alternation, ' +
    'lazy quantifiers\n' +
    '- Filter files with glob (e.g. "*.js", "**/*.tsx") or with type by language (e.g. "js", "py")\n' +
    '- output_mode: "content" returns matching lines, "files_with_matches" returns just paths (DEFAULT), ' +
    '"count" returns a match count per file\n' +
    '- For an open-ended question that will take several rounds of searching, use Task to launch a subagent instead\n' +
    '- Matching is WITHIN A SINGLE LINE by default. Pass multiline: true to match across lines ' +
    '(e.g. "interface\\s+X[\\s\\S]*?field")\n' +
    '- Binary files are skipped; files over 5MB may be skipped\n' +
    `- ${GREP_IGNORE_NOTE}`,
  schema: GrepInput,
  readOnly: true,
  destructive: false,
  needsNetwork: false,
  async run(input, ctx) {
    const r = await resolvePath(ctx, input.path ?? '')
    if (!r.ok) return r.result

    const multiline = input.multiline === true

    // ★ 认不出的 type 要报错,不能当成「不过滤」—— 静默放宽范围是查不出来的错
    if (input.type !== undefined && TYPE_GLOBS[input.type] === undefined) {
      return toolFail(
        `Unknown type "${input.type}". Available: ${Object.keys(TYPE_GLOBS).join(', ')}. ` +
          `Or use the glob parameter and write the filename pattern directly.`
      )
    }

    const st = await ctx.host.fs.stat(r.abs).catch((error: unknown) => { rethrowRemoteFailure(error, ctx); return null })
    if (st === null) return toolFail(`Path does not exist: ${relOf(ctx, r.abs)}`)
    const walkBase = walkBaseOf(ctx, r)
    const base = walkBase.label

    const nameFilterPattern = input.glob ?? (input.type === undefined ? undefined : TYPE_GLOBS[input.type])
    const nameFilter = nameFilterPattern === undefined ? null : compileGlob(nameFilterPattern)
    const needText = input.output_mode === 'content'

    /*
      ★ 先交给原生程序(rg > git grep > grep,见 `grep-native.ts`)。它们跑在独立进程里,
      回溯卡死的是它们自己,超时由 spawn 整树杀掉 —— 所以下面那道 ReDoS 静态筛查
      只拦 JS 这条路。任何一个原生后端用不了都顺延,最后才落到 JS。
    */
    const native = await runNativeGrep(ctx, {
      pattern: input.pattern,
      ignoreCase: input['-i'] === true,
      multiline,
      target: r.abs,
      isDir: st.isDir,
      nameHints: nameFilterPattern === undefined ? null : nativeNameHints(nameFilterPattern),
      maxPerFile: MAX_GREP_MATCHES,
      timeoutMs: NATIVE_TIMEOUT_MS
    })
    if (native.kind === 'ok') {
      const nativeHits = await collectNativeHits(ctx, native, { abs: r.abs, isDir: st.isDir }, walkBase, nameFilter, needText)
      const notes: string[] = []
      if (nativeHits.totalMatches >= MAX_GREP_MATCHES) notes.push(matchCapNote())
      if (native.timedOut || native.truncated) {
        notes.push(
          `${native.backend} 搜索${native.timedOut ? `超过 ${String(NATIVE_TIMEOUT_MS)}ms 被中止` : '输出超过上限'},` +
            `结果不完整。请用 path、glob 或 type 缩小范围再搜一次`
        )
      }
      return formatGrepResult(
        input,
        nativeHits.hits,
        notes,
        notes.length > 0
          ? `No matches found under ${base}.\n[${notes.join('; ')}]`
          : `No content matching "${input.pattern}" under ${base}.`
      )
    }

    // ★ 真实墙钟从这里起算:遍历 + 匹配共用这一个安全预算,不是「遍历 5s 再匹配 5s」
    const wallStartMs = Date.now()

    /*
      ★ 静态筛查是一道**便宜的快速拒绝**:嵌套无界量词当场给模型一句「改成什么」。
      它只认识一个家族,交替式(如 ^(a|aa)+$ )和有界内层但外层无界的写法会漏过去 ——
      漏过去的由下面的隔离池兜底(见 `grep-regex.ts`)。所以这道闸**不再是唯一**
      承重那道,但保留它是因为对最常见的危险写法,「当场拒绝并说明怎么改」比
      「跑几秒再超时」对模型友好得多。
    */
    const risk = redosRisk(input.pattern)
    if (risk !== null) return toolFail(risk)

    const budget = ctx.host.clock.now() + SEARCH_BUDGET_MS
    // path 直接指到一个文件时就只搜那一个,不必遍历
    let files: Array<{ rel: string; abs: string }>
    let walkTruncated = false
    let walkTimedOut = false
    if (!st.isDir) {
      files = [{ rel: relOf(ctx, r.abs), abs: r.abs }]
    } else {
      const res = await walk({
        fs: ctx.host.fs,
        path: ctx.host.path,
        root: walkBase.root,
        start: walkBase.start,
        signal: ctx.signal,
        now: () => ctx.host.clock.now(),
        deadlineMs: SEARCH_BUDGET_MS,
        skip: defaultSkip
      })
      walkTruncated = res.truncated
      walkTimedOut = res.timedOut
      files = res.entries
        .filter((e) => !e.isDir && (nameFilter === null || nameFilter.test(normalizeGlobPath(e.rel))))
        .map((e) => ({ rel: walkBase.display(e.rel), abs: e.abs }))
    }

    /*
      ★ 隔离池在这里才创建:遍历走完之后。它一出生就带一个**真实墙钟** deadline
      (`regexWallBudgetMs`,测试可注入更短的值),到期无条件 terminate 全部 worker。
      池大小 = min(GREP_CONCURRENCY, REGEX_MAX_WORKERS):并发 fs 读取仍是 16 路,
      但同时在跑正则的 worker 有硬上限 —— 一次 Grep 至多这么多线程,且跨文件**复用**。

      ★ 但 16 路读取 × 一次一个正文 = 至多 16 份正文同时在池里排队,这是**有意的**
      排队上限(见 `grep-regex.ts` 的 `MAX_QUEUED_REQUESTS`):再多就拒绝 enqueue
      并明确报错,而不是让正文无限驻留。所以 `GREP_CONCURRENCY` 不得超过那个上限,
      否则多出来的读取会白读一个文件再拿一个「队列满」。
    */
    const pool = new GrepRegexPool({
      pattern: input.pattern,
      ignoreCase: input['-i'] === true,
      multiline,
      signal: ctx.signal,
      budgetMs: Math.max(1, regexWallBudgetMs - (Date.now() - wallStartMs)),
      limits: {
        maxLineChars: MAX_LINE_CHARS,
        maxMultilineChars: MAX_MULTILINE_CHARS,
        maxLinesPerFile: MAX_GREP_MATCHES,
        maxWorkers: Math.min(GREP_CONCURRENCY, REGEX_MAX_WORKERS),
        // ★ 与上面那道 `size > MAX_GREP_FILE_BYTES` 同一个数:这里是读取侧,
        // worker 侧还会再拦一次 —— 池是公开导出的,不能只靠调用方自觉
        maxFileBytes: MAX_GREP_FILE_BYTES
      },
      onEvent: (message) => { ctx.emit({ callId: ctx.callId, message }) }
    })

    let scan: ScanResult
    try {
      // 编译在 worker 里做 —— 语法错误拿到的是 V8 的原话,成句留给下面
      const compileError = await pool.compiled()
      if (compileError !== null) {
        // ★ rg 也说不合法时报 rg 的原话 —— 描述里告诉模型的是 ripgrep 语法
        return toolFail(
          native.regexError !== undefined
            ? `Invalid regular expression: ${native.regexError}`
            : `Invalid regular expression: ${compileError} (checked by the built-in JavaScript regex engine). ` +
              `When searching for a literal, escape . ( ) [ ] { } * + ? with a backslash.`
        )
      }
      const prefilter = literalPrefilter(input.pattern, input['-i'] === true)
      // ★ needText:只有 content 模式才需要行正文,见 `grepFile` 的同名参数
      scan = await scanFiles(files, budget, ctx, (f) => grepFile(ctx, f.abs, f.rel, pool, prefilter, needText))
    } catch (error) {
      if (!(error instanceof RegexBudgetError)) throw error
      // 编译也可能在等待进程级名额时到期，不能把资源繁忙冒充语法错误或空结果。
      scan = { hits: [], totalMatches: 0, scanned: 0, budgetHit: false, timedOut: true }
    } finally {
      // ★ 无论正常结束、超时、中止还是抛错,都 terminate 全部 worker —— 不留孤儿线程
      await pool.dispose()
    }

    const { hits, totalMatches, scanned, budgetHit, timedOut } = scan

    const notes: string[] = []
    if (totalMatches >= MAX_GREP_MATCHES) notes.push(matchCapNote())
    /*
      ★ 超时**必须说出来**。静默返回部分结果的话,模型会据此断言
      「这个字符串在仓库里不存在」—— 而那是个看起来很有说服力的错误答案。
    */
    if (budgetHit || walkTimedOut) {
      notes.push(
        `搜索超过 ${String(SEARCH_BUDGET_MS)}ms 预算,只搜了 ${String(scanned)}/${String(files.length)} 个文件,` +
          `结果不完整。请用 path、glob 或 type 缩小范围再搜一次`
      )
    }
    /* 墙钟包含读取、等待执行资源和匹配；到期本身不能证明正则发生了灾难性回溯。 */
    if (timedOut) {
      notes.push(
        `搜索超过 ${String(regexWallBudgetMs)}ms 安全期限(包含文件读取、等待线程和正则匹配),` +
          `已强制中止,只搜了 ${String(scanned)}/${String(files.length)} 个文件。结果不完整 —— ` +
          `请缩小 path、glob 或 type 范围，或简化正则后重试`
      )
    }
    if (walkTruncated) notes.push('traversal limit reached; some directories were not scanned')

    return formatGrepResult(
      input,
      hits,
      notes,
      notes.length > 0
        ? `No matches found in the ${String(scanned)} files scanned under ${base}.\n[${notes.join('; ')}]`
        : `No content matching "${input.pattern}" in the ${String(files.length)} files under ${base}.`
    )
  }
})

function matchCapNote(): string {
  return `hit the ${String(MAX_GREP_MATCHES)} match cap; narrow the pattern, glob, or path and search again`
}

/** 三种 `output_mode` 的渲染,JS 与原生两条路共用 —— 换后端不能换输出格式。`empty` = 没有命中时的整句 */
function formatGrepResult(
  input: z.infer<typeof GrepInput>,
  hits: FileHits[],
  notes: string[],
  empty: string
): ToolResult {
  if (hits.length === 0) return toolOk(empty)
  const tail = notes.length > 0 ? `\n\n[${notes.join('; ')}]` : ''

  const mode = input.output_mode ?? 'files_with_matches'
  const limit = input.head_limit

  if (mode === 'files_with_matches') {
    const paths = hits.map((h) => h.rel)
    const shown = limit === undefined ? paths : paths.slice(0, limit)
    return toolOk(
      shown.join('\n') +
        (shown.length < paths.length ? `\n[showing the first ${String(shown.length)} files]` : '') +
        tail
    )
  }

  if (mode === 'count') {
    const rows = hits.map((h) => `${h.rel}:${String(h.lines.length)}`)
    const shown = limit === undefined ? rows : rows.slice(0, limit)
    return toolOk(
      shown.join('\n') +
        (shown.length < rows.length ? `\n[showing the first ${String(shown.length)} files]` : '') +
        tail
    )
  }

  const ctxLines = input['-C']
  const before = ctxLines ?? input['-B'] ?? 0
  const after = ctxLines ?? input['-A'] ?? 0
  const rendered = renderContent(hits, before, after, input['-n'] === true)
  const shown = limit === undefined ? rendered : rendered.slice(0, limit)
  return toolOk(
    shown.join('\n') +
      (shown.length < rendered.length ? `\n[showing the first ${String(shown.length)} lines]` : '') +
      tail
  )
}

/**
 * 把原生后端的「绝对路径 → 行号」变成和 JS 那条路同形的 `FileHits`。
 *
 * ★ 忽略表与文件名过滤在这里**再做一遍**:原生侧的下推只是为了少搜(cmd 下还可能
 * 整个下推不了),正确性以这里为准 —— 判据与 `walk()` + `compileGlob` 完全相同。
 * 顺序与 `walk()` 的 BFS 同向:浅的在前,同层按路径逐段比较 —— 撞上命中上限时留下的
 * 是靠近根的文件。截断规则与 `scanFiles` 一致(整文件计入,累计到上限为止)。
 */
async function collectNativeHits(
  ctx: ToolContext,
  native: Extract<NativeGrepOutcome, { kind: 'ok' }>,
  target: { abs: string; isDir: boolean },
  walkBase: WalkBase,
  nameFilter: RegExp | null,
  needText: boolean
): Promise<{ hits: FileHits[]; totalMatches: number }> {
  const rel = (from: string, to: string): string => (ctx.host.path ? ctx.host.path.relative(from, to) : relative(from, to))
  const rows: Array<{ key: string[]; rel: string; abs: string; lines: number[] }> = []
  for (const [abs, lines] of native.hits) {
    // 单文件:和 JS 那条路一样,不过滤、直接用它的展示路径
    if (!target.isDir) {
      rows.push({ key: [], rel: relOf(ctx, abs), abs, lines })
      continue
    }
    const parts = normalizeGlobPath(rel(target.abs, abs)).split('/')
    const file = parts.pop() ?? ''
    if (parts.some((d) => defaultSkip(d, true)) || defaultSkip(file, false)) continue
    const fromRoot = rel(walkBase.root, abs)
    if (nameFilter !== null && !nameFilter.test(normalizeGlobPath(fromRoot))) continue
    rows.push({ key: normalizeGlobPath(fromRoot).split('/'), rel: walkBase.display(fromRoot), abs, lines })
  }
  rows.sort((a, b) => {
    if (a.key.length !== b.key.length) return a.key.length - b.key.length
    for (let i = 0; i < a.key.length; i++) {
      const x = a.key[i] ?? ''
      const y = b.key[i] ?? ''
      if (x !== y) return x < y ? -1 : 1
    }
    return 0
  })

  const hits: FileHits[] = []
  let totalMatches = 0
  for (const row of rows) {
    if (totalMatches >= MAX_GREP_MATCHES) break
    const lines = row.lines.slice(0, MAX_GREP_MATCHES)
    hits.push({ rel: row.rel, lines, text: [] })
    totalMatches += lines.length
  }
  if (needText) {
    // 行正文自己读(原生输出里只要了行号),切行与截断和 JS 那条路一致
    for (let i = 0; i < hits.length; i += GREP_CONCURRENCY) {
      await Promise.all(
        hits.slice(i, i + GREP_CONCURRENCY).map(async (h, j) => {
          const abs = rows[i + j]?.abs ?? ''
          try {
            const raw = await ctx.host.fs.readFile(abs)
            h.text = raw.split('\n').map((l) => (l.length > MAX_LINE_CHARS ? l.slice(0, MAX_LINE_CHARS) : l))
          } catch (error) {
            rethrowRemoteFailure(error, ctx)
          }
        })
      )
    }
  }
  return { hits, totalMatches }
}

/** 给测试用的常量出口,避免测试里再抄一份魔法数字 */
export const SEARCH_LIMITS = {
  MAX_GLOB_RESULTS,
  MAX_GREP_MATCHES,
  MAX_LINE_CHARS,
  MAX_MULTILINE_CHARS,
  SEARCH_BUDGET_MS,
  MAX_GREP_FILE_BYTES,
  GREP_CONCURRENCY,
  REGEX_MAX_WORKERS,
  REGEX_WALL_BUDGET_MS,
  /**
   * ★ 进程级的那些预算在池那边(名额是跨池共享的),这里一并转出来,
   * 好让「并发多个池」的测试只从一处读上限、不再各抄一份数字。
   */
  REGEX_MAX_PROCESS_WORKERS: GREP_REGEX_LIMITS.MAX_PROCESS_WORKERS,
  REGEX_MAX_QUEUED_REQUESTS: GREP_REGEX_LIMITS.MAX_QUEUED_REQUESTS,
  REGEX_MAX_QUEUED_CHARS: GREP_REGEX_LIMITS.MAX_QUEUED_CHARS,
  REGEX_WORKER_MAX_OLD_GEN_MB: GREP_REGEX_LIMITS.WORKER_MAX_OLD_GEN_MB
} as const

/** `type` 参数认识的名字,给测试和文档用 */
export const GREP_TYPES = Object.keys(TYPE_GLOBS)
