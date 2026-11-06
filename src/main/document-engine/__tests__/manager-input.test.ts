/**
 * 画布交互输入经会话管理器的账目:输入与 Agent 修改共用一条队列、一套修订号。
 *
 * 钉住的需求:用户打了字,Agent 手里基于旧修订号的批次必须被挡下(不然会覆盖刚打的字);
 * 只挪光标不能推进修订号(不然 Agent 读一次就作废一次);按键结果不明时按崩溃处理。
 */
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import type { DocumentInputEvent, DocumentInputResult } from '../../../shared/document-engine/interaction'
import { DocumentEngineError, type DocumentCapabilities } from '../../../shared/document-engine/protocol'
import { DocumentSessionManager, type DocumentEngineHandle, type DocumentEngineProvider } from '../manager'
import { parseCapabilities } from '../native-host'

let root: string
beforeEach(() => { root = mkdtempSync(join(tmpdir(), 'ncw-doc-input-')) })
afterEach(() => { rmSync(root, { recursive: true, force: true }) })

const SCOPE = { accountScope: 'acct', workspaceId: 'ws1' }
const KEY_LEFT = 1026

/** 假引擎:可打印字符算修改,方向键不算;`reply` 可替换成超时 / 拒绝 / 坏回执 */
class InputEngine implements DocumentEngineProvider {
  readonly id = 'ncw.office-runtime/office'
  readonly formats = ['docx'] as const
  interactive = true
  received: DocumentInputEvent[][] = []
  reply: ((events: DocumentInputEvent[]) => Promise<DocumentInputResult>) | null = null
  commands: { command: string; args: unknown }[] = []
  /** 模拟用户正在输入法组字:引擎对读写模型的请求答 busy */
  composing = false

  async open(): Promise<DocumentEngineHandle> {
    const capabilities: DocumentCapabilities = {
      format: 'docx', engineVersion: 'fake', operations: ['text.replace'], canSave: true, canExport: [], canUndo: false, macros: { list: false, run: false },
      ...(this.interactive ? { interaction: { keyboard: true, mouse: true, textInput: false }, commands: ['format.bold', 'format.fontSize'] } : {})
    }
    return {
      capabilities,
      apply: async () => {
        if (this.composing) throw new DocumentEngineError('busy', 'cannot apply while the user is composing text with an input method')
        return { warnings: [], undoable: false }
      },
      ...(this.interactive
        ? {
            command: async (command: string, args: unknown) => {
              this.commands.push({ command, args })
              return { modified: command !== 'edit.selectAll', invalidations: { all: true, rects: [] }, states: { 'format.bold': 'true' } }
            }
          }
        : {}),
      query: async () => {
        if (this.composing) throw new DocumentEngineError('busy', 'cannot query while the user is composing text with an input method')
        return { text: '' }
      },
      saveTo: async (path) => { writeFileSync(path, 'saved') },
      input: async (events) => {
        this.received.push(events)
        if (this.reply !== null) return await this.reply(events)
        const modified = events.some((e) => e.type === 'key' && e.action === 'press' && e.charCode > 0)
        return { modified, invalidations: { all: false, rects: modified ? [{ x: 0, y: 0, width: 10, height: 10, part: 0 }] : [] } }
      },
      close: async () => undefined
    }
  }
}

async function setup(options: { interactive?: boolean } = {}): Promise<{ manager: DocumentSessionManager; engine: InputEngine; sessionId: string }> {
  const file = join(root, 'doc.docx')
  writeFileSync(file, 'x')
  const manager = new DocumentSessionManager({ privateDir: join(root, 'private'), timeoutMs: 200 })
  const engine = new InputEngine()
  engine.interactive = options.interactive ?? true
  manager.registerProvider(engine)
  const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id })
  return { manager, engine, sessionId: snapshot.sessionId }
}

const typed = (char: string): unknown[] => [{ type: 'key', action: 'press', charCode: char.codePointAt(0) }, { type: 'key', action: 'release', charCode: char.codePointAt(0) }]

