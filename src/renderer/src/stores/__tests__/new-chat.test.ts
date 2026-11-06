/**
 * ★ **侧边栏的「新建对话」是重用,Tab 条上的 `+` 是新建。**
 *
 * 这两颗按钮以前走同一条 `open`,于是连点侧边栏那颗就攒出一排一模一样的
 * 「新对话」—— 每一张都是白纸,用户以为自己什么也没做成。
 *
 * 这个文件量的就是「两颗按钮语义不同」这件事本身:少了任何一半都是坏的。
 * 全走重用的话,Tab 条那颗 `+` 就再也开不出第二个空对话(stores/tabs.ts 里
 * `open` 的注释写着这条);全走新建就回到了原来的 bug。
 *
 * 「用过没用过」的判据是 `isSessionUntouched`,它读的是会话 store 的注册表,
 * 所以这里通过真的往 store 里写状态来构造「用过」,而不是去 mock 那个判据 ——
 * mock 掉的话,判据本身怎么错都测不出来。
 *
 * `services/app` / `services/agent` 整个替掉:它们背后是 `window.nextcowork`,
 * node 环境里不存在。
 */
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { userMessage } from '../../../../shared/agent/message'
import type { InnerTab } from '../../../../shared/domain/tab'
import { chatKey } from '../../../../shared/domain/tab'

vi.mock('../../services/app', () => ({
  getInnerTabs: vi.fn(async () => ({ tabs: [], activeTabId: null })),
  persistInnerTabs: vi.fn(),
  persistOuterTabs: vi.fn(),
  // 未发出的输入落盘。null = 没有存档,让每个用例从空白 store 起步
  getSessionInput: vi.fn(async () => null),
  persistSessionDraft: vi.fn()
}))

vi.mock('../../services/agent', () => ({
  startRun: vi.fn(),
  attachRun: vi.fn(),
  abortRun: vi.fn(),
  onAgentEvent: vi.fn(() => () => {})
}))

import { sessionStore } from '../session'
import { useTabsStore } from '../tabs'

const WS = 'ws-test'
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

const activeId = (): string | null => useTabsStore.getState().activeIdOf(WS, 'main')

/** 让某个会话「被用过」:发过一条消息的转录长这样 */
function useSession(sessionId: string): void {
  sessionStore(sessionId).setState((s) => ({
    transcript: {
      ...s.transcript,
      messages: [userMessage('m-1', [{ type: 'text', text: '你好' }], 1_000)]
    }
  }))
}

describe('侧边栏「新建对话」', () => {
  it('第一次点开一个;再点不开第二个,而是停在那一个上', () => {
    const tabs = useTabsStore.getState()
    tabs.newChat(WS)
    expect(chats()).toHaveLength(1)
    const first = chats()[0]!

    tabs.newChat(WS)
    tabs.newChat(WS)
    expect(chats()).toHaveLength(1)
    expect(activeId()).toBe(first.id)
  })

  it('那一个用过之后,再点才真的新建', () => {
    const tabs = useTabsStore.getState()
    tabs.newChat(WS)
    const first = chats()[0]!
    useSession(chatKey(first))

    tabs.newChat(WS)
    expect(chats()).toHaveLength(2)
    expect(activeId()).toBe(chats()[1]!.id)
  })

  it('输入框里有草稿的会话不算空 —— 这时候要的是干净的一屏', () => {
    const tabs = useTabsStore.getState()
    tabs.newChat(WS)
    const first = chats()[0]!
    sessionStore(chatKey(first)).getState().setDraft('写了一半')

    tabs.newChat(WS)
    expect(chats()).toHaveLength(2)
  })

  it('人在别的 Tab 上时,切回那个空对话而不是新建', () => {
    const tabs = useTabsStore.getState()
    tabs.newChat(WS)
    const chat = chats()[0]!
    // 底部开一个终端不影响主区;主区再开一个文档,焦点就离开了那个空对话
    tabs.open(WS, 'doc')
    const doc = useTabsStore.getState().tabsOf(WS, 'main').find((t) => t.kind === 'doc')!
    expect(activeId()).toBe(doc.id)

    tabs.newChat(WS)
    expect(chats()).toHaveLength(1)
    expect(activeId()).toBe(chat.id)
  })

  it('Tab 条那颗 `+` 走的是 `open`,连点两次要得到两个对话', () => {
    const tabs = useTabsStore.getState()
    tabs.open(WS, 'chat')
    tabs.open(WS, 'chat')
    expect(chats()).toHaveLength(2)
  })

  it('打开历史会话复用 session 引用,重复点击不会新增 Tab', () => {
    const tabs = useTabsStore.getState()
    tabs.openSession(WS, 'session-history', '一条历史会话')
    expect(chats()).toHaveLength(1)
    const first = chats()[0]!

    tabs.openSession(WS, 'session-history', '一条历史会话')
    expect(chats()).toHaveLength(1)
    expect(activeId()).toBe(first.id)
    expect(first.ref.sessionId).toBe('session-history')
  })

  /**
   * ★ 回归:store 是懒创建的,重启后除了当前那一个,其余 chat Tab 的 store 都不在
   * 注册表里 —— `isSessionUntouched` 的「查不到就是没碰过」于是把跑过一整段对话的
   * 会话也当成白纸,点「新建对话」跳进旧会话。绑过 sessionId 就不是白纸。
   */
  it('引用着历史会话、store 还没建起来的 Tab 不算白纸', () => {
    const tabs = useTabsStore.getState()
    tabs.openSession(WS, 'session-history', '一条历史会话')
    const history = chats()[0]!

    useTabsStore.getState().newChat(WS)

    expect(chats()).toHaveLength(2)
    expect(activeId()).not.toBe(history.id)
    expect(chats()[1]!.ref.sessionId).toBeNull()
  })
})
