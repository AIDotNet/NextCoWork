import { describe, expect, it } from 'vitest'
import { initSuggestWatch, stepSuggestWatch, suggestEligible, type SuggestWatch } from '../suggest-trigger'

function steps(w: SuggestWatch, seq: Array<{ running: boolean; runEndedAt: number | undefined }>): number[] {
  const fired: number[] = []
  let cur = w
  for (const now of seq) {
    const r = stepSuggestWatch(cur, now)
    cur = r.watch
    if (r.ended !== undefined) fired.push(r.ended)
  }
  return fired
}

describe('stepSuggestWatch', () => {
  it('看着一轮跑完 → 报一次', () => {
    const w = initSuggestWatch(undefined, false)
    expect(steps(w, [
      { running: true, runEndedAt: undefined },
      { running: false, runEndedAt: 100 },
      { running: false, runEndedAt: 100 }
    ])).toEqual([100])
  })

  it('挂载那一刻就结束了的那轮不算(切 Tab 回来)', () => {
    const w = initSuggestWatch(50, false)
    expect(steps(w, [{ running: false, runEndedAt: 50 }])).toEqual([])
  })

  it('★ 没看见它跑过的不算(快照恢复直接写 runEndedAt)', () => {
    const w = initSuggestWatch(undefined, false)
    expect(steps(w, [{ running: false, runEndedAt: 77 }])).toEqual([])
  })

  it('挂载时正在跑也算看见了;下一轮照常再报', () => {
    const w = initSuggestWatch(undefined, true)
    expect(steps(w, [
      { running: false, runEndedAt: 100 },
      { running: true, runEndedAt: undefined },
      { running: false, runEndedAt: 200 }
    ])).toEqual([100, 200])
  })
})

describe('suggestEligible', () => {
  const ok = { status: 'done' as const, queued: 0, goalActive: false, planMode: false, skillSourceId: null, sessionId: 's1' }
  it('正常跑完的普通会话', () => {
    expect(suggestEligible(ok)).toBe(true)
  })
  it.each([
    ['报错', { status: 'error' as const }],
    ['被停', { status: 'aborted' as const }],
    ['还有排队消息', { queued: 1 }],
    ['目标挂着', { goalActive: true }],
    ['plan 模式', { planMode: true }],
    ['提炼会话', { skillSourceId: 'src' }],
    ['会话元数据还没读到', { skillSourceId: undefined }],
    ['草稿 Tab', { sessionId: null }]
  ])('%s → 不建议', (_name, over) => {
    expect(suggestEligible({ ...ok, ...over })).toBe(false)
  })
})
