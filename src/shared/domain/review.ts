/**
 * 「改动审查」的跨进程契约 —— 回复底部的审查卡、右侧 `changes` tab、以及
 * 撤销/恢复走的 `review:*` IPC 都用这里的类型。
 *
 * ★ 摘要类型(`ReviewChangeSet` / `ReviewFileEntry`)**不带 before/after 全文**:
 *   一轮改十个文件、每个几十 KB 的话,卡片一挂就是几百 KB 过 IPC。全文只在
 *   用户真的点开某个文件看 diff 时,按 `review:getFileDiff` 单文件取。
 */
export type ChangeKind = 'created' | 'modified' | 'deleted'
export type ChangeSetState = 'applied' | 'reverted'

/** 一个文件在某一轮里的改动条目(卡片/列表用,不含全文)。 */
export interface ReviewFileEntry {
  /** 工作区相对路径;区外文件是绝对路径。 */
  path: string
  changeKind: ChangeKind
  additions: number
  deletions: number
  /** 单文件超上限:内容没入库,禁 diff/undo。 */
  oversize: boolean
  /** 改到了工作区外:禁 undo。 */
  inWorkspace: boolean
}

/** 一轮(顶层 run)的改动集摘要。 */
export interface ReviewChangeSet {
  runId: string
  sessionId: string
  workspaceId: string
  state: ChangeSetState
  fileCount: number
  additions: number
  deletions: number
  files: ReviewFileEntry[]
}

/** 单文件的 before/after 全文,diff 预览用。 */
export interface ReviewFileDiff {
  path: string
  changeKind: ChangeKind
  /** created → ''。oversize 时为 ''(内容未入库)。 */
  before: string
  after: string
  oversize: boolean
}

/** 撤销/恢复单个文件的结果。 */
export interface ReviewFileResult {
  path: string
  /** ok=已回写;conflict=磁盘现状与记录不符且未强制;skipped=oversize/区外;error=写回失败。 */
  status: 'ok' | 'conflict' | 'skipped' | 'error'
  reason?: string
}

/** 撤销/恢复整轮的结果。 */
export interface ReviewMutationResult {
  runId: string
  state: ChangeSetState
  files: ReviewFileResult[]
}
