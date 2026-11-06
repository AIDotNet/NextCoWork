import { mkdir, mkdtemp, realpath, rm, symlink, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { toolResultMessage, userMessage } from '../../../../shared/agent/message'
import { MAX_ATTACHMENT_BYTES } from '../../../../shared/domain/attachment'
import { nodeHost, type KernelHost } from '../../host'
import { encodeUpstream } from '../codec'
import { ImageInputError, imageUrlNote, prepareRequestImages } from '../images'
import { REQUEST } from './openai-fixtures'

let root: string
let host: KernelHost
const signal = new AbortController().signal
const context = { workspaceId: 'workspace', sessionId: 'session-a' }
const imageBytes = Buffer.from('managed image bytes')
const imageUrl = 'ncw://attachments/sessions/session-a/image.png'

function request(dataRef = imageUrl, mime = 'image/png'): typeof REQUEST {
  return { ...REQUEST, messages: [userMessage('u', [
    { type: 'text', text: 'Describe the image.' }, { type: 'image', mime, dataRef }
  ], 0)] }
}

beforeEach(async () => {
  root = await realpath(await mkdtemp(join(tmpdir(), 'nextcowork-upstream-images-')))
  await mkdir(join(root, 'attachments', 'sessions', 'session-a'), { recursive: true })
  host = nodeHost({ paths: { userData: () => root, attachments: () => join(root, 'attachments'), temp: () => root } })
})

afterEach(async () => {
  vi.restoreAllMocks()
  await rm(root, { recursive: true, force: true })
})

describe('upstream managed images', () => {
  it('resolves only the outgoing copy, caches repeated references, and feeds all three protocols', async () => {
    await writeFile(join(root, 'attachments', 'sessions', 'session-a', 'image.png'), imageBytes)
    const original = request()
    original.messages.push(userMessage('u2', [{ type: 'image', mime: 'image/png', dataRef: imageUrl }], 0))
    const before = structuredClone(original)
    const read = vi.spyOn(host.fs, 'readFileBytes')
    const prepared = await prepareRequestImages(original, host, context, signal)
    const dataUrl = `data:image/png;base64,${imageBytes.toString('base64')}`
    expect(prepared.messages[0]?.parts[1]).toMatchObject({ dataRef: dataUrl })
    expect(original).toEqual(before)
    expect(read).toHaveBeenCalledTimes(1)
    for (const protocol of ['anthropic', 'openai-chat', 'openai-responses'] as const) {
      const body = JSON.stringify(encodeUpstream(protocol, prepared, 'model', 'test-key', { userId: context.workspaceId, cacheTtl: '5m' }).body)
      expect(body).toContain(imageBytes.toString('base64'))
      // 地址只能以那行文本出现;图像块本身必须是字节,不能是 ncw://(上游读不到)
      expect(body).toContain(imageUrlNote(imageUrl))
      expect(body.split(imageUrlNote(imageUrl)).join('')).not.toContain('ncw://')
    }
  })

  it('annotates each managed user image with its URL right after it, and leaves the transcript untouched', async () => {
    await writeFile(join(root, 'attachments', 'sessions', 'session-a', 'image.png'), imageBytes)
    const original = request()
    const prepared = await prepareRequestImages(original, host, context, signal)
    expect(prepared.messages[0]?.parts.map((p) => p.type)).toEqual(['text', 'image', 'text'])
    expect(prepared.messages[0]?.parts[2]).toEqual({ type: 'text', text: '[Image URL: ncw://attachments/sessions/session-a/image.png]' })
    expect(original.messages[0]?.parts).toHaveLength(2)
  })

  it('does not annotate inline data URL images, which have no address to give', async () => {
    const prepared = await prepareRequestImages(request(`data:image/png;base64,${imageBytes.toString('base64')}`), host, context, signal)
    expect(prepared.messages[0]?.parts.map((p) => p.type)).toEqual(['text', 'image'])
  })

  it('resolves ncw:// tool-result images to bytes and passes inline ones through', async () => {
    await writeFile(join(root, 'attachments', 'sessions', 'session-a', 'image.png'), imageBytes)
    const inline = `data:image/png;base64,${Buffer.from('inline').toString('base64')}`
    const original = { ...REQUEST, messages: [toolResultMessage('t', [{
      type: 'tool_result', callId: 'c1', isError: false,
      output: { content: 'Generated 2 images.', images: [{ mime: 'image/png', dataRef: imageUrl }, { mime: 'image/png', dataRef: inline }] }
    }], 0)] }
    const before = structuredClone(original)
    const prepared = await prepareRequestImages(original, host, context, signal)
    const part = prepared.messages[0]?.parts[0]
    expect(part?.type === 'tool_result' ? part.output.images : undefined).toEqual([
      { mime: 'image/png', dataRef: `data:image/png;base64,${imageBytes.toString('base64')}` },
      { mime: 'image/png', dataRef: inline }
    ])
    expect(original).toEqual(before)
  })

  it('rejects a tool-result ncw:// image owned by another session', async () => {
    const original = { ...REQUEST, messages: [toolResultMessage('t', [{
      type: 'tool_result', callId: 'c1', isError: false,
      output: { content: 'x', images: [{ mime: 'image/png', dataRef: 'ncw://attachments/sessions/session-b/image.png' }] }
    }], 0)] }
    await expect(prepareRequestImages(original, host, context, signal)).rejects.toMatchObject({
      error: { messageKey: 'attachment.error.foreignSession' }
    })
  })

  it('reads uploaded images from the attachment root, not the active account profile', async () => {
    await writeFile(join(root, 'attachments', 'sessions', 'session-a', 'image.png'), imageBytes)
    host.paths.userData = () => join(root, 'config-profiles', 'account-a')
    const prepared = await prepareRequestImages(request(), host, context, signal)
    expect(prepared.messages[0]?.parts[1]).toMatchObject({ dataRef: `data:image/png;base64,${imageBytes.toString('base64')}` })
  })

  it.each(['image/jpg', 'image/pjpeg', 'IMAGE/JPEG'])('normalizes the image MIME alias %s before encoding', async (mime) => {
    await writeFile(join(root, 'attachments', 'sessions', 'session-a', 'image.png'), imageBytes)
    const prepared = await prepareRequestImages(request(imageUrl, mime), host, context, signal)
    expect(prepared.messages[0]?.parts[1]).toMatchObject({ mime: 'image/jpeg', dataRef: `data:image/jpeg;base64,${imageBytes.toString('base64')}` })
  })

  it('preserves the filesystem error code instead of hiding every read failure', async () => {
    await writeFile(join(root, 'attachments', 'sessions', 'session-a', 'image.png'), imageBytes)
    vi.spyOn(host.fs, 'readFileBytes').mockRejectedValue(Object.assign(new Error('denied'), { code: 'EACCES' }))
    await expect(prepareRequestImages(request(), host, context, signal)).rejects.toMatchObject({
      error: { messageKey: 'attachment.error.unreadable', messageParams: { name: 'image.png', code: 'EACCES' } }
    })
  })

  it('accepts matching inline images without reading the filesystem', async () => {
    const read = vi.spyOn(host.fs, 'realpath')
    const original = request(`data:image/png;base64,${imageBytes.toString('base64')}`)
    expect(await prepareRequestImages(original, host, context, signal)).toEqual(original)
    expect(read).not.toHaveBeenCalled()
  })

  it.each([
    'ncw://attachments/sessions/session-b/image.png',
    'ncw://attachments/themes/image.png',
    'ncw://attachments/sessions/session-a/%2e%2e%2fimage.png',
    'https://example.com/image.png',
    '/outside/image.png'
  ])('rejects unowned or unmanaged references before reading: %s', async (dataRef) => {
    const read = vi.spyOn(host.fs, 'readFileBytes')
    await expect(prepareRequestImages(request(dataRef), host, context, signal)).rejects.toBeInstanceOf(ImageInputError)
    expect(read).not.toHaveBeenCalled()
  })

  it('reports missing attachments as a localized non-retryable input error', async () => {
    await expect(prepareRequestImages(request(), host, context, signal)).rejects.toMatchObject({
      error: { code: 'provider', retryable: false, messageKey: 'attachment.error.missing', messageParams: { name: 'image.png', code: 'ENOENT' } }
    })
  })

  it('rejects a file symlink escaping the owning session', async () => {
    await writeFile(join(root, 'outside.png'), imageBytes)
    await symlink(join(root, 'outside.png'), join(root, 'attachments', 'sessions', 'session-a', 'image.png'))
    const read = vi.spyOn(host.fs, 'readFileBytes')
    await expect(prepareRequestImages(request(), host, context, signal)).rejects.toThrow('escaped')
    expect(read).not.toHaveBeenCalled()
  })

  it('rejects a session directory symlink even when it points inside the attachment root', async () => {
    await writeFile(join(root, 'attachments', 'sessions', 'session-a', 'image.png'), imageBytes)
    await symlink(join(root, 'attachments', 'sessions', 'session-a'), join(root, 'attachments', 'sessions', 'session-link'), 'dir')
    await expect(prepareRequestImages(request('ncw://attachments/sessions/session-link/image.png'), host,
      { ...context, sessionId: 'session-link' }, signal)).rejects.toThrow('escaped')
  })

  it.each([0, MAX_ATTACHMENT_BYTES + 1])('rejects invalid attachment size %s before loading bytes', async (size) => {
    await writeFile(join(root, 'attachments', 'sessions', 'session-a', 'image.png'), imageBytes)
    vi.spyOn(host.fs, 'stat').mockResolvedValue({ size, isDir: false, mtimeMs: 0 })
    const read = vi.spyOn(host.fs, 'readFileBytes')
    await expect(prepareRequestImages(request(), host, context, signal)).rejects.toThrow('size')
    expect(read).not.toHaveBeenCalled()
  })

  it('rejects unsupported or mismatched media types', async () => {
    await expect(prepareRequestImages(request(imageUrl, 'image/svg+xml'), host, context, signal)).rejects.toThrow('media type')
    await expect(prepareRequestImages(request('data:image/jpeg;base64,aGk='), host, context, signal)).rejects.toThrow('data URL')
  })

  it('distinguishes unavailable storage from a missing image file', async () => {
    vi.spyOn(host.fs, 'realpath').mockRejectedValue(Object.assign(new Error('missing storage'), { code: 'ENOENT' }))
    await expect(prepareRequestImages(request(), host, context, signal)).rejects.toMatchObject({
      error: { messageKey: 'attachment.error.storageUnavailable', messageParams: { code: 'ENOENT' } }
    })
  })

  it('rejects a partial read instead of sending truncated image bytes', async () => {
    await writeFile(join(root, 'attachments', 'sessions', 'session-a', 'image.png'), imageBytes)
    const read = vi.spyOn(host.fs, 'readFileBytes').mockResolvedValue(imageBytes.subarray(0, 2))
    await expect(prepareRequestImages(request(), host, context, signal)).rejects.toMatchObject({
      error: { messageKey: 'attachment.error.incompleteRead' }
    })
    expect(read).toHaveBeenCalledWith(expect.any(String), imageBytes.length + 1)
  })

  it('corrects a supported but inaccurate MIME label in the outgoing copy only', async () => {
    const png = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10, 0])
    await writeFile(join(root, 'attachments', 'sessions', 'session-a', 'image.png'), png)
    const original = request(imageUrl, 'image/jpeg')
    const prepared = await prepareRequestImages(original, host, context, signal)
    expect(prepared.messages[0]?.parts[1]).toMatchObject({ mime: 'image/png', dataRef: `data:image/png;base64,${png.toString('base64')}` })
    expect(original.messages[0]?.parts[1]).toMatchObject({ mime: 'image/jpeg', dataRef: imageUrl })
  })

  it('normalizes matching inline MIME aliases without accessing local files', async () => {
    const read = vi.spyOn(host.fs, 'realpath')
    const data = imageBytes.toString('base64')
    const prepared = await prepareRequestImages(request(`data:image/jpg;base64,${data}`, 'image/pjpeg'), host, context, signal)
    expect(prepared.messages[0]?.parts[1]).toMatchObject({ mime: 'image/jpeg', dataRef: `data:image/jpeg;base64,${data}` })
    expect(read).not.toHaveBeenCalled()
  })

  it.each(['', '%invalid%', 'a', 'aGk==='])('rejects malformed inline image payload %s', async (data) => {
    await expect(prepareRequestImages(request(`data:image/png;base64,${data}`), host, context, signal)).rejects.toBeInstanceOf(ImageInputError)
  })

  it('honors cancellation before reading an attachment', async () => {
    const controller = new AbortController()
    controller.abort()
    const read = vi.spyOn(host.fs, 'realpath')
    await expect(prepareRequestImages(request(), host, context, controller.signal)).rejects.toMatchObject({ name: 'AbortError' })
    expect(read).not.toHaveBeenCalled()
  })
})
