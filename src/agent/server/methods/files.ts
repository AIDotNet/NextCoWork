/**
 * 远程文件:列目录、读、写(带版本冲突保护)、新建/改名/移动/复制/删除。
 *
 * 全部复用桌面同一套 `services/workspace-files`(路径围栏、符号链接、二进制/大小判定、
 * 内容摘要冲突检测)。错误只输出稳定的错误码,不回显路径或系统消息。
 * ★ 单帧上限 1 MiB:更大的文件读取得到 `resource_limit`(分片/范围下载是后续的资源通道)。
 */
import { z } from 'zod'
import { IpcError } from '../../../main/ipc/errors'
import { listDirectories } from '../../../main/services/directory-browse'
import { listWorkspaceDir } from '../../../main/services/workspace-listing'
import { readWorkspaceRange } from '../../../main/services/workspace-range'
import type { UploadManager } from '../../../main/services/workspace-upload'
import {
  mutateWorkspaceDocument, readWorkspaceDocument, writeWorkspaceDocument
} from '../../../main/services/workspace-files'
import { DeviceProtocolError, type DeviceErrorCode } from '../../../shared/device/errors'
import {
  DEVICE_METHODS, directoriesListParams, filesListParams, filesMutateParams, filesReadParams, filesReadRangeParams,
  filesUploadAbortParams, filesUploadBeginParams, filesUploadChunkParams, filesUploadCommitParams, filesWriteParams
} from '../../../shared/device/methods'
import type { DeviceValue } from '../../../shared/device/value'
import { WORKSPACE_FILE_ERROR_PREFIX, type WorkspaceFileMutationRequest } from '../../../shared/domain/workspace-file'
import { store } from '../../../main/state/store'
import type { DeviceRpcRouter } from '../rpc'

const anyValue = z.custom<DeviceValue>(() => true)
const plain = (value: unknown): DeviceValue => JSON.parse(JSON.stringify(value ?? null)) as DeviceValue

const FILE_ERRORS: Record<string, DeviceErrorCode> = {
  'not-found': 'not_found', exists: 'conflict', conflict: 'conflict', 'invalid-path': 'invalid_request', symlink: 'forbidden',
  permission: 'forbidden', 'too-large': 'resource_limit', 'not-file': 'invalid_request', 'invalid-encoding': 'invalid_request',
  unsupported: 'not_configured', io: 'internal', 'workspace-unavailable': 'not_found'
}

/** 把桌面文件错误译成稳定的设备错误码;其余异常一律交给 `toDeviceError` 脱敏。 */
async function guarded<T>(work: () => Promise<T> | T): Promise<T> {
  try {
    return await work()
  } catch (error) {
    if (error instanceof IpcError && error.message.startsWith(WORKSPACE_FILE_ERROR_PREFIX)) {
      const code = error.message.slice(WORKSPACE_FILE_ERROR_PREFIX.length)
      throw new DeviceProtocolError(FILE_ERRORS[code] ?? 'internal', `File operation failed: ${code}`)
    }
    throw error
  }
}

function requireWorkspace(workspaceId: string): void {
  if (store.getWorkspace(workspaceId) === undefined) throw new DeviceProtocolError('not_found', 'Workspace does not exist')
}

export function registerFileMethods(router: DeviceRpcRouter, uploads?: UploadManager): void {
  router.query(DEVICE_METHODS.filesList, filesListParams, anyValue, (_context, params) => {
    requireWorkspace(params.workspaceId)
    return guarded(async () => plain(await listWorkspaceDir(params)))
  })

  router.query(DEVICE_METHODS.filesRead, filesReadParams, anyValue, (_context, params) => {
    requireWorkspace(params.workspaceId)
    return guarded(async () => plain(await readWorkspaceDocument(params)))
  })

  router.query(DEVICE_METHODS.filesReadRange, filesReadRangeParams, anyValue, (_context, params) => {
    requireWorkspace(params.workspaceId)
    return guarded(() => plain(readWorkspaceRange({
      workspaceId: params.workspaceId, path: params.path, offset: params.offset, length: params.length,
      ...(params.revision !== undefined && { revision: params.revision })
    })))
  })

  router.execution(DEVICE_METHODS.filesWrite, filesWriteParams, anyValue, (_execution, params) => {
    requireWorkspace(params.workspaceId)
    return guarded(async () => {
      const written = await writeWorkspaceDocument({
        workspaceId: params.workspaceId, path: params.path, content: params.content, revision: params.revision,
        ...(params.encoding !== undefined && { encoding: params.encoding })
      })
      // 回执只带新版本与大小,不重复整份内容。
      return plain({ revision: (written as { revision?: string }).revision ?? null, size: (written as { size?: number }).size ?? null })
    })
  })

  router.execution(DEVICE_METHODS.filesMutate, filesMutateParams, anyValue, (_execution, params) => {
    requireWorkspace(params.workspaceId)
    const request: WorkspaceFileMutationRequest = { workspaceId: params.workspaceId, path: params.path, operation: params.operation }
    if (params.destination !== undefined) request.destination = params.destination
    if (params.overwrite !== undefined) request.overwrite = params.overwrite
    return guarded(async () => plain(await mutateWorkspaceDocument(request)))
  })

  // 目录浏览:只给子目录名,用来选工作区根(不在工作区围栏内,所以单独收紧,见 directory-browse.ts)。
  router.query(DEVICE_METHODS.directoriesList, directoriesListParams, anyValue, (_context, params) => plain(listDirectories(params.path)))

  if (uploads === undefined) return

  // begin / commit 是 execution:重放同一个 key 返回同一个 uploadId / 同一个提交结果。
  router.execution(DEVICE_METHODS.filesUploadBegin, filesUploadBeginParams,
    z.strictObject({ uploadId: z.string(), chunkMax: z.number() }), (execution, params) => {
      requireWorkspace(params.workspaceId)
      return guarded(() => {
        const begun = uploads.begin(execution.binding.controllerId, {
          workspaceId: params.workspaceId, path: params.path, size: params.size, overwrite: params.overwrite === true
        })
        return { uploadId: begun.id, chunkMax: begun.chunkMax }
      })
    })

  // chunk 顺序写、可续传:offset 必须等于已接收字节数,所以重放不会写两遍。
  router.query(DEVICE_METHODS.filesUploadChunk, filesUploadChunkParams, z.strictObject({ received: z.number() }), (context, params) => {
    const bytes = Buffer.from(params.data, 'base64')
    // base64 里混进非法字符会被 Buffer 静默忽略:回编不一致就是被篡改/损坏的输入。
    if (bytes.toString('base64') !== params.data) throw new DeviceProtocolError('invalid_request', 'Invalid chunk encoding')
    return guarded(() => uploads.chunk(context.binding.controllerId, params.uploadId, params.offset, bytes))
  })

  router.execution(DEVICE_METHODS.filesUploadCommit, filesUploadCommitParams, z.strictObject({ size: z.number() }),
    (execution, params) => guarded(() => uploads.commit(execution.binding.controllerId, params.uploadId, params.sha256)))

  router.query(DEVICE_METHODS.filesUploadAbort, filesUploadAbortParams, z.strictObject({ aborted: z.boolean() }),
    (context, params) => ({ aborted: uploads.abort(context.binding.controllerId, params.uploadId) }))
}
