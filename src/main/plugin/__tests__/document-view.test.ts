/**
 * 编辑器画布的通道(`document-view.ts`)+ `PluginDocuments` 的画布入口。
 *
 * 用真的 `DocumentSessionManager` 与 `PluginDocuments` + 假引擎(文档 = 一段文本,键入的字符追加进去)。
 * 钉住的需求:画布与插件 RPC / Agent 落到同一个会话;token 只对打开它的窗口有效;切账户后作废;
 * 关 Tab / 关窗口释放视图并取消没提交的组字;画布不跟插件租约的空闲回收走。
 */
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import type { DocumentInputEvent } from '../../../shared/document-engine/interaction'
import type { DocumentCapabilities } from '../../../shared/document-engine/protocol'
import type { PluginManifest } from '../../../shared/plugin/manifest'
import { DocumentSessionManager, type DocumentEngineHandle, type DocumentEngineProvider } from '../../document-engine/manager'
import { PluginDocuments } from '../document-rpc'
import { DocumentViewChannel } from '../document-view'

const ENGINE = 'ncw.office-runtime/office'
const WINDOW = 7
const OTHER_WINDOW = 8

let root: string
let workspace: string
beforeEach(() => {
  root = mkdtempSync(join(tmpdir(), 'ncw-doc-view-'))
  workspace = join(root, 'ws')
  mkdirSync(workspace)
  writeFileSync(join(workspace, 'report.docx'), 'hello')
})
afterEach(() => { rmSync(root, { recursive: true, force: true }) })

/** 假引擎:按键字符追加进正文;记下收到的每一批输入与关闭次数 */
class TypingEngine implements DocumentEngineProvider {
  readonly id = ENGINE
  readonly formats = ['docx'] as const
  inputs: DocumentInputEvent[][] = []
  closed = 0

  async open(input: { workingPath: string }): Promise<DocumentEngineHandle> {
    let text = readFileSync(input.workingPath, 'utf8')
    const capabilities: DocumentCapabilities = {
      format: 'docx', engineVersion: 'fake', operations: ['text.replace'], canSave: true, canExport: [], canUndo: false,
      macros: { list: false, run: false }, interaction: { keyboard: true, mouse: true, textInput: true }, commands: ['format.bold']
    }
    return {
      capabilities,
      apply: async (operations) => {
        for (const op of operations) if (op.kind === 'text.replace') text += op.text
        return { warnings: [], undoable: false }
      },
      query: async (request) => request.kind === 'styles'
        ? { paragraphStyles: ['Heading 1', 7, 'Standard'] }
        : request.kind === 'outline'
          ? { partNames: ['Sheet1', 'Data'] }
          : request.kind === 'headers'
            ? { rows: [[0, ''], [255, '1'], [-5, 'bad'], [510, 'x'.repeat(17)]], columns: [[0, ''], [1275, 'A']] }
            : { documentType: 'text', width: 12240, height: 15840, pages: [] },
      command: async () => {
        text += '*'
        return { modified: true, invalidations: { all: true, rects: [] }, states: { 'format.bold': 'true' } }
      },
      render: async (request) => ({ width: request.width, height: request.height, format: 'rgba', bytes: new Uint8Array(request.width * request.height * 4) }),
      input: async (events) => {
        this.inputs.push(events)
        let modified = false
        for (const event of events) {
          if (event.type === 'key' && event.action === 'press' && event.charCode > 0) {
            text += String.fromCodePoint(event.charCode)
            modified = true
          }
        }
        return { modified, invalidations: { all: modified, rects: [] } }
      },
      saveTo: async (path) => { writeFileSync(path, text) },
      close: async () => { this.closed += 1 }
    }
  }
}

const WRITER = {
  dependencies: { 'ncw.office-runtime': '^1.0.0' },
  contributes: { customEditors: [{ viewType: 'ncw.writer', documentEngine: ENGINE }, { viewType: 'ncw.plain' }], documentEngines: [] }
} as unknown as PluginManifest

