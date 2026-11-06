/**
 * 上传服务的集成测试 —— 真 SQLite、真文件系统,只把 `electron` 换掉。
 *
 * 用真库真文件而不是全 mock:这一层的价值恰恰在于**落盘顺序与去重查询**,
 * 把 fs 和 sqlite 都换成假的之后,剩下能测的只有「函数调用了另一个函数」。
 *
 * ★ `electron` 整个替掉:`app.getPath` 与 `dialog` 在 node 环境不存在。
 * 附件根指向临时目录,于是 `attachmentRoot()` 跟着走。
 */
import {
  existsSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  rmSync,
  truncateSync,
  writeFileSync
} from 'node:fs'
import { tmpdir } from 'node:os'
import { EventEmitter } from 'node:events'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

let userDataDir = ''
const { acquireEnvironment } = vi.hoisted(() => ({ acquireEnvironment: vi.fn() }))
vi.mock('../../runtime', () => ({ getEnvironments: () => ({ acquire: acquireEnvironment }) }))

vi.mock('electron', () => ({
  app: { getPath: (): string => userDataDir },
  dialog: { showOpenDialog: vi.fn() },
  net: { fetch: vi.fn(async (url: string) => new Response(new Uint8Array(readFileSync(fileURLToPath(url))))) }
}))

import { dialog } from 'electron'
import { MAX_ATTACHMENT_BYTES } from '../../../shared/domain/attachment'
import { closeDatabase, openDatabase } from '../../db'
import * as repo from '../../db/repo'
import { switchConfigProfile } from '../../db/config-profile'
import { attachmentRoot, handleAttachmentRequest } from '../../net/attachment-protocol'
import { ThumbnailCache } from '../../net/attachment-thumbnail'
import { cancelWorkspaceUpload, completeWorkspaceUpload, listSessionAttachments, pickAttachments, prepareWorkspaceUpload, removeAttachment, uploadAttachment } from '../attachment'
import { localEnvironment } from '../../environment/local'
import { nodeHost } from '../../kernel/host'
import { imageUrlNote, prepareRequestImages } from '../../kernel/upstream/images'
import { encodeUpstream } from '../../kernel/upstream/codec'
import { REQUEST } from '../../kernel/upstream/__tests__/openai-fixtures'
import { toolResultMessage, userMessage } from '../../../shared/agent/message'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../../shared/domain/workspace'
import type { WindowContext } from '../../window/registry'

const bytesOf = (s: string): Uint8Array<ArrayBuffer> =>
  new Uint8Array(Buffer.from(s, 'utf8')) as Uint8Array<ArrayBuffer>

beforeEach(() => {
  closeDatabase()
  userDataDir = mkdtempSync(join(tmpdir(), 'nextcowork-att-'))
  openDatabase(userDataDir)
})

afterEach(() => {
  closeDatabase()
  rmSync(userDataDir, { recursive: true, force: true })
})

