/**
 * AgentSession —— think → tool → observe 循环(方案 §4.8)。
 *
 * 这是「我们自己拥有这个循环」那个决定的兑现处(见方案 Context)。约 300 行,
 * 换来的是权限过滤、会话模式、子代理、中断收尾都真的归我们管。
 *
 * ★ 零 electron import,也**零时钟读取** —— `now` 一律走 `host.clock`。
 * 所以整个循环能在普通 vitest 里跑完,不启动 Electron、不联网、不看真时间。
 *
 * ★ session **不重试、不做故障切换**(方案 §5.3):那两件事在 `UpstreamRouter` 里。
 * 放在这里会重放已经执行过的工具调用 —— 一次「重试」把文件写两遍。
 */
import type { AgentError } from '../../shared/agent/error'
import { agentError } from '../../shared/agent/error'
import type { AgentMessage, ContentPart, ToolOutput } from '../../shared/agent/message'
import {
  assistantMessage,
  MAX_TOOL_OUTPUT_CHARS,
  orphanedToolCalls,
  toolResultMessage,
  truncateToolOutput,
  userMessage
} from '../../shared/agent/message'
import type { PermissionDecision } from '../../shared/agent/permission'
import { resolveMaxOutputTokens, type RunRequest } from '../../shared/agent/run-request'
import type { ProviderStreamEvent, StopReason } from '../../shared/agent/stream'
import type { ToolInfo, ToolResult } from '../../shared/agent/tool'
import type { ModelAlias } from '../../shared/domain/provider'
import {
  modelSupportsTools,
  validateModelRuntime
} from '../../shared/domain/model-runtime'
import type { Skill } from '../../shared/domain/skill'
import type { SchedulingBridge } from '../../shared/domain/scheduled'
import type { ShellBridge } from '../../shared/domain/shell'
import type { ImageGenBridge } from './image-gen'
import type { SessionImageStore } from './session-images'
import type { VideoGenBridge } from './video-gen'
import type { SessionVideoStore } from './session-videos'
import type { PlanExecutionContext } from './plan-execution'
import { fileReferenceMatches, type FileReferenceSource } from '../../shared/domain/attachment'
import { EnvironmentError } from '../../shared/domain/environment'
import { ulid } from '../../shared/util/id'
import { abortable, abortableSleep, abortableStream, isAbortError } from './abort'
import { BlockAccumulator, type PendingCall } from './block-accumulator'
import { assemble } from './context-assembler'
import { estimateMessages } from './context-assembler'
import { messagesForModel } from '../../shared/agent/compaction'
import { effectiveContextWindow } from '../../shared/agent/context-management'
import { compactConversation, MAX_CONSECUTIVE_COMPACT_FAILURES } from './compaction/compact'
import type { SummaryRequest } from './compaction/compact'
import { resolveCompactBinding, type CompactBinding, type CompactionSettings } from './compaction/binding'
import type { WorkspaceCompactionOverride } from '../../shared/domain/compaction-model'
import { readAttachableFile, type AttachmentToolNames } from './compaction/attachments'
import { resolvePath } from './tool/builtin/paths'
import { promptTokensOf } from '../../shared/agent/transcript'
import type {
  ContextManagementSettings,
  PersonalizationSettings
} from '../../shared/domain/settings'
import type { GitContext } from './git-context'
import type { KernelHost } from './host'
import type { InteractFn } from './interaction-gate'
import type { RunHandle } from './run-registry'
import { displayPath } from './tool/path-guard'
import type { SpawnSubagentFn, Tool, ToolContext, ToolRegistry } from './tool/registry'
import type { CanonicalRequest, UpstreamRequestContext } from './upstream/canonical'

/**
 * session 需要上游的**全部**能力 —— 就这两个方法。
 *
 * ★ 刻意不写成 `UpstreamRouter` 类型:`UpstreamRouter` 结构上满足它,
 * 所以单测传一个十几行的假上游就能跑完整个循环,不必搭出供应商、凭证、健康表。
 * 这个窄口子是「无头 vitest 是后续每一步的回归网」(方案 §12 步骤 4)成立的前提。
 */
export interface SessionUpstream {
  stream(
    req: CanonicalRequest,
    signal: AbortSignal,
    context: UpstreamRequestContext
  ): AsyncIterable<ProviderStreamEvent>
  listModels(): ModelAlias[]
  /**
   * 「这个别名 + 这个供应商」落在哪一条绑定上。★ 别用 `listModels().find(...)`
   * 自己查 —— 那取的是数组第一条,而路由器取的是 priority 最小的那条,撞名时
   * 会得到不同的答案(于是按 A 的上下文窗口校验、把请求发给 B,还不报错)。
   */
  resolveModel(model: string, modelProviderId?: string): ModelAlias | undefined
}

/**
 * 工具审批的接缝(方案 §4.5 / §4.6)。步骤 5 把 PermissionGate + InteractionGate
 * 接到这里;在那之前默认放行。
 *
 * 单个对象入参是为了以后加字段不改调用方 —— 步骤 5 要用它拼出
 * `PendingInteraction`,而那个形状需要 runId / callId / 工具元数据全套。
 */
export type ApproveFn = (req: {
  runId: string
  callId: string
  tool: Tool
  input: unknown
  signal: AbortSignal
}) => Promise<PermissionDecision>

