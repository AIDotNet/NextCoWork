/**
 * 事件流 → 可渲染转录。方案 §4.1 的「双轨转录」里 **UI 那一轨**。
 *
 * 放在 shared/ 而不是 renderer/stores/ 有具体理由:这是一个纯 reducer,
 * 而它最容易出的 bug —— 内容块按 index 错位、提交后残留活跃块、
 * 工具状态没归位 —— 在界面上表现为「偶尔串行」「偶尔少一段」,
 * 靠盯屏幕根本复现不了,靠无头测试三行就能锁死。
 *
 * ★ 它必须对**重复与乱序免疫**吗?不必 —— seq 已经保证了顺序与不重复
 * (信封连续性 + attach 重放)。这里假设输入是有序且恰好一次的,
 * 换来的是一个能一眼看完的 reducer。
 */
import type { AgentError } from './error'
import type { AgentEvent, RunNotice, RunStatus, SubagentPhase } from './event'
import { mergeGoalStatusMessage, visibleText, type AgentMessage, type ContentPart, type SubagentResult, type ToolOutput } from './message'
import type { TokenUsage } from './stream'
import type { RunCost } from '../domain/pricing'
import type { ContextSegment, ContextStatus } from './context-management'

/**
 * 一张子代理卡片留几条「最近做了什么」。
 *
 * ★ 这条曾经从 3 提到 8,理由是「右侧只读面板里有地方放」—— 后来那块面板改成了
 * 纯正文(标题 + 转录,别的什么都没有),于是 `activity` **现在没有任何渲染消费者**,
 * 多留的 5 条只是白占转录。回到 3:字段本身留着,因为它是转录里唯一一份
 * 「这个子代理最后几步在干嘛」的结构化记录,排查时读得到。
 */
const ACTIVITY_LIMIT = 3

/** 尚未提交的内容块。`index` 就是上游给的块序号(方案 §4.2)。 */
export interface LiveBlock {
  index: number
  kind: 'text' | 'thinking' | 'tool_use'
  /** text/thinking 的正文;tool_use 时是累积中的参数 JSON 片段 */
  text: string
  callId?: string
  name?: string
  /**
   * 只有 thinking 块写这两格:思考的起点与**最近一次思考增量**的墙钟毫秒。
   *
   * 需求:思考卡片实时显示「已思考 N 秒」,提交后换成落盘的 `durationMs` 时不能跳。
   * 所以起点的口径必须和 `main/kernel/block-accumulator.ts` 逐条一致 ——
   * 取「这块出现之前最后一条内容事件」(`lastStreamAt`)而不是这块的首个增量,
   * 理由写在那边 `ThinkingBlock` 的注释里。改这里就得同时改那边。
   */
  startedAt?: number
  endedAt?: number
}

export interface ToolCallState {
  callId: string
  name: string
  input: unknown
  status: 'pending' | 'running' | 'ok' | 'error'
  /** 易失,不进转录(方案 §4.3) */
  progress?: string
  /**
   * 运行中推出的实时交互卡片(第 2 层)。**易失,不进转录** —— 落盘的是 `output.card`。
   * 有它时渲染层自动展开工具卡并交给 `CardRenderer`。
   */
  card?: import('./tool-card').ToolCard
  /**
   * 生图工具运行中已经到手的那几张,键是请求里的格子序号(`ToolProgress.image.index`)。
   * **易失,不进转录** —— 落盘的是 `output.images`,`tool_end` 时清掉。
   *
   * 需求:多图生成期逐张显示。用格子序号做键而不是数组:并发请求的完成顺序不定,
   * 第 3 格先到时它必须出现在第 3 格,而不是挤到第 1 格让后面的占位整体错位。
   */
  partialImages?: Readonly<Record<number, import('./message').ToolOutputImage>>
  /** 运行中能被转去后台(见 `ToolProgress.detachable`)。易失,`tool_end` 时清掉。 */
  detachable?: boolean
  /** `Workflow` 运行中的节点状态(见 `ToolProgress.workflow`)。易失,`tool_end` 时清掉。 */
  workflow?: import('./workflow').WorkflowState
  output?: ToolOutput
  /**
   * `tool_start` 到达时的墙钟毫秒。
   *
   * ★ **可选,且不新增事件类型。** 事件自带 `at` 时用事件的(那是主进程打的戳,
   * 排除了 IPC 排队延迟);没有就退化到 `Date.now()`。已落盘的旧转录重放时两者都没有
   * 就只剩 `Date.now()`,算出来的耗时不准 —— 那是历史数据的固有损失,
   * 不值得为它做一次转录格式迁移。
   */
  startedAt?: number
  /** `tool_end` 到达时的墙钟毫秒。 */
  endedAt?: number
}