describe('uploadAttachment', () => {
  it('requires a single-use window-bound upload approval and preserves source identity', async () => {
    const remote = mkdtempSync(join(tmpdir(), 'ncw-remote-attachment-'))
    const lease = { environment: { ...localEnvironment(nodeHost(), remote), remote: true, description: 'server' }, release: vi.fn() }
    acquireEnvironment.mockReturnValue(lease)
    const sender = Object.assign(new EventEmitter(), { isDestroyed: () => false }) as unknown as WindowContext['sender']
    const ctx: WindowContext = { id: 42, kind: 'main', sender }
    repo.putWorkspace({ id: 'W1', name: 'remote', rootPath: remote, environment: { kind: 'connection', connectionId: 'server' }, settings: DEFAULT_WORKSPACE_SETTINGS, createdAt: 1, lastOpenedAt: 1 })
    repo.putConnectionProfile({ id: 'server', name: 'server', kind: 'ssh', target: { kind: 'config', host: 'server' }, platform: 'auto', enabled: true, revision: 1, createdAt: 1, updatedAt: 1 })
    const attachment = uploadAttachment({ scope: 'session', ownerId: 'S1', displayName: 'report.pdf', mime: 'application/pdf', bytes: bytesOf('report') })
    try {
      const cancelled = await prepareWorkspaceUpload({ id: attachment.id, sessionId: 'S1', workspaceId: 'W1' }, ctx)
      expect(readdirSync(remote)).toEqual([])
      cancelWorkspaceUpload(cancelled.ticket, ctx)
      await expect(completeWorkspaceUpload(cancelled.ticket, ctx)).rejects.toThrow('approval-expired')
      const intent = await prepareWorkspaceUpload({ id: attachment.id, sessionId: 'S1', workspaceId: 'W1' }, ctx)
      await expect(completeWorkspaceUpload(intent.ticket, { ...ctx, id: 43 })).rejects.toThrow('approval-expired')
      const reference = await completeWorkspaceUpload(intent.ticket, ctx)
      expect(reference.source).toMatchObject({ kind: 'workspace', workspaceId: 'W1', connectionRevision: 1, environment: { kind: 'connection', connectionId: 'server' } })
      expect(readFileSync(reference.path, 'utf8')).toBe('report')
      expect(readFileSync(repo.getAttachmentRow(attachment.id)!.path, 'utf8')).toBe('report')
      await expect(completeWorkspaceUpload(intent.ticket, ctx)).rejects.toThrow('approval-expired')
      expect(lease.release).toHaveBeenCalledTimes(2)
    } finally { sender.emit('destroyed'); rmSync(remote, { recursive: true, force: true }) }
  })

  it('stages a selected client document without exposing a local file reference for remote upload', async () => {
    const source = join(userDataDir, 'report.pdf')
    writeFileSync(source, 'REPORT')
    vi.mocked(dialog.showOpenDialog).mockResolvedValueOnce({ canceled: false, filePaths: [source] })
    const picked = await pickAttachments({ scope: 'session', ownerId: 'S1', stageFiles: true })
    expect(picked).toHaveLength(1)
    expect(picked[0]).toMatchObject({ kind: 'attachment', attachment: { displayName: 'report.pdf', size: 6 } })
    expect(JSON.stringify(picked)).not.toContain(source)
    expect(readFileSync(source, 'utf8')).toBe('REPORT')
  })

  it('落盘到 sessions/<id>/ 并返回 ncw:// URL,不泄漏绝对路径', () => {
    const a = uploadAttachment({
      scope: 'session',
      ownerId: 'S1',
      displayName: '截图.png',
      mime: 'image/png',
      bytes: bytesOf('PNGDATA')
    })

    expect(a.url).toMatch(/^ncw:\/\/attachments\/sessions\/S1\/[0-9A-Z]+\.png$/)
    expect(JSON.stringify(a)).not.toContain(userDataDir)
    expect(a.displayName).toBe('截图.png')
    expect(a.size).toBe(7)

    const dir = join(attachmentRoot(), 'sessions', 'S1')
    const files = readdirSync(dir)
    expect(files).toHaveLength(1)
    expect(readFileSync(join(dir, files[0] as string), 'utf8')).toBe('PNGDATA')
  })

  it.each(['upload', 'picker'] as const)('keeps preview and all upstream protocols on the same bytes in a new session via %s', async (entry) => {
    const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', 'base64')
    const sessionId = 'brand-new-image-session'
    expect(repo.getSession(sessionId)).toBeUndefined()
    if (entry === 'picker') {
      const source = join(userDataDir, 'photo.JPG')
      writeFileSync(source, png)
      vi.mocked(dialog.showOpenDialog).mockResolvedValueOnce({ canceled: false, filePaths: [source] })
      const picked = await pickAttachments({ scope: 'session', ownerId: sessionId })
      expect(picked).toMatchObject([{ kind: 'attachment', attachment: { mime: 'image/png' } }])
    } else {
      uploadAttachment({ scope: 'session', ownerId: sessionId, displayName: 'photo.jpg', mime: 'image/jpg', bytes: new Uint8Array(png) })
    }
    const [attachment] = listSessionAttachments({ sessionId })
    expect(attachment?.mime).toBe('image/png')
    const preview = await handleAttachmentRequest(new Request(attachment!.url), attachmentRoot())
    expect(preview.status).toBe(200)
    expect(preview.headers.get('content-type')).toBe('image/png')
    expect(Buffer.from(await preview.arrayBuffer())).toEqual(png)
    const host = nodeHost({ paths: {
      userData: () => join(userDataDir, 'config-profiles', 'account'),
      attachments: attachmentRoot, temp: () => userDataDir
    } })
    const request = { ...REQUEST, messages: [userMessage('u', [{ type: 'image', mime: attachment!.mime, dataRef: attachment!.url }], 0)] }
    const before = structuredClone(request)
    const prepared = await prepareRequestImages(request, host, { workspaceId: 'workspace', sessionId }, new AbortController().signal)
    for (const protocol of ['anthropic', 'openai-chat', 'openai-responses'] as const) {
      const encoded = encodeUpstream(protocol, prepared, 'vision-model', 'test-key', { userId: 'workspace', cacheTtl: '5m' })
      const body = JSON.stringify(encoded.body)
      expect(body).toContain(png.toString('base64'))
      // 地址只以图后那行文本出现(让 Agent 能点名这张图);图像块本身必须是字节
      const note = imageUrlNote(attachment!.url)
      expect(body).toContain(note)
      expect(body.split(note).join('')).not.toContain('ncw://')
    }
    expect(request).toEqual(before)
  })

  /*
    需求:卡片里的图走 `?preview=thumb` 拿缩略档,原图校验一步不少;缩不了就回原图。
  */
  it('serves the generated thumbnail for ?preview=thumb and the original otherwise', async () => {
    const png = new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 10, 0, 0, 0, 5, 160, 0, 0])
    const uploaded = uploadAttachment({ scope: 'session', ownerId: 'S1', displayName: 'shot.png', mime: 'image/png', bytes: png })
    const thumbDir = join(userDataDir, 'thumbs')
    const render = vi.fn(() => new Uint8Array([7, 7, 7]))
    const thumbs = new ThumbnailCache(() => thumbDir, { render })

    const thumb = await handleAttachmentRequest(new Request(`${uploaded.url}?preview=thumb`), attachmentRoot(), thumbs)
    expect(thumb.status).toBe(200)
    expect(thumb.headers.get('content-type')).toBe('image/png')
    expect([...new Uint8Array(await thumb.arrayBuffer())]).toEqual([7, 7, 7])
    expect(render).toHaveBeenCalledTimes(1)

    const original = await handleAttachmentRequest(new Request(uploaded.url), attachmentRoot(), thumbs)
    expect(new Uint8Array(await original.arrayBuffer())).toEqual(png)

    // 解码器说缩不了(另一个空缓存目录):回原图
    const failing = new ThumbnailCache(() => join(userDataDir, 'thumbs-empty'), { render: () => null })
    const fallback = await handleAttachmentRequest(new Request(`${uploaded.url}?preview=thumb`), attachmentRoot(), failing)
    expect(new Uint8Array(await fallback.arrayBuffer())).toEqual(png)
  })

  it('rejects unknown preview values and still refuses escaping paths with a preview', async () => {
    const thumbs = new ThumbnailCache(() => join(userDataDir, 'thumbs'), { render: vi.fn(() => new Uint8Array([1])) })
    const uploaded = uploadAttachment({ scope: 'session', ownerId: 'S1', displayName: 'shot.png', mime: 'image/png', bytes: new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10, 0]) })

    expect((await handleAttachmentRequest(new Request(`${uploaded.url}?preview=full`), attachmentRoot(), thumbs)).status).toBe(400)
    expect((await handleAttachmentRequest(new Request(`${uploaded.url}?preview=thumb&preview=thumb`), attachmentRoot(), thumbs)).status).toBe(400)
    expect((await handleAttachmentRequest(new Request('ncw://attachments/sessions/%2e%2e/%2e%2e/x.png?preview=thumb'), attachmentRoot(), thumbs)).status).toBe(403)
    expect((await handleAttachmentRequest(new Request('ncw://attachments/sessions/S1/MISSING.png?preview=thumb'), attachmentRoot(), thumbs)).status).toBe(404)
  })

  it('uses the image bytes rather than a misleading MIME label for storage and restored metadata', () => {
    const png = new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10, 0])
    const uploaded = uploadAttachment({ scope: 'session', ownerId: 'S1', displayName: 'photo.jpg', mime: 'image/jpg', bytes: png })
    expect(uploaded.mime).toBe('image/png')
    expect(uploaded.url).toMatch(/\.png$/)
    expect(listSessionAttachments({ sessionId: 'S1' })[0]?.mime).toBe(uploaded.mime)
    expect(uploadAttachment({ scope: 'session', ownerId: 'S1', displayName: 'photo.png', mime: 'image/png', bytes: png }).id).toBe(uploaded.id)
  })

  it('does not reuse a legacy attachment with an incompatible stored MIME type', () => {
    const bytes = bytesOf('legacy image')
    const old = uploadAttachment({ scope: 'session', ownerId: 'S1', displayName: 'old', mime: 'application/x-unknown', bytes })
    const uploaded = uploadAttachment({ scope: 'session', ownerId: 'S1', displayName: 'photo.jpg', mime: 'image/jpg', bytes })
    expect(uploaded.id).not.toBe(old.id)
    expect(uploaded.mime).toBe('image/jpeg')
    expect(uploaded.url).toMatch(/\.jpg$/)
    const repeated = uploadAttachment({ scope: 'session', ownerId: 'S1', displayName: 'photo.jpg', mime: 'image/jpeg', bytes })
    expect(repeated.id).toBe(uploaded.id)
  })

  it('reuses the repaired copy even when the system clock moves backwards', () => {
    const now = vi.spyOn(Date, 'now').mockReturnValue(2000)
    const bytes = bytesOf('clock rollback image')
    try {
      uploadAttachment({ scope: 'session', ownerId: 'S1', displayName: 'old', mime: 'application/x-unknown', bytes })
      now.mockReturnValue(1000)
      const repaired = uploadAttachment({ scope: 'session', ownerId: 'S1', displayName: 'photo.jpg', mime: 'image/jpg', bytes })
      const repeated = uploadAttachment({ scope: 'session', ownerId: 'S1', displayName: 'photo.jpg', mime: 'image/jpeg', bytes })
      expect(repeated.id).toBe(repaired.id)
    } finally { now.mockRestore() }
  })

  it('★ 同内容重复上传去重:只占一份磁盘,返回同一个 id', () => {
    const a = uploadAttachment({
      scope: 'session', ownerId: 'S1', displayName: 'a.png', mime: 'image/png', bytes: bytesOf('SAME')
    })
    const b = uploadAttachment({
      scope: 'session', ownerId: 'S1', displayName: 'b.png', mime: 'image/png', bytes: bytesOf('SAME')
    })

    expect(b.id).toBe(a.id)
    expect(readdirSync(join(attachmentRoot(), 'sessions', 'S1'))).toHaveLength(1)
    // displayName 各记各的 —— 去重的是字节,不是用户对它的称呼
    expect(b.displayName).toBe('b.png')
  })

  it('★ 去重不跨 owner:换个会话就该有自己的一份,否则删会话会波及别人', () => {
    uploadAttachment({
      scope: 'session', ownerId: 'S1', displayName: 'a.png', mime: 'image/png', bytes: bytesOf('SAME')
    })
    const b = uploadAttachment({
      scope: 'session', ownerId: 'S2', displayName: 'a.png', mime: 'image/png', bytes: bytesOf('SAME')
    })

    expect(readdirSync(join(attachmentRoot(), 'sessions', 'S1'))).toHaveLength(1)
    expect(readdirSync(join(attachmentRoot(), 'sessions', 'S2'))).toHaveLength(1)
    expect(b.ownerId).toBe('S2')
  })

  it('★ 去重命中但文件已被外部删除时,重新落盘而不是返回一条死记录', () => {
    const a = uploadAttachment({
      scope: 'session', ownerId: 'S1', displayName: 'a.png', mime: 'image/png', bytes: bytesOf('SAME')
    })
    const dir = join(attachmentRoot(), 'sessions', 'S1')
    rmSync(join(dir, readdirSync(dir)[0] as string))

    const b = uploadAttachment({
      scope: 'session', ownerId: 'S1', displayName: 'a.png', mime: 'image/png', bytes: bytesOf('SAME')
    })
    expect(b.id).not.toBe(a.id)
    expect(readdirSync(dir)).toHaveLength(1)
  })

  it('theme scope 落在 themes/ 下且不带 owner 目录', () => {
    const a = uploadAttachment({
      scope: 'theme', displayName: 't.png', mime: 'image/png', bytes: bytesOf('T')
    })
    expect(a.url).toMatch(/^ncw:\/\/attachments\/themes\/[0-9A-Z]+\.png$/)
    expect(existsSync(join(attachmentRoot(), 'themes'))).toBe(true)
  })

  it('超限与空文件被拒', () => {
    expect(() =>
      uploadAttachment({
        scope: 'session', ownerId: 'S1', displayName: 'e', mime: 'image/png',
        bytes: new Uint8Array(0) as Uint8Array<ArrayBuffer>
      })
    ).toThrow()

    expect(() =>
      uploadAttachment({
        scope: 'session', ownerId: 'S1', displayName: 'big', mime: 'application/zip',
        bytes: new Uint8Array(33 * 1024 * 1024) as Uint8Array<ArrayBuffer>
      })
    ).toThrow(/upload limit/)
  })

  it('★ 落盘后不留 .tmp 残片 —— 原子替换的可观测证据', () => {
    uploadAttachment({
      scope: 'session', ownerId: 'S1', displayName: 'a.png', mime: 'image/png', bytes: bytesOf('X')
    })
    const files = readdirSync(join(attachmentRoot(), 'sessions', 'S1'))
    expect(files.some((f) => f.endsWith('.tmp'))).toBe(false)
    expect(files.some((f) => f.startsWith('.'))).toBe(false)
  })

  it('★ 登记为 draft 而不是 committed —— 文件在磁盘上但还没进任何消息', () => {
    const a = uploadAttachment({
      scope: 'session', ownerId: 'S1', displayName: 'a.png', mime: 'image/png', bytes: bytesOf('X')
    })
    expect(repo.getAttachmentRow(a.id)?.status).toBe('draft')
  })

  it('未知 mime 落到 .bin,不产生无扩展名文件', () => {
    const a = uploadAttachment({
      scope: 'session', ownerId: 'S1', displayName: 'x', mime: 'application/x-weird', bytes: bytesOf('X')
    })
    expect(a.url.endsWith('.bin')).toBe(true)
  })
})

