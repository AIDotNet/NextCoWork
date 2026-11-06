/**
 * 技能 / 斜杠命令 / 子代理 / 模式:设备上的「可用能力目录」。控制端能看到清单并开关它们,
 * 复用桌面同一套 `ipc/skills.ts` / `commands.ts` / `agents.ts` / `modes.ts`。
 *
 * - 列表按需重扫磁盘(与桌面一致),返回的是设备上的内容;写操作只有「启用/停用」开关。
 * - 不含安装(市场/ZIP):见 shared/device/methods.ts 的说明。
 */
import { realpathSync } from 'node:fs'
import { sep } from 'node:path'
import { z } from 'zod'
import { listAgents, setAgentEnabled } from '../../../main/ipc/agents'
import { listAllCommands, setCommandEnabled } from '../../../main/ipc/commands'
import { listModes } from '../../../main/ipc/modes'
import { listSkills, setSkillGlobalEnabled } from '../../../main/ipc/skills'
import { getHost } from '../../../main/runtime'
import { store } from '../../../main/state/store'
import { DeviceProtocolError } from '../../../shared/device/errors'
import {
  catalogListParams, DEVICE_METHODS, enabledResult, modesListParams, nameSetEnabledParams, skillsSetEnabledParams
} from '../../../shared/device/methods'
import type { DeviceValue } from '../../../shared/device/value'
import type { DeviceRpcRouter } from '../rpc'

const anyValue = z.custom<DeviceValue>(() => true)

/**
 * 目录条目里的 `source` / `path` 是设备上的**绝对路径**(桌面用它「在文件管理器中显示」)。
 * 远程控制端只需要知道「在工作区里的哪个位置 / 是设备数据目录里的」,所以把这两个根下的绝对路径
 * 改写成 `<workspace>/…` 与 `<device-data>/…`,设备上其余目录结构不出设备。
 * (含 realpath 形态:macOS 的 /var 实际是 /private/var,两种写法都要覆盖。)
 */
function prefixes(workspaceId: string | undefined): Array<[string, string]> {
  const found: Array<[string, string]> = []
  const add = (path: string | undefined, label: string): void => {
    if (path === undefined || path === '') return
    found.push([path, label])
    try { const real = realpathSync.native(path); if (real !== path) found.push([real, label]) } catch { /* 目录还不存在。 */ }
  }
  add(workspaceId === undefined ? undefined : store.getWorkspace(workspaceId)?.rootPath, '<workspace>')
  add(getHost().paths.userData(), '<device-data>')
  return found.sort((a, b) => b[0].length - a[0].length)
}

export function scrub(value: unknown, workspaceId: string | undefined): DeviceValue {
  const roots = prefixes(workspaceId)
  const walk = (item: unknown): unknown => {
    if (typeof item === 'string') {
      for (const [root, label] of roots) {
        if (item === root || item.startsWith(root + sep) || item.startsWith(`${root}/`)) return label + item.slice(root.length).replaceAll('\\', '/')
      }
      return item
    }
    if (Array.isArray(item)) return item.map(walk)
    if (item !== null && typeof item === 'object') return Object.fromEntries(Object.entries(item).map(([key, inner]) => [key, walk(inner)]))
    return item
  }
  return walk(JSON.parse(JSON.stringify(value ?? null))) as DeviceValue
}

export function requireWorkspace(workspaceId: string | undefined): void {
  if (workspaceId !== undefined && store.getWorkspace(workspaceId) === undefined) throw new DeviceProtocolError('not_found', 'Workspace does not exist')
}

/** 桌面函数抛带路径/中文细节的普通 Error:统一成固定短句。 */
export async function guarded<T>(work: () => Promise<T> | T): Promise<T> {
  try { return await work() } catch (error) {
    if (error instanceof DeviceProtocolError) throw error
    if (error instanceof Error) throw new DeviceProtocolError('invalid_request', 'Catalogue request was rejected')
    throw error
  }
}

export function registerCatalogMethods(router: DeviceRpcRouter): void {
  router.query(DEVICE_METHODS.skillsList, catalogListParams, anyValue, (_context, p) => {
    requireWorkspace(p.workspaceId)
    return guarded(async () => scrub(await listSkills(p.workspaceId === undefined ? {} : { workspaceId: p.workspaceId }), p.workspaceId))
  })
  router.query(DEVICE_METHODS.commandsList, catalogListParams, anyValue, (_context, p) => {
    requireWorkspace(p.workspaceId)
    return guarded(async () => scrub(await listAllCommands(p.workspaceId === undefined ? {} : { workspaceId: p.workspaceId }), p.workspaceId))
  })
  router.query(DEVICE_METHODS.agentsList, catalogListParams, anyValue, (_context, p) => {
    requireWorkspace(p.workspaceId)
    return guarded(async () => scrub(await listAgents(p.workspaceId === undefined ? {} : { workspaceId: p.workspaceId }), p.workspaceId))
  })
  router.query(DEVICE_METHODS.modesList, modesListParams, anyValue, (_context, p) => {
    requireWorkspace(p.workspaceId)
    return guarded(async () => scrub(await listModes(p), p.workspaceId))
  })

  router.execution(DEVICE_METHODS.skillsSetEnabled, skillsSetEnabledParams, enabledResult,
    (_execution, p) => guarded(() => { setSkillGlobalEnabled(p); return { updated: true as const } }))
  router.execution(DEVICE_METHODS.commandsSetEnabled, nameSetEnabledParams, enabledResult,
    (_execution, p) => guarded(() => { setCommandEnabled(p); return { updated: true as const } }))
  router.execution(DEVICE_METHODS.agentsSetEnabled, nameSetEnabledParams, enabledResult,
    (_execution, p) => guarded(() => { setAgentEnabled(p); return { updated: true as const } }))
}
