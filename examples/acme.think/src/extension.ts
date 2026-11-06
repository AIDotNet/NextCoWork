/**
 * 需求:给 Agent 一个只做一件事的「深度思考」工具 —— 无副作用、零权限,
 * 唯一产物是模型自己写下的分析。价值全在写入侧:思考随 tool_use 进转录,
 * 成为可回看、可被后续轮次引用的显式推理;本工具不读文件、不联网、不落状态。
 *
 * ★ 提示词工程集中在三处,改任何一处都要把三处连起来读:
 *   1. `DESCRIPTION` —— 进系统提示词,教模型「何时调、按什么标准想」;
 *   2. `thinkSchema` 的字段描述 —— 进模型上下文,把「从用户的整体任务出发」
 *      钉成必填字段(task_goal),而不是靠模型自觉;
 *   3. `ACK`(工具返回文本)—— 在模型刚想完、正要动手的决策点上,
 *      用一行字把「对齐整体目标」再敲一次。
 * ★ 宿主不校验 inputSchema(原样下发给上游、原样收回),invoke 自行收窄。
 * ★ 返回的 card 只走 UI 轨:模型永远看不到卡片,用户看到的是渲染后的思考。
 */
import * as ncw from 'nextcowork'

/**
 * 模型可见的工具描述,经宿主 `sanitizeDescription` 后进系统提示词
 * (上限 4096 字符,这里约 2.3K,留有余量;控制字符由宿主剥)。
 *
 * 刻意用英文:它和本应用全部内置/插件工具的描述同一语言,模型遵循英文
 * 工具描述的稳定性最好;用户可见文案全部走 l10n(见 l10n/*.json)。
 */
const DESCRIPTION = `Structured deep-thinking space with zero side effects: it reads nothing, writes nothing, and returns no new information — its entire value is what you write into it. Writing the analysis down forces explicit, complete reasoning instead of jumping to the first plausible action.

WHEN to call it:
- Before starting non-trivial work: multiple steps or files, a design choice, or a change whose failure would be costly to undo.
- When the request is ambiguous, incomplete, or appears to conflict with earlier instructions or existing code.
- Before choosing among plausible approaches, data structures, APIs, or fix strategies.
- The moment you notice you are about to act on an assumption you have not verified.

HOW to think — the quality bar this tool exists to enforce:
1. Start from the user's OVERALL task, never from the fragment currently in front of you. Restate that overall goal in your own words in task_goal, and keep every later conclusion anchored to it. A technically correct answer to the wrong goal is still a failure, and most wasted work comes from solving a sub-problem while losing the original intent.
2. Parse the request completely before reasoning about solutions: every explicit requirement, constraint and preference; then the implicit ones — environment, existing conventions, surrounding code, backwards compatibility, and what the user clearly expects but did not say.
3. Sweat the details, and name them concretely in details: edge cases, empty/null/zero states, boundaries and off-by-one, error paths, concurrency and ordering, units, encoding, i18n. "Consider edge cases" is not thinking; "the list can be empty and the UI must show an empty state" is.
4. Surface assumptions in risks. Verify the checkable ones before relying on them; explicitly label the rest so a later reader can challenge them.
5. When several approaches are plausible, compare them against what the user's task actually needs — simplicity, performance, safety, or room to change — and say in one line why the chosen one wins.
6. Define what done looks like: the observable outcome the user expects, and how it will be verified.

Keep depth proportional: a one-line question deserves a sentence or two of thought; an architecture change deserves the full analysis. Never invent requirements while thinking — if critical information is missing, ask the user instead of guessing.`

/**
 * JSON Schema 原样下发给上游。字段本身就是提示词:task_goal 必填是本工具的
 * 主锚点 —— 强迫每次思考先回到用户的整体任务,再进入当前片段。
 * additionalProperties:false + 宿主不校验 = 多余字段模型不该发,发了我们也忽略。
 */
const thinkSchema = {
  type: 'object',
  additionalProperties: false,
  required: ['task_goal', 'thought'],
  properties: {
    task_goal: {
      type: 'string',
      description:
        "The user's overall goal for the current task, restated in your own words. Every conclusion must serve this goal, not just the current sub-problem."
    },
    thought: {
      type: 'string',
      description:
        'The deep thinking itself: requirement analysis, the details and constraints noticed, assumptions, approach comparison against the overall goal, and the conclusion reached. Complete sentences, not bullet fragments.'
    },
    details: {
      type: 'array',
      items: { type: 'string' },
      description:
        'Specific details, constraints or edge cases that must not be lost — one concrete item per entry, e.g. "empty input renders an empty state, not an error".'
    },
    risks: {
      type: 'string',
      description:
        'Assumptions made, what could be misread, and what breaks if an assumption turns out wrong.'
    },
    next_step: {
      type: 'string',
      description:
        'The immediate action this analysis justifies, phrased so it can be checked against the overall goal before execution.'
    }
  }
}

