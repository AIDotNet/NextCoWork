/**
 * 插入消息队列 —— Agent 执行期间用户继续发送的输入。
 *
 * ## 为什么是「结构化条目」而不是原来的 `string[]`
 *
 * 队列原本是 `queuedInputs: string[]`。截图形态要求逐条可插话、可编辑、可删除,
 * 而字符串数组里**两条内容相同的消息不可区分** —— `key`、定位、状态全都无从谈起。
 * `id` 是这个文件存在的首要理由。
 *
 * ## 「插话」(promote) 的准确语义
 *
 * 不是中断当前 run 立即注入 —— 那与「不中断当前执行」的需求直接冲突,
 * 且会丢弃已产出的工具结果。它是**下一轮 run 的 input 排在最前**,
 * 跳过 FIFO 等待。所以 promote 是纯本地状态变更,不发起任何请求。
 *
 * ## 逐条冻结 options,而不是全队列共用一份
 *
 * `session.ts` 的 `lastOptions` 注释已经写明「不能读当时的 UI 值:用户可能在
 * 排队期间改了模型下拉,而排队那条消息是按他当时看到的设置写的」。
 * 那条规则对**队列整体**成立,对**队列内部**同样成立 —— 排队 5 分钟里改两次模型,
 * 三条消息就该有三份档位。这里把快照粒度从「全队列一份」收紧到「每条一份」。
 *
 * ## 本文件全是纯函数
 *
 * `id` 与 `now` 一律由调用方注入,不在这里 `ulid()` / `Date.now()` ——
 * 顺序与合并规则是这套机制里最容易出错的部分,它必须能在没有时钟的情况下被断言。
 */
import type { ContentPart } from '../agent/message'
import type { SendOptions, SessionMode } from '../agent/run-request'
import type { PermissionMode } from '../agent/permission'
// ★ mime 推断只有一份 —— 附件域已经有了,队列不再自带一张扩展名表
import { mimeOfExt } from './attachment'

/**
 * 附件 —— 截图条目尾部那个「· 2 图片」。
 *
 * ★ **存 `ncw://` URL,不存绝对路径。** 附件在入队前已经由上传服务落盘并登记,
 * 这里只持有一个引用。存路径的两个问题:把绝对路径写进了会被持久化的队列
 * (它会随存档漂到别的机器),以及渲染层拿到路径也没法直接显示 —— 显示走的是协议。
 */
export interface QueuedAttachment {
  kind: 'image' | 'file'
  name: string
  /** `ncw://attachments/...` */
  url: string
}

/**
 * ★ 只有 `pending` / `promoted` 会出现在列表里。
 * `consumed` / `dropped` 是终态 —— 条目进入终态时**直接移出数组**,
 * 这两个值存在只为让 `QueuedInput` 能描述一条已离开队列的记录(日志/断言),
 * 而不是让数组里躺着一堆需要到处过滤的僵尸。
 */
export type QueuedInputStatus = 'pending' | 'promoted' | 'consumed' | 'dropped'

export interface QueuedInput {
  /** 入队时 mint(ULID)。列表 key、插话/编辑/删除全靠它定位 */
  id: string
  text: string
  attachments: QueuedAttachment[]
  status: QueuedInputStatus
  /**
   * ★ 入队瞬间冻结的档位,后续改 UI 不影响它 —— **唯一的例外是
   * `permissionMode`**:切换权限档位药丸时,`retagQueuedPermission`(见
   * `stores/session.ts`)会把队列里还没被消费的条目原地改成新档位。用户切到
   * 「完全访问」图的就是接下来都不用再被打断,如果排在后面的追问还要
   * 背着旧档位继续走审批,这条切换就等于没生效。
   */
  options: SendOptions
  enqueuedAt: number
  /** 点「插话」的时刻。多条同时被引入时,它是唯一的排序依据 */
  promotedAt?: number
  /** 最终消费它的 run。中断后回溯「这条到底发出去没有」的依据 */
  consumedByRunId?: string
}

/**
 * 落盘载荷(§8.2)。草稿与队列合成**一个键**:同源、同生命周期、同时读写;
 * 拆两个键要付两次 IPC、两套防抖、两次恢复竞态。
 */
export interface SessionInputState {
  /** ★ 结构版本。将来 QueuedInput 破坏性改字段时,读到旧版本直接丢弃而不是崩 */
  v: 1
  draft: string
  /** 只含非终态条目 */
  queued: QueuedInput[]
  savedAt: number
}

export const SESSION_INPUT_VERSION = 1