export interface SessionDeps {
  host: KernelHost
  workspace?: import('./host').WorkspaceHost
  fileReferenceSource?: FileReferenceSource
  upstream: SessionUpstream
  tools: ToolRegistry
  workspaceRoot: string
  /**
   * 已有的转录。**session 不负责持久化** —— 它只把结果留在 `history` 里,
   * 由调用方决定写不写盘。步骤 6 的 SQLite 就接在这个缝上。
   */
  history?: readonly AgentMessage[]
  /** Merge UI-only metadata before the persistence boundary; context keeps the original copy. */
  prepareMessage?: (message: AgentMessage) => AgentMessage
  onMessageCommit?: (message: AgentMessage) => void
  /** Reject coordination queued for a goal that was cleared or replaced. */
  acceptsGoalInput?: (goalId: string) => boolean
  /** 上游响应结束后执行工具；把真实执行结果回填到产生这些调用的用量记录。 */
  onToolUsage?: (summary: { runId: string; toolCalls: number; toolErrors: number }) => void
  /**
   * 工具跑完之后的钩子（PostToolUse）。
   *
   * ★ 只能**追加反馈**，不能改 `output` —— 改的话转录里那段文本和模型收到的那段
   *   就不是同一份了，而事后没有任何办法分辨用户看到的是哪一份。
   * ★ 调用点在 `tool_end` emit **之前**，这样 UI 卡片上显示的就是模型看到的那份。
   */
  onToolExecuted?: (info: {
    tool: Tool
    input: unknown
    output: ToolOutput
    isError: boolean
    callId: string
  }) => Promise<{ additionalContext?: string; isError?: boolean } | undefined>
  skills?: readonly Skill[]
  approve?: ApproveFn
  interact?: InteractFn
  canProposeGoal?: () => boolean
  proposeGoal?: (condition: string, askUser: boolean) => Promise<'set' | 'pending'>
  /**
   * 定时任务的读写通道。缺省 = 这个环境里排不了程,四个定时任务工具整体不下发。
   *
   * ★ 形状同 `spawnSubagent`:内核只认这个窄接口,「store 在哪、写完要不要重排
   * 调度器」全部留在 `main/scheduled/bridge.ts`。内核仍然零 electron、可单测。
   */
  scheduling?: SchedulingBridge
  /**
   * Agent 的 shell 注册表(前台停止句柄 + 后台进程)。缺省 = 这个环境里没有它,
   * `BashOutput` / `KillShell` 不下发,`Bash` 的后台开关当场说明原因。
   *
   * ★ 形状同 `scheduling`:内核只认这个窄接口,「进程怎么起、SSH 租约谁拿着」
   * 全部留在 `main/agent-shells.ts`。内核仍然零 electron、可单测。
   */
  shells?: ShellBridge
  /**
   * 对话内生图的通道。缺省 = 这个环境里生不了图,`generate_image` 整体不下发。
   *
   * ★ 形状同 `scheduling` / `shells`:内核只认这个窄接口,供应商选择、密钥、
   * `/images/generations` 请求全部留在 `main/kernel/image-gen.ts`。内核零 electron、可单测。
   */
  imageGen?: ImageGenBridge
  /** 设置 › 实验 的现读口。见 `ToolContext.experimentalEnabled` */
  experimentalEnabled?: ToolContext['experimentalEnabled']
  /** 见 `ToolContext.subagentModels` */
  subagentModels?: ToolContext['subagentModels']
  /**
   * 本会话的图片仓(生成图 → `ncw://` 会话附件;地址 → 字节)。缺省 = 纯内核测试,
   * 生成图留在内联 data URL、`SaveImage` 不下发。形状与理由同 `imageGen`,
   * 见 `kernel/session-images.ts` 文件头。
   */
  sessionImages?: SessionImageStore
  /**
   * 视频生成的后台通道。缺省 = 这个环境里生不了视频,`generate_video` 不下发。
   *
   * ★ 与 `imageGen` 的形状差别就是整个模块的核心:视频是**异步**的,
   * 所以桥上有 `submit` / `status` / `cancel` 三个动词,而不是一个
   * "提交并等结果"(见 `video-gen.ts` 的文件头)。
   */
  videoGen?: VideoGenBridge
  /**
   * 本会话的视频仓:\`SaveVideo\` 按 \`ncw://\` 地址读回字节。
   * 缺省 = 纯内核测试没装配,`SaveVideo` 不下发。
   */
  sessionVideos?: SessionVideoStore
  /**
   * 回合末的一次询问 —— 「这一轮真的可以停了吗」。
   *
   * ★★ **只有主 run 装配**（装配点在 `main/runtime.ts`）。子 run 没有「停止」这回事：
   *   它的结局是把结论交回父代理，没有人在等它满足一个会话级的条件。
   *   不装配意味着 `depth > 0` 时这个 `await` 是 `undefined?.()` —— 零开销，
   *   也没有第二条「要不要判定」的判断路径。
   *
   * 返回 `continue` 时内核把 `inject` 提交成一条 **internal** 用户消息并继续循环；
   * 返回 `finish` 或 `undefined` 时按原路径收尾。
   *
   * ★ 形状参照 `onToolExecuted`：内核只定义缝，「判定器是谁、钩子从哪来」
   *   全部留在 `main/`。内核仍然零 electron、可单测。
   */
  onTurnEnd?: (input: TurnEndInput) => Promise<TurnEndResult | undefined>
  /**
   * 收窄本轮工具快照的**额外**闸门。
   *
   * ★ plan 模式的 `readOnlyOnly` **不走这里** —— 那是模式的定义,不是配置。
   * 这条缝只接一种来源:子代理定义文件里的 `tools:`(已归一化成 internalId)。
   * 缺省 = 不收窄。
   *
   * `snapshot()` 结构上只过滤、不新增,所以这个清单**永远不可能提权**:
   * 写一个不存在的名字进去,结果是少一个工具,不是多一个。
   */
  allowedTools?: readonly string[] | (() => readonly string[] | undefined)
  /** Dynamic path fence used by file-backed Plan mode. */
  writeFileRestriction?: () => string | undefined
  /**
   * Plan 模式当前那份计划文件(工作区相对路径),每轮现取。
   *
   * ★ 必须是函数:计划可能在**本轮中途**才由 `EnterPlanMode` 产生。
   * 每轮注入的理由见 `runtime.ts` 里装配它的那段 —— 压缩会清空历史工具输出,
   * 而路径原本只存在于那条 `tool_result` 里。
   */
  planFile?: () => string | undefined
  /** Resolved built-in or custom mode instructions. */
  modePrompt?: string
  /**
   * 子代理的角色提示词。★ **追加**在 `BASE_PROMPT` 之后,不替换它 ——
   * 换掉基础提示词的子代理会丢掉「被拒绝时不要试图绕开」那一类约束,
   * 而一个会绕开约束的子代理正是这整套权限设计最不想要的东西。
   */
  agentPrompt?: string
  /** 派子代理。缺省 = 这个环境里派不了(纯内核测试),`Task` 会当场说清楚。 */
  spawnSubagent?: SpawnSubagentFn
  /**
   * 以下两项注入进**这一轮发出去的那份消息流**,转录一个字都不动
   * (`context-assembler.ts` 的 `decorate`)。两个都是 run 开始时读一次的快照。
   */
  projectInstructions?: string
  git?: GitContext
  /**
   * 「偏好 › 个性化」。★ 和上面两项**不同的是它进系统提示词,不进消息流** ——
   * 它是 run 级常量,放进每轮现算的 reminder 里等于每轮为同一句话
   * 重破一次 prompt cache(理由同 `SystemPromptInput` 里 `permissionMode` 那段)。
   */
  personalization?: PersonalizationSettings
  planExecution?: PlanExecutionContext
  /**
   * 提炼会话的头块(`kernel/skill/extraction.ts` 拼好的整块)。run 开始时读一次的快照,
   * 只对主 run 给;缺省 = 这不是提炼会话。注入位置与理由见 `ReminderContext.skillExtraction`。
   */
  skillExtraction?: string
  contextManagement?: ContextManagementSettings
  /**
   * 设置 › 通用 › Agent 的压缩模型那一对 + 档位,run 开始时的快照(同 `maxOutputTokens`)。
   * 缺省 = 纯内核测试没给设置,按「跟随会话模型 / 跟随本轮档位」—— 也就是这两栏
   * 出现之前的行为。三档来源在 `shared/domain/compaction-model.ts`。
   */
  compaction?: CompactionSettings
  /** 这个工作区对上面那组的覆盖(圆环菜单里的两栏)。缺席的字段 = 跟随全局。 */
  workspaceCompaction?: WorkspaceCompactionOverride
  /**
   * 设置 › 通用 › Agent 的「最大输出 Token」,run 开始时的快照。
   *
   * 需求:输出额度只有这一个真源(`AppSettings.maxOutputTokens`),内核不再从
   * 模型目录推。缺省 = 纯内核测试没给设置,退回 `DEFAULT_MAX_OUTPUT_TOKENS`。
   */
  maxOutputTokens?: number
  /**
   * 断流续跑的退避表。缺省 `RESUME_DELAYS_MS`(见那条常量上面的长注释)。
   *
   * ★ 存在的理由和 `UpstreamRouter` 的 `baseDelayMs` 一样:测试里传 `[0, 0, 0]`
   * 就能跑完整条续跑路径,不必上假时钟。传 `[]` 等于关掉续跑。
   */
  resumeDelaysMs?: readonly number[]
}

export interface TurnEndInput {
  sessionId: string
  workspaceId: string
  runId: string
  /** 到这一刻为止的完整转录 —— 判定器的输入就是它。 */
  messages: readonly AgentMessage[]
  isSubagent: boolean
  /**
   * 本 run 至今调用过的工具总数。
   *
   * ★ 刹车判据的来源。`main/` 那层只知道「这一轮模型有没有要求工具」，
   *   它连工具执行都看不到 —— 所以这个数必须由内核数。
   */
  toolCallsThisRun: number
  /**
   * 连续「什么都没做就想停」的轮数。任何一次带工具的回合把它清零。
   *
   * ★ 它拦的是「模型什么都不做还想停」，**不是**「长任务跑了很久」：
   *   一个老实干活的目标可以跑几百轮而从不触发。这正是不加总迭代上限的理由。
   */
  stoppedTurnStreak: number
  /** Has a Stop hook already been evaluated in this run (not reset by tools)? */
  stopHookActive?: boolean
  signal: AbortSignal
}

export interface TurnEndResult {
  kind: 'continue' | 'finish'
  /**
   * 继续时注入的内容。★ 内核会把它提交成 `internal` 用户消息 —— 它是协作消息，
   * 不是用户说的话，不该出现在聊天气泡里（同 `InterjectItem.internal` 的理由）。
   */
  inject?: ContentPart[]
  /** 一条只进 UI 那一轨的状态标记（达成 / 未达成 / 判为不可能 / 已清除）。 */
  goalStatus?: Extract<ContentPart, { type: 'goal_status' }>
  /**
   * 一条用户可见的警告（连续空转被强停之类）。走和 `goalStatus` 同一条附加路径。
   *
   * ★ 用 `AgentError` 而不是裸字符串：`messageKey` 那条既有约定让渲染层按 i18n
   *   翻译本地产生的文案，而这条警告正是本地产生的。
   */
  warning?: AgentError
  /** A deferred/idle-brake finish must first accept coordination received during evaluation. */
  acceptPendingInput?: boolean
  /** 判定的文字结论，给日志与遥测用。 */
  note?: string
}

/**
 * `turn()` 的三种结局。
 *
 * ★ 写成判别联合而不是「返回空 calls 数组表示继续」：后者会让 `executeAll([])`
 *   跑一次空循环，并且往用量账本里写一条 0 工具的记录 —— 一个跑两百轮的目标
 *   会留下两百条那样的假记录。
 */
type TurnOutcome =
  | { kind: 'tools'; calls: PendingCall[]; tools: Map<string, Tool> }
  | { kind: 'continue' }

/**
 * 断流之后自己续跑几次,每次之前等多久。
 *
 * ## 为什么 session 层需要这个(而 `router.ts` 的重试不够)
 *
 * `UpstreamRouter` 只在**还没吐出第一个内容字节之前**重试或切换供应商 ——
 * 一旦上游已经吐了 500 个 token 和一个 tool_use 块,重发会产生重复输出和
 * 错位的工具调用(router.ts §5.3 那五行)。那条边界是对的,不该放宽。
 *
 * 于是「流吐到一半被掐断」在 router 眼里是一个**硬错误**,原样交给这里;
 * 而这里以前对任何 `streamError` 一律 `finish('error')`,整个 run 当场死掉。
 * 主代理死了还有人能重新提问,**子代理没有人** —— `Task` 直接把失败汇报给父代理,
 * 那一分多钟的工作全部作废。
 *
 * ## 为什么放在这一层是安全的
 *
 * `router.ts` 文件头写着「重试绝不放在 session 里 —— 会重放已经执行过的工具调用」。
 * 那说的是 **run 级**重放(从消息历史重跑整个 run)。而流错误发生的那一刻,
 * **本轮一个工具都还没执行** —— `executeAll` 在 `loop()` 里、`turn()` 返回之后才跑。
 * 重发单轮请求重放的工具副作用是**零个**。
 *
 * ## 代价
 *
 * 断流那一次已经生成的内容**整个丢弃**、重新生成,所以这个数组的长度直接等于
 * 最坏情况的重复计费倍数。保留半截回答的做法试过是更糟的:历史会以一条 assistant
 * 消息结尾(Anthropic 的 prefill 语义 + 尾部空白 400),对话里还多出一个割裂的气泡。
 */
const RESUME_DELAYS_MS: readonly number[] = [1_000, 2_000, 5_000, 15_000, 45_000]

const INTERRUPTED = '[interrupted: the user stopped the run before this tool call finished]'
const RECOVERED_INTERRUPTED =
  '[not executed: the previous run ended before this tool call produced a result]'

/** 历史里对不上当前运行环境的文件引用 → 发给模型的替代文本(与上一条同样是模型侧英文)。 */
const foreignReference = (name: string): string =>
  `[attachment "${name}" omitted: it belongs to a different workspace environment and has no usable path here]`

/** 工具执行期间的异常收敛(中断除外)。`defineTool` 已经做过一遍,但 MCP 工具是直接注册的。 */
function toolThrewError(err: unknown): string {
  return err instanceof Error ? `${err.name}: ${err.message}` : String(err)
}

