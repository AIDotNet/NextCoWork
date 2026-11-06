/**
 * 运行时装配 —— 把内核的几个零件接成一台能跑的机器,并持有它们的单例。
 *
 * ★ **本文件零 electron import**,尽管它住在 `src/main/` 下。
 *
 * 宿主由 `main/index.ts` 在 app ready 之后经 `initRuntime(electronHost())` 注入。
 * 反过来写(runtime 直接 `import { electronHost }`)会让 `ipc/agent.ts` 的整条
 * import 链拖进 electron —— 而 `agent-pump.test.ts` 与 `agent-run.test.ts` 能在
 * 无头 Node 里跑完 IPC 泵与整条 Agent 链路,靠的正是这条链是干净的。
 * 哪天有人在这里加一个 electron 的**值**导入,挂的会是那两个测试,而那是正确的报警。
 *
 * 它存在的另一个理由是**收口**:`ipc/agent.ts` 只该关心「合批与推送」,
 * 不该同时知道 provider 表长什么样、工具在哪注册、演示上游挂在哪一层。
 */
import { randomUUID } from 'node:crypto'
import { mkdirSync } from 'node:fs'
import { join } from 'node:path'
import type { RunRequest, ThinkingLevel } from '../shared/agent/run-request'
import { MAX_DEPTH } from '../shared/agent/run-request'
import type { AgentEvent, RunStatus } from '../shared/agent/event'
import { agentError, type AgentError } from '../shared/agent/error'
import { visibleText, type AgentMessage, type SubagentResult } from '../shared/agent/message'
import type { AgentDefinition } from '../shared/domain/agent-def'
import { minPermission } from '../shared/agent/permission'
import { DEFAULT_WORKSPACE_SETTINGS } from '../shared/domain/workspace'
import {
  DEFAULT_UPSTREAM_IDLE_TIMEOUT_SECONDS,
  isUpstreamIdleTimeoutSeconds
} from '../shared/domain/settings'
import { AgentSession } from './kernel/agent-session'
import { createTodoReconciler } from './kernel/todo-reconciliation'
import { abortable } from './kernel/abort'
import type { KernelHost } from './kernel/host'
import { nodeHost } from './kernel/host'
import { createApprovalFn, type ApprovalDeps, type ApprovalPluginInterceptor } from './kernel/approval'
import { interactions, type InteractionDraft } from './kernel/interaction-gate'
import type { RunHandle } from './kernel/run-registry'
import { runs } from './kernel/run-registry'
import { takeChanges } from './kernel/tool/builtin/change-recorder'
import type { Capacity, SlotRefusal } from './kernel/subagent-queue'
import { SubagentQueue } from './kernel/subagent-queue'
import { AGENTS_DIR, PROJECT_AGENTS_PREFIX, scanAgents } from './kernel/agent/load'
import type { GitContext } from './kernel/git-context'
import { readGitContext } from './kernel/git-context'
import { INSTRUCTIONS_MAX, sanitizeInstructions, scanInstructions } from './kernel/instructions'
import { clampWithEllipsis } from './kernel/text'
import { readTextBounded } from './imports/assets'
import { managedInstructionsPath } from './imports/managed-paths'
import { agentRegistry, resetAgentRegistries } from './kernel/agent/registry'
import { MODES_DIR, PROJECT_MODES_PREFIX, scanModes } from './kernel/mode/load'
import { modePromptFor, modeRegistry, resetModeRegistries } from './kernel/mode/registry'
import { activePlanFor, planToolAllowList } from './kernel/plan-run'
import { loadPlanExecution, type PlanExecutionContext } from './kernel/plan-execution'
import type { ModeDefinition } from '../shared/domain/mode'
import { normalizeModeId } from '../shared/domain/mode'
import type { Skill } from '../shared/domain/skill'
import { PROJECT_SKILLS_PREFIX, SKILLS_DIR, currentPluginSkillRoots, scanSkills } from './kernel/skill/load'
import { resetSkillRegistries, skillRegistry } from './kernel/skill/registry'
import { SkillActivationBarrier } from './kernel/skill/activation'
import { buildSkillExtractionBlock, writtenProjectSkillNames } from './kernel/skill/extraction'
import { builtinTools, registerToolProvider } from './kernel/tool/builtin'
import { taskTool } from './kernel/tool/builtin/task'
import { installSkillToolPorts, resetSkillToolPortsForTest } from './kernel/tool/skill-port'
import { syncPluginTools } from './kernel/tool/plugin-tools'
import type { SpawnSubagentFn, ToolRegistration } from './kernel/tool/registry'
import { ToolRegistry } from './kernel/tool/registry'
import { McpManager } from './mcp/manager'
import { environmentTransport } from './mcp/environment-transport'
import type { McpServerConfig, McpServerStatus } from '../shared/domain/mcp'
import { installSearchConfig } from './search/service'
import { withDemo } from './kernel/upstream/demo'
import { opencodeGoProtocolFor } from './kernel/upstream/opencode-protocol'
import { ConfigProfileError, currentConfigScope, defaultWorkspaceIdForScope, MODEL_DEFAULTS_SEEDED_KEY } from './db/config-profile'
import { ensureProviderAccountsSeeded } from './db/provider-accounts'
import type { ProviderConfigSource } from './kernel/upstream/router'
import { UpstreamRouter } from './kernel/upstream/router'
import { AccountPool } from './kernel/upstream/account-pool'
import {
  BUILTIN_PLAN_PROVIDER_ID,
  BUILTIN_PROVIDER_ID,
  endpointFor,
  findPreset
} from '../shared/domain/presets'
import type { ModelAlias, UpstreamProtocol } from '../shared/domain/provider'
import { subagentModelSelection } from '../shared/domain/model-selection'
import { normalizeModelThinkingLevel } from '../shared/domain/model-runtime'
import { subagentThinkingSelection, type SubagentThinking } from '../shared/domain/subagent-thinking'
import { searchSecretRef } from '../shared/domain/search'
import { searchStatuses } from './search/status'
import { store } from './state/store'
import { schedulingBridgeFor } from './scheduled/bridge'
import { shellBridgeFor } from './agent-shells'
import { imageGenBridgeFor } from './kernel/image-gen'
import { sessionImageStoreFor } from './session-images'
import { videoGenBridgeFor } from './video-gen'
import { sessionVideoStoreFor } from './session-videos'
import { getVideoManager, installVideoRuntime } from './video-generation/runtime'
import { getVideoJob as getVideoJobRow, listRecoverableVideoJobs, listVideoJobsForSession, putVideoJob, removeVideoJob } from './db/video-jobs'
import type { VideoJobView } from '../shared/domain/video-generation'
import { resolveImageDataRef } from './kernel/upstream/images'
import { parseCredential } from '../shared/domain/credential'
import { listResolvedModels } from './state/model-bindings'
import { getEffectivePricing } from './pricing-sync'
import { findPricing, priceOf } from '../shared/domain/pricing'
import type { ModelPricing, PriceResult, RunCost } from '../shared/domain/pricing'
import { addTokenUsage, type TokenUsage } from '../shared/agent/stream'
import { defaultProtocolForModel, findBuiltinModel } from '../shared/domain/model-catalog-inventory'
import type { UnpricedUsageAttempt } from '../shared/domain/usage'
import { SessionTitleGenerator } from './session-title'
import { AgentDraftGenerator } from './agent-draft'
import { CommitMessageGenerator } from './commit-message'
import { PromptSuggestionGenerator } from './prompt-suggestion'
import { InlineCompletionService } from './inline-completion'
import type { SessionChange } from '../shared/domain/session'
import { ulid } from '../shared/util/id'
import { clearHookFailuresForTest, installHookHostPorts, resetHookHostPortsForTest, runHookEvent } from './hooks'
import {
  bindGoalRun, goalProposalsFor, handleTurnEnd, installGoalHost, pauseGoal, prepareGoalMessage, releaseGoalRun,
  resetGoalRuntimeForTest, restoreGoal, stopAllGoals, wakeGoal, type TurnEndContext
} from './goal/runtime'
import { getActiveGoal, resetGoalsForTest } from './goal/state'
import { mergeGoalStatusHistory } from './goal/restore'
import { clearRuntimeHooks } from './hook-registry'
import type { ConnectionStatus, SshConnectionProfile } from '../shared/domain/environment'
import { EnvironmentManager } from './environment/manager'
import { localEnvironment } from './environment/local'
import { connectSshEnvironment } from './environment/ssh/provider'
import { openSshProxyTunnel } from './environment/ssh/proxy'
import { EnvironmentError } from './environment/errors'
import { normalizeEnvironmentRef } from '../shared/domain/environment'
import type { FileReferenceSource } from '../shared/domain/attachment'
import type { WorkspaceEnvironment } from './environment/contract'

let host: KernelHost | null = null
let router: UpstreamRouter | null = null
let tools: ToolRegistry | null = null
let mcp: McpManager | null = null
let seeded = false
let sessionTitles: SessionTitleGenerator | null = null
let agentDrafts: AgentDraftGenerator | null = null
let commitMessages: CommitMessageGenerator | null = null
let promptSuggestions: PromptSuggestionGenerator | null = null
let inlineCompletions: InlineCompletionService | null = null
let widgetUpstream: UpstreamRouter | null = null
let environments: EnvironmentManager | null = null
let environmentStatusSink: ((status: ConnectionStatus) => void) | undefined
let environmentAuthentication: ((profile: SshConnectionProfile, senderId: number) => Promise<Parameters<typeof connectSshEnvironment>[2]>) | undefined

export function installEnvironmentInteraction(authentication: NonNullable<typeof environmentAuthentication>, status: NonNullable<typeof environmentStatusSink>): void {
  environmentAuthentication = authentication
  environmentStatusSink = status
}

export function getEnvironments(): EnvironmentManager {
  environments ??= new EnvironmentManager({
    workspace: (id) => store.getWorkspace(id), profile: (id) => store.getConnectionProfile(id),
    local: (root) => localEnvironment(getHost(), root),
    connect: async (profile, context) => {
      if (!environmentAuthentication) throw new EnvironmentError('authentication')
      return connectSshEnvironment(profile, context, await environmentAuthentication(profile, context.senderId), {
        /*
          需求:SSH 连接默认跟随应用/系统代理。ssh 是子进程,`session.setProxy` 管不到它,
          所以在这里替它问一次「连这台主机该走哪个代理」,再把它接到一条本地隧道上
          (`environment/ssh/proxy.ts`)。判定与凭据全部来自 `net/proxy.ts`,和模型请求同一份。

          ★ 走的是**宿主端口**(`KernelHost.resolveProxyForHost`),不是 import `net/proxy`:
          后者自己也要 `getHost()`,引进来就成了 runtime ⇄ net/proxy 的循环依赖 ——
          这个文件被几乎所有主进程模块引用,循环一旦成立,踩到的人不是这里。
          端口由 electron 侧(`host/index.ts`)注入同一份实现。

          ★ 端口缺席(纯 Node 宿主、测试)就是直连:那正是「没设置过代理」时的默认行为。
        */
        openProxyTunnel: async (hostname, port) => {
          const target = await getHost().resolveProxyForHost?.(hostname, port) ?? null
          if (target === null) return null
          getHost().logger.info(`[proxy] ssh ${hostname}:${String(port)} → ${target.scheme}://${target.host}:${String(target.port)}`)
          return openSshProxyTunnel(target, hostname, port,
            (error) => getHost().logger.warn(`[proxy] ssh 隧道握手失败: ${error.message}`))
        }
      })
    },
    onStatus: (status) => environmentStatusSink?.(status)
  })
  return environments
}

/** Goal IO stays behind the same store/host boundary as the agent runtime. */
export function ensureGoalRuntime(): void {
  installGoalHost({
    now: () => getHost().clock.now(),
    history: (sessionId) => store.getHistory(sessionId),
    goalHistory: (sessionId) => {
      const last = store.lastGoalStatusMessage(sessionId)
      return last === undefined ? [] : [last]
    },
    commit: (sessionId, message) => { store.commitMessage(sessionId, message) },
    exists: (sessionId) => store.getSession(sessionId) !== undefined,
    tokens: (sessionId) => Object.values(store.getSessionRunUsage(sessionId))
      .reduce((sum, usage) => sum + usage.inputTokens + usage.outputTokens
        + (usage.cacheReadInputTokens ?? 0) + (usage.cacheCreationInputTokens ?? 0), 0),
    log: (line) => getHost().logger.info(line)
  })
}

runs.onAbortAll(() => { stopAllGoals(); clearRuntimeHooks(); interactions.clear() })

export function getWorkspaceEnvironment(workspaceId: string): WorkspaceEnvironment { return getEnvironments().get(workspaceId) }
export async function shutdownEnvironments(): Promise<void> { await environments?.shutdown() }

/**
 * 谁来广播 MCP 的状态变化。
 *
 * ★ 这个插槽的存在,是本文件那条「零 electron import」铁律的直接后果:
 * 广播要 `windows.emitToAll`,而 `window/registry` 拖着 electron ——
 * 一旦从这里 import 它,`agent-pump.test.ts` 那条无头链路当场断。
 * 所以由 `ipc/mcp.ts`(那一侧本来就在 electron 里)在 `registerIpc()` 时装进来,
 * 和 `registerThemeBridge()` 是同一种接线。
 *
 * 没装(测试里)时是一次 no-op,而不是「状态变了但没人知道」—— 因为测试里
 * 根本没有窗口可广播。
 */
let mcpOnChange: ((id: string) => void) | null = null

/**
 * 会话持久化发生在 runtime，但广播属于 Electron IPC 层。和 MCP 状态
 * 广播一样用一个零 Electron 的注入回调，避免 runtime 反向依赖窗口模块。
 */
let sessionOnChange: ((change: SessionChange) => void) | null = null

/** 一轮改动集封包完成后的广播口（由 ipc 注入，纯 Node 测试中 no-op）。 */
let reviewOnChange: ((change: { runId: string; sessionId: string; workspaceId: string }) => void) | null = null

/**
 * 提炼会话这一轮用 Write/Edit 写了哪些项目 Skill(按名字)。
 * ★ 和上面几个 sink 同一个套路:启用 Skill 要改工作区设置并广播 `skills:changed`,
 * 那是 `ipc/skills.ts` 的事,runtime 不能反向依赖它。由 `ipc/index.ts` 装上。
 */
let skillWrittenOnChange: ((change: { workspaceId: string; sessionId: string; names: string[] }) => Promise<void>) | null = null

// 需求:只登记尚未结束的激活工作,不缓存第二份 activeSkillIds;工作区设置始终是唯一真源。
const skillActivationBarrier = new SkillActivationBarrier()

/**
 * 某条凭证被刷新、或者被标成需要重新登录了。
 *
 * ★ 和上面那个 sink 是同一个套路、同一个理由:内核里的 `CredentialResolver`
 * 拿不到窗口,而 runtime 又不能反向依赖窗口模块(**零 Electron import** 是
 * 无头测试能跑通的前提)。真正的广播器由 `ipc/index.ts` 在启动时装上。
 */
let credentialOnChange: ((credentialRef: string) => void) | null = null

/**
 * 某家供应商的账号列表变了(限流落闸/解除、额度快照更新)。
 *
 * ★ 和上面那两个 sink 同一个套路、同一个理由。装配在 `ipc/index.ts`;
 * 不装的话设置页上的「限流中」要等用户手动重开才更新。
 */
let accountsOnChange: ((providerId: string) => void) | null = null

/** 由 `ipc/index.ts` 装上。★ 和 `setCredentialChangeListener` 逐条同形 */
export function setAccountsChangeListener(fn: ((providerId: string) => void) | null): void {
  accountsOnChange = fn
}

/**
 * 某条视频任务的状态变了（提交成功 / 进度 / 落盘 / 失败）。
 *
 * ★ 和上面那几个 sink 是同一个套路、同一个理由：视频广播要 `windows.emitToAll`
 * (`ipc/video.ts`)，而那个模块拖着 `electron` —— runtime 一旦静态 import 它，
 * 文件头那条「零 electron import」当场破，`agent-run.test.ts` 的无头链路随之断。
 * 所以真正的广播器由 `ipc/index.ts` 在 `registerIpc()` 时装上，
 * 没装（纯内核测试）时是一次 no-op。
 */
let videoOnChange: ((view: VideoJobView) => void) | null = null

/** 由 `ipc/index.ts` 在注册阶段安装；纯 Node 测试中保持 no-op。 */
export function setVideoJobChangeListener(fn: (view: VideoJobView) => void): void {
  videoOnChange = fn
}

/**
 * 装宿主。**必须在第一个 run 之前**,由 `main/index.ts` 在 `app.whenReady()` 里调用 ——
 * `safeStorage` 与 `net.fetch` 都要求 app ready。
 */
export function installHost(h: KernelHost): void {
  shutdownSessionTitles()
  skillActivationBarrier.clear()
  void environments?.shutdown()
  environments = null
  host = h
  /*
    ★ 搜索服务的 Key 访问器。装在这里(而不是 `initRuntime`)是因为
    换宿主就得换访问器 —— 它闭包捕获的是 `h`,不是 `getHost()`。
    捕获后者的话这段代码本身没问题,但它会掩盖「谁依赖宿主」这件事。

    工具那一侧拿不到 `secrets`(`ToolHost` 里没有),所以这是明文 Key
    在整个工具链上的唯一入口,且它不经过 `ToolContext`。
  */
  installSearchConfig({
    statuses: () => searchStatuses(h.secrets),
    apiKey: (id) => h.secrets.get(searchSecretRef(id)),
    // 免 Key 兜底用的自建 SearxNG 地址。★ 现读而不是在这里取一次快照:
    // 用户在设置页改完地址后,下一次搜索就该按新值走,不必重启也不必换宿主。
    selfHostedSearxng: () => store.getSettings().builtinSearch.searxngUrl
  })
  // 路由器在构造时就抓住了 host 的引用,换宿主必须让它重建,
  // 否则新装的宿主对已经建好的路由器完全不起作用
  router = null
  // 辅助请求那几个路由器同样抓着旧 host(`shutdownSessionTitles` 已经处理了标题那一个)
  agentDrafts = null
  commitMessages = null
  promptSuggestions = null
  inlineCompletions = null
  widgetUpstream = null
  /*
    ★ 钩子执行器(零 runtime import)的宿主口。访问器全部**现取**,不做快照 ——
    换宿主之后同一批判定钩子该走新的上游与新的时钟(和上面 `installSearchConfig`
    捕获 `h` 的理由相反:那几个捕获的是入参,这几个必须闭包到 `getHost()`/`getRouter()`)。
  */
  installHookHostPorts({
    host: () => getHost(),
    upstream: () => getRouter(),
    history: (sessionId) => store.getHistory(sessionId),
    session: (sessionId) => store.getSession(sessionId)
  })
  /*
    ★ Skill 工具的遥测口。内核那侧零 store 依赖(见 `kernel/tool/skill-port.ts`),
    这一行是它在生产上的唯一来源;端口没装时工具只是不记遥测,正文照取。
  */
  installSkillToolPorts({
    recordSkillTrigger: (skillId, workspaceId) => { store.recordSkillTrigger(skillId, workspaceId) }
  })
}

export function getHost(): KernelHost {
  /**
   * 没装过就用 `nodeHost` + 演示上游。这**不是测试替身**:`host.ts` 的文件头
   * 已经把 `nodeHost()` 定义成真实默认值,Electron 侧只覆盖 paths/secrets/fetch 三项。
   * 于是无头测试跑的是和 dev 完全同一条装配路径,只是宿主换了一层皮。
   */
  if (host === null) installHost(withDemo(nodeHost()))
  return host!
}

/**
 * 路由器的配置来源。用函数而不是快照数组:设置页改完 provider 立刻生效,
 * 不必重建路由器(见 `ProviderConfigSource` 的注释)。
 */
const providerConfig: ProviderConfigSource = {
  providers: () => store.listProviders(),
  aliases: () => listResolvedModels(),
  failoverEnabled: () => store.getSettings().gateway.failover
}

/**
 * 设置页「连接 › 网络 › 上游空闲超时」那一栏。作为**函数**喂给每一个
 * `UpstreamRouter`(正文 + 三个辅助请求),于是每次请求时现读 —— 改完设置
 * 立刻对下一条请求生效,不必重建路由器。坏值理论上已被 `mergeSettings`
 * 拦光,这里再兜一层默认,防的是直改数据库之类的旁路。
 */
function upstreamIdleTimeoutMs(): number {
  const seconds = store.getSettings().upstreamIdleTimeoutSeconds
  return isUpstreamIdleTimeoutSeconds(seconds)
    ? seconds * 1000
    : DEFAULT_UPSTREAM_IDLE_TIMEOUT_SECONDS * 1000
}