/**
 * 防御性上限:模型输出本身有界,这里拦的是「失控循环把 64KB 工具输出配额
 * 灌满」这类最坏情况。返回文本是固定 ACK,天然有界;真正要裁的只有卡片。
 */
const MAX_THOUGHT_CHARS = 32_000
const MAX_GOAL_CHARS = 2_000
const MAX_RISKS_CHARS = 4_000
const MAX_STEP_CHARS = 2_000
const MAX_DETAIL_ITEMS = 20
const MAX_DETAIL_CHARS = 300
/** 与宿主卡片消毒器的单字段上限(4KB)对齐 —— 自己先裁,别让超限把整卡丢掉。 */
const MAX_CARD_MARKDOWN = 4_000

/**
 * 返回给模型的确认语,刻意保持一行。
 *
 * 需求:结果文本会进转录被模型读到,这正是最后一次提示的落点 —— 在
 * 「刚想完、正要动手」的决策点上把「对齐整体目标」再敲一次,与
 * DESCRIPTION 第 1 条首尾呼应。不发新信息(工具本就不产信息),只对齐。
 */
const ACK =
  'Thought recorded. Your analysis is now part of the transcript. ' +
  'Proceed with the step it justifies, and re-check it against the user\'s overall goal before acting.'

function str(value: unknown, max: number): string {
  return typeof value === 'string' ? value.slice(0, max).trim() : ''
}

function detailList(value: unknown): string[] {
  if (!Array.isArray(value)) return []
  return value
    .filter((item): item is string => typeof item === 'string')
    .slice(0, MAX_DETAIL_ITEMS)
    .map((item) => item.slice(0, MAX_DETAIL_CHARS).trim())
    .filter((item) => item !== '')
}

interface CardLabels {
  goal: string
  details: string
  risks: string
  next: string
}

/**
 * 卡片是用户可见文案,跟随宿主语言;拿不到就退英文。
 * ★ 卡片是锦上添花:一次 RPC 失败不能挂掉工具本体,所以 try 住整个调用。
 */
async function cardLabels(): Promise<CardLabels> {
  try {
    const info = await ncw.env.appInfo()
    if (info.language.startsWith('zh')) {
      return { goal: '目标', details: '关键细节', risks: '风险与假设', next: '下一步' }
    }
  } catch {
    // 宿主语言不可得 → 退英文标签,工具照常返回。
  }
  return { goal: 'Goal', details: 'Key details', risks: 'Risks & assumptions', next: 'Next step' }
}

function buildCardMarkdown(
  labels: CardLabels,
  goal: string,
  thought: string,
  details: string[],
  risks: string,
  nextStep: string
): string {
  const parts: string[] = []
  if (goal !== '') parts.push(`**${labels.goal}** ${goal}`)
  parts.push(thought)
  if (details.length > 0) parts.push(`**${labels.details}**\n${details.map((d) => `- ${d}`).join('\n')}`)
  if (risks !== '') parts.push(`**${labels.risks}** ${risks}`)
  if (nextStep !== '') parts.push(`**${labels.next}** ${nextStep}`)
  const joined = parts.join('\n\n')
  return joined.length > MAX_CARD_MARKDOWN ? `${joined.slice(0, MAX_CARD_MARKDOWN)}\n…` : joined
}

export function activate(context: ncw.ExtensionContext): void {
  context.subscriptions.push(
    ncw.tools.registerTool<Record<string, unknown>>('think', {
      description: DESCRIPTION,
      inputSchema: thinkSchema,
      // 诚实标注:纯读写自己的入参,不碰任何外部世界 —— 审批 UX 据此放行。
      readOnly: true,
      destructive: false,
      needsNetwork: false,
      async invoke({ input }) {
        // 宿主不校验 input,运行期可能是任何形状 —— 全部字段安全收窄。
        const raw: Record<string, unknown> = input !== null && typeof input === 'object' ? input : {}
        const thought = str(raw.thought, MAX_THOUGHT_CHARS)
        // 空思考直接失败(宿主转成工具失败文本):没有 thought 的 think 调用
        // 说明模型在凑数,与其回一条空洞确认,不如让它看到硬错误。
        if (thought === '') throw new Error('think: "thought" is required and must be non-empty')
        const goal = str(raw.task_goal, MAX_GOAL_CHARS)
        const details = detailList(raw.details)
        const risks = str(raw.risks, MAX_RISKS_CHARS)
        const nextStep = str(raw.next_step, MAX_STEP_CHARS)
        const markdown = buildCardMarkdown(await cardLabels(), goal, thought, details, risks, nextStep)
        return {
          content: [{ text: ACK }],
          card: { kind: 'declarative', blocks: [{ type: 'markdown', value: markdown }] }
        }
      }
    })
  )
}
