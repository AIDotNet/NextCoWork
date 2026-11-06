/**
 * 钩子:设备上的「在固定时机跑一条命令/一次模型判定」的配置。
 *
 * 网络上只开放**列出**与**启停**。新建/编辑/删除/试运行不开放:钩子命令在设备上由 shell 执行,
 * 允许控制端写入命令 = 允许它在设备上执行任意命令,这要有单独的确认流程,不在这一批里。
 * 启停只翻转一个已有条目的开关(可逆),不改变它的内容。
 */
import { z } from 'zod'
import { listHooks, setHookEnabledIpc } from '../../../main/ipc/hooks'
import { DeviceProtocolError } from '../../../shared/device/errors'
import { catalogListParams, DEVICE_METHODS, enabledResult, hooksSetEnabledParams } from '../../../shared/device/methods'
import type { DeviceValue } from '../../../shared/device/value'
import type { DeviceRpcRouter } from '../rpc'
import { guarded, requireWorkspace, scrub } from './catalog'

const anyValue = z.custom<DeviceValue>(() => true)

export function registerHookMethods(router: DeviceRpcRouter): void {
  router.query(DEVICE_METHODS.hooksList, catalogListParams, anyValue, (_context, p) => {
    requireWorkspace(p.workspaceId)
    return guarded(async () => scrub(await listHooks(p.workspaceId === undefined ? {} : { workspaceId: p.workspaceId }), p.workspaceId))
  })

  router.execution(DEVICE_METHODS.hooksSetEnabled, hooksSetEnabledParams, enabledResult, (_execution, p) => {
    if (p.scope === 'project' && p.workspaceId === undefined) throw new DeviceProtocolError('invalid_request', 'Project hooks need a workspace')
    requireWorkspace(p.workspaceId)
    return guarded(async () => {
      const known = (await listHooks(p.workspaceId === undefined ? {} : { workspaceId: p.workspaceId }))
        .some((hook) => hook.id === p.id && hook.scope === p.scope)
      if (!known) throw new DeviceProtocolError('not_found', 'Hook does not exist')
      await setHookEnabledIpc({ scope: p.scope, id: p.id, enabled: p.enabled, ...(p.workspaceId !== undefined && { workspaceId: p.workspaceId }) })
      return { updated: true as const }
    })
  })
}
