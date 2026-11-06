/**
 * ToolRegistry —— MCP 一等公民的落点(方案 §4.4)。
 *
 * MCP server 连上后,把 `listTools()` 的结果经**同一个 `register`** 塞进来。
 * Agent 循环完全不知道某个工具是内置、MCP 还是 Skill 带来的 —— 这才是
 * 「一等公民」的实际含义,也意味着只有一条权限链路、一套命名方案、一个执行路径。
 */
import type { ToolInfo, ToolProgress, ToolResult, ToolSource } from '../../../shared/agent/tool'
import type { AgentMessage } from '../../../shared/agent/message'
import type { PermissionMode } from '../../../shared/agent/permission'
import type { Skill } from '../../../shared/domain/skill'
import type { SchedulingBridge } from '../../../shared/domain/scheduled'
import type { ShellBridge } from '../../../shared/domain/shell'
import type { RunStatus } from '../../../shared/agent/event'
import type { AgentError } from '../../../shared/agent/error'
import type { DetachedProcess, KernelHost, PlatformInfo, WorkspacePaths } from '../host'
import type { ImageGenBridge } from '../image-gen'
import type { SessionImageStore } from '../session-images'
import type { VideoGenBridge } from '../video-gen'
import type { SessionVideoStore } from '../session-videos'
import type { InteractFn } from '../interaction-gate'
import { NETWORK_SWITCH_TOOLS } from '../permission-gate'
import { isValidExternalName, sanitizeDescription, ToolNamer } from './naming'

/**
 * 工具能摸到的宿主能力,是 `KernelHost` 的**真子集**。少的两项是故意的:
 *
 * - `secrets`:注册表同时是 MCP 工具的入口(步骤 10),那些工具的代码不是我们写的。
 *   把 `secrets.get()` 递过去,等于 `host.ts` 里「明文 key 永不进内核」那条作废。
 * - `paths`:`userData` 在工作区外面。工具要么走路径围栏,要么根本不该碰路径。
 */
export type ToolHost = Pick<KernelHost, 'fs' | 'spawn' | 'fetch' | 'clock' | 'logger'> & {
  path?: WorkspacePaths
  platform?: PlatformInfo
  remote?: boolean
}

/**
 * 派一个子代理出去 —— `Task` 工具与外面那台机器之间**唯一**的接触面。
 *
 * ★ 这条窄缝的形状和 `SessionUpstream` 是同一个道理,理由也一样:
 * `Task` 住在内核里,而真正能建 run、能推事件给窗口的东西住在 `ipc/agent.ts`。
 * 让 `task.ts` 直接 import 它会形成 `ipc/agent → runtime → tools → task → ipc/agent`
 * 的环,并把 electron 拖进内核的 import 图 —— 那样 `agent-run.test.ts`
 * 「无头 Node 里跑完整条链路」的前提当场就没了。
 *
 * 缺省(`undefined`)= 这个环境里派不了子代理(纯内核测试)。`Task` 会
 * 当场 `toolFail` 并说清楚,而不是抛一个 `undefined is not a function`。
 */
export interface SubagentRequest {
  /** `agents/*.md` 里的 name。找不到时由实现方给出「当前可用:…」的错误 */
  subagentType: string
  /** 交给子代理的完整任务说明 —— 它**看不到当前对话** */
  prompt: string
  /** 3–8 个字的简述,给 UI 上那张卡片用 */
  description: string
  /** 父 run 的这次工具调用 id。`subagent_start/end` 要靠它把子 run 挂到这张卡片上 */
  callId: string
  /** 不等待结果，让父代理继续工作；子 run 仍在主进程中独立运行。 */
  background?: boolean
  /**
   * 这一次派发钉死用哪个模型别名,优先于子代理文件和设置页。
   * ★ 调用方要先用 `ToolContext.subagentModels` 校验过 —— 这里查不到就 `refused`。
   */
  model?: string
  /** 这一次派发的思考档位,优先于子代理文件和设置页;仍按子 run 的模型归一化 */
  thinking?: import('../../../shared/agent/run-request').ThinkingLevel
}

/**
 * 派子代理的两种结局。
 *
 * ★ 拆成两支、而不是把「名字不对」塞进 `status: 'error'`,是因为它们对模型
 * 意味着完全不同的事:`refused` 是**这次调用本身不成立**(名字敲错了、
 * 并发满了),该改的是入参;`finished` 是子代理真的跑了一遍,该看的是产出。
 * 混成一支的话,模型收到的是「子代理 reviewr 失败:没有名为 reviewr 的子代理」——
 * 它会以为那个子代理存在但坏了,于是原样再试一次。
 */
