/**
 * 一次运行的全部输入 —— 方案 §4.8。
 *
 * ★ runId 在这里面,是因为它**由渲染层生成并传入**,不由 agent:run 返回(方案 §3 规则 2)。
 * 否则存在竞态:invoke 的 promise resolve 前内核已吐了三个事件,而渲染层还不知道 runId,
 * 这些事件被直接丢弃。渲染层先 mint 一个 ULID,**订阅在前、启动在后**。
 */
import type { ContentPart } from './message'
import type { PermissionMode } from './permission'
import type { ModeId } from '../domain/mode'
import type { PlanExecutionRef } from '../domain/plan-file'
export type { PlanExecutionRef, PlanFileRef } from '../domain/plan-file'

/** Stable id of a built-in or user-defined conversation mode. */
export type SessionMode = ModeId

/** 界面:自动/极低/低/中/高/超高/最高/关闭 */
export type ThinkingLevel =
  | 'auto'
  | 'off'
  | 'minimal'
  | 'low'
  | 'medium'
  | 'high'
  | 'higher'
  | 'max'

export const THINKING_LEVELS: readonly ThinkingLevel[] = [
  'auto',
  'minimal',
  'low',
  'medium',
  'high',
  'higher',
  'max',
  'off'
]

/**
 * 思考强度的**有序**序列,从弱到强。
 *
 * 需求:旁路请求(压缩 / 目标判定 / 权限审核 / 会话标题)在「想要的档位这个模型不支持」时
 * 要降到**最近的可用档**,而不是让整次请求失败。没有这条序就只能落回 `'auto'` ——
 * 那等于把用户选的强度静默换成模型默认值。落点见 `domain/model-runtime.ts` 的
 * `auxiliaryThinkingLevel`。
 *
 * ★ 和 `THINKING_LEVELS` **不是一回事,不要合并**:那一条是下拉列表的排序
 * (`'auto'` 在首位因为它是缺省项,`'off'` 在末位因为它是「反向」的那一个),
 * 拿它的下标比大小会得出「off 比 max 还强」。这里 `'auto'` **故意不在轴上** ——
 * 它的含义是「模型自己决定」,没有强度可比。
 */
export const THINKING_STRENGTH: readonly ThinkingLevel[] = [
  'off',
  'minimal',
  'low',
  'medium',
  'high',
  'higher',
  'max'
]

export const THINKING_LEVEL_LABEL: Record<ThinkingLevel, string> = {
  auto: '自动',
  minimal: '极低',
  low: '低',
  medium: '中',
  high: '高',
  higher: '超高',
  max: '最高',
  off: '关闭'
}

/**
 * ThinkingLevel 映射成上游的 thinking 预算;界面原文
 * 「不支持该参数的模型将自动忽略此设置」→ 由 ModelAlias.capabilities.thinking 决定是否下发。
 * ★ 实际预算还必须低于下面解析出的输出上限;32K 封顶后,「超高/最高」可能收敛到同一安全值,
 * 否则 Anthropic 的 `budget_tokens >= max_tokens` 会让整次请求直接 400。
 */
export const THINKING_BUDGET: Record<Exclude<ThinkingLevel, 'auto' | 'off'>, number> = {
  minimal: 1024,
  low: 4096,
  medium: 10_000,
  high: 21_333,
  higher: 32_000,
  max: 64_000
}

/**
 * 正文请求每轮输出额度的**默认值**,同时是设置项 `AppSettings.maxOutputTokens`
 * 的出厂值。32K 这个数的由来没变:大输出模型不该从空会话起就挤占上下文压力读数,
 * 也不该让压缩过早触发。
 */
export const DEFAULT_MAX_OUTPUT_TOKENS = 32_000

/**
 * 需求:每轮输出额度由**唯一一个全局设置项**说了算(设置 › 通用 › Agent),
 * 模型目录里那条 `maxOutputTokens` 不再参与 —— 在一处改完就该对所有模型生效。
 *
 * 原先这里取 `min(32000, alias.maxOutputTokens)`,理由是「模型更小的协议输出上限
 * 仍是硬边界,否则旧模型每次请求都直接 400」。那条边界现在退到上下文窗口:
 * 输出额度大于整个窗口的请求必然 400,而那是唯一一条与模型声明无关、永远成立的
 * 硬约束。目录里声明得偏小的输出上限(常见 16384)不再压住用户设的值 ——
 * 症状是设置里写着 32000、请求里发出去的却是 16384,长回答被上游截断,
 * 最后报 `agent.error.outputLimit`。
 *
 * 代价写在这里,免得下一个人当成 bug「修」回去:供应商若真的只接受更小的
 * max_tokens,这一改把那次失败从「静默截断」变成一次显式的上游 400,
 * 用户照着报错把这个设置项调小即可 —— 一处改,不必逐个模型改。
 */
export function resolveMaxOutputTokens(
  configured: number | undefined,
  contextWindow: number | undefined
): number {
  const wanted = typeof configured === 'number' && Number.isFinite(configured) && configured >= 1
    ? Math.floor(configured)
    : DEFAULT_MAX_OUTPUT_TOKENS
  return typeof contextWindow === 'number' && Number.isFinite(contextWindow) && contextWindow >= 1
    ? Math.min(wanted, Math.floor(contextWindow))
    : wanted
}

