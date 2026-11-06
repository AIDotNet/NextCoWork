import { describe, expect, it } from 'vitest'
import {
  thinkingElapsedMs,
  thinkingStatsOfLive,
  thinkingStatsOfPart,
  thinkingTokenReading
} from '../agent/thinking-stats'
import { estimateTokens } from '../agent/token-estimate'

describe('thinkingElapsedMs', () => {
  it('已提交的块读落盘时长,与 now 无关', () => {
    expect(thinkingElapsedMs({ durationMs: 4_200 }, false, 999_999)).toBe(4_200)
  })

  it('没有任何计时事实(旧转录)时返回 undefined,不拿 0 兜底', () => {
    expect(thinkingElapsedMs({}, false, 10_000)).toBeUndefined()
    expect(thinkingElapsedMs({ tokens: 12 }, true, 10_000)).toBeUndefined()
  })

  it('还在长的流式块按 now 走表', () => {
    expect(thinkingElapsedMs({ startedAt: 1_000, endedAt: 3_000 }, true, 7_000)).toBe(6_000)
  })

  it('★ 不再长的流式块定格在最后一个思考增量上,不随 now 继续涨', () => {
    expect(thinkingElapsedMs({ startedAt: 1_000, endedAt: 3_000 }, false, 7_000)).toBe(2_000)
  })

  it('时钟回拨算出负数时夹到 0', () => {
    expect(thinkingElapsedMs({ startedAt: 5_000 }, true, 4_000)).toBe(0)
    expect(thinkingElapsedMs({ durationMs: -3 }, false, 0)).toBe(0)
  })
})

describe('thinkingTokenReading', () => {
  it('上游真值优先,且不标约数', () => {
    expect(thinkingTokenReading('随便什么', { tokens: 321 })).toEqual({ count: 321, estimated: false })
  })

  it('没有真值时按正文估算,并标为约数(与上下文估算同一个函数)', () => {
    const text = 'Let me check the config first. 先看配置。'
    expect(thinkingTokenReading(text, {})).toEqual({ count: estimateTokens(text), estimated: true })
  })

  it('正文为空(redacted / 只有密文)时不给读数 —— 估出一个 0 比不画更误导', () => {
    expect(thinkingTokenReading('  \n', {})).toBeUndefined()
  })
})

describe('thinkingStatsOf*', () => {
  it('已提交 part 只取落盘的两格,缺席的不补 undefined 键', () => {
    expect(thinkingStatsOfPart({ type: 'thinking', text: 'x', durationMs: 5, tokens: 9 })).toEqual({ durationMs: 5, tokens: 9 })
    expect(thinkingStatsOfPart({ type: 'thinking', text: 'x' })).toEqual({})
  })

  it('流式块只取起止时间', () => {
    expect(thinkingStatsOfLive({ index: 0, kind: 'thinking', text: 'x', startedAt: 1, endedAt: 2 })).toEqual({ startedAt: 1, endedAt: 2 })
    expect(thinkingStatsOfLive({ index: 0, kind: 'text', text: 'x' })).toEqual({})
  })
})