describe('listSessionAttachments', () => {
  it('只列出该会话的草稿附件', () => {
    uploadAttachment({ scope: 'session', ownerId: 'S1', displayName: 'a', mime: 'image/png', bytes: bytesOf('A') })
    uploadAttachment({ scope: 'session', ownerId: 'S1', displayName: 'b', mime: 'image/png', bytes: bytesOf('B') })
    uploadAttachment({ scope: 'session', ownerId: 'S2', displayName: 'c', mime: 'image/png', bytes: bytesOf('C') })

    expect(listSessionAttachments({ sessionId: 'S1' })).toHaveLength(2)
    expect(listSessionAttachments({ sessionId: 'S2' })).toHaveLength(1)
  })

  it('已提交的不再算草稿', () => {
    /*
      ★ 先建**会话行与消息行**:`commitAttachmentsByPath` 要写 session_id 与
      message_id,两列上各有一条外键。真实路径里它由 `commitMessage` 在插完
      消息之后调用 —— 测试要如实模拟这个前提,而不是绕开它。

      这两条外键正是「上传时不能写 session_id」的同一个约束在另一端的体现:
      附件的登记必须晚于它所属的消息,而上传必须早于消息。draft/committed
      两态存在的理由就在这个时间差里。
    */
    repo.ensureSession({ id: 'S1', workspaceId: 'W1', rootPathAtCreation: '/tmp' })
    repo.commitMessage('S1', {
      id: 'M1',
      role: 'user',
      parts: [{ type: 'text', text: '带图的消息' }],
      createdAt: Date.now(),
      schemaVersion: 1
    })

    const a = uploadAttachment({
      scope: 'session', ownerId: 'S1', displayName: 'a', mime: 'image/png', bytes: bytesOf('A')
    })
    const row = repo.getAttachmentRow(a.id)
    repo.commitAttachmentsByPath([row?.path as string], 'M1', 'S1')
    expect(listSessionAttachments({ sessionId: 'S1' })).toHaveLength(0)
    expect(repo.getAttachmentRow(a.id)?.status).toBe('committed')
  })

  /*
    需求:生成图落成本会话 draft 附件,地址只在 tool_result 的 output.images 里。
    不随消息转 committed 的话,7 天后被当过期草稿回收,转录里那张图从此是裂图。
  */
  it('tool_result 里的本会话 ncw 图随消息一起转 committed,内联图与别的会话的图不受影响', () => {
    repo.ensureSession({ id: 'S1', workspaceId: 'W1', rootPathAtCreation: '/tmp' })
    const mine = uploadAttachment({ scope: 'session', ownerId: 'S1', displayName: 'generated.png', mime: 'image/png', bytes: bytesOf('G') })
    const other = uploadAttachment({ scope: 'session', ownerId: 'S2', displayName: 'generated.png', mime: 'image/png', bytes: bytesOf('H') })
    const message = toolResultMessage('M2', [{
      type: 'tool_result', callId: 'c1', isError: false,
      output: { content: 'Generated', images: [
        { mime: 'image/png', dataRef: mine.url },
        { mime: 'image/png', dataRef: 'data:image/png;base64,AAAA' },
        { mime: 'image/png', dataRef: other.url }
      ] }
    }], Date.now())
    repo.commitMessage('S1', message)
    // 重放同一条消息(编辑重跑 / 恢复)必须幂等
    repo.commitMessage('S1', message)
    expect(repo.getAttachmentRow(mine.id)?.status).toBe('committed')
    expect(repo.getAttachmentRow(other.id)?.status).toBe('draft')
    expect(listSessionAttachments({ sessionId: 'S1' })).toHaveLength(0)
  })
})

