/*
 * ★ 这里**只导出类型层面的 highlight**:`highlightCode` 的实现拽着
 * `@codemirror/language-data`(整套语法集)。它在 `CodeSource` / `useDiffSyntax`
 * 里是动态 import,一旦从这个桶里静态再导出一次,任何 `import { CodeBlock }`
 * 的代码块都会把那套语法集拖回自己的 chunk —— 打包后没有任何报错,只是首屏变慢。
 */
export { CodeBlock, type CodeBlockProps } from './CodeBlock'
export { CodeSource, reusableSpans, type Highlight } from './CodeSource'
export type { CodeSpan } from './highlight'
export { languageOf } from './language'