/**
 * 全新安装要种进去的东西。
 *
 * ★★ **演示上游(`demo.invalid`)不再进供应商表。**
 * 它以前是种的,理由写在这里:priority 100 全表最低、别名不会和真别名撞、
 * 「一直在」比「条件注册」少一个要回答的问题。**那些理由都还成立,推翻它的是别的:
 * 内置上游现在是一家真服务(RoutinAI),而设置页里并排列着一条地址写着
 * `https://demo.invalid` 的供应商,对用户就是一件需要解释的东西** ——
 * 它既不能用、又删不掉(seed 会把它种回来),而「点开就能用」这个它本来要买的好处,
 * 已经被那家真上游买走了。
 *
 * ★ **`demo.ts` 一行没动,那套机器整个还在**:`withDemo` 仍然包在生产宿主外面
 * (`host/index.ts`),发往 `demo.invalid` 的请求照旧被截下来喂罐头 SSE。
 * 也就是说手工建一个指向 `https://demo.invalid` 的供应商,今天照样能跑完整条链路 ——
 * 拿掉的只是「默认替用户建好它」。测试里要它的,自己 `store.putProvider(DEMO_PROVIDER)`
 * 一行就有(`agent-run.test.ts` / `subagent-wiring.test.ts` 就是这么做的)。
 */
function seed(): void {
  if (seeded) return
  seeded = true
  /*
    ★★ 文件模式(`providers.json`)里**不种内置上游,也不种指向它们的默认模型**:
    文件只放用户自己添加的供应商,模板在「添加供应商」的预设列表里,不该每次启动
    都被写回(删掉的还会复活)。旧库里没配过 key 的内置上游由迁移直接丢弃,见 host 的
    `migrateProvidersToFile`。
  */
  const fileMode = store.usesProviderFile()
  if (!fileMode) seedBuiltinUpstream()
  backfillOpencodeGoProtocol()

  /**
   * 没配过 = 全新安装,指向内置上游的别名。**两个设置项指两个不同的模型:**
   * 正文用 `deepseek-v4-pro`,子代理用 `deepseek-v4-flash` —— 子代理是被批量拉起的
   * (`subagent.perSessionLimit` 默认 4),拿主力模型跑它们既慢又贵。
   *
   * ★ 名字不在这里写死,从预设的 `suggestedModels` 里按位置取 ——
   * 那张表是这两个模型唯一的出处,写第二遍就会有一天它们对不上,
   * 而症状是 `defaultModel` 指着一条**不存在的别名**:不报错,下一次发送才在路由器里炸。
   *
   * ★ 这两条别名**都没有密钥**(见 `seedBuiltinUpstream`),所以第一次发送会得到一个
   * 鉴权错误而不是一段回答 —— 这是拿掉演示上游换来的代价,是清楚的:
   * 一个「填 key」的提示,比一条地址写着 `demo.invalid` 的假供应商更好解释。
   *
   * 常量留在 main 侧而不是写进 `DEFAULT_SETTINGS`:`src/shared/` 不能
   * 反向 import `src/main/`,而上游是 main 的东西。
   *
   * ★★ 「没配过」靠 `MODEL_DEFAULTS_SEEDED_KEY` 判,**不靠值是不是空串**。
   * 原先写的是 `=== ''` 就种,但空串在设置页是一个可选项(「跟随对话」),
   * 而 `seed()` 每次进程启动 / 每次切账户都会重跑 —— 于是用户选了「跟随对话」,
   * 重启后又被顶回 deepseek,表现为「保存了,重启就变回去」,且零报错。
   * 现在每个配置作用域只种一次;标记跟着作用域走(登记在
   * `db/config-profile.ts` 的 `PROFILE_KV_KEYS`),所以新账户的空作用域照样会种。
   * 代价:升级后第一次启动时还没有标记,已经选了「跟随对话」的人会被最后顶回一次。
   */
  if (!fileMode && store.getKv<unknown>(MODEL_DEFAULTS_SEEDED_KEY, false) !== true) {
    store.setKv(MODEL_DEFAULTS_SEEDED_KEY, true)
    const builtin = findPreset(BUILTIN_PROVIDER_ID)?.suggestedModels ?? []
    const settings = store.getSettings()
    const [defaultModel, subagentModel] = [builtin[0], builtin[1]]

    if (defaultModel !== undefined && settings.defaultModel === '') {
      // 种子时把供应商一并写死是对的:这一刻只有内置上游一家,不存在「按优先级择优」的余地。
      store.updateSettings({ defaultModel, defaultModelProviderId: BUILTIN_PROVIDER_ID })
    }
    if (subagentModel !== undefined && settings.subagent.model === '') {
      store.updateSettings({ subagent: { model: subagentModel, modelProviderId: BUILTIN_PROVIDER_ID } })
    }
  }

  seedDefaultWorkspace()
}

/**
 * 内置上游 RoutinAI —— **全新安装被种进供应商表的那两条。**
 *
 * ★★ **它们和演示上游是两个东西。**
 * 演示上游(`demo.invalid`)是**假网络**:`withDemoUpstream` 按主机名把它的请求
 * 截下来喂罐头 SSE。那套机器整个还在(见上面 `seed` 的文件头),只是不再替用户
 * 建好那条配置。内置上游是**真网络**,要真 key。
 *
 * ★ 演示上游不种了,但下面这条禁令**一个字都没松**:绝不能把
 * `DEMO_PROVIDER.baseUrl` 改成 api.routin.ai ——
 * 那个常量正是 `withDemoUpstream` 用来算劫持主机名的东西
 * (`demo.ts` 的 `new URL(DEMO_PROVIDER.baseUrl).hostname`)。改了它,
 * **每一个发往 api.routin.ai 的真请求都会收到罐头假回复**,而且看起来完全正常 ——
 * 正是 demo.ts 文件头那句「dev 里一切正常,因为根本没有请求出去过」的最坏版本。
 *
 * ★★ **两条是两条,不是一条的两种协议。** 按量走 `api.routin.ai` + Anthropic,
 * 订阅(Plan)走 `api.routin.ai/plan/v1` + Responses。两边的 key 不通用 ——
 * 合成一条记录再让用户翻「API 格式」开关,填了哪种 key 另一半就全 401。
 *
 * 地址和模型都从预设表取,不在这里写第二遍:两处各写一份的话,改了预设而没改这里,
 * 内置上游会停在一个旧地址上,而界面显示的是预设那条。
 *
 * ★ 不种密钥。演示上游有一个常量假 key(走的是 secrets 同一条取值路径),
 * 这里**没有** —— 往一个真服务发 `sk-demo-not-a-real-key` 只会换回一个 401,
 * 而那句「未授权访问」会让用户以为是自己填错了。没有 key 就显示「未配置」。
 */
const BUILTIN_UPSTREAMS: readonly {
  presetId: string
  protocol: UpstreamProtocol
  priority: number
}[] = [
  // 50 / 51:都让位给用户自己配的(预设建出来是 `PRESET_PRIORITY` 60)。
  // 手工建的演示上游仍是 100 —— 全表最低,谁都排在它前面
  { presetId: BUILTIN_PROVIDER_ID, protocol: 'anthropic', priority: 50 },
  { presetId: BUILTIN_PLAN_PROVIDER_ID, protocol: 'openai-responses', priority: 51 }
]

/**
 * 预设里的 `suggestedModels` → 一条可用的别名(`alias === upstreamModel`)。
 *
 * ★ 元数据**从内置 catalog 取,不在这里手写**。以前这里写死
 * `200_000 / 64_000 + 四个 true`,而那串数字对 `claude-fable-5-1` 都不准,
 * 对 deepseek(1M 上下文)差了五倍 —— 表现是上下文条和「即将超长」的判断全是错的,
 * 且不报错。catalog 里查不到的(比如 `gpt-5.3-codex-spark`,订阅线独有)才退回保守值。
 */
function builtinAlias(providerId: string, model: string, index: number): ModelAlias {
  const known = findBuiltinModel(model)
  // ★ 厂商默认协议(claude 系 → anthropic 线形),表在 model-catalog-inventory ——
  // 拉取列表(setAliases)和老库回填用的是同一张表,别在这里写第二份判断
  const wireProtocol = defaultProtocolForModel(model)
  return {
    alias: model,
    providerId,
    upstreamModel: model,
    ...(wireProtocol === undefined ? {} : { protocolOverride: wireProtocol }),
    // 表内顺序即优先级 —— 预设里 deepseek 排在前面是因为设置项要指它们
    priority: index * 10,
    capabilities: known?.capabilities ?? {
      tools: true,
      vision: false,
      thinking: true,
      caching: true
    },
    contextWindow: known?.contextWindow ?? 200_000,
    maxOutputTokens: known?.maxOutputTokens ?? 64_000,
    ...(known === undefined
      ? {}
      : {
          displayName: known.displayName,
          modality: known.modality,
          thinkingConfig: known.thinkingConfig
        })
  }
}

function seedBuiltinUpstream(): void {
  for (const spec of BUILTIN_UPSTREAMS) {
    const preset = findPreset(spec.presetId)
    if (preset === null) continue
    const endpoint = endpointFor(preset, spec.protocol)
    if (endpoint === null) continue

    // Seeding runs once per process, including after a database restart. Never
    // overwrite an existing provider: doing so would erase protocolOptions
    // (including the user's Anthropic cache TTL), a protocol switch, or an
    // explicit disabled state. A deleted built-in provider is still recreated.
    const existing = store.listProviders().find((p) => p.id === preset.id)
    if (existing === undefined) {
      store.putProvider({
        id: preset.id,
        name: preset.name,
        protocol: endpoint.protocol,
        baseUrl: endpoint.baseUrl,
        credentialRef: `provider:${preset.id}`,
        priority: spec.priority,
        enabled: true
      })
    }

    /*
      ★ 别名也按「不存在才种」,和上面那条供应商同一个理由 ——
      每次启动无条件 `putAlias` 会把用户改过的上下文长度、显示名、思考档位
      全部顶回种子值,而且悄无声息。

      ★★ **判重必须带 providerId。** 别名的主键是 `(provider_id, alias)`
      (`repo.ts` 的 `ON CONFLICT`),同一个别名**可以**挂在多家上 ——
      那正是故障切换的轴(`router.ts` 的候选链、`enabled-models.ts` 的 `isDefault`
      都建立在「同一个别名、多个供应商」上)。
      只按名字判重的话:用户已经配了 DeepSeek 官方那家、上面有 `deepseek-v4-pro`,
      内置上游就**永远拿不到**这条别名 —— 而 `defaultModel` 指着的那个名字确实存在,
      于是不报错,只是默认走了另一家,且内置上游从此不参与这条别名的故障切换。
    */
    const seeded = new Set(store.listAliases().map((a) => `${a.providerId}\u0000${a.alias}`))
    preset.suggestedModels.forEach((model, i) => {
      if (seeded.has(`${preset.id}\u0000${model}`)) {
        // Migrate the built-in Claude seed introduced before per-model
        // protocols existed. Preserve any explicit user override.
        const existingAlias = store.listAliases().find((a) => a.providerId === preset.id && a.alias === model)
        const wireProtocol = defaultProtocolForModel(model)
        if (existingAlias !== undefined && existingAlias.protocolOverride === undefined && wireProtocol !== undefined) {
          store.putAlias({ ...existingAlias, protocolOverride: wireProtocol })
        }
        return
      }
      store.putAlias(builtinAlias(preset.id, model, i))
    })
  }
}

/** 一次性标记,避免这段回填在每次进程启动时都把整张别名表扫一遍。 */
const OPENCODE_GO_PROTOCOL_MIGRATION_KEY = 'provider.opencode-go-model-protocol-v1'

/**
 * 老库里 OpenCode Go 的别名早于「协议按模型钉」这条规则存在,补种一次。
 *
 * ★★ **光改 `ipc/provider.ts` 的 `setAliases` 救不了已经配好的人。** 那条只在
 * 用户下次拉模型列表时才跑,而他此刻的症状是**发一句就 500**
 * (`muse-spark-1.3-contributor` 被发去 `/chat/completions`)——
 * 一句读不出「和协议有关」的报错。指望他自己想到去重拉一次列表是不现实的。
 *
 * ★ 只补 `protocolOverride` 为空的。用户在「协议」下拉里显式选过的一律不碰,
 * 且标记落库后永不重跑 —— 他之后把某个模型翻回别的协议,不会被下次启动悄悄改回去。
 * (同样的取舍见 `ipc/client-auth.ts` 的 `backfillAnthropicOverride`。)
 *
 * ★ 这里**不广播** `provider:changed`:本文件零 electron import(见文件头),
 * 而 `window/registry` 是 electron 的。不需要广播 —— `ipc/provider.ts` 的
 * `listProviders` / `listModels` 都以 `ensureSeeded()` 开头,渲染层读到的
 * 必然已经是补完之后的值。
 */
function backfillOpencodeGoProtocol(): void {
  if (store.getKv<boolean>(OPENCODE_GO_PROTOCOL_MIGRATION_KEY, false)) return
  store.setKv(OPENCODE_GO_PROTOCOL_MIGRATION_KEY, true)

  const providers = new Map(store.listProviders().map((p) => [p.id, p]))
  for (const alias of store.listAliases()) {
    if (alias.protocolOverride !== undefined) continue
    const provider = providers.get(alias.providerId)
    if (provider === undefined) continue
    const protocol = opencodeGoProtocolFor(provider, alias.upstreamModel)
    if (protocol === undefined) continue
    store.putAlias({ ...alias, protocolOverride: protocol })
  }
}

/**
 * 默认工作区 —— 和演示上游同一个理由:**全新安装点开就能用**。
 *
 * 没有它,首屏的外层 Tab 条是空的,侧边栏下半是空态,输入框没有 workspaceId
 * 可以发 —— 用户必须先经「打开文件夹」选一个目录才能看见这个应用长什么样。
 * 而参考实现里那个「默认工作区」正是这个位置(定时任务页的筛选器里
 * `全部工作区 / 默认工作区 / NextCoWork` 就是它和真实工作区并列)。
 *
 * 根目录落在统一的应用数据根 `.next-cowork/` 下,而不是 `process.cwd()`:
 * 打包后 cwd 在应用包内部,步骤 9 的 fs 工具就会以「围栏之内」的名义写进应用包里。
 *
 * 建目录失败不该拦住启动 —— 标 `unavailable` 就是 `Workspace` 上那个字段
 * 存在的理由(方案 §9:「工作区根会在运行期被删除或改名,加载时标记
 * unavailable 而不是崩溃」)。
 */
/**
 * 默认工作区的 id。**按配置作用域派生**,不是常量。
 *
 * ★★ 常量在这里是**错的**:`workspaces.id` 是主键,而一个作用域里
 * 叫 `ws-default` 的行已经归 `local` 了 —— 账户作用域再想建一个同 id 的,
 * `putWorkspace` 会(正确地)以 `workspaceOwned` 拒绝,于是新登录的账户
 * 连一个能发消息的工作区都没有,首屏是一片空白。
 *
 * ★ 派生值必须**稳定**:每次启动算出来不一样的话,每次启动都会新建一个
 * 「默认工作区」,而它们都是空的、长得一样。哈希是稳定且不可逆的,
 * 于是这个 id 里也不带账户原文。
 *
 * ★ `local` 保持原样 `'ws-default'` —— 库里已经有这一行(可能还带着会话),
 * 换 id 等于把它变成孤儿。
 */
function defaultWorkspaceId(): string {
  return defaultWorkspaceIdForScope(currentConfigScope())
}

function seedDefaultWorkspace(): void {
  const id = defaultWorkspaceId()
  if (store.getWorkspace(id) !== undefined) return
  // 用户自己开过工作区就不再塞默认的 —— 否则每次启动都多一个他没要的 Tab
  if (store.listWorkspaces().length > 0) return

  const rootPath = join(getHost().paths.userData(), 'workspaces', 'default')
  let unavailable = false
  try {
    mkdirSync(rootPath, { recursive: true })
  } catch (err) {
    getHost().logger.warn(`[runtime] 默认工作区目录建不出来,标为不可用:${String(err)}`)
    unavailable = true
  }

  const now = getHost().clock.now()
  const workspace = {
    name: '默认工作区',
    rootPath,
    ...(unavailable ? { unavailable: true } : {}),
    settings: structuredClone(DEFAULT_WORKSPACE_SETTINGS),
    createdAt: now,
    lastOpenedAt: now
  }
  try {
    store.putWorkspace({ id, ...workspace })
  } catch (err) {
    // 这个 id 已被另一个配置作用域占着(当前作用域看不见它,所以上面的查重没拦住)。
    // 默认工作区只是个便利,不能因此让整个应用起不来 —— 换一个随机 id 重试;
    // 写成功后 listWorkspaces 非空,之后的启动不会再走到这里。
    if (!(err instanceof ConfigProfileError) || err.code !== 'workspaceOwned') throw err
    getHost().logger.warn(`[runtime] 默认工作区 id ${id} 属于其他账户,改用新 id`)
    store.putWorkspace({ id: `${id}-${randomUUID().slice(0, 8)}`, ...workspace })
  }
}

/**
 * seed 的公开触发点。
 *
 * `getRouter()` 也会 seed,但首屏拉 provider / 模型列表时可能一个 run 都还没跑过 ——
 * 那时下拉框会是空的,而设置里的 defaultModel 已经指着内置上游了。
 * 两处指向同一个 `seed()`,所以「什么时候 seed 过了」只有一个答案。
 */
export function ensureSeeded(): void {
  seed()
}

/**
 * 账号池 —— **进程内单例,和路由器共命运。**
 *
 * ★★ 单例这件事是它有价值的前提(和 `router.rateLimitGate` 是同一条理由):
 * 主代理和它派出去的每一个子代理共用同一份「这个账号被限流到几点」,
 * 于是一条流吃到 429 之后,其余几条**在发请求之前**就会换到别的账号。
 * 每条流各持一份的话,四个子代理会把同一个账号的额度同时撞光。
 *
 * ★ 落库的那一半由 `store` 做,所以重启之后闸门还在(产品决策 D7);
 * 这个对象自己不缓存任何东西 —— 缓存会和界面上的「立即解除限流」打架。
 */
let accountPool: AccountPool | null = null

function getAccountPool(): AccountPool {
  accountPool ??= new AccountPool({
    /*
      ★ 每次现读,不做快照:设置页刚点完「立即解除限流」,下一条请求就该用上它。
      `auth` 不在这里补(它要解密)—— 账号可用性读的是行上那个反范式的
      `needsReauth`,见 `shared/domain/provider-account.ts`。
    */
    list: (providerId) => store.listProviderAccounts(providerId),
    setLimit: (accountId, limit) => store.setProviderAccountLimit(accountId, limit),
    setQuota: (accountId, quota) => store.setProviderAccountQuota(accountId, quota),
    now: () => getHost().clock.now(),
    rotationEnabled: () => store.getSettings().providerAccountRotation,
    // ★ 又一个注入回调(同 `onCredentialChanged` / `onUsageAttempt`):内核拿不到窗口,
    //   而设置页要立刻看到「限流中」那个徽章亮起来 —— 不推的话用户要关掉再打开设置页
    onChanged: (providerId) => accountsOnChange?.(providerId)
  })
  return accountPool
}

export function getRouter(): UpstreamRouter {
  seed()
  router ??= new UpstreamRouter(getHost(), providerConfig, {
    idleTimeoutMs: upstreamIdleTimeoutMs,
    onUsageAttempt: persistUsageAttempt,
    priceAttempt: priceAttemptForRouter,
    onCredentialChanged: (ref) => credentialOnChange?.(ref),
    /*
      ★ **只有正文这一个路由器接账号池。** 三个辅助请求(标题、子代理草稿、
      提交信息)故意不接:它们的失败不该把用户的主力账号关进闸门,
      而那正是「辅助请求用独立 UpstreamRouter」这条既有设计要挡的东西。
      不接时 `select()` 返回 null,它们走旧槽(当前账号的镜像)—— 照常能发。
    */
    accounts: getAccountPool()
  })
  return router
}

function getSessionTitles(): SessionTitleGenerator {
  if (sessionTitles !== null) return sessionTitles
  // Reuse protocol/configuration/usage handling with independent provider health:
  // a failed auxiliary request must not affect the Agent's provider selection.
  const generator = new SessionTitleGenerator({
    upstream: new UpstreamRouter(getHost(), providerConfig, {
      idleTimeoutMs: upstreamIdleTimeoutMs,
      onUsageAttempt: (record) => {
        if (sessionTitles === generator) persistUsageAttempt(record)
      },
      priceAttempt: priceAttemptForRouter
    }),
    getSession: store.getSession,
    putSession: store.putSession,
    onChange: (session) => sessionOnChange?.({ kind: 'metadata', sessionIds: [session.id], workspaceId: session.workspaceId, renamed: { sessionId: session.id, title: session.title } }),
    logger: getHost().logger
  })
  sessionTitles = generator
  return generator
}

