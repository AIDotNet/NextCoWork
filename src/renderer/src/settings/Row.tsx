/**
 * 设置页里的行。**只在设置里出现,所以不进 `components/ui/`** ——
 * `Toggle` / `Slider` 那些是控件,任何地方都能用;「左边标题+描述、右边控件、
 * 行间一道发丝线」这个形状是这一个界面的排版。
 *
 * 量自 06cd7b3c:行步进 ≈70,标题 13px `fg`,描述 12px `fg-muted`(会换到两三行),
 * 行间 `border-hairline`;控件列右对齐,宽控件一律占到 318px(x760..1078)。
 */
import type { ReactNode } from 'react'
import { cn } from '../lib/cn'

/** 分组:参考图里「系统快捷键」那种小号灰标题 */
export function SettingGroup({
  title,
  children,
  className
}: {
  title?: string
  children: ReactNode
  className?: string
}): ReactNode {
  return (
    <section className={cn('pt-2', className)}>
      {title !== undefined && (
        <h3 className="px-1 pt-3 pb-1 text-[11.5px] text-fg-faint">{title}</h3>
      )}
      {children}
    </section>
  )
}

/** 横排行:左标题+描述,右控件 */
export function SettingRow({
  title,
  description,
  children,
  wide = false,
  last = false
}: {
  title: string
  /** ReactNode 而不是 string:好几行要在描述里嵌一句「落点是步骤 N」 */
  description?: ReactNode
  children?: ReactNode
  /** 控件占满右侧那 318px(Segmented / Slider 这类);默认按内容宽(Toggle 这类) */
  wide?: boolean
  /** 组内最后一行不画线 —— 只有紧接着页脚时才用 */
  last?: boolean
}): ReactNode {
  return (
    <div
      className={cn(
        'flex items-center gap-6 py-4',
        !last && 'border-b border-hairline'
      )}
    >
      <div className="min-w-0 flex-1">
        <p className="text-[13px] text-fg">{title}</p>
        {description !== undefined && (
          <p className="mt-1 text-[12px] leading-[1.5] text-fg-muted">{description}</p>
        )}
      </div>
      {children !== undefined && (
        <div className={cn('flex shrink-0 justify-end', wide && 'w-[318px]')}>{children}</div>
      )}
    </div>
  )
}

/** 竖排:控件通栏放在标题下面(代理地址那种长输入框) */
export function SettingField({
  title,
  description,
  children,
  last = false
}: {
  title: string
  description?: ReactNode
  children: ReactNode
  last?: boolean
}): ReactNode {
  return (
    <div className={cn('py-4', !last && 'border-b border-hairline')}>
      <p className="text-[13px] text-fg">{title}</p>
      {description !== undefined && (
        <p className="mt-1 text-[12px] leading-[1.5] text-fg-muted">{description}</p>
      )}
      <div className="mt-3">{children}</div>
    </div>
  )
}

/** 主开关下面缩进的子行(三个提示音)。**组内不画线** —— 参考图里它们是一簇 */
export function SettingSubRow({
  title,
  children,
  disabled = false
}: {
  title: string
  children: ReactNode
  disabled?: boolean
}): ReactNode {
  return (
    <div className={cn('flex items-center gap-6 py-2.5 pl-4', disabled && 'opacity-40')}>
      <p className="min-w-0 flex-1 text-[12.5px] text-fg-muted">{title}</p>
      <div className="shrink-0">{children}</div>
    </div>
  )
}

/**
 * 右侧不放控件、放一句「这一步还没做」的行。
 *
 * 和 `views/registry.tsx` 的 `Placeholder` 同一个约定,只是行内版:
 * **不编假数据,也不写「即将推出」** —— 直说是第几步,好让人去对方案。
 */
export function TodoRow({
  title,
  description,
  step,
  last = false
}: {
  title: string
  description?: ReactNode
  step: string
  last?: boolean
}): ReactNode {
  return (
    <SettingRow title={title} description={description} last={last}>
      <span className="text-[12px] text-fg-faint">{step}</span>
    </SettingRow>
  )
}

/**
 * 「这个字段能存,但今天全应用没人读它」。
 *
 * 这类行占了本页面的一大半(`defaultPermissionMode` / `subagent` / `gateway` /
 * `proxy` / `notifications` / `locale` 全应用零消费者;`notifications` 现已由
 * `stores/notification-sounds.ts` 读取,那几行的标记随之摘掉),把它们藏起来不诚实,
 * 假装它们已经生效更不诚实 —— 所以每一行都点名将来是谁读它。
 */
export function LandsAt({ children }: { children: ReactNode }): ReactNode {
  return <span className="text-fg-faint">({children})</span>
}