/**
 * 一次会话输入落盘的结果 —— 广播给所有窗口的那一条。
 *
 * ★ **只带 `sessionId` 与状态,不带异常、路径、SQL。** 渲染层要做的只有
 * 「显示一句人话 / 关掉那句人话」,而底层异常的正文里会有绝对路径、库文件名,
 * 甚至一行 SQL —— 那些东西一旦进了事件就会出现在界面上(方案 §9)。
 *
 * - `retrying`:这一份还没写成功,但运行时会继续重试(内存里那份还在)。
 * - `blocked`:重试也没写进去,用户需要自己处理(磁盘满、没写权限)。
 * - `saved`:写成功了 —— 唯一允许渲染层**撤掉**那条提示的状态。
 */
export interface SessionInputPersistence {
  sessionId: string
  state: 'retrying' | 'blocked' | 'saved'
}

/**
 * 队列的写操作。**主进程是队列唯一的写入者**(`main/session-runtime.ts`):
 * 渲染层只发意图、收回执,不再自己决定下一轮什么时候开跑 —— 否则没有窗口在看
 * 这条会话时,排队的消息就永远等不到续跑。
 *
 * - `take`:出队并把文本交回调用方(「撤回到输入框」),草稿由渲染层拼接后另行落盘。
 * - `resume`:用户在停止/报错/重启之后手动继续,与自动续跑走同一条路径。
 */
export type SessionQueueOp =
  | { kind: 'enqueue'; text: string; options: SendOptions; attachments: QueuedAttachment[] }
  | { kind: 'promote'; id: string }
  | { kind: 'edit'; id: string; text: string }
  | { kind: 'drop'; id: string }
  | { kind: 'take'; id: string }
  | { kind: 'retagPermission'; mode: PermissionMode }
  | { kind: 'retagMode'; mode: SessionMode }
  | { kind: 'resume' }

/**
 * 队列的权威快照。`rev` 单调递增:回执与广播可能乱序到达,渲染层只收更新的那份。
 */
export interface SessionQueueSnapshot {
  sessionId: string
  queued: QueuedInput[]
  rev: number
}

export interface SessionQueueResult extends SessionQueueSnapshot {
  /** `enqueue` 超过 `QUEUE_MAX_ITEMS` 时为 false —— 调用方据此保留草稿 */
  accepted: boolean
  /** `take` 取回的文本 */
  text?: string
}

export type SubagentReportStatus = 'none' | 'pending' | 'injecting' | 'reported' | 'blocked'

/** 队列条目数软上限。超过就不是队列了,是便签本(§9) */
export const QUEUE_MAX_ITEMS = 20

/** 单条文本上限。同时挡住「合并后必然超上下文」和「kv 单行被撑爆」 */
export const QUEUE_MAX_TEXT = 32_000

/** 恢复时丢弃多久以前的存档 —— 否则被遗弃的会话草稿会在 kv 里永久堆积 */
export const SESSION_INPUT_TTL_MS = 30 * 24 * 60 * 60 * 1000

// ─────────────────────────────────────────────────────────────

export function makeQueuedInput(
  id: string,
  text: string,
  options: SendOptions,
  now: number,
  attachments: QueuedAttachment[] = []
): QueuedInput {
  return { id, text, attachments, status: 'pending', options, enqueuedAt: now }
}

/**
 * `ContentPart[]` → 队列附件。
 *
 * ★ 入队时**必须**做这一步,否则生成期间带图发送的消息在续跑时会
 * **只剩文字** —— 图片静默消失,而用户明明看到自己发了图。
 *
 * 只认 `ncw://`:外部绝对路径的图不受附件服务管理,把它塞进会被持久化的
 * 队列意味着那条路径会随存档漂到别的机器上。它们直接丢弃并不会更糟,
 * 因为反正也显示不出来(见 `MessageImage` 的降级分支)。
 */
export function partsToAttachments(parts: readonly ContentPart[]): QueuedAttachment[] {
  const out: QueuedAttachment[] = []
  for (const p of parts) {
    if (p.type !== 'image') continue
    if (!p.dataRef.startsWith('ncw://')) continue
    out.push({ kind: 'image', name: fileNameOfUrl(p.dataRef), url: p.dataRef })
  }
  return out
}

function fileNameOfUrl(url: string): string {
  const i = url.lastIndexOf('/')
  return i < 0 ? url : url.slice(i + 1)
}

/** 列表只渲染非终态 */
export function isLive(q: QueuedInput): boolean {
  return q.status === 'pending' || q.status === 'promoted'
}

/**
 * 下一轮该发哪些条目。
 *
 * ★ **有 promoted 就只发 promoted,一条 pending 都不捎带。** 用户点了插话,
 * 表达的是「先说这个」,而不是「顺便也把队首发了」—— 捎带会让下一轮的输入
 * 里混进一条用户没打算现在发的消息。
 *
 * ★ **没有 promoted 时退回原 FIFO 取一条** —— 这是对现状行为的兼容承诺:
 * 用户不点任何按钮,队列的表现与改造前一字不差。
 */