export function shutdownSessionTitles(): void {
  sessionTitles?.clear()
  sessionTitles = null
}

/**
 * 「AI 生成子代理」用的那一次辅助请求。
 *
 * ★ 和 `getSessionTitles` 逐条同形,理由也同一条:**独立的 `UpstreamRouter`**,
 *   让一次失败的辅助请求不去影响正文的供应商健康度。用量回调里那句
 *   `agentDrafts === generator` 同理 —— 换过宿主之后,上一个生成器迟到的
 *   用量不该再记进账。
 */
export function getAgentDraftGenerator(): AgentDraftGenerator {
  if (agentDrafts !== null) return agentDrafts
  const generator = new AgentDraftGenerator({
    upstream: new UpstreamRouter(getHost(), providerConfig, {
      idleTimeoutMs: upstreamIdleTimeoutMs,
      onUsageAttempt: (record) => {
        if (agentDrafts === generator) persistUsageAttempt(record)
      },
      priceAttempt: priceAttemptForRouter
    }),
    logger: getHost().logger
  })
  agentDrafts = generator
  return generator
}

/**
 * 「AI 写提交信息」用的那一次辅助请求。
 *
 * ★ 和上面两个逐条同形。**又一个独立的 `UpstreamRouter`** 不是复制粘贴偷懒:
 *   Git 面板点一次按钮就发一次请求,失败率天然比正文高(diff 大、模型挑食),
 *   这些失败不该把某家供应商在正文侧标记成不健康。
 */
export function getCommitMessageGenerator(): CommitMessageGenerator {
  if (commitMessages !== null) return commitMessages
  const generator = new CommitMessageGenerator({
    upstream: new UpstreamRouter(getHost(), providerConfig, {
      idleTimeoutMs: upstreamIdleTimeoutMs,
      onUsageAttempt: (record) => {
        if (commitMessages === generator) persistUsageAttempt(record)
      },
      priceAttempt: priceAttemptForRouter
    }),
    logger: getHost().logger
  })
  commitMessages = generator
  return generator
}

/**
 * 输入框「回合结束后建议下一步」用的那一次辅助请求(`docs/composer-autocomplete.md`)。
 *
 * ★ 和上面几个逐条同形,又一个独立的 `UpstreamRouter`:建议是每轮都发的后台请求,
 *   它的失败(模型挑食、被限流)不该把某家供应商在正文侧标记成不健康。
 */
export function getPromptSuggestionGenerator(): PromptSuggestionGenerator {
  if (promptSuggestions !== null) return promptSuggestions
  const generator = new PromptSuggestionGenerator({
    upstream: new UpstreamRouter(getHost(), providerConfig, {
      idleTimeoutMs: upstreamIdleTimeoutMs,
      onUsageAttempt: (record) => {
        if (promptSuggestions === generator) persistUsageAttempt(record)
      },
      priceAttempt: priceAttemptForRouter
    }),
    logger: getHost().logger
  })
  promptSuggestions = generator
  return generator
}

/**
 * 输入框「打字中续写」。理由同上;它的缓存与限流状态挂在这个单例上,
 * 换作用域(换账号)时随单例一起丢掉 —— 上一个账号的缓存不该命中到下一个账号。
 */
export function getInlineCompletionService(): InlineCompletionService {
  if (inlineCompletions !== null) return inlineCompletions
  const service = new InlineCompletionService({
    upstream: new UpstreamRouter(getHost(), providerConfig, {
      idleTimeoutMs: upstreamIdleTimeoutMs,
      onUsageAttempt: (record) => {
        if (inlineCompletions === service) persistUsageAttempt(record)
      },
      priceAttempt: priceAttemptForRouter
    }),
    logger: getHost().logger
  })
  inlineCompletions = service
  return service
}

/**
 * widget 里 `host.complete()` 用的那条上游。
 *
 * ★ 又一个独立的路由器,理由同上面几条:widget 是模型写的,一个写坏的循环可能连发
 *   几十次请求,它们的失败不该把正文侧的供应商标成不健康;用量照常落账。
 */
export function getWidgetUpstream(): UpstreamRouter {
  if (widgetUpstream !== null) return widgetUpstream
  const router = new UpstreamRouter(getHost(), providerConfig, {
    idleTimeoutMs: upstreamIdleTimeoutMs,
    onUsageAttempt: (record) => {
      if (widgetUpstream === router) persistUsageAttempt(record)
    },
    priceAttempt: priceAttemptForRouter
  })
  widgetUpstream = router
  return router
}

/**
 * Freeze pricing at the attempt boundary. Later seed updates must never change
 * historical spend, and an unavailable rate remains NULL rather than silently
 * turning into a plausible-looking zero.
 */
export function resolveUsagePricingModelId(upstreamModel: string): string {
  const catalogModel = findBuiltinModel(upstreamModel)
  return catalogModel?.pricingModelId ?? catalogModel?.id ?? upstreamModel
}

/**
 * 这条用量该按哪张价目行计价 —— 查不到就是 null。
 *
 * ★ 返回 null 让调用方记 `costMicros: null`,而不是一个看着挺像样的 0 ——
 * `schema.ts` 要求 NULL 与 0 分得开。那张「用过但查不到定价」的待补表还没实现;
 * 将来实现它的时候,`usage_records` 有 `provider_id` 列,够用。
 *
 * ★ 和 `resolveUsagePricingModelId` 同一个理由抽成导出的纯函数:判断本身要可测,
 * 而 `persistUsageAttempt` 挂在内核回调上,测不到。
 */
export function resolveUsagePricing(
  providerId: string,
  pricingModelId: string,
  at: number
): ModelPricing | null {
  return findPricing(getEffectivePricing(), providerId, pricingModelId, at)
}

/**
 * 这一次请求多少钱 —— 查表 + 计价那三步的唯一落点。
 *
 * ★★ **落盘的钱和推给界面的钱必须出自这一个函数。** 它有两个调用方:
 * `persistUsageAttempt`(写 `usage_records`,设置页的用量统计读它)和 router 的
 * `priceAttempt` 钩子(随 `message_end` 推给聊天页的「任务用量」)。两边各写一遍
 * 的话,同一轮对话的花费会在应用的两个页面上显示成两个数 —— 而且是那种
 * 差一点点、谁也说不清哪个对的两个数。
 */
export function priceUsageAttempt(
  providerId: string,
  upstreamModel: string,
  usage: TokenUsage,
  at: number
): PriceResult | null {
  const pricing = resolveUsagePricing(providerId, resolveUsagePricingModelId(upstreamModel), at)
  return pricing === null ? null : priceOf(pricing, usage, at)
}

/** `priceUsageAttempt` 的 router 钩子形态 —— 只保留界面要的那两个字段。 */
function priceAttemptForRouter(
  providerId: string,
  upstreamModel: string,
  usage: TokenUsage,
  at: number
): RunCost | null {
  const priced = priceUsageAttempt(providerId, upstreamModel, usage, at)
  return priced === null ? null : { micros: priced.micros, currency: priced.currency }
}

function persistUsageAttempt(record: UnpricedUsageAttempt): void {
  const priced = priceUsageAttempt(
    record.providerId,
    record.upstreamModel,
    {
      inputTokens: record.inputTokens,
      outputTokens: record.outputTokens,
      cacheReadInputTokens: record.cacheReadTokens,
      cacheCreationInputTokens: record.cacheWriteTokens,
      cacheCreation1hInputTokens: record.cacheWrite1hTokens,
      ...(record.thinkingTokens !== null && !record.thinkingTokensEstimated
        ? { reasoningTokens: record.thinkingTokens }
        : {})
    },
    record.at
  )

  store.recordUsageAttempt({
    ...record,
    costMicros: priced?.micros ?? null,
    currency: priced?.currency ?? null,
    pricingTier: priced?.tier ?? null,
    pricingWindow: priced?.window ?? null
  })
}

export function getTools(): ToolRegistry {
  if (tools === null) {
    tools = new ToolRegistry()
    // 步骤 9 的 fs/bash、步骤 10 的 MCP、步骤 11 的 task 都从这里进来,
    // 且都经**同一个** register —— 那是消毒与命名的唯一收口点(方案 §4.4)。
    // 插件贡献的工具也走这里:它们经 `registerToolProvider` 进 `builtinTools()`,
    // 于是消毒、命名、去重一条都不会被绕过。
    // ★ 但单例只在这里读一次 provider —— 之后才激活/禁用的插件靠每轮 run 开头的
    //   `syncPluginTools` 对齐,不要以为这一行能看到插件的后续变化。
    for (const reg of builtinTools()) tools.register(reg)
  }
  return tools
}

/**
 * 插件工具的接线口。
 *
 * ★ 存的是**函数**而不是那一刻的工具表:插件会被启用、禁用、重载,而
 * `getTools()` 是惰性单例、`snapshotRunTools()` 每次 run 都重建一次。
 * 存结果意味着某一处会滞留一份过期快照,而过期的症状是「禁用了插件,
 * 模型还在调它的工具」——一次静默的越权。
 */
export function installPluginToolProvider(provide: () => ToolRegistration[]): () => void {
  livePluginTools = provide
  const unregister = registerToolProvider('plugin', provide)
  return () => {
    unregister()
    if (livePluginTools === provide) livePluginTools = () => []
  }
}

/**
 * 插件工具的**现报**来源 —— 每轮 run 开头交给 `syncPluginTools` 对齐单例。
 * ★ 光靠 `registerToolProvider` 不够:它只在 `getTools()` 首次建单例时被读一次,
 *   之后才激活的 `onTool:` 插件的工具永远进不了 Agent 工具表(见 plugin-tools.ts)。
 */
let livePluginTools: () => ToolRegistration[] = () => []

/** 需求：注册表快照前先让 onTool 插件注册工具，不让声明过的工具永远不可见。 */
let preparePluginTools: () => Promise<void> = async () => {}
export function installPluginToolPreparation(prepare: () => Promise<void>): void {
  preparePluginTools = prepare
}

/**
 * 插件的工具拦截器。审批策略(`kernel/approval.ts`)在 `decideAfterHooks` 那一步问它。
 *
 * ★ 没装(纯内核测试、插件系统还没起来)时是一个**弃权**的默认值,
 * 不是一个抛错的桩:一条工具调用不该因为插件系统没初始化就走不下去。
 *
 * ★ 类型是 `ApprovalPluginInterceptor` 的别名 —— 判定逻辑搬去 `kernel/approval.ts`
 * 之后,接口的**归属**也跟过去,这里只保留 runtime 侧那个安装口(ipc 依赖 runtime)。
 */
export type PluginInterceptor = ApprovalPluginInterceptor

let pluginInterceptor: PluginInterceptor | null = null

export function installPluginInterceptor(fn: PluginInterceptor | null): void {
  pluginInterceptor = fn
}

/**
 * 审批策略的注入口。分配一次、闭包读模块级单例(和上面那排 sink 同一个套路):
 * 于是 `installPluginInterceptor` 之后跑起来的 run 会用上新的拦截器,不必重装依赖。
 */
const approvalDeps: ApprovalDeps = {
  upstream: () => getRouter(),
  runHooks: (context) => runHookEvent(context),
  interactions,
  modeFor: (workspaceId, mode) => modeRegistry(workspaceId).resolve(mode),
  reviewer: () => {
    const { permissionReviewerModel: model, permissionReviewerModelProviderId: modelProviderId } = store.getSettings()
    return { model, ...(modelProviderId === undefined ? {} : { modelProviderId }) }
  },
  host: () => getHost(),
  pluginInterceptor: () => pluginInterceptor
}

/**
 * 插件贡献的本轮上下文。
 *
 * ★ 走的是**钩子 `additionalContext` 的同一条路**(拼进 `projectInstructions`),
 * 于是它经 `context-assembler` 的 `decorate` 只进**发出去的那份消息流**,
 * 转录一个字不动 —— 这正是注入上下文该有的语义:不出现在用户的聊天气泡里,
 * 也不会被重放到旧会话。
 *
 * ★ **不是系统提示词。** 插件永远拿不到往系统提示词里塞东西的杠杆:
 * 那会给它影响所有 Agent 行为的能力,而多插件叠加之后不可预测
 * (`context-assembler.ts` 那几段是精心排过序的)。
 */
export type PluginContextProvider = (input: { prompt: string }) => Promise<string>

let pluginContextProvider: PluginContextProvider | null = null

export function installPluginContextProvider(fn: PluginContextProvider | null): void {
  pluginContextProvider = fn
}

/**
 * MCP 运行时。和 `getTools()` 同款惰性单例。
 *
 * ★ 它**必须**拿到 `getTools()` 返回的那一个注册表,而不是自己新建一个 ——
 * MCP 工具和内置工具要一起出现在同一次 `snapshot()` 里,否则模型看得见
 * `Read` 却看不见刚连上的服务器带来的工具。
 */
export function getMcp(): McpManager {
  if (mcp === null) {
    const h = getHost()
    mcp = new McpManager({
      tools: getTools(),
      secrets: h.secrets,
      logger: h.logger,
      onChange: (id) => mcpOnChange?.(id)
    })
  }
  return mcp
}

const workspaceMcps = new Map<string, { key: string; manager: McpManager; tools: ToolRegistry; started: Set<string>; release(): void }>()

function workspaceMcp(workspaceId: string, environment = getWorkspaceEnvironment(workspaceId)) {
  environment.assertReady()
  const existing = workspaceMcps.get(workspaceId)
  if (existing?.key === environment.key) return existing
  if (existing) { void existing.manager.shutdown(); existing.release() }
  const lease = getEnvironments().acquire(workspaceId)
  const scopedTools = new ToolRegistry()
  const app = getHost()
  const manager = new McpManager({ tools: scopedTools, secrets: app.secrets, logger: app.logger,
    assertReady: environment.assertReady,
    transportFactory: (config, values) => environmentTransport(environment, config, values),
    onChange: (id) => mcpOnChange?.(id) })
  const scope = { key: environment.key, manager, tools: scopedTools, started: new Set<string>(), release: lease.release }
  workspaceMcps.set(workspaceId, scope)
  return scope
}

export function listMcpStatuses(): McpServerStatus[] {
  return store.listMcpServers().map((config) => {
    if (config.workspaceId === undefined) return getMcp().statusOf(config)
    const scope = workspaceMcps.get(config.workspaceId)
    try {
      if (scope?.key === getWorkspaceEnvironment(config.workspaceId).key) return scope.manager.statusOf(config)
    } catch { /* Disconnected workspaces have no usable MCP tools. */ }
    return { config, state: 'disconnected', tools: [], toolCount: 0 }
  })
}

export async function connectConfiguredMcp(config: McpServerConfig): Promise<McpServerStatus> {
  if (config.workspaceId === undefined) return getMcp().connect(config)
  try {
    const scope = workspaceMcp(config.workspaceId)
    scope.started.add(config.id)
    return await scope.manager.connect(config)
  } catch (error) {
    return { config, state: 'error', tools: [], toolCount: 0, error: error instanceof Error ? error.message : String(error) }
  }
}

export async function disconnectConfiguredMcp(id: string): Promise<void> {
  await Promise.all([getMcp().disconnect(id), ...[...workspaceMcps.values()].map((scope) => scope.manager.disconnect(id))])
}

async function prepareWorkspaceMcp(workspaceId: string, environment: WorkspaceEnvironment): Promise<ToolRegistry | undefined> {
  const configs = store.listMcpServers().filter((config) => config.workspaceId === workspaceId && config.enabled)
  if (configs.length === 0) return undefined
  const scope = workspaceMcp(workspaceId, environment)
  await Promise.all(configs.filter((config) => !scope.started.has(config.id)).map((config) => {
    scope.started.add(config.id)
    return scope.manager.connect(config)
  }))
  environment.assertReady()
  return scope.tools
}

/**
 * 这个工作区**已经连上的**那些 MCP 工具,没连过就是 undefined。
 *
 * ★ 和 `prepareWorkspaceMcp` 的区别就是一个字:那个会**去连**,这个只**看**。
 * 上下文预览(`ipc/context.ts`)要的是后者 —— 用户点开一个菜单不该顺带
 * 拉起几个子进程,而「还没连上所以这一档是 0」是个诚实的读数。
 */
export function connectedWorkspaceMcpTools(workspaceId: string): ToolRegistry | undefined {
  return workspaceMcps.get(workspaceId)?.tools
}

/** 由 `ipc/mcp.ts` 在 `registerIpc()` 里装上 —— 理由见 `mcpOnChange` 的注释 */
export function setMcpChangeListener(fn: (id: string) => void): void {
  mcpOnChange = fn
}

/** 由 `ipc/index.ts` 在注册阶段安装；纯 Node 测试中保持 no-op。 */
export function setSessionChangeListener(fn: NonNullable<typeof sessionOnChange>): void {
  sessionOnChange = fn
}

/** 由 `ipc/index.ts` 在注册阶段安装；纯 Node 测试中保持 no-op。 */
export function setReviewChangeListener(fn: NonNullable<typeof reviewOnChange>): void {
  reviewOnChange = fn
}

/** 由 `ipc/index.ts` 在注册阶段安装；纯 Node 测试中保持 no-op。理由见 `skillWrittenOnChange`。 */
export function setSkillWrittenListener(fn: NonNullable<typeof skillWrittenOnChange>): void {
  skillWrittenOnChange = fn
}

/** 同上。刷新 token 改写凭证后，把新的登录态推给可能正开着设置页的窗口。 */
export function setCredentialChangeListener(fn: NonNullable<typeof credentialOnChange>): void {
  credentialOnChange = fn
}

/**
 * 退出时收摊。**只在真的建过 manager 时动手** —— 用 `getMcp()` 会在一个
 * 从没连过 MCP 的进程里凭空造一个出来,只为了立刻关掉它。
 */
export async function shutdownMcp(): Promise<void> {
  await mcp?.shutdown()
  await Promise.all([...workspaceMcps.values()].map(async (scope) => { await scope.manager.shutdown(); scope.release() }))
  workspaceMcps.clear()
}

/** app ready 时调一次。seed 提前跑,好让首屏的 bootstrap 已经带上默认模型。 */
export function initRuntime(h: KernelHost): void {
  installHost(h)
  seed()
  installVideoManager()
  /*
    ★ **后台拉起,不 await。**每台服务器的握手有 30 秒预算,而 stdio 那一支
    还要先 `npx` 下一个包 —— 串起来足够让首屏白屏十几秒。
    连不上的那些会各自落到 `state:'error'` 并经 `mcp:changed` 播出去,
    设置页照实显示;对话那一侧只是少几个工具,不是启动不了。
  */
  getMcp().connectEnabledInBackground(store.listMcpServers())
}

/**
 * 装配视频后台 manager(幂等)。
 *
 * ★ 依赖**全部延迟取**:provider / 别名 / 凭据 / 设置都是函数,manager 每次用
 * 现读 —— 于是改设置、切账户立刻对它生效(与 `providerConfig` 那条同一条规矩)。
 *
 * ★ 必须在这里装配而不是在 `initRuntime` 里一次性接死:manager 是**进程级单例**
 * (任务要跨 run、跨会话存活),而 `installVideoRuntime` 会在切换账户时被重装 ——
 * 那时旧的那个要先 `shutdown`(它停的是本机 worker,**不取消云端任务**)。
 */
function installVideoManager(): void {
  installVideoRuntime({
    store: {
      get: (id) => getVideoJobRow(id),
      put: (job) => putVideoJob(job, JSON.stringify(job)),
      remove: (id) => removeVideoJob(id),
      listRecoverable: (profile) => listRecoverableVideoJobs(profile),
      listForSession: (sessionId) => listVideoJobsForSession(sessionId)
    },
    providers: () => store.listProviders(),
    aliases: () => listResolvedModels(),
    preferredModel: () => {
      const settings = store.getSettings()
      return settings.videoModel === ''
        ? null
        : { alias: settings.videoModel, providerId: settings.videoModelProviderId }
    },
    // ★ 「对话视频生成」开关:**只管新建**(见 settings.ts 上那段)。
    enabled: () => store.getSettings().videoGenerationEnabled === true,
    credential: async (ref) => parseCredential(await getHost().secrets.get(ref)),
    fetch: (input, init) => getHost().fetch(input, init),
    now: () => getHost().clock.now(),
    schedule: (ms, fn) => {
      const timer = setTimeout(fn, ms)
      return { cancel: () => { clearTimeout(timer) } }
    },
    retrieve: async (job, asset) =>
      sessionVideoStoreFor({ sessionId: job.sessionId, host: getHost }).download(
        { url: asset.url, ...(asset.headers === undefined ? {} : { headers: asset.headers }), ...(asset.mime === undefined ? {} : { mime: asset.mime }) },
        new AbortController().signal
      ),
    onChange: (view) => videoOnChange?.(view)
  })
  // ★ 启动恢复:只查询/下载,**绝不重复提交**(见 manager.resume)。
  getVideoManager()?.resume(currentConfigScope())
}

