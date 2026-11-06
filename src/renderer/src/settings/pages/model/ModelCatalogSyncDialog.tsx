import { AlertTriangle, Search } from 'lucide-react'
import { useEffect, useMemo, useState, type ReactNode } from 'react'
import type { ModelCatalogSyncPreview, ModelCatalogUpdateItem, ModelFieldChange } from '../../../../../shared/domain/models-dev-catalog'
import { Button } from '../../../components/arc/button/button'
import { EmptyState } from '../../../components/arc/empty-state/empty-state'
import { Dialog } from '../../../components/ui/Dialog'
import { Spinner } from '../../../components/ui/Spinner'
import { TextInput } from '../../../components/ui/TextInput'
import { useI18n } from '../../../i18n'
import { findBuiltinModel } from '../../../../../shared/domain/model-catalog-inventory'
import { applyModelCatalogSync, previewModelCatalogSync } from '../../../services/model-catalog'

/**
 * models.dev 模型同步。打开即拉,列出目录里还没有的模型,勾选后加入用户目录。
 *
 * ★ **默认一个都不勾。** 这些行来自聚合源、没有逐条核实,批量默认加入会把未核实的
 * 数据静悄悄塞满目录;用户搜到想要的再勾,或明确点「全选」。
 * 写入后设置页靠 `modelCatalog:changed` 自动刷新,这里不需要回调。
 */
export function ModelCatalogSyncDialog({ open, onClose }: { open: boolean; onClose: () => void }): ReactNode {
  const { t } = useI18n()
  const [preview, setPreview] = useState<ModelCatalogSyncPreview | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set())
  const [query, setQuery] = useState('')
  const [saving, setSaving] = useState(false)
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    if (!open) return
    let alive = true
    setPreview(null)
    setError(null)
    setSelected(new Set())
    setQuery('')
    void previewModelCatalogSync()
      .then((p) => {
        if (alive) setPreview(p)
      })
      .catch((e: unknown) => {
        if (alive) setError(e instanceof Error ? e.message : String(e))
      })
    return () => {
      alive = false
    }
  }, [open, attempt])

  const match = (m: { id: string; displayName: string }): boolean => {
    const q = query.trim().toLowerCase()
    return q === '' || m.id.toLowerCase().includes(q) || m.displayName.toLowerCase().includes(q)
  }
  const visibleAdded = useMemo(() => (preview?.items ?? []).filter(match), [preview, query])
  const visibleUpdates = useMemo(() => (preview?.updates ?? []).filter(match), [preview, query])
  const visible = useMemo(() => [...visibleUpdates, ...visibleAdded], [visibleUpdates, visibleAdded])
  const allChecked = visible.length > 0 && visible.every((m) => selected.has(m.id))
  const total = (preview?.items.length ?? 0) + (preview?.updates.length ?? 0)

  const toggle = (id: string): void =>
    setSelected((prev) => {
      const next = new Set(prev)
      if (!next.delete(id)) next.add(id)
      return next
    })
  const toggleAll = (): void =>
    setSelected((prev) => {
      const next = new Set(prev)
      for (const m of visible) {
        if (allChecked) next.delete(m.id)
        else next.add(m.id)
      }
      return next
    })

  const submit = (): void => {
    setSaving(true)
    setError(null)
    void applyModelCatalogSync([...selected])
      .then(onClose)
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
      .finally(() => setSaving(false))
  }

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={t('models.syncTitle')}
      description={
        preview === null
          ? t('models.syncFetching')
          : preview.updates.length === 0
            ? t('models.syncDesc', { count: preview.items.length })
            : t('models.syncDescWithUpdates', { added: preview.items.length, updated: preview.updates.length })
      }
      width={600}
      footer={
        <>
          <Button size="sm" type="button" variant="secondary" onClick={onClose} disabled={saving}>
            {t('common.cancel')}
          </Button>
          <Button size="sm" type="button" variant="primary" disabled={preview === null || saving || selected.size === 0} onClick={submit}>
            {saving ? <Spinner size="sm" /> : undefined}
            {t('models.syncApply', { count: selected.size })}
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
        error === null && <EmptyState icon={<Spinner size="md" />} title={t('models.syncFetching')} description={t('models.syncTitle')} className="py-12" />
      ) : total === 0 ? (
        <EmptyState title={t('models.syncNone')} description={t('models.syncTitle')} className="py-10" />
      ) : (
        <>
          <div className="flex items-center gap-3 pb-2.5">
            <div className="min-w-0 flex-1">
              <TextInput size="sm" value={query} onChange={setQuery} placeholder={t('models.searchId')} ariaLabel={t('models.searchId')} icon={<Search size={13} className="text-icon" />} />
            </div>
            <span className="shrink-0 text-[11.5px] text-fg-faint">{t('models.selectedCount', { count: selected.size })}</span>
            <button type="button" onClick={toggleAll} className="app-no-drag shrink-0 text-[11.5px] text-fg-muted underline underline-offset-2 transition-colors hover:text-fg">
              {allChecked ? t('models.syncClearAll') : t('models.syncSelectAll')}
            </button>
          </div>
          <div className="max-h-[46vh] overflow-y-auto overflow-x-hidden rounded-[8px] border border-border">
            {visibleUpdates.length > 0 && (
              <section>
                <h4 className="sticky top-0 z-10 border-b border-hairline bg-surface px-3 py-1.5 text-[11.5px] text-fg-muted">
                  {t('models.syncSectionUpdates', { count: visibleUpdates.length })}
                </h4>
                <ul>
                  {visibleUpdates.map((u) => (
                    <UpdateRow key={u.id} item={u} checked={selected.has(u.id)} onToggle={() => toggle(u.id)} />
                  ))}
                </ul>
              </section>
            )}
            {visibleAdded.length > 0 && (
              <section>
                <h4 className="sticky top-0 z-10 border-b border-hairline bg-surface px-3 py-1.5 text-[11.5px] text-fg-muted">
                  {t('models.syncSectionAdded', { count: visibleAdded.length })}
                </h4>
                <ul>
                  {visibleAdded.map((m) => (
                    <li key={m.id} className="border-b border-hairline last:border-b-0">
                      <label className="flex cursor-pointer items-center gap-3 px-3 py-2">
                        <input type="checkbox" checked={selected.has(m.id)} onChange={() => toggle(m.id)} />
                        <div className="min-w-0 flex-1">
                          <p className="truncate text-[12.5px] text-fg">{m.displayName}</p>
                          <p className="truncate text-[11px] text-fg-faint">{m.id}</p>
                        </div>
                        <span className="shrink-0 text-[11px] text-fg-muted">{m.manufacturerLabel}</span>
                      </label>
                    </li>
                  ))}
                </ul>
              </section>
            )}
          </div>
        </>
      )}
    </Dialog>
  )
}

