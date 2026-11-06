import { describe, expect, it } from 'vitest'
import type { AgentMessage } from '../../../shared/agent/message'
import { userMessage } from '../../../shared/agent/message'
import { decorate } from '../context-assembler'

const history: AgentMessage[] = [userMessage('u1', [{ type: 'text', text: '开始提炼' }], 1)]

function headText(messages: readonly AgentMessage[]): string {
  const part = messages[0]?.parts[0]
  return part?.type === 'text' ? part.text : ''
}

describe('decorate · skill extraction', () => {
  it('injects the skill extraction block in the head, after project instructions', () => {
    const out = decorate(history, { projectInstructions: 'RULES-HERE', skillExtraction: '<skill-extraction>BLOCK</skill-extraction>' })
    const head = headText(out)
    expect(head.startsWith('<system-reminder>')).toBe(true)
    expect(head.indexOf('RULES-HERE')).toBeGreaterThan(-1)
    expect(head.indexOf('BLOCK')).toBeGreaterThan(head.indexOf('RULES-HERE'))
    // 原消息正文排在头块之后,转录本身不被改写
    expect(out[0]?.parts.at(-1)).toEqual({ type: 'text', text: '开始提炼' })
    expect(history[0]?.parts).toHaveLength(1)
  })

  it('injects the block even when the workspace has no AGENTS.md', () => {
    const head = headText(decorate(history, { skillExtraction: 'BLOCK' }))
    expect(head).toContain('Skill extraction task for this conversation')
    expect(head).toContain('BLOCK')
    expect(head).not.toContain('<project-instructions>')
  })

  it('leaves ordinary sessions untouched', () => {
    expect(decorate(history, {})).toBe(history)
  })
})
