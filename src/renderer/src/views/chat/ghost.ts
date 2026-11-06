/**
 * 输入框灰字的纯函数部分:按键、跟打、四个来源各自怎么算、怎么按词接受。
 * 设计见 `docs/composer-autocomplete.md`;状态机在 `ghost-state.ts`,接线在 `useComposerGhost.ts`。
 *
 * ★ 这里**没有一行碰 DOM**:灰字画在哪、光标在哪是 `MentionInput` 的事。
 *   这里的输入只有「草稿纯文本」和几张清单,所以每一条规则都能在 node 单测里直接验。
 */
import type { AgentMessage } from '../../../../shared/agent/message'
import { visibleText } from '../../../../shared/agent/message'
import { graphemeCount, looksSecret, takeGraphemes } from '../../../../shared/domain/composer-completion'
import { parseMentions } from '../../../../shared/domain/file-mention'
import type { FileSuggestion } from '../../../../shared/domain/file-tree'
import type { GitOverview } from '../../../../shared/domain/git'

export type GhostSource = 'suggest' | 'history' | 'file' | 'inline'

/**
 * 一条灰字。★ 存的是「出现那一刻的草稿 + 要补的那段」,而不是「要补的那段」本身:
 * 用户接着打字时,剩下要画的是 `(base + text).slice(草稿.length)` —— 跟打就是这一句。
 */
export interface Ghost {
  source: GhostSource
  base: string
  /** `base` 之后要补的原文,可能含 `[名](路径)` 这类 mention 原文 */
  text: string
  /** 身份。Esc 记的是它:同一条建议按掉之后不会换个时机又冒出来 */
  key: string
  /** 文件灰字:接受时把光标前那个词换成文件 chip */
  file?: { name: string; path: string; tokenStart: number }
}

/** 这条灰字在当前草稿下还剩什么要补;对不上(用户打了别的)或已经补完 → `null`。 */
export function remainderOf(ghost: Ghost, draft: string): string | null {
  const full = ghost.base + ghost.text
  if (!draft.startsWith(ghost.base) || !full.startsWith(draft) || draft.length >= full.length) return null
  return full.slice(draft.length)
}

// ── 按键 ──

export type GhostKeyAction = 'accept' | 'acceptWord' | 'dismiss'

export interface GhostKeyEvent {
  key: string
  shiftKey?: boolean
  altKey?: boolean
  ctrlKey?: boolean
  metaKey?: boolean
  isComposing?: boolean
  keyCode?: number
}

/**
 * 有灰字时这一下按键归谁。`null` = 不归灰字,照原样走(没有灰字时所有键都是 `null`)。
 *
 * ★ **Enter 永远是 `null`**:接受和发送必须是两个动作。Claude Code 曾让 Enter 直接发出建议,
 *   后来以「误执行」修掉(2.1.136);在我们这里一条误发的「提交并推送」可能正跑在完全访问档位上。
 * ★ 组词中一律不碰:`isComposing` 之外再认 `keyCode === 229` —— Safari 的 `compositionend`
 *   抢在确认键之前发,那一下 `isComposing` 已经是 false 了。
 * ★ 带修饰键的 Tab / Esc / → 不归灰字(Shift+Tab 是反向切焦点,Alt+→ 是按词移动光标)。
 */
export function resolveGhostKey(e: GhostKeyEvent, s: { visible: boolean; mac: boolean }): GhostKeyAction | null {
  if (!s.visible || e.isComposing === true || e.keyCode === 229) return null
  const shift = e.shiftKey === true
  const alt = e.altKey === true
  const ctrl = e.ctrlKey === true
  const meta = e.metaKey === true
  const plain = !shift && !alt && !ctrl && !meta
  if ((e.key === 'Tab' || e.key === 'ArrowRight') && plain) return 'accept'
  if (e.key === 'ArrowRight' && !shift && !alt && (s.mac ? meta && !ctrl : ctrl && !meta)) return 'acceptWord'
  if (e.key === 'Escape' && plain) return 'dismiss'
  return null
}

// ── 显示 ──

const DISPLAY_LIMIT = 200

/**
 * 要补的原文 → 灰字上画的字。mention 画成 `@名`、Skill 画成 `✦ 名`(和 chip 上一样),
 * 只取第一行、限长 —— 灰字绝不能把输入框撑高一大截。
 */
export function ghostDisplay(remainder: string): string {
  const line = remainder.split('\n')[0] ?? ''
  const shown = parseMentions(line)
    .map((s) => (s.kind === 'mention' ? `@${s.name}` : s.kind === 'skill' ? `✦ ${s.name}` : s.raw))
    .join('')
  return graphemeCount(shown) > DISPLAY_LIMIT ? `${takeGraphemes(shown, DISPLAY_LIMIT)}…` : shown
}

