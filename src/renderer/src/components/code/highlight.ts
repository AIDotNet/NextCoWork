/**
 * 语法高亮的**唯一**实现 —— markdown 围栏、工具卡代码块、diff 三处共用。
 *
 * 需求:同一个文件在聊天正文、展开详情和 diff 里必须染成同一个样子。所以全仓库
 * 只有这一处加载语法集,且复用编辑器自己的那一份(`@codemirror/language-data`);
 * 引第二个高亮库的结果一定是两套语法集慢慢分叉。
 *
 * (原 `components/markdown/highlight.ts`,内容未变,只是挪到不带 markdown 前缀的
 *  位置 —— 它的三个使用者里只有一个是 markdown。)
 */
import { LanguageDescription } from '@codemirror/language'
import { languages } from '@codemirror/language-data'
import { classHighlighter, highlightTree } from '@lezer/highlight'

export interface CodeSpan { from: number; to: number; className: string }

/** Reuse editor grammars, but render static, selectable code instead of mounting editors. */
export async function highlightCode(code: string, language: string): Promise<CodeSpan[]> {
  if (code.length > 100_000 || !language) return []
  const description = LanguageDescription.matchLanguageName(languages, language, false)
    ?? LanguageDescription.matchFilename(languages, `snippet.${language}`)
  if (!description) return []
  const support = await description.load()
  const spans: CodeSpan[] = []
  highlightTree(support.language.parser.parse(code), classHighlighter, (from, to, className) => {
    spans.push({ from, to, className })
  })
  return spans
}