export interface SubagentState {
  callId: string
  childRunId: string
  /** 子 run 的会话 id,由 `subagent_start` 带过来。点开卡片时拿它去取转录。 */
  childSessionId?: string
  status: RunStatus
  description?: string
  subagentType?: string
  color?: import('../domain/agent-def').AgentColor
  model?: string
  background?: boolean
  phase?: SubagentPhase
  currentTool?: string
  currentTarget?: string
  toolCalls: number
  toolErrors: number
  startedAt?: number
  endedAt?: number
  /**
   * 最后一次从这个子代理听到任何动静的墙钟毫秒。
   *
   * ★★ 这是区分「卡死」和「在跑一件慢活」的**唯一**可靠信号。`startedAt` 只说
   * 它跑了多久 —— 而一个健康的长任务和一个一小时前就停住的 run,在耗时那一栏
   * 上长得一模一样(本仓库真出过:1h5m、794 次工具调用、界面上毫无异常)。
   * 一个还在动的子代理,这个数总在往前走;停住的那个,它会定在原地。
   *
   * 每一条子事件都刷新它 —— 包括心跳式的遥测,所以「没有新事件」就是字面意思。
   */
  lastEventAt?: number
  summary?: string
  error?: AgentError
  usage?: TokenUsage
  contextUsage?: {
    used: number
    window: number
    shouldCompact: boolean
    segments?: ContextSegment[]
  }
  /**
   * 和上面 `TranscriptState.notice` 是同一件事,只不过说的是**这个子代理**。
   *
   * ★★ 主线路的重试提示当初只接到了状态行上,而子代理有自己的卡片、不看状态行 ——
   * 于是「限流了,正在退避」在卡片上的样子和「跑得慢」完全一样,退避几次全失败之后
   * 只剩一句冷冰冰的错误,看起来就像**子代理压根没有重试**(它其实重试过)。
   */
  notice?: RunNotice
  /** Last child-run event sequence already reflected in this state. */
  childSeq?: number
  /** Recent high-signal actions, newest first. Kept small so the chat stays readable. */
  activity?: Array<{ toolName: string; target?: string; at?: number }>
  /** Background result handoff state. */
  reportStatus?: 'none' | 'pending' | 'injecting' | 'reported' | 'blocked'
}

export interface TranscriptState {
  /** 已提交的消息 —— 落盘的就是这些(方案 §9:绝不在 delta 上写盘) */
  messages: AgentMessage[]
  /** 当前这条助手消息里还在流的块,按 index 升序 */
  live: LiveBlock[]
  tools: Record<string, ToolCallState>
  /** Live and completed child agents keyed by their parent Task call id. */
  subagents: Record<string, SubagentState>
  status: RunStatus
  /** Wall-clock bounds for the currently displayed run. */
  runStartedAt?: number
  runEndedAt?: number
  /**
   * 最近一条**内容性**上游事件(`message_start` 与各类内容增量)的墙钟毫秒。
   * 只给新出现的 thinking 块当起点用,见 `LiveBlock.startedAt`。
   */
  lastStreamAt?: number
  model?: string
  /**
   * 这次回复**实际由哪家给的**。★ 和 `RunRequest.modelProviderId`(用户选的那家)
   * 不是一回事:故障切换真的换了家时,这里跟着变,而那边不变。抬头显示的应该是
   * 这一个 —— 说的是既成事实,不是意图。
   *
   * 同理,上面那个 `model` 是上游回包里的**真实模型名**,不是别名。别拿它去查别名表。
   */
  providerId?: string
  /** API-reported usage accumulated across completed requests in the current run. */
  usage?: RunUsage
  /**
   * 历史轮次的用量,从 SQLite 回填(`SessionDetail.runUsage` / `messageRuns`)。
   *
   * ★ 和上面那个 `usage` 是**两份不同来源的同一种数**,刻意不合并:
   * `usage` 是本次 run 流式累加出来的,进程一没就没了;这两张表是落盘的账,
   * 重启后照样在。合并成一份的话,就得在每次 run_end 时把内存那份写回表里 ——
   * 而那正是「同一笔账记两遍、两遍还可能不一致」的开始。展示层按
   * 「当前 run 用 `usage`,历史轮次查表」取用,两条路径各自都是单一事实源。
   *
   * ★ 它们描述的是**整段对话**,不是某一个 run。所以每次开新一轮时,
   * 它们和 `messages` / `tools` 一样必须从上一份状态里带过来,
   * 不能被 `emptyTranscript()` 清掉。
   */
  runUsage?: Record<string, RunUsage>
  /**
   * run → 发送那条消息时用户选中的模型别名,从 SQLite 回填(`SessionDetail.runModel`)。
   *
   * ★ 和 `runUsage` 同理,两份不同来源的同一种数:当前这一轮走 `SendOptions.model`
   * (进程内存,`send()` 打的快照),历史轮次查这张表(落盘账,重启后照样在)。
   * 别拿上面的 `model`(回包真实模型名)去顶替它 —— 那个会被服务端悄悄换名字
   * (过期的预览别名、故障切换)而用户毫无察觉,抬头应该说的是「你选了什么」,
   * 不是「服务端这次到底吐出了什么名字」。
   */
  runModel?: Record<string, string>
  /** 消息 → 产出它的 run。老对话(第 12 条迁移之前)为空。 */
  messageRuns?: Record<string, string>
  contextUsage?: {
    used: number
    window: number
    shouldCompact: boolean
    segments?: ContextSegment[]
  }
  /**
   * 最近一次上游请求**这一次**报回来的输入 token —— 缓存读写也算,它们同样占着窗口。
   *
   * ★ 和 `usage.inputTokens` 的区别是全部:那一个是整轮累加的账(问三次就是三次之和),
   * 拿它除以窗口很快就会超过 100%。上下文占用是个**瞬时量**,只能看最后一次。
   */
  lastInputTokens?: number
  contextStatus?: ContextStatus
  /**
   * 重试 / 故障切换的**瞬时**提示,活到下一次 `message_start` 或 `error` 为止。
   *
   * ★★ `provider_retry` 和 `provider_switch` 以前发了**没人画**(见 `applyEvent`
   * 那个 default 分支的旧注释)。于是「上游繁忙、正在退避重试」在界面上和「卡死了」
   * 长得一模一样:一个转圈,几十秒不动,而且重试成功的话用户永远不知道刚才发生过什么。
   * `router.ts` 那句「没有这条事件,用户看到的就是白白冻结 30 秒」说的就是这个 ——
   * 发射端当初做完了,消费端一直空着。
   *
   * ★ 不进 `messages`:它不是对话内容,重试成功之后没有任何留存价值,
   * 留在对话流里只会变成噪声。
   */
  notice?: RunNotice
  warning?: AgentError
  error?: AgentError
}

