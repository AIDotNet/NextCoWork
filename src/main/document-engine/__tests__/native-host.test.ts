/**
 * 原生 helper 适配层 —— 用一个**真的子进程**(node 跑的假 helper,说同一套分帧协议)
 * 钉住握手、请求配对、错误码收窄、崩溃通知、环境白名单与收尾。
 *
 * 不起真 LibreOffice:那要插件携带的原生构建(计划 §12 A,尚未完成)。这里测的是
 * 宿主这一侧的协议与进程管理,任何一个真 helper 都必须满足同样的契约。
 */
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { DocumentSessionManager } from '../manager'
import { NativeDocumentEngineProvider, NativeHelperConnection, helperEnv, parseCapabilities } from '../native-host'

let root: string

beforeEach(() => { root = mkdtempSync(join(tmpdir(), 'ncw-native-host-')) })
afterEach(() => { rmSync(root, { recursive: true, force: true }) })

/** 让出一段真实时间:下面几条用例钉的是「帧到达的先后」,不是同步调度顺序 */
const pause = (ms: number): Promise<void> => new Promise((resolve) => { setTimeout(resolve, ms) })

/** 假 helper:MODE 环境不可用(白名单会过滤),所以行为由 argv 决定。 */
const FAKE_HELPER = String.raw`
const fs = require('node:fs')
const mode = process.argv[2] || 'ok'
const protocol = mode === 'badproto' ? 99 : 1
function send(obj) {
  const payload = Buffer.from(JSON.stringify(obj), 'utf8')
  const header = Buffer.alloc(5); header.writeUInt32BE(payload.length, 0); header.writeUInt8(0, 4)
  process.stdout.write(Buffer.concat([header, payload]))
}
if (mode === 'silent') { setInterval(() => {}, 1000); return }
send({ v: 1, event: 'hello', data: { protocol, engineVersion: 'fake-lok-1' } })
let text = ''
let buf = Buffer.alloc(0)
process.stdin.on('data', (chunk) => {
  buf = Buffer.concat([buf, chunk])
  while (buf.length >= 5) {
    const len = buf.readUInt32BE(0)
    if (buf.length < 5 + len) break
    const msg = JSON.parse(buf.subarray(5, 5 + len).toString('utf8'))
    buf = buf.subarray(5 + len)
    handle(msg)
  }
})
function handle(msg) {
  const { id, method, params } = msg
  if (method === 'document.open') {
    text = fs.readFileSync(params.path, 'utf8')
    return send({ v: 1, id, ok: true, result: { capabilities: { operations: ['text.replace', 'bogus.op'], canSave: true, canExport: ['pdf', 'exe'], macros: { list: true } } } })
  }
  if (method === 'document.apply') {
    if (mode === 'crash-on-apply') process.exit(3)
    if (params.operations[0].text === 'REJECT') return send({ v: 1, id, ok: false, error: { code: 'invalid_operation', message: 'ref not found' } })
    if (params.operations[0].text === 'WEIRD') return send({ v: 1, id, ok: false, error: { code: 'rm -rf', message: 'x' } })
    for (const op of params.operations) text += op.text
    return send({ v: 1, id, ok: true, result: { warnings: ['simplified'], undoable: true } })
  }
  if (method === 'document.saveAs') { fs.writeFileSync(params.path, text); return send({ v: 1, id, ok: true, result: {} }) }
  if (method === 'document.render') {
    const size = params.width * params.height * 4
    const binary = (bytes) => { const h = Buffer.alloc(5); h.writeUInt32BE(bytes.length, 0); h.writeUInt8(1, 4); process.stdout.write(Buffer.concat([h, bytes])) }
    const reply = { v: 1, id, ok: true, result: { width: params.width, height: params.height, format: 'rgba', attachment: true } }
    /* 一条二进制帧拆成三次 write(帧头 / 前 8 字节 / 其余),间隔 10ms 保证宿主分多次读到 */
    const binaryChunked = (bytes, done) => {
      const h = Buffer.alloc(5); h.writeUInt32BE(bytes.length, 0); h.writeUInt8(1, 4)
      process.stdout.write(h)
      setTimeout(() => {
        process.stdout.write(bytes.subarray(0, 8))
        setTimeout(() => { process.stdout.write(bytes.subarray(8)); done() }, 10)
      }, 10)
    }
    if (mode === 'render-missing') return send(reply)
    if (mode === 'render-short') { binary(Buffer.alloc(size - 4, 9)); return send(reply) }
    if (mode === 'render-double') { binary(Buffer.alloc(size, 9)); binary(Buffer.alloc(size, 9)); return send(reply) }
    if (mode === 'render-double-chunked') { const b = Buffer.alloc(size, 7); binaryChunked(b, () => binaryChunked(b, () => send(reply))); return }
    if (mode === 'render-chunked') { binaryChunked(Buffer.alloc(size, 8), () => send(reply)); return }
    /* 违反 v1 的帧顺序:回执(声明了 attachment)先发,字节后发 */
    if (mode === 'render-reply-first') { send(reply); setTimeout(() => binary(Buffer.alloc(size, 5)), 50); return }
    /* 取消用例:字节立刻到,回执拖到 400ms —— 取消发生在「附件在途」那一刻 */
    if (mode === 'render-late-bytes') { binary(Buffer.alloc(size, 6)); setTimeout(() => send(reply), 400); return }
    /* 填充值跟着尺寸变:附件串台时长度与内容都对不上 */
    if (mode === 'render-fill') { binary(Buffer.alloc(size, size % 256)); return send(reply) }
    binary(Buffer.alloc(size, 9))
    return send(reply)
  }
  if (method === 'env') return send({ v: 1, id, ok: true, result: Object.keys(process.env) })
  if (method === 'shutdown') { send({ v: 1, id, ok: true, result: {} }); process.exit(0) }
}
`

