/**
 * 由 before / after 两段文本算出 diff —— 顺带在**改动的那一对行**里做词级高亮,
 * 并为整文件审查切出带行号的 hunk。
 *
 * 为什么自己写而不引库:仓库没有 diff 依赖(见 package.json)；Edit 片段通常只有
 * 几十行，整文件审查则先裁相同头尾并限制 LCS 预算，不值得为此新增依赖。
 * 计算保持为纯函数，便于覆盖行号、hunk 与超限分支。
 *
 * LCS 本体抽到了 `shared/domain/line-diff.ts`,主进程封包改动集算 `+X −Y` 时复用同一份。
 *
 * (原 `views/chat/diff.ts`。挪到 `components/diff/` 是因为它的产物现在和
 *  `parseUnifiedDiff` 一样,是同一份 `DiffLine` —— 两个生产者一个渲染器。)
 */

import { lcsOps, type LcsOp } from '../../../../shared/domain/line-diff'
import { plainLine, type DiffLine, type DiffSpan } from './model'

/** 一段连续改动及其上下文；行号范围直接用于统一 diff 的 hunk 标题。 */
export interface DiffHunk {
  oldStart: number
  oldCount: number
  newStart: number
  newCount: number
  lines: DiffLine[]
}

/**
 * 词级切分:词、空白、单个符号各成一 token。
 * 保留空白是为了让高亮不吞掉缩进,重组后能还原原文。
 */
function tokenize(line: string): string[] {
  return line.match(/\w+|\s+|[^\w\s]/g) ?? []
}

/** 对一对「删除行 / 新增行」做词级 diff,产出各自侧的高亮 span。 */
function wordSpans(
  oldLine: string,
  newLine: string
): {
  del: DiffSpan[]
  add: DiffSpan[]
} {
  const ops = lcsOps(tokenize(oldLine), tokenize(newLine), (x, y) => x === y)
  const del: DiffSpan[] = []
  const add: DiffSpan[] = []
  for (const op of ops) {
    if (op.tag === 'eq') {
      pushSpan(del, op.a as string, false)
      pushSpan(add, op.b as string, false)
    } else if (op.tag === 'del') {
      pushSpan(del, op.a as string, true)
    } else {
      pushSpan(add, op.b as string, true)
    }
  }
  return { del, add }
}

/** 合并同 `hi` 的相邻 span,减少 DOM 节点。 */
function pushSpan(spans: DiffSpan[], text: string, hi: boolean): void {
  const last = spans[spans.length - 1]
  if (last !== undefined && last.hi === hi) last.text += text
  else spans.push({ text, hi })
}

/** 高亮占比超过这个数,就说明「几乎整行都变了」。 */
const SATURATED = 0.8

/**
 * 一侧几乎全是高亮时抹平它。
 *
 * 配对的两行有时其实毫不相干(LCS 只是按位置把它们凑成了一对),逐词 diff 会
 * 把整行几乎全标上 —— 那和整行高亮一样没有信息量,还会盖住行底色。
 */
function unlitIfSaturated(spans: DiffSpan[]): DiffSpan[] {
  const total = spans.reduce((n, s) => n + s.text.length, 0)
  if (total === 0) return spans
  const lit = spans.reduce((n, s) => (s.hi ? n + s.text.length : n), 0)
  return lit / total < SATURATED ? spans : [{ text: spans.map((s) => s.text).join(''), hi: false }]
}

/** 空文件是 0 行；末尾换行也不应凭空制造一个可见 diff 行。 */
function textLines(text: string): string[] {
  if (text === '') return []
  const lines = text.split('\n')
  if (lines[lines.length - 1] === '') lines.pop()
  return lines
}

/** 找一条两侧都只出现一次、且尽量靠近中部的公共行，作为大区段的可靠切点。 */
function uniqueLineAnchor(oldLines: string[], newLines: string[]): { oldIndex: number; newIndex: number } | null {
  const positions = (lines: string[]): Map<string, number | null> => {
    const result = new Map<string, number | null>()
    lines.forEach((line, index) => result.set(line, result.has(line) ? null : index))
    return result
  }
  const oldPositions = positions(oldLines)
  const newPositions = positions(newLines)
  const oldMiddle = (oldLines.length - 1) / 2
  const newMiddle = (newLines.length - 1) / 2
  let best: { oldIndex: number; newIndex: number; distance: number } | null = null
  for (const [line, oldIndex] of oldPositions) {
    const newIndex = newPositions.get(line)
    if (oldIndex === null || newIndex === undefined || newIndex === null) continue
    const distance = Math.abs(oldIndex - oldMiddle) + Math.abs(newIndex - newMiddle)
    if (best === null || distance < best.distance) best = { oldIndex, newIndex, distance }
  }
  return best === null ? null : { oldIndex: best.oldIndex, newIndex: best.newIndex }
}

