/**
 * 文件路径 → 高亮器认得的语言名。
 *
 * 需求:三个调用方(工具卡、改动审查、Git 面板)都要从一个路径推出语言,
 * 而它原先住在 `views/chat/read-output.ts` 里 —— 另外两个视图要用就只能跨视图
 * import,或者各自再写一份「取扩展名」。
 *
 * ★ 直接把**扩展名**交给 `@codemirror/language-data`(`highlight.ts` 里那条
 * `matchFilename('snippet.' + language)` 的路径),不自己维护一张语言表 ——
 * 维护第二张表的结果一定是它和编辑器认得的语言集慢慢分叉。
 * 没有扩展名就返回空串,高亮器据此直接返回「无 span」,正文照常显示。
 */
export function languageOf(path: string | undefined): string {
  if (path === undefined || path === '') return ''
  const name = path.slice(Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\')) + 1)
  const at = name.lastIndexOf('.')
  return at <= 0 ? '' : name.slice(at + 1).toLowerCase()
}
