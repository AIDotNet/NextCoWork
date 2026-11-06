/**
 * 「这次压缩用哪个模型、用哪个思考档位」—— 三档来源的唯一落点。
 *
 * 需求:上下文压缩原先只能用会话模型,而压缩是一次长输入、短输出的机械活,
 * 用户有理由把它交给另一个更便宜/更稳的模型;同时 `gpt-6-*` 这类关不掉推理的模型
 * 让原先硬编码的「压缩时不思考」当场失败(见 `domain/model-runtime.ts` 的
 * `auxiliaryThinkingLevel`),所以档位也必须可配。
 *
 * ★★ 这个模块存在的理由和 `model-selection.ts` 完全一样:**三档来源必须由一段代码
 * 算出来**。设置页显示的那一档和请求真正发出去的那一档分头算的话,两边只是碰巧一致,
 * 一旦不一致就是「设置里写着 A、账单上记着 B」,而且零报错。
 *
 * 不变式:
 * - 别名和 providerId **成对**取用,绝不拼出「A 家的别名 + B 家的锁」
 *   (那条规则的完整理由在 `model-selection.ts` 的 `subagentModelSelection` 上)。
 * - 三档优先级:工作区 > 全局设置 > 会话模型。工作区那一档能**显式**退回会话模型
 *   (`compactModel === null`),否则一旦全局配了压缩模型,单个工作区就再也回不去。
 *
 * 故意不做的:**不查这条绑定当前还存不存在**。那要路由器,属于主进程
 * (`main/kernel/compaction/binding.ts` 负责回落与日志)。这里保持纯函数,
 * 于是三档规则能在不启动 Electron 的单测里跑。
 *
 * 为什么不复用 `subagentModelSelection`:它的三档是「子代理文件 > 设置 > 父 run」,
 * 没有「显式跟随」这一态(`null`),硬套会把 `null` 读成「没配」而静默用上全局那一档 ——
 * 正好是工作区这一栏要解决的事。两个函数各自十行,合并只会多一个布尔参数。
 */
import type { ThinkingLevel } from '../agent/run-request'
import { INHERIT_THINKING, type SubagentThinking } from './subagent-thinking'

/**
 * 工作区那一层对压缩的覆盖。三个字段全可选 —— 旧库里的工作区 JSON 是裸 `JSON.parse`
 * (`db/repo.ts` 的 `getWorkspace`),声明成必填会让类型在运行时说谎。
 */
export interface WorkspaceCompactionOverride {
  /**
   * - 缺席(`undefined`)或空串 = 跟随全局设置
   * - `null` = **显式**跟随会话模型(用来反盖全局配的压缩模型)
   * - 非空字符串 = 用这个别名
   *
   * ★ 缺席和空串同义:工作区设置是浅合并的,「改回跟随全局」只能写一个空串
   *   (理由写在 `workspace.ts` 的同名字段上)。
   */
  compactModel?: string | null
  /** 与 `compactModel` 成对;`compactModel` 不是非空字符串时这一项无意义。 */
  compactModelProviderId?: string
  /** 缺席或 `null` = 跟随全局设置;`'inherit'` = 跟随会话本轮档位;其余 = 显式档位。 */
  compactThinking?: SubagentThinking | null
}

export interface CompactModelChoice {
  model: string
  modelProviderId: string | undefined
  /** true = 这一档就是会话模型(没配过,或显式选了「跟随会话模型」)。 */
  followsSession: boolean
}

/**
 * 三档来源,越具体越优先:工作区 > 全局设置 > 会话模型。
 *
 * @param workspace 工作区覆盖。见 `WorkspaceCompactionOverride` 的三态。
 * @param configured 全局设置那一对。空别名(含全是空白)= 这一栏是「跟随会话模型」。
 * @param session 这条会话的模型 —— 兜底,也是 `followsSession` 为真时的取值。
 */
export function compactModelSelection(
  workspace: WorkspaceCompactionOverride | undefined,
  configured: { model: string; modelProviderId?: string },
  session: { model: string; modelProviderId?: string }
): CompactModelChoice {
  const follow = (): CompactModelChoice => ({
    model: session.model,
    modelProviderId: session.modelProviderId,
    followsSession: true
  })
  const override = workspace?.compactModel
  // ★ `null` 和 `undefined` 在这里是**两个不同的答案**,所以不能写 `?? ` 或真值判断:
  //   null = 用户在这个工作区明确说了「用会话模型」,undefined = 他没表过态。
  if (override === null) return follow()
  if (typeof override === 'string' && override.trim() !== '') {
    return { model: override, modelProviderId: workspace?.compactModelProviderId, followsSession: false }
  }
  if (configured.model.trim() !== '') {
    return { model: configured.model, modelProviderId: configured.modelProviderId, followsSession: false }
  }
  return follow()
}

/**
 * 三档来源的档位。返回的是**尚未按压缩模型归一化**的原始意图 —— 调用方拿到之后
 * 必须再过一次 `auxiliaryThinkingLevel`(归一化要模型声明,纯函数够不着路由器)。
 *
 * @param workspace 工作区那一栏;缺席或 `null` = 没配(`null` 是「改回跟随全局」写进来的值)。
 * @param configured 全局设置那一栏,出厂是 `'inherit'`。
 * @param sessionLevel `'inherit'` 落到这个值:自动压缩 = 本轮 run 的档位,
 *   手动 /compact = `Session.thinking`(那时没有 run)。
 */
export function compactThinkingSelection(
  workspace: SubagentThinking | null | undefined,
  configured: SubagentThinking,
  sessionLevel: ThinkingLevel
): ThinkingLevel {
  const picked = workspace ?? configured
  return picked === INHERIT_THINKING ? sessionLevel : picked
}
