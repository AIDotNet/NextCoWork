/**
 * 附件 IPC —— 选文件,以及「上传到工作区」的授权流程。
 *
 * ## 落盘/去重/登记不在这里
 *
 * `uploadAttachment` / `removeAttachment` / `listSessionAttachments` 连同它们的
 * 两个辅助(`sha256` / `toAttachment`)搬到了 `services/attachments.ts`:那边只依赖
 * 纯 Node 的 `services/attachment-paths`、`db/repo` 与纯 `ipc/errors`,于是
 * 会话图片仓、生成视频落盘、导入器都不再需要一份 Electron 模块图。这里
 * **原样重新导出**这三个函数,`ipc/index.ts` / `ipc/sessions.ts` 的调用点一行都不用改。
 *
 * ## 本文件剩下的是两件必须跟着窗口走的事
 *
 * 1. `pickAttachments` —— 走主进程 `dialog` 选文件;
 * 2. `prepareWorkspaceUpload` / `completeWorkspaceUpload` / `cancelWorkspaceUpload`
 *    —— 上传 grant 绑定 `WindowContext`(所有权、60s 计时、导航即作废),而
 *    `completeWorkspaceUpload` 自己仍要用 `sha256` 复核读回来的字节。
 *
 * 落盘顺序(临时文件 + rename)与去重范围的取舍写在 `services/attachments.ts`
 * 的文件头 —— 那是它们现在所在的地方。
 */
import { createHash, randomUUID } from 'node:crypto'
import { constants, promises as fs } from 'node:fs'
import { readFileSync, statSync } from 'node:fs'
import { basename, isAbsolute, join, relative } from 'node:path'
import { dialog } from 'electron'
import type {
  AttachmentScope,
  PickedAttachment,
  WorkspaceAttachmentIntent,
  WorkspaceAttachmentReference
} from '../../shared/domain/attachment'
import { MAX_ATTACHMENT_BYTES, isImageMime, mimeOfExt } from '../../shared/domain/attachment'
import * as repo from '../db/repo'
import { attachmentRoot } from '../services/attachment-paths'
import { IpcError, toAgentError } from './errors'
import { EnvironmentError, normalizeEnvironmentRef } from '../../shared/domain/environment'
import type { EnvironmentLease } from '../environment/contract'
import { uploadWorkspaceAttachment } from '../environment/artifacts'
import type { WindowContext } from '../window/registry'
import { uploadAttachment } from '../services/attachments'

/*
  ★ 从纯服务原样重新导出:这两个 + `uploadAttachment` 曾经就在本文件里,调用点
  (`ipc/index.ts`、`ipc/sessions.ts`)继续按现在的路径 import。
*/
export { listSessionAttachments, removeAttachment, uploadAttachment } from '../services/attachments'

type UploadGrant = {
  owner: number
  sessionId: string
  lease: EnvironmentLease
  intent: WorkspaceAttachmentIntent
  source: WorkspaceAttachmentReference['source']
  timer: NodeJS.Timeout
  uploading: boolean
  cancelled: boolean
}
const uploadGrants = new Map<string, UploadGrant>()
const uploadWindows = new WeakSet<WindowContext['sender']>()

export function cancelWorkspaceUpload(ticket: string, ctx: WindowContext): void {
  const grant = uploadGrants.get(ticket)
  if (!grant || grant.owner !== ctx.id) return
  grant.cancelled = true
  clearTimeout(grant.timer)
  if (!grant.uploading) { uploadGrants.delete(ticket); grant.lease.release() }
}

