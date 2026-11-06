/**
 * 一个对象,两个名字 —— 方案 §4.3。
 *
 * 类型放在 shared 是因为渲染层要渲染它们(审批弹窗要显示工具名与入参、
 * 设置页要列 MCP 工具)。**运行时的 Tool 对象只有主进程有**,因为 execute 是闭包。
 */
import type { SubagentResult, ToolOutput, ToolOutputImage } from './message'
import type { ToolCard } from './tool-card'

/**
 * 最小可用的 JSON Schema 形状。刻意不引 `@types/json-schema` ——
 * 我们只用 zod 生成、原样下发给上游,自己不解释它。
 */
export type JsonSchema = {
  type?: string
  properties?: Record<string, JsonSchema>
  required?: string[]
  [k: string]: unknown
}

export type ToolSource =
  | { kind: 'builtin' }
  | { kind: 'mcp'; serverId: string }
  | { kind: 'skill'; skillId: string }
  /**
   * 插件贡献的工具。
   *
   * ★ 带上 `pluginId` 不是为了显示,是为了**成批下线**:插件崩了、被禁用、
   * 被卸载时,`unregisterBySource({ kind: 'plugin', pluginId })` 要能一次
   * 把它的全部工具摘掉。没有这个字段的话,下线只能按工具名逐个来,
   * 而「这个插件注册过哪些工具」那份清单会和注册表分叉。
   */
  | { kind: 'plugin'; pluginId: string }

/**
 * ★ 两个名字是必须的:Anthropic 把工具名限制在 ^[a-zA-Z0-9_-]{1,64}$。
 * `mcp__github-enterprise-internal__create_pull_request_review_comment` 直接超 64,
 * 换来一个什么都没说清楚的 400。
 *
 * 注册表负责生成经消毒、截断、带短哈希去重的 externalName,
 * 且该映射**在一次会话内必须稳定** —— 已落盘的转录里存的是旧名字。
 */
export interface ToolInfo {
  /** 规范名,不限长:mcp__github-enterprise__create_pr_review_comment */
  internalId: string
  /** 模型看到的名字:必须匹配 EXTERNAL_NAME_RE */
  externalName: string
  description: string
  inputSchema: JsonSchema
  /** 决定 plan 模式可用性 + 将来的并行调度资格 */
  readOnly: boolean
  /** Explicitly serial tools must not race other calls in a batch. */
  concurrencySafe?: boolean
  /** 决定权限档位(§4.5 那张 5 行表的入参之一) */
  destructive: boolean
  /**
   * 这次调用本身会不会出网。
   *
   * ★ **只能由我们这一侧填,永远不采信工具作者的说法。**内置工具在
   * `defineTool` 的入参里写死;MCP 工具由 `mcp/bridge.ts` 按**传输方式**推出来
   * (那是我们库里的配置),而**不是**读服务器自报的 annotations ——
   * 否则一个远程 MCP 服务器只要声明「我不联网」,用户那颗联网开关就被它关掉了。
   *
   * ★ 现在它**只是事实描述,不参与任何开关判定**。原先两处消费者
   * (`registry.snapshot({network})` 决定下不下发、`permission-gate.ts` 第 1 行决定放不放行)
   * 都读它;输入框「联网搜索」开关收窄成只管网页搜索与抓取之后(用户决定,见
   * `permission-gate.ts` 的 `NETWORK_SWITCH_TOOLS`),两处都改成按那张名单判。
   * 留着这个字段(而不是删掉)是因为插件协议 `tools.register` 与 MCP 桥都在填它,
   * 将来要按「会不会出网」加任何管控时,事实已经在这里。
   */
  needsNetwork: boolean
  source: ToolSource
}

/** 上游对工具名的硬约束。注册表与网关两侧都按这一个常量校验。 */
export const EXTERNAL_NAME_RE = /^[a-zA-Z0-9_-]{1,64}$/
export const EXTERNAL_NAME_MAX = 64

/**
 * ⚠️ MCP / Skill 提供的工具名与描述是**不可信输入** —— 它们会进入系统提示词,
 * 这正是工具投毒攻击的入口(方案 §4.4)。注册时必须做字符白名单与长度限制。
 */
export const DESCRIPTION_MAX = 4096

/** 进度事件是**易失的** —— 单独的事件类型,永不写入转录(方案 §4.3)。 */
export interface ToolProgress {
  callId: string
  /** 一行人类可读的进度,例如 "读取 src/main/index.ts (12KB)" */
  message: string
  /** 0–1,不确定时省略 */
  fraction?: number
  /**
   * 运行中推出的**实时卡片**(第 2 层交互式)——工具还没返回,先给一张可交互的卡。
   * 同 `progress` 一样**易失、不进转录**:落盘的是最终 `output.card`(结果快照),
   * 这张是过程态。用户在它上面点动作 → 反向通道回到挂起的工具。
   */
  card?: ToolCard
  /**
   * 生图工具**刚到手的那一张**图(`index` = 它在这次请求里的第几格,0 起)。
   *
   * 需求:一次要多张时,生成期的图片卡要逐张把占位换成真图,而不是让用户对着
   * N 格加载动画等到最慢的那一张。只推「新到的这一张」而不是累计数组:每张是
   * 几 MB 的 data URL,累计推送会让第 4 张到来时把前 3 张再过一遍 IPC。
   * ★ 同样易失、不进转录 —— 落盘的是最终 `output.images`;`tool_end` 时清掉。
   * ★ 只有内置 `generate_image` 会填它:插件进度由 `plugin/manager.ts` 逐字段
   * 重建,插件伪造不出这一项。
   */
  image?: { index: number; image: ToolOutputImage }
  /**
   * 这次调用此刻**能被用户转去后台**(前台 `Bash` 且环境支持)。
   *
   * ★ 走进度事件而不是 presenter 的静态能力声明:能不能转取决于**这一次**跑在哪
   * (本地能、SSH 不能),同一个工具名给不出一个固定答案。同样易失、不进转录 ——
   * `tool_end` 之后这条调用也就无所谓转不转了。
   * ★ 只有内置 `Bash` 会填它:插件进度由 `plugin/manager.ts` 逐字段重建,伪造不出。
   */
  detachable?: boolean
  /**
   * `Workflow` 工具此刻的整份节点状态(见 `shared/agent/workflow.ts`)。
   * ★ 同样易失、不进转录 —— 落盘的是最终 `output.workflow`;`tool_end` 时清掉。
   */
  workflow?: import('./workflow').WorkflowState
}

export interface ToolResult {
  output: ToolOutput
  isError: boolean
  /** Optional UI-only metadata; encoders ignore it when sending tool results upstream. */
  subagent?: SubagentResult
  /** Host-owned control signal; the result is committed before ending the run. */
  stopRun?: boolean
}

export function toolOk(content: string, extra?: Omit<ToolOutput, 'content'>): ToolResult {
  return { output: { content, ...extra }, isError: false }
}

export function toolFail(content: string): ToolResult {
  return { output: { content }, isError: true }
}
