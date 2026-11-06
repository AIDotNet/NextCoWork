import { Button as ArcButton } from '../arc/button/button'

/**
 * ★ **宿主代码不要用这个文件** —— 直接 `import { Button } from 'components/arc/button/button'`。
 *
 * 它只为插件 API 留着:`nextcowork/ui`(`plugin-ui/ui.ts`)对第三方插件导出的
 * `Button` 一直是这套参数(`variant: accent | ghost | danger`、`icon`)。删掉或改签名
 * 等于让所有已发布的插件在下次更新宿主时编译失败,所以签名不动,实现换成 Arc。
 *
 *   accent → primary · ghost → secondary · danger → danger
 */
export function Button({
  children,
  onClick,
  variant = 'ghost',
  size = 'md',
  icon,
  disabled = false,
  className
}: {
  children: React.ReactNode
  onClick?: () => void
  variant?: 'accent' | 'ghost' | 'danger'
  size?: 'sm' | 'md'
  icon?: React.ReactNode
  disabled?: boolean
  className?: string
}): React.ReactNode {
  return (
    <ArcButton
      type="button"
      variant={variant === 'accent' ? 'primary' : variant === 'danger' ? 'danger' : 'secondary'}
      size={size}
      disabled={disabled}
      onClick={onClick === undefined ? undefined : () => onClick()}
      className={className}
    >
      {icon}
      {children}
    </ArcButton>
  )
}
