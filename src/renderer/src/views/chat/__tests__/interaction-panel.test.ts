/**
 * 计划审批卡的 DOM 级回归 —— 钉的是「动作不再是一排等重按钮,输入框不常驻」。
 *
 * 纯函数测不到的就是这几条:某块 UI **在不在**、按下去**发出的是哪个 action**。
 * 键盘规则本身在 `interaction-keys.test.ts` 里逐条验过,这里只验它确实接上了。
 *
 * ★ **这一份要真 jsdom 环境,不能照 `subagent-card.test.ts` 手搓。** react-dom
 * 在**首次 import 时**就记下了「这个环境支不支持 input 事件」;手搓 JSDOM 是在那
 * 之后才把 `document` 塞进全局的,于是它认定不支持,转而走 IE 时代的
 * `attachEvent` 轮询兜底 —— 往 textarea 里打字会直接抛 TypeError。那几份测试没
 * 碰过输入框,所以一直没暴露。
 *
 * @vitest-environment jsdom
 */
import { act, createElement } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { InteractionResponse, PendingInteraction } from '../../../../../shared/agent/interaction'
import { I18nProvider } from '../../../i18n'
import { listInteractions, respondInteraction } from '../../../services/agent'
import { InteractionPanel } from '../InteractionPanel'

const sent: InteractionResponse[] = []
let pending: PendingInteraction[] = []

vi.mock('../../../services/agent', () => ({
  listInteractions: vi.fn(async () => pending),
  onAgentEvent: vi.fn(() => () => {}),
  respondInteraction: vi.fn(async (response: InteractionResponse) => { sent.push(response) })
}))
vi.mock('../../../services/app', () => ({
  copyText: vi.fn(async () => {}),
  saveTextFile: vi.fn(async () => ({ path: '/tmp/plan.md' }))
}))

const PLAN: PendingInteraction = {
  id: 'i-1',
  runId: 'run-1',
  kind: 'plan_approval',
  planId: 'plan-1',
  path: '/w/.nextcowork/plans/重构导航.md',
  plan: '# 重构导航\n\n1. 抽出路由表\n',
  createdAt: 0
}

let teardown: (() => Promise<void>) | null = null

afterEach(async () => {
  await teardown?.()
  teardown = null
  sent.length = 0
  vi.clearAllMocks()
})

async function renderPanel(
  interaction: PendingInteraction,
  onExecute?: () => void,
  sessionId?: string
): Promise<HTMLElement> {
  return renderPending([interaction], onExecute, sessionId)
}

async function renderPending(
  interactions: PendingInteraction[],
  onExecute?: () => void,
  sessionId?: string
): Promise<HTMLElement> {
  pending = interactions
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  // Radix 的 RadioGroup 会 unobserve —— 缺一个方法就在卸载时炸,而报错指向 React 内部
  vi.stubGlobal('ResizeObserver', class {
    observe(): void {}
    unobserve(): void {}
    disconnect(): void {}
  })
  Object.assign(window, { nextcowork: { on: () => () => {} } })

  const container = document.createElement('div')
  document.body.append(container)
  const root = createRoot(container)
  await act(async () => root.render(createElement(I18nProvider, {
    initialLocale: 'zh-CN',
    children: createElement(InteractionPanel, { runId: 'run-1', sessionId, onExecute })
  })))
  teardown = async () => {
    await act(async () => root.unmount())
    container.remove()
    vi.unstubAllGlobals()
  }
  return container
}

const rows = (container: HTMLElement): HTMLElement[] =>
  [...container.querySelectorAll<HTMLElement>('[data-testid="interaction-row"]')]

const press = async (el: Element, key: string, init: KeyboardEventInit = {}): Promise<void> => {
  await act(async () => {
    el.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init }))
  })
}

/** 往受控输入框里写字。必须绕开 React 装在实例上的值追踪器,否则它看不出变化。 */
const type = async (field: HTMLTextAreaElement, text: string): Promise<void> => {
  await act(async () => {
    Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')!.set!.call(field, text)
    field.dispatchEvent(new Event('input', { bubbles: true }))
  })
}

const click = async (el: Element | null): Promise<void> => {
  if (el === null) throw new Error('要点的那个节点不在 —— 断言前先确认它渲染了')
  await act(async () => {
    el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
  })
}

