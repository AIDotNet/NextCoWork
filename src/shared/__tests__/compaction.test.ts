/**
 * 压缩边界 —— 「转录里哪一段还发给模型」的唯一判据。
 *
 * 需求:主进程装配请求、手动 /compact、渲染层画分隔线读的都是这里。三处各写一份
 * 「从哪切」就会复现旧检查点表那种**零报错**的错位:界面说压过了、请求里却还是全量。
 * 所以这组用例钉的是这个函数本身的边界条件,而不是某一处调用点的表现。
 */
import { describe, expect, it } from 'vitest'
import type { AgentMessage, ContentPart } from '../agent/message'
import { assistantMessage, userMessage } from '../agent/message'
import { compactBoundaryOf, lastCompactBoundaryIndex, messagesForModel } from '../agent/compaction'

const NOW = Date.UTC(2026, 8, 4, 12, 0, 0)

function ask(id: string): AgentMessage {
  return userMessage(id, [{ type: 'text', text: `问题 ${id}` }], NOW)
}

function boundary(id: string, summary = '摘要'): AgentMessage {
  const parts: ContentPart[] = [
    { type: 'compact_boundary', trigger: 'auto', preTokens: 200_000, postTokens: 8_000, summary },
    { type: 'text', text: summary }
  ]
  return { ...userMessage(id, parts, NOW), internal: true, parts }
}

describe('compactBoundaryOf', () => {
  it('认出边界块,普通消息返回 undefined', () => {
    expect(compactBoundaryOf(boundary('b1'))?.summary).toBe('摘要')
    expect(compactBoundaryOf(ask('u1'))).toBeUndefined()
    expect(compactBoundaryOf(assistantMessage('a1', [{ type: 'text', text: '好' }], NOW))).toBeUndefined()
  })
})

describe('messagesForModel', () => {
  it('没压缩过时整段照发,且不是同一个数组引用', () => {
    const messages = [ask('u1'), ask('u2')]
    expect(messagesForModel(messages).map((m) => m.id)).toEqual(['u1', 'u2'])
    expect(messagesForModel(messages)).not.toBe(messages)
  })

  /**
   * ★★ 边界消息**自己必须留下**:摘要正文就住在它的 text 块里。
   * 连它一起切掉的表现是「压缩之后模型什么都不记得」,而转录、分隔线、
   * 界面上的一切看起来都正常。
   */
  it('★★ 从边界(含)切起', () => {
    const out = messagesForModel([ask('u1'), ask('u2'), boundary('b1'), ask('u3')])
    expect(out.map((m) => m.id)).toEqual(['b1', 'u3'])
  })

  /** 压过两次时以最后一条为准 —— 第一段摘要已经被第二次压缩读进去了。 */
  it('★ 多条边界时只认最后一条', () => {
    const out = messagesForModel([ask('u1'), boundary('b1'), ask('u2'), boundary('b2'), ask('u3')])
    expect(out.map((m) => m.id)).toEqual(['b2', 'u3'])
  })

  /** 边界是最后一条(手动 /compact 刚压完)时,发出去的就是它自己。 */
  it('边界在末尾时只剩它一条', () => {
    expect(messagesForModel([ask('u1'), boundary('b1')]).map((m) => m.id)).toEqual(['b1'])
  })

  it('空转录返回空数组', () => {
    expect(messagesForModel([])).toEqual([])
    expect(lastCompactBoundaryIndex([])).toBe(-1)
  })
})

describe('lastCompactBoundaryIndex', () => {
  it('没有边界返回 -1,有则返回最后一条的下标', () => {
    expect(lastCompactBoundaryIndex([ask('u1'), ask('u2')])).toBe(-1)
    expect(lastCompactBoundaryIndex([ask('u1'), boundary('b1'), ask('u2')])).toBe(1)
    expect(lastCompactBoundaryIndex([boundary('b1'), boundary('b2')])).toBe(1)
  })

  /**
   * ★ 删轮 / 编辑重跑把边界消息删掉之后,它描述的那次压缩**自然失效** ——
   * 这正是用转录内边界替换检查点表的理由:检查点会变成一条锚不住的孤儿,
   * 而孤儿检查点曾经让界面显示一条指不到任何位置的压缩线。
   */
  it('★ 边界被删掉后判据自动退回「没压过」', () => {
    const transcript = [ask('u1'), boundary('b1'), ask('u2')]
    const afterDelete = transcript.filter((m) => m.id !== 'b1')
    expect(lastCompactBoundaryIndex(afterDelete)).toBe(-1)
    expect(messagesForModel(afterDelete).map((m) => m.id)).toEqual(['u1', 'u2'])
  })
})
