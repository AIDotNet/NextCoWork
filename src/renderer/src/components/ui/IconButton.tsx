import { Button } from '../arc/button/button'

/**
 * 纯图标按钮 —— Arc 的 `Button` 加上方形尺寸。
 *
 * ★ **Arc 没有纯图标按钮**,所以保留这一层。按压、悬停、禁用、键盘焦点全是 Arc Button
 *   的;这里只负责三件 Arc 给不了的事:
 *
 *   1. **方形。** Arc 的 `sm` 带左右内边距和 `--control-height-sm` 的最小高度,
 *      一颗图标放进去是个扁长条。标题栏、工具栏都是按 24/28px 的方块排的,
 *      所以宽、高、最小高度、内边距用行内样式钉死 —— Arc 的样式是 CSS Modules
 *      (不在层里),Tailwind 工具类压不住它,行内样式压得住。
 *   2. **`active`。** 「这个面板正开着 / 当前项」—— 用 Arc 的 `secondary`(有底有边)
 *      表达,静息态是 `ghost`。
 *   3. **`pressed`。** 见下面那个参数的注释。
 */
export function IconButton({
  children,
  label,
  onClick,
  active = false,
  pressed,
  disabled = false,
  size = 28,
  width,
  className,
  title
}: {
  children: React.ReactNode
  label: string
  onClick?: () => void
  active?: boolean
  /**
   * 开关按钮(加粗、倾斜……)的按下状态,给读屏读「已按下 / 未按下」。
   * ★ 和 `active` 分开:`active` 在宿主里多半是「这个面板正开着 / 当前项」,不是开关,
   *   一律报 aria-pressed 会把导航项读成开关。缺省 = 不是开关,不输出这个属性。
   */
  pressed?: boolean
  disabled?: boolean
  /** 边长(方形)。给了 `width` 时它只当高度用 */
  size?: number
  /**
   * 单独指定宽度 —— 标题栏那条上的开关**不是方的**:「展开侧边栏」和「工作区文件」
   * 两个盒子都是 38×28,按方形画出来会比参考窄一圈,和红绿灯也对不齐。
   */
  width?: number
  className?: string
  title?: string
}): React.ReactNode {
  return (
    <Button
      type="button"
      variant={active ? 'secondary' : 'ghost'}
      size="sm"
      aria-label={label}
      aria-pressed={pressed}
      title={title ?? label}
      disabled={disabled}
      onClick={onClick}
      className={className}
      style={{ width: width ?? size, height: size, minHeight: size, paddingInline: 0, flexShrink: 0 }}
    >
      {children}
    </Button>
  )
}
