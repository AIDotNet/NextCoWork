/**
 * 代码对比视图(`components/diff/CodeDiffViewer.tsx`)的文案。
 *
 * Git 面板和「改动审查」画的是同一个对比视图,文案不属于其中任何一边,所以单独一个文件。
 *
 * `diff.viewer.unchangedLines` 里的 `$` 是 CodeMirror 的占位符(phrase 机制自己替换),
 * 不是我们的 `{count}` —— 这条文案是递给编辑器的,不经过 `t()` 的插值。
 */

type Params = Record<string, string | number>

export const diffZh = {
  'diff.viewer.toolbar': '对比视图工具栏',
  'diff.viewer.layout': '对比方式',
  'diff.viewer.unified': '行内',
  'diff.viewer.split': '并排',
  'diff.viewer.wrap': '自动换行',
  'diff.viewer.previous': '上一处改动 (⇧F7)',
  'diff.viewer.next': '下一处改动 (F7)',
  'diff.viewer.added': ({ count }: Params) => `新增 ${String(count)} 行`,
  'diff.viewer.removed': ({ count }: Params) => `删除 ${String(count)} 行`,
  'diff.viewer.original': '改动前:{path}',
  'diff.viewer.modified': '改动后:{path}',
  'diff.viewer.unchangedLines': '$ 行未改动,点击展开'
} as const

export const diffEn = {
  'diff.viewer.toolbar': 'Diff toolbar',
  'diff.viewer.layout': 'Diff layout',
  'diff.viewer.unified': 'Inline',
  'diff.viewer.split': 'Side by side',
  'diff.viewer.wrap': 'Wrap lines',
  'diff.viewer.previous': 'Previous change (⇧F7)',
  'diff.viewer.next': 'Next change (F7)',
  'diff.viewer.added': ({ count }: Params) => `${String(count)} lines added`,
  'diff.viewer.removed': ({ count }: Params) => `${String(count)} lines removed`,
  'diff.viewer.original': 'Before: {path}',
  'diff.viewer.modified': 'After: {path}',
  'diff.viewer.unchangedLines': '$ unchanged lines, click to expand'
} as const
