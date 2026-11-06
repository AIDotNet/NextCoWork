/**
 * 会话元数据的写入(改名/归档/收藏/模式/模型):桌面 IPC 与独立设备服务共用,无 Electron。
 * 从 `ipc/sessions.ts` 原样搬出 —— 那个文件整体拉着附件/目标等依赖,设备服务不能 import 它。
 */
import type { SessionMode } from '../../shared/agent/run-request'
import type { SessionChange } from '../../shared/domain/session'
import { normalizeModeId } from '../../shared/domain/mode'
import { store } from '../state/store'
import { windows } from '../window/registry'

function changed(event: SessionChange): void {
  windows.emitToAll('sessions:changed', event)
}

export function setMode(req: { sessionId: string; mode: SessionMode }): void {
  const session = store.getSession(req.sessionId)
  if (session === undefined) return
  const mode = normalizeModeId(req.mode)
  if (session.mode === mode) return
  store.putSession({ ...session, mode, updatedAt: Date.now() })
  changed({ kind: 'metadata', sessionIds: [session.id], workspaceId: session.workspaceId })
}

/**
 * 记住这条会话选中的模型。
 *
 * ★ 不动 `updatedAt`:换个模型不是「这条对话有了新进展」,碰它会让侧边栏的
 *   最近顺序因为一次纯 UI 操作而跳动。
 * ★ `modelProviderId` **无条件写**,不能条件展开 —— 别名换了而供应商没跟着换,
 *   留下的就是「新别名 + 旧供应商」这个谁也没配过的组合(同 runtime.ts 那处)。
 */
export function setModel(req: { sessionId: string; model: string; modelProviderId?: string }): void {
  const session = store.getSession(req.sessionId)
  if (session === undefined) return
  if (session.model === req.model && session.modelProviderId === req.modelProviderId) return
  store.putSession({ ...session, model: req.model, modelProviderId: req.modelProviderId })
  changed({ kind: 'metadata', sessionIds: [session.id], workspaceId: session.workspaceId })
}

export function renameSession(req: { sessionId: string; title: string }): void {
  store.tx(() => {
    store.renameSession(req.sessionId, req.title)
    /*
      ★ 用户改过的标题归用户,但这**不是**脱离同步 —— 改个名字不等于编辑转录,
      源侧后续的消息照样该同步进来。所以这里只打一个覆盖标记,让下一轮
      不要用源侧标题把它盖回去。
    */
    for (const mapping of store.findImportMappingsByTarget(req.sessionId, 'session')) {
      store.putImportMapping({
        ...mapping,
        meta: { ...mapping.meta, titleOverridden: true },
        updatedAt: Date.now()
      })
    }
  })
  const session = store.getSession(req.sessionId)!
  windows.emitToAll('sessions:changed', {
    kind: 'metadata', sessionIds: [session.id],
    workspaceId: session.workspaceId, renamed: { sessionId: session.id, title: session.title }
  })
}

export function setArchived(req: { sessionId: string; archived: boolean }): void {
  store.setSessionArchived(req.sessionId, req.archived)
  changed({ kind: 'metadata', sessionIds: [req.sessionId], workspaceId: store.getSession(req.sessionId)?.workspaceId })
}

export function setFavorited(req: { sessionId: string; favorited: boolean }): void {
  store.setSessionFavorited(req.sessionId, req.favorited)
  changed({ kind: 'metadata', sessionIds: [req.sessionId], workspaceId: store.getSession(req.sessionId)?.workspaceId })
}
