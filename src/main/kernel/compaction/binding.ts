/**
 * 「这一次压缩发给谁、用什么参数」—— 自动压缩与手动 /compact 共用的解析与回落。
 *
 * 需求:压缩模型和思考档位可以配置之后,有四件事必须在**一个地方**决定,
 * 否则两条路径(`AgentSession.compact` 与 `ipc/context.ts` 的 /compact)会缓慢地分家:
 *
 * 1. 三档来源怎么取(工作区 > 全局 > 会话模型),见 `shared/domain/compaction-model.ts`;
 * 2. 配置的那个模型**当前解析不到**时怎么办 —— 回落会话模型,warn 一行,不判失败;
 * 3. 档位怎么按压缩模型归一化(`auxiliaryThinkingLevel`,关不掉推理的模型降到最低档);
 * 4. 输出额度和预裁窗口按**压缩模型**算,不是会话模型。
 *
 * ★ 第 2 条是这个模块最值钱的一行。用户配过的供应商会被删、别名会被改名,
 *   而那两件事和「此刻要不要压缩」毫无关系 —— 让一条过期配置把压缩打死,
 *   表现就是上下文一路涨到上游报超长(正是重写前那次事故的形状)。
 *   所以这里静默回落、只留日志,并把 `fellBack` 记进边界让界面能说出来。
 *
 * 不变式:模型和 providerId **成对**取用(理由在 `domain/model-selection.ts`)。
 * 这个模块不发请求、不碰数据库 —— 它只认一个 `resolveModel` 端口,所以能单测。
 */
import { effectiveContextWindow } from '../../../shared/agent/context-management'
import { resolveMaxOutputTokens, type ThinkingLevel } from '../../../shared/agent/run-request'
import {
  compactModelSelection,
  compactThinkingSelection,
  type WorkspaceCompactionOverride
} from '../../../shared/domain/compaction-model'
import { auxiliaryThinkingLevel } from '../../../shared/domain/model-runtime'
import type { ModelAlias } from '../../../shared/domain/provider'
import { INHERIT_THINKING, type SubagentThinking } from '../../../shared/domain/subagent-thinking'

/** 全局设置里那两栏。缺省 = 纯内核测试没给设置,按「跟随会话模型 / 跟随会话档位」。 */
export interface CompactionSettings {
  model: string
  modelProviderId?: string
  thinking: SubagentThinking
}

export interface CompactBindingInput {
  /** 只要这一个能力 —— 同 `SessionUpstream` 的取向:注入端口,不 import 路由器。 */
  resolveModel: (model: string, modelProviderId?: string) => ModelAlias | undefined
  settings?: CompactionSettings
  workspace?: WorkspaceCompactionOverride
  /** 会话这一档:模型那一对 + 这一刻的思考档位(自动压缩给本轮 run 的,手动给 `Session.thinking`)。 */
  session: { model: string; modelProviderId?: string; thinking: ThinkingLevel }
  /** 全局「最大输出 Token」设置项;缺省时 `resolveMaxOutputTokens` 会落到出厂值。 */
  maxOutputTokens?: number
  warn?: (message: string) => void
}

export interface CompactBinding {
  alias: ModelAlias
  model: string
  modelProviderId: string | undefined
  /** 已按压缩模型归一化过的档位 —— 直接可以下发。 */
  thinking: ThinkingLevel
  maxOutputTokens: number
  /** 压缩模型的协议窗口,给摘要请求的输入预裁当分母。 */
  protocolWindow: number
  fellBack: boolean
}

/**
 * 解析这次压缩的绑定。返回 `undefined` = 连会话模型都解析不到
 * （供应商全删了 / 别名没了）—— 调用方照旧走失败路径,行为和这次改动之前一致。
 */
export function resolveCompactBinding(input: CompactBindingInput): CompactBinding | undefined {
  const configured = input.settings ?? { model: '', thinking: INHERIT_THINKING }
  const choice = compactModelSelection(
    input.workspace,
    { model: configured.model, ...(configured.modelProviderId === undefined ? {} : { modelProviderId: configured.modelProviderId }) },
    { model: input.session.model, ...(input.session.modelProviderId === undefined ? {} : { modelProviderId: input.session.modelProviderId }) }
  )

  let model = choice.model
  let modelProviderId = choice.modelProviderId
  let fellBack = false
  let alias = input.resolveModel(model, modelProviderId)
  if (alias === undefined && !choice.followsSession) {
    /*
      ★ 回落时模型和 providerId **一起**换回会话那一对。只换别名的话会拼出
      「会话的别名 + 压缩模型那家的锁」,候选集为空,然后报一条指着一家
      跟这次压缩毫无关系的供应商的错 —— 用户无从排查。
    */
    input.warn?.(`[compact] 配置的压缩模型不可用,已回落到会话模型: ${model}`)
    model = input.session.model
    modelProviderId = input.session.modelProviderId
    fellBack = true
    alias = input.resolveModel(model, modelProviderId)
  }
  if (alias === undefined) return undefined

  const wanted = compactThinkingSelection(input.workspace?.compactThinking, configured.thinking, input.session.thinking)
  return {
    alias,
    model,
    modelProviderId,
    // ★ 必须归一化:`gpt-6-*` 这类 effort 模型关不掉推理,直接下发 'off' 会被
    //   thinking-adapter 拒成不可重试的错 —— 压缩每次必败、三次熔断。
    thinking: auxiliaryThinkingLevel(wanted, alias),
    maxOutputTokens: resolveMaxOutputTokens(input.maxOutputTokens, alias.contextWindow),
    // 协议窗口:摘要请求只受压缩模型自己的真实上限约束,和「最大上下文」计费开关无关。
    protocolWindow: effectiveContextWindow(alias.contextWindow, true),
    fellBack
  }
}
