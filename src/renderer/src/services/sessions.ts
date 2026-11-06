import type { Session, SessionDetail, SessionListItem, SessionPage, SearchHit } from '../../../shared/domain/session'
import type { AgentMessage } from '../../../shared/agent/message'
import type { SessionMode } from '../../../shared/agent/run-request'
import { AgentErrorException, invoke } from './ipc'

export function listSessions(workspaceId: string, archived?: boolean): Promise<SessionListItem[]> {
  return invoke('sessions:list', archived === undefined ? { workspaceId } : { workspaceId, archived })
}

/**
 * ★ 整段历史。渲染层显示转录**不要**用它 —— 用 `getSessionPage`;只读会话元数据用
 * `getSessionSummary`。它留给确实需要整段的地方(以及旧测试)。
 */
export function getSession(sessionId: string): Promise<SessionDetail> {
  return invoke('sessions:get', { sessionId })
}

/** 转录的一页:`beforeMessageId` 之前(缺省 = 最新)的约 `limit` 条,从一轮的开头切起 */
export function getSessionPage(sessionId: string, limit: number, beforeMessageId?: string): Promise<SessionPage> {
  return invoke('sessions:getPage', beforeMessageId === undefined ? { sessionId, limit } : { sessionId, limit, beforeMessageId })
}

/** 会话元数据 + 消息条数。不读任何消息正文 */
export function getSessionSummary(sessionId: string): Promise<{ session: Session; messageCount: number }> {
  return invoke('sessions:getSummary', { sessionId })
}

/** 会话里最后一条助手消息(子代理交差的正文)。不读整段转录 */
export function getLastAssistantMessage(sessionId: string): Promise<AgentMessage | null> {
  return invoke('sessions:lastAssistant', { sessionId })
}

/** 按消息 id 改写转录 —— 主进程在完整历史上改,见 `shared/agent/history-edit.ts` */
export function editSessionMessage(sessionId: string, messageId: string, text: string, truncate: boolean): Promise<void> {
  return invoke('sessions:editMessage', { sessionId, messageId, text, truncate })
}

export function deleteSessionTurn(sessionId: string, userMessageId: string): Promise<void> {
  return invoke('sessions:deleteTurn', { sessionId, userMessageId })
}

export function deleteSessionReply(sessionId: string, fromId: string, toId: string): Promise<void> {
  return invoke('sessions:deleteReply', { sessionId, fromId, toId })
}

export function replaceHistory(sessionId: string, messages: AgentMessage[]): Promise<void> {
  return invoke('sessions:replaceHistory', { sessionId, messages })
}

export function createSession(workspaceId: string, title?: string, sessionId?: string, mode?: SessionMode): Promise<Session> {
  return invoke('sessions:create', {
    workspaceId,
    ...(title === undefined ? {} : { title }),
    ...(sessionId === undefined ? {} : { sessionId }),
    ...(mode === undefined ? {} : { mode })
  })
}

export function setSessionMode(sessionId: string, mode: SessionMode): Promise<void> {
  return invoke('sessions:setMode', { sessionId, mode })
}

/**
 * 记住这条会话选中的模型。★ 别名和供应商**一起**送 —— 只送一半会在会话元数据上
 * 留下「新别名 + 旧供应商」。
 */
export function setSessionModel(sessionId: string, model: string, modelProviderId?: string): Promise<void> {
  return invoke('sessions:setModel', {
    sessionId,
    model,
    ...(modelProviderId === undefined ? {} : { modelProviderId })
  })
}

export function duplicateSession(sessionId: string, title: string): Promise<Session> {
  return invoke('sessions:duplicate', { sessionId, title })
}

export function branchSession(sessionId: string, uptoMessageId: string, title: string): Promise<Session> {
  return invoke('sessions:branch', { sessionId, uptoMessageId, title })
}

export function renameSession(sessionId: string, title: string): Promise<void> {
  return invoke('sessions:rename', { sessionId, title })
}

export function setArchived(sessionId: string, archived: boolean): Promise<void> {
  return invoke('sessions:setArchived', { sessionId, archived })
}

export function setFavorited(sessionId: string, favorited: boolean): Promise<void> {
  return invoke('sessions:setFavorited', { sessionId, favorited })
}

/**
 * 删除会话。待删子树里有真在跑的 Agent 时主进程抛 conflict(见 `isSessionDeleteRunningError`);
 * 用户确认「停止并删除」后带 `stopRunning: true` 再调一次。
 */
export function deleteSession(sessionId: string, options: { stopRunning?: boolean } = {}): Promise<void> {
  return invoke('sessions:delete', options.stopRunning === true ? { sessionId, stopRunning: true } : { sessionId })
}

/** 主进程 `deleteSession` 抛的那条 conflict 的 messageKey —— 与 `main/ipc/sessions.ts` 保持一致 */
const SESSION_DELETE_RUNNING_KEY = 'session.deleteRunningMessage'

/** 删除是不是被「会话里有运行中的 Agent」挡下的(而不是别的失败) */
export function isSessionDeleteRunningError(error: unknown): boolean {
  return error instanceof AgentErrorException
    && error.error.code === 'conflict'
    && error.error.messageKey === SESSION_DELETE_RUNNING_KEY
}

export function searchAll(q: string, workspaceId?: string, limit = 50): Promise<SearchHit[]> {
  return invoke('conversations:searchAll', workspaceId === undefined ? { q, limit } : { q, workspaceId, limit })
}
