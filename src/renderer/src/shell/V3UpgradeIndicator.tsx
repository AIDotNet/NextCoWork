/**
 * 标题栏右端的「升级到 NextCoWork 3.0」(2.9)。
 *
 * ★ 由 `UpdateIndicator` 在**没有 2.x 自己的更新**时渲染,两颗图标不会同时出现:2.9.x 的修复版优先装,
 *   装完回到「已是最新」,这一颗才出来。
 * ★ 不做悬停卡片:点一下直接打开「设置 · 关于」,完整的说明和按钮都在那里(`V3UpgradeCard`)。
 *   这件事要读几段话、做一个会退出应用的决定,不适合塞进一张 320px 的浮层。
 */
import { Sparkles } from 'lucide-react'
import { useEffect, type ReactNode } from 'react'
import { IconButton } from '../components/ui/IconButton'
import { useI18n } from '../i18n'
import { useV3UpgradeStore } from '../stores/v3-upgrade'
import { useWindowStore } from '../stores/window'

export function V3UpgradeIndicator(): ReactNode {
  const { t } = useI18n()
  const state = useV3UpgradeStore((s) => s.state)
  const load = useV3UpgradeStore((s) => s.load)
  const openSettings = useWindowStore((s) => s.openSettings)

  useEffect(() => { void load() }, [load])

  if (state.status !== 'available') return null

  return (
    <span className="app-no-drag relative flex shrink-0 self-center">
      <IconButton
        label={t('v3.indicatorLabel')}
        size={28}
        width={38}
        onClick={() => openSettings('about')}
        // 和 UpdateIndicator 同一种控件(38×28 + rounded-pill,笔画 1.5)
        className="rounded-pill"
      >
        <Sparkles size={16} strokeWidth={1.5} />
      </IconButton>
      <span aria-hidden className="pointer-events-none absolute right-[6px] top-[4px] size-[6px] rounded-full bg-accent ring-2 ring-chrome" />
    </span>
  )
}