function setup(): { channel: DocumentViewChannel; docs: PluginDocuments; engine: TypingEngine; setAccount: (value: string) => void; now: (ms: number) => void } {
  let account = 'acct'
  let clock = 0
  const sessions = new DocumentSessionManager({ privateDir: join(root, 'private'), timeoutMs: 1000 })
  const engine = new TypingEngine()
  sessions.registerProvider(engine)
  const docs = new PluginDocuments({
    sessions,
    ensureProvider: (id) => (id === ENGINE ? engine : null),
    retireEngines: async () => undefined,
    accountScope: () => account,
    leaseIdleMs: 1000,
    now: () => clock
  })
  const channel = new DocumentViewChannel({
    documents: docs,
    sessions,
    accountScope: () => account,
    lookupPlugin: (id) => (id === 'ncw.writer' ? WRITER : null),
    lookupWorkspaceRoot: (id) => (id === 'ws1' ? workspace : null)
  })
  return { channel, docs, engine, setAccount: (value) => { account = value }, now: (ms) => { clock = ms } }
}

const OPEN = { workspaceId: 'ws1', path: 'report.docx', pluginId: 'ncw.writer', viewType: 'ncw.writer' }
const typed = (text: string): DocumentInputEvent[] => [...text].map((c) => ({ type: 'key', action: 'press', charCode: c.codePointAt(0) ?? 0, keyCode: 0 }))