/**
 * 先裁掉完全相同的头尾，再对真正可能变化的中段做 O(n·m) LCS。
 * 超预算时用唯一公共行继续分段；没有可靠锚点才返回 null。
 *
 * 需求：几千行文件只有少量分散改动时不能为整份文件分配平方级矩阵，也不能
 * 因首尾恰好都改过就误判为无法预览。
 */
function boundedLineOps(
  oldLines: string[],
  newLines: string[],
  budget: { remainingCells: number } | undefined,
  depth = 0
): LcsOp<string>[] | null {
  let prefix = 0
  while (prefix < oldLines.length && prefix < newLines.length && oldLines[prefix] === newLines[prefix]) prefix++

  let suffix = 0
  while (
    suffix < oldLines.length - prefix &&
    suffix < newLines.length - prefix &&
    oldLines[oldLines.length - 1 - suffix] === newLines[newLines.length - 1 - suffix]
  ) suffix++

  const oldMiddle = oldLines.slice(prefix, oldLines.length - suffix)
  const newMiddle = newLines.slice(prefix, newLines.length - suffix)
  const prefixOps: LcsOp<string>[] = oldLines.slice(0, prefix).map((line) => ({ tag: 'eq', a: line, b: line }))
  const suffixOps: LcsOp<string>[] = oldLines
    .slice(oldLines.length - suffix)
    .map((line) => ({ tag: 'eq', a: line, b: line }))

  let middleOps: LcsOp<string>[]
  const cells = oldMiddle.length * newMiddle.length
  if (budget === undefined || cells <= budget.remainingCells) {
    if (budget !== undefined) budget.remainingCells -= cells
    middleOps = lcsOps(oldMiddle, newMiddle, (x, y) => x === y)
  } else {
    // 需求：递归深度也必须有硬上限；恶意重排不能靠连续单点锚定拖垮调用栈。
    if (depth >= 32) return null
    const anchor = uniqueLineAnchor(oldMiddle, newMiddle)
    if (anchor === null) return null
    const left = boundedLineOps(
      oldMiddle.slice(0, anchor.oldIndex),
      newMiddle.slice(0, anchor.newIndex),
      budget,
      depth + 1
    )
    if (left === null) return null
    const right = boundedLineOps(
      oldMiddle.slice(anchor.oldIndex + 1),
      newMiddle.slice(anchor.newIndex + 1),
      budget,
      depth + 1
    )
    if (right === null) return null
    const line = oldMiddle[anchor.oldIndex]!
    middleOps = [...left, { tag: 'eq', a: line, b: line }, ...right]
  }
  return [...prefixOps, ...middleOps, ...suffixOps]
}

function lineOps(oldStr: string, newStr: string): LcsOp<string>[]
function lineOps(oldStr: string, newStr: string, maxCells: number): LcsOp<string>[] | null
function lineOps(oldStr: string, newStr: string, maxCells?: number): LcsOp<string>[] | null {
  const budget = maxCells === undefined ? undefined : { remainingCells: maxCells }
  return boundedLineOps(textLines(oldStr), textLines(newStr), budget)
}

/** 把已对齐的行操作转成 UI 行；工具片段与整文件 hunk 共用同一套词级高亮规则。 */
function linesFromOps(ops: LcsOp<string>[]): DiffLine[] {
  const lines: DiffLine[] = []
  let k = 0
  while (k < ops.length) {
    const op = ops[k]!
    if (op.tag === 'eq') {
      lines.push(plainLine('context', op.a as string))
      k++
      continue
    }
    // 收集一个连续的改动块:先 del 后 add(LCS 回溯天然是这个顺序)
    const dels: string[] = []
    const adds: string[] = []
    while (k < ops.length && ops[k]!.tag === 'del') dels.push(ops[k++]!.a as string)
    while (k < ops.length && ops[k]!.tag === 'add') adds.push(ops[k++]!.b as string)

    // ★ 只有增删行数**相等**时才逐行配对做词级 diff。
    //
    // 一删九增这种块(把一行展开成一整段),拿第一条删去和第一条增凑对纯属
    // 位置巧合,标出来的「差异」没有意义。此时整行就是改动本身,行底色已经
    // 说清楚了 —— 再叠一层词级高亮只会把整行糊成一块实心底,反而看不出改了哪。
    if (dels.length === adds.length) {
      for (let p = 0; p < dels.length; p++) {
        const { del, add } = wordSpans(dels[p]!, adds[p]!)
        lines.push({ kind: 'del', spans: unlitIfSaturated(del), oldLine: null, newLine: null })
        lines.push({ kind: 'add', spans: unlitIfSaturated(add), oldLine: null, newLine: null })
      }
    } else {
      for (const d of dels) lines.push(plainLine('del', d))
      for (const a of adds) lines.push(plainLine('add', a))
    }
  }
  return lines
}

