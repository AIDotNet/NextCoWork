/**
 * 文档会话状态 —— 纯函数状态机,主进程是唯一的写入者。
 *
 * ## 为了什么需求建的
 *
 * 编辑器画布、Agent 工具、保存、崩溃恢复都会改变「这个文档现在是什么状态」。
 * 如果每一方各自记一份 dirty / 修订号,就会出现「编辑器显示已保存、Agent 以为
 * 还脏」或者「引擎重启后旧引用还在被使用」这种对不上的状态。这里把状态迁移
 * 收敛成一个 reducer:任何状态变化都是一次 `reduceSession`,于是它能被直接测。
 *
 * ## 不变式
 *
 * - `modelRevision` 单调递增,每次**已确认**的修改 +1;`savedRevision` 是最后一次
 *   写盘成功时的 modelRevision。dirty = 两者不等 —— **不是**一个单独存的布尔,
 *   否则「保存进行中用户又打了一个字」会被保存成功回执错误地清成 clean。
 * - `generation` 在引擎重启 / 重载后 +1;旧 generation 的引用与操作一律 stale。
 * - `seq` 每次迁移 +1,订阅方据此发现丢事件并请求快照。
 * - `closed` 是终态:之后的任何事件都不能把它复活。
 */
import type { DocumentErrorCode, DocumentFormat } from './protocol'

export type DocumentSessionStatus =
  | 'loading'
  | 'ready'
  | 'saving'
  | 'conflict'
  | 'crashed'
  | 'recovering'
  | 'closed'

export interface DocumentSessionSnapshot {
  sessionId: string
  format: DocumentFormat
  status: DocumentSessionStatus
  generation: number
  modelRevision: number
  savedRevision: number
  /** 最近一次确认读取 / 写入的磁盘字节摘要 */
  diskRevision: string
  seq: number
}

export type DocumentSessionEvent =
  | { type: 'loaded'; diskRevision: string }
  | { type: 'applied'; revision: number }
  | { type: 'saveStarted' }
  /** `savedModelRevision` 是**开始保存那一刻**的 modelRevision,不是回执到达时的 */
  | { type: 'saved'; diskRevision: string; savedModelRevision: number }
  | { type: 'saveFailed' }
  | { type: 'diskConflict' }
  | { type: 'conflictResolved'; diskRevision: string }
  | { type: 'crashed' }
  | { type: 'recovering' }
  | { type: 'reloaded'; diskRevision: string; restoredRevision: number }
  | { type: 'closed' }

export function initialSession(sessionId: string, format: DocumentFormat): DocumentSessionSnapshot {
  return { sessionId, format, status: 'loading', generation: 0, modelRevision: 0, savedRevision: 0, diskRevision: '', seq: 0 }
}

export function isSessionDirty(snapshot: DocumentSessionSnapshot): boolean {
  return snapshot.modelRevision !== snapshot.savedRevision
}

/**
 * 状态迁移。非法迁移**原样返回**(不抛)—— 迟到的回执(例如 helper 已经崩溃后才到的
 * `applied`)是常态,不是故障;调用方用返回值是否变化判断事件有没有被接受。
 */
export function reduceSession(s: DocumentSessionSnapshot, e: DocumentSessionEvent): DocumentSessionSnapshot {
  if (s.status === 'closed') return s
  const next = (patch: Partial<DocumentSessionSnapshot>): DocumentSessionSnapshot => ({ ...s, ...patch, seq: s.seq + 1 })
  switch (e.type) {
    case 'loaded':
      if (s.status !== 'loading') return s
      return next({ status: 'ready', generation: s.generation + 1, diskRevision: e.diskRevision, modelRevision: 0, savedRevision: 0 })
    case 'applied':
      // ★ 修订号只能 +1:跳号意味着有一次修改的回执丢了,不能假装连续
      if ((s.status !== 'ready' && s.status !== 'saving' && s.status !== 'conflict') || e.revision !== s.modelRevision + 1) return s
      return next({ modelRevision: e.revision })
    case 'saveStarted':
      if (s.status !== 'ready') return s
      return next({ status: 'saving' })
    case 'saved':
      if (s.status !== 'saving' || e.savedModelRevision > s.modelRevision) return s
      return next({ status: 'ready', savedRevision: e.savedModelRevision, diskRevision: e.diskRevision })
    case 'saveFailed':
      if (s.status !== 'saving') return s
      return next({ status: 'ready' })
    case 'diskConflict':
      if (s.status !== 'saving' && s.status !== 'ready') return s
      return next({ status: 'conflict' })
    case 'conflictResolved':
      if (s.status !== 'conflict') return s
      return next({ status: 'ready', diskRevision: e.diskRevision })
    case 'crashed':
      if (s.status === 'crashed') return s
      return next({ status: 'crashed' })
    case 'recovering':
      if (s.status !== 'crashed') return s
      return next({ status: 'recovering' })
    case 'reloaded':
      /*
        ★ 重载后 generation +1:引擎里的对象引用全部换了一批,旧引用若还能用,
        就会指到新模型里另一个碰巧同 id 的对象上。
        `restoredRevision` 是恢复快照对应的 modelRevision;savedRevision 保持不变,
        于是「从恢复快照回来的未保存改动」仍然显示为 dirty,不会被当成已保存。
      */
      if (s.status !== 'recovering' && s.status !== 'conflict') return s
      return next({ status: 'ready', generation: s.generation + 1, diskRevision: e.diskRevision, modelRevision: e.restoredRevision, savedRevision: Math.min(s.savedRevision, e.restoredRevision) })
    case 'closed':
      return next({ status: 'closed' })
  }
}

/**
 * 修改批次的前置检查。返回 `null` = 可以进队列。
 *
 * 需求:调用方(UI 或 Agent)准备操作期间,文档可能已被别人改过或引擎已经重启。
 * 两种情况下旧的定位都作废 —— 按旧坐标写进去,就是把字写到了错误的位置。
 */
export function applyPrecondition(
  s: DocumentSessionSnapshot,
  expected: { generation: number; modelRevision: number }
): DocumentErrorCode | null {
  if (s.status === 'closed') return 'session_closed'
  if (s.status === 'crashed' || s.status === 'recovering' || s.status === 'loading') return 'engine_unavailable'
  if (expected.generation !== s.generation) return 'stale_generation'
  if (expected.modelRevision !== s.modelRevision) return 'stale_revision'
  return null
}
