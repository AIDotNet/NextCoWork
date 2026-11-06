/**
 * 往 UI 推的事件 —— 方案 §4.2。它是 ProviderStreamEvent 的**超集**。
 *
 * 分两层的原因:ProviderStreamEvent 是「上游那一层」的词汇(text_delta、tool_call_delta),
 * 内核和网关都用;AgentEvent 多出内核才知道的东西(落盘边界、待决交互、子代理、上下文压力)。
 */
import type { AgentError } from './error'
import type { AgentMessage } from './message'
import type { PendingInteraction, InteractionOutcome } from './interaction'
import type { ProviderStreamEvent } from './stream'
import type { TokenUsage } from './stream'
import type { ToolOutput } from './message'
import type { ToolProgress } from './tool'
import type { ContextSegment, ContextStatus } from './context-management'

export type RunStatus = 'running' | 'done' | 'error' | 'aborted'

export type SubagentPhase = 'starting' | 'thinking' | 'tool' | 'finishing' | 'background'

/**
 * 「正在退避重试 / 已经切到另一家」的**瞬时**提示。
 *
 * ★ 定义在事件层而不是转录层,因为它现在有两个消费者:主对话的状态行,
 * 和子代理卡片(`subagent_update.notice`)。放在 `transcript.ts` 的话,
 * `event.ts` 要反过来 import 它 —— 而依赖方向是 transcript → event,不能成环。
 */
export type RunNotice =
  | { kind: 'retry'; attempt: number; reason: string }
  | { kind: 'switch'; to: string; reason: string }

export type AgentEvent =
  /**
   * `at` 是主进程收到这条上游事件时的墙钟毫秒 —— 和 `tool_start.at` 同一个约定。
   *
   * 需求：思考卡片的实时计时要和提交后落盘的 `durationMs` 用**同一只表**。渲染层自己
   * `Date.now()` 的话，重载后重放日志时所有增量都挤在同一毫秒里，计时从 0 重来，
   * 而提交那一刻又跳回主进程算的真值。可选：fake-emitter 与旧日志没有它，reducer 退回本地时钟。
   */
  | { type: 'stream'; delta: ProviderStreamEvent; at?: number }
  /** ★ 落盘边界 —— db 只在这里写,绝不在 delta 上写(方案 §9) */
  | { type: 'message_commit'; message: AgentMessage }
  /**
   * `at` 是主进程打的墙钟毫秒,用来算工具耗时。
   *
   * ★ **可选**:发射端还没填的时候,转录 reducer 退化到自己 `Date.now()`。
   * 这让「先上渲染层版本、之后再补主进程戳」不必改 UI 一行代码,
   * 也让已落盘的旧事件重放时不会因为缺字段而失败。
   */
  | { type: 'tool_start'; callId: string; toolName: string; input: unknown; at?: number }
  /** 易失,永不进转录 */
  | { type: 'tool_progress'; callId: string; progress: ToolProgress }
  | { type: 'tool_end'; callId: string; output: ToolOutput; isError: boolean; at?: number }
  | { type: 'interaction_request'; interaction: PendingInteraction }
  | { type: 'interaction_resolved'; id: string; outcome: InteractionOutcome }
  | {
      type: 'subagent_start'
      callId: string
      childRunId: string
      /**
       * 子 run 自己那个会话的 id —— 右侧只读面板就是靠它去 `getSession()` 取转录的。
       *
       * ★ **必须由主进程带过来,不能在渲染层拼。** 派生 id 的写法是
       * `${parent.sessionId}:sub:${childRunId}`(见 `runtime.ts` 的 `childRequestFor`),
       * 而 childRunId 本身又是 `${parentRunId}:sub:N` —— 套两层子代理之后,
       * 想从字符串里反推父亲会切出爷爷。权威在库里那一列 `parentSessionId`,
       * 这个字段只是把当时已经算好的那个值原样递出来。
       *
       * 可选,是因为旧转录里没有这一格 —— 那些卡片点开会是空的,仅此而已。
       */
      childSessionId?: string
      description?: string
      subagentType?: string
      color?: import('../domain/agent-def').AgentColor
      model?: string
      background?: boolean
      at?: number
    }
  | {
      type: 'subagent_update'
      callId: string
      childRunId: string
      phase?: SubagentPhase
      currentTool?: string
      currentTarget?: string
      toolCalls?: number
      toolErrors?: number
      usage?: TokenUsage
      contextUsage?: { used: number; window: number; shouldCompact: boolean }
      /**
       * 子代理正在退避重试 / 刚切了供应商。
       *
       * ★ 和上面的 `currentTool` 同一个约定:**看的是键在不在**。
       * `notice: undefined` 是一次显式清除(重试成功了),整个键不写则是「别动它」。
       */
      notice?: RunNotice
      /** Sequence of the corresponding child-run event, used to deduplicate the inherited raw event. */
      childSeq?: number
      at?: number
    }
  | {
      type: 'subagent_end'
      callId: string
      childRunId: string
      status: RunStatus
      summary?: string
      /** Terminal error, including localization metadata, copied from the child run. */
      error?: AgentError
      /** Sequence of the child run_end event, used to deduplicate the inherited raw event. */
      childSeq?: number
      at?: number
    }
  /** ★ 抄自 agent-request-flow.md §4 的 agent:contextUsage —— 见下方注释 */
  | {
      type: 'context_usage'
      /**
       * 发出去之前**本地估的**输入大小(`estimateTokens`,误差英文 ±15% / 中文 ±25%)。
       *
       * ★ 它和转录里的 `lastInputTokens`(上游在 `message_end` 里报回的真值)
       * **不是同一个数,也不该是**:这一条在请求发出前就发,那时真值还不存在 ——
       * 压力条要在这一轮真的挤爆之前就画出来。所以圆环显示真值、只有真值缺席时
       * 才退回这里的估算;而 `segments` 各档之和恒等于 `used`,两者同源。
       *
       * ★ **`shouldCompact` 不是拿这个 `used` 判的**,它读的是「上一次上游真值 +
       * 其后新增消息的估算」(见 `AgentSession.contextTokens`,同 Claude Code 的
       * `tokenCountWithEstimation`)。原先这里写的是「校准系数」—— 那套已随压缩重写删除。
       */
      used: number
      window: number
      shouldCompact: boolean
      /** 归因明细。老 run / 纯内核路径可能没有 —— 见 `ContextUsage.segments`。 */
      segments?: ContextSegment[]
    }
  /** Localized run warning; not a provider error and never model context. */
  | { type: 'notification'; warning: AgentError }
  /**
   * 压缩进行中 / 完成 / 失败 / 熔断。压缩的产物本身(边界消息)走普通的 `message_commit`,
   * 这里只报状态 —— 原先的 `context_checkpoint` 事件随检查点表一起删除。
   */
  | { type: 'context_status'; status: ContextStatus }
  /** `at` is the wall-clock time at which the run reached its terminal state. */
  | { type: 'run_end'; status: RunStatus; error?: AgentError; at?: number }