describe('计划审批卡 · 选项行 + 按需展开', () => {
  it('★ 默认一个输入框都不渲染', async () => {
    const container = await renderPanel(PLAN, () => {})
    // 常驻 textarea 是这次要拆掉的东西:绝大多数审批根本不打字,
    // 它却一直把计划预览往上挤,整张卡看着像「要你填表」。
    expect(container.querySelector('textarea')).toBeNull()
    expect(rows(container)).toHaveLength(3)
  })

  it('★ 数字键换高亮,Enter 执行的是高亮那一行', async () => {
    const container = await renderPanel(PLAN, () => {})
    const list = container.querySelector('[data-testid="interaction-rows"]')!

    await press(list, '2')
    await press(list, 'Enter')

    expect(sent).toHaveLength(1)
    expect(sent[0]).toMatchObject({ kind: 'plan_approval', action: 'approve_new_session' })
  })

  it('★ 「要求修改」是就地展开,不是立刻发一个空回复', async () => {
    const container = await renderPanel(PLAN, () => {})
    const revision = rows(container).find((row) => row.dataset.rowValue === 'request_revision')!

    await click(revision)

    expect(sent).toHaveLength(0)
    const field = container.querySelector('textarea')
    expect(field).not.toBeNull()
    // 一个字都没写时**不给**发送键,而不是给一颗灰的 —— 灰键说不出还差什么。
    expect(container.textContent).not.toContain('发送修改意见')

    await type(field!, '先把路由表抽出来')
    expect(container.textContent).toContain('发送修改意见')

    await press(field!, 'Enter', { metaKey: true })
    expect(sent[0]).toMatchObject({ kind: 'plan_approval', action: 'request_revision', feedback: '先把路由表抽出来' })
  })

  it('★ 展开态里的裸 Enter 是换行,不是提交', async () => {
    const container = await renderPanel(PLAN, () => {})
    await click(rows(container).find((row) => row.dataset.rowValue === 'request_revision')!)

    await press(container.querySelector('textarea')!, 'Enter')

    expect(sent).toHaveLength(0)
  })

  it('★ Esc 收起展开区,行还在', async () => {
    const container = await renderPanel(PLAN, () => {})
    await click(rows(container).find((row) => row.dataset.rowValue === 'request_revision')!)
    expect(container.querySelector('textarea')).not.toBeNull()

    await press(container.querySelector('textarea')!, 'Escape')

    expect(container.querySelector('textarea')).toBeNull()
    expect(rows(container)).toHaveLength(3)
  })

  it('没有执行回调时只剩一条批准行 —— 「在新会话执行」无处可去', async () => {
    const container = await renderPanel(PLAN)
    expect(rows(container).map((row) => row.dataset.rowValue)).toEqual(['approve_current', 'request_revision'])
  })

  it('放弃计划仍是底部带文案的按钮,不是一颗 ×', async () => {
    const container = await renderPanel(PLAN, () => {})
    // deny/reject 是有语义的回复,不是「关掉这张卡」—— 一颗 × 说不出这个区别。
    const abandon = [...container.querySelectorAll('button')].find((b) => b.textContent === '放弃计划')
    await click(abandon ?? null)
    expect(sent[0]).toMatchObject({ kind: 'plan_approval', action: 'reject' })
  })
})

