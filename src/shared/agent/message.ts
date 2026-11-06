/**
 * 消息是「内容块数组」,不是字符串 —— 方案 §4.1,整个方案里最不能省的一条。
 *
 * 同类产品无一例外都从 content: string 迁移到了块模型,而迁移成本极高
 * (要动持久化格式)。这里从第一天就是块。
 */
import type { AgentError } from './error'
import type { FileReferenceSource } from '../domain/attachment'
import type { ThinkingLevel } from './run-request'
import type { ToolCard } from './tool-card'

export interface AgentMessage {
  id: string
  role: 'user' | 'assistant'
  parts: ContentPart[]
  createdAt: number
  /** 每条记录带版本号 —— 最便宜的保险(方案 §9) */
  schemaVersion: 1
  /** Internal coordination messages are sent to the model but omitted from the chat transcript UI. */
  internal?: boolean
}

/** Durable metadata attached to a Task tool result for UI reconstruction. */
export interface SubagentResult {
  childRunId: string
  status?: 'running' | 'done' | 'error' | 'aborted'
  summary?: string
  color?: import('../domain/agent-def').AgentColor
  /** Terminal error, including localization metadata, retained for UI diagnostics. */
  error?: AgentError
  background?: boolean
  reportStatus?: 'none' | 'pending' | 'injecting' | 'reported' | 'blocked'
}

