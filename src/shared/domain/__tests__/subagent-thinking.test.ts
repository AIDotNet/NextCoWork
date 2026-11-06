import { describe, expect, it } from 'vitest'
import { INHERIT_THINKING, isSubagentThinking, subagentThinkingSelection } from '../subagent-thinking'

describe('子代理思考档位选择', () => {
  it('子代理文件声明的档位盖过设置里那一栏', () => {
    expect(subagentThinkingSelection('high', 'low', 'medium')).toBe('high')
  })

  it('没声明时,设置里那一栏盖过父 run', () => {
    expect(subagentThinkingSelection(undefined, 'low', 'medium')).toBe('low')
  })

  it('设置里是「跟随对话」时落回父 run 这一轮的档位', () => {
    expect(subagentThinkingSelection(undefined, INHERIT_THINKING, 'medium')).toBe('medium')
  })

  it('三档都没配也不会崩 —— 落回父 run', () => {
    expect(subagentThinkingSelection(undefined, INHERIT_THINKING, 'auto')).toBe('auto')
  })
})

describe('isSubagentThinking', () => {
  it('合法档位放行', () => {
    expect(isSubagentThinking('high')).toBe(true)
  })

  it('inherit 本身放行', () => {
    expect(isSubagentThinking(INHERIT_THINKING)).toBe(true)
  })

  it('认不出的字符串拦下', () => {
    expect(isSubagentThinking('deep')).toBe(false)
  })

  it('非字符串拦下', () => {
    expect(isSubagentThinking(undefined)).toBe(false)
    expect(isSubagentThinking(42)).toBe(false)
  })
})