describe('工具授权卡 · 同一套行', () => {
  const TOOL: PendingInteraction = {
    id: 'i-2',
    runId: 'run-1',
    kind: 'tool_permission',
    callId: 'call-1',
    toolName: 'Bash',
    input: { command: 'rm -rf build' },
    readOnly: false,
    destructive: true,
    suggestedRule: 'Bash(rm:*)',
    createdAt: 0
  }

  it('默认允许仍只批准原始调用一次', async () => {
    const container = await renderPanel(TOOL)

    await click(rows(container).find((row) => row.dataset.rowValue === 'allow_once')!)

    expect(sent).toEqual([{ id: TOOL.id, kind: 'tool_permission', decision: { kind: 'allow_once' } }])
  })

  it('★ 展开「改完参数再允许」时收起只读预览 —— 同一份参数不摆两遍', async () => {
    const container = await renderPanel(TOOL)
    expect(container.querySelector('pre')).not.toBeNull()

    await click(rows(container).find((row) => row.dataset.rowValue === 'allow_edited')!)

    expect(container.querySelector('pre')).toBeNull()
    expect(container.querySelector('textarea')?.value).toContain('rm -rf build')
  })

  it('★ 改参数时「以后都允许」撤走 —— 它写进配置的是原始那份', async () => {
    const container = await renderPanel(TOOL)
    expect(rows(container).map((row) => row.dataset.rowValue)).toContain('allow_always')

    await click(rows(container).find((row) => row.dataset.rowValue === 'allow_edited')!)

    expect(rows(container).map((row) => row.dataset.rowValue)).not.toContain('allow_always')
  })

  it('改坏的 JSON 不发出去,报错留在卡上', async () => {
    const container = await renderPanel(TOOL)
    await click(rows(container).find((row) => row.dataset.rowValue === 'allow_edited')!)
    const field = container.querySelector('textarea')!
    await type(field, '{ 坏掉的')

    await press(field, 'Enter', { metaKey: true })

    expect(sent).toHaveLength(0)
    expect(container.querySelector('[role="alert"]')).not.toBeNull()
  })

  it.each(['metaKey', 'ctrlKey'] as const)('编辑器移到正文后,%s+Enter 仍提交修改后的参数', async (modifier) => {
    const container = await renderPanel(TOOL)
    await click(rows(container).find((row) => row.dataset.rowValue === 'allow_edited')!)
    const field = container.querySelector('textarea')!
    await type(field, '{"command":"ls build"}')

    await press(field, 'Enter', { [modifier]: true })

    expect(sent).toEqual([{
      id: TOOL.id,
      kind: 'tool_permission',
      decision: { kind: 'allow_edited', input: { command: 'ls build' } }
    }])
  })

  it('编辑器里的裸 Enter 和数字键不会触发授权', async () => {
    const container = await renderPanel(TOOL)
    await click(rows(container).find((row) => row.dataset.rowValue === 'allow_edited')!)
    const field = container.querySelector('textarea')!

    await press(field, 'Enter')
    await press(field, '2')

    expect(sent).toHaveLength(0)
    expect(container.querySelector('textarea')).toBe(field)
  })

  it('Esc 放弃修改并清掉校验错误,重新编辑从原始参数开始', async () => {
    const container = await renderPanel(TOOL)
    await click(rows(container).find((row) => row.dataset.rowValue === 'allow_edited')!)
    const field = container.querySelector('textarea')!
    await type(field, '{ invalid')
    await press(field, 'Enter', { metaKey: true })
    expect(container.querySelector('[role="alert"]')).not.toBeNull()

    await press(field, 'Escape')

    expect(sent).toHaveLength(0)
    expect(container.querySelector('textarea')).toBeNull()
    expect(container.querySelector('[role="alert"]')).toBeNull()
    expect(container.querySelector('pre')?.textContent).toBe(JSON.stringify(TOOL.input, null, 2))
    expect(rows(container).map((row) => row.dataset.rowValue)).toContain('allow_always')
    await click(rows(container).find((row) => row.dataset.rowValue === 'allow_edited')!)
    expect(container.querySelector('textarea')?.value).toBe(JSON.stringify(TOOL.input, null, 2))
  })

  it('使用原始参数只退出编辑,不执行授权', async () => {
    const container = await renderPanel(TOOL)
    await click(rows(container).find((row) => row.dataset.rowValue === 'allow_edited')!)
    await type(container.querySelector('textarea')!, '{"command":"ls build"}')

    await click([...container.querySelectorAll('button')].find((button) => button.textContent === '使用原始参数')!)

    expect(sent).toHaveLength(0)
    expect(container.querySelector('pre')?.textContent).toBe(JSON.stringify(TOOL.input, null, 2))
  })

  it('编辑时仍可拒绝,不会提交修改后的参数', async () => {
    const container = await renderPanel(TOOL)
    await click(rows(container).find((row) => row.dataset.rowValue === 'allow_edited')!)
    await type(container.querySelector('textarea')!, '{"command":"ls build"}')

    await click([...container.querySelectorAll('button')].find((button) => button.textContent === '拒绝')!)

    expect(sent).toEqual([{ id: TOOL.id, kind: 'tool_permission', decision: { kind: 'deny' } }])
  })

  it('提交失败保留编辑草稿,可在固定操作区重试', async () => {
    const container = await renderPanel(TOOL)
    await click(rows(container).find((row) => row.dataset.rowValue === 'allow_edited')!)
    const edited = '{"command":"ls build"}'
    await type(container.querySelector('textarea')!, edited)
    vi.mocked(respondInteraction).mockRejectedValueOnce(new Error('offline'))
    const submit = [...container.querySelectorAll('button')].find((button) => button.textContent === '允许这一次')!

    await click(submit)

    expect(container.querySelector('[role="alert"]')).not.toBeNull()
    expect(container.querySelector('textarea')?.value).toBe(edited)
    expect(submit.disabled).toBe(false)
    await click(submit)
    expect(sent).toEqual([{
      id: TOOL.id, kind: 'tool_permission', decision: { kind: 'allow_edited', input: { command: 'ls build' } }
    }])
  })
})

