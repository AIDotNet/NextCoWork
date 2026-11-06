/**
 * Git 管理面板的主进程一侧。
 *
 * ★ **不走 `SpawnFn`,直接 `execFile` 递 argv 数组。**
 *   `kernel/node-spawn.ts` 那个 `SpawnFn` 收的是一整条命令字符串,内部 `sh -c`
 *   / `cmd.exe /c`。这里的输入 —— 分支名、提交信息、文件路径 —— 全部来自渲染层,
 *   拼进 shell 字符串就必须自己做引号转义,而转义只要漏一个形状就是一次命令注入
 *   (一个叫 `$(rm -rf ~)` 的分支名足够了)。argv 数组压根不起 shell,这个问题
 *   在源头就不存在。代价是拿不到 `SpawnFn` 的进程组清理 —— 而 git 不派生子进程树,
 *   用不上。
 *
 * ★ **「开不出来」不是错误。** 远程工作区 / 目录没了 / 没装 git / 不是仓库,
 *   这四种都返回 `{ available: false, reason }`,由界面如实说明。抛 IpcError 的话
 *   UI 只会得到一个红色 toast,而用户想知道的是「为什么这里没有 git」。
 *   真正的错误(命令失败、提交信息为空)才抛。
 *
 * ★ **flag 注入**:argv 解决了 shell,但没解决 git 自己 —— 一个叫 `--upload-pack=...`
 *   的分支名仍然会被 git 当成选项。路径一律放在 `--` 之后;`--` 管不到的位置
 *   (`switch <branch>`)用 `safeRef()` 挡掉前导 `-`。
 */
import { execFile } from 'node:child_process'
import {
  closeSync,
  fstatSync,
  lstatSync,
  openSync,
  readFileSync,
  readSync,
  readlinkSync,
  realpathSync
} from 'node:fs'
import { isAbsolute, relative, resolve, sep } from 'node:path'
import type {
  GitBranchSummary,
  GitCommitSummary,
  GitDiff,
  GitDiffSides,
  GitOverview,
  GitUnavailableReason
} from '../../shared/domain/git'
import { isLocalEnvironment } from '../../shared/domain/environment'
import { MAX_FILES, parseStatus } from './git-status'
import { COMMIT_MESSAGE_DIFF_LIMIT } from '../commit-message'
import { getCommitMessageGenerator } from '../runtime'
import { store } from '../state/store'
import { IpcError } from './errors'

/** 只读命令。大仓库的 status 可能要几百毫秒,2s 那档太紧。 */
const READ_TIMEOUT_MS = 10_000
/** 改本地状态的命令(add / reset / commit / switch)。 */
const WRITE_TIMEOUT_MS = 30_000
/** 走网络的命令(pull / push)。慢是常态,不是卡死。 */
const NETWORK_TIMEOUT_MS = 120_000

/** 单个 diff 的字符上限。超出截断并置 `truncated`。 */
const MAX_DIFF_CHARS = 200 * 1024
/** 子进程输出缓冲。status 撑满 MAX_FILES 时也够。 */
const MAX_BUFFER = 16 * 1024 * 1024

/**
 * ★ `--no-optional-locks`:别和用户开着的 IDE / GUI 抢 index 锁(同 `git-context.ts`)。
 * ★ `core.quotePath=false`:否则中文、带空格的路径会被 git 转义成 `"\344\270\255"`,
 *   diff 头和 log 里显示成一串八进制。`-z` 的输出本来就不转义,但 diff 的文件头会。
 */
const GIT_PREFIX = ['--no-optional-locks', '-c', 'core.quotePath=false']

interface GitRun {
  code: number
  stdout: string
  stderr: string
  /** 本机没装 git / 不在 PATH 里 */
  missing: boolean
}

/**
 * 跑一次 git。**永不 throw** —— 调用方自己看 `code` 决定这是「一种状态」还是「一次失败」。
 */