export type ContentPart =
  | { type: 'text'; text: string }
  | {
    type: 'thinking'
    text: string
    opaque?: unknown
    /**
     * 需求：思考卡片显示「思考了多久」。由 `BlockAccumulator` 按主进程时钟算出，
     * 口径见那边 `ThinkingBlock` 上的注释（含首个增量之前那段隐藏推理的等待）。
     *
     * ★ **只走 UI 轨**：编码器都是按字段挑着读 `text` / `opaque` 的，这两个字段不会上行。
     * 缺席 = 旧转录或导入的会话 —— 界面上就不画时长（没有事实就不画字段），不拿 0 兜底。
     */
    durationMs?: number
    /**
     * 上游**明确报出**的这一块思考的 token 数（`usage.reasoningTokens`，且这次回复里
     * 只有这一块思考、归属无歧义时才写）。
     *
     * ★ 只存真值，不存估算：估算由渲染层按 `text` 现算，这样旧转录也能显示一个约数，
     * 而「真值 / 约数」的区别一直由这个字段在不在来表达 —— 存了估算就分不开了。
     */
    tokens?: number
  }
  | { type: 'tool_call'; callId: string; name: string; input: unknown }
  | { type: 'tool_result'; callId: string; output: ToolOutput; isError: boolean; subagent?: SubagentResult }
  | { type: 'subagent'; callId: string; childRunId: string; summary?: string }
  | { type: 'image'; mime: string; dataRef: string }
  /**
   * 拖入输入框的非图片文件。★ 不落盘、不经过 attachment 表 ——
   * `path` 是用户机器上的真实绝对路径,只给模型的文件读取工具用,
   * 渲染层把它当成一张只读 chip 显示,**不**当文本塞进气泡里
   * (那会把一整条长路径糊进 `<p>`,撑出横向滚动条)。
   */
  | { type: 'file_ref'; path: string; name: string; source?: FileReferenceSource }
  | { type: 'error'; error: AgentError }
  /**
   * 会话目标盖的一个章：设立 / 达成 / 判为不可能 / 已清除。
   *
   * ★★ **只存在于 UI 那一轨**，和 `{ type: 'error' }` 是同一条已验证的路：
   *   编码器对它一律 `return null`，`estimatePart` 算 0。回传给模型的话，它会开始
   *   为自己的历史成绩辩解（「上一轮我没达成，抱歉」），而那既没用又占窗口。
   *
   * ★ 它**必须进转录**：重载之后「这条会话还挂着一个目标吗」只能从这里反扫
   *   （见 `main/goal/restore.ts`）。目标本身是进程内状态，不落盘。
   *
   * ★ 它**追加在已有消息上**，不单独成一条：编码后是 null，单独一条就是一条
   *   零内容块的助手消息，而上游对空 content 是 400。见 `AgentSession.attachGoalStatus`。
   */
  | {
    type: 'goal_status'
    /** Stable marker identity for merging in-run and out-of-band updates. */
    id?: string
    createdAt?: number
    /** Initial activation, distinct from an unsuccessful evaluation. */
    set?: boolean
    origin?: import('../domain/goal').GoalOrigin
    /** 条件达成。 */
    met: boolean
    /** 判定器明确说「永远做不到」。与「这一轮没达成」是两回事。 */
    failed?: boolean
    /** 用户/模型写的条件原文。**领域值，不翻译**。 */
    condition: string
    /** 判定器给的理由。同样是领域值。 */
    reason?: string
    iterations?: number
    durationMs?: number
    tokens?: number
    /** 这一条是「目标已被清除」那种标记。 */
    cleared?: boolean
  }
  /**
   * 压缩边界:它所在的那条消息**之前**的历史不再发给模型。
   *
   * 需求:上下文压缩按 Claude Code 的模型重写 —— 边界是转录里的一条消息,
   * 不是旁边一张检查点表。原先的表要靠 `coveredThroughMessageId` 锚回转录,
   * 删轮、编辑重跑、digest 预算漏读都会让锚点和事实错位(真实症状:压完仍 309K,
   * 随后一路涨到 624K 没再压过)。放进转录后,「哪些历史生效」只剩一个判据:
   * 最后一个边界之后的消息(`shared/agent/compaction.ts` 的 `messagesForModel`)。
   *
   * ★★ **只存在于 UI 那一轨**,同 `goal_status`:编码器 `return null`,估算算 0。
   *   模型看到的是同一条消息里紧跟着的那段 text(续接语 + 摘要 + 重附的文件)。
   *
   * ★ 它所在的消息是 `internal: true` 的 user 消息:聊天界面只画一条分隔线,
   *   不把摘要当成用户说的话。
   */
  | {
    type: 'compact_boundary'
    /** 自动(阈值 / 上游报超长)还是用户 /compact。 */
    trigger: 'auto' | 'manual'
    /** 压缩前的上下文占用(上游真值优先,见 `AgentSession.contextTokens`)。 */
    preTokens: number
    /** 压缩后这条消息自身的估算占用。 */
    postTokens: number
    /** 模型写的摘要(已去掉 `<analysis>`)。UI 展开看的就是它。领域值,不翻译。 */
    summary: string
    /** /compact 后面跟的那段用户补充指令。 */
    instructions?: string
    /** 压缩后重附进上下文的文件路径,按重附顺序。 */
    restoredFiles?: string[]
    /**
     * 写这份摘要的是谁。领域值,**不翻译**(§6.5)。
     *
     * 需求:压缩模型可以单独配置之后,「这份摘要是谁写的」不再等于「会话模型是谁」。
     * 摘要一旦写坏(漏了用户原话、把已做完的事又列成待办),用户唯一能行动的信息
     * 就是这一组 —— 没有它,他只能得出「压缩没用」这个结论。
     *
     * 缺席 = 这条边界是这个字段出现之前压缩出来的,界面上那两处一律不画
     * (不做防御式 UI:没有事实就不画字段)。
     */
    summaryModel?: {
      model: string
      modelProviderId?: string
      /** 实际下发的档位 —— 已按压缩模型归一化过,不是用户配的那个原值。 */
      thinking: ThinkingLevel
      /** true = 配置的压缩模型当时解析不到,回落成了会话模型。 */
      fellBack?: boolean
    }
  }

/** Preserve out-of-band goal markers when an older stream commit arrives later. */
export function mergeGoalStatusMessage(message: AgentMessage, latest?: AgentMessage): AgentMessage {
  if (message.role !== 'assistant' || latest?.role !== 'assistant') return message
  const statuses = new Map<string, Extract<ContentPart, { type: 'goal_status' }>>()
  for (const part of [...latest.parts, ...message.parts]) {
    if (part.type === 'goal_status') statuses.set(part.id ?? JSON.stringify(part), part)
  }
  if (statuses.size === 0) return message
  return {
    ...message,
    parts: [
      ...message.parts.filter((part) => part.type !== 'goal_status'),
      ...[...statuses.values()].sort((a, b) => (a.createdAt ?? 0) - (b.createdAt ?? 0)
        || (a.id ?? '').localeCompare(b.id ?? ''))
    ]
  }
}

