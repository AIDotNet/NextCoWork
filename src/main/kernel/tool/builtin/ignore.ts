/**
 * 遍历时跳过哪些目录。**一张硬编码的表,不解析 `.gitignore`。**
 *
 * ★ 这个取舍值得写清楚:gitignore 是一门完整的模式语言(嵌套文件、否定规则、
 * 双星、目录锚定、`git check-ignore` 的优先级)。半套实现产出的不是「少几个结果」,
 * 而是**静默的错误答案** —— grep 说仓库里没有这个字符串,而它就在那儿。
 * 一张写死的表至少是可预测的:它跳过什么,`glob` / `grep` 的 description 里直说。
 *
 * 表里都是「生成物 / 依赖 / 工具缓存」,不是源码。代价是:如果用户真有一个叫
 * `dist` 的源码目录,这里会漏掉它 —— 那种情况用 `bash` 里的 `find` 兜底。
 */

/**
 * ★ 只按**目录名**匹配,不按路径。于是 `node_modules` 在任何层级都跳过,
 * 而不用去想它是 `./node_modules` 还是 `packages/a/node_modules`。
 */
export const DEFAULT_SKIP_DIRS: ReadonlySet<string> = new Set([
  // 版本控制
  '.git',
  '.hg',
  '.svn',
  // 依赖
  'node_modules',
  'bower_components',
  'vendor',
  '.yarn',
  '.pnpm-store',
  // 构建产物
  'dist',
  'out',
  'build',
  'target',
  '.next',
  '.nuxt',
  '.svelte-kit',
  '.angular',
  '.output',
  // 缓存
  '.cache',
  '.turbo',
  '.parcel-cache',
  '.gradle',
  '__pycache__',
  '.pytest_cache',
  '.mypy_cache',
  '.ruff_cache',
  '.tox',
  // 环境 / IDE
  '.venv',
  'venv',
  '.idea',
  'Pods',
  // 覆盖率
  'coverage',
  '.nyc_output'
])

/** 无论如何都不该出现在搜索结果里的文件名。 */
export const SKIP_FILES: ReadonlySet<string> = new Set(['.DS_Store', 'Thumbs.db'])

/**
 * ★ **不跳过点开头的目录**(表里明确列出的那几个除外)。
 *
 * `.github` / `.claude` / `.next-cowork` / `.vscode` 里全是用户真的想让模型看见的东西。
 * 「点开头 = 隐藏 = 不重要」这个直觉在代码仓库里是反的。
 */
export function defaultSkip(name: string, isDir: boolean): boolean {
  return isDir ? DEFAULT_SKIP_DIRS.has(name) : SKIP_FILES.has(name)
}
