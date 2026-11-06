import { isAbsolute, join, relative } from 'node:path'
import { agentError, type AgentError } from '../../../shared/agent/error'
import type { AgentMessage, ContentPart, ToolOutputImage } from '../../../shared/agent/message'
import { attachmentRelPath, imageMimeOfBytes, MAX_ATTACHMENT_BYTES, normalizeImageMime, parseNcwUrl } from '../../../shared/domain/attachment'
import type { KernelHost } from '../host'
import type { CanonicalRequest, UpstreamRequestContext } from './canonical'

type ImageInputFailure = 'unsupportedImage' | 'invalidImageData' | 'foreignSession' | 'invalidLocation'
  | 'unsafePath' | 'invalidSize' | 'storageUnavailable' | 'missing' | 'unreadable' | 'incompleteRead'

export class ImageInputError extends Error {
  readonly error: AgentError
  constructor(detail: string, reason: ImageInputFailure, messageParams?: AgentError['messageParams']) {
    super(detail)
    this.error = agentError('provider', detail, { retryable: false, messageKey: `attachment.error.${reason}`, messageParams })
  }
}

function within(root: string, target: string): boolean {
  const rel = relative(root, target)
  return rel !== '' && rel !== '..' && !rel.startsWith(`..${process.platform === 'win32' ? '\\' : '/'}`) && !isAbsolute(rel)
}

/**
 * 图后附的那行地址。需求:Agent 要能点名用户粘贴的图(交给 `generate_image` 改、交给
 * `SaveImage` 落盘),而图像块本身不带任何名字 —— 地址只能以文本的形式进上下文。
 * ★ 只在「要发出去的那份拷贝」里加,转录不动;内容只由 dataRef 决定,
 * 所以每轮逐字相同,不破 prompt cache。
 */
export function imageUrlNote(ref: string): string {
  return `[Image URL: ${ref}]`
}

function isManagedRef(ref: string): boolean {
  return parseNcwUrl(ref) !== null
}

/** Resolve only this session's managed images. Data URLs live in the outgoing copy, never the transcript. */
export async function prepareRequestImages(
  request: CanonicalRequest,
  host: KernelHost,
  context: UpstreamRequestContext,
  signal: AbortSignal
): Promise<CanonicalRequest> {
  if (!request.messages.some((m) => m.parts.some((p) => p.type === 'image'
    || (p.type === 'tool_result' && (p.output.images ?? []).some((image) => isManagedRef(image.dataRef)))))) return request
  const resolved = new Map<string, { mime: string; dataRef: string }>()
  const resolveCached = async (part: { mime: string; dataRef: string }, mime: string): Promise<{ mime: string; dataRef: string }> => {
    const cacheKey = `${mime}:${part.dataRef}`
    let image = resolved.get(cacheKey)
    if (image === undefined) {
      /*
        ★ 单张解析抽成了 `resolveImageDataRef`:对话内生图的改图
        (`kernel/image-gen.ts`)要复用**同一条**「dataRef → 可外发 data URL」
        的安全校验(会话归属、路径围栏、大小、魔数)。复制一份出去的表现是
        两处以后各改各的,而漏改的那一处就是一条能读到别的会话附件的路。
      */
      image = await resolveImageDataRef(part, host, context, signal)
      resolved.set(cacheKey, image)
    }
    return image
  }
  const messages: AgentMessage[] = []
  for (const message of request.messages) {
    const parts: ContentPart[] = []
    for (const part of message.parts) {
      signal.throwIfAborted()
      if (part.type === 'tool_result' && part.output.images !== undefined) {
        /*
          需求:生成图在转录里存的是 `ncw://` 地址(`kernel/session-images.ts`),
          而 Anthropic 编码器只认 `data:` 前缀、OpenAI 两家把 dataRef 原样当 image_url ——
          不在这里解析,模型就看不见自己刚画的图。内联 data URL(截图、旧转录)原样放行,
          和这段代码出现之前一样。地址不再另附:回执正文里已经逐张列出。
        */
        if (!part.output.images.some((image) => isManagedRef(image.dataRef))) { parts.push(part); continue }
        const images: ToolOutputImage[] = []
        for (const image of part.output.images) {
          if (!isManagedRef(image.dataRef)) { images.push(image); continue }
          const out = await resolveCached(image, image.mime)
          images.push({ mime: normalizeImageMime(out.mime) ?? image.mime, dataRef: out.dataRef })
        }
        parts.push({ ...part, output: { ...part.output, images } })
        continue
      }
      if (part.type !== 'image') { parts.push(part); continue }
      const mime = normalizeImageMime(part.mime)
      if (mime === null) {
        throw new ImageInputError(`Unsupported image media type: ${part.mime}`, 'unsupportedImage', { mime: part.mime })
      }
      const image = await resolveCached(part, mime)
      parts.push({ ...part, ...image })
      // 内联 data URL(旧转录)没有地址可给,只给托管图附
      if (isManagedRef(part.dataRef)) parts.push({ type: 'text', text: imageUrlNote(part.dataRef) })
    }
    messages.push({ ...message, parts })
  }
  signal.throwIfAborted()
  return { ...request, messages }
}