const CAPABILITY_LABEL: Readonly<Record<string, string>> = {
  vision: 'models.badgeVision',
  fileInput: 'models.badgeFile',
  videoInput: 'models.badgeVideoInput',
  audioInput: 'models.badgeAudioInput',
  structuredOutput: 'models.capabilityStructuredOutput',
  tools: 'models.badgeTools'
}

function UpdateRow({ item, checked, onToggle }: { item: ModelCatalogUpdateItem; checked: boolean; onToggle: () => void }): ReactNode {
  const { t } = useI18n()
  const builtin = findBuiltinModel(item.id) !== undefined && item.current.overrideBuiltin !== true
  const label = (c: ModelFieldChange): string => {
    const key = CAPABILITY_LABEL[c.field]
    return key === undefined ? t(`models.syncField.${c.field}` as 'models.syncField.contextWindow') : t(key as 'models.badgeVision')
  }
  const show = (v: ModelFieldChange['from']): string => {
    if (v === undefined) return t('models.syncUnset')
    if (typeof v === 'boolean') return v ? t('models.syncYes') : t('models.syncNo')
    if (typeof v === 'number') return v.toLocaleString()
    return v
  }
  return (
    <li className="border-b border-hairline last:border-b-0">
      <label className="flex cursor-pointer items-start gap-3 px-3 py-2.5">
        <input type="checkbox" checked={checked} onChange={onToggle} className="mt-[3px]" />
        <div className="min-w-0 flex-1">
          <p className="text-[12.5px] text-fg">
            {item.displayName}
            <span className="ml-2 rounded-[4px] border border-border px-1 text-[10.5px] text-fg-muted">
              {builtin ? t('models.syncBuiltinBadge') : t('models.syncCustomBadge')}
            </span>
          </p>
          <p className="truncate text-[11px] text-fg-faint">{item.id}</p>
          <ul className="mt-1 space-y-0.5">
            {item.changes.map((c) => (
              <li key={c.field} className="text-[11.5px] text-fg-muted">
                {label(c)}: <span className="text-fg-faint line-through">{show(c.from)}</span>
                <span className="mx-1">→</span>
                <span className="text-fg">{show(c.to)}</span>
              </li>
            ))}
          </ul>
        </div>
      </label>
    </li>
  )
}