export function pickNextBatch(queue: readonly QueuedInput[]): QueuedInput[] {
  const promoted = queue
    .filter((q) => q.status === 'promoted')
    // ★ 用 ?? enqueuedAt 而不是 ?? 0:promotedAt 缺失时退化成入队序,
    //   而不是让它抢到所有人前面。状态与字段不一致时要往安全的方向塌缩。
    .sort((a, b) => (a.promotedAt ?? a.enqueuedAt) - (b.promotedAt ?? b.enqueuedAt))
  if (promoted.length > 0) return promoted

  const next = queue.find((q) => q.status === 'pending')
  return next === undefined ? [] : [next]
}

export interface MergedBatch {
  text: string
  attachments: QueuedAttachment[]
  /** 超出 QUEUE_MAX_TEXT 而被排除、需要退回 pending 的条目 id */
  deferredIds: string[]
}

/**
 * 合并成**一次**输入。
 *
 * ★ 为什么不连发多个 run:run 是串行的,发 N 个 run 等于把插话重新变回排队 ——
 * 第 2 条要等第 1 条整轮跑完,正好抵消掉这套机制存在的意义。
 *
 * ★ 超限时**退回 pending 并汇报**,不静默截断。截断会让用户以为消息发出去了,
 * 而实际上后半句永远消失了 —— 这是比「没发出去」严重得多的失败模式。
 */
export function mergeBatch(batch: readonly QueuedInput[]): MergedBatch {
  const taken: QueuedInput[] = []
  const deferredIds: string[] = []
  let used = 0

  for (const q of batch) {
    const piece = q.text.trim()
    // 已经取了至少一条、再加这条就超限 → 剩下的全部退回,不再继续尝试塞小的:
    // 乱序会让用户看到的发送顺序与他排的顺序对不上。
    const cost = piece.length + (taken.length > 0 ? 2 : 0)
    if (taken.length > 0 && used + cost > QUEUE_MAX_TEXT) {
      deferredIds.push(q.id)
      continue
    }
    taken.push(q)
    used += cost
  }

  return {
    text: taken
      .map((q) => q.text.trim())
      .filter((t) => t !== '')
      .join('\n\n'),
    attachments: dedupeByUrl(taken.flatMap((q) => q.attachments)),
    deferredIds
  }
}

function dedupeByUrl(list: readonly QueuedAttachment[]): QueuedAttachment[] {
  const seen = new Set<string>()
  const out: QueuedAttachment[] = []
  for (const a of list) {
    if (seen.has(a.url)) continue
    seen.add(a.url)
    out.push(a)
  }
  return out
}

/**
 * 合并结果 → `ContentPart[]`。
 *
 * ★ 附件走 `image` part 的 `dataRef`,里面装的是 `ncw://` URL ——
 * 与转录里已有的表示法一致,不为队列发明第二套附件表示。
 * 非图片附件降级成一行文本:`ContentPart` 目前没有 file 类型,
 * 伪造一个会污染整条转录链路。
 */
export function batchToParts(merged: MergedBatch): ContentPart[] {
  const parts: ContentPart[] = []
  if (merged.text !== '') parts.push({ type: 'text', text: merged.text })
  for (const a of merged.attachments) {
    if (a.kind === 'image') {
      parts.push({ type: 'image', mime: mimeOfExt(a.url), dataRef: a.url })
    } else {
      parts.push({ type: 'text', text: `[附件] ${a.name}` })
    }
  }
  return parts
}

/**
 * 读回存档时的校验。
 *
 * ★ 宁可**整份丢弃**也不做部分修复:一份结构对不上的存档意味着写它的代码
 * 与读它的代码不是同一个版本,逐字段兜底只会把不一致带进运行时。
 * 丢弃的代价是用户少了一份草稿,修复失败的代价是一个无法复现的 bug。
 */
export function isValidSessionInput(x: unknown, now: number): x is SessionInputState {
  if (typeof x !== 'object' || x === null) return false
  const s = x as Partial<SessionInputState>
  if (s.v !== SESSION_INPUT_VERSION) return false
  if (typeof s.draft !== 'string') return false
  if (!Array.isArray(s.queued)) return false
  if (typeof s.savedAt !== 'number') return false
  if (now - s.savedAt > SESSION_INPUT_TTL_MS) return false
  return s.queued.every(isValidQueuedInput)
}

function isValidQueuedInput(x: unknown): x is QueuedInput {
  if (typeof x !== 'object' || x === null) return false
  const q = x as Partial<QueuedInput>
  return (
    typeof q.id === 'string' &&
    typeof q.text === 'string' &&
    Array.isArray(q.attachments) &&
    (q.status === 'pending' || q.status === 'promoted') &&
    typeof q.options === 'object' &&
    q.options !== null &&
    typeof q.enqueuedAt === 'number'
  )
}