/**
 * 单张图的 `dataRef` → 可外发的 data URL。
 *
 * 需求:两个消费方共用**同一条**校验 —— 发往上游的请求(`prepareRequestImages`)
 * 和对话内生图的改图(`kernel/image-gen.ts` 的 `edit`)。它拥有这几条不变式:
 *
 * - `ncw://` 附件只认**本会话**的托管图(`scope === 'session'` 且 ownerId 相符),
 *   realpath 之后必须仍然落在 `attachments/sessions/<本会话>/` 之内 —— 软链、
 *   `..`、跨会话引用全在这两行里被拒,复制第二份实现迟早漏掉其中一种;
 * - 大小/魔数复核以**读回来的字节**为准,不信 part 自报的 mime;
 * - `data:` 分支做 base64 往返回环校验,拒绝畸形 data URL。
 *
 * ★ 只读不写:产出的 data URL 留在「要发出去的那份拷贝」里,转录里的 part
 * 原样不动(这正是 `prepareRequestImages` 的文件头约定)。
 */
export async function resolveImageDataRef(
  part: { mime: string; dataRef: string },
  host: KernelHost,
  context: { sessionId?: string },
  signal: AbortSignal
): Promise<{ mime: string; dataRef: string }> {
  signal.throwIfAborted()
  const mime = normalizeImageMime(part.mime)
  if (mime === null) {
    throw new ImageInputError(`Unsupported image media type: ${part.mime}`, 'unsupportedImage', { mime: part.mime })
  }
  const limit = MAX_ATTACHMENT_BYTES / 1024 / 1024
  if (part.dataRef.startsWith('data:')) {
    if (part.dataRef.length > Math.ceil(MAX_ATTACHMENT_BYTES / 3) * 4 + 128) {
      throw new ImageInputError('Invalid image attachment size', 'invalidSize', { limit })
    }
    const match = /^data:([^;,]+);base64,([A-Za-z0-9+/]+={0,2})$/i.exec(part.dataRef)
    if (match === null || normalizeImageMime(match[1]!) !== mime) {
      throw new ImageInputError('Invalid image data URL', 'invalidImageData')
    }
    const data = match[2]!
    const bytes = Buffer.from(data, 'base64')
    const encoded = bytes.toString('base64')
    if (encoded.replace(/=+$/, '') !== data.replace(/=+$/, '')) {
      throw new ImageInputError('Invalid image data URL', 'invalidImageData')
    }
    if (bytes.length === 0 || bytes.length > MAX_ATTACHMENT_BYTES) {
      throw new ImageInputError('Invalid image attachment size', 'invalidSize', { limit })
    }
    const actualMime = imageMimeOfBytes(bytes) ?? mime
    return { mime: actualMime, dataRef: `data:${actualMime};base64,${encoded}` }
  }

  const locator = parseNcwUrl(part.dataRef)
  if (locator === null) throw new ImageInputError('Invalid image attachment location', 'invalidLocation')
  if (locator.scope !== 'session' || locator.ownerId === undefined || locator.ownerId !== context.sessionId) {
    throw new ImageInputError('Image attachment does not belong to this session', 'foreignSession')
  }
  const rel = attachmentRelPath(locator)
  if (rel === null) throw new ImageInputError('Invalid image attachment location', 'invalidLocation')
  let stage: 'storage' | 'file' | 'read' = 'storage'
  try {
    const root = await host.fs.realpath(host.paths.attachments())
    const expectedOwnerRoot = join(root, 'sessions', locator.ownerId)
    const ownerRoot = await host.fs.realpath(expectedOwnerRoot)
    stage = 'file'
    const file = await host.fs.realpath(join(root, rel))
    if (relative(expectedOwnerRoot, ownerRoot) !== '' || !within(root, ownerRoot)
      || !within(ownerRoot, file)) throw new ImageInputError('Image attachment escaped its session directory', 'unsafePath')
    const stat = await host.fs.stat(file)
    if (stat.isDir) throw new ImageInputError('Image attachment is a directory', 'invalidLocation')
    if (stat.size === 0 || stat.size > MAX_ATTACHMENT_BYTES) {
      throw new ImageInputError('Invalid image attachment size', 'invalidSize', { limit })
    }
    stage = 'read'
    const bytes = await host.fs.readFileBytes(file, stat.size + 1)
    if (bytes.length !== stat.size) {
      throw new ImageInputError('Image attachment changed or was not read completely', 'incompleteRead', { name: locator.fileName })
    }
    const actualMime = imageMimeOfBytes(bytes) ?? mime
    return { mime: actualMime, dataRef: `data:${actualMime};base64,${Buffer.from(bytes).toString('base64')}` }
  } catch (error) {
    signal.throwIfAborted()
    if (error instanceof ImageInputError) throw error
    const code = (error as NodeJS.ErrnoException)?.code ?? 'UNKNOWN'
    const reason = stage === 'storage' ? 'storageUnavailable' : code === 'ENOENT' ? 'missing' : 'unreadable'
    throw new ImageInputError(`Image attachment is missing or unreadable (${stage}: ${code})`, reason, { name: locator.fileName, code })
  }
}
