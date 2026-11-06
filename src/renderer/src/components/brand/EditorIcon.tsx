/**
 * 「打开方式」菜单里 IDE / 终端的品牌字形。
 *
 * ## 字形从哪来
 *
 * 全部来自 **simple-icons**(CC0-1.0),原样落在 `assets/editors/*.svg` —— 每个
 * 都是单色的 `fill="currentColor"` 剪影,于是深浅主题、hover、禁用态各写一遍的
 * 事就免了(和 `ProviderIcon` 选 `?raw` 内联而不是 `<img>` 是同一条理由:
 * `<img src>` 里的 svg 是独立文档,继承不到外面的 color)。
 *
 * ★ **为什么不从 `@lobehub/icons-static-svg` 拿。** 那套里只有 Cursor /
 *   Windsurf / Zed 三个,VS Code、JetBrains 全家、Xcode 都没有 —— 混两个来源
 *   的结果是一列图标里两种笔画粗细、两种留白比例,一眼能看出是拼的。
 *   `ProviderIcon` 继续用 lobehub(它是模型/供应商的品牌表,和这里不是一回事)。
 *
 * ## 为什么是两张表
 *
 * `MARK` 只覆盖「有字形」的那些 id,文件管理器 / 终端 / 默认应用走 `FALLBACK` 里的
 * lucide 图标:系统的文件管理器叫什么、终端是哪个 app,各平台都不同,
 * 拿某一个产品的 logo 去代表「文件管理器」是错的(装了别的终端的用户会觉得
 * 我们在说另一个东西)。
 *
 * 两张表加起来必须**恰好**盖满 `OpenTargetIcon` —— 少一个编译不过,多一个
 * 也编译不过(和 `ProviderIcon` 的 `RASTER` 同一条规矩)。
 */
import type { ReactNode } from 'react'
import { AppWindow, FolderOpen, TerminalSquare } from 'lucide-react'
import type { OpenTargetIcon } from '../../../../shared/domain/open-target'
import { cn } from '../../lib/cn'

import androidstudio from '../../assets/editors/androidstudio.svg?raw'
import clion from '../../assets/editors/clion.svg?raw'
import cursor from '../../assets/editors/cursor.svg?raw'
import goland from '../../assets/editors/goland.svg?raw'
import intellij from '../../assets/editors/intellij.svg?raw'
import phpstorm from '../../assets/editors/phpstorm.svg?raw'
import pycharm from '../../assets/editors/pycharm.svg?raw'
import rider from '../../assets/editors/rider.svg?raw'
import rubymine from '../../assets/editors/rubymine.svg?raw'
import sublime from '../../assets/editors/sublime.svg?raw'
import vscode from '../../assets/editors/vscode.svg?raw'
import webstorm from '../../assets/editors/webstorm.svg?raw'
import windsurf from '../../assets/editors/windsurf.svg?raw'
import xcode from '../../assets/editors/xcode.svg?raw'
import zed from '../../assets/editors/zed.svg?raw'

/** 有品牌字形的那些 id。 */
type MarkIcon = Exclude<OpenTargetIcon, 'file-manager' | 'terminal' | 'default-app'>

const MARK: Record<MarkIcon, string> = {
  vscode, cursor, windsurf, zed, sublime, intellij, pycharm, webstorm,
  goland, clion, rider, phpstorm, rubymine, androidstudio, xcode
}

/**
 * 没有品牌字形的两个通用目标。
 *
 * ★ 图标 id 与图形之间的对应关系必须**在这里是穷尽的** —— 主进程新增一个
 *   IDE 时,它会先在 `shared/domain/open-target.ts` 的联合里加一项,于是这里
 *   当场编译不过。没有这一步,漏配的表现是一列菜单里出现一颗空白(严格说是
 *   上一项的图标),而它不报错。
 */
const FALLBACK: Record<Extract<OpenTargetIcon, 'file-manager' | 'terminal' | 'default-app'>, ReactNode> = {
  'file-manager': <FolderOpen />,
  terminal: <TerminalSquare />,
  // 系统默认应用是「交给系统挑」,没有哪一个产品的 logo 能代表它
  'default-app': <AppWindow />
}

/**
 * @param icon 主进程下发的图标 id。
 * @param size 边长。
 *
 * ★ 尺寸**必须由外层显式给**,不能像 `ProviderIcon` 那样只设 `font-size`:
 *   lobehub 的 svg 自带 `width="1em"`,而我们 vendored 的这批只有 `viewBox` ——
 *   内联 svg 缺 width/height 时按 100% 解析,于是它量的是外层盒子的尺寸,
 *   而外层盒子又想由内容撑开,结果是**两个都塌成 0**,菜单里一排空白图标。
 *   显式宽高 + `[&>svg]:size-full` 把这条依赖断掉,两个来源都稳。
 *
 * ★ 图标一律 `aria-hidden`:每一项旁边就有产品名,读屏读两遍是噪音
 *   (和 `ProviderIcon` 不传 `label` 时同一条规矩)。
 */
export function EditorIcon({ icon, size = 14, className }: {
  icon: OpenTargetIcon
  size?: number
  className?: string
}): ReactNode {
  const fallback = FALLBACK[icon as keyof typeof FALLBACK]
  const box = 'inline-flex shrink-0 items-center justify-center [&>svg]:size-full'
  if (fallback !== undefined) {
    return (
      <span aria-hidden style={{ width: size, height: size }} className={cn(box, className)}>
        {fallback}
      </span>
    )
  }
  return (
    <span
      aria-hidden
      style={{ width: size, height: size }}
      className={cn(box, className)}
      dangerouslySetInnerHTML={{ __html: MARK[icon as MarkIcon] }}
    />
  )
}
