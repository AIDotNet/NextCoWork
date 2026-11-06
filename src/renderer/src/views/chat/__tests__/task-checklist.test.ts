/**
 * TaskChecklist 的 DOM 契约：真实 Todo 状态必须逐项保留，收起只改变展示，不能改写任务。
 *
 * 同一份转录数据会被两个位置渲染（输入框上方 / 工具卡片的历史快照），所以这里同时
 * 钉住 `execution` 三档的差别：只有 `running` 才允许出现 activeForm、Spinner 与高亮，
 * 而任务本身的 status 与完成百分比在任何一档下都不许被改写。
 *
 * 折叠的两档也在这里钉住：输入框上方那条**收起即小球**（`minimizeToBall`，点球一次到完全
 * 展开，不留标题行），传 `false` 的调用方（消息里那张卡片）收起只收列表、标题行留着。
 * `defaultCollapsed` 是挂载初值而不是受控值。（消息里那张 TodoWrite 卡片自带标题行与
 * 增量，用例在 `todo-write-checklist.test.ts`；它复用的行渲染仍由这个文件覆盖。）
 *
 * @vitest-environment jsdom
 */
import { act, createElement } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { I18nProvider } from '../../../i18n'
import { CARD_LEAVE_MS, TaskChecklist, type TaskChecklistItem, type TaskChecklistExecution as Execution } from '../TaskChecklist'

let teardown: (() => Promise<void>) | null = null

