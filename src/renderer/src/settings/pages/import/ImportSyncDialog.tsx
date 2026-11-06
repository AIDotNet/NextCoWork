/**
 * 「同步内容」弹窗 —— 七个类别的授权开关。
 *
 * ★ 不照搬截图里那个「隐藏/显示全部类别」的额外开关:首版只有七类,**全部可见**。
 * 加一个折叠开关意味着界面上会出现一个「还有更多」的暗示,而暗示背后什么都没有。
 *
 * ★ 「全选」勾的是**当前这七个**,不是一张空白支票 —— 保存下去的是七个具体名字
 * (见 `IMPORT_CATEGORIES` 的注释)。升级后新增的类别默认未授权,用户得再来一次。
 */
import { Check } from 'lucide-react'
import { useEffect, useState, type ReactNode } from 'react'
import type { ImportCategory } from '../../../../../shared/domain/import'
import { IMPORT_CATEGORIES } from '../../../../../shared/domain/import'
import { Button } from '../../../components/arc/button/button'
import { Dialog } from '../../../components/ui/Dialog'
import { cn } from '../../../lib/cn'
import { useI18n, type TranslationKey } from '../../../i18n'

export function ImportSyncDialog({
  open,
  value,
  busy,
  onClose,
  onSave
}: {
  open: boolean
  value: readonly ImportCategory[]
  busy: boolean
  onClose: () => void
  onSave: (categories: ImportCategory[]) => void
}): ReactNode {
  const { t } = useI18n()
  const [draft, setDraft] = useState<Set<ImportCategory>>(() => new Set(value))

  /*
    ★ 每次打开都从 props 重置。不重置的话,上一次点「取消」丢弃的草稿会在
    下一次打开时原样回来 —— 而用户以为自己已经撤销了那次修改。
  */
  useEffect(() => {
    if (open) setDraft(new Set(value))
  }, [open, value])

  const allSelected = IMPORT_CATEGORIES.every((c) => draft.has(c))

  const toggle = (category: ImportCategory): void => {
    const next = new Set(draft)
    if (next.has(category)) next.delete(category)
    else next.add(category)
    setDraft(next)
  }

  return (
    <Dialog
      open={open}
      title={t('import.syncDialogTitle')}
      description={t('import.syncDialogHint')}
      onClose={onClose}
      width={440}
      footer={
        <>
          <Button size="sm" type="button" variant="secondary" onClick={onClose}>{t('import.cancel')}</Button>
          <Button size="sm" type="button" variant="primary" disabled={busy} onClick={() => onSave([...draft])}>
            {t('import.save')}
          </Button>
        </>
      }
    >
      <button
        type="button"
        role="checkbox"
        aria-checked={allSelected}
        onClick={() => setDraft(allSelected ? new Set() : new Set(IMPORT_CATEGORIES))}
        className="app-no-drag mb-2 flex w-full items-center gap-2.5 rounded-[8px] px-2.5 py-2 text-left transition-colors hover:bg-tint"
      >
        <Box checked={allSelected} />
        <span className="text-[12.5px] text-fg">{t('import.selectAllSupported')}</span>
      </button>

      <ul className="overflow-hidden rounded-[10px] border border-border">
        {IMPORT_CATEGORIES.map((category) => {
          const checked = draft.has(category)
          return (
            <li key={category} className="border-b border-hairline last:border-b-0">
              <button
                type="button"
                role="checkbox"
                aria-checked={checked}
                onClick={() => toggle(category)}
                className="app-no-drag flex w-full items-center gap-2.5 px-2.5 py-2.5 text-left transition-colors hover:bg-tint"
              >
                <Box checked={checked} />
                <span className="min-w-0 flex-1 truncate text-[12.5px] text-fg">
                  {t(`import.category.${category}` as TranslationKey)}
                </span>
              </button>
            </li>
          )
        })}
      </ul>

      <p className="mt-3 text-[11.5px] leading-[1.5] text-fg-faint">{t('import.mcpDisabledHint')}</p>
    </Dialog>
  )
}

/** 勾选框。照 `ImportModelsDialog` 那个的形状 —— 同一套设置界面里不该有两种勾。 */
export function Box({ checked, partial = false }: { checked: boolean; partial?: boolean }): ReactNode {
  return (
    <span
      className={cn(
        'flex size-[15px] shrink-0 items-center justify-center rounded-[4px] border transition-colors',
        checked || partial ? 'border-accent bg-accent text-accent-fg' : 'border-border'
      )}
      aria-hidden
    >
      {checked && <Check size={11} strokeWidth={3} />}
      {!checked && partial && <span className="h-[2px] w-[7px] rounded-full bg-accent-fg" />}
    </span>
  )
}