function toRunError(err: unknown): AgentError {
  if (isAbortError(err)) return agentError('aborted', '已中断')
  return agentError('unknown', err instanceof Error ? err.message : String(err))
}

export class AgentSession {
  private readonly messages: AgentMessage[]
  /** 当前上下文窗口使用的投影；完整 messages 仍保留用于持久化和后续摘要。 */
  private contextMessages: AgentMessage[]
  /**
   * ★ 当前轮次正在累积的块。中断时**唯一**能把半截回复救回来的东西 ——
   * 用户已经在屏幕上读到的字,不能因为他点了停止就凭空消失。
   */
  private pending: BlockAccumulator | null = null
  /**
   * 上一次上游回报的真实提示词大小,以及它覆盖到 `contextMessages` 的第几条。
   *
   * 需求:自动压缩判据要读「上一次真值 + 其后新增消息的估算」(Claude Code 的
   * `tokenCountWithEstimation`)。只读 chars/4 估算的话,代码 / JSON / CJK 偏低 25%+,
   * 用户看着圆环过了窗口,判据却说还早 —— 这正是「到了 600K 还没压缩」的一半病因。
   * ★ 压缩后必须清空:真值描述的是压缩前那份上下文,留着会让判据在压完的下一轮
   * 仍然读到 600K,立刻再压一次。
   */
  private usageAnchor: { tokens: number; messageCount: number } | undefined
  /**
   * 本 run 连续自动压缩失败的次数,到 `MAX_CONSECUTIVE_COMPACT_FAILURES` 熔断。
   *
   * ★ 不封顶的话,确定性失败(模型没权限、摘要恒为空)会变成每一轮白发一次摘要请求,
   * 而判据每一轮都为真。成功一次即清零。
   */
  private compactFailures = 0
  /**
   * 本轮已经因为上游 `context_length` 做过一次被动压缩。
   *
   * ★ 压完还被拒就不再压:否则「压缩 → 仍超 → 再压缩」会在同一轮里循环到熔断,
   * 而每一次都是一整个摘要请求。一轮正常结束时复位。
   */
  private reactiveCompacted = false
  private stopAfterTool = false
  /**
   * 本 run 至今调用过的工具总数。run 结束即随对象一起消失。
   *
   * ★ 它是刹车判据的唯一来源 —— 判定器那层不该自己去看「这轮有没有调工具」。
   */
  private toolCallsThisRun = 0
  /**
   * 连续「什么都没做就想停」的轮数。
   *
   * 拿到 `continue` 时 +1；`executeAll()` 里跑到任何一次工具调用时归零。
   *
   * ★ 复位放在**内核**而不是判定器里：判定与续跑是两个层的事。`main/` 那层
   *   只看得到「这一轮模型有没有要求工具」，连工具执行都看不到。复位放在这里，
   *   `main/` 那层就只需要读一个数。
   */
  private stoppedTurnStreak = 0
  private stopHookActive = false
  /** 断流续跑的退避表。见 `RESUME_DELAYS_MS` 与 `canResume`。 */
  private readonly resumeDelays: readonly number[]

  constructor(
    private readonly deps: SessionDeps,
    private readonly handle: RunHandle,
    private readonly req: RunRequest
  ) {
    this.resumeDelays = deps.resumeDelaysMs ?? RESUME_DELAYS_MS
    this.messages = [...(deps.history ?? [])]
    /**
     * ★ 历史一律走 `isolateHistoryPaths`(不抛),**两个分支都要走**。
     *
     * 远端分支原先走 `normalizePaths`,那是会抛的:历史里只要有一条对不上的引用
     * (工作区改指过另一台服务器、或原本是本地工作区),构造函数就在 run 开始之前抛,
     * 此后每一次 run 都在同一处抛,除了手改历史没有任何修复入口。
     *
     * 本地分支原先直接 `[...this.messages]`,一个字都不校验 —— 更糟:历史引用早已被
     * 归一成相对写法,于是 server-a 的 `src/a.ts` 进本地 run 后指向**本机同名文件**,
     * 模型读到一个无关文件而全程无一处报错。
     */
    this.contextMessages = this.buildContext()
    /**
     * 进程被杀掉或宿主在 session.run() 进入 catch 之前失去控制时，上一轮可能只
     * 来得及提交 assistant/tool_use。必须在提交本轮用户消息之前修复它：否则新
     * 的 user 消息会插在 tool_use 与 tool_result 之间，下一次 Anthropic 请求仍然
     * 会因为「tool_result 必须紧邻」而返回 400。
     */
    this.closeUnexecutedCalls(RECOVERED_INTERRUPTED)
    // 空 input = 续跑(排队消息之外的场景,见 RunRequest.input)
    if (req.input.length > 0) {
      const now = deps.host.clock.now()
      /**
       * ★ 走 `commit` 而不是 `messages.push` —— 用户自己发的那条也要**发出去**。
       *
       * 只 push 的话它就只存在于主进程:渲染层的转录是事件流的投影,
       * 没有这条 commit,用户按下回车后自己的消息永远不出现在对话里。
       * 渲染层现在会先补一条同 ID 的乐观消息;这里复用 `inputMessageId` 后,
       * message_commit 会确认并替换它,步骤 6 落盘的仍然只有这一条。
       *
       * 这里 emit 得到的时机是安全的:`startRun` 先建 handle 与泵、再调驱动,
       * 构造函数跑到这一行时事件已经有人接了。
       */
      this.commit({
        ...userMessage(req.inputMessageId ?? ulid(now), this.normalizePaths(req.input), now),
        ...(req.inputInternal === true ? { internal: true } : {})
      })
    }
  }

  /** 跑完之后的完整转录。调用方据此落盘。 */
  get history(): readonly AgentMessage[] {
    return this.messages
  }

  /**
   * ★ **不抛异常**。run 的结局一律经 `handle.finish` 表达 ——
   * 一个逃出去的异常会变成「run 无声消失」:UI 上转圈不停,而日志里只有一行栈。
   */
  async run(): Promise<void> {
    try {
      await this.loop()
    } catch (err) {
      /**
       * `handle.signal.aborted` 是 `isAbortError` 之外的第二道判断:工具或上游
       * 可能在中断时抛出一个**不像 AbortError 的**错误(比如 undici 把它包成
       * `TypeError: fetch failed`)。用户点了停止,就不该看到一个错误弹窗。
       */
      if (isAbortError(err) || this.handle.signal.aborted) {
        this.finalizeAbort()
        return
      }
      this.deps.host.logger.error('[session] run 失败', err)
      const error = toRunError(err)
      const acc = this.pending
      this.pending = null
      this.commitAssistant([...(acc?.finalize().parts ?? []), { type: 'error', error }])
      this.closeUnexecutedCalls('[not executed: the run failed before this tool could finish]')
      this.handle.finish('error', error)
    }
  }

  // ─────────────────────────── 主循环 ───────────────────────────

  private async loop(): Promise<void> {
    // Long tasks are not stopped at an arbitrary model/tool turn count. The
    // run handle's abort signal remains the explicit way to stop a run.
    for (;;) {
      const outcome = await this.turn()
      if (outcome === null) return // 这一轮已经把 run 收尾了
      if (outcome.kind === 'tools') {
        await this.executeAll(outcome.calls, outcome.tools)
        if (this.stopAfterTool) {
          this.handle.finish('done')
          return
        }
      }
      /**
       * ★ 插话的注入点 —— **在工具结果落进转录之后、下一次请求组装之前**。
       *
       * 这个位置是被两头夹死的,不是随便选的:
       * - 放在 `executeAll` **之前**,用户消息会插在 `tool_call` 与 `tool_result`
       *   之间。Anthropic 要求 tool_result 紧邻它的 tool_call,那是一个 400 ——
       *   与构造函数里 `closeUnexecutedCalls` 那段注释说的是同一件事。
       * - 放在 `turn()` **之后**才有意义:模型必须先看见工具结果,插话才是
       *   「在他做完这一步之后补一句」,而不是凭空打断。
       *
       * Anthropic 编码器会把相邻的同角色消息并成一轮(`encode/anthropic.ts`),
       * 所以模型看到的是一条 `[tool_result…, 插话文本]` 的用户消息 ——
       * tool_result 仍在最前,形状合法。
       *
       * ★ 续跑(`kind === 'continue'`)那条路**也**走这里:用户在目标跑着的时候
       * 排进来的话,应该在下一次请求里被模型看到,而不是等到目标结束。
       */
      this.injectInterjections()
    }
  }

