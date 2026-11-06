/**
 * 宿主协议与 ncw-office-runtime helper 的对齐：按文字定位的 Word 操作、版面查询、
 * 批次逐条结果。这些形状由真实 helper 实测过（见该仓库的 conformance 测试），
 * 宿主这一侧钉住的是「不可信输入的收窄」——不让一条畸形操作漏进引擎。
 */
import { describe, expect, it } from 'vitest'
import { parseOperationResults, validateOperations, validateQuery } from '../protocol'

const WORD = { operations: ['text.findReplace', 'paragraph.style', 'paragraph.insert'] as const }
const ok = (raw: unknown): unknown => {
  const result = validateOperations([raw], { operations: [...WORD.operations] })
  if (!result.ok) throw new Error(result.reason)
  return result.operations[0]
}
const reason = (raw: unknown): string => {
  const result = validateOperations([raw], { operations: [...WORD.operations] })
  return result.ok ? '' : result.reason
}

describe('text-anchored Word operations', () => {
  it('keeps only the fields the engine understands and allows an empty replacement', () => {
    expect(ok({ kind: 'text.findReplace', find: '甲方', replace: '', expectedCount: 2, extra: 'dropped' }))
      .toEqual({ kind: 'text.findReplace', find: '甲方', replace: '', expectedCount: 2 })
    expect(ok({ kind: 'paragraph.style', find: '第一章', style: 'Heading 1', matchCase: true }))
      .toEqual({ kind: 'paragraph.style', find: '第一章', style: 'Heading 1', matchCase: true })
    expect(ok({ kind: 'paragraph.insert', anchor: '付款', position: 'after', text: 'A\nB' }))
      .toEqual({ kind: 'paragraph.insert', anchor: '付款', position: 'after', text: 'A\nB' })
  })

  it('rejects multi-line search text, multi-line replacements and bad counts before reaching the engine', () => {
    expect(reason({ kind: 'text.findReplace', find: 'a\nb', replace: 'x' })).toMatch(/single-line/)
    expect(reason({ kind: 'text.findReplace', find: '', replace: 'x' })).toMatch(/single-line/)
    expect(reason({ kind: 'text.findReplace', find: 'a', replace: 'x\ny' })).toMatch(/replace/)
    expect(reason({ kind: 'text.findReplace', find: 'a', replace: 'x', expectedCount: 0 })).toMatch(/expectedCount/)
    expect(reason({ kind: 'paragraph.style', find: 'a', style: 'S', matchCase: 'yes' })).toMatch(/matchCase/)
  })

  it('requires paragraph.insert to target exactly one anchor with a before/after position', () => {
    expect(reason({ kind: 'paragraph.insert', anchor: 'a', position: 'end', text: 'x' })).toMatch(/before or after/)
    expect(reason({ kind: 'paragraph.insert', anchor: 'a', position: 'after', text: '' })).toMatch(/text/)
    expect(reason({ kind: 'paragraph.insert', anchor: 'a', position: 'after', text: 'x', expectedCount: 2 })).toMatch(/exactly once/)
  })

  it('refuses the whole batch when the engine did not declare the operation', () => {
    const result = validateOperations([{ kind: 'text.findReplace', find: 'a', replace: 'b' }], { operations: ['cells.set'] })
    expect(result).toMatchObject({ ok: false, reason: expect.stringContaining('unsupported_operation') })
  })
})

describe('layout query and per-operation results', () => {
  it('accepts layout with an optional non-negative part', () => {
    expect(validateQuery({ kind: 'layout' })).toEqual({ kind: 'layout' })
    expect(validateQuery({ kind: 'layout', part: 2 })).toEqual({ kind: 'layout', part: 2 })
    expect(validateQuery({ kind: 'layout', part: -1 })).toMatch(/part/)
  })

  it('keeps bounded match counts only when the result list lines up with the batch', () => {
    expect(parseOperationResults([{ matches: 2 }, {}, { matches: 'x', junk: 1 }], 3)).toEqual([{ matches: 2 }, {}, {}])
    expect(parseOperationResults([{ matches: 1 }], 2)).toBeUndefined()
    expect(parseOperationResults('nope', 1)).toBeUndefined()
  })
})
