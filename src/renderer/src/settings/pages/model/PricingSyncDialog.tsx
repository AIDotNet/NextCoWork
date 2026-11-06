import { AlertTriangle } from 'lucide-react'
import { useEffect, useState, type ReactNode } from 'react'
import type { PricingDiffItem, PricingSyncPreview } from '../../../../../shared/domain/models-dev'
import type { ModelPricing } from '../../../../../shared/domain/pricing'
import { Button } from '../../../components/arc/button/button'
import { EmptyState } from '../../../components/arc/empty-state/empty-state'
import { Dialog } from '../../../components/ui/Dialog'
import { Spinner } from '../../../components/ui/Spinner'
import { useI18n } from '../../../i18n'
import { applyPricingSync, previewPricingSync } from '../../../services/pricing'
import { formatRate } from './pricing-table'

/**
 * models.dev 同步预览。打开即拉,逐条勾选后采用。
 *
 * ★ 新增默认勾上(没有旧值可丢),变更默认不勾 —— 变更会覆盖已按官方页核实过的价,
 * 必须是用户主动选的。采用时按模型 id 提交,价格取主进程缓存的那次预览。
 */
export function PricingSyncDialog({ open, onClose }: { open: boolean; onClose: () => void }): ReactNode {
  const { t } = useI18n()
  const [preview, setPreview] = useState<PricingSyncPreview | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set())
  const [saving, setSaving] = useState(false)
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    if (!open) return
    let alive = true
    setPreview(null)
    setError(null)
    void previewPricingSync()
      .then((p) => {
        if (!alive) return
        setPreview(p)
        setSelected(new Set(p.items.filter((i) => i.kind === 'new').map((i) => i.modelId)))
      })
      .catch((e: unknown) => {
        if (alive) setError(e instanceof Error ? e.message : String(e))
      })
    return () => {
      alive = false
    }
  }, [open, attempt])

  const toggle = (id: string): void =>
    setSelected((prev) => {
      const next = new Set(prev)
      if (!next.delete(id)) next.add(id)
      return next
    })

  const submit = (): void => {
    setSaving(true)
    setError(null)
    void applyPricingSync([...selected])
      .then(onClose)
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
      .finally(() => setSaving(false))
  }

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={t('models.pricingSyncDialogTitle')}
      description={
        preview === null ? t('models.pricingSyncing') : t('models.pricingSyncDialogDesc', { total: preview.total, count: preview.items.length })
      }
      width={640}
      footer={
        <>
          <Button size="sm" type="button" variant="secondary" onClick={onClose} disabled={saving}>
            {t('common.cancel')}
          </Button>
          <Button size="sm" type="button" variant="primary" disabled={preview === null || saving || selected.size === 0} onClick={submit}>
            {saving ? <Spinner size="sm" /> : undefined}
            {t('models.pricingSyncApply', { count: selected.size })}
          </Button>
        </>
      }
    >
      {error !== null && (
        <div className="mb-3 flex items-start gap-2 rounded-[8px] border border-danger/40 bg-danger/5 px-2.5 py-2">
          <AlertTriangle size={13} className="mt-[2px] shrink-0 text-danger" />
          <p className="min-w-0 flex-1 text-[11.5px] leading-[1.6] break-words text-danger">{error}</p>
          {preview === null && (
            <Button size="sm" type="button" variant="secondary" onClick={() => setAttempt((n) => n + 1)}>
              {t('common.retry')}
            </Button>
          )}
        </div>
      )}
      {preview === null ? (
        error === null && <EmptyState icon={<Spinner size="md" />} title={t('models.pricingSyncing')} description={t('models.pricingSyncHint')} className="py-12" />
      ) : preview.items.length === 0 ? (
        <EmptyState title={t('models.pricingSyncNone')} description={t('models.pricingSyncHint')} className="py-10" />
      ) : (
        <ul className="max-h-[50vh] overflow-y-auto overflow-x-hidden rounded-[8px] border border-border">
          {preview.items.map((item) => (
            <DiffRow key={item.modelId} item={item} checked={selected.has(item.modelId)} onToggle={() => toggle(item.modelId)} />
          ))}
        </ul>
      )}
    </Dialog>
  )
}

function ratesLine(p: ModelPricing): string {
  const r = p.tiers[0]?.rate
  if (r === undefined) return '—'
  const parts = [`${formatRate(r.input, p.currency)} / ${formatRate(r.output, p.currency)}`]
  if (r.cacheRead !== undefined) parts.push(`cache ${formatRate(r.cacheRead, p.currency)}`)
  if (p.tiers.length > 1) parts.push(`+${p.tiers.length - 1} tier`)
  return parts.join(' · ')
}

function DiffRow({ item, checked, onToggle }: { item: PricingDiffItem; checked: boolean; onToggle: () => void }): ReactNode {
  const { t } = useI18n()
  return (
    <li className="border-b border-hairline last:border-b-0">
      <label className="flex cursor-pointer items-start gap-3 px-3 py-2.5">
        <input type="checkbox" checked={checked} onChange={onToggle} className="mt-[3px]" />
        <div className="min-w-0 flex-1">
          <p className="text-[12.5px] text-fg">
            {item.incoming.displayName}
            <span className="ml-2 rounded-[4px] border border-border px-1 text-[10.5px] text-fg-muted">
              {item.kind === 'new' ? t('models.pricingSyncNew') : t('models.pricingSyncChanged')}
            </span>
          </p>
          <p className="truncate text-[11px] text-fg-faint">{item.modelId}</p>
          {item.current !== undefined && (
            <p className="mt-1 text-[11.5px] text-fg-muted">
              {t('models.pricingSyncCurrent')}: {ratesLine(item.current)}
            </p>
          )}
          <p className="text-[11.5px] text-fg">
            {t('models.pricingSyncIncoming')}: {ratesLine(item.incoming)}
          </p>
        </div>
      </label>
    </li>
  )
}