/**
 * ★ context_usage 每轮由 ContextAssembler 算完请求后顺手发一条,
 * UI 拿它画上下文压力条并在逼近上限时提示 /compact。
 *
 * **上下文用尽是这类应用最高频的失败,而它现在是可以提前看见的** ——
 * 事后补要动每一个 emit 点。
 */

/** attach 的返回值(方案 §4.6:重新加载的渲染层要把三种框都重新画出来)。 */
export interface RunSnapshot {
  runId: string
  sessionId: string
  workspaceId: string
  parentRunId?: string
  depth: number
  status: RunStatus
  /** Wall-clock bounds are optional for snapshots created by older runtimes. */
  startedAt?: number
  endedAt?: number
  /** 快照产出时的 seq;渲染层据此续接增量 */
  seq: number
  /** sinceSeq 之后的事件,按序 */
  events: AgentEvent[]
  /** ★ 不叫 pendingApprovals —— 三种 kind 共用一张表 */
  pendingInteractions: PendingInteraction[]
  /** 子 run 的 id,UI 上是可展开节点 */
  children: string[]
  /**
   * 主进程的事件日志**头部被截掉过**(超过条数/字节上限)。`events` 因此不是这个 run
   * 从头到尾的完整回放 —— 已提交的消息要从库里读(`sessions:getPage`),不能只靠这份快照重建。
   * 缺省 = 日志完整(除去那些被后来的提交/工具结束取代、重放不需要的增量)。
   */
  logTrimmed?: boolean
}

/**
 * 「现在还有哪些顶层 run 活着」的索引项。
 *
 * 需求:渲染层的三处运行中指示(外层工作区 Tab、内层对话 Tab、侧边栏会话行)
 * 必须与主进程 RunRegistry 一致。bootstrap 的首帧和 `agent:activeRuns` 的后续广播
 * 说的是同一件事,所以**共用这一个类型**——两边各写一份的话,迟早只有一边会加字段。
 */
export interface ActiveRunEntry {
  runId: string
  sessionId: string
  workspaceId: string
  status: RunStatus
  /**
   * 这个 run(含它派出的子代理)正等着用户处理的审批 / 提问数。
   *
   * ★ 走全局广播而不是正文事件:没有窗口在看这条会话时正文一条都不推,
   * 而「有个审批在等你」恰恰是那时最该让用户知道的事 —— 应用内标一下,不强制切换。
   * 缺席 = 0。
   */
  pendingInteractions?: number
}

/** run 结束后不再有事件,UI 可以据此收掉 loading 态。 */
export function isTerminal(s: RunStatus): boolean {
  return s !== 'running'
}