  /**
   * 一轮:组装 → 请求 → 拼块 → 提交助手消息。
   *
   * 返回 null 表示 run 已经结束(正常收尾或出错);`'continue'` 表示回合末判定
   * 要求再跑一轮(已经注入过续跑消息);否则返回待执行的工具调用与**本轮下发给
   * 模型的那份工具快照**。
   */
  private async turn(): Promise<TurnOutcome | null> {
    this.handle.signal.throwIfAborted()
    const alias = this.aliasFor(this.req.model)
    if (alias !== undefined) {
      const issue = validateModelRuntime({
        alias,
        messages: this.messages,
        webSearchRequested: this.req.webSearch
      })[0]
      if (issue !== undefined) {
        this.handle.finish(
          'error',
          agentError(issue.code === 'context_length' ? 'context_length' : 'provider', issue.message, {
            retryable: false
          })
        )
        return null
      }
    }

    /**
     * ★ 每轮取一次快照(方案 §4.4)。同一份快照有两个用途:下发给模型的
     * 工具列表,和稍后按名字找回工具 —— 必须是同一份,否则会出现
     * 「下发的列表里有,执行时却找不到」的错位。
     */
    const allowedTools = typeof this.deps.allowedTools === 'function'
      ? this.deps.allowedTools()
      : this.deps.allowedTools
    const available = this.deps.tools.snapshot({
      ...(allowedTools !== undefined ? { allowList: allowedTools } : {}),
      /*
        ★ Composer 上那颗「联网搜索」药丸第一次真的控制住东西的地方。
        关掉时受它管的工具(`NETWORK_SWITCH_TOOLS`:WebFetch / web_search)连下发都不下发,
        模型不会先白跑一轮再被拒。
      */
      network: this.req.webSearch,
      /*
        ★ 子代理问不到人 —— 这是 `Task` 的工具描述里早就写下的承诺,这一行才是
        它的实现。以前描述这么写、机制却让它调得出 `AskUserQuestion`,而
        `InteractionGate.request()` 没有超时:子代理就永远停在那次调用上,
        界面上只剩一个不动的「运行中」。判据用 `depth`(而不是 `parentRunId`)
        是因为 tool ctx 递下去的也是 `depth`,两边说的是同一件事。
      */
      noInteraction: this.req.depth > 0,
      context: this.toolContext('')
    })
    // A model without tool calling must not receive a tool schema merely
    // because the application registry contains tools. Existing tool history
    // is rejected above because it cannot be encoded safely for such a model.
    const advertised = modelSupportsTools(alias) ? available : []
    const byName = new Map(advertised.map((t) => [t.externalName, t]))
    // `execute` 是闭包,过不了结构化克隆 —— 请求体里不该带着它
    const infos: ToolInfo[] = advertised.map(({ execute: _execute, isEnabled: _isEnabled, ...info }) => info)

    const todoToolName = this.deps.tools.byInternalId('TodoWrite')?.externalName
    // 每轮现取 —— `EnterPlanMode` 可能就发生在本轮的上一次工具调用里
    const planFile = this.deps.planFile?.()

    const assembleInput = {
      messages: this.contextMessages,
      tools: infos,
      skills: this.deps.skills ?? [],
      ...(this.deps.modePrompt !== undefined ? { modePrompt: this.deps.modePrompt } : {}),
      ...(this.deps.agentPrompt !== undefined ? { agentPrompt: this.deps.agentPrompt } : {}),
      ...(this.deps.personalization !== undefined
        ? { personalization: this.deps.personalization }
        : {}),
      mode: this.req.mode,
      thinking: this.req.thinking,
      model: this.req.model,
      ...(this.req.modelProviderId === undefined ? {} : { modelProviderId: this.req.modelProviderId }),
      workspaceRoot: this.deps.workspaceRoot,
      now: this.deps.host.clock.now(),
      platform: this.deps.workspace?.platform ?? this.deps.host.platform,
      environment: this.deps.workspace,
      permissionMode: this.req.permissionMode,
      webSearch: this.req.webSearch,
      reminder: {
        ...(this.deps.planExecution !== undefined ? { planExecution: this.deps.planExecution } : {}),
        ...(this.req.widgetContext !== undefined && this.req.widgetContext.length > 0
          ? { widgetContext: this.req.widgetContext }
          : {}),
        ...(planFile !== undefined ? { planFile } : {}),
        ...(this.deps.projectInstructions !== undefined
          ? { projectInstructions: this.deps.projectInstructions }
          : {}),
        ...(this.deps.git !== undefined ? { git: this.deps.git } : {}),
        ...(this.deps.skillExtraction !== undefined ? { skillExtraction: this.deps.skillExtraction } : {}),
        /*
          ★ 从**注册表**查外部名,不从上面那份 `advertised` 快照里取。
          那份被 `readOnlyOnly` / `allowList` / `network` 过滤过 —— 一个 `tools:`
          写得窄的子代理会因此看不见**它自己写的** todo,而症状是「模型忘了
          自己的计划」,没有任何报错。也不能写字面量 'TodoWrite':撞名时
          `ToolNamer` 会加 8 位哈希后缀,那时字面匹配永远静默地返回空。
        */
        ...(todoToolName !== undefined ? { todoToolName } : {}),
        /*
          ★ todo 从**转录**推,不从上下文推。压缩会把边界之前的历史整个移出
          上下文(`messagesForModel`),而模型的进度表就住在那条 `TodoWrite`
          调用里 —— 只看上下文的话,压缩之后它会静默地丢掉自己的计划。
        */
        todoHistory: this.messages
      },
      /*
        ★ 这里给的是**有效窗口**,不是协议窗口 —— 它决定 `shouldCompact` 的分母和
        圆环的分母。协议窗口那条线在下面的 `validateModelRuntime` 里读 `alias` 原值,
        两条线**故意**不一样:默认夹在 272K 是「不越过计费线」,而不是「模型装不下」。
        输出额度取的是**全局设置项**(设置 › 通用 › Agent),模型目录里那条
        `maxOutputTokens` 不再参与;只有模型的协议上下文窗口会把它收窄 ——
        一次请求的输出额度大过整个窗口必然 400。见 `shared/agent/run-request.ts`。
      */
      contextWindow: effectiveContextWindow(alias?.contextWindow, this.req.maxContext === true),
      maxOutputTokens: resolveMaxOutputTokens(this.deps.maxOutputTokens, alias?.contextWindow),
      supportsThinking: alias?.capabilities.thinking ?? false,
      reasoningEfforts: alias?.reasoningEfforts,
      ...(alias?.thinkingConfig !== undefined ? { thinkingConfig: alias.thinkingConfig } : {}),
      /*
        「上一次上游真值 + 其后新增消息的估算」。原先这里是一个校准系数(真值 ÷ 估算,
        夹在 [1, 3]),长会话里上界会把判据夹得比真值低 —— 已随压缩重写删除,
        见 `context-assembler.ts` 组装段的说明。
      */
      ...this.contextTokens()
    }
    let { request, usage, inputTokens } = assemble(assembleInput)

    /*
      需求:占用过了阈值(有效窗口 − 摘要输出预留 − 缓冲,见 `autoCompactThreshold`)
      就在发请求**之前**压缩 —— 同 Claude Code 每轮开头的 autocompact。
      ★ 默认 272K 有效窗口下阈值约 239K;原先这里的机械压缩折叠不动就置位 `exhausted`,
      此后整个 run 不再压缩,于是占用一路涨到 600K(用户报的那个症状)。
      现在压缩只有「模型摘要 + 边界」这一条路,失败三次才熔断。
    */
    if (usage.shouldCompact && this.deps.contextManagement?.autoCompact === true) {
      if (await this.compact({ alias, preTokens: inputTokens })) {
        /*
          ★★ **必须先把旧的 `knownInputTokens` 摘掉再重算。**

          `compact()` 成功后清空了 `usageAnchor`(真值描述的是压缩**之前**那份上下文),
          于是 `contextTokens()` 返回空对象 —— 而空对象展开进去**盖不掉**
          `assembleInput` 里已经有的那个数。症状:压缩明明成功了,重算出来的
          `inputTokens` 仍是压缩前的 200K,紧接着 `validateModelRuntime` 按它判定超窗,
          这一轮以 `context_length` 收场 —— 看上去是「压了一次然后整个 run 当场死掉」,
          而日志里只有一句上下文超长,指不到这里。
        */
        const { knownInputTokens: _stale, ...fresh } = assembleInput
        ;({ request, usage, inputTokens } = assemble({
          ...fresh,
          messages: this.contextMessages,
          ...this.contextTokens()
        }))
      }
    }

    // ★ 在**请求发出前**发,不在收到响应后发 —— 压力条要在这一轮真的挤爆之前
    // 就让用户看见(方案 §4.12)。
    this.handle.emit({ type: 'context_usage', ...usage })

    if (alias !== undefined) {
      const contextIssue = validateModelRuntime({
        alias,
        messages: request.messages,
        /*
          ★ 和 `shouldCompact` 读同一个数(含上游真值的那个),不是原始估算。
          两边口径不同的话会出现「判据说该压了、硬校验却说还早」,
          而这条硬校验是 400 之前最后一道拦网 —— 它偏低就等于不存在。
        */
        estimatedInputTokens: inputTokens,
        // 需求：硬校验必须预留这次真正发送的默认额度，而不是模型目录里的较大协议上限。
        maxOutputTokens: request.maxOutputTokens
      }).find((issue) => issue.code === 'context_length')
      if (contextIssue !== undefined) {
        this.handle.finish(
          'error',
          agentError('context_length', contextIssue.message, { retryable: false })
        )
        return null
      }
    }

    /*
      ★ 断流自动续跑。请求体本身(`assemble` / `context_usage`)留在循环**外面** ——
      两次尝试之间消息一字没变,重新组装是白做功,还会重复发一条压力条事件。
    */
    let attempt = await this.streamOnce(request)
    for (let resume = 0; ; resume++) {
      const failure = attempt.streamError
      if (failure === undefined || !this.canResume(failure, resume)) break
      const delayMs = this.resumeDelays[resume] ?? 0
      /*
        复用 `provider_retry` 而不是新造一个事件:要说的话和 router 退避时一模一样
        (「在等,因为上游出了问题」),而主状态行(`StatusLine.tsx`)、子代理卡片
        (`parts.tsx`)、转录 reducer 三处都已经在画它了。新造一个只会多出三处要改的地方,
        换不来任何新信息。提示在下一个 `message_start` 到达时自动消失。
      */
      this.handle.emit({
        type: 'stream',
        delta: { type: 'provider_retry', attempt: resume + 1, delayMs, reason: failure.message }
      })
      await abortableSleep(delayMs, this.handle.signal)
      this.handle.signal.throwIfAborted()
      // 上一次那份 accumulator 到这里整个丢弃 —— 半截回答不进转录、不进历史。
      attempt = await this.streamOnce(request)
    }
    const { acc, stopReason, streamError } = attempt

    const { parts, calls } = acc.finalize()
    /*
      需求:上游以 `context_length` 拒绝(本地估算没拦住)时,压缩一次再重发这一轮 ——
      同 Claude Code 的 reactive compact。
      ★ 只在一个字都没吐出来时做:已经有内容的话这不是「提示词过长」那类拒绝。
      ★ 每轮只做一次(`reactiveCompacted`):压完还被拒说明压缩救不了,照常报错。
    */
    if (
      streamError?.code === 'context_length'
      && parts.length === 0
      && !this.reactiveCompacted
      && this.deps.contextManagement?.autoCompact === true
    ) {
      this.reactiveCompacted = true
      if (await this.compact({ alias, preTokens: this.usageAnchor?.tokens ?? usage.used })) return { kind: 'continue' }
    }
    if (streamError === undefined) this.reactiveCompacted = false
    /**
     * ★ error part 也进转录。它只属于 UI 那一轨 —— encode/anthropic.ts 的
     * `toBlock` 对它返回 null,所以下一轮上行时会被丢掉,不会让模型
     * 开始为我们的 bug 道歉。而重载后 `attach` 回来的转录里,
     * 失败仍然看得见,不是一个消失了的 toast。
     */
    if (streamError !== undefined) parts.push({ type: 'error', error: streamError })
    this.commitAssistant(parts)

    if (streamError !== undefined) {
      this.closeUnexecutedCalls('[not executed: the upstream response did not complete successfully]')
      this.handle.finish('error', streamError)
      return null
    }

    /**
     * `tool_use` 却一个可执行的调用都没有:所有 tool_call 块都没闭合
     * (流被掐断)。继续循环会**原样重发一次同样的请求** —— 大概率再来一次。
     * 收尾比空转诚实。
     */
    if (stopReason !== 'tool_use' || calls.length === 0) {
      this.closeUnexecutedCalls('[not executed: the model did not request tool execution]')
      if (stopReason === 'tool_use') {
        this.deps.host.logger.warn('[session] stopReason=tool_use 但没有已闭合的工具调用')
      }

      /*
        ★★ 回合末的判定点。位置被两头夹死，不是随便选的：

        - 必须在 `closeUnexecutedCalls` **之后** —— 判定器读的转录必须已经配对完整，
          否则它会看到一串没有 tool_result 的 tool_call，那是我们自己的半成品状态，
          而它会据此判「工具还没跑完，未达成」。
        - 必须在 `handle.finish('done')` **之前** —— 一旦收尾，run 就从注册表摘掉了，
          再想继续就是「同一轮里的第二次 run」，用户看到的是两次回答。

        ★ 判定器抛异常**必须在这里兜住**。结构上它每一轮 end_turn 都会被调用，
          一次逃出去的异常 = `for(;;)` 无限重试同一个请求。降级成「这一轮不判定」。
      */
      // A completed background result or kickoff already queued is work to do, not a stop.
      if (this.injectInterjections() > 0) return { kind: 'continue' }
      let turnEnd: TurnEndResult | undefined
      try {
        turnEnd = stopReason === 'end_turn' ? await this.deps.onTurnEnd?.({
          sessionId: this.req.sessionId,
          workspaceId: this.req.workspaceId,
          runId: this.req.runId,
          messages: this.messages,
          isSubagent: this.req.depth > 0,
          toolCallsThisRun: this.toolCallsThisRun,
          stoppedTurnStreak: this.stoppedTurnStreak,
          stopHookActive: this.stopHookActive,
          signal: this.handle.signal
        }) : undefined
        if (stopReason === 'end_turn' && this.deps.onTurnEnd !== undefined) this.stopHookActive = true
      } catch (err) {
        this.deps.host.logger.warn('[session] 回合末判定失败，本轮按正常收尾处理', err)
      }

      this.handle.signal.throwIfAborted()
      if (turnEnd !== undefined) {
        if (turnEnd.goalStatus !== undefined) this.attachUiParts([turnEnd.goalStatus])
        if (turnEnd.acceptPendingInput === true && this.injectInterjections() > 0) return { kind: 'continue' }
        if (turnEnd.warning !== undefined) this.handle.emit({ type: 'notification', warning: turnEnd.warning })
        if (turnEnd.kind === 'continue' && (turnEnd.inject?.length ?? 0) > 0) {
          this.stoppedTurnStreak += 1
          const at = this.deps.host.clock.now()
          /*
            ★ `internal` 要**展开设置**，不能当构造器的参数传：`userMessage(id, parts, now)`
              只有三个参数。照 `injectInterjections` 里那条既有写法来。
          */
          this.commit({ ...userMessage(ulid(at), turnEnd.inject!, at), internal: true })
          /*
            ★ 交回 `loop()` 再跑一轮，**不在这里递归**：一个跑几百轮的目标会把
              `turn()` 叠成几百层栈帧，而它最后是以一次栈溢出结束的 —— 那种失败
              既看不出原因，也没有任何转录留下来。
          */
          return { kind: 'continue' }
        }
      }

      if (turnEnd?.kind !== 'finish' && this.injectInterjections() > 0) return { kind: 'continue' }
      this.handle.finish('done')
      return null
    }

    return { kind: 'tools', calls, tools: byName }
  }

