/**
 * 附件的类型与寻址 —— 上传落盘、`ncw://` 协议、清理三处共用的**唯一**词汇表。
 *
 * ## 为什么路径拼接与解析必须是纯函数
 *
 * 这个文件里的 `parseNcwUrl` 是**协议 handler 的安全边界的一半**。
 * 协议是渲染层可以任意构造输入的入口(与 `workspace:listDir` 同级别的不可信输入),
 * 而「解析 URL」和「读文件」如果写在同一个函数里,那条边界就只能靠端到端测试去验 ——
 * 而端到端测试跑不了 `%2e%2e%2f` 的几十种变体。
 *
 * 拆成纯函数之后,逃逸校验是一个可以被穷举断言的表达式。
 *
 * ## 磁盘文件名为什么是 ULID 而不是原始文件名
 *
 * 用户提供的文件名**绝不进路径**。三个理由,任意一个都足够:
 *
 * 1. **路径注入**:`../../../.ssh/id_rsa` 拼进 `join()` 就逃出去了。
 * 2. **重名覆盖**:同一会话传两次 `截图.png`,后者盖掉前者 ——
 *    而前一条消息还引用着它,于是历史记录里的图变成了另一张。
 * 3. **跨平台**:Windows 保留名(`CON`/`NUL`)、大小写不敏感、非法字符集,
 *    与 macOS/Linux 三方都不一致。
 *
 * 原始名存进 `displayName` 给 UI 显示 —— 磁盘上不认它。
 */

/**
 * ★ `session` 之外的 scope 都是**无主**的:它们不挂在任何 sessionId 上,
 * 于是不会被 `attachments` 表的 `ON DELETE CASCADE` 带走。这一条直接决定了
 * 清理规则必须按子树分治,否则它们会被当成孤儿删掉(设计 §6)。
 */
import type { AgentError } from '../agent/error'
import type { EnvironmentRef } from './environment'
import { environmentKey } from './environment'

export type AttachmentScope = 'session' | 'theme' | 'export'

export type FileReferenceSource =
  | { kind: 'local' }
  | { kind: 'workspace'; workspaceId: string; environment: EnvironmentRef; rootPath: string; connectionRevision: number }

export interface WorkspaceAttachmentIntent {
  ticket: string
  attachmentId: string
  workspaceId: string
  connectionName: string
  directory: string
  name: string
  size: number
}

export interface WorkspaceAttachmentReference {
  path: string
  name: string
  source: FileReferenceSource
}

export function fileReferenceMatches(source: FileReferenceSource | undefined, expected: FileReferenceSource): boolean {
  if (expected.kind === 'local') return source === undefined || source?.kind === 'local'
  return source?.kind === 'workspace' && source.workspaceId === expected.workspaceId && source.rootPath === expected.rootPath
    && source.connectionRevision === expected.connectionRevision && environmentKey(source.environment) === environmentKey(expected.environment)
}

/** scope → 目录名。复数形式,与既有的 `themes/` 保持一致 */
const SCOPE_DIR: Record<AttachmentScope, string> = {
  session: 'sessions',
  theme: 'themes',
  export: 'exports'
}

const DIR_SCOPE: Record<string, AttachmentScope> = {
  sessions: 'session',
  themes: 'theme',
  exports: 'export'
}

/** ★ 需要 ownerId 的 scope。theme 是全局的,没有主人 */
const NEEDS_OWNER: Record<AttachmentScope, boolean> = {
  session: true,
  theme: false,
  export: true
}

export interface AttachmentLocator {
  scope: AttachmentScope
  /** session/export 必须有;theme 没有 */
  ownerId?: string
  /** ULID + 扩展名。**不是原始文件名** */
  fileName: string
}

