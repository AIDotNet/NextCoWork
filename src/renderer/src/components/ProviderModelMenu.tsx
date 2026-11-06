/**
 * 供应商 → 模型的两级弹层菜单:先选供应商,悬停或点开后在旁边弹出这一家的模型子菜单。
 *
 * 输入框的模型药丸、设置页「通用 → Agent」的默认模型 / 默认子代理、图片模型、视频模型
 * 共用这一个 —— 四处要的是同一套交互(先选供应商、再选模型、当前项打勾)。
 *
 * ## 菜单本身用 Radix 的 DropdownMenu(含 Sub / SubTrigger / SubContent)
 *
 * 之前是 `ui/Menu` 套一个手写的二级面板(`createPortal` + 自己量位置、贴边翻转、
 * 跟 resize/scroll 重算 + 外层 `containsTarget` 防误关)。那一版有四个只能肉眼或键盘
 * 才发现的问题,这里都交给 Radix 解决:
 *   1. 鼠标从供应商行**斜着**移向子菜单时会扫过下面几行,子菜单跟着换成别家
 *      —— Radix 的子菜单有指针「安全区」,斜穿过去不会切换;
 *   2. 面板里没有方向键导航,二级面板挂在 body 末尾、焦点进不去
 *      —— ↑↓ 移动、→ / Enter 进子菜单、← 退回、首字母跳转都是原生的;
 *   3. 二级面板的定位、翻转、限高是手写的 —— 现在由 Radix 的 Popper 负责;
 *   4. 二级面板 `z-[60]` 挂在 body 下,压不过 z-100 的设置浮层
 *      —— Radix 的浮层统一由 `styles/arc-integration.css` 抬到 150。
 *
 * Arc 的 `dropdown-menu` 不能用在这里:它的触发器是固定的「文字 + 箭头」按钮,
 * 也没有二级菜单。Radix 是 Arc 菜单本身的底座,也是本仓库 `ui/Select` 用的同一个库。
 *
 * ★ `modal={false}`:和原来的 `ui/Menu` 一样,菜单开着时外面的页面照常能滚动。
 * ★ Esc 由 Radix 处理,它会 `preventDefault`,设置浮层那个 document 级的 Esc 监听
 *   看到 `defaultPrevented` 就不会连带关掉整个设置面板。
 * ★ 设置浮层里的菜单要夹在内容列里(`data-menu-bounds`,见 `ui/Menu.tsx`),
 *   这里把那块元素交给 Radix 当 `collisionBoundary`。
 *
 * ★ 这个组件不知道「供应商 / 模型该怎么过滤、怎么排序」——`rows` 由调用方算好
 *   再传进来。聊天输入框和设置页对「同一个别名能不能显示成待选项」的规则并不一样
 *   (设置页要在供应商已停用时仍然把它留在列表里方便改,输入框不需要),
 *   把这条规则拉平进共享层只会逼一边迁就另一边的隐藏假设。
 */
import { Check, ChevronRight } from 'lucide-react'
import { DropdownMenu } from 'radix-ui'
import { useRef, useState, type ReactNode } from 'react'
import { cn } from '../lib/cn'

export interface ProviderModelMenuModelOption {
  value: string
  label: string
  selected: boolean
}

export interface ProviderModelMenuRow {
  id: string
  label: string
  /** 副标题,比如「N 个可用模型」 */
  description?: string
  selected: boolean
  models: readonly ProviderModelMenuModelOption[]
}

/** 两级面板共用的外观 —— 与 `ui/Menu` 的面板同一套 token,入场用 @starting-style 淡入。 */
const PANEL = cn(
  'app-no-drag scroll-thin overflow-y-auto rounded-panel border-0 bg-surface-menu p-1.5',
  'shadow-menu outline-none',
  'transition-[opacity,scale] duration-150 ease-panel starting:scale-[.98] starting:opacity-0',
  'motion-reduce:transition-none'
)

const ITEM = cn(
  'flex w-full cursor-default select-none items-center gap-2.5 rounded-[8px] px-2.5 py-[7px]',
  'text-left text-[13px] text-fg outline-none transition-colors',
  'data-[highlighted]:bg-tint-strong data-[disabled]:opacity-40'
)