export async function prepareWorkspaceUpload(req: { id: string; sessionId: string; workspaceId: string }, ctx: WindowContext): Promise<WorkspaceAttachmentIntent> {
  const row = repo.getAttachmentRow(req.id)
  const workspace = repo.getWorkspace(req.workspaceId)
  const ref = normalizeEnvironmentRef(workspace?.environment)
  const session = repo.getSession(req.sessionId)
  if (!workspace || ref.kind !== 'connection' || !row || row.scope !== 'session' || row.ownerId !== req.sessionId
    || row.status !== 'draft' || (session && session.workspaceId !== req.workspaceId)) throw new EnvironmentError('unbound')
  if (row.size > MAX_ATTACHMENT_BYTES || uploadGrants.size >= 64) throw new EnvironmentError('unsupported')
  const profile = repo.getConnectionProfile(ref.connectionId)
  if (!profile?.enabled) throw new EnvironmentError('disabled')
  const { getEnvironments } = await import('../runtime')
  if (ctx.sender.isDestroyed()) throw new EnvironmentError('cancelled')
  const lease = getEnvironments().acquire(req.workspaceId)
  if (!lease.environment.remote) { lease.release(); throw new EnvironmentError('unbound') }
  const ticket = randomUUID()
  const intent: WorkspaceAttachmentIntent = { ticket, attachmentId: req.id, workspaceId: workspace.id,
    connectionName: lease.environment.description, directory: lease.environment.path.join(lease.environment.rootPath, '.next-cowork', 'attachments'),
    name: row.displayName ?? basename(row.path), size: row.size }
  const timer = setTimeout(() => cancelWorkspaceUpload(ticket, ctx), 60_000)
  timer.unref?.()
  uploadGrants.set(ticket, { owner: ctx.id, sessionId: req.sessionId, lease, intent, timer, uploading: false, cancelled: false,
    source: { kind: 'workspace', workspaceId: workspace.id, environment: ref, rootPath: lease.environment.rootPath, connectionRevision: profile.revision } })
  if (!uploadWindows.has(ctx.sender)) {
    uploadWindows.add(ctx.sender)
    const cancel = (): void => { for (const [id, grant] of uploadGrants) if (grant.owner === ctx.id) cancelWorkspaceUpload(id, ctx) }
    ctx.sender.once('destroyed', cancel)
    ctx.sender.on('did-start-navigation', (_event, _url, _inPlace, mainFrame) => { if (mainFrame) cancel() })
  }
  return intent
}

export async function completeWorkspaceUpload(ticket: string, ctx: WindowContext): Promise<WorkspaceAttachmentReference> {
  const grant = uploadGrants.get(ticket)
  if (!grant || grant.owner !== ctx.id || grant.uploading || grant.cancelled) throw new EnvironmentError('approval-expired')
  grant.uploading = true
  clearTimeout(grant.timer)
  const assertCurrent = (): void => {
    if (grant.cancelled || ctx.sender.isDestroyed()) throw new EnvironmentError('cancelled')
    grant.lease.environment.assertReady()
    const source = grant.source
    const workspace = repo.getWorkspace(grant.intent.workspaceId)
    if (source.kind !== 'workspace' || source.environment.kind !== 'connection'
      || JSON.stringify(normalizeEnvironmentRef(workspace?.environment)) !== JSON.stringify(source.environment)
      || workspace?.rootPath !== source.rootPath
      || repo.getConnectionProfile(source.environment.connectionId)?.revision !== source.connectionRevision) throw new EnvironmentError('conflict')
  }
  try {
    assertCurrent()
    const row = repo.getAttachmentRow(grant.intent.attachmentId)
    if (!row || row.status !== 'draft' || row.ownerId !== grant.sessionId || row.scope !== 'session') throw new EnvironmentError('conflict')
    const root = await fs.realpath(join(attachmentRoot(), 'sessions', grant.sessionId))
    const path = await fs.realpath(row.path)
    const rel = relative(root, path)
    if (rel === '' || rel.startsWith('..') || isAbsolute(rel)) throw new EnvironmentError('invalid-path')
    const handle = await fs.open(path, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0))
    let bytes: Buffer
    try {
      const stat = await handle.stat()
      if (!stat.isFile() || stat.size !== row.size || stat.size > MAX_ATTACHMENT_BYTES) throw new EnvironmentError('conflict')
      bytes = Buffer.alloc(stat.size)
      let offset = 0
      while (offset < bytes.length) {
        const result = await handle.read(bytes, offset, bytes.length - offset, offset)
        if (!result.bytesRead) throw new EnvironmentError('conflict')
        offset += result.bytesRead
      }
      if ((await handle.stat()).size !== row.size || sha256(bytes) !== row.checksum) throw new EnvironmentError('conflict')
    } finally { await handle.close() }
    const pathOnServer = await uploadWorkspaceAttachment(grant.lease.environment, bytes, grant.intent.name, assertCurrent)
    return { path: pathOnServer, name: grant.intent.name, source: grant.source }
  } finally { uploadGrants.delete(ticket); grant.lease.release() }
}

