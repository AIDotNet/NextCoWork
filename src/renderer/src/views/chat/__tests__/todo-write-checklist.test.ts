/**
 * 消息里那张 `TodoWrite` 清单卡片的 DOM 契约。
 *
 * 需求:卡片要一眼说清「这次更新改了什么」,而完整清单收在折叠里 —— 所以这里钉的是
 * 三件事:**增量怎么算**(数据来自上下文里那份「上一份」)、**丢掉的项必须单独列出来**
 * (它们在完整清单里已经不存在了)、以及**没有上下文时不许编造增量**。
 *
 * @vitest-environment jsdom
 */
import { act, createElement } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { AgentMessage } from '../../../../../shared/agent/message'
import { assistantMessage, toolResultMessage, userMessage } from '../../../../../shared/agent/message'
import { I18nProvider } from '../../../i18n'
import { TodoHistoryProvider } from '../todo-history'
import { TodoWriteChecklist } from '../TodoWriteChecklist'

let teardown: (() => Promise<void>) | null = null

afterEach(async () => {
  await teardown?.()
  teardown = null
  vi.unstubAllGlobals()
})

const todo = (content: string, status: 'pending' | 'in_progress' | 'completed'): object => ({
  content,
  status,
  activeForm: `Doing ${content}`
})

let n = 0
/** 一次「模型发起调用」+「工具回执」—— 增量只看成功回执过的那些。 */
function call(todos: object[]): { callId: string; messages: AgentMessage[] } {
  const callId = `c${String(++n)}`
  return {
    callId,
    messages: [
      assistantMessage(`a${callId}`, [{ type: 'tool_call', callId, name: 'TodoWrite', input: { todos } }], 0),
      toolResultMessage(`r${callId}`, [{ type: 'tool_result', callId, output: { content: 'ok' }, isError: false }], 0)
    ]
  }
}

/** 挂一张卡片。`messages` 是**完整转录**(含这次调用自己),由 provider 提供上一份。 */
async function mountCard(props: {
  input: unknown
  callId?: string
  messages?: readonly AgentMessage[]
}): Promise<HTMLElement> {
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  const container = document.createElement('div')
  document.body.append(container)
  const root = createRoot(container)
  teardown = async () => {
    await act(async () => root.unmount())
    container.remove()
  }
  const card = createElement(TodoWriteChecklist, {
    callId: props.callId,
    input: props.input
  })
  await act(async () => root.render(createElement(I18nProvider, {
    initialLocale: 'en-US',
    children: props.messages === undefined
      ? card
      : createElement(TodoHistoryProvider, { messages: props.messages, children: card })
  })))
  return container
}

/**
 * 每行的**文字**。取第二个子元素而不是整行的 `textContent` —— 整行还带着序号徽标
 * 与 `sr-only` 的状态词(`1Read codeWaiting`),那样断言会被无关的展示细节绑住。
 */
function rowContents(container: HTMLElement): (string | null)[] {
  return [...container.querySelectorAll('[data-task-status]')].map(
    (row) => row.children[1]?.textContent ?? null
  )
}

describe('TodoWriteChecklist · 这次更新改了什么', () => {
  it('starts collapsed, reports the delta and the current counts, and keeps the full list one click away', async () => {
    const before = call([todo('Read code', 'pending'), todo('Write code', 'pending')])
    const current = call([todo('Read code', 'completed'), todo('Write code', 'in_progress'), todo('Run tests', 'pending')])
    const container = await mountCard({
      input: { todos: [todo('Read code', 'completed'), todo('Write code', 'in_progress'), todo('Run tests', 'pending')] },
      callId: current.callId,
      messages: [userMessage('u', [{ type: 'text', text: 'go' }], 0), ...before.messages, ...current.messages]
    })

    // 默认收起:完整清单在 DOM 里(行仍然可断言),但对屏读器与键盘都不可达
    const toggle = container.querySelector('[aria-expanded]')
    expect(toggle?.getAttribute('aria-expanded')).toBe('false')
    const list = document.getElementById(toggle?.getAttribute('aria-controls') ?? '')
    expect(list?.getAttribute('aria-hidden')).toBe('true')
    expect(list?.hasAttribute('inert')).toBe(true)
    expect(rowContents(container)).toEqual(['Read code', 'Write code', 'Run tests'])

    // 增量:一项刚完成、一项刚开始、一项新增;当前完成度单独一行
    expect(container.textContent).toContain('1 started · 1 completed · 1 added')
    expect(container.textContent).toContain('Task checklist · 1/3 completed')

    await act(async () => (toggle as HTMLButtonElement).click())
    expect(toggle?.getAttribute('aria-expanded')).toBe('true')
    expect(container.textContent).toContain('Hide the full list')
  })

  it('lists dropped items on their own — they are gone from the full list', async () => {
    const before = call([todo('Keep me', 'pending'), todo('Dropped silently', 'pending')])
    const current = call([todo('Keep me', 'pending')])
    const container = await mountCard({
      input: { todos: [todo('Keep me', 'pending')] },
      callId: current.callId,
      messages: [...before.messages, ...current.messages]
    })

    expect(container.textContent).toContain('1 removed')
    const dropped = container.querySelector('[data-testid="todo-dropped"]')
    expect(dropped?.textContent).toContain('Dropped silently')
    // 完整清单里它已经不存在了 —— 这正是必须单独列出来的理由
    expect(rowContents(container)).toEqual(['Keep me'])
  })

  it('marks only the rows this update actually changed', async () => {
    const before = call([todo('Untouched', 'pending'), todo('Moved', 'pending')])
    const current = call([todo('Untouched', 'pending'), todo('Moved', 'in_progress')])
    const container = await mountCard({
      input: { todos: [todo('Untouched', 'pending'), todo('Moved', 'in_progress')] },
      callId: current.callId,
      messages: [...before.messages, ...current.messages]
    })

    const rows = [...container.querySelectorAll('[data-task-status]')]
    expect(rows[0]?.className).not.toContain('bg-tint-hover')
    expect(rows[1]?.className).toContain('bg-tint-hover')
  })

  it('says so when nothing changed', async () => {
    const before = call([todo('Same', 'pending')])
    const current = call([todo('Same', 'pending')])
    const container = await mountCard({
      input: { todos: [todo('Same', 'pending')] },
      callId: current.callId,
      messages: [...before.messages, ...current.messages]
    })

    expect(container.textContent).toContain('No change to the list')
  })

  it('★ 没有上下文时说「不知道」,不说「无变化」—— 后者是一句没有依据的断言', async () => {
    const container = await mountCard({ input: { todos: [todo('Only one', 'pending')] } })

    expect(container.textContent).not.toContain('No change to the list')
    // 也不该把整份清单标成新增
    expect(container.textContent).not.toContain('added')
    // 标题行仍然成立:它报的是清单自己,不依赖任何历史
    expect(container.textContent).toContain('Task checklist · 0/1 completed')
  })

  it('renders half-streamed input without inventing rows', async () => {
    // 流式中的半截 JSON:收窄规则在 `todo-preview.ts`,这里只确认卡片不崩、不编内容
    const container = await mountCard({ input: '{"todos":[{"content":"Half' })

    expect(container.textContent).toContain('Task checklist · 0/0 completed')
    expect(rowContents(container)).toEqual([])
  })
})
