import { Switch } from '../arc/switch/switch'

/**
 * ★ **宿主代码不要用这个文件** —— 直接用 `components/arc/switch/switch` 的 `Switch`
 * (`checked` / `onCheckedChange` / `aria-label`)。
 *
 * 它只为插件 API(`nextcowork/ui` 的 `Toggle`)保留旧签名,实现是 Arc 的 Switch。
 * ★ `label` 走 `aria-label`,不走 Arc 的 `label` —— 后者会在开关旁画一行可见文字,
 *   而这个参数一直只是读屏文案。
 */
export function Toggle({
  checked,
  onChange,
  label,
  disabled = false
}: {
  checked: boolean
  onChange: (v: boolean) => void
  label: string
  disabled?: boolean
}): React.ReactNode {
  return <Switch checked={checked} onCheckedChange={onChange} aria-label={label} disabled={disabled} />
}