describe('removeAttachment', () => {
  it('文件与记录一起消失', () => {
    const a = uploadAttachment({
      scope: 'session', ownerId: 'S1', displayName: 'a', mime: 'image/png', bytes: bytesOf('A')
    })
    const path = repo.getAttachmentRow(a.id)?.path as string
    expect(existsSync(path)).toBe(true)

    removeAttachment({ id: a.id })
    expect(existsSync(path)).toBe(false)
    expect(repo.getAttachmentRow(a.id)).toBeUndefined()
  })

  it('删不存在的 id 不抛', () => {
    expect(() => { removeAttachment({ id: 'nope' }) }).not.toThrow()
  })

  /*
    ★ 这一组钉的是「列得出来却删不掉」:草稿行的 `session_id` 要等消息提交才填,
    而归属判定一度对空 `session_id` 一律按 `local` 处理 —— 登录账户之后
    `getAttachmentRow` 拿不到行,`removeAttachment` 静默 return,而恢复用的
    `listDraftAttachments` 按 owner 判定照样列得出来。用户看到的是:
    删掉输入框里的图,切一下会话它又回来了,全程零报错。
  */
  describe('登录账户之后', () => {
    /** 账户作用域下的一个工作区 + 一条会话,附件挂在它下面 */
    function accountSession(accountId: string, workspaceId: string, sessionId: string): void {
      switchConfigProfile(accountId)
      repo.putWorkspace({
        id: workspaceId, name: workspaceId, rootPath: userDataDir, environment: { kind: 'local' },
        settings: DEFAULT_WORKSPACE_SETTINGS, createdAt: 1, lastOpenedAt: 1
      })
      repo.ensureSession({ id: sessionId, workspaceId, rootPathAtCreation: userDataDir })
    }

    it('删掉的草稿附件不会在下次列草稿时回来', () => {
      accountSession('acct-1', 'W1', 'S1')
      const a = uploadAttachment({
        scope: 'session', ownerId: 'S1', displayName: 'a.png', mime: 'image/png', bytes: bytesOf('A')
      })
      const path = repo.getAttachmentRow(a.id)?.path as string

      removeAttachment({ id: a.id })

      expect(listSessionAttachments({ sessionId: 'S1' })).toHaveLength(0)
      expect(repo.getAttachmentRow(a.id)).toBeUndefined()
      expect(existsSync(path)).toBe(false)
    })

    it('别的账户的草稿附件仍然既列不出也删不掉', () => {
      accountSession('acct-1', 'W1', 'S1')
      const a = uploadAttachment({
        scope: 'session', ownerId: 'S1', displayName: 'a.png', mime: 'image/png', bytes: bytesOf('A')
      })
      const path = repo.getAttachmentRow(a.id)?.path as string

      switchConfigProfile('acct-2')
      expect(listSessionAttachments({ sessionId: 'S1' })).toEqual([])
      removeAttachment({ id: a.id })
      // 跨账户是「查不到」而不是「删掉了」—— 文件与记录都必须原样留给 acct-1
      expect(existsSync(path)).toBe(true)
      switchConfigProfile('acct-1')
      expect(listSessionAttachments({ sessionId: 'S1' })).toHaveLength(1)
    })
  })
})