export interface Attachment {
  /** ULID。同时是磁盘文件名的主干 */
  id: string
  scope: AttachmentScope
  ownerId?: string
  /** 用户看到的名字。**只用于显示**,不参与任何路径拼接 */
  displayName: string
  mime: string
  size: number
  /** sha256 十六进制。同 scope 内去重用 */
  checksum: string
  createdAt: number
  /**
   * ★ 渲染层唯一该用的字段 —— **绝对路径不出主进程**。
   *
   * 给路径的话,渲染层除了拼字符串没有别的用途,却让目录结构变成了跨进程契约;
   * 而且绝对路径会随转录导出漂到别的机器上,成为一条永远指向不存在位置的记录。
   * `ncw://` URL 是可迁移的。
   */
  url: string
}

/**
 * `attachment:pick` 的一项。★ **两种形态不是实现细节,是产品行为的分叉**:
 * 图片要落盘(内联展示得有 `ncw://` 地址),非图片只回传用户选中的真实路径 ——
 * 与拖拽/粘贴那条路完全同规则(见 ChatView 的 `attachFiles`)。
 *
 * 拆成联合而不是「`Attachment` 上多一个可选 `path`」:后者允许出现
 * 两个字段都有或都没有的值,而这两种状态在下游(`partsOf`)没有任何含义。
 */
export type PickedAttachment =
  | { kind: 'attachment'; attachment: Attachment }
  /** `path` 是绝对路径。★ 它由用户在系统对话框里选定,与他拖进来的文件同源 */
  | { kind: 'path'; path: string; name: string }
  /** 单个文件失败也保留结果,不能让它从多选列表里静默消失。 */
  | { kind: 'error'; name: string; error: AgentError }

export interface AttachmentUploadRequest {
  scope: AttachmentScope
  ownerId?: string
  displayName: string
  mime: string
  /**
   * ★ `<ArrayBuffer>` 不是装饰 —— 同 `ImportedImage.bytes` 的理由:
   * 裸 `Uint8Array` 推出来是 `ArrayBufferLike`(含 `SharedArrayBuffer`),
   * 于是 `new Blob([bytes])` 不给过。
   */
  bytes: Uint8Array<ArrayBuffer>
}

/** 上传上限。超过这个量级,IPC 结构化克隆本身就是几百毫秒的卡顿 */
export const MAX_ATTACHMENT_BYTES = 32 * 1024 * 1024

/** 协议名与固定 host。host 保留给将来的第二个命名空间(如 preview) */
export const NCW_SCHEME = 'ncw'
export const NCW_HOST = 'attachments'

/** 草稿附件的回收宽限期。只删「既是 draft 又超期」的(设计 §6) */
export const DRAFT_ATTACHMENT_TTL_MS = 7 * 24 * 60 * 60 * 1000

// ─────────────────────────────────────────────────────────────
// 路径
// ─────────────────────────────────────────────────────────────

/**
 * 单个路径段是否安全。
 *
 * ★ 这里挡的不只是 `..`:
 * - 空段 → `a//b` 解析出来会多一层,拼回去与原路径不等价
 * - `.` → 合法但无意义,允许它等于允许 `./././x` 这种无限变体
 * - 分隔符 → 一个段里带 `/` 就等于凭空多了一级目录
 * - NUL → C 层字符串截断,`a\0.png` 在某些系统调用里变成 `a`
 */
function isSafeSegment(seg: string): boolean {
  if (seg === '' || seg === '.' || seg === '..') return false
  if (seg.includes('/') || seg.includes('\\')) return false
  if (seg.includes('\0')) return false
  return true
}

export function isSafeFileName(name: string): boolean {
  return isSafeSegment(name)
}

/**
 * locator → 相对于附件根的路径。**只产出正斜杠**,由调用方用 `join` 落到平台分隔符。
 *
 * 非法 locator 返回 null 而不是抛 —— 调用点全都在需要「拒绝并返回 403/404」
 * 的位置,异常在那里只会被立刻 catch 成一个返回值。
 */