function helperScript(): string {
  const path = join(root, 'fake-helper.cjs')
  writeFileSync(path, FAKE_HELPER)
  return path
}

function provider(mode = 'ok'): NativeDocumentEngineProvider {
  const script = helperScript()
  return new NativeDocumentEngineProvider({
    id: 'ncw.office-runtime/office',
    formats: ['docx'],
    resolveEntry: async () => process.execPath,
    args: [script, mode],
    workRoot: join(root, 'helpers'),
    startupTimeoutMs: 5000
  })
}

const SCOPE = { accountScope: 'a', workspaceId: 'w' }

describe('NativeHelperConnection', () => {
  it('rejects a helper that never says hello within the startup timeout', async () => {
    await expect(NativeHelperConnection.start({ command: process.execPath, args: [helperScript(), 'silent'], workDir: join(root, 'h1'), startupTimeoutMs: 300 }))
      .rejects.toMatchObject({ code: 'engine_unavailable' })
  })

  it('rejects a helper speaking another protocol version', async () => {
    await expect(NativeHelperConnection.start({ command: process.execPath, args: [helperScript(), 'badproto'], workDir: join(root, 'h2') }))
      .rejects.toThrow(/protocol 99/)
  })

  it('gives the helper only whitelisted environment variables and private HOME/TMP', async () => {
    process.env.NCW_SECRET_TOKEN_FOR_TEST = 'leak'
    try {
      const workDir = join(root, 'h3')
      const connection = await NativeHelperConnection.start({ command: process.execPath, args: [helperScript()], workDir })
      const keys = await connection.request('env', {}) as string[]
      expect(keys).not.toContain('NCW_SECRET_TOKEN_FOR_TEST')
      expect(keys).toContain('HOME')
      expect(helperEnv(workDir).HOME).toBe(join(workDir, 'home'))
      // 随包运行时装好后不能被 Python 字节码缓存改写(索引与 macOS 签名都依赖它不变)
      expect(helperEnv(workDir).PYTHONDONTWRITEBYTECODE).toBe('1')
      await connection.close()
      expect(existsSync(workDir)).toBe(false)
    } finally {
      delete process.env.NCW_SECRET_TOKEN_FOR_TEST
    }
  })
})

