/**
 * 截断重跑之后,**留下来的那部分历史**里的工具卡片必须继续认得出自己。
 *
 * ★ 这里量的是用户报过的那条:一次生图跑完 → 编辑(或点「重新生成」)一条**更晚**的
 * 消息 → 更早那张生成图卡片退回「等待」+「生成中」,而且不会自己恢复(要重开会话
 * 才会好)。判据只落在 `transcript.tools` / `transcript.subagents` 上:卡片那边
 * 「`tools[callId]` 不在 → 行状态 pending → output undefined → 摆加载格」是既有行为,
 * 不必在这里重测一遍。
 *
 * ★ 反向也测:被切掉那一轮的 callId 必须**不在**结果里 —— 否则这次「修好」只是把
 * 整张旧表留下来,下一步就是删掉的回合在别处冒出来。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { assistantMessage, toolResultMessage, userMessage } from '../../../../shared/agent/message'
import type { AgentMessage } from '../../../../shared/agent/message'
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

import { releaseSession, sessionStore } from '../session'

const options = (): SendOptions => ({
  workspaceId: 'w1',
  depth: 0,
  mode: 'normal',
  thinking: 'auto',
  webSearch: true,
  permissionMode: 'ask',
  model: 'gpt-5.6-terra',
  skillIds: []
})

/** 前一轮:生图 + 一个前台子代理；后一轮:另一张生图(它会被切掉)。 */
const history: AgentMessage[] = [
  userMessage('u1', [{ type: 'text', text: '画一张图' }], 1),
  assistantMessage('a1', [
    { type: 'tool_call', callId: 'image', name: 'generate_image', input: { prompt: '一只鹈鹕' } },
    { type: 'tool_call', callId: 'task', name: 'Task', input: { description: '查资料', subagent_type: 'explore' } }
  ], 2),
  toolResultMessage('r1', [
    {
      type: 'tool_result',
      callId: 'image',
      output: { content: '已生成', images: [{ mime: 'image/png', dataRef: 'ncw://attachments/sessions/s/1.png' }] },
      isError: false
    },
    {
      type: 'tool_result',
      callId: 'task',
      output: { content: '查完了' },
      isError: false,
      subagent: { childRunId: 'child-1', status: 'done', summary: '查完了' }
    }
  ], 3),
  assistantMessage('b1', [{ type: 'text', text: '画好了' }], 4),
  userMessage('u2', [{ type: 'text', text: '再来一张' }], 5),
  assistantMessage('a2', [{ type: 'tool_call', callId: 'to-be-cut', name: 'generate_image', input: { prompt: '兔女郎' } }], 6),
  toolResultMessage('r2', [
    {
      type: 'tool_result',
      callId: 'to-be-cut',
      output: { content: '已生成', images: [{ mime: 'image/png', dataRef: 'ncw://attachments/sessions/s/2.png' }] },
      isError: false
    }
  ], 7),
  assistantMessage('b2', [{ type: 'text', text: '改好了' }], 8)
]

const created: string[] = []
function seeded(id: string): ReturnType<typeof sessionStore> {
  created.push(id)
  const store = sessionStore(id)
  store.setState((s) => ({ transcript: { ...s.transcript, messages: history } }))
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

describe('截断重跑后的工具卡片', () => {
  it('★ 更早那张生图卡的工具状态留着,不会被整表清空', async () => {
    const s = seeded('rerun-tools')

    await s.getState().editMessage('u2', '再来一张,改成酒吧', true, options())

    expect(s.getState().transcript.tools['image']).toMatchObject({
      name: 'generate_image',
      status: 'ok',
      output: { images: [{ mime: 'image/png', dataRef: 'ncw://attachments/sessions/s/1.png' }] }
    })
  })

  it('被切掉那一轮的 callId 不在结果里 —— 清空的用意是「别留下它」', async () => {
    const s = seeded('rerun-cut')

    await s.getState().editMessage('u2', '再来一张,改成酒吧', true, options())

    expect(s.getState().transcript.tools['to-be-cut']).toBeUndefined()
  })

  it('子代理卡片同理:留在历史里那个还认得自己', async () => {
    const s = seeded('rerun-subagents')

    await s.getState().editMessage('u2', '再来一张,改成酒吧', true, options())

    expect(s.getState().transcript.subagents['task']).toMatchObject({
      childRunId: 'child-1',
      status: 'done',
      description: '查资料'
    })
  })

  it('不截断的纯文本编辑不动工具状态 —— 那条路本来就没被清过', async () => {
    const s = seeded('rerun-text-only')
    s.setState((state) => ({
      transcript: { ...state.transcript, tools: { 'image': { callId: 'image', name: 'generate_image', input: undefined, status: 'ok' } } }
    }))

    await s.getState().editMessage('u2', '换个说法', false, options())

    expect(Object.keys(s.getState().transcript.tools)).toEqual(['image'])
  })
})
