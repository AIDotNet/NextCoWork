/**
 * 思考卡片「想了多久、想了多少」的读数 —— 纯函数,不读时钟。
 *
 * 需求:思考卡片标题行显示思考用时与思考正文的 token 数。已提交的块和还在流的块
 * 数据来源不同(落盘的 `durationMs` / `tokens` 对 reducer 打的 `startedAt` / `endedAt`),
 * 这个文件把两者收成同一个读数,让「流式中」和「已提交」走同一条换算 ——
 * 否则提交那一瞬间数字会跳(`views/chat/parts.tsx` 文件头讲的就是这件事)。
 *
 * 放 shared 而不是组件里:`TimelineItem`(shared/domain)要带这个形状,而且这几条
 * 取舍(何时算约数、何时不画)值得单测锁死。
 *
 * 故意不做:按长度把一次回复的 `reasoningTokens` 摊给多块思考 —— 见 block-accumulator。
 */
import { elapsedOf } from './duration'
import type { ContentPart } from './message'
import { estimateTokens } from './token-estimate'
import type { LiveBlock } from './transcript'

export interface ThinkingStats {
  /** 已提交块:主进程算好落盘的用时。 */
  durationMs?: number
  /** 流式块:起点与最近一次思考增量(口径见 `LiveBlock.startedAt`)。 */
  startedAt?: number
  endedAt?: number
  /** 上游报告的真值;缺席时读数退回按正文估算。 */
  tokens?: number
}

export function thinkingStatsOfPart(part: Extract<ContentPart, { type: 'thinking' }>): ThinkingStats {
  return {
    ...(part.durationMs === undefined ? {} : { durationMs: part.durationMs }),
    ...(part.tokens === undefined ? {} : { tokens: part.tokens })
  }
}

export function thinkingStatsOfLive(block: LiveBlock): ThinkingStats {
  return {
    ...(block.startedAt === undefined ? {} : { startedAt: block.startedAt }),
    ...(block.endedAt === undefined ? {} : { endedAt: block.endedAt })
  }
}

/**
 * 思考用时(毫秒);没有事实时返回 `undefined`,界面据此不画(旧转录、导入的会话)。
 *
 * `streaming` = 这块此刻还在长:按 `now` 实时走表。不在长了(正文已经开始)就定格在
 * 最后一个思考增量上 —— ★ 不能继续用 `now`,否则正文输出期间思考时长还在涨,
 * 提交那一刻再跳回真值。
 */
export function thinkingElapsedMs(stats: ThinkingStats, streaming: boolean, now: number): number | undefined {
  if (stats.durationMs !== undefined) return Math.max(0, stats.durationMs)
  if (stats.startedAt === undefined) return undefined
  return elapsedOf(
    streaming || stats.endedAt === undefined
      ? { startedAt: stats.startedAt }
      : { startedAt: stats.startedAt, endedAt: stats.endedAt },
    now
  )
}

export interface ThinkingTokenReading {
  count: number
  /** true = 按正文估算的约数,界面必须标「≈」 */
  estimated: boolean
}

/**
 * 思考 token 读数。上游真值优先;没有就按正文估算(与上下文估算同一个函数)。
 * 正文为空(redacted / 只有密文的推理)时不给读数 —— 估出一个 0 比不画更误导。
 */
export function thinkingTokenReading(text: string, stats: ThinkingStats): ThinkingTokenReading | undefined {
  if (stats.tokens !== undefined) return { count: stats.tokens, estimated: false }
  if (text.trim() === '') return undefined
  return { count: estimateTokens(text), estimated: true }
}
