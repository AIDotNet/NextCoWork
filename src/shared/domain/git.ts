/**
 * Git 管理面板的领域类型 —— 主进程与渲染层共用。
 *
 * ★ 这里描述的是**一个仓库此刻的样子**,不是一次 git 调用的输出。
 *   porcelain 的 XY 两位原样带上来(`index` / `worktree`),而不是在主进程
 *   压成一个 `status: 'modified' | 'added' | ...` 的枚举:同一个文件完全可以
 *   「暂存了一次修改、之后又改了一遍」(XY = `MM`),压成一个枚举就必须二选一,
 *   而界面要的恰恰是分别显示这两栏。
 *
 * ★ 路径一律是**仓库相对**(git 自己的写法,`/` 分隔),不是工作区相对 ——
 *   仓库根可能在工作区根的上层(在子目录里打开项目是常态)。它们只被原样
 *   回传给 git 当参数,不参与任何路径拼接。
 */

/** 面板开不出来的四种原因。**都不是错误**,是如实说明这个工作区没有 git 可管。 */
export type GitUnavailableReason =
  /** 远程(SSH)工作区。git 命令跑在本机,对它无从谈起。 */
  | 'remote-workspace'
  /** 工作区目录读不到(被删、被移走、盘没挂上)。 */
  | 'workspace-unavailable'
  /** 本机没有 git,或者它不在 PATH 里。 */
  | 'git-missing'
  /** 目录存在,但不是(也不在)一个 git 仓库里。 */
  | 'not-a-repository'

export interface GitFileChange {
  /** 仓库相对路径 */
  path: string
  /** 重命名 / 复制的来源路径。只有 XY 含 `R` / `C` 时才有 */
  renamedFrom?: string
  /** porcelain 的 X 位(暂存区)。空格 = 这一栏没有改动 */
  index: string
  /** porcelain 的 Y 位(工作区) */
  worktree: string
  staged: boolean
  unstaged: boolean
  untracked: boolean
  /** 冲突中(XY 里含 `U`,或 `AA` / `DD`)。这类不给暂存按钮,先去解决冲突 */
  conflicted: boolean
}

export interface GitCommitSummary {
  hash: string
  shortHash: string
  subject: string
  author: string
  /** 提交时间,毫秒 */
  timestamp: number
}

export interface GitBranchSummary {
  name: string
  current: boolean
  /** 上游分支(`origin/main` 这种)。没设上游就是空串 */
  upstream: string
}

export interface GitRepoState {
  root: string
  /** 空串 = detached HEAD 或空仓库(还没有第一个提交) */
  branch: string
  detached: boolean
  /** 还没有任何提交的新仓库 —— 此时 `git log` 是空的,不是出错 */
  unborn: boolean
  upstream: string
  ahead: number
  behind: number
  files: GitFileChange[]
  /** 改动文件太多,列表被截断了(见主进程的 `MAX_FILES`) */
  filesTruncated: boolean
}

export type GitOverview =
  | ({ available: true } & GitRepoState)
  | { available: false; reason: GitUnavailableReason }

export interface GitDiff {
  path: string
  staged: boolean
  /** 二进制文件:git 不给文本 diff,界面显示一行说明而不是空白 */
  binary: boolean
  /** 超过上限被截断 */
  truncated: boolean
  text: string
}

/**
 * 一次 diff 两侧的**完整文本** —— 给代码对比视图(行内 / 并排、折叠未改动区域)用。
 *
 * 比较基准跟 `git diff` 一致:看工作区时是「暂存区 → 工作区」,看暂存区时是
 * 「HEAD → 暂存区」。某一侧不存在(新文件、已删除、空仓库)就是空串。
 *
 * ★ `null` 不是错误,是「这一份没法按全文对比」:二进制、超过上限、冲突中、
 *   子模块。界面那时退回 `GitDiff.text` 的 unified 文本。
 */
export interface GitDiffSides {
  original: string
  modified: string
}
