/**
 * 工作区 handler。
 *
 * ★ 选目录走主进程 dialog.showOpenDialog —— **渲染层永不指定工作区根**(方案 §9)。
 * 这条不是洁癖:工作区根是「哪些路径算在工作区内」的基点,渲染层能改它,
 * 树、文档、以及所有按根算相对路径的地方就一起跟着漂。
 */
import { dialog } from 'electron'
import { basename } from 'node:path'
import { realpathSync } from 'node:fs'
import type { Workspace, WorkspaceSettings } from '../../shared/domain/workspace'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../shared/domain/workspace'
import { prefixedId } from '../../shared/util/id'
import { store } from '../state/store'
import { findLocalWorkspaceByRoot } from '../state/workspace-root'
import { windows } from '../window/registry'
import { terminalHost } from '../terminal-host'
import { IpcError } from './errors'

function announce(): void {
  windows.emitToAll('workspace:changed', { workspaces: store.listWorkspaces() })
}

export function listWorkspaces(): Workspace[] {
  return store.listWorkspaces()
}

export async function pickWorkspace(): Promise<Workspace | null> {
  const r = await dialog.showOpenDialog({
    title: '选择工作区目录',
    properties: ['openDirectory', 'createDirectory']
  })
  const picked = r.canceled ? undefined : r.filePaths[0]
  if (!picked) return null

  // ★ 立刻 realpath:macOS 上 /var → /private/var,不在这里归一化,
  //   路径围栏后面就要面对两个都"正确"的根(方案 §9)
  const rootPath = realpathSync.native(picked)

  // ★ 匹配走归一化比较:导入建出来的行可能是旧规则写的原样路径,只比字符串会重开一个。
  const existing = findLocalWorkspaceByRoot(rootPath)
  if (existing) {
    const touched = store.putWorkspace({ ...existing, lastOpenedAt: Date.now(), unavailable: false })
    announce()
    return touched
  }

  const now = Date.now()
  const ws = store.putWorkspace({
    id: prefixedId('ws'),
    name: basename(rootPath) || rootPath,
    rootPath,
    settings: structuredClone(DEFAULT_WORKSPACE_SETTINGS),
    createdAt: now,
    lastOpenedAt: now
  })
  announce()
  return ws
}

export function updateWorkspace(req: {
  id: string
  name?: string
  settings?: Partial<WorkspaceSettings>
}): Workspace {
  const cur = store.getWorkspace(req.id)
  if (!cur) throw new IpcError('unknown', `工作区不存在: ${req.id}`)
  const next = store.putWorkspace({
    ...cur,
    name: req.name ?? cur.name,
    settings: req.settings ? { ...cur.settings, ...req.settings } : cur.settings
  })
  announce()
  return next
}

/**
 * 关闭 = 从**记录**里移除,和「关闭外层 Tab」是两回事(方案 §8 三个必须分清的概念)。
 * 外层 Tab 的开关纯粹是窗口状态,不经过这里。
 */
export function closeWorkspace(id: string): void {
  terminalHost.killWorkspace(id)
  store.removeWorkspace(id)
  announce()
}
export { listDir, listWorkspaceDir } from '../services/workspace-listing'
