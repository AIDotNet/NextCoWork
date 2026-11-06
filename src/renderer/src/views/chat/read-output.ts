/**
 * `Read` 的输出 → 代码 + 起始行号。
 *
 * ★★ 需求:展开一次 `Read` 要看到**带高亮的代码**,而 `fs.ts` 给模型的格式是
 * `cat -n`(右对齐 6 位行号 + 制表符 + 正文)。那串行号如果原样喂给语法高亮器,
 * 每一行都以数字开头 —— 解析出来的树和真正的源码毫无关系,染出来的色比不染更糟
 * (它看起来像「高亮坏了」,而不是「这里没有高亮」)。
 *
 * 所以这里把行号**拆出来**:正文交给高亮器,行号交给左边那一栏。
 *
 * ★ **认不出格式就原样返回**,`startLine` 给 undefined:`Read` 带 offset 时
 * 行号不从 1 开始,而别的工具(WebFetch 的正文、MCP 的返回)压根没有行号。
 * 猜错一行号比不画行号糟得多 —— 用户会照着它去跳转。
 */

export interface NumberedOutput {
  /** 去掉行号栏之后的正文 */
  code: string
  /** 第一行对应的文件行号;不是 `cat -n` 格式时为 undefined */
  startLine?: number
}

/** `     1\tcontent` —— 行号右对齐,后面跟一个制表符(与 `fs.ts` 的 padStart(6) 对齐) */
const NUMBERED_RE = /^\s*(\d+)\t(.*)$/

export function parseNumberedOutput(content: string): NumberedOutput {
  if (content === '') return { code: '' }
  const lines = content.split('\n')
  const stripped: string[] = []
  let first: number | undefined
  let previous: number | undefined

  for (const line of lines) {
    // 末尾空行不参与判定:`cat -n` 的输出常以换行收尾
    if (line === '' && stripped.length === lines.length - 1) {
      stripped.push('')
      continue
    }
    const m = NUMBERED_RE.exec(line)
    if (m === null) return { code: content }
    const n = Number(m[1])
    // ★ 行号必须是连续递增的。不连续说明这不是一份 `cat -n`,而是**正文里**
    //   碰巧每行都以数字加制表符开头(TSV、日志)—— 那种文件把第一列当行号切掉,
    //   用户会以为工具吃掉了他的数据。
    if (previous !== undefined && n !== previous + 1) return { code: content }
    if (first === undefined) first = n
    previous = n
    stripped.push(m[2] ?? '')
  }

  if (first === undefined) return { code: content }
  return { code: stripped.join('\n'), startLine: first }
}

/*
 * `languageOf` 搬去了 `components/code/language.ts` —— 改动审查和 Git 面板现在也要
 * 从路径推语言,留在这里就得跨视图 import。理由(为什么只给扩展名、为什么不自己
 * 维护语言表)原样跟着搬过去了。
 */