/**
 * 配置作用域变了:把**进程内**那份配置全部丢掉,再按新的作用域重新种。
 *
 * ★★ 为什么不能只靠「下一次读的时候会现算」:`ipc/context.ts` 的上下文预览
 * 直接读 `skillRegistry(id).list()` / `agentRegistry(id).list()`,那是**上一次 run
 * 开头**扫出来的一份快照。不在这里清掉,切完账户打开上下文预览,列的是
 * 上一个账户装的那些 Skill —— 而 `refreshSkills` / `refreshAgents` 只在
 * **下一次 run 开始**时才跑。
 *
 * ★ 顺序是先拆后种,不是先种后拆:反过来种下去的东西会被紧跟着的
 * `shutdown()` 一起收掉(比如 MCP 刚建好的连接)。
 *
 * ★ 本函数必须 `await` —— `mcp?.shutdown()` 要等子进程真的退出,
 *   `environments.shutdown()` 要等远端连接断开。不 await 的版本会在
 *   它们还在收尾的时候就让调用方去切库,而那些连接里的回调会读到新作用域。
 */
export async function refreshRuntimeForConfigScope(): Promise<void> {
  /*
    终端与远端连接由 `account-switch.ts` 在调用本函数之前先收掉 ——
    那两件事要排在「连 MCP 一起关」之前,理由和这里是同一个:
    它们都守着上一账户的凭据。
  */
  for (const scope of workspaceMcps.values()) { void scope.manager.shutdown(); scope.release() }
  workspaceMcps.clear()
  const manager = mcp
  mcp = null
  await manager?.shutdown()
  // 会话标题生成器自带一套上游健康状态 —— 那是**上一账户的**(哪家 401 过、
  // 哪家限流过),留着会让新账户的第一个会话绕开一家其实没问题的供应商。
  shutdownSessionTitles()

  /*
    ★ 视频 manager 也要重装:它攥着**上一账户的凭据读法**,而任务表按
    config profile 分开 —— 不重装的话,新账户的 manager 会拿旧账户的
    provider 列表去续查旧任务(见 `manager.resume` 里那条凭据指纹判断)。
    `installVideoRuntime` 自己会先 shutdown 掉旧的那个(停本机 worker)。
  */
  installVideoManager()
  // 惰性单例:下一次 `getTools()` / `getRouter()` / `getEnvironments()` 会按新作用域重建。
  tools = null
  router = null
  agentDrafts = null
  commitMessages = null
  promptSuggestions = null
  inlineCompletions = null
  widgetUpstream = null

  /*
    ★ 三张注册表**主动整体丢掉**,不是留着等下一次 run 覆盖:
    内建的那些是代码,新桶出厂自带;用户自己的必须走 ——
    它们在另一个作用域的文件根里,留着就是跨账户串。

    ★ 丢的是**所有工作区的桶**,不是某一个:换账户之后每个工作区的文件根
    都换了地方(分桶见 `kernel/registry-buckets.ts`)。模式那一张以前漏在
    这里没清,分桶之后一并补上。
  */
  resetSkillRegistries()
  resetAgentRegistries()
  resetModeRegistries()
  getTools().register(taskTool())

  // 空账户要有默认供应商与默认工作区,否则首屏是一个空的应用。
  seeded = false
  seed()
  /*
    ★ 账号表的迁移必须跟着作用域走:每个账户作用域有自己的 `credentials` 行
    (`physicalCredentialRef` 加前缀),所以 A 账户迁过不代表 B 账户迁过。
    漏在这里的表现是「切到另一个账户后,原本已登录的 Codex 变成未登录」——
    而它的凭证其实好好地躺在旧槽里,只是没人把它迁成账号 #1。
  */
  await seedProviderAccounts()
}

/**
 * 把「升级之前已经登录的那一个账号」迁成账号 #1(schema 第 24 条)。
 *
 * ★ 异步,所以不在 `seed()` 里 —— 它要读一次密文(解密是异步的)。
 * ★ 失败不抛:迁移不成只是「这家暂时还看不到账号列表」,下次启动会再试一遍;
 *   而在启动路径上抛出去会停在一个半初始化的状态上(见 `index.ts` 那条 `.catch`)。
 */
export async function seedProviderAccounts(): Promise<void> {
  try {
    ensureSeeded()
    const host = getHost()
    const created = await ensureProviderAccountsSeeded(
      store.listProviders(),
      {
        get: (ref) => host.secrets.get(ref),
        set: (ref, value) => host.secrets.set(ref, value)
      },
      host.clock.now()
    )
    if (created > 0) host.logger.info(`[provider-account] 已把 ${created} 个已登录账号迁入账号表`)
  } catch (err) {
    getHost().logger.warn('[provider-account] 账号迁移失败,下次启动重试', err)
  }
}

/**
 * 会话绑定的工作区根。**查不到就给空串,不给回落目录。**
 *
 * 这里以前回落到 `paths.temp()`。★ 那是个会安静地骗人的默认值:模型以为自己
 * 在用户的项目里干活,于是把文件写进一个谁也不会去看的目录,然后报告「已完成」。
 * 打包后回落到 `process.cwd()` 更糟 —— 那是应用包内部,文件工具会以「围栏之内」
 * 的名义往应用包里写。
 *
 * 空串意味着 `resolvePath`(`tool/builtin/paths.ts`)对每一次路径调用都直接拒绝,
 * 并告诉模型「先让用户打开一个工作区」。宁可一步都走不动,也不要走错地方。
 *
 * 批次 5 会更进一步:没有工作区时**根本不下发**碰路径的工具(经 `SessionDeps.allowedTools`),
 * 那样模型连试都不会试。这一步先把安全相关的那一半落地。
 */
function workspaceRootFor(workspaceId: string): string {
  return store.getWorkspace(workspaceId)?.rootPath ?? ''
}

/**
 * 重扫两层 Skill 目录,结果整体换进注册表。
 *
 * ★ **每次发送前扫一遍**,不是启动时扫一次。用户在 `.next-cowork/skills/` 里
 * 放一条新的 Skill 之后,期待的是「下一次提问它就知道了」——「重启应用才生效」
 * 这件事没有任何地方会提示他。代价是两次 readDir,和一次上游往返比可以忽略。
 *
 * ★ 这个函数住在 runtime 而不是 `ipc/skills.ts`:它只需要 `getHost().paths`
 * 和 `store`,两样这里都有,而反过来会让内核的装配依赖 IPC 层。
 */
export async function refreshSkills(workspaceId: string, environment?: WorkspaceEnvironment): Promise<readonly Skill[]> {
  const h = getHost()
  const root = workspaceRootFor(workspaceId)
  environment ??= store.getWorkspace(workspaceId) ? getWorkspaceEnvironment(workspaceId) : undefined
  const remote = environment?.remote ? environment : undefined
  const result = await scanSkills({
    fs: h.fs,
    projectFs: remote?.fs, projectPath: remote?.path,
    globalRoot: join(h.paths.userData(), SKILLS_DIR),
    /*
      ★ 每次都重新问一次,不缓存。这份清单随插件的启用状态变 —— 缓存的症状是
      用户刚禁用了插件,而模型下一轮仍然看得见它带来的 skill,
      且插件页上明明写着它是关的。取一次只是遍历一张十几项的表。
    */
    pluginRoots: currentPluginSkillRoots(),
    projectRoot: root === '' ? '' : remote ? await remote.path.resolveWithin(remote.rootPath, `${PROJECT_SKILLS_PREFIX}/${SKILLS_DIR}`) : join(root, PROJECT_SKILLS_PREFIX, SKILLS_DIR)
  })
  // 诊断只记日志,不阻断:一条坏掉的 SKILL.md 不该让别的都用不了
  for (const d of result.diagnostics) h.logger.warn(`[skill] ${d.path}: ${d.message}`)
  skillRegistry(workspaceId).replaceAll(result)
  return result.skills
}

/**
 * 提炼会话的头块:取源会话转录与本工作区的项目 Skill 清单,交给纯函数拼字。
 *
 * ★ 源会话查不到(被删了)时**不让 run 失败**:块里会写「源会话已删除」,
 * 用户仍能在提炼会话里继续追问、修改已写好的 Skill。
 * ★ 窗口取本 run 所用模型的协议窗口 —— 摘要预算按它算,小窗口模型不能被摘要吃掉一半。
 */
function loadSkillExtraction(sourceSessionId: string, skills: readonly Skill[], req: RunRequest): string {
  const source = store.getSession(sourceSessionId)
  return buildSkillExtractionBlock({
    sourceSessionId,
    source: source === undefined ? undefined : { title: source.title, messages: store.getHistory(sourceSessionId) },
    existingSkills: skills
      .filter((skill) => skill.scope === 'project')
      .map((skill) => ({ name: skill.name, description: skill.description })),
    contextWindow: getRouter().resolveModel(req.model, req.modelProviderId)?.contextWindow
  })
}

/**
 * 读这个工作区的 `AGENTS.md`(全局一份 + 项目一份,拼接后消毒)。
 *
 * ★ 和 `refreshSkills` / `refreshAgents` 不同,这里**不建注册表单例**:
 * 没有任何工具会在运行期查它,它只在组装时用一次。一个返回字符串的函数就够,
 * 而且顺带躲掉了 `skillRegistry()` 那个没有 test reset 钩子的问题。
 */
export async function loadInstructions(workspaceId: string, environment?: WorkspaceEnvironment): Promise<string> {
  const h = getHost()
  const root = workspaceRootFor(workspaceId)
  environment ??= store.getWorkspace(workspaceId) ? getWorkspaceEnvironment(workspaceId) : undefined
  const result = await scanInstructions({
    fs: h.fs,
    ...(environment?.remote ? { projectFs: environment.fs, projectPath: environment.path } : {}),
    globalRoot: h.paths.userData(),
    projectRoot: root
  })
  // 诊断只记日志,不阻断 —— 一份读不了的 AGENTS.md 不该让这次提问跑不起来
  for (const d of result.diagnostics) h.logger.warn(`[instructions] ${d.path}: ${d.message}`)

  /*
    ★★ 导入来的说明拼在**最后**,而且读的是受管副本,不是任何原生文件。

    三条边界一条都不能少:
    - **不覆盖原生 `AGENTS.md`** —— 那是用户自己写的,导入不该动它;
    - **不修改源 `CLAUDE.md`** —— 只读承诺,受管副本是导入时另存的第三份;
    - **原生内容优先占用总长度预算** —— 拼在后面意味着超出 `INSTRUCTIONS_MAX`
      时先被 `clampWithEllipsis` 从尾部截掉的是导入的那部分。
  */
  const managed = await loadManagedInstructions(workspaceId)
  if (managed === '') return result.text
  return clampWithEllipsis(
    result.text === '' ? managed : `${result.text}${MANAGED_SEPARATOR}${managed}`,
    INSTRUCTIONS_MAX
  )
}

const MANAGED_SEPARATOR = '\n\n--- (imported instructions — the native rules above win) ---\n\n'

/**
 * 读全局与本工作区的受管说明副本。
 *
 * ★ 每一份都过 `sanitizeInstructions` —— 它来自另一个工具的目录,和 clone 来的
 * 仓库一样是**不可信输入**(见 `kernel/instructions.ts` 文件头)。「已经导入过
 * 一次」并不让它变可信:那份源文件在导入之后还会被改。
 */
async function loadManagedInstructions(workspaceId: string): Promise<string> {
  const chunks: string[] = []
  for (const source of store.listImportSources()) {
    for (const scopeKey of ['', workspaceId]) {
      if (scopeKey === '' && workspaceId === '') continue
      const mapping = store.getImportMapping(
        source.sourceId,
        scopeKey,
        'instructions',
        scopeKey === '' ? 'global' : scopeKey
      )
      // suppressed = 用户删过这一项,不要在下一轮把它拼回上下文里。
      if (mapping === undefined || mapping.syncState === 'suppressed') continue
      const text = await readTextBounded(managedInstructionsPath(source.sourceId, scopeKey), 64 * 1024)
      if (text === null) continue
      const clean = sanitizeInstructions(text)
      if (clean !== '') chunks.push(clean)
    }
  }
  return chunks.join('\n\n')
}

/**
 * 这一轮真正要下发目录的那几条。
 *
 * 两道筛子,分别是两个不同的问题:`resolve()` 回答「这个工作区装了哪些」
 * (空清单 = 全都要,理由在 `SkillRegistry.resolve` 上),
 * `globalEnabled` 回答「用户有没有在设置里把它整个关掉」。
 */
function activeSkills(
  req: RunRequest,
  snapshot?: readonly Skill[],
  selection?: Pick<RunRequest, 'skillIds' | 'skillSelectionMode'>
): readonly Skill[] {
  const disabled = new Set(store.getDisabledSkillIds())
  const registry = skillRegistry(req.workspaceId)
  const source = snapshot ?? registry.list()
  const selected = selection ?? req
  return (snapshot === undefined
    ? registry.resolve(selected.skillIds, selected.skillSelectionMode ?? 'all')
    : resolveSkillsSnapshot(source, { ...req, skillIds: selected.skillIds, skillSelectionMode: selected.skillSelectionMode }))
    .filter((s) => !disabled.has(s.id) && !s.unavailableReason)
}

function resolveSkillsSnapshot(skills: readonly Skill[], req: RunRequest): readonly Skill[] {
  if (req.skillIds.length === 0) return req.skillSelectionMode === 'explicit' ? [] : skills
  const ids = new Set(req.skillIds)
  return skills.filter((s) => ids.has(s.id))
}

/**
 * 重扫两层模式目录,结果整体换进**这个工作区**那一份注册表。
 *
 * ★ 每次发送前扫一遍而不是启动时扫一次,理由同 `refreshSkills`。
 *
 * (这段注释原本写的是 `Task` 工具的重注册 —— 那件事属于下面的 `refreshAgents`,
 * 而且分桶之后它已经不在这条路上了,原委见那个函数体里的说明。)
 */
export async function refreshModes(workspaceId: string, environment?: WorkspaceEnvironment): Promise<readonly ModeDefinition[]> {
  const h = getHost()
  const root = workspaceRootFor(workspaceId)
  environment ??= store.getWorkspace(workspaceId) ? getWorkspaceEnvironment(workspaceId) : undefined
  const remote = environment?.remote ? environment : undefined
  const result = await scanModes({
    fs: h.fs,
    projectFs: remote?.fs,
    projectPath: remote?.path,
    globalRoot: join(h.paths.userData(), MODES_DIR),
    projectRoot: root === ''
      ? ''
      : remote
        ? await remote.path.resolveWithin(remote.rootPath, `${PROJECT_MODES_PREFIX}/${MODES_DIR}`)
        : join(root, PROJECT_MODES_PREFIX, MODES_DIR),
    availableTools: new Set(getTools().info().map((tool) => tool.internalId))
  })
  for (const diagnostic of result.diagnostics) {
    h.logger.warn(`[mode] ${diagnostic.path}: ${diagnostic.message}`)
  }
  modeRegistry(workspaceId).replaceAll(result)
  return result.modes
}

export async function refreshAgents(workspaceId: string, environment?: WorkspaceEnvironment): Promise<readonly AgentDefinition[]> {
  const h = getHost()
  const root = workspaceRootFor(workspaceId)
  environment ??= store.getWorkspace(workspaceId) ? getWorkspaceEnvironment(workspaceId) : undefined
  const remote = environment?.remote ? environment : undefined
  const result = await scanAgents({
    fs: h.fs,
    projectFs: remote?.fs, projectPath: remote?.path,
    globalRoot: join(h.paths.userData(), AGENTS_DIR),
    projectRoot: root === '' ? '' : remote ? await remote.path.resolveWithin(remote.rootPath, `${PROJECT_AGENTS_PREFIX}/${AGENTS_DIR}`) : join(root, PROJECT_AGENTS_PREFIX, AGENTS_DIR)
  })
  for (const d of result.diagnostics) h.logger.warn(`[agent] ${d.path}: ${d.message}`)
  /*
    ★ 用户在扩展面板里关掉的那些，在这里滤掉 —— 不滤的话「禁用」只改了界面上
    一个开关的样子，模型照样能通过 `Task` 派遣它。

    过滤放在这一层而不是 `scanAgents` 里：内核不认识 kv，而「磁盘上有这个子代理吗」
    和「我想不想用它」是两个问题，扫描器只回答前一个（`ipc/agents.ts` 要列出被
    关掉的那些，靠的就是扫描器不过滤）。
  */
  const disabled = new Set(store.getDisabledAgentNames())
  const enabled = { ...result, agents: result.agents.filter((a) => !disabled.has(a.name)) }
  agentRegistry(workspaceId).replaceAll(enabled)
  /*
    ★ **不再**在这里 `getTools().register(taskTool())`。

    那一行原本是必须的:`Task` 的 description 里逐字带着可用子代理的清单,
    而 description 在 `register()` 的那一刻定死。但它往的是**全局**工具注册表 ——
    也就是说 A 工作区扫出来的子代理会写进一张 B 也在读的表,而这正是分桶要修的事。

    分桶之后这份清单走两条各自带工作区身份的路:每次 run 的 `snapshotRunTools`
    (拿的是本次 run 的 `runAgents`)和上下文预览的 `taskTool(agentRegistry(id).list())`。
    全局表里那一份保持「只有内建」——它只被工具清单一类的只读接口用到。
  */
  return enabled.agents
}

function intersectToolLists(
  first: readonly string[] | undefined,
  second: readonly string[] | undefined
): readonly string[] | undefined {
  if (first === undefined) return second
  if (second === undefined) return first
  const allowed = new Set(second)
  return first.filter((tool) => allowed.has(tool))
}

interface RunResources {
  fileReferenceSource: FileReferenceSource
  environment: WorkspaceEnvironment
  agents: readonly AgentDefinition[]
  tools: ToolRegistry
}

function snapshotRunTools(environment: WorkspaceEnvironment, agents: readonly AgentDefinition[], scoped?: ToolRegistry): ToolRegistry {
  const registry = new ToolRegistry()
  for (const tool of getTools().snapshot()) {
    if (environment.remote && (tool.source.kind === 'mcp' || tool.internalId.startsWith('browser_'))) continue
    registry.register(tool.internalId === 'Bash' && environment.remote ? {
      ...tool,
      description: `Runs a non-interactive command on the SSH server in the workspace directory using ${environment.platform.shell}. `
        + 'Each call starts a fresh shell, stdin is closed, and output and execution time are bounded. '
        + (environment.platform.os === 'win32' ? 'Use PowerShell syntax and Windows paths, not Bash or cmd.exe syntax. ' : 'Use the remote shell syntax and paths. ')
        + 'Commands do not run on the client. A lost connection may leave the outcome unknown; do not automatically repeat a mutation.'
    } : tool)
  }
  registry.register(taskTool(agents))
  for (const tool of scoped?.snapshot() ?? []) registry.register(tool)
  return registry
}

// ─────────────────────────── 子代理 ───────────────────────────

/**
 * 谁来真的把子 run 建起来并推给渲染层。
 *
 * ★ 这个插槽和 `mcpOnChange` 是同一种接线,理由也一样:真正能建 run + 继承
 * 窗口订阅的是 `ipc/agent.ts` 的 `startChildRun`,而它拖着 electron ——
 * 从这里 import 它,`agent-run.test.ts` 那条无头链路当场就断了。
 * 方向必须是 **ipc 依赖 runtime,runtime 永不依赖 ipc**。
 *
 * 没装(纯内核测试)时降级成 `runs.create` + 直接跑:run 照跑,只是事件
 * 不推给任何窗口 —— 而无头测试里本来就没有窗口。
 */
export type ChildRunLauncher = (
  parent: RunHandle,
  req: RunRequest,
  driver: (handle: RunHandle, req: RunRequest) => Promise<void>
) => RunHandle

let childRunLauncher: ChildRunLauncher | null = null

export function installChildRunLauncher(fn: ChildRunLauncher): void {
  childRunLauncher = fn
}

/**
 * 后台子代理跑完之后,谁来把结果交回主代理。
 *
 * ★ 原先是渲染层:它收到子 run 的 `run_end` 才调一次 `send()`。没有窗口在看那条
 * 会话时这一步就不会发生,结果烂在库里。现在由 `main/session-runtime.ts` 接手,
 * 插槽的接线方向同 `installChildRunLauncher`(ipc 依赖 runtime,反之不然)。
 * 没装(纯内核测试)时什么都不做,回执停在 pending —— 与旧行为下「没有窗口」一致。
 */
