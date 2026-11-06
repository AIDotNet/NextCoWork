import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { cn } from '../../lib/cn'

const CONTEXT_MENU_MS = 150

/**
 * 需求:「滚动即关」只能关**菜单所指着的那块表面**滚了的情况,别处的滚动不关。
 * 不满足会怎样:run 执行期间 `Thread` 的 follow() 每来一段流式内容就写一次
 * scrollTop、派发一个 scroll 事件(见 views/chat/Thread.tsx 的 selfScrolled 注释),
 * 全当成「视口变了」的话,表现为「Agent 跑着的时候右键标签,菜单一闪就没」。
 *
 * 判据是**几何**而不是 DOM 归属:调用方只给了坐标、没给被点的元素,而滚动容器的
 * 盒子盖住右键那一点 ⇔ 被点的那块内容就在这个容器里。盖住 = 菜单指着的内容动了,
 * 菜单和它已经对不上了;不盖 = 是无关面板在滚(聊天区自动跟随、工具卡片里的
 * 子滚动条),菜单指着的东西原地没动。
 * document / window 没有盒子,整页滚动一律算「表面动了」,维持原本的关闭行为。
 */
function scrollsUnderAnchor(target: EventTarget | null, anchor: ContextMenuPosition): boolean {
  if (!(target instanceof Element)) return true
  const box = target.getBoundingClientRect()
  return anchor.x >= box.left && anchor.x <= box.right && anchor.y >= box.top && anchor.y <= box.bottom
}

export interface ContextMenuPosition {
  x: number
  y: number
}

/** Pointer-positioned menu used by right-click actions inside scrollable panels. */
export function ContextMenu({
  position,
  label,
  width = 192,
  onClose,
  containsTarget,
  children
}: {
  position: ContextMenuPosition
  label: string
  width?: number
  onClose: () => void
  /**
   * 需求:二级菜单(文件树右键的「打开方式 ›」)portal 在 body 下,DOM 上不在这个面板里。
   * 不把它算作「里面」的话,点子菜单那一下 pointerdown 会先把整个菜单关掉,
   * 表现是子菜单里的项永远点不中。与 `Menu` 的同名参数同义。
   */
  containsTarget?: (target: Node) => boolean
  children: (close: () => void) => ReactNode
}): ReactNode {
  const panelRef = useRef<HTMLDivElement>(null)
  const [placed, setPlaced] = useState<{ left: number; top: number } | null>(null)
  const [shown, setShown] = useState(false)
  const [closing, setClosing] = useState(false)
  const closingRef = useRef(false)
  const closeTimer = useRef<ReturnType<typeof setTimeout> | null>(null)

  // Context menus are conditionally mounted by their callers, so defer the
  // caller's unmount just long enough for the panel to finish its exit.
  const close = useCallback((): void => {
    if (closingRef.current) return
    closingRef.current = true
    setClosing(true)
    closeTimer.current = setTimeout(onClose, CONTEXT_MENU_MS)
  }, [onClose])

  useEffect(() => {
    const frame = requestAnimationFrame(() => setShown(true))
    return () => {
      cancelAnimationFrame(frame)
      if (closeTimer.current !== null) clearTimeout(closeTimer.current)
    }
  }, [])

  useLayoutEffect(() => {
    const panel = panelRef.current
    if (panel === null) return
    const margin = 8
    setPlaced({
      left: Math.max(margin, Math.min(position.x, window.innerWidth - panel.offsetWidth - margin)),
      top: Math.max(margin, Math.min(position.y, window.innerHeight - panel.offsetHeight - margin))
    })
    panel.querySelector<HTMLButtonElement>('[role="menuitem"]:not(:disabled)')?.focus()
  }, [position])

  useEffect(() => {
    // A new right-click can reuse this component while the previous menu is
    // fading out. Cancel that stale close callback and treat the new position
    // as a fresh opening instead of allowing the old menu to close the new one.
    if (!closingRef.current) return
    if (closeTimer.current !== null) clearTimeout(closeTimer.current)
    closingRef.current = false
    setClosing(false)
    setShown(false)
    const frame = requestAnimationFrame(() => setShown(true))
    return () => cancelAnimationFrame(frame)
  }, [position])

  useEffect(() => {
    if (closing) return
    const inside = (target: Node): boolean =>
      panelRef.current?.contains(target) === true || containsTarget?.(target) === true
    const onPointerDown = (event: PointerEvent): void => {
      if (!inside(event.target as Node)) close()
    }
    const onKeyDown = (event: KeyboardEvent): void => {
      if (event.key !== 'Escape' || event.defaultPrevented) return
      event.preventDefault()
      close()
    }
    const onViewportChange = (): void => close()
    // 菜单自己(或它的子菜单)内部滚动不是「视口变了」:子菜单项多到要滚时,
    // 不排除的话一滚轮就把整份菜单关掉。
    // 同理,滚动不在菜单指着的那块表面上时也不关 —— 理由见 scrollsUnderAnchor。
    const onScroll = (event: Event): void => {
      if (event.target instanceof Node && inside(event.target)) return
      if (!scrollsUnderAnchor(event.target, position)) return
      close()
    }
    document.addEventListener('pointerdown', onPointerDown, true)
    document.addEventListener('keydown', onKeyDown)
    window.addEventListener('resize', onViewportChange)
    document.addEventListener('scroll', onScroll, true)
    return () => {
      document.removeEventListener('pointerdown', onPointerDown, true)
      document.removeEventListener('keydown', onKeyDown)
      window.removeEventListener('resize', onViewportChange)
      document.removeEventListener('scroll', onScroll, true)
    }
  }, [close, closing, containsTarget, position])

  return createPortal(
    <div
      ref={panelRef}
      role="menu"
      aria-label={label}
      onContextMenu={(event) => event.preventDefault()}
      style={{
        width,
        left: placed?.left ?? position.x,
        top: placed?.top ?? position.y,
        visibility: placed === null ? 'hidden' : undefined
      }}
      className={cn(
        'app-no-drag fixed z-50 rounded-[10px] border-0 bg-surface-menu p-[5px] shadow-menu outline-none',
        'transition-[opacity,transform,translate,scale] duration-150 ease-panel motion-reduce:transition-none motion-reduce:transform-none motion-reduce:translate-y-0 motion-reduce:scale-100',
        shown && !closing
          ? 'translate-y-0 scale-100 opacity-100'
          : 'pointer-events-none translate-y-0.5 scale-[.98] opacity-0'
      )}
    >
      {children(close)}
    </div>,
    document.body
  )
}