// 定义搬到了 `event.ts`(子代理卡片也要用它,而依赖只能是 transcript → event)。
// 这里原样再导出:`RunNotice` 是转录层词汇的一部分,调用方不必知道它住在哪。
export type { RunNotice }

export function emptyTranscript(): TranscriptState {
  return { messages: [], live: [], tools: {}, subagents: {}, status: 'running' }
}

/**
 * 一个 run 的用量账:token 数,外加算平均 TPS 用的分母。
 *
 * ★ `upstreamMs` **不是 token,绝不参与计价**。`priceOf` 只读它认识的那几个
 * 字段,所以多这一项是安全的;但反过来往 `TokenUsage` 里塞它就不安全了 ——
 * 那个类型是计价与落盘的口径,一个毫秒数混进去迟早被当成某种 token 加起来。
 */
export interface RunUsage extends TokenUsage {
  /**
   * Σ 这一轮里每次上游请求的耗时。工具执行、等待授权、思考之间的空档都不在内 ——
   * 拿它当分母算出来的才是模型的输出速度,而不是「这一轮总共等了多久」。
   *
   * 拿不到就是 undefined(老对话、以及还没收到过 message_end 的 run),
   * 展示层据此不显示 TPS,而不是显示一个 0。
   */
  upstreamMs?: number
  /**
   * Σ 这一轮的花费,由 `message_end.cost` 逐次累加(实时那份),或从
   * `usage_records` 聚合(落盘那份,见 `repo.ts` 的 `runUsageOf`)。
   *
   * ★ `null` 和 `undefined` 在这里**不是一回事**:`undefined` = 还没有任何
   * 计价信息(老对话、没接计价的链路),`null` = **明确算不出**(有请求查不到价,
   * 或一轮里混了多种币种)。展示层两者都不画,但累加方必须分得开 —— 见 `addCost`。
   *
   * ★ 它**不是 token,绝不参与计价**,和上面的 `upstreamMs` 同一条理由。
   */
  cost?: RunCost | null
}

/**
 * 这一次请求实际送上去的提示词有多大。
 *
 * ★ 三项都要加:Anthropic 的 `input_tokens` **不含**命中缓存和写入缓存的那部分,
 * 只看它的话,一段被缓存住的长对话会显示成「几乎没占上下文」。
 */
export function promptTokensOf(usage: TokenUsage): number {
  return usage.inputTokens
    + (usage.cacheReadInputTokens ?? 0)
    + (usage.cacheCreationInputTokens ?? 0)
}

/**
 * 这一轮里有多大比例的提示词是**从缓存读**的。
 *
 * ★ 分母用 `promptTokensOf` 而不是 `inputTokens`:后者**不含**缓存那两项,
 * 拿它当分母的话,一段被完整缓存住的长对话会算出一个远大于 1 的命中率
 * (极端情况下分母是 0)。
 *
 * ★ 一次请求都还没完成时返回 `undefined` 而不是 0 —— `0%` 是「一次都没命中」
 * 这个断言,而此刻真实情况是「还不知道」。两者在界面上必须长得不一样。
 */
export function cacheHitRateOf(usage: TokenUsage | undefined): number | undefined {
  if (usage === undefined) return undefined
  const total = promptTokensOf(usage)
  if (total <= 0) return undefined
  return (usage.cacheReadInputTokens ?? 0) / total
}

/**
 * Add one provider response's usage to a child-agent total.
 *
 * ★ `cost` **必须**在下面那个循环之外单独处理。那个循环做的是无差别加法
 * (`(usage[key] ?? 0) + value`),而 cost 是个对象 —— 让它进循环,币种字段会被
 * 加成 `"0USD"` 这种东西。所以两头都先把它解构出来。
 */
function addUsage(previous: RunUsage | undefined, delta: RunUsage): RunUsage {
  const { cost: previousCost, ...previousTokens } = previous ?? {}
  const { cost: deltaCost, ...deltaTokens } = delta
  const usage: RunUsage = { inputTokens: 0, outputTokens: 0, ...previousTokens }
  for (const key of Object.keys(deltaTokens) as Array<keyof typeof deltaTokens>) {
    const value = deltaTokens[key]
    if (value !== undefined) usage[key] = (usage[key] ?? 0) + value
  }
  const cost = addCost(previousCost, deltaCost)
  return cost === undefined ? usage : { ...usage, cost }
}