export type BackgroundChildHandler = (
  report: { sessionId: string; callId: string; childRunId: string; subagentType?: string; text: string; summary?: string },
  status: RunStatus
) => void

let backgroundChildHandler: BackgroundChildHandler | null = null

export function installBackgroundChildHandler(fn: BackgroundChildHandler | null): void {
  backgroundChildHandler = fn
}

/** 截 Task 回执上那截预览的长度 —— 与 `subagent_end.summary` 同一个口径 */
const SUBAGENT_SUMMARY_CHARS = 240

/**
 * 从父会话的 Task 回执恢复一个后台子代理的交付物 —— 重启之后用户手动点「处理」走这条。
 *
 * ★ 全文读子会话的**最后一条助手消息**(`lastAssistantMessage`),不解析整段子转录。
 * 子会话 id 的派生与 `childRequestFor` 完全一致。
 */
export function readBackgroundReport(sessionId: string, callId: string): {
  sessionId: string
  callId: string
  childRunId: string
  subagentType?: string
  text: string
  summary?: string
  reportStatus?: NonNullable<SubagentResult['reportStatus']>
} | undefined {
  let receipt: SubagentResult | undefined
  let subagentType: string | undefined
  for (const message of store.getHistory(sessionId)) {
    for (const part of message.parts) {
      if (part.type === 'tool_result' && part.callId === callId && part.subagent !== undefined) receipt = part.subagent
      if (part.type === 'tool_call' && part.callId === callId && typeof part.input === 'object' && part.input !== null) {
        const record = part.input as Record<string, unknown>
        const declared = record['subagent_type'] ?? record['subagentType']
        if (typeof declared === 'string' && declared.trim() !== '') subagentType = declared
      }
    }
  }
  if (receipt === undefined || receipt.background !== true) return undefined
  const last = store.lastAssistantMessage(`${sessionId}:sub:${receipt.childRunId}`)
  return {
    sessionId,
    callId,
    childRunId: receipt.childRunId,
    ...(subagentType === undefined ? {} : { subagentType }),
    text: last === undefined ? '' : visibleText(last),
    ...(receipt.summary === undefined ? {} : { summary: receipt.summary }),
    ...(receipt.reportStatus === undefined ? {} : { reportStatus: receipt.reportStatus })
  }
}

/**
 * 把汇报进度写进库里的 Task 回执。
 *
 * ★ 父 run 还在跑时,它在 `finally` 里会整段写回自己那份内存转录;那条写入经
 * `mergeLatestSubagentReceipts` 以库里最新的 `reportStatus` 为准,所以这里写的值
 * 不会被那份旧快照盖回去。
 */
export function persistSubagentReportStatus(
  sessionId: string,
  callId: string,
  status: NonNullable<SubagentResult['reportStatus']>
): void {
  const history = store.getHistory(sessionId)
  let changed = false
  const messages = history.map((message) => {
    let messageChanged = false
    const parts = message.parts.map((part) => {
      if (part.type !== 'tool_result' || part.callId !== callId || part.subagent === undefined) return part
      if (part.subagent.reportStatus === status) return part
      messageChanged = true
      changed = true
      return { ...part, subagent: { ...part.subagent, reportStatus: status } }
    })
    return messageChanged ? { ...message, parts } : message
  })
  if (changed) store.setHistory(sessionId, messages)
}

/** 配置损坏时的安全回退值。正常值来自 settings.subagent。 */
const DEFAULT_CONCURRENT_SUBAGENTS = 4

/**
 * 现读一次并发上限。
 *
 * ★ 每次都重读而不是缓存:用户在运行期间调高上限,下一次派发(以及队列的下一个
 * tick)就该生效。设置文件是用户可编辑输入,所以这里对异常值做防御性归一化 ——
 * 注意 `globalLimit` 合法下界是 **0**(意为「完全禁用子代理」),不是 1。
 */
function subagentCapacity(): Capacity {
  const configured = store.getSettings().subagent
  return {
    perSessionLimit:
      Number.isInteger(configured.perSessionLimit) && configured.perSessionLimit >= 1
        ? configured.perSessionLimit
        : DEFAULT_CONCURRENT_SUBAGENTS,
    globalLimit:
      Number.isInteger(configured.globalLimit) && configured.globalLimit >= 0
        ? configured.globalLimit
        : DEFAULT_CONCURRENT_SUBAGENTS
  }
}

/**
 * 子代理并发队列 —— 满载时排队,而不是把一次调度问题报成失败。策略全在
 * `kernel/subagent-queue.ts`,这里只把「上限从哪读」和「谁在占着名额」接上去。
 */
const subagentQueue = new SubagentQueue(subagentCapacity, {
  childRunIds: (parentRunId) => runs.activeChildrenOf(parentRunId).map((h) => h.runId),
  subagentRunIds: () => runs.activeSubagentRunIds()
})

/** 子 runId 的序号。进程内单调递增就够 —— 它只需要在本进程里唯一。 */
let childSeq = 0

/**
 * 正在被父代理**前台等着**的子代理:childRunId → 「转去后台」。
 *
 * 需求:一个前台子代理跑得比预想久(半小时还没回来),用户想让主代理先接着干别的,
 * 而不是只能干等或者停掉它重来。表项只在父代理真正 await 的那段时间里存在。
 */
const foregroundSubagents = new Map<string, () => boolean>()

/**
 * 把一个前台子代理转去后台。返回「转成了」。
 *
 * false 不是错误:它已经跑完 / 本来就是后台 / 根本不是这个进程里的 run ——
 * 这些情况下用户要的「别再等它」要么已经成立,要么无从谈起。
 */
export function detachForegroundSubagent(childRunId: string): boolean {
  return foregroundSubagents.get(childRunId)?.() ?? false
}

/**
 * 等一个 run 结束。
 *
 * ★ 先判 `status` 再挂监听,两件事都要做:`RunHandle.emit` 在发出 `run_end`
 * 之后会 `listeners.clear()`,所以晚一步挂上去的监听器**永远不会被调用**,
 * 表现是这个 Promise 永远不 resolve —— 父代理就卡在那次工具调用上,
 * 直到用户点停止。
 */
function waitForEnd(handle: RunHandle): Promise<Extract<AgentEvent, { type: 'run_end' }>> {
  return new Promise((resolve) => {
    if (handle.status !== 'running') {
      const end = handle.since(handle.seq - 1)[0]
      resolve(end?.type === 'run_end' ? end : { type: 'run_end', status: handle.status })
      return
    }
    const off = handle.on((ev) => {
      if (ev.type !== 'run_end') return
      off()
      resolve(ev)
    })
  })
}

interface ChildRunResult {
  status: RunStatus
  text: string
  error?: AgentError
  endedAt?: number
  usage?: TokenUsage
}

/**
 * 监听子 run 的低频状态，并把汇总写回父 run。原始子 run 事件仍会通过
 * inherited topic 推给渲染层，后台子任务在父 run 结束后也能继续被观察。
 */
function monitorChildRun(
  parent: RunHandle,
  child: RunHandle,
  childReq: RunRequest,
  callId: string,
  /**
   * ★ 一个**可变**的盒子而不是布尔值:前台子代理可以在半路被用户转去后台
   * (`detachForegroundSubagent`),而「要不要按后台收尾」要在**结束那一刻**读。
   * 派出时读一次的话,被转走的那个会按前台收尾 —— 回执上没有 pending,
   * 结论也不会送回主代理,用户转出去的那份活就这样没了下文。
   */
  mode: { readonly background: boolean },
  environment: WorkspaceEnvironment,
  finished?: Promise<void>
): Promise<ChildRunResult> {
  let toolCalls = 0
  let toolErrors = 0
  let usage: TokenUsage | undefined
  const off = child.on((event, childSeq) => {
    if (event.type === 'stream' && event.delta.type === 'message_end' && event.delta.usage !== undefined) {
      usage = addTokenUsage(usage, event.delta.usage)
    }
    if (event.type === 'tool_start') {
      toolCalls++
      if (parent.status === 'running') parent.emit({
        type: 'subagent_update', callId, childRunId: child.runId,
        phase: 'tool', currentTool: event.toolName, currentTarget: toolTarget(event.input), toolCalls, toolErrors, childSeq, at: event.at
      })
    } else if (event.type === 'tool_end') {
      if (event.isError) toolErrors++
      if (parent.status === 'running') parent.emit({
        type: 'subagent_update', callId, childRunId: child.runId,
        phase: 'thinking', currentTool: undefined, currentTarget: undefined, toolCalls, toolErrors, childSeq, at: event.at
      })
    } else if (event.type === 'context_usage' && parent.status === 'running') {
      parent.emit({
        type: 'subagent_update', callId, childRunId: child.runId,
        contextUsage: { used: event.used, window: event.window, shouldCompact: event.shouldCompact },
        toolCalls, toolErrors, childSeq
      })
    } else if (event.type === 'stream' && event.delta.type === 'message_end' && parent.status === 'running') {
      parent.emit({
        // ★ 一轮回复完整结束 = 之前那句「正在退避重试」必然已经过期。清除走的是
        //   和 `currentTool` 同一个约定(键在即清),所以哪怕这条遥测抢在继承事件
        //   前头到达,卡片上也不会挂着一句过期的重试提示。
        type: 'subagent_update', callId, childRunId: child.runId,
        phase: 'finishing', notice: undefined, usage: event.delta.usage, toolCalls, toolErrors, childSeq
      })
    }
  })

  return (async () => {
    const { status, error } = await waitForEnd(child)
    if (finished !== undefined) await finished.catch(() => {})
    off()
    /*
      名额刚刚腾出来了 —— 立刻重扫一遍队列,把排队中的下一个派出去。
      ★ 这只是**快路径**:队列自己那个 1 秒的兜底重扫才是正确性所在。
      run 被 abort 但从未 finish、launch 抛了异常、run 被整个遗弃,
      这些路径压根走不到这一行,靠的就是那次兜底重扫。
    */
    subagentQueue.notify()
    // 只读最后一条助手消息 —— 子代理交差的正文就是它,整段子转录不必解析一遍
    const last = store.lastAssistantMessage(childReq.sessionId)
    const text = last === undefined ? '' : visibleText(last)
    const endedAt = child.endedAt
    const background = mode.background
    persistSubagentCompletion(parent.sessionId, callId, child.runId, status, text, error, background)
    if (background) {
      try {
        const summary = text.trim() === '' ? undefined : text.trim().slice(0, SUBAGENT_SUMMARY_CHARS)
        backgroundChildHandler?.({
          sessionId: parent.sessionId,
          callId,
          childRunId: child.runId,
          ...(child.backgroundTask?.type === undefined ? {} : { subagentType: child.backgroundTask.type }),
          text,
          ...(summary === undefined ? {} : { summary })
        }, status)
      } catch (handlerError) {
        getHost().logger.warn(`[subagent] background report failed: ${child.runId}`, handlerError)
      }
    }
    /*
      SubagentStop 钩子。★ fire-and-forget，理由同 Stop。
      本地沿用子 run 继承的 Shell 快照；远端仍按工作区取得有效租约。
    */
    try {
      void runHookEvent({
        event: 'SubagentStop',
        environment: environment.remote ? getWorkspaceEnvironment(childReq.workspaceId) : environment,
        sessionId: childReq.sessionId,
        runId: child.runId,
        extra: { status }
      }).catch(() => undefined)
    } catch {
      // 环境已经释放（父 run 早就结束了）—— 子代理收尾的钩子不值得为此报错。
    }
    if (parent.status === 'running') {
      parent.emit({
        type: 'subagent_end', callId, childRunId: child.runId, status,
        childSeq: child.seq,
        ...(text.trim() === '' ? {} : { summary: text.slice(0, 240) }),
        ...(error === undefined ? {} : { error }),
        ...(endedAt === undefined ? {} : { at: endedAt })
      })
    } else if (background) {
      // The parent may have finished before this detached child. Its own run_end
      // is still delivered through the inherited child topic for the renderer.
      getHost().logger.info(`[subagent] background child finished after parent: ${child.runId}`)
    }
    return {
      status, text,
      ...(error === undefined ? {} : { error }),
      ...(endedAt === undefined ? {} : { endedAt }),
      ...(usage === undefined ? {} : { usage })
    }
  })()
}

function toolTarget(input: unknown): string | undefined {
  if (typeof input === 'string') return input.slice(0, 160)
  if (input === null || typeof input !== 'object') return undefined
  const record = input as Record<string, unknown>
  for (const key of ['path', 'filePath', 'query', 'pattern', 'command', 'url']) {
    const value = record[key]
    if (typeof value === 'string' && value.trim() !== '') return value.slice(0, 160)
  }
  return undefined
}

/** Update the durable Task receipt when a detached/background child finishes. */
function persistSubagentCompletion(
  sessionId: string,
  callId: string,
  childRunId: string,
  status: RunStatus,
  text: string,
  error?: AgentError,
  background = false
): void {
  const history = store.getHistory(sessionId)
  let changed = false
  const summary = text.trim() === '' ? undefined : text.trim().slice(0, 240)
  const messages = history.map((message) => {
    let messageChanged = false
    const parts = message.parts.map((part) => {
      if (part.type !== 'tool_result' || part.callId !== callId || part.subagent?.childRunId !== childRunId) return part
      messageChanged = true
      changed = true
      return {
        ...part,
        subagent: {
          ...part.subagent,
          status,
          ...(background ? { reportStatus: 'pending' as const } : {}),
          ...(summary === undefined ? {} : { summary }),
          ...(error === undefined ? {} : { error })
        }
      }
    })
    return messageChanged ? { ...message, parts } : message
  })
  if (changed) store.setHistory(sessionId, messages)
}

/**
 * A parent run writes its complete in-memory history in `finally`, while a
 * detached child can update the durable Task receipt at the same time. Merge
 * the latest receipt into that final write so the older parent snapshot cannot
 * downgrade a completed child back to `running`.
 */
function mergeLatestSubagentReceipts(
  messages: readonly AgentMessage[],
  latest: readonly AgentMessage[]
): AgentMessage[] {
  const receipts = new Map<string, SubagentResult>()
  for (const message of latest) {
    for (const part of message.parts) {
      if (part.type !== 'tool_result' || part.subagent === undefined) continue
      receipts.set(part.callId, part.subagent)
    }
  }
  if (receipts.size === 0) return [...messages]

  const rank = (status: SubagentResult['status']): number => {
    if (status === undefined) return 0
    return status === 'running' ? 1 : 2
  }
  return messages.map((message) => {
    let messageChanged = false
    const parts = message.parts.map((part) => {
      if (part.type !== 'tool_result' || part.subagent === undefined) return part
      const current = receipts.get(part.callId)
      if (current === undefined) return part
      const status = rank(current.status) >= rank(part.subagent.status)
        ? current.status
        : part.subagent.status
      const merged: SubagentResult = {
        ...part.subagent,
        ...(current.background === undefined
          ? {}
          : { background: current.background }),
        ...(current.summary === undefined
          ? {}
          : { summary: current.summary }),
        ...(current.error === undefined
          ? {}
          : { error: current.error }),
        ...(current.reportStatus === undefined
          ? {}
          : { reportStatus: current.reportStatus }),
        ...(status === undefined ? {} : { status })
      }
      messageChanged = true
      return { ...part, subagent: merged }
    })
    return messageChanged ? { ...message, parts } : message
  })
}

/**
 * 子 run 的 `RunRequest`。三处继承规则各自挡着一种真实的坏结果。
 */
function childRequestFor(
  parentReq: RunRequest,
  parent: RunHandle,
  def: AgentDefinition,
  childRunId: string,
  prompt: string,
  configuredModel: { model: string; modelProviderId?: string },
  configuredThinking: SubagentThinking,
  /** 这一次派发自己钉的模型/档位(`SubagentRequest.model/thinking`),压过其余所有来源 */
  pinned: { model?: string; thinking?: ThinkingLevel } = {}
): RunRequest {
  /*
    ★ 别名和供应商必须成对决定 —— 三档来源的优先级连同理由都在
    `subagentModelSelection` 里。`configuredModel` 已经过可用性校验(见调用点)。
  */
  const selection = pinned.model !== undefined
    ? { model: pinned.model }
    : subagentModelSelection(declaredSubagentModel(def), configuredModel, parentReq)
  return {
    runId: childRunId,
    /*
      ★ **独立且派生的 sessionId。**复用父的那个,会被 `store.setHistory`
      整数组覆盖直接毁掉:父子并发跑完,谁后 `finally` 谁赢,父的转录凭空少几轮。
      派生还顺手回答了「子 run 的转录从哪儿读」—— `getHistory` 天然是 `[]`,
      而全新的上下文窗正是子代理存在的意义,不用特判。
    */
    sessionId: `${parentReq.sessionId}:sub:${childRunId}`,
    // 同一个工作区 = 同一道路径围栏。子代理换不了工作区。
    workspaceId: parentReq.workspaceId,
    parentRunId: parent.runId,
    /*
      ★ 上面那个派生 id 只是**惯例**,这一项才是**事实**。

      「这条转录不进侧边栏/搜索/导出」和「删父会话时跟着回收」全都读
      `sessions.parent_session_id`,没有任何一处再去 parse 那个 `:sub:` ——
      它是递归拼出来的(子 runId 自己也含 `:sub:`),想从里面反推父亲,
      第一个切出的是**爷爷**、最后一个切出的是一个**不存在的 id**。
      理由完整写在 `db/schema.ts` 第 10 条迁移上。
    */
    parentSessionId: parentReq.sessionId,
    depth: parentReq.depth + 1,
    input: [{ type: 'text', text: prompt }],
    // 子代理始终按编程模式运行。Plan 模式没有 Task，ACP 则必须把实施交给子代理。
    mode: 'code',
    thinking: childThinkingFor(
      pinned.thinking === undefined ? def : { ...def, thinking: pinned.thinking }, configuredThinking, parentReq, selection
    ),
    // 联网是用户的硬开关,子代理放宽不了
    webSearch: parentReq.webSearch,
    // 同理:子代理不能替用户决定多花一倍的钱,也不该在用户明确开了之后被压回 272K。
    ...(parentReq.maxContext === undefined ? {} : { maxContext: parentReq.maxContext }),
    /*
      ★ 取 min,不是取子代理声明的那个。否则一个被投毒的 MCP 工具描述
      可以诱导主 agent 派一个子 agent 去做它自己不被允许做的事 ——
      这是**真实的提权路径**,不是理论风险(见 `minPermission` 的注释)。

      ★ 取 `parent.permissionMode`(父 run 此刻的活值),不取
      `parentReq.permissionMode`(父 run 开始时的快照)——用户在派子代理之前
      把档位调宽,子代理理应看见那次调整,而不是继续背着父 run 起跑那一刻
      早就作废的旧档位。
    */
    permissionMode: minPermission(parent.permissionMode, def.permissionMode ?? 'full'),
    ...selection,
    skillIds: parentReq.skillIds,
    skillSelectionMode: parentReq.skillSelectionMode,
    agentType: def.name
  }
}

/**
 * 子 run 这一轮用哪个思考档位。
 *
 * 三档来源和模型那一套逐字相同(声明的 > 设置里那一栏 > 父 run),理由见
 * `subagent-thinking.ts`。取完之后**还要按子 run 自己的模型归一化一次** ——
 * 档位是模型的属性,而子 run 的模型和父 run 未必是同一个。
 *
 * ★ 不归一化会怎样:模型不认这个档位时,适配器要到**发请求那一刻**才抛
 * 「模型不支持推理强度「high」」,于是每一次派发全数失败,而父代理只会转述一句
 * 「子代理失败了」—— 用户看不到那条真正的原因,也无从知道该改哪一栏。
 * 归一化是**静默降级**(档位是偏好,不是硬约束;硬约束是药丸上那个显式选择),
 * 与 `availableSubagentModel` 同一个取舍,降级时留一条 warn,免得连日志里都查不到。
 */
function childThinkingFor(
  def: AgentDefinition,
  configured: SubagentThinking,
  parentReq: RunRequest,
  selection: { model: string; modelProviderId?: string }
): ThinkingLevel {
  const wanted = subagentThinkingSelection(def.thinking, configured, parentReq.thinking)
  const alias = getRouter().resolveModel(selection.model, selection.modelProviderId)
  const effective = normalizeModelThinkingLevel(wanted, alias)
  if (effective !== wanted) {
    getHost().logger.warn(
      `[subagent] ${def.name} 的思考档位 ${wanted} 在 ${selection.model} 上不可用,按 ${effective} 跑`
    )
  }
  return effective
}