describe('DocumentViewChannel', () => {
  it('opens the Tab-bound file on the editor\'s engine and shares the live model with the plugin\'s own RPC', async () => {
    const { channel, docs } = setup()
    const opened = await channel.open(WINDOW, OPEN)
    expect(opened).toMatchObject({ path: 'report.docx', state: { status: 'ready', generation: 1, modelRevision: 0, dirty: false }, capabilities: { interaction: { textInput: true } } })
    // 渲染层拿不到会话内部标识
    expect(opened).not.toHaveProperty('sessionId')
    const typedResult = await channel.input(WINDOW, { token: opened.token, generation: 1, events: typed('!') })
    expect(typedResult).toMatchObject({ modified: true, modelRevision: 1 })
    const tile = await channel.render(WINDOW, { token: opened.token, request: { x: 0, y: 0, tileWidth: 1000, tileHeight: 1000, width: 4, height: 4 } })
    expect(tile).toMatchObject({ width: 4, height: 4, modelRevision: 1 })
    // 插件逻辑侧(Agent 工具)打开同一个文件:同一个会话,看得到画布里刚打的字的修订号
    const { data } = await docs.handle('ncw.writer', WRITER, 'documents.open', { path: 'report.docx' }, { workspaceId: 'ws1', workspaceRoot: workspace })
    expect((data as { snapshot: { modelRevision: number } }).snapshot.modelRevision).toBe(1)
    const saved = await channel.save(WINDOW, { token: opened.token })
    expect(saved).toMatchObject({ dirty: false, savedRevision: 1 })
    expect(readFileSync(join(workspace, 'report.docx'), 'utf8')).toBe('hello!')
  })

  it('runs ribbon commands and lists styles for the canvas on the same session', async () => {
    const { channel } = setup()
    const opened = await channel.open(WINDOW, OPEN)
    const bold = await channel.command(WINDOW, { token: opened.token, generation: 1, command: 'format.bold' })
    expect(bold).toMatchObject({ modified: true, modelRevision: 1, states: { 'format.bold': 'true' } })
    await expect(channel.command(OTHER_WINDOW, { token: opened.token, generation: 1, command: 'format.bold' })).rejects.toMatchObject({ code: 'session_closed' })
    expect(await channel.list(WINDOW, { token: opened.token, kind: 'styles' })).toEqual({ names: ['Heading 1', 'Standard'] })
    expect(await channel.list(WINDOW, { token: opened.token, kind: 'parts' })).toEqual({ names: ['Sheet1', 'Data'] })
    // 行列头的形状在主进程收窄:负位置、过长的标签丢掉
    expect(await channel.headers(WINDOW, { token: opened.token, x: 0, y: 0, width: 1000, height: 1000 }))
      .toEqual({ rows: [[0, ''], [255, '1']], columns: [[0, ''], [1275, 'A']] })
  })

  it('refuses a token from another window, after an account switch, and editors without an engine binding', async () => {
    const { channel, setAccount } = setup()
    const opened = await channel.open(WINDOW, OPEN)
    const noSession = { code: 'session_closed' }
    await expect(channel.input(OTHER_WINDOW, { token: opened.token, generation: 1, events: typed('x') })).rejects.toMatchObject(noSession)
    expect(() => channel.state(OTHER_WINDOW, { token: opened.token })).toThrow(expect.objectContaining(noSession))
    setAccount('other')
    await expect(channel.render(WINDOW, { token: opened.token, request: { x: 0, y: 0, tileWidth: 1, tileHeight: 1, width: 1, height: 1 } })).rejects.toMatchObject(noSession)
    setAccount('acct')
    await expect(channel.open(WINDOW, { ...OPEN, viewType: 'ncw.plain' })).rejects.toMatchObject({ code: 'invalid_argument' })
    await expect(channel.open(WINDOW, { ...OPEN, pluginId: 'acme.gone' })).rejects.toMatchObject({ code: 'engine_unavailable' })
    await expect(channel.open(WINDOW, { ...OPEN, workspaceId: 'remote' })).rejects.toMatchObject({ code: 'unsupported_environment' })
    // 视图说不出工作区外的文件:路径门与插件 RPC 同一道
    await expect(channel.open(WINDOW, { ...OPEN, path: '../outside.docx' })).rejects.toMatchObject({ code: 'invalid_argument' })
  })

  it('keeps the canvas session alive past the plugin lease idle timeout', async () => {
    const { channel, docs, now } = setup()
    const opened = await channel.open(WINDOW, OPEN)
    now(60_000)
    await docs.sweepIdle()
    expect(channel.state(WINDOW, { token: opened.token })).toMatchObject({ status: 'ready' })
  })

  it('cancels an unfinished IME composition and releases the view when the Tab closes, but not while another Tab shares the session', async () => {
    const { channel, engine } = setup()
    const first = await channel.open(WINDOW, OPEN)
    const second = await channel.open(WINDOW, OPEN)
    await channel.close(WINDOW, { token: first.token })
    // 另一个 Tab 还开着同一个会话:不能替它取消组字
    expect(engine.inputs).toHaveLength(0)
    expect(engine.closed).toBe(0)
    await channel.close(WINDOW, { token: second.token })
    expect(engine.inputs).toEqual([[{ type: 'text', action: 'compose', text: '' }]])
    expect(engine.closed).toBe(1)
    // 关过的 token 失效;重复关闭是竞态,不报错
    expect(() => channel.state(WINDOW, { token: second.token })).toThrow()
    await expect(channel.close(WINDOW, { token: second.token })).resolves.toBeUndefined()
  })

  it('keeps unsaved canvas edits in the session when the window goes away, and tells every open view about Agent edits', async () => {
    const { channel, docs, engine } = setup()
    const a = await channel.open(WINDOW, OPEN)
    const b = await channel.open(OTHER_WINDOW, OPEN)
    await channel.input(WINDOW, { token: a.token, generation: 1, events: typed('!') })
    const scope = { workspaceId: 'ws1', workspaceRoot: workspace }
    const { data } = await docs.handle('ncw.writer', WRITER, 'documents.open', { path: 'report.docx' }, scope)
    const sessionId = (data as { sessionId: string }).sessionId
    await docs.handle('ncw.writer', WRITER, 'documents.apply', { sessionId, generation: 1, modelRevision: 1, operationId: 'agent', operations: [{ kind: 'text.replace', target: { generation: 1, ref: 'p:0' }, text: '?' }] }, scope)
    const snapshot = { sessionId, status: 'ready' as const, format: 'docx' as const, generation: 1, modelRevision: 2, savedRevision: 0, diskRevision: '', seq: 9 }
    const pushed = channel.changed(snapshot)
    expect(pushed.map((p) => [p.windowId, p.payload.token]).sort()).toEqual([[WINDOW, a.token], [OTHER_WINDOW, b.token]].sort())
    expect(pushed[0]?.payload.state).toMatchObject({ modelRevision: 2, dirty: true, seq: 9 })
    expect(channel.changed({ ...snapshot, sessionId: 'someone-else' })).toEqual([])

    await channel.closeWindow(WINDOW)
    await channel.closeWindow(OTHER_WINDOW)
    await docs.handle('ncw.writer', WRITER, 'documents.close', { sessionId }, scope)
    // 脏会话不被关窗丢掉:重开还在,改动还在
    expect(engine.closed).toBe(0)
    const reopened = await channel.open(WINDOW, OPEN)
    expect(reopened.state).toMatchObject({ modelRevision: 2, dirty: true })
    // 画布的脏会话同样挡住插件的卸载 / 禁用
    await expect(docs.assertCanRelease('ncw.writer')).rejects.toMatchObject({ code: 'rejected' })
  })
})
