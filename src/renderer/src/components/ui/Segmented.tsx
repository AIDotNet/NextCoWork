import type { ReactNode } from 'react'
import SegmentedControl from '../arc/segmented-control/segmented-control'

/**
 * ★ **宿主代码不要用这个文件** —— 直接用 `components/arc/segmented-control/segmented-control`
 * 的默认导出 `SegmentedControl`(`onValueChange`、选项文字必须是字符串)。
 *
 * 它只为插件 API(`nextcowork/ui` 的 `Segmented`)保留旧签名,实现是 Arc 的分段控件:
 *
 *   - `size` / `shape` 不再有效 —— 尺寸由 Arc 的 token 决定(见 `styles/arc.css`);
 *   - 非字符串的选项内容放进 Arc 的 `accessory` 槽;
 *   - Arc 没有 `disabled`,用 `<fieldset disabled>` 包一层:里面的按钮由浏览器原生禁用,
 *     `display: contents` 让这层不参与布局。
 */
export function Segmented<T extends string>({
  value,
  options,
  onChange,
  className,
  label,
  disabled = false
}: {
  value: T
  options: ReadonlyArray<{ value: T; label: ReactNode }>
  onChange: (v: T) => void
  size?: 'sm' | 'md'
  shape?: 'rounded' | 'pill'
  className?: string
  label?: string
  disabled?: boolean
}): ReactNode {
  return (
    <fieldset disabled={disabled} className="contents">
      <SegmentedControl
        value={value}
        label={label}
        className={className}
        options={options.map((o) =>
          typeof o.label === 'string' ? { value: o.value, label: o.label } : { value: o.value, label: '', accessory: o.label }
        )}
        onValueChange={(next) => onChange(next as T)}
      />
    </fieldset>
  )
}