/**
 * 把一次请求的钱累进这一轮的总额。
 *
 * ★★ **`null` 一旦立起来就不再回头。** 「第 1 次请求查不到价、第 2 次查到了」
 * 若只把第 2 次的钱报上去,会得到一个偏低、却完全合理的总额 —— 没有任何人
 * 会发现它少了一截。这与 `pricing.ts` 全篇「宁可返回 null 也不返回一个像样的 0」
 * 是同一条原则。
 *
 * ★ 币种不同也锁 null:应用里没有汇率源,把 ¥ 和 $ 加起来同样是个看着合理的错数。
 * 这在故障切换到另一家(国内↔国外)时是真会发生的。
 *
 * ★ `delta === undefined`(事件根本没带这个字段)是**没接计价**,不是「算不出」——
 * 保持已有累计值不动,老链路的行为于是分毫不变。
 */
function addCost(
  previous: RunCost | null | undefined,
  delta: RunCost | null | undefined
): RunCost | null | undefined {
  if (delta === undefined) return previous
  if (previous === null || delta === null) return null
  if (previous === undefined) return delta
  if (previous.currency !== delta.currency) return null
  return { micros: previous.micros + delta.micros, currency: delta.currency }
}

/** Reconstruct durable Task-card metadata from persisted tool result messages. */
function applySubagentMessage(
  subagents: TranscriptState['subagents'],
  message: AgentMessage
): TranscriptState['subagents'] {
  let next = subagents
  for (const part of message.parts) {
    if (part.type !== 'tool_result' || part.subagent === undefined) continue
    if (next === subagents) next = { ...subagents }
    writeSubagentResult(next, part, part.subagent)
  }
  return next
}

function writeSubagentResult(subagents: TranscriptState['subagents'], part: Extract<ContentPart, { type: 'tool_result' }>, metadata: SubagentResult): void {
  const previous = subagents[part.callId]
  const metadataStatus = metadata.status ?? (part.isError ? 'error' : 'done')
  const status = metadataStatus === 'error'
    ? 'error'
    : previous !== undefined && previous.status !== 'running'
      ? previous.status
      : metadataStatus
  subagents[part.callId] = {
    ...(previous ?? {
      callId: part.callId,
      childRunId: metadata.childRunId,
      toolCalls: 0,
      toolErrors: 0
    }),
    childRunId: metadata.childRunId,
    status,
    ...(metadata.background === undefined ? {} : { background: metadata.background }),
    ...(metadata.color === undefined ? {} : { color: metadata.color }),
    phase: status === 'running' ? (metadata.background === true ? 'background' : 'starting') : 'finishing',
    ...(metadata.summary === undefined ? {} : { summary: metadata.summary }),
    ...(metadata.error === undefined ? {} : { error: metadata.error }),
    ...(metadata.reportStatus === undefined
      ? (metadata.background === true && status === 'done' && previous?.reportStatus === undefined
          ? { reportStatus: 'pending' as const } : {})
      : { reportStatus: metadata.reportStatus })
  }
}

/** Rebuild durable child-agent cards from session history, retaining live telemetry. */
export function subagentsFromMessages(
  messages: readonly AgentMessage[],
  live: Readonly<TranscriptState['subagents']> = {}
): TranscriptState['subagents'] {
  const subagents: TranscriptState['subagents'] = {}
  const taskCalls = new Map<string, { description?: string; subagentType?: string }>()
  for (const message of messages) {
    for (const part of message.parts) {
      if (part.type === 'tool_result' && part.subagent !== undefined) writeSubagentResult(subagents, part, part.subagent)
      if (part.type !== 'tool_call' || part.name.toLowerCase() !== 'task') continue
      const input = part.input
      const record = typeof input === 'object' && input !== null ? input as Record<string, unknown> : undefined
      taskCalls.set(part.callId, {
        ...(typeof record?.description === 'string' ? { description: record.description } : {}),
        ...(typeof record?.subagent_type === 'string' ? { subagentType: record.subagent_type } : {})
      })
    }
  }
  for (const [callId, info] of taskCalls) {
    const current = subagents[callId]
    if (current === undefined) continue
    subagents[callId] = {
      ...current,
      ...(info.description === undefined ? {} : { description: info.description }),
      ...(info.subagentType === undefined ? {} : { subagentType: info.subagentType })
    }
  }
  for (const [callId, saved] of Object.entries(subagents)) {
    const current = live[callId]
    if (current === undefined) continue
    subagents[callId] = { ...saved, ...current, callId, childRunId: current.childRunId }
  }
  for (const [callId, current] of Object.entries(live)) {
    if (subagents[callId] !== undefined) continue
    subagents[callId] = current
  }
  return subagents
}

/** Committed calls/results are durable; progress and execution timestamps are not. */
function applyToolMessage(tools: TranscriptState['tools'], message: AgentMessage): TranscriptState['tools'] {
  let next = tools
  for (const part of message.parts) {
    if (part.type !== 'tool_call' && part.type !== 'tool_result') continue
    if (next === tools) next = { ...tools }
    writeToolPart(next, part)
  }
  return next
}

