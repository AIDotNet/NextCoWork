/**
 * 灰字的状态机。纯函数:`reduceGhost(状态, 动作) → { 状态, 事件 }`,事件只有「被无视了」一种,
 * 交给退避计数。显示与否由 `visibleGhost` 一处判定。
 *
 * ★ 只有**存下来**的两种来源(L2 建议、L3 续写)住在这里 —— 它们是异步到的,要跟打、要记 Esc。
 *   L1 历史和热文件每次按键现算(几百次前缀比较),不需要状态,由调用方作为 `local` 递进来。
 */
import { remainderOf, type Ghost, type GhostSource } from './ghost'

export interface GhostState {
  /** L2:回合结束后的建议 */
  suggest: Ghost | null
  /** L2 按过 Esc 的那一轮:这一轮里再也不出现,哪怕用户删回空草稿 */
  dismissedTrigger: number | undefined
  /** L3:打字续写 */
  inline: Ghost | null
  /** 按过 Esc 的其它灰字(按 key);草稿清空或发送后忘掉 */
  dismissed: readonly string[]
  composing: boolean
  /** 刚粘贴完:等下一次真正的按键再出灰字(粘贴不是「打字停顿」) */
  pasteHold: boolean
  caretAtEnd: boolean
  focused: boolean
}

export const INITIAL_GHOST_STATE: GhostState = {
  suggest: null,
  dismissedTrigger: undefined,
  inline: null,
  dismissed: [],
  composing: false,
  pasteHold: false,
  caretAtEnd: true,
  focused: false
}

export type GhostAction =
  | { type: 'input'; text: string; caret: number | null }
  | { type: 'caret'; text: string; caret: number | null }
  | { type: 'composing'; value: boolean }
  | { type: 'focus'; value: boolean }
  | { type: 'paste' }
  /** 一次会产生字符的按键(粘贴保持期到此为止) */
  | { type: 'keydown' }
  | { type: 'suggestion'; trigger: number; text: string; draft: string }
  | { type: 'inline'; requestId: string; base: string; text: string; draft: string }
  | { type: 'accept'; ghost: Ghost; partial: boolean }
  | { type: 'dismiss'; ghost: Ghost }
  /** 开始运行 / 发送了:存着的灰字全部作废 */
  | { type: 'reset'; reason: 'running' | 'submit' }

export interface GhostEvent {
  kind: 'ignored'
  source: GhostSource
}

export function reduceGhost(s: GhostState, a: GhostAction): { state: GhostState; events: GhostEvent[] } {
  const events: GhostEvent[] = []
  switch (a.type) {
    case 'input': {
      const caretAtEnd = a.caret !== null && a.caret === a.text.length
      // ★ 组词中的草稿是拼音,拿它跟打会把灰字当场判成「对不上」清掉 —— 等 compositionend 再说
      if (s.composing) return { state: { ...s, caretAtEnd }, events }
      let { suggest, inline } = s
      if (suggest !== null && remainderOf(suggest, a.text) === null) {
        events.push({ kind: 'ignored', source: 'suggest' })
        suggest = null
      }
      if (inline !== null && remainderOf(inline, a.text) === null) {
        events.push({ kind: 'ignored', source: 'inline' })
        inline = null
      }
      const dismissed = a.text.trim() === '' ? [] : s.dismissed
      return { state: { ...s, suggest, inline, dismissed, caretAtEnd }, events }
    }
    case 'caret':
      if (s.composing) return { state: s, events }
      return { state: { ...s, caretAtEnd: a.caret !== null && a.caret === a.text.length }, events }
    case 'composing':
      return { state: { ...s, composing: a.value }, events }
    case 'focus':
      return { state: { ...s, focused: a.value }, events }
    case 'paste':
      return { state: { ...s, pasteHold: true }, events }
    case 'keydown':
      return s.pasteHold ? { state: { ...s, pasteHold: false }, events } : { state: s, events }
    case 'suggestion': {
      if (a.text === '' || a.trigger === s.dismissedTrigger) return { state: s, events }
      const ghost: Ghost = { source: 'suggest', base: '', text: a.text, key: `suggest:${String(a.trigger)}` }
      // 结果到的时候用户已经开始打了别的 → 这条已经没用了
      if (a.draft !== '' && remainderOf(ghost, a.draft) === null) return { state: s, events }
      return { state: { ...s, suggest: ghost }, events }
    }
    case 'inline': {
      if (a.text === '') return { state: s, events }
      const ghost: Ghost = { source: 'inline', base: a.base, text: a.text, key: `inline:${a.requestId}` }
      if (remainderOf(ghost, a.draft) === null) return { state: s, events }
      return { state: { ...s, inline: ghost }, events }
    }
    case 'accept': {
      if (a.partial) return { state: s, events }
      return {
        state: {
          ...s,
          suggest: s.suggest?.key === a.ghost.key ? null : s.suggest,
          inline: s.inline?.key === a.ghost.key ? null : s.inline
        },
        events
      }
    }
    case 'dismiss': {
      events.push({ kind: 'ignored', source: a.ghost.source })
      if (a.ghost.source === 'suggest') {
        const trigger = Number(a.ghost.key.slice('suggest:'.length))
        return { state: { ...s, suggest: null, dismissedTrigger: Number.isFinite(trigger) ? trigger : s.dismissedTrigger }, events }
      }
      return {
        state: {
          ...s,
          inline: a.ghost.source === 'inline' ? null : s.inline,
          dismissed: s.dismissed.includes(a.ghost.key) ? s.dismissed : [...s.dismissed, a.ghost.key]
        },
        events
      }
    }
    case 'reset': {
      // 发送时建议还挂着 = 用户发了别的,这条被无视了
      if (a.reason === 'submit' && s.suggest !== null) events.push({ kind: 'ignored', source: 'suggest' })
      if (a.reason === 'submit' && s.inline !== null) events.push({ kind: 'ignored', source: 'inline' })
      return { state: { ...s, suggest: null, inline: null, dismissed: [], pasteHold: false }, events }
    }
  }
}

export interface GhostView {
  ghost: Ghost
  /** 还没接受的原文 */
  remainder: string
}

/**
 * 此刻画哪一条灰字(或者一条都不画)。
 *
 * ★ 显示条件全在这一处:有焦点、光标折叠在末尾、不在组词、没在粘贴保持期、没有弹层、
 *   草稿不以换行结尾(`renderDraft` 会补一个 `<br>`,`::after` 会掉到光标**下面**一行)。
 * ★ 优先级:建议 > 历史 > 文件 > 续写。建议只在托盘里没有附件时出现 ——
 *   拖进一张图之后空草稿 + Enter 的意思是「发这张图」,那一刻的灰字只会误导。
 */
export function visibleGhost(s: GhostState, ctx: {
  draft: string
  popupOpen: boolean
  attachments: number
  local: { history: Ghost | null; file: Ghost | null }
}): GhostView | null {
  if (!s.focused || !s.caretAtEnd || s.composing || s.pasteHold || ctx.popupOpen) return null
  if (ctx.draft.endsWith('\n')) return null
  const candidates = [
    ctx.attachments === 0 ? s.suggest : null,
    ctx.local.history,
    ctx.local.file,
    s.inline
  ]
  for (const ghost of candidates) {
    if (ghost === null || s.dismissed.includes(ghost.key)) continue
    const remainder = remainderOf(ghost, ctx.draft)
    if (remainder !== null && remainder !== '') return { ghost, remainder }
  }
  return null
}