/**
 * `file_ref` → 发给模型的那句话。★ 用标准 markdown 链接语法而不是
 * `[附件] path` 这种自造的方括号提示 —— 模型对 `[text](target)` 训练得多,
 * 括号包住路径也让它在正文里视觉上是一个独立单元,不会跟前后的普通文字粘连。
 */
export function fileRefMarkdown(p: { name: string; path: string }): string {
  return `[${p.name}](${p.path})`
}

/**
 * 工具输出在**工具边界**截断并留明确标记 —— 不要让一个返回 40MB 文件的工具
 * 冲垮 IPC 队列(方案 §4.3)。截断发生在产出侧,不在渲染侧。
 */
export interface ToolOutputImage {
  mime: 'image/png' | 'image/jpeg' | 'image/webp' | 'image/gif'
  /**
   * Either an inline data URL or this session's `ncw://` attachment. `generate_image` and
   * `browser_screenshot` store their results as session attachments (falling back to inline when
   * saving fails), and legacy inline images are converted when their page is read
   * (`main/inline-images.ts`). `prepareRequestImages` resolves `ncw://` to data URLs in the
   * outgoing copy only, so encoders still never see `ncw://`.
   */
  dataRef: string
}

/**
 * 一个视频产物。★ 与图片分开而不是复用 `ToolOutputImage`:
 * 视频**从不内联** —— 它只以 `ncw://` 会话附件地址存在(见
 * `main/video-generation/download.ts`)。一个"可能内联也可能不内联"的类型会让
 * 每个消费方都要判断一次,而其中一处判错就是把几百兆塞进 IPC。
 */
export interface ToolOutputVideo {
  /**
   * 永远是 `ncw://attachments/sessions/<会话>/<ULID>.<ext>`,**不可能是 data: 前缀**
   * —— 视频没有"退回内联"那条路(那正是图片那侧能退回的原因,而这里不行)。
   */
  url: string
  mime: string
  size: number
  /** 封面/缩略图地址(上游给的话),同样是 `ncw://` */
  posterUrl?: string
  /** 产出它的后台任务 id —— 卡片据此在工具调用结束后继续跟进状态 */
  jobId?: string
}

export interface ToolOutput {
  content: string
  /** Visual evidence returned by tools such as browser_screenshot. */
  images?: ToolOutputImage[]
  /**
   * 视频产物(目前只有 `generate_video` 会填)。**走 UI 轨,编码器不下发** ——
   * 文本对话的模型看不到视频内容,它只从 `content` 里读那几句说明与地址。
   */
  videos?: ToolOutputVideo[]
  /** 被截断时为 true,UI 据此显示「输出已截断」 */
  truncated?: boolean
  /** 截断前的原始字节数,给 UI 显示「共 N MB」 */
  originalBytes?: number
  /**
   * 只走 UI 轨的自定义卡片 —— **编码器一律不下发给模型**；编码器只读取
   * `output.content` 与显式的 `output.images`。跟着 `output` 一起落盘/恢复。
   */
  card?: ToolCard
  /**
   * `Workflow` 工具的最终节点状态 —— 同 `card`,**只走 UI 轨,编码器不下发**,
   * 跟着 `output` 落盘,画布据此重画。见 `shared/agent/workflow.ts`。
   */
  workflow?: import('./workflow').WorkflowState
}

/**
 * ★ opaque 是厂商透传逃生舱(方案 §4.1)。
 *
 * Anthropic 扩展思考返回的 thinking block 带 signature,下一轮必须原样回传,丢了就报错;
 * 还有 redacted_thinking、缓存断点标记。因为 parts 要落盘,这个字段事后加等于一次数据迁移。
 * 内核不解释它,只负责存下来、原样送回去。
 */