/**
 * 子代理文件里那一对 `(model, modelProviderId)`,把**已经指不到的那个锁摘掉**。
 *
 * ★ 摘锁而不是整对作废:别名多半还在(供应商被删、或者那家不再提供这个别名),
 * 只按别名择优跑得起来,而带着一个查不到的供应商去路由,拿到的是空候选集 ——
 * 子代理整条失败,父代理只会转述一句「子代理失败了」。用户当初钉那一家是**偏好**,
 * 而「这个子代理要用这个别名」是他更硬的那半个意思。
 *
 * ★ 反过来,别名本身查不到时**不动它**:那是用户写错了模型名,静默换一个模型
 * 跑完再交回结果,比失败难查得多。
 */
function declaredSubagentModel(def: AgentDefinition): { model?: string; modelProviderId?: string } {
  if (def.model === undefined) return {}
  const providerId = def.modelProviderId
  if (providerId === undefined) return { model: def.model }
  if (getRouter().resolveModel(def.model, providerId) !== undefined) {
    return { model: def.model, modelProviderId: providerId }
  }
  getHost().logger.warn(
    `[subagent] ${def.name} 钉的供应商 ${providerId} 提供不了 ${def.model},改按别名择优`
  )
  return { model: def.model }
}

/**
 * 设置页那个「默认子代理」当前**还指得到一条绑定吗**。
 *
 * ★ 这道校验不能省,也不能挪进 `subagentModelSelection`(那是个纯函数,够不着
 * 路由器)。`settings.subagent.model` 是一个别名字符串,而供应商随时会被删、
 * 别名随时会被改名 —— `ipc/provider.ts` 的 `repairModelSelection` 会在那些写入
 * 之后修好它,但**中间存在窗口期**,而且用户手动改设置文件也绕得过去。
 * 悬空时的表现不是报错,是子代理在路由器里拿到空候选集然后整条失败 ——
 * 而父代理只会转述一句「子代理失败了」,用户无从知道是这一栏的锅。
 *
 * 查不到就退回 `{ model: '' }`,让 `subagentModelSelection` 落到下一档(父 run)。
 * 静默降级是对的:这一栏是**偏好**,不是硬约束;硬约束是药丸上那个显式选择。
 */
function availableSubagentModel(configured: { model: string; modelProviderId?: string }):
  { model: string; modelProviderId?: string } {
  const model = configured.model.trim()
  if (model === '') return { model: '' }
  if (getRouter().resolveModel(model, configured.modelProviderId) === undefined) {
    getHost().logger.warn(`[subagent] configured default model is unavailable: ${model}`)
    return { model: '' }
  }
  const providerId = configured.modelProviderId
  return { model, ...(providerId === undefined ? {} : { modelProviderId: providerId }) }
}

/**
 * `ToolContext.spawnSubagent` 的生产实现 —— 两道闸门 + 排队要名额 + 建 run + 等结果。
 *
 * ★ 并发满了走的是**排队**,不是拒绝(见 `kernel/subagent-queue.ts`)。
 * 之前那两道「满了就 refused」的闸门把一个纯调度问题伪装成了一次失败:
 * UI 上是一张红色失败卡,而模型拿到 `toolFail` 之后可能放弃这个子任务、
 * 可能换个 `subagent_type` 再试(以为是那个代理坏了)—— 这次派发的意图直接丢了。
 *
 * ★ 它住在 runtime 而不是 `task.ts`,因为只有这里同时握着**父 handle**
 * (要在它身上发 `subagent_start/end`)和 store(要取子代理的产出)。
 * `ctx.emit` 只会发 `tool_progress`,发不了子代理事件。
 */
/** ⚠️ 只给测试:队列里还有几个在等。接线用例靠它证明等待者没有泄漏。 */
export function queuedSubagentCountForTest(): number {
  return subagentQueue.size
}

function spawnSubagentFor(parent: RunHandle, parentReq: RunRequest, parentSkills: readonly Skill[] | undefined, resources: RunResources): SpawnSubagentFn {
  return async (sub) => {
    /*
      ★ 深度在这里**再断言一次**,尽管 `Task.run` 第一行已经判过。
      那一道是给模型看的(告诉它为什么、以及改做什么),这一道是给代码看的 ——
      防的是将来别的调用方绕过 `Task` 直接调这个函数。
    */
    if (parentReq.depth >= MAX_DEPTH) {
      return {
        kind: 'refused',
        reason:
          `The subagent nesting limit of ${String(MAX_DEPTH)} levels has been reached. ` +
          `Do this step yourself.`
      }
    }

    resources.environment.assertReady()
    const def = resources.agents.find((definition) => definition.name === sub.subagentType)
    if (def === undefined) {
      /*
        ★ **绝不静默回落到 general-purpose。**名字敲错的用户会拿到一份
        「看起来对」的、由错误代理产出的结果,而且永远不会发现。
        把可用清单列全,模型下一次就能选对,或者判断出没有合适的。
      */
      return {
        kind: 'refused',
        reason:
          `There is no subagent named "${sub.subagentType}". Available: ${resources.agents.map((definition) => definition.name).join(', ')}. ` +
          `Pick one from that list, or skip the subagent and do this step yourself.`
      }
    }

    // 读取最新设置,让用户在运行期间调整默认模型与并发上限都能影响下一次派发。
    const configured = store.getSettings().subagent
    // 上限单独走 `subagentCapacity()`:队列每个 tick 也要重读同一份归一化逻辑。
    const { perSessionLimit, globalLimit } = subagentCapacity()

    if (sub.model !== undefined && getRouter().resolveModel(sub.model) === undefined) {
      return {
        kind: 'refused',
        reason: `There is no model named "${sub.model}". Available: ${[...new Set(getRouter().listModels().map((alias) => alias.alias))].join(', ')}.`
      }
    }

    const childRunId = `${parent.runId}:sub:${String(++childSeq)}`
    const childReq = childRequestFor(
      parentReq, parent, def, childRunId, sub.prompt, availableSubagentModel(configured), configured.thinking,
      { ...(sub.model === undefined ? {} : { model: sub.model }), ...(sub.thinking === undefined ? {} : { thinking: sub.thinking }) }
    )

    /*
      ★ 名额拿到之后要同步做完的那一段。整段交给队列在派发循环里**内联执行** ——
      理由见 `subagent-queue.ts` 里 `SlotRequest.start` 的注释(不这么做的话,
      `runs.create` 会落在微任务里,同一个空位会被唤醒的多个等待者同时认领)。
    */
    const mode = { background: sub.background === true }
    let childHandle: RunHandle | undefined
    const start = (): { result: Promise<ChildRunResult> } => {
      /*
        ★ 环境要**再断言一次**。排队可能排了几分钟,而工作区租约在这期间
        完全可能已经被释放 —— 入队前那一次断言早就过期了。
      */
      resources.environment.assertReady()

      const startedAt = getHost().clock.now()
      parent.emit({
        type: 'subagent_start', callId: sub.callId, childRunId,
        // ★ 派生 id 在这里已经算好了(`childRequestFor`),原样递出去 ——
        //   渲染层照着模板再拼一遍的话,套两层子代理就会拼错,见 `event.ts` 那段注释
        childSessionId: childReq.sessionId,
        description: sub.description,
        subagentType: def.name,
        ...(def.color === undefined ? {} : { color: def.color }),
        model: childReq.model,
        background: sub.background === true,
        at: startedAt
      })

      const launch =
        childRunLauncher ??
        /*
          没装启动器时的降级路径。run 照跑,只是不推给任何渲染层 ——
          这恰好是无头测试想要的形状,所以它不是「测试替身」,是一条真实的降级。
        */
        ((_p: RunHandle, r: RunRequest, driver: (h: RunHandle, rr: RunRequest) => Promise<void>) => {
          const h = runs.create(r)
          const release = runs.retainSessionForRun(h)
          /*
            ★ **必须接住 driver 的 rejection**(`ipc/agent.ts` 的 `launch` 早就这么做了,
            只有这条降级路径漏了)。`runAgent` 在 `new AgentSession(...)` 那一步是会
            `release(); throw` 的 —— 抛完没人 `finish()`,这条 run 就永远停在 `running`。
            以前的症状只是「偶尔少一个并发位」;排队之后,它会永久占着名额,
            把后面所有派发全卡在队列里。
          */
          const failed = (error: unknown): void => {
            if (h.signal.aborted) h.finish('aborted')
            else h.finish('error', agentError('unknown', error instanceof Error ? error.message : String(error)))
          }
          try { void Promise.resolve(driver(h, r)).catch(failed).finally(release) }
          catch (error) { try { failed(error) } finally { release() } }
          return h
        })

      /*
        ★ 除了 handle,还要攥住 driver 那个 promise —— 光等 `run_end` 是**不够**的。

        `runAgent` 的形状是 `session.run().finally(() => store.setHistory(...))`:
        `run_end` 在 `run()` 里面就发出去了,而写转录发生在它**之后**的那个
        `finally` 里。只等 handle 的话,这里读到的 history 会是空的 ——
        于是每一次子代理都被报成「结束时没有产出任何文字」,而它明明说了话。
      */
      let finished: Promise<void> | undefined
      const child = launch(parent, childReq, (h, r) => {
        if (sub.background === true) h.backgroundTask = { type: def.name, description: sub.description }
        finished = runAgent(h, r, def, parentSkills, resources)
        return finished
      })
      childHandle = child
      return {
        result: monitorChildRun(parent, child, childReq, sub.callId, mode, resources.environment, finished)
      }
    }

    /*
      ★ 这里依赖 `acquire` 的**同步不变式**:它在第一次 await 之前就决定好了
      「直接启动」还是「入队」,所以 `acquire(...)` 一返回,`queued` 就已经是准的了。
      后台派发要立刻回话给模型,靠的就是这条。别在这两行之间插入 await。
    */
    let queued = false
    const slot = subagentQueue.acquire({
      parent,
      // 后台派发不阻塞父 run,所以它**不算**死锁判据里的「卡住了」。见 `SlotRequest.blocking`
      blocking: sub.background !== true,
      start,
      onQueued: (ahead) => {
        queued = true
        parent.emit({
          type: 'tool_progress',
          callId: sub.callId,
          progress: {
            callId: sub.callId,
            message: ahead === 0 ? '排队中，等待空位' : `排队中，前面还有 ${String(ahead)} 个`
          }
        })
      }
    })

    if (sub.background === true) {
      void slot.then((acquired) => {
        if (acquired.ok) {
          return acquired.value.result.then(() => undefined, (error: unknown) => {
            getHost().logger.warn(`[subagent] background child failed: ${childRunId}`, error)
          })
        }
        /*
          排着队但一秒都没跑过。★ 必须把落盘的那张 Task 回执改成 aborted ——
          否则卡片会永远停在「运行中」,而它其实从来没有启动过。
        */
        getHost().logger.info(`[subagent] queued background child never started (${acquired.reason}): ${childRunId}`)
        persistSubagentCompletion(parent.sessionId, sub.callId, childRunId, 'aborted', '', undefined, true)
        return undefined
      }, (error: unknown) => {
        getHost().logger.warn(`[subagent] background child failed to start: ${childRunId}`, error)
      })
      return {
        kind: 'background',
        childRunId,
        ...(def.color === undefined ? {} : { color: def.color }),
        ...(queued ? { queued: true } : {})
      }
    }

    const acquired = await slot
    if (!acquired.ok) {
      /*
        ★ 死锁拒绝要留痕:它基本等价于「用户把并发上限配得太小,而代理在套娃」——
        生产里这是唯一能发现那件事的途径(队列深度今天没有任何地方在记)。
      */
      if (acquired.reason === 'deadlock') {
        getHost().logger.warn(
          `[subagent] slot deadlock (${String(perSessionLimit)}/${String(globalLimit)}): ${childRunId}`
        )
      }
      return { kind: 'refused', reason: slotRefusalReason(acquired.reason, globalLimit) }
    }

    /*
      前台在等结果 —— 但用户随时可能在卡片上把它转去后台(`detachForegroundSubagent`)。
      两件事谁先到听谁的:结果先到,照常交回;转后台先到,这次调用当场以「后台」收场,
      子 run 原样跑下去,结束时由 `monitorChildRun` 按后台收尾(读的是 `mode`)。
    */
    const result = acquired.value.result
    let detach = (): void => {}
    const detached = new Promise<'detached'>((resolve) => { detach = () => resolve('detached') })
    foregroundSubagents.set(childRunId, () => {
      /*
        ★ 只认「还在跑」的。run_end 已经发出、收尾还没做完的那一小段里转后台,
        `monitorChildRun` 可能已经读过 `mode` 了 —— 这时改它,卡片会被标成后台,
        结论却按前台交回,两边对不上。拒掉,前台的结果马上就到。
      */
      if (mode.background || childHandle === undefined || childHandle.status !== 'running') return false
      mode.background = true
      // ★ 补上后台任务的身份:`activeBackgroundChildrenOfSession`(目标续跑判「还有后台活」)
      //   和交差时的 subagentType 都认它
      childHandle.backgroundTask = { type: def.name, description: sub.description }
      detach()
      return true
    })
    let raced: ChildRunResult | 'detached'
    try {
      raced = await Promise.race([result, detached])
    } finally {
      foregroundSubagents.delete(childRunId)
    }

    if (raced === 'detached') {
      void result.catch((error: unknown) => {
        getHost().logger.warn(`[subagent] detached child failed: ${childRunId}`, error)
      })
      getHost().logger.info(`[subagent] moved to background by user: ${childRunId}`)
      return {
        kind: 'background',
        childRunId,
        detached: true,
        ...(def.color === undefined ? {} : { color: def.color })
      }
    }

    const completed = raced
    return {
      kind: 'finished', childRunId, status: completed.status, text: completed.text,
      ...(def.color === undefined ? {} : { color: def.color }),
      ...(completed.error === undefined ? {} : { error: completed.error }),
      ...(completed.usage === undefined ? {} : { usage: completed.usage })
    }
  }
}

/**
 * 排不进队列时给模型的那句话。
 *
 * ★ 每一种都要说清**「不是你错了,是满了」以及「那你现在该做什么」**。
 * 只说「达到上限」的话,模型会换一个 `subagent_type` 再试一次 —— 它会以为
 * 是那个子代理的问题(和 `task.ts` 里深度闸门那条注释同一个道理)。
 */
function slotRefusalReason(reason: SlotRefusal, globalLimit: number): string {
  switch (reason) {
    case 'disabled':
      return (
        'Subagents are disabled: the global concurrent subagent limit is set to 0. ' +
        'Do this step yourself — launching another subagent will not work.'
      )
    case 'deadlock':
      return (
        `All ${String(globalLimit)} concurrent subagent slots are held by subagents that are ` +
        'themselves waiting for a slot, so none will free up. Do this step yourself.'
      )
    case 'overflow':
      return (
        'Too many subagents are already queued. ' +
        'Wait for the queued ones to finish before launching another, or carry on yourself.'
      )
    case 'cancelled':
      return 'The conversation stopped before a subagent slot became free.'
  }
}


/**
 * 这条 run 属于哪一条**提炼会话**(`Session.skillSource` 非空)—— 不属于则返回 undefined。
 *
 * 需求:一个 run 该不该参与 Skill 激活(挂等待点、白名单改用工作区设置),判据是
 * 「它的根会话是不是提炼会话」。子 run 的 `req.sessionId` 是自己那条派生会话(`:sub:`),
 * 所以要沿**落盘的** `parentSessionId` 往上走到根。
 *
 * ★ 不靠 `runId` 里那个 `:sub:` 前缀反推,也不靠 `runs` 里的父子链:前者是递归拼出来的
 * (见 `db/schema.ts` 第 10 条),后者会因为父 handle 被 reap 而断掉。
 * 不满足会怎样:真正写 SKILL.md 的子代理不被认成提炼会话的 run —— 文件写好了,
 * Skill 页上却一直是关的,而且零报错。
 */
function skillExtractionRoot(sessionId: string): string | undefined {
  const seen = new Set<string>()
  let session = store.getSession(sessionId)
  while (session !== undefined && session.parentSessionId !== undefined && !seen.has(session.id)) {
    seen.add(session.id)
    const parent = store.getSession(session.parentSessionId)
    if (parent === undefined) break
    session = parent
  }
  return session?.skillSource === undefined ? undefined : session.id
}

/**
 * 生产路径的 run 驱动 —— **`ipc/agent.ts` 的默认驱动**。
 * 假发射器从此只是 pump 测试的夹具。
 *
 * `AgentSession.run()` 承诺不抛异常(结局一律经 `handle.finish`),
 * 所以这里不需要 catch;真加一个 catch 反而会掩盖那个承诺哪天被破坏。
 */
