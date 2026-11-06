/**
 * `Grep` / `Glob` 的命中列表 —— 把一坨文本拆成「文件 / 行号 / 命中内容」。
 *
 * ★★ 需求:搜索结果展开后该是一份**可以逐条看的清单**,不是一段贴上去的文本。
 * 两种工具的输出形状不同,但用户要的是同一件事:「命中在哪个文件的第几行」。
 *
 *     Grep:  src/main/db/config.ts:42:  const rows = …
 *     Glob:  src/main/db/config.ts
 *
 * ★ 解析**必须允许失败**:`-A/-B` 带上下文时会夹杂 `--` 分隔行和不带行号的
 * 上下文行,`--output_mode count` 又是另一种形状。认不出来的行原样当作一行文本,
 * 不猜、也不丢 —— 丢掉一行命中,用户会以为搜索漏了。
 *
 * 纯函数单独成文件(§9):形状判断是这块最容易回归的地方,而它不需要 DOM 就能测。
 */

export interface MatchRow {
  /** 认出来的文件路径;认不出就是 undefined,整行落到 `text` */
  path?: string
  /** 认出来的行号 */
  line?: number
  /** 命中内容;纯路径行(Glob)没有这一段 */
  text?: string
}

/**
 * Windows 盘符(`C:\a\b.ts:42:x`)会让「按冒号切」在第一段就切错,
 * 所以先匹配一个可选的盘符前缀。
 */
const MATCH_RE = /^((?:[A-Za-z]:)?[^:]+):(\d+):(.*)$/
const PATH_RE = /^((?:[A-Za-z]:)?[^:\s][^:]*)$/

export function parseMatches(content: string): readonly MatchRow[] {
  const rows: MatchRow[] = []
  for (const raw of content.split('\n')) {
    const line = raw.replace(/\r$/, '')
    if (line === '') continue
    const hit = MATCH_RE.exec(line)
    if (hit !== null) {
      rows.push({ path: hit[1] ?? '', line: Number(hit[2]), text: hit[3] ?? '' })
      continue
    }
    const onlyPath = PATH_RE.exec(line)
    // 纯路径:要求它看起来确实像路径(带分隔符或扩展名),否则一句普通输出
    // (`No matches found`)会被画成一个点不开的文件
    if (onlyPath !== null && /[/\\.]/.test(line) && !line.includes(' ')) {
      rows.push({ path: line })
      continue
    }
    rows.push({ text: line })
  }
  return rows
}