// ─── 便利构造器:全项目只在这里 mint 消息,保证 schemaVersion 不会漏 ───

export function userMessage(id: string, parts: ContentPart[], now: number): AgentMessage {
  return { id, role: 'user', parts, createdAt: now, schemaVersion: 1 }
}

export function assistantMessage(id: string, parts: ContentPart[], now: number): AgentMessage {
  return { id, role: 'assistant', parts, createdAt: now, schemaVersion: 1 }
}

/**
 * 模型看到的历史 = parts 的一个**纯函数投影**;UI 看到的是另一套
 * (含审批提示、进度、子代理折叠节点)。两者是不同的数据,混在一起后患无穷(方案 §4.1)。
 * 这个 helper 是「双轨转录」里 UI 那一轨常用的:取出可显示的纯文本。
 */
export function visibleText(m: AgentMessage): string {
  return m.parts
    .filter((p): p is Extract<ContentPart, { type: 'text' }> => p.type === 'text')
    .map((p) => p.text)
    .join('')
}

/**
 * 中断收尾要用:找出所有已开启但没有配对 tool_result 的 tool_call。
 * 漏掉它们,下一轮的消息数组就是非法的 —— Anthropic 要求每个 tool_use 都必须在
 * 紧随的 user 消息里有配对的 tool_result(方案 §4.8 第 4 步)。
 */
export function orphanedToolCalls(messages: AgentMessage[]): Array<{ callId: string; name: string }> {
  const opened = new Map<string, string>()
  const closed = new Set<string>()
  for (const m of messages) {
    for (const p of m.parts) {
      if (p.type === 'tool_call') opened.set(p.callId, p.name)
      else if (p.type === 'tool_result') closed.add(p.callId)
    }
  }
  return [...opened.entries()]
    .filter(([callId]) => !closed.has(callId))
    .map(([callId, name]) => ({ callId, name }))
}

/**
 * ★ 工具结果放在一条 **user** 消息里,不是接在助手消息后面 —— 方案 §4.1
 * 「内部格式按 Anthropic 形状建模」的字面含义。
 *
 * 这不是风格选择:Anthropic 要求 tool_result 出现在紧随的 user 消息中,
 * 而反方向(存成助手消息的一部分、发请求时再拆出来)意味着编码器要重排消息序列 ——
 * 那是**每加一个上游协议就要重写一遍**的逻辑。存成最终形状,编码器就只是映射。
 */
export function toolResultMessage(id: string, parts: ContentPart[], now: number): AgentMessage {
  return userMessage(id, parts, now)
}

/**
 * 这条 user 消息其实是工具回执,不是用户说的话 —— UI 不能把它画成一个用户气泡
 * (会出现一串空白的「用户发言」)。工具结果的展示走 `TranscriptState.tools`,
 * 那里按 callId 索引,信息还更全(状态、进度、耗时)。
 */
export function isToolResultOnly(m: AgentMessage): boolean {
  return m.role === 'user' && m.parts.length > 0 && m.parts.every((p) => p.type === 'tool_result')
}

/**
 * 工具输出在**产出侧**截断(方案 §4.3)—— 不是渲染侧。
 * 一个 `cat` 掉 40MB 文件的工具,不截断的话这 40MB 要过一次结构化克隆、
 * 一次 IPC、一次 SQLite 写入,还要占着上下文窗口。
 */
export const MAX_TOOL_OUTPUT_CHARS = 64 * 1024

export function truncateToolOutput(content: string): ToolOutput {
  if (content.length <= MAX_TOOL_OUTPUT_CHARS) return { content }
  return {
    // 留明确标记,不是静默截断 —— 模型看到这行才知道自己拿到的是残缺输出,
    // 才可能改用更窄的查询重试。
    content:
      content.slice(0, MAX_TOOL_OUTPUT_CHARS) +
      `\n\n[输出已截断:共 ${content.length} 字符,只保留前 ${MAX_TOOL_OUTPUT_CHARS} 个]`,
    truncated: true,
    originalBytes: content.length
  }
}