/** 第 `offset` 个字符是不是落在一段 mention / Skill 原文的**中间**(切在那里会切出半截语法)。 */
function insideSegment(text: string, offset: number): boolean {
  let pos = 0
  for (const seg of parseMentions(text)) {
    const end = pos + seg.raw.length
    if (seg.kind !== 'text' && offset > pos && offset < end) return true
    pos = end
  }
  return false
}

// ── 按词接受 ──

const WORDS = new Intl.Segmenter(undefined, { granularity: 'word' })

/**
 * ⌘→ / Ctrl+→ 一次接受多少:前导的空白和标点,加上第一个「词」。
 *
 * ★ 用 `Intl.Segmenter` 的 word 粒度而不是按空格:中文没有空格,按空格切一整句就是「一个词」。
 *   Chromium 的 ICU 带中文词典,「上重试逻辑的单元测试」切得出「上 / 重试 / 逻辑 / …」。
 * ★ 切点落进 mention 原文中间时延到它的末尾 —— chip 是原子的,接受半个 `[a.ts](src/a` 毫无意义。
 *
 * @param full 灰字的完整文本(`base + text`)
 * @param cut  当前草稿的长度(从这里往后是还没接受的部分)
 */
export function nextWordChunk(full: string, cut: number): string {
  const rest = full.slice(cut)
  let end = 0
  for (const s of WORDS.segment(rest)) {
    end = s.index + s.segment.length
    if (s.isWordLike === true) break
  }
  let pos = 0
  for (const seg of parseMentions(full)) {
    const segEnd = pos + seg.raw.length
    if (seg.kind !== 'text' && pos - cut < end && segEnd - cut > end && segEnd > cut) end = segEnd - cut
    pos = segEnd
  }
  return rest.slice(0, Math.max(end, 0))
}

// ── L1:历史指令 ──

/** 少于这么多字素不查历史:一两个字的前缀能匹配上的东西太多,灰字等于乱猜 */
const HISTORY_MIN = 3

/**
 * 草稿是某条历史指令的开头 → 补出剩下的(fish 式)。取**最近**的那条。
 *
 * ★ 大小写敏感、逐字前缀:这一层的价值就在于「它从不猜错」—— 补出来的每一个字都是
 *   用户自己以前打过的。模糊匹配会把这份确定性换成「差不多」。
 */
export function historyGhost(draft: string, prompts: readonly string[]): Ghost | null {
  if (draft.trim() === '' || draft.endsWith('\n') || graphemeCount(draft) < HISTORY_MIN) return null
  for (const prompt of prompts) {
    if (prompt.length <= draft.length || !prompt.startsWith(draft)) continue
    if (insideSegment(prompt, draft.length)) continue
    return { source: 'history', base: draft, text: prompt.slice(draft.length), key: `history:${prompt}` }
  }
  return null
}

// ── 热文件 ──

/** 光标前的那个「像文件名」的词 */
const TOKEN = /[A-Za-z0-9._-]+$/u
const TOKEN_MIN = 3

/**
 * 光标前那个词是某个热文件名的开头,而且**只有它一个** → 补出文件名;Tab 后变成文件 chip。
 *
 * ★ 只认唯一命中:两个文件都能补时,猜一个就是在替用户做决定,而他按 Tab 之前看不出
 *   这枚 chip 指向哪一个。这种时候 `@` 弹层才是对的工具。
 * ★ 前面紧挨着 `@` `/` `[` `(` 或反引号时不补:那分别是 `@` 弹层、路径、mention 原文、代码。
 */