function writeToolPart(tools: TranscriptState['tools'], part: Extract<ContentPart, { type: 'tool_call' | 'tool_result' }>): void {
  const previous = tools[part.callId]
  tools[part.callId] = part.type === 'tool_call'
    ? {
        ...previous,
        callId: part.callId,
        name: part.name,
        input: previous?.input ?? part.input,
        status: previous?.status ?? 'pending'
      }
    : {
        ...previous,
        callId: part.callId,
        name: previous?.name ?? '(unknown)',
        input: previous?.input,
        status: part.isError ? 'error' : 'ok',
        output: part.output,
        progress: undefined
      }
}

/** Rebuild from saved messages, retaining available live details for matching calls only. */
export function toolsFromMessages(
  messages: readonly AgentMessage[],
  live: Readonly<TranscriptState['tools']> = {}
): TranscriptState['tools'] {
  const tools: TranscriptState['tools'] = {}
  for (const message of messages) {
    for (const part of message.parts) {
      if (part.type === 'tool_call' || part.type === 'tool_result') writeToolPart(tools, part)
    }
  }
  for (const [callId, saved] of Object.entries(tools)) {
    const current = live[callId]
    if (current === undefined) continue
    tools[callId] = saved.status === 'pending'
      // The history fetch may precede a more recent tool_start/tool_end event.
      ? { ...saved, ...current, name: saved.name }
      : {
          ...current, ...saved,
          name: saved.name === '(unknown)' ? current.name : saved.name,
          input: current.input ?? saved.input
        }
  }
  return tools
}

/**
 * 这份转录背后到底有没有过一个 run。
 *
 * `emptyTranscript()` 的 `status` 是 `'running'`,那是为「run 已经起了、第一个事件
 * 还没到」那一瞬准备的默认值 —— `attach` 上去的时候它就该显示生成中。但**全新会话
 * 用的是同一个初值**,照着渲染就会在一个还没发过任何消息的空会话上写着「生成中」。
 *
 * 分辨这两者的信息不在转录里(`RunStatus` 没有 idle 这一档,也不该为此加一档 ——
 * 那是主进程 RunRegistry 的状态机,不是 UI 的),而在「此刻有没有 activeRunId」。
 * 所以判断要两个入参,不能只看 `status`。
 */
export function hasRun(s: TranscriptState, running: boolean): boolean {
  return running || s.messages.length > 0 || s.live.length > 0
}

/** 找到 index 对应的块并改写;不存在就按 index 升序插进去。 */
function upsertBlock(
  live: LiveBlock[],
  index: number,
  make: () => LiveBlock,
  patch: (b: LiveBlock) => LiveBlock
): LiveBlock[] {
  const at = live.findIndex((b) => b.index === index)
  if (at >= 0) {
    const existing = live[at]
    if (existing === undefined) return live
    const next = [...live]
    next[at] = patch(existing)
    return next
  }
  // 上游按升序发块,但**不保证** —— 并行工具调用时两个块的 start 可能靠得很近。
  // 插入时排序比事后排序省一次遍历,也省掉「谁负责排序」的疑问。
  return [...live, patch(make())].sort((a, b) => a.index - b.index)
}

