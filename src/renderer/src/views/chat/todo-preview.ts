import type { TaskChecklistItem } from './TaskChecklist'

/**
 * `TodoWrite` 的入参 → 任务清单能直接渲染的数据。
 *
 * 需求:工具卡片展开后那份清单要和**输入框上方那张 `TaskChecklist` 长成同一件东西**,
 * 而不是另画一份 ○▸✓ 的纯文本列表 —— 同一份数据两套画法,改一处必漏一处,
 * 而两者在同一屏里上下相邻,漏掉的那个一眼就能看出来。
 *
 * (卡片那条路现在画的是「这次更新改了什么 + 可展开的完整清单」,见
 * `TodoWriteChecklist.tsx`;但**行**仍然是 `TaskChecklistRows` 那一个实现,
 * 上面那条不变式照旧成立。)
 *
 * ★ 入参**可能是半截的**:流式中每一帧都可能多出半个字段,甚至多出半个条目
 * (见 `partial-json.ts`)。所以这里只做收窄,不做校验:
 * - 不是对象、`content` 还没到 → 丢掉这一条(画出来是一行空白,看着像渲染坏了);
 * - `status` 不是三档之一 → 按 `pending` 算,与内核 schema 的默认值一致;
 * - `activeForm` 没到 → 退回 `content`。清单在「进行中」那一行显示的正是 activeForm,
 *   缺了它那一行会变成空的,而那一行恰恰是用户最盯着看的。
 *
 * 纯函数、不碰 React:这些分支只在「模型正写到一半」的那几帧成立,
 * 留在组件里就只能靠盯屏幕验(AGENTS.md §9)。
 */

const STATUSES = new Set(['pending', 'in_progress', 'completed'])

function str(input: unknown, key: string): string {
  if (typeof input !== 'object' || input === null) return ''
  const value = (input as Record<string, unknown>)[key]
  return typeof value === 'string' ? value : ''
}

export function previewTodos(input: unknown): TaskChecklistItem[] {
  if (typeof input !== 'object' || input === null) return []
  const raw = (input as Record<string, unknown>)['todos']
  if (!Array.isArray(raw)) return []
  const todos: TaskChecklistItem[] = []
  for (const item of raw) {
    const content = str(item, 'content')
    if (content === '') continue
    const status = str(item, 'status')
    const activeForm = str(item, 'activeForm')
    todos.push({
      content,
      activeForm: activeForm === '' ? content : activeForm,
      status: STATUSES.has(status) ? (status as TaskChecklistItem['status']) : 'pending'
    })
  }
  return todos
}
