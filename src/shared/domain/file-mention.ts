/**
 * 输入框里的 `@` 文件引用 —— **格式、光标、解析**三件事,一处定义。
 *
 * ## 引用就是一段普通的 markdown 链接
 *
 * 用户选中一个文件之后,草稿里落下的是 `[App.tsx](src/App.tsx)` ——
 * 和 `fileRefMarkdown()` 给拖进来的文件编出的**完全同一种写法**。
 * 这不是巧合,是这个功能能这么小的原因:
 *
 * - 发给模型的那句话不需要任何特殊处理。草稿原样进 `text` part,
 *   模型看到的和拖一个文件进来时看到的是同一个字符串。
 * - 于是 `RunRequest` / `ContentPart` / 三个 encoder **一行都不用改**。
 *
 * 「渲染成 tag」因此纯粹是**显示层**的事:同一段文本,输入框里画一遍
 * (`rich-draft.ts` / `MentionInput`),转录气泡里画一遍(`MentionText`),
 * 两处共用下面这个 `parseMentions`。任何一处自己写正则,就会出现
 * 「输入框里是 tag、发出去变成一串方括号」这种分叉。
 *
 * ## 草稿是纯文本,DOM 只是它的一种画法
 *
 * 输入框是 contentEditable(`<textarea>` 里画不出组件),但**权威仍然是这段
 * 纯文本** —— DOM 由它单向生成,发送时不做任何序列化。反过来以 DOM 为权威的话,
 * 浏览器每一次自作主张的 `<div>`/`<span style>` 都会变成模型看见的内容。
 *
 * 代价是 `<textarea>` 白送的那些行为要自己补:光标偏移、换行、粘贴、拖入、
 * 输入法组词期间不许重画。那份补齐在 `rich-draft.ts` 和 `MentionInput.tsx`。
 */

/**
 * 一段草稿被切开之后的一节。`raw` 是它在原文里**逐字**占的那一段 ——
 * 所有 `raw` 接起来必须精确等于原文,DOM ⇄ 纯文本的往返就靠这一点。
 */
export type MentionSegment =
  | { kind: 'text'; raw: string }
  | { kind: 'mention'; raw: string; name: string; path: string }
  | { kind: 'skill'; raw: string; name: string }

/**
 * `[name](target)`。
 *
 * ★ `name` 不允许含 `]`、`target` 不允许含 `)` 或换行 —— 这与 markdown 本身
 * 对未转义括号的处理一致(要放这些字符得用 `<...>` 或反斜杠转义)。
 * 放宽它需要一个真正的 markdown 行内解析器,而那东西的输出还得能逐字拼回原文。
 */
const LINK = /\[([^\]\n]*)\]\(([^)\n]*)\)/g
const SKILL = /<skill\s+name="([a-z0-9][a-z0-9-]{0,63})"\s*\/>/g

/**
 * 链接目标是不是一个 URL 而非路径。
 *
 * ★ scheme 至少两个字符:Windows 的盘符 `C:\src\a.ts` 会命中一个字符的写法,
 * 于是用户贴进来的绝对路径会被判成网址、不再渲染成 chip。
 */
const URL_SCHEME = /^[a-z][a-z0-9+.-]+:/i

/** 目标看起来像文件路径(而不是 `https://…` 这类真·链接) */
export function isFilePath(target: string): boolean {
  return target !== '' && !URL_SCHEME.test(target)
}

/**
 * 把草稿切成「普通文本」和「文件引用」交替的若干节。
 *
 * ★ **各节的 `raw` 拼起来必须逐字等于入参**,这是高亮层对齐的前提,
 * 也是这个函数唯一一条不能破的约束(测试里有一条随机文本的往返断言)。
 *
 * 普通的网址链接**不**算文件引用:`[文档](https://example.com)` 在输入框里
 * 就该是它写的样子,画成一个文件 chip 是在撒谎。
 */
export function parseMentions(text: string): MentionSegment[] {
  const matches: Array<{ index: number; raw: string; segment: MentionSegment }> = []
  LINK.lastIndex = 0
  for (let m = LINK.exec(text); m !== null; m = LINK.exec(text)) {
    const [raw, name = '', target = ''] = m
    if (isFilePath(target)) matches.push({ index: m.index, raw, segment: { kind: 'mention', raw, name, path: target } })
  }
  SKILL.lastIndex = 0
  for (let m = SKILL.exec(text); m !== null; m = SKILL.exec(text)) {
    const [raw, name = ''] = m
    matches.push({ index: m.index, raw, segment: { kind: 'skill', raw, name } })
  }
  matches.sort((a, b) => a.index - b.index)
  const out: MentionSegment[] = []
  let last = 0
  // 正则带 `g`,而它是模块级常量 —— 每次进来必须把 lastIndex 归零,
  // 否则第二次调用会从上一次停的地方开始找。
  for (const m of matches) {
    if (m.index < last) continue
    if (m.index > last) out.push({ kind: 'text', raw: text.slice(last, m.index) })
    out.push(m.segment)
    last = m.index + m.raw.length
  }
  if (last < text.length) out.push({ kind: 'text', raw: text.slice(last) })
  return out
}

/** 草稿里有没有文件引用。只为让调用方跳过那一层高亮 DOM。 */
export function hasMention(text: string): boolean {
  return parseMentions(text).some((s) => s.kind === 'mention')
}

