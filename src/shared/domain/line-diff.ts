/**
 * 通用 LCS 与「按行数增删计数」—— 主进程封包改动集时算 `+X −Y`,渲染层的
 * `components/diff/compute.ts` 也复用同一份 LCS,避免两处 diff 口径漂移。
 *
 * 纯函数、零依赖(不碰 electron / DOM),所以放 shared,main 与 renderer 都能 import。
 */

export type LcsTag = 'eq' | 'del' | 'add'
export interface LcsOp<T> {
  tag: LcsTag
  a?: T
  b?: T
}

/** 通用 LCS 回溯:返回把 a 变成 b 的对齐操作序列。 */
export function lcsOps<T>(a: T[], b: T[], eq: (x: T, y: T) => boolean): LcsOp<T>[] {
  const n = a.length
  const m = b.length
  // dp[i][j] = a[i..] 与 b[j..] 的最长公共子序列长度
  const dp: number[][] = Array.from({ length: n + 1 }, () => new Array<number>(m + 1).fill(0))
  for (let i = n - 1; i >= 0; i--) {
    const di = dp[i]!
    const di1 = dp[i + 1]!
    const ai = a[i]!
    for (let j = m - 1; j >= 0; j--) {
      di[j] = eq(ai, b[j]!) ? di1[j + 1]! + 1 : Math.max(di1[j]!, di[j + 1]!)
    }
  }
  const ops: LcsOp<T>[] = []
  let i = 0
  let j = 0
  while (i < n && j < m) {
    const ai = a[i]!
    const bj = b[j]!
    if (eq(ai, bj)) {
      ops.push({ tag: 'eq', a: ai, b: bj })
      i++
      j++
    } else if (dp[i + 1]![j]! >= dp[i]![j + 1]!) {
      ops.push({ tag: 'del', a: ai })
      i++
    } else {
      ops.push({ tag: 'add', b: bj })
      j++
    }
  }
  while (i < n) ops.push({ tag: 'del', a: a[i++]! })
  while (j < m) ops.push({ tag: 'add', b: b[j++]! })
  return ops
}

/**
 * 两段文本的行级增删计数 —— 卡片上的 `+X −Y`。
 *
 * before 为 null 表示新建文件(所有行都算新增);after 为 null 表示删除(所有行都算删除)。
 * 末尾空行不参与计数(`''.split('\n')` 会给出一个空串,统一剔掉尾随空行的干扰)。
 */
export function countLineDiff(before: string | null, after: string | null): { additions: number; deletions: number } {
  const beforeLines = toLines(before)
  const afterLines = toLines(after)
  if (before === null) return { additions: afterLines.length, deletions: 0 }
  if (after === null) return { additions: 0, deletions: beforeLines.length }
  const common = lcsLength(beforeLines, afterLines)
  return { additions: afterLines.length - common, deletions: beforeLines.length - common }
}

/**
 * 超过这个格子数就不做精确 LCS,退化成按行计数的多重集差。
 * 4000 × 4000 行的两段改动在去掉公共首尾之后仍然很少见;真碰到时宁可数字略偏,
 * 也不让渲染层或主进程同步跑上几亿次比较。
 */
const MAX_LCS_CELLS = 16_000_000

/**
 * 只要计数的话不需要回溯:增删数 = 各自行数 − LCS 长度。
 *
 * - 先剥掉公共首尾行。它们一定在某个最长公共子序列里,剥掉不改变结果;
 *   一次典型的 Edit 只改中间几行,剩下要比的往往只有个位数。
 * - 剩下的部分用两行滚动数组求长度,内存 O(m),不再分配 n×m 的矩阵。
 */
function lcsLength(a: readonly string[], b: readonly string[]): number {
  let start = 0
  while (start < a.length && start < b.length && a[start] === b[start]) start++
  let endA = a.length
  let endB = b.length
  while (endA > start && endB > start && a[endA - 1] === b[endB - 1]) {
    endA--
    endB--
  }
  const shared = start + (a.length - endA)
  const n = endA - start
  const m = endB - start
  if (n === 0 || m === 0) return shared
  if (n * m > MAX_LCS_CELLS) return shared + multisetOverlap(a.slice(start, endA), b.slice(start, endB))
  let prev = new Int32Array(m + 1)
  let next = new Int32Array(m + 1)
  for (let i = 0; i < n; i++) {
    const ai = a[start + i]
    for (let j = 0; j < m; j++) {
      next[j + 1] = ai === b[start + j] ? prev[j]! + 1 : Math.max(prev[j + 1]!, next[j]!)
    }
    const swap = prev
    prev = next
    next = swap
  }
  return shared + prev[m]!
}

/** 两边都出现的行数(按出现次数取小)。是 LCS 长度的上界,用作超大输入时的近似。 */
function multisetOverlap(a: readonly string[], b: readonly string[]): number {
  const counts = new Map<string, number>()
  for (const line of a) counts.set(line, (counts.get(line) ?? 0) + 1)
  let overlap = 0
  for (const line of b) {
    const left = counts.get(line) ?? 0
    if (left > 0) {
      counts.set(line, left - 1)
      overlap++
    }
  }
  return overlap
}

/** 空文件 → 0 行;否则按 \n 切分并剔除唯一的尾随空行。 */
function toLines(text: string | null): string[] {
  if (text === null || text === '') return []
  const lines = text.split('\n')
  if (lines.length > 0 && lines[lines.length - 1] === '') lines.pop()
  return lines
}
