/**
 * 「从会话提炼 Skill」的启动编排 —— 侧边栏右键和 `/skillify` 共用这一份。
 *
 * 需求:一键开一条提炼会话(主进程记住源会话)、在新 Tab 里打开、自动发出触发语,
 * 让 agent 立刻开始读摘要、核对代码、写 `.next-cowork/skills/<name>/SKILL.md`。
 * 两个入口各写一遍的话,改一处漏一处的症状是「右键提炼用的是源会话模型,
 * `/skillify` 用的却是工作区默认模型」—— 而用户看不出为什么两次结果不一样。
 *
 * 不变式:
 * - 模型 / 供应商 / 思考档位取**新会话**上的值(主进程从源会话抄过来的),不取药丸的当前值。
 * - 模式固定 `code`:提炼要写文件。权限档位照工作区走 —— 写文件要不要批准由用户的设置决定,
 *   这里不替他放宽。
 * - 这里不弹 toast:失败抛给调用方,用 `skillExtractionErrorKey` 翻成一句话(组件里有 `t`)。
 */
import type { SendOptions } from '../../../../shared/agent/run-request'
import type { Session } from '../../../../shared/domain/session'
import type { Workspace } from '../../../../shared/domain/workspace'
import type { TranslationKey, Translate } from '../../i18n'
import { AgentErrorException } from '../../services/ipc'
import { createSkillExtractionSession } from '../../services/skillify'
import { sessionStore } from '../../stores/session'
import { useTabsStore } from '../../stores/tabs'

/** 提炼会话这一轮的档位。纯函数,单独测。 */
export function skillExtractionSendOptions(workspace: Workspace, session: Session): SendOptions {
  return {
    workspaceId: workspace.id,
    depth: 0,
    mode: 'code',
    thinking: session.thinking,
    webSearch: workspace.settings.webSearch,
    maxContext: workspace.settings.maxContext === true,
    permissionMode: workspace.settings.permissionMode,
    model: session.model,
    ...(session.modelProviderId === undefined ? {} : { modelProviderId: session.modelProviderId }),
    // 同 ChatView 的 `sendOptionsOf`:空清单在主进程一侧意味着「全都要」
    skillIds: workspace.settings.activeSkillIds,
    ...(workspace.settings.skillSelectionMode === undefined ? {} : { skillSelectionMode: workspace.settings.skillSelectionMode })
  }
}

/**
 * 首条用户消息。补充说明(`/skillify 重点记录回滚步骤`)跟在触发语后面,
 * 不进主进程的头块 —— 头块是稳定前缀,混进每次都不一样的内容会让缓存失效。
 */
export function skillExtractionPrompt(t: Translate, sourceTitle: string, hint: string): string {
  const base = t('skillify.startPrompt', { title: sourceTitle })
  const extra = hint.trim()
  return extra === '' ? base : `${base}\n\n${t('skillify.hintPrefix', { hint: extra })}`
}

export async function startSkillExtraction({
  workspace,
  sourceSessionId,
  sourceTitle,
  hint,
  t
}: {
  workspace: Workspace
  sourceSessionId: string
  sourceTitle: string
  /** `/skillify` 后面跟的补充说明;侧边栏入口传空串 */
  hint: string
  t: Translate
}): Promise<void> {
  const session = await createSkillExtractionSession(sourceSessionId, t('skillify.sessionTitle', { title: sourceTitle }))
  useTabsStore.getState().openSession(workspace.id, session.id, session.title)
  await sessionStore(session.id).getState().send(
    skillExtractionPrompt(t, sourceTitle, hint),
    skillExtractionSendOptions(workspace, session)
  )
}

/** 主进程带回来的拒绝原因 → 文案 key。白名单收窄,认不出的落到通用文案(同 `skill-error.ts`)。 */
const KNOWN_ERRORS = [
  'skills.extraction.sourceMissing',
  'skills.extraction.sourceEmpty',
  'skills.extraction.sourceRunning',
  'skills.extraction.nested'
] as const

export function skillExtractionErrorKey(error: unknown): TranslationKey {
  const key = error instanceof AgentErrorException ? error.error.messageKey : undefined
  return KNOWN_ERRORS.find((known) => known === key) ?? 'skills.operationFailed'
}