export type SubagentOutcome =
  /** 还没派出去就被拒。`reason` 是给模型看的完整人话,`Task` 原样转交。 */
  | { kind: 'refused'; reason: string }
  | {
      kind: 'finished'
      childRunId: string
      status: RunStatus
      /** 子代理最后一条助手消息的可见文字。`status !== 'done'` 时可能是空串 */
      text: string
      color?: import('../../../shared/domain/agent-def').AgentColor
      /** `status === 'error'` 时的原因 */
      error?: AgentError | string
      /** 子 run 各轮用量之和。上游没报用量时缺省 */
      usage?: import('../../../shared/agent/stream').TokenUsage
    }
  | {
      kind: 'background'
      childRunId: string
      color?: import('../../../shared/domain/agent-def').AgentColor
      /**
       * 并发满了,这次派发**还在排队**,run 尚未创建 —— 有空位时会自动开始。
       *
       * ★ 仍然走 `background` 而不是新开一支 `queued`:对调用方来说两者是同一件事
       * (「派出去了,别等它」),而 `childRunId` 此刻已经 mint 好,任务面板照样找得到。
       * 分成两支只会让 `Task` 里多一条除了措辞之外完全一样的分支。
       */
      queued?: boolean
      /**
       * 前台派出去的,跑到一半被**用户**转去了后台。
       *
       * ★ 单独标出来,因为对模型的措辞不同:它不是自己选的后台,得知道是用户
       * 不想再等,以及结论之后会自己送回来 —— 不然它会以为子任务丢了,再派一次。
       */
      detached?: boolean
    }

export type SpawnSubagentFn = (req: SubagentRequest) => Promise<SubagentOutcome>

/**
 * 工具执行时能拿到的一切。
 *
 * ★ `signal` 必填,且**必须真的传到 execute 体内**(方案 §4.3)。
 * 只中断 HTTP 流而不把 signal 传进工具体,会留下一堆僵尸 shell 和还在写的文件。
 */