  /**
   * 一次上游请求:建 accumulator、消费整条流、把「流结束了却没有 message_end」
   * 和「stopReason=max_tokens」两种结局合成 `streamError`。
   *
   * ★ 从 `turn()` 里拆出来,是为了让外面能套一层断流续跑(见 `RESUME_DELAYS_MS`)。
   * 调用之间**不共享任何状态**:accumulator 每次新建,所以失败那一次整个丢弃、
   * 重来一次是干净的。
   *
   * ★ `this.pending = null` 只在**正常完成**那条路上执行,不放进 `finally` ——
   * 中断发生在流中途时,`run()` 的 catch 要靠 `this.pending` 把那半截回复提交进转录
   * (见 `finalizeAbort`)。清早了,用户点停止之后看到的是一片空白。
   */
  private async streamOnce(request: CanonicalRequest): Promise<{
    acc: BlockAccumulator
    stopReason: StopReason
    streamError: AgentError | undefined
  }> {
    const acc = new BlockAccumulator()
    this.pending = acc
    let stopReason: StopReason = 'end_turn'
    let streamError: AgentError | undefined
    let ended = false

    for await (const ev of abortableStream(this.deps.upstream.stream(request, this.handle.signal, {
      workspaceId: this.req.workspaceId,
      runId: this.req.runId,
      sessionId: this.req.sessionId
    }), this.handle.signal)) {
      this.handle.signal.throwIfAborted()
      // 同一个戳给事件和 accumulator:思考卡片的实时计时与落盘时长必须同一只表(见 event.ts `stream.at`)
      const at = this.deps.host.clock.now()
      this.handle.emit({ type: 'stream', delta: ev, at })
      acc.apply(ev, at)
      if (ev.type === 'message_end') {
        stopReason = ev.stopReason
        ended = true
        /*
          ★ 上游真值在这里、也只在这里进得来 —— 这一跳就是「圆环读真值、判据读估算」
          那条裂缝的补丁。`promptTokensOf` 而不是 `usage.inputTokens`:缓存读写同样
          占着窗口,Anthropic 的 `input_tokens` 不含它们(与转录里 `lastInputTokens`
          同一个理由,也必须是同一个口径 —— 界面上写着 211K 的正是那个数)。

          ★ 锚点记的是**发出这份请求时**的上下文长度:本轮的 assistant 回复还没
          commit,它和之后的工具结果都归「其后新增、按估算补」那一段。
        */
        this.usageAnchor = { tokens: promptTokensOf(ev.usage), messageCount: this.contextMessages.length }
      }
      // 路由器把总失败表达成一个**终止事件**而不是异常(见 router.stream),
      // 所以这里是正常的循环出口,不是 catch。
      else if (ev.type === 'error') streamError = ev.error
    }
    this.handle.signal.throwIfAborted()
    this.pending = null

    if (streamError === undefined && !ended) {
      streamError = agentError('network', 'The upstream connection closed before the response completed.', {
        messageKey: 'agent.error.incompleteResponse'
      })
    }
    if (streamError === undefined && stopReason === 'max_tokens') {
      streamError = agentError('provider', 'The response reached the model output limit.', {
        messageKey: 'agent.error.outputLimit', retryable: false
      })
    }
    return { acc, stopReason, streamError }
  }

  /**
   * 这次断流该不该自己续一轮。
   *
   * ★★ **只认 `code === 'network'`,刻意不放宽到全部 `retryable`。**
   *
   * - `network` 是唯一一类「router 已经无话可说、而 `sawContent` 是恢复没发生的
   *   全部原因」的错误。router 自己的注释说得很清楚:连接层错误「换下一个 provider
   *   大概率也是同一个病因,真正管用的是多等一会儿再碰」—— 这个循环做的正是这件事。
   * - 它也是唯一一类**没有任何服务端信号**的错误(没有 `Retry-After`、没有 5xx body),
   *   盲退避是仅有的手段。
   * - `rate_limit`、5xx `provider`、冷却中的 `no_healthy_provider` 走的是 router 那条
   *   **信息更全**的路(认 `Retry-After`、会切 provider、有健康表)。在它外面再套一个
   *   盲循环,最坏情况是 5 × 3 次尝试的长时间空转 —— 那是另一个决定,不在这里做。
   *
   * 这条判据同时覆盖了 `streamOnce` 自己合成的 `incompleteResponse`:流静默断掉、
   * router 一个 `error` 事件都没发的那一支。
   */
  private canResume(err: AgentError, resume: number): boolean {
    return err.code === 'network'
      && resume < this.resumeDelays.length
      && !this.handle.signal.aborted
  }

  /**
   * 给 `assemble` 的 `knownInputTokens`:上一次上游真值 + 其后新增消息的估算。
   * 没有真值(本 run 第一轮 / 刚压缩完)时不给,判据退回纯估算。
   */
  private contextTokens(): { knownInputTokens?: number } {
    const anchor = this.usageAnchor
    if (anchor === undefined || anchor.messageCount > this.contextMessages.length) return {}
    return { knownInputTokens: anchor.tokens + estimateMessages(this.contextMessages.slice(anchor.messageCount)) }
  }

