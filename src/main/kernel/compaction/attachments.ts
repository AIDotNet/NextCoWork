/**
 * 压缩后重附的上下文 —— Claude Code 的 post-compact attachments。
 *
 * 需求:摘要写的是「做过什么」,不是「文件现在长什么样」。压完之后模型手里如果
 * 只剩摘要,它的第一个动作几乎一定是把刚才在改的那几个文件重新 Read 一遍 ——
 * 白白多一轮往返,而且在那之前它会基于摘要里的旧片段去 Edit,old_string 对不上。
 * CC 的做法是压完立刻把最近碰过的文件**从磁盘重新读一遍**附上,连同待办清单、
 * 用过的技能正文。这里照搬这三样和它的预算。
 *
 * 不变式:
 * - 文件内容**现读**,不从转录里抄。转录里的是当时的快照,之后可能已被 Edit 过 ——
 *   抄旧快照会让模型以为文件还是旧样子。
 * - 读文件走调用方给的 `readFile`(session 传的是工具宿主的 fs):本地和远端工作区
 *   同一条路,这个模块不知道也不该知道文件在哪台机器上。
 * - 读失败(被删、无权限、二进制)静默跳过:重附是锦上添花,不能让压缩因此失败。
 *
 * 故意不做的:不重附 MCP 资源、不重附计划文件、不重附图片 —— CC 有其中几样,
 * 但我们的内核没有对应的「已读状态」可取,硬凑只会附上错的东西。
 */
import type { AgentMessage } from '../../../shared/agent/message'
import { latestTodosFrom, MARK } from '../../../shared/agent/todo'
import { estimateTokens } from '../context-assembler'
import type { KernelFs } from '../host'
import { looksBinary } from '../tool/builtin/paths'

/** 最多重附几个文件。同 CC 的 `POST_COMPACT_MAX_FILES_TO_RESTORE`。 */
export const POST_COMPACT_MAX_FILES = 5
/** 重附文件的总预算。同 CC 的 `POST_COMPACT_TOKEN_BUDGET`。 */
export const POST_COMPACT_FILE_BUDGET = 50_000
/** 单个文件的上限,超过就截断。同 CC 的 `POST_COMPACT_MAX_TOKENS_PER_FILE`。 */
export const POST_COMPACT_MAX_TOKENS_PER_FILE = 5_000
/** 单个技能正文的上限。同 CC 的 `POST_COMPACT_MAX_TOKENS_PER_SKILL`。 */
export const POST_COMPACT_MAX_TOKENS_PER_SKILL = 5_000
/** 技能正文的总预算。同 CC 的 `POST_COMPACT_SKILLS_TOKEN_BUDGET`。 */
export const POST_COMPACT_SKILL_BUDGET = 25_000

/**
 * 超过这个字节数的文件不重附。
 *
 * ★ 看起来多余(反正会截到 5K token),但截断发生在**读完之后**:一个模型读过
 * 前 200 行的 80MB 日志,不设这道闸就会在压缩时被整个读进主进程再扔掉 99.9%。
 * 1MB 远大于 5K token 对应的正文,不会误伤正常源码文件。
 */
export const POST_COMPACT_MAX_FILE_BYTES = 1024 * 1024
const SNIFF_BYTES = 4096

/**
 * 按绝对路径读一个可重附的文本文件;不存在 / 目录 / 过大 / 二进制都返回 undefined。
 *
 * 路径解析由调用方做(session 走工具那条 `resolvePath`,手动 /compact 走
 * `resolveAnywhere`)—— 同 `KernelFs` 的约定:端口只收绝对路径。
 */
export async function readAttachableFile(fs: KernelFs, absPath: string): Promise<string | undefined> {
  if (!(await fs.exists(absPath))) return undefined
  const st = await fs.stat(absPath)
  if (st.isDir || st.size > POST_COMPACT_MAX_FILE_BYTES) return undefined
  if (looksBinary(await fs.readFileBytes(absPath, SNIFF_BYTES))) return undefined
  return fs.readFile(absPath)
}

/** 工具的**外部名**(转录里 tool_call.name 存的是它)。由 session 按 internalId 反查后传入。 */
export interface AttachmentToolNames {
  /** Read / Write / Edit 的外部名 —— 「碰过的文件」从这三种调用里取。 */
  file: ReadonlySet<string>
  todo?: string
  skill?: string
}

export interface PostCompactAttachments {
  /** 每一段都是一个独立的 text 块,追加在摘要之后。 */
  texts: string[]
  restoredFiles: string[]
}

/**
 * 从压缩前的对话里找最近碰过的文件,**新的在前**,去重。
 *
 * ★ 只认成功的调用:失败的 Read(文件不存在)重附一次只会再失败一次。
 */
