/**
 * remark-math 只认 `$…$` / `$$…$$`,而不少模型输出的是 LaTeX 风格的 `\(…\)` / `\[…\]`,
 * 不转换就会把 `\times` 之类当普通文本显示。这里在渲染前把它们改写成 `$` 形式。
 * 围栏代码块和行内代码里的内容原样保留。
 */
const CODE_SPAN = /(```[\s\S]*?(?:```|$)|~~~[\s\S]*?(?:~~~|$)|`+[^`\n]*`+)/g

function convert(text: string): string {
  return text
    .replace(/\\\[([\s\S]+?)\\\]/g, (_, body: string) => `\n$$\n${body.trim()}\n$$\n`)
    .replace(/\\\(([\s\S]+?)\\\)/g, (_, body: string) => `$${body.trim()}$`)
}

export function normalizeMathDelimiters(content: string): string {
  if (!content.includes('\\(') && !content.includes('\\[')) return content
  return content.split(CODE_SPAN).map((part, index) => index % 2 === 1 ? part : convert(part)).join('')
}