export function attachmentRelPath(loc: AttachmentLocator): string | null {
  const dir = SCOPE_DIR[loc.scope]
  if (dir === undefined) return null
  if (!isSafeSegment(loc.fileName)) return null

  if (NEEDS_OWNER[loc.scope]) {
    if (loc.ownerId === undefined || !isSafeSegment(loc.ownerId)) return null
    return `${dir}/${loc.ownerId}/${loc.fileName}`
  }
  // 无主 scope 带了 ownerId 是调用方搞错了,不静默忽略
  if (loc.ownerId !== undefined) return null
  return `${dir}/${loc.fileName}`
}

export function buildNcwUrl(loc: AttachmentLocator): string | null {
  const rel = attachmentRelPath(loc)
  if (rel === null) return null
  // 每段单独编码:encodeURIComponent 会把 `/` 也编掉,整串编码会毁掉路径结构
  const encoded = rel.split('/').map(encodeURIComponent).join('/')
  return `${NCW_SCHEME}://${NCW_HOST}/${encoded}`
}

/** 预览档的查询参数。★ 只有这一个取值;协议侧遇到别的取值直接拒绝(见 `main/net/attachment-protocol.ts`) */
export const NCW_PREVIEW_PARAM = 'preview'
export const NCW_PREVIEW_THUMB = 'thumb'

/**
 * 界面里小尺寸展示用的地址:会话附件里的 png/jpeg 换成缩略档,其余原样返回。
 *
 * 需求:转录里连着几十张截图时,每张 `<img>` 都按原尺寸(常见 2560×1440)解码常驻,
 * 而卡片只画 360px 高。缩略档是主进程按需生成、单独缓存的小文件。
 *
 * ★ 只用于卡片/气泡里的 `<img src>`。灯箱、另存、发给模型的永远是原图地址 ——
 * 这个地址绝不能写进转录。
 */
export function ncwPreviewUrl(dataRef: string): string {
  const loc = parseNcwUrl(dataRef)
  if (loc === null || loc.scope !== 'session') return dataRef
  if (dataRef.includes('?') || dataRef.includes('#')) return dataRef
  const mime = mimeOfExt(loc.fileName)
  if (mime !== 'image/png' && mime !== 'image/jpeg') return dataRef
  return `${dataRef}?${NCW_PREVIEW_PARAM}=${NCW_PREVIEW_THUMB}`
}

/**
 * `ncw://` URL → locator。**非法一律返回 null**,没有第二种失败表达。
 *
 * ★ 解码在校验之前是**必须**的:`%2e%2e%2f` 解码出来就是 `../`,
 * 只在解码前检查等于没检查。而解码后必须重新逐段校验 —— 这两步的顺序
 * 是这个函数存在的全部意义。
 */
export function parseNcwUrl(raw: string): AttachmentLocator | null {
  let url: URL
  try {
    url = new URL(raw)
  } catch {
    return null
  }

  if (url.protocol !== `${NCW_SCHEME}:`) return null
  // ★ 大小写不敏感:Chromium 会把 standard scheme 的 host 强制小写,
  //   而这个函数在主进程侧也被直接调用(测试、反解),两边要得出同样的结论。
  if (url.hostname.toLowerCase() !== NCW_HOST) return null

  let decoded: string
  try {
    decoded = decodeURIComponent(url.pathname)
  } catch {
    // 畸形百分号转义(如 `%zz`)——拒绝,不做尽力而为的修复
    return null
  }

  const segs = decoded.split('/').filter((s) => s !== '')
  // 解码后逐段重验 —— 这一步挡的就是编码逃逸
  if (segs.length < 2 || segs.length > 3) return null
  if (!segs.every(isSafeSegment)) return null

  const scope = DIR_SCOPE[segs[0] as string]
  if (scope === undefined) return null

  if (segs.length === 3) {
    if (!NEEDS_OWNER[scope]) return null
    return { scope, ownerId: segs[1], fileName: segs[2] as string }
  }
  if (NEEDS_OWNER[scope]) return null
  return { scope, fileName: segs[1] as string }
}

// ─────────────────────────────────────────────────────────────
// MIME
// ─────────────────────────────────────────────────────────────

