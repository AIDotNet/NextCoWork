/**
 * 交互输入协议的收窄:视图发来的事件与 helper 回来的回执都是不可信输入。
 * 钉住的是「整批拒绝」与「解析不了的回执不被猜成 modified:false」这两条。
 */
import { describe, expect, it } from 'vitest'
import { MAX_INPUT_EVENTS, parseInputResult, parseInteraction, validateInputEvents } from '../interaction'

const ALL = { keyboard: true, mouse: true, textInput: true }

describe('validateInputEvents', () => {
  it('fills defaults and keeps only the fields the engine understands', () => {
    expect(validateInputEvents([
      { type: 'key', action: 'press', charCode: 0x4e2d, extra: 1 },
      { type: 'mouse', action: 'down', x: 1580, y: 612 },
      { type: 'text', action: 'compose', text: '' },
      { type: 'part', part: 2 }
    ], ALL)).toEqual({
      ok: true,
      events: [
        { type: 'key', action: 'press', charCode: 0x4e2d, keyCode: 0 },
        { type: 'mouse', action: 'down', x: 1580, y: 612, count: 1, buttons: 1, modifier: 0 },
        { type: 'text', action: 'compose', text: '' },
        { type: 'part', part: 2 }
      ]
    })
  })

  it('accepts the visible area as whole positive twips and rejects anything else', () => {
    const AREA = { ...ALL, visibleArea: true }
    expect(validateInputEvents([{ type: 'viewport', x: 0, y: 7650, width: 15000, height: 7650, extra: 1 }], AREA))
      .toEqual({ ok: true, events: [{ type: 'viewport', x: 0, y: 7650, width: 15000, height: 7650 }] })
    for (const bad of [{ x: -1 }, { width: 0 }, { height: 1.5 }, { y: 2 ** 31 }]) {
      expect(validateInputEvents([{ type: 'viewport', x: 0, y: 0, width: 10, height: 10, ...bad }], AREA).ok).toBe(false)
    }
    // 引擎没声明(旧版引擎):不放行 —— 它会把整批连同按键一起拒掉
    expect(validateInputEvents([{ type: 'viewport', x: 0, y: 0, width: 10, height: 10 }], ALL))
      .toEqual({ ok: false, reason: expect.stringMatching(/^event 0: unsupported_operation/) })
    expect(parseInteraction({ keyboard: true, mouse: true, textInput: true, visibleArea: true })).toEqual(AREA)
    expect(parseInteraction({ keyboard: true, mouse: true, textInput: true })).not.toHaveProperty('visibleArea')
  })

  it('rejects the whole batch on the first malformed event, naming its index', () => {
    const result = validateInputEvents([{ type: 'key', action: 'press', charCode: 65 }, { type: 'key', action: 'press' }], ALL)
    expect(result).toEqual({ ok: false, reason: expect.stringMatching(/^event 1: .*charCode or a keyCode/) })
    expect(validateInputEvents([{ type: 'mouse', action: 'down', x: -1, y: 0 }], ALL)).toMatchObject({ ok: false })
    expect(validateInputEvents([{ type: 'mouse', action: 'down', x: 1.5, y: 0 }], ALL)).toMatchObject({ ok: false })
    expect(validateInputEvents([{ type: 'text', action: 'commit', text: '' }], ALL)).toMatchObject({ ok: false })
    expect(validateInputEvents([{ type: 'text', action: 'commit', text: '中'.repeat(1366) }], ALL)).toMatchObject({ ok: false, reason: expect.stringMatching(/bytes/) })
    expect(validateInputEvents([{ type: 'uno', command: '.uno:Save' }], ALL)).toMatchObject({ ok: false, reason: expect.stringMatching(/unknown input event type/) })
    expect(validateInputEvents(new Array(MAX_INPUT_EVENTS + 1).fill({ type: 'key', action: 'press', keyCode: 1280 }), ALL)).toMatchObject({ ok: false })
    expect(validateInputEvents('nope', ALL)).toMatchObject({ ok: false })
  })

  it('refuses input kinds the engine did not declare as unsupported_operation', () => {
    const readOnlyKeys = { keyboard: true, mouse: false, textInput: false }
    expect(validateInputEvents([{ type: 'mouse', action: 'down', x: 0, y: 0 }], readOnlyKeys)).toMatchObject({ ok: false, reason: expect.stringContaining('unsupported_operation') })
    expect(validateInputEvents([{ type: 'text', action: 'commit', text: 'a' }], readOnlyKeys)).toMatchObject({ ok: false, reason: expect.stringContaining('unsupported_operation') })
  })

  it('accepts an empty batch so the view can drain late engine events', () => {
    expect(validateInputEvents([], ALL)).toEqual({ ok: true, events: [] })
  })
})