function run(cwd: string, args: string[], timeoutMs = READ_TIMEOUT_MS): Promise<GitRun> {
  return new Promise<GitRun>((done) => {
    const env = { ...process.env }
    /*
      ★ 和 `node-spawn.ts` 同一个理由:父进程是 Electron 时 ELECTRON_RUN_AS_NODE
      可能是 1,继承下去会毒害 git 的钩子(钩子里的 `node` 其实是 Electron 在冒充);
      NODE_OPTIONS 里常带 --inspect,钩子继承就抢同一个调试端口然后启动失败。
    */
    delete env.ELECTRON_RUN_AS_NODE
    delete env.NODE_OPTIONS
    /*
      ★ 关掉一切会等输入的东西。pull / push 遇到要密码的 remote 时,git 会去开
      终端提示符 —— 而这里没有终端,它就永远挂在那儿,表现成「点了 pull 没反应」。
      宁可让它当场失败,把 stderr 如实显示给用户。
    */
    env.GIT_TERMINAL_PROMPT = '0'
    env.GIT_ASKPASS = ''
    env.SSH_ASKPASS = ''

    execFile(
      'git',
      [...GIT_PREFIX, ...args],
      { cwd, env, timeout: timeoutMs, maxBuffer: MAX_BUFFER, windowsHide: true, encoding: 'utf8' },
      (error, stdout, stderr) => {
        if (!error) {
          done({ code: 0, stdout, stderr, missing: false })
          return
        }
        const code = (error as NodeJS.ErrnoException & { code?: number | string }).code
        // spawn 本身失败:ENOENT = PATH 里没有 git
        if (code === 'ENOENT') {
          done({ code: 127, stdout: '', stderr: error.message, missing: true })
          return
        }
        done({ code: typeof code === 'number' ? code : 1, stdout, stderr, missing: false })
      }
    )
  })
}

/** git 失败时给用户看的那句话。优先 stderr —— git 的 fatal 信息本身就是最好的解释。 */
function fail(result: GitRun, fallback: string): never {
  const detail = result.stderr.trim() || result.stdout.trim()
  throw new IpcError('tool_failed', detail === '' ? fallback : detail)
}

function unavailable(reason: GitUnavailableReason): GitOverview {
  return { available: false, reason }
}

/** 工作区根目录。返回 reason 表示「这个工作区没有本地目录可看」。 */
function workspaceDir(workspaceId: string): { dir: string } | { reason: GitUnavailableReason } {
  const workspace = store.getWorkspace(workspaceId)
  if (!workspace) return { reason: 'workspace-unavailable' }
  // 远程(SSH)工作区:git 跑在本机,对它无从谈起
  if (!isLocalEnvironment(workspace.environment)) return { reason: 'remote-workspace' }
  try {
    const dir = realpathSync.native(workspace.rootPath)
    if (!lstatSync(dir).isDirectory()) return { reason: 'workspace-unavailable' }
    return { dir }
  } catch {
    return { reason: 'workspace-unavailable' }
  }
}

interface Repo {
  /**
   * 工作区目录 —— 不带路径参数的命令(status / log / branch / pull…)的 cwd。
   */
  dir: string
  /**
   * 仓库根(可能在工作区根的上层:在子目录里打开项目是常态)。
   *
   * ★ **带路径参数的命令必须用它当 cwd**(add / reset / diff -- <path>)。
   *   porcelain 给的路径是**仓库根相对**的,而 git 的 pathspec 是 **cwd 相对**的 ——
   *   两者在「工作区就是仓库根」时恰好一样,所以这个错能一路活到有人在子目录里
   *   打开项目为止。那时的表现是:列表显示正常,点暂存/取消暂存全部报
   *   `fatal: pathspec '…' did not match any files`,点文件看 diff 一片空白。
   */
  root: string
}

/** 把工作区解析成一个可用的仓库。不可用时返回 reason,**不抛**。 */
async function openRepo(workspaceId: string): Promise<Repo | { reason: GitUnavailableReason }> {
  const located = workspaceDir(workspaceId)
  if ('reason' in located) return located
  const { dir } = located

  /*
    ★ 一条命令吸收两种情况:没装 git(missing)、不是仓库(非零退出)。
    `--is-inside-work-tree` 在裸仓库里返回 false 而**退出码为 0** —— 裸仓库没有
    工作区,这个面板的暂存/提交全都无从谈起,所以也归到 not-a-repository。
  */
  const inside = await run(dir, ['rev-parse', '--is-inside-work-tree'])
  if (inside.missing) return { reason: 'git-missing' }
  if (inside.code !== 0 || inside.stdout.trim() !== 'true') return { reason: 'not-a-repository' }

  const top = await run(dir, ['rev-parse', '--show-toplevel'])
  if (top.code !== 0) return { reason: 'not-a-repository' }
  return { dir, root: top.stdout.trim() }
}