export interface ToolContext {
  /** Session owning this run; used by durable plan tools. */
  sessionId?: string
  /** 工作区身份。浏览器工具用它做会话隔离；旧的无头调用可省略。 */
  workspaceId?: string
  workspaceRoot: string
  signal: AbortSignal
  /** 运行期快照 —— run 开始时定死 */
  permissionMode: PermissionMode
  /** 子代理深度,0 = 主 run */
  depth: number
  /** 本次调用的 id,进度事件要带 */
  callId: string
  /** 所属 run。子代理要用它当 parentRunId,日志也靠它把工具调用归到某次运行 */
  runId: string
  /** When set, Write/Edit must target this exact resolved path. */
  writeFileRestriction?: string
  /** 本次 run 冻结的 Skill 快照，避免全局注册表重扫后串工作区。 */
  skills?: readonly Skill[]
  /**
   * 这次 run 用的模型别名与(可选的)显式供应商。
   *
   * ★ 存在的理由只有一个:`CreateScheduledTask` 要让新任务**默认继承当前模型**。
   * 不给的话模型只能自己编一个别名,而编错的代价是一条到点才失败的定时任务。
   */
  model?: string
  modelProviderId?: string
  /**
   * 定时任务的读写通道。缺省 = 这个环境里排不了程(纯内核测试、没有工作区),
   * 四个定时任务工具会整体不下发 —— 见 `builtin/scheduled.ts` 的 `isEnabled`。
   */
  scheduling?: SchedulingBridge
  /**
   * 对话内生图的读写通道。缺省 = 这个环境里生不了图(纯内核测试、没装配),
   * `generate_image` 整体不下发(`isEnabled`)—— 见 `kernel/image-gen.ts` 文件头。
   *
   * ★ 形状同 `scheduling` / `shells`:内核只认这个窄接口,「store 在哪、密钥怎么读、
   * `/images/generations` 怎么发」全部留在 `main/kernel/image-gen.ts` +
   * `runtime.ts` 的装配点。内核仍然零 electron、可单测。
   */
  imageGen?: ImageGenBridge
  /**
   * 本会话的图片仓:生成图落成 `ncw://` 会话附件、按地址把图读回字节。
   * 缺省 = 纯内核测试没装配 —— `generate_image` 退回内联 data URL(回执里不列地址),
   * `SaveImage` 整体不下发(`isEnabled`)。见 `kernel/session-images.ts` 文件头。
   */
  sessionImages?: SessionImageStore
  /**
   * 对话内生成视频的异步通道。缺省 = 这个环境里生不了视频,
   * `generate_video` 整体不下发(`isEnabled`)—— 见 \`kernel/video-gen.ts\`。
   *
   * ★ 与 `imageGen` 的关键差别:提交返回的是**任务**,成品由后台 worker
   * 落成本会话附件;`status` / `cancel` 对**已有任务**有效,与当前选中的
   * 模型和开关都无关。
   */
  videoGen?: VideoGenBridge
  /**
   * 本会话的视频仓。缺省 = 纯内核测试没装配 —— `SaveVideo` 整体不下发
   * (它靠这个按 \`ncw://\` 地址读回字节)。见 \`kernel/session-videos.ts\`。
   */
  sessionVideos?: SessionVideoStore
  /**
   * Agent 手上那些 shell(前台停止句柄 + 后台进程)。缺省 = 这个环境里
   * 既停不了单条命令、也起不了后台命令(纯内核测试),`BashOutput` / `KillShell`
   * 整体不下发,`Bash` 的 `run_in_background` 会当场说清楚而不是假装起了。
   */
  shells?: ShellBridge<DetachedProcess>
  /**
   * ★ 宿主**从 ctx 传进来,不在工具里闭包捕获**。
   *
   * `getTools()` 是进程内单例,而 `installHost()` 只重建 `router`、不动 `tools`
   * (见 runtime.ts)。工厂闭包意味着换宿主之后已注册的工具还攥着旧的那个;
   * 而让 `installHost` 顺手重建 `tools`,又会重建 `ToolNamer` 并丢掉运行期注册的
   * MCP 工具 —— 而 naming.ts 写明了映射在会话内必须稳定(转录里存的是 externalName)。
   * ctx 每次调用现造,所以它天然是「当下那个宿主」。
   */
  host: ToolHost
  /** 进度是**易失的**:单独的事件类型,永不写入转录 */
  emit(progress: ToolProgress): void
  /**
   * 这次 run 的转录(在**这次调用之前**的部分)。
   *
   * 需求:工具的结果文案要能对照「自己上一轮做了什么」—— 目前只有 `TodoWrite`
   * 用得到(它每轮发完整清单,得知道这次改了什么才能把差异回显给模型)。不给的话
   * 工具只能自己去存一份状态,而那正是 `todo.ts` 文件头明令排除的「幽灵清单」。
   *
   * ★ 与 `spawnSubagent` 同一个取舍:**给不出就不放这个字段**(纯内核测试、
   * 还没有转录的场景),而不是放一个空数组 —— 「不知道」和「历史是空的」在
   * 派生逻辑里是两件事。
   */
  messages?: readonly AgentMessage[]
  /**
   * `TodoWrite` 在**本会话**里的外部名(`ToolNamer` 分配的,撞名时带哈希后缀)。
   *
   * 同上,只有清单工具用得到,而它必须来自注册表 —— 写字面量 `'TodoWrite'`
   * 在撞名时**静默**取不到任何东西。见 `shared/agent/todo.ts` 的
   * `latestTodosFrom`。
   */
  todoToolName?: string
  /**
   * 派子代理。只有 `Task` 用得到,所以是可选的 —— 让每个工具的
   * ctx 都必须带上一个它永远不会碰的函数,是没有道理的。
   */
  spawnSubagent?: SpawnSubagentFn
  /**
   * 此刻可用的模型别名(去重)。`Workflow` 要在派出任何节点之前校验节点钉的 `model` ——
   * 派到一半才发现名字不对,前面的节点已经白跑了。
   */
  subagentModels?: () => readonly string[]
  /**
   * 设置 › 实验 里某一项此刻开没开。**现读**,改了设置下一轮就生效。
   * 缺省 = 这个环境里没有设置可读(纯内核测试),一律当作关 —— 实验功能的默认就是关。
   */
  experimentalEnabled?: (feature: keyof import('../../../shared/domain/settings').ExperimentalSettings) => boolean
  interact?: InteractFn
  canProposeGoal?: () => boolean
  proposeGoal?: (condition: string, askUser: boolean) => Promise<'set' | 'pending'>
}