describe('parseInteraction', () => {
  it('treats a missing or all-false declaration as no interactive input', () => {
    expect(parseInteraction(undefined)).toBeUndefined()
    expect(parseInteraction({ keyboard: false, mouse: false, textInput: false })).toBeUndefined()
    expect(parseInteraction({ keyboard: true, mouse: 'yes' })).toEqual({ keyboard: true, mouse: false, textInput: false })
  })
})

describe('parseInputResult', () => {
  it('keeps changed layers only and reports null for a caret that went away', () => {
    expect(parseInputResult({
      modified: true,
      invalidations: { all: false, rects: [{ x: 284, y: 1418, width: 11105, height: 275, part: 0 }] },
      cursor: { x: 1591, y: 1418, width: 0, height: 276 },
      cellCursor: null,
      selection: [{ x: 1, y: 2, width: 3, height: 4 }, { x: 'bad' }]
    })).toEqual({
      modified: true,
      invalidations: { all: false, rects: [{ x: 284, y: 1418, width: 11105, height: 275, part: 0 }] },
      cursor: { x: 1591, y: 1418, width: 0, height: 276 },
      cellCursor: null,
      selection: [{ x: 1, y: 2, width: 3, height: 4 }]
    })
  })

  it('falls back to a full repaint instead of dropping an unparseable invalidation', () => {
    expect(parseInputResult({ modified: false, invalidations: { all: false, rects: [{ x: 0, y: 0, width: 1, height: 1 }, { x: 'x' }] } }))
      .toEqual({ modified: false, invalidations: { all: true, rects: [] } })
  })

  it('carries the composing flag when the engine reports it, and leaves it out when it does not', () => {
    expect(parseInputResult({ modified: false, composing: true, invalidations: { all: false, rects: [] } }))
      .toEqual({ modified: false, composing: true, invalidations: { all: false, rects: [] } })
    expect(parseInputResult({ modified: false, composing: 'yes', invalidations: { all: false, rects: [] } })).not.toHaveProperty('composing')
  })

  it('carries the spreadsheet formula bar fields and drops malformed ones', () => {
    expect(parseInputResult({ modified: false, invalidations: { all: false, rects: [] }, cellFormula: '=1+2', cellAddress: 'B3', headersChanged: true }))
      .toEqual({ modified: false, invalidations: { all: false, rects: [] }, cellFormula: '=1+2', cellAddress: 'B3', headersChanged: true })
    expect(parseInputResult({ modified: false, invalidations: { all: false, rects: [] }, cellFormula: 3, cellAddress: 'x'.repeat(65), headersChanged: 'yes' }))
      .toEqual({ modified: false, invalidations: { all: false, rects: [] } })
  })

  it('carries the current part and the part count for thumbnails and sheet tabs, and drops impossible counts', () => {
    expect(parseInputResult({ modified: true, invalidations: { all: false, rects: [] }, part: 1, parts: 2 }))
      .toEqual({ modified: true, invalidations: { all: false, rects: [] }, part: 1, parts: 2 })
    // 0 张幻灯片不可能出现:当作没报,而不是把缩略图栏清空
    expect(parseInputResult({ modified: true, invalidations: { all: false, rects: [] }, parts: 0 })).not.toHaveProperty('parts')
    expect(parseInputResult({ modified: true, invalidations: { all: false, rects: [] }, parts: 1.5 })).not.toHaveProperty('parts')
  })

  it('refuses a receipt that does not say whether the model changed', () => {
    expect(parseInputResult({ invalidations: { all: true, rects: [] } })).toBeNull()
    expect(parseInputResult({ modified: 'true', invalidations: { all: true, rects: [] } })).toBeNull()
    expect(parseInputResult({ modified: false })).toBeNull()
    expect(parseInputResult(null)).toBeNull()
  })
})