/** 拿到仓库或抛错。写操作用这个 —— 到了要 commit 的时候「不可用」就是真的失败了。 */
async function requireRepo(workspaceId: string): Promise<Repo> {
  const repo = await openRepo(workspaceId)
  if ('reason' in repo) throw new IpcError('tool_failed', `git.unavailable.${repo.reason}`)
  return repo
}

// ═══════════════════════════════════════════════════════════════
// 输入校验 —— 渲染层递进来的每一个字符串都在这儿过一遍
// ═══════════════════════════════════════════════════════════════

/**
 * 仓库相对路径。**只校验形状,不做 fs 审计** —— 它不参与路径拼接,
 * 只被原样放在 `--` 之后递给 git(`readUntracked` 是唯一的例外,那里另外查一次)。
 */
function safePath(path: unknown): string {
  if (typeof path !== 'string' || path === '' || path.includes('\0')) {
    throw new IpcError('tool_failed', 'git.invalidPath')
  }
  if (isAbsolute(path)) throw new IpcError('tool_failed', 'git.invalidPath')
  if (path.split('/').some((part) => part === '..')) {
    throw new IpcError('tool_failed', 'git.invalidPath')
  }
  return path
}

function safePaths(paths: unknown): string[] {
  if (!Array.isArray(paths) || paths.length === 0) {
    throw new IpcError('tool_failed', 'git.invalidPath')
  }
  return paths.map((path) => safePath(path))
}

/**
 * 分支名 / ref。
 *
 * ★ 这里挡的是 **flag 注入**,不是 shell 注入:`git switch <name>` 里的 name
 *   没有 `--` 可以垫,一个叫 `--orphan` 的分支名会被当成选项。git 自己的
 *   check-ref-format 本来就禁掉了空格和大部分特殊字符,前导 `-` 是它允许、
 *   而我们必须拒的那一类。
 */
