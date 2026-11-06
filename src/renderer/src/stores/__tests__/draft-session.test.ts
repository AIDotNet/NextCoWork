/**
 * ★ **「新建对话」不该在库里留下任何东西。**
 *
 * 以前 `makeTab('chat')` 铸一个 ULID、紧接着一次 `sessions:create` 把它插进库。
 * 于是点一下「新建对话」、关掉主区最后一个 Tab、工作区第一次露面 —— 每一样都
 * 在侧边栏「最近对话」里留下一条零消息的「新对话」。用户什么也没做,列表却在涨,
 * 而那些记录连一句话都没有,谁也认不出它们是什么。
 *
 * 这个文件量的是那条不变式本身,以及它的另一半:**白纸终究要变成会话**,
 * 而变的那一刻(发送 / 贴附件)必须把渲染层的家当一起搬过去,不能丢草稿。
 *
 * `services/app` / `services/agent` / `services/sessions` 整个替掉:
 * 它们背后是 `window.nextcowork`,node 环境里不存在。
 * ★ `services/sessions` 是这里的**被测对象之一** —— 断言的正是它一次都没被调到。
 */
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { chatKey } from '../../../../shared/domain/tab'
import type { InnerTab } from '../../../../shared/domain/tab'

const persistSessionDraft = vi.fn()

vi.mock('../../services/app', () => ({
  getInnerTabs: vi.fn(async () => ({ tabs: [], activeTabId: null })),
  persistInnerTabs: vi.fn(),
  persistOuterTabs: vi.fn(),
  getSessionInput: vi.fn(async () => null),
  persistSessionDraft: (...args: unknown[]) => persistSessionDraft(...args)
}))

vi.mock('../../services/agent', () => ({
  startRun: vi.fn(),
  attachRun: vi.fn(),
  abortRun: vi.fn(),
  onAgentEvent: vi.fn(() => () => {})
}))

const createSession = vi.fn(async () => undefined)
vi.mock('../../services/sessions', () => ({
  createSession: (...args: unknown[]) => createSession(...(args as [])),
  getSession: vi.fn(async () => {
    throw new Error('会话不存在')
  }),
  getSessionPage: vi.fn(async () => {
    throw new Error('会话不存在')
  })
}))

import { sessionStore } from '../session'
import { useTabsStore } from '../tabs'

const WS = 'ws-draft'
const tabsInitial = useTabsStore.getState()

beforeEach(() => {
  vi.clearAllMocks()
  useTabsStore.setState(tabsInitial, true)
})

const chats = (): Extract<InnerTab, { kind: 'chat' }>[] =>
  useTabsStore
    .getState()
    .tabsOf(WS, 'main')
    .filter((t): t is Extract<InnerTab, { kind: 'chat' }> => t.kind === 'chat')

describe('草稿对话', () => {
  it('新建的对话没有 sessionId,也不发 sessions:create', () => {
    useTabsStore.getState().newChat(WS)

    expect(chats()).toHaveLength(1)
    expect(chats()[0]!.ref.sessionId).toBeNull()
    expect(createSession).not.toHaveBeenCalled()
  })

  it('连开五个 Tab 一条会话都不建 —— 库里不该因为开 Tab 而涨', () => {
    const tabs = useTabsStore.getState()
    for (let i = 0; i < 5; i += 1) tabs.open(WS, 'chat')

    expect(chats()).toHaveLength(5)
    expect(chats().every((t) => t.ref.sessionId === null)).toBe(true)
    expect(createSession).not.toHaveBeenCalled()
  })

  it('绑定才铸出 id,而且幂等 —— 贴图和发送可能先后各调一次', () => {
    const tabs = useTabsStore.getState()
    tabs.newChat(WS)
    const tab = chats()[0]!

    const first = useTabsStore.getState().bindChatSession(WS, tab.id)
    const second = useTabsStore.getState().bindChatSession(WS, tab.id)

    expect(first).not.toBeNull()
    expect(second).toBe(first)
    expect(chats()[0]!.ref.sessionId).toBe(first)
    // 绑定 ≠ 落库。那一行仍然只由主进程 runAgent 的 ensureSession 建。
    expect(createSession).not.toHaveBeenCalled()
  })

  it('绑定时草稿跟着搬家 —— 贴图会让 ChatView 重挂,半句话不能丢', () => {
    const tabs = useTabsStore.getState()
    tabs.newChat(WS)
    const tab = chats()[0]!
    sessionStore(chatKey(tab)).getState().setDraft('写了一半')

    const sessionId = useTabsStore.getState().bindChatSession(WS, tab.id)!

    expect(sessionStore(sessionId).getState().draft).toBe('写了一半')
    // 旧存档要立即清掉,否则下一个复用这个 tabId 的草稿会把它捡回来
    expect(persistSessionDraft).toHaveBeenCalledWith(tab.id, '', true)
  })

  it('已绑定的 Tab 再点「新建对话」才真的开第二个', () => {
    const tabs = useTabsStore.getState()
    tabs.newChat(WS)
    const tab = chats()[0]!
    const sessionId = useTabsStore.getState().bindChatSession(WS, tab.id)!
    // 绑定本身不算「用过」—— 用过的判据仍然是转录/草稿,见 isSessionUntouched
    sessionStore(sessionId).getState().setDraft('已经在写了')

    useTabsStore.getState().newChat(WS)

    expect(chats()).toHaveLength(2)
    expect(chats()[1]!.ref.sessionId).toBeNull()
  })

  it('bindChatSession 对不存在的 Tab 返回 null,不凭空造一个', () => {
    useTabsStore.getState().newChat(WS)
    expect(useTabsStore.getState().bindChatSession(WS, 'no-such-tab')).toBeNull()
  })
})