export interface RunRequest {
  /** ★ 渲染层 mint 的 ULID,订阅在前、启动在后 */
  runId: string
  sessionId: string
  workspaceId: string

  /** 子代理:父 run 的 id 与深度。depth 0 = 主 run */
  parentRunId?: string
  /**
   * ★ 父**会话**,不是父 run —— 两者生命周期差得很远:run 结束就被 reap,
   * 而这一项要一路写进 `sessions.parent_session_id`,活到用户删掉那条对话为止。
   * 它是「这条转录不进任何列表」和「删父会话时跟着回收」的唯一依据。
   */
  parentSessionId?: string
  depth: number

  /** 本轮用户输入。空数组 = 继续跑(排队消息之外的续跑场景) */
  input: ContentPart[]
  /**
   * 渲染层先显示的用户消息 ID。主进程提交同一个 ID,
   * 这样确认事件会替换乐观消息,而不会在转录里产生重复内容。
   */
  inputMessageId?: string
  /**
   * 本轮输入是**协调消息**(目前只有后台子代理的结果回传),发给模型但不进对话流。
   *
   * ★ 没有它的时候,`send(..., internal)` 只把标记写在渲染层那条乐观消息上,
   * 而主进程 commit 回来的那条**不带标记** —— `message_commit` 整条替换之后
   * 标记就没了,于是那段 `Background subagent result (...)` 会作为一条普通用户
   * 消息裸露在对话里(插话那条路没有这个问题,它的 internal 一直是传过去的)。
   */
  inputInternal?: boolean
  /** Identity of a goal coordination input; stale wake-ups must not start a run. */
  inputGoalId?: string

  mode: SessionMode
  thinking: ThinkingLevel
  /** 即使 full 档,这个开关关掉时网络类工具一律拒绝(方案 §4.5) */
  webSearch: boolean
  /**
   * 「最大上下文」的 run 快照 —— 决定 `shouldCompact` 的分母是 272K 还是模型的协议窗口。
   * 缺席 = 按 false 走(旧队列条目、旧 run、子代理 frontmatter 里都没有这一项)。
   */
  maxContext?: boolean
  /** ★ 快照:run 开始时定死,运行期不变 —— 界面说的「下一次新回复生效」 */
  permissionMode: PermissionMode
  /** ModelAlias.alias,不是上游真实模型名 */
  model: string
  /**
   * ★ 用户在药丸里**显式选定**的供应商,是一条**硬约束**:路由器只在这一家里
   * 重试,失败就报错,**不会切到同名别名的另一家**。理由是别名撞名很常见
   * (两家都叫 `gpt-5.6-sol`),而「我选了 Codex 结果发给了 RoutinAI」既看不出来
   * 也算不清账。
   *
   * 缺席 = 没指定过(历史 run、子代理 frontmatter 里的裸别名),此时按
   * `provider.priority` 择优 —— 也就是这个字段引入之前的全局行为。
   */
  modelProviderId?: string
  /** 本轮激活的 Skill */
  skillIds: string[]
  /** 工作区 Skill 白名单语义的 run 快照；缺省按旧版 all 处理。 */
  skillSelectionMode?: 'all' | 'explicit'

  /**
   * 这个 run 是哪个子代理在跑(`agents/<name>.md` 的 name)。缺省 = 主 run。
   *
   * ★ 放在 `RunRequest` 里而不是当成启动器的一个私有参数,是因为它得
   * **活得和 run 一样久**:重扫目录之后 `AgentDefinition` 对象会被整体换掉,
   * 而一个正在跑的子 run 仍然要能说出「我是谁」。名字是稳定的,对象不是。
   */
  agentType?: string
  /** Approved Markdown plan file injected by the main process for an execution run. */
  planExecution?: PlanExecutionRef
  /**
   * 对话里 widget 经 `host.setContext()` 发布的状态摘要,随这条消息交给模型一次
   * (进系统提醒尾块,不进转录、不显示在气泡里)。用户在输入框上方看得见、能移除。
   *
   * ★ 内容是模型写的 widget 产出的,在 `context-assembler.ts` 里按**不可信材料**包起来。
   */
  widgetContext?: Array<{ title: string; text: string }>
}

/**
 * 发起一次 run 所需的档位 —— `RunRequest` 去掉三个「每次都要重新决定」的字段。
 *
 * ★ 定义在 shared 而不是 `stores/session.ts`,是因为**队列条目要逐条冻结它**
 * (`QueuedInput.options`),而 `shared/domain/queued-input.ts` 不能反向依赖渲染层。
 * `session.ts` 仍然 re-export 同名类型,既有引用点不受影响。
 */
export type SendOptions = Omit<RunRequest, 'runId' | 'sessionId' | 'input' | 'inputMessageId' | 'inputInternal' | 'inputGoalId'>

/**
 * Deprecated compatibility exports. AgentSession no longer uses a fixed turn
 * count to terminate runs.
 */
/** @deprecated Runs are no longer stopped after a fixed number of turns. */
export const MAX_TURNS = 25
/** A run has no application-imposed turn limit. */
export const UNLIMITED_TURNS = Number.POSITIVE_INFINITY
/** 没有深度上限的子代理会指数级烧钱(方案 §4.9) */
export const MAX_DEPTH = 2

/** @deprecated Use the run lifecycle/abort signal; retained for API compatibility. */
export function maxTurnsFor(mode: SessionMode): number {
  void mode
  return UNLIMITED_TURNS
}
