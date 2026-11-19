/**
 * 设置 · 关于 顶部的「升级到 NextCoWork 3.0」(2.9)。只在后端确实发布了这台电脑能装的 3.x 时出现。
 *
 * 状态只读 `stores/v3-upgrade.ts`,动作只调那里的 `upgrade` / `openDownload`(AGENTS.md 规则 1 / 4)。
 * 组件里只有「更新说明弹窗开没开」这一个纯 UI 状态。
 */
import { useEffect, useState, type ReactNode } from 'react'
import { Button } from '../../components/arc/button/button'
import { useI18n } from '../../i18n'
import { useV3UpgradeStore } from '../../stores/v3-upgrade'
import { ReleaseNotesDialog } from './ReleaseNotesDialog'

const POINTS = ['v3.point.data', 'v3.point.install', 'v3.point.pairing', 'v3.point.after', 'v3.point.rollback'] as const

export function V3UpgradeCard(): ReactNode {
  const { t } = useI18n()
  const state = useV3UpgradeStore((s) => s.state)
  const upgrading = useV3UpgradeStore((s) => s.upgrading)
  const load = useV3UpgradeStore((s) => s.load)
  const upgrade = useV3UpgradeStore((s) => s.upgrade)
  const download = useV3UpgradeStore((s) => s.download)
  const openDownload = useV3UpgradeStore((s) => s.openDownload)
  const [notesOpen, setNotesOpen] = useState(false)

  useEffect(() => { void load() }, [load])

  if (state.status !== 'available') return null
  const { offer, install } = state
  // 有可在应用内装的文件:下载 → 重启并安装;没有(平台不支持 / 后端没给校验和)就保持「前往下载页并退出」
  const inApp = offer.file !== undefined
  const busy = upgrading || install.phase === 'downloading' || install.phase === 'verifying' || install.phase === 'installing'
  const primaryLabel =
    install.phase === 'downloading' ? t('v3.downloading', { percent: install.total > 0 ? Math.min(99, Math.floor((install.received / install.total) * 100)) : 0 })
    : install.phase === 'verifying' ? t('v3.verifying')
    : install.phase === 'installing' ? t('v3.installing')
    : install.phase === 'ready' ? t('v3.install')
    : install.phase === 'error' ? t('v3.retry')
    : t('v3.download')
  const onPrimary = (): void => { void (install.phase === 'ready' ? upgrade() : download()) }

  return (
    <section className="mb-4 rounded-[14px] border border-accent/40 bg-accent/5 p-4" aria-labelledby="v3-upgrade-title">
      <h2 id="v3-upgrade-title" className="text-[13.5px] font-semibold text-fg">
        {t('v3.title', { version: offer.version })}
      </h2>
      <p className="mt-1 text-[12.5px] leading-[1.6] text-fg-muted">{t('v3.lead')}</p>
      <ul className="mt-2 list-disc space-y-1 pl-5 text-[12px] leading-[1.6] text-fg-muted">
        {POINTS.map((key) => <li key={key}>{t(key)}</li>)}
      </ul>
      <div className="mt-3 flex flex-wrap items-center gap-2">
        {inApp ? (
          <Button type="button" size="sm" variant="primary" disabled={busy} onClick={onPrimary}>
            {primaryLabel}
          </Button>
        ) : (
          <Button type="button" size="sm" variant="primary" disabled={upgrading} onClick={() => void upgrade()}>
            {upgrading ? t('v3.upgrading') : t('v3.upgrade')}
          </Button>
        )}
        <Button type="button" size="sm" variant="secondary" disabled={upgrading} onClick={() => void openDownload()}>
          {t('v3.openDownload')}
        </Button>
        {offer.notes !== undefined && (
          <Button type="button" size="sm" variant="secondary" onClick={() => setNotesOpen(true)}>
            {t('v3.notes')}
          </Button>
        )}
      </div>
      {install.phase === 'error' && (
        <p role="alert" className="mt-2 text-[12px] leading-[1.6] text-danger">{t(`v3.error.${install.code}`)}</p>
      )}
      <ReleaseNotesDialog notes={offer.notes ?? ''} open={notesOpen} onClose={() => setNotesOpen(false)} />
    </section>
  )
}
