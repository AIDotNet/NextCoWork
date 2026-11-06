import { linkSync, mkdtempSync, readFileSync, rmSync, statSync, symlinkSync, utimesSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { DocumentEngineError, type DocumentCapabilities, type DocumentOperation } from '../../../shared/document-engine/protocol'
import type { DocumentSessionSnapshot } from '../../../shared/document-engine/session'
import { commitExport, commitSave, createWorkingCopy, digestFile } from '../file-store'
import { DocumentSessionManager, type DocumentEngineHandle, type DocumentEngineProvider } from '../manager'
import { FrameDecoder, FrameProtocolError, MAX_FRAME_BYTES, encodeBinaryFrame, encodeJsonFrame } from '../native-frame'

let root: string

beforeEach(() => { root = mkdtempSync(join(tmpdir(), 'ncw-doc-engine-')) })
afterEach(() => { rmSync(root, { recursive: true, force: true }) })

/** 让出一段真实时间:下面几条用例钉的是「排队 / 崩溃时序」,不是同步调度顺序 */
const pause = (ms: number): Promise<void> => new Promise((resolve) => { setTimeout(resolve, ms) })

// ─────────────────────────── fake engine ───────────────────────────

/**
 * 假引擎:把文档当成一段文本,`text.replace` 追加文字,`saveTo` 写出当前文本。
 * 可控制「挂起不回」与「中途崩溃」,用来钉住结果未知与崩溃路径。
 *
 * 导出相关的钩子(`exportHook` / `exportFail` / `closeGate`)是后加的:导出要测的是
 * 「等待 helper 期间目标被外部改动」这类窗口,没有钩子就没法在那一瞬间插入修改。
 */
class FakeEngine implements DocumentEngineProvider {
  readonly id = 'ncw.office-runtime/office'
  readonly formats = ['docx', 'xlsx'] as const
  opened = 0
  hang = false
  crashNext = false
  applied: DocumentOperation[][] = []
  /** apply 前先等它:用来把一次修改「卡」在队列里,测排队期间的判定 */
  gate: Promise<void> | null = null
  /** 导出前先跑它:模拟导出等待窗口里目标被创建 / 改写 */
  exportHook: (() => void) | null = null
  /** 让 helper 的导出直接失败 */
  exportFail = false
  /** close 完成前先等它:用来测崩溃后的收尾是否等待 */
  closeGate: Promise<void> | null = null
  closeStarted = 0
  closed = 0
  private crash: (() => void) | null = null

  async open(input: { workingPath: string; onCrash: () => void }): Promise<DocumentEngineHandle> {
    this.opened += 1
    this.crash = input.onCrash
    let text = readFileSync(input.workingPath, 'utf8')
    const capabilities: DocumentCapabilities = {
      format: 'docx', engineVersion: 'fake-1', operations: ['text.replace'], canSave: true, canExport: ['pdf'], canUndo: false, macros: { list: false, run: false }
    }
    return {
      capabilities,
      apply: async (operations) => {
        if (this.crashNext) { this.crashNext = false; this.crash?.(); throw new Error('helper exited') }
        if (this.hang) await new Promise(() => undefined)
        if (this.gate !== null) await this.gate
        this.applied.push(operations)
        for (const op of operations) if (op.kind === 'text.replace') text += op.text
        return { warnings: [], undoable: false }
      },
      saveTo: async (outputPath) => { writeFileSync(outputPath, text) },
      exportTo: async (outputPath) => {
        if (this.exportFail) throw new Error('export failed')
        this.exportHook?.()
        writeFileSync(outputPath, text)
      },
      close: async () => {
        this.closeStarted += 1
        if (this.closeGate !== null) await this.closeGate
        this.closed += 1
      }
    }
  }
}

const SCOPE = { accountScope: 'acct', workspaceId: 'ws1' }

function setup(options: { timeoutMs?: number } = {}): { manager: DocumentSessionManager; engine: FakeEngine; file: string; events: DocumentSessionSnapshot[] } {
  const file = join(root, 'report.docx')
  writeFileSync(file, 'hello')
  const events: DocumentSessionSnapshot[] = []
  const manager = new DocumentSessionManager({ privateDir: join(root, 'private'), timeoutMs: options.timeoutMs ?? 1000, onChange: (s) => { events.push(s) } })
  const engine = new FakeEngine()
  manager.registerProvider(engine)
  return { manager, engine, file, events }
}

const replace = (text: string): unknown[] => [{ kind: 'text.replace', target: { generation: 1, ref: 'p:0' }, text }]

describe('DocumentSessionManager', () => {
  it('shares one live model when the same file is opened twice (no second engine instance)', async () => {
    const { manager, engine, file } = setup()
    const [a, b] = await Promise.all([
      manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id }),
      manager.open({ scope: { ...SCOPE, workspaceId: 'ws2' }, absolutePath: file, providerId: engine.id })
    ])
    expect(a.snapshot.sessionId).toBe(b.snapshot.sessionId)
    expect(a.viewId).not.toBe(b.viewId)
    expect(engine.opened).toBe(1)
  })

  it('does not touch the original file bytes or mtime on open', async () => {
    const { manager, engine, file } = setup()
    utimesSync(file, new Date(1_000_000), new Date(1_000_000))
    const before = statSync(file).mtimeMs
    await manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id })
    expect(statSync(file).mtimeMs).toBe(before)
    expect(readFileSync(file, 'utf8')).toBe('hello')
  })

  it('applies, marks dirty, saves atomically and becomes clean', async () => {
    const { manager, engine, file } = setup()
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id })
    const result = await manager.apply({ sessionId: snapshot.sessionId, scope: SCOPE, operationId: 'op1', generation: 1, modelRevision: 0, operations: replace(' world') })
    expect(result).toMatchObject({ appliedRevision: 1, dirty: true })
    expect(readFileSync(file, 'utf8')).toBe('hello')
    const saved = await manager.save({ sessionId: snapshot.sessionId, scope: SCOPE })
    expect(readFileSync(file, 'utf8')).toBe('hello world')
    expect(saved.savedRevision).toBe(1)
    expect(saved.diskRevision).toBe(await digestFile(file))
  })

  it('rejects stale revisions so an old plan cannot write at outdated positions', async () => {
    const { manager, engine, file } = setup()
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id })
    await manager.apply({ sessionId: snapshot.sessionId, scope: SCOPE, operationId: 'op1', generation: 1, modelRevision: 0, operations: replace('a') })
    await expect(manager.apply({ sessionId: snapshot.sessionId, scope: SCOPE, operationId: 'op2', generation: 1, modelRevision: 0, operations: replace('b') }))
      .rejects.toMatchObject({ code: 'stale_revision' })
    expect(engine.applied).toHaveLength(1)
  })

  it('returns the recorded result for a repeated operationId instead of applying twice', async () => {
    const { manager, engine, file } = setup()
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id })
    const input = { sessionId: snapshot.sessionId, scope: SCOPE, operationId: 'same', generation: 1, modelRevision: 0, operations: replace('x') }
    const first = await manager.apply(input)
    const second = await manager.apply(input)
    expect(second).toEqual(first)
    expect(engine.applied).toHaveLength(1)
  })

  it('reports result_unknown on timeout, marks the session crashed and never replays', async () => {
    const { manager, engine, file } = setup({ timeoutMs: 20 })
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id })
    engine.hang = true
    const input = { sessionId: snapshot.sessionId, scope: SCOPE, operationId: 'slow', generation: 1, modelRevision: 0, operations: replace('x') }
    await expect(manager.apply(input)).rejects.toMatchObject({ code: 'result_unknown' })
    expect(manager.getOperation('slow')?.status).toBe('unknown')
    expect(manager.snapshot(snapshot.sessionId, SCOPE).status).toBe('crashed')
    await expect(manager.apply(input)).rejects.toMatchObject({ code: 'result_unknown' })
  })

  it('reloads from disk after a crash with a new generation, invalidating old references', async () => {
    const { manager, engine, file } = setup()
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id })
    engine.crashNext = true
    await expect(manager.apply({ sessionId: snapshot.sessionId, scope: SCOPE, operationId: 'c', generation: 1, modelRevision: 0, operations: replace('x') }))
      .rejects.toMatchObject({ code: 'result_unknown' })
    const reloaded = await manager.reloadFromDisk({ sessionId: snapshot.sessionId, scope: SCOPE })
    expect(reloaded).toMatchObject({ status: 'ready', generation: 2 })
    await expect(manager.apply({ sessionId: snapshot.sessionId, scope: SCOPE, operationId: 'd', generation: 1, modelRevision: 0, operations: replace('y') }))
      .rejects.toMatchObject({ code: 'stale_generation' })
  })

  it('enters conflict instead of overwriting when the file changed on disk', async () => {
    const { manager, engine, file } = setup()
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id })
    await manager.apply({ sessionId: snapshot.sessionId, scope: SCOPE, operationId: 'op', generation: 1, modelRevision: 0, operations: replace('!') })
    writeFileSync(file, 'someone else')
    await expect(manager.save({ sessionId: snapshot.sessionId, scope: SCOPE })).rejects.toMatchObject({ code: 'disk_conflict' })
    expect(readFileSync(file, 'utf8')).toBe('someone else')
    expect(manager.snapshot(snapshot.sessionId, SCOPE).status).toBe('conflict')
    const reloaded = await manager.reloadFromDisk({ sessionId: snapshot.sessionId, scope: SCOPE })
    expect(reloaded.diskRevision).toBe(await digestFile(file))
  })

  it('keeps a dirty session alive when the last view leaves unless forced', async () => {
    const { manager, engine, file } = setup()
    const { snapshot, viewId } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id })
    await manager.apply({ sessionId: snapshot.sessionId, scope: SCOPE, operationId: 'op', generation: 1, modelRevision: 0, operations: replace('!') })
    expect(await manager.release({ sessionId: snapshot.sessionId, scope: SCOPE, viewId })).toEqual({ closed: false, dirty: true })
    expect(manager.dirtySessions()).toHaveLength(1)
    expect(await manager.release({ sessionId: snapshot.sessionId, scope: SCOPE, viewId, force: true })).toEqual({ closed: true, dirty: false })
    expect(() => manager.snapshot(snapshot.sessionId, SCOPE)).toThrow(DocumentEngineError)
  })

  it('hides sessions from callers outside their account/workspace scope', async () => {
    const { manager, engine, file } = setup()
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id })
    expect(() => manager.snapshot(snapshot.sessionId, { accountScope: 'acct', workspaceId: 'other' })).toThrow(/no such document session/)
    expect(() => manager.snapshot(snapshot.sessionId, { accountScope: 'other', workspaceId: 'ws1' })).toThrow(/no such document session/)
  })

  it('refuses unsupported formats, unknown engines and unsupported operations', async () => {
    const { manager, engine, file } = setup()
    const pdf = join(root, 'a.pdf')
    writeFileSync(pdf, 'x')
    await expect(manager.open({ scope: SCOPE, absolutePath: join(root, 'a.doc'), providerId: engine.id })).rejects.toMatchObject({ code: 'unsupported_format' })
    await expect(manager.open({ scope: SCOPE, absolutePath: pdf, providerId: engine.id })).rejects.toMatchObject({ code: 'unsupported_format' })
    await expect(manager.open({ scope: SCOPE, absolutePath: file, providerId: 'missing/engine' })).rejects.toMatchObject({ code: 'engine_unavailable' })
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id })
    await expect(manager.apply({ sessionId: snapshot.sessionId, scope: SCOPE, operationId: 'u', generation: 1, modelRevision: 0, operations: [{ kind: 'slide.move', from: 0, to: 1 }] }))
      .rejects.toMatchObject({ code: 'unsupported_operation' })
    expect(engine.applied).toHaveLength(0)
  })

  it('saving an unmodified document is a no-op that leaves mtime alone', async () => {
    const { manager, engine, file } = setup()
    utimesSync(file, new Date(2_000_000), new Date(2_000_000))
    const before = statSync(file).mtimeMs
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id })
    await manager.save({ sessionId: snapshot.sessionId, scope: SCOPE })
    expect(statSync(file).mtimeMs).toBe(before)
  })

  it('rejects operation targets from an older engine generation', async () => {
    const { manager, engine, file } = setup()
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id })
    await expect(manager.apply({ sessionId: snapshot.sessionId, scope: SCOPE, operationId: 'g', generation: 1, modelRevision: 0, operations: [{ kind: 'text.replace', target: { generation: 0, ref: 'p:0' }, text: 'x' }] }))
      .rejects.toMatchObject({ code: 'stale_generation' })
    expect(engine.applied).toHaveLength(0)
  })

  it('validates queries and reports engines without query support honestly', async () => {
    const { manager, engine, file } = setup()
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id })
    await expect(manager.query({ sessionId: snapshot.sessionId, scope: SCOPE, request: { kind: 'cells', sheet: 'S', range: 'nope' } })).rejects.toMatchObject({ code: 'invalid_operation' })
    await expect(manager.query({ sessionId: snapshot.sessionId, scope: SCOPE, request: { kind: 'outline' } })).rejects.toMatchObject({ code: 'unsupported_operation' })
  })

  it('exports into the workspace from a private temp without touching the source or the session ledger', async () => {
    const { manager, engine, file } = setup()
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id })
    const target = join(root, 'out.pdf')
    const exported = await manager.exportDocument({ sessionId: snapshot.sessionId, scope: SCOPE, outputPath: target, format: 'pdf' })
    expect(exported.outputPath).toBe(target)
    expect(readFileSync(target, 'utf8')).toBe('hello')
    // 新文件 0600:导出物是文档内容,不该因为 umask 宽松而全局可读
    expect(statSync(target).mode & 0o777).toBe(0o600)
    // 导出 ≠ 保存:源文件与 dirty 账目都不动
    expect(readFileSync(file, 'utf8')).toBe('hello')
    expect(manager.snapshot(snapshot.sessionId, SCOPE).status).toBe('ready')
    expect(exported.snapshot.modelRevision).toBe(exported.snapshot.savedRevision)
  })

  it('refuses an existing export target unless overwrite is set, leaving it and the session untouched', async () => {
    const { manager, engine, file } = setup()
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id })
    const target = join(root, 'existing.pdf')
    writeFileSync(target, 'mine')
    await expect(manager.exportDocument({ sessionId: snapshot.sessionId, scope: SCOPE, outputPath: target, format: 'pdf' }))
      .rejects.toMatchObject({ code: 'invalid_operation' })
    expect(readFileSync(target, 'utf8')).toBe('mine')
    // 纯发布失败不改引擎账目:会话仍是 ready,不是 crashed
    expect(manager.snapshot(snapshot.sessionId, SCOPE).status).toBe('ready')
  })

  it('replaces an existing export target only when overwrite is set', async () => {
    const { manager, engine, file } = setup()
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id })
    const target = join(root, 'replace.pdf')
    writeFileSync(target, 'mine')
    await manager.exportDocument({ sessionId: snapshot.sessionId, scope: SCOPE, outputPath: target, format: 'pdf', overwrite: true })
    expect(readFileSync(target, 'utf8')).toBe('hello')
  })

  it('marks the session crashed when the helper export fails, leaving source and target untouched', async () => {
    const { manager, engine, file } = setup()
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id })
    engine.exportFail = true
    const target = join(root, 'failed.pdf')
    writeFileSync(target, 'mine')
    await expect(manager.exportDocument({ sessionId: snapshot.sessionId, scope: SCOPE, outputPath: target, format: 'pdf', overwrite: true })).rejects.toBeDefined()
    expect(manager.snapshot(snapshot.sessionId, SCOPE).status).toBe('crashed')
    expect(readFileSync(file, 'utf8')).toBe('hello')
    expect(readFileSync(target, 'utf8')).toBe('mine')
  })

  it('does not overwrite a target that appeared while the export was running', async () => {
    const { manager, engine, file } = setup()
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id })
    const target = join(root, 'race.pdf')
    // helper 跑着的时候别人抢先建了这个文件:link 以 EEXIST 失败,不覆盖它
    engine.exportHook = () => { writeFileSync(target, 'concurrent') }
    await expect(manager.exportDocument({ sessionId: snapshot.sessionId, scope: SCOPE, outputPath: target, format: 'pdf', overwrite: true }))
      .rejects.toMatchObject({ code: 'disk_conflict' })
    expect(readFileSync(target, 'utf8')).toBe('concurrent')
  })

  it('refuses to overwrite a target changed by someone else while exporting', async () => {
    const { manager, engine, file } = setup()
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id })
    const target = join(root, 'changed.pdf')
    writeFileSync(target, 'before')
    // 开始时摘要是 'before',导出期间被改成 'changed':commitSave 的检查必须挡住
    engine.exportHook = () => { writeFileSync(target, 'changed') }
    await expect(manager.exportDocument({ sessionId: snapshot.sessionId, scope: SCOPE, outputPath: target, format: 'pdf', overwrite: true }))
      .rejects.toMatchObject({ code: 'disk_conflict' })
    expect(readFileSync(target, 'utf8')).toBe('changed')
  })

  it('refuses to export over the open document and points at documents.save', async () => {
    const { manager, engine, file } = setup()
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id })
    await expect(manager.exportDocument({ sessionId: snapshot.sessionId, scope: SCOPE, outputPath: file, format: 'pdf' }))
      .rejects.toMatchObject({ code: 'invalid_operation', message: expect.stringContaining('documents.save') })
    expect(readFileSync(file, 'utf8')).toBe('hello')
  })

  it('refuses to export over a hard link to the open document (inode match, not path)', async () => {
    const { manager, engine, file } = setup()
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id })
    const alias = join(root, 'alias.pdf')
    linkSync(file, alias)
    await expect(manager.exportDocument({ sessionId: snapshot.sessionId, scope: SCOPE, outputPath: alias, format: 'pdf', overwrite: true }))
      .rejects.toMatchObject({ code: 'invalid_operation', message: expect.stringContaining('documents.save') })
    expect(readFileSync(file, 'utf8')).toBe('hello')
  })

  it('re-checks dirtiness inside the queue so a session that became dirty while queued is kept', async () => {
    const { manager, engine, file } = setup()
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id })
    let release = (): void => undefined
    engine.gate = new Promise<void>((resolve) => { release = resolve })
    const applying = manager.apply({ sessionId: snapshot.sessionId, scope: SCOPE, operationId: 'op', generation: 1, modelRevision: 0, operations: replace('!') })
    await pause(0) // 让 apply 进入队列并卡在 gate 上,此刻会话仍是干净的
    const closing = manager.closeAll(undefined, { onlyClean: true })
    release()
    await applying
    await closing
    // 排队时干净、轮到前变脏 → 必须保留,不能静默把未保存改动丢掉
    expect(manager.dirtySessions()).toHaveLength(1)
    expect(manager.snapshot(snapshot.sessionId, SCOPE).status).toBe('ready')
  })

  it('waits for a close already started by a crash before closeAll finishes cleanup', async () => {
    const { manager, engine, file } = setup()
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id })
    let releaseClose = (): void => undefined
    engine.closeGate = new Promise<void>((resolve) => { releaseClose = resolve })
    engine.crashNext = true
    await expect(manager.apply({ sessionId: snapshot.sessionId, scope: SCOPE, operationId: 'c', generation: 1, modelRevision: 0, operations: replace('x') }))
      .rejects.toMatchObject({ code: 'result_unknown' })
    // 崩溃路径 fire-and-forget 起的关闭还没完成:closeAll 必须等它,而不是直接删掉私有目录
    expect(engine.closeStarted).toBe(1)
    expect(engine.closed).toBe(0)
    let closedAllDone = false
    const closing = manager.closeAll().then(() => { closedAllDone = true })
    await pause(20)
    expect(closedAllDone).toBe(false)
    releaseClose()
    await closing
    expect(engine.closed).toBe(1)
    expect(closedAllDone).toBe(true)
  })

  it('bounds pixel dimensions but lets document-unit coordinates exceed the pixel cap', async () => {
    const { manager, engine, file } = setup()
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: engine.id })
    const request = (patch: Record<string, number>): unknown =>
      ({ x: 0, y: 0, tileWidth: 100, tileHeight: 100, width: 3, height: 2, ...patch })
    // 文档坐标不受像素上限约束:5000 / 900 万是正常的表格 / 图纸坐标,应通过校验到达引擎
    await expect(manager.render({ sessionId: snapshot.sessionId, scope: SCOPE, request: request({ x: 9_000_000, y: 0, tileWidth: 5000, tileHeight: 5000 }) }))
      .rejects.toMatchObject({ code: 'unsupported_operation' })
    // 像素边长仍受 MAX_RENDER_DIMENSION 约束
    await expect(manager.render({ sessionId: snapshot.sessionId, scope: SCOPE, request: request({ width: 4096 }) }))
      .rejects.toMatchObject({ code: 'invalid_operation' })
    // 坐标有界:超过 1e9 拒绝
    await expect(manager.render({ sessionId: snapshot.sessionId, scope: SCOPE, request: request({ x: 1_000_000_001 }) }))
      .rejects.toMatchObject({ code: 'invalid_operation' })
  })
})

