/**
 * 子代理这一轮用哪个思考档位。
 *
 * 和 `model-selection.ts` 的 `subagentModelSelection` 是**一对**,形状也一样:
 * 三档来源、越具体的越优先,而且必须由同一段代码算出来 —— 界面显示的档位和
 * 真正下发的档位分头算的话,两边只是**碰巧**一致,一旦不一致就是「设置里写着高、
 * 请求里发的是中」,且零报错。
 *
 * 三档来源:
 *
 * 1. `declared` —— `agents/<name>.md` frontmatter 里的 `thinking:`。它是对某一个
 *    子代理的明确安排(比如「这个审查代理要深想」),盖过任何全局默认。
 * 2. `configured` —— 设置 › 通用 › Agent 的「子代理思考深度」。★ 它排在父 run
 *    前面才是这一栏存在的理由:子代理是拿来跑量的,用户配它就是为了让那几路
 *    别跟着主力模型这一轮的选择走。排在后面的话,只有「主力模型也没表过态」
 *    时才轮得到它 —— 也就是几乎永远不生效。
 * 3. `parent` —— 都没配时沿用父 run 这一轮的档位。
 *
 * ★ 取出来的档位**还要按子 run 自己的模型归一化一次**(`normalizeModelThinkingLevel`,
 * 调用点在 `main/runtime.ts` 的 `childThinkingFor`):档位是模型的属性,而子 run
 * 的模型未必和父 run 是同一个。这一步不在这里做,因为它是纯函数,够不着路由器。
 */
import { THINKING_LEVELS, type ThinkingLevel } from '../agent/run-request'

/**
 * 「跟随本轮对话」—— 子 run 沿用父 run 这一轮的档位。
 *
 * ★ 为什么必须是一个**独立**的取值,而不是复用 `'auto'`:用户把主力模型调到「高」
 * 之后,子代理该跟着高;而 `'auto'` 已经是「模型自己的默认档」这个**另一个意思**。
 * 两个含义挤在同一个值上,就没有任何一处能表达「跟着父 run 走」了 —— 而那恰好是
 * 引入这一栏之前的行为,必须逐字保留。
 */
export const INHERIT_THINKING = 'inherit'

export type SubagentThinking = ThinkingLevel | typeof INHERIT_THINKING

/**
 * 设置页那一栏与子代理编辑器那张下拉的取值表。
 *
 * `inherit` 排在最前:它是**缺省值**,而缺省项排在列表中间会让「我没动过它」
 * 这件事在界面上读不出来。
 */
export const SUBAGENT_THINKING_CHOICES: readonly SubagentThinking[] = [
  INHERIT_THINKING,
  ...THINKING_LEVELS
]

/** 只认枚举。设置里那一栏和导入进来的备份都用它把关,坏值不许落库。 */
export function isSubagentThinking(value: unknown): value is SubagentThinking {
  return (
    value === INHERIT_THINKING ||
    (typeof value === 'string' && (THINKING_LEVELS as readonly string[]).includes(value))
  )
}

/**
 * 三档来源的落地。`declared` 缺省 = 文件里没写这一行(不是「跟随」,「跟随」由
 * `configured` 那一档表达)。
 *
 * @param declared frontmatter 的 `thinking:`。★ 认不出的值在加载期就被丢掉了
 *   (见 `agent/load.ts`),所以这里拿到的要么是合法档位、要么是 `undefined`。
 * @param configured 设置里那一栏。`'inherit'` = 这一栏是「跟随本轮对话」。
 * @param parent 父 run 这一轮的档位。
 */
export function subagentThinkingSelection(
  declared: ThinkingLevel | undefined,
  configured: SubagentThinking,
  parent: ThinkingLevel
): ThinkingLevel {
  if (declared !== undefined) return declared
  if (configured !== INHERIT_THINKING) return configured
  return parent
}
