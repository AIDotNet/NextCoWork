/**
 * 会话 = 一条转录 + 它的元数据。Tab 只引用它(见 tab.ts)。
 */
import type { AgentMessage } from '../agent/message'
import type { SessionMode, ThinkingLevel } from '../agent/run-request'
import type { RunUsage } from '../agent/transcript'

export type SessionChange = {
  workspaceId?: string
  renamed?: { sessionId: string; title: string }
} & (
  | { kind: 'metadata' | 'messages' | 'history'; sessionIds: string[] }
  | { kind: 'deleted'; sessionIds: string[]; replacement?: { workspaceId: string; id: string; title: string } }
  | { kind?: 'reset' }
)

export interface Session {
  id: string
  workspaceId: string
  /**
   * ★ 非 undefined = 这条转录属于一次**子代理 run**,不是用户的一条对话。
   *
   * 它是所有「面向用户的枚举」的唯一判据:侧边栏、搜索、数据导出、存储统计、
   * 按时长清理,一处都不出现;而 `getSession` / `getHistory` 照读不误 ——
   * 子代理面板和续跑要用。
   *
   * 判据是这一列而**不是** id 里的 `:sub:`:子会话 id 是递归拼出来的,
   * 从里面反推父亲一定会切错(理由写在 `db/schema.ts` 第 10 条迁移上)。
   */
  parentSessionId?: string
  /** Session created by the scheduler; it is opened from the task history instead of chat navigation. */
  origin?: 'chat' | 'scheduled'
  /**
   * 非 undefined = 这是一条「从会话提炼 Skill」的会话,值指向源会话。
   *
   * 需求:提炼会话的**每一个** run(包括用户追问的后续轮次)都要看到源会话的摘要,
   * 所以它必须挂在会话上,而不是像 `planExecution` 那样只挂在某一次 RunRequest 上 ——
   * 挂在 run 上的症状是第二轮追问时 agent 忽然「忘了」要提炼的是哪段对话。
   *
   * ★ 只活在 json 里,没有提列 —— 照 `titleSource` / `modelProviderId` 的先例
   * (它不参与任何 WHERE / 级联)。源会话被删掉之后这里**不清理**:
   * 注入侧(`kernel/skill/extraction.ts`)会把它渲染成「源会话已删除」。
   */
  skillSource?: { sessionId: string }
  title: string
  /** Stored in session JSON; absent on older exports. Manual names are never auto-replaced. */
  titleSource?: 'default' | 'generated' | 'manual'
  model: string
  /**
   * 发起这个会话时选定的供应商。★ **这是历史事实,不是待执行的配置** ——
   * 那家后来被删了也不改写它,否则「当时用的哪家」就失真了。
   */
  modelProviderId?: string
  mode: SessionMode
  thinking: ThinkingLevel
  /**
   * ★ 会话创建时冻结的工作区根路径(方案 §9)。
   * 重新指向工作区不会追溯性地改变旧工具调用的含义。
   */
  rootPathAtCreation: string
  /**
   * ★ 永不恢复运行中状态 —— 启动时所有会话一律 idle,
   * 退出时 running 的 run 在转录里追加 interrupted 标记(方案 §9)。
   * 这样就不必构建你并不需要的崩溃恢复机制。
   */
  status: 'idle' | 'running'
  archived: boolean
  favorited: boolean
  createdAt: number
  updatedAt: number
}

/** Legacy placeholder values are data; their display labels belong to renderer i18n. */
export function isDefaultSessionTitle(title: string): boolean {
  return ['', '新对话', 'New conversation', 'New chat'].includes(title.trim())
}

/** 左侧边栏下半部分按这个分组(今天 / 昨天 / 更早) */
export type SessionGroup = 'today' | 'yesterday' | 'earlier'

export interface SessionListItem {
  id: string
  title: string
  updatedAt: number
  archived: boolean
  favorited: boolean
  /**
   * 取列表那一刻 RunRegistry 里有没有活跃 run。
   *
   * ★ **这是快照,不是订阅**:它只在有人重拉列表时更新,而 run 结束时主进程
   * 并不广播 `sessions:changed`。拿它画「运行中」角标的话,对话跑完之后那颗
   * 转圈会一直挂着,直到用户恰好改了某条会话(改名 / 归档)才消失。
   * 角标请读渲染层的运行中索引(`stores/session.ts` 的 `useRunIndex`)——
   * 它由 `agent:activeRuns` 广播维持,run 一结束就收。
   */
  running: boolean
  /**
   * 这一条本身是提炼会话(`Session.skillSource` 有值)。
   * 需求:侧边栏据此**不画**「提炼为 Skill」菜单项 —— 主进程会拒绝「提炼的提炼」,
   * 画出来就是一个必然失败的按钮。
   */
  skillExtraction?: true
}

export interface SessionDetail {
  session: Session
  messages: AgentMessage[]
  /**
   * 消息 → 产出它的 run。只有第 12 条迁移之后落盘的消息有归属,老对话这张表是空的。
   */
  messageRuns?: Record<string, string>
  /**
   * run → 该 run 的累计用量,从 `usage_records` 聚合而来。
   *
   * ★ 这是**重启之后**唯一能拿到逐轮用量的地方:流式过程中累加出来的那份
   * 只活在渲染进程内存里,进程一没就没了。两者形状相同(都是 `RunUsage`,
   * 连算 TPS 的 `upstreamMs` 也在内),展示层因此不必分辨手上这份是实时的
   * 还是回填的。
   */
  runUsage?: Record<string, RunUsage>
  /**
   * run → 发送那条消息时用户选中的模型别名,从 `usage_records.alias` 回填。
   *
   * ★ 和 `runUsage` 同源同理:流式过程中这份信息只活在渲染进程内存里
   * (`SendOptions.model`),进程一没就没了,重启后只能靠这张表找回来。
   */
  runModel?: Record<string, string>
}

/**
 * 转录的**一页**:最近的那段(或某条消息之前的那段),从一轮的开头切起。
 *
 * 需求:打开一条长会话不该把整段历史 —— 连同每一次工具输出和截图 —— 一次性读进渲染层。
 * 显示从最近的一页开始,往上翻时再按页往前取。改写转录(编辑 / 删除 / 重跑)按消息 id
 * 在主进程的完整历史上做,所以渲染层手里只有一页也不会误删页外的消息。
 *
 * `messageRuns` 只覆盖本页的消息;`runUsage` / `runModel` 是整段会话的汇总(很小)。
 */
export interface SessionPage extends SessionDetail {
  /** 这一页之前还有更早的消息 */
  hasMore: boolean
}

/** conversations:searchAll 的命中项。FTS5 给出的 snippet 带高亮标记。 */
export interface SearchHit {
  sessionId: string
  workspaceId: string
  messageId: string
  title: string
  snippet: string
  createdAt: number
}

export function groupOf(updatedAt: number, now: number): SessionGroup {
  const startOfToday = new Date(now).setHours(0, 0, 0, 0)
  if (updatedAt >= startOfToday) return 'today'
  if (updatedAt >= startOfToday - 86_400_000) return 'yesterday'
  return 'earlier'
}

export const SESSION_GROUP_LABEL: Record<SessionGroup, string> = {
  today: '今天',
  yesterday: '昨天',
  earlier: '更早'
}
