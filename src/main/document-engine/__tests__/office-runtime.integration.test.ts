/**
 * 真实文档引擎的宿主集成测试 —— 真 helper + 随包 LibreOffice，走生产代码路径。
 *
 * 需求（计划 P0）：证明「打开 → 修改 → 读回 → 渲染 → 保存 → 新进程重开」在**宿主**这一侧成立，
 * 而不只是 helper 自己的协议测试（那在 ncw-office-runtime 仓库里）。这里经过的是生产组件：
 * `DocumentEngineProviderRegistry`（默认 provider，spawn 前核对入口摘要）→
 * `DocumentSessionManager` → `NativeDocumentEngineProvider` → 插件 RPC `PluginDocuments`。
 * 两边协议漂移（操作名、查询种类、渲染参数、回执形状）只有在这一层才会暴露。
 *
 * 需要：
 *   NCW_OFFICE_RUNTIME  = 已解开的单平台引擎插件目录（package.json 带本平台 target 与 payload）
 *   或 NCW_OFFICE_RUNTIME_ZIP = 发行 ZIP：先经**真实安装器**装进临时插件根（核对摘要、文件索引、
 *                              重建包内链接），再对装好的那份跑同一组用例 —— 证明发行物本身可用
 *   NCW_OFFICE_FIXTURES = 含 blank.docx / blank.xlsx 的目录
 * 缺失时跳过；经 `npm run test:office-native` 运行（或设 NCW_OFFICE_REQUIRED=1）时缺失即失败 ——
 * CI 上「跳过」等于没验证，不能显示成绿色。
 */
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import type { PluginManifest } from '../../../shared/plugin/manifest'
import { installPluginZip, readInstalledManifest } from '../../plugin/installer'
import { PluginDocuments, type DocumentCallScope } from '../../plugin/document-rpc'
import { DocumentViewChannel } from '../../plugin/document-view'
import { DocumentSessionManager } from '../manager'
import { DocumentEngineProviderRegistry } from '../provider-registry'

const runtimeSource = process.env.NCW_OFFICE_RUNTIME ?? ''
const runtimeZip = process.env.NCW_OFFICE_RUNTIME_ZIP ?? ''
const fixtureDir = process.env.NCW_OFFICE_FIXTURES ?? ''
// 用 npm 的 lifecycle 变量而不是在脚本里写 `VAR=1 cmd`：后者在 Windows cmd 下不成立，而 Windows 是必需目标
const required = process.env.NCW_OFFICE_REQUIRED === '1' || process.env.npm_lifecycle_event === 'test:office-native'
const available = fixtureDir !== '' && (runtimeZip !== '' ? existsSync(runtimeZip) : runtimeSource !== '' && existsSync(join(runtimeSource, 'package.json')))
if (required && !available) {
  throw new Error('NCW_OFFICE_REQUIRED=1 but NCW_OFFICE_RUNTIME / NCW_OFFICE_FIXTURES do not point at a built engine plugin and fixtures')
}

const ENGINE = 'ncw.office-runtime/office'
const TIMEOUT = 180_000
// 安装 ZIP 要解开并逐个核对上万个文件（约 800 MB），给足时间
const INSTALL_TIMEOUT = 900_000
// 引擎打开一份文档要几秒（冷启动 LibreOffice），单次引擎调用给足余量
const ENGINE_TIMEOUT_MS = 60_000

/** 消费方插件的最小清单：绑定引擎、声明依赖 —— 只填 document-rpc 读到的字段 */
const WRITER = {
  dependencies: { 'ncw.office-runtime': '^0.1.0' },
  contributes: { customEditors: [{ viewType: 'ncw.writer', documentEngine: ENGINE }], documentEngines: [] }
} as unknown as PluginManifest

