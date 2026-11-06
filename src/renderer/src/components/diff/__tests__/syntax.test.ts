/**
 * 两层区间合并(词级高亮 × 语法 token)与「按行切 token」的断言。
 *
 * 需求:这两个纯函数是 diff 能同时显示「改了哪几个词」和「这是个关键字」的全部
 * 所在,而它们错了都**不会报错** —— 只会吞掉几个字、或者把颜色套到错行去,
 * 而 diff 本来就密,肉眼很难发现少了一个字符。所以第一条断言永远是
 * 「拼回去等于原文」。
 */
import { describe, expect, it } from 'vitest'
import { diffSides, styleSpans, tokensByLine } from '../syntax'
import { lineText, plainLine, type DiffSpan } from '../model'

const join = (spans: Array<{ text: string }>): string => spans.map((s) => s.text).join('')

describe('styleSpans', () => {
  const spans: DiffSpan[] = [{ text: 'const ', hi: false }, { text: 'a', hi: true }, { text: ' = 1', hi: false }]

  it('没有 token 时原样返回,不吞字也不加类名', () => {
    const styled = styleSpans(spans, [])
    expect(join(styled)).toBe('const a = 1')
    expect(styled.every((s) => s.className === '')).toBe(true)
  })

  it('token 跨过词级边界时按两边切开,拼回去仍是原文', () => {
    // `const a` 被语法高亮当成一段,而 `a` 是词级新增 —— 两层边界不重合
    const styled = styleSpans(spans, [{ from: 0, to: 7, className: 'tok-keyword' }])
    expect(join(styled)).toBe('const a = 1')
    expect(styled.map((s) => [s.text, s.hi, s.className])).toEqual([
      ['const ', false, 'tok-keyword'],
      ['a', true, 'tok-keyword'],
      [' = 1', false, '']
    ])
  })

  it('token 之间的空隙不带类名,相邻同类片段合并成一段', () => {
    const styled = styleSpans([{ text: 'a + b', hi: false }], [
      { from: 0, to: 1, className: 'tok-name' },
      { from: 4, to: 5, className: 'tok-name' }
    ])
    expect(join(styled)).toBe('a + b')
    expect(styled.map((s) => s.className)).toEqual(['tok-name', '', 'tok-name'])
  })
})

describe('tokensByLine', () => {
  it('偏移量换算成行内偏移,换行符不属于任何一行', () => {
    const byLine = tokensByLine('ab\ncd', [{ from: 3, to: 5, className: 'tok-string' }])
    expect(byLine).toEqual([[], [{ from: 0, to: 2, className: 'tok-string' }]])
  })

  it('跨行 token(多行字符串/块注释)在每一行上各留一段', () => {
    const byLine = tokensByLine('a\nb\nc', [{ from: 0, to: 5, className: 'tok-comment' }])
    expect(byLine.map((tokens) => tokens.length)).toEqual([1, 1, 1])
    expect(byLine[1]).toEqual([{ from: 0, to: 1, className: 'tok-comment' }])
  })
})

describe('diffSides', () => {
  it('上下文行进新侧,删除行进旧侧,hunk 标题两侧都不进', () => {
    const lines = [
      plainLine('hunk', '@@ -1,2 +1,2 @@'),
      plainLine('context', 'keep'),
      plainLine('del', 'old'),
      plainLine('add', 'new')
    ]
    const sides = diffSides(lines)

    expect(sides.oldText).toBe('keep\nold')
    expect(sides.newText).toBe('keep\nnew')
    expect(sides.index).toEqual([
      { side: null, line: 0 },
      { side: 'new', line: 0 },
      { side: 'old', line: 1 },
      { side: 'new', line: 1 }
    ])
  })

  it('还原出来的两侧文本逐行对得上原始行', () => {
    const lines = [plainLine('context', 'a'), plainLine('add', 'b')]
    const sides = diffSides(lines)
    expect(sides.newText.split('\n')).toEqual(lines.map(lineText))
  })
})