export function ProviderModelMenu({
  trigger,
  triggerClassName,
  className,
  ariaLabel,
  menuLabel,
  menuIcon,
  align = 'start',
  width = 300,
  loaded = true,
  loadingLabel,
  emptyLabel,
  rows,
  topItem,
  onSelectModel,
  onOpenChange
}: {
  /** 触发按钮的内容;按钮本身由这里渲染(带 `app-no-drag`)。 */
  trigger: ReactNode
  triggerClassName?: string
  /**
   * 外层包裹 `div` 的类名。composer 的药丸要 `shrink`,设置页的行内下拉要撑满控件列
   * ——**默认值只满足前者**,后者必须显式传 `'w-full'`,不然按钮会缩成内容宽度,
   * 在 318px 的控件列里贴左。
   */
  className?: string
  /**
   * 触发器的无障碍 label。不传就用 `menuLabel`——composer 里只有一个模型
   * 选择器,两者相同没关系;设置页同一屏有「默认模型」「默认子代理」两个
   * 实例,共用 `menuLabel`(可见的面板标题文案)会让读屏读出同一句话,
   * 分不清哪个是哪个,所以那边必须显式传这一项。
   */
  ariaLabel?: string
  /** 顶层面板标题(比如「选择模型提供商」),渲染在面板里,人人都看得见。 */
  menuLabel: string
  menuIcon?: ReactNode
  align?: 'start' | 'end'
  width?: number
  /** 数据还没到齐时只显示 `loadingLabel`,不渲染供应商列表。 */
  loaded?: boolean
  loadingLabel?: string
  /** `rows` 为空(没有可选供应商)时显示的提示。 */
  emptyLabel?: string
  rows: readonly ProviderModelMenuRow[]
  /**
   * 顶层列表最前面单独一项,不进入第二级(比如「跟随对话」)。
   * 只有需要「允许不钉死具体模型」这个状态的调用方才传。
   */
  topItem?: { label: string; selected: boolean; onSelect: () => void }
  onSelectModel: (providerId: string, alias: string) => void
  /**
   * 打开/关闭回调。★ 对话模型选择器用它做「打开时校正」(见 `useChatModelGuard`);
   * 可选,缺省不影响任何现有调用方的行为。
   */
  onOpenChange?: (open: boolean) => void
}): ReactNode {
  const triggerRef = useRef<HTMLButtonElement>(null)
  const [bounds, setBounds] = useState<Element | null>(null)

  return (
    <div className={cn('relative flex', className ?? 'min-w-0')}>
      <DropdownMenu.Root
        modal={false}
        onOpenChange={(open) => {
          if (open) setBounds(triggerRef.current?.closest('[data-menu-bounds]') ?? null)
          onOpenChange?.(open)
        }}
      >
        <DropdownMenu.Trigger asChild>
          <button
            ref={triggerRef}
            type="button"
            aria-label={ariaLabel ?? menuLabel}
            className={cn('app-no-drag disabled:opacity-40', triggerClassName)}
          >
            {trigger}
          </button>
        </DropdownMenu.Trigger>
        <DropdownMenu.Portal>
          <DropdownMenu.Content
            align={align}
            side="bottom"
            sideOffset={4}
            collisionPadding={8}
            collisionBoundary={bounds ?? undefined}
            style={{ width }}
            className={cn(PANEL, 'max-h-[var(--radix-dropdown-menu-content-available-height)]')}
          >
            <DropdownMenu.Label className="px-2.5 pt-2 pb-1 text-[11px] text-fg-faint">
              <span className="flex items-center gap-1.5">
                {menuIcon}
                {menuLabel}
              </span>
            </DropdownMenu.Label>
            {topItem !== undefined && (
              <DropdownMenu.Item className={ITEM} onSelect={topItem.onSelect}>
                <span className="min-w-0 flex-1 truncate">{topItem.label}</span>
                <Tick on={topItem.selected} />
              </DropdownMenu.Item>
            )}
            {!loaded ? (
              <Note>{loadingLabel}</Note>
            ) : rows.length === 0 ? (
              <Note>{emptyLabel}</Note>
            ) : (
              rows.map((row) => (
                <DropdownMenu.Sub key={row.id}>
                  <DropdownMenu.SubTrigger className={cn(ITEM, 'data-[state=open]:bg-tint-strong')}>
                    <span className="min-w-0 flex-1">
                      <span className="block truncate">{row.label}</span>
                      {row.description !== undefined && (
                        <span className="mt-0.5 block truncate text-[11px] text-fg-faint">{row.description}</span>
                      )}
                    </span>
                    <Tick on={row.selected} />
                    <ChevronRight size={13} aria-hidden className="shrink-0 text-fg-faint" />
                  </DropdownMenu.SubTrigger>
                  <DropdownMenu.Portal>
                    <DropdownMenu.SubContent
                      sideOffset={6}
                      alignOffset={-5}
                      collisionPadding={8}
                      style={{ width }}
                      className={cn(PANEL, 'max-h-[var(--radix-dropdown-menu-content-available-height)]')}
                    >
                      <DropdownMenu.Label className="px-2.5 pt-2 pb-1 text-[11px] text-fg-faint">
                        {row.label}
                      </DropdownMenu.Label>
                      {row.models.map((m) => (
                        <DropdownMenu.Item
                          key={m.value}
                          className={ITEM}
                          title={m.label}
                          onSelect={() => onSelectModel(row.id, m.value)}
                        >
                          <span className="min-w-0 flex-1 truncate">{m.label}</span>
                          <Tick on={m.selected} />
                        </DropdownMenu.Item>
                      ))}
                    </DropdownMenu.SubContent>
                  </DropdownMenu.Portal>
                </DropdownMenu.Sub>
              ))
            )}
          </DropdownMenu.Content>
        </DropdownMenu.Portal>
      </DropdownMenu.Root>
    </div>
  )
}

/** 勾选位:没勾时仍占位,免得勾上勾下整列跳动(同 `ui/Menu` 的 `MenuItem`)。 */
function Tick({ on }: { on: boolean }): ReactNode {
  return <Check size={14} aria-hidden className={cn('shrink-0 text-accent', !on && 'invisible')} />
}

function Note({ children }: { children: ReactNode }): ReactNode {
  return <DropdownMenu.Label className="px-2.5 pt-2 pb-1 text-[11px] text-fg-faint">{children}</DropdownMenu.Label>
}