describe.skipIf(!available)('real office engine through the host', () => {
  let root = ''
  let workspace = ''
  let sessions: DocumentSessionManager
  let registry: DocumentEngineProviderRegistry
  let docs: PluginDocuments
  let scope: DocumentCallScope

  beforeAll(async () => {
    root = mkdtempSync(join(tmpdir(), 'ncw-office-host-'))
    workspace = join(root, 'ws')
    mkdirSync(workspace)
    const runtimeDir = runtimeZip !== '' ? (await installPluginZip(runtimeZip, join(root, 'plugins'))).target : runtimeSource
    const manifest = await readInstalledManifest(runtimeDir)
    sessions = new DocumentSessionManager({ privateDir: join(root, 'private'), timeoutMs: ENGINE_TIMEOUT_MS })
    registry = new DocumentEngineProviderRegistry({
      sessions,
      workRoot: join(root, 'helpers'),
      platform: process.platform,
      arch: process.arch,
      lookupPlugin: (id) => (id === 'ncw.office-runtime' ? { pluginId: id, root: runtimeDir, manifest } : null)
    })
    docs = new PluginDocuments({
      sessions,
      ensureProvider: (id) => registry.ensure(id),
      engineVersion: (id) => (id === 'ncw.office-runtime' ? manifest.version : null),
      retireEngines: (id) => registry.retire(id),
      accountScope: () => 'acct'
    })
    scope = { workspaceId: 'ws', workspaceRoot: workspace }
  }, INSTALL_TIMEOUT)

  afterAll(async () => {
    await sessions?.closeAll()
    if (root !== '') rmSync(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 })
  }, TIMEOUT)

  const fixture = (kind: 'docx' | 'xlsx', name: string): string => {
    copyFileSync(join(fixtureDir, `blank.${kind}`), join(workspace, name))
    return name
  }

  const open = async (path: string): Promise<{ sessionId: string; generation: number }> => {
    const { data } = await docs.handle('ncw.writer', WRITER, 'documents.open', { path }, scope)
    const opened = data as { sessionId: string; snapshot: { generation: number; modelRevision: number }; capabilities: { operations: string[]; engineVersion: string } }
    return { sessionId: opened.sessionId, generation: opened.snapshot.generation }
  }

  it('reports the Word operations the helper really supports through the host capability filter', async () => {
    const path = fixture('docx', 'caps.docx')
    const { data } = await docs.handle('ncw.writer', WRITER, 'documents.open', { path }, scope)
    const capabilities = (data as { capabilities: { operations: string[]; engineVersion: string; canSave: boolean } }).capabilities
    // 这几支若被宿主的 parseCapabilities 过滤掉，Agent 就只能追加、改不了现有内容
    expect(capabilities.operations).toEqual(expect.arrayContaining(['text.insert', 'text.findReplace', 'paragraph.style', 'paragraph.insert']))
    expect(capabilities.engineVersion).toMatch(/LibreOffice/)
    expect(capabilities.canSave).toBe(true)
  }, TIMEOUT)

  it('.docx: edits the live model, reads unsaved text back, renders it, saves, and a fresh engine sees the change', async () => {
    const path = fixture('docx', 'contract.docx')
    const original = readFileSync(join(workspace, path))
    const { sessionId, generation } = await open(path)

    await docs.handle('ncw.writer', WRITER, 'documents.apply', {
      sessionId, generation, modelRevision: 0, operationId: 'ins',
      operations: [{ kind: 'text.insert', target: { generation, ref: 'document' }, position: 'end', text: '第一章 总则\n甲方 Alpha 签署' }]
    }, scope)
    const replaced = await docs.handle('ncw.writer', WRITER, 'documents.apply', {
      sessionId, generation, modelRevision: 1, operationId: 'rep',
      operations: [{ kind: 'text.findReplace', find: '甲方', replace: '客户', expectedCount: 1 }, { kind: 'paragraph.style', find: '第一章', style: 'Heading 1' }]
    }, scope)
    // 逐条结果穿过宿主到达调用方：Agent 能看到替换了几处
    expect(replaced.data).toMatchObject({ appliedRevision: 2, dirty: true, results: [{ matches: 1 }, { matches: 1 }] })

    // 查询读的是活动模型：未保存的修改已经在里面，而磁盘上的文件一个字节都没变
    const queried = await docs.handle('ncw.writer', WRITER, 'documents.query', { sessionId, request: { kind: 'text' } }, scope)
    expect((queried.data as { result: { text: string } }).result.text).toMatch(/客户 Alpha 签署/)
    expect(readFileSync(join(workspace, path)).equals(original)).toBe(true)

    // 版面 + 渲染：画布拿得到真实页面像素
    const layout = await sessions.query({ sessionId, scope: { accountScope: 'acct', workspaceId: 'ws' }, request: { kind: 'layout' } })
    const page = (layout.result as { pages: { x: number; y: number; width: number; height: number }[] }).pages[0]
    expect(page).toBeDefined()
    const width = 300
    const height = Math.round((width * (page?.height ?? 1)) / (page?.width ?? 1))
    const image = await sessions.render({
      sessionId,
      scope: { accountScope: 'acct', workspaceId: 'ws' },
      request: { x: page?.x, y: page?.y, tileWidth: page?.width, tileHeight: page?.height, width, height }
    })
    expect(image).toMatchObject({ width, height, format: 'rgba', modelRevision: 2 })
    let ink = 0
    for (let i = 0; i < Math.round(height / 8) * width * 4; i += 4) {
      if ((image.bytes[i] ?? 255) < 128 && (image.bytes[i + 1] ?? 255) < 128 && (image.bytes[i + 2] ?? 255) < 128) ink += 1
    }
    expect(ink).toBeGreaterThan(20)

    const saved = await docs.handle('ncw.writer', WRITER, 'documents.save', { sessionId }, scope)
    expect(saved.changed).toEqual({ path, kind: 'modified' })
    expect(readFileSync(join(workspace, path)).equals(original)).toBe(false)
    const closed = await docs.handle('ncw.writer', WRITER, 'documents.close', { sessionId }, scope)
    expect(closed.data).toEqual({ closed: true, dirty: false })

    // 关掉的会话已经收掉 helper；重开 = 一个全新的引擎进程读磁盘上的文件
    const reopened = await open(path)
    expect(reopened.sessionId).not.toBe(sessionId)
    const reread = await docs.handle('ncw.writer', WRITER, 'documents.query', { sessionId: reopened.sessionId, request: { kind: 'text' } }, scope)
    const lines = (reread.data as { result: { text: string } }).result.text.split(/\r?\n/).map((line) => line.trim()).filter((line) => line !== '')
    expect(lines).toEqual(['第一章 总则', '客户 Alpha 签署'])
    await docs.handle('ncw.writer', WRITER, 'documents.close', { sessionId: reopened.sessionId }, scope)
  }, TIMEOUT)

  it('.docx: rejects an expectedCount mismatch as a whole batch and leaves the model untouched', async () => {
    const path = fixture('docx', 'guard.docx')
    const { sessionId, generation } = await open(path)
    await docs.handle('ncw.writer', WRITER, 'documents.apply', {
      sessionId, generation, modelRevision: 0, operationId: 'g-ins',
      operations: [{ kind: 'text.insert', target: { generation, ref: 'document' }, position: 'end', text: '备注 甲方\n备注 乙方' }]
    }, scope)
    await expect(docs.handle('ncw.writer', WRITER, 'documents.apply', {
      sessionId, generation, modelRevision: 1, operationId: 'g-bad',
      operations: [{ kind: 'text.findReplace', find: '备注', replace: 'x', expectedCount: 1 }]
    }, scope)).rejects.toMatchObject({ code: 'invalid_argument' })
    const state = await docs.handle('ncw.writer', WRITER, 'documents.getState', { sessionId }, scope)
    // 被拒的批次不推进修订号，也不把会话标成崩溃
    expect(state.data).toMatchObject({ status: 'ready', modelRevision: 1 })
    await docs.handle('ncw.writer', WRITER, 'documents.close', { sessionId }, scope).catch(() => undefined)
  }, TIMEOUT)

  it('.docx: canvas input edits the same live model, counts only real edits, and blocks an Agent batch planned before the typing', async () => {
    const path = fixture('docx', 'typing.docx')
    const original = readFileSync(join(workspace, path))
    const { sessionId, generation } = await open(path)
    const callScope = { accountScope: 'acct', workspaceId: 'ws' }
    const typed = (text: string): unknown[] => [...text].flatMap((c) => [
      { type: 'key', action: 'press', charCode: c.codePointAt(0) },
      { type: 'key', action: 'release', charCode: c.codePointAt(0) }
    ])
    // ★ 画过的区域才会收到失效通知:先像视图那样画一次首页
    const layout = await sessions.query({ sessionId, scope: callScope, request: { kind: 'layout' } })
    const page = (layout.result as { pages: { x: number; y: number; width: number; height: number }[] }).pages[0]
    await sessions.render({ sessionId, scope: callScope, request: { x: page?.x, y: page?.y, tileWidth: page?.width, tileHeight: page?.height, width: 200, height: 283 } })

    const hello = await sessions.input({ sessionId, scope: callScope, generation, events: typed('Hi') })
    expect(hello).toMatchObject({ modified: true, modelRevision: 1 })
    expect(hello.invalidations.all || hello.invalidations.rects.length > 0).toBe(true)
    // 光标左移:不改模型,修订号不动
    const moved = await sessions.input({ sessionId, scope: callScope, generation, events: [{ type: 'key', action: 'press', keyCode: 1026 }, { type: 'key', action: 'release', keyCode: 1026 }] })
    expect(moved).toMatchObject({ modified: false, modelRevision: 1 })
    // 组字提交:最终文字进模型,拼音不进
    const composing = await sessions.input({ sessionId, scope: callScope, generation, events: [{ type: 'text', action: 'compose', text: 'zhong' }] })
    expect(composing).toMatchObject({ modified: false, composing: true })
    // 组字期间拼音就在模型里:Agent 的读取被引擎以 busy 挡下,插件层是可重试的 rejected,会话照常可用(下面的提交能进)
    await expect(docs.handle('ncw.writer', WRITER, 'documents.query', { sessionId, request: { kind: 'text' } }, scope))
      .rejects.toMatchObject({ code: 'rejected', message: expect.stringContaining('[busy]') })
    const committed = await sessions.input({ sessionId, scope: callScope, generation, events: [{ type: 'text', action: 'commit', text: '中' }] })
    expect(committed).toMatchObject({ modified: true, modelRevision: 2 })

    // Agent 在打字之前读到的是修订 0:它的批次必须被挡下,不能把用户刚打的字覆盖掉
    await expect(docs.handle('ncw.writer', WRITER, 'documents.apply', {
      sessionId, generation, modelRevision: 0, operationId: 'stale-agent',
      operations: [{ kind: 'text.insert', target: { generation, ref: 'document' }, position: 'end', text: 'late' }]
    }, scope)).rejects.toMatchObject({ code: 'rejected', message: expect.stringContaining('[stale_revision]') })
    const queried = await docs.handle('ncw.writer', WRITER, 'documents.query', { sessionId, request: { kind: 'text' } }, scope)
    expect((queried.data as { result: { text: string } }).result.text).toBe('H中i')
    expect(readFileSync(join(workspace, path)).equals(original)).toBe(true)

    // 读到最新修订的 Agent 可以改;改完之后用户画布要重画,但拉取不能把同一次修改再记一遍
    const agent = await docs.handle('ncw.writer', WRITER, 'documents.apply', {
      sessionId, generation, modelRevision: 2, operationId: 'fresh-agent',
      operations: [{ kind: 'text.insert', target: { generation, ref: 'document' }, position: 'end', text: '!' }]
    }, scope)
    expect(agent.data).toMatchObject({ appliedRevision: 3 })
    const drained = await sessions.input({ sessionId, scope: callScope, generation, events: [] })
    expect(drained).toMatchObject({ modified: false, modelRevision: 3 })
    expect(drained.invalidations.all || drained.invalidations.rects.length > 0).toBe(true)
    // Agent 在它自己的视图里改到了文末;用户的插入点仍在「中」之后
    await sessions.input({ sessionId, scope: callScope, generation, events: typed('X') })
    const mixed = await docs.handle('ncw.writer', WRITER, 'documents.query', { sessionId, request: { kind: 'text' } }, scope)
    expect((mixed.data as { result: { text: string } }).result.text).toBe('H中Xi!')

    await docs.handle('ncw.writer', WRITER, 'documents.save', { sessionId }, scope)
    await docs.handle('ncw.writer', WRITER, 'documents.close', { sessionId }, scope)
    const reopened = await open(path)
    const reread = await docs.handle('ncw.writer', WRITER, 'documents.query', { sessionId: reopened.sessionId, request: { kind: 'text' } }, scope)
    expect((reread.data as { result: { text: string } }).result.text.trim()).toBe('H中Xi!')
    await docs.handle('ncw.writer', WRITER, 'documents.close', { sessionId: reopened.sessionId }, scope)
  }, TIMEOUT)

  it('.docx: the editor canvas channel draws, types and saves the same live model the Agent edits, and is told when the Agent changes it', async () => {
    const path = fixture('docx', 'canvas.docx')
    const pushed: unknown[] = []
    const views = new DocumentViewChannel({
      documents: docs,
      sessions,
      accountScope: () => 'acct',
      lookupPlugin: (id) => (id === 'ncw.writer' ? WRITER : null),
      lookupWorkspaceRoot: (id) => (id === 'ws' ? workspace : null)
    })
    const WINDOW = 1
    const opened = await views.open(WINDOW, { workspaceId: 'ws', path, pluginId: 'ncw.writer', viewType: 'ncw.writer' })
    expect(opened.capabilities.interaction).toEqual({ keyboard: true, mouse: true, textInput: true, visibleArea: true })
    const { layout } = await views.layout(WINDOW, { token: opened.token })
    const page = (layout as { pages: { x: number; y: number; width: number; height: number }[] }).pages[0]
    expect(page).toBeDefined()
    const tile = await views.render(WINDOW, { token: opened.token, request: { x: page?.x ?? 0, y: page?.y ?? 0, tileWidth: page?.width ?? 1, tileHeight: page?.height ?? 1, width: 200, height: 283 } })
    expect(tile.bytes.byteLength).toBe(200 * 283 * 4)
    const typed = await views.input(WINDOW, { token: opened.token, generation: opened.state.generation, events: [...'Hi'].flatMap((c) => [
      { type: 'key' as const, action: 'press' as const, charCode: c.codePointAt(0) ?? 0, keyCode: 0 },
      { type: 'key' as const, action: 'release' as const, charCode: c.codePointAt(0) ?? 0, keyCode: 0 }
    ]) })
    expect(typed).toMatchObject({ modified: true, modelRevision: 1 })

    // Agent 经插件 RPC 打开同一个文件:同一个会话,看得到画布刚打的字
    const agentOpen = await docs.handle('ncw.writer', WRITER, 'documents.open', { path }, scope)
    const agent = agentOpen.data as { sessionId: string; snapshot: { generation: number; modelRevision: number } }
    expect(agent.snapshot.modelRevision).toBe(1)
    await docs.handle('ncw.writer', WRITER, 'documents.apply', {
      sessionId: agent.sessionId, generation: agent.snapshot.generation, modelRevision: 1, operationId: 'canvas-agent',
      operations: [{ kind: 'text.insert', target: { generation: agent.snapshot.generation, ref: 'document' }, position: 'end', text: '!' }]
    }, scope)
    // 会话变更要推给画布(生产里由 onChange 触发;这里直接问通道该推给谁)
    pushed.push(...views.changed(sessions.snapshot(agent.sessionId, { accountScope: 'acct', workspaceId: 'ws' })))
    expect(pushed).toEqual([{ windowId: WINDOW, payload: { token: opened.token, state: expect.objectContaining({ modelRevision: 2, dirty: true }) } }])
    // 画布据推送拉取失效区域:不重复计修订号,但知道要重画哪里
    const drained = await views.input(WINDOW, { token: opened.token, generation: opened.state.generation, events: [] })
    expect(drained).toMatchObject({ modified: false, modelRevision: 2 })
    expect(drained.invalidations.all || drained.invalidations.rects.length > 0).toBe(true)

    // 功能区:全选不改模型,加粗改模型并回报按下状态;样式框的数据来自同一个会话
    expect(await views.command(WINDOW, { token: opened.token, generation: opened.state.generation, command: 'edit.selectAll' })).toMatchObject({ modified: false, modelRevision: 2 })
    const bold = await views.command(WINDOW, { token: opened.token, generation: opened.state.generation, command: 'format.bold' })
    expect(bold).toMatchObject({ modified: true, modelRevision: 3, states: { 'format.bold': 'true' } })
    await expect(views.command(WINDOW, { token: opened.token, generation: opened.state.generation, command: '.uno:Save' })).rejects.toMatchObject({ code: 'unsupported_operation' })
    expect((await views.list(WINDOW, { token: opened.token, kind: 'styles' })).names).toContain('Heading 1')

    const saved = await views.save(WINDOW, { token: opened.token })
    expect(saved).toMatchObject({ dirty: false, savedRevision: 3 })
    await views.closeWindow(WINDOW)
    await docs.handle('ncw.writer', WRITER, 'documents.close', { sessionId: agent.sessionId }, scope)
    const reopened = await open(path)
    const reread = await docs.handle('ncw.writer', WRITER, 'documents.query', { sessionId: reopened.sessionId, request: { kind: 'text' } }, scope)
    expect((reread.data as { result: { text: string } }).result.text.trim()).toBe('Hi!')
    await docs.handle('ncw.writer', WRITER, 'documents.close', { sessionId: reopened.sessionId }, scope)
  }, TIMEOUT)

  it('.xlsx: literal cells, computed formulas and a rendered sheet part, exported to PDF without touching the source', async () => {
    const path = fixture('xlsx', 'budget.xlsx')
    const { data } = await docs.handle('ncw.writer', WRITER, 'documents.open', { path }, scope)
    const opened = data as { sessionId: string; snapshot: { generation: number } }
    const outline = await docs.handle('ncw.writer', WRITER, 'documents.query', { sessionId: opened.sessionId, request: { kind: 'outline' } }, scope)
    const sheet = (outline.data as { result: { partNames: string[] } }).result.partNames[0] ?? 'Sheet1'
    await docs.handle('ncw.writer', WRITER, 'documents.apply', {
      sessionId: opened.sessionId, generation: opened.snapshot.generation, modelRevision: 0, operationId: 'cells',
      operations: [{ kind: 'cells.set', sheet, range: 'A1:B1', values: [[21, '007']] }, { kind: 'cells.formula', sheet, cell: 'C1', formula: 'A1*2' }]
    }, scope)
    const cells = await docs.handle('ncw.writer', WRITER, 'documents.query', { sessionId: opened.sessionId, request: { kind: 'cells', sheet, range: 'A1:C1' } }, scope)
    expect((cells.data as { result: { text: string } }).result.text.trim().split('\t')).toEqual(['21', '007', '42'])

    const image = await sessions.render({
      sessionId: opened.sessionId,
      scope: { accountScope: 'acct', workspaceId: 'ws' },
      request: { part: 0, x: 0, y: 0, tileWidth: 3000, tileHeight: 1500, width: 200, height: 100 }
    })
    expect(image.bytes.byteLength).toBe(200 * 100 * 4)

    const exported = await docs.handle('ncw.writer', WRITER, 'documents.export', { sessionId: opened.sessionId, path: 'budget.pdf', format: 'pdf' }, scope)
    expect(exported.changed).toEqual({ path: 'budget.pdf', kind: 'created' })
    expect(readFileSync(join(workspace, 'budget.pdf')).subarray(0, 5).toString('latin1')).toBe('%PDF-')
    // 导出不是保存：会话仍然是脏的
    const state = await docs.handle('ncw.writer', WRITER, 'documents.getState', { sessionId: opened.sessionId }, scope)
    expect(state.data).toMatchObject({ modelRevision: 1, savedRevision: 0 })
  }, TIMEOUT)
})
