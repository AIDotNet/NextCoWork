import { readdirSync, statSync } from 'node:fs'
import type { DirListing, FileEntry } from '../../shared/domain/file-tree'
import { DIR_LISTING_LIMIT, sortEntries } from '../../shared/domain/file-tree'
import { isLocalEnvironment } from '../../shared/domain/environment'
import { resolveAnywhere } from '../kernel/tool/path-guard'
import { store } from '../state/store'
import { IpcError } from '../ipc/errors'
import { getWorkspaceEnvironment } from '../runtime'
import { EnvironmentError, missingPath } from '../environment/errors'

/**
 * 右侧文件树列**一层**。
 *
 * 三件事值得写下来:
 *
 * 1. **`req.path` 是不可信输入**。它来自渲染层,所以一律经 `resolveAnywhere`
 *    (方案 §9 的唯一入口)。它不再把工作区外的路径判成错误 —— 工具卡片和
 *    Markdown 链接现在会给出工作区外的绝对路径,点开它们要能落到这儿。
 *    树本身仍然从工作区根起步,UI 上没有「往上走」的入口。
 * 2. **不递归**。参考实现的目录默认是收起的(`>`),展开一个才拉一层 ——
 *    这不只是交互,它是让 `node_modules` 不会一次性把 IPC 撑爆的原因。
 * 3. **`withFileTypes` + 逐项 `statSync` 分开**。`stat` 会跟随符号链接,
 *    指向不存在目标的软链会抛 —— 那一项按"文件、无大小"处理,不能让整次列目录失败。
 */
export function listDir(req: { workspaceId: string; path: string }): DirListing {
  const ws = store.getWorkspace(req.workspaceId)
  if (!ws) throw new IpcError('unknown', `工作区不存在: ${req.workspaceId}`)
  if (!isLocalEnvironment(ws.environment)) throw new EnvironmentError('disconnected')

  const dir = resolveAnywhere(ws.rootPath, req.path).abs

  const raw = readdirSync(dir, { withFileTypes: true })
  const truncated = raw.length > DIR_LISTING_LIMIT
  const entries: FileEntry[] = []

  for (const d of raw.slice(0, DIR_LISTING_LIMIT)) {
    // 软链要看它指向什么:指向目录的软链在树里就该是个能展开的目录
    let isDir = d.isDirectory()
    let size: number | undefined
    let mtime: number | undefined
    try {
      const st = statSync(`${dir}/${d.name}`)
      isDir = st.isDirectory()
      if (!isDir) size = st.size
      mtime = st.mtimeMs
    } catch {
      // 断掉的软链 / 刚被删掉 / 没权限 —— 仍然列出来,只是没有大小
    }
    entries.push({
      name: d.name,
      path: req.path === '' ? d.name : `${req.path}/${d.name}`,
      kind: isDir ? 'dir' : 'file',
      hidden: d.name.startsWith('.'),
      ...(size !== undefined ? { size } : {}),
      ...(mtime !== undefined ? { mtime } : {})
    })
  }

  return { path: req.path, entries: sortEntries(entries), truncated }
}

export async function listWorkspaceDir(req: { workspaceId: string; path: string }): Promise<DirListing> {
  const workspace = store.getWorkspace(req.workspaceId)
  if (!workspace || isLocalEnvironment(workspace.environment)) return listDir(req)
  const environment = getWorkspaceEnvironment(req.workspaceId)
  const target = (await environment.path.resolve(environment.rootPath, req.path)).abs
  const raw = await environment.fs.readDir(target)
  const entries: FileEntry[] = []
  for (const entry of raw.slice(0, DIR_LISTING_LIMIT)) {
    let stat: { size: number; mtimeMs: number } | undefined
    try { stat = await environment.fs.stat(environment.path.join(target, entry.name)) } catch (error) {
      if (!missingPath(error) && (error as { code?: string })?.code !== 'EACCES') throw error
    }
    entries.push({ name: entry.name, path: req.path ? `${req.path.replaceAll('\\', '/').replace(/\/$/, '')}/${entry.name}` : entry.name,
      kind: entry.isDir ? 'dir' : 'file', hidden: entry.name.startsWith('.'), ...(stat ? { size: stat.size, mtime: stat.mtimeMs } : {}) })
  }
  return { path: req.path, entries: sortEntries(entries), truncated: raw.length > DIR_LISTING_LIMIT }
}
