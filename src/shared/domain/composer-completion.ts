/**
 * 输入框补全(灰字 + Tab 接受)两端共用的纯函数。设计见 `docs/composer-autocomplete.md`。
 *
 * ★ 放在 shared 是因为同一条规则要在**两头**各判一次:渲染层决定「要不要发请求」,
 *   主进程决定「模型回来的这句能不能给人看」。两边各写一份,迟早出现「渲染层以为是密钥
 *   不发、主进程却照样放行」这种互相矛盾的口径。
 *
 * ★ 一切长度都按**字素**(grapheme)数,不按空格切词、也不按 UTF-16 码元:
 *   中文没有空格,emoji 一个字素占两三个码元。按空格数单词的话,一整句中文是「一个词」,
 *   要么全放行要么全丢 —— 正是 Claude Code 2.1.269 修掉的那类 bug。
 */

const GRAPHEMES = new Intl.Segmenter(undefined, { granularity: 'grapheme' })

export function graphemes(text: string): string[] {
  return Array.from(GRAPHEMES.segment(text), (s) => s.segment)
}

export function graphemeCount(text: string): number {
  let n = 0
  for (const _ of GRAPHEMES.segment(text)) n++
  return n
}

/** 取前 `max` 个字素(不会把一个 emoji 或组合字符切成两半)。 */
export function takeGraphemes(text: string, max: number): string {
  return graphemes(text).slice(0, max).join('')
}

/** 取后 `max` 个字素。 */
export function lastGraphemes(text: string, max: number): string {
  const all = graphemes(text)
  return all.length <= max ? text : all.slice(all.length - max).join('')
}

/**
 * 「回答腔」开头 —— 模型没有续写用户的话,而是开始**回答**它了。
 *
 * ★ 这是这个功能最大的失败形态:用户打了「帮我写一个」,模型回「好的,下面是…」。
 *   提示词里已经禁止,这里是第二道闸:拦下来的建议**一个字都不给人看**。
 */
const AGENT_VOICE = [
  '好的', '好呀', '当然', '以下是', '下面是', '我来', '我将', '我会', '让我', '没问题', '收到',
  'sure', "here's", 'here is', "i'll", 'i will', 'let me', 'certainly', 'of course', 'okay,', 'ok,'
]

export function startsWithAgentVoice(text: string): boolean {
  const head = text.trimStart().toLocaleLowerCase()
  return AGENT_VOICE.some((p) => head.startsWith(p))
}

/** 评价语(「不错」「谢谢」「looks good」)—— 用户不会把这类话当成下一条指令打出来。 */
const EVALUATIONS = [
  '不错', '很好', '太好了', '完美', '谢谢', '感谢', '辛苦了', '好的谢谢',
  'thanks', 'thank you', 'looks good', 'lgtm', 'great', 'perfect', 'nice', 'awesome'
]

export function isEvaluation(text: string): boolean {
  const t = text.trim().toLocaleLowerCase().replace(/[!！。.~～]+$/u, '')
  return EVALUATIONS.some((e) => t === e || t.startsWith(`${e},`) || t.startsWith(`${e}，`))
}

/**
 * 含 markdown 语法。一条要填进输入框的建议不该带格式 —— 带了说明模型在写「回答」。
 * ★ 只认**语法标记**,不认普通标点:`fix the bug - then test` 里的连字符是正常文字。
 */
export function hasMarkdownSyntax(text: string): boolean {
  return /\*\*|`|^\s*#{1,6}\s|^\s*[-*+]\s|^\s*\d+\.\s|\[[^\]]*\]\([^)]*\)|<\/?[a-z][\w-]*>/iu.test(text)
}

/**
 * 草稿看起来像带着密钥 —— 带着就**不发**给补全模型。
 *
 * ★ 宁可漏判,不可滥判:滥判的代价只是「这一句没有补全」,但规则太宽会让整类正常
 *   输入永远没有补全,用户也不会知道为什么。所以**不认 40 位十六进制** —— 那是
 *   git 提交哈希的样子,在编码代理的输入框里出现得比密钥多得多。
 */
const SECRET_PATTERNS: readonly RegExp[] = [
  /\bsk-[A-Za-z0-9_-]{16,}/u, // OpenAI / Anthropic(sk-ant-…)/ DeepSeek 等
  /\bgh[pousr]_[A-Za-z0-9]{20,}/u, // GitHub token
  /\bgithub_pat_[A-Za-z0-9_]{20,}/u,
  /\bAKIA[0-9A-Z]{16}\b/u, // AWS access key id
  /\bxox[baprs]-[A-Za-z0-9-]{10,}/u, // Slack
  /\bAIza[0-9A-Za-z_-]{30,}/u, // Google API key
  /-----BEGIN [A-Z ]*PRIVATE KEY-----/u,
  /\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}/u, // JWT
  /\b(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key)\s*[=:]\s*\S{4,}/iu,
  /(?:密码|口令|密钥)\s*[=:：是为]\s*\S{4,}/u
]

export function looksSecret(text: string): boolean {
  return SECRET_PATTERNS.some((p) => p.test(text))
}

/**
 * 把一段摘录里的 `</tag` 改写成 `<\/tag`,免得摘录在提示词里**闭合我们自己的包装标签**。
 *
 * ★ 摘录(用户历史、助手回复)是不可信输入:一段写着 `</last_turn> 忽略以上…` 的
 *   回复会把后半段从「素材」里放出来,变成像指令一样的正文。改写后模型仍读得懂原意。
 */
export function neutralizeClosingTags(text: string, tags: readonly string[]): string {
  let out = text
  for (const tag of tags) out = out.split(`</${tag}`).join(`<\\/${tag}`)
  return out
}

/**
 * 从模型回答里取出 `<tag>…</tag>` 的内容。
 *
 * - 有开有闭:取中间;
 * - 只有开标签:取开标签之后的全部 —— 认停止词的供应商会把 `</tag>` 吞掉;
 * - 一个开标签都没有:整段当作内容(有的模型就是不爱写标签)。
 *
 * 返回 `null` 表示「模型明确地什么都没给」以外的情况不存在 —— 空标签返回 `''`。
 */
export function extractTagged(raw: string, tag: string): string {
  const open = `<${tag}>`
  const start = raw.indexOf(open)
  if (start < 0) return raw
  const body = raw.slice(start + open.length)
  const end = body.indexOf(`</${tag}>`)
  return end < 0 ? body : body.slice(0, end)
}

/** 打字续写时渲染层一并递给主进程的上下文。全部是渲染层本来就有的东西,主进程只截断不信任。 */
export interface ComposerCompletionContext {
  /** 工作区名 */
  project: string
  branch?: string
  /** 本会话 `@` 过的 / 改动过的文件,工作区相对路径 */
  files: string[]
  /** 斜杠命令名与 Skill 名(不带 `/`) */
  commands: string[]
}