describe('显示名(迁移 6)', () => {
  /**
   * ★ 磁盘文件名是 ULID,所以「用户看到的名字」必须单独存一列。
   * 不存的表现很具体:传图 → 关应用 → 重开,chip 从「季度报表.png」
   * 变成「01J8XQZ4M7.png」—— 而 ULID 恰恰是为了不让人认路径才选的。
   */
  it('重启后列回来的仍是原始名,不是 ULID 文件名', () => {
    uploadAttachment({
      scope: 'session', ownerId: 'S1', displayName: '季度报表.png',
      mime: 'image/png', bytes: bytesOf('A')
    })

    const restored = listSessionAttachments({ sessionId: 'S1' })
    expect(restored[0]?.displayName).toBe('季度报表.png')
    // 磁盘上仍是 ULID —— 两者是分开的
    expect(restored[0]?.url).toMatch(/\/[0-9A-Z]{26}\.png$/)
  })

  it('★ 去重命中时显示名取本次的 —— 去重的是字节,不是用户对它的称呼', () => {
    uploadAttachment({
      scope: 'session', ownerId: 'S1', displayName: '旧名.png',
      mime: 'image/png', bytes: bytesOf('SAME')
    })
    const b = uploadAttachment({
      scope: 'session', ownerId: 'S1', displayName: '新名.png',
      mime: 'image/png', bytes: bytesOf('SAME')
    })
    expect(b.displayName).toBe('新名.png')
  })
})