export function recentFilePaths(messages: readonly AgentMessage[], fileTools: ReadonlySet<string>): string[] {
  const failed = new Set<string>()
  for (const message of messages) {
    for (const part of message.parts) {
      if (part.type === 'tool_result' && part.isError) failed.add(part.callId)
    }
  }
  const seen = new Set<string>()
  const out: string[] = []
  for (let i = messages.length - 1; i >= 0; i--) {
    const parts = messages[i]?.parts ?? []
    for (let j = parts.length - 1; j >= 0; j--) {
      const part = parts[j]
      if (part?.type !== 'tool_call' || !fileTools.has(part.name) || failed.has(part.callId)) continue
      const path = filePathOf(part.input)
      if (path === undefined || seen.has(path)) continue
      seen.add(path)
      out.push(path)
    }
  }
  return out
}

function filePathOf(input: unknown): string | undefined {
  if (typeof input !== 'object' || input === null) return undefined
  const value = (input as { file_path?: unknown }).file_path
  return typeof value === 'string' && value !== '' ? value : undefined
}

/** 按估算 token 截断到上限,截断处留一句说明 —— 让模型知道要看全文得自己 Read。 */
export function truncateToTokens(text: string, maxTokens: number): string {
  if (estimateTokens(text) <= maxTokens) return text
  // 二分找最长的前缀:估算对 CJK 和拉丁字符的系数不同,不能直接按字符比例切。
  let lo = 0
  let hi = text.length
  while (lo < hi) {
    const mid = Math.ceil((lo + hi) / 2)
    if (estimateTokens(text.slice(0, mid)) <= maxTokens) lo = mid
    else hi = mid - 1
  }
  return `${text.slice(0, lo)}\n\n[... truncated for context. Use the Read tool to see the rest of the file.]`
}

/** 最后一次成功调用每个技能时拿到的正文,新的在前。 */
function invokedSkills(messages: readonly AgentMessage[], skillTool: string): Array<{ name: string; body: string }> {
  const names = new Map<string, string>()
  for (const message of messages) {
    for (const part of message.parts) {
      if (part.type !== 'tool_call' || part.name !== skillTool) continue
      const input = part.input as { name?: unknown } | null
      if (typeof input?.name === 'string') names.set(part.callId, input.name)
    }
  }
  const seen = new Set<string>()
  const out: Array<{ name: string; body: string }> = []
  for (let i = messages.length - 1; i >= 0; i--) {
    for (const part of messages[i]?.parts ?? []) {
      if (part.type !== 'tool_result' || part.isError) continue
      const name = names.get(part.callId)
      if (name === undefined || seen.has(name)) continue
      seen.add(name)
      out.push({ name, body: part.output.content })
    }
  }
  return out
}

/**
 * 组装重附内容。`readFile` 返回 undefined = 读不到,跳过。
 *
 * 顺序照 CC:文件 → 待办 → 技能。文件在前,是因为模型压完后最先要用的就是它们。
 */
export async function buildPostCompactAttachments(input: {
  messages: readonly AgentMessage[]
  tools: AttachmentToolNames
  readFile: (path: string) => Promise<string | undefined>
  signal?: AbortSignal
}): Promise<PostCompactAttachments> {
  const texts: string[] = []
  const restoredFiles: string[] = []

  let fileBudget = POST_COMPACT_FILE_BUDGET
  for (const path of recentFilePaths(input.messages, input.tools.file)) {
    if (restoredFiles.length >= POST_COMPACT_MAX_FILES || fileBudget <= 0) break
    if (input.signal?.aborted === true) break
    let content: string | undefined
    try {
      content = await input.readFile(path)
    } catch {
      // 需求:重附失败不能让整次压缩失败 —— 见文件头「读失败静默跳过」。
      content = undefined
    }
    if (content === undefined) continue
    const body = truncateToTokens(content, Math.min(POST_COMPACT_MAX_TOKENS_PER_FILE, fileBudget))
    fileBudget -= estimateTokens(body)
    restoredFiles.push(path)
    texts.push(
      `<system-reminder>\nCalled the Read tool with the following input: {"file_path":${JSON.stringify(path)}}\n` +
      `Result of calling the Read tool (re-read from disk after compaction; this is the current content):\n${body}\n</system-reminder>`
    )
  }

  if (input.tools.todo !== undefined) {
    const todos = latestTodosFrom(input.messages, input.tools.todo)
    if (todos !== undefined && todos.length > 0) {
      const list = todos.map((todo) => `${MARK[todo.status]} ${todo.content}`).join('\n')
      texts.push(
        `<system-reminder>\nYour todo list before the conversation was compacted (keep using ${input.tools.todo} to update it; ` +
        `always send the whole list):\n${list}\n</system-reminder>`
      )
    }
  }

  if (input.tools.skill !== undefined) {
    let skillBudget = POST_COMPACT_SKILL_BUDGET
    for (const skill of invokedSkills(input.messages, input.tools.skill)) {
      if (skillBudget <= 0) break
      const body = truncateToTokens(skill.body, Math.min(POST_COMPACT_MAX_TOKENS_PER_SKILL, skillBudget))
      skillBudget -= estimateTokens(body)
      texts.push(
        `<system-reminder>\nThe Skill "${skill.name}" was loaded earlier in this conversation. Its instructions still apply:\n${body}\n</system-reminder>`
      )
    }
  }

  return { texts, restoredFiles }
}
