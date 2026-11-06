/**
 * 「从会话提炼 Skill」的 IPC 封装。
 *
 * 需求:侧边栏右键和 `/skillify` 两个入口都要「开一条提炼会话」,频道字符串只能住在 services 里
 * (AGENTS.md §1)。编排(开 Tab、发首条消息)在 `views/skills/skill-extraction.ts`,这里只拆信封。
 */
import type { Session } from '../../../shared/domain/session'
import { invoke } from './ipc'

/** 失败时抛 `AgentErrorException`;主进程的拒绝原因带 `messageKey`(`skills.extraction.*`)。 */
export function createSkillExtractionSession(sourceSessionId: string, title: string): Promise<Session> {
  return invoke('sessions:createSkillExtraction', { sourceSessionId, title })
}
