/**
 * diff 的统一行模型 —— 两个生产者与唯一的渲染组件之间的那张契约。
 *
 * 需求:仓库里曾经有两套互不相干的 diff。工具卡 / 改动审查那套自己算 LCS、带词级
 * 高亮;Git 面板那套解析 `git diff` 的文本、纯文本无高亮。两套各有一份行底色、
 * 行号栏宽度和换行策略,于是同一份改动在两个界面里长得不一样,而修一处的 bug
 * 永远漏掉另一处。这里只定义「一行 diff 是什么」,不关心它从哪来:
 *
 *     computeDiff / computeDiffHunks(before+after)  ┐
 *                                                    ├→ DiffLine[] → <DiffLines/>
 *     parseUnifiedDiff(git diff 文本)                ┘
 *
 * 不变式:`spans` 拼起来就是这一行的正文,**不含**前导的 `+` / `-` / 空格 ——
 * 那个符号是渲染的事(见 DiffLines 里的符号列),不是内容的一部分。
 *
 * 故意不做的事:模型里不存样式类名、不存已翻译文案。样式只在 `DiffLines` 一处,
 * 文案在调用方 —— 否则这个文件会慢慢长成第二个组件。
 */

export type DiffLineKind =
  /** `@@ -a,b +c,d @@` 那一行 */
  | 'hunk'
  /** 文件头里值得留的那几行(new file mode / rename from…)、`\ No newline at end of file` */
  | 'meta'
  | 'add'
  | 'del'
  | 'context'

/** 一行里的一段文本;`hi` 表示这一段相对另一侧是新增/删除的(词级高亮)。 */
export interface DiffSpan {
  text: string
  hi: boolean
}

export interface DiffLine {
  kind: DiffLineKind
  /** 正文,已按词级高亮切段;没有词级信息时就是一段 `hi: false` */
  spans: DiffSpan[]
  /** 旧文件里的行号。新增行、以及不提供行号的生产者给 null */
  oldLine: number | null
  /** 新文件里的行号。删除行、以及不提供行号的生产者给 null */
  newLine: number | null
}

/** 整行正文。渲染、语法高亮、测试断言都要它 —— 别在各处自己 join。 */
export function lineText(line: DiffLine): string {
  return line.spans.map((span) => span.text).join('')
}

/** 没有词级信息的一行(git 解析出来的、或整行都算改动的那种)。 */
export function plainLine(
  kind: DiffLineKind,
  text: string,
  oldLine: number | null = null,
  newLine: number | null = null
): DiffLine {
  return { kind, spans: [{ text, hi: false }], oldLine, newLine }
}
