import { Copy, Target, X } from 'lucide-react'
import { useEffect, useState, type ReactNode } from 'react'
import type { ActiveGoal } from '../../../../shared/domain/goal'
import { GOAL_CONDITION_MAX, normalizeGoalCondition } from '../../../../shared/domain/goal'
import { formatDuration } from '../../../../shared/agent/duration'
import { formatTokenCount } from '../../../../shared/agent/tokens'
import { Button } from '../../components/arc/button/button'
import { Dialog } from '../../components/ui/Dialog'
import { Tooltip } from '../../components/arc/tooltip/tooltip'
import { copyText } from '../../services/app'
import { useI18n } from '../../i18n'

function useGoalElapsed(goal?: ActiveGoal): number {
  const [now, setNow] = useState(Date.now)
  useEffect(() => {
    if (goal === undefined) return
    setNow(Date.now())
    const timer = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(timer)
  }, [goal?.setAt])
  return goal === undefined ? 0 : Math.max(0, now - goal.setAt)
}

export function GoalPill({ goal, onOpen, onClear }: {
  goal?: ActiveGoal
  onOpen: () => void
  onClear: () => void
}): ReactNode {
  const { t } = useI18n()
  const elapsed = useGoalElapsed(goal)
  return <div className="inline-flex min-w-0 shrink items-center rounded-pill bg-tint text-[11px] text-fg-muted" data-testid="goal-pill">
    <Tooltip content={goal === undefined ? t('goal.pill.none') : t('goal.pill.detail', {
      condition: goal.condition, iterations: goal.iterations, elapsed: formatDuration(elapsed)
    })}>
      <button type="button" onClick={onOpen} aria-label={t('goal.pill.label')}
        className="inline-flex min-w-0 items-center gap-1 rounded-pill px-2 py-1 hover:bg-tint-hover">
        <Target size={12} aria-hidden className={goal === undefined ? '' : 'text-accent'} />
        <span className="max-w-36 truncate">{goal?.condition ?? t('goal.pill.label')}</span>
      </button>
    </Tooltip>
    {goal !== undefined && <button type="button" onClick={onClear} aria-label={t('goal.pill.clear')}
      className="mr-1 shrink-0 rounded-pill p-0.5 hover:bg-tint-hover"><X size={11} aria-hidden /></button>}
  </div>
}

export function GoalPanel({ open, goal, tokens, onClose, onSet, onClear }: {
  open: boolean
  goal?: ActiveGoal
  tokens?: number
  onClose: () => void
  onSet: (condition: string) => Promise<boolean | void>
  onClear: () => Promise<boolean | void>
}): ReactNode {
  const { t } = useI18n()
  const [condition, setCondition] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const elapsed = useGoalElapsed(open ? goal : undefined)
  useEffect(() => {
    if (!open) return
    setCondition(goal?.condition ?? '')
    setError(null)
  }, [open, goal?.id])

  async function submit(clear: boolean): Promise<void> {
    if (busy) return
    const normalized = normalizeGoalCondition(condition)
    if (!clear && normalized === '') { setError(t('goal.error.empty')); return }
    if (!clear && normalized.length > GOAL_CONDITION_MAX) {
      setError(t('goal.error.tooLong', { length: normalized.length })); return
    }
    setBusy(true)
    setError(null)
    try {
      const ok = await (clear ? onClear() : onSet(normalized))
      if (ok === false) setError(t('goal.error.updateFailed'))
      else onClose()
    } catch {
      setError(t('goal.error.updateFailed'))
    } finally { setBusy(false) }
  }

  return <Dialog open={open} title={t('goal.panel.title')} onClose={() => { if (!busy) onClose() }}
    footer={<>
      {goal !== undefined && <Button type="button" variant="danger" size="sm" disabled={busy} onClick={() => void submit(true)}>
        {t('goal.panel.stopEarly')}
      </Button>}
      <Button type="button" variant="secondary" size="sm" disabled={busy} onClick={onClose}>{t('common.cancel')}</Button>
      <Button type="button" variant="primary" size="sm" disabled={busy} onClick={() => void submit(false)}>
        {t(goal === undefined ? 'goal.panel.set' : 'goal.panel.replace')}
      </Button>
    </>}>
    <div className="flex flex-col gap-3" data-testid="goal-panel">
      {goal === undefined && <p className="text-[12px] text-fg-faint">{t('goal.panel.empty')}</p>}
      <label className="flex flex-col gap-1 text-[12px] text-fg-muted">
        {t('goal.panel.condition')}
        <textarea value={condition} onChange={(event) => setCondition(event.target.value)} rows={4} disabled={busy}
          aria-label={t('goal.panel.condition')} placeholder={t('goal.panel.placeholder')}
          className="apple-field w-full resize-y rounded-lg border border-border bg-surface-input p-2 text-fg outline-none focus:border-fg-faint" />
      </label>
      <p className="text-[11px] text-fg-faint">{t('composer.goal.hint')}</p>
      {goal !== undefined && <>
        <Button type="button" variant="secondary" size="sm" onClick={() => {
          void copyText(goal.condition).catch(() => setError(t('goal.error.copyFailed')))
        }}><Copy size={12} />{t('goal.panel.copy')}</Button>
        <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-[12px]">
          <dt className="text-fg-faint">{t('goal.panel.iterations')}</dt>
          <dd>{goal.iterations === 0 ? t('goal.panel.noCheckYet') : goal.iterations}</dd>
          <dt className="text-fg-faint">{t('goal.panel.elapsed')}</dt><dd>{formatDuration(elapsed)}</dd>
          <dt className="text-fg-faint">{t('goal.panel.tokens')}</dt><dd>{formatTokenCount(tokens ?? goal.tokens ?? 0)}</dd>
          <dt className="text-fg-faint">{t('goal.panel.lastCheck')}</dt>
          <dd className="selectable whitespace-pre-wrap break-words">{goal.lastReason || t('goal.panel.noCheckYet')}</dd>
        </dl>
        {goal.deferredSince !== undefined && <p role="status" className="text-[12px] text-fg-muted">{t('goal.panel.deferred')}</p>}
        {(goal.idleCheckinCount ?? 0) >= 3 && <p role="status" className="text-[12px] text-warning">{t('goal.panel.checkinsPaused')}</p>}
      </>}
      {error !== null && <p role="alert" className="text-[12px] text-danger">{error}</p>}
    </div>
  </Dialog>
}
