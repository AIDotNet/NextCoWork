/**
 * 转录按页读、按 id 改 —— 渲染层只持有最近一页之后,这两件事必须在主进程的完整历史上成立。
 *
 * 钉的是三条:
 * 1. **页从一轮的开头切起**:页首那条 tool_result 找不到它的 tool_call 的话,卡片画不出来;
 * 2. **往前翻页接得上**:前一页的末尾紧挨着后一页的开头,不重不漏,`hasMore` 说实话;
 * 3. **按 id 改写不碰页外的历史**:渲染层手里只有一页,拿那一页整段 `replaceHistory`
 *    会把页外的历史当成「删掉了」—— 这正是把编辑改成按 id 的原因。
 */
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import type { AgentMessage } from '../../../shared/agent/message'
import { assistantMessage, userMessage } from '../../../shared/agent/message'
import { editUserMessage, removeSpan, replySpan, turnSpan } from '../../../shared/agent/history-edit'
import { closeDatabase, openDatabase } from '../index'
import * as repo from '../repo'

let dir = ''

beforeEach(() => {
  closeDatabase()
  dir = mkdtempSync(join(tmpdir(), 'nextcowork-history-page-'))
  openDatabase(dir)
  repo.createSession({ id: 's', workspaceId: 'w', rootPathAtCreation: '/w' })
})

afterEach(() => {
  closeDatabase()
  rmSync(dir, { recursive: true, force: true })
})

/** 一轮:提问 → 带 tool_call 的助手 → 工具回执 → 收尾的助手 */
function turn(n: number): AgentMessage[] {
  return [
    userMessage(`u${n}`, [{ type: 'text', text: `问题 ${n}` }], n * 10),
    assistantMessage(`a${n}`, [{ type: 'tool_call', callId: `c${n}`, name: 'Read', input: { path: `${n}.ts` } }], n * 10 + 1),
    userMessage(`r${n}`, [{ type: 'tool_result', callId: `c${n}`, output: { content: '…' }, isError: false }], n * 10 + 2),
    assistantMessage(`b${n}`, [{ type: 'text', text: `回答 ${n}` }], n * 10 + 3)
  ]
}

function seed(turns: number): AgentMessage[] {
  const history = Array.from({ length: turns }, (_, i) => turn(i + 1)).flat()
  repo.replaceHistory('s', history)
  return history
}

const ids = (messages: readonly AgentMessage[]): string[] => messages.map((m) => m.id)

describe('按页读', () => {
  it('★ 页首补到一轮的开头,不从 tool_result 切起', () => {
    seed(5)
    // 最新 3 条落在第 5 轮中间:r5 / b5 前面还有 u5、a5
    const page = repo.getHistoryPage('s', 3)
    expect(ids(page.messages)).toEqual(['u5', 'a5', 'r5', 'b5'])
    expect(page.hasMore).toBe(true)
  })

  it('往前翻页接得上:不重不漏,翻到头时 hasMore 为 false', () => {
    const history = seed(4)
    const seen: string[] = []
    let page = repo.getHistoryPage('s', 4)
    seen.unshift(...ids(page.messages))
    while (page.hasMore) {
      page = repo.getHistoryPage('s', 4, page.messages[0]!.id)
      seen.unshift(...ids(page.messages))
    }
    expect(seen).toEqual(ids(history))
  })

  it('锚点不在这条会话里:空页,不去猜', () => {
    seed(2)
    expect(repo.getHistoryPage('s', 10, 'nope')).toEqual({ messages: [], hasMore: false })
  })

  it('短会话一页读完', () => {
    const history = seed(1)
    expect(repo.getHistoryPage('s', 200)).toEqual({ messages: history, hasMore: false })
  })

  it('单轮含数百条工具消息时限制补齐数量，继续翻页仍不重不漏', () => {
    const history = [
      userMessage('long-question', [{ type: 'text', text: 'Long task' }], 0),
      ...Array.from({ length: 720 }, (_, index) => assistantMessage(`step-${index}`, [
        { type: 'text', text: `Step ${index}` }
      ], index + 1))
    ]
    repo.replaceHistory('s', history)
    const latest = repo.getHistoryPage('s', 60)
    expect(latest.messages).toHaveLength(560)
    expect(latest.messages.at(-1)?.id).toBe('step-719')
    expect(latest.hasMore).toBe(true)
    const earlier = repo.getHistoryPage('s', 60, latest.messages[0]!.id)
    expect(earlier.hasMore).toBe(false)
    expect([...earlier.messages, ...latest.messages]).toEqual(history)
    // Breaking the boundary iterator must release it so a subsequent read still works.
    expect(repo.getHistoryPage('s', 60)).toEqual(latest)
  })

  it('只返回本页消息的 run 归属，并隔离其他会话和无归属消息', () => {
    const history = seed(3)
    for (const message of history) repo.commitMessage('s', message, 'run-s')
    const legacy = userMessage('legacy', [{ type: 'text', text: 'Legacy' }], 100)
    repo.commitMessage('s', legacy)
    repo.createSession({ id: 'other', workspaceId: 'w', rootPathAtCreation: '/w' })
    repo.commitMessage('other', userMessage('foreign', [{ type: 'text', text: 'Other' }], 1), 'run-other')

    expect(repo.messageRunsOf('s', ['u3', 'b3', 'legacy', 'foreign', 'missing'])).toEqual({ u3: 'run-s', b3: 'run-s' })
    expect(repo.messageRunsOf('s', [])).toEqual({})
    expect(Object.keys(repo.messageRunsOf('s'))).toHaveLength(history.length)
  })
})

describe('按 id 改写(共享规则)', () => {
  it('★★ 只有一页时按 id 删一轮:页外的历史原样留着', () => {
    const history = seed(4)
    // 渲染层只拿到最后一页 —— 旧做法拿它整段 replaceHistory,前三轮就没了
    const page = repo.getHistoryPage('s', 4)
    expect(ids(page.messages)).toEqual(['u4', 'a4', 'r4', 'b4'])

    const span = turnSpan(history, 'u4')!
    repo.replaceHistory('s', removeSpan(history, span))

    expect(ids(repo.getHistory('s'))).toEqual(ids(history.slice(0, 12)))
  })

  it('删一轮吃到下一条可见提问之前;删回复吃掉紧随其后的工具回执', () => {
    const history = [...turn(1), ...turn(2)]
    expect(turnSpan(history, 'u1')).toEqual([0, 4])
    expect(replySpan(history, 'a1', 'a1')).toEqual([1, 3])
    expect(turnSpan(history, 'a1')).toBeNull()
  })

  it('编辑保留附件;截断重跑把这条和之后的全部去掉', () => {
    const image = { type: 'image' as const, mime: 'image/png', dataRef: 'ncw://attachments/sessions/s/x.png' }
    const history = [userMessage('u1', [{ type: 'text', text: '旧' }, image], 1), assistantMessage('b1', [{ type: 'text', text: '答' }], 2)]

    expect(editUserMessage(history, 'u1', '新', false)?.[0]?.parts).toEqual([{ type: 'text', text: '新' }, image])
    expect(editUserMessage(history, 'u1', '新', true)).toEqual([])
    expect(editUserMessage(history, 'nope', '新', false)).toBeNull()
  })
})
