/**
 * 附件上传 —— 落盘、去重、登记。
 *
 * ## 落盘顺序不是随意的
 *
 * `.tmp` 写完再 `rename` 是**原子替换**:写到一半崩掉留下的是一个 `.tmp`
 * 残片,而不是一个「大小对不上但看起来正常」的目标文件。后者的危害在于
 * 它的 checksum 已经进了表,下一次去重会**命中这个坏文件**,于是一个
 * 中断的上传会污染之后所有相同内容的上传。
 *
 * ## 为什么去重限定在同 scope + 同 owner
 *
 * 跨会话共用一个物理文件之后,删掉其中一个会话就会波及另一个仍在引用它的会话。
 * 要正确处理得上引用计数 —— 而省下的那点磁盘不值得引入一套引用计数的
 * 正确性负担(理由同 `repo.findAttachmentByChecksum`)。
 *
 * ## 这个模块的边界
 *
 * 三个**与窗口无关**的附件操作(`uploadAttachment` / `removeAttachment` /
 * `listSessionAttachments`)与它们的两个辅助(`sha256` / `toAttachment`)原样
 * 提取到这里,依赖收敛成三样:纯 Node 的 `services/attachment-paths`、`db/repo`
 * 和现有的纯 `ipc/errors`。于是调用它们**不再需要一个 Electron 模块图** ——
 * `main/session-images.ts`、`main/video-generation/download.ts`、
 * `main/imports/service.ts` 都只需要这一份落盘/去重/登记实现。
 *
 * ★ 留在 `ipc/attachment.ts` 的是 `pickAttachments`(要 `dialog`)与整条
 * WindowContext / 上传 grant 逻辑(`prepareWorkspaceUpload` /
 * `completeWorkspaceUpload` / `cancelWorkspaceUpload`,要窗口 sender 与
 * 环境租约)。那边 import 并重新导出本模块的这三个函数,旧调用点不受影响。
 */
import { createHash } from 'node:crypto'
import { lstatSync, mkdirSync, renameSync, rmSync, unlinkSync, writeFileSync } from 'node:fs'
import { basename, dirname, isAbsolute, join, relative, resolve } from 'node:path'
import type {
  Attachment,
  AttachmentScope,
  AttachmentUploadRequest
} from '../../shared/domain/attachment'
import {
  MAX_ATTACHMENT_BYTES,
  attachmentRelPath,
  buildNcwUrl,
  extOfMime,
  isImageMime,
  mimeOfExt,
  imageMimeOfBytes,
  normalizeImageMime
} from '../../shared/domain/attachment'
import { ulid } from '../../shared/util/id'
import * as repo from '../db/repo'
import { IpcError } from '../ipc/errors'
import { attachmentRoot } from './attachment-paths'

function sha256(bytes: Uint8Array): string {
  return createHash('sha256').update(bytes).digest('hex')
}

/**
 * 行 → 对外类型。★ `url` 由 locator 反推,**绝对路径不出主进程**。
 *
 * `displayName` 优先取列里存的原始名;迁移 6 之前的行没有它,退回 ULID 文件名 ——
 * 那正是加这一列之前的表现,旧数据不会更糟。
 */
function toAttachment(row: repo.AttachmentRow): Attachment {
  const scope = row.scope as AttachmentScope
  const url = buildNcwUrl({
    scope,
    ownerId: row.ownerId ?? undefined,
    fileName: basename(row.path)
  })
  if (url === null) {
    // 表里存着一条拼不出 URL 的记录 —— 只可能是数据被外部改过
    throw new IpcError('unknown', 'Invalid attachment record', undefined, { messageKey: 'attachment.error.invalidLocation' })
  }
  return {
    id: row.id,
    scope,
    ownerId: row.ownerId ?? undefined,
    displayName: row.displayName ?? basename(row.path),
    mime: mimeOfExt(row.path),
    size: row.size,
    checksum: row.checksum,
    createdAt: row.createdAt,
    url
  }
}

