/**
 * 插件 `documents.*` RPC(`document-rpc.ts`)的门与租约。
 *
 * 用真的 `DocumentSessionManager` + 假引擎(文档 = 一段文本)驱动:会话、串行队列、
 * 修订号、保存的冲突检查都是真的,只有 LibreOffice 被换掉。钉住的是插件侧那几道门 ——
 * 句柄归属、路径收窄、引擎只来自清单、导出不许写回原文件、放下租约不丢脏改动。
 *
 * 另有一组钉「同一个文件被多个消费者 / 多个账户碰到时会发生什么」的用例:
 * 重复 open 只记一个租约、到期只移除租约而不 force 丢脏会话、切账户后旧句柄一律拒。
 */
import { mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
import { realpath } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import type { DocumentCapabilities, DocumentFormat } from '../../../shared/document-engine/protocol'
import type { PluginManifest } from '../../../shared/plugin/manifest'
import { DocumentSessionManager, type DocumentEngineHandle, type DocumentEngineProvider } from '../../document-engine/manager'
import { PluginDocuments, engineCandidates, toCapabilityError, type DocumentCallScope } from '../document-rpc'
import { DocumentEngineError } from '../../../shared/document-engine/protocol'

let root: string
let workspace: string

beforeEach(() => {
  root = mkdtempSync(join(tmpdir(), 'ncw-doc-rpc-'))
  workspace = join(root, 'ws')
  mkdirSync(workspace)
  writeFileSync(join(workspace, 'report.docx'), 'hello')
})
afterEach(() => { rmSync(root, { recursive: true, force: true }) })

/**
 * 假引擎:`text.replace` 追加文字,保存 / 导出写出当前文本。
 *
 * `formats` 可换:有的用例要一个「清单里绑定了、但它其实打不开 .docx」的引擎。
 * `closed` 记 handle 的关闭次数 —— 它是「会话有没有被真正收掉」在这份测试里唯一看得见的痕迹。
 */
class FakeEngine implements DocumentEngineProvider {
  opened = 0
  closed = 0
  constructor(readonly id: string, readonly formats: readonly DocumentFormat[] = ['docx']) {}

  async open(input: { workingPath: string }): Promise<DocumentEngineHandle> {
    this.opened += 1
    let text = readFileSync(input.workingPath, 'utf8')
    const capabilities: DocumentCapabilities = {
      format: 'docx', engineVersion: 'fake-1', operations: ['text.replace'], canSave: true, canExport: ['pdf'], canUndo: false, macros: { list: false, run: false }
    }
    return {
      capabilities,
      apply: async (operations) => {
        for (const op of operations) if (op.kind === 'text.replace') text += op.text
        return { warnings: [], undoable: false }
      },
      saveTo: async (outputPath) => { writeFileSync(outputPath, text) },
      exportTo: async (outputPath, format) => { writeFileSync(outputPath, `${format}:${text}`) },
      close: async () => { this.closed += 1 }
    }
  }
}

const ENGINE = 'ncw.office-runtime/office'

/** 最小清单:只填 document-rpc 读到的字段 */
function manifestOf(over: { dependencies?: Record<string, string>; editorEngine?: string; engines?: string[] } = {}): PluginManifest {
  return {
    dependencies: over.dependencies ?? { 'ncw.office-runtime': '^1.0.0' },
    contributes: {
      customEditors: over.editorEngine === undefined ? [] : [{ viewType: 'ncw.writer', documentEngine: over.editorEngine }],
      documentEngines: (over.engines ?? []).map((id) => ({ id, component: 'helper', formats: ['docx'] }))
    }
  } as unknown as PluginManifest
}

const WRITER = manifestOf({ editorEngine: ENGINE })

function setup(): { docs: PluginDocuments; engine: FakeEngine; sessions: DocumentSessionManager; scope: DocumentCallScope } {
  const sessions = new DocumentSessionManager({ privateDir: join(root, 'private'), timeoutMs: 1000 })
  const engine = new FakeEngine(ENGINE)
  sessions.registerProvider(engine)
  const docs = new PluginDocuments({
    sessions,
    ensureProvider: (id) => (id === ENGINE ? engine : null),
    retireEngines: async () => undefined,
    accountScope: () => 'acct'
  })
  return { docs, engine, sessions, scope: { workspaceId: 'ws1', workspaceRoot: workspace } }
}

const replace = (text: string): unknown[] => [{ kind: 'text.replace', target: { generation: 1, ref: 'p:0' }, text }]

async function openReport(docs: PluginDocuments, scope: DocumentCallScope, pluginId = 'ncw.writer', manifest = WRITER, path = 'report.docx'): Promise<string> {
  const { data } = await docs.handle(pluginId, manifest, 'documents.open', { path }, scope)
  return (data as { sessionId: string }).sessionId
}

/** 第二个插件(拿别人引擎的那个),用来验证租约按插件分账 */
const OTHER_PLUGIN = manifestOf({ editorEngine: ENGINE })

/**
 * 账户可切、时钟可控的实例。用来观察两件在固定装置里看不见的事:
 * 租约到期只移除租约、以及账户在队列里被换掉时会发生什么。
 */
function setupSwitchable(over: { slowResolvePath?: { reached: () => void; gate: Promise<void> } } = {}): {
  docs: PluginDocuments
  engine: FakeEngine
  sessions: DocumentSessionManager
  scope: DocumentCallScope
  setAccount: (value: string) => void
  at: (value: number) => void
} {
  let account = 'acct'
  let now = 0
  let resolved = 0
  const slow = over.slowResolvePath
  const sessions = new DocumentSessionManager({
    privateDir: join(root, 'private'),
    timeoutMs: 1000,
    // 第二次解析路径时卡住:这一次 open 已经进了 sessions.open,账户还没被换
    ...(slow === undefined
      ? {}
      : {
          resolveRealPath: async (path: string) => {
            resolved += 1
            if (resolved === 2) {
              slow.reached()
              await slow.gate
            }
            return realpath(path)
          }
        })
  })
  const engine = new FakeEngine(ENGINE)
  sessions.registerProvider(engine)
  const docs = new PluginDocuments({
    sessions,
    ensureProvider: (id) => (id === ENGINE ? engine : null),
    retireEngines: async () => undefined,
    accountScope: () => account,
    leaseIdleMs: 100,
    now: () => now
  })
  return { docs, engine, sessions, scope: { workspaceId: 'ws1', workspaceRoot: workspace }, setAccount: (value) => { account = value }, at: (value) => { now = value } }
}

describe('PluginDocuments', () => {
  it('opens through the editor-bound engine, applies, and saves back with a change notice', async () => {
    const { docs, scope } = setup()
    const sessionId = await openReport(docs, scope)
    const applied = await docs.handle('ncw.writer', WRITER, 'documents.apply', { sessionId, generation: 1, modelRevision: 0, operationId: 'op1', operations: replace(' world') }, scope)
    expect(applied.data).toMatchObject({ appliedRevision: 1, dirty: true })
    expect(readFileSync(join(workspace, 'report.docx'), 'utf8')).toBe('hello')
    const saved = await docs.handle('ncw.writer', WRITER, 'documents.save', { sessionId }, scope)
    expect(readFileSync(join(workspace, 'report.docx'), 'utf8')).toBe('hello world')
    expect(saved.changed).toEqual({ path: 'report.docx', kind: 'modified' })
  })

  it('marks model mutation as a side effect before dispatching apply', async () => {
    const { docs, scope } = setup()
    const sessionId = await openReport(docs, scope)
    let marked = 0
    const applied = await docs.handle('ncw.writer', WRITER, 'documents.apply', {
      sessionId, generation: 1, modelRevision: 0, operationId: 'marked-apply', operations: replace(' world')
    }, { ...scope, markSideEffectStarted: () => { marked++ } })
    expect(marked).toBe(1)
    expect(applied.data).toMatchObject({ appliedRevision: 1, dirty: true })
  })

  it('cancelled opens release the view instead of leaving an unreachable lease', async () => {
    let reached: () => void = () => {}
    let release: () => void = () => {}
    const started = new Promise<void>((resolve) => { reached = resolve })
    const gate = new Promise<void>((resolve) => { release = resolve })
    const { docs, sessions, engine, scope } = setupSwitchable({ slowResolvePath: { reached, gate } })
    const existing = await openReport(docs, scope)
    writeFileSync(join(workspace, 'other.docx'), 'other')
    const controller = new AbortController()
    const opening = openReport(docs, { ...scope, signal: controller.signal }, 'ncw.writer', WRITER, 'other.docx')
    await started
    controller.abort()
    release()
    await expect(opening).rejects.toThrow()
    expect(engine.opened).toBe(2)
    expect(engine.closed).toBe(1)
    expect(sessions.dirtySessions()).toEqual([])
    expect((await docs.handle('ncw.writer', WRITER, 'documents.getState', { sessionId: existing }, scope)).data).toMatchObject({ sessionId: existing })
    const reopened = await openReport(docs, scope, 'ncw.writer', WRITER, 'other.docx')
    expect(reopened).toBeTruthy()
    expect(engine.opened).toBe(3)
  })

  it('does not report a change when saving a document that has no edits', async () => {
    const { docs, scope } = setup()
    const sessionId = await openReport(docs, scope)
    const saved = await docs.handle('ncw.writer', WRITER, 'documents.save', { sessionId }, scope)
    expect(saved.changed).toBeUndefined()
  })

  it('reuses one lease when the same plugin reopens the same file through a different spelling', async () => {
    const { docs, engine, scope } = setup()
    const first = await openReport(docs, scope)
    const { data } = await docs.handle('ncw.writer', WRITER, 'documents.open', { path: './sub/../report.docx' }, scope)
    expect((data as { sessionId: string }).sessionId).toBe(first)
    expect(engine.opened).toBe(1)
  })

  it('refuses a sessionId held by another plugin with the same error as an unknown one', async () => {
    const { docs, scope } = setup()
    const sessionId = await openReport(docs, scope)
    const other = manifestOf({ editorEngine: ENGINE })
    await expect(docs.handle('evil.plugin', other, 'documents.apply', { sessionId, generation: 1, modelRevision: 0, operationId: 'x', operations: replace('!') }, scope))
      .rejects.toMatchObject({ code: 'rejected', message: expect.stringContaining('[session_closed]') })
    await expect(docs.handle('evil.plugin', other, 'documents.getState', { sessionId: 'nope' }, scope))
      .rejects.toMatchObject({ code: 'rejected', message: expect.stringContaining('[session_closed]') })
  })

  it('refuses a sessionId from another workspace scope even for the same plugin', async () => {
    const { docs, scope } = setup()
    const sessionId = await openReport(docs, scope)
    await expect(docs.handle('ncw.writer', WRITER, 'documents.getState', { sessionId }, { ...scope, workspaceId: 'ws2' }))
      .rejects.toMatchObject({ message: expect.stringContaining('[session_closed]') })
  })

  it('rejects paths that escape the workspace, lexically or through a symlink', async () => {
    const { docs, scope } = setup()
    writeFileSync(join(root, 'outside.docx'), 'secret')
    symlinkSync(join(root, 'outside.docx'), join(workspace, 'link.docx'))
    await expect(docs.handle('ncw.writer', WRITER, 'documents.open', { path: '../outside.docx' }, scope)).rejects.toMatchObject({ code: 'invalid_argument' })
    await expect(docs.handle('ncw.writer', WRITER, 'documents.open', { path: 'link.docx' }, scope))
      .rejects.toMatchObject({ code: 'invalid_argument', message: expect.stringContaining('symlinks') })
  })

  it('only uses engines named by the manifest: own, editor-bound, or from a declared dependency', async () => {
    const { docs, scope } = setup()
    const noDeps = manifestOf({ dependencies: {} })
    await expect(docs.handle('ncw.writer', noDeps, 'documents.open', { path: 'report.docx', engine: ENGINE }, scope))
      .rejects.toMatchObject({ code: 'invalid_argument', message: expect.stringContaining('dependencies') })
    await expect(docs.handle('ncw.writer', noDeps, 'documents.open', { path: 'report.docx' }, scope))
      .rejects.toMatchObject({ code: 'internal_error', message: expect.stringContaining('[engine_unavailable]') })
    const viaDependency = manifestOf()
    const { data } = await docs.handle('ncw.writer', viaDependency, 'documents.open', { path: 'report.docx', engine: ENGINE }, scope)
    expect(data).toMatchObject({ path: 'report.docx' })
  })

  it('surfaces stale revisions as rejected with the document code, without touching the model', async () => {
    const { docs, scope } = setup()
    const sessionId = await openReport(docs, scope)
    await docs.handle('ncw.writer', WRITER, 'documents.apply', { sessionId, generation: 1, modelRevision: 0, operationId: 'a', operations: replace('1') }, scope)
    await expect(docs.handle('ncw.writer', WRITER, 'documents.apply', { sessionId, generation: 1, modelRevision: 0, operationId: 'b', operations: replace('2') }, scope))
      .rejects.toMatchObject({ code: 'rejected', message: expect.stringContaining('[stale_revision]') })
  })

  it('exports to another workspace file but refuses to export over the document itself', async () => {
    const { docs, scope } = setup()
    const sessionId = await openReport(docs, scope)
    const exported = await docs.handle('ncw.writer', WRITER, 'documents.export', { sessionId, path: 'out.pdf', format: 'pdf' }, scope)
    expect(readFileSync(join(workspace, 'out.pdf'), 'utf8')).toBe('pdf:hello')
    expect(exported.changed).toEqual({ path: 'out.pdf', kind: 'created' })
    await expect(docs.handle('ncw.writer', WRITER, 'documents.export', { sessionId, path: 'report.docx', format: 'docx', overwrite: true }, scope))
      .rejects.toMatchObject({ code: 'invalid_argument', message: expect.stringContaining('documents.save') })
    await expect(docs.handle('ncw.writer', WRITER, 'documents.export', { sessionId, path: 'missing/out.pdf', format: 'pdf' }, scope))
      .rejects.toMatchObject({ code: 'invalid_argument' })
  })

  it('answers getOperation for its own session and reports another session\'s receipt as missing', async () => {
    const { docs, scope } = setup()
    writeFileSync(join(workspace, 'other.docx'), 'x')
    const mine = await openReport(docs, scope)
    const { data } = await docs.handle('ncw.writer', WRITER, 'documents.open', { path: 'other.docx' }, scope)
    const other = (data as { sessionId: string }).sessionId
    await docs.handle('ncw.writer', WRITER, 'documents.apply', { sessionId: other, generation: 1, modelRevision: 0, operationId: 'on-other', operations: replace('y') }, scope)
    expect((await docs.handle('ncw.writer', WRITER, 'documents.getOperation', { sessionId: other, operationId: 'on-other' }, scope)).data)
      .toMatchObject({ status: 'applied' })
    expect((await docs.handle('ncw.writer', WRITER, 'documents.getOperation', { sessionId: mine, operationId: 'on-other' }, scope)).data)
      .toEqual({ status: 'missing' })
  })

  it('keeps unsaved edits alive after close so a later open joins the dirty session', async () => {
    const { docs, engine, scope } = setup()
    const sessionId = await openReport(docs, scope)
    await docs.handle('ncw.writer', WRITER, 'documents.apply', { sessionId, generation: 1, modelRevision: 0, operationId: 'a', operations: replace('!') }, scope)
    const closed = await docs.handle('ncw.writer', WRITER, 'documents.close', { sessionId }, scope)
    expect(closed.data).toEqual({ closed: false, dirty: true })
    const reopened = await docs.handle('ncw.writer', WRITER, 'documents.open', { path: 'report.docx' }, scope)
    expect(reopened.data).toMatchObject({ sessionId, snapshot: { modelRevision: 1, savedRevision: 0 } })
    expect(engine.opened).toBe(1)
  })

  it('drops idle leases on sweep and closes clean sessions', async () => {
    let now = 0
    const sessions = new DocumentSessionManager({ privateDir: join(root, 'private'), timeoutMs: 1000 })
    const engine = new FakeEngine(ENGINE)
    sessions.registerProvider(engine)
    const docs = new PluginDocuments({ sessions, ensureProvider: () => engine, retireEngines: async () => undefined, accountScope: () => 'acct', leaseIdleMs: 100, now: () => now })
    const scope = { workspaceId: 'ws1', workspaceRoot: workspace }
    const sessionId = await openReport(docs, scope)
    now = 500
    await docs.sweepIdle()
    await expect(docs.handle('ncw.writer', WRITER, 'documents.getState', { sessionId }, scope)).rejects.toMatchObject({ message: expect.stringContaining('[session_closed]') })
    await openReport(docs, scope)
    expect(engine.opened).toBe(2)
  })

  it('releases every lease of a disabled plugin and retires the engines it carries', async () => {
    const retired: string[] = []
    const sessions = new DocumentSessionManager({ privateDir: join(root, 'private'), timeoutMs: 1000 })
    const engine = new FakeEngine(ENGINE)
    sessions.registerProvider(engine)
    const docs = new PluginDocuments({ sessions, ensureProvider: () => engine, retireEngines: async (id) => { retired.push(id) }, accountScope: () => 'acct' })
    const scope = { workspaceId: 'ws1', workspaceRoot: workspace }
    const sessionId = await openReport(docs, scope)
    await docs.releasePlugin('ncw.writer')
    expect(retired).toEqual(['ncw.writer'])
    await expect(docs.handle('ncw.writer', WRITER, 'documents.getState', { sessionId }, scope)).rejects.toMatchObject({ message: expect.stringContaining('[session_closed]') })
  })

  it('★ 排队期间这次调用被取消 → 轮到它时不再动手,报普通取消(而不是 result_unknown)', async () => {
    const { docs, sessions, scope } = setup()
    const sessionId = await openReport(docs, scope)
    // 用一次**挂住的导出**占住串行队列,让后面的 apply 真的排在它后面。
    let releaseFirst: () => void = () => {}
    const firstGate = new Promise<void>((resolve) => { releaseFirst = resolve })
    const realExport = sessions.exportDocument.bind(sessions)
    sessions.exportDocument = async (input) => { await firstGate; return realExport(input) }

    const first = docs.handle('ncw.writer', WRITER, 'documents.export', { sessionId, path: 'first.pdf', format: 'pdf' }, scope)
    await new Promise<void>((resolve) => { setImmediate(resolve) })

    const controller = new AbortController()
    const queued = docs.handle('ncw.writer', WRITER, 'documents.apply', {
      sessionId, generation: 1, modelRevision: 0, operationId: 'op-cancelled', operations: replace(' never')
    }, { ...scope, signal: controller.signal })
    // 还在排队时这次调用被取消(工具停止 / RPC 到点)
    controller.abort()
    releaseFirst()
    await first
    await expect(queued).rejects.toMatchObject({ code: 'rejected', message: 'the call was cancelled' })
    // 没有应用到模型上 —— 重试是安全的。
    expect(readFileSync(join(workspace, 'report.docx'), 'utf8')).toBe('hello')
  })

  it('★★ 保存 / 导出这一类会写盘的动作,在越过路径重验之后才记「副作用点」', async () => {
    const { docs, scope } = setup()
    const sessionId = await openReport(docs, scope)
    await docs.handle('ncw.writer', WRITER, 'documents.apply', { sessionId, generation: 1, modelRevision: 0, operationId: 'a', operations: replace('!') }, scope)
    const marks: number[] = []
    const saved = await docs.handle('ncw.writer', WRITER, 'documents.save', { sessionId }, {
      ...scope,
      markSideEffectStarted: () => { marks.push(1) }
    })
    /*
      ★ 这一份值就是 manager 判 result_unknown 的凭据:保存一旦越过它就被交给会话
      管理器,再取消也拿不回来(所以之后必须报「结果未知」而不是可安全重试)。
      路径重验(`resolveWorkspacePath` 那一道)排在它**之前** —— 被那道门拒绝的
      保存不该被算成「可能已经写了」。
    */
    expect(marks).toHaveLength(1)
    expect(saved.changed).toEqual({ path: 'report.docx', kind: 'modified' })
  })
})

/**
 * 同一个文件被多个消费者 / 多个账户碰到时会发生什么。
 *
 * 钉的是租约的记账方式:一个插件对一个文件只记一笔、到期只移除租约、释放一律非 force、
 * 账户换了之后旧句柄一律当不存在。这些都不会报错,只会表现为「东西还在后台跑」或
 * 「切账户后还能读到上一个账户的文档」,所以必须逐个钉住。
 */
describe('PluginDocuments leases', () => {
  it('reuses one lease for a repeated open, so a single close retires it', async () => {
    const { docs, engine, scope } = setup()
    const first = (await docs.handle('ncw.writer', WRITER, 'documents.open', { path: 'report.docx' }, scope)).data as { sessionId: string; viewId: string }
    const again = (await docs.handle('ncw.writer', WRITER, 'documents.open', { path: 'report.docx' }, scope)).data as { sessionId: string; viewId: string }
    // 重复 open 不许叠加视图:多出来的 viewId 插件根本记不住,那个会话就再也关不掉了
    expect(again).toMatchObject({ sessionId: first.sessionId, viewId: first.viewId })
    expect(engine.opened).toBe(1)
    const closed = await docs.handle('ncw.writer', WRITER, 'documents.close', { sessionId: first.sessionId }, scope)
    expect(closed.data).toEqual({ closed: true, dirty: false })
    await expect(docs.handle('ncw.writer', WRITER, 'documents.getState', { sessionId: first.sessionId }, scope))
      .rejects.toMatchObject({ message: expect.stringContaining('[session_closed]') })
    expect(engine.closed).toBe(1)
  })

  it('retires the lease on close even when the session stays dirty, and a reopen brings the edits back', async () => {
    const { docs, sessions, scope } = setup()
    const sessionId = await openReport(docs, scope)
    await docs.handle('ncw.writer', WRITER, 'documents.apply', { sessionId, generation: 1, modelRevision: 0, operationId: 'a', operations: replace('!') }, scope)
    expect((await docs.handle('ncw.writer', WRITER, 'documents.close', { sessionId }, scope)).data).toEqual({ closed: false, dirty: true })
    // 会话留在会话表里(改动还在),但**租约**必须失效 —— 否则「关闭」是句空话
    expect(sessions.dirtySessions()).toHaveLength(1)
    await expect(docs.handle('ncw.writer', WRITER, 'documents.getState', { sessionId }, scope))
      .rejects.toMatchObject({ message: expect.stringContaining('[session_closed]') })
    const reopened = await docs.handle('ncw.writer', WRITER, 'documents.open', { path: 'report.docx' }, scope)
    expect(reopened.data).toMatchObject({ sessionId, snapshot: { modelRevision: 1, savedRevision: 0 } })
  })

  it('releases every lease of a disabled plugin without dropping its dirty session', async () => {
    const { docs, engine, sessions, scope } = setup()
    const sessionId = await openReport(docs, scope)
    await docs.handle('ncw.writer', WRITER, 'documents.apply', { sessionId, generation: 1, modelRevision: 0, operationId: 'a', operations: replace('!') }, scope)
    await docs.releasePlugin('ncw.writer')
    // 非 force:脏会话留在表里,重新启用后还能把它存回去
    expect(sessions.dirtySessions()).toHaveLength(1)
    expect(engine.closed).toBe(0)
    await expect(docs.handle('ncw.writer', WRITER, 'documents.getState', { sessionId }, scope))
      .rejects.toMatchObject({ message: expect.stringContaining('[session_closed]') })
    const reopened = await docs.handle('ncw.writer', WRITER, 'documents.open', { path: 'report.docx' }, scope)
    expect(reopened.data).toMatchObject({ sessionId, snapshot: { modelRevision: 1, savedRevision: 0 } })
    expect(engine.opened).toBe(1)
  })

  it('expires one plugin\'s lease without touching the other consumer\'s view', async () => {
    const { docs, engine, sessions, scope, at } = setupSwitchable()
    const mine = await openReport(docs, scope)
    at(200)
    const other = await openReport(docs, scope, 'acme.other', OTHER_PLUGIN)
    expect(other).toBe(mine)
    at(250) // cutoff = 150:只有第一个租约到期
    await docs.sweepIdle()
    await expect(docs.handle('ncw.writer', WRITER, 'documents.getState', { sessionId: mine }, scope))
      .rejects.toMatchObject({ message: expect.stringContaining('[session_closed]') })
    // 另一个消费者还开着这个会话:清扫一个过期租约不许把它的视图一起收掉
    expect((await docs.handle('acme.other', OTHER_PLUGIN, 'documents.getState', { sessionId: mine }, scope)).data).toMatchObject({ sessionId: mine })
    expect(engine.opened).toBe(1)
    expect(engine.closed).toBe(0)
    at(500) // 这个也到期了:会话干净且没人持有视图,这次才真正收掉
    await docs.sweepIdle()
    await expect(docs.handle('acme.other', OTHER_PLUGIN, 'documents.getState', { sessionId: mine }, scope))
      .rejects.toMatchObject({ message: expect.stringContaining('[session_closed]') })
    expect(engine.closed).toBe(1)
    expect(sessions.dirtySessions()).toHaveLength(0)
    const reopened = await openReport(docs, scope)
    expect(reopened).not.toBe(mine)
    expect(engine.opened).toBe(2)
  })

  it('rejects the queued request and drops the new view when the account switched mid-flight', async () => {
    let unblock: () => void = () => undefined
    const gate = new Promise<void>((resolve) => { unblock = resolve })
    const reached = Promise.withResolvers<void>()
    const { docs, engine, scope, setAccount } = setupSwitchable({ slowResolvePath: { reached: () => reached.resolve(), gate } })
    const live = await openReport(docs, scope)
    writeFileSync(join(workspace, 'other.docx'), 'x')
    const slow = docs.handle('ncw.writer', WRITER, 'documents.open', { path: 'other.docx' }, scope)
    await reached.promise
    // 这一次 open 已经在 sessions.open 里面了,排在它后面的调用先入队再让账户换掉
    const queued = docs.handle('ncw.writer', WRITER, 'documents.getState', { sessionId: live }, scope)
    setAccount('other')
    unblock()
    // 回到 open 时账户已经不符:新拿到的视图属于别人的账户,必须自己放掉再拒绝
    await expect(slow).rejects.toMatchObject({ code: 'rejected', message: expect.stringContaining('[session_closed]') })
    // 排在它后面的那次调用,轮到它时账户已经不符 —— 哪怕它手上的 sessionId 是真的
    await expect(queued).rejects.toMatchObject({ message: expect.stringContaining('[session_closed]') })
    expect(engine.opened).toBe(2)
    expect(engine.closed).toBe(1)
  })

  it('refuses getOperation, getState and close once the account has switched', async () => {
    const { docs, scope, setAccount } = setupSwitchable()
    const sessionId = await openReport(docs, scope)
    await docs.handle('ncw.writer', WRITER, 'documents.apply', { sessionId, generation: 1, modelRevision: 0, operationId: 'op', operations: replace('!') }, scope)
    setAccount('other')
    await expect(docs.handle('ncw.writer', WRITER, 'documents.getOperation', { sessionId, operationId: 'op' }, scope))
      .rejects.toMatchObject({ code: 'rejected', message: expect.stringContaining('[session_closed]') })
    await expect(docs.handle('ncw.writer', WRITER, 'documents.close', { sessionId }, scope))
      .rejects.toMatchObject({ message: expect.stringContaining('[session_closed]') })
    await expect(docs.handle('ncw.writer', WRITER, 'documents.getState', { sessionId }, scope))
      .rejects.toMatchObject({ message: expect.stringContaining('[session_closed]') })
  })

  it('calls ensureProvider for an explicit engine and rejects it when the host does not have it', async () => {
    const asked: string[] = []
    const sessions = new DocumentSessionManager({ privateDir: join(root, 'private'), timeoutMs: 1000 })
    const engine = new FakeEngine(ENGINE)
    sessions.registerProvider(engine)
    const docs = new PluginDocuments({ sessions, ensureProvider: (id) => { asked.push(id); return id === ENGINE ? engine : null }, retireEngines: async () => undefined, accountScope: () => 'acct' })
    const scope = { workspaceId: 'ws1', workspaceRoot: workspace }
    // 裸 id 先被 qualify 成自己清单里声明的引擎,再当场问宿主拿得到吗。
    await expect(docs.handle('ncw.writer', manifestOf({ engines: ['office'] }), 'documents.open', { path: 'report.docx', engine: 'office' }, scope))
      .rejects.toMatchObject({ code: 'internal_error', message: expect.stringContaining('[engine_unavailable]') })
    expect(asked).toEqual(['ncw.writer/office'])
    await expect(docs.handle('ncw.writer', WRITER, 'documents.open', { path: 'report.docx', engine: 'not/a/valid/id' }, scope))
      .rejects.toMatchObject({ code: 'invalid_argument', message: expect.stringContaining('engine is invalid') })
    const opened = await docs.handle('ncw.writer', WRITER, 'documents.open', { path: 'report.docx', engine: ENGINE }, scope)
    expect(opened.data).toMatchObject({ path: 'report.docx' })
  })

  it('rejects incompatible dependency versions before creating or reusing a provider', async () => {
    const sessions = new DocumentSessionManager({ privateDir: join(root, 'private'), timeoutMs: 1000 })
    const engine = new FakeEngine(ENGINE)
    sessions.registerProvider(engine)
    let version = '2.0.0'
    const docs = new PluginDocuments({ sessions, ensureProvider: () => engine, engineVersion: () => version, retireEngines: async () => undefined, accountScope: () => 'acct' })
    const scope = { workspaceId: 'ws1', workspaceRoot: workspace }
    await expect(openReport(docs, scope)).rejects.toMatchObject({ code: 'internal_error' })
    expect(engine.opened).toBe(0)
    version = '1.1.0'
    await openReport(docs, scope)
    version = '2.0.0'
    await expect(openReport(docs, scope)).rejects.toMatchObject({ code: 'internal_error' })
    expect(engine.opened).toBe(1)
  })

  it('skips an auto candidate whose engine does not handle the file format', async () => {
    const sessions = new DocumentSessionManager({ privateDir: join(root, 'private'), timeoutMs: 1000 })
    const pdfOnly = new FakeEngine('ncw.office-runtime/pdf', ['pdf'])
    const own = new FakeEngine('ncw.writer/word')
    sessions.registerProvider(pdfOnly)
    sessions.registerProvider(own)
    const docs = new PluginDocuments({
      sessions,
      ensureProvider: (id) => (id === 'ncw.office-runtime/pdf' ? pdfOnly : id === 'ncw.writer/word' ? own : null),
      retireEngines: async () => undefined,
      accountScope: () => 'acct'
    })
    const scope = { workspaceId: 'ws1', workspaceRoot: workspace }
    const manifest = manifestOf({ editorEngine: 'ncw.office-runtime/pdf', engines: ['word'] })
    const opened = await docs.handle('ncw.writer', manifest, 'documents.open', { path: 'report.docx' }, scope)
    expect(opened.data).toMatchObject({ path: 'report.docx' })
    expect(own.opened).toBe(1)
    expect(pdfOnly.opened).toBe(0)
  })

  it('refuses to export over an existing file unless overwrite is asked for', async () => {
    const { docs, scope } = setup()
    writeFileSync(join(workspace, 'out.pdf'), 'keep me')
    const sessionId = await openReport(docs, scope)
    await expect(docs.handle('ncw.writer', WRITER, 'documents.export', { sessionId, path: 'out.pdf', format: 'pdf' }, scope))
      .rejects.toMatchObject({ code: 'invalid_argument', message: expect.stringContaining('overwrite') })
    expect(readFileSync(join(workspace, 'out.pdf'), 'utf8')).toBe('keep me')
    const overwritten = await docs.handle('ncw.writer', WRITER, 'documents.export', { sessionId, path: 'out.pdf', format: 'pdf', overwrite: true }, scope)
    expect(readFileSync(join(workspace, 'out.pdf'), 'utf8')).toBe('pdf:hello')
    expect(overwritten.changed).toEqual({ path: 'out.pdf', kind: 'modified' })
    // 响应里不许出现宿主绝对路径
    expect(overwritten.data).toEqual({ path: 'out.pdf', snapshot: expect.anything() })
  })

  it('refuses to save after the document\'s parent directory was replaced by a symlink', async () => {
    const { docs, scope } = setup()
    mkdirSync(join(workspace, 'sub'))
    writeFileSync(join(workspace, 'sub', 'doc.docx'), 'hello')
    const sessionId = await openReport(docs, scope, 'ncw.writer', WRITER, 'sub/doc.docx')
    await docs.handle('ncw.writer', WRITER, 'documents.apply', { sessionId, generation: 1, modelRevision: 0, operationId: 'a', operations: replace('!') }, scope)
    const outside = join(root, 'outside')
    mkdirSync(outside)
    writeFileSync(join(outside, 'doc.docx'), 'hello')
    rmSync(join(workspace, 'sub'), { recursive: true, force: true })
    symlinkSync(outside, join(workspace, 'sub'))
    await expect(docs.handle('ncw.writer', WRITER, 'documents.save', { sessionId }, scope))
      .rejects.toMatchObject({ code: 'invalid_argument', message: expect.stringContaining('symlinks') })
    expect(readFileSync(join(outside, 'doc.docx'), 'utf8')).toBe('hello')
  })

  it('refuses to reuse a lease whose engine is no longer available', async () => {
    let available = true
    const sessions = new DocumentSessionManager({ privateDir: join(root, 'private'), timeoutMs: 1000 })
    const engine = new FakeEngine(ENGINE)
    sessions.registerProvider(engine)
    const docs = new PluginDocuments({
      sessions,
      ensureProvider: (id) => (available && id === ENGINE ? engine : null),
      retireEngines: async () => undefined,
      accountScope: () => 'acct'
    })
    const scope = { workspaceId: 'ws1', workspaceRoot: workspace }
    await openReport(docs, scope)
    available = false
    await expect(docs.handle('ncw.writer', WRITER, 'documents.open', { path: 'report.docx' }, scope))
      .rejects.toMatchObject({ code: 'internal_error', message: expect.stringContaining('[engine_unavailable]') })
  })

  it('lets the trusted host discard unsaved state explicitly and reopen the disk version', async () => {
    const { docs, scope } = setup()
    const sessionId = await openReport(docs, scope)
    await docs.handle('ncw.writer', WRITER, 'documents.apply', { sessionId, generation: 1, modelRevision: 0, operationId: 'discard', operations: replace('!') }, scope)
    await expect(docs.assertCanRelease()).rejects.toMatchObject({ code: 'rejected' })
    await docs.discardAll()
    await expect(docs.assertCanRelease()).resolves.toBeUndefined()
    expect(readFileSync(join(workspace, 'report.docx'), 'utf8')).toBe('hello')
    await expect(docs.handle('ncw.writer', WRITER, 'documents.getState', { sessionId }, scope)).rejects.toMatchObject({ code: 'rejected' })
    expect(await openReport(docs, scope)).not.toBe(sessionId)
  })

  it('retains every consumer as an owner of a dirty shared session after its leases close', async () => {
    const { docs, scope } = setup()
    const sessionId = await openReport(docs, scope)
    await docs.handle('ncw.writer', WRITER, 'documents.apply', { sessionId, generation: 1, modelRevision: 0, operationId: 'shared', operations: replace('!') }, scope)
    await docs.handle('ncw.writer', WRITER, 'documents.close', { sessionId }, scope)
    expect(await openReport(docs, scope, 'acme.other')).toBe(sessionId)
    await docs.handle('acme.other', WRITER, 'documents.close', { sessionId }, scope)
    await expect(docs.assertCanRelease('ncw.writer')).rejects.toMatchObject({ code: 'rejected' })
    await expect(docs.assertCanRelease('acme.other')).rejects.toMatchObject({ code: 'rejected' })
  })

  it('does not let a cached lease bypass changed dependencies or a different explicit engine', async () => {
    const { docs, scope } = setup()
    await openReport(docs, scope)
    await expect(docs.handle('ncw.writer', manifestOf({ dependencies: {} }), 'documents.open', { path: 'report.docx' }, scope)).rejects.toMatchObject({ code: 'invalid_argument' })
    await expect(docs.handle('ncw.writer', WRITER, 'documents.open', { path: 'report.docx', engine: 'ncw.office-runtime/other' }, scope)).rejects.toMatchObject({ code: 'internal_error' })
    await expect(docs.handle('ncw.writer', WRITER, 'documents.open', { path: 'report.docx', engine: 'undeclared' }, scope)).rejects.toMatchObject({ code: 'invalid_argument' })
  })

  it('blocks release only while a document of that plugin or of its engine is dirty', async () => {
    const { docs, scope } = setup()
    const sessionId = await openReport(docs, scope)
    await expect(docs.assertCanRelease('ncw.writer')).resolves.toBeUndefined()
    await docs.handle('ncw.writer', WRITER, 'documents.apply', { sessionId, generation: 1, modelRevision: 0, operationId: 'a', operations: replace('!') }, scope)
    await expect(docs.assertCanRelease('ncw.writer')).rejects.toMatchObject({ code: 'rejected', message: expect.stringContaining('unsaved') })
    await expect(docs.assertCanRelease()).rejects.toMatchObject({ message: expect.stringContaining('unsaved') })
    // 引擎插件的 providerId 落在这份脏会话上:它被卸载时那些文档同样没救了
    await expect(docs.assertCanRelease('ncw.office-runtime')).rejects.toMatchObject({ message: expect.stringContaining('unsaved') })
    await expect(docs.assertCanRelease('evil.plugin')).resolves.toBeUndefined()
    await docs.handle('ncw.writer', WRITER, 'documents.save', { sessionId }, scope)
    await expect(docs.assertCanRelease('ncw.writer')).resolves.toBeUndefined()
  })
})

describe('engineCandidates / toCapabilityError', () => {
  it('orders editor-bound engines before the plugin\'s own and qualifies bare ids', () => {
    const manifest = manifestOf({ editorEngine: ENGINE, engines: ['local'] })
    expect(engineCandidates('acme.writer', manifest)).toEqual([ENGINE, 'acme.writer/local'])
  })

  it('maps document codes onto the fixed plugin error codes by how the caller should react', () => {
    expect(toCapabilityError(new DocumentEngineError('invalid_operation', 'm')).code).toBe('invalid_argument')
    expect(toCapabilityError(new DocumentEngineError('result_unknown', 'm')).code).toBe('rejected')
    // 组字中的 busy 是「稍后重试」,不是参数错也不是故障:Agent 看到 [busy] 就知道该等一下再来
    expect(toCapabilityError(new DocumentEngineError('busy', 'm'))).toMatchObject({ code: 'rejected', message: '[busy] m' })
    expect(toCapabilityError(new DocumentEngineError('engine_crashed', 'm'))).toMatchObject({ code: 'internal_error', message: '[engine_crashed] m' })
  })
})
