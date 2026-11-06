import { EmptyState as ArcEmptyState } from '../arc/empty-state/empty-state'

/**
 * ★ **宿主代码不要用这个文件** —— 直接用 `components/arc/empty-state/empty-state`。
 *
 * 它只为插件 API(`nextcowork/ui` 的 `EmptyState`)保留旧签名:`hint` 可选,
 * 对应 Arc 必填的 `description`。
 */
export function EmptyState({
  icon,
  title,
  hint,
  action,
  className
}: {
  icon?: React.ReactNode
  title: string
  hint?: string
  action?: React.ReactNode
  className?: string
}): React.ReactNode {
  return <ArcEmptyState icon={icon} title={title} description={hint ?? ''} action={action} className={className} />
}