afterEach(async () => {
  await teardown?.()
  teardown = null
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

/**
 * 挂一份清单，并给出「换档重渲」的把手 —— 这一组用例要验的正是**同一份 todos**
 * 在两个档位之间的展示差，重渲必须走同一棵树（`root.render` 复用同一个实例）。
 */
async function mountChecklist(props: {
  todos: readonly TaskChecklistItem[]
  execution?: Execution
  defaultCollapsed?: boolean
  minimizeToBall?: boolean
}): Promise<{ container: HTMLElement; show: (next: { todos: readonly TaskChecklistItem[]; execution?: Execution }) => Promise<void> }> {
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  const container = document.createElement('div')
  document.body.append(container)
  const root = createRoot(container)
  const show = async (next: { todos: readonly TaskChecklistItem[]; execution?: Execution }): Promise<void> => {
    await act(async () => root.render(createElement(I18nProvider, {
      initialLocale: 'en-US',
      children: createElement(TaskChecklist, {
        todos: next.todos,
        ...(props.defaultCollapsed === undefined ? {} : { defaultCollapsed: props.defaultCollapsed }),
        ...(props.minimizeToBall === undefined ? {} : { minimizeToBall: props.minimizeToBall }),
        ...(next.execution === undefined ? {} : { execution: next.execution })
      })
    })))
  }
  teardown = async () => {
    await act(async () => root.unmount())
    container.remove()
  }
  await show(props)
  return { container, show }
}

const TODOS: readonly TaskChecklistItem[] = [
  { content: 'Inspect files', activeForm: 'Inspecting files', status: 'completed' },
  { content: 'Update UI', activeForm: 'Updating UI', status: 'in_progress' },
  { content: 'Run tests', activeForm: 'Running tests', status: 'pending' }
]

/** Spinner 只由这一个字形画出来（lucide 自己挂的类名），与动效档位无关。 */
function spinnerCount(container: HTMLElement): number {
  return container.querySelectorAll('.lucide-loader-circle').length
}

function rowStatuses(container: HTMLElement): (string | null)[] {
  return [...container.querySelectorAll('[data-task-status]')].map((row) => row.getAttribute('data-task-status'))
}

/** 列表容器的展开态由 `aria-hidden` 表态，比读 grid 行高稳。 */
function listExpanded(container: HTMLElement): boolean {
  const toggle = container.querySelector('[aria-controls]')
  const list = document.getElementById(toggle?.getAttribute('aria-controls') ?? '')
  return list?.getAttribute('aria-hidden') === 'false'
}

/*
  收起 = 一颗小球，展开 = 完整清单，没有中间态。原先收起后先留一行标题行、过 15 秒
  没人碰才缩成球（`useIdleMinimize.ts`），现在「收起」这一下就是用户自己按的，按下就缩。
  交接的那 `CARD_LEAVE_MS` 是过渡动画本身(四段动画的分工写在 `theme.css`)。
*/
describe('TaskChecklist · collapse turns into the ball', () => {
  const ball = (container: HTMLElement): HTMLButtonElement | null =>
    container.querySelector<HTMLButtonElement>('[data-testid="task-checklist-ball"]')

  it('mounts collapsed as a ball and opens the whole list on one click', async () => {
    const { container } = await mountChecklist({ todos: TODOS, execution: 'running' })

    // 收起来的那一刻就没有标题行：整张卡片被小球换掉
    expect(ball(container)).not.toBeNull()
    expect(container.querySelector('[aria-controls]')).toBeNull()
    // 缩起来只改怎么画:进度数仍在球上,完整报数进无障碍标签
    expect(ball(container)?.getAttribute('aria-label')).toBe('Show task checklist (1/3 completed)')

    await act(async () => ball(container)?.click())
    const toggle = container.querySelector<HTMLButtonElement>('[aria-controls]')
    expect(ball(container)).toBeNull()
    // 点球 = 「我现在要看这份清单」,所以一次点击就到完全展开,不用再点一次标题行
    expect(toggle?.getAttribute('aria-expanded')).toBe('true')
    expect(listExpanded(container)).toBe(true)
    expect(rowStatuses(container)).toEqual(['completed', 'in_progress', 'pending'])
    expect(container.textContent).toContain('Updating UI')
    // 焦点跟到标题行那颗开合按钮上,键盘用户不必从页首重新 Tab
    expect(document.activeElement).toBe(toggle)
  })

  it('turns into the ball on collapse instead of leaving a header row behind', async () => {
    vi.useFakeTimers()
    const { container } = await mountChecklist({ todos: TODOS, execution: 'running', defaultCollapsed: false })

    const toggle = container.querySelector<HTMLButtonElement>('[aria-controls]')
    expect(container.textContent).toContain('Task checklist · 1/3 completed')
    await act(async () => toggle?.click())

    /*
      需求:收起是两棵子树的交接,所以按下这一下**两棵都在**:球已经就位,卡片还在原地
      淡出(`checklist-card-exit`,见 `TaskChecklistShell`)。少了这段,观感是卡片被凭空抽走。
    */
    expect(ball(container)).not.toBeNull()
    expect(container.textContent).toContain('Task checklist · 1/3 completed')
    expect(container.querySelector('.checklist-card-exit')).not.toBeNull()

    // 淡出结束才摘掉卡片:标题行一并消失 —— 它上面报的数小球上也报,留着就是白占那一行
    await act(async () => { vi.advanceTimersByTime(CARD_LEAVE_MS) })
    expect(container.querySelector('[aria-controls]')).toBeNull()
    expect(container.textContent).not.toContain('Task checklist · 1/3 completed')
  })

  it('drops the fade-out altogether when motion is off, so no still card is left behind', async () => {
    vi.useFakeTimers()
    // 关掉动效档:theme.css 里那几段动画是 `animation: none`,留一个不动的残影比直接换掉更像卡住
    document.documentElement.dataset['themeMotion'] = 'off'
    try {
      const { container } = await mountChecklist({ todos: TODOS, execution: 'running', defaultCollapsed: false })
      await act(async () => container.querySelector<HTMLButtonElement>('[aria-controls]')?.click())

      expect(ball(container)).not.toBeNull()
      expect(container.querySelector('[aria-controls]')).toBeNull()
    } finally {
      // 恢复档位本身也会推一次订阅(useMotionLevel 观察 documentElement 的属性),包在 act 里
      await act(async () => { delete document.documentElement.dataset['themeMotion'] })
    }
  })

  it('enters each way with its own animation class', async () => {
    const { container } = await mountChecklist({ todos: TODOS, execution: 'running', defaultCollapsed: false })

    // 展开方向:卡片淡入,列表延后一档铺开(分工写在 theme.css)
    expect(container.querySelector('.checklist-card-enter')).not.toBeNull()
    expect(container.querySelector('.checklist-list-enter')).not.toBeNull()

    await act(async () => container.querySelector<HTMLButtonElement>('[aria-controls]')?.click())
    // 收起方向:小球 pop 入场
    expect(ball(container)?.className).toContain('checklist-ball-enter')
  })

  it('keeps the unfinished-after-stop warning on the ball instead of dropping it', async () => {
    const { container } = await mountChecklist({ todos: TODOS, execution: 'stopped' })

    expect(ball(container)?.getAttribute('aria-label'))
      .toBe('Show task checklist (1/3 completed) · Run ended with 2 unfinished task(s)')
  })

  /*
    悬停在球上要能读到「此刻正在做哪一项」—— 收起之后标题行那句 activeForm 没地方显示了。
    判据与标题行第二行同一条:没人在推进这份清单时**什么都不弹**,否则悬停会把上一轮
    留下的死账说成「正在做」。
  */
  const hover = async (element: Element | null): Promise<void> => {
    // 提示是 Arc 的 Tooltip(Radix):它在 pointermove 上开、带 250ms 延迟,不认 pointerenter
    await act(async () => { element?.dispatchEvent(new MouseEvent('pointermove', { bubbles: true })) })
    await act(async () => { vi.advanceTimersByTime(300) })
  }

  it('tells what is running when the pointer rests on the ball', async () => {
    vi.useFakeTimers()
    const { container } = await mountChecklist({ todos: TODOS, execution: 'running' })

    await hover(ball(container))
    const tip = document.body.querySelector('[role="tooltip"]')
    expect(tip?.textContent).toContain('Updating UI')
    expect(tip?.textContent).toContain('Task checklist · 1/3 completed')
    // 有浮层就不再叠一个系统 title:两者都由悬停触发,说的还是同一件事
    expect(ball(container)?.hasAttribute('title')).toBe(false)
  })

  it('stays silent on hover when nobody is pushing the checklist, keeping only the native one-liner', async () => {
    vi.useFakeTimers()
    const { container } = await mountChecklist({ todos: TODOS, execution: 'snapshot' })

    await hover(ball(container))
    expect(document.body.querySelector('[role="tooltip"]')).toBeNull()
    expect(ball(container)?.getAttribute('title')).toBe('Show task checklist (1/3 completed)')
  })

  it('stays a collapsible header row when the caller opts out of the ball', async () => {
    const { container } = await mountChecklist({ todos: TODOS, execution: 'running', minimizeToBall: false })

    expect(ball(container)).toBeNull()
    expect(container.querySelector('[aria-expanded]')?.getAttribute('aria-expanded')).toBe('false')
    expect(listExpanded(container)).toBe(false)
    // 收起的只是列表：报数、百分比、状态序列都还在，不必展开就能读
    expect(container.textContent).toContain('Task checklist · 1/3 completed')
    expect(container.textContent).toContain('33%')
    expect(rowStatuses(container)).toEqual(['completed', 'in_progress', 'pending'])
    // 标题行那行 activeForm 不属于列表，收起时照旧显示
    expect(container.textContent).toContain('Updating UI')
  })

  it('lets a click open the list instead of springing back to the default on the next render', async () => {
    const { container, show } = await mountChecklist({ todos: TODOS, execution: 'running', minimizeToBall: false })

    const toggle = container.querySelector<HTMLButtonElement>('[aria-controls]')
    await act(async () => toggle?.click())
    expect(listExpanded(container)).toBe(true)

    /*
      需求：`defaultCollapsed` 是挂载初值而非受控值。流式运行里 ChatView 会不停重渲，
      受控写法会把用户刚展开的列表按回收起态 —— 表现为「点了没反应」。
    */
    await show({ todos: TODOS, execution: 'running' })
    expect(listExpanded(container)).toBe(true)
  })

  it('honours defaultCollapsed={false} for callers that open it deliberately', async () => {
    const { container } = await mountChecklist({ todos: TODOS, defaultCollapsed: false })

    expect(container.querySelector('[aria-expanded]')?.getAttribute('aria-expanded')).toBe('true')
    expect(listExpanded(container)).toBe(true)
  })
})

describe('TaskChecklist · real task state', () => {
  it('keeps pending, running and completed rows while the panel is collapsed', async () => {
    vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
    const container = document.createElement('div')
    document.body.append(container)
    const root = createRoot(container)
    teardown = async () => {
      await act(async () => root.unmount())
      container.remove()
    }

    await act(async () => root.render(createElement(I18nProvider, {
      initialLocale: 'en-US',
      children: createElement(TaskChecklist, {
        execution: 'running',
        defaultCollapsed: false,
        // 这份用例读的是「收起只收列表」那一档(aria-hidden / inert);收起即小球的那条在
        // 上面那组用例里,两者是同一次点击的两个不同出口。
        minimizeToBall: false,
        todos: [
          { content: 'Inspect files', activeForm: 'Inspecting files', status: 'completed' },
          { content: 'Update UI', activeForm: 'Updating UI', status: 'in_progress' },
          { content: 'Run tests', activeForm: 'Running tests', status: 'pending' }
        ]
      })
    })))

    expect([...container.querySelectorAll('[data-task-status]')].map((row) => row.getAttribute('data-task-status')))
      .toEqual(['completed', 'in_progress', 'pending'])
    expect(container.textContent).toContain('Updating UI')

    const toggle = container.querySelector<HTMLButtonElement>('[aria-controls]')
    expect(toggle).not.toBeNull()
    await act(async () => toggle?.click())

    expect(toggle?.getAttribute('aria-expanded')).toBe('false')
    const list = document.getElementById(toggle?.getAttribute('aria-controls') ?? '')
    expect(list?.getAttribute('aria-hidden')).toBe('true')
    expect(list?.hasAttribute('inert')).toBe(true)
    expect(container.querySelectorAll('[data-task-status]')).toHaveLength(3)
  })

  it('drops the spinner and activeForm, keeps every status and count, and reports unfinished items once the run stops', async () => {
    // 默认收起 = 一颗小球,读不到行;这几档要比的是清单本体,所以显式展开
    const { container, show } = await mountChecklist({ todos: TODOS, execution: 'running', defaultCollapsed: false })

    expect(container.querySelector('[data-testid="task-checklist"]')?.getAttribute('data-execution-state')).toBe('running')
    expect(container.textContent).toContain('Task checklist · 1/3 completed')
    expect(container.textContent).toContain('33%')
    expect(container.textContent).toContain('Updating UI')
    expect(spinnerCount(container)).toBe(1)
    expect(container.querySelector('[data-task-status="in_progress"]')?.className).toContain('bg-accent')

    await show({ todos: TODOS, execution: 'stopped' })

    expect(container.querySelector('[data-testid="task-checklist"]')?.getAttribute('data-execution-state')).toBe('stopped')
    // 清单本体一个字段都没被改写:status 序列、完成计数、百分比都是原值
    expect(rowStatuses(container)).toEqual(['completed', 'in_progress', 'pending'])
    expect(container.textContent).toContain('Task checklist · 1/3 completed')
    expect(container.textContent).toContain('33%')
    expect(spinnerCount(container)).toBe(0)
    expect(container.textContent).not.toContain('Updating UI')
    expect(container.textContent).toContain('Update UI')
    expect(container.textContent).toContain('Run ended with 2 unfinished task(s)')
    expect(container.querySelector('[data-task-status="in_progress"] .sr-only')?.textContent).toBe('Unfinished')
  })

  it('stays quiet when a stopped checklist has nothing left to finish', async () => {
    const { container } = await mountChecklist({
      execution: 'stopped',
      defaultCollapsed: false,
      todos: [{ content: 'Run tests', activeForm: 'Running tests', status: 'completed' }]
    })

    expect(container.textContent).toContain('Task checklist · 1/1 completed')
    expect(container.textContent).not.toContain('Run ended with')
    expect(spinnerCount(container)).toBe(0)
  })

  it('labels an unattached checklist as a snapshot instead of showing work in progress', async () => {
    const { container } = await mountChecklist({ todos: TODOS, defaultCollapsed: false })

    expect(container.querySelector('[data-testid="task-checklist"]')?.getAttribute('data-execution-state')).toBe('snapshot')
    expect(container.textContent).toContain('Checklist snapshot')
    expect(spinnerCount(container)).toBe(0)
    expect(container.textContent).not.toContain('Updating UI')
    expect(container.textContent).toContain('Update UI')
    expect(rowStatuses(container)).toEqual(['completed', 'in_progress', 'pending'])
    expect(container.textContent).toContain('Task checklist · 1/3 completed')
    expect(container.querySelector('[data-task-status="in_progress"] .sr-only')?.textContent).toBe('Unfinished')
    expect(container.querySelector('[data-task-status="pending"] .sr-only')?.textContent).toBe('Waiting')
  })
})