/**
 * 运行时的工具。
 *
 * ★ 定义在 main 而不是 shared,因为 `execute` 是闭包 —— 过不了 IPC。
 * 渲染层拿到的是它的 `ToolInfo` 部分(审批弹窗要显示名字与入参)。
 *
 * 不拆 `ToolDefinition` / `ToolExecutor`:只有定义要跨网络传输时才划算,
 * 而那时执行器无论如何是闭包(方案 §10)。
 */
export interface Tool extends ToolInfo {
  isEnabled?: (ctx: ToolContext) => boolean
  execute(input: unknown, ctx: ToolContext): Promise<ToolResult>
}

/** 注册方提供的东西 —— **没有 externalName**,那是注册表算出来的 */
export interface ToolRegistration extends Omit<ToolInfo, 'externalName'> {
  isEnabled?: (ctx: ToolContext) => boolean
  execute(input: unknown, ctx: ToolContext): Promise<ToolResult>
}

export interface SnapshotFilter {
  context?: ToolContext
  /**
   * Skill frontmatter 的 `allowedTools`。作者写的可能是 internalId 也可能是
   * externalName —— 两边都认,因为让用户去猜我们内部用哪个名字是没有道理的。
   */
  allowList?: readonly string[]
  /**
   * 工作区的「联网搜索」开关(`WorkspaceSettings.webSearch`)。
   *
   * ★ 关掉时**根本不下发**受它管的工具(`permission-gate.ts` 的 `NETWORK_SWITCH_TOOLS`,
   * 目前只有 `WebFetch` / `web_search`),而不是下发了再在调用时拒绝。
   * 后者也拦得住(`permission-gate.ts` 那张表的第 1 行),但代价是模型先花一轮
   * 去调一个注定被拒的工具 —— 用户看到的是「它先试了一次搜索,被拒,才回答」。
   * 那道闸仍然留着:它挡的是「这一轮下发之后开关被关掉」的窗口,以及
   * 任何绕过快照的调用路径。两道各挡一件事,不重复。
   *
   * 缺省(`undefined`)= 不过滤。这是刻意的:`snapshot()` 有一堆调用点
   * (Skill、子代理、诊断),它们不该因为忘了传这个字段就把联网工具全弄没了 ——
   * 真正承重的那道闸在 `permission-gate.ts`,而它不看这个字段。
   */
  network?: boolean
  /**
   * 摘掉「会挂起等人回答」的那几个工具。子代理专用(`depth > 0`)。
   *
   * ★ 这不是一条偏好,是在**兑现 `Task` 工具描述里已经写下的承诺**:
   * 「A SUBAGENT CANNOT ASK THE USER ANYTHING」。以前描述这么写、机制却让它
   * 问得出来,而 `InteractionGate.request()` 没有任何超时 —— 一个子代理调了
   * `AskUserQuestion`,就永远停在那儿,卡片上只剩一个不动的「运行中」。
   * 那是本仓库真实发生过的死锁,不是理论风险。
   *
   * 权限审批(`tool_permission`)走的是另一条路,不经过快照,由 `approveWith`
   * 里那道对称的闸门挡住 —— 两处都要有,缺一处就还是能挂起。
   */
  noInteraction?: boolean
}

/**
 * 调用后会挂起、直到用户回答才继续的工具 —— 也就是 `run` 里 `await ctx.interact(...)`
 * 的那几个。全仓只有两个:`AskUserQuestion` 和 `ExitPlanMode`。
 *
 * Plan 和 ACP 的主代理工具策略都不允许子代理触发这些交互；这里再做一次机制层过滤，
 * 防止自定义 agent/tool 白名单意外重新开放后造成无人能回答的死锁。
 */
const INTERACTIVE_TOOLS = new Set(['AskUserQuestion', 'ExitPlanMode'])

function sourceKey(s: ToolSource): string {
  switch (s.kind) {
    case 'builtin':
      return 'builtin'
    case 'mcp':
      return `mcp:${s.serverId}`
    case 'skill':
      return `skill:${s.skillId}`
    case 'plugin':
      return `plugin:${s.pluginId}`
  }
}

export class ToolRegistry {
  /** internalId → Tool。**插入顺序即快照顺序**,让下发给模型的工具列表稳定。 */
  private readonly tools = new Map<string, Tool>()
  private readonly namer = new ToolNamer()