  /**
   * 转录 → 发给模型的那份上下文:只取最后一条压缩边界及其之后(`messagesForModel`),
   * 再隔离路径。构造函数和压缩完成后都走这一个入口。
   *
   * ★ 原先这里是「机械压缩 + 检查点摘要」的投影(`projectContextWindow`),压缩状态
   * 住在另一张表里,重开会话要靠检查点把投影重建回来。现在边界本身就是转录里的一条
   * 消息,重开会话读到的就是压缩当场的那一份,不存在「重建得不一样」的可能。
   */
  private buildContext(): AgentMessage[] {
    return messagesForModel(this.messages).map((message) => ({
      ...message,
      parts: this.isolateHistoryPaths(message.parts)
    }))
  }

  /**
   * 自动压缩一次(Claude Code 式):模型把边界之后的整段对话写成摘要,摘要 + 最近读过的
   * 文件 / 用过的技能作为一条带 `compact_boundary` 的 user 消息提交进转录,此后上下文
   * 从这条消息开始。成功返回 true。
   *
   * ★ 走 `commit`:边界消息要落盘、要发给渲染层画分隔线 —— 只改内存的话重开会话
   * 就回到压缩前,而用户在界面上什么也看不到。
   */
  private async compact(input: { alias: ModelAlias | undefined; preTokens: number }): Promise<boolean> {
    if (this.compactFailures >= MAX_CONSECUTIVE_COMPACT_FAILURES) return false
    this.handle.emit({ type: 'context_status', status: { phase: 'compacting', trigger: 'auto' } })
    /*
      需求:压缩可以配成另一个模型/另一个档位(设置 › 通用 › Agent,工作区可覆盖)。
      ★ 解析放在 `compacting` 事件**之后**:它可能 warn 一行并回落,而那不该让
      界面上的「正在压缩」晚出现。`input.alias`(会话模型)仍然只管 `preTokens` 那一侧。
    */
    const binding = this.summaryBinding()
    if (binding === undefined) {
      // 连会话模型都解析不到 —— 走和这次改动之前一样的失败路径。
      return this.compactFailed('压缩模型不可用')
    }
    let result: { ok: true; message: AgentMessage } | { ok: false; error: AgentError }
    try {
      result = await compactConversation({
        messages: this.messages,
        trigger: 'auto',
        preTokens: input.preTokens,
        autoContinue: true,
        protocolWindow: binding.protocolWindow,
        summaryMaxOutputTokens: binding.maxOutputTokens,
        summaryThinking: binding.thinking,
        summaryModel: {
          model: binding.model,
          ...(binding.modelProviderId === undefined ? {} : { modelProviderId: binding.modelProviderId }),
          thinking: binding.thinking,
          fellBack: binding.fellBack
        },
        send: (request) => this.sendSummaryRequest(request, binding),
        attachments: { tools: this.attachmentTools(), readFile: (path) => this.readForAttachment(path) },
        newId: () => ulid(),
        now: this.deps.host.clock.now(),
        signal: this.handle.signal
      })
    } catch (error) {
      if (isAbortError(error) || this.handle.signal.aborted) throw error
      result = { ok: false, error: toRunError(error) }
    }
    if (!result.ok) return this.compactFailed(result.error.message)
    this.compactFailures = 0
    this.commit(result.message)
    this.contextMessages = this.buildContext()
    this.usageAnchor = undefined
    this.handle.emit({ type: 'context_status', status: { phase: 'compacted', trigger: 'auto' } })
    return true
  }

  /** 失败计数 + 熔断相位。恒返回 false,让调用点写成 `return this.compactFailed(...)`。 */
  private compactFailed(reason: string): false {
    this.compactFailures += 1
    this.deps.host.logger.warn('[session] 自动压缩失败', reason)
    const phase = this.compactFailures >= MAX_CONSECUTIVE_COMPACT_FAILURES ? 'disabled' : 'failed'
    this.handle.emit({ type: 'context_status', status: { phase, trigger: 'auto' } })
    return false
  }

  /**
   * 这次压缩发给谁、用什么参数。三档来源与回落规则在 `compaction/binding.ts`,
   * 手动 /compact 走的是同一份。
   *
   * ★ `'inherit'` 在这里落到**本轮 run 的档位**(`this.req.thinking`),不是会话记录上
   * 那个 —— 用户这一轮临时调高了档位,压缩就该跟着这一轮走。
   */
  private summaryBinding(): CompactBinding | undefined {
    return resolveCompactBinding({
      resolveModel: (model, modelProviderId) => this.deps.upstream.resolveModel(model, modelProviderId),
      ...(this.deps.compaction === undefined ? {} : { settings: this.deps.compaction }),
      ...(this.deps.workspaceCompaction === undefined ? {} : { workspace: this.deps.workspaceCompaction }),
      session: {
        model: this.req.model,
        ...(this.req.modelProviderId === undefined ? {} : { modelProviderId: this.req.modelProviderId }),
        thinking: this.req.thinking
      },
      ...(this.deps.maxOutputTokens === undefined ? {} : { maxOutputTokens: this.deps.maxOutputTokens }),
      warn: (message) => { this.deps.host.logger.warn(message) }
    })
  }

  /**
   * 摘要请求。
   *
   * ★ 原先这里钉死「和正文同一个模型、同一家供应商」,理由是它读的是同一段对话,
   * 漂到另一家既换口径也换账单。那条理由仍然成立,所以**默认**依然如此
   * (`compactModel` 出厂是空串 = 跟随会话模型);现在只有用户在设置里显式选了
   * 另一个模型时才会换 —— 换言之,漂走这件事从「可能悄悄发生」变成「他自己选的」。
   */
  private sendSummaryRequest(request: SummaryRequest, binding: CompactBinding): AsyncIterable<ProviderStreamEvent> {
    const canonical = {
      model: binding.model,
      ...(binding.modelProviderId === undefined ? {} : { modelProviderId: binding.modelProviderId }),
      system: request.system,
      messages: request.messages,
      tools: [],
      maxOutputTokens: request.maxOutputTokens,
      thinkingLevel: request.thinkingLevel
    }
    return abortableStream(this.deps.upstream.stream(canonical, this.handle.signal, {
      workspaceId: this.req.workspaceId, runId: `${this.req.runId}:compact`, sessionId: this.req.sessionId
    }), this.handle.signal)
  }

  /**
   * 压缩后重附件要认的工具**外部名**。
   * ★ 从注册表查,不写字面量:撞名时 `ToolNamer` 会加哈希后缀,字面匹配会静默地返回空。
   */
  private attachmentTools(): AttachmentToolNames {
    const name = (id: string): string | undefined => this.deps.tools.byInternalId(id)?.externalName
    const file = new Set([name('Read'), name('Write'), name('Edit')].filter((n): n is string => n !== undefined))
    const todo = name('TodoWrite')
    const skill = name('Skill')
    return { file, ...(todo === undefined ? {} : { todo }), ...(skill === undefined ? {} : { skill }) }
  }

  /** 重附件读文件:和 Read 工具同一套路径解析(远端工作区、工作区外路径都一致)。 */
  private async readForAttachment(path: string): Promise<string | undefined> {
    const ctx = this.toolContext('compact')
    const resolved = await resolvePath(ctx, path)
    if (!resolved.ok) return undefined
    try {
      return await readAttachableFile(ctx.host.fs, resolved.abs)
    } catch {
      // 文件在压缩前后被删 / 没权限:少附一个文件,不该让整次压缩失败。
      return undefined
    }
  }

  // ─────────────────────────── 工具执行 ───────────────────────────

  /**
   * 并行执行本轮的全部工具调用,结果按模型给出的调用顺序合成**一条** user 消息。
   *
   * 同一轮的工具调用来自同一份模型决策,彼此不能看到对方的结果,因此可以同时
   * 开始。结果数组仍按 `calls` 的索引写回,保证上游协议看到稳定的 tool_result
   * 顺序；每个 promise 都会等到结束,这样中断或异常时已经完成的工具结果仍能
   * 在 `finally` 中落盘,而没有结果的调用交给 `finalizeAbort` / 错误收尾补齐。
   */
  private async executeAll(calls: PendingCall[], tools: Map<string, Tool>): Promise<void> {
    const parts: ContentPart[] = []
    const results: Array<ContentPart | undefined> = Array.from({ length: calls.length })
    let failed = false
    let firstError: unknown
    /*
      ★ 两个计数在**执行之前**就更新，不等结果：判据是「模型有没有要求干活」，
        不是「干成了没有」。一个每轮都调工具却每次都失败的 run 仍然在做事
        （它会读到错误、换个做法），而那不是空转。
    */
    if (calls.length > 0) {
      this.toolCallsThisRun += calls.length
      this.stoppedTurnStreak = 0
    }
    try {
      const execute = async (call: PendingCall, index: number): Promise<void> => {
        try {
          results[index] = await this.executeOne(call, tools)
        } catch (error) {
          // Keep other calls running so already-started tools can finish cleanly.
          if (!failed) {
            failed = true
            firstError = error
          }
        }
      }
      if (calls.some((call) => tools.get(call.name)?.concurrencySafe === false)) {
        for (const [index, call] of calls.entries()) {
          await execute(call, index)
          if (failed) break
        }
      } else await Promise.all(calls.map(execute))
      if (failed) throw firstError
    } finally {
      /**
       * ★ `finally` 而不是只在成功路径上提交:中断发生在第 2 个工具执行途中时,
       * 第 1 个**已经真的做完了**。不在这里落进转录,它就会在收尾时被
       * `orphanedToolCalls` 当成孤儿补上一条「已中断」—— 做过的事被记成没做,
       * 而模型下一轮会据此重做一遍。
       */
      for (const result of results) {
        if (result !== undefined) parts.push(result)
      }
      if (parts.length > 0) {
        const now = this.deps.host.clock.now()
        this.commit(toolResultMessage(ulid(now), parts, now))
      }
      const completed = parts.filter(
        (part): part is Extract<ContentPart, { type: 'tool_result' }> =>
          part.type === 'tool_result'
      )
      // An abort or unexpected failure can leave the current and remaining
      // calls without results here. `finalizeAbort` records those as
      // interrupted failures in the transcript, so the usage ledger must use
      // the same accounting rather than presenting them as successful tools.
      const toolErrors =
        completed.filter((part) => part.isError).length + (calls.length - completed.length)
      try {
        this.deps.onToolUsage?.({
          runId: this.req.runId,
          toolCalls: calls.length,
          toolErrors
        })
      } catch (err) {
        // Usage diagnostics must never turn a completed tool call into an
        // agent-run failure.
        this.deps.host.logger.warn('[usage] 回填工具执行统计失败，运行本身不受影响', err)
      }
    }
  }

