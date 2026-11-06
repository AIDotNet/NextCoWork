/**
 * diff 的语法高亮:把「词级增删高亮」和「语法 token」两层区间合成一串互不重叠的
 * 片段,并把一份 diff 还原成可以喂给高亮器的两侧文本。
 *
 * 需求:diff 里读代码同样靠「关键字 / 字符串 / 注释各是一个颜色」。但 diff 的行是
 * **拼出来的**,不是一份完整源码 —— 逐行送进语法解析器得到的树和真代码毫无关系
 * (每一行都是独立的一份「文件」,多行字符串、块注释、未闭合的括号全会染错)。
 * 所以这里先按侧还原成两段连续文本(旧侧 = context+del,新侧 = context+add),
 * 各解析一次,再把 token 按行切回去。
 *
 * 不变式:`styleSpans` 的产物拼起来必须和 `lineText(line)` 一模一样 —— 两层区间
 * 合并最容易出的错就是吞字或重复字,而那在界面上表现为「代码看着不太对」,
 * 没有任何报错。`__tests__/syntax.test.ts` 盯的就是这条。
 *
 * 纯函数,不碰 React —— 异步那一半在 `useDiffSyntax.ts`。
 */

import type { CodeSpan } from '../code/highlight'
import { lineText, type DiffLine, type DiffSpan } from './model'

export interface StyledSpan extends DiffSpan {
  /** 语法 token 的类名(`tok-keyword` …);没有就是空串 */
  className: string
}

/** 哪一行落在哪一侧的第几行;`side: null` = hunk 标题/文件头,没有代码可高亮。 */
export interface DiffSides {
  oldText: string
  newText: string
  index: Array<{ side: 'old' | 'new' | null; line: number }>
}

/**
 * 还原两侧文本。
 *
 * 上下文行两侧都有,统一记到**新侧** —— 正文一样,而新侧是用户更关心的那一份,
 * 记两遍只会让两侧的行数对不上。
 */
export function diffSides(lines: DiffLine[]): DiffSides {
  const oldLines: string[] = []
  const newLines: string[] = []
  const index: DiffSides['index'] = []
  for (const line of lines) {
    if (line.kind === 'del') {
      index.push({ side: 'old', line: oldLines.length })
      oldLines.push(lineText(line))
    } else if (line.kind === 'add' || line.kind === 'context') {
      index.push({ side: 'new', line: newLines.length })
      newLines.push(lineText(line))
      if (line.kind === 'context') oldLines.push(lineText(line))
    } else {
      index.push({ side: null, line: 0 })
    }
  }
  return { oldText: oldLines.join('\n'), newText: newLines.join('\n'), index }
}

/**
 * 把整段文本的 token 按行切开,偏移量换算成**行内**偏移。
 *
 * 跨行的 token(多行字符串、块注释)在每一行上各留一段;换行符本身不属于任何一段。
 */
export function tokensByLine(code: string, spans: CodeSpan[]): CodeSpan[][] {
  const starts = [0]
  for (let i = 0; i < code.length; i++) if (code[i] === '\n') starts.push(i + 1)
  const byLine: CodeSpan[][] = starts.map(() => [])

  let row = 0
  for (const span of spans) {
    while (row + 1 < starts.length && starts[row + 1]! <= span.from) row++
    let cursor = span.from
    let at = row
    while (cursor < span.to && at < starts.length) {
      const lineStart = starts[at]!
      // 行尾不含换行符;最后一行到文本末尾
      const lineEnd = at + 1 < starts.length ? starts[at + 1]! - 1 : code.length
      const to = Math.min(span.to, lineEnd)
      if (to > cursor) {
        byLine[at]!.push({ from: cursor - lineStart, to: to - lineStart, className: span.className })
      }
      at++
      cursor = at < starts.length ? Math.max(span.from, starts[at]!) : span.to
    }
  }
  return byLine
}

/** 合并同类相邻片段,减少 DOM 节点。 */
function push(spans: StyledSpan[], text: string, hi: boolean, className: string): void {
  if (text === '') return
  const last = spans[spans.length - 1]
  if (last !== undefined && last.hi === hi && last.className === className) last.text += text
  else spans.push({ text, hi, className })
}

/**
 * 两层合并:词级 span(增删)× 语法 token(颜色)。
 *
 * `tokens` 必须按 `from` 升序且互不重叠 —— `highlightTree` 的回调就是这么给的。
 * 拿不到 token(还在解析 / 不认识这门语言 / 超限)时原样返回,界面上就是
 * 「有 diff 没有颜色」,而不是空白。
 */
export function styleSpans(spans: DiffSpan[], tokens: CodeSpan[]): StyledSpan[] {
  if (tokens.length === 0) return spans.map((span) => ({ ...span, className: '' }))
  const out: StyledSpan[] = []
  let offset = 0
  let at = 0
  for (const span of spans) {
    const start = offset
    const end = offset + span.text.length
    let cursor = start
    while (cursor < end) {
      while (at < tokens.length && tokens[at]!.to <= cursor) at++
      const token = tokens[at]
      if (token === undefined || token.from >= end) {
        push(out, span.text.slice(cursor - start), span.hi, '')
        break
      }
      if (token.from > cursor) {
        push(out, span.text.slice(cursor - start, token.from - start), span.hi, '')
        cursor = token.from
      }
      const stop = Math.min(token.to, end)
      push(out, span.text.slice(cursor - start, stop - start), span.hi, token.className)
      cursor = stop
    }
    offset = end
  }
  return out
}