describe('NativeDocumentEngineProvider through the session manager', () => {
  it('opens, applies, saves through a real helper process and filters unknown capabilities', async () => {
    const file = join(root, 'a.docx')
    writeFileSync(file, 'hello')
    const manager = new DocumentSessionManager({ privateDir: join(root, 'private'), timeoutMs: 5000 })
    manager.registerProvider(provider())
    const { snapshot, capabilities, viewId } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: 'ncw.office-runtime/office' })
    expect(capabilities).toMatchObject({ engineVersion: 'fake-lok-1', operations: ['text.replace'], canExport: ['pdf'], macros: { list: true, run: false } })
    const applied = await manager.apply({ sessionId: snapshot.sessionId, scope: SCOPE, operationId: 'o1', generation: 1, modelRevision: 0, operations: [{ kind: 'text.replace', target: { generation: 1, ref: 'p' }, text: ' world' }] })
    expect(applied).toMatchObject({ warnings: ['simplified'], undoable: true })
    await manager.save({ sessionId: snapshot.sessionId, scope: SCOPE })
    expect((await import('node:fs')).readFileSync(file, 'utf8')).toBe('hello world')
    await manager.release({ sessionId: snapshot.sessionId, scope: SCOPE, viewId })
  })

  it('maps explicit engine rejections to rejected operations and unknown codes to io', async () => {
    const file = join(root, 'b.docx')
    writeFileSync(file, 'x')
    const manager = new DocumentSessionManager({ privateDir: join(root, 'private'), timeoutMs: 5000 })
    manager.registerProvider(provider())
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: 'ncw.office-runtime/office' })
    const op = (text: string): unknown[] => [{ kind: 'text.replace', target: { generation: 1, ref: 'p' }, text }]
    await expect(manager.apply({ sessionId: snapshot.sessionId, scope: SCOPE, operationId: 'r', generation: 1, modelRevision: 0, operations: op('REJECT') })).rejects.toMatchObject({ code: 'invalid_operation' })
    expect(manager.getOperation('r')?.status).toBe('rejected')
    expect(manager.snapshot(snapshot.sessionId, SCOPE).status).toBe('ready')
    // 未知错误码不可信 → io → 管理器按结果未知处理
    await expect(manager.apply({ sessionId: snapshot.sessionId, scope: SCOPE, operationId: 'w', generation: 1, modelRevision: 0, operations: op('WEIRD') })).rejects.toMatchObject({ code: 'result_unknown' })
    await manager.closeAll()
  })

  it('reports result_unknown and a crashed session when the helper dies mid-apply', async () => {
    const file = join(root, 'c.docx')
    writeFileSync(file, 'x')
    const manager = new DocumentSessionManager({ privateDir: join(root, 'private'), timeoutMs: 5000 })
    manager.registerProvider(provider('crash-on-apply'))
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: 'ncw.office-runtime/office' })
    await expect(manager.apply({ sessionId: snapshot.sessionId, scope: SCOPE, operationId: 'k', generation: 1, modelRevision: 0, operations: [{ kind: 'text.replace', target: { generation: 1, ref: 'p' }, text: 'y' }] }))
      .rejects.toMatchObject({ code: 'result_unknown' })
    expect(manager.snapshot(snapshot.sessionId, SCOPE).status).toBe('crashed')
    await manager.closeAll()
  })

  it('exports through the helper saveAs method without touching the source file', async () => {
    const file = join(root, 'export.docx')
    writeFileSync(file, 'hello')
    const manager = new DocumentSessionManager({ privateDir: join(root, 'private-export'), timeoutMs: 5000 })
    manager.registerProvider(provider())
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: 'ncw.office-runtime/office' })
    try {
      const target = join(root, 'export.pdf')
      /*
        ★ 这条同时钉住协议:假 helper 只实现 document.saveAs。若宿主改回发
        document.exportAs(协议里没有这个方法),这次调用会一直等不到回执,直到
        会话超时——用例会以 timeout 失败,而不是无声地放过去。
      */
      const exported = await manager.exportDocument({ sessionId: snapshot.sessionId, scope: SCOPE, outputPath: target, format: 'pdf' })
      expect(exported.outputPath).toBe(target)
      expect(readFileSync(target, 'utf8')).toBe('hello')
      // 导出 ≠ 保存:源文件与 dirty 账目都不动
      expect(readFileSync(file, 'utf8')).toBe('hello')
      expect(exported.snapshot.modelRevision).toBe(exported.snapshot.savedRevision)
    } finally {
      await manager.closeAll()
    }
  })
})

