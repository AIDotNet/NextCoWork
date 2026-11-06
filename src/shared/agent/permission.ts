/**
 * 权限档位 —— 方案 §4.5,对应界面输入框左下角那个「完全访问 ⌄」下拉。
 * 界面原文:「AI 操作如何审批?更改会在下一次新回复生效」。
 *
 * 这里只有**策略类型**;机制(那张待决表)在 interaction.ts。两者分开是有意的:
 * 策略回答「要不要问」,机制回答「怎么问、问的过程中崩了怎么办」。
 */

export type PermissionMode =
  /** 询问批准:总是询问是否允许工具操作 */
  | 'ask'
  /** 为我批准:仅对潜在不安全操作询问 */
  | 'auto'
  /** 完全访问:不受限制地操作电脑文件,**联网仍受开关控制** */
  | 'full'

export const PERMISSION_MODES: readonly PermissionMode[] = ['ask', 'auto', 'full']

export const PERMISSION_MODE_LABEL: Record<PermissionMode, string> = {
  ask: '询问批准',
  auto: '为我批准',
  full: '完全访问'
}

export const PERMISSION_MODE_HINT: Record<PermissionMode, string> = {
  ask: '总是询问是否允许工具操作',
  auto: '仅对潜在不安全操作询问',
  full: '不受限制地操作电脑文件'
}

/**
 * ★ 决策类型现在就得是 union,不能是 `approved: boolean`。
 * 「以后都允许」和「让我改一下这条命令再执行」是用户一周内必然提的两个需求,
 * 而它们都会改 IPC 签名(方案 §4.5)。
 */
export type PermissionDecision =
  | { kind: 'allow_once' }
  /**
   * 「以后都允许」。`workspace` 落成 `.next-cowork/settings.local.json` 里的一条
   * `permissions.allow` 规则(规则语法见 `permission-rule.ts`);
   * `session` 还没有存放处,`interaction-gate.ts` 目前不收它。
   *
   * ★ 这里**不带规则文本**:规则由主进程按被调工具和入参算出来,
   * 渲染层只负责把它显示给用户看。否则这条决策就是一个「往权限文件里写任意一行」的接口。
   */
  | { kind: 'allow_always'; scope: 'session' | 'workspace' }
  | { kind: 'deny'; reason?: string }
  /** v1 只定义,不实现 */
  | { kind: 'allow_edited'; input: unknown }

/**
 * PermissionGate.evaluate() 的入参。本体就是一张 5 行表,
 * 不做规则 DSL、不做 glob 匹配(方案 §10)。
 */
export interface PermissionQuery {
  mode: PermissionMode
  readOnly: boolean
  destructive: boolean
  /**
   * 受「联网搜索」开关管的工具:即使 full 档,webSearch=false 时也一律拒绝。
   * ★ 调用方按 `permission-gate.ts` 的 `NETWORK_SWITCH_TOOLS` 名单填(目前只有
   * WebFetch / web_search),**不是** `ToolInfo.needsNetwork` —— 字段名沿用旧名,
   * 语义已随开关收窄(见那张名单的注释)。
   */
  needsNetwork?: boolean
  webSearch: boolean
}

/**
 * 子代理不继承 `full`:档位取 min(父档位, 子代理配置档位)(方案 §4.9)。
 * 否则一个被投毒的 MCP 工具描述可以诱导主 agent 派一个子 agent 去做它自己
 * 不被允许做的事 —— 这是**真实的提权路径**,不是理论风险。
 */
const RANK: Record<PermissionMode, number> = { ask: 0, auto: 1, full: 2 }

export function minPermission(a: PermissionMode, b: PermissionMode): PermissionMode {
  return RANK[a] <= RANK[b] ? a : b
}
