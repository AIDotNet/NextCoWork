import { describe, expect, it } from 'vitest'
import type { Ghost } from '../ghost'
import { INITIAL_GHOST_STATE, reduceGhost, visibleGhost, type GhostAction, type GhostState } from '../ghost-state'

function run(actions: GhostAction[], from: GhostState = { ...INITIAL_GHOST_STATE, focused: true }): { state: GhostState; ignored: number } {
  let state = from
  let ignored = 0
  for (const a of actions) {
    const r = reduceGhost(state, a)
    state = r.state
    ignored += r.events.length
  }
  return { state, ignored }
}

const NONE = { history: null, file: null }
const view = (s: GhostState, draft: string, extra: Partial<Parameters<typeof visibleGhost>[1]> = {}): string | undefined =>
  visibleGhost(s, { draft, popupOpen: false, attachments: 0, local: NONE, ...extra })?.remainder

describe('建议下一步的生命周期', () => {
  const arrive: GhostAction = { type: 'suggestion', trigger: 100, text: '跑一下测试', draft: '' }

  it('到达 → 空草稿上显示;跟打时剩后半截', () => {
    const { state } = run([arrive])
    expect(view(state, '')).toBe('跑一下测试')
    const typed = run([{ type: 'input', text: '跑一', caret: 2 }], state).state
    expect(view(typed, '跑一')).toBe('下测试')
  })

  it('打了别的 → 清掉并记一次「被无视」', () => {
    const { state, ignored } = run([arrive, { type: 'input', text: '提', caret: 1 }])
    expect(state.suggest).toBeNull()
    expect(ignored).toBe(1)
  })

  it('★ 组词中不跟打也不清:草稿此刻是拼音', () => {
    const { state, ignored } = run([arrive, { type: 'composing', value: true }, { type: 'input', text: 'pao', caret: null }])
    expect(state.suggest).not.toBeNull()
    expect(ignored).toBe(0)
    // 组词期间不显示
    expect(view(state, 'pao')).toBeUndefined()
    // 上屏「跑」之后,剩下的接着出现
    const done = run([{ type: 'composing', value: false }, { type: 'input', text: '跑', caret: 1 }], state).state
    expect(view(done, '跑')).toBe('一下测试')
  })

  it('Esc 之后这一轮不再出现,哪怕删回空草稿、哪怕同一轮的结果又到一次', () => {
    const shown = run([arrive]).state
    const g = shown.suggest as Ghost
    const { state } = run([{ type: 'dismiss', ghost: g }, { type: 'input', text: '', caret: 0 }, arrive], shown)
    expect(view(state, '')).toBeUndefined()
    // 下一轮的照常出现
    const next = run([{ type: 'suggestion', trigger: 200, text: '提交', draft: '' }], state).state
    expect(view(next, '')).toBe('提交')
  })

  it('结果到的时候用户已经打了别的 → 丢掉', () => {
    const { state } = run([{ type: 'suggestion', trigger: 1, text: '跑一下测试', draft: '提交' }])
    expect(state.suggest).toBeNull()
  })

  it('托盘里有附件时不显示建议', () => {
    expect(view(run([arrive]).state, '', { attachments: 1 })).toBeUndefined()
  })

  it('发送了别的 → 记一次被无视并清空', () => {
    const { state, ignored } = run([arrive, { type: 'reset', reason: 'submit' }])
    expect(state.suggest).toBeNull()
    expect(ignored).toBe(1)
  })

  it('整条接受不算被无视', () => {
    const shown = run([arrive]).state
    const { state, ignored } = run([{ type: 'accept', ghost: shown.suggest as Ghost, partial: false }, { type: 'input', text: '跑一下测试', caret: 5 }], shown)
    expect(state.suggest).toBeNull()
    expect(ignored).toBe(0)
  })
})

describe('显示条件', () => {
  const history: Ghost = { source: 'history', base: '跑一下', text: '测试', key: 'history:跑一下测试' }
  const local = { history, file: null }

  it('粘贴之后等到下一次按键', () => {
    const held = run([{ type: 'paste' }]).state
    expect(view(held, '跑一下', { local })).toBeUndefined()
    expect(view(run([{ type: 'keydown' }], held).state, '跑一下', { local })).toBe('测试')
  })

  it('光标不在末尾、失焦、弹层开着、换行结尾都不显示', () => {
    const base = run([]).state
    expect(view(base, '跑一下', { local })).toBe('测试')
    expect(view(run([{ type: 'caret', text: '跑一下', caret: 1 }], base).state, '跑一下', { local })).toBeUndefined()
    expect(view(run([{ type: 'focus', value: false }], base).state, '跑一下', { local })).toBeUndefined()
    expect(view(base, '跑一下', { local, popupOpen: true })).toBeUndefined()
    expect(view(base, '跑一下\n', { local: { history: { ...history, base: '跑一下\n' }, file: null } })).toBeUndefined()
  })

  it('Esc 过的历史灰字在草稿清空前不再出现', () => {
    const base = run([]).state
    const dismissed = run([{ type: 'dismiss', ghost: history }], base).state
    expect(view(dismissed, '跑一下', { local })).toBeUndefined()
    const cleared = run([{ type: 'input', text: '', caret: 0 }], dismissed).state
    expect(view(cleared, '跑一下', { local })).toBe('测试')
  })
})