function safeRef(name: unknown): string {
  if (typeof name !== 'string') throw new IpcError('tool_failed', 'git.invalidBranch')
  const trimmed = name.trim()
  if (trimmed === '' || trimmed.startsWith('-') || /[\s\0~^:?*[\\]/.test(trimmed)) {
    throw new IpcError('tool_failed', 'git.invalidBranch')
  }
  return trimmed
}

// ═══════════════════════════════════════════════════════════════
// 频道实现
// ═══════════════════════════════════════════════════════════════

export async function getGitOverview(req: { workspaceId: string }): Promise<GitOverview> {
  const repo = await openRepo(req.workspaceId)
  if ('reason' in repo) return unavailable(repo.reason)

  const status = await run(repo.dir, ['status', '--porcelain=v2', '--branch', '-z'])
  if (status.missing) return unavailable('git-missing')
  if (status.code !== 0) fail(status, 'git status 失败')

  return { available: true, root: repo.root, ...parseStatus(status.stdout) }
}

export async function listGitBranches(req: { workspaceId: string }): Promise<GitBranchSummary[]> {
  const repo = await openRepo(req.workspaceId)
  if ('reason' in repo) return []

  /*
    ★ 用 `%09`(制表符)当分隔符是安全的:git 的 ref 名禁止一切 ASCII 控制字符,
    所以分支名和上游名里都不可能出现它。ref-filter 的 `%xx` 十六进制转义正是为此。
  */
  const result = await run(repo.dir, [
    'branch',
    '--list',
    '--format=%(refname:short)%09%(upstream:short)%09%(HEAD)'
  ])
  if (result.code !== 0) return []

  const branches: GitBranchSummary[] = []
  for (const line of result.stdout.split('\n')) {
    if (line.trim() === '') continue
    const [name = '', upstream = '', head = ''] = line.split('\t')
    if (name === '') continue
    branches.push({ name, current: head.trim() === '*', upstream })
  }
  return branches
}

/**
 * 提交记录的字段 / 记录分隔符 —— ASCII 的 US(0x1f)和 RS(0x1e)。
 * 提交标题里不可能出现控制字符,所以不必担心它们把一条记录劈成两半。
 */
const FIELD = '\u001f'
const RECORD = '\u001e'

export async function listGitCommits(req: {
  workspaceId: string
  limit?: number
}): Promise<GitCommitSummary[]> {
  const repo = await openRepo(req.workspaceId)
  if ('reason' in repo) return []

  const limit = Math.min(Math.max(Math.trunc(req.limit ?? 50), 1), 500)
  const result = await run(repo.dir, [
    'log',
    `-${String(limit)}`,
    `--format=%H${FIELD}%h${FIELD}%s${FIELD}%an${FIELD}%at${RECORD}`
  ])
  /*
    ★ 空仓库(还没有第一个提交)时 `git log` 退出 128。那不是错误,是「还没有提交」——
    返回空数组,让界面显示空态而不是一条红色报错。
  */
  if (result.code !== 0) return []

  const commits: GitCommitSummary[] = []
  for (const record of result.stdout.split(RECORD)) {
    // git 在记录之间还会加一个换行,它落在 RECORD 之后
    const line = record.replace(/^\n/, '')
    if (line.trim() === '') continue
    const [hash = '', shortHash = '', subject = '', author = '', at = ''] = line.split(FIELD)
    if (hash === '') continue
    commits.push({
      hash,
      shortHash,
      subject,
      author,
      timestamp: (Number.parseInt(at, 10) || 0) * 1000
    })
  }
  return commits
}

/** diff 里 git 明说是二进制的那一行。 */
function looksBinary(text: string): boolean {
  return /^Binary files .* differ$/m.test(text) || text.includes('GIT binary patch')
}

function clampDiff(path: string, staged: boolean, text: string): GitDiff {
  const binary = looksBinary(text)
  const truncated = !binary && text.length > MAX_DIFF_CHARS
  return {
    path,
    staged,
    binary,
    truncated,
    text: binary ? '' : truncated ? text.slice(0, MAX_DIFF_CHARS) : text
  }
}

/**
 * 仓库相对路径 → 磁盘上的绝对路径。
 *
 * 只有两处真的去拼 fs 路径(`readUntracked` / `readWorktreeSide`)—— 越界一律拒。
 */
function resolveInRepo(repo: Repo, path: string): string {
  const target = resolve(repo.root, path)
  const rel = relative(repo.root, target)
  if (rel === '' || isAbsolute(rel) || rel === '..' || rel.startsWith(`..${sep}`)) {
    throw new IpcError('tool_failed', 'git.invalidPath')
  }
  return target
}

/**
 * 未跟踪文件的 diff。
 *
 * ★ 自己拼,不走 `git diff --no-index /dev/null <path>`:那条命令在 Windows 上
 *   没有可靠的 null 设备写法(`NUL` 不是 git 认得的路径),而未跟踪文件恰恰是
 *   新建项目里最常点开的那一类。拼出来的是标准 unified diff,和 git 的输出同形,
 *   界面那边不需要第二套渲染。
 */
function readUntracked(repo: Repo, path: string): GitDiff {
  const empty: GitDiff = { path, staged: false, binary: false, truncated: false, text: '' }
  const target = resolveInRepo(repo, path)

  let fd: number
  try {
    fd = openSync(target, 'r')
  } catch {
    // 列出来之后文件又没了 —— 给一个空 diff,别报错
    return empty
  }
  try {
    const stat = fstatSync(fd)
    if (!stat.isFile()) return empty
    const size = Math.min(stat.size, MAX_DIFF_CHARS)
    const buffer = Buffer.alloc(size)
    let used = 0
    while (used < size) {
      const read = readSync(fd, buffer, used, size - used, used)
      if (read === 0) break
      used += read
    }
    const bytes = buffer.subarray(0, used)
    // NUL 字节 = 二进制。和 git 自己的判据一致
    if (bytes.includes(0)) return { ...empty, binary: true }

    const content = bytes.toString('utf8')
    const lines = content === '' ? [] : content.replace(/\n$/, '').split('\n')
    const header = `--- /dev/null\n+++ b/${path}\n@@ -0,0 +1,${String(lines.length)} @@\n`
    return {
      path,
      staged: false,
      binary: false,
      truncated: stat.size > MAX_DIFF_CHARS,
      text: header + lines.map((line) => `+${line}`).join('\n')
    }
  } finally {
    closeSync(fd)
  }
}

export async function getGitDiff(req: {
  workspaceId: string
  path: string
  staged: boolean
}): Promise<GitDiff> {
  const repo = await requireRepo(req.workspaceId)
  const path = safePath(req.path)
  const staged = req.staged === true

  if (!staged) {
    /*
      ★ 先问 git「这个文件跟踪了吗」。未跟踪文件的 `git diff` 输出是**空的**
      (它不在 index 里,没有可比的一侧)—— 直接跑的话用户点开一个新文件
      只会看到一片空白,而那看起来完全像是加载失败。
    */
    const tracked = await run(repo.root, ['ls-files', '--error-unmatch', '--', path])
    if (tracked.code !== 0) return readUntracked(repo, path)
  }

  const args = ['diff', '--no-color']
  if (staged) args.push('--cached')
  args.push('--', path)
  // cwd 用仓库根:path 是仓库根相对的,见 `Repo.root` 的 ★
  const result = await run(repo.root, args)
  if (result.code !== 0) fail(result, 'git diff 失败')
  return clampDiff(path, staged, result.stdout)
}

/**
 * 对比视图里单侧全文的上限。超出就退回 unified 文本 —— 那条路自己会截断,
 * 而全文对比要的是**完整**的两份,截一半的全文比不出任何可信的东西。
 */
const MAX_SIDE_CHARS = 1024 * 1024

/** 读一侧内容的结果:`null` = 这一侧没法当文本比(二进制 / 超限 / 读失败)。 */
type Side = string | null

function textSide(content: string): Side {
  // NUL = 二进制,和 git 自己的判据一致
  if (content.length > MAX_SIDE_CHARS || content.includes('\u0000')) return null
  return content
}

/**
 * 从对象库读一个 blob。
 *
 * ★ 用 `cat-file blob` 而不是 `show`:前者给的是原始内容,不走 textconv。
 *   退出码 128 = 对象不存在(新文件在 HEAD 里没有、空仓库没有 HEAD)——
 *   那一侧就是空的,不是失败。
 */
async function readBlobSide(repo: Repo, object: string): Promise<Side> {
  const result = await run(repo.root, ['cat-file', 'blob', object])
  if (result.code === 128) return ''
  if (result.code !== 0) return null
  return textSide(result.stdout)
}

/**
 * 读工作区里的那一侧。
 *
 * ★ 符号链接读**链接本身**(目标路径文本),不跟进去 —— git 存的就是这个,
 *   跟进去读目标文件的话,一个没动过的链接也会显示成「整份重写」。
 */
function readWorktreeSide(repo: Repo, path: string): Side {
  const target = resolveInRepo(repo, path)
  let stat
  try {
    stat = lstatSync(target)
  } catch {
    // 工作区里删掉了 —— 这一侧就是空的
    return ''
  }
  try {
    if (stat.isSymbolicLink()) return readlinkSync(target, 'utf8')
    if (!stat.isFile() || stat.size > MAX_SIDE_CHARS) return null
    return textSide(readFileSync(target, 'utf8'))
  } catch {
    return null
  }
}

/**
 * 这个路径在 index 里的条目。
 *
 * - `untracked`:不在 index 里
 * - `special`:冲突中(有 1/2/3 号 stage)或子模块(160000)—— 全文对比说不清楚
 * - 否则给出 stage 0 那条的 blob
 */
async function indexEntry(
  repo: Repo,
  path: string
): Promise<'untracked' | 'special' | { blob: string }> {
  const result = await run(repo.root, ['ls-files', '--stage', '-z', '--', path])
  if (result.code !== 0) return 'special'
  // 每条:`<mode> <blob> <stage>\t<path>`。pathspec 命中目录时会带出多条,只认精确路径
  const entries = result.stdout
    .split('\u0000')
    .filter((item) => item !== '')
    .map((item) => {
      const tab = item.indexOf('\t')
      const [mode = '', blob = '', stage = ''] = item.slice(0, tab).split(' ')
      return { mode, blob, stage, path: item.slice(tab + 1) }
    })
    .filter((item) => item.path === path)
  const [entry] = entries
  if (entry === undefined) return 'untracked'
  if (entries.length > 1 || entry.stage !== '0' || entry.mode === '160000') return 'special'
  return { blob: entry.blob }
}

export async function getGitDiffSides(req: {
  workspaceId: string
  path: string
  staged: boolean
}): Promise<GitDiffSides | null> {
  const repo = await requireRepo(req.workspaceId)
  const path = safePath(req.path)
  const entry = await indexEntry(repo, path)
  if (entry === 'special') return null

  const [original, modified]: [Side, Side] = req.staged === true
    ? await Promise.all([
      // 暂存区视图:HEAD → index。index 里没有 = 暂存了一次删除
      readBlobSide(repo, `HEAD:${path}`),
      entry === 'untracked' ? '' : readBlobSide(repo, entry.blob)
    ])
    : [
      // 工作区视图:index → 工作区。未跟踪文件没有可比的一侧,原文就是空的
      entry === 'untracked' ? '' : await readBlobSide(repo, entry.blob),
      readWorktreeSide(repo, path)
    ]
  if (original === null || modified === null) return null
  return { original, modified }
}

export async function stageGitPaths(req: { workspaceId: string; paths: string[] }): Promise<void> {
  const repo = await requireRepo(req.workspaceId)
  const paths = safePaths(req.paths)
  // ★ `-A` 让删除也能被暂存 —— 只写 `git add <path>` 的话,删掉的文件暂存不了
  // cwd 用仓库根:paths 是仓库根相对的,见 `Repo.root` 的 ★
  const result = await run(repo.root, ['add', '-A', '--', ...paths], WRITE_TIMEOUT_MS)
  if (result.code !== 0) fail(result, 'git add 失败')
}

export async function unstageGitPaths(req: { workspaceId: string; paths: string[] }): Promise<void> {
  const repo = await requireRepo(req.workspaceId)
  const paths = safePaths(req.paths)

  /*
    ★ 空仓库要换一条命令。`git reset -- <path>` 要拿 HEAD 当比较基准,而还没有
    第一个提交时 HEAD 不存在,它会 fatal。这种仓库里取消暂存 = 把条目从 index 删掉。
  */
  const head = await run(repo.dir, ['rev-parse', '--verify', '--quiet', 'HEAD'])
  const args =
    head.code === 0
      ? ['reset', '-q', 'HEAD', '--', ...paths]
      : ['rm', '-q', '--cached', '-r', '--', ...paths]
  // cwd 用仓库根:paths 是仓库根相对的,见 `Repo.root` 的 ★
  const result = await run(repo.root, args, WRITE_TIMEOUT_MS)
  if (result.code !== 0) fail(result, 'git reset 失败')
}

export async function commitGit(req: {
  workspaceId: string
  message: string
}): Promise<GitCommitSummary> {
  const repo = await requireRepo(req.workspaceId)
  if (typeof req.message !== 'string' || req.message.trim() === '') {
    throw new IpcError('tool_failed', 'git.emptyMessage')
  }

  const result = await run(repo.dir, ['commit', '-m', req.message], WRITE_TIMEOUT_MS)
  if (result.code !== 0) fail(result, 'git commit 失败')

  const [commit] = await listGitCommits({ workspaceId: req.workspaceId, limit: 1 })
  if (!commit) throw new IpcError('tool_failed', 'git commit 失败')
  return commit
}

export async function checkoutGitBranch(req: {
  workspaceId: string
  branch: string
}): Promise<void> {
  const repo = await requireRepo(req.workspaceId)
  const result = await run(repo.dir, ['switch', safeRef(req.branch)], WRITE_TIMEOUT_MS)
  if (result.code !== 0) fail(result, 'git switch 失败')
}

export async function createGitBranch(req: {
  workspaceId: string
  name: string
  checkout: boolean
}): Promise<GitBranchSummary> {
  const repo = await requireRepo(req.workspaceId)
  const name = safeRef(req.name)
  const args = req.checkout === true ? ['switch', '-c', name] : ['branch', name]
  const result = await run(repo.dir, args, WRITE_TIMEOUT_MS)
  if (result.code !== 0) fail(result, 'git branch 失败')

  const branches = await listGitBranches({ workspaceId: req.workspaceId })
  return (
    branches.find((item) => item.name === name) ?? {
      name,
      current: req.checkout === true,
      upstream: ''
    }
  )
}

export async function pullGit(req: { workspaceId: string }): Promise<void> {
  const repo = await requireRepo(req.workspaceId)
  /*
    ★ `--ff-only`:自动合并出冲突的话,用户会在一个**没有终端**的面板里面对一个
    半合并状态。快进不了就如实失败,把「需要合并」这件事交回给用户。
  */
  const result = await run(repo.dir, ['pull', '--ff-only'], NETWORK_TIMEOUT_MS)
  if (result.code !== 0) fail(result, 'git pull 失败')
}

export async function pushGit(req: { workspaceId: string }): Promise<void> {
  const repo = await requireRepo(req.workspaceId)
  /*
    ★ 没有上游时补 `-u origin <branch>`。裸 `git push` 在这种分支上会失败并
    建议一条命令 —— 而用户点的就是「推送」,让他去终端粘一条命令没有道理。
  */
  const upstream = await run(repo.dir, ['rev-parse', '--abbrev-ref', '--symbolic-full-name', '@{u}'])
  let args = ['push']
  if (upstream.code !== 0) {
    const branch = await run(repo.dir, ['branch', '--show-current'])
    const current = branch.stdout.trim()
    // detached HEAD:没有分支可推,也没有合理的默认值可猜
    if (current === '') throw new IpcError('tool_failed', 'git.detachedPush')
    args = ['push', '-u', 'origin', current]
  }
  const result = await run(repo.dir, args, NETWORK_TIMEOUT_MS)
  if (result.code !== 0) fail(result, 'git push 失败')
}

// ═══════════════════════════════════════════════════════════════
// AI 写提交信息
// ═══════════════════════════════════════════════════════════════

/** 发给模型的参考提交条数。够它认出语言和风格(Conventional Commits 与否),再多是浪费。 */
const SUBJECT_SAMPLES = 8

/**
 * 读暂存区,交给模型写一条提交信息草稿。
 *
 * ★ **模型取自设置里的默认模型**,面板上不给选择器 —— 同 `generateAgent` 的理由:
 *   用户点的是「帮我写一句话」,不是「我要挑一个模型」。代价是没配默认模型时
 *   这颗按钮会失败,所以单给了一个 `git.aiNoModel`,而不是含糊的「生成失败」。
 *
 * ★ **diff 在这里截断,不在生成器里**:截断需要知道「原本有多长」才能如实告诉
 *   模型它看到的是残篇,而这份原文只有这儿有。
 *
 * ★ 生成器抛的是 i18n 键,原样裹进 `IpcError` —— 面板那张白名单认得它们。
 *   非本模块抛出的意外(比如 `resolveModel` 内部炸了)兜成 `git.aiFailed`,
 *   免得把一句英文的上游错误直接糊到界面上。
 */
export async function generateGitCommitMessage(req: {
  workspaceId: string
}): Promise<{ message: string }> {
  const repo = await requireRepo(req.workspaceId)

  const diffResult = await run(repo.dir, ['diff', '--cached', '--no-color'])
  if (diffResult.code !== 0) fail(diffResult, 'git diff 失败')
  const full = diffResult.stdout
  if (full.trim() === '') throw new IpcError('tool_failed', 'git.nothingStaged')
  const truncated = full.length > COMMIT_MESSAGE_DIFF_LIMIT
  const diff = truncated ? full.slice(0, COMMIT_MESSAGE_DIFF_LIMIT) : full

  // 文件清单单独取:diff 被截断时它仍然是完整的,模型至少知道还动了些什么
  const nameResult = await run(repo.dir, ['diff', '--cached', '--name-only', '-z'])
  const files = nameResult.code === 0
    ? nameResult.stdout.split('\u0000').filter((name) => name !== '').slice(0, MAX_FILES)
    : []

  // 空仓库里 `git log` 退出 128 —— 那是「还没有提交」,不是失败,给空样本就行
  const logResult = await run(repo.dir, ['log', `-${String(SUBJECT_SAMPLES)}`, '--format=%s'])
  const recentSubjects = logResult.code === 0
    ? logResult.stdout.split('\n').map((line) => line.trim()).filter((line) => line !== '')
    : []

  const settings = store.getSettings()
  if (settings.defaultModel === '') throw new IpcError('tool_failed', 'git.aiNoModel')

  try {
    const message = await getCommitMessageGenerator().generate({
      diff,
      files,
      recentSubjects,
      truncated,
      model: settings.defaultModel,
      ...(settings.defaultModelProviderId === undefined
        ? {}
        : { modelProviderId: settings.defaultModelProviderId }),
      workspaceId: req.workspaceId
    })
    return { message }
  } catch (error) {
    const key = error instanceof Error ? error.message : ''
    throw new IpcError('tool_failed', key.startsWith('git.') ? key : 'git.aiFailed')
  }
}