function sha256(bytes: Uint8Array): string {
  return createHash('sha256').update(bytes).digest('hex')
}

/**
 * 走主进程 dialog 选文件。
 *
 * ★ **渲染层永不指定任意路径**(方案 §9)—— 与 `workspace:pick` / `theme:importImage`
 * 同一条规则。渲染层给的是「我想选文件」这个意图,路径由用户在系统对话框里定。
 * 回传路径**不违反**这条:路径是用户刚刚在对话框里选定的既成事实,与他拖进来的
 * 文件同源,渲染层依旧没有编造一个读取参数的能力。
 *
 * ## 分流规则与拖拽/粘贴必须逐字相同(ChatView 的 `attachFiles`)
 *
 * 图片落盘 —— 内联展示要有 `ncw://` 地址;非图片**不落盘**,只回传真实路径。
 * 复制一份没人会去读的字节副本没有意义:这个应用本身就是个带文件系统工具的
 * Agent,给它真实路径比塞一份副本有用得多。
 *
 * ★ 两个入口分流不一致的代价是**一个入口悄悄失效**:同一个 PDF 从菜单选进来
 * 只剩转录里一句「[附件] 名字」(`partsOf` 对无路径的非图片就是这么退化的),
 * 从拖拽进来却是模型可以打开的 `file_ref`。用户看到的是同一排 chip。
 */
export async function pickAttachments(req: {
  scope: AttachmentScope
  ownerId?: string
  stageFiles?: boolean
}): Promise<PickedAttachment[]> {
  const r = await dialog.showOpenDialog({
    properties: ['openFile', 'multiSelections'],
    title: '选择附件'
  })
  if (r.canceled) return []

  const out: PickedAttachment[] = []
  for (const path of r.filePaths) {
    try {
      const st = statSync(path)
      if (!st.isFile()) continue
      const mime = mimeOfExt(path)
      // ★ 非图片这一支**不读字节**,所以也不受上传上限约束 —— 上限存在的理由是
      //   结构化克隆的卡顿(见 MAX_ATTACHMENT_BYTES),而这里一个字节都不过 IPC。
      //   拖一个 200MB 的日志进来是可以的,从菜单选同一个文件没有理由被静默丢掉。
      if (!isImageMime(mime) && !req.stageFiles) {
        out.push({ kind: 'path', path, name: basename(path) })
        continue
      }
      if (st.size > MAX_ATTACHMENT_BYTES) throw new IpcError('unknown', 'Attachment exceeds the upload limit', undefined, {
        messageKey: 'attachment.error.tooLarge', messageParams: { name: basename(path), limit: MAX_ATTACHMENT_BYTES / 1024 / 1024 }
      })
      const bytes = new Uint8Array(readFileSync(path))
      out.push({
        kind: 'attachment',
        attachment: uploadAttachment({
          scope: req.scope,
          ownerId: req.ownerId,
          displayName: basename(path),
          mime,
          bytes: bytes as Uint8Array<ArrayBuffer>
        })
      })
    } catch (err) {
      // 单个文件失败不拖垮整批,也不能让失败项在托盘里消失。
      const name = basename(path)
      const error = err instanceof IpcError ? err : new IpcError('unknown', `Unable to read attachment: ${String(err)}`, undefined, {
        messageKey: 'attachment.error.readFailed', messageParams: { name, code: (err as NodeJS.ErrnoException)?.code ?? 'UNKNOWN' }
      })
      out.push({ kind: 'error', name, error: toAgentError(error) })
    }
  }
  return out
}
