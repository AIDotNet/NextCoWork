import type { ReactNode } from 'react'
import { CircleAlert } from 'lucide-react'
import { Spinner } from '../../components/ui/Spinner'
import { Button } from '../../components/arc/button/button'
import { useI18n } from '../../i18n'
import './thread-loading.css'

function SkeletonBar({ className }: { className: string }): ReactNode {
  return <div className={`thread-skeleton-bar rounded-md bg-tint ${className}`} />
}

/** Match the transcript column and keep the latest turns just above the composer. */
export function ThreadSkeleton(): ReactNode {
  const { t } = useI18n()
  return (
    <div className="flex min-h-0 flex-1 flex-col justify-end overflow-hidden" role="status" aria-live="polite" aria-busy="true" aria-label={t('chat.loadingHistory')} data-testid="thread-skeleton">
      <div className="mx-auto flex w-full max-w-[760px] flex-col gap-6 px-6 py-6">
        <div className="flex flex-col gap-7" aria-hidden="true">
          {[0, 1].map((turn) => (
            <div key={turn} className="flex flex-col gap-5">
              <SkeletonBar className="h-10 w-[46%] self-end rounded-card" />
              <div className="flex flex-col gap-2.5">
                <SkeletonBar className="mb-1 h-4 w-28" />
                <SkeletonBar className="h-3 w-[92%]" />
                <SkeletonBar className="h-3 w-[84%]" />
                <SkeletonBar className="h-3 w-[61%]" />
              </div>
            </div>
          ))}
        </div>
        <div className="flex items-center gap-2 text-[12px] text-fg-muted">
          <Spinner className="text-accent" />
          <span>{t('chat.loadingHistory')}</span>
        </div>
      </div>
    </div>
  )
}

export function ThreadLoadError({ onRetry }: { onRetry: () => void }): ReactNode {
  const { t } = useI18n()
  return (
    <div className="flex min-h-0 flex-1 flex-col items-center justify-center gap-3 px-6 text-center" data-testid="thread-load-error">
      <CircleAlert size={22} className="text-fg-muted" aria-hidden="true" />
      <p className="text-[13px] text-fg-muted" role="alert">{t('chat.historyLoadFailed')}</p>
      <Button variant="secondary" size="sm" onClick={onRetry}>{t('common.retry')}</Button>
    </div>
  )
}
