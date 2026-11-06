import { describe, expect, it } from 'vitest'
import { SkillActivationBarrier, matchesProjectSkillPath, nextActiveSkillIds, sameSkillIdList } from '../activation'

describe('nextActiveSkillIds', () => {
  it('materializes the implicit all-selection before adding a skill', () => {
    expect(nextActiveSkillIds([], 'all', ['a', 'b'], 'c', true)).toEqual(['a', 'b', 'c'])
    expect(nextActiveSkillIds([], undefined, ['a', 'b'], 'a', false)).toEqual(['b'])
  })

  it('appends to an explicit selection without duplicates', () => {
    expect(nextActiveSkillIds(['a'], 'explicit', ['a', 'b'], 'b', true)).toEqual(['a', 'b'])
    expect(nextActiveSkillIds(['a', 'b'], 'explicit', ['a', 'b'], 'b', true)).toEqual(['a', 'b'])
  })

  it('treats an empty explicit selection as nothing selected', () => {
    expect(nextActiveSkillIds([], 'explicit', ['a', 'b'], 'b', true)).toEqual(['b'])
  })
})

describe('Skill activation helpers', () => {
  it('matches the written directory even when frontmatter uses another valid name', () => {
    expect(matchesProjectSkillPath('/repo/.next-cowork/skills/pricing-rule/SKILL.md', '.next-cowork/skills', 'pricing-rule')).toBe(true)
    expect(matchesProjectSkillPath('.next-cowork\\skills\\pricing-rule\\SKILL.md', '.next-cowork/skills', 'pricing-rule')).toBe(true)
    expect(matchesProjectSkillPath('/repo/.next-cowork/skills/other/SKILL.md', '.next-cowork/skills', 'pricing-rule')).toBe(false)
  })

  it('detects same-length selection changes', () => {
    expect(sameSkillIdList(['a', 'b'], ['a', 'b'])).toBe(true)
    expect(sameSkillIdList(['a', 'b'], ['b', 'a'])).toBe(false)
  })
})

describe('SkillActivationBarrier', () => {
  it('does not make a waiter linger when nothing was written', async () => {
    const barrier = new SkillActivationBarrier()
    expect(await barrier.wait('w')).toBe(false)
  })

  it('holds a waiter until the registered work is released', async () => {
    const barrier = new SkillActivationBarrier()
    const release = barrier.begin('w')
    let passed = false
    const waiting = barrier.wait('w').then((waited) => { passed = true; return waited })
    await Promise.resolve()
    expect(passed, '还没放就走过去了 —— 下一轮会在激活完成前开跑').toBe(false)
    release()
    expect(await waiting).toBe(true)
    // 放完就没人了:后来的等待者不该再被拦住
    expect(await barrier.wait('w')).toBe(false)
  })

  it('waits for work registered while it is already waiting', async () => {
    const barrier = new SkillActivationBarrier()
    const first = barrier.begin('w')
    const waiting = barrier.wait('w')
    // 等待期间另一个子 run 收尾又会挂一笔 —— 漏掉它的话那一笔的激活就没人等
    const second = barrier.begin('w')
    first()
    await Promise.resolve()
    let settled = false
    void waiting.then(() => { settled = true })
    await Promise.resolve()
    expect(settled).toBe(false)
    second()
    expect(await waiting).toBe(true)
  })

  it('keeps workspaces apart', async () => {
    const barrier = new SkillActivationBarrier()
    barrier.begin('a')
    expect(await barrier.wait('b')).toBe(false)
  })

  it('releasing twice does not release a later wait', async () => {
    const barrier = new SkillActivationBarrier()
    const release = barrier.begin('w')
    release()
    const again = barrier.begin('w')
    let settled = false
    const waiting = barrier.wait('w').then(() => { settled = true })
    release()
    await Promise.resolve()
    expect(settled, '重复释放不得放走后来登记的那一笔').toBe(false)
    again()
    await waiting
  })

  it('clears every workspace on teardown', async () => {
    const barrier = new SkillActivationBarrier()
    barrier.begin('a')
    barrier.begin('b')
    barrier.clear()
    expect(await barrier.wait('a')).toBe(false)
    expect(await barrier.wait('b')).toBe(false)
  })
})