// ─────────────────────────── file store ───────────────────────────

describe('file store', () => {
  it('refuses symlinked documents', async () => {
    const real = join(root, 'real.docx')
    writeFileSync(real, 'x')
    const link = join(root, 'link.docx')
    symlinkSync(real, link)
    await expect(createWorkingCopy(link, join(root, 'p'))).rejects.toMatchObject({ code: 'io' })
  })

  it('never replaces a document with an empty engine output', async () => {
    const target = join(root, 't.docx')
    writeFileSync(target, 'data')
    const produced = join(root, 'out.docx')
    writeFileSync(produced, '')
    await expect(commitSave(target, produced, await digestFile(target))).rejects.toMatchObject({ code: 'io' })
    expect(readFileSync(target, 'utf8')).toBe('data')
  })

  it('publishes a new export at 0600 and refuses an existing target by default', async () => {
    const produced = join(root, 'produced.pdf')
    writeFileSync(produced, 'payload')
    const target = join(root, 'published.pdf')
    await commitExport(target, produced, { expectedDiskRevision: null })
    expect(readFileSync(target, 'utf8')).toBe('payload')
    expect(statSync(target).mode & 0o777).toBe(0o600)
    // 已有目标默认拒绝:调用方必须显式 overwrite
    await expect(commitExport(target, produced, { expectedDiskRevision: await digestFile(target) }))
      .rejects.toMatchObject({ code: 'invalid_operation' })
    expect(readFileSync(target, 'utf8')).toBe('payload')
  })

  it('refuses to publish an empty export', async () => {
    const produced = join(root, 'empty.pdf')
    writeFileSync(produced, '')
    await expect(commitExport(join(root, 'empty-target.pdf'), produced, { expectedDiskRevision: null }))
      .rejects.toMatchObject({ code: 'io' })
    expect(() => statSync(join(root, 'empty-target.pdf'))).toThrow()
  })
})

