/**
 * 二级菜单面板 —— 挂在父菜单的某一行旁边(文件树右键的「打开方式 ›」)。
 *
 * 需求:父菜单(`ContextMenu` / `Menu`)的面板带着 `translate` / `scale` 入场动效,
 * 那两条会给后代建立**包含块**,于是嵌在里面的 `fixed` 面板按父面板定位,
 * 表现是子菜单飞到屏幕外(`components/OpenWithMenu.tsx` 文件头记过这次翻车)。
 * 所以这里 `createPortal` 到 body,再按锚点那一行的 `getBoundingClientRect()` 摆。
 *
 * ★ portal 之后子菜单在 DOM 上**不在**父面板里:父面板的「点外面就关」必须把它算作
 *   里面 —— 调用方把 `panelRef` 接到父组件的 `containsTarget` 上(`ContextMenu` / `Menu`
 *   都有这个参数)。漏接的表现是子菜单里的项永远点不中。
 *
 * 故意不做:入场动效(子菜单是跟手出现的,动效只会让横移时看起来慢半拍 ——
 * `ProviderModelMenu` 的第二级也没有)、自己的关闭逻辑(Esc / 点外面都由父菜单统一管)。
 *
 * `ProviderModelMenu` 的 `ModelSubmenu` 是更早的一份同类实现,落点算法写在组件里;
 * 这一份的落点在 `menu-position.ts` 的 `placeSubmenu`(有测试)。两者暂未合并。
 */
import { useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { placeSubmenu, type SubmenuPlacement } from './menu-position'
import { MenuHighlightReset } from './Menu'

export function Submenu({
  anchor,
  label,
  width = 220,
  autoFocus = false,
  panelRef,
  children
}: {
  /** 父菜单里触发它的那一行 */
  anchor: HTMLElement
  /** 无障碍名称;子菜单也是一个独立的 `role="menu"` */
  label: string
  width?: number
  /**
   * 打开时把焦点移进第一项。只有**键盘 / 点击**打开时才要:悬停打开时抢焦点,
   * 鼠标在父菜单里上下扫一遍就会把焦点拽来拽去。
   */
  autoFocus?: boolean
  /** 交给父菜单的 `containsTarget`,见文件头 ★ */
  panelRef: (node: HTMLDivElement | null) => void
  children: ReactNode
}): ReactNode {
  const node = useRef<HTMLDivElement | null>(null)
  const [placed, setPlaced] = useState<SubmenuPlacement | null>(null)

  useLayoutEffect(() => {
    const measure = (): void => {
      const panel = node.current
      if (panel === null) return
      setPlaced(placeSubmenu(anchor.getBoundingClientRect(), panel.scrollHeight, width, {
        width: window.innerWidth,
        height: window.innerHeight
      }))
    }
    measure()
    // 条目是异步探测回来的,高度会在挂载后变一次
    const observer = new ResizeObserver(measure)
    if (node.current !== null) observer.observe(node.current)
    window.addEventListener('resize', measure)
    return () => {
      observer.disconnect()
      window.removeEventListener('resize', measure)
    }
  }, [anchor, width])

  // 等量到位置再聚焦:`visibility: hidden` 的元素 focus() 不生效
  const ready = placed !== null
  useLayoutEffect(() => {
    if (!autoFocus || !ready) return
    node.current?.querySelector<HTMLButtonElement>('[role="menuitem"]:not(:disabled)')?.focus()
  }, [autoFocus, ready])

  return createPortal(
    <div
      ref={(element) => {
        node.current = element
        panelRef(element)
      }}
      role="menu"
      aria-label={label}
      onContextMenu={(event) => event.preventDefault()}
      style={{
        width,
        top: placed?.top ?? 0,
        left: placed?.left ?? 0,
        maxHeight: placed?.maxHeight,
        // 量到位置之前先渲染出来(否则量不到高度),但别让人看见左上角那一帧
        visibility: placed === null ? 'hidden' : undefined
      }}
      className="app-no-drag scroll-thin fixed z-[60] overflow-y-auto rounded-[10px] border-0 bg-surface-menu p-[5px] shadow-menu"
    >
      <MenuHighlightReset>{children}</MenuHighlightReset>
    </div>,
    document.body
  )
}