/**
 * 计算 `oldStr → newStr` 的统一 diff 行序列(不带行号)。
 *
 * 连续的「删除段 + 新增段」若一一对应(同样多行,或对应位置),会两两做词级
 * 高亮;数量不等时,能配上的配对,配不上的整行标为改动。
 */
export function computeDiff(oldStr: string, newStr: string): DiffLine[] {
  return linesFromOps(lineOps(oldStr, newStr))
}

/** 给统一 diff 行补齐旧、新文件行号，供审查视图画固定宽度的双行号栏。 */
function numberLines(lines: DiffLine[]): DiffLine[] {
  let oldLine = 1
  let newLine = 1
  return lines.map((line) => {
    if (line.kind === 'context') return { ...line, oldLine: oldLine++, newLine: newLine++ }
    if (line.kind === 'del') return { ...line, oldLine: oldLine++, newLine: null }
    return { ...line, oldLine: null, newLine: newLine++ }
  })
}

// 预算封顶在约 200 万个矩阵单元；再大就继续分段或停止预览，不能拿渲染器响应性冒险。
const REVIEW_DIFF_MAX_LCS_CELLS = 2_000_000
// 一个 hunk 行会展开成多个 DOM 节点；可见行数也要独立封顶。
const REVIEW_DIFF_MAX_ROWS = 10_000

/**
 * 把整文件 diff 收成标准的改动块，每块只保留附近几行上下文。
 *
 * 需求：打开几千行文件时首屏就必须落在实际改动上，而不是让用户从文件第 1 行
 * 往下找；相距较远的改动保持为独立 hunk，也避免无关正文占满审查空间。
 * 返回 null 表示计算量或可见行数超过安全预算，调用方应停止生成预览。
 */
export function computeDiffHunks(oldStr: string, newStr: string, contextLines = 3): DiffHunk[] | null {
  const ops = lineOps(oldStr, newStr, REVIEW_DIFF_MAX_LCS_CELLS)
  if (ops === null) return null
  const lines = numberLines(linesFromOps(ops))
  const changed = lines.flatMap((line, index) => (line.kind === 'context' ? [] : [index]))
  if (changed.length === 0) return []

  const context = Math.max(0, Math.floor(contextLines))
  const ranges: Array<{ start: number; end: number }> = []
  for (const index of changed) {
    const start = Math.max(0, index - context)
    const end = Math.min(lines.length, index + context + 1)
    const previous = ranges[ranges.length - 1]
    if (previous !== undefined && start <= previous.end) {
      previous.end = Math.max(previous.end, end)
    } else {
      ranges.push({ start, end })
    }
  }
  // 需求：计算能完成不代表浏览器能承受对应 DOM；整文件新增也必须在渲染前止损。
  if (ranges.reduce((total, range) => total + range.end - range.start, 0) > REVIEW_DIFF_MAX_ROWS) return null

  const oldLinesBefore = [0]
  const newLinesBefore = [0]
  for (const line of lines) {
    oldLinesBefore.push(oldLinesBefore[oldLinesBefore.length - 1]! + (line.kind === 'add' ? 0 : 1))
    newLinesBefore.push(newLinesBefore[newLinesBefore.length - 1]! + (line.kind === 'del' ? 0 : 1))
  }

  return ranges.map(({ start, end }) => {
    const hunkLines = lines.slice(start, end)
    const oldBefore = oldLinesBefore[start]!
    const newBefore = newLinesBefore[start]!
    const oldCount = oldLinesBefore[end]! - oldBefore
    const newCount = newLinesBefore[end]! - newBefore
    return {
      oldStart: oldCount === 0 ? oldBefore : oldBefore + 1,
      oldCount,
      newStart: newCount === 0 ? newBefore : newBefore + 1,
      newCount,
      lines: hunkLines
    }
  })
}

/** 统一 diff 标题里的范围格式；单行省略数量,和常见代码审查工具一致。 */
function hunkRange(start: number, count: number): string {
  return count === 1 ? String(start) : `${start},${count}`
}

/**
 * hunk 列表 → 可直接渲染的行序列,块与块之间插一行 `@@ … @@` 标题。
 *
 * 需求:渲染器只认 `DiffLine[]`(git 那一侧解析出来的本来就长这样),所以
 * 「hunk 标题长什么样」必须在这里定死一次,而不是让每个视图自己拼一遍 —— 那正是
 * 两套 diff 时代的样子:同一个 `@@` 头在两个界面里字体、颜色、格式都不一样。
 */
export function hunkLines(hunks: DiffHunk[]): DiffLine[] {
  return hunks.flatMap((hunk) => [
    plainLine(
      'hunk',
      `@@ -${hunkRange(hunk.oldStart, hunk.oldCount)} +${hunkRange(hunk.newStart, hunk.newCount)} @@`
    ),
    ...hunk.lines
  ])
}
