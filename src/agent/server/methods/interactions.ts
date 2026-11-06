/**
 * 审批 / 提问 / 计划确认 / 目标提议的远程应答。
 *
 * 权威待决状态在内核的 `InteractionGate`(主进程内存,随 run 存亡);这里只是把它
 * 暴露给已认证控制端。★ 一次性由闸门自己保证:`respond` 同步删除待决项,
 * 两个控制端同时应答,只有第一个成功,第二个得到 `conflict` —— 不会重复裁决,
 * 也不会让后到的覆盖先到的。
 */
import { z } from 'zod'
import { interactions } from '../../../main/kernel/interaction-gate'
import { descendantRunIds, runHandle } from '../../../main/services/agent'
import type { InteractionResponse } from '../../../shared/agent/interaction'
import { DeviceProtocolError } from '../../../shared/device/errors'
import {
  DEVICE_METHODS, interactionsListParams, interactionsRespondParams, interactionsRespondResult
} from '../../../shared/device/methods'
import type { DeviceValue } from '../../../shared/device/value'
import type { DeviceRpcRouter } from '../rpc'

const anyValue = z.custom<DeviceValue>(() => true)

export function registerInteractionMethods(router: DeviceRpcRouter): void {
  router.query(DEVICE_METHODS.interactionsList, interactionsListParams, anyValue, (_context, params) => {
    let allowed: Set<string> | undefined
    if (params.runId !== undefined) {
      const handle = runHandle(params.runId)
      if (handle === undefined) throw new DeviceProtocolError('not_found', 'Run does not exist')
      allowed = new Set([handle.runId, ...descendantRunIds(handle)])
    }
    const pending = interactions.list().filter((item) => allowed === undefined || allowed.has(item.runId))
    return JSON.parse(JSON.stringify(pending)) as DeviceValue
  })

  router.execution(DEVICE_METHODS.interactionsRespond, interactionsRespondParams, interactionsRespondResult, (_execution, { response }) => {
    if (interactions.get(response.id) === undefined) {
      // 已被另一个控制端/窗口裁决,或 run 已结束:对后到者是冲突,不是成功。
      throw new DeviceProtocolError('conflict', 'Interaction is no longer pending')
    }
    try {
      interactions.respond(response as InteractionResponse)
    } catch (error) {
      const message = error instanceof Error ? error.message : ''
      throw new DeviceProtocolError(message === 'Invalid interaction response' ? 'invalid_request' : 'conflict',
        message === 'Invalid interaction response' ? 'Invalid interaction response' : 'Interaction is no longer pending')
    }
    return { answered: true as const }
  })
}
