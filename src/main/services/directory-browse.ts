/**
 * 远程目录浏览:给「选一个目录当工作区根」用。**只列子目录的名字**,不读任何文件内容、不列文件。
 *
 * ★ 这是少数不受工作区围栏限制的方法(工作区还没选出来)。收紧的办法是:
 *   - 只返回目录名(与是否隐藏),没有大小、时间、权限、文件;
 *   - 路径必须是绝对路径,规范化后再 realpath,返回的是真实路径(符号链接不能让界面显示成别处);
 *   - 空路径 = 当前服务用户的家目录(起点,不是根盘);
 *   - 数量有上限,权限不足明确报 `forbidden`。
 * 真正创建工作区仍走 `workspaces.create`(设备端再次 realpath 并核对是目录)。
 */
import { readdirSync, realpathSync, statSync } from 'node:fs'
import { homedir } from 'node:os'
import { dirname, isAbsolute, parse, resolve } from 'node:path'
import { DeviceProtocolError } from '../../shared/device/errors'

export const DIRECTORY_LISTING_LIMIT = 2000
// eslint-disable-next-line no-control-regex -- 恰恰是要拒绝控制字符
const CONTROL = /[\u0000-\u001f\u007f]/

export interface DirectoryListing {
  /** 真实(realpath)路径。 */
  path: string
  /** 上一级;已在根时为 null。 */
  parent: string | null
  entries: Array<{ name: string; hidden: boolean }>
  truncated: boolean
}

export function listDirectories(requested: string): DirectoryListing {
  if (requested.length > 4096 || CONTROL.test(requested)) throw new DeviceProtocolError('invalid_request', 'Invalid directory path')
  if (requested !== '' && !isAbsolute(requested)) throw new DeviceProtocolError('invalid_request', 'Directory path must be absolute')
  let real: string
  try {
    real = realpathSync.native(resolve(requested === '' ? homedir() : requested))
    if (!statSync(real).isDirectory()) throw new DeviceProtocolError('invalid_request', 'Not a directory')
  } catch (error) {
    if (error instanceof DeviceProtocolError) throw error
    const code = (error as NodeJS.ErrnoException).code
    throw new DeviceProtocolError(code === 'EACCES' || code === 'EPERM' ? 'forbidden' : 'not_found', 'Directory is not available')
  }
  let raw
  try {
    raw = readdirSync(real, { withFileTypes: true })
  } catch (error) {
    const code = (error as NodeJS.ErrnoException).code
    throw new DeviceProtocolError(code === 'EACCES' || code === 'EPERM' ? 'forbidden' : 'not_found', 'Directory is not available')
  }
  const entries: DirectoryListing['entries'] = []
  for (const entry of raw) {
    let isDir = entry.isDirectory()
    if (!isDir && entry.isSymbolicLink()) {
      // 指向目录的符号链接按目录列出;断链/无权限的直接略过。
      try { isDir = statSync(resolve(real, entry.name)).isDirectory() } catch { isDir = false }
    }
    if (isDir) entries.push({ name: entry.name, hidden: entry.name.startsWith('.') })
  }
  entries.sort((a, b) => a.name.localeCompare(b.name))
  const parent = dirname(real)
  return {
    path: real, parent: parent === real || parse(real).root === real ? null : parent,
    entries: entries.slice(0, DIRECTORY_LISTING_LIMIT), truncated: entries.length > DIRECTORY_LISTING_LIMIT
  }
}