describe('rendering through a real helper process', () => {
  const RENDER = { x: 0, y: 0, tileWidth: 100, tileHeight: 100, width: 3, height: 2 }

  async function openWith(mode: string): Promise<{ manager: DocumentSessionManager; sessionId: string }> {
    const file = join(root, `render-${mode}.docx`)
    writeFileSync(file, 'x')
    const manager = new DocumentSessionManager({ privateDir: join(root, `private-${mode}`), timeoutMs: 5000 })
    manager.registerProvider(provider(mode))
    const { snapshot } = await manager.open({ scope: SCOPE, absolutePath: file, providerId: 'ncw.office-runtime/office' })
    return { manager, sessionId: snapshot.sessionId }
  }

  it('pairs the binary frame with its response and returns straight RGBA of the requested size', async () => {
    const { manager, sessionId } = await openWith('ok')
    const image = await manager.render({ sessionId, scope: SCOPE, request: RENDER })
    expect(image).toMatchObject({ width: 3, height: 2, format: 'rgba', generation: 1, modelRevision: 0 })
    expect(image.bytes.byteLength).toBe(24)
    expect(image.bytes[0]).toBe(9)
    // 附件配对之后通道仍正常:接着查一次状态
    expect(manager.snapshot(sessionId, SCOPE).status).toBe('ready')
    await manager.closeAll()
  })

  it('rejects oversized renders before they reach the engine', async () => {
    const { manager, sessionId } = await openWith('ok')
    await expect(manager.render({ sessionId, scope: SCOPE, request: { ...RENDER, width: 4096 } })).rejects.toMatchObject({ code: 'invalid_operation' })
    expect(manager.snapshot(sessionId, SCOPE).status).toBe('ready')
    await manager.closeAll()
  })

  it('treats a missing or short attachment as an engine fault', async () => {
    for (const mode of ['render-missing', 'render-short']) {
      const { manager, sessionId } = await openWith(mode)
      await expect(manager.render({ sessionId, scope: SCOPE, request: RENDER })).rejects.toMatchObject({ code: 'io' })
      expect(manager.snapshot(sessionId, SCOPE).status).toBe('crashed')
      await manager.closeAll()
    }
  })

  it('★ kills a helper that sends two attachments for one response rather than mispairing them', async () => {
    const { manager, sessionId } = await openWith('render-double')
    await expect(manager.render({ sessionId, scope: SCOPE, request: RENDER })).rejects.toBeDefined()
    expect(manager.snapshot(sessionId, SCOPE).status).toBe('crashed')
    await manager.closeAll()
  })

  it('keeps two consecutive renders of different sizes on their own attachments', async () => {
    const { manager, sessionId } = await openWith('render-fill')
    try {
      // 填充值就是字节数(24 = 3×2×4,64 = 4×4×4):附件一旦串台,长度和内容同时对不上
      const first = await manager.render({ sessionId, scope: SCOPE, request: RENDER })
      expect(first.bytes.byteLength).toBe(24)
      expect(first.bytes[0]).toBe(24)
      const second = await manager.render({ sessionId, scope: SCOPE, request: { ...RENDER, width: 4, height: 4 } })
      expect(second.bytes.byteLength).toBe(64)
      expect(second.bytes[0]).toBe(64)
      expect(manager.snapshot(sessionId, SCOPE).status).toBe('ready')
    } finally {
      await manager.closeAll()
    }
  })

  it('reassembles a binary attachment the helper wrote in several chunks', async () => {
    const { manager, sessionId } = await openWith('render-chunked')
    try {
      const image = await manager.render({ sessionId, scope: SCOPE, request: RENDER })
      expect(image.bytes.byteLength).toBe(24)
      expect(image.bytes[0]).toBe(8)
      expect(manager.snapshot(sessionId, SCOPE).status).toBe('ready')
    } finally {
      await manager.closeAll()
    }
  })

  it('★ kills a helper that sends a second attachment even when both are split across chunks', async () => {
    const { manager, sessionId } = await openWith('render-double-chunked')
    try {
      await expect(manager.render({ sessionId, scope: SCOPE, request: RENDER })).rejects.toBeDefined()
      expect(manager.snapshot(sessionId, SCOPE).status).toBe('crashed')
    } finally {
      await manager.closeAll()
    }
  })

  it('rejects a response that declares its attachment before the binary frame arrives', async () => {
    const { manager, sessionId } = await openWith('render-reply-first')
    try {
      await expect(manager.render({ sessionId, scope: SCOPE, request: RENDER })).rejects.toMatchObject({ code: 'io' })
      expect(manager.snapshot(sessionId, SCOPE).status).toBe('crashed')
      // 通道已作废:迟到的 binary 与后续请求都不能再成功(不存在「先 JSON 再 binary」这条协议)
      await expect(manager.render({ sessionId, scope: SCOPE, request: RENDER })).rejects.toMatchObject({ code: 'engine_unavailable' })
    } finally {
      await manager.closeAll()
    }
  })

  it('invalidates the channel when a render is cancelled, so its late attachment cannot reach the next request', async () => {
    const connection = await NativeHelperConnection.start({ command: process.execPath, args: [helperScript(), 'render-late-bytes'], workDir: join(root, 'h4') })
    try {
      const crashes: string[] = []
      connection.onExit((reason) => { crashes.push(reason) })
      const controller = new AbortController()
      const render = connection.requestAttachment('document.render', RENDER, 24, controller.signal)
      // 附件在途时别的请求当场拒绝:否则那条无 id 的附件字节流会有两种解释
      await expect(connection.request('env', {})).rejects.toMatchObject({ code: 'invalid_operation' })
      await pause(200) // helper 立刻发 tile,回执被拖到 400ms:此刻附件已到、请求未完成
      controller.abort()
      await expect(render).rejects.toMatchObject({ code: 'timeout' })
      // ★ 崩溃必须在取消这一刻同步通知到会话管理器,而不是等真实 exit 事件
      expect(crashes).toHaveLength(1)
      await pause(400) // 迟到回执的窗口过去之后,这条通道仍然接不了新请求
      await expect(connection.request('env', {})).rejects.toMatchObject({ code: 'engine_crashed' })
    } finally {
      await connection.close()
    }
  })
})

describe('parseCapabilities', () => {
  it('pins the format to the opened file and drops unknown fields', () => {
    expect(parseCapabilities({ capabilities: { format: 'pdf', operations: ['x', 'cells.set'] } }, 'xlsx', 'v')).toEqual({
      format: 'xlsx', engineVersion: 'v', operations: ['cells.set'], canSave: false, canExport: [], canUndo: false, macros: { list: false, run: false }
    })
    expect(parseCapabilities(null, 'docx', '').operations).toEqual([])
  })
})