export async function runAgent(
  handle: RunHandle,
  req: RunRequest,
  /**
   * 这个 run 由哪个子代理跑。**由启动器闭包传进来,不在这里查注册表**:
   * 查的话,派出去和真的跑起来之间夹着一次目录重扫,拿到的可能已经是
   * 另一份定义了 —— 而模型看到的工具清单还是派出去那一刻的。
   */
  agent?: AgentDefinition,
  inheritedSkills?: readonly Skill[],
  inheritedResources?: RunResources
): Promise<void> {
  req = { ...req, mode: normalizeModeId(req.mode) }
  ensureGoalRuntime()
  const primary = agent === undefined && req.depth === 0 && req.parentSessionId === undefined && req.parentRunId === undefined
  if (req.inputGoalId !== undefined && getActiveGoal(req.sessionId)?.id !== req.inputGoalId) {
    handle.finish('done')
    return
  }
  // Freeze the evaluator alias and provider before the first await of this run.
  const goalSettings = store.getSettings()
  if (primary) {
    /*
      需求:上一轮提炼会话写完 SKILL.md 之后的激活是**异步**的 —— run_end 先到渲染层,
      而 `activateWrittenSkills`(扫目录 + 写工作区设置 + 广播)还要一会儿。同一条会话
      排着队的下一轮必须等它做完再开跑,否则刚写的 Skill 不在这一轮的系统提示词里,
      表现是「提炼完了,但下一次提问它还是不知道」。

      ★ 位置:在挂自己的 `beforeFinish` **之前**、在重扫 Skill 目录之前 —— 这一轮读到的
      注册表与工作区设置必须已经是激活之后的那一份。
      ★ 只有 primary 等。子 run 和父 run 共用同一工作区的等待点,而父 run 的那个要等
      子 run 收尾、激活做完才放 —— 子 run 也等的话就是父子互等,表现为整条会话
      卡死到用户点停止为止。
      ★ 用 abortable 而不是裸 await:等待期间用户按停止必须能立刻退出。
    */
    try {
      await abortable(() => skillActivationBarrier.wait(req.workspaceId), handle.signal)
    } catch (error) {
      /*
        中断不是失败:这一轮从来没有开始过,所以不写 run 记录(同上面 `inputGoalId`
        那条早退)。但 `finish` 仍要走 —— 渲染层在等最后那条 run_end。
      */
      if (!handle.signal.aborted) throw error
      handle.finish('aborted')
      return
    }
    const pause = (): void => pauseGoal(req.sessionId)
    handle.signal.addEventListener('abort', pause, { once: true })
    handle.beforeFinish((status) => {
      handle.signal.removeEventListener('abort', pause)
      if (status !== 'done') pause()
    })
  }
  const existing = store.getSession(req.sessionId)
  const workspace = store.getWorkspace(req.workspaceId)
  if (!workspace || (existing && existing.workspaceId !== req.workspaceId)) throw new EnvironmentError('unbound')
  const session = store.ensureSession({
    id: req.sessionId,
    workspaceId: req.workspaceId,
    // 非空 = 这是子代理的转录:落盘照旧,但它从此不出现在任何面向用户的枚举里。
    ...(req.parentSessionId === undefined ? {} : { parentSessionId: req.parentSessionId }),
    model: req.model,
    ...(req.modelProviderId === undefined ? {} : { modelProviderId: req.modelProviderId }),
    mode: req.mode,
    thinking: req.thinking,
    rootPathAtCreation: workspaceRootFor(req.workspaceId)
  })
  /*
    需求:提炼会话的收尾要异步等激活做完,而 run_end 一到渲染层就会把排队中的下一轮
    发出来 —— 所以等待点必须在 run_end **之前**同步挂上:注册表的 `beforeFinish`
    在 `finish()` 里同步跑,早于它 emit 那条 run_end。

    ★ 只给提炼会话这一条线上的 run 挂:真正写 SKILL.md 的常常是它派出去的子 run,
    所以子 run 也要挂(归属沿落盘的 `parentSessionId` 往上找,见 `skillExtractionRoot`)。
    普通 run 不挂 —— 它们不产生激活,挂了只会让同一工作区里无关的下一轮白等。
  */
  const extractionRootId = skillExtractionRoot(req.sessionId)
  let releaseActivation = (): void => {}
  // 新会话第一次发送时把模型/模式冻结到元数据；后续 run 不覆盖用户改过的标题。
  // ★ 比较里必须带上 modelProviderId:同一个会话里从 RoutinAI 切到 Codex 时别名没变,
  //   漏了这一项的话会话元数据会一直停在旧供应商上。
  if (existing !== undefined && (existing.model !== req.model
    || existing.modelProviderId !== req.modelProviderId
    || existing.mode !== req.mode || existing.thinking !== req.thinking)) {
    store.putSession({ ...session, model: req.model,
      // ★ 无条件写,不能用条件展开:req 这次没锁供应商而会话上还留着上次那个的话,
      //   条件展开清不掉它,于是会话永远停在旧供应商上 —— 又是「只改一半」。
      modelProviderId: req.modelProviderId,
      mode: req.mode, thinking: req.thinking, updatedAt: Date.now() })
  }
  /*
    ★ 这一行是**这条会话在侧边栏里出生的那一刻**。

    渲染层不再抢先建会话(白纸不落库,见 `renderer/stores/tabs.ts` 的 `makeTab`),
    所以「最近对话」里这一条只能等这里广播。不广播的话,它要拖到标题生成器
    跑完第一次 `putSession` 才冒出来 —— 中间那几秒用户看着自己的消息在流式,
    左边却什么都没有,像是发进了黑洞。

    子代理的转录不算:`parentSessionId` 非空的那些从来不进面向用户的枚举。
  */
  if (existing === undefined && req.parentSessionId === undefined) sessionOnChange?.({ kind: 'metadata', sessionIds: [req.sessionId], workspaceId: req.workspaceId })
  const startedAt = getHost().clock.now()
  store.setRunRecord(req.runId, req.sessionId, 'running', startedAt)
  /*
    ★ 扫描在**建 session 之前**。系统提示词是在第一轮组装时定下来的,
    晚一步扫的话这一轮的目录还是上一轮那份 —— 用户刚装的那条 Skill
    要到下一次提问才出现,而他会以为是没装上。

    ★ 子 run **不重扫**:它的定义在派出去那一刻就定死了(见上面的 `agent` 参数),
    而重扫会在父代理正跑着的时候把 `Task` 的 description 换掉。
  */
  let runSkills: readonly Skill[] | undefined = inheritedSkills
  let environment: WorkspaceEnvironment
  let scopedMcpTools: ToolRegistry | undefined
  let runAgents = inheritedResources?.agents ?? agentRegistry(req.workspaceId).list()
  let runMode = modeRegistry(req.workspaceId).resolve(req.mode)
  let release = (): void => {}
  let planExecution: PlanExecutionContext | undefined
  let skillExtraction: string | undefined
  let projectInstructions: string
  let git: GitContext | undefined
  try {
    if (inheritedResources) {
      const lease = getEnvironments().retain(inheritedResources.environment)
      environment = lease.environment
      release = lease.release
    }
    else {
      const lease = getEnvironments().acquire(req.workspaceId)
      environment = lease.environment
      release = lease.release
    }
    environment.assertReady()
    if (req.planExecution !== undefined) {
      planExecution = await abortable(() => loadPlanExecution(environment, req.planExecution!), handle.signal)
    }
    if (agent === undefined) {
      await abortable(() => refreshModes(req.workspaceId, environment), handle.signal)
      runMode = modeRegistry(req.workspaceId).resolve(req.mode)
      runSkills = await abortable(() => refreshSkills(req.workspaceId, environment), handle.signal)
      runAgents = await abortable(() => refreshAgents(req.workspaceId, environment), handle.signal)
      scopedMcpTools = await abortable(() => prepareWorkspaceMcp(req.workspaceId, environment), handle.signal)
    }
    /*
      需求:提炼会话的每个主 run 都带上源会话摘要(`Session.skillSource`)。
      每个 run 现算而不是建会话时存一份:源会话之后可能又追加了消息,或者被删了。
      子 run 不给 —— 它拿到的是父 run 派下来的具体任务,几万 token 的摘要只是负担。
      复用上面刚扫出来的 `runSkills`,不再扫一次目录。
    */
    if (primary && session.skillSource !== undefined) {
      skillExtraction = loadSkillExtraction(session.skillSource.sessionId, runSkills ?? [], req)
    }

    /*
      ★ 这两件事在那个 `if` **外面** —— 先弄清那个 `if` 到底在防什么:
      它防的**不是**「数据太旧」,而是 `refreshSkills` / `refreshAgents` 会
      `replaceAll()` **这个工作区那一份**注册表(父 run 跑到一半时子 run 去重扫,
      父代理下一轮的 Skill 目录就被换掉了)。分桶只隔开了工作区之间,
      同一个工作区里的父子 run 仍然共用一份 —— 所以这道闸门照旧需要。

      读一个文件、shell 一次 git,**什么单例都不动**,所以不属于那道闸门。
      而子代理在**同一个工作区**里改同一份代码:不给它项目规矩,它会写出一份
      不合仓库约定的代码交回来 —— 父代理拿到的只有结论,看不出错在哪一步。

      ★ 唯一不给子代理的是 todo 快照,而它**不需要特判**:快照是从 `messages`
      反推的,子 run 的 `messages` 是空的,推出来自然就是没有。这条自解。
    */
    projectInstructions = await abortable(() => loadInstructions(req.workspaceId, environment), handle.signal)
    git = await abortable(() => readGitContext(
      environment.spawn,
      environment.rootPath,
      handle.signal
    ), handle.signal)
    environment.assertReady()
    handle.signal.throwIfAborted()
  } catch (error) {
    release()
    if (!handle.signal.aborted) throw error
    handle.finish('aborted')
    store.setRunRecord(req.runId, req.sessionId, handle.status, startedAt, getHost().clock.now())
    return
  }

  if (runMode.id !== req.mode) {
    req = { ...req, mode: runMode.id }
    const current = store.getSession(req.sessionId)
    if (current !== undefined && current.mode !== runMode.id) {
      store.putSession({ ...current, mode: runMode.id, updatedAt: Date.now() })
      if (req.parentSessionId === undefined) {
        sessionOnChange?.({ kind: 'metadata', sessionIds: [req.sessionId], workspaceId: req.workspaceId })
      }
    }
  }

  const history = store.getHistory(req.sessionId)
  // 需求：在本 run 提交消息前冻结历史边界；提醒额度只活在这个 run 的闭包里。
  const reconcileTodos = createTodoReconciler(history.length)
  if (primary) restoreGoal(req.sessionId, history)

  /*
    UserPromptSubmit 钩子。★ 位置是**拿到环境之后、`new AgentSession` 之前** ——
    用户消息是在 AgentSession 的构造函数里 commit 的，晚一步就拦不住了。
    （`ipc/agent.ts` 的 `startRun` 是同步函数，在那儿 await 不了，所以只能落这里。）

    守卫 `agent === undefined`：子 run 不跑这个事件，它没有「用户提交了提示词」这回事。
  */
  if (agent === undefined && req.input.length > 0 && req.inputInternal !== true) {
    const reports = await runHookEvent({
      event: 'UserPromptSubmit',
      environment,
      sessionId: req.sessionId,
      runId: req.runId,
      // `req.input` 是 ContentPart[]（可能带附件）；钩子只关心文字那部分。
      extra: { prompt: req.input.filter((p) => p.type === 'text').map((p) => p.text).join('\n') },
      signal: handle.signal
    })
    const blocked = reports.find((r) => r.outcome === 'blocked' || r.decision === 'deny')
    if (blocked !== undefined) {
      release()
      handle.finish('error', agentError('unknown', blocked.reason ?? '被钩子拦下'))
      store.setRunRecord(req.runId, req.sessionId, handle.status, startedAt, getHost().clock.now())
      return
    }
    /*
      注入走 `projectInstructions` 而不是新开一个字段：它经 `context-assembler.ts`
      的 `decorate` 只进**发出去的那份消息流**，转录一个字不动 —— 这正是钩子注入
      上下文该有的语义（不该出现在用户的聊天气泡里，也不该被重放到旧会话）。
    */
    const injected = [
      ...reports.map((r) => r.additionalContext ?? ''),
      /*
        插件的上下文提供者。★ 和钩子并进同一个注入块,而不是另开一段 ——
        两者对模型来说是同一件事(「这一轮额外要知道的东西」),分开只会让
        提示词里多一层没有语义的嵌套。长度上限与强制包裹在 `plugin/manager.ts`。
      */
      pluginContextProvider === null
        ? ''
        : await pluginContextProvider({
          prompt: req.input.filter((p) => p.type === 'text').map((p) => p.text).join('\n')
        }).catch(() => '')
    ].filter((s) => s !== '').join('\n')
    if (injected !== '') {
      projectInstructions = projectInstructions === ''
        ? `<hook-context>\n${injected}\n</hook-context>`
        : `${projectInstructions}\n\n<hook-context>\n${injected}\n</hook-context>`
    }
  }
  const ref = normalizeEnvironmentRef(workspace.environment)
  const fileReferenceSource: FileReferenceSource = ref.kind === 'connection'
    ? { kind: 'workspace', workspaceId: workspace.id, environment: ref, rootPath: environment.rootPath, connectionRevision: store.getConnectionProfile(ref.connectionId)?.revision ?? -1 }
    : { kind: 'local' }
  /*
    ★ 三步缺一不可，且顺序不能动:先唤醒插件 → 再把它们的工具并进全局注册表 →
    最后才截本轮工具快照。

    - 少了 `preparePluginTools()`:只声明 `onTool:` 的插件还没注册任何工具。
    - 少了 `syncPluginTools()`:那些工具仍然停在 `getTools()` 单例之外 —— 单例只在
      首次构建时读过一次 provider,而那一次发生在 `startPlugins()` **之前**,
      所以装着微信插件的进程里,模型看到的一直是一张没有插件工具的表。
      症状:插件明明白白列在「已启用插件提供的 Agent 工具」里,模型却说没有这个工具,
      而且全程零报错。

    `syncPluginTools` 同时负责撤下被禁用/卸载插件的工具(见 `plugin-tools.ts` 的 ★),
    所以它必须每轮都跑,不能只在启用时跑一次。
  */
  if (inheritedResources === undefined) {
    await preparePluginTools()
    syncPluginTools(getTools(), livePluginTools())
  }
  const resources: RunResources = inheritedResources ?? { environment, fileReferenceSource, agents: runAgents, tools: snapshotRunTools(environment, runAgents, scopedMcpTools) }
  const baseAllowedTools = intersectToolLists(runMode.tools, agent?.tools)
  const modeToolPool = baseAllowedTools ?? resources.tools.info().map((tool) => tool.internalId)
  const planWorkflowEnabled = runMode.id === 'plan' || runMode.tools?.includes('EnterPlanMode') === true
  const allowedTools = (): readonly string[] => planWorkflowEnabled
    ? planToolAllowList(req, modeToolPool)
    : modeToolPool.filter((tool) => tool !== 'EnterPlanMode' && tool !== 'ExitPlanMode')
  /**
   * 这一轮的 Stop 事件已经在 `onTurnEnd` 里发过了。
   *
   * ★ 一个**本地**变量，不是注册表：`onTurnEnd` 就是在这个函数里装配的，
   *   闭包捕获它既不需要跨模块的登记，也不会在 run 之间泄漏。
   */
  let stopHookFired = false
  const goalContext: TurnEndContext = {
    environment, workspaceId: req.workspaceId, runId: req.runId,
    model: req.model, modelProviderId: req.modelProviderId,
    evaluatorModel: goalSettings.goalEvaluatorModel,
    evaluatorModelProviderId: goalSettings.goalEvaluatorModelProviderId,
    log: (line) => getHost().logger.info(line),
    backgroundWork: () => runs.activeBackgroundChildrenOfSession(req.sessionId).map((child) => ({
      taskId: child.runId, type: child.backgroundTask!.type, description: child.backgroundTask!.description
    })),
    isIdle: () => !runs.runningIn(req.workspaceId).some((run) => run.sessionId === req.sessionId),
    options: {
      workspaceId: req.workspaceId, depth: 0, model: req.model, modelProviderId: req.modelProviderId,
      mode: req.mode, thinking: req.thinking, permissionMode: req.permissionMode,
      webSearch: req.webSearch, maxContext: req.maxContext,
      skillIds: req.skillIds, skillSelectionMode: req.skillSelectionMode
    },
    ...(session.origin === 'scheduled' ? {} : {
      inject: (parts, goalId) => {
        if (getActiveGoal(req.sessionId)?.id !== goalId) return false
        const active = runs.runningIn(req.workspaceId).find((run) => run.sessionId === req.sessionId)
        if (active !== undefined) {
          if (active.signal.aborted) return false
          active.enqueueInternal({ id: ulid(getHost().clock.now()), parts, internal: true, goalId })
          return true
        }
        return wakeGoal(req.sessionId, parts, goalId)
      }
    })
  }
  if (primary) bindGoalRun(req.sessionId, goalContext, req.input.length > 0 && req.inputInternal !== true)
  /*
    需求:提炼会话的 Skill 激活状态以主进程工作区设置为准。
    普通 run 继续使用 `RunRequest` 的冻结快照,否则用户在一轮运行中改设置会改变
    这一轮的工具目录;提炼 run 是例外,因为它自己负责刚写入 Skill 并广播工作区设置。
    不满足会怎样:渲染层还没收到 `workspace:changed` 时,新一轮会拿旧白名单,
    Skill 已在磁盘上却不会进系统提示词。

    ★ 原先这里把「刚写进去、激活还没跑完的名字」投影进白名单(`pendingSkillNamesByWorkspace`),
    那条路已经拆掉:上一轮的激活有屏障挡着,这一轮开跑之前它必然已经做完,工作区设置
    就是唯一真源。留两份真源(设置 + 内存里待激活的名字)迟早对不上,而对不上的表现是
    「同一个工作区里两条会话看到的 Skill 目录不一样」。
    ★ 整条提炼会话线(主 run 与它派出去的子 run)都用这份设置:子 run 的 `req` 是父 run
    快照的副本,只认 `req` 的话,刚激活的那条 Skill 在子代理眼里根本不存在。
    ★ 启动时读一次就冻结(`runSkills` 刚扫完、屏障也等过了),这一轮中途不再重读。
  */
  const extractionSkillSelection = extractionRootId === undefined
    ? undefined
    : (() => {
      const settings = store.getWorkspace(req.workspaceId)?.settings
      if (settings === undefined) return undefined
      return {
        skillIds: settings.activeSkillIds,
        ...(settings.skillSelectionMode === undefined ? {} : { skillSelectionMode: settings.skillSelectionMode })
      }
    })()
  let agentSession: AgentSession
  try { agentSession = new AgentSession(
    {
      // 本地 run 冻结同一份 shell 事实与执行器；改设置只影响下一次任务。
      host: environment.remote ? getHost() : {
        ...getHost(), platform: environment.platform, spawn: environment.spawn
      },
      fileReferenceSource: resources.fileReferenceSource,
      ...(environment.remote ? { workspace: environment } : {}),
      upstream: getRouter(),
      tools: resources.tools,
      workspaceRoot: environment.rootPath,
      /**
       * ★ 多轮的全部实现。缺省(空转录)意味着模型每轮都从零开始 ——
       * 界面上明明有三轮问答,它却只看得见最后一句。
       *
       * 存储由 `state/store` 收口到 SQLite；这里始终只依赖
       * `getHistory`/`message_commit`，不接触数据库结构。
       */
      history,
      contextManagement: store.getSettings().contextManagement,
      /*
        需求:压缩用哪个模型 / 哪个思考档位,同样按 run 开始那一刻的设置冻结
        (同下面的输出额度)。三档来源是「工作区 > 全局 > 会话模型」,
        所以两份都要传 —— 只传全局的话,圆环菜单里那两栏在自动压缩上永远不生效。
      */
      compaction: {
        model: store.getSettings().compactModel,
        ...(store.getSettings().compactModelProviderId === undefined
          ? {}
          : { modelProviderId: store.getSettings().compactModelProviderId }),
        thinking: store.getSettings().compactThinking
      },
      workspaceCompaction: workspace.settings,
      // 需求:输出额度按 run 开始那一刻的设置冻结,和权限档位同一个口径
      //(「下一次新回复生效」)—— 跑到一半改设置不该让同一个 run 前后两轮额度不同。
      maxOutputTokens: store.getSettings().maxOutputTokens,
      ...(primary ? goalProposalsFor({
        handle, context: goalContext, gate: interactions, interactive: session.origin !== 'scheduled',
        planning: () => req.mode === 'plan' || activePlanFor(req) !== undefined,
        // AppSettings is trusted user state; repository/local files cannot change consent policy.
        setting: () => store.getSettings().modelProposedGoals
      }) : {}),
      /*
        定时任务的读写通道。★ **无条件装配**(只要有工作区),包括定时任务自己
        跑出来的那些会话 —— 链式排程是允许的,拦住无限延伸的是
        `bridge.ts` 里的 `chainDepth`,不是「这里装不装」。子代理那道门在工具侧
        (`depth === 0`),因为只有工具才分得清读和写。
      */
      scheduling: schedulingBridgeFor({
        workspaceId: req.workspaceId,
        sessionId: req.sessionId,
        model: req.model,
        ...(req.modelProviderId === undefined ? {} : { modelProviderId: req.modelProviderId }),
        resolveModel: (model, modelProviderId) => getRouter().resolveModel(model, modelProviderId) !== undefined
      }),
      /*
        Agent 的 shell 注册表。★ **每个 run 都装**,子代理也不例外:工具卡片上的
        停止按钮要能停到子代理正在跑的那条命令,而「起一个后台服务」这件事
        也没有理由只许主代理做。
        ★ 租约现取现还(见 `shellBridgeFor` 的注释)—— 后台进程握着的那把
        必须比这次 run 活得久,否则 run 一结束,SSH 连接就进了 60 秒空闲回收,
        而 `BashOutput` 只会说「读不到」。
      */
      shells: shellBridgeFor({
        workspaceId: req.workspaceId,
        environment,
        retain: () => getEnvironments().retain(environment),
        now: () => getHost().clock.now()
      }),
      // 设置 › 实验:现读,同生图开关 —— 改了设置下一轮起工具就跟着上/下
      experimentalEnabled: (feature) => store.getSettings().experimental?.[feature] === true,
      subagentModels: () => [...new Set(getRouter().listModels().map((alias) => alias.alias))],
      /*
        对话内生图的通道。★ 和 `scheduling` / `shells` 一样**每个 run 都装**:
        有没有生图模型由桥自己的 `available()` 现答(它读的是设置页那份配置),
        「装不装桥」不该随配置变化 —— 否则改一次设置就多一条装配路径。

        ★ 密钥在这里读、在桥里用:内核拿到的只有 `credential(ref)` 这一个回调,
        `secrets` 本体不进工具链(理由同 `ToolHost` 刻意少了 `secrets` 那两条)。
      */
      imageGen: imageGenBridgeFor({
        providers: () => store.listProviders(),
        /*
          ★★ 必须是**解析后**的别名(`listResolvedModels`,与路由器 `providerConfig.aliases`
          和设置页 `provider:listModels` 同一个口径),不能用 `store.listAliases()` 的
          原始落库记录:目录收录的图片模型,它的 modality/能力位可能从来没写进库
          (登录同步/老导入发生在目录收录之前),原始记录因此长得像文本模型。
          用原始记录的症状是:「图片生成」设置页的下拉里**能选中**这个模型(那边读
          解析后的),保存也成功,但对话里 `generate_image` 整体不下发,零报错 ——
          用户看到的就是「选了图片模型却说没有工具」。
        */
        aliases: () => listResolvedModels(),
        /*
          用户点名的生图模型(`AppSettings.imageModel` 那一对)。★ 每次现读,
          不缓存:设置的唯一权威在主进程,缓存一份的表现是改完设置对话里还用旧模型。
          空串 = 没选过 → 桥答「没有可用模型」,工具不下发。
        */
        preferredModel: () => {
          const settings = store.getSettings()
          return settings.imageModel === ''
            ? null
            : { alias: settings.imageModel, providerId: settings.imageModelProviderId }
        },
        // 「对话生图」开关,同样现读(理由同上):关掉后下一轮起工具不再下发
        enabled: () => store.getSettings().imageGenerationEnabled === true,
        credential: async (ref) => parseCredential(await getHost().secrets.get(ref)),
        fetch: (input, init) => getHost().fetch(input, init),
        /*
          改图的源图解析。★ 复用 `resolveImageDataRef`(发上游请求时解析附件的
          同一个函数):会话归属、路径围栏、大小、魔数那套校验只有一份,
          两份实现迟早漏一份,而漏的那份就是能读到别的会话附件的路。
          sessionId 给的是**这次 run 所属的会话** —— 和上游请求那侧同一个口径。
        */
        resolveImage: (source, signal) => resolveImageDataRef(source, getHost(), { sessionId: req.sessionId }, signal)
      }),
      /*
        本会话的图片仓:生成图落成 `ncw://` 会话附件、`SaveImage` 按地址读回字节。
        ★ 与 `imageGen.resolveImage` 同一个 sessionId 口径 —— 地址只在它所属的会话里解析得通,
        子代理的 run 因此拿到的是**子会话**的仓(它生成的图父代理点不到名,这是会话隔离本身)。
      */
      sessionImages: sessionImageStoreFor({ sessionId: req.sessionId, host: getHost }),
      /*
        视频生成的后台通道。★ 与生图那条的关键差别就是**异步**:
        这里给的是 `submit` / `status` / `cancel` 三个动词,成品由 manager
        在几分钟后落成本会话附件(见 `video-generation/manager.ts` 的文件头)。

        ★ 与 `imageGen` 同一条口径:有没有视频模型由桥现答,「装不装」不随配置变化。
        ★ `retrieve` 复用**同一个** `sessionVideoStoreFor` —— 于是下载走的
        落盘路径与 `SaveVideo` 读回的是同一条(会话归属、realpath 围栏、魔数)。
      */
      videoGen: videoGenBridgeFor({
        sessionId: req.sessionId,
        manager: getVideoManager,
        configProfile: currentConfigScope,
        workspaceId: () => req.workspaceId
      }),
      sessionVideos: sessionVideoStoreFor({ sessionId: req.sessionId, host: getHost }),
      acceptsGoalInput: (goalId) => primary && getActiveGoal(req.sessionId)?.id === goalId,
      prepareMessage: (message) => primary ? prepareGoalMessage(req.sessionId, message) : message,
      onMessageCommit: (message) => {
        // 停止后被强行收口的 run(#187):会话可能已经属于下一轮,迟到的消息不再落盘
        if (handle.abandoned) return
        // ★ 带上 runId:重启之后逐轮用量全靠这一跳把消息接回 `usage_records`
        // (那张表一直有 run_id,缺的一直是反向的归属)。
        store.commitMessage(req.sessionId, message, req.runId)
        // message_commit 已经完成 SQLite 写入，再通知渲染层刷新侧边栏
        // 的 updatedAt；事件泵随后仍会按原顺序接收 message_commit。
        //
        /*
          ★ 子 run 不广播。`commitMessage` 只 bump **它自己那条会话**的 updated_at,
          父的一个字节没变;而子会话又不在 `listSessions` 里 —— 这次广播会让每个
          窗口重拉一遍列表,然后渲染出和上一帧逐字相同的结果。一个跑 30 轮的
          后台子代理就是 30 次这样的空转,且正好落在主进程最忙的时刻
          (`listSessions` 对每一行还要多一次 `getSession` 去读 favorited,
          而 `DatabaseSync` 是同步阻塞的)。

          判据用 `parentSessionId` 而不是 `depth === 0`:让「这条会话是隐藏的」
          和「不发侧边栏事件」共用同一个条件,将来多一种隐藏会话也自动跟上。
        */
        if (req.parentSessionId === undefined) sessionOnChange?.({ kind: 'messages', sessionIds: [req.sessionId], workspaceId: req.workspaceId })
      },
      onToolUsage: ({ runId, toolCalls, toolErrors }) => {
        store.updateUsageToolsForRun(runId, toolCalls, toolErrors)
      },
      /*
        PostToolUse 钩子。★ 只能追加反馈，不能改 output（见 `SessionDeps` 上那段）。

        `exit 2` 时把 `isError` 翻成 true —— 「你的改动被 lint 钩子拒绝了」是模型
        能据此行动的信息，而一段夹在正常输出里的抱怨它多半会忽略。
      */
      onToolExecuted: async ({ tool, input, output, isError }) => {
        const reports = await runHookEvent({
          event: 'PostToolUse',
          environment,
          sessionId: req.sessionId,
          runId: req.runId,
          tool: { internalId: tool.internalId, externalName: tool.externalName, input },
          extra: { toolOutput: output.content, toolIsError: isError },
          signal: handle.signal
        })
        if (reports.length === 0) return undefined
        const context = reports.map((r) => r.additionalContext ?? r.reason ?? '').filter((s) => s !== '').join('\n')
        const blocked = reports.some((r) => r.outcome === 'blocked')
        return {
          ...(context === '' ? {} : { additionalContext: context }),
          ...(blocked ? { isError: true } : {})
        }
      },
      approve: createApprovalFn(req, handle, environment, approvalDeps),
      /*
        ★★ 回合末判定 —— **只装在主 run 上**（`agent === undefined` 且没有父 run）。

        子 run 没有「停止」这回事：它的结局是把结论交回父代理，没有人在等它
        满足一个会话级的条件。不装配意味着子 run 里那个 `await` 是
        `undefined?.()`，零开销，也没有第二条「要不要判定」的判断路径。

        ★ `stopHookFired` 在这里置位，`finally` 里那条 fire-and-forget 的 Stop
          据此跳过 —— 同一轮收尾不能把 Stop 事件发两遍（用户的通知脚本会响两次，
          而 `exit 2` 的那条会在一个没人接的地方阻断）。
      */
      ...(primary
        ? {
          onTurnEnd: async (turnEnd) => {
            stopHookFired = true
            const decision = await handleTurnEnd(turnEnd, goalContext)
            // 需求：补报进度不能覆盖 Goal/Stop 的续跑、强停或后台等待裁决，也不能催计划模式写平行清单。
            if (decision !== undefined || turnEnd.signal.aborted || getActiveGoal(req.sessionId) !== undefined
              || req.mode === 'plan' || activePlanFor(req) !== undefined
              || runs.activeBackgroundChildrenOfSession(req.sessionId).length > 0) return decision
            const todo = resources.tools.byInternalId('TodoWrite')
            const allowed = allowedTools()
            const toolName = todo !== undefined && (allowed.includes(todo.internalId) || allowed.includes(todo.externalName))
              ? todo.externalName
              : undefined
            // 和 Goal 续跑一样，补报后还要经过 Stop；次数上限独立于会被工具调用清零的空转计数。
            return reconcileTodos(turnEnd, toolName)
          }
        }
        : {}),
      /*
        ★ 只有主 run 装配 —— 子代理没有人可问,让它连 `interact` 都拿不到。

        这是 `noInteraction` 快照过滤之外的**第二道**:快照是"下发的工具列表里
        没有它",这里是"就算别的路径调到了,`ctx.interact` 也是 undefined"。
        `builtin/interaction.ts` 的既有兜底会返回
        「User interaction is unavailable in this environment.」—— 一次工具失败,
        模型看得见、能换路,而不是停在一次永不结算的 `interactions.request` 上。
      */
      ...(req.parentRunId === undefined
        ? { interact: (draft: InteractionDraft) => interactions.request(handle, draft, getHost().clock.now()) }
        : {}),
      /*
        ★ 这里给的是**目录**,不是正文。`context-assembler.ts` 只读
        name / description 两个字段拼成一行一条的清单,正文要模型自己调
        `Skill` 工具去取(渐进披露)。传的仍然是完整的 `Skill` 对象,
        是因为注册表本来就有它,而多一份裁剪过的类型只会多一处要同步的地方。
      */
      skills: activeSkills(req, runSkills, extractionSkillSelection),
      /*
        ★ 角色提示词是**追加**的(见 `buildSystemPrompt` 里那段),
        工具清单是**收窄**的(`snapshot` 只过滤不新增)。两个方向都只能变严,
        所以一个 agent 定义文件永远不可能给子代理拿到父代理没有的东西。
      */
      modePrompt: modePromptFor(runMode),
      ...(agent !== undefined ? { agentPrompt: agent.prompt } : {}),
      allowedTools,
      ...(planWorkflowEnabled
        ? {
          writeFileRestriction: () => activePlanFor(req)?.absolutePath,
          /*
            ★ 计划文件的路径每轮重新注入。它原本只存在于 EnterPlanMode 那条
            `tool_result` 里,而机械压缩会把历史工具输出整条清空
            (`context-assembler.ts` 的 `compactPart`)—— 压缩之后模型只能凭记忆写路径,
            于是撞上 `restrictedWrite` 的围栏。提醒块是每轮现算的,压缩动不了它。
          */
          planFile: () => activePlanFor(req)?.path
        }
        : {}),
      spawnSubagent: spawnSubagentFor(handle, req, runSkills, resources),
      /*
        ★ 这两项注入的是**发出去的那份消息流**,转录一个字都不动
        (`context-assembler.ts` 的 `decorate`)。commit 进转录的话,用户会在
        **自己的**聊天气泡里逐字读到整篇 AGENTS.md,而且旧会话会永远重放旧规矩。
      */
      ...(projectInstructions !== '' ? { projectInstructions } : {}),
      ...(git !== undefined ? { git } : {}),
      /*
        ★ 和上面两项一样**照给子代理** —— 用户设的是「跟我说话时是什么样」,
        而子代理写的代码、交回的结论最终都是给同一个人看的。

        无条件给,不在这里判断三栏是不是全空:`buildPersonalizationSection`
        对全空的那份返回空串,而空串会被 `buildSystemPrompt` 的 filter 丢掉 ——
        「什么都没填」这件事只该有一个地方知道,多一处判断就多一处会漂移的判断。
      */
      personalization: store.getSettings().personalization,
      ...(planExecution === undefined ? {} : { planExecution }),
      ...(skillExtraction === undefined ? {} : { skillExtraction })
    },
    handle,
    req
  ) } catch (error) { release(); throw error }
  /**
   * `finally` 而不是 `then`:中断路径上 `finalizeAbort` 已经把半截回复和
   * 补上的 tool_result 都写进了 `history`。不在中断时落盘,下一轮就会带着
   * 一堆没有配对 tool_result 的 tool_call 上行 —— 那正是方案 §4.8
   * 花了整节篇幅避免的 400。
   */
  /*
    需求:只有即将进入 running.finally 的 run 才能登记激活等待点。
    原先为抢在 run_end 之前在 ensureSession 后注册,但启动扫描取消、提交钩子拒绝、
    构造失败都不会进入下面的 finally,表现为整个工作区后续对话永久卡住。
    现在在启动步骤全部成功后、run() 之前同步注册,既先于 finish,又保证有清理路径。
  */
  if (extractionRootId !== undefined) {
    handle.beforeFinish(() => { releaseActivation = skillActivationBarrier.begin(req.workspaceId) })
  }
  const running = agentSession.run()
  if (agent === undefined && req.depth === 0 && history.length === 0 && req.input.length > 0 && !handle.signal.aborted) {
    // The primary stream has already started. Title generation is detached from
    // its promise, and from tool calls, approval waits, and later user turns.
    try {
      const current = store.getSession(req.sessionId)
      const firstMessage = agentSession.history[0]
      if (current !== undefined && firstMessage !== undefined) getSessionTitles().start(current, firstMessage, req.model, req.modelProviderId)
    } catch {
      getHost().logger.warn('[session-title] Could not start background title generation.')
    }
  }
  /*
    需求:提炼会话写完 SKILL.md 之后的激活是异步的(扫目录 + 写工作区设置 + 广播),
    而这条 run 真正的收尾要等它做完 —— 下一轮的等待点靠这一刻才被放走。
    ★ 收尾本来就是异步续延(见下面 `store.setHistory` 那条注释),这里再多等一步激活。
  */
  return running.finally(async () => {
    if (primary) {
      releaseGoalRun(req.sessionId)
      clearRuntimeHooks(req.sessionId)
    }
    /*
      ★ 这里**不**清计划。计划按会话存(`kernel/plan-run.ts` 的 `PlanScope`),
      一次计划几乎总要跨好几轮:模型问一批问题、run 结束、用户回答、新 run 接着写。
      在这里清掉等于「下一轮 Write 又消失了」。它的正常终点在 `ExitPlanMode`
      拿到批准或拒绝的那一刻。
    */
    /*
      Stop 钩子 —— 一轮运行收尾。★ fire-and-forget 且**不 await**：这里是
      `finally` 里的异步续延，落盘才是正事；让一条通知脚本拖慢转录落盘，
      换来的是「应用退出时这一轮没存上」。
      守卫 `depth === 0 && parentSessionId === undefined`：子 run 收尾走
      SubagentStop，不重复触发。

      ★ `!stopHookFired`：正常 `end_turn` 那条路已经在 `onTurnEnd` 里**等着**
        跑过一次了（那次要看结果，因为 Stop 现在能阻断）。这里只补中断、
        出错、`stopAfterTool` 这三条到不了 `onTurnEnd` 的路径 —— 两边都发的话
        用户的通知脚本会在每一轮响两次。
    */
    if (req.depth === 0 && req.parentSessionId === undefined && !stopHookFired) {
      void runHookEvent({
        event: 'Stop',
        environment,
        sessionId: req.sessionId,
        runId: req.runId,
        workspaceId: req.workspaceId,
        commandOnly: true,
        extra: { status: handle.status, stop_hook_active: false }
      }).catch(() => undefined)
    }
    // message_commit 已逐条落盘；replaceHistory 是兼容旧调用/修复异常的最终校验。
    // A detached child may have completed between the last commit and this
    // final write. Preserve its newest durable metadata when replacing the
    // parent's in-memory snapshot.
    /**
     * ★ 这是一个**异步续延**:`abortAll()` 早已返回,库可能已经在退出流程里封掉了
     * (`shutdownRuns()` 同步返回,6 秒兜底的 `finish()` 不等环境/MCP 关完就 closeDatabase)。
     * 封库后写入会抛 —— 抛在 finally 里会变成无人接管的 rejection,所以在这里收住,
     * 并留一条日志:这一条转录确实没落盘,不能假装它落了。
     */
    let writtenSkills: string[] = []
    try {
      // 停止后被强行收口的 run(#187):库里的历史可能已经被下一轮续写,这份过期快照不能整份覆盖它
      if (handle.abandoned) return
      const latest = store.getHistory(req.sessionId)
      store.setHistory(req.sessionId, mergeGoalStatusHistory(mergeLatestSubagentReceipts(agentSession.history, latest), latest))
      store.setRunRecord(req.runId, req.sessionId, handle.status, startedAt, getHost().clock.now())

      /*
        ★ 封包本轮的文件改动 —— 采集器读即清。**不看 handle.status**:中断/出错时
        磁盘上已发生的写入就是既成事实,照样封,好让审查卡也能撤销「跑到一半」的改动。
        子代理(child run)的改动 roll-up 到顶层 root run:阻塞型子代理先于父收尾,
        此时父 handle 仍在注册表里,climb 得到父的 runId 与**父的会话**,卡片据此
        用 `messageRuns[messageId]` 一次查全(见 `db/schema.ts` 第 23 条)。
      */
      const changes = takeChanges(req.runId)
      if (changes.length > 0) {
        let rootRunId = req.runId
        let rootSessionId = req.sessionId
        let cursor = req.parentRunId
        const seen = new Set<string>([req.runId])
        while (cursor !== undefined && !seen.has(cursor)) {
          const parent = runs.get(cursor)
          if (parent === undefined) break
          seen.add(cursor)
          rootRunId = parent.runId
          rootSessionId = parent.sessionId
          cursor = parent.parentRunId
        }
        store.saveFileChangeSet({
          rootRunId,
          sourceRunId: req.runId,
          sessionId: rootSessionId,
          workspaceId: req.workspaceId,
          changes: changes.map((c) => ({
            relPath: c.relPath,
            absPath: c.abs,
            before: c.before,
            after: c.after,
            changeKind: c.changeKind,
            inWorkspace: c.inWorkspace
          })),
          at: getHost().clock.now()
        })
        // 落盘之后再广播,渲染层的审查卡/tab 收到才能拉到已存在的改动集(避开竞态)。
        reviewOnChange?.({ runId: rootRunId, sessionId: rootSessionId, workspaceId: req.workspaceId })
        /*
          需求:提炼会话写完 SKILL.md 后自动在当前工作区启用(显式选装模式下新 Skill 默认是关的)。
          判据取**落盘的会话归属**(`skillExtractionRoot`),不取上面那个 `rootSessionId`:
          真正写盘的多半是提炼会话派出去的子 agent,而父 handle 可能已经被 reap,
          `runs` 那条链断掉时会把子 run 的名字当成根 —— 表现为 Skill 写好了却没人启用。
          ★ 真正的激活在下面 await(它要扫目录 + 写设置 + 广播),不放进这个 try:
          这个 catch 说的是「收尾写入没落盘」,激活失败是另一件事。
        */
        if (extractionRootId !== undefined) {
          writtenSkills = writtenProjectSkillNames(changes.filter((c) => c.inWorkspace).map((c) => c.relPath))
        }
      }
    } catch (error) {
      getHost().logger.warn(`[runtime] run ${req.runId} 的收尾写入没有落盘:${error instanceof Error ? error.message : String(error)}`)
    }
    /*
      需求:提炼会话(含它派出去的子 run)这一轮写进磁盘的项目 Skill,要在放走等待者
      **之前**启用完 —— 同一条会话排队的下一轮靠这个屏障保证「它开跑时工作区设置里
      已经有这条 Skill」。
      ★ 失败不把这一轮判成失败:文件已经落盘,用户仍可以去 Skill 页手动打开它。
    */
    if (extractionRootId !== undefined && writtenSkills.length > 0) {
      try {
        await skillWrittenOnChange?.({ workspaceId: req.workspaceId, sessionId: extractionRootId, names: writtenSkills })
      } catch (error) {
        getHost().logger.warn(`[skill] 提炼写入的 Skill 自动启用失败:${error instanceof Error ? error.message : String(error)}`)
      }
    }
  }).finally(() => {
    /*
      ★ 放等待者走**必须**在这一层(`finally` 的 `finally`):激活抛了、收尾写入抛了,
      都要放。留一个永不释放的等待点,同一工作区里下一轮会一直卡在屏障上,而用户
      看到的只是「消息发出去了没反应」。
      ★ `release()`(环境租约)也挪到了这里 —— 上面的激活要用它:远端工作区的 fs
      走的就是这条连接,先放租约的话激活可能撞上空闲回收,表现为静默失败。
    */
    releaseActivation()
    release()
  })
}

