/**
 * 设置 › 通用 › 文件:「默认打开方式」下拉。
 *
 * 需求:文件树右键菜单第一行「在 X 中打开」的 X 由用户指定(参考截图里是 Zed / VS Code)。
 * 候选就是这台机器上探测到的那几个(`workspace:listOpenTargets`,与菜单读同一份缓存),
 * 外加一项「自动」(空串,落点规则见 `pickPrimaryTarget`)。
 *
 * ★ 存着的 id 本机探测不到时(卸载了 / 设置来自另一台机器)**不偷偷改回自动**:
 *   下拉里多出一行「已选的程序当前不可用」让用户看见,菜单那边自己会按落点退回。
 *   悄悄改写的话,用户重装 IDE 之后得再来设置一遍,而他不知道为什么。
 */
import type { ReactNode } from 'react'
import { isOpenTargetPreference } from '../../../../shared/domain/open-target'
import { openTargetLabel } from '../../components/OpenWithMenu'
import { useOpenTargets } from '../../components/useOpenTargets'
import { Select } from '../../components/ui/Select'
import { useI18n } from '../../i18n'

export function DefaultOpenTargetSelect({
  value,
  onChange
}: {
  /** `AppSettings.defaultOpenTarget`;空串 = 自动 */
  value: string
  onChange: (targetId: string) => void
}): ReactNode {
  const { t } = useI18n()
  const targets = useOpenTargets()
  const known = targets?.some((target) => target.id === value) === true
  const options = [
    { value: '', label: t('openWith.settingAuto') },
    ...(targets ?? []).map((target) => ({ value: target.id, label: openTargetLabel(t, target) })),
    // 探测还没回来时先占一行「加载中」,免得触发器上闪一下空白
    ...(value !== '' && !known
      ? [{ value, label: targets === null ? t('common.loading') : t('openWith.settingUnavailable') }]
      : [])
  ]
  return (
    <Select
      value={value}
      options={options}
      inModal
      ariaLabel={t('openWith.settingTitle')}
      onValueChange={(next) => {
        if (isOpenTargetPreference(next)) onChange(next)
      }}
    />
  )
}