describe('问答卡 · 「其它」长在那一行里', () => {
  const ask = (allowFreeform: boolean): PendingInteraction => ({
    id: 'i-3',
    runId: 'run-1',
    kind: 'ask_user',
    createdAt: 0,
    questions: [{
      header: '修复范围',
      question: '改哪一层?',
      options: [{ label: '只改渲染层' }, { label: '连内核一起改' }],
      multiSelect: false,
      allowFreeform
    }]
  })

  it('★ 选中「其它」之前没有输入框,选中之后它就在那一行里面', async () => {
    const container = await renderPanel(ask(true))
    expect(container.querySelector('textarea')).toBeNull()

    const other = [...container.querySelectorAll<HTMLElement>('[data-row-value]')].at(-1)!
    await click(other)

    const field = container.querySelector('textarea')
    expect(field).not.toBeNull()
    // 长在行里面 = 它和那一行同属一个行框,且那个框里只有这一行。
    // 挂在列表下面的话,能装下输入框的最近祖先会是整个选项列表,这条就挂了。
    const frame = other.parentElement!.parentElement!
    expect(frame.contains(field!)).toBe(true)
    expect(frame.querySelectorAll('[data-row-value]')).toHaveLength(1)
  })

  it('★ 数字键选中对应选项,选完自动跳过「提交」不发空答案', async () => {
    const container = await renderPanel(ask(false))
    const card = container.querySelector('[data-testid="agent-interaction"]')!

    await press(card, '2')

    expect(container.querySelector('[data-row-value="连内核一起改"]')?.getAttribute('aria-checked')).toBe('true')
    expect(sent).toHaveLength(0)

    await press(card, 'Enter')
    expect(sent[0]).toMatchObject({ kind: 'ask_user', answers: [['连内核一起改']] })
  })

  it('只勾了「其它」却一个字没写时不让提交', async () => {
    const container = await renderPanel(ask(true))
    const card = container.querySelector('[data-testid="agent-interaction"]')!
    await click([...container.querySelectorAll<HTMLElement>('[data-row-value]')].at(-1)!)

    await press(card, 'Enter', { metaKey: true })

    expect(sent).toHaveLength(0)
  })
})

describe('目标提议卡 · 模型想设一个完成条件', () => {
  const PROPOSAL: PendingInteraction = {
    id: 'i-goal',
    runId: 'run-1',
    sessionId: 'session-1',
    kind: 'goal_proposal',
    condition: '`bun test` 退出码为 0，且转录里有这次运行的输出',
    createdAt: 0
  }

  it('★ 从 listInteractions 的返回里渲染,条件原文照抄', async () => {
    const container = await renderPanel(PROPOSAL, undefined, 'session-1')

    // 卡片是按这一对 id 去问的;问错了就把别的会话的提议摆到了这一张屏幕上
    expect(vi.mocked(listInteractions)).toHaveBeenCalledWith('run-1', 'session-1')
    const card = container.querySelector('[data-testid="agent-interaction"]')
    expect(card?.getAttribute('data-interaction-kind')).toBe('goal_proposal')
    expect(container.textContent).toContain('模型想设一个完成条件')
    expect(container.textContent).toContain(PROPOSAL.condition)
    expect(rows(container).map((row) => row.dataset.rowValue)).toEqual(['approve', 'decline'])
  })

  it('★ 同意只发 {id, kind, approved} —— 一个多余字段都不带', async () => {
    const container = await renderPanel(PROPOSAL, undefined)

    await click(rows(container).find((row) => row.dataset.rowValue === 'approve') ?? null)

    // 用 toEqual 而不是 toMatchObject:回送形状就是主进程的输入契约,
    // 多带一个字段(比如把 condition 捎回去)等于在这里开了一个口子
    expect(sent).toHaveLength(1)
    expect(sent[0]).toEqual({ id: 'i-goal', kind: 'goal_proposal', approved: true })
  })

  it('★ 拒绝发 approved:false,卡片当场撤走', async () => {
    const container = await renderPanel(PROPOSAL, undefined)

    await click(rows(container).find((row) => row.dataset.rowValue === 'decline') ?? null)

    expect(sent[0]).toEqual({ id: 'i-goal', kind: 'goal_proposal', approved: false })
    // 答完就消失 —— 留在屏幕上会让人以为还没决定
    expect(container.querySelector('[data-testid="agent-interaction"]')).toBeNull()
  })

  it('数字键 2 + Enter 落在拒绝上 —— 和别的卡共用同一套行', async () => {
    const container = await renderPanel(PROPOSAL, undefined)

    await press(container.querySelector('[data-testid="interaction-rows"]')!, '2')
    await press(container.querySelector('[data-testid="interaction-rows"]')!, 'Enter')

    expect(sent[0]).toEqual({ id: 'i-goal', kind: 'goal_proposal', approved: false })
  })

  it('提议与旧卡同时待决时互不干扰 —— 老几条路一条都没变', async () => {
    const container = await renderPending([PLAN, PROPOSAL], () => {})

    expect(rows(container).map((row) => row.dataset.rowValue)).toEqual([
      'approve_current', 'approve_new_session', 'request_revision', 'approve', 'decline'
    ])
    expect([...container.querySelectorAll('[data-testid="agent-interaction"]')]
      .map((card) => card.getAttribute('data-interaction-kind')))
      .toEqual(['plan_approval', 'goal_proposal'])
  })
})
