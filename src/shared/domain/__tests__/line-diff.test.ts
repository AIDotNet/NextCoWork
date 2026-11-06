import { describe, expect, it } from 'vitest'
import { countLineDiff, lcsOps } from '../line-diff'

describe('countLineDiff', () => {
  it('新建文件:所有行都算新增', () => {
    expect(countLineDiff(null, 'a\nb\nc')).toEqual({ additions: 3, deletions: 0 })
  })

  it('删除文件:所有行都算删除', () => {
    expect(countLineDiff('a\nb', null)).toEqual({ additions: 0, deletions: 2 })
  })

  it('改一行:一增一删', () => {
    expect(countLineDiff('a\nb\nc', 'a\nB\nc')).toEqual({ additions: 1, deletions: 1 })
  })

  it('纯新增行:只增不删', () => {
    expect(countLineDiff('a\nc', 'a\nb\nc')).toEqual({ additions: 1, deletions: 0 })
  })

  it('尾随空行不干扰计数', () => {
    expect(countLineDiff('a\n', 'a\nb\n')).toEqual({ additions: 1, deletions: 0 })
  })

  it('内容相同:零增零删', () => {
    expect(countLineDiff('a\nb', 'a\nb')).toEqual({ additions: 0, deletions: 0 })
  })

  it('与完整 LCS 回溯的计数一致(含公共首尾)', () => {
    let seed = 7
    const random = (): number => (seed = (seed * 1103515245 + 12345) % 2147483648) / 2147483648
    const text = (length: number): string => Array.from({ length }, () => 'abcde'[Math.floor(random() * 5)]).join('\n')
    for (let round = 0; round < 200; round++) {
      const shared = text(Math.floor(random() * 4))
      const before = [shared, text(Math.floor(random() * 12)), shared].join('\n')
      const after = [shared, text(Math.floor(random() * 12)), shared].join('\n')
      // 与 countLineDiff 同口径切行:剔掉唯一的尾随空行
      const lines = (value: string): string[] => value === '' ? [] : value.replace(/\n$/, '').split('\n')
      const ops = lcsOps(lines(before), lines(after), (x, y) => x === y)
      const expected = {
        additions: ops.filter((op) => op.tag === 'add').length,
        deletions: ops.filter((op) => op.tag === 'del').length
      }
      expect(countLineDiff(before, after)).toEqual(expected)
    }
  })

  it('大文件只改一行时不必比较整个文件', () => {
    const lines = Array.from({ length: 50_000 }, (_, index) => `line ${index}`)
    const changed = [...lines]
    changed[25_000] = 'changed'
    expect(countLineDiff(lines.join('\n'), changed.join('\n'))).toEqual({ additions: 1, deletions: 1 })
  })
})