export function applyEvent(s: TranscriptState, e: AgentEvent): TranscriptState {
  switch (e.type) {
    case 'stream': {
      const d = e.delta
      switch (d.type) {
        case 'message_start':
          /*
            ★ 内容开始流了 = 重试成功,过期的重试提示和错误到此为止。

            ★★ `live: []` 在正常路径上是**无操作** —— `message_commit` 已经清过了
            (见下面那条「提交即清空活跃块」),所以这里 `live` 必空。它存在只为一件事:
            **断流续跑**(`agent-session.ts` 的 `RESUME_DELAYS_MS`)。被丢弃的那次尝试
            没有 commit,只有这一句能把它抹掉;漏掉的话,续跑那次的 `text_delta index:0`
            会**续写**在上一次半截文字的后面,界面上是一段前后接不上的乱码。

            重载后的重放同理:`run-registry` 只在 `message_commit` 时裁剪 delta,
            被丢弃那次的 delta 会原样留在日志里重放一遍,也靠这一句清掉。
          */
          return {
            ...s,
            model: d.model,
            providerId: d.providerId,
            notice: undefined,
            error: undefined,
            live: [],
            lastStreamAt: e.at ?? Date.now()
          }

        case 'text_delta':
        case 'thinking_delta': {
          const kind = d.type === 'text_delta' ? 'text' : 'thinking'
          const at = e.at ?? Date.now()
          return {
            ...s,
            lastStreamAt: at,
            live: upsertBlock(
              s.live,
              d.index,
              // 起点口径见 `LiveBlock.startedAt`:与 block-accumulator 逐条一致
              () => (kind === 'thinking'
                ? { index: d.index, kind, text: '', startedAt: s.lastStreamAt ?? at }
                : { index: d.index, kind, text: '' }),
              (b) => (b.kind === 'thinking' ? { ...b, text: b.text + d.text, endedAt: at } : { ...b, text: b.text + d.text })
            )
          }
        }

        case 'tool_call_start':
          return {
            ...s,
            lastStreamAt: e.at ?? Date.now(),
            live: upsertBlock(
              s.live,
              d.index,
              () => ({ index: d.index, kind: 'tool_use', text: '' }),
              (b) => ({ ...b, kind: 'tool_use', callId: d.callId, name: d.name })
            )
          }

        case 'tool_call_delta':
          return {
            ...s,
            lastStreamAt: e.at ?? Date.now(),
            live: upsertBlock(
              s.live,
              d.index,
              () => ({ index: d.index, kind: 'tool_use', text: '', callId: d.callId }),
              (b) => ({ ...b, text: b.text + d.argsDelta })
            )
          }

        case 'tool_call_end':
          // 参数已经攒齐,但**这里不 JSON.parse** —— 解析是内核的事(ToolCallAccumulator),
          // 而且流式中途的 JSON 一定非法。UI 拿到的 input 来自 tool_start。
          return s

        case 'message_end': {
          // latencyMs 走 upstreamMs 这个名字进状态:事件说的是「这一次请求」,
          // 状态记的是「这一轮的累计」,同一个数在两处的含义不同,名字也不该同。
          //
          // ★ cost 用「字段在不在」传递第三种状态(见 stream.ts 上的表),所以
          // 这里必须条件展开,不能写 `cost: d.cost` —— 那会把「没接计价」变成
          // 一个显式的 undefined,和「算不出」在下游就分不开了。
          const delta: RunUsage = {
            ...d.usage,
            ...(d.latencyMs === undefined ? {} : { upstreamMs: d.latencyMs }),
            ...(d.cost === undefined ? {} : { cost: d.cost })
          }
          return { ...s, usage: addUsage(s.usage, delta), lastInputTokens: promptTokensOf(d.usage) }
        }

        case 'provider_retry':
          return { ...s, notice: { kind: 'retry', attempt: d.attempt, reason: d.reason } }

        case 'provider_switch':
          return { ...s, notice: { kind: 'switch', to: d.to, reason: d.reason } }

        case 'error':
          // 错误框里会写全,状态行不必再挂着一句过期的「正在重试」
          return { ...s, error: d.error, notice: undefined }

        default:
          return s
      }
    }

    case 'message_commit':
      // ★ 提交即清空活跃块。漏掉这一句,已提交的内容会和活跃块同时显示 —— 全文重影。
      {
        const existing = s.messages.findIndex((message) => message.id === e.message.id)
        const messages = existing < 0
          ? [...s.messages, e.message]
          : s.messages.map((message, index) => (index === existing ? mergeGoalStatusMessage(e.message, message) : message))
        return {
          ...s,
          messages,
          live: [],
          tools: applyToolMessage(s.tools, e.message),
          subagents: applySubagentMessage(s.subagents, e.message)
        }
      }

    case 'tool_start':
      return {
        ...s,
        tools: {
          ...s.tools,
          [e.callId]: {
            callId: e.callId,
            name: e.toolName,
            input: e.input,
            status: 'running',
            startedAt: e.at ?? Date.now()
          }
        }
      }

    case 'tool_progress': {
      const prev = s.tools[e.callId]
      if (!prev) return s
      return {
        ...s,
        tools: {
          ...s.tools,
          [e.callId]: {
            ...prev,
            progress: e.progress.message,
            // 实时卡片易失,但一旦推出就覆盖上一张;不推(card 缺省)则保留上一张。
            ...(e.progress.card === undefined ? {} : { card: e.progress.card }),
            // 逐张到达的生图结果按格子序号**累加**(每条进度只带新到的那一张)
            ...(e.progress.image === undefined
              ? {}
              : { partialImages: { ...prev.partialImages, [e.progress.image.index]: e.progress.image.image } }),
            // 同 `card`:推了就以这一条为准,没推则保留上一条的说法
            ...(e.progress.detachable === undefined ? {} : { detachable: e.progress.detachable }),
            // 整份快照,推了就替换(见 shared/agent/workflow.ts 文件头)
            ...(e.progress.workflow === undefined ? {} : { workflow: e.progress.workflow })
          }
        }
      }
    }

    case 'tool_end': {
      const prev = s.tools[e.callId]
      const base: ToolCallState = prev ?? {
        callId: e.callId,
        name: '(unknown)',
        input: undefined,
        status: 'running'
      }
      return {
        ...s,
        tools: {
          ...s.tools,
          [e.callId]: {
            ...base,
            status: e.isError ? 'error' : 'ok',
            output: e.output,
            progress: undefined,
            // 实时卡片也随结束清掉 —— 让展示切到最终 output.card(结果快照);
            // 否则那张过程态的卡会一直盖在结果上。
            card: undefined,
            // 过程态的逐张图同理:结束后以 output.images 为准,不留两份
            partialImages: undefined,
            detachable: undefined,
            // 结束后以 output.workflow 为准
            workflow: undefined,
            endedAt: e.at ?? Date.now()
          }
        }
      }
    }

    case 'notification':
      return { ...s, warning: e.warning }

    case 'context_usage':
      return {
        ...s,
        contextUsage: {
          used: e.used,
          window: e.window,
          shouldCompact: e.shouldCompact,
          // ★ 缺省时**不要补一个空数组**:界面对「还不知道」和「全是 0」要有不同反应。
          ...(e.segments === undefined ? {} : { segments: e.segments })
        }
      }

    case 'context_status': {
      if (e.status.phase !== 'compacted') return { ...s, contextStatus: e.status }
      /*
        需求:压完之后圆环必须立刻回落。`lastInputTokens` 是压缩**之前**那次请求的真值
        (比如 624K),不清掉的话它要等到下一次上游回包才会被覆盖 —— 压缩成功了,
        圆环却还写着「已超出」。清掉后界面退回 session 紧接着发的那条 `context_usage` 估算。
      */
      const { lastInputTokens: _stale, ...rest } = s
      return { ...rest, contextStatus: e.status }
    }

    case 'subagent_start': {
      const previous = s.subagents[e.callId]
      return {
        ...s,
        subagents: {
          ...s.subagents,
          [e.callId]: {
            callId: e.callId,
            childRunId: e.childRunId,
            status: 'running',
            ...(e.childSessionId === undefined ? {} : { childSessionId: e.childSessionId }),
            ...(e.description === undefined ? {} : { description: e.description }),
            ...(e.subagentType === undefined ? {} : { subagentType: e.subagentType }),
            ...(e.color === undefined ? {} : { color: e.color }),
            ...(e.model === undefined ? {} : { model: e.model }),
            ...(e.background === undefined ? {} : { background: e.background }),
            phase: e.background === true ? 'background' : 'starting',
            toolCalls: 0,
            toolErrors: 0,
            lastEventAt: e.at ?? Date.now(),
            ...(e.at === undefined ? {} : { startedAt: e.at }),
            ...(previous?.childRunId === e.childRunId && previous.reportStatus !== undefined
              ? { reportStatus: previous.reportStatus } : {})
          }
        }
      }
    }

    case 'subagent_update': {
      const previous = s.subagents[e.callId]
      if (previous === undefined) return s
      if (e.childSeq !== undefined && previous.childSeq !== undefined && e.childSeq <= previous.childSeq) return s
      return {
        ...s,
        subagents: {
          ...s.subagents,
          [e.callId]: {
            ...previous,
            // ★ 无条件刷新:这一格的意义是「最后一次听到动静」,而不是
            //   「最后一次状态有变化」—— 只在有变化时更新的话,一个反复
            //   report 同一个 phase 的健康子代理会被显示成停住了。
            lastEventAt: e.at ?? Date.now(),
            ...(e.phase === undefined ? {} : { phase: e.phase }),
            // Presence matters here: `currentTool: undefined` is an explicit
            // clear sent by `tool_end`, while an omitted field means "keep the
            // last tool" for telemetry updates that do not change it.
            ...('currentTool' in e ? { currentTool: e.currentTool } : {}),
            ...('currentTarget' in e ? { currentTarget: e.currentTarget } : {}),
            ...('currentTool' in e && typeof e.currentTool === 'string' ? {
              activity: [{ toolName: e.currentTool, target: e.currentTarget, at: e.at }, ...(previous.activity ?? [])].slice(0, ACTIVITY_LIMIT)
            } : {}),
            ...(e.toolCalls === undefined ? {} : { toolCalls: e.toolCalls }),
            ...(e.toolErrors === undefined ? {} : { toolErrors: e.toolErrors }),
            ...(e.usage === undefined ? {} : { usage: addUsage(previous.usage, e.usage) }),
            ...(e.contextUsage === undefined ? {} : { contextUsage: e.contextUsage }),
            // 同 `currentTool`:键在 = 显式设置或清除,键不在 = 保持原样
            ...('notice' in e ? { notice: e.notice } : {}),
            ...(e.childSeq === undefined ? {} : { childSeq: e.childSeq })
          }
        }
      }
    }

    case 'subagent_end': {
      const previous = s.subagents[e.callId]
      if (previous === undefined) return s
      if (e.childSeq !== undefined && previous.childSeq !== undefined
        && (e.childSeq < previous.childSeq || (e.childSeq === previous.childSeq && e.summary === undefined))) return s
      return {
        ...s,
        subagents: {
          ...s.subagents,
          [e.callId]: {
            ...previous,
            status: e.status,
            /*
              ★ **不要**在这里把 phase 改写成 `'finishing'`。

              以前这么写,结果是任何终态卡片都显示「收尾中」—— 不管它是正常
              跑完的、被中断的、还是停在某次工具调用上一小时没动的。排查时
              最想知道的恰恰是**它停在哪一步**,而这一栏当时等于零信息。
              留住 `previous.phase`:正常跑完的本来就已经是 `'finishing'`
              (最后那条 `message_end` 置的),而卡在工具上的会照实说「工具」。
            */
            lastEventAt: e.at ?? Date.now(),
            currentTool: undefined,
            currentTarget: undefined,
            // 终态下错误框会把话说全,顶上再挂一句过期的「正在重试」只会误导
            // —— 和 `applyEvent` 里 `error` 分支清 `notice` 是同一条理由
            notice: undefined,
            ...(e.summary === undefined ? {} : { summary: e.summary }),
            ...(e.error === undefined ? {} : { error: e.error }),
            ...(e.childSeq === undefined ? {} : { childSeq: e.childSeq }),
            ...(e.at === undefined ? {} : { endedAt: e.at }),
            // 回传状态由主进程确认;迟到的结束事件不能把 injecting/reported 打回 pending。
            ...(previous.background === true && previous.reportStatus === undefined
              ? { reportStatus: 'pending' as const } : {})
          }
        }
      }
    }

    case 'run_end':
      return {
        ...s,
        status: e.status,
        runEndedAt: e.at ?? Date.now(),
        ...(e.error ? { error: e.error } : {})
      }

    default:
      // interaction_* 已由交互面板接管。
      return s
  }
}

