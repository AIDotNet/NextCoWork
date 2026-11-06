/**
 * 「已有同名文件,要覆盖吗」。挂在 shell 根部 —— 发起改名的那个标签可能在提交
 * 之后就被重排、被移到另一格,对话框跟着它走的话会被一起卸载,Promise 就挂住了。
 */
import type { ReactNode } from 'react'
import { Button } from '../components/arc/button/button'
import { Dialog } from '../components/ui/Dialog'
import { useI18n } from '../i18n'
import { settleOverwrite, useOverwriteConfirmStore } from './overwrite-confirm'

export function OverwriteConfirmDialog(): ReactNode {
  const { t } = useI18n()
  const pending = useOverwriteConfirmStore((state) => state.pending)

  return (
    <Dialog
      open={pending !== null}
      title={t('nav.overwriteTitle')}
      /*
        ★ 这里**必须** settle。点遮罩 / 按 Esc 关掉时不 resolve 的话,发起改名的
        那个 Promise 永远挂着 —— 而 `confirmOverwrite` 见到 `pending !== null`
        会把此后每一次改名都直接判 false,表现是「改名再也没反应了」。
      */
      onClose={() => settleOverwrite(false)}
      footer={
        <>
          <Button type="button" variant="secondary" onClick={() => settleOverwrite(false)}>{t('common.cancel')}</Button>
          {/* danger:被覆盖的那份会离开原地,即便还能从废纸篓捞回来 */}
          <Button type="button" variant="danger" onClick={() => settleOverwrite(true)}>{t('nav.overwrite')}</Button>
        </>
      }
    >
      {/* 这句话放正文不放 description —— 它要说清"旧的那份去了废纸篓",
          挤进标题下那行 12px 小字里会被读者跳过。 */}
      <p className="text-[13px] leading-relaxed text-fg-muted">
        {t('nav.overwriteHint', { name: pending?.name ?? '' })}
      </p>
    </Dialog>
  )
}
