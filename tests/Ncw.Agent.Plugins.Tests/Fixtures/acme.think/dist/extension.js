// 移植自 2.x 的 examples/acme.think(去掉 TypeScript 类型后的 ESM 单文件,esbuild 输出风格):零权限、无副作用的 Agent 工具,卡片只走 UI 轨。
import * as ncw from 'nextcowork'

const DESCRIPTION = `Structured deep-thinking space with zero side effects: it reads nothing, writes nothing, and returns no new information — its entire value is what you write into it. Writing the analysis down forces explicit, complete reasoning instead of jumping to the first plausible action.

WHEN to call it:
- Before starting non-trivial work: multiple steps or files, a design choice, or a change whose failure would be costly to undo.
- When the request is ambiguous, incomplete, or appears to conflict with earlier instructions or existing code.
- Before choosing among plausible approaches, data structures, APIs, or fix strategies.
- The moment you notice you are about to act on an assumption you have not verified.

HOW to think: start from the user's OVERALL task, restate it in task_goal, name the details (edge cases, empty states, error paths) concretely, surface assumptions in risks, compare approaches, and define what done looks like. Keep depth proportional to the task.`

const thinkSchema = {
  type: 'object',
  additionalProperties: false,
  required: ['task_goal', 'thought'],
  properties: {
    task_goal: { type: 'string', description: "The user's overall goal for the current task, restated in your own words." },
    thought: { type: 'string', description: 'The deep thinking itself: requirement analysis, details, assumptions, approach comparison and the conclusion.' },
    details: { type: 'array', items: { type: 'string' }, description: 'Specific details or edge cases that must not be lost, one per entry.' },
    risks: { type: 'string', description: 'Assumptions made and what breaks if one turns out wrong.' },
    next_step: { type: 'string', description: 'The immediate action this analysis justifies.' }
  }
}

const MAX_THOUGHT_CHARS = 32000
const MAX_GOAL_CHARS = 2000
const MAX_DETAIL_ITEMS = 20
const MAX_DETAIL_CHARS = 300

const ACK = 'Thought recorded. Your analysis is now part of the transcript. Proceed with the step it justifies, and re-check it against the user\'s overall goal before acting.'

function str(value, max) {
  return typeof value === 'string' ? value.slice(0, max).trim() : ''
}

function detailList(value) {
  if (!Array.isArray(value)) return []
  return value.filter((item) => typeof item === 'string').slice(0, MAX_DETAIL_ITEMS).map((item) => item.slice(0, MAX_DETAIL_CHARS).trim()).filter((item) => item !== '')
}

export function activate(context) {
  context.subscriptions.push(
    ncw.tools.registerTool('think', {
      description: DESCRIPTION,
      inputSchema: thinkSchema,
      readOnly: true,
      async invoke({ input }) {
        const goal = str(input.task_goal, MAX_GOAL_CHARS)
        const thought = str(input.thought, MAX_THOUGHT_CHARS)
        if (goal === '' || thought === '') {
          return { content: [{ text: 'Both task_goal and thought are required.' }], isError: true }
        }
        const details = detailList(input.details)
        const blocks = [
          { type: 'keyValue', rows: [{ label: 'Goal', value: goal }] },
          { type: 'markdown', value: thought }
        ]
        if (details.length > 0) blocks.push({ type: 'list', items: details.map((label) => ({ label })) })
        const risks = str(input.risks, 4000)
        if (risks !== '') blocks.push({ type: 'text', value: risks })
        // 卡片只影响 UI,不下发给模型:模型永远只看到 ACK
        return { content: [{ text: ACK }], card: { kind: 'declarative', blocks } }
      }
    })
  )
}
