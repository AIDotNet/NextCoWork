import { describe, expect, it } from 'vitest'
import type { FileEntry } from '../../../../../shared/domain/file-tree'
import type { Row } from '../flatten'
import { enterDelays, treeKeyAction } from '../tree-rows'

/**
 * 文件树的键盘导航与入场动画挑选。两者都不会报错,只会表现为「按 ← 跳到了奇怪的地方」
 * 或「切回文件树时整棵树又落了一遍」,所以写成断言钉住。
 */

const row = (path: string, depth: number, kind: FileEntry['kind'] = 'file'): Row => ({
  entry: { name: path.slice(path.lastIndexOf('/') + 1), path, kind, hidden: false },
  depth
})

/*
    src/            0  (展开)
      lib/          1  (展开)
        a.ts        2
      b.ts          1
    docs/           0  (收起)
    README.md       0
*/
const ROWS: Row[] = [
  row('src', 0, 'dir'),
  row('src/lib', 1, 'dir'),
  row('src/lib/a.ts', 2),
  row('src/b.ts', 1),
  row('docs', 0, 'dir'),
  row('README.md', 0)
]
const EXPANDED = new Set(['src', 'src/lib'])

describe('treeKeyAction', () => {
  it('↑↓ 在相邻行之间移动,到头就不动(返回 null,不吞键)', () => {
    expect(treeKeyAction(ROWS, 0, 'ArrowDown', EXPANDED)).toEqual({ kind: 'focus', index: 1 })
    expect(treeKeyAction(ROWS, 3, 'ArrowUp', EXPANDED)).toEqual({ kind: 'focus', index: 2 })
    expect(treeKeyAction(ROWS, 0, 'ArrowUp', EXPANDED)).toBeNull()
    expect(treeKeyAction(ROWS, 5, 'ArrowDown', EXPANDED)).toBeNull()
  })

  it('Home / End 跳到首尾', () => {
    expect(treeKeyAction(ROWS, 3, 'Home', EXPANDED)).toEqual({ kind: 'focus', index: 0 })
    expect(treeKeyAction(ROWS, 0, 'End', EXPANDED)).toEqual({ kind: 'focus', index: 5 })
  })

  it('→ 在收起的目录上展开,在展开的目录上进入第一个子项,在文件上不管', () => {
    expect(treeKeyAction(ROWS, 4, 'ArrowRight', EXPANDED)).toEqual({ kind: 'expand', path: 'docs' })
    expect(treeKeyAction(ROWS, 0, 'ArrowRight', EXPANDED)).toEqual({ kind: 'focus', index: 1 })
    expect(treeKeyAction(ROWS, 5, 'ArrowRight', EXPANDED)).toBeNull()
  })

  it('展开了但子项还没列回来时,→ 停在原地', () => {
    const pending = [row('src', 0, 'dir'), row('README.md', 0)]
    expect(treeKeyAction(pending, 0, 'ArrowRight', new Set(['src']))).toBeNull()
  })

  it('← 在展开的目录上收起;在其它行上回到父目录那一行', () => {
    expect(treeKeyAction(ROWS, 1, 'ArrowLeft', EXPANDED)).toEqual({ kind: 'collapse', path: 'src/lib' })
    expect(treeKeyAction(ROWS, 2, 'ArrowLeft', EXPANDED)).toEqual({ kind: 'focus', index: 1 })
    // ★ b.ts 的上一行是更深的 a.ts,父目录要越过它往回找
    expect(treeKeyAction(ROWS, 3, 'ArrowLeft', EXPANDED)).toEqual({ kind: 'focus', index: 0 })
    expect(treeKeyAction(ROWS, 5, 'ArrowLeft', EXPANDED)).toBeNull()
  })

  it('Enter / 空格是「打开」;别的键不归树管', () => {
    expect(treeKeyAction(ROWS, 5, 'Enter', EXPANDED)).toEqual({ kind: 'activate' })
    expect(treeKeyAction(ROWS, 5, ' ', EXPANDED)).toEqual({ kind: 'activate' })
    expect(treeKeyAction(ROWS, 5, 'Tab', EXPANDED)).toBeNull()
  })
})

describe('enterDelays', () => {
  it('★ 与上一次完全相同的行一行都不播 —— 从快照恢复的树不该重新落一遍', () => {
    expect(enterDelays(ROWS, [...ROWS]).size).toBe(0)
  })

  it('只挑新露出来的行,按出现顺序错开 25ms', () => {
    const collapsed = ROWS.filter((r) => !r.entry.path.startsWith('src/'))
    const delays = enterDelays(collapsed, ROWS)
    expect([...delays.keys()]).toEqual(['src/lib', 'src/lib/a.ts', 'src/b.ts'])
    expect([...delays.values()]).toEqual([0, 25, 50])
  })

  it('错开封顶 100ms;一次最多给 40 行播,展开上千行的目录时后面的直接出现', () => {
    const many = Array.from({ length: 500 }, (_, i) => row(`node_modules/p${i}`, 1))
    const delays = enterDelays([], many)
    expect(delays.size).toBe(40)
    expect(Math.max(...delays.values())).toBe(100)
  })
})
