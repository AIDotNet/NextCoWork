/**
 * 功能区命令表(`commands.ts`)。
 *
 * 钉住的需求:表是封闭的(不认识的、这种文档不支持的都拒);参数按形状重建、多余的键丢掉;
 * 引擎声明的命令与状态只留宿主认得的 id。
 */
import { describe, expect, it } from 'vitest'
import { parseCommandIds, parseCommandStates, validateCommand } from '../commands'

const WORD = ['format.bold', 'format.fontName', 'format.fontSize', 'format.color', 'style.paragraph', 'insert.table']

describe('validateCommand', () => {
  it('accepts argument-free commands and rebuilds arguments without extra keys', () => {
    expect(validateCommand('format.bold', { sneaky: '.uno:Save' }, WORD)).toEqual({ ok: true, command: 'format.bold' })
    expect(validateCommand('format.fontSize', { size: 10.5, extra: 1 }, WORD)).toEqual({ ok: true, command: 'format.fontSize', args: { size: 10.5 } })
    expect(validateCommand('insert.table', { rows: 2, columns: 3 }, WORD)).toEqual({ ok: true, command: 'insert.table', args: { rows: 2, columns: 3 } })
    expect(validateCommand('format.color', { color: -1 }, WORD)).toEqual({ ok: true, command: 'format.color', args: { color: -1 } })
  })

  it('refuses commands outside the closed table and those the document does not offer', () => {
    // 表外的命令(保存、任意 UNO 串)在宿主就拒,送不进引擎
    expect(validateCommand('.uno:Save', undefined, WORD)).toMatchObject({ ok: false, reason: expect.stringMatching(/^unsupported_operation/) })
    expect(validateCommand('file.save', undefined, WORD)).toMatchObject({ ok: false })
    expect(validateCommand('cells.merge', undefined, WORD)).toMatchObject({ ok: false, reason: expect.stringMatching(/^unsupported_operation/) })
  })

  it('refuses malformed arguments', () => {
    expect(validateCommand('format.fontSize', { size: 0 }, WORD)).toMatchObject({ ok: false })
    expect(validateCommand('format.fontSize', { size: '12' }, WORD)).toMatchObject({ ok: false })
    expect(validateCommand('format.fontName', { name: 'Arial\u0000' }, WORD)).toMatchObject({ ok: false })
    expect(validateCommand('format.fontName', { name: 'x'.repeat(129) }, WORD)).toMatchObject({ ok: false })
    expect(validateCommand('format.color', { color: 0x1000000 }, WORD)).toMatchObject({ ok: false })
    expect(validateCommand('insert.table', { rows: 2.5, columns: 3 }, WORD)).toMatchObject({ ok: false })
    expect(validateCommand('style.paragraph', {}, WORD)).toMatchObject({ ok: false })
  })
})

describe('spreadsheet formula bar commands', () => {
  const SHEET = ['cells.enter', 'cells.goto']

  it('passes formulas and empty text through as cell text, and only well-formed references to the name box', () => {
    expect(validateCommand('cells.enter', { text: '=SUM(A1:A3)' }, SHEET)).toEqual({ ok: true, command: 'cells.enter', args: { text: '=SUM(A1:A3)' } })
    expect(validateCommand('cells.enter', { text: '' }, SHEET)).toMatchObject({ ok: true })
    expect(validateCommand('cells.enter', { text: 'a\u0000b' }, SHEET)).toMatchObject({ ok: false })
    expect(validateCommand('cells.enter', { text: 'x'.repeat(32768) }, SHEET)).toMatchObject({ ok: false })
    expect(validateCommand('cells.goto', { ref: '$B$3:C10' }, SHEET)).toEqual({ ok: true, command: 'cells.goto', args: { ref: '$B$3:C10' } })
    // 名称框里只认单元格引用:工作表函数、宏 URL、区域外的写法一律拒
    for (const ref of ['a1', 'A1;B2', 'Sheet1.A1', 'A0x', '']) expect(validateCommand('cells.goto', { ref }, SHEET)).toMatchObject({ ok: false })
  })
})

describe('presentation commands', () => {
  const SLIDES = ['slides.new', 'slides.layout', 'slides.delete', 'insert.textBox', 'insert.table']

  it('takes no arguments for slide management and inserts, and only the offered layouts', () => {
    expect(validateCommand('slides.new', { index: 3 }, SLIDES)).toEqual({ ok: true, command: 'slides.new' })
    expect(validateCommand('insert.textBox', { text: 'x' }, SLIDES)).toEqual({ ok: true, command: 'insert.textBox' })
    expect(validateCommand('slides.layout', { layout: 20, extra: 1 }, SLIDES)).toEqual({ ok: true, command: 'slides.layout', args: { layout: 20 } })
    // 图表 / 竖排等界面上没有入口的版式、非数字、缺省,都拒
    for (const layout of [2, 21, -1, '20', undefined]) expect(validateCommand('slides.layout', { layout }, SLIDES)).toMatchObject({ ok: false })
    expect(validateCommand('slides.duplicate', undefined, SLIDES)).toMatchObject({ ok: false, reason: expect.stringMatching(/^unsupported_operation/) })
  })
})

describe('engine declarations', () => {
  it('keeps only command ids the host knows, once each', () => {
    expect(parseCommandIds(['format.bold', 'format.bold', 'uno.Save', 7])).toEqual(['format.bold'])
    expect(parseCommandIds(['uno.Save'])).toBeUndefined()
    expect(parseCommandIds('format.bold')).toBeUndefined()
  })

  it('keeps known states as strings and truncates long values', () => {
    expect(parseCommandStates({ 'format.bold': 'true', 'format.fontName': 'x'.repeat(300), Bold: 'true', 'format.italic': 1 }))
      .toEqual({ 'format.bold': 'true', 'format.fontName': 'x'.repeat(256) })
    expect(parseCommandStates({})).toBeUndefined()
  })
})
