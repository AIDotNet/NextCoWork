/**
 * `parseUnifiedDiff` 的断言。
 *
 * 需求:右栏的行号、增删统计、以及「哪一行算正文」全部由这个函数决定,而它最容易
 * 错的两处都没有报错可看 —— 行号串位、以及正文以 `---` 开头的删除行被当成文件头
 * 丢掉(表现是改动在界面上凭空消失)。这两条各有一个用例。
 */
import { describe, expect, it } from 'vitest'
import { parseUnifiedDiff } from '../parse-unified'
import { lineText } from '../model'

const LIMIT = 1000

describe('parseUnifiedDiff', () => {
  it('drops the git file header noise and keeps the hunk header', () => {
    const parsed = parseUnifiedDiff(
      [
        'diff --git a/src/a.ts b/src/a.ts',
        'index 26282a78..2b278cb8 100644',
        '--- a/src/a.ts',
        '+++ b/src/a.ts',
        '@@ -1,2 +1,2 @@',
        ' keep',
        '-old',
        '+new',
        ''
      ].join('\n'),
      LIMIT
    )
    expect(parsed.lines.map((r) => r.kind)).toEqual(['hunk', 'context', 'del', 'add'])
    expect(lineText(parsed.lines[0]!)).toBe('@@ -1,2 +1,2 @@')
  })

  it('numbers old and new sides independently from the hunk header', () => {
    const parsed = parseUnifiedDiff(
      ['@@ -10,3 +20,4 @@', ' a', '-b', '+c', '+d', ' e', ''].join('\n'),
      LIMIT
    )
    expect(parsed.lines.slice(1).map((r) => [lineText(r), r.oldLine, r.newLine])).toEqual([
      ['a', 10, 20],
      ['b', 11, null],
      ['c', null, 21],
      ['d', null, 22],
      ['e', 12, 23]
    ])
  })

  it('keeps a removed line whose own content starts with ---', () => {
    const parsed = parseUnifiedDiff(['@@ -1,1 +1,1 @@', '---', '+***', ''].join('\n'), LIMIT)
    expect(parsed.lines.map((r) => [r.kind, lineText(r)])).toEqual([
      ['hunk', '@@ -1,1 +1,1 @@'],
      ['del', '--'],
      ['add', '***']
    ])
    expect([parsed.added, parsed.removed]).toEqual([1, 1])
  })

  it('handles a hunk header without line counts', () => {
    const parsed = parseUnifiedDiff(['@@ -7 +9 @@', '-x', '+y', ''].join('\n'), LIMIT)
    expect(parsed.lines[1]?.oldLine).toBe(7)
    expect(parsed.lines[2]?.newLine).toBe(9)
  })

  it('counts every added and removed line even past the row limit', () => {
    const body = Array.from({ length: 50 }, (_, i) => `+line ${String(i)}`)
    const parsed = parseUnifiedDiff(['@@ -0,0 +1,50 @@', ...body, ''].join('\n'), 10)
    expect(parsed.lines).toHaveLength(10)
    expect(parsed.added).toBe(50)
    expect(parsed.total).toBe(51)
  })

  it('does not emit a trailing blank row for the newline git ends with', () => {
    const parsed = parseUnifiedDiff('@@ -1,1 +1,1 @@\n+only\n', LIMIT)
    expect(parsed.lines).toHaveLength(2)
  })

  it('keeps the rename header but not the index line', () => {
    const parsed = parseUnifiedDiff(
      [
        'diff --git a/a.ts b/b.ts',
        'similarity index 98%',
        'rename from a.ts',
        'rename to b.ts',
        'index 1111111..2222222 100644',
        ''
      ].join('\n'),
      LIMIT
    )
    expect(parsed.lines.map((r) => lineText(r))).toEqual([
      'similarity index 98%',
      'rename from a.ts',
      'rename to b.ts'
    ])
    expect(parsed.lines.every((r) => r.kind === 'meta')).toBe(true)
  })

  it('treats the no-newline marker as meta without consuming a line number', () => {
    const parsed = parseUnifiedDiff(
      ['@@ -1,1 +1,1 @@', '-a', '\\ No newline at end of file', '+a', ''].join('\n'),
      LIMIT
    )
    expect(parsed.lines[2]?.kind).toBe('meta')
    expect(parsed.lines[3]?.newLine).toBe(1)
  })
})