describe('会话行不存在时也能上传', () => {  /**
   * ★ 这条用例钉的是一个真实缺陷:`attachments.session_id` 上有指向 `sessions`
   * 的外键,而上传发生在**发送之前** —— 新建对话还没发第一条消息时,
   * `sessions` 表里没有那一行。早先的实现往 `session_id` 里写 ownerId,
   * 于是每一次「新对话里先传图」都直接 FOREIGN KEY constraint failed。
   *
   * 现在 draft 只写无外键的 `owner_id`,`session_id` 留到消息提交那一刻再填。
   */
  it('全新 sessionId(sessions 表里没有这一行)不触发外键失败', () => {
    expect(() =>
      uploadAttachment({
        scope: 'session',
        ownerId: 'BRAND-NEW-SESSION',
        displayName: 'a.png',
        mime: 'image/png',
        bytes: bytesOf('A')
      })
    ).not.toThrow()

    expect(listSessionAttachments({ sessionId: 'BRAND-NEW-SESSION' })).toHaveLength(1)
  })
})

/**
 * ★ 这一组锁的是**两个入口的行为等价**,不是 dialog 本身。
 *
 * 菜单「添加附件」与拖拽/粘贴必须给出同形的托盘项:图片落盘、非图片只带路径。
 * 曾经不是这样 —— 菜单一律落盘,而非图片落盘后在 `partsOf` 那边退化成一句
 * 「[附件] 名字」,于是同一个 PDF 从菜单进来模型读不到,从拖拽进来能读到。
 */
