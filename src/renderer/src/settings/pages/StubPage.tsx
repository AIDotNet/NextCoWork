import type { ReactNode } from 'react'
import { EmptyState } from '../../components/arc/empty-state/empty-state'
import { SETTINGS_ICON } from '../icons'
import type { SettingsPageId } from '../nav'
import { useI18n } from '../../i18n'

/**
 * 照参考图铺出来、但本版没有内容的四页。
 *
 * ★ **直说是被砍掉的 / 排在第几步,不写「即将推出」。** 账户 / 钱包 / 每日回顾
 * 在方案 §10 是明确砍掉的(侧边栏左下角之所以从「账户」换成设置入口就是因为
 * 这个),写成「即将推出」是在骗自己;电脑操作是步骤 9,那有个真实的编号可写。
 */
const STUB_PAGES = ['account', 'wallet', 'review', 'computer'] as const

export function StubPage({ page }: { page: SettingsPageId }): ReactNode {
  const { t } = useI18n()
  const Icon = SETTINGS_ICON[page]
  const isStub = STUB_PAGES.includes(page as (typeof STUB_PAGES)[number])
  const key = isStub ? page : undefined
  return (
    <div className="flex min-h-0 flex-1 items-center justify-center">
      <EmptyState
        icon={<Icon size={26} />}
        title={key === undefined ? t('stub.default') : t(`stub.${key}.title` as 'stub.account.title' | 'stub.wallet.title' | 'stub.review.title' | 'stub.computer.title')}
        description={key === undefined ? '' : t(`stub.${key}.hint` as 'stub.account.hint' | 'stub.wallet.hint' | 'stub.review.hint' | 'stub.computer.hint')}
      />
    </div>
  )
}