const EXT_BY_MIME: Record<string, string> = {
  'image/png': '.png',
  'image/jpeg': '.jpg',
  'image/gif': '.gif',
  'image/webp': '.webp',
  'image/svg+xml': '.svg',
  'application/pdf': '.pdf',
  'text/plain': '.txt',
  'text/markdown': '.md',
  'application/json': '.json',
  'application/zip': '.zip',
  /*
    ★ 视频进这张表的理由是**协议侧要按扩展名回 Content-Type**:
    `attachment-protocol.ts` 用 `mimeOfExt` 决定响应头,推不出来时浏览器
    只会把文件当下载、`<video>` 播不了(它的注释里记的就是这条)。
    少了这四行,生成的视频在卡片上是一个"下载"而不是一个播放器。
  */
  'video/mp4': '.mp4',
  'video/webm': '.webm',
  'video/quicktime': '.mov',
  'video/x-matroska': '.mkv'
}

const MIME_BY_EXT: Record<string, string> = {
  png: 'image/png',
  jpg: 'image/jpeg',
  jpeg: 'image/jpeg',
  jfif: 'image/jpeg',
  gif: 'image/gif',
  webp: 'image/webp',
  svg: 'image/svg+xml',
  pdf: 'application/pdf',
  txt: 'text/plain',
  md: 'text/markdown',
  json: 'application/json',
  zip: 'application/zip',
  mp4: 'video/mp4',
  m4v: 'video/mp4',
  webm: 'video/webm',
  mov: 'video/quicktime',
  mkv: 'video/x-matroska'
}

export type SupportedImageMime = 'image/png' | 'image/jpeg' | 'image/gif' | 'image/webp'

/** 上传与发送共用白名单;MIME 别名不能让预览可见的图片在发送时被拒。 */
export function normalizeImageMime(mime: string): SupportedImageMime | null {
  switch ((mime.split(';', 1)[0] ?? '').trim().toLowerCase()) {
    case 'image/png': case 'image/x-png': return 'image/png'
    case 'image/jpeg': case 'image/jpg': case 'image/pjpeg': return 'image/jpeg'
    case 'image/gif': return 'image/gif'
    case 'image/webp': return 'image/webp'
    default: return null
  }
}

/** 只识别容器签名,不在共享层解码整张图;字节可纠正不可靠的扩展名与 MIME。 */
export function imageMimeOfBytes(bytes: Uint8Array): SupportedImageMime | null {
  if ([137, 80, 78, 71, 13, 10, 26, 10].every((value, i) => bytes[i] === value)) return 'image/png'
  if (bytes[0] === 255 && bytes[1] === 216 && bytes[2] === 255) return 'image/jpeg'
  const header = String.fromCharCode(...bytes.subarray(0, 12))
  if (header.startsWith('GIF87a') || header.startsWith('GIF89a')) return 'image/gif'
  if (header.startsWith('RIFF') && header.slice(8, 12) === 'WEBP') return 'image/webp'
  return null
}

/**
 * ★ 未知 mime 落到 `.bin` 而**不是没有扩展名**:无扩展名的文件在
 * `mimeOfExt` 那边推不出类型,协议就只能回 `application/octet-stream`,
 * 浏览器于是把它当下载而不是内联 —— 一个 mime 表的空缺会变成一个显示 bug。
 */
export function extOfMime(mime: string): string {
  return EXT_BY_MIME[normalizeImageMime(mime) ?? mime.toLowerCase()] ?? '.bin'
}

export function mimeOfExt(pathOrName: string): string {
  const i = pathOrName.lastIndexOf('.')
  if (i < 0) return 'application/octet-stream'
  return MIME_BY_EXT[pathOrName.slice(i + 1).toLowerCase()] ?? 'application/octet-stream'
}

export function isImageMime(mime: string): boolean {
  return mime.trim().toLowerCase().startsWith('image/')
}

export function isVideoMime(mime: string): boolean {
  return mime.trim().toLowerCase().startsWith('video/')
}
