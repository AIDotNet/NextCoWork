/**
 * 设置与工作区的远程读写。
 *
 * 写入全部是 `execution`(幂等回执)。设置走字段**白名单**(见 shared/device/methods.ts),
 * 工作区根由设备自己 realpath、核对是目录,并按归一化路径去重 —— 与桌面选目录后的
 * 处理一致,只是目录由控制端指定、设备端校验。
 */
import { realpathSync, statSync } from 'node:fs'
import { basename, isAbsolute } from 'node:path'
import { z } from 'zod'
import { DEFAULT_WORKSPACE_SETTINGS, type WorkspaceSettings } from '../../../shared/domain/workspace'
import { DeviceProtocolError } from '../../../shared/device/errors'
import {
  DEVICE_METHODS, settingsUpdateParams, settingsUpdateResult, updatedResult, workspacesCloseParams,
  workspacesCreateParams, workspacesCreateResult, workspacesUpdateParams
} from '../../../shared/device/methods'
import type { DeviceValue } from '../../../shared/device/value'
import { prefixedId } from '../../../shared/util/id'
import { store } from '../../../main/state/store'
import { findLocalWorkspaceByRoot } from '../../../main/state/workspace-root'
import type { DeviceRpcRouter } from '../rpc'

const anyValue = z.custom<DeviceValue>(() => true)
const plain = (value: unknown): DeviceValue => JSON.parse(JSON.stringify(value ?? null)) as DeviceValue
const withoutUndefined = <T extends object>(value: T): T =>
  Object.fromEntries(Object.entries(value).filter(([, item]) => item !== undefined)) as T

export function registerConfigMethods(router: DeviceRpcRouter): void {
  router.query(DEVICE_METHODS.settingsGet, z.null(), anyValue, () => plain(store.getSettings()))

  router.execution(DEVICE_METHODS.settingsUpdate, settingsUpdateParams, settingsUpdateResult, (_execution, { patch }) => {
    store.updateSettings(withoutUndefined(patch))
    return { updated: true as const }
  })

  router.query(DEVICE_METHODS.workspacesList, z.null(), anyValue, () => plain(store.listWorkspaces()))

  router.execution(DEVICE_METHODS.workspacesCreate, workspacesCreateParams, workspacesCreateResult, (_execution, params) => {
    if (!isAbsolute(params.path)) throw new DeviceProtocolError('invalid_request', 'Workspace path must be absolute')
    let root: string
    try {
      root = realpathSync.native(params.path)
      if (!statSync(root).isDirectory()) throw new Error('not a directory')
    } catch {
      throw new DeviceProtocolError('not_found', 'Workspace directory does not exist')
    }
    // 归一化比较:同一目录不会因路径写法不同而开出第二个工作区。
    const existing = findLocalWorkspaceByRoot(root)
    if (existing !== undefined) {
      store.putWorkspace({ ...existing, lastOpenedAt: Date.now(), unavailable: false })
      return { workspaceId: existing.id, created: false }
    }
    const now = Date.now()
    const created = store.putWorkspace({
      id: prefixedId('ws'), name: params.name ?? (basename(root) || root), rootPath: root,
      settings: structuredClone(DEFAULT_WORKSPACE_SETTINGS), createdAt: now, lastOpenedAt: now
    })
    return { workspaceId: created.id, created: true }
  })

  router.execution(DEVICE_METHODS.workspacesUpdate, workspacesUpdateParams, updatedResult, (_execution, params) => {
    const current = store.getWorkspace(params.id)
    if (current === undefined) throw new DeviceProtocolError('not_found', 'Workspace does not exist')
    store.putWorkspace({
      ...current, name: params.name ?? current.name,
      settings: params.settings === undefined
        ? current.settings
        : { ...current.settings, ...withoutUndefined(params.settings) } as WorkspaceSettings
    })
    return { updated: true as const }
  })

  router.execution(DEVICE_METHODS.workspacesClose, workspacesCloseParams, updatedResult, (_execution, params) => {
    if (store.getWorkspace(params.id) === undefined) throw new DeviceProtocolError('not_found', 'Workspace does not exist')
    store.removeWorkspace(params.id)
    return { updated: true as const }
  })
}
