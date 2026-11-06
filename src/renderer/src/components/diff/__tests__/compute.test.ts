/**
 * `computeDiff` / `computeDiffHunks` / `hunkLines` 的断言。
 *
 * 盯的是「词级高亮什么时候该出现」和「hunk 的行号范围对不对」—— 两者错了都不会
 * 报错,只会让界面上的 diff 悄悄变得没有信息量,或者行号整体串位。
 */
import { describe, expect, it } from 'vitest'
import { computeDiff, computeDiffHunks, hunkLines } from '../compute'
import { lineText, type DiffLine, type DiffLineKind } from '../model'

/** 把行拍平成便于断言的紧凑形式。 */
const shape = (lines: DiffLine[]): string[] =>
  lines.map((line) => `${line.kind[0]}:${lineText(line)}`)

/** 取某一类行里被高亮(hi)的文本片段。 */
const hi = (lines: DiffLine[], kind: DiffLineKind): string[] =>
  lines
    .filter((line) => line.kind === kind)
    .flatMap((line) => line.spans.filter((s) => s.hi).map((s) => s.text))

describe('computeDiff', () => {
  it('完全相同的文本只产出 context 行', () => {
    const lines = computeDiff('a\nb', 'a\nb')
    expect(shape(lines)).toEqual(['c:a', 'c:b'])
    expect(hi(lines, 'del')).toEqual([])
    expect(hi(lines, 'add')).toEqual([])
  })

  it('改动的一对行做词级高亮,未变的词不高亮', () => {
    const lines = computeDiff(
      'import { locales } from "@/config";',
      'import { locales, hasLocale } from "@/config";'
    )
    // 一删一增,且拼回去就是原文
    expect(shape(lines)).toEqual([
      'd:import { locales } from "@/config";',
      'a:import { locales, hasLocale } from "@/config";'
    ])
    // 只有新增的词被高亮,公共部分不高亮
    expect(hi(lines, 'add').join('')).toContain('hasLocale')
    expect(hi(lines, 'add').join('')).not.toContain('locales')
    expect(hi(lines, 'del')).toEqual([])
  })

  it('保留上下文行,只把中间那行标成改动', () => {
    const lines = computeDiff('head\nfoo\ntail', 'head\nbar\ntail')
    expect(shape(lines)).toEqual(['c:head', 'd:foo', 'a:bar', 'c:tail'])
  })

  it('落单的新增行只靠行底色表达,不叠词级高亮', () => {
    const lines = computeDiff('a\nc', 'a\nb\nc')
    expect(shape(lines)).toEqual(['c:a', 'a:b', 'c:c'])
    // 整行都是新增时,再把整行标成高亮等于没标 —— 还会糊成一块实心底
    expect(hi(lines, 'add')).toEqual([])
  })

  it('落单的删除行同样不叠词级高亮', () => {
    const lines = computeDiff('a\nb\nc', 'a\nc')
    expect(shape(lines)).toEqual(['c:a', 'd:b', 'c:c'])
    expect(hi(lines, 'del')).toEqual([])
  })

  it('配对的两行几乎毫不相干时,抹平词级高亮', () => {
    const lines = computeDiff('<ModelStatusStrip model={model} />', '                <Suspense')
    // 只是被 LCS 按位置凑成一对,逐词标注会把整行标满 —— 退回行级表达
    expect(hi(lines, 'add')).toEqual([])
    expect(hi(lines, 'del')).toEqual([])
  })

  it('改动占比不高时保留词级高亮', () => {
    const lines = computeDiff('const a = foo(x)', 'const a = bar(x)')
    expect(hi(lines, 'add')).toEqual(['bar'])
    expect(hi(lines, 'del')).toEqual(['foo'])
  })

  it('增删行数不等时不做词级配对,按先删后增排列', () => {
    const lines = computeDiff('x1\nx2', 'y1')
    expect(shape(lines)).toEqual(['d:x1', 'd:x2', 'a:y1'])
    // 凑不成对,就不标词级高亮
    expect(hi(lines, 'del')).toEqual([])
    expect(hi(lines, 'add')).toEqual([])
  })

  it('不带行号:工具卡里的片段没有行号可言', () => {
    const lines = computeDiff('a', 'b')
    expect(lines.every((line) => line.oldLine === null && line.newLine === null)).toBe(true)
  })
})