export function applyEvents(s: TranscriptState, events: readonly AgentEvent[]): TranscriptState {
  return events.reduce(applyEvent, s)
}

/**
 * Project a child run's own events onto the matching parent Task card. Child
 * events arrive on the inherited IPC topic, so this keeps background runs
 * observable even after the parent run has reached its terminal state.
 */
export function applyChildEvent(
  s: TranscriptState,
  childRunId: string,
  e: AgentEvent,
  childSeq?: number
): TranscriptState {
  const entry = Object.values(s.subagents).find((item) => item.childRunId === childRunId)
  if (entry === undefined) return s
  // Parent telemetry and the inherited child topic describe the same event.
  // Whichever arrives first records the child sequence; the other is ignored.
  if (childSeq !== undefined && entry.childSeq !== undefined && childSeq <= entry.childSeq) return s
  const update = (patch: AgentEvent): TranscriptState => applyEvent(s, patch)
  const childUpdate = (patch: Extract<AgentEvent, { type: 'subagent_update' | 'subagent_end' }>): TranscriptState => update(
    childSeq === undefined ? patch : { ...patch, childSeq }
  )

  switch (e.type) {
    case 'message_commit': {
      // A detached/background child may finish after the parent run has ended,
      // so there is no parent `subagent_end` event carrying the final text.
      // Its committed assistant message is still enough to populate the card.
      const text = visibleText(e.message).trim()
      if (e.message.role !== 'assistant' || text === '') return s
      return {
        ...s,
        subagents: {
          ...s.subagents,
          [entry.callId]: {
            ...entry,
            summary: text.slice(0, 240),
            // 这一支绕开了 `childUpdate`,所以得自己刷一次 —— 漏掉的话,
            // 一个只提交消息、不报遥测的后台子代理会显示成「很久没动静」。
            // `message_commit` 不带 `at`(见 `event.ts`),只能用本地时钟。
            lastEventAt: Date.now(),
            ...(childSeq === undefined ? {} : { childSeq })
          }
        }
      }
    }
    case 'stream':
      if (e.delta.type === 'message_start') {
        // 上游开始回话了 = 刚才那次退避成功了,提示到此为止(显式清除)
        return childUpdate({ type: 'subagent_update', callId: entry.callId, childRunId,
          phase: 'thinking', notice: undefined, at: undefined })
      }
      if (e.delta.type === 'message_end') {
        return childUpdate({ type: 'subagent_update', callId: entry.callId, childRunId,
          phase: 'finishing', usage: e.delta.usage })
      }
      /*
        ★★ 这两条以前落在下面那个 `return s` 上,于是**子代理的重试全程不可见**:
        卡片上只有一个转圈的「运行中」,退避几次全失败之后直接跳到错误框 ——
        看起来像它一次都没重试过。而重试就发生在 `router.ts` 那两层循环里,
        主代理和子代理走的是同一份代码,区别只在于当时没人画子代理这一份。
      */
      if (e.delta.type === 'provider_retry') {
        return childUpdate({ type: 'subagent_update', callId: entry.callId, childRunId,
          notice: { kind: 'retry', attempt: e.delta.attempt, reason: e.delta.reason } })
      }
      if (e.delta.type === 'provider_switch') {
        return childUpdate({ type: 'subagent_update', callId: entry.callId, childRunId,
          notice: { kind: 'switch', to: e.delta.to, reason: e.delta.reason } })
      }
      return s
    case 'tool_start':
      return childUpdate({ type: 'subagent_update', callId: entry.callId, childRunId,
        phase: 'tool', currentTool: e.toolName, currentTarget: toolTargetOf(e.input), toolCalls: entry.toolCalls + 1 })
    case 'tool_end':
      return childUpdate({ type: 'subagent_update', callId: entry.callId, childRunId,
        phase: 'thinking', currentTool: undefined, currentTarget: undefined, toolErrors: entry.toolErrors + (e.isError ? 1 : 0) })
    case 'context_usage':
      return childUpdate({ type: 'subagent_update', callId: entry.callId, childRunId,
        contextUsage: { used: e.used, window: e.window, shouldCompact: e.shouldCompact } })
    case 'run_end':
      return childUpdate({ type: 'subagent_end', callId: entry.callId, childRunId,
        status: e.status, ...(e.error === undefined ? {} : { error: e.error }), at: e.at })
    default:
      return s
  }
}

function toolTargetOf(input: unknown): string | undefined {
  if (typeof input === 'string') return input.slice(0, 160)
  if (input === null || typeof input !== 'object') return undefined
  const record = input as Record<string, unknown>
  for (const key of ['path', 'filePath', 'query', 'pattern', 'command', 'url']) {
    const value = record[key]
    if (typeof value === 'string' && value.trim() !== '') return value.slice(0, 160)
  }
  return undefined
}

/** 活跃块里的纯文本 —— 「正在打字」的那一段。 */
export function liveText(s: TranscriptState): string {
  return s.live
    .filter((b) => b.kind === 'text')
    .map((b) => b.text)
    .join('')
}