export interface SkillQuery { start: number; end: number; query: string }
const MAX_SKILL_QUERY = 64
function canTriggerSkillAfter(ch: string): boolean { return /[\s([{<'"`,;:]/.test(ch) }

export function skillQueryAt(text: string, caret: number): SkillQuery | null {
  if (caret < 0 || caret > text.length) return null
  for (let i = caret - 1; i >= 0 && caret - i <= MAX_SKILL_QUERY + 1; i--) {
    const ch = text[i] as string
    if (ch === '/') {
      if (i > 0 && !canTriggerSkillAfter(text[i - 1] as string)) return null
      const query = text.slice(i + 1, caret)
      if (/^(?:usr|var|tmp|home|Users|Library|opt)(?:\/|$)/.test(query)) return null
      return { start: i, end: caret, query }
    }
    if (/[\s()[\]<>]/.test(ch)) return null
  }
  return null
}

export interface SkillInsertion { text: string; caret: number }
export function insertSkill(text: string, range: { start: number; end: number }, name: string): SkillInsertion {
  const raw = `<skill name="${name}" />`
  const after = text.slice(range.end)
  const pad = after.startsWith(' ') || after.startsWith('\n') ? '' : ' '
  return { text: `${text.slice(0, range.start)}${raw}${pad}${after}`, caret: range.start + raw.length + pad.length }
}

/**
 * 把 `/查询` 换成 `/命令名 `。
 *
 * ★ 落进草稿的是**命令名**而不是它的正文 —— 展开留到发送那一刻
 * (`applyCommand`)。把几千字的模板当场塞进输入框的话,用户既没法再补参数,
 * 也看不清自己到底要发什么。
 */
export function insertCommand(text: string, range: { start: number; end: number }, name: string): SkillInsertion {
  const raw = `/${name}`
  const after = text.slice(range.end)
  const pad = after.startsWith(' ') || after.startsWith('\n') ? '' : ' '
  return { text: `${text.slice(0, range.start)}${raw}${pad}${after}`, caret: range.start + raw.length + pad.length }
}

// ─────────────────────────────────────────────────────────────
// 触发与替换
// ─────────────────────────────────────────────────────────────

/** 正在输入中的那个 `@查询`,以及它在草稿里占的区间(前闭后开)。 */
export interface MentionQuery {
  /** `@` 本身的下标 */
  start: number
  /** 光标位置 —— 替换区间的右端 */
  end: number
  /** `@` 之后到光标之间的那几个字,可能是空串(刚敲下 `@`) */
  query: string
}

/**
 * ★ 查询超过这个长度就不再认它是一次文件检索。
 *
 * 没有这条上限的话,用户敲一个 `@` 之后写下一整段话,每敲一个字都会
 * 触发一次全工作区检索 —— 而那段话显然不是文件名。
 */
const MAX_QUERY = 80

/** `@` 前面允许出现的字符。★ 空集合意味着只有行首/串首能触发。 */
function canTriggerAfter(ch: string): boolean {
  // 邮箱地址 `me@example.com` 里的 `@` 前面是字母,于是**不**触发 —— 正是要的。
  return /[\s([{<"'`,;:]/.test(ch)
}

/**
 * 光标此刻是不是落在一个 `@查询` 里。不是就返回 null。
 *
 * ★ 判据是**位置**而不是「刚才敲了 `@`」:用户点回到一个写了一半的
 * `@comp` 中间时,列表该重新出现。「敲过 Esc 就别再弹」那条属于组件的
 * 临时状态,不该混进这个纯函数里。
 */
export function mentionQueryAt(text: string, caret: number): MentionQuery | null {
  if (caret < 0 || caret > text.length) return null
  // 从光标往回找 `@`。中途撞上空白就说明这不是一个连续的查询。
  for (let i = caret - 1; i >= 0 && caret - i <= MAX_QUERY + 1; i--) {
    const ch = text[i] as string
    if (ch === '@') {
      if (i > 0 && !canTriggerAfter(text[i - 1] as string)) return null
      return { start: i, end: caret, query: text.slice(i + 1, caret) }
    }
    // 空白、换行、以及 markdown 链接的括号 —— 都说明 `@` 不在这一段里
    if (/[\s()[\]]/.test(ch)) return null
  }
  return null
}

/** 一次选中的结果:新的草稿全文,以及光标该落在哪。 */
export interface MentionInsertion {
  text: string
  caret: number
}

/**
 * 把 `@查询` 换成一条文件引用。
 *
 * ★ 后面补一个空格,光标落在空格之后 —— 少了它,用户接着打字会直接
 * 贴在 `)` 后面,而 `[a](b)后面的话` 在 markdown 里仍然解析得出来,
 * 于是这个错误不会有任何反馈,只是模型收到的那句话粘成了一团。
 * 已经有空格(用户点回旧引用重选)就不再补第二个。
 */
export function insertMention(
  text: string,
  range: { start: number; end: number },
  file: { name: string; path: string }
): MentionInsertion {
  const link = `[${file.name}](${file.path})`
  const after = text.slice(range.end)
  const pad = after.startsWith(' ') || after.startsWith('\n') ? '' : ' '
  return {
    text: `${text.slice(0, range.start)}${link}${pad}${after}`,
    caret: range.start + link.length + pad.length
  }
}

/**
 * 把一条文件引用**追加到草稿末尾** —— 文件树右键「添加到聊天」走这条。
 *
 * 需求:用户在右侧文件树上挑中一个文件,要的是「把它带进我正在写的这句话」,
 * 而不是替换掉已经写了一半的草稿;草稿末尾没有空白时先补一个空格,
 * 否则 `看下这个[a](a)` 在输入框里会和前一个字粘在一起。
 *
 * ★ 写法与 `insertMention` 逐字相同(同一个函数产出),于是追加进来的这一段在输入框里
 *   照样画成 chip,发给模型的也还是那一种 `[name](path)` —— 两条入口不分叉。
 */
export function appendMention(text: string, file: { name: string; path: string }): string {
  const base = text === '' || /\s$/.test(text) ? text : `${text} `
  return insertMention(base, { start: base.length, end: base.length }, file).text
}
