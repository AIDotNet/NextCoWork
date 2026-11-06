/**
 * 「重新生成」用的是**此刻**的模型,不是上一次发送时那个。
 *
 * ★ 这条曾经反着写:`editMessage` 读的是 `lastOptions`(上一次发送的快照),
 * 于是「切个模型再试一次」——按重新生成最主要的理由——完全失效,
 * 而界面上没有任何迹象说明为什么:药丸显示新模型,跑的是旧模型。
 *
 * 断言落在 `startRun` 真正收到的 `model` / `modelProviderId` 上,
 * 而不是 store 的中间状态 —— 中间状态对不对不重要,发出去的那份才算数。
 */
import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest'
import { assistantMessage, userMessage } from '../../../../shared/agent/message'
import type { SendOptions } from '../../../../shared/agent/run-request'

vi.mock('../../services/agent', () => ({
  startRun: vi.fn(async () => ({ started: true })),
  attachRun: vi.fn(),
  abortRun: vi.fn(),
  onAgentEvent: vi.fn(() => () => {})
}))

vi.mock('../../services/app', () => ({
  getInnerTabs: vi.fn(async () => ({ tabs: [], activeTabId: null })),
  persistInnerTabs: vi.fn(),
  persistOuterTabs: vi.fn(),
  getSessionInput: vi.fn(async () => null),
  persistSessionDraft: vi.fn()
}))

vi.mock('../../services/sessions', () => {
  const getSession = vi.fn(async (_sessionId: string): Promise<unknown> => null)
  return {
    getSession,
    // 转录按页读(`getSessionPage`):委托给各用例摆好的整段历史,一页就是全部
    getSessionPage: vi.fn(async (sessionId: string) => {
      const detail = await getSession(sessionId)
      return detail == null ? detail : { ...(detail as object), hasMore: false }
    }),
  replaceHistory: vi.fn(async () => undefined),
    editSessionMessage: vi.fn(async () => undefined),
    deleteSessionTurn: vi.fn(async () => undefined),
    deleteSessionReply: vi.fn(async () => undefined),
  }
})

import { startRun } from '../../services/agent'
import { releaseSession, sessionStore } from '../session'

const mockStartRun = vi.mocked(startRun)

const options = (over: Partial<SendOptions> = {}): SendOptions => ({
  workspaceId: 'w1',
  depth: 0,
  mode: 'normal',
  thinking: 'auto',
  webSearch: true,
  permissionMode: 'ask',
  model: 'gpt-5.6-terra',
  modelProviderId: 'routin',
  skillIds: [],
  ...over
})

const created: string[] = []
function seeded(id: string): ReturnType<typeof sessionStore> {
  created.push(id)
  const store = sessionStore(id)
  store.setState((s) => ({
    transcript: {
      ...s.transcript,
      messages: [
        userMessage('u1', [{ type: 'text', text: '分析一下当前的项目' }], 1),
        assistantMessage('a1', [{ type: 'text', text: '好的' }], 2)
      ]
    }
  }))
  return store
}

beforeEach(() => { vi.clearAllMocks() })

afterEach(() => {
  for (const id of created) {
    sessionStore(id).setState({ activeRunId: null })
    releaseSession(id)
  }
  created.length = 0
})

describe('重新生成时用哪个模型', () => {
  it('★ 用此刻传进来的模型,而不是上一次发送时那个', async () => {
    const s = seeded('regen-model')
    // 上一次是用 Codex 发的
    await s.getState().send('分析一下当前的项目', options({ model: 'gpt-6-astra', modelProviderId: 'codex' }))
    s.setState({ activeRunId: null })
    mockStartRun.mockClear()

    // 用户切到了 RoutinAI / gpt-5.6-terra,然后点重新生成
    await s.getState().editMessage('u1', '分析一下当前的项目', true, options())

    expect(mockStartRun).toHaveBeenCalledTimes(1)
    expect(mockStartRun.mock.calls[0]?.[0]).toMatchObject({
      model: 'gpt-5.6-terra',
      modelProviderId: 'routin'
    })
  })

  it('切回「未锁定供应商」时也要跟着变,不能把旧的 providerId 留下来', async () => {
    const s = seeded('regen-unpin')
    await s.getState().send('分析一下当前的项目', options({ modelProviderId: 'codex' }))
    s.setState({ activeRunId: null })
    mockStartRun.mockClear()

    await s.getState().editMessage('u1', '分析一下当前的项目', true,
      options({ modelProviderId: undefined }))

    expect(mockStartRun.mock.calls[0]?.[0].modelProviderId).toBeUndefined()
  })

  it('档位/模式一并跟着此刻的值走 —— 重新生成等同于「现在把这条重发一次」', async () => {
    const s = seeded('regen-opts')
    await s.getState().send('分析一下当前的项目', options({ thinking: 'off', permissionMode: 'ask' }))
    s.setState({ activeRunId: null })
    mockStartRun.mockClear()

    await s.getState().editMessage('u1', '分析一下当前的项目', true,
      options({ thinking: 'high', permissionMode: 'auto' }))

    expect(mockStartRun.mock.calls[0]?.[0]).toMatchObject({ thinking: 'high', permissionMode: 'auto' })
  })
})
