/**
 * `Bash` / `BashOutput` 那段结果文本的**解包**。
 *
 * ★★ 需求:展开一条命令看到的应该是一个终端,不是一段文本。而工具给模型的
 * 结果是有信封的 —— `bash.ts` 的 `section()` 把两条流各包一层:
 *
 *     <stdout>
 *     Name      Length
 *     </stdout>
 *     <stderr>
 *     warning: …
 *     </stderr>
 *
 * 那两对尖括号是**写给模型的**(它需要知道哪段是 stderr),直接画出来的话,
 * 用户会在自己的终端输出里读到两行凭空出现的 XML 标签 —— 这正是这次要修的样子。
 * 这里把它拆成「说明 + 若干条流」,标签交给渲染层用颜色表达。
 *
 * ★ **认不出信封就整段当 stdout**,不猜也不报错:`BashOutput` 的回读、
 * 「(command succeeded with no output)」、以及将来任何换了写法的输出都会落到这条 ——
 * 终端里少一点颜色,好过丢一段用户正要看的文字。
 *
 * 纯函数、无 DOM,所以单独成文件:信封格式变了要能在测试里一眼看出来(§9)。
 */

export type TerminalStream = 'stdout' | 'stderr'

export interface TerminalSection {
  stream: TerminalStream
  text: string
}

export interface TerminalOutput {
  /**
   * 信封之外的那几行 —— 失败时 `bash.ts` 会在最前面加一句
   * 「Command exited with code 1.」/ 超时那句。它不是命令吐出来的东西,
   * 所以单独一格,由渲染层画成提示而不是输出。
   */
  notice: string
  sections: readonly TerminalSection[]
}

/** `<stdout>` / `<stderr>` 各自成段;`[\s\S]` 是因为这里不能开 `s` 标志(ES2018 之外) */
const SECTION_RE = /<(stdout|stderr)>\n?([\s\S]*?)\n?<\/\1>/g

export function parseTerminalOutput(content: string): TerminalOutput {
  const sections: TerminalSection[] = []
  const outside: string[] = []
  let cursor = 0
  SECTION_RE.lastIndex = 0
  for (let m = SECTION_RE.exec(content); m !== null; m = SECTION_RE.exec(content)) {
    outside.push(content.slice(cursor, m.index))
    cursor = m.index + m[0].length
    const stream = m[1] === 'stderr' ? 'stderr' : 'stdout'
    const text = m[2] ?? ''
    if (text !== '') sections.push({ stream, text })
  }
  outside.push(content.slice(cursor))

  const notice = outside.join('\n').trim()
  // 没有信封:整段就是输出本身。此时 notice 留空 —— 否则同一段文字会被画两遍。
  if (sections.length === 0) {
    return { notice: '', sections: content.trim() === '' ? [] : [{ stream: 'stdout', text: content.replace(/\n+$/, '') }] }
  }
  return { notice, sections }
}
