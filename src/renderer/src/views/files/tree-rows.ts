/**
 * 文件树**行级**的纯逻辑:键盘导航、以及哪些行是「刚出现的」(要播入场动画)。
 *
 * 需求:右侧文件树对照 beUI 的 File Tree(beui.dev/components/motion/file-tree)优化 ——
 * 完整的键盘导航(↑↓ / Home End / → 展开或进入 / ← 收起或回到父目录)与
 * 「展开目录时子项依次落下」。那个组件本身吃的是一棵**一次性声明**的 JSX 树,而我们的树是
 * 懒加载、平铺渲染的(见 `flatten.ts`),所以没有照搬组件,只把这两段行为抽成纯函数落在这里。
 *
 * 单独成文件与 `flatten.ts` 同理:`.tsx` 一 import 就拖进 React / store,node 环境的
 * vitest 跑不起来;这里只认 `Row[]`,可测。
 */
import type { Row } from './flatten'

export type TreeKeyAction =
  | { kind: 'focus'; index: number }
  | { kind: 'expand'; path: string }
  | { kind: 'collapse'; path: string }
  | { kind: 'activate' }

/**
 * 一次按键在第 `index` 行上意味着什么。返回 null = 这个键不归树管(调用方不要 preventDefault,
 * 否则 Tab 之类的键会被吞掉)。
 *
 * ★ ← 的「回到父目录」是在**平铺后的行数组**里往回找第一个 `depth - 1` 的行,而不是按路径
 *   切父目录:搜索过滤时中间的行会被撤掉(`flatten` 保留命中项的祖先),但祖先行一定在,
 *   按行找永远落得到实处;按路径切的话,子树根(`rootPath` 非空)下还得再算一次相对层级。
 */
export function treeKeyAction(
  rows: readonly Row[],
  index: number,
  key: string,
  expanded: ReadonlySet<string>
): TreeKeyAction | null {
  const row = rows[index]
  if (row === undefined) return null
  switch (key) {
    case 'ArrowDown':
      return index + 1 < rows.length ? { kind: 'focus', index: index + 1 } : null
    case 'ArrowUp':
      return index > 0 ? { kind: 'focus', index: index - 1 } : null
    case 'Home':
      return { kind: 'focus', index: 0 }
    case 'End':
      return { kind: 'focus', index: rows.length - 1 }
    case 'ArrowRight': {
      if (row.entry.kind !== 'dir') return null
      if (!expanded.has(row.entry.path)) return { kind: 'expand', path: row.entry.path }
      // 已展开:进到第一个子项;子项还没列回来(或目录是空的)就停在原地
      const next = rows[index + 1]
      return next !== undefined && next.depth === row.depth + 1 ? { kind: 'focus', index: index + 1 } : null
    }
    case 'ArrowLeft': {
      if (row.entry.kind === 'dir' && expanded.has(row.entry.path)) return { kind: 'collapse', path: row.entry.path }
      for (let i = index - 1; i >= 0; i--) {
        if (rows[i]?.depth === row.depth - 1) return { kind: 'focus', index: i }
      }
      return null
    }
    case 'Enter':
    case ' ':
      return { kind: 'activate' }
    default:
      return null
  }
}

/**
 * 入场动画的节奏,取自 beUI File Tree 的 `ROW_ENTER`:每行错开 25ms,最多错开到 100ms。
 * (时长与位移在 `theme.css` 的 `.file-tree-row-enter` 里,同一出处。)
 */
const ENTER_STEP_MS = 25
const ENTER_MAX_DELAY_MS = 100
/**
 * ★ 一次最多给这么多行播入场。展开 `node_modules` 一下就是上千行,上千个同时跑的
 *   CSS 动画会让那一帧明显掉帧;而视口里一屏也就三四十行,超出的那些本来就看不见。
 */
const ENTER_LIMIT = 40

/**
 * 这一次相对上一次**新出现**的行 → 各自的入场延迟(ms)。
 *
 * 首帧由调用方把 `previous` 设成与 `rows` 相同(于是一行都不算新):从缓存恢复出来的
 * 那棵树不该每次切回来都「重新落一遍」—— 那正是要消除的「回来时树像被重置了」的观感。
 */
export function enterDelays(previous: readonly Row[], rows: readonly Row[]): ReadonlyMap<string, number> {
  const before = new Set(previous.map((row) => row.entry.path))
  const delays = new Map<string, number>()
  for (const row of rows) {
    if (before.has(row.entry.path)) continue
    if (delays.size >= ENTER_LIMIT) break
    delays.set(row.entry.path, Math.min(delays.size * ENTER_STEP_MS, ENTER_MAX_DELAY_MS))
  }
  return delays
}
