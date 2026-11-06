/**
 * 输入框补全读库的那三条查询(`docs/composer-autocomplete.md`)。
 *
 * ★ 它们要回答的都是「用户**亲手**说过什么」,而库里 `role = 'user'` 的行远不止于此:
 *   工具结果、内部注入、子代理的转录、展开后的斜杠命令模板。每一组用例钉住一类「不算」。
 */
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { assistantMessage, toolResultMessage, userMessage, type AgentMessage } from '../../../shared/agent/message'
import { closeDatabase, openDatabase } from '../index'
import * as repo from '../repo'

let dir = ''

beforeEach(() => {
  closeDatabase()
  dir = mkdtempSync(join(tmpdir(), 'nextcowork-recent-prompts-'))
  openDatabase(dir)
})

afterEach(() => {
  closeDatabase()
  rmSync(dir, { recursive: true, force: true })
})

const AT = 1_700_000_000_000
const ask = (id: string, text: string, at = AT): AgentMessage => userMessage(id, [{ type: 'text', text }], at)
const say = (id: string, text: string, at = AT): AgentMessage => assistantMessage(id, [{ type: 'text', text }], at)

describe('recentSessionPrompts', () => {
  it('只取用户亲手发的消息,新的在前', () => {
    repo.ensureSession({ id: 's1', workspaceId: 'ws' })
    repo.commitMessage('s1', ask('u1', '第一句', AT))
    repo.commitMessage('s1', say('a1', '回答', AT + 1))
    repo.commitMessage('s1', toolResultMessage('t1', [{ type: 'tool_result', callId: 'c1', output: { content: 'ok' }, isError: false }], AT + 2))
    repo.commitMessage('s1', { ...ask('i1', '内部注入', AT + 3), internal: true })
    repo.commitMessage('s1', userMessage('img', [{ type: 'image', mime: 'image/png', dataRef: 'ref' }], AT + 4))
    repo.commitMessage('s1', ask('u2', '第二句', AT + 5))

    expect(repo.recentSessionPrompts('s1', 10).map((m) => m.id)).toEqual(['u2', 'u1'])
    expect(repo.recentSessionPrompts('s1', 1).map((m) => m.id)).toEqual(['u2'])
  })
})

describe('lastVisibleAssistantMessage', () => {
  it('跳过内部的助手消息', () => {
    repo.ensureSession({ id: 's1', workspaceId: 'ws' })
    repo.commitMessage('s1', say('a1', '给人看的回复', AT))
    repo.commitMessage('s1', { ...say('a2', '内部', AT + 1), internal: true })

    expect(repo.lastVisibleAssistantMessage('s1')?.id).toBe('a1')
    expect(repo.lastVisibleAssistantMessage('nope')).toBeUndefined()
  })
})

describe('recentWorkspacePrompts', () => {
  it('新的在前、去重,只看本工作区的顶层会话', () => {
    repo.ensureSession({ id: 's1', workspaceId: 'ws' })
    repo.ensureSession({ id: 's2', workspaceId: 'ws' })
    repo.ensureSession({ id: 'other', workspaceId: 'ws2' })
    repo.ensureSession({ id: 's1:sub:x', workspaceId: 'ws', parentSessionId: 's1' })
    repo.commitMessage('s1', ask('u1', '跑一下测试', AT))
    repo.commitMessage('s2', ask('u2', '提交并推送', AT + 1))
    repo.commitMessage('s2', ask('u3', '跑一下测试', AT + 2))
    repo.commitMessage('other', ask('o1', '别的工作区', AT + 3))
    repo.commitMessage('s1:sub:x', ask('sub1', '子代理的任务说明', AT + 4))

    expect(repo.recentWorkspacePrompts('ws')).toEqual(['跑一下测试', '提交并推送'])
  })

  it('过长或多行的不要(库里的斜杠命令是展开后的整份模板)', () => {
    repo.ensureSession({ id: 's1', workspaceId: 'ws' })
    repo.commitMessage('s1', ask('long', 'x'.repeat(501), AT))
    repo.commitMessage('s1', ask('lines', 'a\nb\nc\nd', AT + 1))
    repo.commitMessage('s1', ask('ok', '看一下 CI 为什么红了', AT + 2))

    expect(repo.recentWorkspacePrompts('ws')).toEqual(['看一下 CI 为什么红了'])
  })

  it('有上限', () => {
    repo.ensureSession({ id: 's1', workspaceId: 'ws' })
    for (let i = 0; i < 8; i++) repo.commitMessage('s1', ask(`u${String(i)}`, `指令 ${String(i)}`, AT + i))
    expect(repo.recentWorkspacePrompts('ws', { limit: 3 })).toEqual(['指令 7', '指令 6', '指令 5'])
  })
})
