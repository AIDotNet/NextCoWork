import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { Session } from '../../../../shared/domain/session'
import { DEFAULT_WORKSPACE_SETTINGS, type Workspace } from '../../../../shared/domain/workspace'

const state = vi.hoisted(() => ({
  create: vi.fn(),
  openSession: vi.fn(),
  send: vi.fn()
}))

vi.mock('../../services/skillify', () => ({ createSkillExtractionSession: state.create }))
vi.mock('../../stores/session', () => ({ sessionStore: () => ({ getState: () => ({ send: state.send }) }) }))
vi.mock('../../stores/tabs', () => ({ useTabsStore: { getState: () => ({ openSession: state.openSession }) } }))

import { AgentErrorException } from '../../services/ipc'
import { skillExtractionErrorKey, skillExtractionPrompt, skillExtractionSendOptions, startSkillExtraction } from './skill-extraction'

const workspace: Workspace = {
  id: 'w1',
  name: 'shop',
  rootPath: '/repo',
  settings: { ...DEFAULT_WORKSPACE_SETTINGS, defaultModel: 'workspace-default', permissionMode: 'ask', activeSkillIds: ['s1'], skillSelectionMode: 'explicit' },
  createdAt: 1,
  lastOpenedAt: 1
}

const session: Session = {
  id: 'x1',
  workspaceId: 'w1',
  title: '提炼 Skill · 改价格',
  model: 'source-model',
  modelProviderId: 'codex',
  mode: 'code',
  thinking: 'high',
  rootPathAtCreation: '/repo',
  status: 'idle',
  archived: false,
  favorited: false,
  createdAt: 1,
  updatedAt: 1,
  skillSource: { sessionId: 'src' }
}

const t = (key: string, params: Record<string, string | number> = {}): string =>
  `${key}${Object.entries(params).map(([k, v]) => `|${k}=${String(v)}`).join('')}`

beforeEach(() => {
  vi.clearAllMocks()
  state.create.mockResolvedValue(session)
  state.send.mockResolvedValue(undefined)
})

describe('skill extraction', () => {
  it('uses the source session model and code mode in send options', () => {
    const options = skillExtractionSendOptions(workspace, session)
    expect(options).toMatchObject({
      workspaceId: 'w1',
      depth: 0,
      mode: 'code',
      model: 'source-model',
      modelProviderId: 'codex',
      thinking: 'high',
      permissionMode: 'ask',
      skillIds: ['s1'],
      skillSelectionMode: 'explicit'
    })
  })

  it('appends the user hint to the trigger prompt only when present', () => {
    expect(skillExtractionPrompt(t, '改价格', '  ')).toBe('skillify.startPrompt|title=改价格')
    expect(skillExtractionPrompt(t, '改价格', ' 记录回滚 ')).toBe('skillify.startPrompt|title=改价格\n\nskillify.hintPrefix|hint=记录回滚')
  })

  it('creates the session, opens its tab, then sends the trigger prompt', async () => {
    await startSkillExtraction({ workspace, sourceSessionId: 'src', sourceTitle: '改价格', hint: '', t })
    expect(state.create).toHaveBeenCalledWith('src', 'skillify.sessionTitle|title=改价格')
    expect(state.openSession).toHaveBeenCalledWith('w1', 'x1', session.title)
    expect(state.send).toHaveBeenCalledWith('skillify.startPrompt|title=改价格', expect.objectContaining({ model: 'source-model', mode: 'code' }))
    expect(state.send.mock.invocationCallOrder[0]).toBeGreaterThan(state.openSession.mock.invocationCallOrder[0]!)
  })

  it('maps known refusals to their copy and everything else to the generic failure', () => {
    const refused = new AgentErrorException({ code: 'conflict', message: 'x', retryable: false, messageKey: 'skills.extraction.nested' })
    expect(skillExtractionErrorKey(refused)).toBe('skills.extraction.nested')
    expect(skillExtractionErrorKey(new AgentErrorException({ code: 'conflict', message: 'x', retryable: false, messageKey: 'skills.extraction.sourceRunning' }))).toBe('skills.extraction.sourceRunning')
    expect(skillExtractionErrorKey(new Error('skills.extraction.nested'))).toBe('skills.operationFailed')
    expect(skillExtractionErrorKey(new AgentErrorException({ code: 'unknown', message: 'boom', retryable: false }))).toBe('skills.operationFailed')
  })
})