export function uploadAttachment(req: AttachmentUploadRequest): Attachment {
  if (req.bytes.byteLength === 0) throw new IpcError('unknown', 'Empty attachment', undefined, {
    messageKey: 'attachment.error.empty', messageParams: { name: req.displayName }
  })
  if (req.bytes.byteLength > MAX_ATTACHMENT_BYTES) {
    throw new IpcError('unknown', 'Attachment exceeds the upload limit', undefined, {
      messageKey: 'attachment.error.tooLarge', messageParams: { name: req.displayName, limit: MAX_ATTACHMENT_BYTES / 1024 / 1024 }
    })
  }

  let mime = req.mime
  const inferredMime = mimeOfExt(req.displayName)
  if (req.scope === 'session' && (isImageMime(mime) || isImageMime(inferredMime))) {
    // 浏览器/扩展名可能把 JPEG 写成 image/jpg,也可能把 PNG 标成 JPEG。
    // 能识别文件头时以字节为准,否则只接受发送端同一份 MIME 白名单。
    const declaredMime = mime.trim() === '' || mime === 'application/octet-stream' ? inferredMime : mime
    const imageMime = imageMimeOfBytes(req.bytes) ?? normalizeImageMime(declaredMime)
    if (imageMime === null) throw new IpcError('unknown', `Unsupported image media type: ${declaredMime}`, undefined, {
      messageKey: 'attachment.error.unsupportedImage', messageParams: { mime: declaredMime }
    })
    mime = imageMime
  }
  const checksum = sha256(req.bytes)

  // ★ 去重的是字节,但旧记录的类型、落点与大小也必须仍然可用。
  const hit = repo.findAttachmentByChecksum(checksum, req.scope, req.ownerId)
  if (hit !== undefined && (!isImageMime(mime) || mimeOfExt(hit.path) === mime)) {
    const hitRel = attachmentRelPath({ scope: req.scope, ownerId: req.ownerId, fileName: basename(hit.path) })
    if (hitRel !== null && resolve(hit.path) === resolve(attachmentRoot(), hitRel)) {
      try {
        const stat = lstatSync(hit.path)
        if (stat.isFile() && stat.size === req.bytes.byteLength) return { ...toAttachment(hit), displayName: req.displayName }
      } catch { /* 文件已丢失或不可读时,用本次上传的字节重新落盘。 */ }
    }
  }

  const id = ulid()
  const fileName = `${id}${extOfMime(mime)}`
  const rel = attachmentRelPath({ scope: req.scope, ownerId: req.ownerId, fileName })
  if (rel === null) throw new IpcError('unknown', 'Invalid attachment location', undefined, { messageKey: 'attachment.error.invalidLocation' })

  const target = join(attachmentRoot(), rel)

  // ★ 临时文件名以 `.` 开头:清理时按前缀就能认出残片,
  //   而它又不会被 `ncw://` 寻址到(fileName 段校验不过)。
  const tmp = join(dirname(target), `.${fileName}.tmp`)
  try {
    mkdirSync(dirname(target), { recursive: true })
    writeFileSync(tmp, req.bytes)
    renameSync(tmp, target)
  } catch (err) {
    try { rmSync(tmp, { force: true }) } catch { /* 保留原始写入错误。 */ }
    throw new IpcError('unknown', `Unable to save attachment: ${String(err)}`, undefined, {
      messageKey: 'attachment.error.writeFailed',
      messageParams: { name: req.displayName, code: (err as NodeJS.ErrnoException)?.code ?? 'UNKNOWN' }
    })
  }

  const createdAt = Date.now()
  repo.putDraftAttachment({
    id,
    scope: req.scope,
    ownerId: req.ownerId,
    path: target,
    size: req.bytes.byteLength,
    checksum,
    displayName: req.displayName,
    createdAt
  })

  const url = buildNcwUrl({ scope: req.scope, ownerId: req.ownerId, fileName })
  if (url === null) throw new IpcError('unknown', 'Invalid attachment location', undefined, { messageKey: 'attachment.error.invalidLocation' })

  return {
    id,
    scope: req.scope,
    ownerId: req.ownerId,
    displayName: req.displayName,
    mime,
    size: req.bytes.byteLength,
    checksum,
    createdAt,
    url
  }
}

export function removeAttachment(req: { id: string }): void {
  const row = repo.getAttachmentRow(req.id)
  if (row === undefined) return

  // This endpoint is for removing an attachment chip before send. A
  // committed row belongs to a message and must not be detached through a
  // renderer-controlled id; doing so would leave the transcript referring to
  // a missing file.
  if (row.status === 'committed' || row.messageId !== null || row.sessionId !== null) {
    throw new IpcError('unknown', '已提交的附件不能从消息中移除')
  }

  // Only files under the application-managed session tree may be unlinked.
  // Agent-generated images can be absolute paths and are deliberately kept as
  // references only; an attachment id must never become an arbitrary delete
  // primitive for the renderer.
  const managedRoot = join(attachmentRoot(), 'sessions')
  const path = resolve(row.path)
  const rel = relative(resolve(managedRoot), path)
  const managed = rel !== '' && !rel.startsWith('..') && !isAbsolute(rel)
  if (managed && repo.attachmentReferenceCount(row.path, row.id) === 0) {
    try {
      // lstat/unlink removes a symlink itself and never follows it to an
      // external target. Directories are not valid uploaded attachments.
      const st = lstatSync(row.path)
      if (st.isDirectory()) throw new Error('附件路径是目录')
      unlinkSync(row.path)
    } catch (err) {
      // A missing file is already equivalent to removal. Permission and type
      // errors are surfaced while the database row is retained for retry.
      if ((err as NodeJS.ErrnoException)?.code !== 'ENOENT') {
        throw new IpcError('unknown', `删除附件文件失败: ${String(err)}`)
      }
    }
  }

  // Remove the row after the physical operation. For external paths this only
  // removes our bookkeeping row; the user's file is never touched.
  repo.removeAttachmentRow(req.id)
}

/** 重启后恢复草稿附件区。★ 显示名从 `display_name` 列读回,不再退化成 ULID */
export function listSessionAttachments(req: { sessionId: string }): Attachment[] {
  return repo.listDraftAttachments(req.sessionId).map(toAttachment)
}