describe('pickAttachments —— 与拖拽/粘贴同一套分流', () => {
  const showOpenDialog = vi.mocked(dialog.showOpenDialog)

  /** 在临时目录里造一个真文件,返回它的绝对路径 */
  function fixture(name: string, content: string): string {
    const path = join(userDataDir, name)
    writeFileSync(path, content)
    return path
  }

  beforeEach(() => {
    showOpenDialog.mockReset()
  })

  it('★ 非图片不落盘,只回传用户选中的真实路径', async () => {
    const path = fixture('说明.pdf', 'PDFDATA')
    showOpenDialog.mockResolvedValue({ canceled: false, filePaths: [path] })

    const out = await pickAttachments({ scope: 'session', ownerId: 'S1' })

    expect(out).toEqual([{ kind: 'path', path, name: '说明.pdf' }])
    // 一个字节都不该被复制到附件根下
    expect(existsSync(join(attachmentRoot(), 'sessions', 'S1'))).toBe(false)
    expect(repo.listDraftAttachments('S1')).toHaveLength(0)
  })

  it.each(['bmp', 'avif', 'heic', 'heif', 'tif', 'tiff'])('preserves the existing file-reference path for %s selections', async (extension) => {
    const path = fixture(`source.${extension}`, 'SOURCE')
    showOpenDialog.mockResolvedValue({ canceled: false, filePaths: [path] })
    expect(await pickAttachments({ scope: 'session', ownerId: 'S1' })).toEqual([
      { kind: 'path', path, name: `source.${extension}` }
    ])
    expect(repo.listDraftAttachments('S1')).toEqual([])
  })

  it('图片仍走落盘 —— 内联展示要有 ncw:// 地址', async () => {
    const path = fixture('截图.png', 'PNGDATA')
    showOpenDialog.mockResolvedValue({ canceled: false, filePaths: [path] })

    const out = await pickAttachments({ scope: 'session', ownerId: 'S1' })

    expect(out).toHaveLength(1)
    expect(out[0]?.kind).toBe('attachment')
    const a = out[0]?.kind === 'attachment' ? out[0].attachment : undefined
    expect(a?.url).toMatch(/^ncw:\/\/attachments\/sessions\/S1\/[0-9A-Z]+\.png$/)
    expect(a?.displayName).toBe('截图.png')
    // ★ 绝对路径不出主进程,这一支的约束没有被放松
    expect(JSON.stringify(a)).not.toContain(userDataDir)
  })

  it('reports an unsupported image at selection time without dropping other selections', async () => {
    const svg = fixture('drawing.svg', '<svg xmlns="http://www.w3.org/2000/svg"></svg>')
    const document = fixture('report.pdf', 'PDF')
    showOpenDialog.mockResolvedValue({ canceled: false, filePaths: [svg, document] })
    expect(await pickAttachments({ scope: 'session', ownerId: 'S1' })).toMatchObject([
      { kind: 'error', name: 'drawing.svg', error: { messageKey: 'attachment.error.unsupportedImage' } },
      { kind: 'path', path: document, name: 'report.pdf' }
    ])
  })

  it('returns large non-image paths and reports oversized images instead of dropping them', async () => {
    // 稀疏文件:声明 33MB 但不占磁盘,跑得比写 33MB 快得多
    const big = fixture('巨大.log', '')
    truncateSync(big, MAX_ATTACHMENT_BYTES + 1)
    const bigPng = fixture('巨图.png', '')
    truncateSync(bigPng, MAX_ATTACHMENT_BYTES + 1)
    showOpenDialog.mockResolvedValue({ canceled: false, filePaths: [big, bigPng] })

    const out = await pickAttachments({ scope: 'session', ownerId: 'S1' })

    // 上限存在的理由是结构化克隆的卡顿,而路径这一支根本不读字节 ——
    // 拖一个 200MB 的日志进来是可以的,菜单选同一个文件没有理由被静默丢掉。
    expect(out).toMatchObject([
      { kind: 'path', path: big, name: '巨大.log' },
      { kind: 'error', name: '巨图.png', error: { messageKey: 'attachment.error.tooLarge', messageParams: { limit: 32 } } }
    ])
  })

  it('单个文件失败不拖垮整批', async () => {
    const ok = fixture('好.pdf', 'A')
    showOpenDialog.mockResolvedValue({
      canceled: false,
      filePaths: [join(userDataDir, '不存在.pdf'), ok]
    })

    const out = await pickAttachments({ scope: 'session', ownerId: 'S1' })
    expect(out).toMatchObject([
      { kind: 'error', name: '不存在.pdf', error: { messageKey: 'attachment.error.readFailed', messageParams: { code: 'ENOENT' } } },
      { kind: 'path', path: ok, name: '好.pdf' }
    ])
  })

  it('目录被跳过 —— 不递归展开', async () => {
    showOpenDialog.mockResolvedValue({ canceled: false, filePaths: [userDataDir] })
    expect(await pickAttachments({ scope: 'session', ownerId: 'S1' })).toEqual([])
  })

  it('取消返回空数组', async () => {
    showOpenDialog.mockResolvedValue({ canceled: true, filePaths: [] })
    expect(await pickAttachments({ scope: 'session' })).toEqual([])
  })
})