  private async executeOne(call: PendingCall, tools: Map<string, Tool>): Promise<ContentPart> {
    this.handle.signal.throwIfAborted()
    const { callId } = call

    if (!call.ok) {
      // 不去执行,把**原文**回给模型 —— 只说「参数错了」它无从下手
      return this.toolFailure(
        callId,
        `The tool arguments were not valid JSON (${call.reason}). This is what you sent:\n${call.raw}`
      )
    }

    const tool = tools.get(call.name)
    if (tool === undefined) {
      // 模型会编工具名;这一轮进行中工具也可能被下线。两种都是**工具错误**,不是崩溃。
      return this.toolFailure(
        callId,
        `There is no tool named ${call.name}. Pick one from the list of available tools.`
      )
    }

    // ★ 下发/回传/展示三处用的都是 externalName —— 三条轨道上必须是同一个名字,
    // 用户才能把审批弹窗、工具卡片和转录对上号。
    this.handle.emit({ type: 'tool_start', callId, toolName: tool.externalName, input: call.input })

    const decision = await abortable(() => this.approve(callId, tool, call.input), this.handle.signal)
    this.handle.signal.throwIfAborted()
    if (decision.kind === 'deny') {
      // 拒绝要让模型**看见**:系统提示词里写了「被拒绝时不要试图绕开」,
      // 而那句话的前提是它知道自己被拒了。
      return this.toolFailure(callId, decision.reason ?? 'The user denied this tool call.')
    }
    // allow_edited 的入参是用户改过的 —— 用原值执行等于无视用户的修改
    const input = decision.kind === 'allow_edited' ? decision.input : call.input
    this.handle.signal.throwIfAborted()

    let result: ToolResult
    try {
      result = await abortable(() => tool.execute(input, this.toolContext(callId)), this.handle.signal)
    } catch (err) {
      /**
       * ★ 中断**原样抛出**,不伪装成工具失败(`define.test.ts` 钉住的契约)。
       * 伪装成失败的话,模型会看到「工具失败了」然后继续往下跑 ——
       * 用户点了停止,对话却还在动。
       */
      if (isAbortError(err) || this.handle.signal.aborted) throw err
      // 其余异常收敛成工具错误:`tool_failed` 进转录并继续循环(方案 §4.11)
      return this.toolFailure(callId, `Tool execution failed: ${toolThrewError(err)}`)
    }

    /**
     * 截断在**产出侧**(方案 §4.3)。这里不能直接 `truncateToolOutput(content)` ——
     * 内容没超限时它返回一个干净的 `{ content }`,会把工具自己标好的
     * `truncated` / `originalBytes` 抹掉(工具在更靠近数据的地方截断,标记更准)。
     */
    const output =
      result.output.content.length > MAX_TOOL_OUTPUT_CHARS
        ? { ...result.output, ...truncateToolOutput(result.output.content) }
        : result.output

    /*
      PostToolUse 钩子。★ 在 `tool_end` **之前**调用，并且它只能往后**追加**：
      UI 卡片、转录、模型收到的那份必须是同一段文本 —— 允许改写 output 的话，
      事后没有任何办法分辨用户看到的是哪一份。
      钩子里的异常不该拖垮工具循环，所以整个吞掉（失败已经记进诊断了）。
    */
    let finalOutput = output
    let finalIsError = result.isError
    if (this.deps.onToolExecuted !== undefined) {
      const feedback = await this.deps
        .onToolExecuted({ tool, input: call.input, output, isError: result.isError, callId })
        .catch(() => undefined)
      if (feedback?.additionalContext !== undefined && feedback.additionalContext.trim() !== '') {
        finalOutput = {
          ...output,
          content: `${output.content}\n\n<hook-feedback>\n${feedback.additionalContext.trim()}\n</hook-feedback>`
        }
      }
      if (feedback?.isError === true) finalIsError = true
    }

    this.handle.emit({ type: 'tool_end', callId, output: finalOutput, isError: finalIsError })
    if (result.stopRun === true && !finalIsError) this.stopAfterTool = true
    return {
      type: 'tool_result',
      callId,
      output: finalOutput,
      isError: finalIsError,
      ...(result.subagent === undefined ? {} : { subagent: result.subagent })
    }
  }

  private toolContext(callId: string): ToolContext {
    const writeFileRestriction = this.deps.writeFileRestriction?.()
    return {
      sessionId: this.req.sessionId,
      workspaceId: this.req.workspaceId,
      workspaceRoot: this.deps.workspaceRoot,
      // ★ 必传:只断 SSE 不断工具,会留下一堆僵尸 shell 和还在写的文件(方案 §4.3)
      signal: this.handle.signal,
      permissionMode: this.req.permissionMode,
      depth: this.req.depth,
      callId,
      runId: this.req.runId,
      ...(writeFileRestriction === undefined ? {} : { writeFileRestriction }),
      skills: this.deps.skills,
      // 新建的定时任务默认继承这一次 run 的模型 —— 见 ToolContext.model 上那段
      model: this.req.model,
      ...(this.req.modelProviderId === undefined ? {} : { modelProviderId: this.req.modelProviderId }),
      // ★ 递的是 deps.host 本身(结构上满足 ToolHost),不是拷贝出来的五个字段 ——
      //   拷贝会在换宿主后留下一份旧引用,正是 ctx 传递想避免的那件事
      host: this.deps.workspace ? {
        fs: this.deps.workspace.fs, spawn: this.deps.workspace.spawn, path: this.deps.workspace.path,
        remote: this.deps.workspace.remote, platform: this.deps.workspace.platform,
        fetch: this.deps.host.fetch, clock: this.deps.host.clock, logger: this.deps.host.logger
      } : this.deps.host,
      // 进度是易失的:单独的事件类型,永不写入转录
      emit: (progress) => this.handle.emit({ type: 'tool_progress', callId, progress }),
      /*
        需求:`TodoWrite` 要把「这次更新改了什么」回显给模型,而它每轮发的是完整
        清单 —— 上一份只能从**转录**里取。给的是 `contextMessages`(发给模型的那份),
        与它看到的上下文完全一致;不新存一份状态,`todo.ts` 文件头那条无状态设计不动。

        ★ 工具名从**注册表**查,不从本轮 `advertised` 快照里取:那份快照被
        `readOnlyOnly` / `allowList` 过滤过,而转录里存的是 `ToolNamer` 分配的外部名
        —— 撞名时两者会不一致,且不报任何错(见 `shared/agent/todo.ts`)。
      */
      messages: this.contextMessages,
      ...(this.deps.tools.byInternalId('TodoWrite') === undefined
        ? {}
        : { todoToolName: this.deps.tools.byInternalId('TodoWrite')?.externalName }),
      /*
        ★ 没装启动器时**不放这个字段进去**,而不是放一个抛错的函数:
        `Task` 判的是 `ctx.spawnSubagent === undefined`,据此给出一句
        「这个环境里派不了子代理」的人话。放一个会抛的桩,模型看到的
        就变成一条内部错误信息了。
      */
      ...(this.deps.spawnSubagent !== undefined ? { spawnSubagent: this.deps.spawnSubagent } : {}),
      ...(this.deps.interact !== undefined ? { interact: this.deps.interact } : {}),
      ...(this.deps.canProposeGoal === undefined ? {} : { canProposeGoal: this.deps.canProposeGoal }),
      ...(this.deps.proposeGoal === undefined ? {} : { proposeGoal: this.deps.proposeGoal }),
      ...(this.deps.scheduling === undefined ? {} : { scheduling: this.deps.scheduling }),
      ...(this.deps.shells === undefined ? {} : { shells: this.deps.shells }),
      ...(this.deps.imageGen === undefined ? {} : { imageGen: this.deps.imageGen }),
      ...(this.deps.experimentalEnabled === undefined ? {} : { experimentalEnabled: this.deps.experimentalEnabled }),
      ...(this.deps.subagentModels === undefined ? {} : { subagentModels: this.deps.subagentModels }),
      ...(this.deps.sessionImages === undefined ? {} : { sessionImages: this.deps.sessionImages }),
      ...(this.deps.videoGen === undefined ? {} : { videoGen: this.deps.videoGen }),
      ...(this.deps.sessionVideos === undefined ? {} : { sessionVideos: this.deps.sessionVideos })
    }
  }

  private async approve(callId: string, tool: Tool, input: unknown): Promise<PermissionDecision> {
    if (this.deps.approve === undefined) return { kind: 'allow_once' }
    return this.deps.approve({
      runId: this.req.runId,
      callId,
      tool,
      input,
      signal: this.handle.signal
    })
  }

  /** 工具错误也要发 `tool_end`,否则 UI 上那张卡片会永远转圈 */
  private toolFailure(callId: string, message: string): ContentPart {
    const output: ToolOutput = { content: message }
    this.handle.emit({ type: 'tool_end', callId, output, isError: true })
    return { type: 'tool_result', callId, output, isError: true }
  }

