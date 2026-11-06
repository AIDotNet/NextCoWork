/**
 * 子代理定义 —— 照搬 Claude Code 的 `agents/*.md`。
 *
 * ```
 * <appData>/agents/<name>.md                      全局
 * <workspaceRoot>/.next-cowork/agents/<name>.md  项目(同名时项目胜出)
 * ```
 *
 * ★ 和 Skill 的**不对称是有意的**:Skill 是 `skills/<name>/SKILL.md`(一个目录,
 * 能带脚本、模板、参考资料),子代理是 `agents/<name>.md`(单文件,不带资产)。
 * 一个子代理需要的全部东西就是「你是谁 + 你能用哪些工具」,没有可随身携带的素材。
 * 这是 CC 的实际形状,照搬 —— 用户会把 CC 的 agent 文件原样拖过来。
 *
 * ★ 类型住在 `shared/` 而不是 `main/kernel/agent/`:渲染层要在子代理卡片上
 * 显示名字和描述,而 `shared → main` 这个方向是禁止的。
 */
import type { PermissionMode } from '../agent/permission'
import type { ThinkingLevel } from '../agent/run-request'

/** 定义从哪儿来的。`builtin` 那一支写死在代码里,不落盘 —— 理由见 `agent/builtin.ts`。 */
export type AgentSourceKind = 'builtin' | 'global' | 'project'

export interface AgentDefinition {
  /** `subagent_type` 入参里写的就是它 */
  name: string
  /**
   * 给**模型**看的,不是给用户看的。
   *
   * ★ 它会逐字进 `Task` 工具的 description —— 模型判断「这个活该派给谁」
   * 唯一的依据就是这一行。写成「代码审查代理」不如写成
   * 「审查刚写完的代码,找出 bug 与风格问题。写完一段代码之后主动用它」。
   */
  description: string
  /** 正文 = 角色提示词。**追加**在 `BASE_PROMPT` 之后,不替换它。 */
  prompt: string
  /**
   * 允许用的工具。**省略 = 继承主 run 的全部工具**(和 CC 一致)。
   *
   * ★ 存的是**归一化之后**的 internalId,不是文件里的原文 —— 归一化发生在
   * 加载期(`agent/tool-alias.ts`),不是使用期。放到使用期做的话,
   * 一个认不出的名字要到子代理真的跑起来才暴露,而那时它已经带着
   * 一张空工具表在自信地编答案了。
   */
  tools?: string[]
  /** ModelAlias.alias。省略 = 继承父 run 的模型。 */
  model?: string
  /**
   * 钉死给哪一家发。省略 = 只认别名,由路由器按优先级择优(和 CC 的文件一样)。
   *
   * ★ 同一个别名可以挂在好几家上,而那几家**计费不同** —— 只写别名的话,
   * 用户以为自己选的是订阅那条线,实际可能走了按量那条。所以这个键存在,
   * 但它是**可选**的:从 CC 粘过来的文件没有它,那条路径一个字不能变。
   */
  modelProviderId?: string
  /**
   * 思考档位。省略 = 跟随「子代理思考深度」那一栏,再退回父 run 这一轮的档位。
   *
   * ★ 和 `model` **不是**同一个决定:换一个便宜模型跑量的子代理,照样可能
   *   需要在难题上想深一点。三档来源与归一化的理由都在 `shared/domain/subagent-thinking.ts`。
   */
  thinking?: ThinkingLevel
  /**
   * 子代理自己的权限档位。★ 最终档位是 `min(父档位, 这个)` ——
   * 它只能**收窄**,永远不能放宽(见 `minPermission` 的注释)。
   */
  permissionMode?: PermissionMode
  /**
   * 列表上那个小圆点。**纯装饰**,不进提示词、不影响运行。
   *
   * ★ 认不出的值当作没写,**不作废整条** —— 这一点和 `permissionMode` 正好相反,
   * 那个是安全字段(读不懂它就按父代理的档位跑,和作者的本意相反),这个读错了
   * 最坏结果是少一个圆点。因为一条坏颜色而让用户的子代理消失,代价完全不成比例。
   */
  color?: AgentColor
  source: { kind: AgentSourceKind; path: string }
}

/**
 * 颜色标记的取值 —— 和 Claude Code 的 `color:` 同一组词,同一个理由:
 * 用户会把 CC 的 agent 文件原样粘过来,认得出它写的那个词才不会被编辑一次就抹掉。
 */
export const AGENT_COLORS = [
  'yellow',
  'red',
  'orange',
  'green',
  'cyan',
  'blue',
  'purple',
  'pink'
] as const

export type AgentColor = (typeof AGENT_COLORS)[number]

export function isAgentColor(value: string): value is AgentColor {
  return (AGENT_COLORS as readonly string[]).includes(value)
}

/** 固定色值同时用于扩展列表和运行中的子代理卡片。 */
export const AGENT_COLOR_HEX: Record<AgentColor, string> = {
  yellow: '#e0a300',
  red: '#e5484d',
  orange: '#f76b15',
  green: '#30a46c',
  cyan: '#00a2c7',
  blue: '#3e63dd',
  purple: '#8e4ec6',
  pink: '#d6409f'
}

export function agentColorHex(color: string | undefined): string | undefined {
  return color !== undefined && isAgentColor(color) ? AGENT_COLOR_HEX[color] : undefined
}

/**
 * 表单里能勾的工具。
 *
 * ★★ 这张表的每一项都**必须**被 `main/kernel/agent/tool-alias.ts` 的
 * `normalizeToolName` 原样认出 —— 有一条测试守着这件事。漂了的话表单会勾出一个
 * 加载器不认识的名字,而 `load.ts` 的 `resolveTools` 在「一个都认不出」时是把
 * **整条子代理作废**的:用户存完之后会发现它不见了,界面上什么也没说。
 *
 * ★ 不含 `echo`(测试工具)。`Task` 在里面 —— 子代理再派子代理是 CC 就允许的,
 * 深度由 `Task` 工具自己的上限管,不在这一层拦。
 */
export const AGENT_TOOL_CHOICES = [
  'Read',
  'Write',
  'Edit',
  'Bash',
  'Glob',
  'Grep',
  'LS',
  'WebFetch',
  'web_search',
  'TodoWrite',
  'Task',
  'Skill'
] as const

/**
 * 「AI 生成」一颗按钮的产物 —— 一份**还没落盘**的子代理草稿。
 *
 * ★ 它不是 `AgentDefinition`:那个带 `source`(文件在哪儿),而草稿的全部意义
 * 就是还没有文件。字段也刻意只有表单填得进去的那几个 —— 权限档位不生成,
 * 那是安全决定,该由人来做。
 */
export interface AgentDraft {
  name: string
  description: string
  prompt: string
  /** 省略 = 继承全部工具。★ 模型被要求「能省则省」,见 `main/agent-draft.ts` 的提示词。 */
  tools?: string[]
  color?: AgentColor
}

/** 和 `SKILL_NAME_RE` 同一个形状,同一个理由:名字会进提示词、会被模型原样当入参回传。 */
export const AGENT_NAME_RE = /^[a-z0-9][a-z0-9-]{0,63}$/

/**
 * 描述的上限。★ 比 Skill 的 1024 小一半:每条子代理的描述都进
 * **`Task` 工具的 description**,而工具定义每一轮都重发。
 */
export const AGENT_DESCRIPTION_MAX = 512

/** 角色提示词的上限。它每轮进系统提示词,和 Skill 正文不同 —— 后者只出现一次。 */
export const AGENT_PROMPT_MAX = 16 * 1024