describe('computeDiffHunks', () => {
  it('大文件只保留改动附近的上下文并给出双侧行号', () => {
    const before = Array.from({ length: 5_000 }, (_, index) => `line ${index + 1}`)
    const after = [...before]
    after[4_199] = 'changed line 4200'

    const hunks = computeDiffHunks(before.join('\n'), after.join('\n'), 2) ?? []

    expect(hunks).toHaveLength(1)
    expect(hunks[0]).toMatchObject({ oldStart: 4_198, oldCount: 5, newStart: 4_198, newCount: 5 })
    expect(hunks[0]?.lines.map((line) => [line.kind, line.oldLine, line.newLine])).toEqual([
      ['context', 4_198, 4_198],
      ['context', 4_199, 4_199],
      ['del', 4_200, null],
      ['add', null, 4_200],
      ['context', 4_201, 4_201],
      ['context', 4_202, 4_202]
    ])
  })

  it('相距较远的改动拆成独立 hunk 而不渲染中间正文', () => {
    const before = Array.from({ length: 20 }, (_, index) => `line ${index + 1}`)
    const after = [...before]
    after[2] = 'changed line 3'
    after[17] = 'changed line 18'

    const hunks = computeDiffHunks(before.join('\n'), after.join('\n'), 1) ?? []

    expect(hunks).toHaveLength(2)
    expect(hunks.map((hunk) => [hunk.oldStart, hunk.newStart])).toEqual([[2, 2], [17, 17]])
    expect(hunks.flatMap((hunk) => hunk.lines).some((line) => line.oldLine === 10)).toBe(false)
  })

  it('首尾都变化的大文件会按中部公共行分段而不是误判超限', () => {
    const before = Array.from({ length: 1_500 }, (_, index) => `line ${index + 1}`)
    const after = [...before]
    after[0] = 'changed first line'
    after[1_499] = 'changed last line'

    const hunks = computeDiffHunks(before.join('\n'), after.join('\n')) ?? []

    expect(hunks).toHaveLength(2)
    expect(hunks.map((hunk) => [hunk.oldStart, hunk.newStart])).toEqual([[1, 1], [1_497, 1_497]])
  })

  it('纯新增 hunk 使用插入点作为旧侧零行范围', () => {
    const [hunk] = computeDiffHunks('a\nb', 'a\ninserted\nb', 0) ?? []

    expect(hunk).toMatchObject({ oldStart: 1, oldCount: 0, newStart: 2, newCount: 1 })
    expect(hunk?.lines[0]).toMatchObject({ kind: 'add', oldLine: null, newLine: 2 })
  })

  it('新建和删除文件不为缺失的一侧制造空白行', () => {
    const [created] = computeDiffHunks('', 'a\nb') ?? []
    const [deleted] = computeDiffHunks('a\nb', '') ?? []

    expect(created).toMatchObject({ oldStart: 0, oldCount: 0, newStart: 1, newCount: 2 })
    expect(created?.lines.map((line) => line.kind)).toEqual(['add', 'add'])
    expect(deleted).toMatchObject({ oldStart: 1, oldCount: 2, newStart: 0, newCount: 0 })
    expect(deleted?.lines.map((line) => line.kind)).toEqual(['del', 'del'])
  })

  it('完整重写超过安全预算时拒绝生成平方级矩阵', () => {
    const before = Array.from({ length: 1_500 }, (_, index) => `old ${index}`).join('\n')
    const after = Array.from({ length: 1_500 }, (_, index) => `new ${index}`).join('\n')

    expect(computeDiffHunks(before, after)).toBeNull()
  })

  it('单侧新增过多时在创建大量 DOM 行之前停止预览', () => {
    const after = Array.from({ length: 10_001 }, (_, index) => `line ${index}`).join('\n')

    expect(computeDiffHunks('', after)).toBeNull()
  })
})

describe('hunkLines', () => {
  it('每个 hunk 前面插一行 @@ 标题,和 git 输出同形', () => {
    const hunks = computeDiffHunks('a\nb\nc', 'a\nB\nc', 1) ?? []

    expect(hunkLines(hunks).map((line) => `${line.kind}:${lineText(line)}`)).toEqual([
      'hunk:@@ -1,3 +1,3 @@',
      'context:a',
      'del:b',
      'add:B',
      'context:c'
    ])
  })

  it('单行范围省略数量(和常见代码审查工具一致)', () => {
    const hunks = computeDiffHunks('a\nb', 'a\nB', 0) ?? []

    expect(lineText(hunkLines(hunks)[0]!)).toBe('@@ -2 +2 @@')
  })
})
