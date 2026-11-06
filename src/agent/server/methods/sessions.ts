/**
 * 会话:元数据写入(改名、归档、收藏、模式、模型)与元数据读取(摘要、最后一条助手消息)。
 * 写入复用桌面同一套逻辑(`services/session-metadata.ts`);读取是纯库查询,与桌面逐字段一致。
 *
 * 会修改的会话必须存在,且不能是子代理会话(`parentSessionId`)—— 那些不是用户的对话,不该被控制端改。
 * 不含删除(见 `shared/device/methods.ts` 的说明)。
 */
import { z } from 'zod'
import { DeviceProtocolError } from '../../../shared/device/errors'
import {
  DEVICE_METHODS, sessionsIdParams, sessionsRenameParams, sessionsSetArchivedParams, sessionsSetFavoritedParams, sessionsSetModeParams,
  sessionsSetModelParams, sessionsUpdatedResult
} from '../../../shared/device/methods'
import type { DeviceValue } from '../../../shared/device/value'
import { normalizeModeId } from '../../../shared/domain/mode'
import { renameSession, setArchived, setFavorited, setMode, setModel } from '../../../main/services/session-metadata'
import { store } from '../../../main/state/store'
import type { DeviceRpcRouter } from '../rpc'

const anyValue = z.custom<DeviceValue>(() => true)
const plain = (value: unknown): DeviceValue => JSON.parse(JSON.stringify(value ?? null)) as DeviceValue

function requireUserSession(sessionId: string): void {
  const session = store.getSession(sessionId)
  if (session === undefined || session.parentSessionId !== undefined) throw new DeviceProtocolError('not_found', 'Session does not exist')
}

export function registerSessionMetadataMethods(router: DeviceRpcRouter): void {
  const updated = { updated: true as const }
  router.execution(DEVICE_METHODS.sessionsRename, sessionsRenameParams, sessionsUpdatedResult, (_execution, p) => {
    requireUserSession(p.sessionId)
    const title = p.title.trim()
    if (title === '') throw new DeviceProtocolError('invalid_request', 'Title must not be blank')
    renameSession({ sessionId: p.sessionId, title })
    return updated
  })
  router.execution(DEVICE_METHODS.sessionsSetArchived, sessionsSetArchivedParams, sessionsUpdatedResult, (_execution, p) => {
    requireUserSession(p.sessionId)
    setArchived(p)
    return updated
  })
  router.execution(DEVICE_METHODS.sessionsSetFavorited, sessionsSetFavoritedParams, sessionsUpdatedResult, (_execution, p) => {
    requireUserSession(p.sessionId)
    setFavorited(p)
    return updated
  })
  router.execution(DEVICE_METHODS.sessionsSetMode, sessionsSetModeParams, sessionsUpdatedResult, (_execution, p) => {
    requireUserSession(p.sessionId)
    setMode({ sessionId: p.sessionId, mode: normalizeModeId(p.mode) })
    return updated
  })
  router.execution(DEVICE_METHODS.sessionsSetModel, sessionsSetModelParams, sessionsUpdatedResult, (_execution, p) => {
    requireUserSession(p.sessionId)
    // 别名与供应商必须成对存在:指到不存在的组合会让下一轮 run 才报错,所以这里就拒绝。
    const known = store.listAliases().some((alias) => alias.alias === p.model && (p.modelProviderId === undefined || alias.providerId === p.modelProviderId))
    if (!known) throw new DeviceProtocolError('not_found', 'Model does not exist')
    setModel(p)
    return updated
  })

  // 元数据读取。摘要 = 会话记录 + 条数(不读正文);最后一条助手消息给后台子代理交差那条链路用。
  router.query(DEVICE_METHODS.sessionsSummary, sessionsIdParams, anyValue, (_context, p) => {
    const summary = store.getSessionSummary(p.sessionId)
    if (summary === undefined) throw new DeviceProtocolError('not_found', 'Session does not exist')
    return plain(summary)
  })
  router.query(DEVICE_METHODS.sessionsLastAssistant, sessionsIdParams, anyValue, (_context, p) => {
    // 会话不存在与「没有助手消息」是两回事:前者报 not_found,后者如实回 null。
    if (store.getSession(p.sessionId) === undefined) throw new DeviceProtocolError('not_found', 'Session does not exist')
    return plain(store.lastAssistantMessage(p.sessionId) ?? null)
  })
}