export function fileGhost(draft: string, files: readonly FileSuggestion[]): Ghost | null {
  if (draft.endsWith('\n')) return null
  const m = TOKEN.exec(draft)
  if (m === null) return null
  const token = m[0]
  const tokenStart = m.index
  if (token.length < TOKEN_MIN) return null
  const before = tokenStart > 0 ? draft[tokenStart - 1] : undefined
  if (before !== undefined && /[@/\\[(`]/u.test(before)) return null
  if (insideSegment(draft, tokenStart)) return null
  const lower = token.toLocaleLowerCase()
  const hits = files.filter((f) => f.name.length > token.length && f.name.toLocaleLowerCase().startsWith(lower))
  if (hits.length !== 1) return null
  const hit = hits[0] as FileSuggestion
  return {
    source: 'file',
    base: draft,
    text: hit.name.slice(token.length),
    key: `file:${hit.path}`,
    file: { name: hit.name, path: hit.path, tokenStart }
  }
}

/**
 * git 给的路径(相对**仓库根**)→ mention 要的路径(相对**工作区根**)。
 * 工作区就是仓库根:原样;工作区是仓库的子目录:剥掉前缀,子目录外的文件丢掉;
 * 其它情况(对不上)返回 `null` —— 宁可这个来源不出灰字,也不画一枚指错文件的 chip。
 */
export function gitPathToWorkspacePath(repoRoot: string, workspaceRoot: string, path: string): string | null {
  const norm = (p: string): string => p.replace(/\\/gu, '/').replace(/\/+$/u, '')
  const repo = norm(repoRoot)
  const ws = norm(workspaceRoot)
  const file = path.replace(/\\/gu, '/')
  if (repo === ws) return file
  if (!ws.startsWith(`${repo}/`)) return null
  const prefix = `${ws.slice(repo.length + 1)}/`
  return file.startsWith(prefix) ? file.slice(prefix.length) : null
}

function basename(path: string): string {
  const i = path.lastIndexOf('/')
  return i < 0 ? path : path.slice(i + 1)
}

const HOT_LIMIT = 200

/** 热文件 = 本会话提到过的(在前)+ git 里改动过、还在的。按路径去重。 */
export function hotFilesFrom(
  overview: GitOverview | null,
  workspaceRoot: string,
  mentioned: readonly FileSuggestion[]
): FileSuggestion[] {
  const out = new Map<string, FileSuggestion>()
  for (const f of mentioned) if (!out.has(f.path)) out.set(f.path, f)
  if (overview !== null && overview.available) {
    for (const change of overview.files) {
      if (change.index === 'D' || change.worktree === 'D') continue
      const path = gitPathToWorkspacePath(overview.root, workspaceRoot, change.path)
      if (path === null || path === '' || out.has(path)) continue
      out.set(path, { path, name: basename(path) })
      if (out.size >= HOT_LIMIT) break
    }
  }
  return [...out.values()].slice(0, HOT_LIMIT)
}

/** 用户消息里 `@` 过的文件,新的在前、去重,最多 50 个。 */
export function mentionedFilesOf(messages: readonly AgentMessage[]): FileSuggestion[] {
  const out = new Map<string, FileSuggestion>()
  for (let i = messages.length - 1; i >= 0 && out.size < 50; i--) {
    const m = messages[i]
    if (m === undefined || m.role !== 'user' || m.internal === true) continue
    for (const seg of parseMentions(visibleText(m))) {
      if (seg.kind === 'mention' && !out.has(seg.path)) out.set(seg.path, { path: seg.path, name: seg.name })
    }
  }
  return [...out.values()]
}

// ── L3:打字续写 ──

const INLINE_MIN = 4
const INLINE_MAX = 2_000

/**
 * 这一刻值不值得为草稿发一次续写请求。所有条件都是**省钱**的:每一条不满足的情况,
 * 要么模型猜不出(太短、已经说完了),要么有更好的来源(本地灰字),要么根本不该发(密钥)。
 */
export function inlineEligible(draft: string, s: {
  enabled: boolean
  composing: boolean
  caretAtEnd: boolean
  popupOpen: boolean
  hasLocalGhost: boolean
  dismissed: boolean
  pasteHold: boolean
  remote: boolean
}): boolean {
  if (!s.enabled || s.remote || s.composing || !s.caretAtEnd || s.popupOpen || s.hasLocalGhost || s.dismissed || s.pasteHold) return false
  if (draft.length > INLINE_MAX || graphemeCount(draft.trim()) < INLINE_MIN) return false
  if (/[。!?.!?！？\n]$/u.test(draft)) return false
  return !looksSecret(draft)
}

/** 连续被无视这么多次之后放慢:用户显然没在用它,别再每次停顿都花一次钱 */
export function inlineDebounceMs(ignoredStreak: number): number {
  return ignoredStreak >= 10 ? 1_000 : 300
}

// ── 退避与提示 ──

/**
 * 「建议下一步」的退避:连续 10 条没被用,改成每 3 轮才请求一次;用一次就复位。
 * 同 Claude Code「Showing fewer prompt suggestions · use one to bring them back」。
 *
 * ★ 模块级状态,随应用重启复位:这是「最近在不在用」的信号,不该攒成永久的结论。
 */
export function createBackoff(threshold = 10, every = 3): {
  shouldRequest(): boolean
  note(outcome: 'accepted' | 'ignored'): void
  streak(): number
} {
  let ignored = 0
  let turns = 0
  return {
    shouldRequest(): boolean {
      if (ignored < threshold) return true
      turns += 1
      return turns % every === 0
    },
    note(outcome): void {
      if (outcome === 'accepted') { ignored = 0; turns = 0 } else ignored += 1
    },
    streak: () => ignored
  }
}

export const suggestionBackoff = createBackoff()
export const inlineBackoff = createBackoff()

const HINT_KEY = 'next-cowork:composer-ghost-accepts'
const HINT_UNTIL = 3

/** 接受过几次之前,灰字旁边一直挂着 `Tab` 提示 —— 学会了就收起来。读不到存储时照常显示。 */
export function showAcceptHint(): boolean {
  try {
    return Number(window.localStorage.getItem(HINT_KEY) ?? '0') < HINT_UNTIL
  } catch {
    return true
  }
}

export function noteAccepted(): void {
  try {
    const n = Number(window.localStorage.getItem(HINT_KEY) ?? '0')
    window.localStorage.setItem(HINT_KEY, String(Number.isFinite(n) ? n + 1 : 1))
  } catch {
    // 存储不可用(隐私模式、配额):提示多挂几次,不影响功能
  }
}
