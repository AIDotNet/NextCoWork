import { describe, expect, it } from 'vitest'
import {
  DOCUMENT_FORMAT_KIND,
  DOCUMENT_FORMATS,
  MAX_OPERATIONS_PER_BATCH,
  documentFormatOf,
  validateOperations
} from '../protocol'
import { applyPrecondition, initialSession, isSessionDirty, reduceSession, type DocumentSessionSnapshot } from '../session'

const ALL = { operations: ['text.replace', 'cells.set', 'slide.move', 'pdf.annotate'] as const }
const caps = { operations: [...ALL.operations] }

describe('document formats', () => {
  it('maps every launch format to a kind and nothing else', () => {
    expect(Object.keys(DOCUMENT_FORMAT_KIND).sort()).toEqual([...DOCUMENT_FORMATS].sort())
  })

  it('infers formats from extensions case-insensitively and refuses legacy/unknown ones', () => {
    expect(documentFormatOf('a/b/Report.DOCX')).toBe('docx')
    expect(documentFormatOf('m.xlsm')).toBe('xlsm')
    expect(documentFormatOf('old.doc')).toBeNull()
    expect(documentFormatOf('.pdf')).toBeNull()
    expect(documentFormatOf('noext')).toBeNull()
  })
})

describe('validateOperations', () => {
  it('accepts well-formed supported operations', () => {
    const result = validateOperations([
      { kind: 'text.replace', target: { generation: 1, ref: 'p:3' }, text: '你好' },
      { kind: 'cells.set', sheet: 'Sheet1', range: 'A1:B1', values: [[1, 'x']] }
    ], caps)
    expect(result.ok).toBe(true)
  })

  it('rejects the whole batch when one operation is unsupported, instead of applying half', () => {
    const result = validateOperations([
      { kind: 'text.replace', target: { generation: 1, ref: 'p:3' }, text: 'a' },
      { kind: 'style.apply', target: { generation: 1, ref: 'p:3' }, style: 'Heading 1' }
    ], caps)
    expect(result).toEqual({ ok: false, reason: 'operations[1]: unsupported_operation style.apply' })
  })

  it('rejects unknown kinds, raw command strings and malformed targets', () => {
    expect(validateOperations([{ kind: 'uno', command: '.uno:Save' }], caps).ok).toBe(false)
    expect(validateOperations([{ kind: 'text.replace', target: { ref: 'p:1' }, text: 'a' }], caps).ok).toBe(false)
    expect(validateOperations([{ kind: 'cells.set', sheet: 'S', range: 'A1', values: [[{}]] }], caps).ok).toBe(false)
    expect(validateOperations([{ kind: 'cells.set', sheet: 'S', range: 'A1', values: [[Number.NaN]] }], caps).ok).toBe(false)
    expect(validateOperations([{ kind: 'pdf.annotate', page: 0, rect: [0, 0, 1], text: 'x' }], caps).ok).toBe(false)
  })

  it('bounds batch size so a single call cannot stall the engine event loop', () => {
    const op = { kind: 'slide.move', from: 0, to: 1 }
    expect(validateOperations(Array.from({ length: MAX_OPERATIONS_PER_BATCH + 1 }, () => op), caps).ok).toBe(false)
    expect(validateOperations([], caps).ok).toBe(false)
    expect(validateOperations('nope', caps).ok).toBe(false)
  })
})

function ready(): DocumentSessionSnapshot {
  return reduceSession(initialSession('s1', 'docx'), { type: 'loaded', diskRevision: 'd0' })
}

describe('session reducer', () => {
  it('becomes ready on load with generation 1 and is clean', () => {
    const s = ready()
    expect(s).toMatchObject({ status: 'ready', generation: 1, diskRevision: 'd0', seq: 1 })
    expect(isSessionDirty(s)).toBe(false)
  })

  it('keeps edits made during a save dirty after the save succeeds', () => {
    let s = reduceSession(ready(), { type: 'applied', revision: 1 })
    s = reduceSession(s, { type: 'saveStarted' })
    s = reduceSession(s, { type: 'applied', revision: 2 })
    s = reduceSession(s, { type: 'saved', diskRevision: 'd1', savedModelRevision: 1 })
    expect(s).toMatchObject({ status: 'ready', savedRevision: 1, modelRevision: 2, diskRevision: 'd1' })
    expect(isSessionDirty(s)).toBe(true)
  })

  it('ignores revision gaps and late events after close', () => {
    const s = ready()
    expect(reduceSession(s, { type: 'applied', revision: 5 })).toBe(s)
    const closed = reduceSession(s, { type: 'closed' })
    expect(reduceSession(closed, { type: 'applied', revision: 1 })).toBe(closed)
    expect(reduceSession(closed, { type: 'loaded', diskRevision: 'x' })).toBe(closed)
  })

  it('bumps generation on reload so old references go stale, and keeps restored edits dirty', () => {
    let s = reduceSession(ready(), { type: 'applied', revision: 1 })
    s = reduceSession(s, { type: 'crashed' })
    expect(applyPrecondition(s, { generation: 1, modelRevision: 1 })).toBe('engine_unavailable')
    s = reduceSession(s, { type: 'recovering' })
    s = reduceSession(s, { type: 'reloaded', diskRevision: 'd0', restoredRevision: 1 })
    expect(s.generation).toBe(2)
    expect(isSessionDirty(s)).toBe(true)
    expect(applyPrecondition(s, { generation: 1, modelRevision: 1 })).toBe('stale_generation')
    expect(applyPrecondition(s, { generation: 2, modelRevision: 0 })).toBe('stale_revision')
    expect(applyPrecondition(s, { generation: 2, modelRevision: 1 })).toBeNull()
  })

  it('moves to conflict on disk conflict and blocks saves until resolved', () => {
    let s = reduceSession(ready(), { type: 'applied', revision: 1 })
    s = reduceSession(s, { type: 'saveStarted' })
    s = reduceSession(s, { type: 'diskConflict' })
    expect(s.status).toBe('conflict')
    expect(reduceSession(s, { type: 'saveStarted' })).toBe(s)
    s = reduceSession(s, { type: 'conflictResolved', diskRevision: 'd9' })
    expect(s).toMatchObject({ status: 'ready', diskRevision: 'd9' })
    expect(isSessionDirty(s)).toBe(true)
  })
})