  // ─────────────────────────── 中断收尾(方案 §4.8) ───────────────────────────

  /**
   * ★ 方案 §4.8 五件事里的**第 4 件**。前三件由 RunHandle.abort 与 InteractionGate 做,
   * 只有这一件必须在这里 —— 因为只有 session 知道转录长什么样。
   *
   * **漏掉它,下一轮请求就是 400**:Anthropic 要求每个 `tool_use` 都必须在紧随的
   * user 消息里有配对的 `tool_result`。而中断恰恰最容易在「工具已经开始、还没回来」
   * 的那一刻发生。报错会指向消息数组,看起来像 adapter 的 bug ——
   * 这是手写 Agent 循环最常见的自伤。
   */
  private finalizeAbort(): void {
    // 1) 半截回复。BlockAccumulator 会丢掉未闭合的 tool_call 块,
    //    所以这一步**不会**制造出新的孤儿。
    const acc = this.pending
    this.pending = null
    if (acc !== null) this.commitAssistant(acc.finalize().parts)

    this.closeUnexecutedCalls(INTERRUPTED)
    this.handle.finish('aborted')
  }

  /** Closed calls can also be stranded by a stream failure after tool_call_end. */
  private closeUnexecutedCalls(reason: string): void {
    const orphans = orphanedToolCalls(this.messages)
    if (orphans.length > 0) {
      const output: ToolOutput = { content: reason }
      const parts: ContentPart[] = orphans.map((o) => ({
        type: 'tool_result',
        callId: o.callId,
        output,
        isError: true
      }))
      for (const o of orphans) {
        // 没有这条,UI 上那张工具卡片会停在「执行中」直到下次重载
        this.handle.emit({ type: 'tool_end', callId: o.callId, output, isError: true })
      }
      const now = this.deps.host.clock.now()
      this.commit(toolResultMessage(ulid(now), parts, now))
    }

  }

  // ─────────────────────────── 小工具 ───────────────────────────

  /**
   * 把渲染层排进来的插话变成用户消息。
   *
   * ★ **一条一条提交,不合并成一条。** 合并省不下任何请求(编码器反正会把
   * 相邻用户消息并成一轮),却要为「N 个排队条目 ↔ 1 条消息 id」再发明一套
   * 回执 —— 而 id 一一对应正是渲染层能靠 `message_commit` 收敛队列的全部理由
   * (见 `shared/agent/interject.ts`)。
   *
   * ★ 用条目自带的 id 而不是新 mint 一个,同上。
   *
   * ★ 空 parts 的条目**直接跳过**:提交一条空消息,下一次请求就会被上游以
   * `all messages must have non-empty content` 拒掉,而渲染层那边它已经因为
   * 收到 commit 而离开了队列 —— 消息既没发出去、也回不来了。
   */
  /**
   * 用户拖/选进来的文件引用,路径改写成**说给模型听的那一种形式**:
   * 工作区内的压成工作区相对(`src/a.ts`),工作区外的保留绝对路径。
   *
   * ## 为什么归一化在这里,而不在渲染层或编码层
   *
   * - 渲染层没有 realpath:macOS 上工作区根记录的是 `showOpenDialog` 原样返回的
   *   `/var/…`,而拖进来的文件路径是 `/private/var/…`,词法比较会把工作区**内**的
   *   文件判到外面去,于是一条本该是 `src/a.ts` 的引用写成了长绝对路径。
   * - 编码层没有 `workspaceRoot`,而且改写发生在编码层的话,转录里存的与发出去的
   *   就成了两个字符串 —— UI 悬浮提示说一套,模型看到另一套。
   * - 这里是**两个入口的汇合点**:构造函数那条(拖拽/菜单/粘贴发出的第一句)和
   *   插队那条(生成中排队的消息)都从这里落进转录。改一处就够。
   *
   * ★ 相对形式不是为了好看:模型的文件工具以工作区根为 cwd,`src/a.ts` 可以直接
   * 喂回 `read_file`,而它在 grep 回执里看到的也正是这个写法(同一个 `displayPath`)。
   * 两边不一致时,模型会把同一个文件当成两个。
   */
  private normalizePaths(parts: readonly ContentPart[]): ContentPart[] {
    return parts.map((part) => {
      if (part.type !== 'file_ref') return part
      const expected = this.deps.fileReferenceSource ?? { kind: 'local' }
      if ((this.deps.workspace?.remote && expected.kind !== 'workspace') || !fileReferenceMatches(part.source, expected)) throw new EnvironmentError('conflict')
      const path = this.deps.workspace?.remote
        ? this.deps.workspace.path.display(this.deps.workspaceRoot, part.path)
        : displayPath(this.deps.workspaceRoot, part.path)
      return { ...part, path }
    })
  }

  /**
   * 历史里的文件引用:同样的匹配规则,但**不抛异常**。
   *
   * 抛和放行都是错的。抛会让整条会话永久卡死(见构造函数);放行则把一条在**别的**
   * 环境里算出来的路径喂给模型 —— 两台服务器上存在同一个相对路径时,它会读到一个
   * 完全无关的同名文件,而且没有任何一处会报错。所以换成一句纯文本:模型知道这里
   * 曾经有过一个附件,但拿不到一条能用的路径。
   *
   * 只作用于 `contextMessages`(发给模型的那份)。`this.messages` 是转录,原样保留,
   * 渲染层那张 chip 不受影响。
   */
  private isolateHistoryPaths(parts: readonly ContentPart[]): ContentPart[] {
    const expected = this.deps.fileReferenceSource ?? { kind: 'local' }
    return parts.filter((part) => part.type !== 'goal_status').map((part) => {
      if (part.type !== 'file_ref') return part
      if ((this.deps.workspace?.remote && expected.kind !== 'workspace') || !fileReferenceMatches(part.source, expected)) {
        return { type: 'text', text: foreignReference(part.name) }
      }
      const path = this.deps.workspace?.remote
        ? this.deps.workspace.path.display(this.deps.workspaceRoot, part.path)
        : displayPath(this.deps.workspaceRoot, part.path)
      return { ...part, path }
    })
  }

  private injectInterjections(): number {
    const items = this.handle.takeInterject()
    let injected = 0
    for (const item of items) {
      if (item.parts.length === 0) continue
      if (item.goalId !== undefined && this.deps.acceptsGoalInput?.(item.goalId) !== true) continue
      const now = this.deps.host.clock.now()
      this.commit({ ...userMessage(item.id, this.normalizePaths(item.parts), now), ...(item.internal ? { internal: true } : {}) })
      injected++
    }
    return injected
  }

  /**
   * ★ 查不到别名时**不在这里报错**,用兜底参数照常组装。
   *
   * 让 `UpstreamRouter` 的 `noCandidateError` 成为唯一的权威错误:它分得清
   * 「没有配置这个别名」「所有供应商都在冷却中」「没有已启用的供应商」,
   * 而 session 只知道「表里没有」。两处都报的话,用户会随机收到信息量少的那一条。
   */
  private aliasFor(model: string): ModelAlias | undefined {
    return this.deps.upstream.resolveModel(model, this.req.modelProviderId)
  }

  private commitAssistant(parts: ContentPart[]): void {
    // 空 parts 的消息会被上游拒绝(`all messages must have non-empty content`),
    // 而中断在第一个 delta 之前发生时它就是空的。
    if (parts.length === 0) return
    const now = this.deps.host.clock.now()
    this.commit(assistantMessage(ulid(now), parts, now))
  }

  /**
   * 把只属于 UI 那一轨的标记挂在**最后一条消息**上。
   *
   * ★★ 追加而不是新 commit：`goal_status` 编码后是 null，单独成一条就是一条
   *   零内容块的助手消息 —— 而上游对空 content 是 400（`encode/anthropic.ts`
   *   文件头第 1 条规则）。`commitAssistant` 里那条 `parts.length === 0` 拦的是
   *   「数组为空」，拦不住「数组里有元素但都编码成 null」。
   *   挂在别人身上既保住了转录（重载恢复要反扫它），又不新增任何一条上行消息。
   *
   * ★ **只改转录那一份**（`this.messages`），不进 `contextMessages`：
   *   两个数组是构造函数里 `map` 出来的**两批对象**，靠引用对不上；
   *   而且 `goal_status` 本来就该只存在于转录里 —— 编码器那条 `return null`
   *   是为了防「它被别人带进去了」，不是为了让它进去。
   *
   * ★ 只挂在**助手**消息上。用户那一轨不能出现「目标状态」，两轨必须分开。
   *   挂不上时（转录为空、或最后一条是用户消息）就退化成「只发广播、不进转录」——
   *   那种场景下这一轮本来什么都没发生，重载后恢复不出这个目标是**正确的**。
   */
  private attachUiParts(parts: readonly ContentPart[]): void {
    const index = this.messages.length - 1
    const last = this.messages[index]
    if (last === undefined || last.role !== 'assistant') return
    const next: AgentMessage = { ...last, parts: [...last.parts, ...parts] }
    const committed = this.deps.prepareMessage?.(next) ?? next
    this.deps.onMessageCommit?.(committed)
    this.messages[index] = committed
    this.handle.emit({ type: 'message_commit', message: committed })
  }

  /** 落盘边界(方案 §4.2):也是 RunRegistry 裁剪冗余 delta 的那个点 */
  private commit(message: AgentMessage): void {
    const committed = this.deps.prepareMessage?.(message) ?? message
    this.deps.onMessageCommit?.(committed)
    this.messages.push(committed)
    // Only transcript markers may be added by persistence; never send them upstream.
    this.contextMessages.push({ ...message, parts: message.parts.filter((part) => part.type !== 'goal_status') })
    this.handle.emit({ type: 'message_commit', message: committed })
  }
}