describe('DocumentSessionManager.input', () => {
  it('advances the revision only for input that changed the model, so an Agent batch planned before typing is refused', async () => {
    const { manager, sessionId } = await setup()
    const moved = await manager.input({ sessionId, scope: SCOPE, generation: 1, events: [{ type: 'key', action: 'press', keyCode: KEY_LEFT }] })
    expect(moved).toMatchObject({ modified: false, modelRevision: 0 })
    const typedResult = await manager.input({ sessionId, scope: SCOPE, generation: 1, events: typed('a') })
    expect(typedResult).toMatchObject({ modified: true, modelRevision: 1, generation: 1 })
    expect(manager.snapshot(sessionId, SCOPE)).toMatchObject({ modelRevision: 1, savedRevision: 0 })
    await expect(manager.apply({ sessionId, scope: SCOPE, operationId: 'agent', generation: 1, modelRevision: 0, operations: [{ kind: 'text.replace', target: { generation: 1, ref: 'p:0' }, text: 'x' }] }))
      .rejects.toMatchObject({ code: 'stale_revision' })
  })

  it('refuses input laid out against an older engine generation before it reaches the engine', async () => {
    const { manager, engine, sessionId } = await setup()
    await expect(manager.input({ sessionId, scope: SCOPE, generation: 0, events: typed('a') })).rejects.toMatchObject({ code: 'stale_generation' })
    expect(engine.received).toHaveLength(0)
  })

  it('refuses read-only engines and undeclared input kinds without sending anything', async () => {
    const readOnly = await setup({ interactive: false })
    await expect(readOnly.manager.input({ sessionId: readOnly.sessionId, scope: SCOPE, generation: 1, events: [] })).rejects.toMatchObject({ code: 'unsupported_operation' })
    const { manager, engine, sessionId } = await setup()
    await expect(manager.input({ sessionId, scope: SCOPE, generation: 1, events: [...typed('a'), { type: 'text', action: 'commit', text: '中' }] }))
      .rejects.toMatchObject({ code: 'unsupported_operation' })
    await expect(manager.input({ sessionId, scope: SCOPE, generation: 1, events: [{ type: 'mouse', action: 'down', x: -5, y: 0 }] }))
      .rejects.toMatchObject({ code: 'invalid_operation' })
    expect(engine.received).toHaveLength(0)
  })

  it('passes an engine refusal through and keeps the session usable', async () => {
    const { manager, engine, sessionId } = await setup()
    engine.reply = async () => { throw new DocumentEngineError('invalid_operation', 'part is out of range') }
    await expect(manager.input({ sessionId, scope: SCOPE, generation: 1, events: [{ type: 'part', part: 9 }] })).rejects.toMatchObject({ code: 'invalid_operation' })
    expect(manager.snapshot(sessionId, SCOPE)).toMatchObject({ status: 'ready', modelRevision: 0 })
  })

  it('marks the session crashed when the engine cannot confirm whether keys reached the model', async () => {
    const { manager, engine, sessionId } = await setup()
    engine.reply = () => new Promise(() => undefined)
    await expect(manager.input({ sessionId, scope: SCOPE, generation: 1, events: typed('a') })).rejects.toMatchObject({ code: 'result_unknown' })
    expect(manager.snapshot(sessionId, SCOPE).status).toBe('crashed')
    await expect(manager.input({ sessionId, scope: SCOPE, generation: 1, events: [] })).rejects.toMatchObject({ code: 'engine_unavailable' })
  })
})

describe('busy while the user composes', () => {
  it('passes busy through without crashing the session, and lets the same operationId retry once the composition ends', async () => {
    const { manager, engine, sessionId } = await setup()
    engine.composing = true
    const batch = { sessionId, scope: SCOPE, operationId: 'agent-1', generation: 1, modelRevision: 0, operations: [{ kind: 'text.replace', target: { generation: 1, ref: 'p:0' }, text: 'x' }] }
    await expect(manager.apply(batch)).rejects.toMatchObject({ code: 'busy' })
    await expect(manager.query({ sessionId, scope: SCOPE, request: { kind: 'text' } })).rejects.toMatchObject({ code: 'busy' })
    // ★ 当成故障的话,用户打中文时 Agent 读一次会话就崩一次
    expect(manager.snapshot(sessionId, SCOPE)).toMatchObject({ status: 'ready', modelRevision: 0 })
    engine.composing = false
    // busy 不是对这批操作的裁决:同一个 operationId 可以原样重试,而不是被记成「已拒绝」
    await expect(manager.apply(batch)).resolves.toMatchObject({ operationId: 'agent-1', appliedRevision: 1 })
  })
})

describe('DocumentSessionManager.command', () => {
  it('runs a declared ribbon command with rebuilt arguments and advances the revision like typing does', async () => {
    const { manager, engine, sessionId } = await setup()
    const result = await manager.command({ sessionId, scope: SCOPE, generation: 1, command: 'format.fontSize', args: { size: 20, extra: 'x' } })
    expect(result).toMatchObject({ modified: true, modelRevision: 1, states: { 'format.bold': 'true' } })
    expect(engine.commands).toEqual([{ command: 'format.fontSize', args: { size: 20 } }])
    // 用户在工具栏上改了格式,Agent 手里旧修订号的批次要被挡下
    await expect(manager.apply({ sessionId, scope: SCOPE, operationId: 'late', generation: 1, modelRevision: 0, operations: [{ kind: 'text.replace', target: { generation: 1, ref: 'p:0' }, text: 'x' }] }))
      .rejects.toMatchObject({ code: 'stale_revision' })
  })

  it('refuses unknown, undeclared and malformed commands before they reach the engine', async () => {
    const { manager, engine, sessionId } = await setup()
    await expect(manager.command({ sessionId, scope: SCOPE, generation: 1, command: '.uno:Save' })).rejects.toMatchObject({ code: 'unsupported_operation' })
    await expect(manager.command({ sessionId, scope: SCOPE, generation: 1, command: 'insert.table', args: { rows: 1, columns: 1 } })).rejects.toMatchObject({ code: 'unsupported_operation' })
    await expect(manager.command({ sessionId, scope: SCOPE, generation: 1, command: 'format.fontSize', args: { size: -3 } })).rejects.toMatchObject({ code: 'invalid_operation' })
    await expect(manager.command({ sessionId, scope: SCOPE, generation: 0, command: 'format.bold' })).rejects.toMatchObject({ code: 'stale_generation' })
    expect(engine.commands).toEqual([])
    const readOnly = await setup({ interactive: false })
    await expect(readOnly.manager.command({ sessionId: readOnly.sessionId, scope: SCOPE, generation: 1, command: 'format.bold' })).rejects.toMatchObject({ code: 'unsupported_operation' })
  })
})

describe('parseCapabilities interaction', () => {
  it('keeps the interaction declaration from the helper and drops it when absent', () => {
    const declared = parseCapabilities({ capabilities: { operations: [], interaction: { keyboard: true, mouse: true, textInput: true } } }, 'docx', 'LibreOffice 26.8')
    expect(declared.interaction).toEqual({ keyboard: true, mouse: true, textInput: true })
    expect(parseCapabilities({ capabilities: { operations: [] } }, 'pdf', 'LibreOffice 26.8')).not.toHaveProperty('interaction')
  })
})