  /**
   * 注册(或替换)一个工具。返回补全了 `externalName` 的运行时对象。
   *
   * 重复的 internalId 是**替换**而不是报错 —— MCP server 重连时会原样再注册一遍,
   * 那是正常路径,不是错误。因为 externalName 由 internalId 记忆,替换后名字不变,
   * 历史转录里的引用仍然对得上。
   */
  register(reg: ToolRegistration): Tool {
    const externalName = this.namer.nameFor(reg.internalId)
    /* c8 ignore next 3 */
    if (!isValidExternalName(externalName)) {
      throw new Error(`为 ${reg.internalId} 生成的 externalName 非法:${externalName}`)
    }
    const tool: Tool = {
      ...reg,
      externalName,
      // ⚠️ 不可信输入的收口点:MCP / Skill 的描述直接进系统提示词
      description: sanitizeDescription(reg.description)
    }
    this.tools.set(reg.internalId, tool)
    return tool
  }

  /**
   * 下线一个来源的全部工具(MCP server 断开、Skill 停用)。
   *
   * ★ 若该源某个工具**正在执行,不能打断它**(方案 §4.4)。
   * 这里天然满足:执行方在调用前就持有了 `Tool` 对象的引用,
   * 从 map 里删掉不影响那个引用 —— 前提是执行方**不在完成时回注册表重新查找**。
   */
  unregisterBySource(source: ToolSource): number {
    const key = sourceKey(source)
    let n = 0
    for (const [id, t] of this.tools) {
      if (sourceKey(t.source) === key) {
        this.tools.delete(id)
        n++
      }
    }
    return n
  }

  /**
   * ★ **每轮开始时取一次**(方案 §4.4)。
   *
   * 这样 server 中途断开产出的是一个工具错误(下一轮它就不在列表里了),
   * 而不是崩溃 —— 也不会出现「下发的工具列表里有,执行时却找不到」的错位。
   */
  snapshot(filter: SnapshotFilter = {}): Tool[] {
    const allow = filter.allowList === undefined ? undefined : new Set(filter.allowList)
    const out: Tool[] = []
    for (const t of this.tools.values()) {
      if (filter.context !== undefined && t.isEnabled?.(filter.context) === false) continue
      // ★ 按名单而不是 `t.needsNetwork`:开关只管网页搜索与抓取(见 NETWORK_SWITCH_TOOLS)。
      //   必须和 runtime.ts `approveWith` 读同一张表,分家就是「列表里没有,调用却放行」。
      if (filter.network === false && NETWORK_SWITCH_TOOLS.has(t.internalId)) continue
      if (filter.noInteraction === true && INTERACTIVE_TOOLS.has(t.internalId)) continue
      if (allow !== undefined && !allow.has(t.internalId) && !allow.has(t.externalName)) continue
      out.push(t)
    }
    return out
  }

  /**
   * 模型回传的是 externalName —— 这是把它换回工具的唯一入口。
   *
   * 返回 undefined 有两种含义,调用方都该当成**工具错误**而不是崩溃:
   * 名字是模型编的,或者工具在这一轮进行中被下线了。
   */
  resolveByExternalName(name: string): Tool | undefined {
    const internalId = this.namer.toInternal(name)
    return internalId === undefined ? undefined : this.tools.get(internalId)
  }

  byInternalId(internalId: string): Tool | undefined {
    return this.tools.get(internalId)
  }

  /**
   * 预留一个 internalId 的 externalName,**不注册工具**。
   *
   * ★ 给插件 catalog 用:插件工具只有被激活事件叫醒、跑完 `tools.register` RPC
   * 之后才会真的 `register()`,而菜单/卡片贡献要在激活**之前**就画出来,
   * 那时渲染层已经需要 externalName 去 join。直接委托 `namer.nameFor`
   * (幂等 + 记忆化):此刻预留的名字,与将来真正 `register()` 时分配的**同一个**,
   * 于是历史转录与 presenter 都不会失配。
   */
  reserveName(internalId: string): string {
    return this.namer.nameFor(internalId)
  }

  /** 给 IPC `agent:listTools` 用 —— 去掉 execute 之后才过得了结构化克隆 */
  info(filter: SnapshotFilter = {}): ToolInfo[] {
    return this.snapshot(filter).map(({ execute: _execute, isEnabled: _isEnabled, ...info }) => info)
  }

  get size(): number {
    return this.tools.size
  }
}