// ─────────────────────────── frames ───────────────────────────

describe('native frame codec', () => {
  it('reassembles frames split across and packed within chunks', () => {
    const bytes = Buffer.concat([encodeJsonFrame({ a: 1 }), encodeBinaryFrame(new Uint8Array([1, 2, 3])), encodeJsonFrame('z')])
    const decoder = new FrameDecoder()
    const frames = [...decoder.push(bytes.subarray(0, 3)), ...decoder.push(bytes.subarray(3, 12)), ...decoder.push(bytes.subarray(12))]
    expect(frames).toEqual([{ kind: 'json', value: { a: 1 } }, { kind: 'binary', bytes: new Uint8Array([1, 2, 3]) }, { kind: 'json', value: 'z' }])
    expect(decoder.pendingBytes).toBe(0)
  })

  it('rejects oversized lengths before allocating, and stays broken afterwards', () => {
    const header = Buffer.alloc(5)
    header.writeUInt32BE(MAX_FRAME_BYTES + 1, 0)
    const decoder = new FrameDecoder()
    expect(() => decoder.push(header)).toThrow(FrameProtocolError)
    expect(() => decoder.push(encodeJsonFrame(1))).toThrow(FrameProtocolError)
  })

  it('rejects unknown frame types and invalid JSON', () => {
    const bad = Buffer.from([0, 0, 0, 1, 9, 0])
    expect(() => new FrameDecoder().push(bad)).toThrow(/unknown frame type/)
    const notJson = Buffer.concat([Buffer.from([0, 0, 0, 1, 0]), Buffer.from('{')])
    expect(() => new FrameDecoder().push(notJson)).toThrow(/not valid JSON/)
  })
})
