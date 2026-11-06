/**
 * 统一 diff 文本(`git diff` 的原文) → 可渲染的行模型。
 *
 * 需求:右栏原本把 `git diff` 的原文整段按首字符上色铺出来,于是用户要在
 * `diff --git` / `index 26282a78..2b278cb8` / `--- a/…` / `+++ b/…` 这四行噪音里
 * 自己找改动,而且**没有行号** —— 想知道「这段改在第几行」只能回编辑器数。
 * 这里把原文拆成带行号的行模型,让组件只管画。
 *
 * 不变式:`lines` 的顺序就是原文顺序,一行都不重排。diff 的价值在于上下文相邻,
 * 排序/折叠/合并同类项都会让它不再是那份 git 生成的东西。
 *
 * 故意不做的事:
 * - **不做左右分栏(side-by-side)**。这一栏最窄时只有几百像素(左栏 340px 是固定的),
 *   劈成两半之后每一半都要横向滚动,比单栏更难读。
 * - **不做词级高亮**。`git diff` 只告诉我们「这一行没了、那一行来了」,谁和谁配对
 *   是重算一遍才知道的事;这里不重算,所以产出的每一行都是一整段 `hi: false`。
 *
 * 语法高亮的那条「故意不做」已经撤销 —— 现在由 `useDiffSyntax` 异步补上,
 * 原先的理由(同步渲染时跑语法解析会把面板卡住半秒)在那个文件里写明了怎么绕开。
 *
 * ★ **`---` / `+++` 只在 hunk 之外才是文件头。** 一行被删掉的正文如果本身以 `--`
 *   开头(Markdown 的分隔线、C 的注释、`--flag` 的文档),它在 diff 里就长成 `---…`。
 *   按前缀无条件当文件头丢掉的表现是:**删掉一条分隔线的改动在界面上凭空消失**,
 *   而且后面所有行的行号都往前串一位。所以这里靠 `@@` 维护 inHunk 状态来判。
 *
 * (原 `views/git/diff-model.ts`。行模型本身挪去了 `./model.ts`,两套 diff 现在
 *  共用同一份 `DiffLine` 和同一个渲染组件。)
 */

import { plainLine, type DiffLine } from './model'

export interface ParsedDiff {
  lines: DiffLine[]
  /** 新增 / 删除的行数。统计覆盖**整份** diff,即使 `lines` 被 limit 截断了 */
  added: number
  removed: number
  /** 截断前本该有多少行(用于「只显示了前 N 行」那句话) */
  total: number
}

/** 纯噪音的文件头。只在 hunk 之外成立,见文件头 ★。 */
const NOISE = ['diff --git ', 'index ', '--- ', '+++ ']

/**
 * `@@ -12,7 +12,9 @@` → 起始行号。`,count` 可以省略(表示 1 行)。
 * 尾部可能还跟着函数名(`@@ -1,2 +1,2 @@ function foo()`),不参与解析。
 */
const HUNK = /^@@+ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/

/**
 * @param limit 最多产出多少行 —— 超出的部分只统计不落 `lines`,理由见
 *              `GitFeature` 里的 `DIFF_LINE_LIMIT`。
 */
export function parseUnifiedDiff(text: string, limit: number): ParsedDiff {
  const lines: DiffLine[] = []
  let added = 0
  let removed = 0
  let total = 0
  let inHunk = false
  let oldLine = 0
  let newLine = 0

  const source = text.split('\n')
  // git 的输出以换行结尾,split 之后末尾留一个空串 —— 它不是 diff 的一行
  if (source.length > 0 && source[source.length - 1] === '') source.pop()

  for (const line of source) {
    if (!inHunk && NOISE.some((prefix) => line.startsWith(prefix))) continue

    const hunk = HUNK.exec(line)
    if (hunk !== null) {
      inHunk = true
      oldLine = Number.parseInt(hunk[1] ?? '1', 10)
      newLine = Number.parseInt(hunk[2] ?? '1', 10)
      total += 1
      if (lines.length < limit) lines.push(plainLine('hunk', line))
      continue
    }

    if (!inHunk) {
      // 文件头里剩下的:new file mode / deleted file mode / rename from / similarity index…
      if (line === '') continue
      total += 1
      if (lines.length < limit) lines.push(plainLine('meta', line))
      continue
    }

    const mark = line[0]
    if (mark === '+') {
      added += 1
      total += 1
      if (lines.length < limit) lines.push(plainLine('add', line.slice(1), null, newLine))
      newLine += 1
    } else if (mark === '-') {
      removed += 1
      total += 1
      if (lines.length < limit) lines.push(plainLine('del', line.slice(1), oldLine, null))
      oldLine += 1
    } else if (mark === '\\') {
      // `\ No newline at end of file` —— 不占任何一侧的行号
      total += 1
      if (lines.length < limit) lines.push(plainLine('meta', line))
    } else {
      // 上下文行(前导空格)。空行在 diff 里也可能真的是空串,同样当上下文
      total += 1
      if (lines.length < limit) {
        lines.push(plainLine('context', mark === ' ' ? line.slice(1) : line, oldLine, newLine))
      }
      oldLine += 1
      newLine += 1
    }
  }

  return { lines, added, removed, total }
}