/** 测试专用:把单例清干净,让每个用例从同一个起点开始。 */
export function resetRuntimeForTest(): void {
  shutdownSessionTitles()
  void environments?.shutdown()
  environments = null
  for (const scope of workspaceMcps.values()) { void scope.manager.shutdown(); scope.release() }
  workspaceMcps.clear()
  interactions.clear()
  host = null
  router = null
  agentDrafts = null
  commitMessages = null
  promptSuggestions = null
  inlineCompletions = null
  widgetUpstream = null
  tools = null
  preparePluginTools = async () => {}
  // ★ 和上一行同理:现报函数是模块级的,留着的话上一个用例的插件工具会
  //   经下一份注册表的 `syncPluginTools` 复活 —— 而它们指向的 runtime 已经销毁。
  livePluginTools = () => []
  // 不 await shutdown:这个函数是同步的(beforeEach 里调),而留着的
  // manager 会攥着上一个用例的 ToolRegistry —— 那正是要断开的引用
  mcp = null
  mcpOnChange = null
  sessionOnChange = null
  reviewOnChange = null
  skillWrittenOnChange = null
  // ★ 视频广播口也是模块级的:留着的话上一个用例装的广播器会收到下一个用例的
  //   任务通知,而它指向的窗口注册表早就换了一份(同 `livePluginTools` 的理由)。
  videoOnChange = null
  // ★ 必须清:留着的话,下一个用例第一个 primary run 会等一个永远不会放的等待点,
  //   表现为一串莫名其妙的超时。
  skillActivationBarrier.clear()
  childRunLauncher = null
  childSeq = 0
  // ★ 必须清:留着的话,等待者和它那个 interval 会跨用例泄漏 ——
  //   下一个用例会被上一个用例的定时器唤醒,而它的 registry 早就换了一份。
  subagentQueue.reset()
  /*
    ★ 目标与运行期钩子也必须清。留着的话，下一个用例的第一次 `end_turn` 会被
      上一个用例挂上的那条 Stop prompt 钩子拦住 —— 而它的判定器早就换了一份。
  */
  resetGoalRuntimeForTest()
  resetGoalsForTest()
  clearHookFailuresForTest()
  resetHookHostPortsForTest()
  resetSkillToolPortsForTest()
  clearRuntimeHooks()
  /*
    ★ 也必须清注册表。`afterEach` 走的是 `runs.abortAll()`,而 `abort()` 不置 status ——
    残留的「永远 running」的 run 会被队列当成占位者,把下一个用例的派发永久挂起。
  */
  runs.clearForTest()
  seeded = false
  store.clearHistoriesForTest()
}
