/**
 * 需求：外部 HTTP 输入必须先校验，再转换为内部运行请求；不能接受客户端伪造的
 * 子代理关系、内部消息或任意 RunRequest 字段。错误只在 HTTP 边界转成响应。
 */
import { z } from 'zod'
import { PERMISSION_MODES } from '../../shared/agent/permission'
import { THINKING_LEVELS } from '../../shared/agent/run-request'
import { MODE_ID_RE } from '../../shared/domain/mode'

export class HttpError extends Error {
  constructor(readonly status: number, readonly code: string, message: string) {
    super(message)
  }
}

export const identifier = z.string().min(1).max(256)
export const sessionInput = z.object({ workspaceId: identifier, title: z.string().max(256).optional() }).strict()
export const runInput = z.object({
  sessionId: identifier,
  text: z.string().min(1).max(128_000).refine((value) => value.trim().length > 0),
  model: identifier.optional(),
  modelProviderId: identifier.optional(),
  mode: z.string().regex(MODE_ID_RE).optional(),
  thinking: z.enum(THINKING_LEVELS).optional(),
  permissionMode: z.enum(PERMISSION_MODES).optional(),
  webSearch: z.boolean().optional()
}).strict()

// 需求：只开放当前真正可执行的审批动作；allow_edited/session scope 仍是内部预留项。
export const interactionInput = z.discriminatedUnion('kind', [
  z.object({ kind: z.literal('tool_permission'), decision: z.discriminatedUnion('kind', [
    z.object({ kind: z.literal('allow_once') }).strict(),
    z.object({ kind: z.literal('deny'), reason: z.string().max(32768).optional() }).strict(),
    z.object({ kind: z.literal('allow_always'), scope: z.literal('workspace') }).strict()
  ]) }).strict(),
  z.object({ kind: z.literal('ask_user'), answers: z.array(z.array(z.string().min(1).max(32768)).max(100)).max(100).nullable() }).strict(),
  z.object({ kind: z.literal('goal_proposal'), approved: z.boolean() }).strict(),
  z.object({ kind: z.literal('plan_approval'), action: z.enum(['approve_current', 'approve_new_session', 'request_revision', 'reject']), feedback: z.string().max(32768).optional() }).strict()
])

/** 需求：坏字段必须在创建会话/启动 run 之前拒绝，避免失败请求留下半成品。 */
export function parse<T>(schema: z.ZodType<T>, input: unknown): T {
  const result = schema.safeParse(input)
  if (!result.success) throw new HttpError(400, 'invalid_request', result.error.issues.map((issue) => `${issue.path.join('.')}: ${issue.message}`).join('; '))
  return result.data
}
